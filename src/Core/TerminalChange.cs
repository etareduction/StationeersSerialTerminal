using System;

namespace SerialTerminal.Core
{
    /// <summary>
    /// What a <see cref="TerminalState"/> mutation touched. The device maps
    /// these to its dirty signals (repaint version, network update flags).
    /// </summary>
    [Flags]
    internal enum TerminalChange
    {
        None = 0,

        /// <summary>Screen cells or cursor changed; renderers must repaint.</summary>
        Screen = 1,

        /// <summary>Input FIFO count or overflow flag changed; screen did not.</summary>
        Status = 2
    }
}
