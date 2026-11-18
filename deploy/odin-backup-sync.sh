#!/usr/bin/env bash
# Odin 经 HTTPS 拉取备份；本轮源目录、下载、SHA-256 及新鲜度均是成功条件。
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/backup-checks.sh"
ENV_FILE="${ENV_FILE:-/etc/daohua-backup-sync.env}"
if [[ -f "$ENV_FILE" ]]; then
  set -a; source "$ENV_FILE"; set +a
fi

BACKUP_URL="${BACKUP_URL:-https://daohua.yuohira.com/internal/backups}"
BACKUP_TOKEN="${BACKUP_TOKEN:-${DAOHUA_BACKUP_TOKEN:-}}"
LOCAL_DIR="${LOCAL_DIR:-/root/daohua-backups}"
LOCAL_HOURLY_KEEP="${LOCAL_HOURLY_KEEP:-72}"
LOCAL_DAILY_KEEP="${LOCAL_DAILY_KEEP:-30}"
LOCAL_WEEKLY_KEEP="${LOCAL_WEEKLY_KEEP:-24}"
LOCAL_BASE_KEEP="${LOCAL_BASE_KEEP:-8}"

[[ "$BACKUP_URL" == https://* && -n "$BACKUP_TOKEN" && "$BACKUP_TOKEN" != *[$'\r\n']* ]] || {
  printf '必须配置 HTTPS 备份地址和合法的备份鉴权令牌。\n' >&2; exit 2;
}
for keep in "$LOCAL_HOURLY_KEEP" "$LOCAL_DAILY_KEEP" "$LOCAL_WEEKLY_KEEP" "$LOCAL_BASE_KEEP"; do
  [[ "$keep" =~ ^[1-9][0-9]{0,8}$ ]] || { printf '各分类保留份数必须为正整数。\n' >&2; exit 2; }
done

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

fetch() {
  local relative="$1" destination="$2" seconds="$3" status
  local -a bounds=()
  if [[ "$relative" == *.sha256 ]]; then bounds=(--max-filesize 1024); fi
  # 不读 curlrc、不跟随重定向、凭据不进 curl argv；只接受完整的 200 响应。
  if ! status="$(curl -q --silent --show-error --proto '=https' --connect-timeout 15 --max-time "$seconds" \
      "${bounds[@]}" --header @- --output "$destination" --write-out '%{http_code}' -- "${BACKUP_URL%/}/$relative" \
      <<<"X-Backup-Token: $BACKUP_TOKEN")" || [[ "$status" != 200 ]]; then
    printf '源备份请求失败: %s（HTTP %s）\n' "$relative" "${status:-未知}" >&2
    return 1
  fi
}

verified=0
categories=(hourly daily weekly base)
declare -A newest_by_category=()
for category in "${categories[@]}"; do
  cache="$LOCAL_DIR/$category"
  [[ ! -L "$cache" ]] || { printf '拒绝写入符号链接归档目录: %s\n' "$category" >&2; exit 1; }
  dir="$stage/$category"
  mkdir -p -- "$dir"
  fetch "$category/" "$stage/listing" 60
  extension=dump
  if [[ "$category" == base ]]; then extension='tar\.gz'; fi
  # Caddy browse 使用相对 href；严格匹配完整文件名，不把 .partial 当作备份，也不跟随 HTML 中的地址。
  links="$(grep -oE "href=\"(\./)?daohua-postgres-$category-[0-9]{8}T[0-9]{6}Z\.$extension(\.sha256)?\"" "$stage/listing")" || {
    rc=$?
    if ((rc != 1)); then exit "$rc"; fi
  }
  unset files
  declare -A files=()
  while IFS= read -r link; do
    [[ -n "$link" ]] || continue
    name="${link#href=\"}"
    name="${name%\"}"
    files["${name#./}"]=1
  done <<< "$links"

  newest=""
  for name in "${!files[@]}"; do
    data="${name%.sha256}"
    [[ ${files[$data]:-} && ${files[$data.sha256]:-} ]] || {
      printf '源目录缺少校验对: %s/%s\n' "$category" "$data" >&2; exit 1;
    }
    [[ "$name" != *.sha256 ]] || continue
    if [[ "$name" > "$newest" ]]; then newest="$name"; fi
  done
  newest_by_category["$category"]="$newest"
  case "$category" in
    hourly) max_age="${VERIFY_DUMP_MAX_AGE_SECONDS:-10800}" ;;
    base) max_age="${VERIFY_BASE_MAX_AGE_SECONDS:-129600}" ;;
    *) max_age="" ;; # 日/周计划尚未运行时允许空集。
  esac
  if [[ -n "$max_age" ]]; then
    backup_is_fresh "$newest" "$max_age" || { printf '源目录未提供新鲜校验对: %s\n' "$category" >&2; exit 1; }
  fi

  for name in "${!files[@]}"; do
    [[ "$name" != *.sha256 ]] || continue
    sidecar="$dir/$name.sha256"
    fetch "$category/$name.sha256" "$sidecar" 60
    if [[ -L "$cache/$name" ]] || ! backup_check_sha "$cache/$name" "$sidecar"; then
      fetch "$category/$name" "$dir/$name" 7200
      backup_check_sha "$dir/$name" "$sidecar" || { printf '下载的备份 SHA-256 不符: %s/%s\n' "$category" "$name" >&2; exit 1; }
    fi
    verified=$((verified + 1))
  done
done

# 全部分类通过后，才发布已核验的数据和边车，再按源文件名时间顺序执行保留策略。
keeps=("$LOCAL_HOURLY_KEEP" "$LOCAL_DAILY_KEEP" "$LOCAL_WEEKLY_KEEP" "$LOCAL_BASE_KEEP")
for i in "${!categories[@]}"; do
  category="${categories[i]}"
  dest="$LOCAL_DIR/$category"
  mkdir -p -- "$dest"
  for path in "$stage/$category"/*; do mv -fT -- "$path" "$dest/${path##*/}"; done
  ext=dump
  if [[ "$category" == base ]]; then ext='tar\.gz'; fi
  backups=()
  for path in "$dest"/*; do
    if [[ "${path##*/}" =~ ^daohua-postgres-$category-[0-9]{8}T[0-9]{6}Z\.$ext$ && -f "$path" && ! -L "$path" && -f "$path.sha256" && ! -L "$path.sha256" ]]; then
      backups+=("$path")
    fi
  done
  excess=$((${#backups[@]} - keeps[i]))
  for path in "${backups[@]}"; do
    if ((excess <= 0)); then break; fi
    if [[ "${path##*/}" == "${newest_by_category[$category]}" ]]; then continue; fi
    rm -f -- "$path" "$path.sha256"
    excess=$((excess - 1))
  done
done
printf 'Odin 异地备份同步完成：本轮源清单校验对=%d，小时与物理基线均新鲜。\n' "$verified"
