using System.Diagnostics.CodeAnalysis;
using ImGuiNET.Unity;
using SerialTerminal.Devices;
using UnityEngine;
using UnityEngine.Rendering;

namespace SerialTerminal.Display
{
    /// <summary>
    /// Owns the screen quad, its RenderTexture and the per-device ImGui mesh
    /// renderer; repaints the texture whenever the terminal content version
    /// changes. Attached at runtime by TerminalScreenBehaviour, on clients only:
    /// this class cannot load on the dedicated server (fields need RG.ImGui.Unity).
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
        private RenderTexture _texture;
        private CommandBuffer _commandBuffer;
        private ImGuiRendererMesh _renderer;
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

            bool visible = _device.OnOff && _device.Powered;
            if (_quadRenderer.enabled != visible)
            {
                _quadRenderer.enabled = visible;
            }
            if (!visible)
            {
                return;
            }

            int version = _device.ScreenVersion;
            if (version == _renderedVersion)
            {
                return;
            }
            if (OffscreenImGui.Render(_texture, _renderer, _commandBuffer, _device))
            {
                _renderedVersion = version;
            }
        }

        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
            Justification = "False positive: CreateRenderer returns null when the game's shader resources are missing")]
        [SuppressMessage("Style", "IDE0029:Null check can be simplified",
            Justification = "?? on UnityEngine.Object bypasses the lifetime-aware == operator; a destroyed anchor must fall through to the fallbacks")]
        private bool EnsureSetup()
        {
            if (_quadRenderer != null)
            {
                return true;
            }
            if (!OffscreenImGui.EnsureContext())
            {
                return false;
            }
            _renderer = OffscreenImGui.CreateRenderer();
            if (_renderer == null)
            {
                SerialTerminalPlugin.Log.LogWarning("Terminal screen: no ImGui renderer available");
                _setupFailed = true;
                return false;
            }

            // Screen size: the size captured from the source prefab's monitor canvas,
            // else the LogicDisplay panel width (square).
            float width;
            float height;
            if (_data.ScreenWorldWidth > 0f && _data.ScreenWorldHeight > 0f)
            {
                width = _data.ScreenWorldWidth;
                height = _data.ScreenWorldHeight;
            }
            else
            {
                width = Mathf.Max(0.1f, _device.MaxPixelWidth);
                height = width;
            }

            // Texture matches the screen's aspect so glyphs aren't stretched.
            const int texWidth = 512;
            int texHeight = Mathf.Clamp(Mathf.RoundToInt(texWidth * height / width), 128, 2048);
            _texture = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB)
            {
                name = "SerialTerminalScreen",
                filterMode = FilterMode.Bilinear
            };
            if (!_texture.Create())
            {
                SerialTerminalPlugin.Log.LogWarning(
                    $"Terminal screen: could not create the {texWidth}x{texHeight} render texture");
                _setupFailed = true;
                return false;
            }
            _commandBuffer = new CommandBuffer { name = "SerialTerminalScreen" };

            Shader shader = FindScreenShader();
            if (shader == null)
            {
                SerialTerminalPlugin.Log.LogWarning("Terminal screen: no usable unlit shader found");
                _setupFailed = true;
                return false;
            }
            _material = new Material(shader) { mainTexture = _texture };

            // The anchor may be an inactive GameObject (the disabled vanilla canvas),
            // so the quad is parented to the device and copies the anchor's world pose.
            Transform anchor = _data.ScreenAnchor != null
                ? _data.ScreenAnchor
                : (_device.DigitTransform != null ? _device.DigitTransform : _device.transform);

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
            SerialTerminalPlugin.Log.LogInfo(
                $"Terminal screen quad at {quad.transform.position} (anchor fwd {anchor.forward}),"
                + $" {width:F3}x{height:F3} m, texture {texWidth}x{texHeight}, shader {shader.name}");
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

        private static Shader FindScreenShader()
        {
            string[] candidates = ["Unlit/Texture", "UI/Default", "Sprites/Default", "Standard"];
            foreach (string shaderName in candidates)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader != null)
                {
                    return shader;
                }
            }
            return null;
        }

        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
            Justification = "Unity message, called by the engine")]
        private void OnDestroy()
        {
            if (_renderer != null)
            {
                OffscreenImGui.DestroyRenderer(_renderer);
                _renderer = null;
            }
            _commandBuffer?.Release();
            _commandBuffer = null;
            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
                _texture = null;
            }
            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
        }
    }
}
