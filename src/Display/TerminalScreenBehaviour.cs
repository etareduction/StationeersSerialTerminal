using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using SerialTerminal.Devices;
using UnityEngine;

namespace SerialTerminal.Display
{
    /// <summary>
    /// Lives next to SerialTerminalDevice on the prefab; carries the serialized
    /// screen pose captured at prefab-build time and, on clients, hands off to
    /// the ImGui renderer component. Deliberately free of ImGui-typed members:
    /// the dedicated server ships no RG.ImGui assemblies, and one such field is
    /// enough to make this class - and with it the whole prefab build - fail to
    /// load there (TypeLoadException at AddComponent).
    /// </summary>
    public class TerminalScreenBehaviour : MonoBehaviour
    {
        // Set at prefab-build time (PrefabFactory): pose + size of the monitor face,
        // taken from the vanilla Computer's world-space UI canvas. Serialized so
        // every spawned instance inherits them.
        public Transform ScreenAnchor;
        public float ScreenWorldWidth;
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

        // NoInlining keeps TerminalScreenRenderer (ImGui-typed fields) out of this
        // method's JIT: on the server this is never called, so the type never loads.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AttachRenderer()
        {
            gameObject.AddComponent<TerminalScreenRenderer>();
        }
    }
}
