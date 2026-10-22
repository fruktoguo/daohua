#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: migrate-prod-db.sh
# 主要职责: 在生产主服执行 migrations/ 兼容性迁移链，并在迁移前后强制「停写 → 迁移 → 校验」顺序。
#
# 为什么必须停写：
#   存档分段标签（game.player_state_sections / platform.section_flush_queue 的 schema_version）
#   是 SectionCodec 的严格相等校验项。若旧版本进程在迁移窗口内继续写入，会把已被迁移推进的
#   标签写回旧值，导致新版本进程加载该玩家时抛 `section schema 版本不受支持` 并反复重连。
#   因此迁移必须在 app/compute 两个写者全部停止后执行，迁移完成再启动新镜像。
#
# 顺序契约（本脚本强制执行）：
#   1. 校验平台版本、确认 app/compute 已停止（默认拒绝在运行中迁移）；
#   2. 备份全库（默认开启，可用 --skip-backup 跳过）；
#   3. 按 platform.schema_version 链式应用 migrations/ 中落后脚本；
#   4. 校验 platform.schema_version 达到代码要求，且存档标签已全部收敛到当前 SectionCodec 版本；
#   5. 输出结论供部署脚本判定，失败一律非零退出（绝不半途"看起来成功"）。
#
# 用法:
#   deploy/migrate-prod-db.sh                 # 标准生产迁移（停写校验 + 备份 + 迁移 + 校验）
#   deploy/migrate-prod-db.sh --dry-run       # 只打印将要执行的迁移，不改库
#   deploy/migrate-prod-db.sh --allow-running # 排障用：允许写者仍在运行时迁移（危险）
#   deploy/migrate-prod-db.sh --skip-backup   # 跳过本脚本的全库备份（外部已备份时）
#   deploy/migrate-prod-db.sh --no-stop-writers # 只做迁移与校验，不主动停止写者（调用方已停）
#
# 环境变量:
#   DAOHUA_DIR     生产部署目录（默认 /opt/daohua）
#   DB_CONTAINER   数据库容器名（默认 daohua-db）
#   DB_USER/DB_NAME/DB_PASSWORD  数据库连接（默认从 daohua.env 读取）
#   COMPOSE_FILE   compose 文件（默认 ${DAOHUA_DIR}/compose.prod.yaml）
# ==============================================================================
set -Eeuo pipefail

DAOHUA_DIR="${DAOHUA_DIR:-/opt/daohua}"
# docker 命令名可覆盖：仅用于在非生产环境（如 WSL/桌面混合环境）复演同一套流程。
DOCKER="${DOCKER:-docker}"
DB_CONTAINER="${DB_CONTAINER:-daohua-db}"
DB_USER="${DB_USER:-daohua}"
DB_NAME="${DB_NAME:-daohua}"
COMPOSE_FILE="${COMPOSE_FILE:-${DAOHUA_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DAOHUA_DIR}/daohua.env}"
MIGRATIONS_DIR="${MIGRATIONS_DIR:-${DAOHUA_DIR}/migrations}"
BACKUP_DIR="${BACKUP_DIR:-${DAOHUA_DIR}/backups/pre-migration}"

DRY_RUN=0
ALLOW_RUNNING=0
SKIP_BACKUP=0
STOP_WRITERS=1
# 本脚本是否主动停过写者：决定结束时是否需要恢复，避免把调用方特意停下的服务又拉起来。
STOPPED_BY_US=0

usage() {
  sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --allow-running) ALLOW_RUNNING=1 ;;
    --skip-backup) SKIP_BACKUP=1 ;;
    --no-stop-writers) STOP_WRITERS=0 ;;
    -h|--help) usage 0 ;;
    *) printf '未知参数: %s\n' "$arg" >&2; usage 1 ;;
  esac
done

log()  { printf '==> %s\n' "$*"; }
warn() { printf '警告: %s\n' "$*" >&2; }
die()  { printf '错误: %s\n' "$*" >&2; exit 1; }

# ------------------------------------------------------------------------------
# 写者停止/恢复：app 与 compute 都必须停，避免迁移窗口内的旧标签写回。
# ------------------------------------------------------------------------------
compose() {
  if [[ -f "$ENV_FILE" ]]; then
    "$DOCKER" compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
  else
    "$DOCKER" compose -f "$COMPOSE_FILE" "$@"
  fi
}

writers_running() {
  local running=()
  for name in daohua-app daohua-compute; do
    if "$DOCKER" inspect --format '{{.State.Running}}' "$name" 2>/dev/null | grep -q '^true$'; then
      running+=("$name")
    fi
  done
  printf '%s\n' "${running[@]:-}"
}

stop_writers() {
  log '停止 app 与 compute-node 写者（迁移窗口内不允许写入存档标签）'
  compose stop app compute-node
  local remaining
  remaining="$(writers_running)"
  if [[ -n "$remaining" ]]; then
    die "写者未完全停止：${remaining//$'\n'/, }（禁止在运行中迁移）"
  fi
}

restart_writers() {
  log '恢复 app 与 compute-node（使用当前 daohua.env 中的镜像）'
  compose up -d app compute-node
}

# ------------------------------------------------------------------------------
# psql 通道：统一 -X -q -v ON_ERROR_STOP=1，任何 SQL 失败即整条链中止。
# ------------------------------------------------------------------------------
psql_exec() {
  "$DOCKER" exec -i "$DB_CONTAINER" psql -X -q -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME"
}

psql_scalar() {
  "$DOCKER" exec -i "$DB_CONTAINER" psql -X -t -A -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" -c "$1"
}

# ------------------------------------------------------------------------------
# 前置检查
# ------------------------------------------------------------------------------
command -v "$DOCKER" >/dev/null 2>&1 || die "未找到 docker 命令: $DOCKER"
"$DOCKER" inspect "$DB_CONTAINER" >/dev/null 2>&1 || die "数据库容器 ${DB_CONTAINER} 不存在。"
psql_scalar 'SELECT 1;' >/dev/null 2>&1 || die "无法连接数据库（容器 ${DB_CONTAINER} / 库 ${DB_NAME}）。"

[[ -d "$MIGRATIONS_DIR" ]] || die "迁移目录不存在：${MIGRATIONS_DIR}（部署时需同步 migrations/ 到生产目录）"

has_version_table="$(psql_scalar "SELECT to_regclass('platform.schema_version') IS NOT NULL;")"
[[ "$has_version_table" == "t" ]] || die '目标库缺少 platform.schema_version（库结构不完整），拒绝迁移。'

current="$(psql_scalar 'SELECT max(version) FROM platform.schema_version;')"
[[ -n "$current" ]] || die 'platform.schema_version 为空，无法判定当前版本。'
log "当前数据库 schema 版本: v${current}"

required="${EXPECTED_SCHEMA_VERSION:-}"
if [[ -z "$required" ]]; then
  # 生产目录通常不带源码，优先用部署时随镜像传入的期望版本；缺失则只按迁移链推进。
  source_schema="${DAOHUA_DIR}/src/Server/Clean/PostgresSchema.cs"
  if [[ -f "$source_schema" ]]; then
    required="$(grep -oE 'public const int Version = [0-9]+' "$source_schema" | grep -oE '[0-9]+' | head -n1 || true)"
  fi
fi
if [[ -z "$required" ]]; then
  warn '未提供 EXPECTED_SCHEMA_VERSION 且生产目录无源码：仅按 migrations/ 链推进，不做代码版本对账。'
fi

# 写者状态：默认要求已停止，避免迁移窗口内被旧版本覆盖标签。
running="$(writers_running)"
if [[ -n "$running" ]]; then
  if [[ "$ALLOW_RUNNING" == "1" ]]; then
    warn "写者仍在运行：${running//$'\n'/, }；--allow-running 已强制继续（仅排障使用）。"
  elif [[ "$STOP_WRITERS" == "1" ]]; then
    stop_writers
    STOPPED_BY_US=1
  else
    die "写者仍在运行：${running//$'\n'/, }。请先停止 app 与 compute 再迁移（或用 --no-stop-writers 明确表示由调用方负责）。"
  fi
fi

# ------------------------------------------------------------------------------
# 备份（迁移不可逆，失败后必须能回到原点）
# ------------------------------------------------------------------------------
if [[ "$SKIP_BACKUP" == "1" ]]; then
  warn '--skip-backup：跳过全库备份（请确认外部已有可恢复备份）。'
elif [[ "$DRY_RUN" == "1" ]]; then
  log '（--dry-run：跳过备份）'
else
  mkdir -p "$BACKUP_DIR"
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  dump_path="${BACKUP_DIR}/daohua-pre-migration-v${current}-${stamp}.dump"
  log "全库备份 → ${dump_path}"
  "$DOCKER" exec "$DB_CONTAINER" sh -c "pg_dump -U ${DB_USER} -d ${DB_NAME} -Fc -f /tmp/_pre_migration.dump" \
    || die '备份失败，拒绝继续迁移。'
  "$DOCKER" cp "${DB_CONTAINER}:/tmp/_pre_migration.dump" "$dump_path" >/dev/null \
    || die '备份归档拷贝失败，拒绝继续迁移。'
  "$DOCKER" exec "$DB_CONTAINER" rm -f /tmp/_pre_migration.dump >/dev/null 2>&1 || true
  [[ -s "$dump_path" ]] || die "备份文件为空：${dump_path}"
  printf '%s\n' "$dump_path" > "${BACKUP_DIR}/latest"
  log "备份完成（$(du -h "$dump_path" | cut -f1)）"
fi

# ------------------------------------------------------------------------------
# 链式迁移
# ------------------------------------------------------------------------------
shopt -s nullglob
migration_files=("${MIGRATIONS_DIR}"/[0-9][0-9][0-9][0-9]_v*_to_v*.sql)
shopt -u nullglob

if (( ${#migration_files[@]} == 0 )); then
  log 'migrations/ 目录没有可用脚本，跳过迁移链。'
else
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

    # 需要真实资产退款的迁移不允许被自动链顺带执行（必须人工显式确认）。
    if grep -qiE 'refund|退款' "$next_file" && [[ "$DRY_RUN" != "1" ]]; then
      die "迁移 $(basename "$next_file") 涉及真实资产退款，必须人工确认后显式执行。"
    fi

    log "应用 $(basename "$next_file")（v${current} → v${next_version}）"
    if [[ "$DRY_RUN" == "1" ]]; then
      printf '    （--dry-run：未执行）\n'
    else
      psql_exec < "$next_file" || die "迁移 $(basename "$next_file") 执行失败，数据库保持在该脚本事务边界（该脚本自带 BEGIN/COMMIT）。"
    fi
    current="$next_version"
    applied=$((applied + 1))
  done

  if (( applied == 0 )); then
    log '数据库已是 migrations/ 覆盖到的最新版本，无需迁移。'
  else
    log "迁移链执行完成，库版本: v${current}"
  fi
fi

# ------------------------------------------------------------------------------
# 校验：版本对账 + 存档标签零残留
#   存档标签残留是本次生产事故的直接原因（版本号对了但分段标签停在旧值），
#   因此这里把「无任何旧标签行」作为迁移成功的硬条件。
# ------------------------------------------------------------------------------
# 校验本身是只读查询，--dry-run 也要执行：否则会把「有陈旧标签」误报成可部署。
if true; then
  max_version="$(psql_scalar 'SELECT max(version) FROM platform.schema_version;')"
  if [[ -n "$required" && "$max_version" != "$required" ]]; then
    die "库版本 v${max_version} 未达到代码要求 v${required}，且 migrations/ 无可继续升级的脚本。"
  fi
  log "版本对账通过：库 v${max_version}${required:+ = 代码要求 v${required}}"

  # 目标存档标签版本：必须用 SectionCodec.CurrentSchemaVersion，不能用库版本——
  # 两者并不相等（例如库 v41 对应存档标签 v38），用错会把标签推进到解码器不认的值。
  if [[ -z "${EXPECTED_SECTION_VERSION:-}" ]]; then
    die '未提供 EXPECTED_SECTION_VERSION（应等于代码 SectionCodec.CurrentSchemaVersion）：
  没有它无法判定存档标签是否收敛，可能带着旧标签启动新镜像而让玩家无法加载。
  请由部署脚本传入，例如 EXPECTED_SECTION_VERSION=38。'
  fi
  target_label="$EXPECTED_SECTION_VERSION"
  stale_heads="$(psql_scalar "SELECT count(*) FROM game.player_state_heads WHERE schema_version <> ${target_label};")"
  stale_sections="$(psql_scalar "SELECT count(*) FROM game.player_state_sections WHERE schema_version <> ${target_label};")"
  stale_queue="$(psql_scalar "SELECT count(*) FROM platform.section_flush_queue WHERE schema_version <> ${target_label};")"

  log "存档标签分布（要求 = v${target_label}）：heads=${stale_heads} sections=${stale_sections} queue=${stale_queue} 行不一致"
  if (( stale_heads + stale_sections + stale_queue > 0 )); then
    die "存在存档标签不一致（heads=${stale_heads} sections=${stale_sections} queue=${stale_queue}）：
  这些行会被 SectionCodec 严格相等校验拒绝，导致对应玩家无法加载并反复重连。
  请确认迁移脚本是否覆盖了该版本，或人工推进到同一标签后再启动新镜像。
  参考 SQL（先备份、先停写）：
    UPDATE game.player_state_sections   SET schema_version = ${target_label} WHERE schema_version <> ${target_label};
    UPDATE game.player_state_heads      SET schema_version = ${target_label} WHERE schema_version <> ${target_label};
    UPDATE platform.section_flush_queue SET schema_version = ${target_label} WHERE schema_version <> ${target_label};"
  fi
  log '存档标签校验通过：全部行与当前解码器版本一致。'
fi

# ------------------------------------------------------------------------------
# 恢复写者
# ------------------------------------------------------------------------------
if [[ "${STOPPED_BY_US:-0}" == "1" ]]; then
  if [[ "$DRY_RUN" == "1" ]]; then
    warn '（--dry-run：迁移前已停止的写者需要人工恢复：docker compose up -d app compute）'
  else
    restart_writers
  fi
fi

log '迁移流程结束。'
