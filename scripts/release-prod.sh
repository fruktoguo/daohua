#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: release-prod.sh
# 主要职责: 正式服全自动一键打包与发布（yuohira 主服 + odin 计算节点）
#
# 核心发布流水线:
#   1. 预检: 本机 Git/Submodule、Docker/CCR 凭据与远端连通性快速探测；
#   2. 通过本次提交的备份门禁后，才并发推送镜像及准备 odin；
#   3. 主服受控部署: 自动生成合规随机烟测账号，调用 deploy-prod.sh 执行容器更新与双层烟测；
#   4. odin 原子热切: 切换 odin 目录并热重启进程，看门狗自动拉起；
#   5. 双端验证与总结: 校验数据指纹一致性、计算节点接入状态，。
#
# 选项:
#   --skip-odin      跳过 odin 计算节点发布（仅发布主服）
#   --skip-backup    跳过本轮逻辑备份生成，仍必须通过门禁
#   --refresh-base-backup  显式生成物理基线后再校验（首次引入或基线过期）
#   --skip-smoke     跳过主服回环与公网烟测
#   --push-existing  跳过镜像构建，仅推送并使用本机已有镜像
#   --host HOST      主服 SSH 别名（默认 yuohira）
#   --odin-host HOST odin SSH 别名（默认 odin）
#   -h, --help       显示帮助
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST_OVERRIDE=""
ODIN_HOST_OVERRIDE=""
SKIP_ODIN=0
SKIP_BACKUP=0
REFRESH_BASE_BACKUP=0
SKIP_SMOKE=0
PUSH_EXISTING=0
# Web 化后 APK 仅承载原生壳与静态资源基线：默认不再每次发布重打，
# 仅在原生壳/SDK/权限变更或用户明确要求时通过 --with-apk 构建并发布。
WITH_APK=0
SKIP_APK_UPLOAD=0
SKIP_HD_PACK=0
SKIP_CADDY=0
APK_BUILT=0
BUMP_TYPE=""
EXPLICIT_VERSION=""
EXPLICIT_TAG=""
usage() {
  printf '%s\n' \
    '用法：scripts/release-prod.sh [选项]' \
    '' \
    '选项：' \
    '  --skip-odin      跳过 odin 计算节点发布（仅更新主服）' \
    '  --skip-backup    跳过本轮逻辑备份生成，仍要求新鲜逻辑/物理备份通过门禁' \
    '  --refresh-base-backup  用本次提交的脚本新建物理基线（先确认磁盘与 WAL 保留预算）' \
    '  --skip-smoke     跳过部署后的内网与公网烟测' \
    '  --skip-caddy     跳过远端 Caddy 站点配置安装与 reload' \
    '  --with-apk       构建 Android 安装包并发布 GitHub Releases（默认跳过：Web 化后仅原生壳变更时需要）' \
    '  --skip-apk       显式跳过 APK 构建（与默认行为一致，兼容旧用法）' \
    '  --skip-apk-upload 跳过向 GitHub Releases 上传安装包（配合 --with-apk）' \
    '  --skip-hd-pack   跳过 HD 画质资源包上传至主服 /hd/*' \
    '  --push-existing  跳过镜像重新构建，仅推送本机已有镜像' \
    '  --host HOST      主服 SSH 别名（默认 yuohira）' \
    '  --odin-host HOST odin SSH 别名（默认 odin）' \
    '  --version VER    确认已提交的版本号（如 0.2.1）' \
    '  --tag TAG        显式覆盖发布标签（默认: v<version>）' \
    '  -h, --help       显示帮助信息'
}

while (($# > 0)); do
  case "$1" in
    --skip-odin) SKIP_ODIN=1; shift ;;
    --skip-backup) SKIP_BACKUP=1; shift ;;
    --refresh-base-backup) REFRESH_BASE_BACKUP=1; shift ;;
    --skip-smoke) SKIP_SMOKE=1; shift ;;
    --skip-caddy) SKIP_CADDY=1; shift ;;
    --with-apk) WITH_APK=1; shift ;;
    --skip-apk) WITH_APK=0; shift ;;
    --skip-apk-upload) SKIP_APK_UPLOAD=1; shift ;;
    --skip-hd-pack) SKIP_HD_PACK=1; shift ;;
    --push-existing) PUSH_EXISTING=1; shift ;;
    --allow-dirty) printf '正式发布不允许 --allow-dirty。\n' >&2; exit 2 ;;
    --host) HOST_OVERRIDE="${2:?--host 缺少值}"; shift 2 ;;
    --odin-host) ODIN_HOST_OVERRIDE="${2:?--odin-host 缺少值}"; shift 2 ;;
    --bump) BUMP_TYPE="${2:?--bump 缺少类型 (patch|minor|major)}"; shift 2 ;;
    --version) EXPLICIT_VERSION="${2:?--version 缺少值}"; shift 2 ;;
    --tag) EXPLICIT_TAG="${2:?--tag 缺少值}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知参数: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

cd "$ROOT_DIR"

# 自动加载本机受控发布凭据文件（已被 .gitignore 忽略，不入库）
if [[ -f "$ROOT_DIR/.env.release.local" ]]; then
  # shellcheck disable=SC1091
  set -a
  source "$ROOT_DIR/.env.release.local"
  set +a
fi

REMOTE_HOST="${HOST_OVERRIDE:-${SSH_HOST:-yuohira}}"
ODIN_HOST="${ODIN_HOST_OVERRIDE:-${ODIN_HOST:-odin}}"
if [[ ! "$REMOTE_HOST" =~ ^[A-Za-z0-9_][A-Za-z0-9._-]*$ || ( "$SKIP_ODIN" != 1 && ! "$ODIN_HOST" =~ ^[A-Za-z0-9_][A-Za-z0-9._-]*$ ) ]]; then
  printf 'SSH 主机别名非法。\n' >&2; exit 2
fi

if [[ "$SKIP_BACKUP" != [01] || "$REFRESH_BASE_BACKUP" != [01] ]]; then
  printf '备份控制参数必须为 0 或 1。\n' >&2; exit 2
fi

# 清理本地临时产物前，先回收尚未结束的后台流。EXIT 不触碰远端维护标记或 timer。
RELEASE_TMP="$(mktemp -d /tmp/daohua-release.XXXXXX 2>/dev/null || mktemp -d -t daohua-release.XXXXXX)"
pids=()
cleanup() {
  local rc=$? pid
  trap - EXIT
  trap '' HUP INT TERM
  for pid in "${pids[@]}"; do
    # 等待既有流收尾，尤其不能删除仍写入的打包目录或中断远端备份生成。
    wait "$pid" 2>/dev/null || true
  done
  rm -rf -- "$RELEASE_TMP" || true
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

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

# 2. 固定完整发布输入；不修改用户 Git 配置。
source "$ROOT_DIR/deploy/release-source.sh"
release_sources "$ROOT_DIR"
export RELEASE_MAIN_REVISION="$main_revision" RELEASE_YUO_REVISION="$yuo_revision" RELEASE_PRIVATE_REVISION="$private_revision"

# 2.5 前置校验烟测凭据（禁止在脚本中硬编码默认密码）
if [[ "$SKIP_SMOKE" != "1" ]] && [[ -z "${SMOKE_COOKIE:-}" && -z "${SMOKE_SESSION_TOKEN:-}" ]]; then
  if [[ -z "${SMOKE_PASSWORD:-}" ]]; then
    printf '错误: 启用发布烟测时必须设置 SMOKE_PASSWORD（可在被 .gitignore 忽略的 .env.release.local 中配置），禁止硬编码默认口令。\n' >&2
    exit 1
  fi
  if [[ "${SMOKE_PASSWORD}" =~ ^(DaohuaSmoke#|change-me|replace-me|password|secret|verify-) ]]; then
    printf '错误: SMOKE_PASSWORD 仍使用已废弃泄露口令或占位符，请轮换后再发布。\n' >&2
    exit 1
  fi
fi

GIT_REV="$RELEASE_MAIN_REVISION"
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
    printf '失败！无法免密连接 %s（如需仅更新主服请显式传入 --skip-odin）。\n' "$ODIN_HOST" >&2
    exit 1
  fi
fi

# 版本号只来自提交；禁止发布脚本改写用户工作区。
PKG_VERSION="$(git show "$GIT_REV:version.json" | node -p 'JSON.parse(require("fs").readFileSync(0,"utf8")).version')"
if [[ -n "$BUMP_TYPE" || ( -n "$EXPLICIT_VERSION" && "$EXPLICIT_VERSION" != "$PKG_VERSION" ) ]]; then
  printf '请先提交目标 version.json 后再发布；本脚本不改写版本文件。\n' >&2; exit 1
fi

RELEASE_TAG="${EXPLICIT_TAG:-v${PKG_VERSION}}"
[[ "$RELEASE_TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]] || { printf '非法发布标签。\n' >&2; exit 1; }
printf '  - 发布版本: %s (Tag: %s)\n' "$PKG_VERSION" "$RELEASE_TAG"

# ------------------------------------------------------------------------------
# 阶段 1: 先验证恢复点，再开始任何 CCR 推送或节点预传。
# ------------------------------------------------------------------------------
printf '\n==> [阶段 1/4] 使用发布提交校验备份门禁（不执行迁移或安装脚本）...\n'

# 不依赖主服已安装的旧 verifier/base 参数，不在 /opt 覆盖任何脚本。
# 用 root 私有临时目录接收固定文件并清理；生成基线必须由操作者显式选择。
gate_command="$(cat <<'REMOTE_GATE'
set -Eeuo pipefail
gate="$(mktemp -d /tmp/daohua-release-gate.XXXXXX)"
trap "rm -rf -- \"$gate\"" EXIT
trap "exit 129" HUP
trap "exit 130" INT
trap "exit 143" TERM
tar --no-same-owner -xf - -C "$gate" deploy/backup.sh deploy/base-backup.sh deploy/backup-access.sh deploy/backup-checks.sh deploy/verify-backup.sh deploy/wal-archive.sh
if [[ "$1" == 0 ]]; then bash "$gate/deploy/backup.sh"; fi
if [[ "$2" == 1 ]]; then bash "$gate/deploy/base-backup.sh"; fi
bash "$gate/deploy/verify-backup.sh"
REMOTE_GATE
)"
if ! git archive "$GIT_REV" -- deploy/backup.sh deploy/base-backup.sh deploy/backup-access.sh deploy/backup-checks.sh deploy/verify-backup.sh deploy/wal-archive.sh |
    ssh -T -o BatchMode=yes "$REMOTE_HOST" "sudo -n bash -c '$gate_command' bash '$SKIP_BACKUP' '$REFRESH_BASE_BACKUP'"; then
  printf '备份门禁失败：未推送镜像、未预传或部署。首次引入若缺新鲜物理基线，请先核对磁盘及 WAL 保留预算，再显式使用 --refresh-base-backup。\n' >&2
  exit 1
fi
printf '  ✔ 本次发布提交的备份门禁通过。\n'
printf '==> 并发启动镜像构建推送与 Odin 交叉编译...\n'

# 流 A: Docker 镜像构建并推送
STREAM_A_LOG="$RELEASE_TMP/stream_a_docker.log"
(
  set -Eeuo pipefail
  printf '[流 A] 开始构建与推送 CCR 镜像 (tag: %s)...\n' "$RELEASE_TAG"
  BUILD_ARGS=("--push" "--release-tag" "$RELEASE_TAG")
  if [[ "$PUSH_EXISTING" == "1" ]]; then
    BUILD_ARGS=("--push-existing" "--release-tag" "$RELEASE_TAG")
  fi
  bash "$ROOT_DIR/scripts/docker-build-prod.sh" "${BUILD_ARGS[@]}" > "$STREAM_A_LOG" 2>&1

) &
PID_STREAM_A=$!
pids+=("$PID_STREAM_A")

# 流 C: odin 掌机 linux-arm64 交叉编译与预传
STREAM_C_LOG="$RELEASE_TMP/stream_c_odin.log"
if [[ "$SKIP_ODIN" != "1" ]]; then
  (
    set -Eeuo pipefail
    printf '[流 C] 本地交叉编译 ComputeNode (linux-arm64) 并预传至 odin...\n'
    ODIN_PUBLISH_DIR="$RELEASE_TMP/daohua-compute-odin"
    ODIN_SOURCE="$RELEASE_TMP/odin-source"
    mkdir -p "$ODIN_SOURCE/vendor/YuoCore"
    git archive "$GIT_REV" | tar -x -C "$ODIN_SOURCE"
    git -C "$ROOT_DIR/vendor/YuoCore" archive "$RELEASE_YUO_REVISION" | tar -x -C "$ODIN_SOURCE/vendor/YuoCore"
    ODIN_OUT="$ODIN_PUBLISH_DIR"
    if command -v cygpath >/dev/null 2>&1; then
      ODIN_OUT="$(cygpath -w "$ODIN_PUBLISH_DIR")"
    fi
    dotnet publish "$ODIN_SOURCE/src/ComputeNode" -c Release -r linux-arm64 --self-contained -o "$ODIN_OUT" --nologo \
      -p:ContinuousIntegrationBuild=true -p:DaoHuaObservability=false -p:IncludeSourceRevisionInInformationalVersion=false -p:InformationalVersion="${RELEASE_TAG}" > "$STREAM_C_LOG" 2>&1
    ssh -o BatchMode=yes "$ODIN_HOST" 'rm -rf /root/daohua-compute.new && mkdir -p /root/daohua-compute.new' >> "$STREAM_C_LOG" 2>&1
    tar -czf - -C "$ODIN_PUBLISH_DIR" . | ssh -o BatchMode=yes "$ODIN_HOST" 'tar -xzf - -C /root/daohua-compute.new/' >> "$STREAM_C_LOG" 2>&1

  ) &
  PID_STREAM_C=$!
  pids+=("$PID_STREAM_C")
else

  PID_STREAM_C=""
fi


# 等待并发任务结束
wait $PID_STREAM_A || {
  printf '\n错误: [流 A] Docker 镜像构建或推送失败！日志摘要:\n' >&2
  tail -n 35 "$STREAM_A_LOG" >&2
  exit 1
}
printf '  ✔ [流 A] 镜像构建与推送完成。\n'

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

repo_digest() {
  local repo="$1" digests digest
  digests="$(docker image inspect "$repo:$RELEASE_TAG" --format '{{range .RepoDigests}}{{println .}}{{end}}')" || return 1
  while IFS= read -r digest; do
    if [[ "$digest" == "$repo@sha256:"* && "${digest##*@}" =~ ^sha256:[a-f0-9]{64}$ ]]; then
      printf '%s\n' "$digest"; return 0
    fi
  done <<< "$digests"
  printf '找不到本次镜像仓库对应的已推送 digest: %s\n' "$repo" >&2
  return 1
}
APP_IMAGE_REF="$(repo_digest ccr.ccs.tencentyun.com/yuohira/daohua)"
COMPUTE_IMAGE_REF="$(repo_digest ccr.ccs.tencentyun.com/yuohira/daohua-compute)"

printf '  - Server Image : %s\n' "$APP_IMAGE_REF"
printf '  - Compute Image: %s\n' "$COMPUTE_IMAGE_REF"

# ------------------------------------------------------------------------------
# 阶段 3: 主服受控部署与 odin 热重载
# ------------------------------------------------------------------------------
printf '\n==> [阶段 3/4] 部署主服并热切换 odin 计算节点...\n'

# 1. 固定专用烟测账号与口令（优先读取外部环境变量或 .env.release.local，禁止在脚本硬编码默认口令）
SMOKE_ACC="${SMOKE_ACCOUNT:-smoke_ci_verifier}"
SMOKE_PW="${SMOKE_PASSWORD:-}"

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

release_sources "$ROOT_DIR"
[[ "$main_revision" == "$GIT_REV" && "$yuo_revision" == "$RELEASE_YUO_REVISION" && "$private_revision" == "$RELEASE_PRIVATE_REVISION" ]] || {
  printf '发布输入已变化，拒绝部署。\n' >&2; exit 1;
}
printf '  - 触发主服部署 (deploy-prod.sh)...\n'
export SMOKE_ACCOUNT="$SMOKE_ACC"
export SMOKE_PASSWORD="$SMOKE_PW"
export SMOKE_REGISTER=1

bash "$ROOT_DIR/scripts/deploy-prod.sh" "${DEPLOY_FLAGS[@]}"

# 2. 若 odin 已准备完毕，执行原子目录切换与热重启
OLD_ODIN_PID=""
if [[ "$SKIP_ODIN" != "1" ]]; then
  printf '  - 原子切换 odin 目录并重启服务...\n'
  OLD_ODIN_PID="$(ssh -n -o BatchMode=yes "$ODIN_HOST" "(pgrep -x DaoHua.ComputeNode 2>/dev/null || pgrep -x DaoHua.ComputeN 2>/dev/null || true) | head -n1" 2>/dev/null || true)"
  ssh -n -o BatchMode=yes "$ODIN_HOST" '
    set -Eeuo pipefail
    rm -rf /root/daohua-compute.old
    [[ -d /root/daohua-compute ]] && mv /root/daohua-compute /root/daohua-compute.old
    mv /root/daohua-compute.new /root/daohua-compute
    chmod +x /root/daohua-compute/DaoHua.ComputeNode
    pkill -x DaoHua.ComputeNode 2>/dev/null || pkill -x DaoHua.ComputeN 2>/dev/null || true
  '
fi

# ------------------------------------------------------------------------------
# 阶段 3.5: Android 完全版客户端编译与 GitHub Releases 自动发布（显式 --with-apk 才执行）
# ------------------------------------------------------------------------------
RELEASE_DOWNLOAD_URL=""
if [[ "$WITH_APK" == "1" ]]; then
  printf '\n==> [阶段 3.5] 独占构建 Android 完全版安装包并发布到 GitHub Releases...\n'
  # 现有 APK 脚本使用本地 Android/签名工具链；不并入 Docker 三 revision 溯源证明。
  APK_ARGS=("--require-full" "--tag" "$RELEASE_TAG")
  apk="$ROOT_DIR/artifacts/daohua.apk"
  apk_marker="$RELEASE_TMP/apk-start"
  touch "$apk_marker"
  bash "$ROOT_DIR/scripts/build-apk.sh" "${APK_ARGS[@]}"
  [[ -s "$apk" && "$apk" -nt "$apk_marker" ]] || { printf 'APK 构建未生成本轮新产物。\n' >&2; exit 1; }
  APK_BUILT=1
  if [[ "$SKIP_APK_UPLOAD" != "1" ]]; then
    bash "$ROOT_DIR/scripts/upload-release-apk.sh" --file "$apk" --tag "$RELEASE_TAG"
    RELEASE_DOWNLOAD_URL="https://github.com/fruktoguo/daohua/releases/download/${RELEASE_TAG}/daohua.apk"
  fi
else
  printf '\n==> [阶段 3.5] 跳过 APK 构建（Web 化后默认行为；原生壳变更时使用 --with-apk）。\n'
fi

# ------------------------------------------------------------------------------
# 阶段 3.6: HD 画质资源包上传主服（/hd/* 静态路由，Android 原生热更数据源）
# ------------------------------------------------------------------------------
if [[ "$SKIP_HD_PACK" != "1" ]]; then
  hd_pack="$RELEASE_TMP/hd-pack"
  node scripts/gen-hd-pack.mjs "$hd_pack" >/dev/null
  [[ -s "$hd_pack/asset-manifest.json" ]] || { printf '未生成本轮 HD 画质包。\n' >&2; exit 1; }
  printf '\n==> [阶段 3.6] 上传本轮 HD 画质资源包...\n'
  SSH_HOST="$REMOTE_HOST" bash "$ROOT_DIR/scripts/upload-hd-pack.sh" "$hd_pack"
else
  printf '\n==> [阶段 3.6] 用户指定跳过 HD 画质资源包上传。\n'
fi

# ------------------------------------------------------------------------------
# 阶段 4: 双端状态核对与运维入口提取
# ------------------------------------------------------------------------------
printf '\n==> [阶段 4/4] 双端状态核验与日志汇总...\n'

# 1. 核对 odin 进程 PID 替换、版本、配表指纹及上行连入状态
if [[ "$SKIP_ODIN" != "1" ]]; then
  MAIN_FP="$(curl -s -m 5 https://daohua.yuohira.com/api/v1/meta | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s).gameDataFingerprint||'')}catch{}})" 2>/dev/null || true)"
  ODIN_STATUS='{"status":"offline"}'
  NEW_ODIN_PID=""
  ODIN_READY=0
  for _ in {1..30}; do
    NEW_ODIN_PID="$(ssh -n -o BatchMode=yes "$ODIN_HOST" "(pgrep -x DaoHua.ComputeNode 2>/dev/null || pgrep -x DaoHua.ComputeN 2>/dev/null || true) | head -n1" 2>/dev/null || true)"
    ODIN_STATUS="$(ssh -n -o BatchMode=yes "$ODIN_HOST" 'curl -s -m 2 http://127.0.0.1:8090/healthz' 2>/dev/null || printf '{"status":"offline"}')"
    if [[ -n "$NEW_ODIN_PID" && ( -z "$OLD_ODIN_PID" || "$NEW_ODIN_PID" != "$OLD_ODIN_PID" ) ]] \
       && [[ "$ODIN_STATUS" == *'"status":"ok"'* && "$ODIN_STATUS" == *"\"version\":\"${RELEASE_TAG}\""* && "$ODIN_STATUS" == *'"upstreamConnected":true'* ]]; then
      ODIN_READY=1
      break
    fi
    sleep 2
  done
  printf '  - odin PID=%s (旧 %s) 状态: %s\n' "${NEW_ODIN_PID:-无}" "${OLD_ODIN_PID:-无}" "$ODIN_STATUS"
  if [[ "$ODIN_READY" != "1" ]]; then
    printf '错误: odin 未在 60 秒内完成重启并接入主服目标版本 %s！\n' "$RELEASE_TAG" >&2
    exit 1
  fi
  if [[ -n "$MAIN_FP" && "$ODIN_STATUS" != *"\"gameDataFingerprint\":\"${MAIN_FP}\""* ]]; then
    printf '错误: odin 配表指纹与主服不一致 (主服=%s)！\n' "$MAIN_FP" >&2
    exit 1
  fi
fi

printf '\n======================================================\n'
printf '        正式服全量发布完成 (Commit: %s)\n' "$GIT_REV"
printf '======================================================\n'
printf '  • 主服版本标签 : %s\n' "$RELEASE_TAG"
printf '  • Server 镜像  : %s\n' "$APP_IMAGE_REF"
printf '  • Compute 镜像 : %s\n' "$COMPUTE_IMAGE_REF"
printf '  • YuoCore revision: %s\n' "$RELEASE_YUO_REVISION"
printf '  • Private revision: %s（容器镜像输入）\n' "$RELEASE_PRIVATE_REVISION"
if [[ "$APK_BUILT" == "1" && -f "$ROOT_DIR/artifacts/daohua.apk" ]]; then
  APK_SIZE="$(ls -lh "$ROOT_DIR/artifacts/daohua.apk" | awk '{print $5}')"
  printf '  • 安卓安装包   : artifacts/daohua.apk (%s, 完全版)\n' "$APK_SIZE"
  if [[ -n "$RELEASE_DOWNLOAD_URL" ]]; then
    printf '  • Release 直链 : %s\n' "$RELEASE_DOWNLOAD_URL"
  fi
fi
if [[ "$SKIP_SMOKE" == 1 ]]; then
  printf '  • 双层烟测结果 : 已显式跳过\n'
else
  printf '  • 双层烟测结果 : 全部通过\n'
fi
printf '======================================================\n\n'
