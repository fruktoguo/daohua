/**
 * 玩家四大行动类型：修炼 (cultivation)、探索 (exploration)、炼丹 (alchemy)、战斗 (combat)
 */
export type ActionType = 'cultivation' | 'exploration' | 'alchemy' | 'combat'

/**
 * 玩家在线与离线挂机状态模式：
 * - 'online': 在线连接中
 * - 'idle-offline': 离线怠速挂机
 * - 'fully-offline': 深度离线状态
 */
export type PresenceMode = 'online' | 'idle-offline' | 'fully-offline'

/**
 * 行动队列条目运行状态：排队中 (queued)、运行中 (running)、受阻中断 (blocked)
 */
export type QueueEntryStatus = 'queued' | 'running' | 'blocked'

/**
 * 行动定义配置接口（描述某个行动的耗时、产出与消耗规则）
 */
export interface ActionDefinition {
  /** 行动唯一 ID */
  id: string
  /** 行动显示名称 */
  name: string
  /** 行动描述文案 */
  description: string
  /** 行动大类 */
  type: ActionType
  /** 单轮执行耗时（秒） */
  durationSeconds: number
  /** 特殊执行类型（如 'combat-encounter'） */
  executionKind?: string
  /** 关联的战斗遭遇 ID */
  combatEncounterId?: string
  /** 消耗材料物品与数量映射 */
  inputs?: Record<string, number>
  /** 产出资源/物品与数量映射 */
  outputs?: Record<string, number>
  /** 允许执行该行动的地图节点 ID 列表 */
  availableNodeIds?: string[]
}

/**
 * 处于玩家队列中的单个行动条目
 */
export interface QueueEntry {
  /** 条目唯一实例 ID */
  entryId: string
  /** 关联的行动配置 ID */
  actionId: string
  /** 行动名称快照 */
  actionName: string
  /** 行动大类 */
  type: ActionType
  /** 当前状态 */
  status: QueueEntryStatus
  /** 当前轮次已执行秒数 */
  progressSeconds: number
  /** 单轮总耗时（秒） */
  durationSeconds: number
  /** 剩余循环次数（-1 表示无限循环，0 表示当前轮结束后出队） */
  remainingRepeats: number
  /** 执行类型 */
  executionKind?: string
  /** 跨节点移动目标节点 ID */
  travelDestinationNodeId?: string | null
  /** 移动经过的路径边 ID 列表 */
  travelPathEdgeIds?: string[] | null
  /** 远征/遭遇战执行进度 */
  expedition?: ExpeditionProgress | null
}

/**
 * 自动消耗品槽位配置
 */
export interface AutoUseSlot {
  /** 绑定的物品 ID（为 null 时为空槽） */
  itemId: string | null
  /** 单次使用数量 */
  quantity: number
  /** 是否启用 */
  enabled: boolean
  /** 触发判定条件列表 */
  triggers?: CombatTrigger[]
}

/**
 * 玩家行动队列完整状态
 */
export interface QueueState {
  /** 队列中的行动条目列表 */
  entries: QueueEntry[]
  /** 是否开启全局循环 */
  loopEnabled: boolean
  /** 各行动类型对应的自动消耗品槽位映射 */
  autoUseSlots: Record<ActionType, AutoUseSlot[]>
}

/**
 * 行动进度同步轻量结构
 */
export interface QueueProgress {
  entryId: string
  status: QueueEntryStatus
  progressSeconds: number
  durationSeconds: number
  expedition?: ExpeditionProgress | null
}

/**
 * 远征探索与战斗实时进度结构
 */
export interface ExpeditionProgress {
  /** 阶段：行进(travel)、初次搜寻(initial-search)、搜寻(search)、战斗中(combat)等 */
  phase: 'travel' | 'initial-search' | 'search' | 'combat' | string
  /** 当前阶段归一化进度 (0.0 ~ 1.0) */
  progress: number
  /** 阶段已消耗秒数 */
  elapsedSeconds: number
  /** 阶段总秒数 */
  durationSeconds: number
  /** 行进已走距离 */
  travelledDistance?: number | null
  /** 行进总距离 */
  totalDistance?: number | null
  /** 玩家当前生命值 */
  playerHitpoints?: number | null
  /** 玩家最大生命值 */
  playerMaxHitpoints?: number | null
  /** 玩家当前灵力值 */
  playerManapoints?: number | null
  /** 玩家最大灵力值 */
  playerMaxManapoints?: number | null
  /** 敌方当前生命值 */
  enemyHitpoints?: number | null
  /** 敌方最大生命值 */
  enemyMaxHitpoints?: number | null
  /** 敌方当前灵力值 */
  enemyManapoints?: number | null
  /** 敌方最大灵力值 */
  enemyMaxManapoints?: number | null
  /** 敌人名称 */
  enemyName?: string | null
  /** 当前活动描述 */
  activity?: string | null
  /** 玩家当前施法/攻击动作 */
  playerAction?: CombatActionState | null
  /** 敌方当前施法/攻击动作 */
  enemyAction?: CombatActionState | null
  /** 战斗过程记录列表 */
  activities?: string[] | null
  /** 战斗事件序列号 */
  battleSequence?: number
  /** 功法技能剩余冷却毫秒数映射 */
  abilityCooldownMilliseconds?: Record<string, number> | null
  /** 消耗品槽剩余冷却毫秒数数组 */
  consumableCooldownMilliseconds?: number[] | null
  /** 遭遇战 ID */
  encounterId?: string | null
}

/**
 * 实时战斗动作状态（攻击、施法、受控、读条）
 */
export interface CombatActionState {
  name: string
  kind: 'attack' | 'cast' | 'control' | 'respawn' | 'wait' | string
  elapsedMilliseconds: number
  durationMilliseconds: number
  completesAtUnixMilliseconds?: number | null
}

/**
 * 角色视觉渲染配置
 */
export interface CharacterVisual {
  renderer: 'text' | 'image' | 'live2d' | 'model3d' | string
  source: string
  poster?: string | null
}

/**
 * 实时战斗血蓝资源与冷却状态
 */
export interface CombatResourceState {
  hitpoints: number
  maxHitpoints: number
  manapoints: number
  maxManapoints: number
  abilityCooldownMilliseconds?: Record<string, number> | null
  consumableCooldownMilliseconds?: number[] | null
}

/**
 * 完整战斗属性面板结构
 */
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

/**
 * 玩家背包中的单个物品堆叠实例
 */
export interface InventoryItem {
  /** 物品配置 ID */
  itemId: string
  /** 物品名称 */
  name: string
  /** 分类名称 */
  category: string
  /** 拥有数量 */
  amount: number
  /** 色调主题 */
  tone: 'green' | 'violet' | 'gold' | 'cyan' | string
  /** 图标字符/符号 */
  glyph: string
  /** 品阶等级 */
  tier?: number
  /** 描述文本 */
  description?: string
  /** 是否允许在市场上自由交易 */
  isTradable?: boolean
}

/**
 * 物品静态全局配置定义
 */
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

/**
 * 物品附加 Buff 效果定义
 */
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

/**
 * 服务端下发的日志事件数据
 */
export interface LogEvent {
  sequence: number
  timestampUnixMilliseconds: number
  template: string
}

/**
 * 角色身上当前生效的增益/减益状态 (Buff)
 */
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

/**
 * 角色历史累计统计数据
 */
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

/**
 * 战斗技能槽位配置
 */
export interface CombatAbilitySlot {
  abilityId: string | null
  level: number
  triggers?: CombatTrigger[]
}

/**
 * 战斗自动触发条件规则配置
 */
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

/**
 * 功法技能配置定义
 */
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

/**
 * 参战单位/敌人基础定义
 */
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

/**
 * 遭遇战随机刷怪权重定义
 */
export interface CombatSpawnDefinition {
  enemy: CombatantDefinition
  weight: number
  strength: number
}

/**
 * 遭遇战副本配置定义
 */
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

/**
 * 装备属性增益汇总
 */
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

/**
 * 装备配置定义
 */
export interface CombatEquipmentDefinition {
  equipmentId: string
  name: string
  slot: string
  tier: number
  stats: CombatEquipmentStats
  enhancementBonus: CombatEquipmentStats
}

/**
 * 已装备的物品实例（含强化等级）
 */
export interface EquippedItem {
  itemId: string
  enhancementLevel: number
}

/**
 * 战斗消耗品槽位配置
 */
export interface ConsumableSlot {
  itemId: string | null
  quantity: number
  enabled: boolean
  triggers?: CombatTrigger[]
}

/**
 * 玩家战斗装配方案
 */
export interface CombatLoadout {
  style: string
  damageType: string
  staminaLevel: number
  intelligenceLevel: number
  attackLevel: number
  defenseLevel: number
  swordsmanshipLevel: number
  talismanLevel: number
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

/**
 * 地图节点附近 NPC 或建筑物实体
 */
export interface NearbyEntity {
  entityId: string
  name: string
  kind: 'npc' | 'building' | string
  description: string
  effect: string
}

/**
 * 大地图节点定义
 */
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

/**
 * 大地图连接边（道路）定义
 */
export interface WorldEdgeDefinition {
  edgeId: string
  fromNodeId: string
  toNodeId: string
  durationSeconds: number
}

/**
 * 大地图世界区域定义
 */
export interface WorldMapDefinition {
  mapId: string
  name: string
  description: string
  backgroundSeed: number
  nodes: WorldNodeDefinition[]
  edges: WorldEdgeDefinition[]
}

/**
 * 玩家当前所在的世界位置与周边环境状态
 */
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

/**
 * 玩家全量快照 (PlayerSnapshot) - 服务端下发的权威完整状态
 */
export interface PlayerSnapshot {
  /** 玩家唯一 ID */
  playerId: string
  /** 角色名称 */
  name: string
  /** 快照版本号（单调递增） */
  version: number
  /** 服务端权威时间（Unix 秒） */
  serverTimeUnixSeconds: number
  /** 服务端权威时间（Unix 毫秒） */
  serverTimeUnixMilliseconds?: number | null
  /** 在线/离线挂机模式 */
  presenceMode: PresenceMode
  /** 行动队列状态 */
  queue: QueueState
  /** 背包物品列表 */
  inventory: InventoryItem[]
  /** 生效 Buff 列表 */
  buffs: Buff[]
  /** 历史统计数据 */
  stats: Stats
  /** 最近日志事件列表 */
  logEvents?: LogEvent[]
  /** 战斗装配数据 */
  combat?: CombatLoadout
  /** 实时战斗资源 */
  combatResources?: CombatResourceState
  /** 战斗属性面板 */
  combatAttributes?: CombatAttributes
  /** 角色视觉外观 */
  visual?: CharacterVisual
  /** 当前所在大地图世界状态 */
  world?: WorldState
  /** 是否处于突破待决状态 */
  isPendingBreakthrough?: boolean
  /** 绑定的登录账号 */
  account?: string | null
  /** 账号下角色序号 */
  sequenceId?: number
  /** 角色创建时间（Unix 秒） */
  createdAtUnixSeconds?: number
  /** 改名冷却倒计时（秒） */
  renameCooldownSeconds?: number
}

/**
 * 玩家增量补丁 (PlayerPatch) - 用于局部轻量同步
 */
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

/**
 * 前后端 WebSocket 传输协议通用封包结构
 */
export interface Packet<T = unknown> {
  /** 包体类型（如 'snapshot', 'delta', 'command', 'hello', 'ack', 'error'） */
  kind: string
  /** 请求唯一追溯 ID */
  requestId: string
  /** 消息序号 */
  sequence: number
  /** 承载的业务负载数据 */
  payload: T
}

/**
 * 命令操作确认响应负载
 */
export interface AckPayload {
  success: boolean
  code: string
  message: string
  serverTimeUnixSeconds: number
}

/**
 * 业务错误响应负载
 */
export interface ErrorPayload {
  code: string
  message: string
}

/**
 * 市场挂单深度档位
 */
export interface MarketLevel {
  /** 挂单单价 */
  price: number
  /** 该价格下的总数量 */
  quantity: number
  /** 该价格下的订单数 */
  orderCount: number
}

/**
 * 物品市场买卖盘订单薄 (OrderBook)
 */
export interface MarketBook {
  /** 物品配置 ID */
  itemId: string
  /** 强化等级 */
  enhancementLevel: number
  /** 卖盘深度档位列表（按价格升序） */
  asks: MarketLevel[]
  /** 买盘深度档位列表（按价格降序） */
  bids: MarketLevel[]
  /** 当前活跃挂单总量 */
  activeOrderCount: number
  /** 账本版本序列号 */
  sequence: number
}

/**
 * 市场交易整体指标度量数据
 */
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

/**
 * 玩家个人市场挂单信息
 */
export interface MarketOrder {
  /** 订单唯一 ID */
  orderId: string
  /** 物品配置 ID */
  itemId: string
  /** 强化等级 */
  enhancementLevel: number
  /** 交易方向：买入 (buy) 或 卖出 (sell) */
  side: 'buy' | 'sell'
  /** 挂单单价 */
  price: number
  /** 原始挂单总数量 */
  originalQuantity: number
  /** 剩余未成交数量 */
  remainingQuantity: number
  /** 已成交数量 */
  filledQuantity: number
  /** 订单状态：活跃中、部分成交、全部成交、已取消、已过期 */
  status: 'active' | 'partially-filled' | 'filled' | 'cancelled' | 'expired'
  /** 订单序列号 */
  sequence: number
  /** 创建时间 ISO 字符串 */
  createdAt: string
  /** 过期时间 ISO 字符串 */
  expiresAt: string
}
