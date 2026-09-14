/**
 * 道化太初客户端快照解码与校验引擎。
 *
 * 核心安全与完整性职责：
 * 1. 对服务端下发的 Base64 编码 Section 载荷执行 SHA-256 完整性哈希校验；
 * 2. 严格核对 Snapshot 头校验和（Head Checksum），防止数据篡改或网络坏帧；
 * 3. 将扁平的分段载荷（Progression, World, Combat, Buffs, Loadout, Settings, ActionQueue）及资产钱包投影为前端响应式 PlayerSnapshot 与 PlayerPatch。
 */
import { resolveActionDefinition, resolveItemMetadata } from '../game/ids'
import type {
  ActionType,
  AutoUseSlot,
  Buff,
  CharacterVisual,
  CombatAttributes,
  CombatLoadout,
  CombatResourceState,
  CombatTrigger,
  ExpeditionPhase,
  ExpeditionProgress,
  InventoryItem,
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

const ACTION_TYPES: readonly ActionType[] = ['cultivation', 'exploration', 'alchemy', 'combat']
const REQUIRED_SNAPSHOT_SECTIONS = ['progression', 'world', 'combat', 'buffs', 'loadout', 'settings', 'action_queue'] as const

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

function base64ToBytes(value: string): Uint8Array {
  const normalized = value.replaceAll('-', '+').replaceAll('_', '/')
  const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, '=')
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
  const text = new TextDecoder().decode(raw)
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
    if (section.schemaVersion !== 2) throw new Error(`不支持的 section schema 版本: ${section.section} v${section.schemaVersion}。`)
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
  return { itemId: null, quantity: 1, enabled: false, remainingDurationSeconds: 0, triggers: [] }
}

function defaultAutoUseSlots(): QueueState['autoUseSlots'] {
  return Object.fromEntries(ACTION_TYPES.map(type => [type, Array.from({ length: 3 }, emptySlot)])) as QueueState['autoUseSlots']
}

function mapExpeditionPhase(value: unknown): ExpeditionPhase {
  if (typeof value === 'number') {
    if (!Number.isSafeInteger(value)) throw new Error('战斗远征 phase 枚举值无效。')
    switch (value) {
      case 0: return 'initial-search'
      case 1: return 'search'
      case 2: return 'combat'
      case 3: return 'respawn'
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

function mapExpedition(value: unknown, actionId: string): ExpeditionProgress {
  const row = object(value)
  if (Object.keys(row).length === 0) throw new Error(`行动 ${actionId} 的战斗远征载荷无效。`)
  const encounterId = identifierValue(row.encounterId, `行动 ${actionId} 的 encounterId`)
  const phase = mapExpeditionPhase(row.phase)
  const elapsedSeconds = finiteNumber(row.phaseElapsedSeconds, `行动 ${actionId} 的 phaseElapsedSeconds`)
  const durationSeconds = finiteNumber(
    row.phaseDurationSeconds,
    `行动 ${actionId} 的 phaseDurationSeconds`,
    phase === 'combat' ? 0 : Number.MIN_VALUE,
  )
  if (phase !== 'combat' && elapsedSeconds > durationSeconds + 1e-9) throw new Error(`行动 ${actionId} 的战斗远征阶段进度超出范围。`)
  const playerHitpoints = finiteNumber(row.playerHitpoints, `行动 ${actionId} 的 playerHitpoints`)
  const enemyHitpoints = finiteNumber(row.enemyHitpoints, `行动 ${actionId} 的 enemyHitpoints`)

  // 这些标量用于恢复/审计，即使当前界面不直接展示，也必须拒绝坏值，避免把损坏快照继续传播。
  finiteValue(row.seed, `行动 ${actionId} 的 seed`)
  finiteNumber(row.battleElapsedSeconds, `行动 ${actionId} 的 battleElapsedSeconds`)
  finiteNumber(row.playerNextAttackAtSeconds, `行动 ${actionId} 的 playerNextAttackAtSeconds`)
  finiteNumber(row.enemyNextAttackAtSeconds, `行动 ${actionId} 的 enemyNextAttackAtSeconds`)
  finiteNumber(row.playerAttackCount, `行动 ${actionId} 的 playerAttackCount`)
  finiteNumber(row.enemyAttackCount, `行动 ${actionId} 的 enemyAttackCount`)
  const outcome = finiteInteger(row.outcome, `行动 ${actionId} 的 outcome`)
  if (outcome > 3) throw new Error(`行动 ${actionId} 的 outcome 枚举值无效。`)

  return {
    phase,
    progress: durationSeconds > 0 ? Math.max(0, Math.min(1, elapsedSeconds / durationSeconds)) : 0,
    elapsedSeconds,
    durationSeconds,
    playerHitpoints,
    enemyHitpoints,
    enemyId: optionalString(row.enemyId, `行动 ${actionId} 的 enemyId`),
    encounterId,
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
    if (!ACTION_TYPES.includes(actionType) || !Number.isInteger(slotIndex) || slotIndex < 0 || slotIndex >= 3) continue
    // 服务端以 JsonIgnoreCondition.WhenWritingNull 序列化，未指定物品时可空 itemId 会被整键省略，
    // 因此缺失与 null 必须视为同一语义（未配置物品），而非坏数据。
    if ((slot.itemId !== null && slot.itemId !== undefined && typeof slot.itemId !== 'string') || typeof slot.quantity !== 'number' || !Number.isFinite(slot.quantity) || slot.quantity <= 0 || typeof slot.enabled !== 'boolean') {
      throw new Error('settings.autoUseSlots 槽位字段无效。')
    }
    const remainingDurationSeconds = finiteNumber(
      slot.remainingDurationSeconds,
      `settings.autoUseSlots[${actionType}:${slotIndex}].remainingDurationSeconds`,
    )
    autoUseSlots[actionType][slotIndex] = {
      itemId: slot.itemId || null,
      quantity: Math.max(1, slot.quantity),
      enabled: slot.enabled,
      remainingDurationSeconds,
      triggers: [],
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
    const travelPathEdgeIds = Array.isArray(row.travelPathEdgeIds)
      ? row.travelPathEdgeIds.map(id => stringValue(id)).filter(id => id.length > 0)
      : null
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
      travelPathEdgeIds,
      travelSegments,
      expedition,
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
  return {
    realmLevel: numberValue(row.realmLevel, 1),
    qi: numberValue(row.qi),
    // 服务端 progression 只有一个经验字段 Experience（突破时清零），即境界修为 spirit；
    // 不存在独立的道基底蕴字段，foundationExp 保持未定义。
    spirit: numberValue(row.experience),
    completedActions: numberValue(row.completedActions),
  }
}

function enumValue<T extends string>(value: unknown, values: readonly T[], name: string): T {
  if (typeof value !== 'string' || !values.includes(value as T)) throw new Error(`${name} 枚举无效。`)
  return value as T
}

function mapCombat(value: unknown): { resources: CombatResourceState; attributes: CombatAttributes } {
  const row = requiredObject(value, 'combat')
  const source = requiredObject(row.attributes, 'combat.attributes')
  const numericKeys = [
    'maxHitpoints', 'maxManapoints', 'attackIntervalMilliseconds', 'accuracyRating', 'maxDamage',
    'evasionRating', 'armor', 'magicResistance', 'metalResistance', 'woodResistance', 'waterResistance',
    'fireResistance', 'earthResistance', 'tenacity', 'hpRegenPerFiveSeconds', 'mpRegenPerFiveSeconds',
    'penetrationRatio', 'lifeStealRatio', 'armorDamageRatio', 'hpDrainRatio', 'damageTakenRatio',
    'criticalChance', 'criticalDamageRatio', 'criticalAvoidance', 'damageAbsorptionRatio',
    'criticalRating', 'criticalAvoidanceRating',
  ] as const
  const numbers = Object.fromEntries(numericKeys.map(key => [key, finiteValue(source[key], `combat.attributes.${key}`)])) as Pick<CombatAttributes, typeof numericKeys[number]>
  const attributes: CombatAttributes = {
    ...numbers,
    style: enumValue(source.style, ['sword', 'blade', 'talisman', 'five-elements', 'body'], 'combat.style'),
    category: enumValue(source.category, ['physical', 'spell'], 'combat.category'),
    element: enumValue(source.element, ['none', 'metal', 'wood', 'water', 'fire', 'earth'], 'combat.element'),
  }
  return {
    resources: {
      hitpoints: finiteNumber(row.hitpoints, 'combat.hitpoints'),
      manapoints: finiteNumber(row.manapoints, 'combat.manapoints'),
      maxHitpoints: attributes.maxHitpoints,
      maxManapoints: attributes.maxManapoints,
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
  if (!Array.isArray(row.abilitySlots) || !Array.isArray(row.equipment) || !Array.isArray(row.consumableSlots)) {
    throw new Error('loadout section 槽位载荷无效。')
  }
  return {
    staminaLevel: finiteNumber(core.staminaLevel, 'loadout.staminaLevel'),
    intelligenceLevel: finiteNumber(core.intelligenceLevel, 'loadout.intelligenceLevel'),
    attackLevel: finiteNumber(core.attackLevel, 'loadout.attackLevel'),
    defenseLevel: finiteNumber(core.defenseLevel, 'loadout.defenseLevel'),
    swordsmanshipLevel: finiteNumber(core.swordsmanshipLevel, 'loadout.swordsmanshipLevel'),
    talismanLevel: finiteNumber(core.talismanLevel, 'loadout.talismanLevel'),
    abilitySlots: row.abilitySlots.map(item => {
      const slot = requiredObject(item, 'loadout.abilitySlot')
      return { abilityId: optionalString(slot.abilityId, 'abilitySlot.abilityId'), level: finiteNumber(slot.level, 'abilitySlot.level'), triggers: mapTriggers(slot.triggers) }
    }),
    consumableSlots: row.consumableSlots.map(item => {
      const slot = requiredObject(item, 'loadout.consumableSlot')
      if (typeof slot.enabled !== 'boolean') throw new Error('consumableSlot.enabled 无效。')
      return { itemId: optionalString(slot.itemId, 'consumableSlot.itemId'), quantity: finiteNumber(slot.quantity, 'consumableSlot.quantity'), enabled: slot.enabled, triggers: mapTriggers(slot.triggers) }
    }),
    equipment: row.equipment.map(item => {
      const equipment = requiredObject(item, 'loadout.equipment')
      return { itemId: identifierValue(equipment.itemId, 'equipment.itemId'), enhancementLevel: finiteNumber(equipment.enhancementLevel, 'equipment.enhancementLevel') }
    }),
    learnedAbilityIds: requiredStrings(row.learnedAbilityIds, 'loadout.learnedAbilityIds'),
  }
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
  }
}

function mapBuffs(value: unknown): Buff[] {
  if (!Array.isArray(value)) throw new Error('buffs section 载荷必须是数组。')
  return arrayValue(value).map(item => {
    const row = object(item)
    const buffId = stringValue(row.buffId)
    if (!buffId) throw new Error('buffs section 中存在无效 buffId。')
    return {
      buffId,
      name: identifierValue(row.name, 'buff.name'),
      stacks: finiteNumber(row.stacks, 'buff.stacks'),
      qiGainMultiplier: finiteNumber(row.qiGainMultiplier, 'buff.qiGainMultiplier'),
      expiresAtUnixSeconds: finiteNumber(row.expiresAtUnixSeconds, 'buff.expiresAtUnixSeconds'),
      attackLevelRatio: finiteValue(row.attackLevelRatio, 'buff.attackLevelRatio'),
      damageRatio: finiteValue(row.damageRatio, 'buff.damageRatio'),
      attackSpeedRatio: finiteValue(row.attackSpeedRatio, 'buff.attackSpeedRatio'),
      gatheringOutputMultiplier: finiteNumber(row.gatheringOutputMultiplier, 'buff.gatheringOutputMultiplier'),
      alchemyOutputMultiplier: finiteNumber(row.alchemyOutputMultiplier, 'buff.alchemyOutputMultiplier'),
      sourceItemId: stringValue(row.sourceItemId) || null,
      modifiers: Object.fromEntries(Object.entries(object(row.modifiers)).map(([key, value]) => [key, finiteValue(value, `buff.modifiers.${key}`)])),
    }
  })
}

function mapAsset(asset: AssetBalanceDto): InventoryItem {
  if (!asset || typeof asset.assetId !== 'string' || asset.assetId.length === 0 || !Number.isFinite(asset.quantity) || asset.quantity < 0) {
    throw new Error('玩家资产余额载荷无效。')
  }
  const metadata = resolveItemMetadata(asset.assetId)
  return {
    itemId: asset.assetId,
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
  if (!dto.head || dto.head.schemaVersion !== 2) throw new Error('玩家快照 schema 版本不受支持。')
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
  if (!Array.isArray(sections.get('buffs')) || !Array.isArray(sections.get('action_queue'))) {
    throw new Error('玩家快照 buffs/action_queue section 载荷无效。')
  }
  const name = dto.displayName
  const serverTime = seconds(dto.lastSimulatedAt)
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
    stats: mapStats(progression),
    combat: loadout,
    combatResources: combat.resources,
    combatAttributes: combat.attributes,
    visual: visualFor(name),
    world,
    isPendingBreakthrough: false,
    renameCooldownSeconds: 0,
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
  }
  if (sections.has('progression')) patch.stats = mapStats(sections.get('progression'))
  if (sections.has('world')) patch.world = mapWorld(sections.get('world'))
  if (sections.has('combat')) {
    const combat = mapCombat(sections.get('combat'))
    patch.combatResources = combat.resources
    patch.combatAttributes = combat.attributes
  }
  if (sections.has('loadout')) patch.combat = mapLoadout(sections.get('loadout'))
  if (sections.has('buffs')) patch.buffs = mapBuffs(sections.get('buffs'))
  if (sections.has('action_queue')) patch.queue = mapQueue(sections.get('action_queue'), settings, current.queue)
  if (dto.changedAssets !== null && dto.changedAssets !== undefined) {
    if (!Array.isArray(dto.changedAssets)) throw new Error('玩家资产增量载荷无效。')
    const changed = new Map(mapAssets(dto.changedAssets, true).map(item => [item.itemId, item]))
    patch.inventory = current.inventory.filter(item => !changed.has(item.itemId)).concat(Array.from(changed.values()).filter(item => item.amount !== 0))
  }
  return patch
}
