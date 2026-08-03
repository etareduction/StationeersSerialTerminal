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
            /// </summary>
            public void Draw()
            {
                float lineH = ImGui.GetTextLineHeight();
                float charW = ImGui.CalcTextSize("M").x;
                ImDrawListPtr drawList = ImGui.GetWindowDrawList();
                Vector2 origin = ImGui.GetCursorScreenPos();

                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.Text, TextColor);
                foreach (string line in snapshot.Lines)
                {
                    ImGui.TextUnformatted(line);
                }
                ImGui.PopStyleColor();
                ImGui.PopStyleVar();

                Vector2 cursorMin = new(
                    origin.x + (snapshot.CursorCol * charW),
                    origin.y + (snapshot.CursorRow * lineH));
                drawList.AddRectFilled(cursorMin, cursorMin + new Vector2(charW, lineH), CursorColor);
            }
        }

        /// <summary>The color packed in ImGui's ABGR order.</summary>
        /// <param name="c">The color to pack.</param>
        private static uint Abgr(Color32 c)
        {
            return ((uint)c.a << 24) | ((uint)c.b << 16) | ((uint)c.g << 8) | c.r;
        }
    }
}
