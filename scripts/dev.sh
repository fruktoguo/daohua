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
  printf '\n==> 正在停止前后端服务...\n'
  if [[ -n "$FRONTEND_PID" ]]; then
    kill_tree "$FRONTEND_PID" TERM
  fi
  if [[ -n "$BACKEND_PID" ]]; then
    kill_tree "$BACKEND_PID" TERM
  fi
  sleep 0.5
  if [[ -n "$FRONTEND_PID" ]]; then
    kill_tree "$FRONTEND_PID" KILL
  fi
  if [[ -n "$BACKEND_PID" ]]; then
    kill_tree "$BACKEND_PID" KILL
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

# 4. 启动服务端
printf '==> 启动服务端 (http://127.0.0.1:5128)...\n'
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Xiuxian.Server/Xiuxian.Server.csproj &
BACKEND_PID=$!

# 5. 等待服务端健康端点就绪
printf '==> 等待服务端就绪...\n'
BACKEND_READY=0
for _ in {1..60}; do
  if curl -s -f http://127.0.0.1:5128/healthz >/dev/null 2>&1; then
    BACKEND_READY=1
    break
  fi
  if ! kill -0 "$BACKEND_PID" 2>/dev/null; then
    printf '错误: 服务端进程异常退出。\n' >&2
    exit 1
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
printf '====================================================\n'
printf '  道化太初 本地开发服务已启动\n'
printf '  ----------------------------------------------------\n'
printf '  后端 API:    http://127.0.0.1:5128\n'
printf '  WebSocket:  ws://127.0.0.1:5128/ws\n'
printf '  前端地址:    http://127.0.0.1:15172\n'
printf '  ----------------------------------------------------\n'
printf '  提示: 请在浏览器中手动打开前端地址进行体验。\n'
printf '  按 Ctrl+C 可停止全部服务。\n'
printf '====================================================\n\n'

# 挂起等待任意子进程退出
wait
