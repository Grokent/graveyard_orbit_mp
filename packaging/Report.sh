#!/usr/bin/env bash
# Linux / Steam Deck (Proton) diagnostics collector.
set -u
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$HOME/GraveyardOrbit-Co-op-Report-$STAMP"
mkdir -p "$OUT"

SAVE_DIR="$HOME/.local/share/Steam/steamapps/compatdata/4654120/pfx/drive_c/users/steamuser/AppData/LocalLow/AnchorheadGames/Graveyard Orbit"
SAVE="$SAVE_DIR/graveyard_orbit_save.json"
MAP="$DIR/BepInEx/plugins/GraveyardOrbit.Coop/maps/wrecked_space_rock.json"

{
  echo "GRAVEYARD ORBIT CO-OP - DIAGNOSTIC REPORT"
  echo "Generated : $(date '+%Y-%m-%d %H:%M:%S')"
  echo "Game      : $DIR"
  echo "OS        : $(uname -srm)"
  echo "GPU       : $(lspci 2>/dev/null | grep -i -E 'vga|3d' | head -1)"
  echo
  echo "BepInEx   : $(head -1 "$DIR/BepInEx/LogOutput.log" 2>/dev/null)"
  if [[ -f "$DIR/GraveyardOrbit.exe" ]]; then
    echo "Game exe  : $(stat -c%s "$DIR/GraveyardOrbit.exe") bytes"
  fi
  if [[ -f "$DIR/BepInEx/plugins/GraveyardOrbit.Coop/GraveyardOrbit.Coop.dll" ]]; then
    echo "Mod SHA256: $(sha256sum "$DIR/BepInEx/plugins/GraveyardOrbit.Coop/GraveyardOrbit.Coop.dll" | cut -d' ' -f1)"
  fi
  if [[ -f "$MAP" ]]; then
    echo "Map       : $(stat -c%s "$MAP") bytes"
    echo "Map SHA256: $(sha256sum "$MAP" | cut -d' ' -f1)"
    FLAT="$(mktemp)"
    tr -d '[:space:]' < "$MAP" > "$FLAT"
    COUNT="$(grep -o '"count":[0-9]*' "$FLAT" | head -1)"
    BOUNDS="$(grep -o '"bounds":{[^}]*}' "$FLAT" | head -1)"
    rm -f "$FLAT"
    echo "Map stats : $COUNT $BOUNDS"
  fi
  echo
  if [[ -f "$SAVE" ]]; then
    echo "SAVE"
    if command -v python3 >/dev/null 2>&1; then
      python3 - "$SAVE" <<'PY'
import json,sys
o=json.load(open(sys.argv[1]))
prof=o.get('profile') or {}
print('  structure   :', o.get('current_structure'))
print('  money / day :', o.get('money'), '/', o.get('current_day'))
print('  sorties     :', o.get('sorties'))
print('  save_gen    :', o.get('save_gen'))
print('  level_seed  :', o.get('level_seed'))
print('  anomaly_seed:', prof.get('anomaly_seed'))
PY
    else
      grep -o '"level_seed":[-0-9]*' "$SAVE" | head -1
      grep -o '"anomaly_seed":[-0-9]*' "$SAVE" | head -1
    fi
  else
    echo "SAVE: not found at $SAVE"
  fi
} > "$OUT/report.txt"

cp -f "$DIR/BepInEx/LogOutput.log" "$OUT/" 2>/dev/null
grep -E "\[coop\]|\[steam\]|\[contract\]|\[join\]|\[flow\]|\[spawnfan\]|Error|Exception|Crash|Fatal" \
  "$DIR/BepInEx/LogOutput.log" > "$OUT/log-highlights.txt" 2>/dev/null
cp -f "$DIR/BepInEx/config/agentbox.graveyardorbit.coop.cfg" "$OUT/" 2>/dev/null
cp -f "$SAVE_DIR/Player.log" "$OUT/" 2>/dev/null

ARCHIVE="$HOME/GraveyardOrbit-Co-op-Report-$STAMP.tar.gz"
tar -czf "$ARCHIVE" -C "$HOME" "$(basename "$OUT")"

echo "Report created:"
echo "  $ARCHIVE"
echo
echo "Send that archive to the developer."
