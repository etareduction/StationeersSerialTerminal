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

APP_ID=544550

is_game_dir() {
    [ -f "$1/$MANAGED/Assembly-CSharp.dll" ]
}

# There is no Steam CLI that reports install paths (steamcmd manages its own
# separate installs), but the client's own bookkeeping is exact: every library
# entry in libraryfolders.vdf lists the app ids it holds, and each library's
# appmanifest_<id>.acf names the folder under common/. Read those rather than
# probing for a directory of the expected name.
steam_library_configs() {
    for root in \
        "$HOME/.steam/steam" \
        "$HOME/.local/share/Steam" \
        "$HOME/.steam/root" \
        "$HOME/Library/Application Support/Steam"
    do
        for vdf in "$root/steamapps/libraryfolders.vdf" "$root/config/libraryfolders.vdf"; do
            [ -f "$vdf" ] && echo "$vdf"
        done
    done
}

# The library whose "apps" block contains APP_ID; "path" precedes it in the
# same entry, so the last one seen is this app's library.
library_holding_app() {
    awk -v app="\"$APP_ID\"" '
        /"path"/ {
            line = $0
            sub(/^[^"]*"path"[ \t]*"/, "", line)
            sub(/".*$/, "", line)
            path = line
        }
        $1 == app { print path; exit }
    ' "$1"
}

find_in_steam_libraries() {
    steam_library_configs | while IFS= read -r vdf; do
        library=$(library_holding_app "$vdf")
        [ -n "$library" ] || continue
        manifest="$library/steamapps/appmanifest_$APP_ID.acf"
        [ -f "$manifest" ] || continue
        installdir=$(sed -n 's/.*"installdir"[[:space:]]*"\(.*\)".*/\1/p' "$manifest" | head -n 1)
        [ -n "$installdir" ] || continue
        candidate="$library/steamapps/common/$installdir"
        if is_game_dir "$candidate"; then
            echo "$candidate"
            break
        fi
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
