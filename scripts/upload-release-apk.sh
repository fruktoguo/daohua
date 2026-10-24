#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: upload-release-apk.sh
# 主要职责: 将道化太初 Android 完全版客户端 (daohua.apk) 自动上传至 GitHub Releases
#
# 选项:
#   --tag TAG        发布 Tag（默认: v<package_version>-sha-<git_rev>）
#   --file FILE      APK 文件路径（默认: artifacts/daohua.apk）
#   --title TITLE    Release 标题（默认: 道化太初 <TAG>）
#   --notes NOTES    Release 说明文案
#   --repo REPO      目标 GitHub 仓库（默认: fruktoguo/daohua）
#   -h, --help       显示帮助信息
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APK_FILE="$ROOT_DIR/artifacts/daohua.apk"
TARGET_REPO="fruktoguo/daohua"
TAG=""
TITLE=""
NOTES=""

while (($# > 0)); do
  case "$1" in
    --tag) TAG="${2:?--tag 缺少值}"; shift 2 ;;
    --file) APK_FILE="${2:?--file 缺少值}"; shift 2 ;;
    --title) TITLE="${2:?--title 缺少值}"; shift 2 ;;
    --notes) NOTES="${2:?--notes 缺少值}"; shift 2 ;;
    --repo) TARGET_REPO="${2:?--repo 缺少值}"; shift 2 ;;
    -h|--help)
      printf '用法: scripts/upload-release-apk.sh [--tag TAG] [--file FILE] [--title TITLE] [--notes NOTES] [--repo REPO]\n'
      exit 0
      ;;
    *)
      printf '未知参数: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

cd "$ROOT_DIR"

if [[ ! -f "$APK_FILE" ]]; then
  printf '错误: 未找到 APK 文件: %s\n' "$APK_FILE" >&2
  exit 1
fi

if ! command -v gh >/dev/null 2>&1; then
  printf '错误: 缺少 GitHub CLI (gh) 工具，无法执行 Release 上传。\n' >&2
  exit 1
fi

# 自动探测并注入本地 Clash 代理端口（7890）以增强 GitHub API 连接韧性
if [[ -z "${HTTPS_PROXY:-}" && -z "${https_proxy:-}" ]]; then
  if (echo >/dev/tcp/127.0.0.1/7890) 2>/dev/null; then
    export HTTPS_PROXY="http://127.0.0.1:7890"
    export HTTP_PROXY="http://127.0.0.1:7890"
  fi
fi

# 自动推导 Tag 与 Title
if [[ -z "$TAG" ]]; then
  PKG_VERSION="$(node -p "try{require('./frontend/package.json').version}catch(e){'0.1.0'}" 2>/dev/null || printf '0.1.0')"
  GIT_REV="$(git rev-parse --short HEAD 2>/dev/null || printf 'latest')"
  TAG="v${PKG_VERSION}-sha-${GIT_REV}"
fi

if [[ -z "$TITLE" ]]; then
  TITLE="道化太初 ${TAG}"
fi

if [[ -z "$NOTES" ]]; then
  NOTES="道化太初官方正式服配套 Android 客户端（含完整防多开与安全防御体系）。"
fi

APK_SIZE="$(ls -lh "$APK_FILE" | awk '{print $5}')"
printf '==> 准备上传 Android 安装包至 GitHub Releases...\n'
printf '  - 目标仓库: %s\n' "$TARGET_REPO"
printf '  - Release 标签: %s\n' "$TAG"
printf '  - APK 文件: %s (%s)\n' "$APK_FILE" "$APK_SIZE"

# 检查 Release 是否已存在
if gh release view "$TAG" --repo "$TARGET_REPO" >/dev/null 2>&1; then
  printf '  - Release %s 已存在，覆盖上传 APK 资产 (clobber)...\n' "$TAG"
  gh release upload "$TAG" "$APK_FILE#daohua.apk" --repo "$TARGET_REPO" --clobber
else
  printf '  - 创建新 Release %s 并上传 APK 资产...\n' "$TAG"
  gh release create "$TAG" "$APK_FILE#daohua.apk" \
    --repo "$TARGET_REPO" \
    --title "$TITLE" \
    --notes "$NOTES"
fi

RELEASE_URL="https://github.com/${TARGET_REPO}/releases/tag/${TAG}"
DOWNLOAD_URL="https://github.com/${TARGET_REPO}/releases/download/${TAG}/daohua.apk"

printf '\n✔ Android 安装包已成功发布到 GitHub Releases！\n'
printf '  - Release 详情页: %s\n' "$RELEASE_URL"
printf '  - APK 直接下载直链: %s\n\n' "$DOWNLOAD_URL"
