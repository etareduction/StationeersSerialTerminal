using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.UI.ImGuiUi;
using ImGuiNET;
using UI.ImGuiUi.ImGuiWindows;
using UnityEngine;

namespace SerialTerminal
{
    /// <summary>
    /// The interactive terminal window, registered with the game's own
    /// ImGuiWindowManager (drawn every frame from ImGuiManager.RenderOverlay;
    /// the manager also handles the Typing input state and mouse-pointer mode).
    /// One window at a time: a scrollback-less fixed screen (mirroring the
    /// in-world surface). Input is unbuffered: every keystroke goes straight to
    /// the device FIFO (Enter sends CR, Backspace sends BS) — there is no local
    /// line editing.
    /// </summary>
    public class TerminalWindow : UI.ImGuiUi.ImGuiWindows.ImGuiWindow
    {
        // Window auto-closes when the player walks this far from the terminal (meters).
        private const float CloseDistance = 8f;

        // MouseModeController.Check re-locks the cursor every frame unless a modal
        // is registered; the window manager only handles mouse-as-pointer mode.
        private static readonly ImGuiModal Modal = new ImGuiModal();
        private static TerminalWindow _current;

        private readonly SerialTerminalDevice _device;
        private bool _justOpened = true;

        private TerminalWindow(SerialTerminalDevice device)
            : base(device.DisplayName + "###SerialTerminalWindow", new Vector2(480f, 360f))
        {
            _device = device;
        }

        public override ImGuiWindowFlags Flags =>
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;

        public static void Open(SerialTerminalDevice device)
        {
            if (device == null || (_current != null && _current._device == device))
            {
                return;
            }
            _current?.CloseWindow();
            _current = new TerminalWindow(device);
            ImGuiWindowManager.Open(_current);
        }

        public override void OnOpen()
        {
            MouseModeController.AddModal(Modal);
            // Raw KeyManager.GetButtonDown calls (chat on Enter, F2 spawn menu,
            // UI toggles...) ignore the Typing input state; the only vanilla gate
            // they all share is InventoryManager.EnablePlayerKeys.
            InventoryManager.EnablePlayerKeys = false;
        }

        public override void OnClose()
        {
            _current = null;
            MouseModeController.RemoveModal(Modal);
            InventoryManager.EnablePlayerKeys = true;
            CursorManager.Instance?.OnApplicationFocus(focus: true);
        }

        public override void DrawContent()
        {
            SerialTerminalDevice device = _device;
            if (!device || !device.isActiveAndEnabled
                || GameManager.GameState != GameState.Running || OutOfRange(device))
            {
                CloseWindow();
                return;
            }

            // Human respawn re-enables player keys; keep them off while we're open.
            InventoryManager.EnablePlayerKeys = false;

            ImGuiIOPtr io = ImGui.GetIO();
            ImGui.PushFont(TerminalDraw.PickFont(io));

            float charW = ImGui.CalcTextSize("M").x;
            float lineH = ImGui.GetTextLineHeight();
            Vector2 screenSize = new Vector2(
                device.ColumnCount * charW + 16f,
                device.RowCount * lineH + 16f);

            if (_justOpened)
            {
                _justOpened = false;
                Vector2 framePad = ImGui.GetStyle().FramePadding;
                Vector2 windowSize = new Vector2(
                    screenSize.x + 24f,
                    screenSize.y + lineH + framePad.y * 2f + 64f);
                ImGui.SetWindowSize(windowSize);
                ImGui.SetWindowPos((io.DisplaySize - windowSize) * 0.5f);
                ImGui.SetWindowFocus();
            }

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
            else
            {
                ImGui.TextDisabled("keys go to terminal | Esc closes");
            }

            bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            if (focused && powered)
            {
                SendKeystrokes(device);
            }
            ImGui.PopFont();

            // The base class closes on Escape only when the root window itself is
            // focused; the screen child can hold focus instead, so cover that here.
            if (focused && KeyManager.GetButtonDown(KeyCode.Escape))
            {
                CloseWindow();
            }
        }

        /// <summary>
        /// Forwards this frame's typed characters (Unity legacy input, includes
        /// OS key repeat) to the device. Enter arrives as '\n' or '\r' depending
        /// on platform — both are sent as CR (13), like a real terminal keyboard.
        /// Backspace arrives as '\b' (8) and is sent through unchanged.
        /// </summary>
        private static void SendKeystrokes(SerialTerminalDevice device)
        {
            string typed = Input.inputString;
            if (!string.IsNullOrEmpty(typed))
            {
                device.SubmitInput(typed.Replace('\n', '\r'));
            }
        }

        private static bool OutOfRange(SerialTerminalDevice device)
        {
            var human = InventoryManager.ParentHuman;
            if (human == null)
            {
                return false;
            }
            return (device.transform.position - human.transform.position).sqrMagnitude
                > CloseDistance * CloseDistance;
        }
    }
}
