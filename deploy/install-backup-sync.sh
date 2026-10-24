#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: install-backup-sync.sh
# 主要职责: 在本地计算节点上一键安装备份自动拉取定时服务 (systemd)
#
# 使用方法:
#   sudo bash deploy/install-backup-sync.sh [选项]
#
# 选项:
#   --local-dir DIR     本地备份存储目录（默认 /data/daohua-backups）
#   --remote-host HOST  主服 SSH 主机名/别名（默认 yuohira）
#   --now               安装完成后立即执行一次同步拉取测试
#   -h, --help          显示帮助
# ==============================================================================

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOCAL_DIR="${LOCAL_DIR:-/data/daohua-backups}"
REMOTE_HOST="${REMOTE_HOST:-yuohira}"
RUN_NOW=0

usage() {
  printf '%s\n' \
    '用法: sudo bash deploy/install-backup-sync.sh [选项]' \
    '' \
    '选项:' \
    '  --local-dir DIR     本地备份归档目录 (默认: /data/daohua-backups)' \
    '  --remote-host HOST  主服 SSH 别名 (默认: yuohira)' \
    '  --now               安装后立即触发一次同步' \
    '  -h, --help          显示帮助'
}

while (($# > 0)); do
  case "$1" in
    --local-dir) LOCAL_DIR="${2:?缺少目录参数}"; shift 2 ;;
    --remote-host) REMOTE_HOST="${2:?缺少主机参数}"; shift 2 ;;
    --now) RUN_NOW=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知选项: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ "${EUID}" -ne 0 ]]; then
  printf '错误: 该安装脚本必须以 root (sudo) 权限执行。\n' >&2
  exit 1
fi

command -v rsync >/dev/null 2>&1 || { printf '错误: 未找到 rsync，请先运行 apt install rsync 安装。\n' >&2; exit 1; }
command -v ssh >/dev/null 2>&1 || { printf '错误: 未找到 ssh。\n' >&2; exit 1; }
command -v sha256sum >/dev/null 2>&1 || { printf '错误: 未找到 sha256sum。\n' >&2; exit 1; }

mkdir -p /opt/daohua
cp "${SCRIPT_DIR}/backup-pull-sync.sh" /opt/daohua/backup-pull-sync.sh
chmod +x /opt/daohua/backup-pull-sync.sh

cat >/etc/daohua-backup-sync.env <<EOF
# 道化太初本地计算节点灾备同步环境变量
REMOTE_HOST=${REMOTE_HOST}
REMOTE_DIR=/opt/daohua/backups/
LOCAL_DIR=${LOCAL_DIR}
LOCAL_HOURLY_KEEP_DAYS=90
EOF

cp "${SCRIPT_DIR}/daohua-backup-sync.service" /etc/systemd/system/daohua-backup-sync.service
cp "${SCRIPT_DIR}/daohua-backup-sync.timer" /etc/systemd/system/daohua-backup-sync.timer

systemctl daemon-reload
systemctl enable --now daohua-backup-sync.timer

printf '✔ 已成功安装并启用 daohua-backup-sync.timer 定时器。\n'
printf '  - 定时调度: 每小时第 15 分钟自动拉取\n'
printf '  - 主服源端: %s\n' "$REMOTE_HOST"
printf '  - 本地存储: %s\n' "$LOCAL_DIR"

if (( RUN_NOW == 1 )); then
  printf '==> 立即触发首次同步...\n'
  /opt/daohua/backup-pull-sync.sh
fi
