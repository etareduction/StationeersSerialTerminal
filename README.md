# Serial Terminal

A Stationeers mod adding the Norsec TTY-6 Serial Terminal: a free-standing
computer block — monitor, desk unit and keyboard — that is secretly just a glass
teletype. No processor, no storage; IC10 circuits print to it and read typed
input from it through a six-register memory-mapped UART. Click the screen to
type, and every keystroke goes straight to the wire.

- [`mod/API.md`](mod/API.md) — the device's register-level API reference
- [`DESIGN.md`](DESIGN.md) — how the mod is built and why
- [`examples/`](examples) — IC10 programs: a self-test and two interactive shells

## Building

The mod compiles against the game's assemblies, which are not redistributable
and therefore not in this repo. `setup.sh` links them into a gitignored `lib/`
from your own install:

    ./setup.sh

It asks Steam where Stationeers is (including libraries on other drives). If
that fails, name the directory yourself:

    ./setup.sh /path/to/steamapps/common/Stationeers

BepInEx and [StationeersLaunchPad](https://github.com/StationeersMods/StationeersLaunchPad)
must already be installed in the game folder — three of the referenced
assemblies come from there. Re-run `setup.sh` if the game moves.

Then:

    cd src && dotnet build SerialTerminal.csproj -c Release

Run it from `src/`: `global.json` pins the SDK to 8.0.x and resolves against the
working directory, not the project directory, so building from the repo root
would skip the pin.

## Installing

The build writes `SerialTerminal.dll` straight into [`mod/`](mod), which *is*
the deployable mod folder. Symlink (or copy) it into the game's user-data mods
directory as `SerialTerminal`:

- Windows — `Documents\My Games\Stationeers\mods\`
- Linux/Proton — `<proton-prefix>/drive_c/users/steamuser/Documents/My Games/Stationeers/mods/`

Enable it from StationeersLaunchPad's in-game mod list. LaunchPad loads mods
once at startup, so a rebuild needs a full game restart — and building over a
DLL the running game has mapped will not end well, so build between sessions.

Recipe/language XML only merges for mods in that folder; a bare DLL in
`BepInEx/plugins` loads code but gets no recipes or localization.
