#!/bin/sh
# Populate lib/ with symlinks to the assemblies this mod compiles against.
#
# The game's own assemblies are not redistributable, so they are not in this
# repo; lib/ is gitignored and every reference in src/SerialTerminal.csproj
# points at it. Run this once after cloning, and again if the game moves.
#
#   ./setup.sh                      # ask Steam where Stationeers is
#   ./setup.sh /path/to/Stationeers # or say it outright
#   STATIONEERS_DIR=... ./setup.sh
#
# Requires BepInEx and StationeersLaunchPad to be installed in the game folder.
set -eu

REPO_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
LIB_DIR="$REPO_DIR/lib"

MANAGED="rocketstation_Data/Managed"

# Assemblies to link, as paths relative to the game directory.
ASSEMBLIES="
$MANAGED/Assembly-CSharp.dll
$MANAGED/Assembly-CSharp-firstpass.dll
$MANAGED/UnityEngine.dll
$MANAGED/UnityEngine.CoreModule.dll
$MANAGED/UnityEngine.InputLegacyModule.dll
$MANAGED/UnityEngine.PhysicsModule.dll
$MANAGED/UnityEngine.AnimationModule.dll
$MANAGED/Unity.TextMeshPro.dll
$MANAGED/RG.ImGui.dll
$MANAGED/RG.ImGui.Unity.dll
$MANAGED/UniTask.dll
BepInEx/core/BepInEx.dll
BepInEx/core/0Harmony.dll
BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll
"

is_game_dir() {
    [ -f "$1/$MANAGED/Assembly-CSharp.dll" ]
}

# Steam records every library root in libraryfolders.vdf, including ones on
# other drives, so ask it rather than guessing at mount points.
find_in_steam_libraries() {
    for vdf in \
        "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
        "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf" \
        "$HOME/Library/Application Support/Steam/steamapps/libraryfolders.vdf"
    do
        [ -f "$vdf" ] || continue
        sed -n 's/.*"path"[[:space:]]*"\(.*\)".*/\1/p' "$vdf" | while IFS= read -r root; do
            candidate="$root/steamapps/common/Stationeers"
            if is_game_dir "$candidate"; then
                echo "$candidate"
                break
            fi
        done
    done
}

resolve_game_dir() {
    if [ $# -gt 0 ] && [ -n "$1" ]; then
        echo "$1"
        return
    fi
    if [ -n "${STATIONEERS_DIR:-}" ]; then
        echo "$STATIONEERS_DIR"
        return
    fi
    found=$(find_in_steam_libraries | head -n 1)
    if [ -n "$found" ]; then
        echo "$found"
        return
    fi
    # No Steam config to read: try the default install locations directly.
    for candidate in \
        "$HOME/.local/share/Steam/steamapps/common/Stationeers" \
        "$HOME/.steam/steam/steamapps/common/Stationeers" \
        "/c/Program Files (x86)/Steam/steamapps/common/Stationeers"
    do
        if is_game_dir "$candidate"; then
            echo "$candidate"
            return
        fi
    done
}

GAME_DIR=$(resolve_game_dir "${1:-}")

if [ -z "$GAME_DIR" ]; then
    echo "setup: could not find Stationeers." >&2
    echo "       Pass the game directory: ./setup.sh /path/to/Stationeers" >&2
    exit 1
fi

if ! is_game_dir "$GAME_DIR"; then
    echo "setup: '$GAME_DIR' does not look like a Stationeers install" >&2
    echo "       (no $MANAGED/Assembly-CSharp.dll under it)." >&2
    exit 1
fi

mkdir -p "$LIB_DIR"

missing=""
linked=0
for rel in $ASSEMBLIES; do
    src="$GAME_DIR/$rel"
    name=$(basename "$rel")
    if [ ! -f "$src" ]; then
        missing="$missing  $rel
"
        continue
    fi
    ln -sfn "$src" "$LIB_DIR/$name"
    linked=$((linked + 1))
done

if [ -n "$missing" ]; then
    echo "setup: missing from '$GAME_DIR':" >&2
    printf '%s' "$missing" >&2
    echo "       BepInEx and StationeersLaunchPad must be installed before building." >&2
    exit 1
fi

echo "Linked $linked assemblies in lib/ -> $GAME_DIR"
echo "Build with: cd src && dotnet build SerialTerminal.csproj -c Release"
