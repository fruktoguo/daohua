#!/usr/bin/env bash
# 由 deploy-prod.sh 持共享备份锁执行受控部署，统一管理维护态、停写、迁移与镜像切换。
set -Eeuo pipefail
stage="${1:?}" app="${2:?}" compute="${3:?}" schema="${4:?}" section="${5:?}" caddy="${6:?}" smoke="${7:?}"
digest_re='^[A-Za-z0-9][A-Za-z0-9._/:@-]*@sha256:[a-f0-9]{64}$'
[[ "$app" =~ $digest_re && "$compute" =~ $digest_re ]] || { printf '受控发布仅接受不可变 digest。\n' >&2; exit 1; }
cd /opt/daohua
compose() { docker compose --env-file daohua.env -f compose.prod.yaml --profile worker "$@"; }
image_label() { docker image inspect --format "{{index .Config.Labels \"$2\"}}" "$1"; }
for image in "$app" "$compute"; do
  [[ "$(image_label "$image" io.daohua.db-schema)" == "$schema" && "$(image_label "$image" io.daohua.section-schema)" == "$section" ]] || {
    printf '候选镜像 schema/分段标签与发布源码不一致，拒绝迁移。\n' >&2; exit 1;
  }
done
for label in org.opencontainers.image.revision io.daohua.yuocore-revision io.daohua.private-revision; do
  value="$(image_label "$app" "$label")"
  format='^([0-9a-f]{40}|[0-9a-f]{64})$'
  [[ "$value" =~ $format && "$value" == "$(image_label "$compute" "$label")" ]] || {
    printf '镜像对缺失或不共享构建来源标签: %s\n' "$label" >&2; exit 1;
  }
done
writers=(app compute-node persistence-worker)
maintenance=0
committed=0
env_tmp=""
writers_touched=0
finish() {
  local rc=$?
  trap - EXIT
  trap '' HUP INT TERM
  if ((maintenance && !committed)); then
    touch maintenance.flag || printf '无法创建维护标记，请立即人工处理。\n' >&2
    if ((writers_touched)); then
      compose stop "${writers[@]}" || printf '无法确认写者已停止，请立即人工处理。\n' >&2
    fi
    printf '部署失败：保留维护态，不回拉旧镜像；需核对数据库版本后前向修复或另行授权恢复。\n' >&2
  fi
  if [[ -n "$env_tmp" ]]; then rm -f -- "$env_tmp"; fi
  exit "$rc"
}
trap finish EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

# 容器换代与定时备份互斥：先持锁（带超时）再切入维护态，避免备份卡死或锁超时把线上无故留在维护页。
exec 8>/run/lock/daohua-db-backup.lock
backup_lock_timeout="${RELEASE_BACKUP_LOCK_TIMEOUT:-300}"
flock -w "$backup_lock_timeout" -E 75 8 || {
  rc=$?
  if ((rc == 75)); then
    printf '等待共享备份锁超时（%s 秒），发布中止（尚未进入维护态）；请核对备份任务后重试。\n' "$backup_lock_timeout" >&2
    exit 1
  fi
  printf '获取共享备份锁失败（flock 退出码 %s），发布中止。\n' "$rc" >&2
  exit "$rc"
}

maintenance=1
touch maintenance.flag
# 若主机仍残留已下线的自动更新 / 连续 WAL 定时单元，顺带停用并清理单元文件。
systemctl disable --now daohua-ccr-auto-update.timer daohua-wal-archive.timer >/dev/null 2>&1 || true
rm -f /etc/systemd/system/daohua-ccr-auto-update.service /etc/systemd/system/daohua-ccr-auto-update.timer \
  /etc/systemd/system/daohua-wal-archive.service /etc/systemd/system/daohua-wal-archive.timer \
  /opt/daohua/ccr-auto-update.sh /opt/daohua/wal-archive.sh /opt/daohua/postgres-wal-archive.sh

install -d -m 0755 /opt/daohua
install -m 0644 "$stage/compose.prod.yaml" /opt/daohua/compose.prod.yaml
install -d -m 0755 /opt/daohua/static /opt/daohua/static/status
install -m 0644 "$stage/privacy.html" /opt/daohua/static/privacy.html
cp -f "$stage"/status/* /opt/daohua/static/status/
chmod 0644 /opt/daohua/static/status/*
install -m 0644 "$stage/backup-checks.sh" /opt/daohua/backup-checks.sh
install -m 0644 "$stage/backup-access.sh" /opt/daohua/backup-access.sh
install -m 0755 "$stage/backup.sh" /opt/daohua/backup.sh
install -m 0755 "$stage/base-backup.sh" /opt/daohua/base-backup.sh
install -m 0755 "$stage/pitr-restore.sh" /opt/daohua/pitr-restore.sh
install -m 0755 "$stage/verify-backup.sh" /opt/daohua/verify-backup.sh
install -m 0755 "$stage/market-ledger-archive.sh" /opt/daohua/market-ledger-archive.sh
install -m 0755 "$stage/migrate-prod-db.sh" /opt/daohua/migrate-prod-db.sh
rm -rf /opt/daohua/migrations
install -d -m 0755 /opt/daohua/migrations
cp -f "$stage"/migrations/*.sql /opt/daohua/migrations/
install -m 0644 "$stage/daohua-db-backup.service" /etc/systemd/system/daohua-db-backup.service
install -m 0644 "$stage/daohua-db-backup.timer" /etc/systemd/system/daohua-db-backup.timer
install -m 0644 "$stage/daohua-base-backup.service" /etc/systemd/system/daohua-base-backup.service
install -m 0644 "$stage/daohua-base-backup.timer" /etc/systemd/system/daohua-base-backup.timer
install -m 0644 "$stage/daohua-ledger-archive.service" /etc/systemd/system/daohua-ledger-archive.service
install -m 0644 "$stage/daohua-ledger-archive.timer" /etc/systemd/system/daohua-ledger-archive.timer
systemctl daemon-reload
rm -f /opt/daohua/failed-image.id
if [[ "$caddy" == 1 ]]; then
  install -m 0755 "$stage/install-caddy.sh" ./install-caddy.sh
  ./install-caddy.sh "$stage/daohua.yuohira.com.caddy"
fi

# 读者组是主服显式配置；不从 Caddy 用户名、现有 GID 或 SSH 账号猜测授权。
(
  source ./daohua.env
  source ./backup-access.sh
  umask 077
  backup_prepare_access "${BACKUP_DIR:-/opt/daohua/backups}"
)
running="$(docker ps --format '{{.Names}}')"
if grep -Fx 'daohua-persistence-worker' <<<"$running" >/dev/null; then touch release-worker.expected; fi
writers_touched=1
compose stop "${writers[@]}"
compose up -d --no-deps --wait --wait-timeout 60 db
EXPECTED_SCHEMA_VERSION="$schema" EXPECTED_SECTION_VERSION="$section" ./migrate-prod-db.sh --no-stop-writers

services=(app compute-node)
if [[ -f release-worker.expected ]]; then services+=(persistence-worker); fi
APP_IMAGE="$app" COMPUTE_IMAGE="$compute" compose up --detach --no-deps --pull never --force-recreate --wait --wait-timeout 180 "${services[@]}"
app_id="$(docker image inspect --format '{{.Id}}' "$app")"
compute_id="$(docker image inspect --format '{{.Id}}' "$compute")"
[[ "$(docker inspect --format '{{.Image}} {{.State.Health.Status}}' daohua-app)" == "$app_id healthy" ]]
[[ "$(docker inspect --format '{{.Image}} {{.State.Health.Status}}' daohua-compute)" == "$compute_id healthy" ]]
if [[ -f release-worker.expected ]]; then
  [[ "$(docker inspect --format '{{.Image}} {{.State.Health.Status}}' daohua-persistence-worker)" == "$app_id healthy" ]]
fi
curl --max-time 10 --fail --silent --show-error http://127.0.0.1:15128/readyz >/dev/null
if [[ "$smoke" == 1 ]]; then
  # 使用部署者上传的临时凭据文件，不写入发布配置或日志。
  (set -a; source "$stage/smoke.env"; set +a; node "$stage/smoke-production.mjs" http://127.0.0.1:15128)
fi

env_tmp="$(mktemp ./daohua.env.release.XXXXXX)"
cp -p -- daohua.env "$env_tmp"
sed -i -E '/^(APP_IMAGE|COMPUTE_IMAGE)=/d' "$env_tmp"
printf '\nAPP_IMAGE=%s\nCOMPUTE_IMAGE=%s\n' "$app" "$compute" >> "$env_tmp"
mv -f -- "$env_tmp" daohua.env
printf '%s\n' "$schema" > schema-version.expected
printf '%s\n' "$section" > section-version.expected
systemctl enable --now daohua-db-backup.timer daohua-base-backup.timer daohua-ledger-archive.timer
rm -f -- maintenance.flag
if [[ "$smoke" == 1 ]]; then
  # 用公网域名检验网关；失败时 EXIT handler 恢复维护并停写，锁尚未释放。
  (set -a; source "$stage/smoke.env"; set +a; SMOKE_REQUIRE_READYZ=0 node "$stage/smoke-production.mjs" https://daohua.yuohira.com)
fi
rm -f -- release-worker.expected
committed=1
