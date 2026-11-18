#!/usr/bin/env bash
# 备份文件时间戳由源端写入文件名；复制/重下载不能延长恢复点的新鲜度。
backup_is_fresh() {
  local file="${1##*/}" max_age="$2" stamp now epoch
  [[ "$max_age" =~ ^[1-9][0-9]{0,8}$ ]] || return 1
  [[ "$file" =~ ^daohua-postgres-(hourly|daily|weekly|base)-([0-9]{8})T([0-9]{6})Z\.(dump|tar\.gz)$ ]] || return 1
  stamp="${BASH_REMATCH[2]}${BASH_REMATCH[3]}"
  epoch="$(date -u -d "${stamp:0:4}-${stamp:4:2}-${stamp:6:2}T${stamp:8:2}:${stamp:10:2}:${stamp:12:2}Z" +%s)" || return 1
  now="$(date -u +%s)" || return 1
  (( now - epoch >= -300 && now - epoch <= max_age ))
}

# 只接受目标文件的单条摘要；不能让边车文件指示 sha256sum 去校验其他路径。
backup_check_sha() {
  local file="$1" sidecar="${2:-$1.sha256}" expected actual name entry
  [[ -s "$file" && -f "$sidecar" ]] || return 1
  entry="$(cat -- "$sidecar")" || return 1
  expected="${entry:0:64}"
  name="${file##*/}"
  [[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || return 1
  [[ "${entry:64}" == "  $name" || "${entry:64}" == " *$name" ]] || return 1
  actual="$(sha256sum < "$file")" || return 1
  [[ "${actual:0:64}" == "${expected,,}" ]]
}
