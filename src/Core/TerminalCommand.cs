namespace SerialTerminal.Core
{
    /// <summary>
    /// Command codes accepted by writes to the CTRL register (see DESIGN.md).
    /// Values are the wire-visible codes chips write; unknown codes — including
    /// zero — are ignored by design (only a bad register address errors).
    /// </summary>
    internal enum TerminalCommand
    {
        /// <summary>Writing 0 is a no-op.</summary>
        None = 0,

        ClearScreen = 1,

        FlushInput = 2,

        ClearOverflow = 3,

        OutputUnbuffered = 4,

        OutputBuffered = 5,

        InputUnbuffered = 6,

        InputBuffered = 7,

        EchoOff = 8,

        EchoOn = 9
    }
}
