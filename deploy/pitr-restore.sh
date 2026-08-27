#!/usr/bin/env bash
# 在隔离目录准备 PostgreSQL PITR 恢复实例。默认只解包和校验；--start 才启动临时容器。

set -Eeuo pipefail

BASE_ARCHIVE=""
TARGET_TIME=""
OUTPUT_DIR=""
WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-/opt/daohua/wal-archive}"
START_INSTANCE=0
CONFIRM=0

usage() {
  printf '%s\n' \
    '用法：pitr-restore.sh --base <base.tar.gz> --target-time <UTC时间> --output <目录> --confirm [--start]' \
    '  默认仅解包、校验 backup_manifest 并生成 recovery.signal。' \
    '  --start 会启动临时容器 daohua-pitr-restore（仍不会触碰生产卷）。'
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
[[ -n "$TARGET_TIME" && "$TARGET_TIME" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T ]] || { printf 'target-time 必须为 ISO-8601 UTC 时间。\n' >&2; exit 2; }
[[ -n "$OUTPUT_DIR" && "$OUTPUT_DIR" == /tmp/daohua-pitr-* ]] || { printf 'output 必须是 /tmp/daohua-pitr-* 隔离目录。\n' >&2; exit 2; }
[[ ! -e "$OUTPUT_DIR" ]] || { printf '输出目录已存在，为避免覆盖请换用新目录: %s\n' "$OUTPUT_DIR" >&2; exit 1; }
[[ -d "$WAL_ARCHIVE_DIR" ]] || { printf '找不到 WAL 归档目录: %s\n' "$WAL_ARCHIVE_DIR" >&2; exit 1; }

if [[ -f "${BASE_ARCHIVE}.sha256" ]]; then
  (cd "$(dirname "$BASE_ARCHIVE")" && sha256sum --check "$(basename "${BASE_ARCHIVE}.sha256")")
fi

mkdir -p "$OUTPUT_DIR"
tar -xzf "$BASE_ARCHIVE" -C "$OUTPUT_DIR"
[[ -f "$OUTPUT_DIR/backup_manifest" ]] || { printf '物理备份缺少 backup_manifest，拒绝继续。\n' >&2; exit 1; }

cat >"$OUTPUT_DIR/postgresql.auto.conf" <<EOF
restore_command = 'cp /wal-archive/%f %p'
recovery_target_time = '${TARGET_TIME}'
recovery_target_action = 'pause'
EOF
touch "$OUTPUT_DIR/recovery.signal"
chmod 0700 "$OUTPUT_DIR"
docker run --rm -u 0 -v "$OUTPUT_DIR:/var/lib/postgresql/data" postgres:18-alpine \
  chown -R postgres:postgres /var/lib/postgresql/data

docker run --rm --user postgres \
  -v "$OUTPUT_DIR:/var/lib/postgresql/data:ro" \
  postgres:18-alpine pg_verifybackup /var/lib/postgresql/data

if [[ "$START_INSTANCE" == 1 ]]; then
  docker rm -f daohua-pitr-restore >/dev/null 2>&1 || true
  docker run -d --name daohua-pitr-restore \
    -p 127.0.0.1:15432:5432 \
    -v "$OUTPUT_DIR:/var/lib/postgresql/data" \
    -v "$WAL_ARCHIVE_DIR:/wal-archive:ro" \
    postgres:18-alpine >/dev/null
  printf 'PITR 临时实例已启动: 127.0.0.1:15432（完成核对后执行 docker rm -f daohua-pitr-restore）\n'
fi
printf 'PITR 恢复目录已准备: %s\n' "$OUTPUT_DIR"
