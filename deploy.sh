#!/usr/bin/env bash
# Additively install BepInEx (if missing) and copy the built plugin into the
# game folder. Never replaces an existing game file.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Graveyard Orbit}"
BEP_ZIP="$ROOT/tools/BepInEx_win_x64_5.4.23.6.zip"
DLL="$ROOT/src/GraveyardOrbit.Coop/bin/Release/GraveyardOrbit.Coop.dll"

if [[ ! -d "$GAME" ]]; then
  echo "Game folder not found: $GAME" >&2
  exit 1
fi
if [[ ! -f "$DLL" ]]; then
  echo "Plugin not built. Run ./build.sh first." >&2
  exit 1
fi

if [[ ! -d "$GAME/BepInEx" ]]; then
  echo "Installing BepInEx 5.4.23.6 (additive: winhttp.dll, doorstop_config.ini, BepInEx/)"
  unzip -oq "$BEP_ZIP" -d "$GAME"
else
  echo "BepInEx already present — leaving it untouched."
fi

PLUGIN_DIR="$GAME/BepInEx/plugins/GraveyardOrbit.Coop"
mkdir -p "$PLUGIN_DIR"
cp -f "$DLL" "$PLUGIN_DIR/"
[[ -f "${DLL%.dll}.pdb" ]] && cp -f "${DLL%.dll}.pdb" "$PLUGIN_DIR/" || true

echo
echo "Deployed -> $PLUGIN_DIR/GraveyardOrbit.Coop.dll"
echo
echo "Steam launch options (game properties):"
echo '  WINEDLLOVERRIDES="winhttp=n,b" %command%'
echo
echo "Logs: $GAME/BepInEx/LogOutput.log"
echo "Config: $GAME/BepInEx/config/agentbox.graveyardorbit.coop.cfg"
