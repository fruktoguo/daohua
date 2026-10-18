import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { createServer } from '../frontend/node_modules/vite/dist/node/index.js'

// 使用 Vite 的真实模块转换路径，覆盖 import.meta.glob 和浏览器快照解码。
const mockSessionStorage = new Map()
let reloadCount = 0
globalThis.window = {
  localStorage: { getItem: () => null },
  sessionStorage: {
    getItem: (key) => mockSessionStorage.get(key) ?? null,
    setItem: (key, val) => mockSessionStorage.set(key, String(val)),
    removeItem: (key) => mockSessionStorage.delete(key),
  },
  location: { origin: 'http://localhost', reload: () => { reloadCount += 1 } },
  addEventListener: () => {},
  removeEventListener: () => {},
  dispatchEvent: () => true,
  setTimeout: (fn) => fn(),
  clearTimeout: () => {},
}
globalThis.sessionStorage = globalThis.window.sessionStorage
globalThis.document = { documentElement: { lang: '' }, addEventListener: () => {}, removeEventListener: () => {} }
const server = await createServer({ root: fileURLToPath(new URL('../frontend', import.meta.url)), server: { middlewareMode: true } })
try {
  const { decodePlayerSnapshotDeltaDto, decodePlayerSnapshotDto } = await server.ssrLoadModule('/src/network/snapshot.ts')
  const { encodeJsonEnvelope, WireMessageType, createRequestId } = await server.ssrLoadModule('/src/network/protocol.ts')
  const { GameClient } = await server.ssrLoadModule('/src/network/gameClient.ts')
  const { setSessionAccount } = await server.ssrLoadModule('/src/network/sessionState.ts')
  if (process.argv[2]) {
    // fixture 文件字节即信封 JSON 原始字节：section data 区间校验按线上字节逐位复算。
    const fixtureBytes = await readFile(process.argv[2])
    const fixture = JSON.parse(fixtureBytes.toString('utf8'))
    const decoded = await decodePlayerSnapshotDto(fixture, fixtureBytes)
    assert.equal(decoded.combat.learnedAbilityIds[0], 'ability.heavy-slash')
    assert.equal(decoded.combat.abilitySlots[0].abilityId, 'ability.heavy-slash')
    assert.ok(decoded.combatAttributes.hpRegenPerFiveSeconds > 0)
  }
  const { parseCsvToMap, LOCALES } = await server.ssrLoadModule('/src/i18n/index.ts')
  const { registerBaseAttributeAnchors, calculateBaseAttributes } = await server.ssrLoadModule('/src/game/combatConstants.ts')
  const payload = {
    mapId: 'map.test', nodeId: 'node.test', x: 1, y: 2, edgeProgress: 0,
    locationName: '契约测试节点', cultivationMultiplier: 1.25,
    availableActionIds: ['action.test'], encounterIds: ['encounter.test'],
    npcs: [{ entityId: 'npc.test', name: '测试修士', kind: 'npc', description: '描述', effect: '效果' }],
    buildings: [{ entityId: 'building.test', name: '测试建筑', kind: 'building', description: '', effect: '' }],
    nodeBuildings: [{
      entityId: 'building.player-test', name: '自建建筑', kind: 'building', description: '', effect: '',
      group: 'production', level: 2, targetLevel: 0, buildProgress: 1, materialProgress: 1,
      timeProgress: 1, requiredSeconds: 0, elapsedSeconds: 0,
      activeUsers: 40, capacity: 100, efficiency: 1, materials: [],
    }],
    buildableBuildingIds: ['building.player-test'],
    monsters: [{
      monsterId: 'enemy.test', name: '测试妖兽', level: 2, tier: 1, rank: 'normal',
      budgetCost: 1, maxHitpoints: 10, physicalAttack: 2, armor: 0,
      drops: [{ itemId: 'item.loot', dropChance: 0.5, minQuantity: 1, maxQuantity: 3 }],
    }],
  }
  // data 内嵌 JSON 对象；checksum = 该成员在信封 JSON 中原始字节（JSON.stringify 原文）的 SHA-256。
  const section = {
    section: 'world', schemaVersion: 35, sectionVersion: 1,
    data: payload, checksum: createHash('sha256').update(JSON.stringify(payload), 'utf8').digest('hex'),
    updatedAt: '2026-09-14T00:00:05Z',
  }
  const current = { playerId: 'player.test', serverTimeUnixSeconds: 1 }
  const delta = { playerId: current.playerId, stateVersion: 2, lastSimulatedAt: '2026-09-14T00:00:05Z', changedSections: [section], displayName: '契约道号' }
  const deltaBytes = Buffer.from(JSON.stringify(delta))
  const patch = await decodePlayerSnapshotDeltaDto(delta, current, deltaBytes)
  // 道号属身份标量而非分段：delta 透传后即时生效（改名无需等待全量快照）。
  assert.equal(patch.name, '契约道号')
  assert.deepEqual(patch.visual, { renderer: 'text', source: '契' })
  assert.deepEqual(patch.world.availableActionIds, payload.availableActionIds)
  assert.deepEqual(patch.world.encounterIds, payload.encounterIds)
  assert.deepEqual(patch.world.npcs, payload.npcs)
  assert.deepEqual(patch.world.buildings, payload.buildings)
  assert.deepEqual(patch.world.nodeBuildings, payload.nodeBuildings)
  assert.deepEqual(patch.world.buildableBuildingIds, payload.buildableBuildingIds)
  // 怪物掉落明细往返：drops 结构化字段（itemId/dropChance/minQuantity/maxQuantity）解码对齐。
  assert.equal(patch.world.monsters[0].drops[0].itemId, 'item.loot')
  assert.equal(patch.world.monsters[0].drops[0].dropChance, 0.5)
  assert.equal(patch.world.monsters[0].drops[0].minQuantity, 1)
  assert.equal(patch.world.monsters[0].drops[0].maxQuantity, 3)
  assert.equal(patch.serverTimeUnixSeconds, Date.parse(delta.lastSimulatedAt) / 1000)
  await assert.rejects(decodePlayerSnapshotDeltaDto({ ...delta, lastSimulatedAt: undefined }, current, deltaBytes), /lastSimulatedAt/)
  // 坏 checksum 必须在完整业务解码阶段失败，不能被合法 envelope 提前清零。
  const badChecksum = { ...delta, changedSections: [{ ...section, checksum: '0'.repeat(64) }] }
  await assert.rejects(decodePlayerSnapshotDeltaDto(badChecksum, current, Buffer.from(JSON.stringify(badChecksum))), /checksum|校验/i)

  // 真实坏帧计数：坏 JSON 计入失败，合法 Pong 不清零；完整业务 payload 成功应用后恢复。
  setSessionAccount({ accountId: 'account.test', playerId: 'player.test', account: 'account.test', name: '契约测试' })
  const client = new GameClient()
  const socket = { readyState: 1 }
  Object.assign(client, {
    disposed: false, socket, sessionGeneration: 7, playerId: 'player.test', account: 'account.test', sequenceId: 0,
    lastServerSequence: 0, parseFailureCount: 0, parseFailureWindowStart: 0, parseFailureResynced: false,
  })
  await client.handleMessage(encodeJsonEnvelope(WireMessageType.Pong, createRequestId(), 1, {}), socket, 7, 'player.test')
  assert.equal(client.parseFailureCount, 0)
  await client.handleMessage(new TextEncoder().encode('{bad-json').buffer, socket, 7, 'player.test')
  assert.equal(client.parseFailureCount, 1)
  await client.handleMessage(encodeJsonEnvelope(WireMessageType.LogBatch, createRequestId(), 2, { events: [] }), socket, 7, 'player.test')
  assert.equal(client.parseFailureCount, 0)

  // 旧 socket 的延迟 Blob 解码完成后必须被世代校验拦截，不能写入新会话。
  let releaseDelayedBlob
  const delayedBlob = new Blob([encodeJsonEnvelope(WireMessageType.LogBatch, createRequestId(), 3, { events: [] })])
  delayedBlob.arrayBuffer = () => new Promise(resolve => { releaseDelayedBlob = resolve })
  const delayed = client.handleMessage(delayedBlob, socket, 7, 'player.test')
  const replacementSocket = { readyState: 1 }
  client.socket = replacementSocket
  client.sessionGeneration = 8
  releaseDelayedBlob(new ArrayBuffer(0))
  await delayed
  assert.equal(client.parseFailureCount, 0)

  // 命令回执增量形态：snapshotForm=delta 时 changedSections 位于 snapshot 成员内，
  // data 字节区间按同一校验和口径复算（与服务端 CommandResultDto 内嵌结构一致）。
  const receipt = {
    requestId: 'req-1', succeeded: true, code: 'OK', message: '', stateVersion: 2,
    snapshotForm: 'delta', snapshot: delta,
  }
  const receiptBytes = Buffer.from(JSON.stringify(receipt))
  const receiptPatch = await decodePlayerSnapshotDeltaDto(receipt.snapshot, current, receiptBytes, ['snapshot', 'changedSections'])
  assert.equal(receiptPatch.world.nodeId, payload.nodeId)
  assert.equal(receiptPatch.version, receipt.stateVersion)
  assert.deepEqual(parseCsvToMap('key,zh_source,translation\ncommon.confirm,确认,"Confirm, now"\ncommon.cancel,取消,\n'), { 'common.confirm': 'Confirm, now' })
  assert.throws(() => parseCsvToMap('key,translation\ncommon.confirm,"broken"suffix'), /分隔符/)
  assert.ok(!LOCALES.includes('template'))
  const anchor = { level: 1, hp: 100, mp: 50, ad: 10, ap: 5, armor: 10, mr: 0, acc: 1, eva: 1, crit: 1, cavoid: 1, breakPt: 1, parry: 1, hpRegen: 7.25, mpRegen: 0.125 }
  registerBaseAttributeAnchors([anchor, { ...anchor, level: 3, hp: 1000, hpRegen: 8.25 }])
  const pool = calculateBaseAttributes(0, 2)
  assert.equal(pool.baseHpRegen, 7.75)
  assert.equal(pool.baseMpRegen, 0.125)
  const { resolveMarketInstrumentMetadata } = await server.ssrLoadModule('/src/game/marketStore.ts')
  const { resolveItemMetadata: resolveItemMeta, withItemGrade: withGrade } = await server.ssrLoadModule('/src/game/ids.ts')
  const t2Weapon = {
    itemId: 'equipment.pishan-blade',
    name: '披山刀',
    category: 'equipment',
    subCategory: 'weapon',
    tier: 2,
    sellPrice: 50,
    equipmentDefinition: { equipmentId: 'equipment.pishan-blade', name: '披山刀', slot: 'weapon', tier: 2 },
  }
  const marketMeta = resolveMarketInstrumentMetadata(t2Weapon)
  assert.equal(marketMeta.tier, 2, '坊市标的 tier 必须保留原始等阶')
  assert.equal(marketMeta.level, 18, '坊市标的 level 必须换算为修士等级 Lv.18，不得错误显示为 T 级')
  assert.equal(marketMeta.rarity, 'yellow', 'T2 标的稀有度必须为黄品 (yellow)')
  const bagMeta = withGrade(resolveItemMeta(t2Weapon.itemId) ?? { label: t2Weapon.name, glyph: '·', tone: 'gold', category: 'weapon', rarity: 'mundane', rarityLabel: '', description: '' }, 2)
  assert.equal(marketMeta.level, bagMeta.level, '坊市标的等级必须与背包等级完全对齐')
  const level18Meta = withGrade(resolveItemMeta(t2Weapon.itemId), 18)
  assert.equal(level18Meta.level, 18, 'withItemGrade 传入 level 18 时必须能反推等阶并保留 Lv.18')
  assert.equal(level18Meta.rarity, 'yellow', 'withItemGrade 传入 level 18 时稀有度必须为 yellow')
  // 版本更新感知与防死循环熔断断言
  const { notifyServerVersion, triggerVersionReload, isReloadCircuitBroken, _resetVersionStateForTest } = await server.ssrLoadModule('/src/network/versionManager.ts')
  _resetVersionStateForTest()
  notifyServerVersion('v1.0.0')
  assert.equal(reloadCount, 0, '首次通知建立基线不应触发刷新')
  notifyServerVersion('v1.0.0')
  assert.equal(reloadCount, 0, '相同版本不应触发刷新')
  notifyServerVersion('v1.0.1')
  assert.equal(reloadCount, 1, '会话版本漂移应触发一次刷新')
  // 测试熔断机制（模拟下一次载入后再次触发重试）
  _resetVersionStateForTest()
  triggerVersionReload('v1.0.1', '测试再次重试')
  assert.equal(reloadCount, 2, '第二次重试仍放行')
  assert.equal(isReloadCircuitBroken('v1.0.1'), true, '达到 2 次后必须标记为熔断状态')
  _resetVersionStateForTest()
  triggerVersionReload('v1.0.1', '超限重试')
  assert.equal(reloadCount, 2, '熔断后绝对不可再次调用 reload 造成死循环')
  _resetVersionStateForTest()

  // 触发条件格式化断言：状态层数必须正确注入具体状态名称，且句式为自然语言“当...时”
  const { describeCombatTrigger, resolveTriggerBuffName } = await server.ssrLoadModule('/src/game/format.ts')
  assert.equal(resolveTriggerBuffName('buff.spirit-tea'), '凝神茶', 'Buff ID 必须解析为凝神茶')
  assert.equal(resolveTriggerBuffName('buff.qi-gathering'), '引气', 'Buff ID 必须解析为引气')
  assert.equal(resolveTriggerBuffName('buff.zhixue'), '止血散', 'Buff ID 必须解析为止血散')

  const buffTrigger = {
    dependency: 'target-enemy',
    condition: 'buff-stacks',
    buffId: 'buff.spirit-tea',
    comparator: 'greater-than',
    value: 0,
  }
  const buffSummary = describeCombatTrigger(buffTrigger)
  assert.equal(buffSummary, '当 目标 的 凝神茶 状态层数 大于 0 时', '状态层数条件必须包含具体Buff名称并以自然语言呈现')

  const hpTrigger = {
    dependency: 'self',
    condition: 'current-hp-percentage',
    comparator: 'less-than-equal',
    value: 50,
  }
  const hpSummary = describeCombatTrigger(hpTrigger)
  assert.equal(hpSummary, '当 自身 的 生命百分比 小于等于 50% 时', '生命百分比条件必须以自然语言呈现')

  console.log('前端契约通过：世界实体、增量时刻、CSV 空翻译、配置回复点数、坊市标的等级与背包对齐、版本感知防环与触发条件自然语言格式化通过。')
} finally {
  await server.close()
}
