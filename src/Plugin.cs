using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;

namespace SerialTerminal
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class SerialTerminalPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.etareduction.serialterminal";
        public const string NAME = "SerialTerminal";
        public const string VERSION = "0.2.0";

        public static readonly Mod MOD = new Mod(NAME, VERSION);

        internal static ManualLogSource Log;
        private static bool _initialized;

        // Terminal grid and screen tuning (visual constants that can't be known
        // without seeing the panel in-game; safe to tweak without a rebuild).
        internal static ConfigEntry<int> Rows;
        internal static ConfigEntry<int> Columns;
        internal static ConfigEntry<string> SourcePrefab;
        internal static ConfigEntry<int> ScreenTextureSize;
        internal static ConfigEntry<float> ScreenAspect;
        internal static ConfigEntry<float> ScreenWidth;
        internal static ConfigEntry<float> ScreenZOffset;
        internal static ConfigEntry<int> FontIndex;
        internal static ConfigEntry<string> TextColor;
        internal static ConfigEntry<float> CloseDistance;

        private void Awake()
        {
            if (_initialized)
            {
                Logger.LogWarning("SerialTerminal loaded twice - is the DLL present in more than one location?");
                return;
            }
            _initialized = true;
            Log = Logger;

            Rows = Config.Bind("Terminal", "Rows", 20,
                "Number of text rows on the terminal screen.");
            Columns = Config.Bind("Terminal", "Columns", 40,
                "Number of text columns on the terminal screen.");
            SourcePrefab = Config.Bind("Screen", "SourcePrefab", "StructureComputer",
                "Vanilla structure prefab to clone for the terminal body. StructureComputer"
                + " is the modern computer; LogicDisplay prefabs also work.");
            ScreenTextureSize = Config.Bind("Screen", "TextureSize", 512,
                "Resolution of the render texture shown on the console surface.");
            ScreenAspect = Config.Bind("Screen", "Aspect", 1.0f,
                "Screen quad height as a fraction of its width.");
            ScreenWidth = Config.Bind("Screen", "Width", 0f,
                "Screen quad width in meters. 0 = use the display's MaxPixelWidth.");
            ScreenZOffset = Config.Bind("Screen", "ZOffset", 0.002f,
                "Offset of the screen quad along the panel normal (can be negative).");
            FontIndex = Config.Bind("UI", "FontIndex", 0,
                "Index into the game's ImGui font atlas used for terminal text.");
            TextColor = Config.Bind("UI", "TextColor", "#33FF33",
                "Terminal text color as #RRGGBB.");
            CloseDistance = Config.Bind("UI", "CloseDistance", 8f,
                "Terminal window auto-closes when the player is farther than this (meters).");

            MOD.AddSaveDataType<SerialTerminalSaveData>();
            MOD.Networking.RegisterLegacyMessage<TerminalInputMessage>();

            new Harmony(GUID).PatchAll(typeof(SerialTerminalPlugin).Assembly);
            Log.LogInfo("SerialTerminal " + VERSION + " initialized");
        }
    }
}
