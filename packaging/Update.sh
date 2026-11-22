#!/usr/bin/env bash
# Graveyard Orbit Co-op — installer / updater (Linux / Steam Deck / Proton)
#
# Downloads the latest GitHub release and copies it into the game folder.
# Existing BepInEx settings are left alone.
#
# Usage:
#   ./Update.sh                       auto-detect the Steam install
#   ./Update.sh --force               reinstall even if already current
#   ./Update.sh --game-dir "/path/to/Graveyard Orbit"
#
# GRAVEYARD_ORBIT_DIR can also point at the game folder.
set -euo pipefail

OWNER="Grokent"
REPO="graveyard_orbit_mp"
MOD_VERSION="0.1.0"
GAME_FOLDER="Graveyard Orbit"
EXE_NAME="GraveyardOrbit.exe"

FORCE=0
GAME_DIR="${GRAVEYARD_ORBIT_DIR:-}"

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
say() { printf '%s\n' "$*"; }

usage() {
cat <<'EOF'
Graveyard Orbit Co-op updater

  ./Update.sh                        auto-detect the Steam install
  ./Update.sh --force                reinstall even if already current
  ./Update.sh --game-dir "<path>"    use a specific game folder

Set GRAVEYARD_ORBIT_DIR to skip detection.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --game-dir)   [[ $# -ge 2 ]] || die "Missing value for --game-dir."; GAME_DIR="$2"; shift 2 ;;
    --game-dir=*) GAME_DIR="${1#*=}"; shift ;;
    --force|-f)   FORCE=1; shift ;;
    -h|--help)    usage; exit 0 ;;
    *)            die "Unknown argument: $1 (try --help)" ;;
  esac
done

# ------------------------------------------------------------------ locate
find_game() {
  local root candidate vdf
  local roots=()
  for root in \
    "$HOME/.local/share/Steam" \
    "$HOME/.steam/steam" \
    "$HOME/.steam/root" \
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
  do
    [[ -d "$root" ]] || continue
    roots+=("$root")
  done

  for root in "${roots[@]}"; do
    local libs=("$root")
    vdf="$root/steamapps/libraryfolders.vdf"
    if [[ -f "$vdf" ]]; then
      while IFS= read -r candidate; do
        [[ -n "$candidate" ]] && libs+=("$candidate")
      done < <(sed -n 's/.*"path"[[:space:]]*"\(.*\)".*/\1/p' "$vdf" | sed 's/\\\\/\//g')
    fi
    for candidate in "${libs[@]}"; do
      if [[ -f "$candidate/steamapps/common/$GAME_FOLDER/$EXE_NAME" ]]; then
        printf '%s\n' "$candidate/steamapps/common/$GAME_FOLDER"
        return 0
      fi
    done
  done
  return 1
}

# ------------------------------------------------------------------ network
latest_tag() {
  local api="https://api.github.com/repos/$OWNER/$REPO/releases/latest"
  local url="https://github.com/$OWNER/$REPO/releases/latest"
  local effective tag=""

  # The /releases/latest redirect ends in /releases/tag/<tag>; no API quota.
  if command -v curl >/dev/null 2>&1; then
    effective="$(curl -fsSL -o /dev/null -w '%{url_effective}' "$url" 2>/dev/null || true)"
    case "$effective" in */tag/*) tag="${effective##*/}" ;; esac
    if [[ -z "$tag" ]]; then
      tag="$(curl -fsSL -H 'Accept: application/vnd.github+json' "$api" 2>/dev/null \
        | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1 || true)"
    fi
  elif command -v wget >/dev/null 2>&1; then
    tag="$(wget -q -O - -H 'Accept: application/vnd.github+json' "$api" 2>/dev/null \
      | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1 || true)"
  fi

  [[ -n "$tag" ]] || return 1
  printf '%s\n' "$tag"
}

download() {
  local url="$1" out="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL --retry 3 --retry-delay 2 -o "$out" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget -q --tries=3 -O "$out" "$url"
  else
    die "Need curl or wget to download the update."
  fi
}

extract() {
  local zip="$1" dest="$2"
  if command -v unzip >/dev/null 2>&1; then
    unzip -oq "$zip" -d "$dest"
  elif command -v python3 >/dev/null 2>&1; then
    python3 -m zipfile -e "$zip" "$dest"
  else
    die "Need unzip or python3 to extract the update."
  fi
}

# ------------------------------------------------------------------ main
say "Graveyard Orbit Co-op updater"

if [[ -z "$GAME_DIR" ]]; then
  GAME_DIR="$(find_game || true)"
fi
[[ -n "$GAME_DIR" ]] || die "Could not find Graveyard Orbit. Pass the folder, e.g.
  ./Update.sh --game-dir \"$HOME/.local/share/Steam/steamapps/common/$GAME_FOLDER\""
[[ -f "$GAME_DIR/$EXE_NAME" ]] || die "'$GAME_DIR' does not look like the Graveyard Orbit folder (no $EXE_NAME)."
[[ -w "$GAME_DIR" ]] || die "No write permission for '$GAME_DIR'."

say "Game folder: $GAME_DIR"

if command -v pgrep >/dev/null 2>&1 && pgrep -x GraveyardOrbit >/dev/null 2>&1; then
  say "Note: Graveyard Orbit looks like it is running. Restart the game after updating."
fi

say "Checking GitHub for the latest build..."
TAG="$(latest_tag || true)"
[[ -n "$TAG" ]] || die "Could not reach GitHub. Check your connection or update manually:
  https://github.com/$OWNER/$REPO/releases/latest"

ASSET_URL="https://github.com/$OWNER/$REPO/releases/download/$TAG/GraveyardOrbit-Co-op-$MOD_VERSION-$TAG.zip"

INSTALLED=""
if [[ -f "$GAME_DIR/VERSION.txt" ]]; then
  INSTALLED="$(head -n1 "$GAME_DIR/VERSION.txt" | tr -d '[:space:]')"
fi

if [[ "$FORCE" -eq 0 && "$INSTALLED" == "$TAG" ]]; then
  say "Already up to date ($TAG). Use --force to reinstall."
  exit 0
fi

if [[ -n "$INSTALLED" ]]; then
  say "Installed: $INSTALLED  ->  Latest: $TAG"
else
  say "Latest: $TAG"
fi

TMP="$(mktemp -d "${TMPDIR:-/tmp}/go-coop.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

say "Downloading $TAG ..."
download "$ASSET_URL" "$TMP/update.zip" || die "Download failed: $ASSET_URL"

mkdir -p "$TMP/package"
say "Extracting..."
extract "$TMP/update.zip" "$TMP/package" || die "Could not extract the download."

DLL="$GAME_DIR/BepInEx/plugins/GraveyardOrbit.Coop/GraveyardOrbit.Coop.dll"
if [[ -f "$DLL" ]]; then
  cp -f "$DLL" "$DLL.bak"
fi

say "Installing..."
cp -a "$TMP/package/." "$GAME_DIR/"

AFTER="$TAG"
[[ -f "$GAME_DIR/VERSION.txt" ]] && AFTER="$(head -n1 "$GAME_DIR/VERSION.txt" | tr -d '[:space:]')"

say ""
say "Installed $AFTER. Launch the game through Steam."
say "Both players must run the same build; the lobby blocks START on a mismatch."
say "Linux / Proton reminder: the launch option"
say '  WINEDLLOVERRIDES="winhttp=n,b" %command%'
say "must be set once in the game's Steam properties."
