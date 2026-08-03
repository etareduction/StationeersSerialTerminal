using System.Xml.Serialization;
using Assets.Scripts.Objects.Electrical;

namespace SerialTerminal.Devices
{
    /// <summary>
    /// Registered with the save serializer via MOD.AddSaveDataType in Plugin.Awake.
    /// </summary>
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

        [XmlElement]
        public bool OutputBuffered;

        [XmlElement]
        public bool InputBuffered;

        [XmlElement]
        public bool LocalEcho;
    }
}
