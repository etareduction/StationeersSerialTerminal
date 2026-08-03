using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using SerialTerminal.Display;
using UnityEngine;

namespace SerialTerminal.Devices
{
    /// <summary>
    /// Lives next to SerialTerminalDevice on the prefab; carries the serialized
    /// screen pose captured at prefab-build time and, on clients, hands off to
    /// the ImGui renderer component. Deliberately free of ImGui-typed members:
    /// the dedicated server ships no RG.ImGui assemblies, and one such field is
    /// enough to make this class - and with it the whole prefab build - fail to
    /// load there (TypeLoadException at AddComponent). That server-safety is
    /// also why it sits in Devices, not Display: everything in Display is
    /// client-only by rule.
    /// </summary>
    public class TerminalScreenBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Set at prefab-build time (PrefabFactory): pose of the monitor face, taken
        /// from the vanilla Computer's world-space UI canvas. Serialized so every
        /// spawned instance inherits it.
        /// </summary>
        public Transform ScreenAnchor;

        /// <summary>Monitor face width in meters, captured with the anchor.</summary>
        public float ScreenWorldWidth;

        /// <summary>Monitor face height in meters, captured with the anchor.</summary>
        public float ScreenWorldHeight;

        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
            Justification = "Unity message, called by the engine")]
        private void LateUpdate()
        {
            if (!GameManager.IsBatchMode && GetComponent<SerialTerminalDevice>() != null)
            {
                AttachRenderer();
            }
            enabled = false;
        }

        /// <summary>
        /// NoInlining keeps TerminalScreenRenderer (ImGui-typed fields) out of this
        /// method's JIT: on the server this is never called, so the type never loads.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AttachRenderer()
        {
            _ = gameObject.AddComponent<TerminalScreenRenderer>();
        }
    }
}
