#!/usr/bin/env bash
# 本地回归：使用 util-linux flock 和临时锁，Docker/Curl 全部替换为本地桩。
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
real_flock="$(command -v flock)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
mkdir "$work/bin"

cat >"$work/bin/flock" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "$*" in
  '-n 9') exec "$REAL_FLOCK" "$@" ;;
  '-w 30 -E 75 8')
    [[ ! -e "$MAINTENANCE_FLAG_FILE" ]] || { echo '加锁前已创建维护标记' >&2; exit 99; }
    if [[ "$CASE" == error ]]; then exit 71; fi
    # 核对生产脚本的等待上限后，缩短真实锁竞争测试的耗时。
    exec "$REAL_FLOCK" -w 0.2 -E 75 8
    ;;
  *) printf '未预期的 flock 参数：%s\n' "$*" >&2; exit 99 ;;
esac
EOF

cat >"$work/bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf -v old_app 'sha256:%064d' 1
printf -v old_compute 'sha256:%064d' 2
printf -v new_app 'sha256:%064d' 3
printf -v new_compute 'sha256:%064d' 4
printf -v revision '%040d' 1
printf -v pair '%032d' 1
assert_backup_lock() {
  local rc=0
  "$REAL_FLOCK" -n -E 75 "$BACKUP_LOCK_FILE" -c true || rc=$?
  [[ "$rc" == 75 ]] || { echo '执行更新前未持有备份锁' >&2; exit 99; }
}
case "$1" in
  pull) ;;
  image)
    ref="$5"
    case "$ref" in
      "$old_app"|local/app@"$old_app") id="$old_app"; repo=local/app ;;
      "$old_compute"|local/compute@"$old_compute") id="$old_compute"; repo=local/compute ;;
      "$new_app"|local/app:prod|local/app@"$new_app") id="$new_app"; repo=local/app ;;
      "$new_compute"|local/compute:prod|local/compute@"$new_compute") id="$new_compute"; repo=local/compute ;;
      *) exit 99 ;;
    esac
    case "$4" in
      *RepoDigests*) printf '%s\n%s@%s\n' "$id" "$repo" "$id" ;;
      '{{.Id}}') printf '%s\n' "$id" ;;
      *io.daohua.db-schema*) printf '41 38 %s %s %s %s\n' "$revision" "$revision" "$revision" "$pair" ;;
      *) exit 99 ;;
    esac
    ;;
  inspect)
    case "$4" in
      daohua-app) old="$old_app"; new="$new_app" ;;
      daohua-compute) old="$old_compute"; new="$new_compute" ;;
      *) exit 99 ;;
    esac
    case "$3" in
      *State.Running*) printf '%s true\n' "$old" ;;
      *Health.Status*) printf '%s healthy\n' "$new" ;;
      *) exit 99 ;;
    esac
    ;;
  ps) printf 'daohua-app\ndaohua-compute\n' ;;
  exec)
    [[ ! -e "$MAINTENANCE_FLAG_FILE" ]] || exit 99
    assert_backup_lock
    printf 'verify\n' >>"$DEPLOY_DIR/events"
    if [[ "$CASE" == wrong-schema ]]; then echo f; else echo t; fi
    ;;
  compose)
    [[ "${8}" == up && -f "$MAINTENANCE_FLAG_FILE" ]] || exit 99
    assert_backup_lock
    printf 'up\n' >>"$DEPLOY_DIR/events"
    ;;
  *) exit 99 ;;
esac
EOF

cat >"$work/bin/curl" <<'EOF'
#!/usr/bin/env bash
[[ -f "$MAINTENANCE_FLAG_FILE" ]]
EOF
chmod +x "$work/bin/"*

run_case() {
  local scenario="$1" expected="$2" message="$3" dir="$work/$1" rc=0
  mkdir "$dir"
  printf 'APP_IMAGE=local/app@sha256:%064d\nCOMPUTE_IMAGE=local/compute@sha256:%064d\n' 1 2 >"$dir/daohua.env"
  printf '41\n' >"$dir/schema-version.expected"
  printf '38\n' >"$dir/section-version.expected"
  touch "$dir/compose.prod.yaml"
  if [[ "$scenario" == busy ]]; then
    exec 7>"$dir/backup.lock"
    "$real_flock" -n 7
  fi
  env -i PATH="$work/bin:/usr/bin:/bin" REAL_FLOCK="$real_flock" CASE="$scenario" \
    DEPLOY_DIR="$dir" LOCK_FILE="$dir/update.lock" BACKUP_LOCK_FILE="$dir/backup.lock" \
    MAINTENANCE_FLAG_FILE="$dir/maintenance.flag" \
    timeout 5s bash "$root/deploy/ccr-auto-update.sh" 7>&- >"$dir/stdout" 2>"$dir/stderr" || rc=$?
  if [[ "$scenario" == busy ]]; then exec 7>&-; fi
  if [[ "$rc" != "$expected" || -e "$dir/maintenance.flag" || -e "$dir/failed-image.id" ]] ||
    ! grep -F -- "$message" "$dir/stdout" "$dir/stderr" >/dev/null; then
    printf '场景 %s 未通过：退出码 %s（期望 %s）\n' "$scenario" "$rc" "$expected" >&2
    cat "$dir/stdout" "$dir/stderr" >&2
    exit 1
  fi
  case "$scenario" in
    busy|error) [[ ! -e "$dir/events" ]] ;;
    wrong-schema) [[ "$(cat "$dir/events")" == verify ]] ;;
    success) [[ "$(cat "$dir/events")" == $'verify\nup' ]] ;;
  esac
  printf '通过：%s\n' "$scenario"
}

run_case busy 0 '等待共享备份锁超时'
run_case error 71 '获取共享备份锁失败'
run_case wrong-schema 1 '数据库版本或存量分段标签未收敛'
run_case success 0 'prod 镜像对更新成功'
