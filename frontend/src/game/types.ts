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
 * - 'respawn': 重生/复活读条中
 * - 'duel-pending': 斗法撮合等待中
 * - 'duel': 斗法脚本回放中
 */
export type ExpeditionPhase = 'initial-search' | 'search' | 'combat' | 'respawn' | 'duel-pending' | 'duel'

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
 * 行动收益类别：修为 (qi)、灵力/战斗蓝 (mana)、气血 (hitpoints)、历练经验 (experience)、物品产出 (item)、增益状态 (buff)
 */
export type ActionYieldKind = 'qi' | 'mana' | 'hitpoints' | 'experience' | 'item' | 'buff'

/**
 * 收益结算周期：持续速率 (second) / 单轮完成结算 (repeat)
 */
export type ActionYieldPeriod = 'second' | 'repeat'

/**
 * 行动单条收益定义（服务端目录 yields 协议字段）
 * id/name/durationSeconds/modifiers 仅 item/buff 类别有值，其余为 null
 */
export interface ActionYield {
  /** 收益类别 */
  kind: ActionYieldKind
  /** 结算周期：second 为每秒持续速率，repeat 为每轮完成结算 */
  per: ActionYieldPeriod
  /** 收益数量（item 为物品件数，buff 为叠加层数，其余为点数/速率） */
  amount: number
  /** 物品 ID 或 BuffId（仅 item/buff 有值） */
  id?: string | null
  /** Buff 显示名称（仅 buff 有值） */
  name?: string | null
  /** Buff 持续秒数（仅 buff 有值） */
  durationSeconds?: number | null
  /** Buff 修饰器（仅 buff 有值；与 BuffModifiers 同一形状） */
  modifiers?: BuffModifiers | null
}

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
  /** 行动收益列表（修为/灵力/气血/历练经验/物品/增益） */
  yields?: ActionYield[]
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
  /** 移动全程总里程（服务端世界图边距权威值） */
  travelTotalDistance?: number | null
  /** 移动路段进度详情列表（每段携带边 ID 与里程） */
  travelSegments?: TravelSegment[] | null
  /** 远征/遭遇战执行进度 */
  expedition?: ExpeditionProgress | null
  /** 是否勾选斗法（PVP 匹配），仅战斗遭遇行动有效 */
  pvpEnabled: boolean
}
/**
 * 旅行移动路段数据（服务端世界图权威分段）
 */
export interface TravelSegment {
  /** 地图边 ID */
  edgeId: string
  /** 该路段在整条旅途上的起始归一化进度 */
  startProgress: number
  /** 该路段在整条旅途上的结束归一化进度 */
  endProgress: number
  /** 该路段耗时（秒） */
  durationSeconds: number
  /** 该路段里程（世界图边距） */
  distance: number
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
  /** 栏位分组：战斗域分 potion（药品栏）与 general（通用栏），其余行动域恒为 general */
  group?: 'potion' | 'general'
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
 * 同波单个敌方单位的战斗快照（服务端白名单投影：名称/品阶/立绘/完整属性随快照下发，
 * 前端不得再按 enemyId 反查怪物目录补齐战斗数值）
 */
export interface ExpeditionEnemy {
  /** 配表怪物定义 ID */
  enemyId: string
  /** 显示名 */
  name: string
  /** 品阶：normal / elite / boss */
  rank: string
  /** 立绘渲染配置 */
  visual?: CharacterVisual | null
  /** 当前气血 */
  hitpoints: number
  /** 当前灵力 */
  manapoints: number
  /** 下一次出手的战斗时刻（秒）；吟唱中为吟唱完成时刻 */
  nextAttackAtSeconds: number
  /** 累计出手次数 */
  attackCount: number
  /** 累积威胁值 */
  threat: number
  /** 正在吟唱的技能 ID；为 null 时下一次出手是普通攻击 */
  pendingAbilityId?: string | null
  /** 当前动作总时长秒数（吟唱时长或普攻间隔，服务端权威值） */
  pendingActionDurationSeconds: number
  /** 完整战斗属性面板（与玩家 combat.attributes 同一形状） */
  attributes?: CombatAttributes | null
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
  /** 玩家当前生命值 */
  playerHitpoints?: number | null
  /** 玩家当前灵力值 */
  playerManapoints?: number | null
  /** 本场战斗已流逝的战斗时钟秒数（出手/技能时刻表的参照系） */
  battleElapsedSeconds?: number
  /** 玩家下一次出手的战斗时刻（秒），配合 battleElapsedSeconds 推导行动条 */
  playerNextAttackAtSeconds?: number
  /** 敌方当前生命值（首个存活敌人） */
  enemyHitpoints?: number | null
  /** 敌方当前灵力值（首个存活敌人） */
  enemyManapoints?: number | null
  /** 敌人定义标识符（首个存活敌人） */
  enemyId?: string | null
  /** 同波全体敌人快照（战斗中非空，按下标稳定排序；原地复活倒地窗口内仍保留） */
  enemies?: ExpeditionEnemy[] | null
  /** 原地复活截止点（战斗时钟秒）：倒地窗口内非空，其余为 null */
  playerRespawnAtSeconds?: number | null
  /** 原地复活读条总时长（秒）：倒地窗口内非空 */
  playerRespawnDurationSeconds?: number | null
  /** 当前波次序号 */
  waveNumber?: number | null
  /** 玩家累积威胁值 */
  playerThreat?: number | null
  /** 遭遇战 ID */
  encounterId?: string | null
  /** 斗法回放显示信息（仅 phase 为 duel / duel-pending 时可能下发） */
  duel?: ExpeditionDuel | null
}

/**
 * 斗法（PVP）暂存区物品条目：
 * 勾选斗法的战斗行动期间，战斗掉落先入暂存区；
 * acquiredAtUnixSeconds + 1200s 到期自动结算入钱包，斗法败北则全部归胜方。
 */
export interface DuelStagedItem {
  /** 物品配置 ID */
  itemId: string
  /** 暂存数量 */
  quantity: number
  /** 进入暂存区的 Unix 秒时间戳 */
  acquiredAtUnixSeconds: number
}

/**
 * 单场战斗行动的六维经验入账集合
 */
export interface BattleReportRootExp {
  stamina: number
  meridians: number
  attack: number
  bone: number
  agility: number
  consciousness: number
}

/**
 * 战报物品收益条目（按 itemId 聚合）
 */
export interface BattleReportItem {
  itemId: string
  quantity: number
}

/**
 * 战报功法技能经验条目（按 abilityId 聚合）
 */
export interface BattleReportAbilityExp {
  abilityId: string
  experience: number
}

/**
 * 战报等级提升条目：kind 区分境界 / 六维 / 功法三类目标。
 * - realm：境界提升，targetId 为空串；
 * - root：六维提升，targetId 为六维键（stamina/meridians/attack/bone/agility/consciousness）；
 * - ability：功法提升，targetId 为功法 ID。
 */
export interface BattleReportLevelUp {
  kind: 'realm' | 'root' | 'ability'
  targetId: string
  fromLevel: number
  toLevel: number
}

/**
 * 行动收益账本（当前行动实例 / 离线挂机 / 每日统计 / 归档历史共用结构）。
 * reportId 为行动实例 ID；离线账本固定 "offline"，每日账本固定 "daily"（两者 actionId/actionName 允许空串）。
 */
export interface BattleReport {
  /** 账本唯一 ID（行动实例 ID 或固定 offline/daily） */
  reportId: string
  /** 行动配置 ID */
  actionId: string
  /** 行动显示名快照 */
  actionName: string
  /** 行动开始 Unix 秒 */
  startedAtUnixSeconds: number
  /** 行动结束/归档 Unix 秒（进行中为 0） */
  endedAtUnixSeconds: number
  /** 修为入账量 */
  qi: number
  /** 底蕴（历练经验）入账量 */
  experience: number
  /** 六维经验入账量 */
  rootExp: BattleReportRootExp
  /** 功法技能经验入账列表 */
  abilityExp: BattleReportAbilityExp[]
  /** 期间发生的等级提升（境界/六维/功法） */
  levelUps: BattleReportLevelUp[]
  /** 物品获得列表 */
  items: BattleReportItem[]
  /** 物品失去列表（行动输入、自动消耗品、斗法暂存被缴） */
  itemsLost: BattleReportItem[]
  /** 已完成轮次 */
  cycles: number
  /** 战斗遭遇场次 */
  encounters: number
  /** 普通敌怪击杀数 */
  normalKills: number
  /** 精英敌怪击杀数 */
  eliteKills: number
  /** 首领敌怪击杀数 */
  bossKills: number
  /** 死亡次数（讨伐失败与斗法败北） */
  deaths: number
  /** 每日账本所属自然日序号（UTC+8，仅每日账本使用） */
  dayIndex: number
  /** 本场行动内斗法胜场数 */
  duelWins: number
  /** 本场行动内斗法负场数 */
  duelLosses: number
}

/**
 * 斗法（PVP）分段状态（快照/增量 duel section 载荷）
 */
export interface DuelState {
  /** 斗法遭遇计数（>0 时下次战斗可触发玩家匹配） */
  encounterCharges: number
  /** 斗法冷却截止 Unix 秒（败北后 1 小时不再进入斗法） */
  cooldownUntilUnixSeconds: number
  /** 进行中的斗法 ID（无则 null） */
  activeDuelId: string | null
  /** 暂存区物品列表 */
  stagedItems: DuelStagedItem[]
  /** 当前队首行动账本 */
  currentReport: BattleReport | null
  /** 离线挂机账本（每次离线会话重置，reportId 固定 "offline"） */
  offlineReport: BattleReport | null
  /** 每日统计账本（自然日重置，reportId 固定 "daily"） */
  dailyReport: BattleReport | null
  /** 服务端保留的最近战报（最多 10 条） */
  reports: BattleReport[]
}

/**
 * 斗法剧本单条事件：出手时刻、双方身份与结算后的气血/灵力真值（服务端推演快照透传）
 */
export interface ExpeditionDuelEvent {
  /** 事件发生的剧本时刻（秒，与 battleElapsedSeconds 同一时钟） */
  atSeconds: number
  /** 出手方是否为斗法发起方（结合 viewerIsInitiator 翻译为本方/对方） */
  attackerIsInitiator: boolean
  /** 受击方是否为斗法发起方 */
  targetIsInitiator: boolean
  /** 出手功法 ID（普攻为 null，用于行动条显示技能名） */
  abilityId?: string | null
  /** 出手方结算后剩余灵力 */
  attackerManapointsAfter: number
  /** 受击方结算后剩余灵力 */
  targetManapointsAfter: number
}

/**
 * 远征斗法回放显示信息（expedition.duel 载荷）
 */
export interface ExpeditionDuel {
  /** 对手队伍道号 */
  opponentName: string
  /** 本方是否为斗法发起方 */
  isInitiator: boolean
  /** 斗法结果枚举：0 未决 / 1 胜 / 2 负 / 3 平 */
  outcome: number
  /** 是否已结算 */
  resolved: boolean
  /** 对手镜像完整战斗属性（撮合时冻结；上限取 attributes.maxHitpoints/maxManapoints） */
  opponentAttributes: CombatAttributes
  /** 对手当前生命值（随回放事件推进） */
  opponentHitpoints: number
  /** 对手当前灵力值（随回放事件推进） */
  opponentManapoints: number
  /** 剧本总时长（秒） */
  durationSeconds: number
  /** 剧本事件列表（用于推导双方下一次出手与资源条的行动条） */
  events: ExpeditionDuelEvent[]
}

/**
 * 实时战斗动作状态（攻击、施法、受控、读条）
 */
export interface CombatActionState {
  /** 动作显示名 */
  name: string
  /** 动作类别 */
  kind: CombatActionKind
  /** 已进行毫秒数 */
  elapsedMilliseconds: number
  /** 动作总耗时毫秒数 */
  durationMilliseconds: number
  /** 服务端给出的预计完成时刻（Unix 毫秒，用于校准读条） */
  completesAtUnixMilliseconds?: number | null
}

/**
 * 角色视觉渲染配置
 */
export interface CharacterVisual {
  /** 渲染器形态 */
  renderer: CharacterRendererKind
  /** 渲染源：图片 URL / 字符字形 / 模型资源标识，含义随 renderer 变化 */
  source: string
  /** 预览/封面图（live2d、model3d 等重型渲染的占位海报） */
  poster?: string | null
}

/**
 * 实时战斗血蓝资源与冷却状态
 */
export interface CombatResourceState {
  /** 当前气血 */
  hitpoints: number
  /** 气血上限 */
  maxHitpoints: number
  /** 当前灵力 */
  manapoints: number
  /** 灵力上限 */
  maxManapoints: number
  /** 功法冷却表：abilityId -> 剩余冷却毫秒 */
  abilityCooldownMilliseconds?: Record<string, number> | null
  /** 战斗消耗品冷却表：槽位下标 -> 剩余冷却毫秒 */
  consumableCooldownMilliseconds?: number[] | null
  /** 当前吟唱中的功法 ID（无则 null） */
  castingAbilityId?: string | null
  /** 吟唱剩余毫秒数 */
  castRemainingMilliseconds?: number
  /** 吟唱总毫秒数（配表吟唱 × 施法时间乘区；仅吟唱中下发，无吟唱为 null） */
  castTotalMilliseconds?: number | null
}

/**
 * 统一战斗属性面板（服务端权威终值；玩家/妖兽/斗法镜像同一形状同一语义）。
 * 全部百分比属性按点数下发（100 点 = 100%，总纲第七篇 §7.7.5），
 * 前端按对应曲线渲染：增伤类 Linear（+x → ×(1+x/100)），减伤/速率类 NonLinear（+x → ×100/(100+x)）。
 */
export interface CombatAttributes {
  maxHitpoints: number
  maxManapoints: number
  /** 物理攻击力（普攻结算基准） */
  physicalAttack: number
  /** 术法攻击力（术法技能结算基准） */
  spellAttack: number
  armor: number
  magicResistance: number
  accuracy: number
  evasion: number
  /** 暴击点数（与目标免暴对抗决定暴击率） */
  critical: number
  /** 免暴点数（降低被暴击概率） */
  criticalResistance: number
  /** 暴击伤害倍率（≥1，直存倍率） */
  criticalMultiplier: number
  /** 破招点数（与化解对抗） */
  breakPower: number
  /** 化解点数（与破招对抗） */
  parryResolve: number
  hpRegenPerFiveSeconds: number
  mpRegenPerFiveSeconds: number
  attackIntervalMilliseconds: number
  /** 普攻伤害倍率（成长曲线） */
  basicAttackRatio: number
  /** 技能强度乘区 */
  skillIntensity: number
  /** 冷却乘区 */
  cooldownFactor: number
  /** 吟唱乘区 */
  castTimeFactor: number
  /** 物理增伤点数（Linear） */
  physicalDamageBonusPoints: number
  /** 法术增伤点数（Linear） */
  spellDamageBonusPoints: number
  metalDamageBonusPoints: number
  woodDamageBonusPoints: number
  waterDamageBonusPoints: number
  fireDamageBonusPoints: number
  earthDamageBonusPoints: number
  /** 总增伤点数（Linear） */
  totalDamageBonusPoints: number
  /** 普攻增幅点数（Linear） */
  basicAttackBonusPoints: number
  /** 技能增幅点数（Linear） */
  skillBonusPoints: number
  /** 物理减伤点数（NonLinear） */
  physicalDamageReductionPoints: number
  /** 法术减伤点数（NonLinear） */
  spellDamageReductionPoints: number
  metalDamageReductionPoints: number
  woodDamageReductionPoints: number
  waterDamageReductionPoints: number
  fireDamageReductionPoints: number
  earthDamageReductionPoints: number
  /** 总减伤/免伤点数（NonLinear，末位收口） */
  damageAbsorptionPoints: number
  /** 攻击速度点数（NonLinear：仅缩短普攻读条） */
  attackSpeedPoints: number
  /** 行动速度点数（NonLinear：缩短全部读条） */
  actionSpeedPoints: number
  /** 冷却缩减点数（NonLinear：缩短全部冷却） */
  cooldownReductionPoints: number
  /** 威胁增幅点数（Linear） */
  threatBonusPoints: number
  /** 气血汲取点数（Linear 非负） */
  hpDrainPoints: number
  /** 灵力汲取点数（Linear 非负） */
  mpDrainPoints: number
  /** 伤害反弹点数 */
  damageReflectPoints: number
  /** 治疗增幅点数（Linear） */
  healingBonusPoints: number
}

/**
 * 玩家背包中的单个物品堆叠实例
 */
export interface InventoryItem {
  /** 物品配置 ID */
  itemId: string
  /** 资产变体（如强化等级）；null/缺省表示基础形态。同一 itemId 的不同变体是独立余额行 */
  variant?: string | null
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
  /** 物品唯一 ID */
  itemId: string
  /** 物品显示名 */
  name: string
  /** 物品描述文案 */
  description: string
  /** 基础形态大类 */
  kind: ItemKind
  /** 细分分类键 */
  category: ItemCategoryKey | string
  /** 品阶（1~8，映射品质与等级） */
  tier: number
  /** 系统出售价（灵石） */
  sellPrice: number
  /** 是否允许市场挂单交易 */
  isTradable: boolean
  /** 可挂载自动使用槽位的行动类型 */
  usableActionTypes: ActionType[]
  /** 使用冷却秒数 */
  cooldownSeconds: number
  /** 使用后即时恢复气血 */
  hitpointRestore: number
  /** 使用后即时恢复灵力 */
  manapointRestore: number
  /** 使用后附加的 Buff 效果列表 */
  buffEffects?: ItemBuffEffectDefinition[] | null
  /** 持续生效总秒数（持续型消耗品） */
  durationSeconds?: number
  /** 是否持续型消耗品（开启后按生效时长持续增益） */
  isContinuous?: boolean
  /** 修炼产出倍率 */
  cultivationMultiplier?: number
  /** 历练采集产出倍率 */
  gatheringMultiplier?: number
  /** 炼丹产出倍率 */
  alchemyMultiplier?: number
}

/**
 * Buff 修饰器唯一形状：行动收益授权 / 物品消耗品增益 / 活跃 Buff 三处共用同一字段集。
 * statRatios 为通用属性比率字典（key 为属性名，值按层数叠加进属性乘区）。
 */
export interface BuffModifiers {
  /** 每 5 秒结算点气血回复量 */
  hpRegen?: number
  /** 每 5 秒结算点灵力回复量 */
  mpRegen?: number
  /** 攻击速度点数（NonLinear） */
  attackSpeedPoints?: number
  /** 免伤点数（NonLinear 总减伤乘区） */
  damageAbsorptionPoints?: number
  /** 通用属性比率字典（可选） */
  statRatios?: Record<string, number> | null
}

/**
 * 物品附加 Buff 效果定义（消耗品 buffs[] 配置，与领域结算字段一一对应）
 */
export interface ItemBuffEffectDefinition extends BuffModifiers {
  /** Buff 配置 ID（用于叠加归并与到期移除） */
  buffId?: string
  /** Buff 显示名 */
  name: string
  /** 单次生效秒数 */
  durationSeconds: number
}

/**
 * 服务端下发的日志事件数据
 */
export interface LogEvent {
  /** 服务端日志单调序号（用于去重与排序） */
  sequence: number
  /** 发生时刻（Unix 毫秒） */
  timestampUnixMilliseconds: number
  /** 语义模板文本，内嵌 {{kind:id|label|extra}} 引用标记（见 gameLogService.parseLogTemplate） */
  template: string
  /** 服务端侧已折叠的重复次数，缺省为 1 */
  count?: number
  /** 日志分组频道：`system`（系统）或 `combat`（战斗）；缺省视为 `system` */
  channel?: 'system' | 'combat'
}

/**
 * 角色身上当前生效的增益/减益状态 (Buff)
 * 字段即服务端 ActiveBuff 权威结算规格；修饰器形状与 BuffModifiers 一致。
 */
export interface Buff extends Required<Pick<BuffModifiers, 'hpRegen' | 'mpRegen' | 'attackSpeedPoints' | 'damageAbsorptionPoints'>> {
  /** Buff 配置 ID */
  buffId: string
  /** Buff 显示名 */
  name: string
  /** 当前叠加层数 */
  stacks: number
  /** 到期时刻（Unix 秒） */
  expiresAtUnixSeconds: number
  /** 来源物品 ID */
  sourceItemId?: string | null
  /** 通用属性比率字典（可选） */
  statRatios?: Record<string, number> | null
}

/**
 * 角色历史累计统计数据
 */
export interface Stats {
  /** 服务端权威境界等级（1~127），缺省时由 spirit 累计修为反推 */
  realmLevel?: number
  /** 修为累计量（progression.qi 透传，与境界经验 spirit 分属不同计数） */
  qi: number
  /** 境界修为（progression.experience 透传，突破时由服务端按规则清零） */
  spirit: number
  /** 道基底蕴（当前 wire 未下发独立字段，保留契约位） */
  foundationExp?: number
  /** 累计采集灵草次数 */
  herbsCollected?: number
  /** 累计炼成丹药次数 */
  pillsCrafted?: number
  /** 累计完成修炼周天轮数 */
  cultivationCycles?: number
  /** 累计完成的行动轮次 */
  completedActions: number
  /** 累计受阻中断的行动次数 */
  blockedActions?: number
  /** 斗法胜场数 */
  combatWins?: number
  /** 战斗败北次数 */
  combatLosses?: number
  /** 历练经验入账量 */
  combatExperience?: number
  /** 累计造成伤害 */
  combatDamageDealt?: number
  /** 累计承受伤害 */
  combatDamageTaken?: number
  /** 累计击杀敌方单位（普通+精英+首领合计） */
  combatEnemiesDefeated?: number
  /** 斗法中击败的玩家人数 */
  combatPlayersDefeated?: number
  /** 普通怪击杀数 */
  normalKills?: number
  /** 精英怪击杀数 */
  eliteKills?: number
  /** 首领怪击杀数 */
  bossKills?: number
  /** 累计修炼秒数 */
  cultivationSeconds?: number
  /** 角色存活秒数（游戏内年龄） */
  ageSeconds?: number
  /** 寿元上限（年） */
  lifespanYears?: number
}

/**
 * 战斗技能槽位配置
 */
export interface CombatAbilitySlot {
  /** 装配的功法 ID（null 为空槽） */
  abilityId: string | null
  /** 功法演练等级 */
  level: number
  /** 自动释放触发条件列表 */
  triggers?: CombatTrigger[]
}

/**
 * 战斗自动触发条件规则配置
 */
export interface CombatTrigger {
  /** 判定目标（自身/目标敌/气血最低或最高的敌我单位） */
  dependency: CombatTriggerDependency
  /** 参与判定的量（当前/缺失气血、灵力及其百分比、Buff 层数、控制状态） */
  condition: CombatTriggerCondition
  /** 比较方式（数值比较或 is-active/is-inactive 状态存在性判定） */
  comparator: CombatTriggerComparator
  /** 判定阈值（百分比条件为点数，状态条件忽略） */
  value: number
  /** 关联 Buff ID（condition 为 buff-stacks 或状态判定时使用） */
  buffId?: string | null
}

/**
 * 功法技能配置定义
 */
export interface CombatAbilityDefinition {
  /** 功法唯一 ID */
  abilityId: string
  /** 功法显示名 */
  name: string
  /** 功法招式描述（悬浮详情自然语言开头，如「从掌心发出一道雷光」） */
  description?: string
  /** 功法门类：internal 内功心法（仅可装配 0 号内功位）/ spell 神通术法；缺省视为 spell */
  discipline?: 'internal' | 'spell'
  /** 武道流派 */
  style: CombatStyle
  /** 伤害大类 */
  category: DamageCategory
  /** 五行属性 */
  element: Element
  /** 冷却毫秒数（缺省按 0，即无冷却） */
  cooldownMilliseconds?: number
  /** 吟唱/引导毫秒数（缺省按 0） */
  castMilliseconds?: number
  /** 灵力消耗（缺省按 0） */
  manaCost?: number
  /** 伤害倍率（相对攻击属性的比例点数，缺省按 0） */
  damageRatio?: number
  /** 固定附加伤害（缺省按 0） */
  flatDamage?: number
  /** 效果大类（伤害/治疗），缺省视为伤害 */
  effectType?: CombatAbilityEffectType
  /** 治疗倍率 */
  healingRatio?: number
  /** 固定治疗量 */
  flatHealing?: number
  /** 出厂默认触发条件 */
  defaultTriggers?: CombatTrigger[] | null
  /** 施法目标范围，缺省为单体敌人 */
  targetType?: CombatAbilityTargetType
  /** 支援类：固定威胁值提升（嘲讽等） */
  threatGain?: number
  /** 支援类：威胁增幅点数（Linear，施放时并入施放方威胁增幅） */
  threatPoints?: number
  /** 品阶（配表声明，缺省视为 1） */
  tier?: number
  /** 根骨加成属性键（配表声明） */
  rootAttribute?: string
  /** 支援类：施放后施加的战内 Buff（破阵子等）；修饰字段为点数口径 */
  buff?: { name?: string; durationSeconds?: number; damageAbsorptionPoints?: number; hpRegen?: number; mpRegen?: number; attackSpeedPoints?: number; threatPoints?: number } | null
}

/**
 * 怪物掉落条目定义
 * 结算语义为「先判定后数量」：先按 dropChance 判定是否掉落，
 * 命中后再在 [minQuantity, maxQuantity] 整数区间内均匀抽取掉落数量。
 */
export interface MonsterDropDefinition {
  /** 掉落物品 ID */
  itemId: string
  /** 掉落概率（0~1 小数比率，先判定是否掉落） */
  dropChance: number
  /** 数量区间下限（含） */
  minQuantity: number
  /** 数量区间上限（含） */
  maxQuantity: number
}

/**
 * 参战单位/敌人基础定义
 */
export interface CombatantDefinition {
  /** 怪物/单位配置 ID */
  enemyId: string
  /** 显示名 */
  name: string
  /** 立绘渲染配置 */
  visual: CharacterVisual
  style: CombatStyle
  category: DamageCategory
  element: Element
  rank?: UnitRank
  /** 完整战斗属性（与玩家 combat.attributes 同一形状同一语义） */
  attributes: CombatAttributes
  /** 击杀后入账的历练经验（底蕴） */
  experience: number
  /** 境界等级（怪物同构建模的等级锚点） */
  level?: number
  /** 品阶（由等级映射的境界阶位） */
  tier?: number
  /** 占用刷怪强度预算的点数 */
  budgetCost?: number
  /** 击杀后入账的修为 */
  qiReward?: number
  /** 击杀后入账的六维经验 */
  rootExpReward?: number
  /** 掉落条目列表（先按 dropChance 判定，再在数量区间均匀抽取） */
  drops?: MonsterDropDefinition[]
  /** 该单位可使用的功法 ID 列表 */
  abilityIds: string[]
}

/**
 * 遭遇战随机刷怪权重定义
 */
export interface CombatSpawnDefinition {
  /** 候选怪物定义 */
  enemy: CombatantDefinition
  /** 抽取权重 */
  weight: number
  /** 占用刷怪强度预算的点数 */
  strength: number
}

/**
 * 遭遇战副本配置定义
 */
export interface CombatEncounterDefinition {
  /** 遭遇战唯一 ID */
  encounterId: string
  /** 遭遇战显示名 */
  name: string
  /** 主敌（保底出场的核心敌人） */
  enemy: CombatantDefinition
  /** 随机刷怪候选池 */
  randomSpawns: CombatSpawnDefinition[]
  /** 单场最大刷怪数量 */
  maxSpawnCount: number
  /** 单场刷怪强度预算上限 */
  maxTotalStrength: number
  /** Boss 波次敌人列表 */
  bossEnemies: CombatantDefinition[]
  /** 每多少波普通战斗后进入一次 Boss 波 */
  battlesPerBoss: number
  /** 搜寻下一波敌人的耗时毫秒数 */
  searchMilliseconds: number
  /** 敌人重生耗时毫秒数 */
  respawnMilliseconds: number
  /** 玩家败北后复活耗时毫秒数 */
  playerRespawnMilliseconds: number
}

/**
 * 装备属性增益汇总
 */
/**
 * 装备配置定义（协议只透传配表原始数值块，结算由服务端属性解析器完成；
 * 前端不得再消费派生 stats 幻影块或本地重建战斗终值）
 */
export interface CombatEquipmentDefinition {
  /** 装备唯一 ID */
  equipmentId: string
  /** 装备显示名 */
  name: string
  /** 穿戴槽位 */
  slot: EquipmentSlot
  /** 品阶 */
  tier: number
  /** 每级强化附加的属性增益（配表原始字典） */
  enhancementBonus?: Record<string, number>
  /** 配表原始比例段（服务端透传，供详情面板展示） */
  baseStatRatios?: Record<string, number>
  /** 配表原始固定值段 */
  flatStats?: Record<string, number>
  /** 特殊机制段（如 threatBonusPoints 威胁增幅、healingBonusPoints 治疗增幅等点数键） */
  specialMechanics?: Record<string, number>
}

/**
 * 已装备的物品实例（含强化等级）
 */
export interface EquippedItem {
  /** 装备配置 ID */
  itemId: string
  /** 强化等级 */
  enhancementLevel: number
}

/**
 * 战斗消耗品槽位配置（服务端 AutoUseSlotState 投影；消耗品与功法共用
 * 「对象-类型-比对」触发条件词表，触发判定在领域层统一求值）
 */
export interface ConsumableSlot {
  /** 绑定的物品 ID（null 为空槽） */
  itemId: string | null
  /** 单次使用数量 */
  quantity: number
  /** 是否启用自动使用 */
  enabled: boolean
}

/**
 * 玩家战斗装配方案
 */
export interface CombatLoadout {
  /** 六维根基等级：体魄 */
  staminaLevel: number
  /** 六维根基等级：经脉 */
  meridiansLevel: number
  /** 六维根基等级：力道 */
  attackLevel: number
  /** 六维根基等级：根骨 */
  boneLevel: number
  /** 六维根基等级：身法 */
  agilityLevel: number
  /** 六维根基等级：神识 */
  consciousnessLevel: number
  /** 六维累计经验（等级由服务端派生，缺省视为 0） */
  staminaExp?: number
  meridiansExp?: number
  attackExp?: number
  boneExp?: number
  agilityExp?: number
  consciousnessExp?: number
  /** 功法技能槽位（第 0 槽为内功位，其余为术法位） */
  abilitySlots: CombatAbilitySlot[]
  /** 已穿戴装备列表 */
  equipment?: EquippedItem[]
  /** 已学会的功法 ID 列表 */
  learnedAbilityIds?: string[]
  /** 功法经验表：abilityId -> 累计经验 */
  abilityExperiences?: Record<string, number>
}

/**
 * 地图节点附近 NPC 或建筑物实体
 */
export interface NearbyEntity {
  /** 实体唯一 ID */
  entityId: string
  /** 显示名 */
  name: string
  /** 实体类别 */
  kind: NearbyEntityKind
  /** 描述文案 */
  description: string
  /** 效果说明文案（如修炼加成、交易功能） */
  effect: string
}

/**
 * 地点栖息的怪物实体定义
 */
export interface NearbyMonsterDefinition {
  /** 怪物配置 ID */
  monsterId: string
  /** 显示名 */
  name: string
  /** 等级 */
  level: number
  /** 品阶 */
  tier: number
  /** 阶位（normal/elite/boss 等） */
  rank: string
  /** 占用刷怪预算的点数 */
  budgetCost: number
  /** 气血上限 */
  maxHitpoints: number
  /** 物理攻击力 */
  physicalAttack: number
  /** 护甲 */
  armor: number
  /** 掉落条目列表（先按 dropChance 判定，再在数量区间均匀抽取） */
  drops: MonsterDropDefinition[]
  /** 立绘资源地址 */
  visualSource?: string | null
  /** 立绘渲染器形态 */
  visualRenderer?: string | null
}

/**
 * 建筑每级基准效果（产出/收益为比率，回血为每 5 秒固定值）
 */
export interface BuildingModifiers {
  /** 历练采集行动加速比率 */
  explorationSpeedRatio: number
  /** 历练采集行动收益比率 */
  explorationOutputRatio: number
  /** 炼丹炼器行动加速比率 */
  alchemySpeedRatio: number
  /** 炼丹炼器行动收益比率 */
  alchemyOutputRatio: number
  /** 吐纳修炼灵气收益比率 */
  cultivationOutputRatio: number
  /** 战斗气血自然回复（每 5 秒固定值） */
  combatHitpointRegen: number
  /** 战斗额外减伤比率 */
  combatDamageReductionRatio: number
}

/**
 * 建筑一级基础材料需求项（第 N 级需求 = 基础 × 1.1^(N-1)）
 */
export interface BuildingMaterial {
  /** 材料物品 ID */
  itemId: string
  /** 一级基础数量 */
  quantity: number
}

/**
 * 玩家可建造建筑的静态配表定义（/api/v1/catalog/buildings 下发）
 */
export interface BuildingDefinition {
  /** 建筑配置 ID */
  buildingId: string
  /** 建筑显示名 */
  name: string
  /** 建筑描述文案 */
  description: string
  /** 效果说明文案 */
  effect: string
  /** 建造解锁所需境界等级 */
  requiredRealmLevel: number
  /** 限定可建造节点 ID 列表（缺省/为空表示任意节点可建） */
  availableNodeIds?: string[] | null
  /** 建筑分组（production/combat/other） */
  group: string
  /** 每级基准效果 */
  modifiers?: BuildingModifiers
  /** 一级基础材料清单 */
  materials?: BuildingMaterial[]
}

/**
 * 节点公共建筑材料明细（本级需求与已交数量）
 */
export interface NodeBuildingMaterial {
  /** 材料物品 ID */
  itemId: string
  /** 本级需求数量 */
  required: number
  /** 已提交数量 */
  contributed: number
}

/**
 * 节点公共建筑实例（快照 world.nodeBuildings 下发；系统建筑为配表实体，玩家建筑为公共工程）
 */
export interface NodeBuilding {
  /** 建筑配置 ID */
  entityId: string
  /** 建筑显示名 */
  name: string
  /** 实体类型（building） */
  kind?: string
  /** 建筑描述文案 */
  description: string
  /** 效果说明文案 */
  effect: string
  /** 建筑分组 */
  group: string
  /** 已建成有效等级（0 = 尚未建成） */
  level: number
  /** 进行中工程目标等级（0 = 空闲） */
  targetLevel: number
  /** 建造总进度 = min(材料进度, 时间进度) */
  buildProgress: number
  /** 材料填充进度 (0~1) */
  materialProgress: number
  /** 时间推进进度 (0~1) */
  timeProgress: number
  /** 本级工程所需总秒数 */
  requiredSeconds: number
  /** 本级工程已耗时秒数 */
  elapsedSeconds: number
  /** 本级材料需求与已交明细 */
  materials: NodeBuildingMaterial[]
}

/**
 * 大地图节点定义
 */
export interface WorldNodeDefinition {
  /** 节点唯一 ID */
  nodeId: string
  /** 地点显示名 */
  name: string
  /** 地点描述 */
  description: string
  /** 地图画布 X 坐标 */
  x: number
  /** 地图画布 Y 坐标 */
  y: number
  /** 该节点的修炼收益倍率（灵脉加成） */
  cultivationMultiplier: number
  /** 该节点可执行的行动 ID 列表 */
  availableActionIds: string[]
  /** 该节点可进入的遭遇战 ID 列表 */
  encounterIds: string[]
  /** 驻留 NPC 列表 */
  npcs: NearbyEntity[]
  /** 建筑/功能实体列表 */
  buildings: NearbyEntity[]
  /** 刷怪预算下限 */
  minBudget?: number
  /** 刷怪预算上限 */
  maxBudget?: number
  /** 精英怪刷新间隔（秒） */
  eliteInterval?: number
  /** 首领怪刷新间隔（秒） */
  bossInterval?: number
  /** 常驻普通怪列表（静态目录投影，CombatantDefinition 形态） */
  monsters?: CombatantDefinition[]
  /** 精英怪列表 */
  eliteEnemies?: CombatantDefinition[]
  /** 首领怪列表 */
  bossEnemies?: CombatantDefinition[]
}

/**
 * 大地图连接边（道路）定义
 */
export interface WorldEdgeDefinition {
  /** 边唯一 ID */
  edgeId: string
  /** 起点节点 ID */
  fromNodeId: string
  /** 终点节点 ID */
  toNodeId: string
  /** 通过该边的行进耗时（秒） */
  durationSeconds: number
}

/**
 * 大地图世界区域定义
 */
export interface WorldMapDefinition {
  /** 地图唯一 ID */
  mapId: string
  /** 地图显示名 */
  name: string
  /** 地图描述 */
  description: string
  /** 程序化背景生成随机种子 */
  backgroundSeed: number
  /** 节点列表 */
  nodes: WorldNodeDefinition[]
  /** 连接边列表 */
  edges: WorldEdgeDefinition[]
}

/**
 * 玩家当前所在的世界位置与周边环境状态
 */
export interface WorldState {
  /** 当前所在地图 ID */
  mapId: string
  /** 当前所在节点 ID（行进在边上时为 null，与 edgeId 互斥） */
  nodeId: string | null
  /** 当前行进中的边 ID（驻留节点时为 null） */
  edgeId: string | null
  /** 当前边上的归一化行进进度 (0~1) */
  edgeProgress: number
  /** 当前地图 X 坐标 */
  x: number
  /** 当前地图 Y 坐标 */
  y: number
  /** 当前地点显示名（旅途中为兜底文案） */
  locationName: string
  /** 当前地点修炼收益倍率 */
  cultivationMultiplier: number
  /** 当前地点可执行的行动 ID 列表 */
  availableActionIds: string[]
  /** 当前地点可进入的遭遇战 ID 列表 */
  encounterIds: string[]
  /** 周边 NPC 列表 */
  npcs: NearbyEntity[]
  /** 周边建筑/功能实体列表（系统建筑） */
  buildings: NearbyEntity[]
  /** 当前节点公共建筑列表（玩家建筑为共享工程，含等级与双进度） */
  nodeBuildings?: NodeBuilding[]
  /** 当前节点可建造建筑 ID 列表（null = 该节点不可建造，隐藏加号卡） */
  buildableBuildingIds?: string[] | null
  /** 刷怪预算下限 */
  minBudget?: number
  /** 刷怪预算上限 */
  maxBudget?: number
  /** 精英怪刷新间隔（秒） */
  eliteInterval?: number
  /** 首领怪刷新间隔（秒） */
  bossInterval?: number
  /** 周边普通怪列表 */
  monsters?: NearbyMonsterDefinition[]
  /** 周边精英怪列表 */
  eliteEnemies?: NearbyMonsterDefinition[]
  /** 周边首领怪列表 */
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
  /** 斗法（PVP）与战斗收益战报状态 */
  duel: DuelState
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
 * 仅携带本次发生变更的分段；action_queue 分段为唯一行动队列通道（全量分段替换）。
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
  combat?: CombatLoadout
  combatResources?: CombatResourceState
  combatAttributes?: CombatAttributes
  visual?: CharacterVisual
  world?: WorldState
  duel?: DuelState
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
  /** 市场账本分区（分片）数量 */
  shardCount: number
  /** 当前活跃挂单总数 */
  activeOrderCount: number
  /** 累计挂单笔数 */
  placementCount: number
  /** 累计撮合成交笔数 */
  matchCount: number
  /** 累计撤单笔数 */
  cancelCount: number
  /** 累计被驳回笔数 */
  rejectedCount: number
  /** 累计成交物品数量 */
  tradedQuantity: number
  /** 累计成交总额（灵石） */
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
  /** 过期时间 ISO 字符串；无期限订单为 null */
  expiresAt: string | null
}
