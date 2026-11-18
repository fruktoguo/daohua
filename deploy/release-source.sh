#!/usr/bin/env bash
# 只读 Git 元数据核对；构建从提交归档，不读取可变工作树的私有实现。
release_sources() {
  local root="$1" allow_dirty="${2:-0}" entry
  private_root="$root/../DaoHua-Private"
  main_revision="$(git -C "$root" rev-parse --verify HEAD)" || return 1
  entry="$(git -C "$root" ls-tree "$main_revision" -- vendor/YuoCore)" || return 1
  [[ "$entry" =~ ^160000\ commit\ ([0-9a-f]{40,64})$'\t'vendor/YuoCore$ ]] || { echo '无法读取 YuoCore gitlink。' >&2; return 1; }
  yuo_revision="${BASH_REMATCH[1]}"
  [ "$(git -C "$root/vendor/YuoCore" rev-parse --verify HEAD)" = "$yuo_revision" ] || { echo 'YuoCore 不在主仓提交固定的位置。' >&2; return 1; }
  [[ -d "$private_root" && -e "$private_root/.git" ]] || { echo '完整镜像需要独立私有 Git 仓库。' >&2; return 1; }
  private_revision="$(git -C "$private_root" rev-parse --verify HEAD)" || return 1
  release_clean "$root/vendor/YuoCore" YuoCore || return 1
  release_clean "$private_root" '私有仓库' || return 1
  if [[ "$allow_dirty" != 1 ]]; then release_clean "$root" 主仓库 || return 1; fi
}
release_clean() {
  local status
  status="$(git -C "$1" status --porcelain --untracked-files=all --ignore-submodules=none)" || return 1
  [[ -z "$status" ]] || { printf '%s 有未提交改动，拒绝打包。\n' "$2" >&2; return 1; }
}
