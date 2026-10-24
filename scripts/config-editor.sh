#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: config-editor.sh
# 主要职责: 一键启动独立游戏数值与规则配置编辑器 (Web GUI)
#
# 核心启动流程:
#   1. 依赖自检: 检查 tools/config-editor/node_modules 是否就绪，若缺失自动 npm install；
#   2. 端口占用检查: 检测 5199 端口占用状态；
#   3. 启动开发服务器: 启动 Vite 开发服务器 (127.0.0.1:5199)，内嵌文件读写与 .NET 校验 API 插件；
#   4. 信号捕获清理: 捕获 Ctrl+C (SIGINT) 及退出信号，优雅清理子进程。
#
# 使用场景:
#   - 本地查看、可视化编辑与保存全部游戏配置文件 (config/game-data/*.json)。
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

EDITOR_DIR="tools/config-editor"
PORT="${CONFIG_EDITOR_PORT:-5199}"
EDITOR_PID=""

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
  printf '\n==> 正在停止配置编辑器服务...\n'
  if [[ -n "${EDITOR_PID:-}" ]]; then
    kill_tree "$EDITOR_PID" TERM
  fi
  sleep 1
  if [[ -n "${EDITOR_PID:-}" ]]; then
    kill_tree "$EDITOR_PID" KILL
  fi
  wait 2>/dev/null || true
  printf '配置编辑器已停止。\n'
}

trap cleanup SIGINT SIGTERM EXIT

# 1. 检查依赖
if [[ ! -d "$EDITOR_DIR/node_modules" ]]; then
  printf '==> 安装配置编辑器依赖...\n'
  npm --prefix "$EDITOR_DIR" install
fi

# 2. 检查端口
if ss -H -ltn "sport = :$PORT" 2>/dev/null | grep -q .; then
  printf '错误: 端口 127.0.0.1:%s 已被占用，请先停止冲突进程。\n' "$PORT" >&2
  exit 1
fi

export CHOKIDAR_USEPOLLING=true
export WATCHPACK_POLLING=true

# 3. 启动 Vite 开发服务器
printf '==> 启动道化太初配置编辑器 (http://127.0.0.1:%s)...\n' "$PORT"
npm --prefix "$EDITOR_DIR" run dev -- --host 127.0.0.1 --port "$PORT" &
EDITOR_PID=$!

printf '\n'
printf '====================================================\n'
printf '  道化太初 · 游戏数值与规则配置编辑器已就绪\n'
printf '  访问地址:    http://127.0.0.1:%s\n' "$PORT"
printf '  配置目录:    %s/config/game-data\n' "$ROOT_DIR"
printf '  支持特性:    全量配置解析 / 全字段下拉 / 跨表外键索引 / 快捷键 Ctrl+S / 备份快照\n'
printf '====================================================\n\n'

wait "$EDITOR_PID" 2>/dev/null || true
