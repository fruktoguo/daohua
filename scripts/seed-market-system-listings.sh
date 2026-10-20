#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: seed-market-system-listings.sh
# 主要职责: 本地开发库坊市种子——以系统身份为全部可回收物品与装备挂 1 亿份卖单
#   （物品价格=回收价一半；装备 +0/+10/+20 全档=锻造配方材料成本价）。
#
# 硬约束（AGENTS.md §3）:
#   1. 仅作用于本地回环库（daohua-local-db），不涉及生产数据；
#   2. 幂等：系统身份与每张卖单按 assetId 确定性生成 UUID/幂等键，重复执行只为缺失品种补挂；
#   3. 落库事实链完整对齐 PostgresMarketRepository.CommitPlacementAsync
#      （订单 + 库存注入/冻结流水 + 冻结记录），成交/撤单/到期路径与正常挂单完全一致。
#
# 生效方式: 坊市盘口是内存投影（MarketRuntime 启动时从 market.orders 重建），
#   落库后需重启后端（dev-hot 守护会自动拉起）即可上架。
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

DB_CONTAINER="${DB_CONTAINER:-daohua-local-db}"
DB_USER="${DB_USER:-daohua}"
DB_NAME="${DB_NAME:-daohua}"
DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-5432}"
DB_PASSWORD="${DB_PASSWORD:-daohua-local-dev}"
SEED_PARTITION_ID="${MARKET_PARTITION_ID:-0}"
# 系统身份固定 UUID：确定性生成保证脚本幂等，勿与真实注册账号冲突（f 前缀保留段）。
SYSTEM_ACCOUNT_ID="${SYSTEM_ACCOUNT_ID:-f0000000-0000-4000-8000-000000000001}"
SYSTEM_PLAYER_ID="${SYSTEM_PLAYER_ID:-f0000000-0000-4000-8000-000000000002}"
DRY_RUN=0
IF_MISSING=0

usage() {
  cat <<'EOF'
用法: scripts/seed-market-system-listings.sh [--dry-run] [--if-missing]

  --dry-run     只打印将要执行的 SQL，不改动数据库。
  --if-missing  仅当本地库缺失活跃系统卖单时补种；若已有活跃卖单或表未初始化则静默跳过（供启动链自愈使用）。
环境变量（默认值对齐本地 compose.yaml）:
  DB_CONTAINER         本地数据库容器名，默认 daohua-local-db
  DB_USER / DB_NAME    数据库角色与库名，默认 daohua / daohua
  MARKET_PARTITION_ID  市场分片序号，默认 0（须与服务端 Market:PartitionId 一致）

说明:
  - 「可回收物品」= config/game-data/items/*.json 中 sellPrice > 0 的非货币物品；
    灵石（currency.*）为货币本身不挂售。
  - 装备 = config/game-data/equipment/*.json 全部件，每件挂 +0/+10/+20 三档变体，
    统一按锻造配方材料成本价（actions/*.json 中产出的配方 inputs 按物品
    sellPrice×数量求和，equipment.* 投入递归展开）；配方缺失的装备跳过并告警。
  - 挂售价：物品 = sellPrice / 2，装备 = 上述口径；数量 1 亿份，有效期 3650 天。
  - 幂等：重复执行只为缺失品种补挂；已成交/已撤销的系统订单不会自动重挂。
  - 落库后需重启后端重建坊市盘口。
EOF
}

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --if-missing) IF_MISSING=1 ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知参数: %s\n\n' "$arg" >&2; usage >&2; exit 2 ;;
  esac
done

psql_exec() {
  if command -v docker >/dev/null 2>&1 \
      && docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$DB_CONTAINER"; then
    docker exec -i "$DB_CONTAINER" psql -X -q -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME"
  elif command -v psql >/dev/null 2>&1; then
    PGPASSWORD="$DB_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -X -q -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME"
  else
    return 127
  fi
}

psql_scalar() {
  if command -v docker >/dev/null 2>&1 \
      && docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$DB_CONTAINER"; then
    docker exec -i "$DB_CONTAINER" psql -X -t -A -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" -c "$1"
  elif command -v psql >/dev/null 2>&1; then
    PGPASSWORD="$DB_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -X -t -A -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" -c "$1"
  else
    return 127
  fi
}

# 连通性 + 建表状态 + 活跃系统卖单数一次往返取回：启动链每次都要付 Docker exec 的
# 进程与 IPC 开销（Windows 上单次 170~300ms），拆成多次独立探针会白等近一秒。
# 输出口径：`<连通标记>|<表是否已建>|<活跃系统卖单数>`，连通失败时整条为空。
PROBE="$(psql_scalar "SELECT '1|' || (to_regclass('market.orders') IS NOT NULL)::text || '|'
  || CASE WHEN to_regclass('market.orders') IS NULL THEN '0'
          ELSE (SELECT count(*)::text FROM market.orders WHERE idempotency_key LIKE 'system-seed:%' AND status = 'active')
     END;" 2>/dev/null || true)"

if [[ -z "$PROBE" ]]; then
  printf '错误: 无法连接本地 PostgreSQL（docker 容器 %s 与本机 psql 均不可用）。\n' "$DB_CONTAINER" >&2
  exit 1
fi

ORDERS_READY="${PROBE#1|}"
ORDERS_READY="${ORDERS_READY%%|*}"
ACTIVE_SEEDS="${PROBE##*|}"

if [[ "$ORDERS_READY" != "true" ]]; then
  if [[ "$IF_MISSING" == "1" ]]; then
    # 启动自愈场景下若表尚未初始化，静默跳过（等服务端由 PostgresSchema.Sql 直建）
    exit 0
  fi
  printf '错误: 本地库尚未初始化（无 market.orders）。请先启动一次服务端，由 PostgresSchema.Sql 直建 schema。\n' >&2
  exit 1
fi

if [[ "$IF_MISSING" == "1" && "${ACTIVE_SEEDS:-0}" != "0" ]]; then
  # 已有活跃系统卖单，无需重新补种
  exit 0
fi
if [[ "$IF_MISSING" == "1" ]]; then
  printf '==> [坊市种子] 检测到本地库无活跃系统卖单，开始自动补种系统货架...\n'
fi
SQL_FILE="$(mktemp "${TMPDIR:-/tmp}/daohua-market-seed.XXXXXX.sql")"
trap 'rm -f "$SQL_FILE"' EXIT

SEED_PARTITION_ID="$SEED_PARTITION_ID" \
SYSTEM_ACCOUNT_ID="$SYSTEM_ACCOUNT_ID" \
SYSTEM_PLAYER_ID="$SYSTEM_PLAYER_ID" \
node <<'NODE' > "$SQL_FILE"
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const partitionId = parseInt(process.env.SEED_PARTITION_ID || '0', 10);
const accountId = process.env.SYSTEM_ACCOUNT_ID;
const playerId = process.env.SYSTEM_PLAYER_ID;
const systemLogin = 'system-market@daohua.internal';
const systemDisplayName = '系统坊市';
const listingQuantity = 100000000; // 1 亿份
const listingDays = 3650;          // 有效期 10 年，避免到期自动下架

const uuidNamespace = Buffer.from('daohua:market-system-seed:v1', 'utf8');
function deterministicUuid(name) {
  const digest = crypto.createHash('sha256').update(uuidNamespace).update('|').update(name, 'utf8').digest();
  const bytes = Buffer.from(digest.subarray(0, 16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20, 32)}`;
}

// 复刻 PostgresMarketRepository.ComputeMarketRequestHash 的规范化哈希（单行 delta 场景），
// 保证种子流水与运行时流水在幂等冲突校验口径上一致。
function marketRequestHash(assetId, variant, delta) {
  const canonical = `${playerId}\n${playerId}|${assetId}|${variant}|${delta}\n`;
  return crypto.createHash('sha256').update(canonical, 'utf8').digest('hex');
}

const dataDir = path.join('config', 'game-data', 'items');
const items = [];
const sellPriceById = new Map();
for (const file of fs.readdirSync(dataDir).sort()) {
  if (!file.endsWith('.json')) continue;
  const list = JSON.parse(fs.readFileSync(path.join(dataDir, file), 'utf8'));
  for (const item of list) {
    const id = String(item.id ?? '');
    if (typeof item.sellPrice === 'number') sellPriceById.set(id, item.sellPrice);
    if (typeof item.sellPrice === 'number' && item.sellPrice > 0 && !id.startsWith('currency.')) {
      items.push({ id, name: String(item.name ?? id), sellPrice: item.sellPrice });
    }
  }
}
items.sort((left, right) => (left.id < right.id ? -1 : left.id > right.id ? 1 : 0));

if (items.length === 0) {
  console.error('错误: 配表中没有可挂售物品（sellPrice > 0 的非货币物品为空）。');
  process.exit(1);
}

// 装备定价基准 = 锻造材料成本价：扫描全部行动配表，以 yields 产出定位配方；
// inputs 中 item.* 按回收价×数量计、equipment.* 投入递归展开（T2 配方含 T1 装备投入）。
const recipeByProduct = new Map();
const actionsDir = path.join('config', 'game-data', 'actions');
for (const file of fs.readdirSync(actionsDir).sort()) {
  if (!file.endsWith('.json')) continue;
  const list = JSON.parse(fs.readFileSync(path.join(actionsDir, file), 'utf8'));
  for (const action of list) {
    if (!action.inputs || typeof action.inputs !== 'object') continue;
    for (const yieldEntry of action.yields ?? []) {
      if (yieldEntry.kind === 'item' && typeof yieldEntry.id === 'string' && !recipeByProduct.has(yieldEntry.id)) {
        recipeByProduct.set(yieldEntry.id, action.inputs);
      }
    }
  }
}

const maxRecipeDepth = 4; // 配方链最深 T2→T1 两层，超限视为配表成环直接报错
const equipmentCostCache = new Map();
function equipmentMaterialCost(equipmentId, depth) {
  if (depth > maxRecipeDepth) {
    console.error(`错误: 装备配方递归超过深度上限 ${maxRecipeDepth}（疑似成环）：${equipmentId}`);
    process.exit(1);
  }
  if (equipmentCostCache.has(equipmentId)) return equipmentCostCache.get(equipmentId);
  const inputs = recipeByProduct.get(equipmentId);
  if (!inputs) return null;
  let cost = 0;
  for (const [inputId, quantity] of Object.entries(inputs)) {
    const amount = Number(quantity);
    if (!Number.isFinite(amount) || amount <= 0) return null;
    if (inputId.startsWith('equipment.')) {
      const nested = equipmentMaterialCost(inputId, depth + 1);
      if (nested === null) return null;
      cost += nested * amount;
    } else {
      const sellPrice = sellPriceById.get(inputId);
      if (typeof sellPrice !== 'number' || sellPrice <= 0) return null;
      cost += sellPrice * amount;
    }
  }
  equipmentCostCache.set(equipmentId, cost);
  return cost;
}

const equipments = [];
const equipmentDir = path.join('config', 'game-data', 'equipment');
for (const file of fs.readdirSync(equipmentDir).sort()) {
  if (!file.endsWith('.json')) continue;
  const list = JSON.parse(fs.readFileSync(path.join(equipmentDir, file), 'utf8'));
  for (const equipment of list) {
    const id = String(equipment.id ?? '');
    const cost = equipmentMaterialCost(id, 0);
    if (cost === null) {
      console.error(`警告: 装备 ${id} 缺少锻造配方或材料无回收价，跳过挂售。`);
      continue;
    }
    equipments.push({ id, name: String(equipment.name ?? id), cost });
  }
}
equipments.sort((left, right) => (left.id < right.id ? -1 : left.id > right.id ? 1 : 0));

// 装备强化变体挂售档位：+10/+20 与基础件同价（系统货架统一按最低系统报价=材料成本价）。
const enhancedSeedLevels = [10, 20];

// 统一挂售清单：物品价 = 回收价一半、装备全部变体档 = 材料成本价；定价基准写入 SQL 注释备查。
// 基础变体幂等键沿用 system-seed:{assetId}（与历史种子兼容），强化变体追加 :{variant} 后缀，
// 保证重复执行只为缺失变体补挂。
const listings = [
  ...items.map(item => ({ id: item.id, variant: '', name: item.name, price: item.sellPrice / 2, basis: `回收价 ${item.sellPrice} → 挂售价 ${item.sellPrice / 2}` })),
  ...equipments.flatMap(equipment => [
    { id: equipment.id, variant: '', name: equipment.name, price: equipment.cost, basis: `锻造材料成本价 ${equipment.cost}` },
    ...enhancedSeedLevels.map(level => ({
      id: equipment.id, variant: `+${level}`, name: `${equipment.name} +${level}`,
      price: equipment.cost, basis: `锻造材料成本价 ${equipment.cost}（与 +0 同价）`,
    })),
  ]),
];

const esc = (text) => String(text).replace(/'/g, "''");
const lines = [];
lines.push(`-- 由 scripts/seed-market-system-listings.sh 生成：系统身份坊市挂售种子`);
lines.push(`-- 分区 ${partitionId}，挂单 ${listings.length} 条（物品 ${items.length} + 装备 ${equipments.length}×3 档变体），每条 ${listingQuantity} 份`);
lines.push(`-- 挂售价口径：物品 = 回收价 / 2，装备 +0/+10/+20 全档 = 锻造材料成本价`);
lines.push('BEGIN;');
lines.push('');
lines.push('-- 系统身份：不可登录账号 + 货架玩家（固定 UUID 幂等）');
lines.push(`INSERT INTO identity.accounts
    (account_id, login_normalized, password_hash, password_salt, password_iterations, status, created_at, updated_at)
VALUES ('${accountId}', '${systemLogin}',
        sha256(random()::text::bytea), sha256(random()::text::bytea), 100000, 1, now(), now())
ON CONFLICT (account_id) DO NOTHING;`);
lines.push('');
lines.push(`INSERT INTO identity.players
    (player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
VALUES ('${playerId}', '${accountId}', '${esc(systemDisplayName)}', '${esc(systemDisplayName)}', now(), now(), 1)
ON CONFLICT (player_id) DO NOTHING;`);

for (const listing of listings) {
  const price = listing.price;
  const variant = listing.variant;
  // 幂等键后缀：基础变体沿用历史键 system-seed:{assetId}，强化变体追加 :{variant} 互不冲突。
  const keySuffix = variant === '' ? '' : `:${variant}`;
  const uuidSuffix = variant === '' ? '' : `|${variant}`;
  const orderId = deterministicUuid(`order|${listing.id}${uuidSuffix}`);
  const reservationId = deterministicUuid(`reservation|${listing.id}${uuidSuffix}`);
  const grantTxId = deterministicUuid(`grant-tx|${listing.id}${uuidSuffix}`);
  const orderTxId = deterministicUuid(`order-tx|${listing.id}${uuidSuffix}`);
  const grantHash = marketRequestHash(listing.id, variant, listingQuantity);
  const orderHash = marketRequestHash(listing.id, variant, -listingQuantity);
  lines.push('');
  lines.push(`-- ${listing.id} ${listing.name}（${listing.basis}）`);
  lines.push(`-- 库存语义：系统玩家的 1 亿库存全部冻结进卖单（reservation 持有），余额表不落行；
-- 账本记「注入 +1亿 / 冻结 -1亿」两行流水，净额 0 与余额一致；撤单/到期退款按需自动建余额行。
WITH new_order AS (
    INSERT INTO market.orders
        (order_id, partition_id, owner_player_id, side, asset_id, variant, price,
         quantity, filled_quantity, status, version, sequence, idempotency_key,
         created_at, expires_at, updated_at)
    VALUES ('${orderId}', ${partitionId}, '${playerId}', 'sell', '${esc(listing.id)}', '${esc(variant)}', ${price},
            ${listingQuantity}, 0, 'active', 1,
            (SELECT COALESCE(max(sequence), 0) + 1 FROM market.orders WHERE partition_id = ${partitionId}),
            'system-seed:${esc(listing.id)}${esc(keySuffix)}', now(), now() + interval '${listingDays} days', now())
    ON CONFLICT DO NOTHING
    RETURNING order_id, asset_id, variant, quantity
), grant_tx AS (
    INSERT INTO economy.transactions
        (transaction_id, idempotency_key, request_hash, transaction_type, actor_id, created_at)
    SELECT '${grantTxId}', 'system-grant:${esc(listing.id)}${esc(keySuffix)}', '${grantHash}', 'system-grant', '${playerId}', now() FROM new_order
    ON CONFLICT (idempotency_key) DO NOTHING
), grant_lines AS (
    INSERT INTO economy.transaction_lines
        (transaction_id, line_no, player_id, asset_id, variant, delta)
    SELECT '${grantTxId}', 0, '${playerId}', asset_id, variant, quantity FROM new_order
    ON CONFLICT (transaction_id, line_no) DO NOTHING
), order_tx AS (
    INSERT INTO economy.transactions
        (transaction_id, idempotency_key, request_hash, transaction_type, actor_id, created_at)
    SELECT '${orderTxId}', 'market-order:system-seed:${esc(listing.id)}${esc(keySuffix)}', '${orderHash}', 'market', '${playerId}', now() FROM new_order
    ON CONFLICT (idempotency_key) DO NOTHING
), order_lines AS (
    INSERT INTO economy.transaction_lines
        (transaction_id, line_no, player_id, asset_id, variant, delta)
    SELECT '${orderTxId}', 0, '${playerId}', asset_id, variant, -quantity FROM new_order
    ON CONFLICT (transaction_id, line_no) DO NOTHING
), reservation AS (
    INSERT INTO economy.reservations
        (reservation_id, order_id, player_id, asset_id, variant, instance_id, side,
         quantity, reserved_value, status, created_at, updated_at)
    SELECT '${reservationId}', order_id, '${playerId}', asset_id, variant, NULL, 'sell',
           quantity, 0, 'active', now(), now()
    FROM new_order
    ON CONFLICT DO NOTHING
)
SELECT count(*) FROM new_order;`);
}

lines.push('');
lines.push('COMMIT;');
process.stdout.write(lines.join('\n') + '\n');
NODE

if [[ "$DRY_RUN" == "1" ]]; then
  printf -- '---- --dry-run：以下为将执行的 SQL ----\n'
  cat "$SQL_FILE"
  exit 0
fi

printf '正在写入系统身份与 %s 个品种的挂售种子（分区 %s）...\n' \
  "$(grep -c '^WITH new_order' "$SQL_FILE")" "$SEED_PARTITION_ID"
if ! psql_exec < "$SQL_FILE" > /dev/null; then
  printf '错误: 种子 SQL 执行失败，事务已回滚，本地库未变更。\n' >&2
  exit 1
fi

printf '种子落库完成。汇总：\n'
psql_scalar "SELECT '系统卖单(活跃/全部): ' || count(*) FILTER (WHERE status IN ('active','partially_filled')) || '/' || count(*)
    FROM market.orders WHERE idempotency_key LIKE 'system-seed:%';"
psql_scalar "SELECT '系统冻结记录(活跃): ' || count(*) FROM economy.reservations WHERE player_id = '${SYSTEM_PLAYER_ID}' AND status = 'active';"
psql_scalar "SELECT '系统余额行(应为0，库存全部冻结进卖单): ' || count(*)
    FROM economy.player_asset_balances WHERE player_id = '${SYSTEM_PLAYER_ID}';"
psql_scalar "SELECT '样本: ' || asset_id || ' 挂售价 ' || price || ' × ' || quantity
    FROM market.orders WHERE idempotency_key LIKE 'system-seed:%' AND status = 'active'
    ORDER BY asset_id LIMIT 3;"

printf '\n坊市盘口为内存投影：请重启后端（dev-hot 守护会自动拉起）以重建盘口上架。\n'
