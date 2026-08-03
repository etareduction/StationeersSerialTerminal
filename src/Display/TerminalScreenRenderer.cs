using System.Diagnostics.CodeAnalysis;
using SerialTerminal.Core;
using SerialTerminal.Devices;
using UnityEngine;
using UnityEngine.Rendering;

namespace SerialTerminal.Display
{
    /// <summary>
    /// Unity wiring for one in-world terminal screen: builds the screen quad,
    /// owns an <see cref="OffscreenSurface"/> and repaints it whenever the
    /// terminal snapshot version changes. Attached at runtime by
    /// TerminalScreenBehaviour, on clients only: this class cannot load on the
    /// dedicated server (fields need RG.ImGui.Unity).
    /// </summary>
    [RequireComponent(typeof(SerialTerminalDevice), typeof(TerminalScreenBehaviour))]
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "Unity component; IDisposable is not part of the MonoBehaviour lifecycle - OnDestroy releases everything")]
    public class TerminalScreenRenderer : MonoBehaviour
    {
        /// <summary>
        /// One quad geometry for every terminal instance; per-device state is the
        /// RenderTexture/material, not the mesh. Never destroyed.
        /// </summary>
        private static Mesh _sharedQuadMesh;

        private SerialTerminalDevice _device;
        private TerminalScreenBehaviour _data;
        private OffscreenSurface _surface;
        private MeshRenderer _quadRenderer;
        private Material _material;
        private int _renderedVersion = -1;
        private bool _setupFailed;

        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
            Justification = "Unity message, called by the engine")]
        private void Awake()
        {
            _device = GetComponent<SerialTerminalDevice>();
            _data = GetComponent<TerminalScreenBehaviour>();
        }

        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
            Justification = "Unity message, called by the engine")]
        private void LateUpdate()
        {
            if (_device == null || _data == null)
            {
                enabled = false;
                return;
            }
            if (_device.IsCursor || _setupFailed)
            {
                return;
            }
            if (!EnsureSetup())
            {
                return;
            }

            bool visible = _device.IsOperating;
            if (_quadRenderer.enabled != visible)
            {
                _quadRenderer.enabled = visible;
            }
            if (!visible)
            {
                return;
            }

            TerminalSnapshot snapshot = _device.GetSnapshot();
            if (snapshot.Version == _renderedVersion)
            {
                return;
            }
            if (OffscreenImGui.Render(_surface, snapshot))
            {
                _renderedVersion = snapshot.Version;
            }
        }

        private bool EnsureSetup()
        {
            if (_quadRenderer != null)
            {
                return true;
            }
            if (!OffscreenImGui.EnsureContext())
            {
                // Main ImGui context not up yet; retry on a later frame.
                return false;
            }

            // Pose + size of the monitor face, captured at prefab build from the
            // vanilla Computer's canvas; PrefabFactory refuses to register the
            // prefab without it, so a miss here means a broken prefab.
            Transform anchor = _data.ScreenAnchor;
            float width = _data.ScreenWorldWidth;
            float height = _data.ScreenWorldHeight;
            if (anchor == null || width <= 0f || height <= 0f)
            {
                SerialTerminalPlugin.Log.LogWarning("Terminal screen: prefab carries no captured screen pose");
                _setupFailed = true;
                return false;
            }

            // Texture matches the screen's aspect so glyphs aren't stretched.
            const int texWidth = 512;
            int texHeight = Mathf.RoundToInt(texWidth * height / width);
            _surface = OffscreenSurface.TryCreate(texWidth, texHeight);
            if (_surface == null)
            {
                // TryCreate logged the reason; with the context up, it is final.
                _setupFailed = true;
                return false;
            }

            // The one shader this screen supports; if a game update strips it
            // from the build, fail loudly instead of guessing with another.
            Shader shader = Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                SerialTerminalPlugin.Log.LogWarning("Terminal screen: the Unlit/Texture shader is missing from the game build");
                _surface.Dispose();
                _surface = null;
                _setupFailed = true;
                return false;
            }
            _material = new Material(shader) { mainTexture = _surface.Texture };

            // The anchor is an inactive GameObject (the disabled vanilla canvas),
            // so the quad is parented to the device and copies the anchor's world pose.
            // Double-sided mesh: canvas/quad facing conventions differ per prefab, and
            // a wrong guess means an invisible (backface-culled) screen. The back side
            // mirrors UVs so the text reads correctly from whichever side is visible.
            GameObject quad = new("SerialTerminalScreenQuad")
            {
                layer = anchor.gameObject.layer
            };
            quad.transform.SetParent(_device.transform, worldPositionStays: false);
            // Nudged off the panel face so the quad doesn't z-fight with the monitor mesh.
            quad.transform.SetPositionAndRotation(anchor.position + (anchor.forward * 0.002f), anchor.rotation);
            quad.transform.localScale = new Vector3(width, height, 1f);
            if (_sharedQuadMesh == null)
            {
                _sharedQuadMesh = BuildDoubleSidedQuad();
            }
            quad.AddComponent<MeshFilter>().sharedMesh = _sharedQuadMesh;

            _quadRenderer = quad.AddComponent<MeshRenderer>();
            _quadRenderer.sharedMaterial = _material;
            _quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _quadRenderer.receiveShadows = false;
            return true;
        }

        private static Mesh BuildDoubleSidedQuad()
        {
            var mesh = new Mesh
            {
                name = "SerialTerminalScreenMesh",
                vertices =
            [
                // front (visible from -Z, like Unity's Quad primitive)
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
                // back (visible from +Z)
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            ],
                uv =
            [
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            ],
                normals =
            [
                -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward,
                Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward
            ],
                triangles =
            [
                0, 2, 1, 1, 2, 3,
                4, 5, 6, 6, 5, 7
            ]
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
            Justification = "Unity message, called by the engine")]
        private void OnDestroy()
        {
            _surface?.Dispose();
            _surface = null;
            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
        }
    }
}
