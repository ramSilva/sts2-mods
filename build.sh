#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

./EnemyIntentTracker/build.sh "$@"
./DamageTracker/build.sh "$@"
