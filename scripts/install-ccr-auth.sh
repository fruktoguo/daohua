#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: install-ccr-auth.sh
# 主要职责: 远端生产服务器腾讯云 CCR 镜像仓库认证凭据的受控安全安装
#
# 核心操作流程与安全约束:
#   1. 操作确认防护: 强制要求传入 `--confirm` 参数，避免非预期误触；
#   2. 本机凭据解析: 从本机 ~/.docker/config.json 提取指定 Registry 的 Base64 auth 凭据；
#   3. 安全受控注入: 通过 SSH 执行 `docker login --password-stdin` 写入远端 root Docker 配置，
#      禁止在日志、终端或脚本中打印明文凭据；
#   4. 远端拉取验证: 在远端测试 `docker pull` 拉取目标镜像权限；
#   5. 服务与定时器联动: 若远端生产资产已就绪，自动启动并启用 CCR 自动更新服务与 timer；
#   6. 公网闭环烟测: 触发公网 HTTPS/WebSocket 烟测验证站点可用性。
#
# 使用方式:
#   scripts/install-ccr-auth.sh --confirm
# ==============================================================================

set -Eeuo pipefail
set +x


ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REMOTE_HOST="${DAOHUA_SSH_HOST:-yuohira}"
REGISTRY="${DAOHUA_REGISTRY:-ccr.ccs.tencentyun.com}"
IMAGE="${DAOHUA_IMAGE:-ccr.ccs.tencentyun.com/yuohira/daohua:prod}"
DOCKER_CONFIG_FILE="${DOCKER_CONFIG_FILE:-${HOME}/.docker/config.json}"

if [[ "${1:-}" != "--confirm" ]]; then
  printf '%s\n' \
    '该操作会把本机 CCR 登录态安装到远端 root Docker 配置。' \
    '确认已获得明确授权后，请执行：scripts/install-ccr-auth.sh --confirm' >&2
  exit 2
fi
if (($# != 1)); then
  printf '不支持的额外参数。\n' >&2
  exit 2
fi

if [[ ! "$REMOTE_HOST" =~ ^[A-Za-z0-9._-]+$ ]]; then
  printf 'SSH 主机名包含非法字符: %s\n' "$REMOTE_HOST" >&2
  exit 1
fi
if [[ ! "$REGISTRY" =~ ^[A-Za-z0-9.-]+(:[0-9]+)?$ ]]; then
  printf 'Registry 地址包含非法字符: %s\n' "$REGISTRY" >&2
  exit 1
fi
if [[ ! "$IMAGE" =~ ^[A-Za-z0-9._/:@-]+$ ]]; then
  printf '镜像引用包含非法字符: %s\n' "$IMAGE" >&2
  exit 1
fi
for command in jq base64 ssh docker; do
  if ! command -v "$command" >/dev/null 2>&1; then
    printf '缺少命令: %s\n' "$command" >&2
    exit 1
  fi
done
if [[ ! -r "$DOCKER_CONFIG_FILE" ]]; then
  printf '无法读取本机 Docker 配置: %s\n' "$DOCKER_CONFIG_FILE" >&2
  exit 1
fi

encoded_auth="$(jq -er --arg registry "$REGISTRY" '.auths[$registry].auth' "$DOCKER_CONFIG_FILE")"
decoded_auth="$(printf '%s' "$encoded_auth" | base64 --decode)"
if [[ "$decoded_auth" != *:* ]]; then
  printf '本机 CCR auth 格式无效。\n' >&2
  exit 1
fi
username="${decoded_auth%%:*}"
password="${decoded_auth#*:}"
if [[ ! "$username" =~ ^[A-Za-z0-9._@-]+$ || -z "$password" ]]; then
  printf '本机 CCR 用户名或密码格式无效。\n' >&2
  exit 1
fi

printf '==> 停用自动更新，安装远端 CCR 登录态\n'
ssh -o BatchMode=yes "$REMOTE_HOST" 'sudo systemctl disable --now daohua-ccr-auto-update.timer >/dev/null 2>&1 || true'
printf '%s' "$password" | ssh -o BatchMode=yes "$REMOTE_HOST" \
  "sudo docker login --username '$username' --password-stdin '$REGISTRY'"

unset encoded_auth decoded_auth password

printf '==> 验证远端登录配置和真实 CCR 拉取\n'
ssh -o BatchMode=yes "$REMOTE_HOST" "
  set -Eeuo pipefail
  sudo jq -e --arg registry '$REGISTRY' '.auths[\$registry].auth | type == \"string\" and length > 0' /root/.docker/config.json >/dev/null
  sudo docker pull '$IMAGE' >/dev/null
  if sudo systemctl cat daohua-ccr-auto-update.service >/dev/null 2>&1 \
    && sudo test -f /opt/daohua/compose.prod.yaml; then
    sudo systemctl start daohua-ccr-auto-update.service
    sudo systemctl enable --now daohua-ccr-auto-update.timer
    sudo docker inspect --format 'image={{.Config.Image}} id={{.Image}} health={{.State.Health.Status}}' daohua-app
    sudo systemctl is-active daohua-ccr-auto-update.timer
  else
    printf 'CCR 拉取成功；部署资产尚未安装，暂不启用 timer。\n'
  fi
"

if ssh -o BatchMode=yes "$REMOTE_HOST" 'curl --fail --silent --show-error http://127.0.0.1:15128/readyz >/dev/null 2>&1'; then
  printf '==> 公网闭环烟测\n'
  node "$ROOT_DIR/scripts/smoke-production.mjs" https://xx.yuohira.com
fi
printf 'CCR 登录态已安装；存在部署资产时自动更新也已启用。\n'
