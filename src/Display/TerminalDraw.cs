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
        public static readonly uint WindowBackground = Color32ToImGui(new Color32(16, 16, 16, 255));
        public static readonly uint ScreenBackground = Color32ToImGui(new Color32(2, 8, 2, 255));

        // Phosphor green (#33FF33) with a translucent block cursor.
        public static readonly uint TextColor = Color32ToImGui(new Color32(51, 255, 51, 255));
        public static readonly uint CursorColor = (TextColor & 0x00FFFFFFu) | 0xA0000000u;

        public static ImFontPtr PickFont(ImGuiIOPtr io)
        {
            return io.Fonts.Fonts[0];
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

            Vector2 cursorMin = new(origin.x + cursorCol * charW, origin.y + cursorRow * lineH);
            drawList.AddRectFilled(cursorMin, cursorMin + new Vector2(charW, lineH), CursorColor);
        }

        private static uint Color32ToImGui(Color32 c)
        {
            return ((uint)c.a << 24) | ((uint)c.b << 16) | ((uint)c.g << 8) | c.r;
        }
    }
}
