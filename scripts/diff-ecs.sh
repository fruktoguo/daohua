#!/usr/bin/env bash
set -euo pipefail

# ECS 双内核差分测试台驱动。
# 同一份操作脚本分别跑在 vendor/YuoCore（基线 main）与 vendor/YuoCore-optimized
# 两个同名程序集上——两次隔离构建、两个进程分别运行，事件台账逐字节比对。
#
# 用法：
#   scripts/diff-ecs.sh [--seeds N] [--ops M] [--from-seed N] [--skip-build] [--keep-going]
#
# 产物（均在 artifacts/diff/，不提交）：
#   bin-current/ bin-optimized/     两套隔离的 publish 产物
#   ledger-{variant}-seed{n}.jsonl  随机序列台账
#   ledger-{variant}-directed.jsonl 定向用例台账
#   stderr-{variant}-seed{n}.log    每次运行的内核错误输出
#   report.txt                      差异汇总

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/tests/EcsDifferential/EcsDifferential.csproj"
OUT="$ROOT_DIR/artifacts/diff"
SEEDS=20
OPS=6000
FROM_SEED=1
SKIP_BUILD=0

while (($#)); do
  case "$1" in
    --seeds) SEEDS="${2:?}"; shift 2 ;;
    --ops) OPS="${2:?}"; shift 2 ;;
    --from-seed) FROM_SEED="${2:?}"; shift 2 ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "未知参数：$1" >&2; exit 2 ;;
  esac
done

build_variant() {
  local variant="$1" core="$2" ecs_variant="$3"
  echo "[build] $variant -> $core"
  # BenchVariant 触发仓库根 Directory.Build.props 的 obj/bin 隔离，
  # 两次构建的 YuoCoreRoot 不同，同名程序集不能共享中间产物。
  dotnet publish "$PROJECT" -c Release \
    -o "$OUT/bin-$variant" \
    "/p:YuoCoreRoot=$core" \
    "/p:BenchVariant=diff-$variant" \
    "/p:EcsBuildVariant=$ecs_variant" \
    /m:1
}

if (( ! SKIP_BUILD )); then
  build_variant current "$ROOT_DIR/vendor/YuoCore" current
  build_variant optimized "$ROOT_DIR/vendor/YuoCore-optimized" ecs-optimized
fi

run_variant() {
  local variant="$1"
  local bin="$OUT/bin-$variant/EcsDifferential"
  [[ -x "$bin" ]] || { echo "缺少可执行产物：$bin" >&2; exit 4; }
  for ((s = FROM_SEED; s < FROM_SEED + SEEDS; s++)); do
    "$bin" --variant "$variant" --seed "$s" --ops "$OPS" \
      --out "$OUT/ledger-$variant-seed$s.jsonl" \
      >"$OUT/stdout-$variant-seed$s.log" 2>"$OUT/stderr-$variant-seed$s.log" \
      || echo "[warn] $variant seed=$s 退出码 $?" >&2
  done
  "$bin" --variant "$variant" --directed \
    --out "$OUT/ledger-$variant-directed.jsonl" \
    >"$OUT/stdout-$variant-directed.log" 2>"$OUT/stderr-$variant-directed.log" \
    || echo "[warn] $variant directed 退出码 $?" >&2
}

run_variant current
run_variant optimized

python3 "$ROOT_DIR/scripts/ecs-diff-report.py" "$OUT" | tee "$OUT/report.txt"
