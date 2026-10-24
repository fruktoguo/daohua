#!/usr/bin/env bash
# 在隔离目录准备 PostgreSQL PITR 恢复实例。默认只解包和校验；--start 才启动临时容器。

set -Eeuo pipefail

BASE_ARCHIVE=""
TARGET_TIME=""
OUTPUT_DIR=""
DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
START_INSTANCE=0
CONFIRM=0
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi
WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"

usage() {
  printf '%s\n' \
    '用法：pitr-restore.sh --base <base.tar.gz> --target-time <UTC时间> --output <目录> --confirm [--start]' \
    '  默认仅解包、校验 backup_manifest 并生成 recovery.signal。' \
    '  --start 仅启动隔离临时实例，不代表恢复已验收；必须另行执行 SQL/业务核对。'
}

while (($# > 0)); do
  case "$1" in
    --base) BASE_ARCHIVE="${2:?--base 缺少值}"; shift 2 ;;
    --target-time) TARGET_TIME="${2:?--target-time 缺少值}"; shift 2 ;;
    --output) OUTPUT_DIR="${2:?--output 缺少值}"; shift 2 ;;
    --wal-dir) WAL_ARCHIVE_DIR="${2:?--wal-dir 缺少值}"; shift 2 ;;
    --start) START_INSTANCE=1; shift ;;
    --confirm) CONFIRM=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知参数: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ "$CONFIRM" == 1 ]] || { printf '恢复属于高风险操作，必须显式传入 --confirm。\n' >&2; exit 1; }
[[ -f "$BASE_ARCHIVE" ]] || { printf '找不到物理备份: %s\n' "$BASE_ARCHIVE" >&2; exit 1; }
[[ "$TARGET_TIME" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|\+00:00)$ ]] || { printf 'target-time 必须为 ISO-8601 UTC 时间。\n' >&2; exit 2; }
# PostgreSQL 的恢复参数使用数值 UTC 偏移；保留小数秒，避免 Z 形式在恢复启动时被拒绝。
if [[ "$TARGET_TIME" == *Z ]]; then TARGET_TIME="${TARGET_TIME%Z}+00:00"; fi
[[ -n "$OUTPUT_DIR" && "$OUTPUT_DIR" == /tmp/daohua-pitr-* ]] || { printf 'output 必须是 /tmp/daohua-pitr-* 隔离目录。\n' >&2; exit 2; }
[[ ! -e "$OUTPUT_DIR" ]] || { printf '输出目录已存在，为避免覆盖请换用新目录: %s\n' "$OUTPUT_DIR" >&2; exit 1; }
[[ -d "$WAL_ARCHIVE_DIR" ]] || { printf '找不到 WAL 归档目录: %s\n' "$WAL_ARCHIVE_DIR" >&2; exit 1; }
WAL_ARCHIVE_DIR="$WAL_ARCHIVE_DIR" "$SCRIPT_DIR/wal-archive.sh" verify

[[ -f "${BASE_ARCHIVE}.sha256" ]] || { printf '物理备份缺少 SHA-256 校验文件，拒绝恢复。\n' >&2; exit 1; }
(cd "$(dirname "$BASE_ARCHIVE")" && sha256sum --check "$(basename "${BASE_ARCHIVE}.sha256")")

mkdir -p "$OUTPUT_DIR"
tar -xzf "$BASE_ARCHIVE" -C "$OUTPUT_DIR"
[[ -f "$OUTPUT_DIR/backup_manifest" ]] || { printf '物理备份缺少 backup_manifest，拒绝继续。\n' >&2; exit 1; }

chmod 0700 "$OUTPUT_DIR"
docker run --rm -u 0 -v "$OUTPUT_DIR:/var/lib/postgresql/data" postgres:18-alpine \
  chown -R postgres:postgres /var/lib/postgresql/data

docker run --rm --user postgres \
  -v "$OUTPUT_DIR:/var/lib/postgresql/data:ro" \
  postgres:18-alpine pg_verifybackup /var/lib/postgresql/data

# 必须先核验原始 manifest，再写恢复配置，否则会自行破坏备份校验和。
cat >"$OUTPUT_DIR/postgresql.auto.conf" <<EOF
restore_command = 'gzip -dc /wal-archive/%f.gz > %p'
recovery_target_time = '${TARGET_TIME}'
recovery_target_action = 'pause'
EOF
touch "$OUTPUT_DIR/recovery.signal"
docker run --rm -u 0 -v "$OUTPUT_DIR:/var/lib/postgresql/data" postgres:18-alpine \
  chown postgres:postgres /var/lib/postgresql/data/postgresql.auto.conf /var/lib/postgresql/data/recovery.signal

if [[ "$START_INSTANCE" == 1 ]]; then
  [[ -n "${DB_BOOTSTRAP_USER:-}" ]] || { printf '隔离恢复需要 DB_BOOTSTRAP_USER。
' >&2; exit 1; }
  [[ -n "${DB_BOOTSTRAP_PASSWORD:-}" ]] || { printf '隔离恢复需要 DB_BOOTSTRAP_PASSWORD。
' >&2; exit 1; }
  export POSTGRES_USER="$DB_BOOTSTRAP_USER"
  export POSTGRES_PASSWORD="$DB_BOOTSTRAP_PASSWORD"
  export POSTGRES_DB="${POSTGRES_DB:-${DB_NAME:-}}"
  [[ -n "$POSTGRES_DB" ]] || { printf '隔离恢复需要 POSTGRES_DB 或 DB_NAME。\n' >&2; exit 1; }
  container_name="daohua-pitr-restore-$$"
  docker run -d --name "$container_name" \
    --env POSTGRES_USER --env POSTGRES_PASSWORD --env POSTGRES_DB \
    --env PGDATA=/var/lib/postgresql/data \
    -p 127.0.0.1:15432:5432 \
    -v "$OUTPUT_DIR:/var/lib/postgresql/data" \
    -v "$WAL_ARCHIVE_DIR:/wal-archive:ro" \
    postgres:18-alpine postgres -c archive_mode=off >/dev/null
  recovered=0
  for ((attempt=0; attempt<60; attempt++)); do
    if [[ "$(docker exec "$container_name" sh -ceu '
      export PGPASSWORD="$POSTGRES_PASSWORD"
      psql -X -t -A -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 \
        -c "SELECT pg_is_in_recovery() AND pg_is_wal_replay_paused();"
    ' 2>/dev/null)" == t ]]; then recovered=1; break; fi
    sleep 1
  done
  if [[ "$recovered" != 1 ]]; then
    printf 'PITR 未到达指定恢复目标并暂停，拒绝报告恢复成功；已停止本次隔离实例。\n' >&2
    docker rm -f "$container_name" >/dev/null
    exit 1
  fi
  printf 'PITR 已到达恢复目标并暂停: 127.0.0.1:15432，容器 %s（业务核对后手动移除该容器）。\n' "$container_name"
fi
printf 'PITR 恢复目录已准备: %s\n' "$OUTPUT_DIR"
