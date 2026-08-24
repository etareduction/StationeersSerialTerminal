using System;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;
using ImGuiNET.Unity;
using UnityEngine;

namespace SerialTerminal.Display
{
    /// <summary>
    /// Adds the mod's own Unicode monospace font (mod/Fonts/DejaVuSansMono.ttf)
    /// to the game's shared ImGui atlas, as a Harmony postfix on
    /// TextureManager.BuildFontAtlas: appended after the game's own fonts (the
    /// game UI and Fonts[0] stay untouched) and before the atlas texture is
    /// created, so every atlas rebuild re-adds it. The terminal falls back to
    /// the game font when the file is missing.
    /// </summary>
    internal static class TerminalFontAtlas
    {
        /// <summary>Rasterized glyph size; both surfaces scale via FontGlobalScale.</summary>
        private const float SizePixels = 20f;

        /// <summary>
        /// Codepoint ranges requested from the font (inclusive pairs, then the
        /// zero terminator ImGui expects); ranges the font has no glyphs for
        /// cost nothing to request.
        /// </summary>
        private static readonly ushort[] GlyphRanges =
        [
            0x0020, 0x024F, // ASCII, Latin-1, Latin Extended-A/B
            0x0370, 0x03FF, // Greek
            0x0400, 0x052F, // Cyrillic + supplement
            0x1E00, 0x1EFF, // Latin Extended Additional
            0x2000, 0x20BF, // punctuation, super/subscripts, currency
            0x2100, 0x218F, // letterlike, number forms
            0x2190, 0x22FF, // arrows, math operators
            0x2300, 0x23FF, // misc technical
            0x2500, 0x25FF, // box drawing, blocks, geometric shapes
            0x2600, 0x26FF, // misc symbols
            0x2800, 0x28FF, // braille
            0x0000,
        ];

        /// <summary>Native view of GlyphRanges; ImGui reads it again on every
        /// atlas rebuild, so it stays pinned for the process lifetime.</summary>
        private static readonly IntPtr NativeRanges =
            GCHandle.Alloc(GlyphRanges, GCHandleType.Pinned).AddrOfPinnedObject();

        /// <summary>The terminal font's index in the shared atlas; -1 = not added.</summary>
        private static int _fontIndex = -1;

        /// <summary>The terminal font from the shared atlas, or the game font
        /// (Fonts[0]) when the injection has not run or failed.</summary>
        /// <param name="io">The IO owning the shared atlas.</param>
        public static ImFontPtr FontFor(ImGuiIOPtr io)
        {
            ImFontAtlasPtr atlas = io.Fonts;
            return _fontIndex > 0 && _fontIndex < atlas.Fonts.Size
                ? atlas.Fonts[_fontIndex]
                : atlas.Fonts[0];
        }

        /// <summary>Harmony postfix on TextureManager.BuildFontAtlas.</summary>
        /// <param name="io">The IO whose atlas was just built.</param>
        public static void AfterBuildFontAtlas(ImGuiIOPtr io)
        {
            _fontIndex = -1;
            string path = Path.Combine(
                Path.GetDirectoryName(typeof(TerminalFontAtlas).Assembly.Location),
                "Fonts", "DejaVuSansMono.ttf");
            if (!File.Exists(path))
            {
                SerialTerminalPlugin.Log.LogWarning(
                    "Terminal font missing, terminal falls back to the game font: " + path);
                return;
            }
            try
            {
                // FontConfig.SetDefaults/ApplyTo is the only unsafe-free route to
                // ImGui's native config defaults; its CustomGlyphRanges/BuildRanges
                // path is unusable here (the marshalling helper is private).
                FontConfig config = default;
                config.SetDefaults();
                // PixelSnapH plus the point-filtered atlas make oversampling moot.
                config.Oversample = new Vector2Int(1, 1);
                config.PixelSnapH = true;
                ImFontConfig native = default;
                ImFontConfigPtr nativeConfig = new(ref native);
                config.ApplyTo(nativeConfig);
                nativeConfig.GlyphRanges = NativeRanges;
                _ = io.Fonts.AddFontFromFileTTF(path, SizePixels, nativeConfig);
                _ = io.Fonts.Build();
                _fontIndex = io.Fonts.Fonts.Size - 1;
                SerialTerminalPlugin.Log.LogInfo(
                    $"Terminal font added to the ImGui atlas (font {_fontIndex})");
            }
            catch (Exception exception)
            {
                SerialTerminalPlugin.Log.LogError("Terminal font failed to load: " + exception);
            }
        }
    }
}
