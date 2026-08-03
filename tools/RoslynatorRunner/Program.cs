// Analyzer host replacing the roslynator CLI: loads projects through
// MSBuildWorkspace and runs their own analyzer package references on the
// current Roslyn (the CLI bundles an older compiler that cannot parse C# 14
// `extension` blocks). Ported from the pons repo's RoslynatorRunner.
//
//     RoslynatorRunner [--severity hidden|info|warning|error] [project.csproj ...]
//
// Reports diagnostics at or above the severity floor (default: info) and
// exits non-zero when any are found. Diagnostics in generated sources under
// obj/ are skipped. With no project arguments, scans the working directory.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

// Must be the very first MSBuild-related call. Registers the SDK's MSBuild
// with the assembly loader before any Microsoft.Build.* type is JIT-resolved.
MSBuildLocator.RegisterDefaults();

var minSeverity = DiagnosticSeverity.Info;
var projectPaths = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--severity", StringComparison.Ordinal) && i + 1 < args.Length)
        minSeverity = Enum.Parse<DiagnosticSeverity>(args[++i], ignoreCase: true);
    else
        projectPaths.Add(Path.GetFullPath(args[i]));
}

await RunAsync(projectPaths, minSeverity).ConfigureAwait(false);

static async Task RunAsync(List<string> projectPaths, DiagnosticSeverity minSeverity)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var ct = cts.Token;

    string[] csprojFiles = projectPaths.Count > 0
        ? [.. projectPaths]
        : Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj", SearchOption.AllDirectories);

    if (csprojFiles.Length == 0)
    {
        await Console.Error.WriteLineAsync("No .csproj files found.").ConfigureAwait(false);
        Environment.ExitCode = 2;
        return;
    }

    using var workspace = MSBuildWorkspace.Create();
    workspace.SkipUnrecognizedProjects = true;
    workspace.RegisterWorkspaceFailedHandler(e =>
    {
        if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            Console.Error.WriteLine($"workspace: {e.Diagnostic.Message}");
    });

    // OpenProjectAsync is not safe to parallelize on a single workspace, and
    // transitively-loaded project references will already be present — so we
    // load sequentially and skip anything already loaded.
    var swLoad = Stopwatch.StartNew();
    Console.WriteLine($"Loading {csprojFiles.Length} project(s)...");

    foreach (var csproj in csprojFiles)
    {
        var name = Path.GetFileNameWithoutExtension(csproj);
        if (workspace.CurrentSolution.Projects.Any(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        try
        {
            await workspace.OpenProjectAsync(csproj, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"load failed {csproj}: {ex.Message}").ConfigureAwait(false);
        }
    }
    Console.WriteLine($"Loaded in {swLoad.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");

    var diagnostics = await AnalyzeProjectsAsync(workspace, minSeverity, ct).ConfigureAwait(false);
    PrintDiagnostics(diagnostics);
    PrintSummary(workspace, diagnostics);

    Environment.ExitCode = diagnostics.Count == 0 ? 0 : 1;
}

static void PrintDiagnostics(List<(string Project, Diagnostic Diagnostic)> diagnostics)
{
    // MSBuild-style output so IDEs and CI log parsers recognize it.
    var grouped = diagnostics
        .GroupBy(x => x.Project, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    foreach (var group in grouped)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {group.Key} ===");

        var ordered = group
            .Select(x => x.Diagnostic)
            .OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line);

        foreach (var d in ordered)
        {
            var span = d.Location.GetLineSpan();
            var path = span.Path ?? "<unknown>";
            var line = span.StartLinePosition.Line + 1;
            var col = span.StartLinePosition.Character + 1;
            var sev = d.Severity.ToString().ToUpperInvariant();
            Console.WriteLine($"{path}({line.ToString(CultureInfo.InvariantCulture)},{col.ToString(CultureInfo.InvariantCulture)}): {sev} {d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}");
        }
    }
}

static void PrintSummary(MSBuildWorkspace workspace, List<(string Project, Diagnostic Diagnostic)> diagnostics)
{
    Console.WriteLine();
    var csharpProjects = workspace.CurrentSolution.Projects
        .Where(p => string.Equals(p.Language, LanguageNames.CSharp, StringComparison.Ordinal))
        .ToArray();
    Console.WriteLine($"Analyzed {csharpProjects.Length} project(s)");
    Console.WriteLine($"Total diagnostics: {diagnostics.Count}");
}

static async Task<List<(string Project, Diagnostic Diagnostic)>> AnalyzeProjectsAsync(
    MSBuildWorkspace workspace, DiagnosticSeverity minSeverity, CancellationToken ct)
{
    // Heavy work — parallel across projects, and each CompilationWithAnalyzers
    // runs its analyzers concurrently internally. Cap at half the cores since
    // each project holds a full Compilation in memory.
    var diagnostics = new ConcurrentBag<(string Project, Diagnostic Diagnostic)>();
    var projects = workspace.CurrentSolution.Projects
        .Where(p => string.Equals(p.Language, LanguageNames.CSharp, StringComparison.Ordinal))
        .ToArray();

    var swAnalyze = Stopwatch.StartNew();

    await Parallel.ForEachAsync(
        projects,
        new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct,
        },
        async (project, token) =>
        {
            try
            {
                var analyzers = project.AnalyzerReferences
                    .SelectMany(r => r.GetAnalyzers(project.Language))
                    .ToImmutableArray();

                if (analyzers.IsDefaultOrEmpty) return;

                var compilation = await project.GetCompilationAsync(token).ConfigureAwait(false);
                if (compilation is null) return;

                var options = new CompilationWithAnalyzersOptions(
                    options: project.AnalyzerOptions,
                    onAnalyzerException: (ex, analyzer, _) =>
                        Console.Error.WriteLine($"analyzer exception {analyzer.GetType().Name}: {ex.Message}"),
                    concurrentAnalysis: true,
                    logAnalyzerExecutionTime: false,
                    reportSuppressedDiagnostics: false);

                var withAnalyzers = compilation.WithAnalyzers(analyzers, options);
                var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync(token).ConfigureAwait(false);

                foreach (var d in diags)
                {
                    if (d.Severity >= minSeverity && !IsGeneratedSource(d))
                        diagnostics.Add((project.Name, d));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Console.Error.WriteLineAsync($"analyze failed {project.Name}: {ex.Message}").ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

    swAnalyze.Stop();
    Console.WriteLine($"Analyzed in {swAnalyze.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");

    return [.. diagnostics];
}

// Generated sources (obj/*.AssemblyInfo.cs and friends) carry findings nobody
// can act on — the old CLI gate excluded them with a glob, this one by path.
static bool IsGeneratedSource(Diagnostic d)
{
    var path = d.Location.SourceTree?.FilePath;
    return path is not null
        && path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
