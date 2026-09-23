/**
 * 道化太初客户端快照解码与校验引擎。
 *
 * 核心安全与完整性职责：
 * 1. 对服务端下发的 Base64 编码 Section 载荷执行 SHA-256 完整性哈希校验；
 * 2. 严格核对 Snapshot 头校验和（Head Checksum），防止数据篡改或网络坏帧；
 * 3. 将扁平的分段载荷（Progression, World, Combat, Buffs, Loadout, Settings, ActionQueue）及资产钱包投影为前端响应式 PlayerSnapshot 与 PlayerPatch。
 */
import { calculateRealmProgress } from '../game/combatConstants'
import { resolveActionDefinition, resolveItemMetadata } from '../game/ids'
import type {
  ActionType,
  AutoUseSlot,
  BattleReport,
  Buff,
  CharacterVisual,
  CombatAttributes,
  CombatLoadout,
  CombatResourceState,
  CombatTrigger,
  DuelState,
  ExpeditionDuel,
  ExpeditionDuelEvent,
  ExpeditionEnemy,
  ExpeditionPhase,
  ExpeditionProgress,
  InventoryItem,
  MonsterDropDefinition,
  NearbyMonsterDefinition,
  NodeBuilding,
  PlayerPatch,
  PlayerSnapshot,
  QueueEntry,
  QueueState,
  Stats,
  TravelSegment,
  WorldState,
} from '../game/types'
import type { AssetBalanceDto, PlayerSectionDto, PlayerSnapshotDeltaDto, PlayerSnapshotDto } from './protocol'

type JsonObject = Record<string, unknown>

declare global {
  interface Uint8ArrayConstructor {
    /**
     * ES2025 `Uint8Array.fromBase64` 最小声明：tsconfig lib 固定 ES2022 未收录该 API。
     * 浏览器 Baseline 已可用；Node 契约门禁（ssrLoadModule）等旧 V8 运行时可能缺失，
     * 故声明为可选并做运行时探测回退。
     */
    fromBase64?(base64: string, options?: {
      alphabet?: 'base64' | 'base64url'
      lastChunkHandling?: 'loose' | 'strict' | 'stop-before-partial'
    }): Uint8Array
  }
}

const ACTION_TYPES: readonly ActionType[] = ['cultivation', 'exploration', 'alchemy', 'combat']
/** 全量快照必须齐备的分段清单；缺任何一段说明服务端快照残缺，整包拒绝 */
const REQUIRED_SNAPSHOT_SECTIONS = ['progression', 'world', 'combat', 'buffs', 'loadout', 'settings', 'action_queue', 'duel'] as const

function object(value: unknown): JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function requiredObject(value: unknown, name: string): JsonObject {
  const result = object(value)
  if (Object.keys(result).length === 0) {
    throw new Error(`${name} section 载荷必须是对象。`)
  }
  return result
}

function stringValue(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function numberValue(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function finiteNumber(value: unknown, name: string, minimum = 0): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < minimum) {
    throw new Error(`${name}必须是有限且不小于 ${minimum} 的数值。`)
  }
  return value
}

function finiteValue(value: unknown, name: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error(`${name}必须是有限数值。`)
  return value
}

function finiteInteger(value: unknown, name: string, minimum = 0): number {
  const result = finiteNumber(value, name, minimum)
  if (!Number.isSafeInteger(result)) throw new Error(`${name}必须是安全整数。`)
  return result
}

function stateVersionValue(value: unknown, name: string): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${name}状态版本无效。`)
  }
  return value
}

function booleanValue(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback
}

function arrayValue(value: unknown): unknown[] {
  return Array.isArray(value) ? value : []
}

/** wire.v1 section 中的 Domain record struct ID 统一以 { value } 形式或纯字符串形式序列化。 */
function identifierValue(value: unknown, name = '标识'): string {
  if (typeof value === 'string' && value.length > 0) return value
  const candidate = object(value)
  const result = stringValue(candidate.value)
  if (!result) throw new Error(`${name}载荷无效。`)
  return result
}

/** 模块级共享文本解码器：分段解码高频触发，避免每段新建 TextDecoder 的微分配。 */
const sectionTextDecoder = new TextDecoder()

function base64ToBytes(value: string): Uint8Array {
  const normalized = value.replaceAll('-', '+').replaceAll('_', '/')
  const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, '=')
  // 优先走 ES2025 原生 Uint8Array.fromBase64（无中间 binary string 分配）；
  // 缺失该 API 的运行时（如契约门禁的 Node 旧 V8）回退 atob + 逐字节拷贝，解码语义一致。
  if (Uint8Array.fromBase64) return Uint8Array.fromBase64(padded)
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index)
  return bytes
}

async function decodeSectionPayload(section: PlayerSectionDto): Promise<unknown> {
  if (section.codec !== 'json') throw new Error(`不支持的 wire.v1 section codec: ${section.codec}`)
  const encoded = base64ToBytes(section.payloadBase64)
  if (section.storedBytes <= 0 || encoded.byteLength !== section.storedBytes) {
    throw new Error(`section ${section.section} 存储长度校验失败。`)
  }
  const raw = encoded
  if (section.rawBytes <= 0 || raw.byteLength !== section.rawBytes) {
    throw new Error(`section ${section.section} 原始长度校验失败。`)
  }
  if (!/^[0-9a-f]{64}$/i.test(section.checksum)) throw new Error(`section ${section.section} 校验和格式无效。`)
  const digest = await crypto.subtle.digest('SHA-256', raw.slice().buffer as ArrayBuffer)
  const checksum = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
  if (checksum !== section.checksum.toLowerCase()) throw new Error(`section ${section.section} 校验和不匹配。`)
  const text = sectionTextDecoder.decode(raw)
  return JSON.parse(text) as unknown
}

async function decodeSections(sections: readonly PlayerSectionDto[]): Promise<Map<string, unknown>> {
  if (!Array.isArray(sections)) throw new Error('玩家 section 列表无效。')
  const names = new Set<string>()
  for (const section of sections) {
    if (!section || typeof section.section !== 'string' || section.section.length === 0 || names.has(section.section)) {
      throw new Error('玩家 section 标识重复或为空。')
    }
    names.add(section.section)
    if (section.schemaVersion !== 3) throw new Error(`不支持的 section schema 版本: ${section.section} v${section.schemaVersion}。`)
  }
  const decoded = await Promise.all(sections.map(async section => [section.section, await decodeSectionPayload(section)] as const))
  return new Map(decoded)
}

async function verifyHeadChecksum(dto: PlayerSnapshotDto): Promise<void> {
  const expected = dto.head.checksum?.toLowerCase()
  if (!expected || !/^[0-9a-f]{64}$/.test(expected)) throw new Error('玩家快照头校验和缺失或格式无效。')
  const canonical = dto.sections
    .slice()
    .sort((left, right) => left.section.localeCompare(right.section))
    .map(section => `${section.section}:${section.sectionVersion}:${section.checksum}`)
    .join('|')
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(canonical).buffer as ArrayBuffer)
  const actual = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
  if (actual !== expected) throw new Error('玩家快照头校验和不匹配。')
}

function mapPresence(value: unknown): PlayerSnapshot['presenceMode'] {
  if (value === 0) return 'fully-offline'
  if (value === 1) return 'online'
  if (value === 2) return 'idle-offline'
  throw new Error('玩家 presence 枚举值无效。')
}

function emptySlot(): AutoUseSlot {
  return { itemId: null, quantity: 1, enabled: false, remainingDurationSeconds: 0 }
}

/**
 * 统一战斗属性解码：玩家 combat.attributes、远征敌方条目 attributes、斗法镜像
 * opponentAttributes 共用同一字段集与语义（全部百分比属性按点数下发）。
 */
function mapCombatAttributes(value: unknown, name: string): CombatAttributes {
  const source = requiredObject(value, name)
  const numericKeys = [
    'maxHitpoints', 'maxManapoints', 'physicalAttack', 'spellAttack', 'armor', 'magicResistance',
    'accuracy', 'evasion', 'critical', 'criticalResistance', 'criticalMultiplier',
    'breakPower', 'parryResolve', 'hpRegenPerFiveSeconds', 'mpRegenPerFiveSeconds',
    'attackIntervalMilliseconds', 'basicAttackRatio', 'skillIntensity', 'cooldownFactor', 'castTimeFactor',
    'physicalDamageBonusPoints', 'spellDamageBonusPoints',
    'metalDamageBonusPoints', 'woodDamageBonusPoints', 'waterDamageBonusPoints',
    'fireDamageBonusPoints', 'earthDamageBonusPoints',
    'totalDamageBonusPoints', 'basicAttackBonusPoints', 'skillBonusPoints',
    'physicalDamageReductionPoints', 'spellDamageReductionPoints',
    'metalDamageReductionPoints', 'woodDamageReductionPoints', 'waterDamageReductionPoints',
    'fireDamageReductionPoints', 'earthDamageReductionPoints',
    'damageAbsorptionPoints',
    'attackSpeedPoints', 'actionSpeedPoints', 'cooldownReductionPoints', 'threatBonusPoints',
    'hpDrainPoints', 'mpDrainPoints', 'damageReflectPoints', 'healingBonusPoints',
  ] as const
  const numbers = Object.fromEntries(numericKeys.map(key => [key, finiteValue(source[key], `${name}.${key}`)]))
  return numbers as unknown as CombatAttributes
}

/** 立绘渲染配置解码（远征敌方条目随快照下发的 catalog visual 透传对象）。 */
function mapCharacterVisual(value: unknown, name: string): CharacterVisual | null {
  if (value === null || value === undefined) return null
  const row = object(value)
  const source = stringValue(row.source)
  if (!source) return null
  const renderer = stringValue(row.renderer, 'text')
  if (!['text', 'glyph', 'image', 'live2d', 'model3d'].includes(renderer)) {
    throw new Error(`${name}.renderer 枚举无效。`)
  }
  return { renderer: renderer as CharacterVisual['renderer'], source, poster: optionalString(row.poster, `${name}.poster`) }
}

function defaultAutoUseSlots(): QueueState['autoUseSlots'] {
  // 战斗域双组栏位：前 3 槽药品栏（potion），后 3 槽通用栏（general）；其余行动域 3 槽通用栏。
  return Object.fromEntries(ACTION_TYPES.map(type => [
    type,
    Array.from({ length: type === 'combat' ? 6 : 3 }, (_, index) => ({
      ...emptySlot(),
      group: type === 'combat' ? (index < 3 ? 'potion' : 'general') : 'general',
    } as AutoUseSlot)),
  ])) as QueueState['autoUseSlots']
}

function mapExpeditionPhase(value: unknown): ExpeditionPhase {
  if (typeof value === 'number') {
    if (!Number.isSafeInteger(value)) throw new Error('战斗远征 phase 枚举值无效。')
    switch (value) {
      case 0: return 'initial-search'
      case 1: return 'search'
      case 2: return 'combat'
      case 3: return 'respawn'
      case 4: return 'duel-pending'
      case 5: return 'duel'
      default: throw new Error('战斗远征 phase 枚举值无效。')
    }
  }
  if (typeof value !== 'string') throw new Error('战斗远征 phase 字段无效。')
  switch (value.trim().toLowerCase().replaceAll('_', '-')) {
    case '0':
    case 'initialsearch':
    case 'initial-search': return 'initial-search'
    case '1':
    case 'search': return 'search'
    case '2':
    case 'combat': return 'combat'
    case '3':
    case 'respawn':
    case 'respawning': return 'respawn'
    case '4':
    case 'duelpending':
    case 'duel-pending': return 'duel-pending'
    case '5':
    case 'duel': return 'duel'
    default: throw new Error('战斗远征 phase 字段无效。')
  }
}

function optionalString(value: unknown, name: string): string | null {
  if (value === null || value === undefined) return null
  if (typeof value !== 'string' || value.length === 0 || value.length > 128) {
    throw new Error(`${name}字段无效。`)
  }
  return value
}

function mapExecutionKind(value: unknown, fallback?: QueueEntry['executionKind']): QueueEntry['executionKind'] {
  if (value === null || value === undefined) return fallback
  if (typeof value !== 'string') throw new Error('行动 executionKind 字段无效。')
  switch (value.trim().toLowerCase().replaceAll('_', '-')) {
    case 'timed': return 'timed'
    case 'travel': return 'travel'
    case 'combat-encounter':
    case 'combatencounter': return 'combat-encounter'
    default: throw new Error('行动 executionKind 字段无效。')
  }
}

/** 解析远征载荷中的斗法回放显示信息（expedition.duel，可缺省；字段名与 DuelPlaybackState 序列化对齐） */
function mapExpeditionDuel(value: unknown, actionId: string): ExpeditionDuel | null {
  if (value === null || value === undefined) return null
  const row = object(value)
  if (Object.keys(row).length === 0) throw new Error(`行动 ${actionId} 的 duel 载荷无效。`)
  const outcome = finiteInteger(row.viewerOutcome, `行动 ${actionId} 的 duel.viewerOutcome`)
  if (outcome > 3) throw new Error(`行动 ${actionId} 的 duel.viewerOutcome 枚举值无效。`)
  const opponentAttributes = mapCombatAttributes(row.opponentAttributes, `行动 ${actionId} 的 duel.opponentAttributes`)
  const opponentHitpoints = finiteNumber(row.opponentHitpoints, `行动 ${actionId} 的 duel.opponentHitpoints`)
  const opponentManapoints = finiteNumber(row.opponentManapoints, `行动 ${actionId} 的 duel.opponentManapoints`)
  if (opponentAttributes.maxHitpoints <= 0 || opponentHitpoints < 0 || opponentHitpoints > opponentAttributes.maxHitpoints) {
    throw new Error(`行动 ${actionId} 的 duel 对手血量无效。`)
  }
  if (opponentManapoints < 0 || opponentManapoints > opponentAttributes.maxManapoints) {
    throw new Error(`行动 ${actionId} 的 duel 对手灵力无效。`)
  }
  // 剧本事件只提取行动条/资源条所需字段（时刻、双方身份、功法与结算后灵力）；伤害文本由日志呈现。
  const events: ExpeditionDuelEvent[] = arrayValue(row.events)
    .map((entry, index) => {
      const duelEvent = object(entry)
      const label = `行动 ${actionId} 的 duel.events[${index}]`
      return {
        atSeconds: finiteNumber(duelEvent.atSeconds, `${label}.atSeconds`),
        attackerIsInitiator: booleanValue(duelEvent.attackerIsInitiator),
        targetIsInitiator: booleanValue(duelEvent.targetIsInitiator),
        abilityId: optionalString(duelEvent.abilityId, `${label}.abilityId`),
        attackerManapointsAfter: finiteNumber(duelEvent.attackerManapointsAfter, `${label}.attackerManapointsAfter`),
        targetManapointsAfter: finiteNumber(duelEvent.targetManapointsAfter, `${label}.targetManapointsAfter`),
      }
    })
  return {
    opponentName: stringValue(row.opponentName),
    isInitiator: booleanValue(row.viewerIsInitiator),
    outcome,
    resolved: booleanValue(row.resolved),
    opponentAttributes,
    opponentHitpoints,
    opponentManapoints,
    durationSeconds: finiteNumber(row.durationSeconds, `行动 ${actionId} 的 duel.durationSeconds`),
    events,
  }
}

function mapExpedition(value: unknown, actionId: string): ExpeditionProgress {
  const row = object(value)
  if (Object.keys(row).length === 0) throw new Error(`行动 ${actionId} 的战斗远征载荷无效。`)
  const encounterId = identifierValue(row.encounterId, `行动 ${actionId} 的 encounterId`)
  const phase = mapExpeditionPhase(row.phase)
  const elapsedSeconds = finiteNumber(row.phaseElapsedSeconds, `行动 ${actionId} 的 phaseElapsedSeconds`)
  const durationSeconds = finiteNumber(
    row.phaseDurationSeconds,
    `行动 ${actionId} 的 phaseDurationSeconds`,
    phase === 'combat' || phase === 'duel' || phase === 'duel-pending' ? 0 : Number.MIN_VALUE,
  )
  // 搜寻/重生为固定时长阶段，进度不得越界；combat/duel 由交战实况决定、duel-pending 等待撮合无确定上界，不做区间校验。
  if ((phase === 'initial-search' || phase === 'search' || phase === 'respawn') && elapsedSeconds > durationSeconds + 1e-9) {
    throw new Error(`行动 ${actionId} 的战斗远征阶段进度超出范围。`)
  }
  const playerHitpoints = finiteNumber(row.playerHitpoints, `行动 ${actionId} 的 playerHitpoints`)
  const playerManapoints = finiteNumber(row.playerManapoints, `行动 ${actionId} 的 playerManapoints`)

  // 这些标量用于恢复/审计，即使当前界面不直接展示，也必须拒绝坏值，避免把损坏快照继续传播。
  finiteValue(row.seed, `行动 ${actionId} 的 seed`)
  const battleElapsedSeconds = finiteNumber(row.battleElapsedSeconds, `行动 ${actionId} 的 battleElapsedSeconds`)
  const playerNextAttackAtSeconds = finiteNumber(row.playerNextAttackAtSeconds, `行动 ${actionId} 的 playerNextAttackAtSeconds`)
  finiteNumber(row.playerAttackCount, `行动 ${actionId} 的 playerAttackCount`)
  const playerThreat = finiteNumber(row.playerThreat, `行动 ${actionId} 的 playerThreat`)
  const waveNumber = finiteInteger(row.waveNumber, `行动 ${actionId} 的 waveNumber`)
  // 倒地窗口（原地复活遭遇）：复活截止点与读条时长均为战斗时钟秒，存活或非倒地形态为 null。
  const playerRespawnAtSeconds = row.playerRespawnAtSeconds === null || row.playerRespawnAtSeconds === undefined
    ? null
    : finiteNumber(row.playerRespawnAtSeconds, `行动 ${actionId} 的 playerRespawnAtSeconds`)
  const playerRespawnDurationSeconds = row.playerRespawnDurationSeconds === null || row.playerRespawnDurationSeconds === undefined
    ? null
    : finiteNumber(row.playerRespawnDurationSeconds, `行动 ${actionId} 的 playerRespawnDurationSeconds`)
  const outcome = finiteInteger(row.outcome, `行动 ${actionId} 的 outcome`)
  if (outcome > 3) throw new Error(`行动 ${actionId} 的 outcome 枚举值无效。`)

  // 多敌同场：enemies 数组携带同波全体敌人的权威快照（名称/品阶/立绘/完整属性均随包下发）；
  // 搜寻/复活阶段为空。前端不得再按 enemyId 反查本地怪物目录补齐战斗数值。
  const enemies: ExpeditionEnemy[] = (row.enemies === null || row.enemies === undefined ? [] : arrayValue(row.enemies))
    .map((entry, index) => {
      const enemy = object(entry)
      const label = `行动 ${actionId} 的 enemies[${index}]`
      return {
        enemyId: identifierValue(enemy.enemyId, `${label}.enemyId`),
        name: stringValue(enemy.name) || identifierValue(enemy.enemyId, `${label}.enemyId`),
        rank: enumValue(enemy.rank, ['normal', 'elite', 'boss'] as const, `${label}.rank`),
        visual: mapCharacterVisual(enemy.visual, `${label}.visual`),
        hitpoints: finiteNumber(enemy.hitpoints, `${label}.hitpoints`),
        manapoints: finiteNumber(enemy.manapoints, `${label}.manapoints`),
        nextAttackAtSeconds: finiteNumber(enemy.nextAttackAtSeconds, `${label}.nextAttackAtSeconds`),
        attackCount: finiteNumber(enemy.attackCount, `${label}.attackCount`),
        threat: finiteNumber(enemy.threat, `${label}.threat`),
        pendingAbilityId: enemy.pendingAbilityId === null || enemy.pendingAbilityId === undefined
          ? null
          : identifierValue(enemy.pendingAbilityId, `${label}.pendingAbilityId`),
        pendingActionDurationSeconds: finiteNumber(enemy.pendingActionDurationSeconds, `${label}.pendingActionDurationSeconds`),
        attributes: enemy.attributes === null || enemy.attributes === undefined
          ? null
          : mapCombatAttributes(enemy.attributes, `${label}.attributes`),
      }
    })
  const primary = enemies.find(enemy => enemy.hitpoints > 0) ?? enemies[0] ?? null

  return {
    phase,
    progress: durationSeconds > 0 ? Math.max(0, Math.min(1, elapsedSeconds / durationSeconds)) : 0,
    elapsedSeconds,
    durationSeconds,
    playerHitpoints,
    playerManapoints,
    battleElapsedSeconds,
    playerNextAttackAtSeconds,
    enemyHitpoints: primary?.hitpoints ?? 0,
    enemyManapoints: primary?.manapoints ?? 0,
    enemyId: primary?.enemyId ?? null,
    enemies,
    playerRespawnAtSeconds,
    playerRespawnDurationSeconds,
    waveNumber,
    playerThreat,
    encounterId,
    duel: mapExpeditionDuel(row.duel, actionId),
  }
}

function mapAutoUseSlots(value: unknown): QueueState['autoUseSlots'] {
  if (!Array.isArray(value)) throw new Error('settings.autoUseSlots 必须是数组。')
  const autoUseSlots = defaultAutoUseSlots()
  for (const raw of arrayValue(value)) {
    const slot = object(raw)
    if (Object.keys(slot).length === 0) throw new Error('settings.autoUseSlots 中存在无效槽位。')
    const actionType = stringValue(slot.actionType) as ActionType
    const slotIndex = numberValue(slot.slotIndex, -1)
    const group = slot.group === 'potion' ? 'potion' : 'general'
    if (!ACTION_TYPES.includes(actionType) || !Number.isInteger(slotIndex) || slotIndex < 0 || slotIndex >= 3) continue
    // 战斗域映射：potion 组占前 3 位，general 组占后 3 位；非战斗域只有 general 组。
    const mappedIndex = actionType === 'combat'
      ? (group === 'potion' ? slotIndex : slotIndex + 3)
      : slotIndex
    // 服务端以 JsonIgnoreCondition.WhenWritingNull 序列化，未指定物品时可空 itemId 会被整键省略，
    // 因此缺失与 null 必须视为同一语义（未配置物品），而非坏数据。
    if ((slot.itemId !== null && slot.itemId !== undefined && typeof slot.itemId !== 'string') || typeof slot.quantity !== 'number' || !Number.isFinite(slot.quantity) || slot.quantity <= 0 || typeof slot.enabled !== 'boolean') {
      throw new Error('settings.autoUseSlots 槽位字段无效。')
    }
    const remainingDurationSeconds = finiteNumber(
      slot.remainingDurationSeconds,
      `settings.autoUseSlots[${actionType}:${slotIndex}].remainingDurationSeconds`,
    )
    autoUseSlots[actionType][mappedIndex] = {
      itemId: slot.itemId || null,
      quantity: Math.max(1, slot.quantity),
      enabled: slot.enabled,
      remainingDurationSeconds,
      group,
    }
  }
  return autoUseSlots
}

function mapTravelSegments(value: unknown): TravelSegment[] | null {
  if (!Array.isArray(value) || value.length === 0) return null
  const segments: TravelSegment[] = []
  for (const item of value) {
    const s = object(item)
    const edgeId = stringValue(s.edgeId)
    if (!edgeId) continue
    segments.push({
      edgeId,
      startProgress: numberValue(s.startProgress, 0),
      endProgress: numberValue(s.endProgress, 1),
      durationSeconds: numberValue(s.durationSeconds, 0),
      distance: numberValue(s.distance, numberValue(s.durationSeconds, 0)),
    })
  }
  return segments.length > 0 ? segments : null
}

function mapQueue(value: unknown, settingsValue?: JsonObject, base?: QueueState): QueueState {
  if (!Array.isArray(value)) throw new Error('action_queue section 载荷必须是数组。')
  if (settingsValue && (typeof settingsValue.loopEnabled !== 'boolean' || !Array.isArray(settingsValue.autoUseSlots))) {
    throw new Error('settings section 循环或自动使用字段无效。')
  }
  const entries = arrayValue(value).map(item => {
    const row = object(item)
    const intent = object(row.intent)
    const actionId = identifierValue(intent.actionId, '行动 actionId')
    const definition = resolveActionDefinition(actionId)
    const serverActionName = optionalString(row.actionName, `行动 ${actionId} 的 actionName`)
    const serverActionType = optionalString(row.actionType, `行动 ${actionId} 的 actionType`)
    const statusRaw = row.status
    const status = statusRaw === 0 ? 'queued' : statusRaw === 1 ? 'running' : statusRaw === 2 ? 'blocked' : (() => { throw new Error(`行动 ${actionId} 状态无效。`) })()
    const entryId = stringValue(row.id)
    if (!entryId) throw new Error(`行动 ${actionId} 缺少实例 ID。`)
    const expedition = row.expedition === null || row.expedition === undefined
      ? null
      : mapExpedition(row.expedition, actionId)
    const executionKind = mapExecutionKind(
      row.executionKind,
      definition?.executionKind ?? (expedition ? 'combat-encounter' : undefined),
    )
    const combatEncounterId = row.combatEncounterId === undefined
      ? definition?.combatEncounterId ?? expedition?.encounterId ?? null
      : optionalString(row.combatEncounterId, `行动 ${actionId} 的 combatEncounterId`)
    const durationSeconds = typeof row.durationSeconds === 'number' && Number.isFinite(row.durationSeconds) && row.durationSeconds >= 0
      ? row.durationSeconds
      : (definition?.durationSeconds ?? 0)
    const travelDestinationNodeId = typeof row.travelDestinationNodeId === 'string' && row.travelDestinationNodeId.length > 0
      ? row.travelDestinationNodeId
      : (stringValue(intent.targetNodeId) || null)
    const travelTotalDistance = row.travelTotalDistance === null || row.travelTotalDistance === undefined
      ? null
      : finiteNumber(row.travelTotalDistance, `行动 ${actionId} 的 travelTotalDistance`)
    const travelSegments = mapTravelSegments(row.travelSegments)
    const actionName = serverActionName || definition?.name || actionId
    const type = definition?.type || (ACTION_TYPES.includes(serverActionType as ActionType) ? (serverActionType as ActionType) : undefined) || 'cultivation'
    const entry: QueueEntry = {
      entryId,
      actionId,
      actionName,
      type,
      status,
      progressSeconds: numberValue(row.progressSeconds),
      durationSeconds,
      remainingRepeats: numberValue(row.remainingRepeats, numberValue(intent.repeatCount, 1)),
      executionKind,
      combatEncounterId,
      travelDestinationNodeId,
      travelTotalDistance,
      travelSegments,
      expedition,
      pvpEnabled: booleanValue(intent.pvpEnabled),
    }
    return entry
  })
  return {
    entries,
    loopEnabled: settingsValue ? booleanValue(settingsValue.loopEnabled, true) : (base?.loopEnabled ?? true),
    autoUseSlots: settingsValue ? mapAutoUseSlots(settingsValue.autoUseSlots) : (base?.autoUseSlots ?? defaultAutoUseSlots()),
  }
}

function mapStats(value: unknown): Stats {
  const row = object(value)
  const normalKills = numberValue(row.normalKills)
  const eliteKills = numberValue(row.eliteKills)
  const bossKills = numberValue(row.bossKills)
  return {
    realmLevel: numberValue(row.realmLevel, 1),
    qi: numberValue(row.qi),
    // 服务端 progression 只有一个经验字段 Experience（突破时清零），即境界修为 spirit；
    // 不存在独立的道基底蕴字段，foundationExp 保持未定义。
    spirit: numberValue(row.experience),
    completedActions: numberValue(row.completedActions),
    normalKills,
    eliteKills,
    bossKills,
    combatEnemiesDefeated: normalKills + eliteKills + bossKills,
    combatLosses: numberValue(row.totalDefeats),
    cultivationSeconds: numberValue(row.cultivationSeconds),
    ageSeconds: numberValue(row.ageSeconds),
    lifespanYears: numberValue(row.lifespanYears),
  }
}

function enumValue<T extends string>(value: unknown, values: readonly T[], name: string): T {
  if (typeof value !== 'string' || !values.includes(value as T)) throw new Error(`${name} 枚举无效。`)
  return value as T
}

function mapCombat(value: unknown): { resources: CombatResourceState; attributes: CombatAttributes } {
  const row = requiredObject(value, 'combat')
  const attributes = mapCombatAttributes(row.attributes, 'combat.attributes')
  // 技能冷却/吟唱与消耗品冷却：服务端按战斗技能状态与自动使用槽位定长下发。
  const skills = requiredObject(row.skills, 'combat.skills')
  // 冷却表允许为空对象（无冷却中技能），不能用 requiredObject 的非空校验。
  const cooldowns = object(skills.cooldowns)
  const abilityCooldownMilliseconds: Record<string, number> = {}
  for (const [abilityId, remaining] of Object.entries(cooldowns)) {
    abilityCooldownMilliseconds[abilityId] = finiteNumber(remaining, `combat.skills.cooldowns.${abilityId}`)
  }
  const consumableCooldownMilliseconds = arrayValue(row.consumableCooldownMilliseconds)
    .map((value, index) => finiteNumber(value, `combat.consumableCooldownMilliseconds[${index}]`))
  const castingAbilityId = skills.castingAbilityId === null || skills.castingAbilityId === undefined
    ? null
    : identifierValue(skills.castingAbilityId, 'combat.skills.castingAbilityId')
  // 资源与上限走 resources 子对象；上限必须与 attributes 面板一致，防双通道漂移。
  const resources = requiredObject(row.resources, 'combat.resources')
  const maxHitpoints = finiteNumber(resources.maxHitpoints, 'combat.resources.maxHitpoints')
  const maxManapoints = finiteNumber(resources.maxManapoints, 'combat.resources.maxManapoints')
  return {
    resources: {
      hitpoints: finiteNumber(resources.hitpoints, 'combat.resources.hitpoints'),
      manapoints: finiteNumber(resources.manapoints, 'combat.resources.manapoints'),
      maxHitpoints,
      maxManapoints,
      abilityCooldownMilliseconds,
      consumableCooldownMilliseconds,
      castingAbilityId,
      castRemainingMilliseconds: finiteNumber(skills.castRemainingMilliseconds, 'combat.skills.castRemainingMilliseconds'),
      castTotalMilliseconds: skills.castTotalMilliseconds === null || skills.castTotalMilliseconds === undefined
        ? null
        : finiteNumber(skills.castTotalMilliseconds, 'combat.skills.castTotalMilliseconds'),
    },
    attributes,
  }
}

function mapTriggers(value: unknown): CombatTrigger[] {
  if (!Array.isArray(value)) throw new Error('战斗触发条件必须是数组。')
  return value.map(item => {
    const row = requiredObject(item, 'trigger')
    return {
      dependency: enumValue(row.dependency, ['self', 'target-enemy', 'ally-lowest-hp', 'ally-highest-hp', 'enemy-lowest-hp', 'enemy-highest-hp'], 'trigger.dependency'),
      condition: enumValue(row.condition, ['current-hp', 'current-mana', 'missing-hp', 'missing-mana', 'current-hp-percentage', 'missing-hp-percentage', 'current-mana-percentage', 'missing-mana-percentage', 'buff-stacks', 'stun-status', 'blind-status', 'silence-status'], 'trigger.condition'),
      comparator: enumValue(row.comparator, ['less-than-equal', 'greater-than-equal', 'is-active', 'is-inactive', 'less-than', 'greater-than'], 'trigger.comparator'),
      value: finiteValue(row.value, 'trigger.value'),
      buffId: optionalString(row.buffId, 'trigger.buffId'),
    }
  })
}

function mapLoadout(value: unknown): CombatLoadout {
  const row = requiredObject(value, 'loadout')
  const core = requiredObject(row.coreAttributes, 'loadout.coreAttributes')
  // 消耗品槽位已收敛至 settings.autoUseSlots 单一通道，loadout 只携带功法槽与装备。
  if (!Array.isArray(row.abilitySlots) || !Array.isArray(row.equipment)) {
    throw new Error('loadout section 槽位载荷无效。')
  }
  return {
    staminaLevel: finiteNumber(core.staminaLevel, 'loadout.staminaLevel'),
    meridiansLevel: finiteNumber(core.meridiansLevel, 'loadout.meridiansLevel'),
    attackLevel: finiteNumber(core.attackLevel, 'loadout.attackLevel'),
    boneLevel: finiteNumber(core.boneLevel, 'loadout.boneLevel'),
    agilityLevel: finiteNumber(core.agilityLevel, 'loadout.agilityLevel'),
    consciousnessLevel: finiteNumber(core.consciousnessLevel, 'loadout.consciousnessLevel'),
    staminaExp: numberValue(core.staminaExp),
    meridiansExp: numberValue(core.meridiansExp),
    attackExp: numberValue(core.attackExp),
    boneExp: numberValue(core.boneExp),
    agilityExp: numberValue(core.agilityExp),
    consciousnessExp: numberValue(core.consciousnessExp),
    abilitySlots: row.abilitySlots.map(item => {
      const slot = requiredObject(item, 'loadout.abilitySlot')
      return { abilityId: optionalString(slot.abilityId, 'abilitySlot.abilityId'), level: finiteNumber(slot.level, 'abilitySlot.level'), triggers: mapTriggers(slot.triggers) }
    }),
    equipment: row.equipment.map(item => {
      const equipment = requiredObject(item, 'loadout.equipment')
      return { itemId: identifierValue(equipment.itemId, 'equipment.itemId'), enhancementLevel: finiteNumber(equipment.enhancementLevel, 'equipment.enhancementLevel') }
    }),
    learnedAbilityIds: requiredStrings(row.learnedAbilityIds, 'loadout.learnedAbilityIds'),
    abilityExperiences: mapAbilityExperiences(row.abilityExperiences),
  }
}

function mapAbilityExperiences(value: unknown): Record<string, number> {
  if (value === undefined || value === null) return {}
  // 技能经验允许为空对象（未获取任何技能经验），不能用 requiredObject 的空对象校验。
  if (typeof value !== 'object' || Array.isArray(value)) throw new Error('loadout.abilityExperiences section 载荷必须是对象。')
  const row = value as JsonObject
  const result: Record<string, number> = {}
  for (const [key, exp] of Object.entries(row)) result[key] = finiteNumber(exp, `loadout.abilityExperiences.${key}`)
  return result
}

function requiredStrings(value: unknown, name: string): string[] {
  if (!Array.isArray(value)) throw new Error(`${name} 必须是数组。`)
  return value.map(item => {
    if (typeof item !== 'string' || !item) throw new Error(`${name} 包含无效标识。`)
    return item
  })
}

function mapNearbyEntities(value: unknown): WorldState['npcs'] {
  if (!Array.isArray(value)) throw new Error('world 周边实体必须是数组。')
  return value.map(item => {
    const row = requiredObject(item, 'world 实体')
    const kind = row.kind
    if (kind !== 'npc' && kind !== 'building' && kind !== 'portal' && kind !== 'resource-node') {
      throw new Error('world 实体 kind 无效。')
    }
    return {
      entityId: identifierValue(row.entityId, 'world 实体 ID'),
      name: stringValue(row.name),
      kind,
      description: stringValue(row.description),
      effect: stringValue(row.effect),
    }
  })
}

/** 怪物掉落条目解析：元素必须是对象且 itemId 非空；概率与数量字段缺省 0，与同级字段缺省风格一致。 */
function mapMonsterDrops(value: unknown): MonsterDropDefinition[] {
  if (!Array.isArray(value)) return []
  return value.map(item => {
    const row = requiredObject(item, 'world 生灵掉落')
    return {
      itemId: identifierValue(row.itemId, 'world 生灵掉落 ID'),
      dropChance: numberValue(row.dropChance, 0),
      minQuantity: numberValue(row.minQuantity, 0),
      maxQuantity: numberValue(row.maxQuantity, 0),
    }
  })
}

function mapNearbyMonsters(value: unknown): NearbyMonsterDefinition[] {
  if (!Array.isArray(value)) return []
  return value.map(item => {
    const row = requiredObject(item, 'world 栖息生灵')
    return {
      monsterId: identifierValue(row.monsterId, 'world 生灵 ID'),
      name: stringValue(row.name),
      level: numberValue(row.level, 1),
      tier: numberValue(row.tier, 1),
      rank: stringValue(row.rank, 'normal'),
      budgetCost: numberValue(row.budgetCost, 0),
      maxHitpoints: numberValue(row.maxHitpoints, 0),
      physicalAttack: numberValue(row.physicalAttack, 0),
      armor: numberValue(row.armor, 0),
      drops: mapMonsterDrops(row.drops),
      visualSource: typeof row.visualSource === 'string' ? row.visualSource : null,
      visualRenderer: typeof row.visualRenderer === 'string' ? row.visualRenderer : null,
    }
  })
}

function mapNodeBuildings(value: unknown): NodeBuilding[] {
  if (!Array.isArray(value)) return []
  return value.map(item => {
    const row = requiredObject(item, 'world 节点建筑')
    return {
      entityId: identifierValue(row.entityId, 'world 节点建筑 ID'),
      name: stringValue(row.name),
      kind: stringValue(row.kind, 'building'),
      description: stringValue(row.description),
      effect: stringValue(row.effect),
      group: stringValue(row.group, 'other'),
      level: numberValue(row.level, 0),
      targetLevel: numberValue(row.targetLevel, 0),
      buildProgress: numberValue(row.buildProgress, 0),
      materialProgress: numberValue(row.materialProgress, 0),
      timeProgress: numberValue(row.timeProgress, 0),
      requiredSeconds: numberValue(row.requiredSeconds, 0),
      elapsedSeconds: numberValue(row.elapsedSeconds, 0),
      materials: Array.isArray(row.materials)
        ? row.materials.map(material => {
            const materialRow = requiredObject(material, 'world 节点建筑材料')
            return {
              itemId: identifierValue(materialRow.itemId, 'world 节点建筑材料 ID'),
              required: numberValue(materialRow.required, 0),
              contributed: numberValue(materialRow.contributed, 0),
            }
          })
        : [],
    }
  })
}

function optionalWorldNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function mapWorld(value: unknown): WorldState {
  const row = object(value)
  const edgeId = typeof row.edgeId === 'string' && row.edgeId.length > 0 ? row.edgeId : null
  const nodeId = edgeId ? null : (typeof row.nodeId === 'string' && row.nodeId.length > 0 ? row.nodeId : 'node.yunxi-village')
  const edgeProgress = numberValue(row.edgeProgress, 0)
  return {
    mapId: stringValue(row.mapId, 'map.qingyun-mountains'),
    nodeId,
    edgeId,
    edgeProgress,
    x: numberValue(row.x, 20),
    y: numberValue(row.y, 65),
    locationName: stringValue(row.locationName, nodeId ?? '旅途中'),
    cultivationMultiplier: numberValue(row.cultivationMultiplier, 1),
    availableActionIds: requiredStrings(row.availableActionIds, 'world.availableActionIds'),
    encounterIds: requiredStrings(row.encounterIds, 'world.encounterIds'),
    npcs: mapNearbyEntities(row.npcs),
    buildings: mapNearbyEntities(row.buildings),
    nodeBuildings: mapNodeBuildings(row.nodeBuildings),
    buildableBuildingIds: Array.isArray(row.buildableBuildingIds)
      ? row.buildableBuildingIds.filter((id): id is string => typeof id === 'string')
      : null,
    minBudget: optionalWorldNumber(row.minBudget),
    maxBudget: optionalWorldNumber(row.maxBudget),
    eliteInterval: optionalWorldNumber(row.eliteInterval),
    bossInterval: optionalWorldNumber(row.bossInterval),
    monsters: mapNearbyMonsters(row.monsters),
    eliteEnemies: mapNearbyMonsters(row.eliteEnemies),
    bossEnemies: mapNearbyMonsters(row.bossEnemies),
  }
}

function mapBuffs(value: unknown): Buff[] {
  if (!Array.isArray(value)) throw new Error('buffs section 载荷必须是数组。')
  return arrayValue(value).map(item => {
    const row = object(item)
    const buffId = stringValue(row.buffId)
    if (!buffId) throw new Error('buffs section 中存在无效 buffId。')
    // 唯一 Buff 形状：ActiveBuff 平铺权威字段 + 通用属性比率字典 statRatios。
    return {
      buffId,
      name: stringValue(row.name) || buffId,
      stacks: finiteNumber(row.stacks, 'buff.stacks'),
      expiresAtUnixSeconds: finiteNumber(row.expiresAtUnixSeconds, 'buff.expiresAtUnixSeconds'),
      hpRegen: finiteNumber(row.hpRegen, 'buff.hpRegen'),
      mpRegen: finiteNumber(row.mpRegen, 'buff.mpRegen'),
      attackSpeedPoints: finiteNumber(row.attackSpeedPoints, 'buff.attackSpeedPoints'),
      damageAbsorptionPoints: finiteNumber(row.damageAbsorptionPoints, 'buff.damageAbsorptionPoints'),
      sourceItemId: stringValue(row.sourceItemId) || null,
      statRatios: row.statRatios === null || row.statRatios === undefined
        ? null
        : Object.fromEntries(Object.entries(object(row.statRatios)).map(([key, value]) => [key, finiteValue(value, `buff.statRatios.${key}`)])),
    }
  })
}

/** 战报物品条目列表解析（items / itemsLost 共用；字段缺失时容错为空列表） */
function mapBattleReportItems(value: unknown, name: string): BattleReport['items'] {
  return arrayValue(value).map((item, index) => {
    const entry = object(item)
    return {
      itemId: identifierValue(entry.itemId, `${name}[${index}].itemId`),
      quantity: finiteNumber(entry.quantity, `${name}[${index}].quantity`),
    }
  })
}

/**
 * 战报等级提升 kind 归一化：契约约定 "realm"|"root"|"ability" 字符串，
 * 同时容忍 System.Text.Json 默认数值枚举（0/1/2）；未知值条目丢弃，避免整包失败。
 */
function mapBattleReportLevelUpKind(value: unknown): BattleReport['levelUps'][number]['kind'] | null {
  if (value === 'realm' || value === 0) return 'realm'
  if (value === 'root' || value === 1) return 'root'
  if (value === 'ability' || value === 2) return 'ability'
  return null
}

function mapBattleReport(value: unknown, name: string): BattleReport {
  const row = requiredObject(value, name)
  const rootExp = object(row.rootExp)
  return {
    reportId: identifierValue(row.reportId, `${name}.reportId`),
    actionId: stringValue(row.actionId),
    actionName: stringValue(row.actionName),
    startedAtUnixSeconds: finiteNumber(row.startedAtUnixSeconds, `${name}.startedAtUnixSeconds`),
    endedAtUnixSeconds: finiteNumber(row.endedAtUnixSeconds, `${name}.endedAtUnixSeconds`),
    qi: finiteValue(row.qi, `${name}.qi`),
    experience: finiteValue(row.experience, `${name}.experience`),
    rootExp: {
      stamina: numberValue(rootExp.stamina),
      meridians: numberValue(rootExp.meridians),
      attack: numberValue(rootExp.attack),
      bone: numberValue(rootExp.bone),
      agility: numberValue(rootExp.agility),
      consciousness: numberValue(rootExp.consciousness),
    },
    abilityExp: arrayValue(row.abilityExp).flatMap((item, index) => {
      const entry = object(item)
      const abilityId = stringValue(entry.abilityId)
      // 功法 ID 缺失的脏条目直接丢弃（边界容错，不影响其余字段）。
      if (!abilityId) return []
      return [{
        abilityId,
        experience: finiteValue(entry.experience, `${name}.abilityExp[${index}].experience`),
      }]
    }),
    levelUps: arrayValue(row.levelUps).flatMap((item) => {
      const entry = object(item)
      const kind = mapBattleReportLevelUpKind(entry.kind)
      if (kind === null) return []
      return [{
        kind,
        targetId: stringValue(entry.targetId),
        fromLevel: numberValue(entry.fromLevel),
        toLevel: numberValue(entry.toLevel),
      }]
    }),
    items: mapBattleReportItems(row.items, `${name}.items`),
    itemsLost: mapBattleReportItems(row.itemsLost, `${name}.itemsLost`),
    cycles: numberValue(row.cycles),
    encounters: numberValue(row.encounters),
    normalKills: numberValue(row.normalKills),
    eliteKills: numberValue(row.eliteKills),
    bossKills: numberValue(row.bossKills),
    deaths: numberValue(row.deaths),
    dayIndex: numberValue(row.dayIndex),
    duelWins: numberValue(row.duelWins),
    duelLosses: numberValue(row.duelLosses),
  }
}

/** 可空账本字段解析：null/undefined → null，其余按 BattleReport 校验 */
function mapOptionalBattleReport(value: unknown, name: string): BattleReport | null {
  if (value === null || value === undefined) return null
  return mapBattleReport(value, name)
}

function mapDuel(value: unknown): DuelState {
  const row = requiredObject(value, 'duel')
  return {
    encounterCharges: finiteNumber(row.encounterCharges, 'duel.encounterCharges'),
    cooldownUntilUnixSeconds: finiteNumber(row.cooldownUntilUnixSeconds, 'duel.cooldownUntilUnixSeconds'),
    activeDuelId: optionalString(row.activeDuelId, 'duel.activeDuelId'),
    stagedItems: arrayValue(row.stagedItems).map((item, index) => {
      const entry = object(item)
      return {
        itemId: identifierValue(entry.itemId, `duel.stagedItems[${index}].itemId`),
        quantity: finiteNumber(entry.quantity, `duel.stagedItems[${index}].quantity`),
        acquiredAtUnixSeconds: finiteNumber(entry.acquiredAtUnixSeconds, `duel.stagedItems[${index}].acquiredAtUnixSeconds`),
      }
    }),
    currentReport: mapOptionalBattleReport(row.currentReport, 'duel.currentReport'),
    // 离线挂机与每日统计单例账本（reportId 固定 "offline"/"daily"）。
    offlineReport: mapOptionalBattleReport(row.offlineReport, 'duel.offlineReport'),
    dailyReport: mapOptionalBattleReport(row.dailyReport, 'duel.dailyReport'),
    // activeReports 为在队行动实例账本列表：前端面板不消费，解析层直接忽略。
    reports: arrayValue(row.reports).map((item, index) => mapBattleReport(item, `duel.reports[${index}]`)),
  }
}

function mapAsset(asset: AssetBalanceDto): InventoryItem {
  if (!asset || typeof asset.assetId !== 'string' || asset.assetId.length === 0 || !Number.isFinite(asset.quantity) || asset.quantity < 0) {
    throw new Error('玩家资产余额载荷无效。')
  }
  const metadata = resolveItemMetadata(asset.assetId)
  return {
    itemId: asset.assetId,
    variant: asset.variant ?? null,
    name: metadata?.label ?? asset.assetId,
    category: metadata?.category ?? 'general',
    amount: asset.quantity,
    tone: metadata?.tone ?? 'gray',
    glyph: metadata?.glyph ?? '◈',
    tier: metadata?.level ?? 1,
    description: metadata?.description,
    isTradable: true,
  }
}

function mapAssets(assets: readonly AssetBalanceDto[], includeZero = false): InventoryItem[] {
  return assets.map(mapAsset).filter(asset => includeZero || asset.amount !== 0)
}

/**
 * 资产行唯一键：同一物品不同变体（如强化等级）是独立的余额行，合入键必须包含 variant，
 * 否则多行同名资产会在增量合入时互相覆盖。
 */
function assetKeyOf(itemId: string, variant: string | null | undefined): string {
  return variant ? `${itemId}\u001F${variant}` : itemId
}

/**
 * 背包渲染顺序与服务端权威顺序对齐：asset_id 字典序（Ordinal）优先、variant 次之。
 * 增量合入后按此重排，保证增量视图与全量快照顺序完全一致——
 * 数量增减只更新数值不搬移位置，只有新增/清零才引起列表进出。
 */
function compareInventoryItems(left: InventoryItem, right: InventoryItem): number {
  if (left.itemId !== right.itemId) return left.itemId < right.itemId ? -1 : 1
  const leftVariant = left.variant ?? ''
  const rightVariant = right.variant ?? ''
  return leftVariant === rightVariant ? 0 : leftVariant < rightVariant ? -1 : 1
}

function seconds(iso: string): number {
  const parsed = Date.parse(iso)
  if (!Number.isFinite(parsed)) throw new Error('玩家快照时间戳无效。')
  return parsed / 1000
}

function visualFor(name: string): CharacterVisual {
  return { renderer: 'text', source: name.slice(0, 1) || '修' }
}

/**
 * 解码并严格校验服务端下发的全量玩家快照 DTO。
 * 流程：
 * 1. 校验基础字段存在性与版本匹配；
 * 2. 校验整体 Head Checksum 摘要；
 * 3. 并行解码并逐段校验各 Section 的 SHA-256 校验和；
 * 4. 解析转换为前端 PlayerSnapshot 完整聚合状态。
 */
export async function decodePlayerSnapshotDto(dto: PlayerSnapshotDto, account = ''): Promise<PlayerSnapshot> {
  if (!dto || typeof dto.playerId !== 'string' || !dto.playerId) throw new Error('玩家快照缺少 playerId。')
  const stateVersion = stateVersionValue(dto.stateVersion, '玩家快照')
  if (!dto.head || dto.head.schemaVersion !== 3) throw new Error('玩家快照 schema 版本不受支持。')
  if (dto.head.playerId !== dto.playerId || dto.head.stateVersion !== stateVersion) throw new Error('玩家快照头与主体版本不一致。')
  if (!dto.displayName) throw new Error('玩家快照缺少 displayName。')
  if (!Array.isArray(dto.sections) || !Array.isArray(dto.assets)) throw new Error('玩家快照 sections/assets 载荷无效。')
  await verifyHeadChecksum(dto)
  const sections = await decodeSections(dto.sections)
  for (const section of REQUIRED_SNAPSHOT_SECTIONS) {
    if (!sections.has(section)) throw new Error(`玩家快照缺少 ${section} section。`)
  }
  const settingsEnvelope = requiredObject(sections.get('settings'), 'settings')
  const settings = requiredObject(settingsEnvelope.settings, 'settings.settings')
  const progression = requiredObject(sections.get('progression'), 'progression')
  const world = mapWorld(requiredObject(sections.get('world'), 'world'))
  const combat = mapCombat(requiredObject(sections.get('combat'), 'combat'))
  const loadout = mapLoadout(requiredObject(sections.get('loadout'), 'loadout'))
  const duel = mapDuel(sections.get('duel'))
  if (!Array.isArray(sections.get('buffs')) || !Array.isArray(sections.get('action_queue'))) {
    throw new Error('玩家快照 buffs/action_queue section 载荷无效。')
  }
  const name = dto.displayName
  const serverTime = seconds(dto.lastSimulatedAt)
  const stats = mapStats(progression)
  const statsRealm = calculateRealmProgress(stats.spirit, stats.realmLevel)
  const renameCooldownUntil = numberValue(settings.renameCooldownUntilUnixSeconds)
  return {
    playerId: dto.playerId,
    name,
    account,
    version: stateVersion,
    serverTimeUnixSeconds: serverTime,
    serverTimeUnixMilliseconds: serverTime * 1000,
    createdAtUnixSeconds: serverTime,
    presenceMode: mapPresence(settingsEnvelope.presence),
    queue: mapQueue(sections.get('action_queue'), settings),
    inventory: mapAssets(dto.assets),
    buffs: mapBuffs(sections.get('buffs')),
    stats,
    combat: loadout,
    combatResources: combat.resources,
    combatAttributes: combat.attributes,
    visual: visualFor(name),
    world,
    duel,
    // 待突破 = 境界经验已填满当前层所需（容差 0.0001 吸收浮点误差）且确有修为存量
    isPendingBreakthrough: statsRealm.expToNext > 0 && statsRealm.expToNext - statsRealm.currentLayerExp <= 0.0001 && stats.spirit > 0,
    renameCooldownSeconds: Math.max(0, renameCooldownUntil - serverTime),
  }
}

/**
 * 解码服务端下发的增量快照补丁 DTO。
 * 只反序列化本次发生变更的 Section 与资产差量，生成轻量级的 PlayerPatch 对象供状态机就地合入。
 */
export async function decodePlayerSnapshotDeltaDto(
  dto: PlayerSnapshotDeltaDto,
  current: PlayerSnapshot,
): Promise<PlayerPatch> {
  if (!dto || dto.playerId !== current.playerId) throw new Error('玩家增量快照归属不一致。')
  const stateVersion = stateVersionValue(dto.stateVersion, '玩家增量快照')
  if (!Array.isArray(dto.changedSections)) throw new Error('玩家增量 section 列表无效。')
  const sections = await decodeSections(dto.changedSections)
  // 服务端权威时刻必须随每个增量刷新：行动倒计时与进度条以「服务端时刻 + 本地流逝」推算，
  // 若沿用上一次全量快照的时间戳，估算时钟偏移会随页面停留时长线性累积。
  // 每个增量必须提供可信的新时间锚点，缺失即协议错误。
  const simulatedAt = optionalString(dto.lastSimulatedAt, '玩家增量快照 lastSimulatedAt')
  if (simulatedAt === null) throw new Error('玩家增量快照缺少 lastSimulatedAt。')
  const serverTime = seconds(simulatedAt)
  const patch: PlayerPatch = {
    playerId: dto.playerId,
    version: stateVersion,
    serverTimeUnixSeconds: serverTime,
    serverTimeUnixMilliseconds: serverTime * 1000,
  }
  const settingsEnvelope = sections.has('settings') ? requiredObject(sections.get('settings'), 'settings') : undefined
  const settings = settingsEnvelope ? requiredObject(settingsEnvelope.settings, 'settings.settings') : undefined
  if (settingsEnvelope && settings) {
    patch.presenceMode = mapPresence(settingsEnvelope.presence)
    patch.queue = { ...current.queue, loopEnabled: booleanValue(settings.loopEnabled, true), autoUseSlots: mapAutoUseSlots(settings.autoUseSlots) }
    patch.renameCooldownSeconds = Math.max(0, numberValue(settings.renameCooldownUntilUnixSeconds) - serverTime)
  }
  if (sections.has('progression')) {
    patch.stats = mapStats(sections.get('progression'))
    const realm = calculateRealmProgress(patch.stats.spirit, patch.stats.realmLevel)
    patch.isPendingBreakthrough = realm.expToNext > 0 && realm.expToNext - realm.currentLayerExp <= 0.0001 && patch.stats.spirit > 0
  }
  if (sections.has('world')) patch.world = mapWorld(sections.get('world'))
  if (sections.has('combat')) {
    const combat = mapCombat(sections.get('combat'))
    patch.combatResources = combat.resources
    patch.combatAttributes = combat.attributes
  }
  if (sections.has('loadout')) patch.combat = mapLoadout(sections.get('loadout'))
  if (sections.has('duel')) patch.duel = mapDuel(sections.get('duel'))
  if (sections.has('buffs')) patch.buffs = mapBuffs(sections.get('buffs'))
  if (sections.has('action_queue')) patch.queue = mapQueue(sections.get('action_queue'), settings, current.queue)
  if (dto.changedAssets !== null && dto.changedAssets !== undefined) {
    if (!Array.isArray(dto.changedAssets)) throw new Error('玩家资产增量载荷无效。')
    // 按 (itemId, variant) 复合键原位合入：数量变动只更新数值，不清空原位置再追加到尾部，
    // 否则每个采集/消耗 tick 都会把变动物品甩到背包末尾（界面表现为物品频繁跳位）。
    const merged = new Map(current.inventory.map(item => [assetKeyOf(item.itemId, item.variant), item]))
    for (const item of mapAssets(dto.changedAssets, true)) {
      const key = assetKeyOf(item.itemId, item.variant)
      if (item.amount === 0) merged.delete(key)
      else merged.set(key, item)
    }
    patch.inventory = Array.from(merged.values()).sort(compareInventoryItems)
  }
  return patch
}
