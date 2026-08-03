using ImGuiNET;
using SerialTerminal.Devices;
using UnityEngine;

namespace SerialTerminal.Display
{
    /// <summary>
    /// Shared terminal drawing helpers used by both the interactive window (main
    /// ImGui context) and the in-world screen (offscreen context).
    /// </summary>
    internal static class TerminalDraw
    {
        public static readonly uint WindowBackground = new Color32(16, 16, 16, 255).ImGuiColor;
        public static readonly uint ScreenBackground = new Color32(2, 8, 2, 255).ImGuiColor;

        /// <summary>Phosphor green (#33FF33).</summary>
        public static readonly uint TextColor = new Color32(51, 255, 51, 255).ImGuiColor;

        /// <summary>Translucent block cursor over the phosphor green.</summary>
        public static readonly uint CursorColor = (TextColor & 0x00FFFFFFu) | 0xA0000000u;

        extension(ImGuiIOPtr io)
        {
            /// <summary>The font terminal text is drawn with (first in the atlas).</summary>
            public ImFontPtr TerminalFont => io.Fonts.Fonts[0];
        }

        extension(SerialTerminalDevice device)
        {
            /// <summary>
            /// Draws the terminal's cell grid plus block cursor at the current
            /// cursor position of the current ImGui window. Caller pushes the font.
            /// </summary>
            public void DrawBuffer()
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

                Vector2 cursorMin = new(origin.x + (cursorCol * charW), origin.y + (cursorRow * lineH));
                drawList.AddRectFilled(cursorMin, cursorMin + new Vector2(charW, lineH), CursorColor);
            }
        }

        extension(Color32 c)
        {
            /// <summary>The color packed in ImGui's ABGR order.</summary>
            public uint ImGuiColor => ((uint)c.a << 24) | ((uint)c.b << 16) | ((uint)c.g << 8) | c.r;
        }
    }
}
