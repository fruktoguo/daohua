import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import path from 'node:path'
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
  const { encodeJsonEnvelope, decodeEnvelope, decodeJsonPayload, WireMessageType, createRequestId } = await server.ssrLoadModule('/src/network/protocol.ts')
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
  // 角色卡沿用真实信封解析路径；普通钱包数量不可与不可售的离线储备相加。
  const cardRequestId = createRequestId()
  const playerCard = { requestId: cardRequestId, playerId: 'player.test', autoUseSlots: [
    { slotIndex: 0, itemId: 'item.qi-gathering-pill', quantity: 3, enabled: true, group: 'potion', offlineReserveQuantity: 7 },
  ] }
  const cardEnvelope = decodeEnvelope(encodeJsonEnvelope(WireMessageType.PlayerCard, cardRequestId, 1, playerCard))
  assert.equal(cardEnvelope.messageType, WireMessageType.PlayerCard)
  const decodedCard = decodeJsonPayload(cardEnvelope)
  assert.equal(decodedCard.autoUseSlots[0].quantity, 3)
  assert.equal(decodedCard.autoUseSlots[0].offlineReserveQuantity, 7)
  assert.deepEqual(decodedCard, playerCard)
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
    section: 'world', schemaVersion: 36, sectionVersion: 1,
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

  // action_queue 轮次累计：服务端逐行下发 completedRepeats（无限循环与单项循环跨轮重置的唯一判据），
  // 解码必须透传，否则进度条按恒 0 比对、跨轮不回绕。
  const queueRow = {
    id: 'aaaaaaaa-0000-0000-0000-000000000001',
    intent: { actionId: 'action.test', repeatCount: -1, targetNodeId: null, parameters: null, pvpEnabled: false },
    actionName: '契约行动', actionType: 'cultivation', status: 1, progressSeconds: 3, durationSeconds: 10,
    remainingRepeats: -1, completedRepeats: 4, executionKind: 'timed',
  }
  const queueSection = {
    section: 'action_queue', schemaVersion: 36, sectionVersion: 1, data: [queueRow],
    checksum: createHash('sha256').update(JSON.stringify([queueRow]), 'utf8').digest('hex'),
    updatedAt: '2026-09-14T00:00:05Z',
  }
  const queueDelta = { playerId: current.playerId, stateVersion: 3, lastSimulatedAt: '2026-09-14T00:00:06Z', changedSections: [queueSection] }
  const queuePatch = await decodePlayerSnapshotDeltaDto(queueDelta, current, Buffer.from(JSON.stringify(queueDelta)))
  assert.equal(queuePatch.queue.entries[0].completedRepeats, 4)
  assert.equal(queuePatch.queue.entries[0].remainingRepeats, -1)
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
  // 静态代码门禁：扫描 frontend/src 下全部 TSX/TS 组件与自定义 Hook，
  // 严禁在条件返回（early return）之后调用 Hook，严格遵循 React Rules of Hooks。
  const require = createRequire(import.meta.url)
  const tsPath = fileURLToPath(new URL('../frontend/node_modules/typescript/lib/typescript.js', import.meta.url))
  const ts = require(tsPath)

  function findTsxFiles(dir) {
    const results = []
    for (const file of readdirSync(dir)) {
      const full = path.join(dir, file)
      const stat = statSync(full)
      if (stat.isDirectory()) {
        results.push(...findTsxFiles(full))
      } else if (file.endsWith('.tsx') || file.endsWith('.ts')) {
        results.push(full)
      }
    }
    return results
  }

  const frontendSrcDir = fileURLToPath(new URL('../frontend/src', import.meta.url))
  const hookViolations = []
  for (const file of findTsxFiles(frontendSrcDir)) {
    const content = readFileSync(file, 'utf8')
    const sf = ts.createSourceFile(file, content, ts.ScriptTarget.Latest, true)

    function inspectNode(node) {
      if (
        ts.isFunctionDeclaration(node) ||
        ts.isFunctionExpression(node) ||
        ts.isArrowFunction(node)
      ) {
        if (node.body && ts.isBlock(node.body)) {
          let returnSeen = false
          let returnLine = 0
          for (const stmt of node.body.statements) {
            let hasHook = false
            let hookName = ''
            let hookLine = 0

            function findHook(n) {
              if (
                n !== stmt &&
                (ts.isFunctionDeclaration(n) ||
                  ts.isFunctionExpression(n) ||
                  ts.isArrowFunction(n))
              ) {
                return
              }
              if (ts.isCallExpression(n)) {
                let name = ''
                if (ts.isIdentifier(n.expression)) {
                  name = n.expression.text
                } else if (ts.isPropertyAccessExpression(n.expression) && ts.isIdentifier(n.expression.name)) {
                  name = n.expression.name.text
                }
                if (/^use[A-Z]/.test(name)) {
                  hasHook = true
                  hookName = name
                  hookLine = sf.getLineAndCharacterOfPosition(n.getStart()).line + 1
                }
              }
              ts.forEachChild(n, findHook)
            }
            findHook(stmt)

            if (returnSeen && hasHook) {
              hookViolations.push(`${path.relative(frontendSrcDir, file)}:${hookLine} 调用 Hook '${hookName}' 发生在第 ${returnLine} 行条件返回之后，违反 React Rules of Hooks`)
            }

            function findReturn(n) {
              if (
                n !== stmt &&
                (ts.isFunctionDeclaration(n) ||
                  ts.isFunctionExpression(n) ||
                  ts.isArrowFunction(n))
              ) {
                return
              }
              if (ts.isReturnStatement(n)) {
                returnSeen = true
                returnLine = sf.getLineAndCharacterOfPosition(n.getStart()).line + 1
              }
              ts.forEachChild(n, findReturn)
            }
            findReturn(stmt)
          }
        }
      }
      ts.forEachChild(node, inspectNode)
    }

    inspectNode(sf)
  }

  assert.equal(
    hookViolations.length,
    0,
    `发现 React Rules of Hooks 违规（条件返回后调用 Hook）：\n${hookViolations.join('\n')}`
  )

  // 技艺经验曲线门禁：前 30 级按「升入 1 级 -90% → 升入 30 级 -0%」线性削减，30 级以上与修为曲线一致；
  // 锚点取自总纲 §20.1.2.1 权威表，任一侧单独改动系数（服务端 TechniqueCatalog.EarlyDiscountRatio /
  // 前端 techniqueEarlyDiscountRatio）都会在此暴露。
  const { TECHNIQUE_EXP_TO_NEXT, calculateTechniqueProgress, techniqueEarlyDiscountRatio } = await server.ssrLoadModule('/src/game/combatConstants.ts')
  assert.equal(TECHNIQUE_EXP_TO_NEXT[0], 1000, '技艺 0→1 级需求应为修为条 10000 削减 90% 后的 1000')
  assert.equal(TECHNIQUE_EXP_TO_NEXT[1], 1572, '技艺 1→2 级需求应为 1572')
  assert.equal(TECHNIQUE_EXP_TO_NEXT[17], 502069, '技艺 17→18 级需求应为 502069')
  assert.equal(TECHNIQUE_EXP_TO_NEXT[28], 9179979, '技艺 28→29 级需求应为 9179979')
  assert.equal(TECHNIQUE_EXP_TO_NEXT[29], 12631000, '技艺 29→30 级削减归零，需求须等于修为条')
  assert.equal(TECHNIQUE_EXP_TO_NEXT[30], 15158000, '技艺 30 级以上不得削减，须与修为曲线一致')
  assert.equal(techniqueEarlyDiscountRatio(1), 0.9, '升入 1 级削减比例应为 90%')
  assert.equal(techniqueEarlyDiscountRatio(30), 0, '升入 30 级削减比例应为 0%')
  assert.equal(techniqueEarlyDiscountRatio(31), 0, '30 级以上不应削减')
  assert.deepEqual(calculateTechniqueProgress(1000), { level: 1, currentLayerExp: 0, expToNext: 1572, progressPercentage: 0 })
  assert.equal(calculateTechniqueProgress(1604846).level, 18, '累计 1,604,846 经验应正好达到 18 级（总纲 §20.1.2.1）')
  assert.equal(calculateTechniqueProgress(1604845).level, 17, '差 1 点不得提前升到 18 级')

  console.log('前端契约通过：世界实体、增量时刻、CSV 空翻译、配置回复点数、坊市标的等级与背包对齐、技艺经验曲线、版本感知防环、触发条件自然语言格式化与 React Hooks 规则门禁通过。')
} finally {
  await server.close()
}
