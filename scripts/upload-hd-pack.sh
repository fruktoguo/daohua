#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: upload-hd-pack.sh
# 主要职责: 上传 Android HD 画质资源包至主服 /opt/daohua/static/hd
#
# 机制:
#   Caddy 将 /hd/* 映射到 /opt/daohua/static/hd（符号链接）。
#   本脚本将 artifacts/hd-pack 流式打包上传至远端 staging，
#   落盘为 hd.releases/<时间戳>/ 版本化目录后原子切换符号链接，
#   保证发布过程中 /hd/* 始终指向完整版本，失败不破坏线上服务。
#
# 用法:
#   scripts/upload-hd-pack.sh [packDir]     # 默认 artifacts/hd-pack
# 环境变量:
#   SSH_HOST  主服 SSH 别名（默认 yuohira）
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REMOTE_HOST="${SSH_HOST:-yuohira}"
PACK_DIR="$(cd "${1:-$ROOT_DIR/artifacts/hd-pack}" 2>/dev/null && pwd || true)"
MANIFEST=""

if [[ -n "$PACK_DIR" && -f "$PACK_DIR/asset-manifest.json" ]]; then
  MANIFEST="$PACK_DIR/asset-manifest.json"
fi

if [[ -z "$MANIFEST" ]]; then
  printf '错误: 未找到 HD 资源包清单 %s/asset-manifest.json\n' "${PACK_DIR:-<未提供>}" >&2
  printf '请先执行 scripts/build-apk.sh（HD 模式）或 scripts/gen-hd-pack.mjs 生成画质包。\n' >&2
  exit 1
fi

command -v ssh >/dev/null 2>&1 || { printf '错误: 缺少 ssh 命令。\n' >&2; exit 1; }
command -v tar >/dev/null 2>&1 || { printf '错误: 缺少 tar 命令。\n' >&2; exit 1; }

printf '==> 上传 HD 画质资源包 → %s:/opt/daohua/static/hd\n' "$REMOTE_HOST"
printf '  - 本地包: %s\n' "$PACK_DIR"

# 1. 远端 staging 解包（用户态目录，不直接动线上链接）
ssh -o BatchMode=yes "$REMOTE_HOST" \
  'rm -rf /tmp/daohua-hd-pack.new && mkdir -p /tmp/daohua-hd-pack.new'
tar -czf - -C "$PACK_DIR" . | \
  ssh -o BatchMode=yes "$REMOTE_HOST" 'tar -xzf - -C /tmp/daohua-hd-pack.new'

# 2. 校验清单完整性后原子切换符号链接
RELEASE_NAME="hd-$(date -u +%Y%m%d%H%M%S)"
ssh -o BatchMode=yes "$REMOTE_HOST" "
  set -Eeuo pipefail
  test -f /tmp/daohua-hd-pack.new/asset-manifest.json || { printf 'staging 缺少 asset-manifest.json，放弃切换\n' >&2; exit 1; }
  sudo install -d -m 0755 /opt/daohua/static/hd.releases
  sudo rm -rf '/opt/daohua/static/hd.releases/$RELEASE_NAME'
  sudo mv /tmp/daohua-hd-pack.new '/opt/daohua/static/hd.releases/$RELEASE_NAME'
  sudo rm -rf /opt/daohua/static/hd
  sudo ln -s 'hd.releases/$RELEASE_NAME' /opt/daohua/static/hd
  # 仅保留最近 3 个历史版本
  cd /opt/daohua/static/hd.releases
  ls -1t | tail -n +4 | while read -r old; do sudo rm -rf \"\$old\"; done
  printf 'HD 包已上线: %s\n' '$RELEASE_NAME'
"

printf '✔ HD 画质资源包上传完成（/hd/* 即刻生效，无需重启 Caddy）。\n'
