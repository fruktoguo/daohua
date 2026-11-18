#!/usr/bin/env bash
# BACKUP_READER_GROUP 由主服受控配置显式指定，必须是仅含预期服务账号的既有专用组。
# 由备份锁持有方调用。未配置组时仅备份进程可访问；不创建组、不改组成员。
backup_access_check_path() {
  local path="$1" mode
  [[ ! -L "$path" && -O "$path" ]] || { printf '拒绝处理链接或非当前用户所有的备份路径: %s\n' "$path" >&2; return 1; }
  if [[ -n "$backup_reader_group" ]]; then
    mode="$(LC_ALL=C ls -ldn -- "$path")" || return 1
    # GNU ls 的 + 表示扩展/默认 ACL；不猜测或覆盖既有授权。
    [[ "${mode:10:1}" != + ]] || { printf '备份路径存在 ACL，需人工审计后再启用读者组: %s\n' "$path" >&2; return 1; }
  fi
}

backup_access_mode() {
  local mode="$1" path="$2"
  backup_access_check_path "$path" || return 1
  if [[ -n "$backup_reader_group" ]]; then chgrp -- "$backup_reader_group" "$path" || return 1; fi
  chmod "$mode" -- "$path"
}

backup_prepare_access() {
  local root="$1" category dir entry gid ext path name pattern
  backup_reader_group="${BACKUP_READER_GROUP:-}"
  backup_dir_mode=0700 backup_file_mode=0600
  if [[ -n "$backup_reader_group" ]]; then
    [[ "$backup_reader_group" =~ ^[A-Za-z_][A-Za-z0-9_-]*$ ]] || { printf 'BACKUP_READER_GROUP 必须为专用组名。\n' >&2; return 1; }
    entry="$(getent group "$backup_reader_group")" || { printf '已配置的备份读者组不存在。\n' >&2; return 1; }
    IFS=: read -r _ _ gid _ <<< "$entry"
    [[ "$gid" =~ ^[1-9][0-9]*$ ]] || { printf '禁止使用 root 组作为备份读者组。\n' >&2; return 1; }
    backup_dir_mode=0750 backup_file_mode=0640
  fi

  mkdir -p -- "$root" || return 1
  backup_access_check_path "$root" || return 1
  # 私有迁移前快照不导出，即使同一根目录授予读者组也不可遍历。
  if [[ -e "$root/pre-migration" || -L "$root/pre-migration" ]]; then
    [[ -d "$root/pre-migration" ]] && backup_access_check_path "$root/pre-migration" && chmod 0700 -- "$root/pre-migration" || return 1
  fi
  local -a valid_paths=()
  for category in hourly daily weekly base; do
    dir="$root/$category"
    mkdir -p -- "$dir" || return 1
    backup_access_check_path "$dir" || return 1
    ext=dump
    if [[ "$category" == base ]]; then ext='tar\.gz'; fi
    pattern="^daohua-postgres-$category-[0-9]{8}T[0-9]{6}Z\.$ext(\.sha256)?$"
    for path in "$dir"/*; do
      name="${path##*/}"
      [[ "$name" =~ $pattern ]] || continue
      [[ -f "$path" ]] && backup_access_check_path "$path" || return 1
      valid_paths+=("$path")
    done
  done
  # 只规范受管理的四类归档和边车，不递归授予备份根下的其他内容。
  for path in "${valid_paths[@]}"; do backup_access_mode "$backup_file_mode" "$path" || return 1; done
  for category in hourly daily weekly base; do backup_access_mode "$backup_dir_mode" "$root/$category" || return 1; done
  backup_access_mode "$backup_dir_mode" "$root"
}

# staging 应位于备份根内且为 0700；即使数据/边车已 chmod，完整校验对在发布前也不可被组读取。
backup_publish_pair() {
  local data="$1" dest="$2" hash sidecar="$1.sha256"
  hash="$(sha256sum <"$data")" || return 1
  printf '%s  %s\n' "${hash:0:64}" "${dest##*/}" > "$sidecar" || return 1
  backup_access_mode "$backup_file_mode" "$data" && backup_access_mode "$backup_file_mode" "$sidecar" || return 1
  mv -fT -- "$data" "$dest" && mv -fT -- "$sidecar" "$dest.sha256"
}
