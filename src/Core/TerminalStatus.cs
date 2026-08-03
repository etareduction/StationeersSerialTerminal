namespace SerialTerminal.Core
{
    /// <summary>
    /// The status payload as it travels over the wire: input FIFO depth and the
    /// overflow flag. Remote clients mirror it for tooltips and logic reads.
    /// </summary>
    internal sealed record TerminalStatus
    {
        public required bool Overflow { get; init; }

        public required int RxCount { get; init; }
    }
}
