# Serial Terminal — Design

A wall-mounted serial terminal for Stationeers: IC10 circuits read/write ASCII characters
through a memory-mapped UART, players type on it via the in-game keyboard window.

## Fiction / balance

- **Norsec TTY-6 "Teletype"** — Norsec (Northern Security Systems) is the vanilla
  logic/computing manufacturer (Logic Motherboard, Sorter Motherboard are "Norsec K-cops").
  A dumb serial terminal fits their catalogue and the game's retro-futurist tone: it is
  deliberately *not* a general-purpose computer, just a glass teletype like a 1970s ADM-3A.
- Power draw **50 W** — the vanilla convention for every display/console/IC housing.
- Built from **Kit (Serial Terminal)** `ItemKitSerialTerminal`, printed on the
  **Electronics Printer, Tier 1**: `10 Copper, 5 Gold, 2 Steel, 2 Solder, 40 s, 3000 J`
  — sits between Kit (IC Housing) (10 Cu, 4 Steel, 2 Solder / 2 kJ) and
  Kit (Computer) (5 Fe, 5 Au, 10 Cu / 6 kJ). More gold than a plain display because of the
  UART + keyboard controller.
- Deconstruction returns the kit (standard `BuildStates[0].Tool.ToolExit`).

## Hardware model (what IC10 sees)

The device implements `IMemoryReadable`/`IMemoryWritable`, so vanilla `get`/`put`
(`getd`/`putd`) work on it. Four registers, modelled on a 6551 ACIA-style memory-mapped UART:

| Addr | Name  | `get` (read)                          | `put` (write)                                  |
|------|-------|---------------------------------------|------------------------------------------------|
| 0    | DATA  | pop next input char (0 if empty)      | print one ASCII char (control chars honoured)   |
| 1    | STR   | peek next input char (no consume)     | print packed ASCII-6 string (`STR("HELLO ")`)  |
| 2    | COUNT | input chars available                 | — (error)                                       |
| 3    | CTRL  | status: bit0 = input ready, bit1 = overflow | 1 = clear screen, 2 = flush input, 3 = clear overflow flag |

Control characters on output: `\n`(10) newline, `\r`(13) col 0, `\b`(8) backspace,
FF(12) clear screen. Screen scrolls up when full. Out-of-range address → vanilla
`StackOverflow/Underflow` chip exception, exactly like other stack devices.

Packed ASCII-6 is the vanilla text convention (`ProgrammableChip.PackAscii6/UnpackAscii6`,
IC10 `STR("...")` literals, LED-display String mode) — 6 chars per double, 53-bit payload.

Logic types (all vanilla — no LogicType enum patching):
- `Setting` (RW): write = print packed ASCII-6 (same as `put 1`); read = last value written.
- `Quantity` (R): input chars available.
- `Error` (R): 1 when the input buffer has overflowed (sticky until CTRL 3 / flush).
- `Color` (RW), `On`, `Power`, `RequiredPower`, `PrefabHash`, `ReferenceId`, `NameHash`: inherited.

Input buffer: 256-byte FIFO (a generous hardware UART FIFO). On overflow new chars are
dropped and the overflow flag set — real UART behaviour, and it gives IC programs a
detectable error state. Player-typed lines are terminated with `\n` (13,10? — just 10).

## Player interaction

Click the screen (`Activate` interactable added to the cloned prefab) → ImGui terminal
window (`TerminalWindow`, drawn inside the game's own ImGui frame): the fixed cell grid
mirroring the in-world screen plus an input line at the bottom. Enter → line + `\n` goes
into the input FIFO. On a client, the line travels to the server via a LaunchPadBooster
`INetworkMessage` (`TerminalInputMessage { referenceId, text }`); guarded by the standard
"is local player" check used by `LogicHashGen`. Esc / close button / walking away
(8 m) closes the window. Input capture uses the same pattern as
the game's own creative spawn menu: `KeyManager.SetInputState(..., Typing)` +
`MouseModeController.AddModal(ImGuiModal)`.

## ImGui rendering (v0.2, replaces LED glyphs)

The game ships Dear ImGui (`RG.ImGui.dll`, `RG.ImGui.Unity.dll`) and drives one
screen-overlay context from `ImGuiManager.LateUpdate`. Two uses here:

- **Interactive window**: Harmony postfix on `ImguiCreativeSpawnMenu.Draw` (same hook
  the IC10Editor mod uses) draws `TerminalWindow` inside the game's frame.
- **In-world console surface**: `OffscreenImGui` creates a *second* ImGui context with
  `ImGui.CreateContext(sharedFontAtlas)` so it shares the game's font atlas (and thus
  texture IDs in `ImGuiManager.igTextureManager`). Each terminal gets its own
  `ImGuiRendererMesh` + `CommandBuffer` + square `RenderTexture`, shown on a quad
  parented to the display's `DigitTransform`. `TerminalScreenBehaviour` repaints the
  texture only when the device's screen version changes — zero cost while idle.
  Font scale is computed per repaint so the whole cell grid fills the texture.

The screen buffer keeps a monotonically increasing `ScreenVersion`;
`SnapshotLines()` returns a cached main-thread copy rebuilt only on version change.
`LogicDisplay.SetDisplay` stays prefix-blocked so the vanilla numeric readout can
never repopulate the digit glyph list (cleared once in `Awake`).

## Implementation strategy (no Unity editor, no asset bundles)

Clone-and-swap, per the community "Mirrored Devices" pattern (StationeersPlus research,
FPGA mod conventions):

1. Harmony prefix on `Prefab.LoadAll`:
   - Clone `StructureConsoleLED1x2` (LED Display Medium) under a hidden DontDestroyOnLoad parent.
   - Replace its `LogicDisplay` component with `SerialTerminal : LogicDisplay` (field-copy via
     reflection, then fix `Interactables[].Parent`, slot/collider back-references).
   - `PrefabName = "StructureSerialTerminal"`, `PrefabHash = Animator.StringToHash(name)`.
   - Clone `ItemKitConsole` → `ItemKitSerialTerminal`, `Constructables = [terminal]`,
     wire `BuildStates[0].Tool.ToolExit` to the new kit.
   - Add both to `WorldManager.Instance.SourcePrefabs`.
2. Rendering: `LogicDisplay` subclass reuses the batched glyph renderer
   (`LogicDisplayDigitRenderer` draws `DigitGlyphs`; offsets are full Vector3 → multi-row
   layout; `DigitTransform.localScale` shrinks glyphs). Override `SetDisplay` so every
   vanilla repaint path renders the terminal grid instead of the numeric readout.
   Grid is fixed at 20×40 (constants in `SerialTerminalDevice`; no config).
   Chars without a glyph mesh fall back to uppercase, then '?'.
3. Sync/persistence:
   - Server→client: screen text + flags on a spare `NetworkUpdateFlags` bit (512) in
     `BuildUpdate`/`ProcessUpdate` + join serialization.
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
SerialTerminal/
├── About/About.xml            # ModMetadata
├── GameData/serialterminal.xml        # printer recipe
├── GameData/Language/english.xml      # names + Stationpedia descriptions
└── SerialTerminal.dll         # BepInEx-style plugin, loaded by SLP
```

Deployed to `.../compatdata/544550/pfx/drive_c/users/steamuser/Documents/My Games/Stationeers/mods/`
(the game merges recipe/language XML only for real mods in the mods folder — a bare DLL in
`BepInEx/plugins` would load code but get no recipes/localization).

## IC10 usage example

```
alias term d0
# print a prompt
put term 1 STR("READY.")
put term 0 10          # newline
loop:
l r0 term Quantity     # chars waiting?
blez r0 loop
get r1 term 0          # pop char
put term 0 r1          # echo it back
j loop
```
