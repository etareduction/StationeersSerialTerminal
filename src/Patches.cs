using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.UI.ImGuiUi;
using HarmonyLib;
using UnityEngine;

namespace SerialTerminal
{
    [HarmonyPatch(typeof(Prefab), nameof(Prefab.LoadAll))]
    internal static class PrefabLoadAllPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            PrefabFactory.CreateAll();
        }
    }

    /// <summary>
    /// LogicDisplay.SetDisplay is not virtual; every vanilla repaint (power, mode,
    /// color, setting, registration) ends there. Redirect it to the terminal renderer
    /// for our devices so the numeric readout never overwrites the terminal grid.
    /// </summary>
    [HarmonyPatch(typeof(Assets.Scripts.Objects.Electrical.LogicDisplay),
        nameof(Assets.Scripts.Objects.Electrical.LogicDisplay.SetDisplay))]
    internal static class LogicDisplaySetDisplayPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Assets.Scripts.Objects.Electrical.LogicDisplay __instance)
        {
            if (__instance is SerialTerminalDevice terminal)
            {
                terminal.RenderTerminalNow();
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The game drives its ImGui frame from ImGuiManager.LateUpdate; the creative
    /// spawn menu's Draw runs inside that frame on every gameplay path, so a postfix
    /// on it is the standard per-frame hook for mod ImGui windows (same trick the
    /// IC10Editor mod uses).
    /// </summary>
    [HarmonyPatch(typeof(ImguiCreativeSpawnMenu), nameof(ImguiCreativeSpawnMenu.Draw))]
    internal static class ImGuiFramePatch
    {
        private static bool _errorLogged;

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                TerminalWindow.Draw();
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    SerialTerminalPlugin.Log.LogError("Terminal window draw failed: " + e);
                }
            }
        }
    }

    /// <summary>
    /// Fallback display names/descriptions in case the mod's GameData/Language XML was
    /// not merged (e.g. the DLL was dropped somewhere without the mod folder). XML wins:
    /// entries are only added when missing.
    /// </summary>
    [HarmonyPatch(typeof(Localization.LanguageFolder), nameof(Localization.LanguageFolder.LoadAll))]
    internal static class LocalizationFallbackPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            AddIfMissing(PrefabFactory.TerminalPrefabName, "Serial Terminal",
                "The Norsec TTY-6 serial terminal.");
            AddIfMissing(PrefabFactory.KitPrefabName, "Kit (Serial Terminal)",
                "This kit places a Norsec TTY-6 serial terminal.");
        }

        private static void AddIfMissing(string prefabName, string displayName, string description)
        {
            var things = (Dictionary<int, Localization.LocalizationThingDat>)AccessTools
                .Field(typeof(Localization), "ThingLocalized").GetValue(null);
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
