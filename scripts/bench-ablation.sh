#!/usr/bin/env bash
set -Eeuo pipefail

# ECS 消融实验驱动（TASK-20260927-07）。先用 ecs-current 建立终态参照库（oop 参照已随生产架构切换退役），
# 再对同一 ecs-optimized 产物按配置注入 DAOHUA_ECS_OPT_* 环境开关逐项消融；
# 每轮重灌 optimized 库、独立进程启动、跑完立即 compare 三库终态。
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH_DLL="$ROOT_DIR/src/Bench/bin/Release/net10.0/DaoHua.Bench.dll"
RUN_ID="ablation-$(date -u +%Y%m%dT%H%M%SZ)"
MANIFEST="$ROOT_DIR/artifacts/bench/manifests/characters-3000.json"
PLAYERS="300"
HOURS="12"
SCENARIO="Gathering"
CONCURRENCY="32"
OBSERVABILITY="false"
SEED_PLAYERS="3000"
RUN_ROOT=""
WITH_ANCHORS=1
CONFIGS="opt-full,no-bulk,no-active,no-cache,serial-load,kernel-only"

usage() {
  cat <<'EOF'
用法：scripts/bench-ablation.sh [选项]
  --run-id ID        输出目录名（默认 ablation-<UTC时间>）
  --manifest 文件    角色 manifest（默认 characters-3000.json）
  --seed-players 数  manifest 角色数校验（默认 3000）
  --players 数       参与回放玩家数（默认 300）
  --hours 数         离线模拟时长（默认 12）
  --scenario 名称    Gathering/Crafting/Combat（默认 Gathering）
  --concurrency 数   并发登录数（默认 32）
  --observability    true/false（默认 false，即关闭深度埋点）
  --configs 列表     逗号分隔：opt-full,no-bulk,no-active,no-cache,serial-load,no-bulk-active,kernel-only
  --no-anchors       跳过 ecs-current 重跑（沿用其数据库现有终态）
  --output-dir 目录  覆盖 artifacts/bench/<run-id>
EOF
}

while (($#)); do
  case "$1" in
    --run-id) RUN_ID="${2:?--run-id 缺少值}"; shift 2 ;;
    --manifest) MANIFEST="${2:?--manifest 缺少值}"; shift 2 ;;
    --seed-players) SEED_PLAYERS="${2:?--seed-players 缺少值}"; shift 2 ;;
    --players) PLAYERS="${2:?--players 缺少值}"; shift 2 ;;
    --hours) HOURS="${2:?--hours 缺少值}"; shift 2 ;;
    --scenario) SCENARIO="${2:?--scenario 缺少值}"; shift 2 ;;
    --concurrency) CONCURRENCY="${2:?--concurrency 缺少值}"; shift 2 ;;
    --observability) OBSERVABILITY="${2:?--observability 缺少值}"; shift 2 ;;
    --configs) CONFIGS="${2:?--configs 缺少值}"; shift 2 ;;
    --no-anchors) WITH_ANCHORS=0; shift ;;
    --output-dir) RUN_ROOT="${2:?--output-dir 缺少值}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
  esac
done

RUN_ROOT="${RUN_ROOT:-$ROOT_DIR/artifacts/bench/$RUN_ID}"
[[ -f "$BENCH_DLL" ]] || { echo "缺少 Bench 产物" >&2; exit 3; }
[[ -f "$MANIFEST" ]] || { echo "缺少 manifest：$MANIFEST" >&2; exit 3; }
manifest_players="$(python3 -c "import json;print(len(json.load(open('$MANIFEST'))))")"
[[ "$manifest_players" == "$SEED_PLAYERS" ]] || { echo "manifest 角色数 $manifest_players != $SEED_PLAYERS" >&2; exit 3; }
mkdir -p "$RUN_ROOT"

password="${DAOHUA_BENCH_DB_PASSWORD:-daohua-local-dev}"
conn() { printf 'Host=127.0.0.1;Port=5432;Database=%s;Username=daohua;Password=%s;Command Timeout=300' "$1" "$password"; }
DB_CUR="daohua_bench_ecs_current"; DB_OPT="daohua_bench_ecs_optimized"
PORT_CUR=15139; PORT_OPT=15140

EPOCH="$(python3 -c "import json;print(json.load(open('$MANIFEST'))[0]['startAt'])")"
TARGET="$(python3 -c "
from datetime import datetime, timedelta
print((datetime.fromisoformat('$EPOCH'.replace('Z','+00:00')) + timedelta(hours=float('$HOURS'))).isoformat().replace('+00:00','Z'))")"
export DAOHUA_BENCH_OBSERVABILITY="$OBSERVABILITY"

stop_variant() { scripts/bench-service.sh --variant "$1" --port "$2" --stop >/dev/null 2>&1 || true; }
cleanup() { set +e; for v in ecs-current ecs-optimized; do stop_variant "$v" 0; done; }
trap cleanup EXIT INT TERM

seed_db() {
  local variant="$1" db="$2"
  dotnet "$BENCH_DLL" seed --manifest "$MANIFEST" --output "$RUN_ROOT/manifest-$variant.json" \
    --epoch "$EPOCH" --database "$(conn "$db")" >"$RUN_ROOT/$variant.seed.log" 2>&1
}

run_variant() {
  # $1=variant $2=port $3=报告名前缀 $4..=附加环境赋值 KEY=VAL
  local variant="$1" port="$2" slug="$3"; shift 3
  local report="$RUN_ROOT/${slug}-$(printf '%s' "$SCENARIO" | tr '[:upper:]' '[:lower:]')-${PLAYERS}p-${HOURS}h-offline-login.json"
  env "$@" scripts/bench-service.sh --variant "$variant" --port "$port" \
    --start --simulation-target "$TARGET" >"$RUN_ROOT/${slug}.start.log" 2>&1
  for _ in {1..60}; do
    curl --fail --silent --max-time 2 "http://127.0.0.1:$port/healthz" >/dev/null 2>&1 && break
    sleep 0.5
  done
  set +e
  dotnet "$BENCH_DLL" run --variant "$variant" --url "http://127.0.0.1:$port" \
    --manifest "$RUN_ROOT/manifest-$variant.json" --scenario "$SCENARIO" --players "$PLAYERS" \
    --mode offline-login --target-hours "$HOURS" --concurrency "$CONCURRENCY" \
    --report "$report" >"${report%.json}.stdout.log" 2>"${report%.json}.stderr.log"
  local code=$?
  set -e
  stop_variant "$variant" "$port"
  echo "$slug,$SCENARIO,$PLAYERS,$HOURS,$report,exit=$code" >>"$RUN_ROOT/summary.csv"
  return "$code"
}

compare_terminal() {
  local slug="$1" report="$RUN_ROOT/compare-${slug}.json"
  set +e
  dotnet "$BENCH_DLL" compare --database-b "$(conn "$DB_CUR")" \
    --database-c "$(conn "$DB_OPT")" --manifest "$RUN_ROOT/manifest-ecs-optimized.json" \
    --report "$report" >"${report%.json}.stdout.log" 2>"${report%.json}.stderr.log"
  local code=$?
  set -e
  echo "compare,$slug,$([ "$code" -eq 0 ] && echo exact || echo mismatch)" >>"$RUN_ROOT/summary.csv"
}

echo 'config,scenario,players,hours,report,status' >"$RUN_ROOT/summary.csv"
cp "$MANIFEST" "$RUN_ROOT/manifest.json"
echo "epoch=$EPOCH target=$TARGET observability=$OBSERVABILITY run_root=$RUN_ROOT"

if (( WITH_ANCHORS )); then
  seed_db ecs-current "$DB_CUR" && run_variant ecs-current "$PORT_CUR" ecs-current
fi

# 消融配置：名称 + 环境开关（缺省值即优化全开）
run_config() {
  local slug="$1"; shift
  seed_db ecs-optimized "$DB_OPT"
  run_variant ecs-optimized "$PORT_OPT" "$slug" "$@"
  compare_terminal "$slug"
}

IFS=',' read -r -a config_list <<<"$CONFIGS"
for config in "${config_list[@]}"; do
  case "$config" in
    opt-full)        run_config opt-full ;;
    no-bulk)         run_config no-bulk        DAOHUA_ECS_OPT_BULK=0 ;;
    no-active)       run_config no-active      DAOHUA_ECS_OPT_ACTIVE=0 ;;
    no-cache)        run_config no-cache       DAOHUA_ECS_OPT_CACHE=0 ;;
    serial-load)     run_config serial-load    DAOHUA_ECS_OPT_PARALLELLOAD=0 ;;
    no-bulk-active)  run_config no-bulk-active DAOHUA_ECS_OPT_BULK=0 DAOHUA_ECS_OPT_ACTIVE=0 ;;
    kernel-only)     run_config kernel-only    DAOHUA_ECS_OPT_BULK=0 DAOHUA_ECS_OPT_ACTIVE=0 DAOHUA_ECS_OPT_CACHE=0 DAOHUA_ECS_OPT_PARALLELLOAD=0 ;;
    *) echo "未知消融配置：$config" >&2; exit 2 ;;
  esac
done

echo "消融矩阵完成：$RUN_ROOT"
