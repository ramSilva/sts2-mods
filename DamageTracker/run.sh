#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

STEAM_APP_ID="${STEAM_APP_ID:-2868840}"
LOG_FILE="${LOG_FILE:-$HOME/Library/Application Support/SlayTheSpire2/logs/godot.log}"
MOD_TAG="[DamageTracker]"

if [[ "${NO_INSTALL:-0}" != "1" ]]; then
  ./build.sh
fi

if [[ "${NO_LAUNCH:-0}" != "1" ]]; then
  echo "Launching via Steam: steam://rungameid/$STEAM_APP_ID"
  open "steam://rungameid/$STEAM_APP_ID"
fi

mkdir -p "$(dirname "$LOG_FILE")"
touch "$LOG_FILE"

echo "Tailing: $LOG_FILE"
echo "(Ctrl-C stops the tail; the game keeps running.)"

if [[ "${MOD_ONLY:-0}" == "1" ]]; then
  exec tail -n 0 -F "$LOG_FILE" | grep --line-buffered -F "$MOD_TAG"
else
  exec tail -n 0 -F "$LOG_FILE"
fi
