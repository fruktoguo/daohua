export type ActionType = 'cultivation' | 'exploration' | 'alchemy' | 'combat'

export type PresenceMode = 'online' | 'idle-offline' | 'fully-offline'

export type QueueEntryStatus = 'queued' | 'running' | 'blocked'

export interface ActionDefinition {
  id: string
  name: string
  description: string
  type: ActionType
  durationSeconds: number
  executionKind?: string
  combatEncounterId?: string
  inputs?: Record<string, number>
  outputs?: Record<string, number>
  availableNodeIds?: string[]
}

export interface QueueEntry {
  entryId: string
  actionId: string
  actionName: string
  type: ActionType
  status: QueueEntryStatus
  progressSeconds: number
  durationSeconds: number
  remainingRepeats: number
  executionKind?: string
  travelDestinationNodeId?: string | null
  travelPathEdgeIds?: string[] | null
  expedition?: ExpeditionProgress | null
}

export interface AutoUseSlot {
  itemId: string | null
  quantity: number
  enabled: boolean
  triggers?: CombatTrigger[]
}

export interface QueueState {
  entries: QueueEntry[]
  loopEnabled: boolean
  autoUseSlots: Record<ActionType, AutoUseSlot[]>
}

export interface QueueProgress {
  entryId: string
  status: QueueEntryStatus
  progressSeconds: number
  durationSeconds: number
  expedition?: ExpeditionProgress | null
}

export interface ExpeditionProgress {
  phase: 'travel' | 'initial-search' | 'search' | 'combat' | 'death' | string
  progress: number
  elapsedSeconds: number
  durationSeconds: number
  travelledDistance?: number | null
  totalDistance?: number | null
  playerHitpoints?: number | null
  playerMaxHitpoints?: number | null
  playerManapoints?: number | null
  playerMaxManapoints?: number | null
  enemyHitpoints?: number | null
  enemyMaxHitpoints?: number | null
  enemyManapoints?: number | null
  enemyMaxManapoints?: number | null
  enemyName?: string | null
  activity?: string | null
  playerAction?: CombatActionState | null
  enemyAction?: CombatActionState | null
  activities?: string[] | null
  battleSequence?: number
  abilityCooldownMilliseconds?: Record<string, number> | null
  consumableCooldownMilliseconds?: number[] | null
  encounterId?: string | null
}

export interface CombatActionState {
  name: string
  kind: 'attack' | 'cast' | 'control' | 'respawn' | 'wait' | string
  elapsedMilliseconds: number
  durationMilliseconds: number
  completesAtUnixMilliseconds?: number | null
}

export interface CharacterVisual {
  renderer: 'text' | 'image' | 'live2d' | 'model3d' | string
  source: string
  poster?: string | null
}

export interface CombatResourceState {
  hitpoints: number
  maxHitpoints: number
  manapoints: number
  maxManapoints: number
  abilityCooldownMilliseconds?: Record<string, number> | null
  consumableCooldownMilliseconds?: number[] | null
}

export interface CombatAttributes {
  maxHitpoints: number
  maxManapoints: number
  attackIntervalMilliseconds: number
  accuracyRating: number
  maxDamage: number
  evasionRating: number
  armor: number
  waterResistance: number
  natureResistance: number
  fireResistance: number
  style: string
  damageType: string
  tenacity: number
  hpRegenPerTenSeconds: number
  mpRegenPerTenSeconds: number
  penetrationRatio: number
  pierceChance: number
  curseRatio: number
  furyRatio: number
  weakenRatio: number
  lifeStealRatio: number
  armorDamageRatio: number
  hpDrainRatio: number
  damageTakenRatio: number
  criticalChance: number
  criticalDamageRatio: number
  criticalAvoidance: number
  damageAbsorptionRatio: number
  criticalRating?: number
  criticalAvoidanceRating?: number
}

export interface InventoryItem {
  itemId: string
  name: string
  category: string
  amount: number
  tone: 'green' | 'violet' | 'gold' | 'cyan' | string
  glyph: string
  tier?: number
  description?: string
  isTradable?: boolean
}

export interface ItemDefinition {
  itemId: string
  name: string
  description: string
  kind: string
  category: string
  tier: number
  sellPrice: number
  isTradable: boolean
  usableActionTypes: ActionType[]
  cooldownSeconds: number
  recoverySeconds: number
  hitpointRestore: number
  manapointRestore: number
  triggerHpRatio: number
  triggerAtCombatStart: boolean
  defaultCombatTriggers?: CombatTrigger[] | null
  buffEffects?: ItemBuffEffectDefinition[] | null
}

export interface ItemBuffEffectDefinition {
  name: string
  durationSeconds: number
  maxStacks: number
  qiGainMultiplier: number
  attackLevelRatio: number
  damageRatio: number
  attackSpeedRatio: number
  gatheringOutputMultiplier: number
  alchemyOutputMultiplier: number
}

export interface LogEvent {
  sequence: number
  timestampUnixMilliseconds: number
  template: string
}

export interface Buff {
  buffId: string
  name: string
  stacks: number
  qiGainMultiplier: number
  expiresAtUnixSeconds: number
  attackLevelRatio?: number
  damageRatio?: number
  attackSpeedRatio?: number
  gatheringOutputMultiplier?: number
  alchemyOutputMultiplier?: number
  sourceItemId?: string | null
}

export interface Stats {
  qi: number
  spirit: number
  foundationExp?: number
  herbsCollected: number
  pillsCrafted: number
  cultivationCycles: number
  completedActions: number
  blockedActions: number
  combatWins?: number
  combatLosses?: number
  combatExperience?: number
  combatDamageDealt?: number
  combatDamageTaken?: number
  combatEnemiesDefeated?: number
  combatPlayersDefeated?: number
}

export interface CombatAbilitySlot {
  abilityId: string | null
  level: number
  triggers?: CombatTrigger[]
}

export interface CombatTrigger {
  dependency:
    | 'self'
    | 'target-enemy'
    | 'ally-lowest-hp'
    | 'ally-highest-hp'
    | 'enemy-lowest-hp'
    | 'enemy-highest-hp'
    | string
  condition: 'current-hp' | 'current-mana' | 'missing-hp' | 'missing-mana' | 'current-hp-percentage' | 'missing-hp-percentage' | 'current-mana-percentage' | 'missing-mana-percentage' | 'buff-stacks' | 'stun-status' | 'blind-status' | 'silence-status' | string
  comparator: 'less-than-equal' | 'greater-than-equal' | 'is-active' | 'is-inactive' | 'less-than' | 'greater-than' | string
  value: number
  buffId?: string | null
}

export interface CombatAbilityDefinition {
  abilityId: string
  name: string
  style: string
  damageType: string
  cooldownMilliseconds: number
  castMilliseconds: number
  manaCost: number
  damageRatio: number
  flatDamage: number
  penetrationRatio: number
  triggerHpRatio: number
  effectType?: 'damage' | 'heal' | string
  healingRatio?: number
  flatHealing?: number
  defaultTriggers?: CombatTrigger[] | null
  damageOverTimeRatio?: number
  damageOverTimeMilliseconds?: number
  damageOverTimeTickMilliseconds?: number
  stunChance?: number
  stunMilliseconds?: number
  blindChance?: number
  blindMilliseconds?: number
  silenceChance?: number
  silenceMilliseconds?: number
  damageRatioLevelBonus?: number
  flatDamageLevelBonus?: number
  healingRatioLevelBonus?: number
  flatHealingLevelBonus?: number
  targetType?: 'enemy' | 'all-enemies' | 'self' | 'all-allies' | 'lowest-hp-ally' | string
  pierceChance?: number
  armorDamageRatio?: number
  hpDrainRatio?: number
}

export interface CombatantDefinition {
  enemyId: string
  name: string
  visual: CharacterVisual
  style: string
  damageType: string
  maxHitpoints: number
  maxManapoints: number
  attackIntervalMilliseconds: number
  accuracyRating: number
  evasionRating: number
  maxDamage: number
  armor: number
  waterResistance: number
  natureResistance: number
  fireResistance: number
  tenacity: number
  pierceChance: number
  curseRatio: number
  furyRatio: number
  weakenRatio: number
  lifeStealRatio: number
  armorDamageRatio: number
  hpDrainRatio: number
  criticalChance: number
  criticalDamageRatio: number
  criticalAvoidance: number
  damageAbsorptionRatio: number
  hpRegenPerTenSeconds: number
  mpRegenPerTenSeconds: number
  enrageTimeMilliseconds: number
  experience: number
  abilityIds: string[]
}

export interface CombatSpawnDefinition {
  enemy: CombatantDefinition
  weight: number
  strength: number
}

export interface CombatEncounterDefinition {
  encounterId: string
  name: string
  enemy: CombatantDefinition
  randomSpawns: CombatSpawnDefinition[]
  maxSpawnCount: number
  maxTotalStrength: number
  bossEnemies: CombatantDefinition[]
  battlesPerBoss: number
  searchMilliseconds: number
  respawnMilliseconds: number
  playerRespawnMilliseconds: number
}

export interface CombatEquipmentStats {
  accuracyRatio: number
  damageRatio: number
  attackSpeedRatio: number
  evasionRatio: number
  armor: number
  waterResistance: number
  natureResistance: number
  fireResistance: number
  tenacity: number
  penetrationRatio: number
  pierceChance: number
  curseRatio: number
  furyRatio: number
  weakenRatio: number
  lifeStealRatio: number
  armorDamageRatio: number
  hpDrainRatio: number
  criticalChance: number
  criticalDamageRatio: number
  criticalAvoidance: number
  damageAbsorptionRatio: number
  attackIntervalMilliseconds: number
  style?: string | null
  damageType?: string | null
}

export interface CombatEquipmentDefinition {
  equipmentId: string
  name: string
  slot: string
  tier: number
  stats: CombatEquipmentStats
  enhancementBonus: CombatEquipmentStats
}

export interface EquippedItem {
  itemId: string
  enhancementLevel: number
}

export interface ConsumableSlot {
  itemId: string | null
  quantity: number
  enabled: boolean
  triggers?: CombatTrigger[]
}

export interface CombatLoadout {
  style: string
  damageType: string
  staminaLevel: number
  intelligenceLevel: number
  attackLevel: number
  defenseLevel: number
  swordsmanshipLevel: number
  talismanLevel: number
  fiveElementsLevel: number
  abilitySlots: CombatAbilitySlot[]
  consumableSlots: ConsumableSlot[]
  combatDropRate?: number
  combatDropQuantity?: number
  combatRareFind?: number
  searchSpeedRatio?: number
  tenacity?: number
  equipment?: EquippedItem[]
  learnedAbilityIds?: string[]
}

export interface NearbyEntity {
  entityId: string
  name: string
  kind: 'npc' | 'building' | string
  description: string
  effect: string
}

export interface WorldNodeDefinition {
  nodeId: string
  name: string
  description: string
  x: number
  y: number
  cultivationMultiplier: number
  availableActionIds: string[]
  encounterIds: string[]
  npcs: NearbyEntity[]
  buildings: NearbyEntity[]
}

export interface WorldEdgeDefinition {
  edgeId: string
  fromNodeId: string
  toNodeId: string
  durationSeconds: number
}

export interface WorldMapDefinition {
  mapId: string
  name: string
  description: string
  backgroundSeed: number
  nodes: WorldNodeDefinition[]
  edges: WorldEdgeDefinition[]
}

export interface WorldState {
  mapId: string
  nodeId: string | null
  edgeId: string | null
  edgeProgress: number
  x: number
  y: number
  locationName: string
  cultivationMultiplier: number
  availableActionIds: string[]
  encounterIds: string[]
  npcs: NearbyEntity[]
  buildings: NearbyEntity[]
}

export interface PlayerSnapshot {
  playerId: string
  name: string
  version: number
  serverTimeUnixSeconds: number
  serverTimeUnixMilliseconds?: number | null
  presenceMode: PresenceMode
  queue: QueueState
  inventory: InventoryItem[]
  buffs: Buff[]
  stats: Stats
  events: string[]
  logEvents?: LogEvent[]
  combat?: CombatLoadout
  combatResources?: CombatResourceState
  combatAttributes?: CombatAttributes
  visual?: CharacterVisual
  world?: WorldState
  isPendingBreakthrough?: boolean
  account?: string | null
  sequenceId?: number
  createdAtUnixSeconds?: number
  renameCooldownSeconds?: number
}

export interface PlayerPatch {
  playerId: string
  version: number
  serverTimeUnixSeconds: number
  serverTimeUnixMilliseconds?: number | null
  presenceMode?: PresenceMode
  queue?: QueueState
  inventory?: InventoryItem[]
  buffs?: Buff[]
  stats?: Stats
  events?: string[]
  queueProgress?: QueueProgress
  eventBatch?: LogEvent[]
  combat?: CombatLoadout
  combatResources?: CombatResourceState
  combatAttributes?: CombatAttributes
  visual?: CharacterVisual
  world?: WorldState
  isPendingBreakthrough?: boolean
  name?: string
  account?: string | null
  sequenceId?: number
  createdAtUnixSeconds?: number
  renameCooldownSeconds?: number
}

export interface Packet<T = unknown> {
  kind: string
  requestId: string
  sequence: number
  payload: T
}

export interface AckPayload {
  success: boolean
  code: string
  message: string
  serverTimeUnixSeconds: number
}

export interface ErrorPayload {
  code: string
  message: string
}

export interface MarketLevel {
  price: number
  quantity: number
  orderCount: number
}

export interface MarketBook {
  itemId: string
  enhancementLevel: number
  asks: MarketLevel[]
  bids: MarketLevel[]
  activeOrderCount: number
  sequence: number
}

export interface MarketMetrics {
  shardCount: number
  activeOrderCount: number
  placementCount: number
  matchCount: number
  cancelCount: number
  rejectedCount: number
  tradedQuantity: number
  tradedGrossAmount: number
}

export interface MarketOrder {
  orderId: string
  itemId: string
  enhancementLevel: number
  side: 'buy' | 'sell'
  price: number
  originalQuantity: number
  remainingQuantity: number
  filledQuantity: number
  status: 'active' | 'partially-filled' | 'filled' | 'cancelled' | 'expired'
  sequence: number
  createdAt: string
  expiresAt: string
}
