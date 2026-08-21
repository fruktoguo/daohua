#!/usr/bin/env bash

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BACKEND_PID=""
FRONTEND_PID=""

kill_tree() {
  local pid="$1"
  local sig="${2:-TERM}"
  if [[ -z "$pid" ]] || ! kill -0 "$pid" 2>/dev/null; then
    return 0
  fi
  local cpid
  for cpid in $(pgrep -P "$pid" 2>/dev/null || true); do
    kill_tree "$cpid" "$sig"
  done
  kill -"$sig" "$pid" 2>/dev/null || true
}

cleanup() {
  trap - SIGINT SIGTERM EXIT
  SHUTTING_DOWN=1
  printf '\n==> 正在停止前后端服务...\n'
  if [[ -n "$FRONTEND_PID" ]]; then
    kill_tree "$FRONTEND_PID" TERM
  fi
  if [[ -n "$BACKEND_SUPERVISOR_PID" ]]; then
    # 对监督子 shell 整棵进程树发 TERM，带走其下的 dotnet watch 与服务端进程；
    # 服务端收到 TERM 后会走优雅停服（保存市场账本并释放写者租约）。
    kill_tree "$BACKEND_SUPERVISOR_PID" TERM
  fi
  sleep 2
  if [[ -n "$FRONTEND_PID" ]]; then
    kill_tree "$FRONTEND_PID" KILL
  fi
  if [[ -n "$BACKEND_SUPERVISOR_PID" ]]; then
    kill_tree "$BACKEND_SUPERVISOR_PID" KILL
  fi
  wait 2>/dev/null || true
  printf '服务已全部停止。\n'
}

trap cleanup SIGINT SIGTERM EXIT

# 1. 检查 YuoCore submodule
if [[ ! -f vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj ]]; then
  printf '==> 初始化 YuoCore submodule...\n'
  git submodule update --init --recursive
fi

# 2. 检查前端依赖
if [[ ! -d frontend/node_modules ]]; then
  printf '==> 安装前端依赖...\n'
  npm --prefix frontend install
fi

# 3. 检查端口占用
if ss -H -ltn 'sport = :5128' 2>/dev/null | grep -q .; then
  printf '错误: 端口 127.0.0.1:5128 已被占用，请先停止冲突进程。\n' >&2
  exit 1
fi

if ss -H -ltn 'sport = :15172' 2>/dev/null | grep -q .; then
  printf '错误: 端口 127.0.0.1:15172 已被占用，请先停止冲突进程。\n' >&2
  exit 1
fi

# 4. 启动服务端（dotnet watch：检测到任意源码修改后自动重新构建并重启进程；
#    编译失败时保持旧进程继续运行，与 mud_mmo_next 的 dev-hot 行为一致）
printf '==> 启动服务端 (http://127.0.0.1:5128, 源码变更自动重建重启)...\n'

# 监督 dotnet watch：进程异常退出（如热重启时市场租约交接失败）后按退避自动拉起，
# 30 秒内连续 3 次失败则放慢到 5 秒间隔，避免崩溃循环刷屏。
BACKEND_RESTART_COUNT=0
BACKEND_LAST_EXIT_AT=0
SHUTTING_DOWN=0

start_backend() {
  ASPNETCORE_ENVIRONMENT=Development dotnet watch --no-hot-reload \
    --project src/Xiuxian.Server/Xiuxian.Server.csproj &
}

# 监督循环在子 shell 中运行；父脚本只持有监督进程 PID，
# 停止时对监督进程整棵进程树发信号即可带走 dotnet watch。
supervise_backend() {
  local exit_code now delay
  while true; do
    start_backend
    local child_pid=$!
    set +e
    wait "$child_pid"
    exit_code=$?
    set -e
    if [[ "$SHUTTING_DOWN" -eq 1 ]]; then
      return 0
    fi
    now="$(date +%s)"
    if (( now - BACKEND_LAST_EXIT_AT < 30 )); then
      BACKEND_RESTART_COUNT=$((BACKEND_RESTART_COUNT + 1))
    else
      BACKEND_RESTART_COUNT=0
    fi
    BACKEND_LAST_EXIT_AT="$now"
    delay=1
    if (( BACKEND_RESTART_COUNT >= 3 )); then
      delay=5
    fi
    printf '==> 服务端监督: dotnet watch 异常退出 (code=%s)，%ss 后自动拉起\n' "$exit_code" "$delay" >&2
    sleep "$delay"
  done
}

supervise_backend &
BACKEND_SUPERVISOR_PID=$!

# 5. 等待服务端健康端点就绪（监督循环会自动拉起异常退出的进程，这里只探测端点）
printf '==> 等待服务端就绪...\n'
BACKEND_READY=0
for _ in {1..120}; do
  if curl -s -f http://127.0.0.1:5128/healthz >/dev/null 2>&1; then
    BACKEND_READY=1
    break
  fi
  sleep 0.5
done

if [[ "$BACKEND_READY" -ne 1 ]]; then
  printf '警告: 服务端就绪等待超时，继续启动前端...\n' >&2
fi

# 6. 启动前端（不打开浏览器）
printf '==> 启动前端开发服务器 (http://127.0.0.1:15172)...\n'
npm --prefix frontend run dev -- --host 127.0.0.1 --port 15172 --no-open &
FRONTEND_PID=$!

printf '\n'
printf '  道化太初 本地开发服务已启动\n'
printf '  ----------------------------------------------------\n'
printf '  后端 API:    http://127.0.0.1:5128\n'
printf '  WebSocket:  ws://127.0.0.1:5128/ws\n'
printf '  前端地址:    http://127.0.0.1:15172\n'
printf '  热更新:      服务端源码变更后自动重建并重启（编译失败保持旧进程）\n'
printf '====================================================\n\n'

# 挂起等待监督循环或前端退出；任一退出即触发 cleanup 停止全部服务
wait -n "$BACKEND_SUPERVISOR_PID" "$FRONTEND_PID" 2>/dev/null || true

