#!/usr/bin/env bash
# 以两个脚本自建的空库验证一次性身份门禁；要求已编译 Release 测试和已缓存 postgres:18-alpine。
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
unset VERIFY_POSTGRES_EXTERNAL DAOHUA_MARKET_TEST_CONNECTION DAOHUA_POSTGRES_TEST_RUN_ID \
  DAOHUA_DATABASE_URL DAOHUA_COMPUTE_TEST_CONNECTION ConnectionStrings__daohua \
  CAPACITY_REAL_DATABASE_URL CAPACITY_REAL_SERVER_URL DOCKER_HOST DOCKER_CONTEXT
context="$(docker context show | tr -d '\r')"
endpoint="$(docker context inspect "$context" --format '{{(index .Endpoints "docker").Host}}' | tr -d '\r')"
case "$endpoint" in
  unix:///*|npipe:////./pipe/*) export DOCKER_CONTEXT="$context" ;;
  *) printf '隔离冒烟测试仅允许本机 Docker。\n' >&2; exit 1 ;;
esac
docker image inspect postgres:18-alpine --format '{{.Os}}' | grep -q linux
suite='FullyQualifiedName~PostgresSchemaHostedServiceTests.StartAsync_WhenSchemaMatches_CompletesWithoutThrowingEvenIfSectionLabelsStale'
test -f tests/Server.Tests/bin/Release/net10.0/Server.Tests.dll

run_test() {
  dotnet test tests/Server.Tests/Server.Tests.csproj -c Release --no-build --no-restore --filter "$suite"
}

query_owned_db() {
  # VERIFY_PG_CONTAINER 来自同一 Bash 宿主刚成功执行的 docker create；不通过名字或标签查找已有容器。
  [[ "$VERIFY_PG_CONTAINER" =~ ^[0-9a-f]{64}$ ]] || return 1
  docker exec "$VERIFY_PG_CONTAINER" sh -ceu '
    PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -XAtq -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$1"
  ' sh "$1" | tr -d '\r'
}

assert_rejected() {
  local result
  if result="$(run_test 2>&1)"; then
    printf '应拒绝但测试进程返回通过。\n' >&2
    return 1
  fi
  if [[ "$result" != *'拒绝建表：测试库缺少本轮 ready 标识、所有者不匹配或已被另一测试宿主领取。'* ]]; then
    printf '测试失败，但不是预期的库身份拒绝。\n%s\n' "$result" >&2
    return 1
  fi
}

check_unmarked() {
  query_owned_db "COMMENT ON DATABASE daohua_test_${DAOHUA_POSTGRES_TEST_RUN_ID} IS NULL;"
  assert_rejected
  [[ "$(query_owned_db "SELECT to_regnamespace('platform') IS NULL AND to_regnamespace('game') IS NULL AND shobj_description(oid, 'pg_database') IS NULL FROM pg_database WHERE datname=current_database();")" == t ]]
  printf '未标记的空库被拒；无 platform/game schema，数据库标识仍为空。\n'
}

check_second_host() {
  local old new
  run_test
  old="$(query_owned_db "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname=current_database();")"
  [[ "$old" =~ ^daohua-postgres-test-v1:${DAOHUA_POSTGRES_TEST_RUN_ID}:claimed:[0-9a-f]{32}$ ]]
  assert_rejected
  new="$(query_owned_db "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname=current_database();")"
  [[ "$new" == "$old" ]]
  printf '第一宿主通过旧标签回归；第二宿主被拒，原认领标识未变。\n'
}

( source scripts/verify.sh --with-postgres -- check_unmarked )
( source scripts/verify.sh --with-postgres -- check_second_host )
