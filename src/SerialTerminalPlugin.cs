using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ImGuiNET.Unity;
using LaunchPadBooster;
using SerialTerminal.Devices;
using SerialTerminal.Display;
using SerialTerminal.Networking;
using UnityEngine;

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

            Harmony harmony = new(GUID);
            harmony.PatchAll(typeof(SerialTerminalPlugin).Assembly);
            // Application.isBatchMode is reliable this early; GameManager's
            // flag (set on engine init) additionally covers server-platform
            // builds launched without -batchmode.
            if (!Application.isBatchMode && !GameManager.IsBatchMode)
            {
                PatchFontAtlas(harmony);
            }
            Log.LogInfo($"SerialTerminal {VERSION} initialized");
        }

        /// <summary>
        /// NoInlining keeps the ImGui types (which the dedicated server cannot
        /// load) out of Awake's JIT; the font patch applies on clients only.
        /// </summary>
        /// <param name="harmony">The plugin's Harmony instance.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PatchFontAtlas(Harmony harmony)
        {
            _ = harmony.Patch(
                AccessTools.Method(typeof(TextureManager), nameof(TextureManager.BuildFontAtlas)),
                postfix: new HarmonyMethod(typeof(TerminalFontAtlas), nameof(TerminalFontAtlas.AfterBuildFontAtlas)));
        }
    }
}
