using System.Diagnostics.CodeAnalysis;
using Assets.Scripts.Objects;
using HarmonyLib;
using SerialTerminal.Prefabs;

namespace SerialTerminal.Patches
{
    /// <summary>
    /// The mod's only Harmony patch. Prefab cloning must run at Prefab.LoadAll
    /// time - the earliest point where WorldManager.Instance.SourcePrefabs is
    /// guaranteed populated (LaunchPadBooster's own PrefabPatch uses the same
    /// hook, and clone-and-swap cannot go through Mod.SetupPrefabs because
    /// setups only run over prefabs already registered via AddPrefabs).
    /// </summary>
    [HarmonyPatch(typeof(Prefab), nameof(Prefab.LoadAll))]
    [SuppressMessage("Design", "MA0182:Avoid unused internal types",
        Justification = "Discovered and applied by Harmony's PatchAll attribute scan")]
    internal static class PrefabLoadAllPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            PrefabFactory.CreateAll();
        }
    }
}
