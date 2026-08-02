# Serial Terminal — Design

A serial terminal for Stationeers — a free-standing PC-style monitor-and-keyboard
block (the "Computer (Modern)" form factor): IC10 circuits read/write ASCII
characters through a memory-mapped UART, players type on it directly, one
keystroke at a time.

## Fiction / balance

- **Norsec TTY-6 "Teletype"** — Norsec (Northern Security Systems) is the vanilla
  logic/computing manufacturer (Logic Motherboard, Sorter Motherboard are "Norsec K-cops").
  A dumb serial terminal fits their catalogue and the game's retro-futurist tone: it
  *looks* like a full computer — desk unit, monitor, keyboard — but is deliberately
  *not* one, just a glass teletype like a 1970s ADM-3A: display + keyboard + UART,
  no processor, no storage.
- Power draw **50 W** — the vanilla convention for every display/console/IC housing.
- Built from **Kit (Serial Terminal)** `ItemKitSerialTerminal`, printed on the
  **Electronics Printer, Tier 1**: `10 Copper, 5 Gold, 2 Steel, 2 Solder, 40 s, 3000 J`
  — sits between Kit (IC Housing) (10 Cu, 4 Steel, 2 Solder / 2 kJ) and
  Kit (Computer) (5 Fe, 5 Au, 10 Cu / 6 kJ). More gold than a plain display because of the
  UART + keyboard controller.
- Deconstruction returns the kit (standard `BuildStates[0].Tool.ToolExit`).

## Hardware model (what IC10 sees)

The device implements `IMemoryReadable`/`IMemoryWritable`, so vanilla `get`/`put`
(`getd`/`putd`) work on it. Six registers, modelled on a 6551 ACIA-style memory-mapped
UART plus 6845 CRTC-style cursor address registers:

| Addr | Name  | `get` (read)                          | `put` (write)                                  |
|------|-------|---------------------------------------|------------------------------------------------|
| 0    | DATA  | pop input: 1 char, or ≤6 chars packed ASCII-6 in buffered mode (0 if empty) | print: 1 char, or a packed ASCII-6 string in buffered mode |
| 1    | STR   | peek next input char (no consume)     | print packed ASCII-6 string (`STR("HELLO ")`)  |
| 2    | COUNT | input chars available                 | — (error)                                       |
| 3    | CTRL  | status: bit0 input ready, bit1 overflow, bit2 output buffered, bit3 input buffered, bit4 local echo | 1 clear screen, 2 flush input, 3 clear overflow, 4/5 output unbuffered/buffered, 6/7 input unbuffered/buffered, 8/9 local echo off/on |
| 4    | ROW   | cursor row                            | set cursor row (clamped)                        |
| 5    | COL   | cursor column                         | set cursor column (clamped)                     |

Input and output transfer modes are independent and both default to unbuffered
(byte-at-a-time); buffered mode moves one packed ASCII-6 string (≤6 chars) per
DATA access. Buffered input packs the earliest-typed char in the highest byte —
the same layout `STR()` produces — so `UnpackAscii6` round-trips it.

Control characters on output: LF(10) down one row *column unchanged* (scrolls at
the bottom), CR(13) col 0, NEL(133) = CR+LF in one code, BS(8) cursor left
(non-destructive, stops at col 0), DEL(127) destructive backspace (BS SP BS in
one code), FF(12) clear screen. Line wrap at col 40 = CR+LF. Out-of-range
address → vanilla `StackOverflow/Underflow` chip exception, exactly like other
stack devices.

Packed ASCII-6 is the vanilla text convention (`ProgrammableChip.PackAscii6/UnpackAscii6`,
IC10 `STR("...")` literals, LED-display String mode) — 6 chars per double, 53-bit payload.

Logic types (all vanilla — no LogicType enum patching):
- `Setting` (RW): write = print packed ASCII-6 (same as `put 1`); read = last value written.
- `Quantity` (R): input chars available.
- `Error` (R): 1 when the input buffer has overflowed (sticky until CTRL 3 / flush).
- `Color` (RW), `On`, `Power`, `RequiredPower`, `PrefabHash`, `ReferenceId`, `NameHash`: inherited.

Input buffer: 256-byte FIFO (a generous hardware UART FIFO). On overflow new chars are
dropped and the overflow flag set — real UART behaviour, and it gives IC programs a
detectable error state. Player input is unbuffered: every keystroke goes straight into
the FIFO (Enter sends CR 13, Backspace sends BS 8 — real terminal keyboard codes; no
local line editing).

The terminal has no NVRAM: leaving the operating state (switched off or power
lost, tracked via `OnInteractableUpdated` OnOff/Powered transitions, server-side)
runs the same full reset as `clr`. State persists across save/load only while
powered (`_wasOperating` is re-derived from the restored interactable states on
deserialize).

Local echo (CTRL 8/9, default off) is the ADM-3A HALF/FULL DUPLEX switch: in half
duplex the keyboard controller prints keystrokes device-side the moment they arrive
(printables as-is, CR → NEL, BS → DEL), bypassing the IC10 tick (2 Hz) entirely so
typing feels instant. Echo happens even when the FIFO is full — the glass is wired
to the keyboard, not the host.

## Player interaction

Click the screen (`Activate` interactable added to the cloned prefab) → ImGui terminal
window (`TerminalWindow`, drawn inside the game's own ImGui frame): the fixed cell grid
mirroring the in-world screen. No input line — keystrokes are forwarded raw
(`Input.inputString` per frame, Enter mapped to CR) into the input FIFO. On a client,
each frame's keystrokes travel to the server via a LaunchPadBooster
`INetworkMessage` (`TerminalInputMessage { referenceId, text }`); guarded by the standard
"is local player" check used by `LogicHashGen`. Esc / close button / walking away
(8 m) closes the window. Input capture uses the same pattern as
the game's own creative spawn menu: `KeyManager.SetInputState(..., Typing)` +
`MouseModeController.AddModal(ImGuiModal)`.

## ImGui rendering (v0.2, replaces LED glyphs)

The game ships Dear ImGui (`RG.ImGui.dll`, `RG.ImGui.Unity.dll`) and drives one
screen-overlay context from `ImGuiManager.LateUpdate`. Two uses here:

- **Interactive window**: `TerminalWindow` subclasses the game's
  `UI.ImGuiUi.ImGuiWindows.ImGuiWindow` and registers with `ImGuiWindowManager`
  (drawn every frame from `ImGuiManager.RenderOverlay`; the manager also owns the
  Typing input state and mouse-pointer mode). A `MouseModeController` modal keeps
  the cursor unlocked, and `InventoryManager.EnablePlayerKeys` stays off while open.
- **In-world monitor surface**: `OffscreenImGui` creates a *second* ImGui context with
  `ImGui.CreateContext(sharedFontAtlas)` so it shares the game's font atlas (and thus
  texture IDs in `ImGuiManager.igTextureManager`). Each terminal gets its own
  `ImGuiRendererMesh` + `CommandBuffer` + `RenderTexture`, shown on a double-sided
  quad placed at the monitor face — pose and size captured at prefab build from the
  vanilla Computer's `ComputerScreen` world-space canvas (then deactivated), stored
  in `TerminalScreenBehaviour.ScreenAnchor/ScreenWorldWidth/Height`.
  `TerminalScreenBehaviour` repaints the texture only when the device's screen
  version changes — zero cost while idle. Font scale is computed per repaint so the
  whole cell grid fills the texture.

The screen buffer keeps a monotonically increasing `ScreenVersion`;
`SnapshotLines()` returns a cached main-thread copy rebuilt only on version change.
The vanilla numeric readout draws only for displays in
`LogicDisplayDigitRenderer.ActiveDisplays`; `SerialTerminalDevice.OnAddToPool`
vetoes membership in that pool, so `SetDisplay` may run (it needs the dummy
`DigitTransform` the factory assigns) but its glyphs are never rendered.

## Implementation strategy (no Unity editor, no asset bundles)

Clone-and-swap, per the community "Mirrored Devices" pattern (StationeersPlus research,
FPGA mod conventions):

1. Harmony prefix on `Prefab.LoadAll`:
   - Clone `StructureComputer` ("Computer (Modern)" — the free-standing
     monitor-and-keyboard block; `StructureConsoleLED5Large`/`StructureConsoleLED1x2`
     are fallbacks) under a hidden DontDestroyOnLoad parent.
   - Replace its device component with `SerialTerminalDevice : LogicDisplay`
     (field-copy of the shared base-class chain via reflection, then fix
     `Interactables[].Parent`, slot/collider back-references; interface backing
     fields like `ISmartRotatable` copied explicitly).
   - `PrefabName = "StructureSerialTerminal"`, `PrefabHash = Animator.StringToHash(name)`.
   - Clone `ItemKitComputer` → `ItemKitSerialTerminal`, `Constructables = [terminal]`,
     wire `BuildStates[0].Tool.ToolExit` to the new kit.
   - Register both via `Mod.AddPrefabs` (SDK bookkeeping: join validation) and add
     to `WorldManager.Instance.SourcePrefabs` directly for the current `LoadAll`.
     This `Prefab.LoadAll` prefix is the mod's only Harmony patch.
2. Rendering: ImGui on a RenderTexture quad over the monitor face (see the ImGui
   section above); the vanilla numeric readout is suppressed by the `OnAddToPool`
   veto (no patch). Grid is fixed at 20×40 (constants in `SerialTerminalDevice`;
   no config).
3. Sync/persistence:
   - Server→client on class-specific `NetworkUpdateFlags` bits in
     `BuildUpdate`/`ProcessUpdate` + join serialization: screen text + cursor
     (bit 1024) and FIFO count + overflow (bit 2048), so draining input never
     resends the unchanged screen.
   - Save: `SerialTerminalSaveData : LogicBaseSaveData` registered through
     LaunchPadBooster `Mod.AddSaveDataType<T>()`.
   - `WriteMemory`/`ReadMemory` run on the sim thread; rendering marshalled to the main
     thread (same pattern as vanilla `RenderText`).
4. Localization + Stationpedia description + recipes via the standard mod-folder
   `GameData/*.xml` (`ElectronicsPrinterRecipes`, `Language/english.xml` RecordThing
   entries) — Stationpedia pages are auto-generated, and it will show
   "Memory 32 B, Read Write" from the IMemory interfaces automatically.

## Shipping layout (StationeersLaunchPad local mod)

```
mod/                           # this folder IS the mod
├── About/About.xml            # ModMetadata
├── API.md                     # device API reference
├── GameData/serialterminal.xml        # printer recipe
├── GameData/Language/english.xml      # names + Stationpedia descriptions
└── SerialTerminal.dll         # BepInEx-style plugin, loaded by SLP (build output)
```

The csproj builds the DLL straight into `mod/`, and `mod/` is symlinked into the
game's user-data mods folder as `SerialTerminal` — `Documents/My Games/Stationeers/mods`
on Windows, or the same path under `<proton-prefix>/drive_c/users/steamuser/` when
running through Proton. No deploy step, just build and restart the game. (The game merges recipe/language
XML only for real mods in the mods folder — a bare DLL in `BepInEx/plugins` would
load code but get no recipes/localization.)

## IC10 usage example

```
alias term d0
# print a prompt
put term 1 STR("READY.")
put term 0 133         # NEL: newline (CR+LF)
loop:
yield
l r0 term Quantity     # chars waiting?
blez r0 loop
get r1 term 0          # pop keystroke (Enter = CR 13, Backspace = BS 8)
bne r1 13 print
move r1 133            # echo Enter as a full newline
print:
put term 0 r1          # echo it back
j loop
```

More complete examples (rubout handling, buffered mode, cursor addressing) live in
`examples/`.
