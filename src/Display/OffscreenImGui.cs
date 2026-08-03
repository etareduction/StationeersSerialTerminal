using System;
using Assets.Scripts.UI;
using HarmonyLib;
using ImGuiNET;
using ImGuiNET.Unity;
using SerialTerminal.Devices;
using UnityEngine;
using UnityEngine.Rendering;

namespace SerialTerminal.Display
{
    /// <summary>
    /// A second ImGui context that shares the game's font atlas and renders into
    /// RenderTextures on demand (only when a terminal's content changes), using
    /// the game's own mesh renderer + texture manager.
    /// </summary>
    internal static class OffscreenImGui
    {
        private static IntPtr _context = IntPtr.Zero;

        private static readonly AccessTools.FieldRef<ImGuiManager> CurrentManagerRef =
            AccessTools.StaticFieldRefAccess<ImGuiManager>(
                AccessTools.Field(typeof(ImGuiManager), "current"));

        private static readonly AccessTools.FieldRef<ImGuiManager, ShaderResourcesAsset> ShadersRef =
            AccessTools.FieldRefAccess<ImGuiManager, ShaderResourcesAsset>("_shaders");

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

        /// <summary>
        /// Creates a renderer (own mesh + material) for one terminal screen.
        /// Caller must have ensured the context (EnsureContext); a null return
        /// here means the shader resources are missing - a permanent failure.
        /// </summary>
        public static ImGuiRendererMesh CreateRenderer()
        {
            if (_context == IntPtr.Zero)
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
        /// <param name="target">RenderTexture the frame is drawn into.</param>
        /// <param name="renderer">Per-screen mesh renderer from CreateRenderer.</param>
        /// <param name="commandBuffer">Command buffer reused for the blit.</param>
        /// <param name="device">The terminal whose cells are drawn.</param>
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
                ImFontPtr font = io.TerminalFont;
                float charW = font.GetCharAdvance('M');
                float lineH = font.FontSize;
                const float pad = 16f;
                float scale = Mathf.Min(
                    (target.width - pad) / (SerialTerminalDevice.Columns * charW),
                    (target.height - pad) / (SerialTerminalDevice.Rows * lineH));
                io.FontGlobalScale = Mathf.Max(0.1f, scale);

                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.zero);
                ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, TerminalDraw.ScreenBackground);
                _ = ImGui.Begin("##screen",
                    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs);
                ImGui.PushFont(font);
                device.DrawBuffer();
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
}
