using System.Runtime.CompilerServices;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using LaunchPadBooster.Networking;
using Rendering;
using SerialTerminal.Core;
using SerialTerminal.Display;
using SerialTerminal.Networking;

namespace SerialTerminal.Devices
{
    /// <summary>
    /// Norsec TTY-6 serial terminal, the game-facing adapter around
    /// <see cref="SyncedTerminal"/> (which owns the UART registers, screen
    /// emulation, input FIFO, locking and repaint versioning). Nearly every
    /// member is a game override or interface method the base classes require;
    /// each one delegates and maps the reported change onto network dirty bits.
    /// </summary>
    public class SerialTerminalDevice : LogicDisplay, IMemoryReadable, IMemoryWritable
    {
        /// <summary>
        /// Class-specific NetworkUpdateFlags bit for the screen payload (cells +
        /// cursor). Vanilla puts per-class payloads at 1024+ (see
        /// NetworkUpdateType.Thing.*); below us the chain occupies 8/16/32/128
        /// (Thing), 64 (Structure) and 256 (LogicUnitBase).
        /// </summary>
        private const ushort ScreenNetworkFlag = 1024;

        /// <summary>Class-specific NetworkUpdateFlags bit for rx count + overflow.</summary>
        private const ushort StatusNetworkFlag = 2048;

        private readonly SyncedTerminal _terminal = new();

        /// <summary>
        /// Previous operating state; its falling edge (switched off or power lost)
        /// wipes all state.
        /// </summary>
        private bool _wasOperating;

        /// <summary>The terminal only works switched on and powered.</summary>
        internal bool IsOperating => OnOff && Powered;

        /// <summary>FIFO depth and overflow from whichever side is authoritative here.</summary>
        private (int RxCount, bool Overflow) Readout =>
            _terminal.Readout(live: GameManager.RunSimulation);

        /// <summary>Immutable screen snapshot for the window and screen renderers.</summary>
        internal TerminalSnapshot GetSnapshot()
        {
            return _terminal.Snapshot();
        }

        /// <summary>Flags the changed payloads for the next network update.</summary>
        /// <param name="change">Change flags reported by <see cref="SyncedTerminal"/>.</param>
        private void Broadcast(TerminalChange change)
        {
            if (change == TerminalChange.None || !NetworkManager.IsServer)
            {
                return;
            }
            if ((change & TerminalChange.Screen) != TerminalChange.None)
            {
                NetworkUpdateFlags |= ScreenNetworkFlag;
            }
            if ((change & TerminalChange.Status) != TerminalChange.None)
            {
                NetworkUpdateFlags |= StatusNetworkFlag;
            }
        }

        /// <summary>
        /// The vanilla numeric readout draws only for displays inside the digit
        /// renderer's ActiveDisplays pool; vetoing membership keeps whatever glyphs
        /// SetDisplay writes invisible, without patching LogicDisplay.
        /// (SetDisplay still needs a non-null DigitTransform - PrefabFactory
        /// guarantees one.)
        /// </summary>
        /// <param name="densePool">The renderer pool asking to add this display.</param>
        /// <param name="slot">The pool slot offered to this display.</param>
        public override bool OnAddToPool(object densePool, int slot)
        {
            return !ReferenceEquals(densePool, LogicDisplayDigitRenderer.ActiveDisplays)
                && base.OnAddToPool(densePool, slot);
        }

        #region IMemory (IC10 get/put)

        public int GetStackSize()
        {
            return TerminalState.RegisterCount;
        }

        public double ReadMemory(int address)
        {
            (double value, TerminalChange change) = _terminal.ReadRegister(address);
            Broadcast(change);
            return value;
        }

        public void WriteMemory(int address, double value)
        {
            Broadcast(_terminal.WriteRegister(address, value));
        }

        public void ClearMemory()
        {
            Broadcast(_terminal.Reset());
        }

        #endregion IMemory (IC10 get/put)

        /// <summary>
        /// No NVRAM: losing power or being switched off wipes the whole terminal
        /// (screen, FIFO, flags, modes) — a power cycle is a full reset.
        /// </summary>
        /// <param name="interactable">The interactable whose state changed.</param>
        public override void OnInteractableUpdated(Interactable interactable)
        {
            base.OnInteractableUpdated(interactable);
            if (interactable.Action is not (InteractableType.OnOff or InteractableType.Powered))
            {
                return;
            }
            // Interactable states flap while a world loads (power network warm-up);
            // that is not a power cycle. _wasOperating is restored by DeserializeSave.
            if (GameManager.GameState != GameState.Running)
            {
                return;
            }
            bool operating = IsOperating;
            if (_wasOperating && !operating && GameManager.RunSimulation)
            {
                ClearMemory();
            }
            _wasOperating = operating;
        }

        #region Player input

        public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
        {
            if (interactable.Action == InteractableType.Activate)
            {
                DelayedActionInstance action = new()
                {
                    Duration = 0f,
                    ActionMessage = interactable.ContextualName
                };
                if (!IsOperating)
                {
                    return action.Fail(GameStrings.DeviceNotOn);
                }
                if (!doAction)
                {
                    return action.Succeed();
                }
                if (!GameManager.IsBatchMode
                    && interaction.SourceThing is Entity entity && entity.IsLocalPlayer)
                {
                    OpenTerminalWindow(this);
                }
                return action.Succeed();
            }
            return base.InteractWith(interactable, interaction, doAction);
        }

        /// <summary>
        /// NoInlining keeps TerminalWindow (whose ImGui base class the dedicated
        /// server cannot load) out of InteractWith's JIT: InteractWith runs on the
        /// server for every interaction, this helper only for the local player.
        /// </summary>
        /// <param name="device">The terminal to show a window for.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OpenTerminalWindow(SerialTerminalDevice device)
        {
            TerminalWindow.Open(device);
        }

        public override string GetContextualName(Interactable interactable)
        {
            return interactable.Action == InteractableType.Activate
                ? "Open Terminal"
                : base.GetContextualName(interactable);
        }

        /// <summary>Local player pressed keys in the terminal window (raw, unbuffered).</summary>
        /// <param name="text">Raw keystrokes typed this frame.</param>
        public void SubmitInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (GameManager.RunSimulation)
            {
                EnqueueInput(text);
            }
            else
            {
                new TerminalInputMessage
                {
                    TerminalId = ReferenceId,
                    Text = text
                }.SendToHost();
            }
        }

        /// <summary>Server side: queue raw keystrokes into the input FIFO.</summary>
        /// <param name="text">Raw keystrokes from the client.</param>
        public void EnqueueInput(string text)
        {
            Broadcast(_terminal.AcceptKeystrokes(text));
        }

        #endregion Player input

        #region Logic types

        public override bool CanLogicRead(LogicType logicType)
        {
            return logicType is LogicType.Quantity or LogicType.Error
                || base.CanLogicRead(logicType);
        }

        public override double GetLogicValue(LogicType logicType)
        {
            return logicType switch
            {
                LogicType.Quantity => Readout.RxCount,
                LogicType.Error => Readout.Overflow ? 1 : 0,
                _ => base.GetLogicValue(logicType)
            };
        }

        public override void SetLogicValue(LogicType logicType, double value)
        {
            base.SetLogicValue(logicType, value);
            if (logicType == LogicType.Setting)
            {
                Broadcast(_terminal.Print(value));
            }
        }

        #endregion Logic types

        #region Network sync

        public override void BuildUpdate(RocketBinaryWriter writer, ushort networkUpdateType)
        {
            base.BuildUpdate(writer, networkUpdateType);
            if (Thing.IsNetworkUpdateRequired(ScreenNetworkFlag, networkUpdateType))
            {
                writer.Write(_terminal.CaptureScreen());
            }
            if (Thing.IsNetworkUpdateRequired(StatusNetworkFlag, networkUpdateType))
            {
                writer.Write(_terminal.CaptureStatus());
            }
        }

        public override void ProcessUpdate(RocketBinaryReader reader, ushort networkUpdateType)
        {
            base.ProcessUpdate(reader, networkUpdateType);
            if (Thing.IsNetworkUpdateRequired(ScreenNetworkFlag, networkUpdateType))
            {
                _ = _terminal.RestoreScreen(reader.ReadScreenContent());
            }
            if (Thing.IsNetworkUpdateRequired(StatusNetworkFlag, networkUpdateType))
            {
                _terminal.RestoreStatus(reader.ReadTerminalStatus());
            }
        }

        public override void SerializeOnJoin(RocketBinaryWriter writer)
        {
            base.SerializeOnJoin(writer);
            writer.Write(_terminal.CaptureScreen());
            writer.Write(_terminal.CaptureStatus());
        }

        public override void DeserializeOnJoin(RocketBinaryReader reader)
        {
            base.DeserializeOnJoin(reader);
            _ = _terminal.RestoreScreen(reader.ReadScreenContent());
            _terminal.RestoreStatus(reader.ReadTerminalStatus());
        }

        #endregion Network sync

        #region Save data

        public override ThingSaveData SerializeSave()
        {
            ThingSaveData saveData = new SerialTerminalSaveData();
            InitialiseSaveData(ref saveData);
            return saveData;
        }

        protected override void InitialiseSaveData(ref ThingSaveData savedData)
        {
            base.InitialiseSaveData(ref savedData);
            if (savedData is SerialTerminalSaveData data)
            {
                data.CopyFrom(_terminal.Capture());
            }
        }

        public override void DeserializeSave(ThingSaveData savedData)
        {
            base.DeserializeSave(savedData);
            if (savedData is SerialTerminalSaveData data)
            {
                _ = _terminal.Restore(data.ToMemento());
                // Interactable states (OnOff/Powered) are restored by the base
                // deserialize, so this reflects the state at save time.
                _wasOperating = IsOperating;
            }
        }

        #endregion Save data

        public override StringBuilder GetExtendedText()
        {
            StringBuilder sb = base.GetExtendedText();
            (int rxCount, bool overflow) = Readout;
            _ = sb.Append("Input Buffer ")
                .AppendLine((rxCount + "/" + TerminalState.RxCapacity).AsColor("yellow"));
            if (overflow)
            {
                _ = sb.AppendLine("Input Overflow".AsColor("red"));
            }
            return sb;
        }
    }
}
