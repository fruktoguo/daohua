#!/usr/bin/env bash
set -Eeuo pipefail

# 基准矩阵调度器。只调用真实 HTTP/WS workload，不直调 SimulationEngine。
# 每个样本都会先停止受管基准进程、清理白名单基准库并灌入同一 manifest，再按变体顺序
# 启动独立进程；终态比较失败也只记录证据，不覆盖其它变体报告。
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH_DLL="$ROOT_DIR/src/Bench/bin/Release/net10.0/DaoHua.Bench.dll"
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
MANIFEST="$ROOT_DIR/artifacts/bench/manifests/characters-3000.json"
PLAYERS="1,10,1000"
SEED_PLAYERS="3000"
HOURS="1,8,24,48"
SCENARIOS="Gathering,Crafting,Combat"
VARIANTS="oop,ecs-current,ecs-optimized"
CONCURRENCY="32"
RUN_ROOT="$ROOT_DIR/artifacts/bench/$RUN_ID"
EPOCH=""

usage() {
  cat <<'EOF'
用法：scripts/bench-matrix.sh [选项]
  --run-id ID       输出目录名（默认 UTC 时间）
  --manifest 文件   同一份角色 manifest（默认 artifacts/bench/manifests/characters-3000.json）
  --players 列表    逗号分隔：1,10,1000
  --seed-players 数  校验 manifest 角色数（默认 3000；小规模验收可设 3）
  --hours 列表      逗号分隔：1,8,24,48；通过真实 WS simulation.advance 回放
  --scenarios 列表  Gathering,Crafting,Combat
  --variants 列表   oop,ecs-current,ecs-optimized
  --concurrency 数  同时执行的玩家命令数（默认 32）
  --epoch ISO时间   业务实验起点（默认取 manifest 第一角色 startAt）
  --output-dir 目录 覆盖 artifacts/bench/<run-id>
EOF
}

while (($#)); do
  case "$1" in
    --run-id) RUN_ID="${2:?--run-id 缺少值}"; shift 2 ;;
    --manifest) MANIFEST="${2:?--manifest 缺少值}"; shift 2 ;;
    --players) PLAYERS="${2:?--players 缺少值}"; shift 2 ;;
    --seed-players) SEED_PLAYERS="${2:?--seed-players 缺少值}"; shift 2 ;;
    --hours) HOURS="${2:?--hours 缺少值}"; shift 2 ;;
    --scenarios) SCENARIOS="${2:?--scenarios 缺少值}"; shift 2 ;;
    --variants) VARIANTS="${2:?--variants 缺少值}"; shift 2 ;;
    --concurrency) CONCURRENCY="${2:?--concurrency 缺少值}"; shift 2 ;;
    --epoch) EPOCH="${2:?--epoch 缺少值}"; shift 2 ;;
    --output-dir) RUN_ROOT="${2:?--output-dir 缺少值}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ -f "$BENCH_DLL" ]] || { echo "缺少 Bench 产物，请先 dotnet build src/Bench/Bench.csproj -c Release" >&2; exit 3; }
[[ -f "$MANIFEST" ]] || { echo "缺少 manifest：$MANIFEST" >&2; exit 3; }
[[ "$CONCURRENCY" =~ ^[0-9]+$ ]] && ((CONCURRENCY >= 1 && CONCURRENCY <= 3000)) || {
  echo "--concurrency 必须为 1..3000" >&2
  exit 2
}
manifest_players="$(python3 - "$MANIFEST" <<'PY'
import json, sys
print(len(json.load(open(sys.argv[1]))))
PY
)"
[[ "$manifest_players" == "$SEED_PLAYERS" ]] || {
  echo "manifest 角色数 $manifest_players 与 --seed-players $SEED_PLAYERS 不一致" >&2
  exit 3
}
mkdir -p "$RUN_ROOT"

url_for() {
  case "$1" in
    oop) echo 'http://127.0.0.1:15138' ;;
    ecs-current) echo 'http://127.0.0.1:15139' ;;
    ecs-optimized) echo 'http://127.0.0.1:15140' ;;
    *) return 1 ;;
  esac
}
port_for() {
  case "$1" in
    oop) echo 15138 ;;
    ecs-current) echo 15139 ;;
    ecs-optimized) echo 15140 ;;
    *) return 1 ;;
  esac
}
database_for() {
  case "$1" in
    oop) echo daohua_bench_oop ;;
    ecs-current) echo daohua_bench_ecs_current ;;
    ecs-optimized) echo daohua_bench_ecs_optimized ;;
    *) return 1 ;;
  esac
}

IFS=',' read -r -a variant_list <<<"$VARIANTS"
IFS=',' read -r -a scenario_list <<<"$SCENARIOS"
IFS=',' read -r -a player_list <<<"$PLAYERS"
IFS=',' read -r -a hour_list <<<"$HOURS"

for variant in "${variant_list[@]}"; do
  url_for "$variant" >/dev/null || { echo "不支持 variant=$variant" >&2; exit 2; }
done

if [[ -z "$EPOCH" ]]; then
  EPOCH="$(python3 - "$MANIFEST" <<'PY'
import json, sys
data = json.load(open(sys.argv[1]))
print(data[0]["startAt"])
PY
  )"
fi

password="${DAOHUA_BENCH_DB_PASSWORD:-daohua-local-dev}"
connection_for() {
  printf 'Host=127.0.0.1;Port=5432;Database=%s;Username=daohua;Password=%s' "$1" "$password"
}

stop_variant() {
  local variant="$1"
  scripts/bench-service.sh --variant "$variant" --port "$(port_for "$variant")" --stop >/dev/null 2>&1 || true
}

cleanup() {
  set +e
  for variant in "${variant_list[@]}"; do stop_variant "$variant"; done
}
trap cleanup EXIT INT TERM

start_variant() {
  local variant="$1" port="$(port_for "$1")" url="$(url_for "$1")"
  scripts/bench-service.sh --variant "$variant" --port "$port" --start --simulation-target "$EPOCH" >"$RUN_ROOT/${variant}.start.log" 2>&1
  for _ in {1..60}; do
    if curl --fail --silent --max-time 2 "$url/healthz" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  echo "variant=$variant 未在限定时间通过 healthz，见 $RUN_ROOT/${variant}.start.log" >&2
  return 1
}

seed_all() {
  for variant in "${variant_list[@]}"; do stop_variant "$variant"; done
  for variant in "${variant_list[@]}"; do
    dotnet "$BENCH_DLL" seed --manifest "$MANIFEST" --output "$RUN_ROOT/manifest.json" --epoch "$EPOCH" \
      --database "$(connection_for "$(database_for "$variant")")" \
      >"$RUN_ROOT/${variant}.seed.log" 2>&1
    python3 - "$MANIFEST" "$RUN_ROOT/manifest.json" <<'PY'
import json, sys
def normalize(path):
    return json.dumps(json.load(open(path)), sort_keys=True, separators=(',', ':'))
if normalize(sys.argv[1]) != normalize(sys.argv[2]):
    raise SystemExit(f"manifest 与输入不一致：{sys.argv[1]} != {sys.argv[2]}")
PY
  done
}

summary="$RUN_ROOT/summary.csv"
echo 'variant,scenario,players,target_hours,report,status' >"$summary"
cp "$MANIFEST" "$RUN_ROOT/manifest.json"
overall_fail=0

for scenario in "${scenario_list[@]}"; do
  for players in "${player_list[@]}"; do
    for hours in "${hour_list[@]}"; do
      seed_all
      for variant in "${variant_list[@]}"; do
        scenario_slug="$(printf '%s' "$scenario" | tr '[:upper:]' '[:lower:]')"
        report="$RUN_ROOT/${variant}-${scenario_slug}-${players}p-${hours}h.json"
        url="$(url_for "$variant")"
        start_variant "$variant"
        set +e
        dotnet "$BENCH_DLL" run \
          --variant "$variant" --url "$url" --manifest "$RUN_ROOT/manifest.json" \
          --scenario "$scenario" --players "$players" --target-hours "$hours" --concurrency "$CONCURRENCY" \
          --report "$report" >"${report%.json}.stdout.log" 2>"${report%.json}.stderr.log"
        code=$?
        set -e
        status="$(python3 - "$report" "$code" <<'PY'
import json, pathlib, sys
path = pathlib.Path(sys.argv[1])
code = int(sys.argv[2])
if not path.exists():
    print(f"failed-no-report-{code}")
    raise SystemExit
try:
    payload = json.loads(path.read_text())
    print(payload.get("metric", {}).get("measurement_status", "unknown"))
except Exception:
    print(f"failed-invalid-report-{code}")
PY
        )"
        echo "$variant,$scenario,$players,$hours,$report,$status" >>"$summary"
        if [[ "$code" -ne 0 || "$status" == *partial* || "$status" == failed* ]]; then overall_fail=1; fi
        stop_variant "$variant"
      done
      if [[ "$VARIANTS" != *oop* || "$VARIANTS" != *ecs-current* || "$VARIANTS" != *ecs-optimized* ]]; then
        echo "compare,${scenario},${players},${hours},,skipped-variants-subset" >>"$summary"
        continue
      fi
      scenario_slug="$(printf '%s' "$scenario" | tr '[:upper:]' '[:lower:]')"
      compare="$RUN_ROOT/compare-${scenario_slug}-${players}p-${hours}h.json"
      set +e
      dotnet "$BENCH_DLL" compare --database-a "$(connection_for "$(database_for oop)")" \
        --database-b "$(connection_for "$(database_for ecs-current)")" \
        --database-c "$(connection_for "$(database_for ecs-optimized)")" \
        --manifest "$RUN_ROOT/manifest.json" --report "$compare" >"${compare%.json}.stdout.log" 2>"${compare%.json}.stderr.log"
      compare_code=$?
      set -e
      echo "compare,$scenario,$players,$hours,$compare,$([ "$compare_code" -eq 0 ] && echo exact || echo mismatch)" >>"$summary"
      if [[ "$compare_code" -ne 0 ]]; then overall_fail=1; fi
    done
  done
done

for variant in "${variant_list[@]}"; do stop_variant "$variant"; done

echo "矩阵完成：${RUN_ROOT}（summary.csv 记录每个样本的实际状态；partial/failed 不代表性能门禁通过）"
exit "${overall_fail:-0}"
