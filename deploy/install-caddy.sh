#!/usr/bin/env bash
# 保存并恢复主配置、站点、环境文件与 systemd drop-in；绝不重启共享 Caddy。
set -Eeuo pipefail

SOURCE_FILE="${1:-}"
CADDY_FILE="/etc/caddy/Caddyfile"
SITES_DIR="/etc/caddy/sites"
TARGET_FILE="${SITES_DIR}/daohua.caddy"
ENV_FILE="/opt/daohua/daohua.env"
CADDY_SECRETS_FILE="/etc/caddy/daohua-secrets.env"
CADDY_DROPIN_DIR="/etc/systemd/system/caddy.service.d"
CADDY_DROPIN_FILE="${CADDY_DROPIN_DIR}/daohua-env.conf"
IMPORT_LINE='import /etc/caddy/sites/*.caddy'
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

read_env_value() {
  local file="$1" key="$2" raw=""
  [[ -f "$file" ]] || return 1
  raw="$(grep -E "^${key}=" "$file" | tail -n 1 | cut -d= -f2- || true)"
  raw="${raw%$'\r'}"
  raw="${raw#\"}"
  raw="${raw%\"}"
  raw="${raw#\'}"
  raw="${raw%\'}"
  printf '%s' "$raw"
}

exec 9>/run/lock/daohua-install-caddy.lock
flock 9
resolved_token="${DAOHUA_BACKUP_TOKEN:-}"
if [[ -z "$resolved_token" ]]; then
  resolved_token="$(read_env_value "$ENV_FILE" "BACKUP_TOKEN" || true)"
fi
if [[ -z "$resolved_token" ]]; then
  resolved_token="$(read_env_value "$ENV_FILE" "DAOHUA_BACKUP_TOKEN" || true)"
fi
if [[ -z "$resolved_token" ]]; then
  resolved_token="$(read_env_value "$CADDY_SECRETS_FILE" "DAOHUA_BACKUP_TOKEN" || true)"
fi

if [[ -z "$resolved_token" || "${#resolved_token}" -lt 32 ]]; then
  printf '错误: DAOHUA_BACKUP_TOKEN/BACKUP_TOKEN 未设置或长度不足 32 位，拒绝配置 Caddy 备份鉴权路由。\n' >&2
  exit 1
fi
if [[ ! "$resolved_token" =~ ^[A-Za-z0-9._~+/-]+=*$ ]]; then
  printf '错误: DAOHUA_BACKUP_TOKEN 包含非法字符，拒绝写入 Caddy 环境文件。\n' >&2
  exit 1
fi
if [[ "$resolved_token" =~ ^(daohua-backup-token|change-me|replace-me|verify-|secret|password) ]]; then
  printf '错误: DAOHUA_BACKUP_TOKEN 仍使用已废弃泄露值或占位符，拒绝部署。\n' >&2
  exit 1
fi

umask 077
backup_dir="$(mktemp -d /etc/caddy/.daohua-rollback.XXXXXX)"
assets=("$CADDY_FILE" "$TARGET_FILE" "$CADDY_SECRETS_FILE" "$CADDY_DROPIN_FILE")
changed=()
reload_attempted=0
dropin_changed=0
manager_reload_attempted=0
committed=0

validate_caddy() {
  # 错误可能包含环境变量展开值，不输出鉴权令牌。
  caddy validate --config "$CADDY_FILE" >/dev/null 2>&1
}

finish() {
  local rc=$? failed=0 i path
  trap - EXIT
  trap '' HUP INT TERM
  if ((committed)); then return; fi
  if ((${#changed[@]} == 0)); then
    rm -rf -- "$backup_dir"
    exit "$rc"
  fi
  for i in "${changed[@]}"; do
    path="${assets[i]}"
    rm -f -- "$path" || { failed=1; continue; }
    if [[ -e "$backup_dir/old.$i" || -L "$backup_dir/old.$i" ]]; then
      cp -a -- "$backup_dir/old.$i" "$path" || failed=1
    fi
  done
  if ((manager_reload_attempted)); then systemctl daemon-reload || failed=1; fi
  unset DAOHUA_BACKUP_TOKEN
  if [[ -f "$CADDY_SECRETS_FILE" ]]; then
    DAOHUA_BACKUP_TOKEN="$(read_env_value "$CADDY_SECRETS_FILE" DAOHUA_BACKUP_TOKEN)" || failed=1
    export DAOHUA_BACKUP_TOKEN
  fi
  validate_caddy || failed=1
  # 校验失败时从未应用新配置；仅在调用过 reload 后重载已恢复的旧配置。
  if ((reload_attempted && !failed)); then
    timeout 25s systemctl reload caddy >/dev/null 2>&1 || failed=1
  fi
  rm -f -- "$backup_dir"/new.*
  if ((failed)); then
    printf 'Caddy 恢复或复验未通过；未重启共享服务。人工恢复快照: %s\n' "$backup_dir" >&2
  else
    printf 'Caddy 变更失败，已恢复原配置；未重启共享服务。\n' >&2
    rm -rf -- "$backup_dir"
  fi
  exit 1
}
trap finish EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

systemctl is-active --quiet caddy || { printf '共享 Caddy 未运行；拒绝擅自启动/重启。\n' >&2; exit 1; }
for i in "${!assets[@]}"; do
  path="${assets[i]}"
  if [[ -e "$path" || -L "$path" ]]; then
    [[ -f "$path" ]] || { printf 'Caddy 配置不是可读取文件: %s\n' "$path" >&2; exit 1; }
    cp -a -- "$path" "$backup_dir/old.$i"
  fi
done
cp --preserve=mode,ownership,timestamps -- "$CADDY_FILE" "$backup_dir/new.0"
cp -- "$SOURCE_FILE" "$backup_dir/new.1"
caddy fmt --overwrite "$backup_dir/new.1"
printf 'DAOHUA_BACKUP_TOKEN=%s\n' "$resolved_token" >"$backup_dir/new.2"
# 不覆盖共享服务原有 ExecStart；systemd reload 命令也加载 EnvironmentFile。
printf '[Service]\nEnvironmentFile=%s\n' "$CADDY_SECRETS_FILE" >"$backup_dir/new.3"
if ! grep -Fqx "$IMPORT_LINE" "$CADDY_FILE"; then
  printf '\n%s\n' "$IMPORT_LINE" >>"$backup_dir/new.0"
fi
(umask 022; mkdir -p -- "$SITES_DIR" "$CADDY_DROPIN_DIR")
modes=(0 0644 0600 0644)
for i in "${!assets[@]}"; do
  path="${assets[i]}"
  if [[ -f "$path" ]] && cmp -s "$backup_dir/new.$i" "$path" \
      && { [[ "$i" != 2 ]] || [[ "$(stat -c '%a:%u:%g' "$path")" == 600:0:0 && ! -L "$path" ]]; }; then
    continue
  fi
  [[ "$i" != 0 || ! -L "$path" ]] || { printf '主 Caddyfile 是符号链接，请人工配置站点 import。\n' >&2; exit 1; }
  changed+=("$i")
  if [[ "$i" == 3 ]]; then dropin_changed=1; fi
  if [[ "$i" == 0 ]]; then
    mv -fT -- "$backup_dir/new.$i" "$path"
  else
    rm -f -- "$path"
    install -m "${modes[i]}" -o root -g root -- "$backup_dir/new.$i" "$path"
  fi
done
export DAOHUA_BACKUP_TOKEN="$resolved_token"
validate_caddy || { printf 'Caddy 新配置验证失败。\n' >&2; exit 1; }
if ((${#changed[@]} == 0)); then
  rm -rf -- "$backup_dir"
  committed=1
  printf 'Caddy 配置无变更且验证通过，跳过 reload。\n'
  exit 0
fi
if ((dropin_changed)); then
  manager_reload_attempted=1
  systemctl daemon-reload
fi
reload_attempted=1
timeout 25s systemctl reload caddy >/dev/null 2>&1 || { printf 'Caddy reload 失败或超时。\n' >&2; exit 1; }
rm -f -- "$backup_dir"/new.*
committed=1
printf 'Caddy 站点已验证并 reload: %s；原配置快照（root 专用）: %s\n' "$TARGET_FILE" "$backup_dir"
