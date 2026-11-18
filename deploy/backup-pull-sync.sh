#!/usr/bin/env bash
# 经 SSH/rsync 拉取本轮源清单；校验完备、确认新鲜度后才发布到本地归档池。
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/backup-checks.sh"
ENV_FILE="${ENV_FILE:-/etc/daohua-backup-sync.env}"
if [[ -f "$ENV_FILE" ]]; then
  set -a; source "$ENV_FILE"; set +a
fi

REMOTE_HOST="${REMOTE_HOST:-yuohira}"
REMOTE_DIR="${REMOTE_DIR:-/opt/daohua/backups/}"
LOCAL_DIR="${LOCAL_DIR:-/data/daohua-backups}"
LOCAL_HOURLY_KEEP_DAYS="${LOCAL_HOURLY_KEEP_DAYS:-90}"
SSH_OPTS="${SSH_OPTS:--o BatchMode=yes -o ConnectTimeout=15}"

while (($# > 0)); do
  case "$1" in
    --local-dir|-l) LOCAL_DIR="${2:?缺少本地目录}"; shift 2 ;;
    --remote-host|-r) REMOTE_HOST="${2:?缺少远程主机}"; shift 2 ;;
    --remote-dir) REMOTE_DIR="${2:?缺少远程目录}"; shift 2 ;;
    *) printf '未知选项: %s\n' "$1" >&2; exit 2 ;;
  esac
done
[[ "$REMOTE_HOST" =~ ^[A-Za-z0-9_][A-Za-z0-9._-]*$ && "$REMOTE_DIR" =~ ^/[A-Za-z0-9._/-]+$ ]] || {
  printf '远端主机或备份目录不合法。\n' >&2; exit 2;
}
[[ "$LOCAL_HOURLY_KEEP_DAYS" =~ ^(0|[1-9][0-9]*)$ ]] || { printf 'LOCAL_HOURLY_KEEP_DAYS 必须为非负整数。\n' >&2; exit 2; }

umask 077
mkdir -p -- "$LOCAL_DIR"
LOCAL_DIR="$(cd "$LOCAL_DIR" && pwd -P)"
exec 8>"${LOCK_FILE:-$LOCAL_DIR/.backup-sync.lock}"
flock -n -E 75 8 || { rc=$?; printf '未获得备份拉取锁，本轮未验证。\n' >&2; exit "$rc"; }
stage="$(mktemp -d "$LOCAL_DIR/.sync.XXXXXX")"
trap 'rm -rf -- "$stage"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
export LC_ALL=C
shopt -s nullglob

categories=(hourly daily weekly base)
filters=()
pairs=()
for category in "${categories[@]}"; do
  ext=dump
  if [[ "$category" == base ]]; then ext=tar.gz; fi
  dest="$LOCAL_DIR/$category"
  [[ ! -L "$dest" ]] || { printf '拒绝写入符号链接归档目录: %s\n' "$category" >&2; exit 1; }
  filters+=("--include=/$category/" "--include=/$category/daohua-postgres-$category-*Z.$ext" "--include=/$category/daohua-postgres-$category-*Z.$ext.sha256")
done
# 不在现有归档上 --inplace 写入。不变的文件可通过 link-dest 复用；--checksum 避免仅凭大小/mtime 复用坏文件。
rsync -rt --checksum --link-dest="$LOCAL_DIR" "${filters[@]}" --exclude='*' \
  -e "ssh $SSH_OPTS" -- "${REMOTE_HOST}:${REMOTE_DIR%/}/" "$stage/" || {
  rc=$?
  printf 'rsync 拉取失败（退出码 %d），本轮未验证。\n' "$rc" >&2
  exit "$rc"
}

verified=0
for category in "${categories[@]}"; do
  dir="$stage/$category"
  [[ -d "$dir" ]] || { printf '本轮缺少源目录: %s\n' "$category" >&2; exit 1; }
  ext=dump
  if [[ "$category" == base ]]; then ext=tar.gz; fi
  data_pattern="^daohua-postgres-$category-[0-9]{8}T[0-9]{6}Z\\.${ext//./\\.}$"
  newest=""
  for path in "$dir"/*; do
    name="${path##*/}"
    [[ "${name%.sha256}" =~ $data_pattern ]] || continue
    data="$dir/${name%.sha256}"
    [[ -f "$data" && ! -L "$data" && -f "$data.sha256" && ! -L "$data.sha256" ]] || {
      printf '源备份缺少常规文件校验对: %s/%s\n' "$category" "${name%.sha256}" >&2; exit 1;
    }
    [[ "$name" != *.sha256 ]] || continue
    backup_check_sha "$data" || { printf '源备份 SHA-256 不符: %s/%s\n' "$category" "$name" >&2; exit 1; }
    if [[ "$name" > "$newest" ]]; then newest="$name"; fi
    pairs+=("$category/$name")
    verified=$((verified + 1))
  done
  case "$category" in
    hourly) newest_hourly="$newest"; max_age="${VERIFY_DUMP_MAX_AGE_SECONDS:-10800}" ;;
    base) max_age="${VERIFY_BASE_MAX_AGE_SECONDS:-129600}" ;;
    *) continue ;; # 尚未到日/周计划时允许空集。
  esac
  backup_is_fresh "$newest" "$max_age" || { printf '源目录未提供新鲜校验对: %s\n' "$category" >&2; exit 1; }
done

# 全部检查通过才发布；未出现在本轮源目录的历史归档不参与成功判定，也不因同步而删除。
for category in "${categories[@]}"; do mkdir -p -- "$LOCAL_DIR/$category"; done
for pair in "${pairs[@]}"; do
  for path in "$pair" "$pair.sha256"; do
    source_file="$stage/$path"
    target="$LOCAL_DIR/$path"
    if [[ ! -L "$target" && "$source_file" -ef "$target" ]]; then continue; fi
    mv -fT -- "$source_file" "$target"
  done
done

if ((LOCAL_HOURLY_KEEP_DAYS > 0)); then
  old="$(find "$LOCAL_DIR/hourly" -maxdepth 1 -type f -name 'daohua-postgres-hourly-*.dump' -mtime "+$LOCAL_HOURLY_KEEP_DAYS" -print)"
  while IFS= read -r path; do
    [[ "${path##*/}" =~ ^daohua-postgres-hourly-[0-9]{8}T[0-9]{6}Z\.dump$ ]] || continue
    if [[ "${path##*/}" != "$newest_hourly" ]]; then rm -f -- "$path" "$path.sha256"; fi
  done <<< "$old"
fi
printf '异地备份同步完成：本轮源清单校验对=%d，小时与物理基线均新鲜。\n' "$verified"
