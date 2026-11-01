#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: taptap-publish.sh
# 主要职责: 道化太初 TapTap 开发者中心包体上传、物料盘点与版本提审自动化管理脚本
#
# 核心功能:
#   1. status: 检查 taptap-cli 工具链状态、凭证有效性与开发者/应用上下文
#   2. login: 启动 TapTap 设备码授权并展示登录验证链接
#   3. inspect: 盘点待发布物料或安装包 (默认 artifacts/daohua.apk)
#   4. upload: 上传 Android 安装包至 TapTap 开发者后台 (支持 --dry-run / --yes)
#   5. review-check: 检查提审条件、资料缺口与合规风险
#
# 环境变量:
#   TAPTAP_DEV_ID   默认开发者 ID (可通过 --dev-id 覆盖)
#   TAPTAP_APP_ID   默认应用 ID (可通过 --app-id 覆盖)
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

COMMAND="${1:-status}"
if [[ "$COMMAND" != -* ]] && [[ $# -gt 0 ]]; then
  shift
fi

DEV_ID="${TAPTAP_DEV_ID:-}"
APP_ID="${TAPTAP_APP_ID:-}"
APK_PATH="artifacts/daohua.apk"
DRY_RUN=0
CONFIRM_YES=0
IDEMPOTENCY_KEY=""

while (($# > 0)); do
  case "$1" in
    --dev-id)
      DEV_ID="${2:?--dev-id 缺少值}"
      shift 2
      ;;
    --app-id)
      APP_ID="${2:?--app-id 缺少值}"
      shift 2
      ;;
    --apk)
      APK_PATH="${2:?--apk 缺少值}"
      shift 2
      ;;
    --dry-run)
      DRY_RUN=1
      shift
      ;;
    --yes)
      CONFIRM_YES=1
      shift
      ;;
    --idempotency-key)
      IDEMPOTENCY_KEY="${2:?--idempotency-key 缺少值}"
      shift 2
      ;;
    -h|--help)
      printf '用法: scripts/taptap-publish.sh [status|login|inspect|upload|review-check] [选项]\n'
      printf '\n子命令:\n'
      printf '  status         检查 taptap-cli 环境、登录态与当前上下文 (默认)\n'
      printf '  login          以设备码交互模式发起 TapTap 账号授权\n'
      printf '  inspect        盘点安装包与物料完整性\n'
      printf '  upload         上传 APK 到 TapTap 开发者中心包体库\n'
      printf '  review-check   执行应用提审前资料缺口与合规检查\n'
      printf '\n选项:\n'
      printf '  --dev-id <id>            指定开发者主体 ID (也可通过 TAPTAP_DEV_ID 设置)\n'
      printf '  --app-id <id>            指定游戏应用 ID (也可通过 TAPTAP_APP_ID 设置)\n'
      printf '  --apk <path>             指定待上传或检查的 APK 文件 (默认: artifacts/daohua.apk)\n'
      printf '  --dry-run                试跑预览，不执行任何远端写入\n'
      printf '  --yes                    显式确认执行写入 (upload 操作必须)\n'
      printf '  --idempotency-key <key>  指定请求幂等键 (防重复提交)\n'
      printf '  -h, --help               显示帮助信息\n'
      exit 0
      ;;
    *)
      printf '未知参数: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

ensure_cli() {
  if ! command -v taptap-cli >/dev/null 2>&1; then
    printf '错误: 未找到 taptap-cli 命令。\n' >&2
    printf '请先执行安装: npm i -g @taptap/cli --registry=https://registry.npmjs.org/\n' >&2
    exit 1
  fi
}

cmd_status() {
  ensure_cli
  printf '\n======================================================\n'
  printf '        道化太初 · TapTap CLI 工具链与上下文检查\n'
  printf '======================================================\n'
  
  printf '• CLI 工具版本: '
  taptap-cli version --format json 2>/dev/null | grep '"version"' || taptap-cli version || true
  
  printf '\n• 认证状态检查:\n'
  taptap-cli auth status || true
  
  printf '\n• 上下文参数配置:\n'
  if [[ -n "$DEV_ID" ]]; then
    printf '  - 开发者 ID (DEV_ID): %s\n' "$DEV_ID"
  else
    printf '  - 开发者 ID (DEV_ID): 未设置 (建议 export TAPTAP_DEV_ID=<id> 或传 --dev-id)\n'
  fi
  if [[ -n "$APP_ID" ]]; then
    printf '  - 游戏应用 ID (APP_ID): %s\n' "$APP_ID"
  else
    printf '  - 游戏应用 ID (APP_ID): 未设置 (建议 export TAPTAP_APP_ID=<id> 或传 --app-id)\n'
  fi

  if [[ -f "$APK_PATH" ]]; then
    local apk_size
    apk_size="$(ls -lh "$APK_PATH" | awk '{print $5}')"
    printf '\n• 本地安装包产物: 已就绪 (%s, %s)\n' "$APK_PATH" "$apk_size"
  else
    printf '\n• 本地安装包产物: 未就绪 (%s 不存在，发版前请先运行 scripts/build-apk.sh)\n' "$APK_PATH"
  fi
  printf '======================================================\n\n'
}

cmd_login() {
  ensure_cli
  printf '正在发起 TapTap 登录授权请求...\n'
  taptap-cli auth login
}

cmd_inspect() {
  ensure_cli
  printf '正在盘点物料产物: %s\n' "$APK_PATH"
  if [[ ! -e "$APK_PATH" ]]; then
    printf '错误: 物料路径不存在: %s\n' "$APK_PATH" >&2
    printf '若需构建 Android APK，请先执行: scripts/build-apk.sh\n' >&2
    exit 1
  fi

  # 将相对路径规范化为工作区相对路径
  local target_rel
  target_rel="$(realpath --relative-to="$ROOT_DIR" "$APK_PATH" 2>/dev/null || echo "$APK_PATH")"
  taptap-cli materials +inspect "$target_rel" --format pretty
}

cmd_upload() {
  ensure_cli
  if [[ ! -f "$APK_PATH" ]]; then
    printf '错误: 未找到待上传的 APK 文件: %s\n' "$APK_PATH" >&2
    printf '请先执行构建: scripts/build-apk.sh\n' >&2
    exit 1
  fi

  if [[ -z "$DEV_ID" ]]; then
    printf '错误: 缺少 --dev-id 参数 (或环境变量 TAPTAP_DEV_ID 未设置)\n' >&2
    exit 1
  fi

  if [[ -z "$APP_ID" ]]; then
    printf '错误: 缺少 --app-id 参数 (或环境变量 TAPTAP_APP_ID 未设置)\n' >&2
    exit 1
  fi

  local target_rel
  target_rel="$(realpath --relative-to="$ROOT_DIR" "$APK_PATH" 2>/dev/null || echo "$APK_PATH")"

  local upload_cmd=(taptap-cli upload-apk "$target_rel" --dev-id "$DEV_ID" --app-id "$APP_ID")
  
  if [[ -n "$IDEMPOTENCY_KEY" ]]; then
    upload_cmd+=(--idempotency-key "$IDEMPOTENCY_KEY")
  fi

  if [[ "$DRY_RUN" == "1" ]]; then
    printf '【预览模式 (Dry Run)】调用预检:\n'
    "${upload_cmd[@]}" --dry-run
    return 0
  fi

  if [[ "$CONFIRM_YES" != "1" ]]; then
    printf '警告: 上传包体属于写操作。如需真正提交，请附加 --yes 参数。\n'
    printf '当前调用命令为: %s --dry-run\n' "${upload_cmd[*]}"
    "${upload_cmd[@]}" --dry-run
    return 0
  fi

  printf '正在正式上传 APK 包体 (%s) 至 TapTap (dev-id: %s, app-id: %s)...\n' "$target_rel" "$DEV_ID" "$APP_ID"
  "${upload_cmd[@]}" --yes
}

cmd_review_check() {
  ensure_cli
  if [[ -z "$DEV_ID" ]]; then
    printf '错误: 缺少 --dev-id 参数 (或环境变量 TAPTAP_DEV_ID 未设置)\n' >&2
    exit 1
  fi

  if [[ -z "$APP_ID" ]]; then
    printf '错误: 缺少 --app-id 参数 (或环境变量 TAPTAP_APP_ID 未设置)\n' >&2
    exit 1
  fi

  printf '正在检查 TapTap 应用提审状态 (dev-id: %s, app-id: %s)...\n' "$DEV_ID" "$APP_ID"
  taptap-cli app analyze-app-status --dev-id "$DEV_ID" --app-id "$APP_ID" --format pretty
}

case "$COMMAND" in
  status)
    cmd_status
    ;;
  login)
    cmd_login
    ;;
  inspect)
    cmd_inspect
    ;;
  upload)
    cmd_upload
    ;;
  review-check)
    cmd_review_check
    ;;
  *)
    printf '未知子命令: %s (可用子命令: status, login, inspect, upload, review-check)\n' "$COMMAND" >&2
    exit 2
    ;;
esac
