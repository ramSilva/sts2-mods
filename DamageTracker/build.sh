#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

CONFIG="${CONFIG:-Release}"
GAME_ROOT="${GAME_ROOT:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
MODS_DIR="${MODS_DIR:-$GAME_ROOT/SlayTheSpire2.app/Contents/MacOS/mods}"
MOD_ID="DamageTracker"
STAGE_DIR="build/mods/$MOD_ID"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet not found on PATH. Install .NET 9 SDK from https://dotnet.microsoft.com/download/dotnet/9.0" >&2
  exit 1
fi

if [[ "${NO_BUILD:-0}" == "1" ]]; then
  echo "NO_BUILD=1 set; skipping dotnet build."
else
  dotnet build -c "$CONFIG"
fi

if [[ ! -d "$MODS_DIR" ]]; then
  echo "error: mods dir not found: $MODS_DIR" >&2
  echo "Set MODS_DIR=... to override, or launch STS2 once to create it." >&2
  exit 1
fi

if [[ ! -f "$STAGE_DIR/$MOD_ID.dll" ]]; then
  echo "error: staged DLL missing: $STAGE_DIR/$MOD_ID.dll" >&2
  echo "Run without NO_BUILD=1 at least once to produce a build." >&2
  exit 1
fi

INSTALL_DIR="$MODS_DIR/$MOD_ID"
mkdir -p "$INSTALL_DIR"
cp -f "$STAGE_DIR/$MOD_ID.dll"            "$INSTALL_DIR/$MOD_ID.dll"
cp -f "$STAGE_DIR/$MOD_ID.json"           "$INSTALL_DIR/$MOD_ID.json"

echo "Installed $MOD_ID to $INSTALL_DIR"
echo "Launch STS2 and check Settings > Modding to verify it loaded."
