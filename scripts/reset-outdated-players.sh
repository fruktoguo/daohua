#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: reset-outdated-players.sh
# 主要职责: 本地开发存档版本漂移处置（内测「兼容清零」规则的自动化落地）
#
# 背景:
#   SectionCodec.CurrentSchemaVersion 每次升级后，本地库中旧版本玩家的
#   head/section/flush_queue 载荷会被 LoadAsync 直接拒绝（InvalidDataException），
#   WebSocket 会话反复刷屏报错。
#
# 行为（按优先级）:
#   1. 从 src/Domain/Persistence/SectionCodec.cs 提取当前 CurrentSchemaVersion；
#   2. 仅对本地库（DB_HOST=127.0.0.1/localhost）自动执行，防误指远端；
#   3. 检测到存档行漂移或 platform.schema_version 落后时，先执行
#      migrations/NNNN_vX_to_vY.sql 兼容性迁移链（存档版本升级应在对应
#      版本迁移文件里内嵌 UPDATE schema_version 标签推进）；
#   4. 迁移成功则完成；仍有不可迁移的漂移时，必须经控制台交互确认
#      （输入 RESET）才执行玩家游戏域数据重置，拒绝或无 TTY 一律中止；
#   5. 重置保留 identity.players/accounts 身份行，玩家下次连接自动走新角色初始化。
#
# 退出码:
#   0 = 无漂移 / 迁移成功 / 操作者确认后已重置 / 环境不可用跳过；
#   1 = 迁移或重置 SQL 执行失败；2 = 操作者拒绝或无法交互确认（dev.sh 据此中止启动）。
#
# 使用场景:
#   - scripts/dev.sh 启动流程自动调用；
#   - 手工执行: scripts/reset-outdated-players.sh（可选 --dry-run 只检测不处置）。
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-5432}"
DB_NAME="${DB_NAME:-daohua}"
DB_USER="${DB_USER:-daohua}"
DB_PASSWORD="${DB_PASSWORD:-daohua-local-dev}"
DB_CONTAINER="${DB_CONTAINER:-daohua-local-db}"
DRY_RUN=0
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=1

# 只允许对本地库自动处置；任何非回环地址都视为潜在远端，直接拒绝。
case "$DB_HOST" in
  127.0.0.1|localhost|::1) ;;
  *)
    printf '==> [存档处置] 跳过：DB_HOST=%s 非本地回环地址，拒绝自动处置玩家数据。\n' "$DB_HOST" >&2
    exit 0
    ;;
esac

SECTION_SCHEMA_VERSION="$(sed -n 's/.*CurrentSchemaVersion = \([0-9][0-9]*\).*/\1/p' src/Domain/Persistence/SectionCodec.cs | head -n 1)"
if [[ -z "$SECTION_SCHEMA_VERSION" ]]; then
  printf '==> [存档处置] 跳过：无法从 SectionCodec.cs 提取 CurrentSchemaVersion。\n' >&2
  exit 0
fi

run_psql() {
  if command -v docker >/dev/null 2>&1 \
      && docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$DB_CONTAINER"; then
    docker exec -i "$DB_CONTAINER" psql -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
  elif command -v psql >/dev/null 2>&1; then
    PGPASSWORD="$DB_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 "$@"
  else
    return 127
  fi
}

STALE_TABLES="SELECT player_id FROM game.player_state_heads WHERE schema_version <> ${SECTION_SCHEMA_VERSION}
  UNION ALL
  SELECT player_id FROM game.player_state_sections WHERE schema_version <> ${SECTION_SCHEMA_VERSION}
  UNION ALL
  SELECT player_id FROM platform.section_flush_queue WHERE schema_version <> ${SECTION_SCHEMA_VERSION}"

count_stale() {
  run_psql -tA -c "SELECT CASE
    WHEN to_regclass('game.player_state_heads') IS NULL THEN 0
    ELSE (SELECT COUNT(DISTINCT player_id) FROM (${STALE_TABLES}) stale)
  END;" 2>/dev/null
}

list_stale_players() {
  run_psql -tA <<SQL || true
SELECT p.display_name || ' (' || p.player_id || ')'
FROM identity.players p
WHERE EXISTS (SELECT 1 FROM game.player_state_heads h WHERE h.player_id = p.player_id AND h.schema_version <> ${SECTION_SCHEMA_VERSION})
   OR EXISTS (SELECT 1 FROM game.player_state_sections s WHERE s.player_id = p.player_id AND s.schema_version <> ${SECTION_SCHEMA_VERSION})
   OR EXISTS (SELECT 1 FROM platform.section_flush_queue q WHERE q.player_id = p.player_id AND q.schema_version <> ${SECTION_SCHEMA_VERSION});
SQL
}

STALE_COUNT="$(count_stale)" || STALE_COUNT=""
if [[ -z "${STALE_COUNT}" ]]; then
  printf '==> [存档处置] 跳过：无法连接本地 PostgreSQL（docker 容器 %s 与本机 psql 均不可用）。\n' "$DB_CONTAINER" >&2
  exit 0
fi

# platform.schema_version（库级表结构+存档标签的统一内容版本）落后于
# PostgresSchema.Version 时同样需要走 migrations/ 链补齐。
PLATFORM_REQUIRED="$(grep -oE 'Version = [0-9]+' src/Server/Clean/PostgresSchema.cs | head -n 1 | grep -oE '[0-9]+' || true)"
PLATFORM_CURRENT="$(run_psql -tA -c "SELECT CASE WHEN to_regclass('platform.schema_version') IS NULL THEN 0 ELSE COALESCE((SELECT max(version) FROM platform.schema_version), 0) END;" 2>/dev/null || true)"
[[ "${PLATFORM_CURRENT:-}" =~ ^[0-9]+$ ]] || PLATFORM_CURRENT=0

NEED_MIGRATE=0
if [[ "$STALE_COUNT" != "0" ]]; then
  printf '==> [存档处置] 检测到 %s 名玩家的存档仍为旧 schema 版本（当前要求 v%s）。\n' "$STALE_COUNT" "$SECTION_SCHEMA_VERSION"
  NEED_MIGRATE=1
fi
if [[ -n "$PLATFORM_REQUIRED" && "$PLATFORM_CURRENT" -lt "$PLATFORM_REQUIRED" ]]; then
  printf '==> [存档处置] 本地库 platform schema v%s 落后于代码要求 v%s。\n' "$PLATFORM_CURRENT" "$PLATFORM_REQUIRED"
  NEED_MIGRATE=1
fi
[[ "$NEED_MIGRATE" == "0" ]] && exit 0

if [[ "$DRY_RUN" == "1" ]]; then
  printf '==> [存档处置] --dry-run：将执行 migrations/ 兼容性迁移链。\n'
  scripts/migrate-local-db.sh --dry-run || true
  exit 0
fi

# 优先迁移：执行 migrations/NNNN_vX_to_vY.sql 兼容性迁移链
# （存档版本标签 UPDATE 内嵌在对应版本迁移文件中，随链一并推进）。
if ! scripts/migrate-local-db.sh; then
  printf '错误: [存档处置] 兼容性迁移执行失败，中止启动，请人工排查。\n' >&2
  exit 1
fi

STALE_COUNT="$(count_stale)" || STALE_COUNT=""
if [[ "${STALE_COUNT:-0}" == "0" ]]; then
  printf '==> [存档处置] 迁移完成，存档已升级到 v%s，无需重置。\n' "$SECTION_SCHEMA_VERSION"
  exit 0
fi

printf '==> [存档处置] 迁移后仍有 %s 名玩家存档漂移：\n' "$STALE_COUNT"
list_stale_players

# 交互确认：无 TTY 一律拒绝自动重置并中止启动，禁止无人值守删除玩家数据。
if ! { exec 9<>/dev/tty; } 2>/dev/null; then
  printf '错误: [存档处置] 存在不可迁移的旧版本存档且当前无交互终端，拒绝自动重置。\n' >&2
  printf '      请到终端手工运行 scripts/reset-outdated-players.sh 确认处置。\n' >&2
  exit 2
fi

printf '警告: 以下操作将重置上述 %s 名玩家的游戏域数据（修为/背包/挂单等），仅保留账号与角色名。\n' "$STALE_COUNT" >&9
printf '      输入 RESET 确认重置；其他任意输入取消并中止启动: ' >&9
read -r answer <&9
exec 9>&-
if [[ "$answer" != "RESET" ]]; then
  printf '==> [存档处置] 已取消重置，启动中止。\n'
  exit 2
fi

# 单事务清理 stale 玩家全域数据；保留 identity.players/accounts/auth_sessions。
# 删除口径对齐 src/Bench/Program.cs 的玩家级清理序列，另补市场单与社交子表
# （trades 引用 orders、reservations 引用 asset_instances，必须先子后父）。
run_psql <<SQL
BEGIN;
CREATE TEMP TABLE _stale_players ON COMMIT DROP AS
SELECT DISTINCT player_id FROM (${STALE_TABLES}) s;

DELETE FROM market.trades t WHERE t.buyer_id IN (SELECT player_id FROM _stale_players)
   OR t.seller_id IN (SELECT player_id FROM _stale_players)
   OR t.buy_order_id IN (SELECT order_id FROM market.orders o WHERE o.owner_player_id IN (SELECT player_id FROM _stale_players))
   OR t.sell_order_id IN (SELECT order_id FROM market.orders o WHERE o.owner_player_id IN (SELECT player_id FROM _stale_players));
DELETE FROM market.settlement_claims WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM market.orders WHERE owner_player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM social.party_invitations WHERE invitee_id IN (SELECT player_id FROM _stale_players)
   OR inviter_id IN (SELECT player_id FROM _stale_players)
   OR party_id IN (SELECT party_id FROM social.parties WHERE leader_id IN (SELECT player_id FROM _stale_players));
DELETE FROM social.party_members WHERE player_id IN (SELECT player_id FROM _stale_players)
   OR party_id IN (SELECT party_id FROM social.parties WHERE leader_id IN (SELECT player_id FROM _stale_players));
DELETE FROM social.parties WHERE leader_id IN (SELECT player_id FROM _stale_players);
DELETE FROM social.friendships WHERE player_id IN (SELECT player_id FROM _stale_players)
   OR friend_id IN (SELECT player_id FROM _stale_players);
DELETE FROM social.friend_requests WHERE requester_id IN (SELECT player_id FROM _stale_players)
   OR target_id IN (SELECT player_id FROM _stale_players);
DELETE FROM social.chat_messages WHERE sender_id IN (SELECT player_id FROM _stale_players);
DELETE FROM platform.compute_jobs WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM platform.command_receipts WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM platform.section_flush_queue WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM platform.integration_outbox WHERE aggregate_id IN (SELECT player_id FROM _stale_players);
DELETE FROM economy.reservations WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM economy.transaction_lines WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM economy.transactions WHERE actor_id IN (SELECT player_id FROM _stale_players)
   AND NOT EXISTS (SELECT 1 FROM economy.transaction_lines l WHERE l.transaction_id = economy.transactions.transaction_id);
DELETE FROM economy.player_asset_instances WHERE owner_player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM economy.player_asset_balances WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM game.player_runtime_leases WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM game.player_projection WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM game.player_action_queue WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM game.player_state_sections WHERE player_id IN (SELECT player_id FROM _stale_players);
DELETE FROM game.player_state_heads WHERE player_id IN (SELECT player_id FROM _stale_players);
COMMIT;
SQL

printf '==> [存档处置] 已重置 %s 名玩家的游戏域数据（账号与角色名保留，下次连接自动重建角色）。\n' "$STALE_COUNT"
