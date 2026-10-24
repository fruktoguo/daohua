#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: build-apk.sh
# 主要职责: 本地全自动编译并输出道化太初 Android 高清混合离线安装包 (daohua.apk)
#
# 核心流程:
#   1. 环境探测 (自动适配 Windows / WSL 宿主互操作与原生环境)；
#   2. 高清资产安全注入 (立绘 1024px, 建筑 128px, 道具 128px)；
#   3. 前端编译，并在编译后 100% 还原源码工作区 (0 污染 Git)；
#   4. 资源同步到 Android assets/www 并执行 assembleRelease；
#   5. 输出产物至 artifacts/daohua.apk (严格隔离，不入 Git)。
#
# 选项:
#   --no-hd            不使用高清母模 (使用 Web 默认 48px/64px/128px 资产)
#   --skip-web-build   跳过前端构建 (直接复用现有 dist)
#   --soft-fail        环境缺失或构建失败时仅警告并返回 0 (供发布流水线安全降级)
#   -h, --help         显示帮助
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SKIP_WEB_BUILD=0
SOFT_FAIL=0
HD_MODE=1
REQUIRE_FULL=0
UPLOAD_RELEASE=0
RELEASE_TAG=""

while (($# > 0)); do
  case "$1" in
    --no-hd) HD_MODE=0; shift ;;
    --skip-web-build) SKIP_WEB_BUILD=1; shift ;;
    --soft-fail) SOFT_FAIL=1; shift ;;
    --require-full|--complete) REQUIRE_FULL=1; shift ;;
    --release) UPLOAD_RELEASE=1; shift ;;
    --tag) RELEASE_TAG="${2:?--tag 缺少值}"; shift 2 ;;
    -h|--help)
      printf '用法: scripts/build-apk.sh [--no-hd] [--skip-web-build] [--soft-fail] [--require-full] [--release] [--tag TAG]\n'
      exit 0
      ;;
    *)
      printf '未知参数: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

cd "$ROOT_DIR"

cleanup() {
  # 确保退出时前端源码必须恢复如初
  if [[ -f "$ROOT_DIR/scripts/inject-hd-assets.mjs" ]]; then
    node "$ROOT_DIR/scripts/inject-hd-assets.mjs" restore >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT INT TERM

printf '\n======================================================\n'
printf '        道化太初 · Android 安装包构建流水线\n'
if [[ -f "$ROOT_DIR/scripts/sync-private-modules.mjs" ]]; then
  node "$ROOT_DIR/scripts/sync-private-modules.mjs" >/dev/null 2>&1 || true
fi

HAS_PRIVATE=0
if [[ -f "$ROOT_DIR/frontend/src/private-impl/index.ts" ]]; then
  HAS_PRIVATE=1
  printf '        [口径: 完整客户端 (含反多开子系统与专有防护)]\n'
else
  if [[ "$REQUIRE_FULL" == "1" ]]; then
    printf '\n错误: 指定了 --require-full (必须完全版本)，但未检测到私有扩展模块 (缺少 frontend/src/private-impl/index.ts)。\n' >&2
    printf '正式服客户端必须包含反多开声明，拒绝构建开源 Stub 版！\n' >&2
    exit 1
  fi
  printf '        [口径: 开源客户端 (Stub，仅供本地测试)]\n'
fi

if [[ "$HD_MODE" == "1" ]]; then
  printf '        [画质: 全量超清 Retina (立绘 1024px | 建筑 128px | 道具 128px)]\n'
else
  printf '        [画质: Web 默认轻量版]\n'
fi
printf '======================================================\n\n'

SHELL_DIR=""
if [[ -d "$ROOT_DIR/tools/android-shell" ]]; then
  SHELL_DIR="$ROOT_DIR/tools/android-shell"
elif [[ -d "$ROOT_DIR/artifacts/android-shell" ]]; then
  SHELL_DIR="$ROOT_DIR/artifacts/android-shell"
else
  if [[ "$SOFT_FAIL" == "1" ]]; then
    printf '警告: 未找到 android-shell 工程目录，跳过 APK 构建。\n'
    exit 0
  fi
  printf '错误: 未找到 android-shell 工程目录！\n' >&2
  exit 1
fi

# 1. 前端编译与资源同步
if [[ "$SKIP_WEB_BUILD" != "1" || ! -d "$ROOT_DIR/frontend/dist" ]]; then
  if [[ "$HD_MODE" == "1" ]]; then
    printf '==> [1/4] 注入高清建筑与道具母模...\n'
    node "$ROOT_DIR/scripts/inject-hd-assets.mjs" apply
  fi

  printf '==> [2/4] 编译前端 Web 产物...\n'
  if command -v cmd.exe >/dev/null 2>&1; then
    cmd.exe /c "npm --prefix frontend run build"
  else
    npm --prefix "$ROOT_DIR/frontend" run build
  fi

  if [[ "$HD_MODE" == "1" ]]; then
    printf '==> [3/4] 恢复前端工作区 (确保 Git 零改动)...\n'
    node "$ROOT_DIR/scripts/inject-hd-assets.mjs" restore
  fi
fi

printf '==> [4/4] 同步前端离线资源到 Android assets/www...\n'
mkdir -p "$SHELL_DIR/app/src/main/assets/www"
rm -rf "$SHELL_DIR/app/src/main/assets/www"/*
cp -r "$ROOT_DIR/frontend/dist"/* "$SHELL_DIR/app/src/main/assets/www/"

if [[ "$HD_MODE" == "1" ]]; then
  # 覆盖 1024px 超清立绘母模
  node "$ROOT_DIR/scripts/inject-hd-assets.mjs" overlay-dist "$SHELL_DIR/app/src/main/assets/www"
fi

# 准备版本指纹并注入 Android 构建工程
GIT_SHA="$(git rev-parse --short=8 HEAD 2>/dev/null || echo "dev")"
PKG_VERSION="$(node -p "try { require('./frontend/package.json').version } catch(e) { '0.1.0' }" 2>/dev/null || echo "0.1.0")"
if [[ -z "$RELEASE_TAG" ]]; then
  RELEASE_TAG="v${PKG_VERSION}-sha-${GIT_SHA}"
fi
COMMIT_COUNT="$(git rev-list --count HEAD 2>/dev/null || echo "100")"
APP_VERSION_CODE="$(( 10000 + COMMIT_COUNT ))"
BUILD_TIME="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

VERSION_JSON="{\"versionName\":\"${PKG_VERSION}\",\"versionCode\":${APP_VERSION_CODE},\"gitSha\":\"${GIT_SHA}\",\"releaseTag\":\"${RELEASE_TAG}\",\"buildTime\":\"${BUILD_TIME}\"}"
mkdir -p "$SHELL_DIR/app/src/main/assets" "$SHELL_DIR/app/src/main/assets/www"
printf '%s\n' "$VERSION_JSON" > "$SHELL_DIR/app/src/main/assets/app-version.json"
printf '%s\n' "$VERSION_JSON" > "$SHELL_DIR/app/src/main/assets/www/app-version.json"

GRADLE_PROPS="-PappVersionName=${PKG_VERSION} -PappVersionCode=${APP_VERSION_CODE}"

# 2. 执行 Android 打包
printf '\n==> 执行 Android assembleRelease 打包...\n'

BUILD_SUCCESS=0
WIN_GRADLE="C:\\Users\\YuoHira\\.gradle\\wrapper\\dists\\gradle-8.13-bin\\5xuhj0ry160q40clulazy9h7d\\gradle-8.13\\bin\\gradle.bat"

if command -v cmd.exe >/dev/null 2>&1; then
  # WSL / Windows 互操作模式
  WIN_SHELL_PATH="tools\\android-shell"
  if [[ "$SHELL_DIR" =~ artifacts/android-shell ]]; then
    WIN_SHELL_PATH="artifacts\\android-shell"
  fi
  cmd.exe /c "${WIN_GRADLE} -p ${WIN_SHELL_PATH} assembleRelease ${GRADLE_PROPS}" && BUILD_SUCCESS=1 || BUILD_SUCCESS=0
elif command -v gradle >/dev/null 2>&1; then
  gradle -p "$SHELL_DIR" assembleRelease ${GRADLE_PROPS} && BUILD_SUCCESS=1 || BUILD_SUCCESS=0
elif [[ -f "/c/Users/YuoHira/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle.bat" ]]; then
  "/c/Users/YuoHira/.gradle/wrapper/dists/gradle-8.13-bin/5xuhj0ry160q40clulazy9h7d/gradle-8.13/bin/gradle.bat" -p "$SHELL_DIR" assembleRelease ${GRADLE_PROPS} && BUILD_SUCCESS=1 || BUILD_SUCCESS=0
fi

if [[ "$BUILD_SUCCESS" != "1" ]]; then
  if [[ "$SOFT_FAIL" == "1" ]]; then
    printf '警告: Android APK 构建未成功 (可能是环境缺失)，跳过本阶段。\n'
    exit 0
  fi
  printf '错误: Android APK 构建失败！\n' >&2
  exit 1
fi

# 3. 提取产物至 artifacts/daohua.apk
mkdir -p "$ROOT_DIR/artifacts"
RELEASE_APK="$SHELL_DIR/app/build/outputs/apk/release/app-release.apk"
if [[ -f "$RELEASE_APK" ]]; then
  cp "$RELEASE_APK" "$ROOT_DIR/artifacts/daohua.apk"
  APK_SIZE="$(ls -lh "$ROOT_DIR/artifacts/daohua.apk" | awk '{print $5}')"
  printf '\n======================================================\n'
  printf '  ✔ [Android APK] 超清离线安装包构建成功！\n'
  printf '  • 产物路径: %s\n' "$ROOT_DIR/artifacts/daohua.apk"
  printf '  • 安装包大小: %s\n' "$APK_SIZE"
  if [[ "$HD_MODE" == "1" ]]; then
    printf '  • 视觉规格: 立绘 1024px | 建筑 128px | 道具 128px 超清覆盖\n'
  fi
  printf '  • 版本指纹: %s (versionCode: %s)\n' "$RELEASE_TAG" "$APP_VERSION_CODE"
  printf '  • 自动更新: 已内嵌 (每小时自动检测 GitHub Releases: fruktoguo/daohua)\n'
  printf '======================================================\n\n'
  if [[ "$UPLOAD_RELEASE" == "1" ]]; then
    UPLOAD_ARGS=("--file" "$ROOT_DIR/artifacts/daohua.apk")
    if [[ -n "$RELEASE_TAG" ]]; then
      UPLOAD_ARGS+=("--tag" "$RELEASE_TAG")
    fi
    bash "$ROOT_DIR/scripts/upload-release-apk.sh" "${UPLOAD_ARGS[@]}"
  fi
else
  if [[ "$SOFT_FAIL" == "1" ]]; then
    printf '警告: 未找到生成的 app-release.apk\n'
    exit 0
  fi
  printf '错误: 未找到生成的 app-release.apk\n' >&2
  exit 1
fi
