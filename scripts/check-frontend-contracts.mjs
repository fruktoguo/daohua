import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { createServer } from '../frontend/node_modules/vite/dist/node/index.js'

// 使用 Vite 的真实模块转换路径，覆盖 import.meta.glob 和浏览器快照解码。
globalThis.window = { localStorage: { getItem: () => null }, location: { origin: 'http://localhost' } }
globalThis.document = { documentElement: { lang: '' } }
const server = await createServer({ root: new URL('../frontend', import.meta.url).pathname, server: { middlewareMode: true } })
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
    section: 'world', schemaVersion: 24, sectionVersion: 1,
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
  console.log('前端契约通过：世界实体、增量时刻、CSV 空翻译、配置回复点数。')
} finally {
  await server.close()
}
