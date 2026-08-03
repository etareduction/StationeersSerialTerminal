using System;
using System.Diagnostics.CodeAnalysis;
using ImGuiNET.Unity;
using UnityEngine;
using UnityEngine.Rendering;

namespace SerialTerminal.Display
{
    /// <summary>
    /// The offscreen rendering resources of one in-world terminal screen: the
    /// RenderTexture, the command buffer that blits into it and the per-screen
    /// ImGui mesh renderer. Created via <see cref="TryCreate"/> once the shared
    /// offscreen context is up; disposed with the owning component. Client-only
    /// (ImGui-typed field), like everything in this namespace except
    /// TerminalScreenBehaviour.
    /// </summary>
    internal sealed class OffscreenSurface : IDisposable
    {
        private OffscreenSurface(RenderTexture texture, CommandBuffer commandBuffer, ImGuiRendererMesh renderer)
        {
            Texture = texture;
            CommandBuffer = commandBuffer;
            Renderer = renderer;
        }

        public RenderTexture Texture { get; }

        public CommandBuffer CommandBuffer { get; }

        public ImGuiRendererMesh Renderer { get; }

        /// <summary>
        /// Builds the surface, or returns null when the game's ImGui shader
        /// resources or the RenderTexture are unavailable. Caller must have
        /// ensured the offscreen context (OffscreenImGui.EnsureContext), so a
        /// null return is a permanent failure.
        /// </summary>
        /// <param name="width">Texture width in pixels.</param>
        /// <param name="height">Texture height in pixels.</param>
        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
            Justification = "False positive: CreateRenderer returns null when the game's shader resources are missing")]
        public static OffscreenSurface TryCreate(int width, int height)
        {
            ImGuiRendererMesh renderer = OffscreenImGui.CreateRenderer();
            if (renderer == null)
            {
                SerialTerminalPlugin.Log.LogWarning("Terminal screen: no ImGui renderer available");
                return null;
            }
            RenderTexture texture = new(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB)
            {
                name = "SerialTerminalScreen",
                filterMode = FilterMode.Bilinear
            };
            if (!texture.Create())
            {
                SerialTerminalPlugin.Log.LogWarning(
                    $"Terminal screen: could not create the {width}x{height} render texture");
                OffscreenImGui.DestroyRenderer(renderer);
                return null;
            }
            CommandBuffer commandBuffer = new() { name = "SerialTerminalScreen" };
            return new OffscreenSurface(texture, commandBuffer, renderer);
        }

        public void Dispose()
        {
            OffscreenImGui.DestroyRenderer(Renderer);
            CommandBuffer.Release();
            Texture.Release();
            UnityEngine.Object.Destroy(Texture);
        }
    }
}
