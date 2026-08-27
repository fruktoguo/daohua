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
const EMPTY_SLOT: AutoUseSlot = { itemId: null, quantity: 1, enabled: false, remainingDurationSeconds: 0, triggers: [] }

function object(value: unknown): JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {}
}

function stringValue(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function numberValue(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function booleanValue(value: unknown, fallback = false): boolean {
  return typeof value === 'boolean' ? value : fallback
}

function arrayValue(value: unknown): unknown[] {
  return Array.isArray(value) ? value : []
}

/** System.Text.Json 序列化 record struct ID 时可能是字符串或 { value } 对象；协议映射统一取稳定文本值。 */
function identifierValue(value: unknown): string {
  if (typeof value === 'string') return value
  const candidate = object(value)
  return stringValue(candidate.value)
}

function base64ToBytes(value: string): Uint8Array {
  const normalized = value.replaceAll('-', '+').replaceAll('_', '/')
  const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, '=')
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index)
  return bytes
}

interface BrotliStreamConstructor {
  new (format: string): TransformStream<Uint8Array, Uint8Array>
}

async function decodeSectionPayload(section: PlayerSectionDto): Promise<unknown> {
  const encoded = base64ToBytes(section.payloadBase64)
  if (section.storedBytes > 0 && encoded.byteLength !== section.storedBytes) {
    throw new Error(`section ${section.section} 存储长度校验失败。`)
  }
  let raw = encoded
  if (section.codec === 'br-json') {
    const ctor = (globalThis as unknown as { DecompressionStream?: BrotliStreamConstructor }).DecompressionStream
    if (!ctor) throw new Error('当前浏览器不支持 br-json section 解码。')
    const stream = new Blob([encoded.slice().buffer as ArrayBuffer]).stream().pipeThrough(new ctor('br'))
    raw = new Uint8Array(await new Response(stream).arrayBuffer())
  } else if (section.codec !== 'json') {
    throw new Error(`不支持的 section codec: ${section.codec}`)
  }
  if (section.rawBytes > 0 && raw.byteLength !== section.rawBytes) {
    throw new Error(`section ${section.section} 原始长度校验失败。`)
  }
  if (section.checksum) {
    const digest = await crypto.subtle.digest('SHA-256', raw.slice().buffer as ArrayBuffer)
    const checksum = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
    if (checksum !== section.checksum.toLowerCase()) throw new Error(`section ${section.section} 校验和不匹配。`)
  }
  const text = new TextDecoder().decode(raw)
  return JSON.parse(text) as unknown
}

async function decodeSections(sections: readonly PlayerSectionDto[]): Promise<Map<string, unknown>> {
  for (const section of sections) {
    if (section.schemaVersion !== 1) throw new Error(`不支持的 section schema 版本: ${section.section} v${section.schemaVersion}。`)
  }
  const decoded = await Promise.all(sections.map(async section => [section.section, await decodeSectionPayload(section)] as const))
  return new Map(decoded)
}

async function verifyHeadChecksum(dto: PlayerSnapshotDto): Promise<void> {
  const expected = dto.head.checksum?.toLowerCase()
  if (!expected) return
  const encoded = dto.sections.map(section => base64ToBytes(section.payloadBase64))
  const total = encoded.reduce((size, bytes) => size + bytes.byteLength, 0)
  const joined = new Uint8Array(total)
  let offset = 0
  for (const bytes of encoded) {
    joined.set(bytes, offset)
    offset += bytes.byteLength
  }
  const digest = await crypto.subtle.digest('SHA-256', joined.slice().buffer as ArrayBuffer)
  const actual = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
  if (actual !== expected) throw new Error('玩家快照头校验和不匹配。')
}

function mapPresence(value: unknown): PlayerSnapshot['presenceMode'] {
  if (typeof value === 'string') {
    if (value === 'online') return 'online'
    if (value === 'suspended') return 'idle-offline'
    if (value === 'offline') return 'fully-offline'
  }
  if (numberValue(value) === 1) return 'online'
  if (numberValue(value) === 2) return 'idle-offline'
  return 'fully-offline'
}

function defaultAutoUseSlots(): QueueState['autoUseSlots'] {
  return Object.fromEntries(ACTION_TYPES.map(type => [type, Array.from({ length: 3 }, () => ({ ...EMPTY_SLOT }))])) as QueueState['autoUseSlots']
}

function mapQueue(value: unknown): QueueState {
  const entries = arrayValue(value).map(item => {
    const row = object(item)
    const intent = object(row.intent)
    const actionId = identifierValue(intent.actionId) || stringValue(row.actionId, 'cultivation.draw-qi')
    const definition = resolveActionDefinition(actionId)
    const statusRaw = row.status
    const status = statusRaw === 1 || statusRaw === 'running' ? 'running' : statusRaw === 2 || statusRaw === 'blocked' ? 'blocked' : 'queued'
    const entry: QueueEntry = {
      entryId: identifierValue(row.id) || stringValue(row.entryId),
      actionId,
      actionName: definition?.name ?? actionId,
      type: definition?.type ?? 'cultivation',
      status,
      progressSeconds: numberValue(row.progressSeconds),
      durationSeconds: definition?.durationSeconds ?? numberValue(row.durationSeconds),
      remainingRepeats: numberValue(row.remainingRepeats, numberValue(intent.repeatCount, 1)),
      executionKind: definition?.executionKind,
      travelDestinationNodeId: stringValue(intent.targetNodeId) || null,
      travelPathEdgeIds: null,
      travelSegments: null,
      expedition: null,
    }
    return entry
  }).filter(entry => Boolean(entry.entryId))
  return { entries, loopEnabled: true, autoUseSlots: defaultAutoUseSlots() }
}

function mapStats(value: unknown): Stats {
  const row = object(value)
  return {
    qi: numberValue(row.qi),
    spirit: numberValue(row.experience),
    foundationExp: numberValue(row.experience),
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

function mapCombat(value: unknown): { loadout: CombatLoadout; resources: CombatResourceState; attributes: CombatAttributes } {
  const row = object(value)
  const loadout: CombatLoadout = {
    staminaLevel: numberValue(row.staminaLevel, 1),
    intelligenceLevel: numberValue(row.intelligenceLevel, 1),
    attackLevel: numberValue(row.attackLevel, 1),
    defenseLevel: numberValue(row.defenseLevel, 1),
    swordsmanshipLevel: numberValue(row.swordsmanshipLevel, 1),
    talismanLevel: numberValue(row.talismanLevel, 1),
    abilitySlots: arrayValue(row.abilitySlots).map(slot => {
      const item = object(slot)
      return { abilityId: identifierValue(item.abilityId) || null, level: Math.max(1, numberValue(item.level, 1)), triggers: [] }
    }),
    consumableSlots: arrayValue(row.consumableSlots).map(slot => {
      const item = object(slot)
      return { itemId: stringValue(item.itemId) || null, quantity: Math.max(1, numberValue(item.quantity, 1)), enabled: booleanValue(item.enabled), triggers: [] }
    }),
    equipment: arrayValue(row.equipment).map(item => {
      const entry = object(item)
      return { itemId: stringValue(entry.itemId), enhancementLevel: numberValue(entry.enhancementLevel) }
    }).filter(item => Boolean(item.itemId)),
    learnedAbilityIds: arrayValue(row.learnedAbilities ?? row.learnedAbilityIds).map(identifierValue).filter(Boolean),
  }
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
  return { loadout, resources, attributes }
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
  return arrayValue(value).map(item => {
    const row = object(item)
    const buffId = stringValue(row.buffId)
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
  }).filter(buff => Boolean(buff.buffId))
}

function mapAssets(assets: readonly AssetBalanceDto[]): InventoryItem[] {
  return assets
    .filter(asset => Number.isFinite(asset.quantity) && asset.quantity !== 0)
    .map(asset => {
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
    })
}

function seconds(iso: string, fallback: number): number {
  const parsed = Date.parse(iso)
  return Number.isFinite(parsed) ? parsed / 1000 : fallback
}

function visualFor(name: string): CharacterVisual {
  return { renderer: 'text', source: name.slice(0, 1) || '修' }
}

export async function decodePlayerSnapshotDto(dto: PlayerSnapshotDto, account = ''): Promise<PlayerSnapshot> {
  if (!dto || typeof dto.playerId !== 'string' || !dto.playerId) throw new Error('玩家快照缺少 playerId。')
  if (!dto.head || dto.head.schemaVersion !== 1) throw new Error('玩家快照 schema 版本不受支持。')
  await verifyHeadChecksum(dto)
  const sections = await decodeSections(dto.sections ?? [])
  const settings = object(sections.get('settings'))
  const progression = sections.get('progression')
  const world = mapWorld(sections.get('world'))
  const combat = mapCombat(sections.get('combat'))
  const name = dto.displayName || '修士'
  const serverTime = seconds(dto.lastSimulatedAt, Date.now() / 1000)
  return {
    playerId: dto.playerId,
    name,
    account,
    version: Math.max(0, dto.stateVersion),
    serverTimeUnixSeconds: serverTime,
    serverTimeUnixMilliseconds: serverTime * 1000,
    createdAtUnixSeconds: serverTime,
    presenceMode: mapPresence(settings.presence),
    queue: mapQueue(sections.get('action_queue')),
    inventory: mapAssets(dto.assets ?? []),
    buffs: mapBuffs(sections.get('buffs')),
    stats: mapStats(progression),
    combat: combat.loadout,
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
  const sections = await decodeSections(dto.changedSections ?? [])
  const patch: PlayerPatch = {
    playerId: dto.playerId,
    version: dto.stateVersion,
    serverTimeUnixSeconds: current.serverTimeUnixSeconds,
  }
  if (sections.has('settings')) {
    const settings = object(sections.get('settings'))
    patch.presenceMode = mapPresence(settings.presence)
  }
  if (sections.has('progression')) patch.stats = mapStats(sections.get('progression'))
  if (sections.has('world')) patch.world = mapWorld(sections.get('world'))
  if (sections.has('combat')) {
    const combat = mapCombat(sections.get('combat'))
    patch.combat = combat.loadout
    patch.combatResources = combat.resources
    patch.combatAttributes = combat.attributes
  }
  if (sections.has('buffs')) patch.buffs = mapBuffs(sections.get('buffs'))
  if (sections.has('action_queue')) patch.queue = mapQueue(sections.get('action_queue'))
  if (dto.changedAssets) {
    const changed = new Map(mapAssets(dto.changedAssets).map(item => [item.itemId, item]))
    patch.inventory = current.inventory.filter(item => !changed.has(item.itemId)).concat(Array.from(changed.values()))
  }
  return patch
}
