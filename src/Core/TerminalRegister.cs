namespace SerialTerminal.Core
{
    /// <summary>
    /// The TTY-6's UART register map — the IC10 get/put address space (see
    /// DESIGN.md). Values are the wire-visible addresses chips use; addresses
    /// outside the map raise the chip error.
    /// </summary>
    internal enum TerminalRegister
    {
        /// <summary>r: pop input (char, or packed ascii-6 in buffered mode) / w: print (same).</summary>
        Data = 0,

        /// <summary>r: peek input char / w: print packed ascii-6.</summary>
        Str = 1,

        /// <summary>r: input chars available.</summary>
        Count = 2,

        /// <summary>r: status flags / w: command.</summary>
        Ctrl = 3,

        /// <summary>rw: cursor row (clamped).</summary>
        Row = 4,

        /// <summary>rw: cursor column (clamped).</summary>
        Col = 5,

        /// <summary>rw: pen colour for newly printed characters (clamped) —
        /// logic colours 0–11, -1 the default phosphor green.</summary>
        Color = 6
    }
}
