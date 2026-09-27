#!/usr/bin/env bash
set -Eeuo pipefail

# 固定离线 12 小时后上线结算的目标规模；其它矩阵参数可直接透传覆盖。
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$ROOT_DIR/scripts/bench-matrix.sh" \
  --mode offline-login \
  --hours 12 \
  --players 100,500,1000 \
  --scenarios Gathering,Crafting,Combat \
  "$@"
