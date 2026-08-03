# Serial Terminal

A Stationeers mod adding the Norsec TTY-6 Serial Terminal: a free-standing
computer block — monitor, desk unit and keyboard — with no processor and no
storage. It is a character display and keyboard wired to a six-register
memory-mapped UART: IC10 circuits print to it and read typed input from it
with `get`/`put`. Click the screen to type; each keystroke is delivered
directly to the input buffer.

- [`mod/API.md`](mod/API.md) — the device's register-level API reference
- [`DESIGN.md`](DESIGN.md) — how the mod is built and why
- [`examples/`](examples) — IC10 programs: a self-test and two interactive shells

## Building

The mod compiles against the game's assemblies, which are not redistributable
and therefore not in this repo. `setup.sh` links them into a gitignored `lib/`
from your own install:

    ./setup.sh

It reads Steam's own library records to find the install, including libraries
on other drives. If that fails, name the directory yourself:

    ./setup.sh /path/to/steamapps/common/Stationeers

BepInEx and [StationeersLaunchPad](https://github.com/StationeersMods/StationeersLaunchPad)
must already be installed in the game folder — three of the referenced
assemblies come from there. Re-run `setup.sh` if the game moves.

Then:

    cd src && dotnet build SerialTerminal.csproj -c Release

Run it from `src/`: `global.json` pins the SDK to 8.0.x and resolves against the
working directory, not the project directory, so building from the repo root
would skip the pin.

## Verifying

Verification is part of the build. Every `dotnet build` runs three gates, and
a finding in any of them fails the build:

- **compiler** — the analyzer packages (Meziantou, Roslynator, Unity) and the
  IDE rules configured in `.editorconfig`, warnings as errors. It cannot run
  all of them: the simplification analyzers (`IDE0001` and friends) need a
  workspace and never fire from `csc`.
- **`dotnet format --verify-no-changes --severity info`** — whitespace, style
  and analyzer fixers. The only gate that reports the simplification rules, so
  a clean compile alone does not mean a clean tree.
- **`roslynator analyze --severity-level info --report-not-configurable`** — a
  second analyzer host, reaching info severity and the `NotConfigurable`
  diagnostics the other two never surface.

The two out-of-process gates cost about 13s on top of a ~2s compile. For a
fast inner loop:

    dotnet build -p:SkipVerify=true

An advisory sweep lists hidden-severity findings (add braces, explicit type,
comment-to-doc-comment...):

    dotnet build -t:VerifyDeep

Every one of those severities is a deliberate choice in `.editorconfig`, so
that sweep never gates — read it when reconsidering a style decision, not as a
list of defects.

## Installing

The build writes `SerialTerminal.dll` straight into [`mod/`](mod), which *is*
the deployable mod folder. Symlink (or copy) it into the game's user-data mods
directory as `SerialTerminal`:

- Windows — `Documents\My Games\Stationeers\mods\`
- Linux/Proton — `<proton-prefix>/drive_c/users/steamuser/Documents/My Games/Stationeers/mods/`

Enable it from StationeersLaunchPad's in-game mod list. LaunchPad loads mods
once at startup, so a rebuild needs a full game restart — and overwriting a
DLL the running game has mapped can crash it, so build between sessions.

Recipe/language XML only merges for mods in that folder; a bare DLL in
`BepInEx/plugins` loads code but gets no recipes or localization.
