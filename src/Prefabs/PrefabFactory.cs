using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using LaunchPadBooster.Utils;
using SerialTerminal.Devices;
using UnityEngine;

namespace SerialTerminal.Prefabs
{
    /// <summary>
    /// Builds the mod's prefabs at Prefab.LoadAll time by cloning vanilla ones
    /// (the community "mirrored devices" pattern - no Unity editor, no asset
    /// bundles). Orchestration only: source lookup, registration and logging;
    /// the build steps live in TerminalPrefabBuilder and PrefabSurgery.
    /// </summary>
    public static class PrefabFactory
    {
        public const string TerminalPrefabName = "StructureSerialTerminal";
        public const string KitPrefabName = "ItemKitSerialTerminal";

        /// <summary>"Computer (Modern)" — the only supported clone source.</summary>
        public const string SourcePrefabName = "StructureComputer";

        public const string SourceKitName = "ItemKitComputer";

        private static bool _created;

        public static void CreateAll()
        {
            if (_created)
            {
                return;
            }
            if (WorldManager.Instance == null)
            {
                SerialTerminalPlugin.Log.LogError("WorldManager not available; cannot register prefabs");
                return;
            }
            List<Thing> sourcePrefabs = WorldManager.Instance.SourcePrefabs;
            if (PrefabUtils.FindPrefab(Animator.StringToHash(TerminalPrefabName)) != null)
            {
                _created = true;
                return;
            }

            Computer sourceComputer = PrefabUtils.FindPrefab<Computer>(SourcePrefabName);
            MultiConstructor sourceKit = PrefabUtils.FindPrefab<MultiConstructor>(SourceKitName);
            if (sourceComputer == null || sourceKit == null)
            {
                SerialTerminalPlugin.Log.LogError(
                    $"Source prefabs not found (computer={sourceComputer != null}, kit={sourceKit != null}); has the game renamed them?");
                return;
            }
            // The monitor canvas provides the screen pose and the Activate hitbox;
            // without it the terminal cannot be built. No fallback source: fail
            // loudly and register nothing until the mod is updated for the game.
            if (sourceComputer.ComputerScreen == null
                || !sourceComputer.ComputerScreen.TryGetComponent(out RectTransform _))
            {
                SerialTerminalPlugin.Log.LogError(
                    $"{SourcePrefabName} has no monitor canvas to anchor the screen; the game layout changed and the mod needs an update");
                return;
            }

            try
            {
                GameObject root = new("~SerialTerminalMod");
                root.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(root);

                SerialTerminalDevice terminal = TerminalPrefabBuilder.CreateTerminal(sourceComputer, root.transform);
                MultiConstructor kit = TerminalPrefabBuilder.CreateKit(sourceKit, terminal, root.transform);

                // Deconstructing the terminal must hand back our kit, not Kit (Consoles).
                foreach (BuildState state in terminal.BuildStates)
                {
                    if (state?.Tool != null && state.Tool.ToolExit != null)
                    {
                        state.Tool.ToolExit = kit;
                    }
                }

                // AddPrefabs registers with the SDK (flags the mod as required for
                // multiplayer join validation) and appends to SourcePrefabs on the
                // NEXT Prefab.LoadAll - our prefix is inside the current one, so the
                // direct adds below cover it. Both sides dedupe.
                SerialTerminalPlugin.MOD.AddPrefabs([terminal.gameObject, kit.gameObject]);
                if (!sourcePrefabs.Contains(terminal)) sourcePrefabs.Add(terminal);
                if (!sourcePrefabs.Contains(kit)) sourcePrefabs.Add(kit);
                _created = true;
            }
            catch (Exception e)
            {
                SerialTerminalPlugin.Log.LogError("Failed to create prefabs: " + e);
            }
        }

    }
}
