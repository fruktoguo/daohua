#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: deploy-prod.sh
# 主要职责: 远端生产环境全量受控发布、资产同步、服务更新与公网验收
#
# 核心发布流程:
#   1. 远端前置环境检查: 验证远端 SSH 连通性、Docker 与 Docker Compose 运行时就绪；
#   2. CCR 镜像权限验证: 验证远端是否具备拉取指定版本镜像权限；
#   3. 同步生产资产: 创建安全临时目录，上传 Compose、Caddy、备份脚本及 systemd 配置文件；
#   4. 环境变量与安全校验: 确保远端 /opt/daohua/daohua.env 存在且已配置 PostgreSQL 密钥；
#   5. 安装资产与触发更新: 将资产安装到 /opt/daohua，触发 ccr-auto-update.sh 进行沙箱预检与平滑更新；
#   6. systemd 服务与定时器配置: 启用并启动 CCR 自动更新 timer 与数据库备份 timer；
#   7. Caddy 反向代理部署: 可选校验并无缝热重载 Caddy 站点配置 (xx.yuohira.com.caddy)；
#   8. 服务就绪与公网烟测: 检查容器健康与回环 /readyz 端点，执行公网 HTTPS/WebSocket 烟测。
#
# 参数说明:
#   --host HOST       远端 SSH 主机名（默认 yuohira）
#   --image IMAGE     指定的生产 CCR 镜像引用（默认 ccr.ccs.tencentyun.com/yuohira/daohua:prod）
#   --skip-caddy      跳过远端 Caddy 站点配置的安装与 reload
#   --skip-smoke      跳过发布后的公网 HTTP/WebSocket 烟测
#   -h, --help        显示帮助信息
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REMOTE_HOST="${DAOHUA_SSH_HOST:-yuohira}"
IMAGE="${DAOHUA_IMAGE:-ccr.ccs.tencentyun.com/yuohira/daohua:prod}"
INSTALL_CADDY=1
RUN_PUBLIC_SMOKE=1
REMOTE_TMP=""

usage() {
  printf '%s\n' \
    '用法：scripts/deploy-prod.sh [选项]' \
    '' \
    '选项：' \
    '  --host HOST       SSH 主机，默认 yuohira' \
    '  --image IMAGE     首次部署的 CCR 镜像，默认 prod' \
    '  --skip-caddy      不修改远端 Caddy' \
    '  --skip-smoke      不执行公网 HTTP/WebSocket 烟测' \
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
if [[ ! "$IMAGE" =~ ^[A-Za-z0-9._/:@-]+$ ]]; then
  printf '镜像引用包含非法字符: %s\n' "$IMAGE" >&2
  exit 1
fi

cleanup() {
  if [[ -n "$REMOTE_TMP" && "$REMOTE_TMP" == /tmp/daohua-deploy.* ]]; then
    ssh -o BatchMode=yes "$REMOTE_HOST" "rm -rf -- '$REMOTE_TMP'" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

printf '==> 检查远端运行环境\n'
ssh -o BatchMode=yes "$REMOTE_HOST" \
  'sudo -n true && sudo docker info >/dev/null && sudo docker compose version >/dev/null'

printf '==> 验证远端 CCR 拉取权限\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "sudo docker pull '$IMAGE' >/dev/null"

REMOTE_TMP="$(ssh -o BatchMode=yes "$REMOTE_HOST" 'mktemp -d /tmp/daohua-deploy.XXXXXX')"
if [[ "$REMOTE_TMP" != /tmp/daohua-deploy.* ]]; then
  printf '远端临时目录不符合预期: %s\n' "$REMOTE_TMP" >&2
  exit 1
fi

printf '==> 上传部署资产\n'
scp -q \
  "$ROOT_DIR/deploy/compose.prod.yaml" \
  "$ROOT_DIR/deploy/ccr-auto-update.sh" \
  "$ROOT_DIR/deploy/backup.sh" \
  "$ROOT_DIR/deploy/daohua-ccr-auto-update.service" \
  "$ROOT_DIR/deploy/daohua-ccr-auto-update.timer" \
  "$ROOT_DIR/deploy/daohua-db-backup.service" \
  "$ROOT_DIR/deploy/daohua-db-backup.timer" \
  "$ROOT_DIR/deploy/install-caddy.sh" \
  "$ROOT_DIR/deploy/xx.yuohira.com.caddy" \
  "${REMOTE_HOST}:${REMOTE_TMP}/"

printf '==> 安装生产 Compose 与 systemd timer\n'
printf '==> 校验生产环境变量\n'
ssh -o BatchMode=yes "$REMOTE_HOST" '
  set -Eeuo pipefail
  if ! sudo test -f /opt/daohua/daohua.env; then
    printf "缺少 /opt/daohua/daohua.env，请设置 DAOHUA_POSTGRES_PASSWORD。\n" >&2
    exit 1
  fi
  if ! sudo grep -q "^DAOHUA_POSTGRES_PASSWORD=" /opt/daohua/daohua.env; then
    printf "缺少 DAOHUA_POSTGRES_PASSWORD，PostgreSQL 无法启动。\n" >&2
    exit 1
  fi
'

ssh -o BatchMode=yes "$REMOTE_HOST" "
  set -Eeuo pipefail
  sudo install -d -m 0755 /opt/daohua
  sudo install -m 0644 '$REMOTE_TMP/compose.prod.yaml' /opt/daohua/compose.prod.yaml
  sudo install -m 0755 '$REMOTE_TMP/ccr-auto-update.sh' /opt/daohua/ccr-auto-update.sh
  sudo install -m 0755 '$REMOTE_TMP/backup.sh' /opt/daohua/backup.sh
  sudo install -m 0644 '$REMOTE_TMP/daohua-ccr-auto-update.service' /etc/systemd/system/daohua-ccr-auto-update.service
  sudo install -m 0644 '$REMOTE_TMP/daohua-ccr-auto-update.timer' /etc/systemd/system/daohua-ccr-auto-update.timer
  sudo install -m 0644 '$REMOTE_TMP/daohua-db-backup.service' /etc/systemd/system/daohua-db-backup.service
  sudo install -m 0644 '$REMOTE_TMP/daohua-db-backup.timer' /etc/systemd/system/daohua-db-backup.timer
  sudo systemctl daemon-reload
  sudo rm -f /opt/daohua/failed-image.id
  sudo env DAOHUA_IMAGE='$IMAGE' /opt/daohua/ccr-auto-update.sh
  sudo systemctl enable --now daohua-ccr-auto-update.timer
  sudo systemctl enable --now daohua-db-backup.timer
"

if [[ "$INSTALL_CADDY" == "1" ]]; then
  printf '==> 校验并安装 Caddy 站点\n'
  ssh -o BatchMode=yes "$REMOTE_HOST" "
    set -Eeuo pipefail
    sudo install -m 0755 '$REMOTE_TMP/install-caddy.sh' /opt/daohua/install-caddy.sh
    sudo install -m 0644 '$REMOTE_TMP/xx.yuohira.com.caddy' /opt/daohua/xx.yuohira.com.caddy
    sudo /opt/daohua/install-caddy.sh /opt/daohua/xx.yuohira.com.caddy
  "
fi

printf '==> 核对远端容器和回环健康端点\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "
  set -Eeuo pipefail
  sudo docker inspect --format 'image={{.Config.Image}} id={{.Image}} health={{.State.Health.Status}}' daohua-app
  curl --fail --silent --show-error http://127.0.0.1:15128/readyz
  printf '\n'
  sudo systemctl is-enabled daohua-ccr-auto-update.timer
  sudo systemctl is-active daohua-ccr-auto-update.timer
"

if [[ "$RUN_PUBLIC_SMOKE" == "1" ]]; then
  printf '==> 公网 HTTPS 与 WebSocket 烟测\n'
  smoke_ok=0
  for attempt in {1..6}; do
    if node "$ROOT_DIR/scripts/smoke-production.mjs" https://xx.yuohira.com; then
      smoke_ok=1
      break
    fi
    printf '第 %s 次公网烟测未通过，5 秒后重试。\n' "$attempt" >&2
    sleep 5
  done
  if [[ "$smoke_ok" != "1" ]]; then
    printf '公网烟测连续失败。\n' >&2
    exit 1
  fi
fi

printf '生产部署完成。\n'
