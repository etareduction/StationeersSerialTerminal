using System.Diagnostics.CodeAnalysis;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.UI.ImGuiUi;
using ImGuiNET;
using SerialTerminal.Core;
using SerialTerminal.Devices;
using UI.ImGuiUi.ImGuiWindows;
using UnityEngine;

namespace SerialTerminal.Display
{
    /// <summary>
    /// The interactive terminal window, registered with the game's
    /// ImGuiWindowManager (which also handles the Typing input state and
    /// mouse-pointer mode). One window at a time, no scrollback, mirroring the
    /// in-world surface. Keystrokes are forwarded raw to the device FIFO
    /// (Enter sends CR, Backspace BS); no local line editing.
    /// </summary>
    public sealed class TerminalWindow : UI.ImGuiUi.ImGuiWindows.ImGuiWindow
    {
        /// <summary>Window auto-closes when the player walks this far from the terminal (meters).</summary>
        private const float CloseDistance = 8f;

        /// <summary>
        /// MouseModeController.Check re-locks the cursor every frame unless a modal
        /// is registered; the window manager only handles mouse-as-pointer mode.
        /// </summary>
        private static readonly ImGuiModal Modal = new();
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

        [SuppressMessage("Style", "IDE0031:Null check can be simplified",
            Justification = "?. on UnityEngine.Object bypasses the lifetime-aware == operator and could call into a destroyed manager")]
        public override void OnClose()
        {
            _current = null;
            MouseModeController.RemoveModal(Modal);
            InventoryManager.EnablePlayerKeys = true;
            if (CursorManager.Instance != null)
            {
                CursorManager.Instance.OnApplicationFocus(focus: true);
            }
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

            TerminalSnapshot snapshot = device.GetSnapshot();
            ImGuiIOPtr io = ImGui.GetIO();
            ImGui.PushFont(io.TerminalFont);

            float charW = ImGui.CalcTextSize("M").x;
            float lineH = ImGui.GetTextLineHeight();
            Vector2 screenSize = new(
                (snapshot.Lines[0].Length * charW) + TerminalDraw.Pad,
                (snapshot.Lines.Length * lineH) + TerminalDraw.Pad);

            if (_justOpened)
            {
                _justOpened = false;
                Vector2 framePad = ImGui.GetStyle().FramePadding;
                Vector2 windowSize = new(
                    screenSize.x + 24f,
                    screenSize.y + lineH + (framePad.y * 2f) + 64f);
                ImGui.SetWindowSize(windowSize);
                ImGui.SetWindowPos((io.DisplaySize - windowSize) * 0.5f);
                ImGui.SetWindowFocus();
            }

            bool powered = device.IsOperating;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, TerminalDraw.ScreenBackground);
            _ = ImGui.BeginChild("##terminalscreen", screenSize, border: true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            if (powered)
            {
                snapshot.Draw();
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
        /// OS key repeat) to the device raw; the keyboard controller in
        /// TerminalState normalizes Enter ('\n' or '\r' depending on platform)
        /// to CR (13) and maps characters outside the terminal's set.
        /// </summary>
        /// <param name="device">The terminal receiving the keystrokes.</param>
        private static void SendKeystrokes(SerialTerminalDevice device)
        {
            string typed = Input.inputString;
            if (!string.IsNullOrEmpty(typed))
            {
                device.SubmitInput(typed);
            }
        }

        private static bool OutOfRange(SerialTerminalDevice device)
        {
            var human = InventoryManager.ParentHuman;
            return human != null
                && (device.transform.position - human.transform.position).sqrMagnitude
                    > CloseDistance * CloseDistance;
        }
    }
}
