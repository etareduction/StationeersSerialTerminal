using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.UI;
using Assets.Scripts.Util;
using HarmonyLib;
using UnityEngine;

namespace SerialTerminal
{
    /// <summary>
    /// Norsec TTY-6 serial terminal. A dumb glass teletype: IC10 circuits talk to it
    /// through a 4-register memory-mapped UART (get/put), players type on it through
    /// the vanilla keyboard input window.
    /// </summary>
    public class SerialTerminalDevice : LogicDisplay, IMemoryReadable, IMemoryWritable
    {
        // UART register map (see DESIGN.md)
        private const int ADDR_DATA = 0;   // r: pop input char / w: print char
        private const int ADDR_STR = 1;    // r: peek input char / w: print packed ascii-6
        private const int ADDR_COUNT = 2;  // r: input chars available
        private const int ADDR_CTRL = 3;   // r: status flags / w: 1 clear screen, 2 flush input, 3 clear overflow
        private const int REGISTER_COUNT = 4;

        private const int CTRL_CLEAR_SCREEN = 1;
        private const int CTRL_FLUSH_INPUT = 2;
        private const int CTRL_CLEAR_OVERFLOW = 3;

        public const int RxCapacity = 256;
        private const ushort ScreenNetworkFlag = 512;

        private static readonly AccessTools.FieldRef<LogicDisplay, List<DigitGlyph>> DigitGlyphsRef =
            AccessTools.FieldRefAccess<LogicDisplay, List<DigitGlyph>>("_digitGlyphs");

        private readonly object _stateLock = new object();
        private readonly Queue<char> _rx = new Queue<char>();
        private char[] _cells = new char[0];
        private int _rows = 1;
        private int _cols = 1;
        private int _cursorRow;
        private int _cursorCol;
        private bool _overflow;

        // Incremented on every visible state change; the ImGui window and the
        // in-world screen renderer poll it to know when to repaint.
        private int _version;
        private string[] _lineCache;
        private int _lineCacheVersion = -1;
        private int _cacheCursorRow;
        private int _cacheCursorCol;

        public int ScreenVersion => _version;

        public int RowCount => _rows;

        public int ColumnCount => _cols;

        public override void Awake()
        {
            base.Awake();
            int rows = Mathf.Clamp(SerialTerminalPlugin.Rows.Value, 4, 64);
            int cols = Mathf.Clamp(SerialTerminalPlugin.Columns.Value, 8, 128);
            lock (_stateLock)
            {
                ResizeGrid(rows, cols);
            }
            // Vanilla numeric readout must never draw; SetDisplay is prefix-blocked
            // for this type, so once cleared the list stays empty.
            List<DigitGlyph> glyphs = DigitGlyphsRef(this);
            if (glyphs != null)
            {
                glyphs.Clear();
            }
        }

        /// <summary>
        /// Main-thread snapshot of the screen for drawing. Rebuilt only when the
        /// version changed since the last call.
        /// </summary>
        public string[] SnapshotLines(out int cursorRow, out int cursorCol)
        {
            int version = _version;
            if (_lineCache == null || _lineCacheVersion != version)
            {
                lock (_stateLock)
                {
                    string[] lines = new string[_rows];
                    for (int r = 0; r < _rows; r++)
                    {
                        lines[r] = new string(_cells, r * _cols, _cols);
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

        private void ResizeGrid(int rows, int cols)
        {
            char[] cells = new char[rows * cols];
            for (int i = 0; i < cells.Length; i++) cells[i] = ' ';
            if (_cells.Length > 0)
            {
                int copyRows = Mathf.Min(rows, _rows);
                int copyCols = Mathf.Min(cols, _cols);
                for (int r = 0; r < copyRows; r++)
                    for (int c = 0; c < copyCols; c++)
                        cells[r * cols + c] = _cells[r * _cols + c];
            }
            _cells = cells;
            _rows = rows;
            _cols = cols;
            _cursorRow = Mathf.Clamp(_cursorRow, 0, rows - 1);
            _cursorCol = Mathf.Clamp(_cursorCol, 0, cols - 1);
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
                        char c = _rx.Dequeue();
                        MarkScreenDirty();
                        return c;
                    }
                    case ADDR_STR:
                        return _rx.Count > 0 ? _rx.Peek() : 0;
                    case ADDR_COUNT:
                        return _rx.Count;
                    case ADDR_CTRL:
                        return (_rx.Count > 0 ? 1 : 0) | (_overflow ? 2 : 0);
                    default:
                        throw new StackUnderflowException();
                }
            }
        }

        public void WriteMemory(int address, double value)
        {
            lock (_stateLock)
            {
                switch (address)
                {
                    case ADDR_DATA:
                    {
                        int code = (int)value;
                        if (code > 0 && code < 256) PutChar((char)code);
                        break;
                    }
                    case ADDR_STR:
                        PutString(ProgrammableChip.UnpackAscii6(value, signed: true));
                        break;
                    case ADDR_COUNT:
                        throw new StackOverflowException();
                    case ADDR_CTRL:
                        switch ((int)value)
                        {
                            case CTRL_CLEAR_SCREEN: ClearScreen(); break;
                            case CTRL_FLUSH_INPUT: _rx.Clear(); _overflow = false; break;
                            case CTRL_CLEAR_OVERFLOW: _overflow = false; break;
                        }
                        break;
                    default:
                        throw new StackOverflowException();
                }
            }
            MarkScreenDirty();
        }

        public void ClearMemory()
        {
            lock (_stateLock)
            {
                ClearScreen();
                _rx.Clear();
                _overflow = false;
            }
            MarkScreenDirty();
        }

        #endregion

        #region Terminal emulation (callers must hold _stateLock)

        private void PutString(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text) PutChar(c);
        }

        private void PutChar(char c)
        {
            switch (c)
            {
                case '\n':
                    NewLine();
                    return;
                case '\r':
                    _cursorCol = 0;
                    return;
                case '\b':
                    if (_cursorCol > 0) _cursorCol--;
                    else if (_cursorRow > 0) { _cursorRow--; _cursorCol = _cols - 1; }
                    _cells[_cursorRow * _cols + _cursorCol] = ' ';
                    return;
                case '\f':
                    ClearScreen();
                    return;
            }
            if (c < ' ') return;
            _cells[_cursorRow * _cols + _cursorCol] = c;
            _cursorCol++;
            if (_cursorCol >= _cols) NewLine();
        }

        private void NewLine()
        {
            _cursorCol = 0;
            _cursorRow++;
            if (_cursorRow < _rows) return;
            _cursorRow = _rows - 1;
            Array.Copy(_cells, _cols, _cells, 0, (_rows - 1) * _cols);
            for (int c = 0; c < _cols; c++) _cells[(_rows - 1) * _cols + c] = ' ';
        }

        private void ClearScreen()
        {
            for (int i = 0; i < _cells.Length; i++) _cells[i] = ' ';
            _cursorRow = 0;
            _cursorCol = 0;
        }

        #endregion

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
                if (!GameManager.IsBatchMode && IsLocalPlayerInteraction(interaction))
                {
                    TerminalWindow.Open(this);
                }
                return action.Succeed();
            }
            return base.InteractWith(interactable, interaction, doAction);
        }

        public override string GetContextualName(Interactable interactable)
        {
            if (interactable.Action == InteractableType.Activate)
            {
                return "Open Terminal";
            }
            return base.GetContextualName(interactable);
        }

        private static bool IsLocalPlayerInteraction(Interaction interaction)
        {
            Human local = InventoryManager.ParentHuman;
            if (local == null || local.OrganBrain == null) return false;
            Entity source = interaction.SourceThing as Entity;
            return source != null && source.OrganBrain != null
                && source.OrganBrain.ClientId == local.OrganBrain.ClientId;
        }

        /// <summary>Local player typed a line in the terminal window.</summary>
        public void SubmitLine(string text)
        {
            if (text == null) return;
            if (GameManager.RunSimulation)
            {
                EnqueueInputLine(text);
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

        /// <summary>Server side: queue a typed line (with trailing newline) into the input FIFO.</summary>
        public void EnqueueInputLine(string text)
        {
            lock (_stateLock)
            {
                foreach (char raw in text + "\n")
                {
                    char c = raw > '\u007f' ? '?' : raw;
                    if (_rx.Count >= RxCapacity)
                    {
                        _overflow = true;
                        break;
                    }
                    _rx.Enqueue(c);
                }
            }
            MarkScreenDirty();
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
                    lock (_stateLock) return _rx.Count;
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
                    PutString(ProgrammableChip.UnpackAscii6(value, signed: true));
                }
                MarkScreenDirty();
            }
        }

        #endregion

        #region Rendering

        // Every vanilla repaint path (power/mode/color changes, registration, setting
        // changes) funnels through LogicDisplay.SetDisplay, which is not virtual - a
        // Harmony prefix in Patches.cs redirects those calls here. The vanilla body
        // must never run: it would fill the digit glyph list with the numeric readout.
        internal void RenderTerminalNow()
        {
            List<DigitGlyph> glyphs = DigitGlyphsRef(this);
            if (glyphs != null)
            {
                glyphs.Clear();
            }
            Interlocked.Increment(ref _version);
        }

        private void MarkScreenDirty()
        {
            if (NetworkManager.IsServer)
            {
                NetworkUpdateFlags |= ScreenNetworkFlag;
            }
            Interlocked.Increment(ref _version);
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
        }

        public override void ProcessUpdate(RocketBinaryReader reader, ushort networkUpdateType)
        {
            base.ProcessUpdate(reader, networkUpdateType);
            if (Thing.IsNetworkUpdateRequired(ScreenNetworkFlag, networkUpdateType))
            {
                ReadScreenState(reader);
            }
        }

        public override void SerializeOnJoin(RocketBinaryWriter writer)
        {
            base.SerializeOnJoin(writer);
            WriteScreenState(writer);
        }

        public override void DeserializeOnJoin(RocketBinaryReader reader)
        {
            base.DeserializeOnJoin(reader);
            ReadScreenState(reader);
        }

        private void WriteScreenState(RocketBinaryWriter writer)
        {
            lock (_stateLock)
            {
                writer.WriteString(ScreenToString());
                writer.WriteByte((byte)_cursorRow);
                writer.WriteByte((byte)_cursorCol);
                writer.WriteBoolean(_overflow);
            }
        }

        private void ReadScreenState(RocketBinaryReader reader)
        {
            string text = reader.ReadString();
            byte row = reader.ReadByte();
            byte col = reader.ReadByte();
            bool overflow = reader.ReadBoolean();
            lock (_stateLock)
            {
                ScreenFromString(text);
                _cursorRow = Mathf.Clamp(row, 0, _rows - 1);
                _cursorCol = Mathf.Clamp(col, 0, _cols - 1);
                _overflow = overflow;
            }
            Interlocked.Increment(ref _version);
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
                    data.InputBuffer = new string(_rx.ToArray()).Replace("\n", "\\n");
                    data.CursorRow = _cursorRow;
                    data.CursorCol = _cursorCol;
                    data.Overflow = _overflow;
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
                    _cursorRow = Mathf.Clamp(data.CursorRow, 0, _rows - 1);
                    _cursorCol = Mathf.Clamp(data.CursorCol, 0, _cols - 1);
                    _overflow = data.Overflow;
                    _rx.Clear();
                    if (!string.IsNullOrEmpty(data.InputBuffer))
                    {
                        foreach (char c in data.InputBuffer.Replace("\\n", "\n"))
                        {
                            if (_rx.Count < RxCapacity) _rx.Enqueue(c);
                        }
                    }
                }
                Interlocked.Increment(ref _version);
            }
        }

        #endregion

        #region Screen <-> string (callers must hold _stateLock)

        private string ScreenToString()
        {
            StringBuilder sb = new StringBuilder(_cells.Length + _rows);
            for (int r = 0; r < _rows; r++)
            {
                int end = _cols;
                while (end > 0 && _cells[r * _cols + end - 1] == ' ') end--;
                sb.Append(_cells, r * _cols, end);
                if (r < _rows - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        private void ScreenFromString(string text)
        {
            ClearScreen();
            if (string.IsNullOrEmpty(text)) return;
            string[] lines = text.Split('\n');
            for (int r = 0; r < _rows && r < lines.Length; r++)
            {
                string line = lines[r];
                for (int c = 0; c < _cols && c < line.Length; c++)
                {
                    _cells[r * _cols + c] = line[c];
                }
            }
        }

        #endregion

        public override StringBuilder GetExtendedText()
        {
            StringBuilder sb = base.GetExtendedText();
            lock (_stateLock)
            {
                sb.Append("Input Buffer ").AppendLine((_rx.Count + "/" + RxCapacity).AsColor("yellow"));
                if (_overflow)
                {
                    sb.AppendLine("Input Overflow".AsColor("red"));
                }
            }
            return sb;
        }
    }
}
