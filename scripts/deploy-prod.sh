#!/usr/bin/env bash

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REMOTE_HOST="${XIUXIAN_SSH_HOST:-yuohira}"
IMAGE="${XIUXIAN_IMAGE:-ccr.ccs.tencentyun.com/yuohira/xiuxian:prod}"
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
  if [[ -n "$REMOTE_TMP" && "$REMOTE_TMP" == /tmp/xiuxian-deploy.* ]]; then
    ssh -o BatchMode=yes "$REMOTE_HOST" "rm -rf -- '$REMOTE_TMP'" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

printf '==> 检查远端运行环境\n'
ssh -o BatchMode=yes "$REMOTE_HOST" \
  'sudo -n true && sudo docker info >/dev/null && sudo docker compose version >/dev/null'

printf '==> 验证远端 CCR 拉取权限\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "sudo docker pull '$IMAGE' >/dev/null"

REMOTE_TMP="$(ssh -o BatchMode=yes "$REMOTE_HOST" 'mktemp -d /tmp/xiuxian-deploy.XXXXXX')"
if [[ "$REMOTE_TMP" != /tmp/xiuxian-deploy.* ]]; then
  printf '远端临时目录不符合预期: %s\n' "$REMOTE_TMP" >&2
  exit 1
fi

printf '==> 上传部署资产\n'
scp -q \
  "$ROOT_DIR/deploy/compose.prod.yaml" \
  "$ROOT_DIR/deploy/ccr-auto-update.sh" \
  "$ROOT_DIR/deploy/backup.sh" \
  "$ROOT_DIR/deploy/xiuxian-ccr-auto-update.service" \
  "$ROOT_DIR/deploy/xiuxian-ccr-auto-update.timer" \
  "$ROOT_DIR/deploy/install-caddy.sh" \
  "$ROOT_DIR/deploy/xx.yuohira.com.caddy" \
  "${REMOTE_HOST}:${REMOTE_TMP}/"

printf '==> 安装生产 Compose 与 systemd timer\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "
  set -Eeuo pipefail
  sudo install -d -m 0755 /opt/xiuxian
  sudo install -m 0644 '$REMOTE_TMP/compose.prod.yaml' /opt/xiuxian/compose.prod.yaml
  sudo install -m 0755 '$REMOTE_TMP/ccr-auto-update.sh' /opt/xiuxian/ccr-auto-update.sh
  sudo install -m 0755 '$REMOTE_TMP/backup.sh' /opt/xiuxian/backup.sh
  sudo install -m 0644 '$REMOTE_TMP/xiuxian-ccr-auto-update.service' /etc/systemd/system/xiuxian-ccr-auto-update.service
  sudo install -m 0644 '$REMOTE_TMP/xiuxian-ccr-auto-update.timer' /etc/systemd/system/xiuxian-ccr-auto-update.timer
  sudo systemctl daemon-reload
  sudo rm -f /opt/xiuxian/failed-image.id
  sudo env XIUXIAN_IMAGE='$IMAGE' /opt/xiuxian/ccr-auto-update.sh
  sudo systemctl enable --now xiuxian-ccr-auto-update.timer
"

if [[ "$INSTALL_CADDY" == "1" ]]; then
  printf '==> 校验并安装 Caddy 站点\n'
  ssh -o BatchMode=yes "$REMOTE_HOST" "
    set -Eeuo pipefail
    sudo install -m 0755 '$REMOTE_TMP/install-caddy.sh' /opt/xiuxian/install-caddy.sh
    sudo install -m 0644 '$REMOTE_TMP/xx.yuohira.com.caddy' /opt/xiuxian/xx.yuohira.com.caddy
    sudo /opt/xiuxian/install-caddy.sh /opt/xiuxian/xx.yuohira.com.caddy
  "
fi

printf '==> 核对远端容器和回环健康端点\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "
  set -Eeuo pipefail
  sudo docker inspect --format 'image={{.Config.Image}} id={{.Image}} health={{.State.Health.Status}}' xiuxian-app
  curl --fail --silent --show-error http://127.0.0.1:15128/readyz
  printf '\n'
  sudo systemctl is-enabled xiuxian-ccr-auto-update.timer
  sudo systemctl is-active xiuxian-ccr-auto-update.timer
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
