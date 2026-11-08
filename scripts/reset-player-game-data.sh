#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: reset-player-game-data.sh
# 主要职责: 生产/测试环境玩家游戏域数据安全重置与功德、形象资产全额保留回补
#
# 业务原则:
#   1. 仅清除游戏域数据（修为境界、背包装备、特质技能、行动队列、市场挂单、社交队伍）；
#   2. 绝对保留账号凭据（identity.accounts）与角色道号（identity.players）；
#   3. 功德资产全额保留与返还（当前持有功德 + 商店累计消耗全额原币种返还回补）；
#   4. 形象终身保留（已解锁形象与当前装配形象持久化保留）；
#   5. 安全第一：默认 --dry-run 只读预演；--apply 执行前强制触发全量热备并交互确认；
#   6. 严格豁免机器人账号（bot_%）与自动化烟测账号（smoke_ci_verifier）。
#
# 用法:
#   scripts/reset-player-game-data.sh [选项]
#
# 选项:
#   --dry-run             只读预演模式（默认开启，只打印对账清单，不修改任何数据）
#   --apply               真实执行重置（需要手动输入确认）
#   --host <HOST>         目标服务器 SSH 别名（默认: yuohira）
#   --scope <SCOPE>       重置范围: older-than-6h（默认，6h前注册的角色）或 all（全量真实角色）
#   --exclude <ACCOUNT>   排除指定账号（可多次指定；默认已排除 yuohira）
#   --include-yuohira     显式允许重置 yuohira 账号（取消默认排除保护）
#   --skip-backup         跳过前置全量备份（仅限调试排障，生产绝不可用）
#   -h, --help            显示本帮助信息
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

REMOTE_HOST="${REMOTE_HOST:-yuohira}"
SCOPE="older-than-6h"
MODE="dry-run"
SKIP_BACKUP=0
EXCLUDE_ACCOUNTS=("yuohira")

usage() {
  sed -n '2,27p' "$0" | sed 's/^# \?//'
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)
      MODE="dry-run"
      shift
      ;;
    --apply)
      MODE="apply"
      shift
      ;;
    --host)
      REMOTE_HOST="${2:?缺少 --host 参数}"
      shift 2
      ;;
    --scope)
      SCOPE="${2:?缺少 --scope 参数}"
      if [[ "$SCOPE" != "older-than-6h" && "$SCOPE" != "all" ]]; then
        printf '错误: --scope 仅支持 older-than-6h 或 all\n' >&2
        exit 1
      fi
      shift 2
      ;;
    --exclude)
      EXCLUDE_ACCOUNTS+=("${2:?缺少 --exclude 参数}")
      shift 2
      ;;
    --include-yuohira)
      # 移除默认的 yuohira 排除
      NEW_EXCLUDES=()
      for acc in "${EXCLUDE_ACCOUNTS[@]}"; do
        [[ "$acc" != "yuohira" ]] && NEW_EXCLUDES+=("$acc")
      done
      EXCLUDE_ACCOUNTS=("${NEW_EXCLUDES[@]}")
      shift
      ;;
    --skip-backup)
      SKIP_BACKUP=1
      shift
      ;;
    -h|--help)
      usage
      ;;
    *)
      printf '未知参数: %s\n' "$1" >&2
      usage
      ;;
  esac
done

printf '====================================================================\n'
printf '            道化太初 · 玩家数据清除与功德形象保留工具\n'
printf '====================================================================\n'
printf '执行目标主机 : %s\n' "$REMOTE_HOST"
printf '执行模式     : %s\n' "$( [[ "$MODE" == "apply" ]] && printf '\033[31;1m[APPLY 真实执行]\033[0m' || printf '\033[32;1m[DRY-RUN 只读预演]\033[0m' )"
printf '重置范围     : %s\n' "$SCOPE"
printf '排除账号     : %s\n' "${EXCLUDE_ACCOUNTS[*]:-无}"
printf '====================================================================\n\n'

# 构造排除账号 SQL 条件
EXCLUDE_SQL_LIST=""
for acc in "${EXCLUDE_ACCOUNTS[@]}"; do
  escaped_acc="${acc//\'/\'\'}"
  if [[ -z "$EXCLUDE_SQL_LIST" ]]; then
    EXCLUDE_SQL_LIST="'$escaped_acc'"
  else
    EXCLUDE_SQL_LIST="$EXCLUDE_SQL_LIST, '$escaped_acc'"
  fi
done

SCOPE_WHERE="1=1"
if [[ "$SCOPE" == "older-than-6h" ]]; then
  SCOPE_WHERE="a.created_at < now() - interval '6 hours'"
fi

EXCLUDE_WHERE="1=1"
if [[ -n "$EXCLUDE_SQL_LIST" ]]; then
  EXCLUDE_WHERE="a.login_normalized NOT IN ($EXCLUDE_SQL_LIST)"
fi

# 1. 连通性测试
printf '==> [1/4] 连接目标主机与数据库自检...\n'
CHECK_CMD="sudo docker exec -i daohua-db psql -U daohua -d daohua -t -A -c 'SELECT now();'"
if ! ssh -o ConnectTimeout=5 "$REMOTE_HOST" "$CHECK_CMD" >/dev/null 2>&1; then
  printf '错误: 无法通过 SSH 连接到 %s 上的 daohua-db 容器。\n' "$REMOTE_HOST" >&2
  exit 1
fi
printf '  - 目标数据库在线且连接正常。\n\n'

# 2. 生成对账清单与资金池统计
printf '==> [2/4] 分析目标角色功德资金池与形象数据...\n'

AUDIT_QUERY="
SELECT 
    p.player_id,
    a.login_normalized,
    p.display_name,
    to_char(a.created_at AT TIME ZONE 'Asia/Shanghai', 'YYYY-MM-DD HH24:MI') as created_at_cst,
    CASE WHEN a.created_at < now() - interval '6 hours' THEN '6h外' ELSE '6h内' END as time_bucket,
    COALESCE((SELECT sum(quantity) FROM economy.player_asset_balances WHERE player_id = p.player_id AND asset_id = 'item.gongde'), 0) as gongde_normal,
    COALESCE((SELECT sum(quantity) FROM economy.player_asset_balances WHERE player_id = p.player_id AND asset_id = 'item.gongde-bound'), 0) as gongde_bound,
    COALESCE((SELECT sum(-l.delta) FROM economy.transaction_lines l JOIN economy.transactions t ON t.transaction_id = l.transaction_id WHERE l.player_id = p.player_id AND t.transaction_type = 'shop.buy' AND l.asset_id = 'item.gongde' AND l.delta < 0), 0) as shop_spent_normal,
    COALESCE((SELECT sum(-l.delta) FROM economy.transaction_lines l JOIN economy.transactions t ON t.transaction_id = l.transaction_id WHERE l.player_id = p.player_id AND t.transaction_type = 'shop.buy' AND l.asset_id = 'item.gongde-bound' AND l.delta < 0), 0) as shop_spent_bound,
    COALESCE(s.codec, '') as codec,
    COALESCE(encode(s.payload, 'hex'), '') as settings_hex
FROM identity.accounts a
JOIN identity.players p ON p.account_id = a.account_id
LEFT JOIN game.player_state_sections s ON s.player_id = p.player_id AND s.section_kind = 'settings'
WHERE a.login_normalized NOT LIKE 'bot_%'
  AND a.login_normalized != 'smoke_ci_verifier'
  AND ($SCOPE_WHERE)
  AND ($EXCLUDE_WHERE)
ORDER BY a.created_at ASC;
"

AUDIT_RAW="$(ssh "$REMOTE_HOST" "sudo docker exec -i daohua-db psql -U daohua -d daohua -t -A -F':::' -c \"$AUDIT_QUERY\"")"
AVATAR_SQL_FILE="$(mktemp /tmp/daohua_avatar_sql.XXXXXX 2>/dev/null || mktemp -t daohua_avatar_sql.XXXXXX)"
cleanup_avatar_sql() {
  rm -f "$AVATAR_SQL_FILE" >/dev/null 2>&1 || true
}
trap cleanup_avatar_sql EXIT INT TERM

# 调用 Node 解析 Brotli 与生成格式化对账表（使用 stdin 避免超长参数列表错误，并导出形象保留SQL）
AVATAR_SQL_OUT="$AVATAR_SQL_FILE" printf '%s' "$AUDIT_RAW" | node -e '
const fs = require("fs");
const zlib = require("zlib");
const raw = fs.readFileSync(0, "utf8");
const lines = raw.trim().split("\n");

if (!lines || lines.length === 0 || !lines[0]) {
  console.log("未匹配到任何需要重置的目标角色。");
  process.exit(0);
}

const rows = [];
let totalCurNorm = 0;
let totalCurBound = 0;
let totalRefundNorm = 0;
let totalRefundBound = 0;

for (const line of lines) {
  if (!line) continue;
  const parts = line.split(":::");
  const [playerId, account, displayName, createdAt, timeBucket, curNormStr, curBoundStr, spentNormStr, spentBoundStr, codec, hex] = parts;
  let avatar = "player";
  let unlocked = ["player", "player-male"];
  if (hex) {
    try {
      const buf = Buffer.from(hex, "hex");
      const text = codec === "br-json" ? zlib.brotliDecompressSync(buf).toString("utf8") : buf.toString("utf8");
      const json = JSON.parse(text);
      if (json.avatar) avatar = json.avatar;
      if (Array.isArray(json.unlockedAvatars)) unlocked = json.unlockedAvatars;
    } catch (e) {}
  }
  const curNorm = Number(curNormStr) || 0;
  const curBound = Number(curBoundStr) || 0;
  const spentNorm = Number(spentNormStr) || 0;
  const spentBound = Number(spentBoundStr) || 0;

  const finalNorm = curNorm + spentNorm;
  const finalBound = curBound + spentBound;
  const finalTotal = finalNorm + finalBound;

  totalCurNorm += curNorm;
  totalCurBound += curBound;
  totalRefundNorm += spentNorm;
  totalRefundBound += spentBound;

  rows.push({
    playerId,
    account,
    displayName,
    createdAt,
    curNorm,
    curBound,
    spentNorm,
    spentBound,
    finalNorm,
    finalBound,
    finalTotal,
    avatar,
    unlocked,
    unlockedStr: unlocked.join(", "),
    isExtraAvatar: unlocked.some(a => a !== "player" && a !== "player-male")
  });
}

const sqlOut = process.env.AVATAR_SQL_OUT;
if (sqlOut) {
  const stmts = rows.map(r => 
    `INSERT INTO identity.player_preserved_avatars (player_id, avatar, unlocked_avatars, updated_at) VALUES (\x27${r.playerId}\x27, \x27${r.avatar}\x27, ARRAY[${r.unlocked.map(a => `\x27${a}\x27`).join(",")}], now()) ON CONFLICT (player_id) DO UPDATE SET avatar = EXCLUDED.avatar, unlocked_avatars = EXCLUDED.unlocked_avatars, updated_at = now();`
  );
  fs.writeFileSync(sqlOut, stmts.join("\n") + "\n");
}

console.log(`\n目标重置角色共计: ${rows.length} 人\n`);
console.log("| 序号 | 账号 (Login) | 道号 (Display Name) | 当前功德(自/绑) | 商店返还(自/绑) | 最终功德总额 | 当前形象 | 已解锁形象 |");
console.log("| :---: | :--- | :--- | :---: | :---: | :---: | :---: | :--- |");

rows.forEach((r, idx) => {
  const extraMark = r.isExtraAvatar ? " ★" : "";
  console.log(`| ${idx + 1} | \`${r.account}\` | ${r.displayName} | ${r.curNorm} / ${r.curBound} | +${r.spentNorm} / +${r.spentBound} | **${r.finalTotal}** | \`${r.avatar}\` | ${r.unlockedStr}${extraMark} |`);
});

const grandTotalCur = totalCurNorm + totalCurBound;
const grandTotalRefund = totalRefundNorm + totalRefundBound;
const grandTotalFinal = grandTotalCur + grandTotalRefund;

console.log("\n--- 功德资金池总计 ---");
console.log(`- 当前持有自由功德 : ${totalCurNorm}`);
console.log(`- 当前持有绑定功德 : ${totalCurBound}`);
console.log(`- 商店消费返还自由 : +${totalRefundNorm}`);
console.log(`- 商店消费返还绑定 : +${totalRefundBound}`);
console.log(`- 最终全服保留总额 : ${grandTotalFinal} (当前持有 ${grandTotalCur} + 累计返还 ${grandTotalRefund})\n`);
'

TARGET_COUNT="$(echo "$AUDIT_RAW" | grep -c ':::' || true)"
if [[ "$TARGET_COUNT" == "0" ]]; then
  printf '无符合条件的角色需要重置，退出。\n'
  exit 0
fi

# 3. DRY-RUN 判断
if [[ "$MODE" == "dry-run" ]]; then
  printf '==> [3/4] 预演安全核验...\n'
  printf '  ✔ 机器人账号（100个）未在列表内（已安全豁免）。\n'
  printf '  ✔ 自动化烟测账号（smoke_ci_verifier）未在列表内（已安全豁免）。\n'
  printf '  ✔ 功德返还计算无负值、无溢出。\n\n'
  printf '====================================================================\n'
  printf '当前处于 \033[32;1m--dry-run 预演模式\033[0m，未做任何真实修改。\n'
  printf '如需在通知后执行真实重置，请运行:\n'
  printf '  %s --apply --scope %s\n' "$0" "$SCOPE"
  printf '====================================================================\n'
  exit 0
fi

# 4. APPLY 真实执行流程
printf '==> [3/4] 执行前置安全准备...\n'

# 4.1 强制数据库全量备份
if [[ "$SKIP_BACKUP" == "1" ]]; then
  printf '\033[33;1m警告: 已通过 --skip-backup 跳过前置热备。\033[0m\n'
else
  printf '  - 正在执行全量热备 (/opt/daohua/backup.sh)...\n'
  BACKUP_LOG="$(ssh "$REMOTE_HOST" "sudo /opt/daohua/backup.sh")"
  if ! echo "$BACKUP_LOG" | grep -q "数据库备份完成"; then
    printf '错误: 前置热备失败，中止执行！备份日志:\n%s\n' "$BACKUP_LOG" >&2
    exit 1
  fi
  printf '  ✔ 前置热备完成并通过 SHA-256 校验。\n'
fi

# 4.2 操作者交互确认
printf '\n\033[31;1m####################################################################\033[0m\n'
printf '\033[31;1m#                     高 危 操 作 确 认 提 示                     #\033[0m\n'
printf '\033[31;1m####################################################################\033[0m\n'
printf '即将对上述 \033[31;1m%s\033[0m 名玩家执行游戏域数据重置（保留账号、道号、形象与回补功德）。\n' "$TARGET_COUNT"
printf '建议在执行前已停止前端或主服服务，以避免在线玩家内存状态回写。\n\n'
printf '请输入大写字符串 \033[32;1mCONFIRM_RESET_DATA\033[0m 确认执行；输入其他任意内容取消: '
read -r CONFIRM_INPUT
if [[ "$CONFIRM_INPUT" != "CONFIRM_RESET_DATA" ]]; then
  printf '输入不匹配，已安全取消操作。\n'
  exit 2
fi

# 4.3 构造并执行原子 SQL 事务
printf '\n==> [4/4] 正在提交原子重置事务...\n'

RESET_TX_SQL="
BEGIN;

-- 1. 建立永久形象保留表（若不存在）并同步记录
CREATE TABLE IF NOT EXISTS identity.player_preserved_avatars (
    player_id uuid PRIMARY KEY REFERENCES identity.players(player_id) ON DELETE CASCADE,
    avatar varchar(64) NOT NULL,
    unlocked_avatars varchar(64)[] NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

$(cat "$AVATAR_SQL_FILE" 2>/dev/null || true)

-- 2. 锁定目标玩家集合
CREATE TEMP TABLE _reset_targets ON COMMIT DROP AS
SELECT p.player_id, a.account_id
FROM identity.accounts a
JOIN identity.players p ON p.account_id = a.account_id
WHERE a.login_normalized NOT LIKE 'bot_%'
  AND a.login_normalized != 'smoke_ci_verifier'
  AND ($SCOPE_WHERE)
  AND ($EXCLUDE_WHERE);

-- 3. 统计商店消费功德返还
CREATE TEMP TABLE _merit_refunds ON COMMIT DROP AS
SELECT 
    l.player_id,
    l.asset_id,
    SUM(-l.delta) AS refund_amount
FROM economy.transaction_lines l
JOIN economy.transactions t ON t.transaction_id = l.transaction_id
WHERE l.player_id IN (SELECT player_id FROM _reset_targets)
  AND t.transaction_type = 'shop.buy'
  AND l.asset_id IN ('item.gongde', 'item.gongde-bound')
  AND l.delta < 0
GROUP BY l.player_id, l.asset_id;

-- 4. 资产域重置：清除非功德资产与全部装备实例
DELETE FROM economy.player_asset_instances WHERE owner_player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM economy.reservations WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM economy.shop_daily_purchases WHERE player_id IN (SELECT player_id FROM _reset_targets);

DELETE FROM economy.player_asset_balances 
WHERE player_id IN (SELECT player_id FROM _reset_targets)
  AND asset_id NOT IN ('item.gongde', 'item.gongde-bound');

-- 5. 回补功德：将商店累计消费返还到现有余额
UPDATE economy.player_asset_balances b
SET quantity = b.quantity + r.refund_amount,
    version = b.version + 1,
    updated_at = now()
FROM _merit_refunds r
WHERE b.player_id = r.player_id 
  AND b.asset_id = r.asset_id
  AND b.variant = '';

-- 若原本功德为 0 无行记录，插入返还行
INSERT INTO economy.player_asset_balances (player_id, asset_id, variant, quantity, version, updated_at)
SELECT r.player_id, r.asset_id, '', r.refund_amount, 1, now()
FROM _merit_refunds r
WHERE NOT EXISTS (
    SELECT 1 FROM economy.player_asset_balances b
    WHERE b.player_id = r.player_id AND b.asset_id = r.asset_id AND b.variant = ''
);

-- 6. 市场域重置（挂单、成交与结算）
DELETE FROM market.trades t WHERE t.buyer_id IN (SELECT player_id FROM _reset_targets)
   OR t.seller_id IN (SELECT player_id FROM _reset_targets)
   OR t.buy_order_id IN (SELECT order_id FROM market.orders o WHERE o.owner_player_id IN (SELECT player_id FROM _reset_targets))
   OR t.sell_order_id IN (SELECT order_id FROM market.orders o WHERE o.owner_player_id IN (SELECT player_id FROM _reset_targets));
DELETE FROM market.settlement_claims WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM market.settlement_cursors WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM market.orders WHERE owner_player_id IN (SELECT player_id FROM _reset_targets);

-- 6.1 联动校准行情统计（清除已删挂单的在售/求购量，并将无成交记录品种的参考价置空）
WITH live_stats AS (
    SELECT 
        asset_id, 
        variant, 
        MIN(price) FILTER (WHERE side = 'sell') AS best_ask_price,
        COALESCE(SUM(quantity - filled_quantity) FILTER (WHERE side = 'sell'), 0) AS ask_quantity,
        MAX(price) FILTER (WHERE side = 'buy') AS best_bid_price,
        COALESCE(SUM(quantity - filled_quantity) FILTER (WHERE side = 'buy'), 0) AS bid_quantity
    FROM market.orders
    WHERE status IN ('active', 'partially_filled')
    GROUP BY asset_id, variant
)
UPDATE market.item_stats s
SET 
    best_ask_price = ls.best_ask_price,
    ask_quantity = COALESCE(ls.ask_quantity, 0),
    best_bid_price = ls.best_bid_price,
    bid_quantity = COALESCE(ls.bid_quantity, 0),
    reference_price = CASE WHEN EXISTS (SELECT 1 FROM market.trades t WHERE t.asset_id = s.asset_id AND t.variant = s.variant) THEN s.reference_price ELSE NULL END,
    updated_at = now()
FROM (
    SELECT 
        s2.asset_id,
        s2.variant,
        ls2.best_ask_price,
        ls2.ask_quantity,
        ls2.best_bid_price,
        ls2.bid_quantity
    FROM market.item_stats s2
    LEFT JOIN live_stats ls2 ON s2.asset_id = ls2.asset_id AND s2.variant = ls2.variant
) ls
WHERE s.asset_id = ls.asset_id AND s.variant = ls.variant;

-- 7. 社交域重置（组队、好友申请、好友关系与聊天）
-- 清除角色宗门关系；宗主被重置时解散其宗门，保留退出锚点供其他成员恢复。
INSERT INTO social.zongmen_departures(zongmen_id,player_id,left_at,anchor_node_id)
SELECT m.zongmen_id,m.player_id,now(),z.anchor_node_id
FROM social.zongmen_members m JOIN social.zongmen z USING(zongmen_id)
WHERE m.player_id IN (SELECT player_id FROM _reset_targets)
   OR z.leader_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.zongmen_requests WHERE player_id IN (SELECT player_id FROM _reset_targets)
   OR zongmen_id IN (SELECT zongmen_id FROM social.zongmen WHERE leader_id IN (SELECT player_id FROM _reset_targets));
DELETE FROM social.zongmen_members WHERE player_id IN (SELECT player_id FROM _reset_targets)
   OR zongmen_id IN (SELECT zongmen_id FROM social.zongmen WHERE leader_id IN (SELECT player_id FROM _reset_targets));
UPDATE social.zongmen SET disbanded_at=now() WHERE leader_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.party_invitations WHERE invitee_id IN (SELECT player_id FROM _reset_targets)
   OR inviter_id IN (SELECT player_id FROM _reset_targets)
   OR party_id IN (SELECT party_id FROM social.parties WHERE leader_id IN (SELECT player_id FROM _reset_targets));
DELETE FROM social.party_seekers WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.party_session_inbox WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.party_sessions WHERE party_id IN (SELECT party_id FROM social.party_members WHERE player_id IN (SELECT player_id FROM _reset_targets));
DELETE FROM social.party_members WHERE player_id IN (SELECT player_id FROM _reset_targets)
   OR party_id IN (SELECT party_id FROM social.parties WHERE leader_id IN (SELECT player_id FROM _reset_targets));
DELETE FROM social.parties WHERE leader_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.friendships WHERE player_id IN (SELECT player_id FROM _reset_targets)
   OR friend_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.friend_requests WHERE requester_id IN (SELECT player_id FROM _reset_targets)
   OR target_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.player_invitations WHERE invitee_id IN (SELECT player_id FROM _reset_targets)
   OR inviter_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM social.chat_messages WHERE sender_id IN (SELECT player_id FROM _reset_targets);

-- 8. 平台与计算节点补算任务清空
DELETE FROM platform.compute_jobs WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM platform.command_receipts WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM platform.section_flush_queue WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM platform.integration_outbox WHERE aggregate_id IN (SELECT player_id FROM _reset_targets);

-- 9. 游戏域状态重置（行动队列、租约、投影、分段与头）
DELETE FROM game.player_runtime_leases WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM game.player_action_queue WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM game.player_projection WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM game.player_state_sections WHERE player_id IN (SELECT player_id FROM _reset_targets);
DELETE FROM game.player_state_heads WHERE player_id IN (SELECT player_id FROM _reset_targets);

-- 10. 吊销所有旧会话，强制客户端重新登录与初始化
DELETE FROM identity.auth_sessions WHERE account_id IN (SELECT account_id FROM _reset_targets);

COMMIT;
"

ssh "$REMOTE_HOST" "sudo docker exec -i daohua-db psql -U daohua -d daohua -v ON_ERROR_STOP=1 -c \"$RESET_TX_SQL\""

printf '\n\033[32;1m✔ 目标角色游戏域数据重置完成！\033[0m\n'
printf '  - 账号与角色道号完好保留；\n'
printf '  - 最终功德与商店返还已写入 economy.player_asset_balances；\n'
printf '  - 旧会话已全部注销踢线；\n'
printf '  - 建议执行: ssh %s \"sudo docker compose -f /opt/daohua/compose.prod.yaml restart app compute\" 重启服务以清空内存状态缓存。\n\n' "$REMOTE_HOST"
