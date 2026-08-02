using System;
using Assets.Scripts;
using Assets.Scripts.UI;
using HarmonyLib;
using ImGuiNET;
using ImGuiNET.Unity;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SerialTerminal
{
    /// <summary>
    /// Shared terminal drawing helpers used by both the interactive window (main
    /// ImGui context) and the in-world screen (offscreen context).
    /// </summary>
    internal static class TerminalDraw
    {
        public static readonly uint WindowBackground = Color32ToImGui(new Color32(16, 16, 16, 255));
        public static readonly uint ScreenBackground = Color32ToImGui(new Color32(2, 8, 2, 255));

        private static uint _textColor;
        private static string _parsedTextColor;

        public static uint TextColor
        {
            get
            {
                string configured = SerialTerminalPlugin.TextColor.Value;
                if (_parsedTextColor != configured)
                {
                    _textColor = ParseHtmlColor(configured, new Color32(51, 255, 51, 255));
                    _parsedTextColor = configured;
                }
                return _textColor;
            }
        }

        public static uint CursorColor => (TextColor & 0x00FFFFFFu) | 0xA0000000u;

        public static ImFontPtr PickFont(ImGuiIOPtr io)
        {
            int count = io.Fonts.Fonts.Size;
            int index = Mathf.Clamp(SerialTerminalPlugin.FontIndex.Value, 0, count - 1);
            return io.Fonts.Fonts[index];
        }

        /// <summary>
        /// Draws the terminal cell grid plus block cursor at the current cursor
        /// position of the current ImGui window. Caller pushes the font.
        /// </summary>
        public static void DrawBuffer(SerialTerminalDevice device)
        {
            string[] lines = device.SnapshotLines(out int cursorRow, out int cursorCol);
            float lineH = ImGui.GetTextLineHeight();
            float charW = ImGui.CalcTextSize("M").x;
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            Vector2 origin = ImGui.GetCursorScreenPos();

            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Text, TextColor);
            for (int r = 0; r < lines.Length; r++)
            {
                ImGui.TextUnformatted(lines[r]);
            }
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();

            Vector2 cursorMin = new Vector2(origin.x + cursorCol * charW, origin.y + cursorRow * lineH);
            drawList.AddRectFilled(cursorMin, cursorMin + new Vector2(charW, lineH), CursorColor);
        }

        public static uint ParseHtmlColor(string html, Color32 fallback)
        {
            Color32 c = fallback;
            if (!string.IsNullOrEmpty(html) && ColorUtility.TryParseHtmlString(html.Trim(), out Color parsed))
            {
                c = parsed;
            }
            return Color32ToImGui(c);
        }

        private static uint Color32ToImGui(Color32 c)
        {
            return ((uint)c.a << 24) | ((uint)c.b << 16) | ((uint)c.g << 8) | c.r;
        }
    }

    /// <summary>
    /// A second ImGui context that shares the game's font atlas and renders into
    /// RenderTextures on demand (only when a terminal's content changes), using
    /// the game's own mesh renderer + texture manager.
    /// </summary>
    internal static class OffscreenImGui
    {
        private static IntPtr _context = IntPtr.Zero;

        private static readonly AccessTools.FieldRef<ImGuiManager> CurrentManagerRef;
        private static readonly AccessTools.FieldRef<ImGuiManager, ShaderResourcesAsset> ShadersRef;

        static OffscreenImGui()
        {
            CurrentManagerRef = AccessTools.StaticFieldRefAccess<ImGuiManager>(
                AccessTools.Field(typeof(ImGuiManager), "current"));
            ShadersRef = AccessTools.FieldRefAccess<ImGuiManager, ShaderResourcesAsset>("_shaders");
        }

        private static ImGuiManager Manager => CurrentManagerRef();

        /// <summary>Main ImGui context must be alive and current (between frames).</summary>
        private static bool MainContextReady =>
            Manager != null && ImGuiManager.igTextureManager != null
            && ImGui.GetCurrentContext() != IntPtr.Zero;

        public static bool EnsureContext()
        {
            if (_context != IntPtr.Zero)
            {
                return true;
            }
            if (!MainContextReady)
            {
                return false;
            }
            IntPtr mainContext = ImGui.GetCurrentContext();
            ImFontAtlasPtr sharedAtlas = ImGui.GetIO().Fonts;
            _context = ImGui.CreateContext(sharedAtlas);
            ImGui.SetCurrentContext(_context);
            ImGuiStylePtr style = ImGui.GetStyle();
            style.WindowRounding = 0f;
            style.WindowBorderSize = 0f;
            style.WindowPadding = new Vector2(8f, 8f);
            ImGui.SetCurrentContext(mainContext);
            SerialTerminalPlugin.Log.LogInfo("Offscreen ImGui context created (shared font atlas)");
            return true;
        }

        /// <summary>Creates a renderer (own mesh + material) for one terminal screen.</summary>
        public static ImGuiRendererMesh CreateRenderer()
        {
            if (!EnsureContext())
            {
                return null;
            }
            ShaderResourcesAsset shaders = ShadersRef(Manager);
            if (shaders == null)
            {
                return null;
            }
            var renderer = new ImGuiRendererMesh(shaders, ImGuiManager.igTextureManager);
            IntPtr mainContext = ImGui.GetCurrentContext();
            ImGui.SetCurrentContext(_context);
            renderer.Initialize(ImGui.GetIO());
            ImGui.SetCurrentContext(mainContext);
            return renderer;
        }

        public static void DestroyRenderer(ImGuiRendererMesh renderer)
        {
            if (renderer == null || _context == IntPtr.Zero)
            {
                return;
            }
            IntPtr previous = ImGui.GetCurrentContext();
            ImGui.SetCurrentContext(_context);
            renderer.Shutdown(ImGui.GetIO());
            ImGui.SetCurrentContext(previous);
        }

        /// <summary>
        /// Runs one ImGui frame in the offscreen context and renders it into the
        /// given RenderTexture immediately.
        /// </summary>
        public static bool Render(RenderTexture target, ImGuiRendererMesh renderer,
            CommandBuffer commandBuffer, SerialTerminalDevice device)
        {
            if (_context == IntPtr.Zero || !MainContextReady)
            {
                return false;
            }
            IntPtr mainContext = ImGui.GetCurrentContext();
            ImGui.SetCurrentContext(_context);
            try
            {
                ImGuiIOPtr io = ImGui.GetIO();
                io.DisplaySize = new Vector2(target.width, target.height);
                io.DeltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.001f);

                // Scale the shared font so the whole cell grid fills the texture.
                ImFontPtr font = TerminalDraw.PickFont(io);
                float charW = font.GetCharAdvance('M');
                float lineH = font.FontSize;
                float pad = 16f;
                float scale = Mathf.Min(
                    (target.width - pad) / (device.ColumnCount * charW),
                    (target.height - pad) / (device.RowCount * lineH));
                io.FontGlobalScale = Mathf.Max(0.1f, scale);

                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.zero);
                ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, TerminalDraw.ScreenBackground);
                ImGui.Begin("##screen",
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs);
                ImGui.PushFont(font);
                TerminalDraw.DrawBuffer(device);
                ImGui.PopFont();
                ImGui.End();
                ImGui.PopStyleColor();
                ImGui.Render();

                commandBuffer.Clear();
                commandBuffer.SetRenderTarget(target);
                commandBuffer.ClearRenderTarget(clearDepth: true, clearColor: true, Color.black);
                renderer.RenderDrawLists(commandBuffer, ImGui.GetDrawData());
                Graphics.ExecuteCommandBuffer(commandBuffer);
                return true;
            }
            finally
            {
                ImGui.SetCurrentContext(mainContext);
            }
        }
    }

    /// <summary>
    /// Lives next to SerialTerminalDevice on the prefab. Owns the screen quad,
    /// its RenderTexture and the per-device ImGui mesh renderer; repaints the
    /// texture whenever the terminal content version changes.
    /// </summary>
    public class TerminalScreenBehaviour : MonoBehaviour
    {
        // Set at prefab-build time (PrefabFactory): pose + size of the monitor face,
        // taken from the vanilla Computer's world-space UI canvas. Serialized so
        // every spawned instance inherits them.
        public Transform ScreenAnchor;
        public float ScreenWorldWidth;
        public float ScreenWorldHeight;

        private SerialTerminalDevice _device;
        private RenderTexture _texture;
        private CommandBuffer _commandBuffer;
        private ImGuiRendererMesh _renderer;
        private MeshRenderer _quadRenderer;
        private Material _material;
        private Mesh _quadMesh;
        private int _renderedVersion = -1;
        private bool _setupFailed;

        private void Awake()
        {
            _device = GetComponent<SerialTerminalDevice>();
        }

        private void LateUpdate()
        {
            if (_device == null || GameManager.IsBatchMode)
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

            // Screen size: explicit config override, else the size captured from the
            // source prefab's monitor canvas, else the LogicDisplay panel width.
            float width;
            float height;
            if (SerialTerminalPlugin.ScreenWidth.Value > 0f)
            {
                width = SerialTerminalPlugin.ScreenWidth.Value;
                height = width * Mathf.Max(0.1f, SerialTerminalPlugin.ScreenAspect.Value);
            }
            else if (ScreenWorldWidth > 0f && ScreenWorldHeight > 0f)
            {
                width = ScreenWorldWidth;
                height = ScreenWorldHeight;
            }
            else
            {
                width = Mathf.Max(0.1f, _device.MaxPixelWidth);
                height = width * Mathf.Max(0.1f, SerialTerminalPlugin.ScreenAspect.Value);
            }

            // Texture matches the screen's aspect so glyphs aren't stretched.
            int texWidth = Mathf.Clamp(SerialTerminalPlugin.ScreenTextureSize.Value, 128, 2048);
            int texHeight = Mathf.Clamp(Mathf.RoundToInt(texWidth * height / width), 128, 2048);
            _texture = new RenderTexture(texWidth, texHeight, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB)
            {
                name = "SerialTerminalScreen",
                filterMode = FilterMode.Bilinear
            };
            _texture.Create();
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
            Transform anchor = ScreenAnchor != null
                ? ScreenAnchor
                : (_device.DigitTransform != null ? _device.DigitTransform : _device.transform);

            // Double-sided mesh: canvas/quad facing conventions differ per prefab, and
            // a wrong guess means an invisible (backface-culled) screen. The back side
            // mirrors UVs so the text reads correctly from whichever side is visible.
            GameObject quad = new GameObject("SerialTerminalScreenQuad");
            quad.layer = anchor.gameObject.layer;
            quad.transform.SetParent(_device.transform, worldPositionStays: false);
            quad.transform.position = anchor.position
                + anchor.forward * SerialTerminalPlugin.ScreenZOffset.Value;
            quad.transform.rotation = anchor.rotation;
            quad.transform.localScale = new Vector3(width, height, 1f);
            _quadMesh = BuildDoubleSidedQuad();
            quad.AddComponent<MeshFilter>().sharedMesh = _quadMesh;

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
            var mesh = new Mesh { name = "SerialTerminalScreenMesh" };
            mesh.vertices = new[]
            {
                // front (visible from -Z, like Unity's Quad primitive)
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
                // back (visible from +Z)
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            };
            mesh.normals = new[]
            {
                -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward,
                Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 1, 2, 3,
                4, 5, 6, 6, 5, 7
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Shader FindScreenShader()
        {
            string[] candidates = { "Unlit/Texture", "UI/Default", "Sprites/Default", "Standard" };
            foreach (string name in candidates)
            {
                Shader shader = Shader.Find(name);
                if (shader != null)
                {
                    return shader;
                }
            }
            return null;
        }

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
                Object.Destroy(_texture);
                _texture = null;
            }
            if (_material != null)
            {
                Object.Destroy(_material);
                _material = null;
            }
            if (_quadMesh != null)
            {
                Object.Destroy(_quadMesh);
                _quadMesh = null;
            }
        }
    }
}
