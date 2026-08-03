namespace SerialTerminal.Core
{
    /// <summary>
    /// The screen as it travels over the wire and into saves: rows joined with
    /// '\n' and trailing blanks trimmed, plus the cursor cell.
    /// </summary>
    internal sealed record ScreenContent
    {
        public required string Text { get; init; }

        public required int CursorRow { get; init; }

        public required int CursorCol { get; init; }
    }
}
