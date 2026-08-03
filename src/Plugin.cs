using System.Collections.Generic;
using Assets.Scripts;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using UnityEngine;

namespace SerialTerminal
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class SerialTerminalPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.etareduction.serialterminal";
        public const string NAME = "SerialTerminal";
        public const string VERSION = "0.4.3";

        public static readonly Mod MOD = new Mod(NAME, VERSION);

        internal static ManualLogSource Log;
        private static bool _initialized;

        // No public API adds thing localization entries; the dictionary itself is
        // private, so this one reflection read stays.
        private static readonly AccessTools.FieldRef<Dictionary<int, Localization.LocalizationThingDat>> ThingLocalizedRef =
            AccessTools.StaticFieldRefAccess<Dictionary<int, Localization.LocalizationThingDat>>(
                AccessTools.Field(typeof(Localization), "ThingLocalized"));

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

            // LanguageFolder.LoadAll (its only caller is SetLanguage) rebuilds the
            // localization tables and fires OnLanguageChanged right after; run once
            // immediately too, in case the initial load happened before this plugin.
            Localization.OnLanguageChanged += AddLocalizationFallback;
            AddLocalizationFallback();

            new Harmony(GUID).PatchAll(typeof(SerialTerminalPlugin).Assembly);
            Log.LogInfo("SerialTerminal " + VERSION + " initialized");
        }

        /// <summary>
        /// Fallback display names/descriptions in case the mod's GameData/Language XML
        /// was not merged (e.g. the DLL was dropped somewhere without the mod folder).
        /// XML wins: entries are only added when missing.
        /// </summary>
        private static void AddLocalizationFallback()
        {
            AddIfMissing(PrefabFactory.TerminalPrefabName, "Serial Terminal",
                "The Norsec TTY-6 serial terminal.");
            AddIfMissing(PrefabFactory.KitPrefabName, "Kit (Serial Terminal)",
                "This kit places a Norsec TTY-6 serial terminal.");
        }

        private static void AddIfMissing(string prefabName, string displayName, string description)
        {
            Dictionary<int, Localization.LocalizationThingDat> things = ThingLocalizedRef();
            int key = Animator.StringToHash(prefabName);
            if (!things.ContainsKey(key))
            {
                things[key] = new Localization.LocalizationThingDat
                {
                    PrefabName = displayName,
                    Description = description
                };
            }
        }
    }
}
