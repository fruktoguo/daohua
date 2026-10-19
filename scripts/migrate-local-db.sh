#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: migrate-local-db.sh
# 主要职责: 对**本地开发数据库**按 platform.schema_version 依次执行 migrations/ 中落后的兼容性迁移脚本。
#
# 硬约束（AGENTS.md §3）:
#   1. 本脚本由本地开发启动链（reset-outdated-players.sh 经 dev.sh）自动调用，也可人工显式执行；
#      服务端启动门禁、verify.sh 与任何运行时装配都**不引用**它；
#   2. 代码中不存在 migration runner，空库启动永远由 PostgresSchema.Sql 直建当前版本；
#   3. migrations/ 目录可整体删除，删除后本脚本只会提示「无迁移可用」。
#
# 使用场景:
#   - 本地库停在旧 schema 版本、服务端启动报「PostgreSQL schema 版本不匹配」时，
#     用本脚本就地升级以保留本地账号与存档；不想保留数据则直接重建库（见 --help）。
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

DB_CONTAINER="${DB_CONTAINER:-daohua-local-db}"
DB_USER="${DB_USER:-daohua}"
DB_NAME="${DB_NAME:-daohua}"
DRY_RUN=0

usage() {
  cat <<'EOF'
用法: scripts/migrate-local-db.sh [--dry-run]

  --dry-run   只打印将要执行的迁移，不改动数据库。

环境变量（默认值对齐本地 compose.yaml）:
  DB_CONTAINER  本地数据库容器名，默认 daohua-local-db
  DB_USER       数据库角色，默认 daohua
  DB_NAME       数据库名，默认 daohua

不想保留本地数据时的替代做法（会清空本地账号与存档）:
  docker exec daohua-local-db psql -U daohua -d postgres -c 'DROP DATABASE daohua;'
  docker exec daohua-local-db psql -U daohua -d postgres -c 'CREATE DATABASE daohua;'
  之后直接启动服务端，由 PostgresSchema.Sql 直建当前版本。
EOF
}

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知参数: %s\n\n' "$arg" >&2; usage >&2; exit 2 ;;
  esac
done

DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-5432}"
case "$DB_HOST" in
  127.0.0.1|localhost|::1) ;;
  *) printf '错误: migrate-local-db.sh 仅允许回环 DB_HOST，拒绝远程地址: %s\n' "$DB_HOST" >&2; exit 1 ;;
esac
DB_PASSWORD="${DB_PASSWORD:-daohua-local-dev}"
psql_exec() {
  if command -v docker >/dev/null 2>&1 \
      && docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$DB_CONTAINER"; then
    docker exec -i "$DB_CONTAINER" psql -X -q -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME"
  elif command -v psql >/dev/null 2>&1; then
    PGPASSWORD="$DB_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -X -q -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME"
  else
    return 127
  fi
}

psql_scalar() {
  if command -v docker >/dev/null 2>&1 \
      && docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$DB_CONTAINER"; then
    docker exec -i "$DB_CONTAINER" psql -X -t -A -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" -c "$1"
  elif command -v psql >/dev/null 2>&1; then
    PGPASSWORD="$DB_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -X -t -A -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" -c "$1"
  else
    return 127
  fi
}

if ! psql_scalar 'SELECT 1;' >/dev/null 2>&1; then
  printf '错误: 无法连接本地 PostgreSQL（docker 容器 %s 与本机 psql 均不可用）。\n' "$DB_CONTAINER" >&2
  exit 1
fi

# 1. 空库直接放行：启动服务端时由权威 DDL 直建当前版本，不走本脚本。
has_version_table="$(psql_scalar "SELECT to_regclass('platform.schema_version') IS NOT NULL;")"
if [[ "$has_version_table" != "t" ]]; then
  printf '本地库尚未初始化（无 platform.schema_version）。无需迁移：直接启动服务端即可直建当前 schema。\n'
  exit 0
fi

current="$(psql_scalar 'SELECT max(version) FROM platform.schema_version;')"
if [[ -z "$current" ]]; then
  printf '错误: platform.schema_version 表为空，无法判定当前版本（该库结构不完整）。\n' >&2
  printf '      请按 --help 中的说明重建本地库，由 PostgresSchema.Sql 直建当前版本。\n' >&2
  exit 1
fi
printf '当前 schema 版本: %s\n' "$current"

# 2. 收集迁移脚本（文件名 NNNN_vX_to_vY.sql，按序号升序）。
shopt -s nullglob
migration_files=(migrations/[0-9][0-9][0-9][0-9]_v*_to_v*.sql)
shopt -u nullglob

if (( ${#migration_files[@]} == 0 )); then
  printf 'migrations/ 目录内没有可用迁移脚本（该目录可整体删除，不影响运行）。\n'
  exit 0
fi

# 3. 逐级链式应用：每轮找到 from 版本等于当前版本的脚本，应用后推进当前版本。
applied=0
while :; do
  next_file=""
  next_version=""
  for file in "${migration_files[@]}"; do
    base="$(basename "$file")"
    from_version="${base%%_to_*}"
    from_version="${from_version##*_v}"
    to_version="${base##*_to_v}"
    to_version="${to_version%.sql}"
    if [[ "$from_version" == "$current" ]]; then
      next_file="$file"
      next_version="$to_version"
      break
    fi
  done

  [[ -z "$next_file" ]] && break
  # v37 会触发资产退款与旧会话散场，不能由开发启动链隐式执行。
  if [[ "$next_file" == "migrations/0037_v36_to_v37.sql" && "$DRY_RUN" != "1" ]]; then
    printf 'v36 → v37 需要显式离线资产迁移；本地启动链不会自动执行。\n' >&2
    printf '请停止 API/worker/计算节点、备份全库并等待租约过期，再人工执行 migrations/0037_v36_to_v37.sql。\n' >&2
    exit 1
  fi


  printf '==> 应用 %s（v%s → v%s）\n' "$(basename "$next_file")" "$current" "$next_version"
  if [[ "$DRY_RUN" == "1" ]]; then
    printf '    （--dry-run：未执行）\n'
  else
    psql_exec < "$next_file"
  fi
  current="$next_version"
  applied=$((applied + 1))
done

if (( applied == 0 )); then
  printf '本地库已是 migrations/ 覆盖到的最新版本，无需迁移。\n'
else
  printf '迁移完成，当前 schema 版本: %s\n' "$current"
fi

# 4. 与代码要求的版本做最终对账（仅用于提示，不构成运行时依赖）。
required="$(grep -oE 'Version = [0-9]+' src/Server/Clean/PostgresSchema.cs | head -n 1 | grep -oE '[0-9]+' || true)"
if [[ -n "$required" && "$current" != "$required" ]]; then
  printf '\n警告: 本地库当前为 v%s，但代码要求 v%s，且 migrations/ 中没有可继续升级的脚本。\n' "$current" "$required" >&2
  printf '      请确认是否需要新增迁移脚本，或按 --help 中的说明重建本地库。\n' >&2
  exit 1
fi

if [[ -n "$required" ]]; then
  printf '版本对账通过：本地库 v%s = 代码要求 v%s。\n' "$current" "$required"
fi
