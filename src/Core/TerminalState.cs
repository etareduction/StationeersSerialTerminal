using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Assets.Scripts.Objects.Electrical;

namespace SerialTerminal.Core
{
    /// <summary>
    /// The TTY-6 itself: 7-register UART protocol, 40x20 terminal emulation and
    /// the keyboard input FIFO — no Unity, networking or save concerns. Every
    /// mutation reports what it touched as <see cref="TerminalChange"/> flags so
    /// the owning device can raise the matching dirty signals. Not thread-safe;
    /// SerialTerminalDevice serializes access behind its lock.
    /// </summary>
    internal sealed class TerminalState
    {
        public const int Rows = 20;
        public const int Columns = 40;
        public const int RxCapacity = 256;
        /// <summary>Register count; the map is the contiguous addresses 0..N-1.</summary>
        public static readonly int RegisterCount = Enum.GetValues(typeof(TerminalRegister)).Length;

        /// <summary>Highest pen colour value: the last standard logic colour (Purple).</summary>
        public const int MaxColor = 11;

        /// <summary>Colour plane char for the default phosphor green pen (-1).</summary>
        internal const char DefaultColorChar = ' ';

        /// <summary>Colour plane encoding (snapshots, wire, saves): one char per cell, colours 0–11.</summary>
        private const string ColorChars = "0123456789ab";

        /// <summary>
        /// Control characters honoured on output.
        /// 8: cursor left, stops at column 0.
        /// </summary>
        internal const char CH_BS = '\b';

        /// <summary>10: down one row, column unchanged.</summary>
        internal const char CH_LF = '\n';

        /// <summary>12: clear screen, cursor home.</summary>
        internal const char CH_FF = '\f';

        /// <summary>13: cursor to column 0.</summary>
        internal const char CH_CR = '\r';

        /// <summary>127: destructive backspace (BS SP BS).</summary>
        internal const char CH_DEL = '\u007f';

        /// <summary>133: next line (CR + LF).</summary>
        internal const char CH_NEL = '\u0085';

        /// <summary>Max chars per packed ascii-6 double (IC10 STR convention).</summary>
        private const int PackedChars = 6;

        private readonly char[] _cells = new char[Rows * Columns];
        private readonly char[] _colors = new char[Rows * Columns];
        private readonly Queue<char> _rx = new();
        private int _cursorRow;
        private int _cursorCol;

        /// <summary>
        /// Deferred (VT-style) wrap: set after printing in the last column;
        /// applied just before the next printable character, so the bottom-right
        /// cell is usable without scrolling.
        /// </summary>
        private bool _wrapPending;

        /// <summary>Block cursor visibility (CTRL 10/11); display-only.</summary>
        private bool _cursorHidden;

        /// <summary>Colour applied to newly printed characters (colour plane char).</summary>
        private char _pen = DefaultColorChar;

        /// <summary>
        /// Transfer mode for writes to the DATA register: unbuffered = one char per
        /// put, buffered = one packed ascii-6 string (up to 6 chars) per put.
        /// </summary>
        private bool _outputBuffered;

        /// <summary>
        /// Transfer mode for reads from the DATA register: unbuffered = one char per
        /// get, buffered = one packed ascii-6 string (up to 6 chars) per get.
        /// </summary>
        private bool _inputBuffered;

        /// <summary>
        /// Half-duplex switch: the keyboard controller echoes keystrokes device-side,
        /// without waiting for the circuit.
        /// </summary>
        private bool _localEcho;

        public TerminalState()
        {
            ClearScreen();
        }

        public int RxCount => _rx.Count;

        public bool Overflow { get; private set; }

        #region Registers (IC10 get/put)

        /// <summary>Reads one UART register (IC10 <c>get</c>).</summary>
        /// <param name="address">Register address; out-of-range raises the chip error.</param>
        /// <exception cref="StackUnderflowException">
        /// The address is not a readable register.
        /// </exception>
        public (double Value, TerminalChange Change) ReadRegister(int address)
        {
            switch ((TerminalRegister)address)
            {
                case TerminalRegister.Data:
                    if (_rx.Count == 0) return (0, TerminalChange.None);
                    double result;
                    if (_inputBuffered)
                    {
                        // Pop up to 6 chars, packed ascii-6 (first typed char in
                        // the highest byte, same layout as STR("...")).
                        long packed = 0;
                        for (int i = 0; i < PackedChars && _rx.Count > 0; i++)
                        {
                            packed = (packed << 8) | (byte)_rx.Dequeue();
                        }
                        result = packed;
                    }
                    else
                    {
                        result = _rx.Dequeue();
                    }
                    // Only the FIFO count changed, not the screen.
                    return (result, TerminalChange.Status);
                case TerminalRegister.Str:
                    return (_rx.Count > 0 ? _rx.Peek() : 0, TerminalChange.None);
                case TerminalRegister.Count:
                    return (_rx.Count, TerminalChange.None);
                case TerminalRegister.Ctrl:
                    int flags = (_rx.Count > 0 ? 1 : 0)
                        | (Overflow ? 2 : 0)
                        | (_outputBuffered ? 4 : 0)
                        | (_inputBuffered ? 8 : 0)
                        | (_localEcho ? 16 : 0)
                        | (_cursorHidden ? 32 : 0);
                    return (flags, TerminalChange.None);
                case TerminalRegister.Row:
                    return (_cursorRow, TerminalChange.None);
                case TerminalRegister.Col:
                    return (_cursorCol, TerminalChange.None);
                case TerminalRegister.Color:
                    return (CharToColor(_pen), TerminalChange.None);
                default:
                    throw new StackUnderflowException();
            }
        }

        /// <summary>
        /// Writes one UART register (IC10 <c>put</c>).
        /// </summary>
        /// <remarks>
        /// The game's own IC10 interpreter throws System.StackOverflowException for a
        /// bad `put` and converts it to the StackOverFlow chip error by type check
        /// (ProgrammableChip.WriteMemory does the same); a custom type would surface
        /// as Unknown instead.
        /// </remarks>
        /// <param name="address">Register address; out-of-range raises the chip error.</param>
        /// <param name="value">Value written by the chip.</param>
        /// <exception cref="StackOverflowException">
        /// The address is not a writable register.
        /// </exception>
        [SuppressMessage("Usage", "CA2201:Do not raise reserved exception types",
            Justification = "Matches the game's IC10 error convention")]
        [SuppressMessage("Design", "MA0012:Do not raise reserved exception type",
            Justification = "Matches the game's IC10 error convention")]
        public TerminalChange WriteRegister(int address, double value)
        {
            switch ((TerminalRegister)address)
            {
                case TerminalRegister.Data:
                    if (_outputBuffered)
                    {
                        PutPacked(value);
                        return TerminalChange.Screen;
                    }
                    int code = (int)value;
                    if (code > 0) PutCodePoint(code);
                    return TerminalChange.Screen;
                case TerminalRegister.Str:
                    PutPacked(value);
                    return TerminalChange.Screen;
                case TerminalRegister.Count:
                    throw new StackOverflowException();
                case TerminalRegister.Ctrl:
                    return Execute((TerminalCommand)(int)value);
                case TerminalRegister.Row:
                    _wrapPending = false;
                    _cursorRow = Clamp((int)value, 0, Rows - 1);
                    return TerminalChange.Screen;
                case TerminalRegister.Col:
                    _wrapPending = false;
                    _cursorCol = Clamp((int)value, 0, Columns - 1);
                    return TerminalChange.Screen;
                case TerminalRegister.Color:
                    _pen = ColorToChar(Clamp((int)value, -1, MaxColor));
                    return TerminalChange.None;
                default:
                    throw new StackOverflowException();
            }
        }

        /// <summary>Runs one CTRL command; unknown codes are ignored by design.</summary>
        /// <param name="command">Command code written to the CTRL register.</param>
        private TerminalChange Execute(TerminalCommand command)
        {
            switch (command)
            {
                case TerminalCommand.ClearScreen: ClearScreen(); return TerminalChange.Screen;
                case TerminalCommand.FlushInput: _rx.Clear(); Overflow = false; return TerminalChange.Status;
                case TerminalCommand.ClearOverflow: Overflow = false; return TerminalChange.Status;
                case TerminalCommand.OutputUnbuffered: _outputBuffered = false; return TerminalChange.None;
                case TerminalCommand.OutputBuffered: _outputBuffered = true; return TerminalChange.None;
                case TerminalCommand.InputUnbuffered: _inputBuffered = false; return TerminalChange.None;
                case TerminalCommand.InputBuffered: _inputBuffered = true; return TerminalChange.None;
                case TerminalCommand.EchoOff: _localEcho = false; return TerminalChange.None;
                case TerminalCommand.EchoOn: _localEcho = true; return TerminalChange.None;
                case TerminalCommand.CursorHide: _cursorHidden = true; return TerminalChange.Screen;
                case TerminalCommand.CursorShow: _cursorHidden = false; return TerminalChange.Screen;
                case TerminalCommand.None:
                default: return TerminalChange.None;
            }
        }

        /// <summary>Print one packed ascii-6 double (logic Setting writes).</summary>
        /// <param name="value">Packed ascii-6 double as written by the circuit.</param>
        public TerminalChange Print(double value)
        {
            PutPacked(value);
            return TerminalChange.Screen;
        }

        /// <summary>Full reset (IC10 <c>clr</c>, power loss): screen, FIFO, flags, modes.</summary>
        public TerminalChange Reset()
        {
            ClearScreen();
            _pen = DefaultColorChar;
            _rx.Clear();
            Overflow = false;
            _outputBuffered = false;
            _inputBuffered = false;
            _localEcho = false;
            _cursorHidden = false;
            return TerminalChange.Screen | TerminalChange.Status;
        }

        #endregion Registers (IC10 get/put)

        #region Keyboard

        /// <summary>Queues raw keystrokes into the input FIFO (with local echo when on).</summary>
        /// <param name="text">Raw keystrokes typed by the player.</param>
        public TerminalChange AcceptKeystrokes(string text)
        {
            bool echoed = _localEcho;
            foreach (char raw in text)
            {
                // Enter arrives as '\n' or '\r' depending on platform; the
                // keyboard controller sends both as CR (13). Anything past DEL
                // is outside the terminal's character set.
                char c = raw == CH_LF ? CH_CR : (raw > CH_DEL ? '?' : raw);
                // Half-duplex echo happens even when the FIFO is full.
                if (echoed) EchoChar(c);
                _ = TryEnqueue(c);
            }
            return echoed
                ? TerminalChange.Screen | TerminalChange.Status
                : TerminalChange.Status;
        }

        /// <summary>Sole owner of the FIFO capacity limit and the overflow flag.</summary>
        /// <param name="c">The character to queue.</param>
        private bool TryEnqueue(char c)
        {
            if (_rx.Count >= RxCapacity)
            {
                Overflow = true;
                return false;
            }
            _rx.Enqueue(c);
            return true;
        }

        /// <summary>
        /// Local-echo rendering of one keystroke: Enter (CR) echoes as a full
        /// newline, Backspace (BS) as a rubout.
        /// </summary>
        /// <param name="c">The keystroke to echo.</param>
        private void EchoChar(char c)
        {
            switch (c)
            {
                case CH_CR: PutChar(CH_NEL); return;
                case CH_BS: PutChar(CH_DEL); return;
                default:
                    if (c >= ' ') PutChar(c);
                    return;
            }
        }

        #endregion Keyboard

        #region Terminal emulation

        /// <summary>Print one packed ascii-6 double (IC10 STR convention).</summary>
        /// <param name="value">Packed ascii-6 double as written by the chip.</param>
        private void PutPacked(double value)
        {
            string text = ProgrammableChip.UnpackAscii6(value, signed: true);
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text) PutChar(c);
        }

        /// <summary>
        /// Print one Unicode code point (unbuffered DATA writes): BMP characters
        /// print directly; surrogate halves and any value above U+FFFF print '?'
        /// (a cell holds one UTF-16 char), keeping bad writes visible.
        /// </summary>
        /// <param name="codePoint">Positive value written by the chip.</param>
        private void PutCodePoint(int codePoint)
        {
            PutChar(codePoint <= char.MaxValue && !char.IsSurrogate((char)codePoint)
                ? (char)codePoint
                : '?');
        }

        [SuppressMessage("Style", "IDE0010:Add missing cases",
            Justification = "Switches over char to honour the handful of control characters listed above; everything else falls through to the printable path below")]
        private void PutChar(char c)
        {
            switch (c)
            {
                case CH_LF:
                    _wrapPending = false;
                    LineFeed();
                    return;
                case CH_CR:
                    _wrapPending = false;
                    _cursorCol = 0;
                    return;
                case CH_NEL:
                    _wrapPending = false;
                    _cursorCol = 0;
                    LineFeed();
                    return;
                case CH_BS:
                    _wrapPending = false;
                    if (_cursorCol > 0) _cursorCol--;
                    return;
                case CH_DEL:
                    if (_wrapPending)
                    {
                        // Rub out the just-printed last-column char in place.
                        _wrapPending = false;
                        _cells[(_cursorRow * Columns) + _cursorCol] = ' ';
                        _colors[(_cursorRow * Columns) + _cursorCol] = DefaultColorChar;
                        return;
                    }
                    if (_cursorCol > 0)
                    {
                        _cursorCol--;
                        _cells[(_cursorRow * Columns) + _cursorCol] = ' ';
                        _colors[(_cursorRow * Columns) + _cursorCol] = DefaultColorChar;
                    }
                    return;
                case CH_FF:
                    ClearScreen();
                    return;
            }
            if (c < ' ') return;
            if (_wrapPending)
            {
                _wrapPending = false;
                _cursorCol = 0;
                LineFeed();
            }
            _cells[(_cursorRow * Columns) + _cursorCol] = c;
            _colors[(_cursorRow * Columns) + _cursorCol] = _pen;
            if (_cursorCol == Columns - 1) _wrapPending = true;
            else _cursorCol++;
        }

        /// <summary>Cursor down one row, column unchanged; scrolls at the bottom.</summary>
        private void LineFeed()
        {
            _cursorRow++;
            if (_cursorRow < Rows) return;
            _cursorRow = Rows - 1;
            Array.Copy(_cells, Columns, _cells, 0, (Rows - 1) * Columns);
            Array.Copy(_colors, Columns, _colors, 0, (Rows - 1) * Columns);
            for (int c = 0; c < Columns; c++)
            {
                _cells[((Rows - 1) * Columns) + c] = ' ';
                _colors[((Rows - 1) * Columns) + c] = DefaultColorChar;
            }
        }

        private void ClearScreen()
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                _cells[i] = ' ';
                _colors[i] = DefaultColorChar;
            }
            _cursorRow = 0;
            _cursorCol = 0;
            _wrapPending = false;
        }

        #endregion Terminal emulation

        #region Capture / restore

        /// <summary>Immutable presentation snapshot, stamped with the given version.</summary>
        /// <param name="version">Content version the snapshot represents.</param>
        public TerminalSnapshot Snapshot(int version)
        {
            string[] lines = new string[Rows];
            string[] colors = new string[Rows];
            for (int r = 0; r < Rows; r++)
            {
                lines[r] = new string(_cells, r * Columns, Columns);
                colors[r] = new string(_colors, r * Columns, Columns);
            }
            return new TerminalSnapshot
            {
                Version = version,
                Lines = lines,
                Colors = colors,
                CursorRow = _cursorRow,
                CursorCol = _cursorCol,
                CursorVisible = !_cursorHidden
            };
        }

        /// <summary>Screen text + cursor, as sent over the wire and into saves.</summary>
        public ScreenContent CaptureScreen()
        {
            return new ScreenContent
            {
                Text = PlaneToString(_cells),
                Colors = PlaneToString(_colors),
                CursorRow = _cursorRow,
                CursorCol = _cursorCol,
                CursorHidden = _cursorHidden
            };
        }

        /// <summary>Replaces screen cells and cursor (network sync, save load).</summary>
        /// <param name="screen">Wire/save form of the screen.</param>
        public TerminalChange RestoreScreen(ScreenContent screen)
        {
            PlaneFromString(_cells, screen.Text);
            PlaneFromString(_colors, screen.Colors);
            _cursorRow = Clamp(screen.CursorRow, 0, Rows - 1);
            _cursorCol = Clamp(screen.CursorCol, 0, Columns - 1);
            _cursorHidden = screen.CursorHidden;
            return TerminalChange.Screen;
        }

        /// <summary>FIFO depth + overflow, as sent over the wire.</summary>
        public TerminalStatus CaptureStatus()
        {
            return new TerminalStatus
            {
                Overflow = Overflow,
                RxCount = _rx.Count
            };
        }

        /// <summary>Complete state for save serialization.</summary>
        public TerminalMemento Capture()
        {
            return new TerminalMemento
            {
                Screen = CaptureScreen(),
                InputBuffer = new string([.. _rx]),
                Overflow = Overflow,
                OutputBuffered = _outputBuffered,
                InputBuffered = _inputBuffered,
                LocalEcho = _localEcho,
                PenColor = CharToColor(_pen),
                WrapPending = _wrapPending
            };
        }

        /// <summary>Restores complete state from a save.</summary>
        /// <param name="memento">State captured by <see cref="Capture"/>.</param>
        public TerminalChange Restore(TerminalMemento memento)
        {
            _ = RestoreScreen(memento.Screen);
            _pen = ColorToChar(Clamp(memento.PenColor, -1, MaxColor));
            _outputBuffered = memento.OutputBuffered;
            _inputBuffered = memento.InputBuffered;
            _localEcho = memento.LocalEcho;
            _wrapPending = memento.WrapPending;
            _rx.Clear();
            if (!string.IsNullOrEmpty(memento.InputBuffer))
            {
                foreach (char c in memento.InputBuffer)
                {
                    _ = TryEnqueue(c);
                }
            }
            // After the FIFO refill, so a truncating TryEnqueue cannot leak
            // into the restored flag: the saved value wins.
            Overflow = memento.Overflow;
            return TerminalChange.Screen | TerminalChange.Status;
        }

        #endregion Capture / restore

        #region Screen <-> string

        /// <summary>One cell plane (text or colours) as rows joined with '\n',
        /// trailing blanks trimmed.</summary>
        /// <param name="cells">The plane to serialize.</param>
        private static string PlaneToString(char[] cells)
        {
            StringBuilder sb = new(cells.Length + Rows);
            for (int r = 0; r < Rows; r++)
            {
                int end = Columns;
                while (end > 0 && cells[(r * Columns) + end - 1] == ' ') end--;
                _ = sb.Append(cells, r * Columns, end);
                if (r < Rows - 1) _ = sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Refills one cell plane from its serialized form; null or
        /// missing cells blank out.</summary>
        /// <param name="cells">The plane to refill.</param>
        /// <param name="text">Serialized plane, as produced by <see cref="PlaneToString"/>.</param>
        private static void PlaneFromString(char[] cells, string text)
        {
            for (int i = 0; i < cells.Length; i++) cells[i] = ' ';
            if (string.IsNullOrEmpty(text)) return;
            string[] lines = text.Split('\n');
            for (int r = 0; r < Rows && r < lines.Length; r++)
            {
                string line = lines[r];
                for (int c = 0; c < Columns && c < line.Length; c++)
                {
                    // Cells must stay XML/wire-serializable: no lone surrogates.
                    cells[(r * Columns) + c] = char.IsSurrogate(line[c]) ? '?' : line[c];
                }
            }
        }

        #endregion Screen <-> string

        /// <summary>The colour plane char for a pen colour value.</summary>
        /// <param name="color">Pen colour value, -1 (default) to <see cref="MaxColor"/>.</param>
        internal static char ColorToChar(int color)
        {
            return color < 0 ? DefaultColorChar : ColorChars[color];
        }

        /// <summary>The pen colour value for a colour plane char, -1 for the default.</summary>
        /// <param name="code">Colour plane char.</param>
        internal static int CharToColor(char code)
        {
            return ColorChars.IndexOf(code);
        }

        /// <summary>net472 has no Math.Clamp; local helper keeps the core Unity-free.</summary>
        /// <param name="value">Value to clamp.</param>
        /// <param name="min">Inclusive lower bound.</param>
        /// <param name="max">Inclusive upper bound.</param>
        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
