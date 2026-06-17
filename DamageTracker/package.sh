#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

CONFIG="${CONFIG:-Release}"
MOD_ID="DamageTracker"
STAGE_DIR="build/mods/$MOD_ID"
DIST_DIR="build/dist"

if ! command -v zip >/dev/null 2>&1; then
  echo "error: zip not found on PATH." >&2
  exit 1
fi

if [[ "${NO_BUILD:-0}" == "1" ]]; then
  echo "NO_BUILD=1 set; skipping dotnet build."
else
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: dotnet not found on PATH. Install .NET 9 SDK from https://dotnet.microsoft.com/download/dotnet/9.0" >&2
    exit 1
  fi
  dotnet build -c "$CONFIG"
fi

for f in "$MOD_ID.dll" "$MOD_ID.json" "mod_manifest.json" "Sts2Mods.Common.dll"; do
  if [[ ! -f "$STAGE_DIR/$f" ]]; then
    echo "error: staged file missing: $STAGE_DIR/$f" >&2
    echo "Run without NO_BUILD=1 at least once to produce a build." >&2
    exit 1
  fi
done

VERSION=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STAGE_DIR/$MOD_ID.json" | head -1)
if [[ -z "$VERSION" ]]; then
  echo "error: could not read version from $STAGE_DIR/$MOD_ID.json" >&2
  exit 1
fi

mkdir -p "$DIST_DIR"
ZIP_PATH="$DIST_DIR/$MOD_ID-$VERSION.zip"
rm -f "$ZIP_PATH"

(cd "$(dirname "$STAGE_DIR")" && zip -rq "$OLDPWD/$ZIP_PATH" "$(basename "$STAGE_DIR")")

echo "Packaged $ZIP_PATH"
echo "Share it; recipients extract the $MOD_ID/ folder into their game's mods/ directory."
