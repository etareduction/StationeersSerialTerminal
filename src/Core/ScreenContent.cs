namespace SerialTerminal.Core
{
    /// <summary>
    /// The screen as it travels over the wire and into saves: rows joined with
    /// '\n' and trailing blanks trimmed, plus the cursor cell and the matching
    /// colour plane.
    /// </summary>
    internal sealed record ScreenContent
    {
        public required string Text { get; init; }

        /// <summary>Colour plane, same layout as <see cref="Text"/>; null = all default.</summary>
        public string Colors { get; init; }

        public required int CursorRow { get; init; }

        public required int CursorCol { get; init; }

        /// <summary>True while the cursor is hidden (CTRL 10/11); false in
        /// pre-cursor-control saves.</summary>
        public bool CursorHidden { get; init; }
    }
}
