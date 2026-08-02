#!/bin/sh
# Build the plugin and deploy the mod folder into the game's (Proton) mods directory.
set -e
cd "$(dirname "$0")"

MODS="/mnt/990/steam/steamapps/compatdata/544550/pfx/drive_c/users/steamuser/Documents/My Games/Stationeers/mods"

# Build from src/ so src/global.json's SDK pin applies (the system SDK 10 workload set is broken).
(cd src && dotnet build SerialTerminal.csproj -c Release)

mkdir -p "$MODS/SerialTerminal"
cp -r mod/About mod/GameData "$MODS/SerialTerminal/"
cp src/bin/Release/SerialTerminal.dll "$MODS/SerialTerminal/"

echo "Deployed to $MODS/SerialTerminal"
