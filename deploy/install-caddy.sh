#!/usr/bin/env bash

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
