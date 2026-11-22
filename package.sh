#!/usr/bin/env bash
# Build the redistributable install zip. Windows and Linux/Proton use the same
# payload: extract the zip into the Graveyard Orbit game folder.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="0.1.0"
BUILD="$(sed -n 's/.*BuildTag = "\([0-9]*\)".*/\1/p' "$ROOT/src/GraveyardOrbit.Coop/CoopPlugin.cs")"
OUT="$ROOT/release"
STAGE="$OUT/stage"
ZIP="$OUT/GraveyardOrbit-Co-op-$VERSION-MP$BUILD.zip"
DLL="$ROOT/src/GraveyardOrbit.Coop/bin/Release/GraveyardOrbit.Coop.dll"
BEP_ZIP="$ROOT/tools/BepInEx_win_x64_5.4.23.6.zip"

[[ -f "$DLL" ]] || { echo "Build the plugin first: ./build.sh" >&2; exit 1; }
[[ -f "$BEP_ZIP" ]] || { echo "Missing $BEP_ZIP (run ./setup.sh)." >&2; exit 1; }

rm -rf "$OUT"
mkdir -p "$STAGE/BepInEx/plugins/GraveyardOrbit.Coop"

# BepInEx loader + runtime (additive payload)
unzip -oq "$BEP_ZIP" -d "$STAGE"

cp -f "$DLL" "$STAGE/BepInEx/plugins/GraveyardOrbit.Coop/"
cp -f "$ROOT/INSTALL.txt" "$STAGE/INSTALL.txt"
cp -f "$ROOT/LICENSE" "$STAGE/LICENSE"
cp -f "$ROOT/THIRD_PARTY_NOTICES.md" "$STAGE/THIRD_PARTY_NOTICES.md"
mkdir -p "$STAGE/licenses"
cp -f "$ROOT/licenses/LGPL-2.1.txt" "$STAGE/licenses/"
cp -f "$ROOT/packaging/Report.bat" "$STAGE/Report.bat"
cp -f "$ROOT/packaging/Report.ps1" "$STAGE/Report.ps1"
cp -f "$ROOT/packaging/Report.sh" "$STAGE/Report.sh"
chmod +x "$STAGE/Report.sh"

(
  cd "$STAGE"
  python3 - "$STAGE" "$ZIP" <<'PY'
import os, sys, zipfile
stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(stage):
        dirs.sort()
        for name in sorted(files):
            path = os.path.join(root, name)
            z.write(path, os.path.relpath(path, stage))
PY
)

rm -rf "$STAGE"

echo "Created: $ZIP"
echo
unzip -l "$ZIP"
