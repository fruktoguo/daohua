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
 * 行动执行推进模式分类：标准计时推进 (timed)、战斗遭遇推进 (combat-encounter)、大地图移动旅行 (travel)
 */
export type ActionExecutionKind = 'timed' | 'combat-encounter' | 'travel'

/**
 * 远征探索与战斗历练阶段枚举：
 * - 'none': 无/未初始化
 * - 'travel': 节点间行进中
 * - 'initial-search': 初始搜寻敌人
 * - 'search': 战后搜寻下一波敌人
 * - 'combat': 战斗交锋中
 */
export type ExpeditionPhase = 'none' | 'travel' | 'initial-search' | 'search' | 'combat'

/**
 * 实时战斗动作类型：攻击 (attack)、施法 (cast)、控制 (control)、等待/调息 (wait)、复活读条 (respawn)
 */
export type CombatActionKind = 'attack' | 'cast' | 'control' | 'wait' | 'respawn'

/**
 * 战斗流派/武道门类：御剑 (sword)、重刃 (blade)、符箓 (talisman)、五行奇术 (five-elements)、横练肉身 (body)
 */
export type CombatStyle = 'sword' | 'blade' | 'talisman' | 'five-elements' | 'body'

/**
 * 伤害大类：物理 (physical)、术法 (spell)
 */
export type DamageCategory = 'physical' | 'spell'

/**
 * 五行属性类型：无属性 (none)、金 (metal)、木 (wood)、水 (water)、火 (fire)、土 (earth)
 */
export type Element = 'none' | 'metal' | 'wood' | 'water' | 'fire' | 'earth'

/**
 * 物品基础形态大类：材料 (material)、消耗品 (consumable)、货币 (currency)、资源 (resource)、装备 (equipment)
 */
export type ItemKind = 'material' | 'consumable' | 'currency' | 'resource' | 'equipment'

/**
 * 物品细分分类键：通用 (general)、材料 (material)、药材 (herb)、矿石 (mineral)、丹药 (pill)、
 * 灵食 (food)、灵饮 (drink)、符箓 (talisman)、功法秘籍 (manual)、装备 (equipment)、
 * 宝箱 (chest)、货币 (currency)、资源 (resource)
 */
export type ItemCategoryKey =
  | 'general'
  | 'material'
  | 'herb'
  | 'mineral'
  | 'pill'
  | 'food'
  | 'drink'
  | 'talisman'
  | 'manual'
  | 'equipment'
  | 'chest'
  | 'currency'
  | 'resource'

/**
 * 装备穿戴槽位：主手武器 (weapon)、双手武器 (two-hand)、副手/盾牌 (off-hand)、
 * 头部 (head)、护甲/上衣 (armor)、腿部/下装 (legs)、鞋靴 (boots)、
 * 戒指 (ring)、项链 (neck)、饰品/法宝 (trinket)、符箓/灵宝 (talisman)
 */
export type EquipmentSlot =
  | 'weapon'
  | 'two-hand'
  | 'off-hand'
  | 'head'
  | 'armor'
  | 'legs'
  | 'boots'
  | 'ring'
  | 'neck'
  | 'trinket'
  | 'talisman'

/**
 * 参战单位/怪物阶位等级：
 * 小怪 (minion)、普通 (normal)、精英 (elite)、领袖 (leader)、首领/Boss (boss)、
 * 召唤物 (summon)、宠物 (pet)、镜像玩家 (player-mirror)、NPC (npc)
 */
export type UnitRank =
  | 'minion'
  | 'normal'
  | 'elite'
  | 'leader'
  | 'boss'
  | 'summon'
  | 'pet'
  | 'player-mirror'
  | 'npc'

/**
 * 地图周边实体类型：NPC (npc)、建筑 (building)、传送阵 (portal)、资源点 (resource-node)
 */
export type NearbyEntityKind = 'npc' | 'building' | 'portal' | 'resource-node'

/**
 * 角色/立绘视觉渲染器形态：文字图标 (text)、符号字符 (glyph)、贴图图片 (image)、Live2D 动效 (live2d)、3D模型 (model3d)
 */
export type CharacterRendererKind = 'text' | 'glyph' | 'image' | 'live2d' | 'model3d'

/**
 * 交易委托买卖方向：买入 (buy)、卖出 (sell)
 */
export type MarketSide = 'buy' | 'sell'

/**
 * 市场订单生命周期状态：已挂单活跃 (active)、部分成交 (partially-filled)、全部成交 (filled)、已取消 (cancelled)、已过期 (expired)
 */
export type MarketOrderStatus = 'active' | 'partially-filled' | 'filled' | 'cancelled' | 'expired'

/**
 * 战斗自动触发条件依赖目标
 */
export type CombatTriggerDependency =
  | 'self'
  | 'target-enemy'
  | 'ally-lowest-hp'
  | 'ally-highest-hp'
  | 'enemy-lowest-hp'
  | 'enemy-highest-hp'

/**
 * 战斗自动触发条件判定类型
 */
export type CombatTriggerCondition =
  | 'current-hp'
  | 'current-mana'
  | 'missing-hp'
  | 'missing-mana'
  | 'current-hp-percentage'
  | 'missing-hp-percentage'
  | 'current-mana-percentage'
  | 'missing-mana-percentage'
  | 'buff-stacks'
  | 'stun-status'
  | 'blind-status'
  | 'silence-status'

/**
 * 战斗自动触发条件比较操作符
 */
export type CombatTriggerComparator =
  | 'less-than-equal'
  | 'greater-than-equal'
  | 'is-active'
  | 'is-inactive'
  | 'less-than'
  | 'greater-than'

/**
 * 功法技能效果类型
 */
export type CombatAbilityEffectType = 'damage' | 'heal'

/**
 * 功法技能施法目标范围
 */
export type CombatAbilityTargetType =
  | 'enemy'
  | 'all-enemies'
  | 'self'
  | 'all-allies'
  | 'lowest-hp-ally'

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
  executionKind?: ActionExecutionKind
  /** 是否仅供服务端/队列内部使用，不在玩家行动目录中展示 */
  hiddenFromCatalog?: boolean
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
  executionKind?: ActionExecutionKind
  /** 关联的战斗遭遇 ID */
  combatEncounterId?: string | null
  /** 跨节点移动目标节点 ID */
  travelDestinationNodeId?: string | null
  /** 移动经过的路径边 ID 列表 */
  travelPathEdgeIds?: string[] | null
  /** 移动路段进度详情列表 */
  travelSegments?: TravelSegment[] | null
  /** 远征/遭遇战执行进度 */
  expedition?: ExpeditionProgress | null
}
/**
 * 旅行移动路段数据
 */
export interface TravelSegment {
  edgeId: string
  startProgress: number
  endProgress: number
  durationSeconds: number
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
  /** 已开启的持续消耗品剩余有效秒数（银河奶牛式） */
  remainingDurationSeconds?: number
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
  phase: ExpeditionPhase
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
  /** 敌人定义标识符 */
  enemyId?: string | null
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
  kind: CombatActionKind
  elapsedMilliseconds: number
  durationMilliseconds: number
  completesAtUnixMilliseconds?: number | null
}

/**
 * 角色视觉渲染配置
 */
export interface CharacterVisual {
  renderer: CharacterRendererKind
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
  magicResistance: number
  metalResistance: number
  woodResistance: number
  waterResistance: number
  fireResistance: number
  earthResistance: number
  style: CombatStyle
  category: DamageCategory
  element: Element
  tenacity: number
  hpRegenPerTenSeconds: number
  mpRegenPerTenSeconds: number
  penetrationRatio: number
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
  kind: ItemKind
  category: ItemCategoryKey | string
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
  durationSeconds?: number
  isContinuous?: boolean
  cultivationMultiplier?: number
  gatheringMultiplier?: number
  alchemyMultiplier?: number
}

/**
 * 物品附加 Buff 效果定义
 */
export interface ItemBuffEffectDefinition {
  buffId?: string
  name: string
  durationSeconds: number
  maxStacks: number
  description?: string
  qiGainMultiplier?: number
  attackLevelRatio?: number
  damageRatio?: number
  attackSpeedRatio?: number
  hpRegen?: number
  mpRegen?: number
  damageAbsorptionRatio?: number
  armor?: number
  magicResistance?: number
  criticalChance?: number
  criticalDamageRatio?: number
  tenacity?: number
  gatheringOutputMultiplier?: number
  alchemyOutputMultiplier?: number
  modifiers?: Record<string, number>
}

/**
 * 服务端下发的日志事件数据
 */
export interface LogEvent {
  sequence: number
  timestampUnixMilliseconds: number
  template: string
  count?: number
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
  modifiers?: Record<string, number> | null
}

/**
 * 角色历史累计统计数据
 */
export interface Stats {
  realmLevel?: number
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
  dependency: CombatTriggerDependency
  condition: CombatTriggerCondition
  comparator: CombatTriggerComparator
  value: number
  buffId?: string | null
}

/**
 * 功法技能配置定义
 */
export interface CombatAbilityDefinition {
  abilityId: string
  name: string
  style: CombatStyle
  category: DamageCategory
  element: Element
  cooldownMilliseconds: number
  castMilliseconds: number
  manaCost: number
  damageRatio: number
  flatDamage: number
  penetrationRatio: number
  triggerHpRatio: number
  effectType?: CombatAbilityEffectType
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
  targetType?: CombatAbilityTargetType
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
  style: CombatStyle
  category: DamageCategory
  element: Element
  rank?: UnitRank
  maxHitpoints: number
  maxManapoints: number
  attackIntervalMilliseconds: number
  accuracyRating: number
  evasionRating: number
  maxDamage: number
  armor: number
  magicResistance: number
  metalResistance: number
  woodResistance: number
  waterResistance: number
  fireResistance: number
  earthResistance: number
  tenacity: number
  lifeStealRatio: number
  armorDamageRatio: number
  hpDrainRatio: number
  criticalChance: number
  criticalDamageRatio: number
  criticalAvoidance: number
  damageAbsorptionRatio: number
  hpRegenPerTenSeconds: number
  mpRegenPerTenSeconds: number
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
  magicResistance: number
  metalResistance: number
  woodResistance: number
  waterResistance: number
  fireResistance: number
  earthResistance: number
  tenacity: number
  penetrationRatio: number
  lifeStealRatio: number
  armorDamageRatio: number
  hpDrainRatio: number
  criticalChance: number
  criticalDamageRatio: number
  criticalAvoidance: number
  damageAbsorptionRatio: number
  attackIntervalMilliseconds: number
  style?: CombatStyle | null
  category?: DamageCategory | null
  element?: Element | null
}

/**
 * 装备配置定义
 */
export interface CombatEquipmentDefinition {
  equipmentId: string
  name: string
  slot: EquipmentSlot
  tier: number
  stats: CombatEquipmentStats
  enhancementBonus: CombatEquipmentStats
  baseStatRatios?: Record<string, number>
  flatStats?: Record<string, number>
  specialMechanics?: Record<string, number>
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
  abilityExperiences?: Record<string, number>
}

/**
 * 地图节点附近 NPC 或建筑物实体
 */
export interface NearbyEntity {
  entityId: string
  name: string
  kind: NearbyEntityKind
  description: string
  effect: string
}

/**
 * 地点栖息的怪物实体定义
 */
export interface NearbyMonsterDefinition {
  monsterId: string
  name: string
  level: number
  tier: number
  rank: string
  budgetCost: number
  maxHitpoints: number
  maxDamage: number
  armor: number
  drops: string[]
  visualSource?: string | null
  visualRenderer?: string | null
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
  minBudget?: number
  maxBudget?: number
  eliteInterval?: number
  bossInterval?: number
  monsters?: NearbyMonsterDefinition[]
  eliteEnemies?: NearbyMonsterDefinition[]
  bossEnemies?: NearbyMonsterDefinition[]
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
  minBudget?: number
  maxBudget?: number
  eliteInterval?: number
  bossInterval?: number
  monsters?: NearbyMonsterDefinition[]
  eliteEnemies?: NearbyMonsterDefinition[]
  bossEnemies?: NearbyMonsterDefinition[]
}

/**
 * 玩家 UI 投影（由 wire.v1 PlayerSnapshotDto 的 section 载荷解码得到）。
 * 该类型只服务于 React 渲染，不作为网络或持久化契约。
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
 * 玩家 UI 增量投影（由 wire.v1 PlayerSnapshotDeltaDto 解码得到）。
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
  side: MarketSide
  /** 挂单单价 */
  price: number
  /** 原始挂单总数量 */
  originalQuantity: number
  /** 剩余未成交数量 */
  remainingQuantity: number
  /** 已成交数量 */
  filledQuantity: number
  /** 订单状态：活跃中、部分成交、全部成交、已取消、已过期 */
  status: MarketOrderStatus
  /** 订单序列号 */
  sequence: number
  /** 创建时间 ISO 字符串 */
  createdAt: string
  /** 过期时间 ISO 字符串 */
  expiresAt: string
}
