using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.UI;
using Assets.Scripts.Util;
using Rendering;
using UnityEngine;

namespace SerialTerminal
{
    /// <summary>
    /// Norsec TTY-6 serial terminal. A dumb glass teletype: IC10 circuits talk to it
    /// through a 6-register memory-mapped UART (get/put), players type on it through
    /// the terminal window; every keystroke goes straight into the input FIFO.
    /// </summary>
    public class SerialTerminalDevice : LogicDisplay, IMemoryReadable, IMemoryWritable
    {
        // UART register map (see DESIGN.md)
        private const int ADDR_DATA = 0;   // r: pop input (char, or packed ascii-6 in buffered mode) / w: print (same)
        private const int ADDR_STR = 1;    // r: peek input char / w: print packed ascii-6
        private const int ADDR_COUNT = 2;  // r: input chars available
        private const int ADDR_CTRL = 3;   // r: status flags / w: command
        private const int ADDR_ROW = 4;    // rw: cursor row (clamped)
        private const int ADDR_COL = 5;    // rw: cursor column (clamped)
        private const int REGISTER_COUNT = 6;

        private const int CTRL_CLEAR_SCREEN = 1;
        private const int CTRL_FLUSH_INPUT = 2;
        private const int CTRL_CLEAR_OVERFLOW = 3;
        private const int CTRL_OUTPUT_UNBUFFERED = 4;
        private const int CTRL_OUTPUT_BUFFERED = 5;
        private const int CTRL_INPUT_UNBUFFERED = 6;
        private const int CTRL_INPUT_BUFFERED = 7;
        private const int CTRL_ECHO_OFF = 8;
        private const int CTRL_ECHO_ON = 9;

        // Control characters honoured on output.
        private const char CH_BS = '\b';     // 8: cursor left, stops at column 0
        private const char CH_LF = '\n';     // 10: down one row, column unchanged
        private const char CH_FF = '\f';     // 12: clear screen, cursor home
        private const char CH_CR = '\r';     // 13: cursor to column 0
        private const char CH_DEL = '\u007f'; // 127: destructive backspace (BS SP BS)
        private const char CH_NEL = '\u0085'; // 133: next line (CR + LF)

        // Max chars per packed ascii-6 double (IC10 STR convention).
        private const int PackedChars = 6;

        public const int Rows = 20;
        public const int Columns = 40;
        public const int RxCapacity = 256;

        // Class-specific NetworkUpdateFlags bits. Vanilla puts per-class payloads at
        // 1024+ (see NetworkUpdateType.Thing.*); below us the chain occupies
        // 8/16/32/128 (Thing), 64 (Structure) and 256 (LogicUnitBase).
        private const ushort ScreenNetworkFlag = 1024;  // cells + cursor
        private const ushort StatusNetworkFlag = 2048;  // rx count + overflow

        private readonly object _stateLock = new object();
        private readonly Queue<char> _rx = new Queue<char>();
        private readonly char[] _cells = CreateBlankCells();
        private int _cursorRow;
        private int _cursorCol;
        private bool _overflow;
        // Transfer modes for the DATA register: unbuffered = one char per get/put,
        // buffered = one packed ascii-6 string (up to 6 chars) per get/put.
        private bool _outputBuffered;
        private bool _inputBuffered;
        // Half-duplex switch: the keyboard controller prints keystrokes directly,
        // without waiting for the circuit to echo them (instant feel; default off).
        private bool _localEcho;
        // Volatile-RAM tracking: the terminal wipes all state when it stops
        // operating (switched off or power lost), like a real glass TTY.
        private bool _wasOperating;
        // Input queue count as last synced from the server. _rx itself only lives
        // where the simulation runs; remote clients need this for tooltips/logic reads.
        private int _syncedRxCount;

        // Incremented on every visible state change; the ImGui window and the
        // in-world screen renderer poll it to know when to repaint.
        private int _version;
        private string[] _lineCache;
        private int _lineCacheVersion = -1;
        private int _cacheCursorRow;
        private int _cacheCursorCol;

        public int ScreenVersion => _version;

        public int RowCount => Rows;

        public int ColumnCount => Columns;

        /// <summary>Input queue length; live where simulating, last synced value on clients.</summary>
        private int DisplayRxCount
        {
            get
            {
                lock (_stateLock)
                {
                    return GameManager.RunSimulation ? _rx.Count : _syncedRxCount;
                }
            }
        }

        private static char[] CreateBlankCells()
        {
            char[] cells = new char[Rows * Columns];
            for (int i = 0; i < cells.Length; i++) cells[i] = ' ';
            return cells;
        }

        /// <summary>
        /// The vanilla numeric readout draws only for displays inside the digit
        /// renderer's ActiveDisplays pool; vetoing membership keeps whatever glyphs
        /// SetDisplay writes invisible, without patching LogicDisplay.
        /// (SetDisplay still needs a non-null DigitTransform - PrefabFactory
        /// guarantees one.)
        /// </summary>
        public override bool OnAddToPool(object densePool, int slot)
        {
            if (ReferenceEquals(densePool, LogicDisplayDigitRenderer.ActiveDisplays))
            {
                return false;
            }
            return base.OnAddToPool(densePool, slot);
        }

        /// <summary>
        /// Main-thread snapshot of the screen for drawing. Rebuilt only when the
        /// version changed since the last call.
        /// </summary>
        public string[] SnapshotLines(out int cursorRow, out int cursorCol)
        {
            int version = _version;
            if (_lineCacheVersion != version)
            {
                lock (_stateLock)
                {
                    string[] lines = new string[Rows];
                    for (int r = 0; r < Rows; r++)
                    {
                        lines[r] = new string(_cells, r * Columns, Columns);
                    }
                    _lineCache = lines;
                    _cacheCursorRow = _cursorRow;
                    _cacheCursorCol = _cursorCol;
                    _lineCacheVersion = version;
                }
            }
            cursorRow = _cacheCursorRow;
            cursorCol = _cacheCursorCol;
            return _lineCache;
        }

        #region IMemory (IC10 get/put)

        public int GetStackSize()
        {
            return REGISTER_COUNT;
        }

        public double ReadMemory(int address)
        {
            lock (_stateLock)
            {
                switch (address)
                {
                    case ADDR_DATA:
                    {
                        if (_rx.Count == 0) return 0;
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
                        // Popping input never touches the screen cells; only the
                        // FIFO count changed.
                        MarkStatusDirty();
                        return result;
                    }
                    case ADDR_STR:
                        return _rx.Count > 0 ? _rx.Peek() : 0;
                    case ADDR_COUNT:
                        return _rx.Count;
                    case ADDR_CTRL:
                        return (_rx.Count > 0 ? 1 : 0)
                            | (_overflow ? 2 : 0)
                            | (_outputBuffered ? 4 : 0)
                            | (_inputBuffered ? 8 : 0)
                            | (_localEcho ? 16 : 0);
                    case ADDR_ROW:
                        return _cursorRow;
                    case ADDR_COL:
                        return _cursorCol;
                    default:
                        throw new StackUnderflowException();
                }
            }
        }

        public void WriteMemory(int address, double value)
        {
            bool screenChanged = true;
            bool statusChanged = false;
            lock (_stateLock)
            {
                switch (address)
                {
                    case ADDR_DATA:
                    {
                        if (_outputBuffered)
                        {
                            PutPacked(value);
                            break;
                        }
                        int code = (int)value;
                        if (code > 0 && code < 256) PutChar((char)code);
                        break;
                    }
                    case ADDR_STR:
                        PutPacked(value);
                        break;
                    case ADDR_COUNT:
                        throw new StackOverflowException();
                    case ADDR_CTRL:
                        screenChanged = false;
                        switch ((int)value)
                        {
                            case CTRL_CLEAR_SCREEN: ClearScreen(); screenChanged = true; break;
                            case CTRL_FLUSH_INPUT: _rx.Clear(); _overflow = false; statusChanged = true; break;
                            case CTRL_CLEAR_OVERFLOW: _overflow = false; statusChanged = true; break;
                            case CTRL_OUTPUT_UNBUFFERED: _outputBuffered = false; break;
                            case CTRL_OUTPUT_BUFFERED: _outputBuffered = true; break;
                            case CTRL_INPUT_UNBUFFERED: _inputBuffered = false; break;
                            case CTRL_INPUT_BUFFERED: _inputBuffered = true; break;
                            case CTRL_ECHO_OFF: _localEcho = false; break;
                            case CTRL_ECHO_ON: _localEcho = true; break;
                        }
                        break;
                    case ADDR_ROW:
                        _cursorRow = Mathf.Clamp((int)value, 0, Rows - 1);
                        break;
                    case ADDR_COL:
                        _cursorCol = Mathf.Clamp((int)value, 0, Columns - 1);
                        break;
                    default:
                        throw new StackOverflowException();
                }
            }
            if (screenChanged) MarkScreenDirty();
            if (statusChanged) MarkStatusDirty();
        }

        public void ClearMemory()
        {
            lock (_stateLock)
            {
                ClearScreen();
                _rx.Clear();
                _overflow = false;
                _outputBuffered = false;
                _inputBuffered = false;
                _localEcho = false;
            }
            MarkScreenDirty();
            MarkStatusDirty();
        }

        #endregion

        #region Terminal emulation (callers must hold _stateLock)

        /// <summary>Print one packed ascii-6 double (IC10 STR convention).</summary>
        private void PutPacked(double value)
        {
            PutString(ProgrammableChip.UnpackAscii6(value, signed: true));
        }

        private void PutString(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text) PutChar(c);
        }

        private void PutChar(char c)
        {
            switch (c)
            {
                case CH_LF:
                    LineFeed();
                    return;
                case CH_CR:
                    _cursorCol = 0;
                    return;
                case CH_NEL:
                    _cursorCol = 0;
                    LineFeed();
                    return;
                case CH_BS:
                    if (_cursorCol > 0) _cursorCol--;
                    return;
                case CH_DEL:
                    if (_cursorCol > 0)
                    {
                        _cursorCol--;
                        _cells[_cursorRow * Columns + _cursorCol] = ' ';
                    }
                    return;
                case CH_FF:
                    ClearScreen();
                    return;
            }
            if (c < ' ') return;
            _cells[_cursorRow * Columns + _cursorCol] = c;
            _cursorCol++;
            if (_cursorCol >= Columns)
            {
                _cursorCol = 0;
                LineFeed();
            }
        }

        /// <summary>Cursor down one row, column unchanged; scrolls at the bottom.</summary>
        private void LineFeed()
        {
            _cursorRow++;
            if (_cursorRow < Rows) return;
            _cursorRow = Rows - 1;
            Array.Copy(_cells, Columns, _cells, 0, (Rows - 1) * Columns);
            for (int c = 0; c < Columns; c++) _cells[(Rows - 1) * Columns + c] = ' ';
        }

        private void ClearScreen()
        {
            for (int i = 0; i < _cells.Length; i++) _cells[i] = ' ';
            _cursorRow = 0;
            _cursorCol = 0;
        }

        #endregion

        /// <summary>
        /// No NVRAM: losing power or being switched off wipes the whole terminal
        /// (screen, FIFO, flags, modes) — a power cycle is a full reset.
        /// </summary>
        public override void OnInteractableUpdated(Interactable interactable)
        {
            base.OnInteractableUpdated(interactable);
            if (interactable.Action != InteractableType.OnOff
                && interactable.Action != InteractableType.Powered)
            {
                return;
            }
            // Interactable states flap while a world loads (power network warm-up);
            // that is not a power cycle. _wasOperating is restored by DeserializeSave.
            if (GameManager.GameState != GameState.Running)
            {
                return;
            }
            bool operating = OnOff && Powered;
            if (_wasOperating && !operating && GameManager.RunSimulation)
            {
                ClearMemory();
            }
            _wasOperating = operating;
        }

        #region Player input

        public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
        {
            if (interactable.Action == InteractableType.Activate)
            {
                DelayedActionInstance action = new DelayedActionInstance
                {
                    Duration = 0f,
                    ActionMessage = interactable.ContextualName
                };
                if (!OnOff || !Powered)
                {
                    return action.Fail(GameStrings.DeviceNotOn);
                }
                if (!doAction)
                {
                    return action.Succeed();
                }
                if (!GameManager.IsBatchMode
                    && interaction.SourceThing is Entity entity && entity.IsLocalPlayer)
                {
                    OpenTerminalWindow(this);
                }
                return action.Succeed();
            }
            return base.InteractWith(interactable, interaction, doAction);
        }

        // NoInlining keeps TerminalWindow (whose ImGui base class the dedicated
        // server cannot load) out of InteractWith's JIT: InteractWith runs on the
        // server for every interaction, this helper only for the local player.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OpenTerminalWindow(SerialTerminalDevice device)
        {
            TerminalWindow.Open(device);
        }

        public override string GetContextualName(Interactable interactable)
        {
            if (interactable.Action == InteractableType.Activate)
            {
                return "Open Terminal";
            }
            return base.GetContextualName(interactable);
        }

        /// <summary>Local player pressed keys in the terminal window (raw, unbuffered).</summary>
        public void SubmitInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (GameManager.RunSimulation)
            {
                EnqueueInput(text);
            }
            else
            {
                new TerminalInputMessage
                {
                    TerminalId = ReferenceId,
                    Text = text
                }.SendToServer();
            }
        }

        /// <summary>Server side: queue raw keystrokes into the input FIFO, as-is.</summary>
        public void EnqueueInput(string text)
        {
            bool echoed;
            lock (_stateLock)
            {
                echoed = _localEcho;
                foreach (char raw in text)
                {
                    char c = raw > CH_DEL ? '?' : raw;
                    // Half-duplex: the keyboard controller prints directly, even
                    // when the FIFO is full - the glass is wired to the keyboard.
                    if (echoed) EchoChar(c);
                    if (_rx.Count >= RxCapacity)
                    {
                        _overflow = true;
                        continue;
                    }
                    _rx.Enqueue(c);
                }
            }
            if (echoed) MarkScreenDirty();
            MarkStatusDirty();
        }

        /// <summary>
        /// Local-echo rendering of one keystroke (caller must hold _stateLock):
        /// Enter (CR) echoes as a full newline, Backspace (BS) as a rubout.
        /// </summary>
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

        #endregion

        #region Logic types

        public override bool CanLogicRead(LogicType logicType)
        {
            if (logicType == LogicType.Quantity || logicType == LogicType.Error)
            {
                return true;
            }
            return base.CanLogicRead(logicType);
        }

        public override double GetLogicValue(LogicType logicType)
        {
            switch (logicType)
            {
                case LogicType.Quantity:
                    return DisplayRxCount;
                case LogicType.Error:
                    lock (_stateLock) return _overflow ? 1 : 0;
                default:
                    return base.GetLogicValue(logicType);
            }
        }

        public override void SetLogicValue(LogicType logicType, double value)
        {
            base.SetLogicValue(logicType, value);
            if (logicType == LogicType.Setting)
            {
                lock (_stateLock)
                {
                    PutPacked(value);
                }
                MarkScreenDirty();
            }
        }

        #endregion

        #region Dirty flags

        private void MarkScreenDirty()
        {
            if (NetworkManager.IsServer)
            {
                NetworkUpdateFlags |= ScreenNetworkFlag;
            }
            Interlocked.Increment(ref _version);
        }

        /// <summary>FIFO count / overflow changed but the screen did not.</summary>
        private void MarkStatusDirty()
        {
            if (NetworkManager.IsServer)
            {
                NetworkUpdateFlags |= StatusNetworkFlag;
            }
        }

        #endregion

        #region Network sync

        public override void BuildUpdate(RocketBinaryWriter writer, ushort networkUpdateType)
        {
            base.BuildUpdate(writer, networkUpdateType);
            if (Thing.IsNetworkUpdateRequired(ScreenNetworkFlag, networkUpdateType))
            {
                WriteScreenState(writer);
            }
            if (Thing.IsNetworkUpdateRequired(StatusNetworkFlag, networkUpdateType))
            {
                WriteStatusState(writer);
            }
        }

        public override void ProcessUpdate(RocketBinaryReader reader, ushort networkUpdateType)
        {
            base.ProcessUpdate(reader, networkUpdateType);
            if (Thing.IsNetworkUpdateRequired(ScreenNetworkFlag, networkUpdateType))
            {
                ReadScreenState(reader);
            }
            if (Thing.IsNetworkUpdateRequired(StatusNetworkFlag, networkUpdateType))
            {
                ReadStatusState(reader);
            }
        }

        public override void SerializeOnJoin(RocketBinaryWriter writer)
        {
            base.SerializeOnJoin(writer);
            WriteScreenState(writer);
            WriteStatusState(writer);
        }

        public override void DeserializeOnJoin(RocketBinaryReader reader)
        {
            base.DeserializeOnJoin(reader);
            ReadScreenState(reader);
            ReadStatusState(reader);
        }

        private void WriteScreenState(RocketBinaryWriter writer)
        {
            lock (_stateLock)
            {
                writer.WriteString(ScreenToString());
                writer.WriteByte((byte)_cursorRow);
                writer.WriteByte((byte)_cursorCol);
            }
        }

        private void ReadScreenState(RocketBinaryReader reader)
        {
            string text = reader.ReadString();
            byte row = reader.ReadByte();
            byte col = reader.ReadByte();
            lock (_stateLock)
            {
                ScreenFromString(text);
                _cursorRow = Mathf.Clamp(row, 0, Rows - 1);
                _cursorCol = Mathf.Clamp(col, 0, Columns - 1);
            }
            Interlocked.Increment(ref _version);
        }

        private void WriteStatusState(RocketBinaryWriter writer)
        {
            lock (_stateLock)
            {
                writer.WriteBoolean(_overflow);
                writer.WriteUInt16((ushort)_rx.Count);
            }
        }

        private void ReadStatusState(RocketBinaryReader reader)
        {
            bool overflow = reader.ReadBoolean();
            ushort rxCount = reader.ReadUInt16();
            lock (_stateLock)
            {
                _overflow = overflow;
                _syncedRxCount = rxCount;
            }
        }

        #endregion

        #region Save data

        public override ThingSaveData SerializeSave()
        {
            ThingSaveData saveData = new SerialTerminalSaveData();
            InitialiseSaveData(ref saveData);
            return saveData;
        }

        protected override void InitialiseSaveData(ref ThingSaveData savedData)
        {
            base.InitialiseSaveData(ref savedData);
            if (savedData is SerialTerminalSaveData data)
            {
                lock (_stateLock)
                {
                    data.ScreenText = ScreenToString();
                    data.InputBuffer = EscapeBuffer(new string(_rx.ToArray()));
                    data.CursorRow = _cursorRow;
                    data.CursorCol = _cursorCol;
                    data.Overflow = _overflow;
                    data.OutputBuffered = _outputBuffered;
                    data.InputBuffered = _inputBuffered;
                    data.LocalEcho = _localEcho;
                }
            }
        }

        public override void DeserializeSave(ThingSaveData savedData)
        {
            base.DeserializeSave(savedData);
            if (savedData is SerialTerminalSaveData data)
            {
                lock (_stateLock)
                {
                    ScreenFromString(data.ScreenText);
                    _cursorRow = Mathf.Clamp(data.CursorRow, 0, Rows - 1);
                    _cursorCol = Mathf.Clamp(data.CursorCol, 0, Columns - 1);
                    _overflow = data.Overflow;
                    _outputBuffered = data.OutputBuffered;
                    _inputBuffered = data.InputBuffered;
                    _localEcho = data.LocalEcho;
                    _rx.Clear();
                    if (!string.IsNullOrEmpty(data.InputBuffer))
                    {
                        foreach (char c in UnescapeBuffer(data.InputBuffer))
                        {
                            if (_rx.Count < RxCapacity) _rx.Enqueue(c);
                        }
                    }
                }
                // Interactable states (OnOff/Powered) are restored by the base
                // deserialize, so this reflects the state at save time.
                _wasOperating = OnOff && Powered;
                Interlocked.Increment(ref _version);
            }
        }

        #endregion

        // Control characters are not valid in XML 1.0, so the saved input FIFO
        // escapes them as \xNN (plus \\ for a literal backslash). Legacy saves
        // used \n for newlines; UnescapeBuffer still accepts that.
        private static string EscapeBuffer(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c < ' ' || c == CH_DEL) sb.Append("\\x").Append(((int)c).ToString("x2"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string UnescapeBuffer(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i == text.Length - 1)
                {
                    sb.Append(c);
                    continue;
                }
                char next = text[++i];
                switch (next)
                {
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'x':
                        if (i + 2 < text.Length
                            && int.TryParse(text.Substring(i + 1, 2),
                                System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            sb.Append((char)code);
                            i += 2;
                        }
                        break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }

        #region Screen <-> string (callers must hold _stateLock)

        private string ScreenToString()
        {
            StringBuilder sb = new StringBuilder(_cells.Length + Rows);
            for (int r = 0; r < Rows; r++)
            {
                int end = Columns;
                while (end > 0 && _cells[r * Columns + end - 1] == ' ') end--;
                sb.Append(_cells, r * Columns, end);
                if (r < Rows - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        private void ScreenFromString(string text)
        {
            ClearScreen();
            if (string.IsNullOrEmpty(text)) return;
            string[] lines = text.Split('\n');
            for (int r = 0; r < Rows && r < lines.Length; r++)
            {
                string line = lines[r];
                for (int c = 0; c < Columns && c < line.Length; c++)
                {
                    _cells[r * Columns + c] = line[c];
                }
            }
        }

        #endregion

        public override StringBuilder GetExtendedText()
        {
            StringBuilder sb = base.GetExtendedText();
            int rxCount;
            bool overflow;
            lock (_stateLock)
            {
                rxCount = GameManager.RunSimulation ? _rx.Count : _syncedRxCount;
                overflow = _overflow;
            }
            sb.Append("Input Buffer ").AppendLine((rxCount + "/" + RxCapacity).AsColor("yellow"));
            if (overflow)
            {
                sb.AppendLine("Input Overflow".AsColor("red"));
            }
            return sb;
        }
    }
}
