#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: install-caddy.sh
# 主要职责: 生产 Caddy Web 服务器反向代理站点安全安装、格式化与无缝热重载
#
# 核心操作流程与回滚机制:
#   1. 权限检查: 必须以 root 身份运行；
#   2. 自动备份: 在变更前对主配置 /etc/caddy/Caddyfile 及现有站点配置生成带时间戳备份；
#   3. 站点安装与格式化: 将指定的站点配置文件安装至 /etc/caddy/sites/daohua.caddy 并执行 caddy fmt；
#   4. 主配置 import 确保: 若主 Caddyfile 尚未包含 `import /etc/caddy/sites/*.caddy`，自动追加引入行；
#   5. 严格语法校验: 执行 `caddy validate --config /etc/caddy/Caddyfile`；
#   6. 无缝安全重载: 校验成功后通过 `systemctl reload caddy` 实现平滑热生效；
#   7. 自动故障恢复: 校验或 reload 任意环节失败立即自动恢复至备份配置并重新校验 reload。
#
# 使用方式:
#   /opt/daohua/install-caddy.sh /path/to/daohua.yuohira.com.caddy
# ==============================================================================

set -Eeuo pipefail

SOURCE_FILE="${1:-}"
CADDY_FILE="/etc/caddy/Caddyfile"
SITES_DIR="/etc/caddy/sites"
TARGET_FILE="${SITES_DIR}/daohua.caddy"
IMPORT_LINE='import /etc/caddy/sites/*.caddy'
TIMESTAMP="$(date -u +'%Y%m%dT%H%M%SZ')"
MAIN_BACKUP="${CADDY_FILE}.backup-${TIMESTAMP}"
SITE_BACKUP="${TARGET_FILE}.backup-${TIMESTAMP}"
SITE_EXISTED=0

if [[ "${EUID}" -ne 0 ]]; then
  printf '该脚本必须以 root 运行。\n' >&2
  exit 1
fi
if [[ -z "$SOURCE_FILE" || ! -f "$SOURCE_FILE" ]]; then
  printf '缺少待安装的 Caddy 站点文件。\n' >&2
  exit 1
fi
if [[ ! -f "$CADDY_FILE" ]]; then
  printf '未找到现有 Caddyfile: %s\n' "$CADDY_FILE" >&2
  exit 1
fi

install -d -m 0755 "$SITES_DIR"
cp --preserve=mode,ownership,timestamps "$CADDY_FILE" "$MAIN_BACKUP"
if [[ -f "$TARGET_FILE" ]]; then
  SITE_EXISTED=1
  cp --preserve=mode,ownership,timestamps "$TARGET_FILE" "$SITE_BACKUP"
fi

rollback() {
  cp --preserve=mode,ownership,timestamps "$MAIN_BACKUP" "$CADDY_FILE"
  if [[ "$SITE_EXISTED" == "1" ]]; then
    cp --preserve=mode,ownership,timestamps "$SITE_BACKUP" "$TARGET_FILE"
  else
    rm -f "$TARGET_FILE"
  fi
}

install -m 0644 -o root -g root "$SOURCE_FILE" "$TARGET_FILE"
caddy fmt --overwrite "$TARGET_FILE"
if ! grep -Fqx "$IMPORT_LINE" "$CADDY_FILE"; then
  printf '\n%s\n' "$IMPORT_LINE" >>"$CADDY_FILE"
fi

if ! caddy validate --config "$CADDY_FILE"; then
  printf 'Caddy 配置校验失败，正在恢复原配置。\n' >&2
  rollback
  caddy validate --config "$CADDY_FILE"
  exit 1
fi
if ! systemctl reload caddy; then
  printf 'Caddy reload 失败，正在恢复原配置。\n' >&2
  rollback
  caddy validate --config "$CADDY_FILE"
  systemctl reload caddy
  exit 1
fi

printf 'Caddy 站点已安装: %s\n' "$TARGET_FILE"
printf '主配置备份: %s\n' "$MAIN_BACKUP"
