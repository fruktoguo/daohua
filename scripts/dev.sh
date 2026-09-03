#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: dev.sh
# 主要职责: 一键启动本地前后端全栈开发环境（联调模式）
#
# 核心启动流程:
#   1. 环境前置自检: 检查 YuoCore submodule 是否存在，若缺失自动拉取；
#   2. 前端依赖自检: 检查 frontend/node_modules 是否就绪，若缺失自动 npm install；
#   3. 数据库自检自愈: 检查 PostgreSQL 并按需拉起，校验配表指纹并交互自愈（清空/刷平）；
#   4. 端口占用检查: 检测 5128 (后端服务) 与 15172 (前端 Vite) 是否被占用；
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

# 3.1 检查游戏数据指纹一致性（策划配表变动时交互选择：直接清空 vs 忽略刷平）
check_and_resolve_game_data_fingerprint() {
  local game_data_dir="${ROOT_DIR}/config/game-data"
  if [[ ! -d "$game_data_dir" ]]; then
    return 0
  fi

  # 计算当前源码配置中 JSON 资源的 SHA-256 前 16 位指纹
  local current_fingerprint
  current_fingerprint=$(node -e '
    const fs = require("fs");
    const path = require("path");
    const crypto = require("crypto");
    function listFiles(dir, base = dir) {
      let results = [];
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) results.push(...listFiles(full, base));
        else if (entry.name.endsWith(".json")) results.push({ full, rel: path.relative(base, full).split(path.sep).join("/") });
      }
      return results;
    }
    const files = listFiles(path.resolve("config/game-data"));
    files.sort((a, b) => a.rel.localeCompare(b.rel));
    const hash = crypto.createHash("sha256");
    for (const f of files) hash.update(fs.readFileSync(f.full));
    process.stdout.write(hash.digest("hex").slice(0, 16).toLowerCase());
  ' 2>/dev/null || true)

  if [[ -z "$current_fingerprint" ]]; then
    return 0
  fi

  # 查询本地数据库中现存玩家存档的不同指纹
  local query_sql="SELECT CASE WHEN to_regclass('game.player_state_heads') IS NOT NULL THEN COALESCE((SELECT string_agg(DISTINCT game_data_fingerprint, ', ') FROM game.player_state_heads WHERE game_data_fingerprint <> ''), '') ELSE '' END;"
  local db_fingerprints=""

  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    db_fingerprints=$(docker compose exec -T db psql -U "${DB_USER:-daohua}" -d "${DB_NAME:-daohua}" -t -A -c "$query_sql" 2>/dev/null || true)
  fi

  # 若数据库无任何存档记录，或现有存档指纹与当前配表指纹完全一致，则无需处理
  if [[ -z "$db_fingerprints" ]] || [[ "$db_fingerprints" == "$current_fingerprint" ]]; then
    return 0
  fi

  printf '\n'
  printf '%s\n' '===================================================='
  printf '==> [数据校验] 检测到策划配表指纹与数据库现有存档不一致:\n'
  printf '    数据库现有存档指纹: %s\n' "$db_fingerprints"
  printf '    当前静态配表最新指纹: %s\n' "$current_fingerprint"
  printf '\n'
  printf '配表变动可能引发旧存档结算冲突或客户端断连拦截。\n'
  printf '请选择数据处理策略:\n'
  printf '  [1] 直接清空: 级联清空本地旧测试数据（账号与存档重置，最干净，推荐）\n'
  printf '  [2] 忽略刷平: 将旧存档指纹强行刷平为最新指纹（保留当前角色与背包）\n'
  printf '  [3] 保持原样: 不做任何修改，直接继续启动\n'
  printf '  [0] 退出启动\n'
  printf '%s\n' '----------------------------------------------------'

  local choice=""
  if [[ -n "${DEV_DB_ACTION:-}" ]]; then
    choice="$DEV_DB_ACTION"
    printf '检测到 DEV_DB_ACTION=%s，自动执行该策略。\n' "$choice"
  elif [[ -t 0 ]] || [[ -r /dev/tty ]]; then
    read -r -p "请输入选项 [1/2/3/0] (默认: 1): " choice < /dev/tty || choice="1"
  else
    choice="3"
    printf '非交互终端环境，默认保持原样继续启动。\n'
  fi
  choice="${choice:-1}"

  case "$choice" in
    1|wipe|clear)
      printf '==> 正在清空本地测试数据...\n'
      docker compose exec -T db psql -U "${DB_USER:-daohua}" -d "${DB_NAME:-daohua}" -c "TRUNCATE TABLE identity.accounts, platform.section_flush_queue, platform.command_receipts, platform.integration_outbox CASCADE;" >/dev/null 2>&1 || true
      printf '==> 本地测试数据已清空。进入前端重新注册/登录将自动生成纯净初始角色。\n'
      ;;
    2|align|force)
      printf '==> 正在将旧存档指纹强行刷平为 %s...\n' "$current_fingerprint"
      docker compose exec -T db psql -U "${DB_USER:-daohua}" -d "${DB_NAME:-daohua}" -c "UPDATE game.player_state_heads SET game_data_fingerprint = '${current_fingerprint}'; UPDATE platform.section_flush_queue SET game_data_fingerprint = '${current_fingerprint}';" >/dev/null 2>&1 || true
      printf '==> 旧存档指纹已强行更新完毕。\n'
      ;;
    3|skip|ignore)
      printf '==> 保持原样，跳过指纹处理。\n'
      ;;
    0|q|quit|exit)
      printf '==> 用户已取消启动。\n'
      exit 0
      ;;
    *)
      printf '==> 未知输入 [%s]，默认执行 [1] 直接清空本地测试数据...\n' "$choice"
      docker compose exec -T db psql -U "${DB_USER:-daohua}" -d "${DB_NAME:-daohua}" -c "TRUNCATE TABLE identity.accounts, platform.section_flush_queue, platform.command_receipts, platform.integration_outbox CASCADE;" >/dev/null 2>&1 || true
      printf '==> 本地测试数据已清空。\n'
      ;;
  esac
  printf '%s\n\n' '===================================================='
}

check_and_resolve_game_data_fingerprint

# 4. 检查端口占用
if port_in_use 5128; then
  printf '错误: 端口 127.0.0.1:5128 已被占用 (PID: %s)，请先停止冲突进程。\n' "$(port_pids 5128)" >&2
  exit 1
fi

if port_in_use 15172; then
  printf '错误: 端口 127.0.0.1:15172 已被占用 (PID: %s)，请先停止冲突进程。\n' "$(port_pids 15172)" >&2
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
