using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.UI.ImGuiUi;
using ImGuiNET;
using UnityEngine;

namespace SerialTerminal
{
    /// <summary>
    /// The interactive terminal window, drawn in the game's own ImGui context
    /// (hooked from ImguiCreativeSpawnMenu.Draw in Patches.cs). One window at a
    /// time: a scrollback-less fixed screen (mirroring the in-world surface) with
    /// an input line at the bottom.
    /// </summary>
    public static class TerminalWindow
    {
        private const string InputStateKey = "SerialTerminalWindow";

        private static readonly ImGuiModal Modal = new ImGuiModal();
        private static SerialTerminalDevice _device;
        private static string _input = string.Empty;
        private static bool _justOpened;

        public static void Open(SerialTerminalDevice device)
        {
            if (device == null || _device == device)
            {
                return;
            }
            if (_device == null)
            {
                KeyManager.SetInputState(InputStateKey, KeyInputState.Typing);
                MouseModeController.AddModal(Modal);
                // Raw KeyManager.GetButtonDown calls (chat on Enter, F2 spawn menu,
                // UI toggles...) ignore the Typing input state; the only vanilla gate
                // they all share is InventoryManager.EnablePlayerKeys.
                InventoryManager.EnablePlayerKeys = false;
            }
            _device = device;
            _input = string.Empty;
            _justOpened = true;
        }

        public static void Close()
        {
            if (_device == null)
            {
                return;
            }
            _device = null;
            KeyManager.RemoveInputState(InputStateKey);
            MouseModeController.RemoveModal(Modal);
            InventoryManager.EnablePlayerKeys = true;
            CursorManager.Instance?.OnApplicationFocus(focus: true);
        }

        /// <summary>Called every frame from inside the game's ImGui frame.</summary>
        public static void Draw()
        {
            SerialTerminalDevice device = _device;
            if (device == null)
            {
                return;
            }
            if (!device || !device.isActiveAndEnabled || GameManager.GameState != GameState.Running)
            {
                Close();
                return;
            }
            if (OutOfRange(device))
            {
                Close();
                return;
            }

            // Human respawn re-enables player keys; keep them off while we're open.
            InventoryManager.EnablePlayerKeys = false;

            ImGuiIOPtr io = ImGui.GetIO();
            ImFontPtr font = TerminalDraw.PickFont(io);
            ImGui.PushFont(font);

            int rows = device.RowCount;
            int cols = device.ColumnCount;
            float charW = ImGui.CalcTextSize("M").x;
            float lineH = ImGui.GetTextLineHeight();
            Vector2 framePad = ImGui.GetStyle().FramePadding;
            Vector2 screenSize = new Vector2(cols * charW + 16f, rows * lineH + 16f);
            Vector2 windowSize = new Vector2(
                screenSize.x + 24f,
                screenSize.y + lineH + framePad.y * 2f + 64f);

            ImGui.SetNextWindowSize(windowSize, ImGuiCond.Appearing);
            ImGui.SetNextWindowPos((io.DisplaySize - windowSize) * 0.5f, ImGuiCond.Appearing);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, TerminalDraw.WindowBackground);

            bool open = true;
            ImGui.Begin(device.DisplayName + "###SerialTerminalWindow", ref open,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse);

            bool powered = device.OnOff && device.Powered;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, TerminalDraw.ScreenBackground);
            ImGui.BeginChild("##terminalscreen", screenSize, true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            if (powered)
            {
                TerminalDraw.DrawBuffer(device);
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();

            if (!powered)
            {
                ImGui.TextUnformatted("-- no power --");
            }

            if (_justOpened)
            {
                ImGui.SetKeyboardFocusHere();
                _justOpened = false;
            }
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##terminalinput", ref _input, 512u,
                    ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (powered && !string.IsNullOrEmpty(_input))
                {
                    device.SubmitLine(_input);
                }
                _input = string.Empty;
                ImGui.SetKeyboardFocusHere(-1);
            }

            bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopFont();

            if (!open || (focused && KeyManager.GetButton(KeyCode.Escape)))
            {
                Close();
            }
        }

        private static bool OutOfRange(SerialTerminalDevice device)
        {
            var human = InventoryManager.ParentHuman;
            if (human == null)
            {
                return false;
            }
            float maxDistance = Mathf.Max(2f, SerialTerminalPlugin.CloseDistance.Value);
            return (device.transform.position - human.transform.position).sqrMagnitude
                > maxDistance * maxDistance;
        }
    }
}
