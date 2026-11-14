#!/usr/bin/env bash
# Build a mod from this kit and copy its DLL into <GameDir>/BepInEx/plugins (waits for Sprocket to close first).
# Linux version of deploy.ps1, for Sprocket running under Proton.
#   ./tools/deploy.sh MyFirstMod
#   ./tools/deploy.sh TurretAddon --game-dir ~/Games/Sprocket --dotnet /path/to/dotnet
set -euo pipefail

project="${1:?Usage: $0 <Project> [--game-dir DIR] [--dotnet PATH]}"; shift
game_dir="$HOME/.local/share/Steam/steamapps/common/Sprocket"
dotnet="dotnet"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --game-dir) game_dir="$2"; shift 2 ;;
        --dotnet) dotnet="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
proj="$(find "$root/$project" -maxdepth 1 -name '*.csproj' 2>/dev/null | head -n1)"
[[ -n "$proj" ]] || { echo "No .csproj in folder '$project'" >&2; exit 1; }
"$dotnet" build "$proj" -c Release -nologo -v q "-p:GameDir=$game_dir" || { echo "Build failed" >&2; exit 1; }

name="$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$proj" | head -n1)"
[[ -n "$name" ]] || name="$(basename "$proj" .csproj)"
dll="$(dirname "$proj")/bin/Release/net6.0/$name.dll"
pdb="${dll%.dll}.pdb"

# A closed game can linger as a tiny husk process; only wait for one that is really running (RSS over 1 MB).
while ps -eo rss=,args= | awk '$2 ~ /Sprocket\.exe$/ && $1 > 1024 { found = 1 } END { exit !found }'; do
    echo "Close Sprocket to install $name.dll..."; sleep 3
done
cp -f "$dll" "$game_dir/BepInEx/plugins/"
echo "Installed $name.dll into $game_dir/BepInEx/plugins"

for t in \
    "$game_dir/ModManager/mods/Sprocket (test copy)/Quality of Life/BepInEx/plugins" \
    "$game_dir/ModManager/mods/Sprocket (test copy)/BepInEx/plugins" \
    "$game_dir/ModManager/mods/Sprocket/Quality of Life/BepInEx/plugins"; do
    if [[ -d "$(dirname "$t")" ]]; then
        mkdir -p "$t"
        cp -f "$dll" "$t/"
        [[ -f "$pdb" ]] && cp -f "$pdb" "$t/"
        echo "Installed $name.dll into $t"
    fi
done
