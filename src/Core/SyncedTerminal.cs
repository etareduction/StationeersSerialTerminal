namespace SerialTerminal.Core
{
    /// <summary>
    /// Thread-safe owner of the mod's one mutable object: wraps
    /// <see cref="TerminalState"/> behind a lock (IC10 runs off the main
    /// thread), stamps every screen change with a monotonically increasing
    /// version, caches the presentation snapshot per version, and keeps the
    /// client-side mirror of the server's status payload. Still game-free:
    /// the device maps the returned change flags onto network dirty bits.
    /// </summary>
    internal sealed class SyncedTerminal
    {
        private readonly object _lock = new();
        private readonly TerminalState _state = new();
        private int _version;

        /// <summary>The server's status payload as last synced; authoritative on clients.</summary>
        private TerminalStatus _synced = new() { Overflow = false, RxCount = 0 };

        /// <summary>Last built snapshot; immutable, so a stale read is harmless.</summary>
        private TerminalSnapshot _snapshot;

        /// <summary>FIFO depth and overflow; live where simulating, synced mirror on clients.</summary>
        /// <param name="live">True where the simulation runs, false on remote clients.</param>
        public (int RxCount, bool Overflow) Readout(bool live)
        {
            lock (_lock)
            {
                return live
                    ? (_state.RxCount, _state.Overflow)
                    : (_synced.RxCount, _synced.Overflow);
            }
        }

        /// <summary>Reads one UART register (IC10 <c>get</c>).</summary>
        /// <param name="address">Register address; out-of-range raises the chip error.</param>
        public (double Value, TerminalChange Change) ReadRegister(int address)
        {
            lock (_lock)
            {
                (double value, TerminalChange change) = _state.ReadRegister(address);
                return (value, Bump(change));
            }
        }

        /// <summary>Writes one UART register (IC10 <c>put</c>).</summary>
        /// <param name="address">Register address; out-of-range raises the chip error.</param>
        /// <param name="value">Value written by the chip.</param>
        public TerminalChange WriteRegister(int address, double value)
        {
            lock (_lock) return Bump(_state.WriteRegister(address, value));
        }

        /// <summary>Print one packed ascii-6 double (logic Setting writes).</summary>
        /// <param name="value">Packed ascii-6 double as written by the circuit.</param>
        public TerminalChange Print(double value)
        {
            lock (_lock) return Bump(_state.Print(value));
        }

        /// <summary>Queues raw keystrokes into the input FIFO (with local echo when on).</summary>
        /// <param name="text">Raw keystrokes typed by the player.</param>
        public TerminalChange AcceptKeystrokes(string text)
        {
            lock (_lock) return Bump(_state.AcceptKeystrokes(text));
        }

        /// <summary>Full reset (IC10 <c>clr</c>, power loss): screen, FIFO, flags, modes.</summary>
        public TerminalChange Reset()
        {
            lock (_lock) return Bump(_state.Reset());
        }

        /// <summary>
        /// Immutable screen snapshot for drawing, rebuilt only when the version
        /// changed since the last call. The unlocked version read can at worst
        /// serve one stale (immutable) snapshot for a frame.
        /// </summary>
        public TerminalSnapshot Snapshot()
        {
            TerminalSnapshot cached = _snapshot;
            if (cached != null && cached.Version == _version)
            {
                return cached;
            }
            lock (_lock)
            {
                cached = _state.Snapshot(_version);
            }
            _snapshot = cached;
            return cached;
        }

        /// <summary>Screen text + cursor, as sent over the wire and into saves.</summary>
        public ScreenContent CaptureScreen()
        {
            lock (_lock) return _state.CaptureScreen();
        }

        /// <summary>Replaces screen cells and cursor (network sync on clients).</summary>
        /// <param name="screen">Wire form of the screen.</param>
        public TerminalChange RestoreScreen(ScreenContent screen)
        {
            lock (_lock) return Bump(_state.RestoreScreen(screen));
        }

        /// <summary>FIFO depth + overflow, as sent over the wire.</summary>
        public TerminalStatus CaptureStatus()
        {
            lock (_lock) return _state.CaptureStatus();
        }

        /// <summary>Client-side mirror of the server's status payload.</summary>
        /// <param name="status">Status as synced from the server.</param>
        public void RestoreStatus(TerminalStatus status)
        {
            lock (_lock) _synced = status;
        }

        /// <summary>Complete state for save serialization.</summary>
        public TerminalMemento Capture()
        {
            lock (_lock) return _state.Capture();
        }

        /// <summary>Restores complete state from a save.</summary>
        /// <param name="memento">State captured by <see cref="Capture"/>.</param>
        public TerminalChange Restore(TerminalMemento memento)
        {
            lock (_lock) return Bump(_state.Restore(memento));
        }

        /// <summary>
        /// Screen changes invalidate cached snapshots and trigger repaints.
        /// Caller must hold the lock.
        /// </summary>
        /// <param name="change">Change flags reported by the state.</param>
        private TerminalChange Bump(TerminalChange change)
        {
            if ((change & TerminalChange.Screen) != TerminalChange.None)
            {
                _version++;
            }
            return change;
        }
    }
}
