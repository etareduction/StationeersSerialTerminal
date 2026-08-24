namespace SerialTerminal.Core
{
    /// <summary>
    /// Immutable picture of the screen for presentation: fixed-width lines plus
    /// the cursor cell, stamped with the content version it was built from.
    /// Renderers compare <see cref="Version"/> to decide whether to repaint and
    /// never touch live terminal state.
    /// </summary>
    internal sealed record TerminalSnapshot
    {
        public required int Version { get; init; }

        /// <summary>One string per row, each padded to the full column count.</summary>
        public required string[] Lines { get; init; }

        /// <summary>Colour plane: one encoding char per cell, same shape as <see cref="Lines"/>.</summary>
        public required string[] Colors { get; init; }

        public required int CursorRow { get; init; }

        public required int CursorCol { get; init; }

        /// <summary>False while the cursor is hidden (CTRL 10/11).</summary>
        public required bool CursorVisible { get; init; }
    }
}
