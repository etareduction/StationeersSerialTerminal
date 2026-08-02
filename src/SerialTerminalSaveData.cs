using System.Xml.Serialization;
using Assets.Scripts.Objects.Electrical;

namespace SerialTerminal
{
    [XmlInclude(typeof(SerialTerminalSaveData))]
    public class SerialTerminalSaveData : LogicBaseSaveData
    {
        [XmlElement]
        public string ScreenText;

        [XmlElement]
        public string InputBuffer;

        [XmlElement]
        public int CursorRow;

        [XmlElement]
        public int CursorCol;

        [XmlElement]
        public bool Overflow;
    }
}
