using Assets.Scripts;
using Assets.Scripts.Objects;
using ImGuiNET;
using SerialTerminal.Core;
using UnityEngine;

namespace SerialTerminal.Display
{
    /// <summary>
    /// Shared terminal drawing helpers used by both the interactive window (main
    /// ImGui context) and the in-world screen (offscreen context). Draws from
    /// immutable <see cref="TerminalSnapshot"/>s only — presentation never
    /// touches live terminal state.
    /// </summary>
    internal static class TerminalDraw
    {
        /// <summary>Padding around the cell grid, shared by both drawing surfaces.</summary>
        public const float Pad = 16f;

        public static readonly uint ScreenBackground = Abgr(new Color32(2, 8, 2, 255));

        /// <summary>Phosphor green (#33FF33).</summary>
        public static readonly uint TextColor = Abgr(new Color32(51, 255, 51, 255));

        /// <summary>Translucent block cursor over the phosphor green.</summary>
        public static readonly uint CursorColor = (TextColor & 0x00FFFFFFu) | 0xA0000000u;

        extension(ImGuiIOPtr io)
        {
            /// <summary>The font terminal text is drawn with (first in the atlas).</summary>
            public ImFontPtr TerminalFont => io.Fonts.Fonts[0];
        }

        extension(TerminalSnapshot snapshot)
        {
            /// <summary>
            /// Draws the snapshot's cell grid plus block cursor at the current
            /// cursor position of the current ImGui window. Caller pushes the font.
            /// Consecutive same-coloured cells in a row share one draw call.
            /// </summary>
            public void Draw()
            {
                float lineH = ImGui.GetTextLineHeight();
                float charW = ImGui.CalcTextSize("M").x;
                ImDrawListPtr drawList = ImGui.GetWindowDrawList();
                Vector2 origin = ImGui.GetCursorScreenPos();

                for (int row = 0; row < snapshot.Lines.Length; row++)
                {
                    string line = snapshot.Lines[row];
                    string colors = snapshot.Colors[row];
                    int col = 0;
                    while (col < line.Length)
                    {
                        int start = col;
                        char code = colors[col];
                        while (col < line.Length && colors[col] == code) col++;
                        drawList.AddText(
                            new Vector2(origin.x + (start * charW), origin.y + (row * lineH)),
                            CellColor(code),
                            line[start..col]);
                    }
                }
                // Advance the layout cursor over the grid, as text items would.
                ImGui.Dummy(new Vector2(snapshot.Lines[0].Length * charW, snapshot.Lines.Length * lineH));

                Vector2 cursorMin = new(
                    origin.x + (snapshot.CursorCol * charW),
                    origin.y + (snapshot.CursorRow * lineH));
                drawList.AddRectFilled(cursorMin, cursorMin + new Vector2(charW, lineH), CursorColor);
            }
        }

        /// <summary>Draw colour for one colour plane char; phosphor green for
        /// the default pen or a missing swatch.</summary>
        /// <param name="code">Colour plane char from the snapshot.</param>
        private static uint CellColor(char code)
        {
            int color = TerminalState.CharToColor(code);
            ColorSwatch swatch = color < 0 ? null : GameManager.GetColorSwatch(color);
            if (swatch == null)
            {
                return TextColor;
            }
            Color32 rgb = swatch.Color;
            rgb.a = 255;
            return Abgr(rgb);
        }

        /// <summary>The color packed in ImGui's ABGR order.</summary>
        /// <param name="c">The color to pack.</param>
        private static uint Abgr(Color32 c)
        {
            return ((uint)c.a << 24) | ((uint)c.b << 16) | ((uint)c.g << 8) | c.r;
        }
    }
}
