#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: dev.sh
# 主要职责: 一键启动本地前后端全栈开发环境（联调模式）
#
# 核心启动流程:
#   1. 环境前置自检: 检查 YuoCore submodule 是否存在，若缺失自动拉取；
#   2. 前端依赖自检: 检查 frontend/node_modules 是否就绪，若缺失自动 npm install；
#   3. 数据库自检自愈: 检查 PostgreSQL 并按需拉起；
#   4. 端口占用检查: 检测 5128 (后端服务)、5129 (运维面板) 与 15172 (前端 Vite)；
#      若占用者命令行指向本仓库（上次会话残留的孤儿进程），自动清理后继续，外来进程报错退出；
#   5. 启动后端守护: 启动 scripts/dev-hot.mjs 监听 C# 源码并在变更时平滑热重启后端；
#   6. 等待后端就绪: 轮询探测 http://127.0.0.1:5128/healthz 就绪状态；
#   7. 启动前端服务: 启动 Vite 开发服务器 (127.0.0.1:15172)，反向代理 /api 与 /ws；
#   8. 信号捕获清理: 捕获 Ctrl+C (SIGINT) 及退出信号，级联停止前后端全部子进程树。
#
# 使用场景:
#   - 本地日常前后端交互功能开发与联调测试。
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BACKEND_SUPERVISOR_PID=""
FRONTEND_PID=""

port_in_use() {
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -H -ltn "sport = :${port}" 2>/dev/null | grep -q .
    return $?
  fi
  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"$port" -sTCP:LISTEN -t 2>/dev/null | grep -q .
    return $?
  fi
  return 1
}

port_pids() {
  local port="$1"
  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"$port" -sTCP:LISTEN -t 2>/dev/null | tr '\n' ' '
  fi
}

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
  if [[ -n "${FRONTEND_PID:-}" ]]; then
    kill_tree "$FRONTEND_PID" TERM
  fi
  if [[ -n "${BACKEND_SUPERVISOR_PID:-}" ]]; then
    # 对 dev-hot 守护进程整棵进程树发 TERM；守护进程会先优雅停止服务端
    # （保存市场账本并释放写者租约）再退出。
    kill_tree "$BACKEND_SUPERVISOR_PID" TERM
  fi
  sleep 2
  if [[ -n "${FRONTEND_PID:-}" ]]; then
    kill_tree "$FRONTEND_PID" KILL
  fi
  if [[ -n "${BACKEND_SUPERVISOR_PID:-}" ]]; then
    kill_tree "$BACKEND_SUPERVISOR_PID" KILL
  fi
  wait 2>/dev/null || true
  printf '服务已全部停止。\n'
}

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

# 3. 检查并准备 PostgreSQL 数据库
DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-5432}"
db_ready() {
  (echo > /dev/tcp/"$DB_HOST"/"$DB_PORT") 2>/dev/null
}

if ! db_ready; then
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    printf '==> 检测到 PostgreSQL (%s:%s) 未就绪，自动通过 Docker Compose 启动本地数据库...\n' "$DB_HOST" "$DB_PORT"
    if ! docker compose up -d db; then
      printf '错误: Docker Compose 无法启动本地 PostgreSQL (%s:%s)。\n' "$DB_HOST" "$DB_PORT" >&2
      exit 1
    fi
    printf '==> 等待 PostgreSQL 数据库就绪...\n'
    for _ in {1..30}; do
      if db_ready; then
        break
      fi
      sleep 0.5
    done
  else
    printf '错误: 本地 PostgreSQL 未在 %s:%s 运行，且 Docker 不可用。\n' "$DB_HOST" "$DB_PORT" >&2
  fi
fi

if ! db_ready; then
  printf '错误: PostgreSQL 仍未就绪 (%s:%s)，已停止启动流程。\n' "$DB_HOST" "$DB_PORT" >&2
  exit 1
fi

# 4. 检查端口占用：占用者命令行包含本仓库路径时视为上次会话残留的孤儿（dev.sh 被强杀
#    时 cleanup trap 不会执行，子进程树会残留），自动清理后继续；否则报错退出。
is_repo_process() {
  ps -p "$1" -o command= 2>/dev/null | grep -qF "$ROOT_DIR"
}

free_port_or_fail() {
  local port="$1" desc="$2"
  port_in_use "$port" || return 0
  local pids all_repo=1 pid
  pids="$(port_pids "$port")"
  for pid in $pids; do
    is_repo_process "$pid" || all_repo=0
  done
  if [[ "$all_repo" -eq 1 ]]; then
    printf '==> 端口 %s 被上次会话残留的进程占用 (PID: %s)，自动清理后继续。\n' "$desc" "$pids"
    for pid in $pids; do kill_tree "$pid" TERM; done
    sleep 1
    for pid in $pids; do kill_tree "$pid" KILL; done
  fi
  if port_in_use "$port"; then
    printf '错误: 端口 %s 已被占用 (PID: %s)，请先停止冲突进程。\n' "$desc" "$(port_pids "$port")" >&2
    exit 1
  fi
}

free_port_or_fail 5128 "127.0.0.1:5128 (后端服务)"
free_port_or_fail 15172 "127.0.0.1:15172 (前端 Vite)"
# 本地运维面板端口（对齐 appsettings.Development.json 的 Ops:Port，绑定 0.0.0.0）。
free_port_or_fail 5129 "5129 (本地运维面板)"

trap cleanup SIGINT SIGTERM EXIT

# 4.5 存档版本漂移处置：SectionCodec schema 升级后旧版本玩家的 head/section 载荷会被
#     运行时直接拒绝（连接即抛「schema 版本不受支持」）。在后端启动前（此时旧进程已清理、
#     无并发写库）优先执行 migrations/ 兼容性迁移链；不可迁移的漂移必须经
#     控制台交互确认才重置（保留账号与角色名）。操作者拒绝或脚本失败一律中止启动，
#     禁止带着必然崩溃的旧存档拉起服务端。
if ! scripts/reset-outdated-players.sh; then
  printf '错误: 存档版本漂移未处置（迁移失败或已取消重置），启动中止。\n' >&2
  exit 1
fi

# 5. 启动服务端（dev-hot.mjs 守护进程：源码变更进入 60s 防抖静默期，期间有新变更则刷新；
#    静默期满先 dotnet build，编译失败保持旧进程，成功才优雅重启并自动拉起异常退出）
printf '==> 启动服务端 (http://127.0.0.1:5128, 源码变更静默 60s 后重建重启)...\n'
node scripts/dev-hot.mjs &
BACKEND_SUPERVISOR_PID=$!

# 6. 等待服务端健康端点就绪（监督循环会自动拉起异常退出的进程，这里只探测端点）
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
  printf '错误: 服务端未能在等待窗口内就绪，已停止启动流程。\n' >&2
  exit 1
fi

# 7. 启动前端（不打开浏览器）
printf '==> 启动前端开发服务器 (http://127.0.0.1:15172)...\n'
npm --prefix frontend run dev -- --host 127.0.0.1 --port 15172 --no-open &
FRONTEND_PID=$!

printf '\n'
printf '  道化太初 本地开发服务已启动\n'
printf '  热更新:      源码变更静默 60s 后重建重启（编译失败保持旧进程）\n'
printf '  后端 API:    http://127.0.0.1:5128\n'
printf '  WebSocket:  ws://127.0.0.1:5128/ws\n'
printf '  运维面板:    http://127.0.0.1:5129/ops（局域网: http://<本机IP>:5129/ops）\n'
printf '  前端地址:    http://127.0.0.1:15172\n'
printf '  ----------------------------------------------------\n'
printf '====================================================\n\n'

# 挂起等待监督循环或前端退出；任一退出即触发 cleanup 停止全部服务
# 兼容 macOS 自带 Bash 3.2 (不支持 wait -n) 与 Linux Bash 4.3+
if ! wait -n "$BACKEND_SUPERVISOR_PID" "$FRONTEND_PID" 2>/dev/null; then
  while kill -0 "$BACKEND_SUPERVISOR_PID" 2>/dev/null && kill -0 "$FRONTEND_PID" 2>/dev/null; do
    sleep 1 &
    wait $! 2>/dev/null || true
  done
fi
