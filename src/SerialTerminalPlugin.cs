using System.Diagnostics.CodeAnalysis;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using SerialTerminal.Devices;
using SerialTerminal.Networking;

namespace SerialTerminal
{
    /// <summary>
    /// Display names and descriptions come exclusively from the mod folder's
    /// GameData/Language XML; a bare DLL install shows raw prefab names, which
    /// is the intended signal that the install is broken.
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class SerialTerminalPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.etareduction.serialterminal";
        public const string NAME = "SerialTerminal";
        public const string VERSION = "0.5.0";

        public static readonly Mod MOD = new(NAME, VERSION);

        internal static ManualLogSource Log;
        private static bool _initialized;

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Disposing the Harmony instance would unpatch; patches must live for the process lifetime")]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
            Justification = "Unity message, called by the engine")]
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
            MOD.Networking.RegisterMessage<TerminalInputMessage>();

            new Harmony(GUID).PatchAll(typeof(SerialTerminalPlugin).Assembly);
            Log.LogInfo($"SerialTerminal {VERSION} initialized");
        }
    }
}
