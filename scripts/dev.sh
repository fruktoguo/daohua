#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: dev.sh
# 主要职责: 一键启动本地前后端全栈开发环境（联调模式）
#
# 核心启动流程:
#   1. 环境前置自检: 检查 YuoCore submodule 是否存在，若缺失自动拉取；
#   2. 前端依赖自检: 检查 frontend/node_modules 是否就绪，若缺失自动 npm install；
#   3. 数据库自检自愈: 检查 PostgreSQL 并按需拉起；
#   4. 端口占用检查: 检测 5128 (后端服务) 与 15172 (前端 Vite)；
#      若占用者命令行指向本仓库（上次会话残留的孤儿进程），自动清理后继续，外来进程报错退出；
#   5. 启动后端守护: 启动 scripts/dev-hot.mjs 监听 C# 源码并在变更时平滑热重启；交互终端由 TUI 收拢日志；
#   6. 等待后端就绪: 轮询探测 http://127.0.0.1:5128/healthz 就绪状态；
#   7. 启动前端服务: 启动 Vite 开发服务器 (127.0.0.1:15172)，反向代理 /api、/ops/api 与 /ws；
#   8. 启动本地域名入口: 若 Docker 可用且 80 端口空闲，以容器方式拉起 Caddy
#      （deploy/caddy-local.Caddyfile），把 http://daohua.localhost 反代到前端（免记端口）；
#      Docker 不可用、80 被占用或容器启动失败时仅提示并跳过，不影响 15172 直连；
#   9. 信号捕获清理: 捕获 Ctrl+C (SIGINT) 及退出信号，级联停止域名入口、前后端全部子进程树；按 q/Ctrl+C 也可从 TUI 触发。
#
# 使用场景:
#   - 本地日常前后端交互功能开发与联调测试。
#
# 环境变量:
#   - DAOHUA_LOCAL_PROXY=0: 显式关闭本地域名入口（默认 1，启用）。
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"
BACKEND_SUPERVISOR_PID=""
FRONTEND_PID=""
TUI_PID=""
TUI_ENABLED=0
# 本地域名入口容器名（docker --rm：停止即自动删除）。
CADDY_CONTAINER="daohua-local-caddy"
LOG_DIR="${TMPDIR:-/tmp}/daohua-dev-$$"
mkdir -p "$LOG_DIR"

write_state() {
  printf '%s\n' "$1" > "$LOG_DIR/state"
}

start_tui() {
  if [[ ! -t 0 || ! -t 1 || "${DAOHUA_TUI:-1}" == "0" ]]; then
    return 0
  fi
  node scripts/dev-tui.mjs --log-dir "$LOG_DIR" --parent-pid "$$" </dev/tty >/dev/tty 2>/dev/tty &
  TUI_PID=$!
  TUI_ENABLED=1
  # TUI 使用独立终端句柄，编排脚本及子服务改写入固定日志文件，避免刷屏。
  exec >> "$LOG_DIR/main.log" 2>&1
}
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
  write_state "正在停止"
  printf '\n==> 正在停止前后端服务...\n'
  if [[ -n "${CADDY_CONTAINER:-}" ]] && command -v docker >/dev/null 2>&1; then
    # 域名入口是 docker --rm 容器：stop 即停止并自动删除。
    docker stop "$CADDY_CONTAINER" >/dev/null 2>&1 || true
  fi
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
  if [[ -n "${TUI_PID:-}" ]]; then
    kill_tree "$TUI_PID" TERM
  fi
  wait 2>/dev/null || true
  printf '服务已全部停止。\n'
}
write_state "准备环境"
trap cleanup EXIT
trap 'cleanup; exit 130' SIGINT
trap 'cleanup; exit 143' SIGTERM
start_tui

# 1. 检查 YuoCore submodule
write_state "检查依赖"
if [[ ! -f vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj ]]; then
  printf '==> 初始化 YuoCore submodule...\n'
  git submodule update --init --recursive
fi

# 2. 检查前端依赖
if [[ ! -d frontend/node_modules ]]; then
  printf '==> 安装前端依赖...\n'
  npm --prefix frontend install
fi

write_state "检查数据库"
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

write_state "检查端口"
free_port_or_fail 5128 "127.0.0.1:5128 (后端服务)"
free_port_or_fail 15172 "127.0.0.1:15172 (前端 Vite)"

write_state "处理存档版本"

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
write_state "启动后端"
printf '==> 启动服务端 (http://127.0.0.1:5128, 源码变更静默 60s 后重建重启)...\n'
if [[ "$TUI_ENABLED" -eq 1 ]]; then
  node scripts/dev-hot.mjs > "$LOG_DIR/backend.log" 2>&1 &
else
  node scripts/dev-hot.mjs &
fi
BACKEND_SUPERVISOR_PID=$!
printf '%s\n' "$BACKEND_SUPERVISOR_PID" > "$LOG_DIR/backend.pid"

# 6. 等待服务端健康端点就绪（监督循环会自动拉起异常退出的进程，这里只探测端点）
write_state "等待后端就绪"
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

# 6.5 运维面板就绪探测：面板并入主端口（页面由前端承载，数据端点在后端）。
#     这里只做告警不阻断启动——面板缺失不影响游戏联调，但静默缺失会误导排查。
if curl -s -f http://127.0.0.1:5128/ops/api/overview >/dev/null 2>&1; then
  printf '==> 运维面板数据端点已就绪 (/ops/api/overview)。\n'
else
  printf '警告: 运维面板数据端点 (/ops/api/overview) 未就绪，面板可能不可用（请检查 Ops:Enabled 与 Development 环境）。\n' >&2
fi

# 7. 启动前端（不打开浏览器）
write_state "启动前端"
printf '==> 启动前端开发服务器 (http://127.0.0.1:15172)...\n'
if [[ "$TUI_ENABLED" -eq 1 ]]; then
  npm --prefix frontend run dev -- --host 127.0.0.1 --port 15172 --no-open > "$LOG_DIR/frontend.log" 2>&1 &
else
  npm --prefix frontend run dev -- --host 127.0.0.1 --port 15172 --no-open &
fi
FRONTEND_PID=$!
printf '%s\n' "$FRONTEND_PID" > "$LOG_DIR/frontend.pid"

# 8. 本地域名入口（可选）：Docker 化 Caddy 绑 80，把 http://daohua.localhost 反代到前端 dev server。
#    *.localhost 由浏览器自动解析到回环，无需改 /etc/hosts；容器端口转发由 Docker Desktop 处理，无需 sudo。
#    Docker 不可用、80 被占用或容器启动失败时一律只提示并跳过，不阻断 15172 直连。
write_state "准备本地域名入口"
LOCAL_ENTRY_STATE="未启用"
CADDY_CONFIG="$ROOT_DIR/deploy/caddy-local.Caddyfile"
if [[ "${DAOHUA_LOCAL_PROXY:-1}" == "1" ]]; then
  if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
    printf '提示: Docker 不可用，跳过本地域名入口（仍可用 http://127.0.0.1:15172）。\n'
  elif [[ ! -f "$CADDY_CONFIG" ]]; then
    printf '警告: 缺少 %s，跳过本地域名入口。\n' "$CADDY_CONFIG" >&2
  elif port_in_use 80; then
    # 80 被占用：若占用者就是本脚本上次留下的同名容器，直接复用（避免误判为外部冲突）。
    if docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$CADDY_CONTAINER"; then
      printf '==> 复用已在运行的本地域名入口容器 %s。\n' "$CADDY_CONTAINER"
      LOCAL_ENTRY_STATE="已就绪（复用现有容器）"
    else
      printf '提示: 端口 80 已被占用，跳过本地域名入口（仍可用 http://127.0.0.1:15172）。\n'
    fi
  else
    printf '==> 启动本地域名入口 (http://daohua.localhost → 127.0.0.1:15172，Docker 化 Caddy)...\n'
    printf '    首次运行需要拉取 caddy:2-alpine 镜像，可能耗时较久。\n'
    docker rm -f "$CADDY_CONTAINER" >/dev/null 2>&1 || true
    if docker run --detach --rm --name "$CADDY_CONTAINER" \
        --publish 80:80 \
        --env DAOHUA_FRONTEND_PORT=15172 \
        --volume "$CADDY_CONFIG:/etc/caddy/Caddyfile:ro" \
        caddy:2-alpine >/dev/null 2>&1; then
      for _ in {1..40}; do
        port_in_use 80 && break
        sleep 0.25
      done
    fi
    if port_in_use 80; then
      LOCAL_ENTRY_STATE="已就绪"
    else
      printf '警告: 本地域名入口未能在等待窗口内就绪（镜像拉取失败或 Caddy 配置有误），已继续启动。\n' >&2
      docker rm -f "$CADDY_CONTAINER" >/dev/null 2>&1 || true
    fi
  fi
fi

write_state "运行中"
printf '\n'
printf '  道化太初 本地开发服务已启动\n'
printf '  热更新:      源码变更静默 60s 后重建重启（编译失败保持旧进程）\n'
printf '  后端 API:    http://127.0.0.1:5128\n'
printf '  WebSocket:  ws://127.0.0.1:5128/ws\n'
printf '  运维面板:    http://127.0.0.1:15172/ops（数据端点经主端口 5128，写操作需面板令牌）\n'
printf '  前端地址:    http://127.0.0.1:15172\n'
if [[ "$LOCAL_ENTRY_STATE" == 已就绪* ]]; then
  printf '  域名入口:    http://daohua.localhost/（面板 http://daohua.localhost/ops）\n'
else
  printf '  域名入口:    %s（需 Docker 可用且 80 端口空闲）\n' "$LOCAL_ENTRY_STATE"
fi
printf '  日志目录:    %s\n' "$LOG_DIR"
printf '  按 q 或 Ctrl+C 停止全部服务\n'
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
