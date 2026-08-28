#!/usr/bin/env bash
# 备份门禁：校验 custom dump、物理 base backup manifest 与 WAL 归档完整性。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
BACKUP_DIR="${BACKUP_DIR:-${DEPLOY_DIR}/backups}"
WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
DB_CONTAINER_NAME="${DB_CONTAINER_NAME:-daohua-db}"

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi

compose() {
  if [[ -f "$ENV_FILE" ]]; then docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"; else docker compose -f "$COMPOSE_FILE" "$@"; fi
}

latest_dump="$(find "$BACKUP_DIR/hourly" "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly" -type f -name '*.dump' -print 2>/dev/null | sort | tail -n 1 || true)"
[[ -n "$latest_dump" ]] || { printf '没有找到 custom dump 备份。\n' >&2; exit 1; }
dump_sha="${latest_dump}.sha256"
if [[ ! -f "$dump_sha" ]]; then
  printf '缺少 custom dump 校验和文件，备份门禁失败: %s\n' "$dump_sha" >&2
  exit 1
fi
(cd "$(dirname "$latest_dump")" && sha256sum --check "$(basename "$dump_sha")")
compose exec -T db pg_restore --list <"$latest_dump" >/dev/null
printf 'custom dump 校验通过: %s\n' "$latest_dump"

latest_base="$(find "$BACKUP_DIR/base" -maxdepth 1 -type f -name '*.tar.gz' -print 2>/dev/null | sort | tail -n 1 || true)"
if [[ -n "$latest_base" ]]; then
  tmp_dir="$(mktemp -d /tmp/daohua-verify-base.XXXXXX)"
  trap 'rm -rf -- "$tmp_dir"' EXIT
  base_sha="${latest_base}.sha256"
  if [[ ! -f "$base_sha" ]]; then
    printf '缺少 base backup 校验和文件，备份门禁失败: %s\n' "$base_sha" >&2
    exit 1
  fi
  (cd "$(dirname "$latest_base")" && sha256sum --check "$(basename "$base_sha")")
  tar -xzf "$latest_base" -C "$tmp_dir"
  [[ -f "$tmp_dir/backup_manifest" ]] || { printf 'base backup 缺少 backup_manifest。\n' >&2; exit 1; }
  docker run --rm --user postgres -v "$tmp_dir:/var/lib/postgresql/data:ro" postgres:18-alpine \
    pg_verifybackup /var/lib/postgresql/data
  printf '物理 base backup 校验通过: %s\n' "$latest_base"
else
  printf '警告：尚未生成物理 base backup，PITR 尚未具备恢复基线。\n' >&2
fi

"${DEPLOY_DIR}/wal-archive.sh" verify
printf '备份门禁通过。\n'
