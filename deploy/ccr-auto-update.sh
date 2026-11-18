#!/usr/bin/env bash
# 每轮检查 CCR 的 prod 镜像对；只按固定 digest 换代，不迁移数据或重建数据库。
set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
FAILED_IMAGE_FILE="${FAILED_IMAGE_FILE:-${DEPLOY_DIR}/failed-image.id}"
MAINTENANCE_FLAG_FILE="${MAINTENANCE_FLAG_FILE:-${DEPLOY_DIR}/maintenance.flag}"
LOCK_FILE="${LOCK_FILE:-/run/lock/daohua-ccr-auto-update.lock}"

# 与受控发布互斥，从一致的部署配置读取仓库与数据库连接目标。
exec 9>"$LOCK_FILE"
if ! flock -n 9; then printf '已有更新任务，跳过本轮。\n'; exit 0; fi
if [[ -e "$MAINTENANCE_FLAG_FILE" ]]; then printf '维护态未解除，跳过自动更新。\n'; exit 0; fi
[[ -f "$ENV_FILE" && -f "$COMPOSE_FILE" ]] || { printf '缺少已部署的环境文件或 Compose。\n' >&2; exit 1; }
set -a; source "$ENV_FILE"; set +a

repository() {
  local ref="$1" repo
  [[ "$ref" =~ ^([A-Za-z0-9][A-Za-z0-9._:/-]*)@sha256:[a-f0-9]{64}$ ]] || return 1
  repo="${BASH_REMATCH[1]}"
  if [[ "${repo##*/}" == *:* ]]; then repo="${repo%:*}"; fi
  printf '%s\n' "$repo"
}
if ! app_repo="$(repository "${APP_IMAGE:-}")" || ! compute_repo="$(repository "${COMPUTE_IMAGE:-}")" || [[ "$app_repo" == "$compute_repo" ]]; then
  printf '缺少已验收的 Server/ComputeNode digest 仓库锚点，请先走受控发布。\n' >&2; exit 1
fi

# prod 只 inspect 一次，取得同一镜像对象的配置 ID 和仓库 digest；后续绝不再使用移动引用。
pin_image() {
  local source="$1" repo="$2" entries id ref
  entries="$(docker image inspect --format '{{.Id}}{{range .RepoDigests}}{{println}}{{.}}{{end}}' "$source")" || return 1
  id="${entries%%$'\n'*}"
  [[ "$id" =~ ^sha256:[a-f0-9]{64}$ && "$entries" == *$'\n'* ]] || return 1
  while IFS= read -r ref; do
    if [[ "$ref" == "$repo@"* && "${ref##*@}" =~ ^sha256:[a-f0-9]{64}$ ]]; then
      # 固定引用还必须在本地指向刚才的 ID；Compose 禁止再 pull。
      [[ "$(docker image inspect --format '{{.Id}}' "$ref")" == "$id" ]] || return 1
      printf '%s %s\n' "$id" "$ref"
      return 0
    fi
  done <<< "${entries#*$'\n'}"
  printf '本地镜像缺少匹配的仓库 digest: %s\n' "$repo" >&2
  return 1
}

docker pull "$app_repo:prod" >/dev/null
docker pull "$compute_repo:prod" >/dev/null
app_pin="$(pin_image "$app_repo:prod" "$app_repo")"
compute_pin="$(pin_image "$compute_repo:prod" "$compute_repo")"
read -r candidate_app_id candidate_app_ref <<< "$app_pin"
read -r candidate_compute_id candidate_compute_ref <<< "$compute_pin"

running_image() {
  local state
  state="$(docker inspect --format '{{.Image}} {{.State.Running}}' "$1")" || return 1
  [[ "$state" =~ ^(sha256:[a-f0-9]{64})\ true$ ]] || return 1
  printf '%s\n' "${BASH_REMATCH[1]}"
}
if ! running_app_id="$(running_image daohua-app)" || ! running_compute_id="$(running_image daohua-compute)"; then
  printf '未找到完整运行中的镜像对，不能自动换代，请走受控发布。\n' >&2; exit 1
fi
if [[ "$candidate_app_id" == "$running_app_id" && "$candidate_compute_id" == "$running_compute_id" ]]; then
  printf 'prod 镜像对 ID 未变化，不重建容器。\n'; exit 0
fi
candidate_marker="$candidate_app_id|$candidate_compute_id"
if [[ -f "$FAILED_IMAGE_FILE" && "$(<"$FAILED_IMAGE_FILE")" == "$candidate_marker" ]]; then
  printf '此 prod 镜像对此前失败，不自动重试。\n'; exit 0
fi

if [[ ! -f "$DEPLOY_DIR/schema-version.expected" || ! -f "$DEPLOY_DIR/section-version.expected" ]]; then
  printf '缺少已验收的 schema/分段标签，需先走受控发布。\n' >&2; exit 1
fi
expected_schema="$(<"$DEPLOY_DIR/schema-version.expected")"
expected_section="$(<"$DEPLOY_DIR/section-version.expected")"
[[ "$expected_schema" =~ ^[1-9][0-9]{0,8}$ && "$expected_section" =~ ^[1-9][0-9]{0,8}$ ]] || {
  printf '已验收 schema/分段标签格式错误。\n' >&2; exit 1;
}

pair_metadata() {
  local app compute schema section main yuo private pair
  local format='{{index .Config.Labels "io.daohua.db-schema"}} {{index .Config.Labels "io.daohua.section-schema"}} {{index .Config.Labels "org.opencontainers.image.revision"}} {{index .Config.Labels "io.daohua.yuocore-revision"}} {{index .Config.Labels "io.daohua.private-revision"}} {{index .Config.Labels "io.daohua.build-pair"}}'
  app="$(docker image inspect --format "$format" "$1")" || return 1
  compute="$(docker image inspect --format "$format" "$2")" || return 1
  [[ "$app" == "$compute" ]] || return 1
  read -r schema section main yuo private pair <<< "$app"
  local revision_re='^([a-f0-9]{40}|[a-f0-9]{64})$'
  [[ "$schema" == "$expected_schema" && "$section" == "$expected_section" && "$main" =~ $revision_re &&
     "$yuo" =~ $revision_re && "$private" =~ $revision_re && "$pair" =~ ^[a-f0-9]{32}$ ]]
}
if ! pair_metadata "$candidate_app_id" "$candidate_compute_id" || ! pair_metadata "$running_app_id" "$running_compute_id"; then
  printf '候选或回滚镜像未成对、缺少来源标签或不属于已验收 schema/分段版本；不自动换代。跨版本请走受控发布。\n' >&2
  exit 1
fi
rollback_app_pin="$(pin_image "$running_app_id" "$app_repo")"
rollback_compute_pin="$(pin_image "$running_compute_id" "$compute_repo")"
[[ "${rollback_app_pin%% *}" == "$running_app_id" && "${rollback_compute_pin%% *}" == "$running_compute_id" ]]

# 检查 DB 版本和三处实际存档标签。查询异常/超时、空 schema 和旧/空标签均拒绝。
verify_database() {
  local result
  result="$(docker exec -e PGOPTIONS=-cstatement_timeout=15000 "${DB_CONTAINER_NAME:-daohua-db}" \
    psql -XAt -v ON_ERROR_STOP=1 -U "${DB_BOOTSTRAP_USER:-daohua_bootstrap}" -d "${DB_NAME:-daohua}" -c "
      SELECT ((SELECT max(version) FROM platform.schema_version) = $expected_schema) IS TRUE
         AND NOT EXISTS (SELECT 1 FROM game.player_state_heads WHERE schema_version IS DISTINCT FROM $expected_section)
         AND NOT EXISTS (SELECT 1 FROM game.player_state_sections WHERE schema_version IS DISTINCT FROM $expected_section)
         AND NOT EXISTS (SELECT 1 FROM platform.section_flush_queue WHERE schema_version IS DISTINCT FROM $expected_section);")" || return 1
  [[ "$result" == t ]]
}

compose() { docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" --profile worker "$@"; }
services=(app compute-node)
running_names="$(docker ps --format '{{.Names}}')"
if grep -Fx daohua-persistence-worker <<< "$running_names" >/dev/null; then
  [[ "$(running_image daohua-persistence-worker)" == "$running_app_id" ]] || {
    printf '持久化 worker 未与上一镜像对收敛，不自动换代。\n' >&2; exit 1;
  }
  services+=(persistence-worker)
fi

deploy_pair() {
  local app="$1" compute="$2" app_id="$3" compute_id="$4" i
  APP_IMAGE="$app" COMPUTE_IMAGE="$compute" compose up --detach --no-deps --pull never --force-recreate \
    --wait --wait-timeout 180 "${services[@]}" || return 1
  for i in {1..30}; do
    if curl --max-time 2 --fail --silent http://127.0.0.1:15128/readyz >/dev/null; then break; fi
    if ((i == 30)); then return 1; fi
    sleep 2
  done
  [[ "$(docker inspect --format '{{.Image}} {{.State.Health.Status}}' daohua-app)" == "$app_id healthy" ]] || return 1
  [[ "$(docker inspect --format '{{.Image}} {{.State.Health.Status}}' daohua-compute)" == "$compute_id healthy" ]] || return 1
  if ((${#services[@]} == 3)); then [[ "$(running_image daohua-persistence-worker)" == "$app_id" ]] || return 1; fi
}

# 容器换代与备份互斥；任何失败或信号都不能静默解除维护态。
exec 8>"${BACKUP_LOCK_FILE:-/run/lock/daohua-db-backup.lock}"
flock -w 30 -E 75 8 || {
  rc=$?
  if ((rc == 75)); then
    printf '等待共享备份锁超时（30 秒），跳过本轮自动更新。\n'
    exit 0
  fi
  printf '获取共享备份锁失败（flock 退出码 %s），拒绝自动换代。\n' "$rc" >&2
  exit "$rc"
}
verify_database || { printf '数据库版本或存量分段标签未收敛，拒绝自动换代。\n' >&2; exit 1; }
healthy=0
finish() {
  local rc=$?
  trap - EXIT
  trap '' HUP INT TERM
  if ((healthy)); then
    rm -f -- "$MAINTENANCE_FLAG_FILE" || exit 1
  else
    compose stop "${services[@]}" || printf '无法确认写者已停止，需要人工处理。\n' >&2
    printf '保留维护态，需人工处理。\n' >&2
  fi
  exit "$rc"
}
touch "$MAINTENANCE_FLAG_FILE"
trap finish EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

if deploy_pair "$candidate_app_ref" "$candidate_compute_ref" "$candidate_app_id" "$candidate_compute_id"; then
  rm -f -- "$FAILED_IMAGE_FILE"
  healthy=1
  printf 'prod 镜像对更新成功: %s\n' "$candidate_marker"
  exit 0
fi

printf '%s\n' "$candidate_marker" >"$FAILED_IMAGE_FILE"
printf '新镜像健康或 ID 检查失败，先停写再复验数据库；仅允许同版本回滚。\n' >&2
compose stop "${services[@]}"
verify_database || { printf '数据库版本或分段标签已变化，禁止镜像回滚。\n' >&2; exit 1; }
if ! deploy_pair "${rollback_app_pin#* }" "${rollback_compute_pin#* }" "$running_app_id" "$running_compute_id"; then
  printf '同版本镜像回滚未收敛。\n' >&2; exit 1
fi
healthy=1
printf '已恢复到健康的上一镜像对；失败候选不会自动重试。\n' >&2
exit 1
