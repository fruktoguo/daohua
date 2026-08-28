import { resolveActionDefinition, resolveItemMetadata } from '../game/ids'
import type {
  ActionType,
  AutoUseSlot,
  Buff,
  CharacterVisual,
  CombatAttributes,
  CombatLoadout,
  CombatResourceState,
  InventoryItem,
  PlayerPatch,
  PlayerSnapshot,
  QueueEntry,
  QueueState,
  Stats,
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
    if (section.schemaVersion !== 1) throw new Error(`不支持的 section schema 版本: ${section.section} v${section.schemaVersion}。`)
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

function mapAutoUseSlots(value: unknown): QueueState['autoUseSlots'] {
  if (!Array.isArray(value)) throw new Error('settings.autoUseSlots 必须是数组。')
  const autoUseSlots = defaultAutoUseSlots()
  for (const raw of arrayValue(value)) {
    const slot = object(raw)
    if (Object.keys(slot).length === 0) throw new Error('settings.autoUseSlots 中存在无效槽位。')
    const actionType = stringValue(slot.actionType) as ActionType
    const slotIndex = numberValue(slot.slotIndex, -1)
    if (!ACTION_TYPES.includes(actionType) || !Number.isInteger(slotIndex) || slotIndex < 0 || slotIndex >= 3) continue
    if ((slot.itemId !== null && typeof slot.itemId !== 'string') || typeof slot.quantity !== 'number' || !Number.isFinite(slot.quantity) || slot.quantity <= 0 || typeof slot.enabled !== 'boolean') {
      throw new Error('settings.autoUseSlots 槽位字段无效。')
    }
    autoUseSlots[actionType][slotIndex] = {
      itemId: slot.itemId || null,
      quantity: Math.max(1, slot.quantity),
      enabled: slot.enabled,
      remainingDurationSeconds: 0,
      triggers: [],
    }
  }
  return autoUseSlots
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
    const statusRaw = row.status
    const status = statusRaw === 0 ? 'queued' : statusRaw === 1 ? 'running' : statusRaw === 2 ? 'blocked' : (() => { throw new Error(`行动 ${actionId} 状态无效。`) })()
    const entryId = stringValue(row.id)
    if (!entryId) throw new Error(`行动 ${actionId} 缺少实例 ID。`)
    const entry: QueueEntry = {
      entryId,
      actionId,
      actionName: definition?.name ?? actionId,
      type: definition?.type ?? 'cultivation',
      status,
      progressSeconds: numberValue(row.progressSeconds),
      durationSeconds: definition?.durationSeconds ?? 0,
      remainingRepeats: numberValue(row.remainingRepeats, numberValue(intent.repeatCount, 1)),
      executionKind: definition?.executionKind,
      travelDestinationNodeId: stringValue(intent.targetNodeId) || null,
      travelPathEdgeIds: null,
      travelSegments: null,
      expedition: null,
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
    qi: numberValue(row.qi),
    // 服务端 progression 只有一个经验字段 Experience（突破时清零），即境界修为 spirit；
    // 不存在独立的道基底蕴字段，foundationExp 保持未定义。
    spirit: numberValue(row.experience),
    herbsCollected: numberValue(row.herbsCollected),
    pillsCrafted: numberValue(row.pillsCrafted),
    cultivationCycles: numberValue(row.cultivationCycles),
    completedActions: numberValue(row.completedActions),
    blockedActions: numberValue(row.blockedActions),
    combatWins: numberValue(row.combatWins),
    combatLosses: numberValue(row.combatLosses),
    combatExperience: numberValue(row.combatExperience),
    combatDamageDealt: numberValue(row.combatDamageDealt),
    combatDamageTaken: numberValue(row.combatDamageTaken),
  }
}

function mapCombat(value: unknown): { resources: CombatResourceState; attributes: CombatAttributes } {
  const row = object(value)
  const hitpoints = numberValue(row.hitpoints, 100)
  const maxHitpoints = numberValue(row.maxHitpoints, 100)
  const manapoints = numberValue(row.manapoints, 100)
  const maxManapoints = numberValue(row.maxManapoints, 100)
  const resources: CombatResourceState = { hitpoints, maxHitpoints, manapoints, maxManapoints }
  const attributes: CombatAttributes = {
    maxHitpoints,
    maxManapoints,
    attackIntervalMilliseconds: 1000,
    accuracyRating: 0,
    maxDamage: numberValue(row.attackPower, 10),
    evasionRating: 0,
    armor: numberValue(row.defense, 10),
    magicResistance: 0,
    metalResistance: 0,
    woodResistance: 0,
    waterResistance: 0,
    fireResistance: 0,
    earthResistance: 0,
    style: 'sword',
    category: 'physical',
    element: 'none',
    tenacity: 0,
    hpRegenPerTenSeconds: 0,
    mpRegenPerTenSeconds: 0,
    penetrationRatio: 0,
    lifeStealRatio: 0,
    armorDamageRatio: 0,
    hpDrainRatio: 0,
    damageTakenRatio: 0,
    criticalChance: 0,
    criticalDamageRatio: 1.5,
    criticalAvoidance: 0,
    damageAbsorptionRatio: 0,
  }
  return { resources, attributes }
}

function mapLoadout(value: unknown): CombatLoadout {
  const row = object(value)
  if (!Array.isArray(row.learnedAbilities) || !Array.isArray(row.equippedItemIds)) {
    throw new Error('loadout section 载荷字段无效。')
  }
  const learnedAbilityIds = arrayValue(row.learnedAbilities).map(value => identifierValue(value, '功法 ID'))
  const equippedItemIds = arrayValue(row.equippedItemIds).map(value => identifierValue(value, '装备 ID'))
  return {
    staminaLevel: 1,
    intelligenceLevel: 1,
    attackLevel: 1,
    defenseLevel: 1,
    swordsmanshipLevel: 1,
    talismanLevel: 1,
    abilitySlots: learnedAbilityIds.map(abilityId => ({ abilityId, level: 1, triggers: [] })),
    consumableSlots: [],
    equipment: equippedItemIds.map(itemId => ({ itemId, enhancementLevel: 0 })),
    learnedAbilityIds,
  }
}

function mapWorld(value: unknown): WorldState {
  const row = object(value)
  const nodeId = stringValue(row.nodeId, 'node.yunxi-village')
  return {
    mapId: stringValue(row.mapId, 'map.qingyun-mountains'),
    nodeId,
    edgeId: null,
    edgeProgress: 0,
    x: numberValue(row.x, 20),
    y: numberValue(row.y, 65),
    locationName: stringValue(row.locationName, nodeId),
    cultivationMultiplier: numberValue(row.cultivationMultiplier, 1),
    availableActionIds: [],
    encounterIds: [],
    npcs: [],
    buildings: [],
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
      name: stringValue(row.name, buffId),
      stacks: numberValue(row.stacks, 1),
      qiGainMultiplier: numberValue(row.qiGainMultiplier, 1),
      expiresAtUnixSeconds: numberValue(row.expiresAtUnixSeconds),
      attackLevelRatio: numberValue(row.attackLevelRatio),
      damageRatio: numberValue(row.damageRatio),
      attackSpeedRatio: numberValue(row.attackSpeedRatio),
      gatheringOutputMultiplier: numberValue(row.gatheringOutputMultiplier, 1),
      alchemyOutputMultiplier: numberValue(row.alchemyOutputMultiplier, 1),
      sourceItemId: stringValue(row.sourceItemId) || null,
      modifiers: object(row.modifiers) as Record<string, number>,
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

export async function decodePlayerSnapshotDto(dto: PlayerSnapshotDto, account = ''): Promise<PlayerSnapshot> {
  if (!dto || typeof dto.playerId !== 'string' || !dto.playerId) throw new Error('玩家快照缺少 playerId。')
  const stateVersion = stateVersionValue(dto.stateVersion, '玩家快照')
  if (!dto.head || dto.head.schemaVersion !== 1) throw new Error('玩家快照 schema 版本不受支持。')
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

export async function decodePlayerSnapshotDeltaDto(
  dto: PlayerSnapshotDeltaDto,
  current: PlayerSnapshot,
): Promise<PlayerPatch> {
  if (!dto || dto.playerId !== current.playerId) throw new Error('玩家增量快照归属不一致。')
  const stateVersion = stateVersionValue(dto.stateVersion, '玩家增量快照')
  if (!Array.isArray(dto.changedSections)) throw new Error('玩家增量 section 列表无效。')
  const sections = await decodeSections(dto.changedSections)
  const patch: PlayerPatch = {
    playerId: dto.playerId,
    version: stateVersion,
    serverTimeUnixSeconds: current.serverTimeUnixSeconds,
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
