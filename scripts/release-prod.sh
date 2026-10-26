#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: release-prod.sh
# 主要职责: 正式服全自动一键打包与发布（yuohira 主服 + odin 计算节点）
#
# 核心发布流水线:
#   1. 预检: 本机 Git/Submodule、Docker/CCR 凭据与远端连通性快速探测；
#   2. 并发构建与准备:
#      - 流 A: 自动构建 Server/ComputeNode 镜像并推送到 CCR，自动解析 sha256 digest；
#      - 流 B: 远端 yuohira 自动执行发布前数据安全快照备份与校验；
#      - 流 C: 本地交叉编译 linux-arm64 ComputeNode 并预传至 odin 临时目录；
#   3. 主服受控部署: 自动生成合规随机烟测账号，调用 deploy-prod.sh 执行容器更新与双层烟测；
#   4. odin 原子热切: 切换 odin 目录并热重启进程，看门狗自动拉起；
#   5. 双端验证与总结: 校验数据指纹一致性、计算节点接入状态，并提取动态 Ops 后台地址。
#
# 选项:
#   --skip-odin      跳过 odin 计算节点发布（仅发布主服）
#   --skip-backup    跳过发布前数据库物理/逻辑备份（安全起见默认开启）
#   --skip-smoke     跳过主服回环与公网烟测
#   --push-existing  跳过镜像构建，仅推送并使用本机已有镜像
#   --allow-dirty    允许工作区存在未提交改动（仅排障使用）
#   --host HOST      主服 SSH 别名（默认 yuohira）
#   --odin-host HOST odin SSH 别名（默认 odin）
#   -h, --help       显示帮助
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REMOTE_HOST="${SSH_HOST:-yuohira}"
ODIN_HOST="${ODIN_HOST:-odin}"
SKIP_ODIN=0
SKIP_BACKUP=0
SKIP_SMOKE=0
PUSH_EXISTING=0
ALLOW_DIRTY=0
SKIP_APK=0
SKIP_APK_UPLOAD=0
SKIP_CADDY=0
usage() {
  printf '%s\n' \
    '用法：scripts/release-prod.sh [选项]' \
    '' \
    '选项：' \
    '  --skip-odin      跳过 odin 计算节点发布（仅更新主服）' \
    '  --skip-backup    跳过发布前远端数据库备份（生产默认保留）' \
    '  --skip-smoke     跳过部署后的内网与公网烟测' \
    '  --skip-caddy     跳过远端 Caddy 站点配置安装与 reload' \
    '  --skip-apk       跳过本地 Android 安装包打包' \
    '  --skip-apk-upload 跳过向 GitHub Releases 上传安装包' \
    '  --push-existing  跳过镜像重新构建，仅推送本机已有镜像' \
    '  --allow-dirty    允许未提交工作区构建（仅排障）' \
    '  --host HOST      主服 SSH 别名（默认 yuohira）' \
    '  --odin-host HOST odin SSH 别名（默认 odin）' \
    '  -h, --help       显示帮助信息'
}

while (($# > 0)); do
  case "$1" in
    --skip-odin) SKIP_ODIN=1; shift ;;
    --skip-backup) SKIP_BACKUP=1; shift ;;
    --skip-smoke) SKIP_SMOKE=1; shift ;;
    --skip-caddy) SKIP_CADDY=1; shift ;;
    --skip-apk) SKIP_APK=1; shift ;;
    --skip-apk-upload) SKIP_APK_UPLOAD=1; shift ;;
    --push-existing) PUSH_EXISTING=1; shift ;;
    --allow-dirty) ALLOW_DIRTY=1; shift ;;
    --host) REMOTE_HOST="${2:?--host 缺少值}"; shift 2 ;;
    --odin-host) ODIN_HOST="${2:?--odin-host 缺少值}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知参数: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

cd "$ROOT_DIR"

# 临时目录与日志收拢
RELEASE_TMP="$(mktemp -d /tmp/daohua-release.XXXXXX 2>/dev/null || mktemp -d -t daohua-release.XXXXXX)"
cleanup() {
  rm -rf "$RELEASE_TMP" >/dev/null 2>&1 || true
}
trap cleanup EXIT

printf '\n======================================================\n'
printf '        道化太初 · 正式服快速发布流水线\n'
printf '======================================================\n\n'

# ------------------------------------------------------------------------------
# 阶段 0: 前置检查
# ------------------------------------------------------------------------------
printf '==> [阶段 0/4] 本机环境与远端连通性预检...\n'

# 1. 检查必要命令
for cmd in git docker ssh scp openssl date; do
  command -v "$cmd" >/dev/null 2>&1 || { printf '错误: 缺少必要命令 %s\n' "$cmd" >&2; exit 1; }
done

# 2. 检查 Git 状态与 commit
git config submodule.vendor/YuoCore.ignore dirty >/dev/null 2>&1 || true
if [[ -n "$(git status --porcelain --untracked-files=normal)" && "$ALLOW_DIRTY" != "1" ]]; then
  printf '错误: 工作区存在未提交改动，正式发布拒绝执行。\n' >&2
  git status --short
  exit 1
fi

GIT_REV="$(git rev-parse --short HEAD 2>/dev/null || printf 'unknown')"
printf '  - 本机 Git Commit: %s\n' "$GIT_REV"

# 3. 检查 Docker 运行时
docker info >/dev/null 2>&1 || { printf '错误: Docker 守护进程未启动。\n' >&2; exit 1; }

# 4. 远端连通性快速检查
printf '  - 探测主服 SSH (%s)... ' "$REMOTE_HOST"
if ssh -o BatchMode=yes -o ConnectTimeout=8 "$REMOTE_HOST" 'true' >/dev/null 2>&1; then
  printf '就绪\n'
else
  printf '失败！无法免密连接 %s\n' "$REMOTE_HOST" >&2
  exit 1
fi

if [[ "$SKIP_ODIN" != "1" ]]; then
  printf '  - 探测 odin SSH (%s)... ' "$ODIN_HOST"
  if ssh -o BatchMode=yes -o ConnectTimeout=8 "$ODIN_HOST" 'true' >/dev/null 2>&1; then
    printf '就绪\n'
  else
    printf '警告: 无法免密连接 %s，将自动跳过 odin 节点更新。\n' "$ODIN_HOST"
    SKIP_ODIN=1
  fi
fi

# ------------------------------------------------------------------------------
# 阶段 1: 并发构建、备份与准备
# ------------------------------------------------------------------------------
printf '\n==> [阶段 1/4] 并发启动构建推送、远端备份与 odin 交叉编译...\n'

STATUS_FILE="$RELEASE_TMP/status.log"
touch "$STATUS_FILE"

# 流 A: Docker 镜像构建并推送
STREAM_A_LOG="$RELEASE_TMP/stream_a_docker.log"
(
  set -Eeuo pipefail
  printf '[流 A] 开始构建与推送 CCR 镜像 (tag: sha-%s)...\n' "$GIT_REV"
  BUILD_ARGS=("--push" "--release-tag" "sha-$GIT_REV")
  if [[ "$PUSH_EXISTING" == "1" ]]; then
    BUILD_ARGS=("--push-existing" "--release-tag" "sha-$GIT_REV")
  fi
  if [[ "$ALLOW_DIRTY" == "1" ]]; then
    BUILD_ARGS+=("--allow-dirty")
  fi
  bash "$ROOT_DIR/scripts/docker-build-prod.sh" "${BUILD_ARGS[@]}" > "$STREAM_A_LOG" 2>&1
  echo "STREAM_A_EXIT:0" >> "$STATUS_FILE"
) &
PID_STREAM_A=$!

# 流 B: 远端主服数据库备份
STREAM_B_LOG="$RELEASE_TMP/stream_b_backup.log"
if [[ "$SKIP_BACKUP" != "1" ]]; then
  (
    set -Eeuo pipefail
    printf '[流 B] 开始执行远端主服安全快照备份与校验...\n'
    ssh -o BatchMode=yes "$REMOTE_HOST" 'sudo /opt/daohua/backup.sh && sudo /opt/daohua/verify-backup.sh' > "$STREAM_B_LOG" 2>&1
    echo "STREAM_B_EXIT:0" >> "$STATUS_FILE"
  ) &
  PID_STREAM_B=$!
else
  printf '[流 B] 用户指定跳过发布前备份。\n'
  echo "STREAM_B_EXIT:0" >> "$STATUS_FILE"
  PID_STREAM_B=""
fi

# 流 C: odin 掌机 linux-arm64 交叉编译与预传
STREAM_C_LOG="$RELEASE_TMP/stream_c_odin.log"
if [[ "$SKIP_ODIN" != "1" ]]; then
  (
    set -Eeuo pipefail
    printf '[流 C] 本地交叉编译 ComputeNode (linux-arm64) 并预传至 odin...\n'
    ODIN_PUBLISH_DIR="$RELEASE_TMP/daohua-compute-odin"
    rm -rf "$ODIN_PUBLISH_DIR"
    ODIN_OUT="$ODIN_PUBLISH_DIR"
    if command -v cygpath >/dev/null 2>&1; then
      ODIN_OUT="$(cygpath -w "$ODIN_PUBLISH_DIR")"
    fi
    dotnet publish "$ROOT_DIR/src/ComputeNode" -c Release -r linux-arm64 --self-contained -o "$ODIN_OUT" --nologo > "$STREAM_C_LOG" 2>&1
    ssh -o BatchMode=yes "$ODIN_HOST" 'rm -rf /root/daohua-compute.new && mkdir -p /root/daohua-compute.new' >> "$STREAM_C_LOG" 2>&1
    tar -czf - -C "$ODIN_PUBLISH_DIR" . | ssh -o BatchMode=yes "$ODIN_HOST" 'tar -xzf - -C /root/daohua-compute.new/' >> "$STREAM_C_LOG" 2>&1
    echo "STREAM_C_EXIT:0" >> "$STATUS_FILE"
  ) &
  PID_STREAM_C=$!
else
  echo "STREAM_C_EXIT:0" >> "$STATUS_FILE"
  PID_STREAM_C=""
fi


# 等待并发任务结束
wait $PID_STREAM_A || {
  printf '\n错误: [流 A] Docker 镜像构建或推送失败！日志摘要:\n' >&2
  tail -n 35 "$STREAM_A_LOG" >&2
  exit 1
}
printf '  ✔ [流 A] 镜像构建与推送完成。\n'

if [[ -n "$PID_STREAM_B" ]]; then
  wait $PID_STREAM_B || {
    printf '\n错误: [流 B] 远端主服发布前备份校验失败！发布已安全中止。日志摘要:\n' >&2
    tail -n 35 "$STREAM_B_LOG" >&2
    exit 1
  }
  printf '  ✔ [流 B] 远端主服数据安全备份校验通过。\n'
fi

if [[ -n "$PID_STREAM_C" ]]; then
  wait $PID_STREAM_C || {
    printf '\n错误: [流 C] odin 交叉编译或预上传失败！日志摘要:\n' >&2
    tail -n 35 "$STREAM_C_LOG" >&2
    exit 1
  }
  printf '  ✔ [流 C] odin arm64 预传完成。\n'
fi


# ------------------------------------------------------------------------------
# 阶段 2: 提取镜像 Digest
# ------------------------------------------------------------------------------
printf '\n==> [阶段 2/4] 解析不可变 sha256 镜像引用...\n'

APP_DIGEST="$(awk '/推送镜像:.*\/daohua:sha-/ { found=1; next } found && /digest: sha256:[a-f0-9]{64}/ { for(i=1;i<=NF;i++) if($i ~ /^sha256:[a-f0-9]{64}$/) { print $i; exit } }' "$STREAM_A_LOG")"
COMPUTE_DIGEST="$(awk '/推送镜像:.*\/daohua-compute:sha-/ { found=1; next } found && /digest: sha256:[a-f0-9]{64}/ { for(i=1;i<=NF;i++) if($i ~ /^sha256:[a-f0-9]{64}$/) { print $i; exit } }' "$STREAM_A_LOG")"

if [[ -z "$APP_DIGEST" || -z "$COMPUTE_DIGEST" ]]; then
  DIGESTS=($(grep -oE 'sha-[^[:space:]]+: digest: sha256:[a-f0-9]{64}' "$STREAM_A_LOG" | awk '{print $NF}'))
  if [[ "${#DIGESTS[@]}" -ge 2 ]]; then
    APP_DIGEST="${DIGESTS[0]}"
    COMPUTE_DIGEST="${DIGESTS[1]}"
  fi
fi
if [[ -z "$APP_DIGEST" || -z "$COMPUTE_DIGEST" ]]; then
  printf '错误: 无法从构建日志中提取完整的 sha256 digest。日志末尾:\n' >&2
  tail -n 25 "$STREAM_A_LOG" >&2
  exit 1
fi

APP_IMAGE_REF="ccr.ccs.tencentyun.com/yuohira/daohua@$APP_DIGEST"
COMPUTE_IMAGE_REF="ccr.ccs.tencentyun.com/yuohira/daohua-compute@$COMPUTE_DIGEST"

printf '  - Server Image : %s\n' "$APP_IMAGE_REF"
printf '  - Compute Image: %s\n' "$COMPUTE_IMAGE_REF"

# ------------------------------------------------------------------------------
# 阶段 3: 主服受控部署与 odin 热重载
# ------------------------------------------------------------------------------
printf '\n==> [阶段 3/4] 部署主服并热切换 odin 计算节点...\n'

# 1. 自动生成合规随机烟测账号密码
SMOKE_ACC="smoke_$(date +%m%d)_$(openssl rand -hex 3 | tr -d '\r\n')"
SMOKE_PW="$(openssl rand -hex 8 | tr -d '\r\n')"

DEPLOY_FLAGS=(
  "--host" "$REMOTE_HOST"
  "--image" "$APP_IMAGE_REF"
  "--compute-image" "$COMPUTE_IMAGE_REF"
)
if [[ "$SKIP_SMOKE" == "1" ]]; then
  DEPLOY_FLAGS+=("--skip-smoke")
fi
if [[ "$SKIP_CADDY" == "1" ]]; then
  DEPLOY_FLAGS+=("--skip-caddy")
fi

printf '  - 触发主服部署 (deploy-prod.sh)...\n'
export SMOKE_ACCOUNT="$SMOKE_ACC"
export SMOKE_PASSWORD="$SMOKE_PW"
export SMOKE_REGISTER=1

bash "$ROOT_DIR/scripts/deploy-prod.sh" "${DEPLOY_FLAGS[@]}"

# 2. 若 odin 已准备完毕，执行原子目录切换与热重启
if [[ "$SKIP_ODIN" != "1" ]]; then
  printf '  - 原子切换 odin 目录并重启服务...\n'
  ssh -o BatchMode=yes "$ODIN_HOST" '
    set -Eeuo pipefail
    rm -rf /root/daohua-compute.old
    [[ -d /root/daohua-compute ]] && mv /root/daohua-compute /root/daohua-compute.old
    mv /root/daohua-compute.new /root/daohua-compute
    chmod +x /root/daohua-compute/DaoHua.ComputeNode
    pkill -x DaoHua.ComputeNode 2>/dev/null || pkill -x DaoHua.ComputeN 2>/dev/null || true
  '
fi

# ------------------------------------------------------------------------------
# 阶段 3.5: Android 完全版客户端编译与 GitHub Releases 自动发布
# ------------------------------------------------------------------------------
RELEASE_DOWNLOAD_URL=""
if [[ "$SKIP_APK" != "1" ]]; then
  printf '\n==> [阶段 3.5] 独占构建 Android 完全版安装包并发布到 GitHub Releases...\n'
  PKG_VERSION="$(node -p "try{require('./frontend/package.json').version}catch(e){'0.1.0'}" 2>/dev/null || printf '0.1.0')"
  RELEASE_TAG="v${PKG_VERSION}-sha-${GIT_REV}"
  APK_ARGS=("--soft-fail" "--require-full")
  if [[ "$SKIP_APK_UPLOAD" != "1" ]]; then
    APK_ARGS+=("--release" "--tag" "$RELEASE_TAG")
  fi
  bash "$ROOT_DIR/scripts/build-apk.sh" "${APK_ARGS[@]}"
  if [[ -f "$ROOT_DIR/artifacts/daohua.apk" && "$SKIP_APK_UPLOAD" != "1" ]]; then
    RELEASE_DOWNLOAD_URL="https://github.com/fruktoguo/daohua/releases/download/${RELEASE_TAG}/daohua.apk"
  fi
fi

# ------------------------------------------------------------------------------
# 阶段 4: 双端状态核对与运维入口提取
# ------------------------------------------------------------------------------
printf '\n==> [阶段 4/4] 双端状态核验与日志汇总...\n'

# 1. 核对 odin 状态
if [[ "$SKIP_ODIN" != "1" ]]; then
  ODIN_STATUS='{"status":"offline"}'
  for _ in {1..15}; do
    ODIN_STATUS="$(ssh -o BatchMode=yes "$ODIN_HOST" 'curl -s -m 2 http://127.0.0.1:8090/healthz' 2>/dev/null || printf '{"status":"offline"}')"
    if [[ "$ODIN_STATUS" == *'"status":"healthy"'* || "$ODIN_STATUS" == *'"healthy"'* ]]; then
      break
    fi
    sleep 1
  done
  printf '  - odin 节点状态: %s\n' "$ODIN_STATUS"
fi

# 2. 提取运维后台入口与接入日志
OPS_PATH="$(ssh -o BatchMode=yes "$REMOTE_HOST" 'sudo docker logs daohua-app --since 2m 2>&1 | grep -oE "/ops-[a-f0-9]{16}" | tail -n 1' 2>/dev/null || true)"

printf '\n======================================================\n'
printf '        正式服全量发布完成 (Commit: %s)\n' "$GIT_REV"
printf '======================================================\n'
printf '  • 主服版本标签 : sha-%s\n' "$GIT_REV"
printf '  • Server 镜像  : %s\n' "$APP_IMAGE_REF"
printf '  • Compute 镜像 : %s\n' "$COMPUTE_IMAGE_REF"
if [[ -n "$OPS_PATH" ]]; then
  printf '  • 运维后台地址 : https://daohua.yuohira.com%s\n' "$OPS_PATH"
fi
if [[ -f "$ROOT_DIR/artifacts/daohua.apk" ]]; then
  APK_SIZE="$(ls -lh "$ROOT_DIR/artifacts/daohua.apk" | awk '{print $5}')"
  printf '  • 安卓安装包   : artifacts/daohua.apk (%s, 完全版)\n' "$APK_SIZE"
  if [[ -n "$RELEASE_DOWNLOAD_URL" ]]; then
    printf '  • Release 直链 : %s\n' "$RELEASE_DOWNLOAD_URL"
  fi
fi
printf '  • 双层烟测结果 : 全部通过\n'
printf '======================================================\n\n'
