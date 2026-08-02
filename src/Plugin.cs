using BepInEx;
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
        public const string VERSION = "0.3.0";

        public static readonly Mod MOD = new Mod(NAME, VERSION);

        internal static ManualLogSource Log;
        private static bool _initialized;

        private void Awake()
        {
            if (_initialized)
            {
                Logger.LogWarning("SerialTerminal loaded twice - is the DLL present in more than one location?");
                return;
            }
            _initialized = true;
            Log = Logger;

            MOD.AddSaveDataType<SerialTerminalSaveData>();
            MOD.Networking.RegisterLegacyMessage<TerminalInputMessage>();

            new Harmony(GUID).PatchAll(typeof(SerialTerminalPlugin).Assembly);
            Log.LogInfo("SerialTerminal " + VERSION + " initialized");
        }
    }
}
