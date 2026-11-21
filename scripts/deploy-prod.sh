#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: deploy-prod.sh
# 主要职责: 远端生产环境全量受控发布、资产同步、服务更新与公网验收
#
# 核心发布流程:
#   1. 远端前置环境检查: 验证远端 SSH 连通性、Docker 与 Docker Compose 运行时就绪；
#   2. CCR 镜像权限验证: 验证远端是否具备拉取 Server/ComputeNode 成对镜像权限；
#   3. 同步生产资产: 创建安全临时目录，上传 Compose、Caddy、备份脚本及 systemd 配置文件；
#   4. 环境变量与安全校验: 确保远端 /opt/daohua/daohua.env 存在且已配置分离的 PostgreSQL 登录角色与 ComputeNode 密钥；
#   5. 安装资产与触发更新: 将资产安装到 /opt/daohua，显式指定 Server/ComputeNode digest 后触发成对更新；
#   6. systemd 服务与定时器配置: 启用并启动 CCR 自动更新 timer 与数据库备份 timer；
#   7. Caddy 反向代理部署: 可选校验并无缝热重载 Caddy 站点配置 (daohua.yuohira.com.caddy)；
#   8. 服务就绪与双层烟测: 检查容器健康与回环 /readyz，先在服务器回环跑全量烟测，
#      再在本地跑公网 HTTPS/WebSocket 烟测（公网模式下断言 /readyz 已被网关拦截）。
#
# 参数说明:
#   --host HOST       远端 SSH 主机名（默认 yuohira）
#   --image IMAGE     指定 Server 不可变 @sha256 镜像引用
#   --compute-image IMAGE  指定 ComputeNode 不可变 @sha256 镜像引用
#   --skip-caddy      跳过远端 Caddy 站点配置的安装与 reload
#   --skip-smoke      跳过发布后的内网/公网 HTTP/WebSocket 烟测
#   -h, --help        显示帮助信息
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# 迁移前必须能判定「库版本」与「存档标签版本」两个独立目标：
#   EXPECTED_SCHEMA_VERSION  = PostgresSchema.Version（platform.schema_version 目标）
#   EXPECTED_SECTION_VERSION = SectionCodec.CurrentSchemaVersion（分段载荷标签目标）
# 两者并不相等（例如 v41 / v38），搞混会把存档标签推进到解码器拒绝的值。
EXPECTED_SCHEMA_VERSION="$(grep -oE 'public const int Version = [0-9]+' "$ROOT_DIR/src/Server/Clean/PostgresSchema.cs" | grep -oE '[0-9]+' | head -n1)"
EXPECTED_SECTION_VERSION="$(grep -oE 'public const int CurrentSchemaVersion = [0-9]+' "$ROOT_DIR/src/Domain/Persistence/SectionCodec.cs" | grep -oE '[0-9]+' | head -n1)"
if [[ -z "$EXPECTED_SCHEMA_VERSION" || -z "$EXPECTED_SECTION_VERSION" ]]; then
  printf '无法从源码推断 schema 版本（库/存档标签），拒绝部署：迁移校验缺少判定依据。\n' >&2
  exit 1
fi
EXPECTED_APP_VERSION="v$(cd "$ROOT_DIR" && node -p "try{require('./version.json').version}catch{''}" 2>/dev/null || true)"
# 正式只接收完整镜像，声明如有需要从受控环境显式提供，不扫描私有仓库。
SMOKE_EXPECT_PRIVATE="${SMOKE_EXPECT_PRIVATE:-1}"

REMOTE_HOST="${SSH_HOST:-yuohira}"
IMAGE="${APP_IMAGE:-}"
COMPUTE_IMAGE="${COMPUTE_IMAGE:-}"
IMAGE_EXPLICIT=0
COMPUTE_IMAGE_EXPLICIT=0
INSTALL_CADDY=1
RUN_PUBLIC_SMOKE=1
REMOTE_TMP=""
SMOKE_ENV_FILE=""

usage() {
  printf '%s\n' \
    '用法：scripts/deploy-prod.sh [选项]' \
    '' \
    '选项：' \
    '  --host HOST       SSH 主机，默认 yuohira' \
    '  --image IMAGE     首次部署的 CCR 镜像，默认 prod' \
    '  --compute-image IMAGE  指定 ComputeNode 镜像（默认按 Server 镜像名推导）' \
    '  --skip-caddy      不修改远端 Caddy' \
    '  --skip-smoke      不执行发布后的内网/公网烟测' \
    '  -h, --help        显示帮助'
}

while (($# > 0)); do
  case "$1" in
    --host)
      REMOTE_HOST="${2:?--host 缺少值}"
      shift 2
      ;;
    --image)
      IMAGE="${2:?--image 缺少值}"
      IMAGE_EXPLICIT=1
      shift 2
      ;;
    --compute-image)
      COMPUTE_IMAGE="${2:?--compute-image 缺少值}"
      COMPUTE_IMAGE_EXPLICIT=1
      shift 2
      ;;
    --skip-caddy)
      INSTALL_CADDY=0
      shift
      ;;
    --skip-smoke)
      RUN_PUBLIC_SMOKE=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf '未知参数: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ ! "$REMOTE_HOST" =~ ^[A-Za-z0-9._-]+$ ]]; then
  printf 'SSH 主机名包含非法字符: %s\n' "$REMOTE_HOST" >&2
  exit 1
fi
if [[ -z "$IMAGE" || -z "$COMPUTE_IMAGE" ]]; then
  printf '生产发布必须显式提供 --image 与 --compute-image，不接受默认镜像。\n' >&2
  exit 1
fi
if [[ ! "$IMAGE" =~ ^[A-Za-z0-9._/:@-]+$ || ! "$COMPUTE_IMAGE" =~ ^[A-Za-z0-9._/:@-]+$ ]]; then
  printf '镜像引用包含非法字符。\n' >&2
  exit 1
fi
if [[ ! "$IMAGE" =~ @sha256:[a-f0-9]{64}$ || ! "$COMPUTE_IMAGE" =~ @sha256:[a-f0-9]{64}$ ]]; then
  printf '生产发布要求 Server/ComputeNode 均使用完整 @sha256 digest。\n' >&2
  exit 1
fi
if [[ "$RUN_PUBLIC_SMOKE" == "1" ]] && [[ -z "${SMOKE_COOKIE:-}" && -z "${SMOKE_SESSION_TOKEN:-}" && ( -z "${SMOKE_ACCOUNT:-}" || -z "${SMOKE_PASSWORD:-}" ) ]]; then
  printf '启用发布烟测时必须显式提供 SMOKE_COOKIE、SMOKE_SESSION_TOKEN，或 SMOKE_ACCOUNT+SMOKE_PASSWORD；否则使用 --skip-smoke。\n' >&2
  exit 1
fi


cleanup() {
  if [[ -n "$REMOTE_TMP" && "$REMOTE_TMP" == /tmp/daohua-deploy.* ]]; then
    ssh -o BatchMode=yes "$REMOTE_HOST" "rm -rf -- '$REMOTE_TMP'" >/dev/null 2>&1 || true
  fi
  if [[ -n "$SMOKE_ENV_FILE" ]]; then
    rm -f -- "$SMOKE_ENV_FILE"
  fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

printf '==> 检查远端运行环境\n'
ssh -o BatchMode=yes "$REMOTE_HOST" \
  'sudo -n true && sudo docker info >/dev/null && sudo docker compose version >/dev/null'

printf '==> 验证远端 CCR 拉取权限\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "sudo docker pull '$IMAGE' >/dev/null && sudo docker pull '$COMPUTE_IMAGE' >/dev/null"

REMOTE_TMP="$(ssh -o BatchMode=yes "$REMOTE_HOST" 'mktemp -d /tmp/daohua-deploy.XXXXXX')"
if [[ "$REMOTE_TMP" != /tmp/daohua-deploy.* ]]; then
  printf '远端临时目录不符合预期: %s\n' "$REMOTE_TMP" >&2
  exit 1
fi
if [[ "$RUN_PUBLIC_SMOKE" == "1" ]]; then
  SMOKE_ENV_FILE="$(mktemp "${TMPDIR:-/tmp}/daohua-smoke-env.XXXXXX")"
  chmod 600 "$SMOKE_ENV_FILE"
  if [[ -n "${SMOKE_COOKIE:-}" ]]; then
    printf 'SMOKE_COOKIE=%q\n' "$SMOKE_COOKIE" > "$SMOKE_ENV_FILE"
  elif [[ -n "${SMOKE_SESSION_TOKEN:-}" ]]; then
    printf 'SMOKE_SESSION_TOKEN=%q\n' "$SMOKE_SESSION_TOKEN" > "$SMOKE_ENV_FILE"
  else
    printf 'SMOKE_ACCOUNT=%q\nSMOKE_PASSWORD=%q\n' "$SMOKE_ACCOUNT" "$SMOKE_PASSWORD" > "$SMOKE_ENV_FILE"
    if [[ "${SMOKE_REGISTER:-0}" == "1" ]]; then
      printf 'SMOKE_REGISTER=1\n' >> "$SMOKE_ENV_FILE"
    fi
  fi
  printf 'SMOKE_EXPECT_PRIVATE=%q\n' "${SMOKE_EXPECT_PRIVATE:-0}" >> "$SMOKE_ENV_FILE"
  if [[ -n "${SMOKE_MODULE_DECLARATION:-}" ]]; then
    printf 'SMOKE_MODULE_DECLARATION=%q\n' "$SMOKE_MODULE_DECLARATION" >> "$SMOKE_ENV_FILE"
  fi
  SMOKE_ENV_FILE_UPLOAD="$SMOKE_ENV_FILE"
  if command -v cygpath >/dev/null 2>&1; then
    SMOKE_ENV_FILE_UPLOAD="$(cygpath -m "$SMOKE_ENV_FILE")"
  fi
  scp -q "$SMOKE_ENV_FILE_UPLOAD" "${REMOTE_HOST}:${REMOTE_TMP}/smoke.env"
fi


printf '==> 上传部署资产\n'
scp -q \
  "$ROOT_DIR/deploy/compose.prod.yaml" \
  "$ROOT_DIR/deploy/apply-prod-release.sh" \
  "$ROOT_DIR/deploy/backup-checks.sh" \
  "$ROOT_DIR/deploy/backup-access.sh" \
  "$ROOT_DIR/deploy/backup.sh" \
  "$ROOT_DIR/deploy/base-backup.sh" \
  "$ROOT_DIR/deploy/pitr-restore.sh" \
  "$ROOT_DIR/deploy/verify-backup.sh" \
  "$ROOT_DIR/deploy/market-ledger-archive.sh" \
  "$ROOT_DIR/deploy/daohua-db-backup.service" \
  "$ROOT_DIR/deploy/daohua-db-backup.timer" \
  "$ROOT_DIR/deploy/daohua-base-backup.service" \
  "$ROOT_DIR/deploy/daohua-base-backup.timer" \
  "$ROOT_DIR/deploy/daohua-ledger-archive.service" \
  "$ROOT_DIR/deploy/daohua-ledger-archive.timer" \
  "$ROOT_DIR/deploy/install-caddy.sh" \
  "$ROOT_DIR/deploy/daohua.yuohira.com.caddy" \
  "$ROOT_DIR/frontend/public/privacy.html" \
  "$ROOT_DIR/deploy/migrate-prod-db.sh" \
  "$ROOT_DIR/scripts/smoke-production.mjs" \
  "${REMOTE_HOST}:${REMOTE_TMP}/"

# migrations/ 与 static/status/ 独立同步到生产目录（前者不入镜像，后者由宿主机 Caddy 在停服期直出）。
printf '==> 上传兼容性迁移脚本与维护页静态资源\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "rm -rf '${REMOTE_TMP}/migrations' '${REMOTE_TMP}/status' && mkdir -p '${REMOTE_TMP}/migrations' '${REMOTE_TMP}/status'"
scp -q "$ROOT_DIR"/migrations/*.sql "${REMOTE_HOST}:${REMOTE_TMP}/migrations/"
scp -q "$ROOT_DIR"/deploy/static/status/* "${REMOTE_HOST}:${REMOTE_TMP}/status/"
# 防御 Windows core.autocrlf 检出导致的 CRLF 污染远端可执行脚本与 systemd 单元
ssh -o BatchMode=yes "$REMOTE_HOST" "sed -i 's/\r$//' '${REMOTE_TMP}'/*.sh '${REMOTE_TMP}'/*.service '${REMOTE_TMP}'/*.timer '${REMOTE_TMP}'/*.yaml '${REMOTE_TMP}'/*.caddy '${REMOTE_TMP}'/migrations/*.sql"

printf '==> 安装生产 Compose 与 systemd timer\n'
printf '==> 校验生产环境变量\n'
ssh -o BatchMode=yes "$REMOTE_HOST" '
  set -Eeuo pipefail
  if ! sudo test -f /opt/daohua/daohua.env; then
    printf "缺少 /opt/daohua/daohua.env，请配置 DB_BOOTSTRAP_USER/DB_APP_USER/DB_WORKER_USER 与对应凭据。\n" >&2
    exit 1
  fi
  if ! sudo grep -Eq "^DB_BOOTSTRAP_USER=[^[:space:]#]{1,63}$" /opt/daohua/daohua.env || ! sudo grep -Eq "^DB_BOOTSTRAP_PASSWORD=[^[:space:]#]{24,}$" /opt/daohua/daohua.env; then
    printf "DB_BOOTSTRAP_USER/DB_BOOTSTRAP_PASSWORD 必须显式配置，仅供数据库初始化使用。\n" >&2
    exit 1
  fi
  if ! sudo grep -Eq "^DB_APP_USER=[^[:space:]#]{1,63}$" /opt/daohua/daohua.env || ! sudo grep -Eq "^DB_APP_PASSWORD=[^[:space:]#]{24,}$" /opt/daohua/daohua.env; then
    printf "DB_APP_USER/DB_APP_PASSWORD 必须显式配置专用业务角色凭据。\n" >&2
    exit 1
  fi
  if ! sudo grep -Eq "^DB_WORKER_USER=[^[:space:]#]{1,63}$" /opt/daohua/daohua.env || ! sudo grep -Eq "^DB_WORKER_PASSWORD=[^[:space:]#]{24,}$" /opt/daohua/daohua.env; then
    printf "DB_WORKER_USER/DB_WORKER_PASSWORD 必须显式配置专用 worker 角色凭据。\n" >&2
    exit 1
  fi
  if ! sudo grep -Eq "^BACKUP_TOKEN=[^[:space:]#]{32,}$" /opt/daohua/daohua.env; then
    printf "BACKUP_TOKEN 必须显式配置且长度不少于 32 位。\n" >&2
    exit 1
  fi
  if sudo grep -Eq "^(DB_BOOTSTRAP_PASSWORD|DB_APP_PASSWORD|DB_WORKER_PASSWORD|COMPUTE_TOKEN|BACKUP_TOKEN|TAPTAP_CLIENT_SECRET)=(change-me|replace-me|password|secret|verify-|daohua-backup-token)" /opt/daohua/daohua.env; then
    printf "生产环境变量仍使用明显占位值或已废弃默认值，拒绝部署。\n" >&2
    exit 1
  fi
'

# 受控部署直接进入远端事务执行迁移、容器换代与双层烟测（已下线 CCR 自动更新竞争锁）。
printf '==> 受控停写、迁移、切换和验收\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "sudo bash '$REMOTE_TMP/apply-prod-release.sh' '$REMOTE_TMP' '$IMAGE' '$COMPUTE_IMAGE' '$EXPECTED_SCHEMA_VERSION' '$EXPECTED_SECTION_VERSION' '$INSTALL_CADDY' '$RUN_PUBLIC_SMOKE'"

printf '生产部署完成。\n'
