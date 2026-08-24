namespace SerialTerminal.Core
{
    /// <summary>
    /// Complete terminal state as an immutable value, for save round-trips.
    /// <see cref="InputBuffer"/> is the raw FIFO contents; XML escaping is the
    /// save layer's concern (see InputBufferEscape).
    /// </summary>
    internal sealed record TerminalMemento
    {
        public required ScreenContent Screen { get; init; }

        public required string InputBuffer { get; init; }

        public required bool Overflow { get; init; }

        public required bool OutputBuffered { get; init; }

        public required bool InputBuffered { get; init; }

        public required bool LocalEcho { get; init; }

        /// <summary>Pen colour value: -1 default, 0–11 logic colour values.</summary>
        public required int PenColor { get; init; }
    }
}
