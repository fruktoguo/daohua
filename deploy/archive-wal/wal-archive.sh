#!/usr/bin/env bash
# 生产 WAL 归档巡检与轮转：不触碰 PostgreSQL 数据卷，只校验并清理已过期的 WAL 段。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"
WAL_RETENTION_HOURS="${WAL_RETENTION_HOURS:-24}"

usage() {
  printf '%s\n' \
    '用法：wal-archive.sh verify|prune|status' \
    '  verify  检查文件名、权限、临时文件和最近 WAL 段' \
    '  prune   删除超过 WAL_RETENTION_HOURS 的 WAL 段（默认 24 小时）' \
    '  status  输出归档目录容量与文件数量'
}

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a
  source "$ENV_FILE"
  set +a
  WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"
  WAL_RETENTION_HOURS="${WAL_RETENTION_HOURS:-24}"
fi

[[ "$WAL_RETENTION_HOURS" =~ ^[1-9][0-9]*$ ]] || {
  printf 'WAL_RETENTION_HOURS 必须是正整数。\n' >&2
  exit 2
}

action="${1:-status}"
case "$action" in -h|--help) usage; exit 0;; verify|prune|status) ;; *) usage >&2; exit 2;; esac
if [[ "$#" -gt 1 && ! ( "$action" == verify && "$#" == 2 && "$2" == --historical ) ]]; then usage >&2; exit 2; fi
if [[ ! -d "$WAL_ARCHIVE_DIR" ]]; then
  printf 'WAL 归档目录不存在: %s\n' "$WAL_ARCHIVE_DIR" >&2
  exit 1
fi

# glob 保留每个路径为一个参数（含空白），不吞 find/管道错误。
[[ -r "$WAL_ARCHIVE_DIR" && -x "$WAL_ARCHIVE_DIR" ]] || exit 1
shopt -s nullglob dotglob
wal_files=()
latest_segment=0
now="$(date -u +%s)"
for file in "$WAL_ARCHIVE_DIR"/*; do
  name="${file##*/}"
  if [[ ! -f "$file" || -L "$file" || ! "$name" =~ ^([0-9A-Fa-f]{24}(\.[0-9A-Fa-f]{8}\.backup)?|[0-9A-Fa-f]{8}\.history)\.gz$ ]]; then
    if [[ "$action" == verify ]]; then
      printf '发现非 WAL 文件、临时文件或链接: %s\n' "$file" >&2
      exit 1
    fi
    continue
  fi
  wal_files+=("$file")
  if [[ "$action" == verify ]]; then
    [[ "$(stat -c '%a' -- "$file")" == 600 ]] || { printf 'WAL 文件权限必须为 0600: %s\n' "$file" >&2; exit 1; }
    if [[ "$name" =~ ^[0-9A-Fa-f]{24}\.gz$ ]]; then
      epoch="$(stat -c '%Y' -- "$file")"
      if ((epoch > latest_segment)); then latest_segment="$epoch"; fi
    fi
  fi
done
case "$action" in
  verify)
    max_age="${VERIFY_WAL_MAX_AGE_SECONDS:-86400}"
    if ((latest_segment == 0)); then
      printf '没有已归档的 WAL 段。\n' >&2; exit 1
    fi
    if [[ "${2:-}" != --historical ]]; then
      [[ "$max_age" =~ ^[1-9][0-9]{0,8}$ ]] && ((now - latest_segment >= -300 && now - latest_segment <= max_age)) || {
        printf 'WAL 归档缺少新鲜段（或 VERIFY_WAL_MAX_AGE_SECONDS 非法）。\n' >&2; exit 1;
      }
    fi
    wal_check_files=("${wal_files[@]}")
    if [[ "${FULL_WAL_VERIFY:-0}" != "1" && "${#wal_check_files[@]}" -gt 100 ]]; then
      wal_check_files=("${wal_check_files[@]: -100}")
    fi
    for file in "${wal_check_files[@]}"; do
      gzip -t "$file" || { printf 'WAL 压缩归档损坏: %s\n' "$file" >&2; exit 1; }
    done
    printf 'WAL 归档文件抽检通过（不证明 WAL 连续或 PITR 目标可达）: files=%s dir=%s\n' "${#wal_files[@]}" "$WAL_ARCHIVE_DIR"
    ;;
  prune)
    while IFS= read -r -d '' file; do
      if [[ "$(basename "$file")" =~ ^([0-9A-Fa-f]{24}|[0-9A-Fa-f]{24}\.[0-9A-Fa-f]{8}\.backup|[0-9A-Fa-f]{8}\.history)\.gz$ ]]; then
        if [[ "$(find "$file" -prune -mmin "+$((WAL_RETENTION_HOURS * 60))" -print)" == "$file" ]]; then
          printf '%s\n' "$file"
          rm -f -- "$file"
        fi
      fi
    done < <(find "$WAL_ARCHIVE_DIR" -maxdepth 1 -type f -print0)
    printf 'WAL 归档轮转完成: retention_hours=%s\n' "$WAL_RETENTION_HOURS"
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
