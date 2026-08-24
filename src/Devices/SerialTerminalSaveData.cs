using System.Xml.Serialization;
using Assets.Scripts.Objects.Electrical;
using SerialTerminal.Core;

namespace SerialTerminal.Devices
{
    /// <summary>
    /// Registered with the save serializer via MOD.AddSaveDataType in Plugin.Awake.
    /// Owns the mapping to and from <see cref="TerminalMemento"/>, including the
    /// \xNN escaping that keeps control characters out of the save XML.
    /// </summary>
    public class SerialTerminalSaveData : LogicBaseSaveData
    {
        [XmlElement]
        public string ScreenText;

        /// <summary>Colour plane for ScreenText; absent in pre-colour saves.</summary>
        [XmlElement]
        public string ScreenColors;

        [XmlElement]
        public string InputBuffer;

        [XmlElement]
        public int CursorRow;

        [XmlElement]
        public int CursorCol;

        [XmlElement]
        public bool Overflow;

        [XmlElement]
        public bool OutputBuffered;

        [XmlElement]
        public bool InputBuffered;

        [XmlElement]
        public bool LocalEcho;

        /// <summary>-1 so pre-colour saves load with the default pen.</summary>
        [XmlElement]
        public int PenColor = -1;

        /// <summary>Fills the XML fields from a captured terminal state.</summary>
        /// <param name="memento">State captured at save time.</param>
        internal void CopyFrom(TerminalMemento memento)
        {
            ScreenText = memento.Screen.Text;
            ScreenColors = memento.Screen.Colors;
            InputBuffer = InputBufferEscape.Escape(memento.InputBuffer);
            CursorRow = memento.Screen.CursorRow;
            CursorCol = memento.Screen.CursorCol;
            Overflow = memento.Overflow;
            OutputBuffered = memento.OutputBuffered;
            InputBuffered = memento.InputBuffered;
            LocalEcho = memento.LocalEcho;
            PenColor = memento.PenColor;
        }

        /// <summary>The saved state as an immutable memento, ready to restore.</summary>
        internal TerminalMemento ToMemento()
        {
            return new TerminalMemento
            {
                Screen = new ScreenContent
                {
                    Text = ScreenText,
                    Colors = ScreenColors,
                    CursorRow = CursorRow,
                    CursorCol = CursorCol
                },
                InputBuffer = string.IsNullOrEmpty(InputBuffer)
                    ? string.Empty
                    : InputBufferEscape.Unescape(InputBuffer),
                Overflow = Overflow,
                OutputBuffered = OutputBuffered,
                InputBuffered = InputBuffered,
                LocalEcho = LocalEcho,
                PenColor = PenColor
            };
        }
    }
}
