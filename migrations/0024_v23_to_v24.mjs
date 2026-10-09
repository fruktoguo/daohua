#!/usr/bin/env node
// v23 → v24 一次性离线载荷切换；只用 Node 标准库，不接入启动或运行时。
import { spawn } from 'node:child_process';
import { createHash, timingSafeEqual } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { brotliCompressSync, brotliDecompressSync, constants } from 'node:zlib';

const maxBytes = 4 * 1024 * 1024;
const kinds = ['action_queue', 'buffs', 'combat', 'duel', 'loadout', 'progression', 'settings', 'world'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const literal = value => `'${String(value).replaceAll("'", "''")}'`;
const bytesSql = bytes => `decode('${bytes.toString('hex')}', 'hex')`;
const version = row => BigInt(row.version);
const sameHash = (left, right) => /^[0-9a-f]{64}$/i.test(left) && /^[0-9a-f]{64}$/i.test(right)
  && timingSafeEqual(Buffer.from(left.toLowerCase()), Buffer.from(right.toLowerCase()));

function parseDocument(text) {
  // 远征 seed 为 Int64；保留原数字文本，禁止 JS 双精度重编码篡改个人队列。
  return JSON.parse(text, (_key, value, context) =>
    typeof value === 'number' && !Number.isSafeInteger(value) && /^-?\d+$/.test(context.source)
      ? JSON.rawJSON(context.source) : value);
}

function decode(row) {
  if (typeof row.payload !== 'string' || row.payload.length === 0) throw new Error(`分段载荷缺失：${row.source}/${row.player_id}/${row.kind}。`);
  const stored = Buffer.from(row.payload, 'hex');
  if (!stored.length || stored.length > maxBytes)
    throw new Error(`分段存储载荷大小无效：${row.source}/${row.player_id}/${row.kind}。`);
  const raw = row.codec === 'json' ? stored : row.codec === 'br-json'
    ? brotliDecompressSync(stored, { maxOutputLength: maxBytes }) : null;
  if (!raw || raw.length !== row.raw_bytes || raw.length > maxBytes || !sameHash(hash(raw), row.checksum))
    throw new Error('分段原始长度、codec 或 checksum 校验失败。');
  try { return parseDocument(raw.toString('utf8')); }
  catch { throw new Error('分段 JSON 无效，拒绝改档。'); }
}

function encode(document) {
  const raw = Buffer.from(JSON.stringify(document));
  if (!raw.length || raw.length > maxBytes) throw new Error('转换后分段超过大小上限。');
  const compressed = raw.length >= 512 ? brotliCompressSync(raw, {
    params: { [constants.BROTLI_PARAM_QUALITY]: 1 }
  }) : raw;
  const useBrotli = compressed.length < raw.length;
  return { payload: useBrotli ? compressed : raw, codec: useBrotli ? 'br-json' : 'json', raw_bytes: raw.length, checksum: hash(raw) };
}

function convertActions(document) {
  if (!Array.isArray(document)) throw new Error('action_queue 不是数组。');
  const head = document[0];
  const removedHead = head?.participation != null;
  let resources = null;
  if (removedHead && head.expedition != null) {
    const { playerHitpoints: hp, playerManapoints: mp } = head.expedition;
    if (typeof hp !== 'number' || !Number.isFinite(hp) || hp < 0
      || typeof mp !== 'number' || !Number.isFinite(mp) || mp < 0)
      throw new Error('旧参与远征血蓝无效，拒绝改档。');
    resources = { hitpoints: hp <= 0 ? 1 : hp, manapoints: mp };
  }
  const actions = document.filter(action => action.participation == null);
  // Remove 仅在原队首被移除时激活下一首；Blocked 保持阻塞，其余个人项不改进度/次数。
  if (removedHead && actions[0]?.status === 0) actions[0].status = 1;
  return { actions, resources, removed: document.length - actions.length };
}

function headChecksum(rows) {
  return hash(rows.slice().sort((a, b) => a.kind < b.kind ? -1 : a.kind > b.kind ? 1 : 0)
    .map(row => `${row.kind}:${row.version}:${row.checksum}`).join('|'));
}

function casUpdate(row, encoded) {
  const table = row.source === 'persisted' ? 'game.player_state_sections' : 'platform.section_flush_queue';
  const versionColumn = row.source === 'persisted' ? 'section_version' : 'state_version';
  const stored = row.source === 'persisted' ? `, stored_bytes = ${encoded.payload.length}` : '';
  return `DO $cutover$ BEGIN
    UPDATE ${table} SET payload = ${bytesSql(encoded.payload)}, codec = ${literal(encoded.codec)},
      raw_bytes = ${encoded.raw_bytes}, checksum = ${literal(encoded.checksum)}, schema_version = 24${stored}
    WHERE player_id = ${literal(row.player_id)}::uuid AND section_kind = ${literal(row.kind)}
      AND ${versionColumn} = ${row.version} AND checksum = ${literal(row.checksum)} AND schema_version = ${row.schema_version};
    IF NOT FOUND THEN RAISE EXCEPTION '迁移 CAS 失败，分段版本或 checksum 已改变'; END IF;
  END $cutover$;`;
}

function prepare(snapshot) {
  const statements = [];
  let removed = 0;
  let syntheticCombat = 0;
  const players = new Map();
  for (const row of snapshot.rows) {
    if (!players.has(row.player_id)) players.set(row.player_id, { persisted: [], pending: [] });
    players.get(row.player_id)[row.source].push(row);
  }
  const heads = new Map(snapshot.heads.map(head => [head.player_id, head]));
  for (const [playerId, layers] of players) {
    const head = heads.get(playerId);
    const originalPersistedChecksum = headChecksum(layers.persisted);
    if (head && !sameHash(originalPersistedChecksum, head.checksum)) throw new Error('persisted head checksum 校验失败。');
    if (head && layers.persisted.some(row => version(row) > version(head))) throw new Error('persisted 分段版本越过提交头。');
    const effective = new Map(layers.persisted.map(row => [row.kind, row]));
    for (const row of layers.pending) {
      const persisted = effective.get(row.kind);
      if (!persisted || version(row) > version(persisted)) effective.set(row.kind, row);
      else if (version(row) === version(persisted) && row.checksum !== persisted.checksum)
        throw new Error('durable 同一版本载荷冲突，拒绝改档。');
    }
    if (effective.size !== kinds.length || kinds.some(kind => !effective.has(kind)))
      throw new Error('玩家有效分段不完整，拒绝改档。');
    const documents = new Map();
    const conversions = new Map();
    // 连过时的 durable 行也校验、转换，不能留下将来被误刷盘的旧参与引用；
    // 所有 v23 row 都重算 raw/stored/checksum，避免只推进标签掩盖损坏载荷。
    for (const row of [...layers.persisted, ...layers.pending]) {
      if (row.schema_version !== 23) throw new Error('参与迁移的玩家分段必须全部为 v23。');
      documents.set(row, decode(row));
      if (row.kind === 'action_queue') {
        const converted = convertActions(documents.get(row));
        conversions.set(row, converted);
        removed += converted.removed;
      }
    }
    const baseAction = layers.persisted.find(row => row.kind === 'action_queue');
    const baseCombat = layers.persisted.find(row => row.kind === 'combat');
    const latestAction = effective.get('action_queue');
    const latestCombat = effective.get('combat');
    const latestResources = conversions.get(latestAction).resources;
    const baseResources = baseAction ? conversions.get(baseAction).resources : null;
    if (baseResources && baseCombat) Object.assign(documents.get(baseCombat), baseResources);
    const pendingCombat = layers.pending.find(row => row.kind === 'combat');
    if (latestResources) {
      if (latestCombat.source === 'pending') Object.assign(documents.get(latestCombat), latestResources);
      else if (latestAction.source === 'persisted') Object.assign(documents.get(latestCombat), latestResources);
      else {
        // action_queue 比 combat 更新：补齐 durable 血蓝，但不抬升任何玩家状态版本。
        const document = parseDocument(JSON.stringify(documents.get(latestCombat)));
        Object.assign(document, latestResources);
        const encoded = encode(document);
        const stateVersion = [...effective.values()].reduce((max, row) => version(row) > max ? version(row) : max,
          head ? version(head) : 0n).toString();
        if (pendingCombat) {
          statements.push(`DO $cutover$ BEGIN
            DELETE FROM platform.section_flush_queue WHERE player_id = ${literal(playerId)}::uuid AND section_kind = 'combat'
              AND state_version = ${pendingCombat.version} AND checksum = ${literal(pendingCombat.checksum)};
            IF NOT FOUND THEN RAISE EXCEPTION '迁移 CAS 失败，过时 combat 已改变'; END IF;
          END $cutover$;`);
        }
        const metadata = layers.pending.filter(row => row.version.toString() === stateVersion)
          .sort((a, b) => a.updated_at < b.updated_at ? 1 : a.updated_at > b.updated_at ? -1 : 0);
        const fingerprints = new Set(metadata.map(row => row.game_data_fingerprint).filter(value => value?.trim()));
        if (fingerprints.size > 1) throw new Error('durable 最高版本游戏数据指纹冲突。');
        const fingerprint = [...fingerprints][0] ?? head?.game_data_fingerprint ?? '';
        const simulated = metadata.map(row => row.last_simulated_at).filter(Boolean)
          .concat(head?.last_simulated_at ? [head.last_simulated_at] : []).sort().at(-1);
        if (!simulated) throw new Error('durable combat 缺少模拟时刻。');
        statements.push(`INSERT INTO platform.section_flush_queue
          (player_id, section_kind, state_version, schema_version, codec, payload, raw_bytes, checksum,
           game_data_fingerprint, last_simulated_at, next_attempt_at, updated_at)
          VALUES (${literal(playerId)}::uuid, 'combat', ${stateVersion}, 24, ${literal(encoded.codec)},
            ${bytesSql(encoded.payload)}, ${encoded.raw_bytes}, ${literal(encoded.checksum)}, ${literal(fingerprint)},
            ${literal(simulated)}::timestamptz, now(), ${literal(metadata[0]?.updated_at ?? latestAction.updated_at)}::timestamptz);`);
        syntheticCombat++;
      }
    }
    for (const row of [...layers.persisted, ...layers.pending]) {
      if (row.kind !== 'action_queue' && row.kind !== 'combat') {
        const encoded = encode(documents.get(row));
        statements.push(casUpdate(row, encoded));
        row.checksumAfter = encoded.checksum;
        continue;
      }
      if (row === pendingCombat && latestResources && latestAction.source === 'pending' && latestCombat.source === 'persisted') continue;
      const document = row.kind === 'action_queue' ? conversions.get(row).actions : documents.get(row);
      const encoded = encode(document);
      statements.push(casUpdate(row, encoded));
      row.checksumAfter = encoded.checksum;
    }
    // 提交头永远绑定 game 表，不能用 overlay 的较新 checksum 污染 persisted head。
    if (head) {
      const checksum = headChecksum(layers.persisted.map(row => ({ ...row, checksum: row.checksumAfter ?? row.checksum })));
      statements.push(`DO $cutover$ BEGIN
        UPDATE game.player_state_heads SET checksum = ${literal(checksum)}
        WHERE player_id = ${literal(playerId)}::uuid AND state_version = ${head.version}
          AND checksum = ${literal(head.checksum)} AND schema_version = ${head.schema_version};
        IF NOT FOUND THEN RAISE EXCEPTION '迁移 CAS 失败，提交头版本或 checksum 已改变'; END IF;
      END $cutover$;`);
    } else if (layers.persisted.length) throw new Error('persisted 分段缺少提交头。');
    // 关系行动表只是有效 action_queue 的投影；pending 优先，避免旧 persisted 行重新带回参与引用。
    const projectionAction = latestAction;
    if (projectionAction) {
      statements.push(`DELETE FROM game.player_action_queue WHERE player_id = ${literal(playerId)}::uuid;`);
      const statuses = ['pending', 'running', 'blocked'];
      conversions.get(projectionAction).actions.forEach((action, slot) => {
        if (!statuses[action.status] || typeof action.intent?.actionId?.value !== 'string'
          || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(action.id))
          throw new Error('个人行动关系投影字段无效。');
        statements.push(`INSERT INTO game.player_action_queue
          (player_id, slot, action_id, action_payload, action_version, status, next_due_at, idempotency_key, updated_at)
          VALUES (${literal(playerId)}::uuid, ${slot}, ${literal(action.intent.actionId.value)},
            ${bytesSql(Buffer.from(JSON.stringify(action.intent)))}, ${projectionAction.version}, ${literal(statuses[action.status])},
            NULL, ${literal(`action:${action.id.toLowerCase()}`)}, ${literal(projectionAction.updated_at)}::timestamptz);`);
      });
    }
  }
  return { sql: statements.join('\n'), players: players.size, removed, syntheticCombat };
}

async function main() {
  if (process.argv.length !== 2) throw new Error('用法：node migrations/0024_v23_to_v24.mjs；环境变量 DB_CONTAINER / DB_USER / DB_NAME 可覆盖本地默认值。');
  if (typeof JSON.rawJSON !== 'function') throw new Error('需要支持无损 JSON 数字的 Node.js 22 或更新版本。');
  const migration = await readFile(new URL('./0024_v23_to_v24.sql', import.meta.url), 'utf8');
  // 去掉 SQL 文件自身事务边界，保证载荷写入、DDL 与标签推进只提交一次。
  const body = migration.replace(/^BEGIN;\s*$/m, '').replace(/^COMMIT;\s*$/m, '');
  const child = spawn('docker', ['exec', '-i', process.env.DB_CONTAINER ?? 'daohua-local-db',
    'psql', '-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-U', process.env.DB_USER ?? 'daohua',
    '-d', process.env.DB_NAME ?? 'daohua'], { stdio: ['pipe', 'pipe', 'pipe'] });
  // 不转发数据库原始错误：psql 可能在上下文中回显载荷或 SQL；对外只报阶段。
  child.stderr.resume();
  child.stdin.on('error', () => {});
  let phase = '连接、停服租约检查或快照读取';
  let finished = false;
  let converted;
  const snapshot = { rows: [], heads: [] };
  const exit = new Promise((resolve, reject) => {
    child.once('error', () => reject(new Error('无法启动 docker exec psql，请检查 Docker 与容器。')));
    child.once('close', code => code === 0 && finished ? resolve() : reject(new Error(`${phase}失败；未确认提交，禁止清库或继续启动。`)));
  });
  // 从开始到提交使用同一连接和事务；表锁阻止停服后仍尝试抢租约/刷盘的并发写者。
  child.stdin.write(`BEGIN;
    SET LOCAL lock_timeout = '10s';
    SET LOCAL statement_timeout = '120s';
    DO $$ BEGIN
      IF EXISTS (SELECT 1 FROM game.player_runtime_leases WHERE lease_until > clock_timestamp()) THEN
        RAISE EXCEPTION '必须停止全部服务并等待有效租约释放或过期';
      END IF;
      IF (SELECT max(version) FROM platform.schema_version) NOT IN (23, 24) THEN
        RAISE EXCEPTION '只允许 v23 到 v24';
      END IF;
    END $$;
    LOCK TABLE game.player_runtime_leases, game.player_state_heads, game.player_state_sections,
      platform.section_flush_queue, game.player_action_queue, social.party_sessions
      IN SHARE ROW EXCLUSIVE MODE;
    DO $$ BEGIN
      IF EXISTS (SELECT 1 FROM game.player_runtime_leases WHERE lease_until > clock_timestamp()) THEN
        RAISE EXCEPTION '锁定后发现有效租约';
      END IF;
    END $$;
    CREATE TEMP TABLE cutover_players ON COMMIT DROP AS
      SELECT player_id FROM game.player_state_sections WHERE schema_version = 23 AND section_kind = 'action_queue'
      UNION SELECT player_id FROM platform.section_flush_queue WHERE schema_version = 23 AND section_kind = 'action_queue';
    SELECT 'HEAD ' || json_build_object('player_id', h.player_id, 'version', h.state_version::text,
      'schema_version', h.schema_version, 'checksum', h.checksum, 'game_data_fingerprint', h.game_data_fingerprint,
      'last_simulated_at', h.last_simulated_at)::text
      FROM game.player_state_heads h JOIN cutover_players c USING (player_id);
    SELECT 'ROW ' || json_build_object('source', 'persisted', 'player_id', s.player_id, 'kind', s.section_kind,
      'version', s.section_version::text, 'schema_version', s.schema_version, 'checksum', s.checksum, 'codec', s.codec,
      'raw_bytes', s.raw_bytes, 'stored_bytes', s.stored_bytes, 'updated_at', s.updated_at,
      'payload', encode(s.payload, 'hex'))::text
      FROM game.player_state_sections s JOIN cutover_players c USING (player_id);
    SELECT 'ROW ' || json_build_object('source', 'pending', 'player_id', q.player_id, 'kind', q.section_kind,
      'version', q.state_version::text, 'schema_version', q.schema_version, 'checksum', q.checksum, 'codec', q.codec,
      'raw_bytes', q.raw_bytes, 'stored_bytes', NULL, 'updated_at', q.updated_at,
      'game_data_fingerprint', q.game_data_fingerprint, 'last_simulated_at', q.last_simulated_at,
      'payload', encode(q.payload, 'hex'))::text
      FROM platform.section_flush_queue q JOIN cutover_players c USING (player_id);
    SELECT 'SNAPSHOT_READY';\n`);
  const lines = createInterface({ input: child.stdout });
  try {
    for await (const line of lines) {
      if (line.startsWith('HEAD ')) snapshot.heads.push(JSON.parse(line.slice(5)));
      else if (line.startsWith('ROW ')) snapshot.rows.push(JSON.parse(line.slice(4)));
      else if (line === 'SNAPSHOT_READY') {
        phase = '载荷校验与转换';
        converted = prepare(snapshot);
        phase = 'CAS 写入、SQL 升级或事务提交';
        child.stdin.end(`${converted.sql}\n${body}\nCOMMIT;\nSELECT 'CUTOVER_COMMITTED';\n`);
      } else if (line === 'CUTOVER_COMMITTED') finished = true;
    }
    await exit;
  } catch (error) {
    // 连接关闭会回滚未提交事务；不发送任何删除玩家/队伍的 SQL。
    child.stdin.destroy();
    child.kill();
    await exit.catch(() => {});
    throw error;
  }
  console.log(`v24 离线切换已提交：${converted.players} 名玩家，移除 ${converted.removed} 个旧参与行动引用，补齐 ${converted.syntheticCombat} 条 durable combat；角色、队伍、资产与既有玩家版本保留。`);
}

main().catch(error => {
  console.error(`迁移中止：${error.message}`);
  process.exitCode = 1;
});
