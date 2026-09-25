#!/usr/bin/env bash
set -euo pipefail

# 三版本基准服务入口。每次只负责一个变体，避免同名 YuoCore 程序集进入同一进程。
# 默认不启动生产服务；--build 只发布当前选择的项目，--probe 通过真实 HTTP/WS 探针。
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VARIANT=""
BUILD=0
PROBE=0
START=0
STOP=0
NO_RESTORE=0
FOREGROUND=0
PORT_OVERRIDE=""
SIMULATION_TARGET=""
SHARD_COUNT=""

usage() {
  cat <<'EOF'
用法：scripts/bench-service.sh --variant oop|ecs-current|ecs-optimized [--build] [--start|--stop|--probe] [--foreground] [--no-restore] [--port 端口] [--simulation-target ISO时间] [--shard-count 数量]
  oop             端口 5128，数据库 daohua_bench_oop，src/Server
  ecs-current     端口 5129，数据库 daohua_bench_ecs_current，src/ServerEcs + vendor/YuoCore
  ecs-optimized   端口 5130，数据库 daohua_bench_ecs_optimized，src/ServerEcsOptimized + vendor/YuoCore-optimized
环境变量：DAOHUA_DATABASE_URL 覆盖数据库连接串，ASPNETCORE_ENVIRONMENT 默认 Development。
--simulation-target 仅在 Development 基准服务固定业务模拟截止时间；租约/会话仍使用系统 UTC。
--shard-count 仅对 ECS 变体设置 Ecs:ShardCount；省略时 ECS 基准默认为 4，OOP 不设置。
EOF
}

while (($#)); do
  case "$1" in
    --variant) VARIANT="${2:?--variant 缺少值}"; shift 2 ;;
    --build) BUILD=1; shift ;;
    --probe) PROBE=1; shift ;;
    --start) START=1; shift ;;
    --stop) STOP=1; shift ;;
    --foreground) FOREGROUND=1; shift ;;
    --no-restore) NO_RESTORE=1; shift ;;
    --port) PORT_OVERRIDE="${2:?--port 缺少值}"; shift 2 ;;
    --simulation-target) SIMULATION_TARGET="${2:?--simulation-target 缺少值}"; shift 2 ;;
    --shard-count) SHARD_COUNT="${2:?--shard-count 缺少值}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ -z "$VARIANT" ]]; then echo "必须提供 --variant" >&2; usage >&2; exit 2; fi
case "$VARIANT" in
  oop)
    PROJECT="$ROOT_DIR/src/Server/Server.csproj"; PORT=5128; DATABASE="daohua_bench_oop"; ARTIFACT="$ROOT_DIR/artifacts/runtime/oop"; CORE="$ROOT_DIR/vendor/YuoCore"; CORE_SHA="452b3a1e202c9454a436b87a49383cef8585773c"; ;;
  ecs-current)
    PROJECT="$ROOT_DIR/src/ServerEcs/ServerEcs.csproj"; PORT=5129; DATABASE="daohua_bench_ecs_current"; ARTIFACT="$ROOT_DIR/artifacts/runtime/ecs-current"; CORE="$ROOT_DIR/vendor/YuoCore"; CORE_SHA="452b3a1e202c9454a436b87a49383cef8585773c"; ;;
  ecs-optimized)
    PROJECT="$ROOT_DIR/src/ServerEcsOptimized/ServerEcsOptimized.csproj"; PORT=5130; DATABASE="daohua_bench_ecs_optimized"; ARTIFACT="$ROOT_DIR/artifacts/runtime/ecs-optimized"; CORE="$ROOT_DIR/vendor/YuoCore-optimized"; CORE_SHA="35a834ff1e7480ebdc5379e5f4d145533bea0177"; ;;
  *) echo "不支持的 variant：$VARIANT" >&2; exit 2 ;;
esac
if [[ -n "$PORT_OVERRIDE" ]]; then PORT="$PORT_OVERRIDE"; fi
if [[ -n "$SHARD_COUNT" ]] && ! [[ "$SHARD_COUNT" =~ ^[0-9]+$ ]] ; then echo "shard 数量无效：$SHARD_COUNT" >&2; exit 2; fi
if ! [[ "$PORT" =~ ^[0-9]+$ ]] || ((PORT < 1024 || PORT > 65535)); then echo "端口无效：$PORT" >&2; exit 2; fi

if [[ ! -f "$PROJECT" ]]; then
  echo "项目尚未落盘：$PROJECT" >&2
  exit 3
fi
if [[ ! -d "$CORE" ]]; then echo "YuoCore 目录不存在：$CORE" >&2; exit 3; fi
actual_sha="$(git -C "$CORE" rev-parse HEAD)"
if [[ "$actual_sha" != "$CORE_SHA" ]]; then echo "YuoCore provenance 不匹配：$CORE 期望 $CORE_SHA 实际 $actual_sha" >&2; exit 3; fi
PID_FILE="$ARTIFACT/service.pid"
LOG_FILE="$ARTIFACT/service.log"
PORT_FILE="$ARTIFACT/service.port"

pid_is_running() {
  local pid="${1:-}"
  [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null
}

pid_is_this_variant() {
  local pid="${1:-}"
  pid_is_running "$pid" || return 1
  [[ "$(lsof -p "$pid" -a -d cwd -Fn 2>/dev/null | sed -n 's/^n//p')" == "$ARTIFACT" ]]
}

port_is_listening() {
  command -v lsof >/dev/null 2>&1 && lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1
}

artifact_process_ids() {
  for pid in $(pgrep -x dotnet 2>/dev/null || true); do
    [[ "$pid" == "$$" ]] && continue
    if pid_is_running "$pid" && [[ "$(lsof -p "$pid" -a -d cwd -Fn 2>/dev/null | sed -n 's/^n//p')" == "$ARTIFACT" ]]; then
      echo "$pid"
    fi
  done
}

if ((STOP)); then
  if [[ -f "$PID_FILE" ]]; then
    pid="$(cat "$PID_FILE")"
    if pid_is_this_variant "$pid"; then
      kill "$pid"
      for _ in {1..20}; do pid_is_running "$pid" || break; sleep 0.1; done
    elif pid_is_running "$pid"; then
      echo "拒绝停止非本变体进程 pid=$pid" >&2
      exit 5
    fi
    rm -f "$PID_FILE"
  fi
  echo "已停止 variant=$VARIANT"
  exit 0
fi

if ((BUILD)); then
  if [[ -f "$PID_FILE" ]] && pid_is_running "$(cat "$PID_FILE")"; then
    echo "拒绝清理仍在运行的产物：variant=$VARIANT pid=$(cat "$PID_FILE")，先 --stop" >&2
    exit 5
  fi
  running_artifacts="$(artifact_process_ids)"
  if [[ -n "$running_artifacts" ]]; then
    echo "拒绝清理仍在运行的产物：variant=${VARIANT} pid=${running_artifacts}，先停止对应进程" >&2
    exit 5
  fi
  rm -rf "$ARTIFACT"
  mkdir -p "$ARTIFACT"
  # BaseIntermediateOutputPath 必须作为全局属性传入（早于 Microsoft.Common.props），
  # 否则项目引用会混用默认 obj，导致两个同名 YuoCore 的生成文件互相污染。
  build_args=(publish "$PROJECT" -c Release -o "$ARTIFACT" "/p:BenchVariant=$VARIANT" "/p:EcsBuildVariant=$VARIANT" "/p:DomainBuildVariant=$VARIANT" "/p:EcsIsolatedBuild=true" "/p:DomainIsolatedBuild=true" "/p:YuoCoreRoot=$CORE" "/m:1")
  if ((NO_RESTORE)); then build_args+=(--no-restore); fi
  dotnet "${build_args[@]}"
fi

if ((START)); then
  case "$VARIANT" in
    oop) dll="$ARTIFACT/Server.dll" ;;
    ecs-current) dll="$ARTIFACT/ServerEcs.dll" ;;
    ecs-optimized) dll="$ARTIFACT/ServerEcsOptimized.dll" ;;
  esac
  if [[ ! -f "$dll" ]]; then
    echo "找不到发布产物，请先使用 --build" >&2; exit 4
  fi
  if [[ -f "$PID_FILE" ]] && pid_is_running "$(cat "$PID_FILE")"; then
    echo "变体已经运行 pid=$(cat "$PID_FILE")，拒绝重复启动" >&2
    exit 5
  fi
  running_artifacts="$(artifact_process_ids)"
  if [[ -n "$running_artifacts" ]]; then
    echo "产物已有进程运行 pid=$running_artifacts，拒绝重复启动" >&2
    exit 5
  fi
  if port_is_listening; then
    echo "端口 $PORT 已被占用，拒绝启动 variant=$VARIANT" >&2
    exit 5
  fi
  export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
  export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:$PORT}"
  # 基准只需要主 API 端口，关闭 Development 运维附加端口避免碰到现有 5129 服务。
  export Ops__Enabled=false
  export Storage__ConnectionString="${DAOHUA_DATABASE_URL:-Host=127.0.0.1;Port=5432;Database=$DATABASE;Username=daohua;Password=daohua-local-dev;Maximum Pool Size=32;Timeout=5;Command Timeout=30}"
  export Storage__EnableSchemaBootstrap=true
  export DAOHUA_BENCH_VARIANT="$VARIANT"
  export DAOHUA_BENCH_MODE="${DAOHUA_BENCH_MODE:-replay}"
  if [[ "$VARIANT" != "oop" ]]; then
    export Ecs__ShardCount="${SHARD_COUNT:-4}"
  fi
  if [[ -n "$SIMULATION_TARGET" ]]; then
    export DAOHUA_BENCH_SIMULATION_TARGET="$SIMULATION_TARGET"
    # ECS 世界的业务时钟必须从同一实验 epoch 启动；租约、会话等运维时钟仍由服务使用 UTC。
    # ServerEcs 在未显式提供 DAOHUA_BENCH_EPOCH 时使用该值作为世界初始时钟。
    if [[ "$VARIANT" != "oop" ]]; then export DAOHUA_BENCH_EPOCH="$SIMULATION_TARGET"; fi
  fi
  : >"$LOG_FILE"
  if ((FOREGROUND)); then
    # 适合本地验收和保留真实 PID 的前台模式；shell exec 后 PID 文件仍对应 dotnet。
    cd "$ARTIFACT"
    echo "$$" >"$PID_FILE"
    trap 'rm -f "$PID_FILE"' EXIT
    exec dotnet "$dll"
  fi
  (
    cd "$ARTIFACT"
    nohup dotnet "$dll" </dev/null >"$LOG_FILE" 2>&1 &
    echo $! >"$PID_FILE"
  )
  echo "$PORT" >"$PORT_FILE"
  pid="$(cat "$PID_FILE")"
  sleep 0.2
  if ! pid_is_this_variant "$pid"; then
    echo "启动失败：未找到真实 dotnet 进程 pid=$pid，日志：$LOG_FILE" >&2
    exit 6
  fi
  echo "已启动 variant=$VARIANT pid=$pid port=$PORT log=$LOG_FILE"
  exit 0
fi

if ((PROBE)); then
  if [[ ! -f "$ROOT_DIR/src/Bench/bin/Release/net10.0/DaoHua.Bench.dll" ]]; then
    dotnet build "$ROOT_DIR/src/Bench/Bench.csproj" -c Release --no-restore >/dev/null
  fi
  dotnet "$ROOT_DIR/src/Bench/bin/Release/net10.0/DaoHua.Bench.dll" probe --variant "$VARIANT" --url "http://127.0.0.1:$PORT" --report "$ARTIFACT/protocol-smoke.json"
fi

echo "variant=$VARIANT port=$PORT database=$DATABASE artifact=$ARTIFACT core=$CORE core_sha=$actual_sha"
