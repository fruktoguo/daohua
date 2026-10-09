#!/usr/bin/env bash
# 生产 WAL 归档巡检与轮转：不触碰 PostgreSQL 数据卷，只校验并清理已过期的 WAL 段。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"
WAL_RETENTION_DAYS="${WAL_RETENTION_DAYS:-8}"

usage() {
  printf '%s\n' \
    '用法：wal-archive.sh verify|prune|status' \
    '  verify  检查文件名、权限、临时文件和最近 WAL 段' \
    '  prune   删除超过 WAL_RETENTION_DAYS 的 WAL 段（默认 8 天）' \
    '  status  输出归档目录容量与文件数量'
}

[[ "$WAL_RETENTION_DAYS" =~ ^[1-9][0-9]*$ ]] || {
  printf 'WAL_RETENTION_DAYS 必须是正整数。\n' >&2
  exit 2
}

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a
  source "$ENV_FILE"
  set +a
  WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"
  WAL_RETENTION_DAYS="${WAL_RETENTION_DAYS:-8}"
fi

action="${1:-status}"
[[ -d "$WAL_ARCHIVE_DIR" ]] || {
  printf 'WAL 归档目录不存在: %s\n' "$WAL_ARCHIVE_DIR" >&2
  exit 1
}

wal_files=()
while IFS= read -r -d '' file; do
  if [[ "$(basename "$file")" =~ ^([0-9A-Fa-f]{24}|[0-9A-Fa-f]{24}\.[0-9A-Fa-f]{8}\.backup|[0-9A-Fa-f]{8}\.history)\.gz$ ]]; then
    wal_files+=("$file")
  fi
done < <(find "$WAL_ARCHIVE_DIR" -maxdepth 1 -type f -print0 | sort -z)
case "$action" in
  verify)
    shopt -s nullglob
    temporary_files=("$WAL_ARCHIVE_DIR"/.*.tmp.* "$WAL_ARCHIVE_DIR"/.*.verify.*)
    if ((${#temporary_files[@]} > 0)); then
      printf '发现未清理的 WAL 临时文件: %s\n' "${temporary_files[*]}" >&2
      exit 1
    fi
    while IFS= read -r -d '' file; do
      [[ "$(basename "$file")" =~ ^([0-9A-Fa-f]{24}|[0-9A-Fa-f]{24}\.[0-9A-Fa-f]{8}\.backup|[0-9A-Fa-f]{8}\.history)\.gz$ ]] || {
        printf '发现不支持的 WAL 归档对象，拒绝忽略: %s\n' "$file" >&2
        exit 1
      }
    done < <(find "$WAL_ARCHIVE_DIR" -maxdepth 1 -type f -print0)
    for file in "${wal_files[@]}"; do
      [[ "$(stat -c '%a' "$file" 2>/dev/null || stat -f '%Lp' "$file")" == "600" ]] || {
        printf 'WAL 文件权限必须为 0600: %s\n' "$file" >&2
        exit 1
      }
      gzip -t "$file" || { printf 'WAL 压缩归档损坏: %s\n' "$file" >&2; exit 1; }
    done
    printf 'WAL 归档校验通过: files=%s dir=%s\n' "${#wal_files[@]}" "$WAL_ARCHIVE_DIR"
    ;;
  prune)
    while IFS= read -r -d '' file; do
      if [[ "$(basename "$file")" =~ ^([0-9A-Fa-f]{24}|[0-9A-Fa-f]{24}\.[0-9A-Fa-f]{8}\.backup|[0-9A-Fa-f]{8}\.history)\.gz$ ]]; then
        if [[ "$(find "$file" -prune -mtime "+$((WAL_RETENTION_DAYS - 1))" -print)" == "$file" ]]; then
          printf '%s\n' "$file"
          rm -f -- "$file"
        fi
      fi
    done < <(find "$WAL_ARCHIVE_DIR" -maxdepth 1 -type f -print0)
    printf 'WAL 归档轮转完成: retention_days=%s\n' "$WAL_RETENTION_DAYS"
    ;;
  status)
    du -sh "$WAL_ARCHIVE_DIR"
    printf 'WAL 压缩归档文件数: %s\n' "${#wal_files[@]}"
    if ((${#wal_files[@]} > 0)); then
      printf '最早: %s\n' "${wal_files[0]}"
      printf '最晚: %s\n' "${wal_files[${#wal_files[@]} - 1]}"
    fi
    ;;
  -h|--help)
    usage
    ;;
  *)
    usage >&2
    exit 2
    ;;
esac
