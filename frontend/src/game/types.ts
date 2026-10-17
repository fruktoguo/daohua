/**
 * 玩家行动类型：修炼 (cultivation)、探索 (exploration)、炼丹技艺 (alchemy)、战斗 (combat)、装备强化 (enhancement)
 * 符箓/阵法制造行动复用 alchemy 大类（经 technique 字段区分所属技艺）。
 */
export type ActionType = 'cultivation' | 'exploration' | 'alchemy' | 'combat' | 'enhancement'

/**
 * 自动使用物品栏域（独立于行动大类的归属键，见总纲 §6.2.3）：
 * exploration 历练栏涵盖修炼/历练两大类行动；alchemy/forging/talisman/formation
 * 为制造技艺栏（制造大类行动按 technique 归属）；enhancement 强化栏；combat 战斗栏。
 */
export type AutoUseBarType = 'exploration' | 'alchemy' | 'forging' | 'talisman' | 'formation' | 'enhancement' | 'combat'

/**
 * 生活技艺标识（七技）：炼丹 alchemy、炼器 forging、符箓 talisman、强化 enhancement、
 * 阵法 formation、采集 gathering、挖掘 mining。配表行动经 technique 字段归属到对应技艺。
 */
export type TechniqueId = 'alchemy' | 'forging' | 'talisman' | 'enhancement' | 'formation' | 'gathering' | 'mining'

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
  | 'spiritMaterial'
  | 'mineral'
  | 'exoticMaterial'
  | 'refinedMaterial'
  | 'herb'
  | 'pill'
  | 'food'
  | 'drink'
  | 'talisman'
  | 'formation'
  | 'manual'
  | 'equipment'
  | 'chest'
  | 'currency'
  | 'resource'
/**
 * 装备穿戴槽位：主手武器 (weapon)、双手武器 (two-hand)、副手 (off-hand，盾牌/暗器/法珠/丹瓶)、
 * 头部 (head)、护甲/上衣 (armor)、腿部/下装 (legs)、
 * 戒指 (ring)、项链 (neck)、饰品/法宝 (trinket)、符箓/灵宝 (talisman)、
 * 七技艺工具专属槽位 tool-<techniqueId>（采集/挖掘/炼丹/炼器/符箓/阵法/强化各一，
 * 服务端按 slot 字符串唯一互斥，仅容纳对应技艺的工具装备）。
 */
export type EquipmentSlot =
  | 'weapon'
  | 'two-hand'
  | 'off-hand'
  | 'head'
  | 'armor'
  | 'legs'
  | 'ring'
  | 'neck'
  | 'trinket'
  | 'talisman'
  | 'tool-alchemy'
  | 'tool-forging'
  | 'tool-talisman'
  | 'tool-enhancement'
  | 'tool-formation'
  | 'tool-gathering'
  | 'tool-mining'

/**
 * 参战单位/怪物生命层次：
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
 * 行动收益类别：灵力/战斗蓝 (mana)、气血 (hitpoints)、修为 (experience)、物品产出 (item)、增益状态 (buff)
 */
export type ActionYieldKind = 'mana' | 'hitpoints' | 'experience' | 'item' | 'buff' | 'rootexp'

/** 六维归属键（与服务端 CoreAspectKind 小写名一致） */
export type RootAspectKey = 'stamina' | 'meridians' | 'attack' | 'bone' | 'agility' | 'consciousness'

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
  /** 收益比率（1.0 代表 100%，0.5 代表 50%） */
  ratio?: number | null
  /** 收益百分比（100 代表 100%，50 代表 50%） */
  percentage?: number | null
  id?: string | null
  /** Buff 显示名称（仅 buff 有值） */
  name?: string | null
  /** Buff 持续秒数（仅 buff 有值） */
  durationSeconds?: number | null
  /** Buff 修饰器（仅 buff 有值；与 BuffModifiers 同一形状） */
  modifiers?: BuffModifiers | null
  /** 六维归属维度（仅 rootexp 有值；定额收益不吃产出倍率） */
  aspect?: RootAspectKey | null
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
  /** 归属生活技艺（配表可省；技艺经验按完成轮次的基础耗时入账） */
  technique?: TechniqueId | string | null
  /** 单轮执行耗时（秒） */
  durationSeconds: number
  /** 境界等级需求 / 基准等级（T1为1，T2为18等） */
  level?: number
  /** 特殊执行类型（如 'combat-encounter'） */
  executionKind?: ActionExecutionKind
  /** 是否仅供服务端/队列内部使用，不在玩家行动目录中展示 */
  hiddenFromCatalog?: boolean
  /** 关联的战斗遭遇 ID */
  combatEncounterId?: string
  /** 消耗材料物品与数量映射 */
  inputs?: Record<string, number>
  /** 行动收益列表（修为/灵力/气血/物品/增益） */
  yields?: ActionYield[]
  /** 允许执行该行动的地图节点 ID 列表 */
  availableNodeIds?: string[]
  /** 禁止执行该行动的地图节点 ID 列表（与 availableNodeIds 互斥；常驻行动按地点裁剪） */
  excludedNodeIds?: string[]
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
  /** 已完成轮次数（服务端权威累计值，用于识别无限循环或单项循环跨轮重置） */
  completedRepeats?: number
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
  /** 强化等行动的目标物品 ID（ActionIntent.TargetItemId 投影） */
  targetItemId?: string | null
  /** 辅助物品 ID（如强化保护材料；ActionIntent.AuxItemId 投影） */
  auxItemId?: string | null
  /** 行动参数中的目标强化等级（ActionIntent.Parameters["targetLevel"] 投影） */
  targetLevel?: number | null
  /** 强化行动当前强化的物品变体等级（单品锁定追踪） */
  enhancementCurrentLevel?: number | null
  /** 行动意图的动态参数字典（如 targetLevel、protectLevel、initialLevel） */
  parameters?: Record<string, number> | null
  /** 行动参与会话标记：组织适配器创建，行动执行与表现不识别组织类型。 */
  participation?: { sessionId: string; endsAtUnixSeconds?: number | null } | null
}

/** 判断队列行动是否属于行动参与会话。 */
export function hasActionParticipation(entry: Pick<QueueEntry, 'participation'>): boolean {
  return entry.participation != null
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
  /** 开轮地点不符时是否自动移动回入队锚点 */
  autoTravelEnabled: boolean
  /** 队列是否处于手动暂停 */
  paused: boolean
  /** 各物品栏域对应的自动消耗品槽位映射（历练 6 槽、技艺 3 槽、战斗药品 3+通用 3） */
  autoUseSlots: Record<AutoUseBarType, AutoUseSlot[]>
}

/**
 * 同波单个敌方单位的战斗快照（服务端投影：名称/生命层次/立绘/实时战斗字段随快照下发）。
 * 属性面板为按需一次性交付——UnitInspect 登记后随下一帧快照携带一次即停止，
 * 前端按 enemyId 缓存复用；未点开详情的怪不占带宽；图鉴/生态等战前查看走 CombatantDefinition 本地目录。
 */
/**
 * 战内 Buff 快照（服务端投影）：战斗时钟到期时刻与效果字段随单位快照下发，
 * 结算权威在服务端；expiresAtSeconds 与 battleElapsedSeconds 同一时钟域。
 */
export interface CombatBuffEntry {
  /** Buff 配置 ID */
  buffId: string
  /** Buff 显示名（授予时定格） */
  name: string
  /** 当前叠加层数 */
  stacks: number
  /** 到期时刻（战斗时钟秒，与 battleElapsedSeconds 同域；double.MaxValue 表示永驻） */
  expiresAtSeconds: number
  /** 每 5 秒结算点气血回复量 */
  hpRegen?: number
  /** 每 5 秒结算点灵力回复量 */
  mpRegen?: number
  /** 攻击速度点数 */
  attackSpeedPoints?: number
  /** 免伤点数 */
  damageAbsorptionPoints?: number
  /** 威胁增幅点数 */
  threatPoints?: number
  /** 伤害增幅点数 */
  damageBonusPoints?: number
  /** 每秒威胁成长点数（最大气血×点数/100） */
  threatPerSecondMaxHpPoints?: number
  /** 气血回复后段比率 */
  hpRegenPostRatio?: number
  /** 灵力回复后段比率 */
  mpRegenPostRatio?: number
}

export interface ExpeditionEnemy {
  /** 配表怪物定义 ID */
  enemyId: string
  /** 显示名 */
  name: string
  /** 生命层次：normal / elite / boss */
  rank: string
  /** 立绘渲染配置 */
  visual?: CharacterVisual | null
  /** 当前气血 */
  hitpoints: number
  /** 当前灵力 */
  manapoints: number
  /** 气血上限（血蓝条分母，配表缺失时为 null） */
  maxHitpoints?: number | null
  /** 灵力上限 */
  maxManapoints?: number | null
  /** 下一次出手的战斗时刻（秒）；吟唱中为吟唱完成时刻 */
  nextAttackAtSeconds: number
  /** 累计出手次数 */
  attackCount: number
  /** 有效威胁值：基础实际值×(1+威胁增幅点数/100)，与目标判定同口径 */
  threat: number
  /** 正在吟唱的技能 ID；为 null 时下一次出手是普通攻击 */
  pendingAbilityId?: string | null
  /** 当前动作总时长秒数（吟唱时长或普攻间隔，服务端权威值） */
  pendingActionDurationSeconds: number
  /** 完整战斗属性面板（与玩家 combat.attributes 同一形状）；仅被观察目标命中时随快照携带 */
  attributes?: CombatAttributes | null
  /** 生效中的战内 Buff（战斗时钟到期时刻随快照下发） */
  buffs?: CombatBuffEntry[] | null
}

/**
 * 组队讨伐盟友单位快照（expedition.allies 数组元素，§21.13）。
 * 按会话规格成员序排列，包含全部在场成员（含本机成员位）。
 * 属性面板为按需一次性交付——UnitInspect 登记 side:ally + memberIndex 后随下一帧快照
 * 携带一次开打冻结面板即停止，前端按 ally:{playerId} 缓存复用。
 */
export interface ExpeditionAlly {
  /** 会话规格成员序下标（0 起，与 allies 数组序一致） */
  memberIndex: number
  /** 成员玩家 ID（与队伍名册 playerId 配对键） */
  playerId: string
  /** 成员道号 */
  displayName: string
  avatar?: string
  /** 当前气血 */
  hitpoints: number
  /** 当前灵力 */
  manapoints: number
  /** 气血上限（血蓝条分母，快照缺失时为 null） */
  maxHitpoints?: number | null
  /** 灵力上限 */
  maxManapoints?: number | null
  /** 下一次出手的战斗时刻（秒，与 battleElapsedSeconds 同一时钟） */
  nextAttackAtSeconds: number
  /** 累计出手次数 */
  attackCount: number
  /** 有效威胁值：基础实际值×(1+威胁增幅点数/100)，与目标判定同口径 */
  threat: number
  /** 正在吟唱的技能 ID；为 null 时下一次出手是普通攻击 */
  pendingAbilityId?: string | null
  /** 当前动作总时长秒数（吟唱时长或普攻间隔，服务端权威值） */
  pendingActionDurationSeconds: number
  /** 倒地复活截止点（战斗时钟秒）；存活或无复活窗口时为 null */
  respawnAtSeconds?: number | null
  /** 开打冻结战斗属性面板（与玩家 combat.attributes 同一形状）；仅被观察目标命中时随快照携带 */
  attributes?: CombatAttributes | null
  /** 开打冻结功法槽位（CombatAbilitySlot 形状）；与 attributes 同帧一次性交付，未观察时为 null */
  skills?: CombatAbilitySlot[] | null
  /** 开打定格已穿戴装备（仅装配展示用）；与 attributes 同帧一次性交付，未观察时为 null */
  equipment?: EquippedItem[] | null
  /** 生效中的战内 Buff（战斗时钟到期时刻随快照下发） */
  buffs?: CombatBuffEntry[] | null
}

/**
 * 遭遇战单次打击/受创事件（伤害/闪避/破招/拆招/暴击），用于战场浮字与动作反馈
 */
export interface CombatRecentEvent {
  /** 战斗时钟时刻（秒） */
  at: number
  /** 出手方 ID (player / enemy:0 / ally:1) */
  attackerId: string
  /** 受击方 ID */
  targetId: string
  /** 实收伤害 */
  damage: number
  /** 是否命中（false 为闪避） */
  hit: boolean
  /** 是否暴击 */
  critical?: boolean
  /** 是否破招 */
  broken?: boolean
  /** 是否拆招 */
  parried?: boolean
  /** 施放技能 ID（普攻为 null） */
  abilityId?: string | null
  /** 结算后的受击方剩余气血 */
  targetHp?: number
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
  /** 玩家有效威胁值：基础实际值×(1+威胁增幅点数/100)，与目标判定同口径 */
  playerThreat?: number | null
  /** 本机生效中的战内 Buff（含开战注入的消耗品副本）；非战斗相位 PlayerState 为空为 null */
  playerBuffs?: CombatBuffEntry[] | null
  /** 遭遇战 ID */
  encounterId?: string | null
  /** 斗法回放显示信息（仅 phase 为 duel / duel-pending 时可能下发） */
  duel?: ExpeditionDuel | null
  /** 行动参与会话盟友阵列：按参与者固定顺序下发，含本机成员位。 */
  allies?: ExpeditionAlly[] | null
  /** 本机成员在 allies 中的下标 */
  selfIndex?: number | null
  /** 最近步进产生的基础战斗事件流（普攻/技能命中/闪避/破招/拆招/暴击） */
  recentEvents?: CombatRecentEvent[] | null
}

/**
 * 斗法（PVP）暂存区物品批次：
 * 勾选斗法的战斗行动期间，战斗掉落先入暂存区；
 * 服务端按 stagedBucketSeconds（300s）档位桶合并，acquiredAtUnixSeconds 为桶尾时刻，
 * + 1200s 到期自动结算入钱包，斗法败北则全部归胜方。
 */
export interface DuelStagedItem {
  /** 物品配置 ID */
  itemId: string
  /** 本批暂存数量 */
  quantity: number
  /** 批次桶尾 Unix 秒时间戳（结算时刻 = 该值 + 1200s） */
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
 * 战报等级提升条目：kind 区分境界 / 六维 / 功法 / 技艺四类目标。
 * - realm：境界提升，targetId 为空串；
 * - root：六维提升，targetId 为六维键（stamina/meridians/attack/bone/agility/consciousness）；
 * - ability：功法提升，targetId 为功法 ID；
 * - technique：技艺提升，targetId 为技艺 ID。
 */
export interface BattleReportLevelUp {
  kind: 'realm' | 'root' | 'ability' | 'technique'
  targetId: string
  fromLevel: number
  toLevel: number
}

/** 战报技艺经验条目（按 techniqueId 聚合）。 */
export interface BattleReportTechniqueExp {
  techniqueId: TechniqueId | string
  experience: number
}

/**
 * 行动内战斗单位类别（wire 数值枚举）：本机玩家 / 参与会话盟友 / 怪物类型桶。
 * 斗法对方玩家队伍不入账；同类型怪物合并为一个 monster 单位。
 */
export type BattleReportCombatantKind = 'self' | 'ally' | 'monster'

/**
 * 行动内单个统计单位的战斗累计行：伤害输出/实收/减免按物理·术法分列，
 * 治疗分自动回复（healingRegen）与其他治疗输出（healingOther）。
 * 减免 = 命中时原始威力 − 实际落账伤害。
 */
export interface BattleReportCombatant {
  /** 单位键：player / ally:{成员下标} / monster:{怪物定义 ID} */
  unitKey: string
  /** 单位类别 */
  kind: BattleReportCombatantKind
  /** 展示名（道号/成员名/怪物名） */
  displayName: string
  /** 造成物理伤害 */
  damageDealtPhysical: number
  /** 造成术法伤害 */
  damageDealtSpell: number
  /** 实收物理伤害 */
  damageTakenPhysical: number
  /** 实收术法伤害 */
  damageTakenSpell: number
  /** 减免物理伤害 */
  damageMitigatedPhysical: number
  /** 减免术法伤害 */
  damageMitigatedSpell: number
  /** 自动回复量（战内 5 秒 tick 与行动期间战外自然回复） */
  healingRegen: number
  /** 其他治疗输出量（技能治疗、气血汲取、药品瞬回） */
  healingOther: number
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
  /** 修为入账量（当前层修为计量条瓶颈截断后的真实增量） */
  experience: number
  /** 底蕴净变动（修为满溢沉淀为正、修炼消耗兑换为负） */
  foundation: number
  /** 战斗经验入账量（按修为基础量等额入账，不受增幅影响） */
  combatExperience: number
  /** 六维经验入账量 */
  rootExp: BattleReportRootExp
  /** 功法技能经验入账列表 */
  abilityExp: BattleReportAbilityExp[]
  /** 生活技艺经验入账列表 */
  techniqueExp: BattleReportTechniqueExp[]
  /** 期间发生的等级提升（境界/六维/功法/技艺） */
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
  /** 行动内战斗单位统计（仅进行中行动账本下发；归档账本为空列表） */
  combatants: BattleReportCombatant[]
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
  /** 本次攻击是否命中（false 为闪避） */
  hit?: boolean
  /** 结算伤害 */
  damage?: number
  /** 是否暴击 */
  critical?: boolean
  /** 是否破招 */
  broken?: boolean
  /** 是否拆招 */
  parried?: boolean
}

/**
 * 远征斗法回放显示信息（expedition.duel 载荷）
 */
export interface ExpeditionDuel {
  /** 对手队伍道号 */
  opponentName: string
  opponentAvatar?: string
  /** 本方是否为斗法发起方 */
  isInitiator: boolean
  /** 斗法结果枚举：0 未决 / 1 胜 / 2 负 / 3 平 */
  outcome: number
  /** 是否已结算 */
  resolved: boolean
  /** 对手气血上限（镜像血蓝条分母，常驻标量） */
  opponentMaxHitpoints?: number | null
  /** 对手灵力上限 */
  opponentMaxManapoints?: number | null
  /** 对手镜像完整战斗属性（撮合时冻结；按需投影——仅观察目标命中镜像位时随快照携带） */
  opponentAttributes?: CombatAttributes | null
  /** 对手镜像功法槽位（撮合时冻结；与 opponentAttributes 同帧一次性交付） */
  opponentSkills?: CombatAbilitySlot[] | null
  /** 对手镜像已穿戴装备（撮合时冻结；与 opponentAttributes 同帧一次性交付） */
  opponentEquipment?: EquippedItem[] | null
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
  /** 暴击伤害点数（Linear：暴击倍率 = 1+点数/100，基础 50 点即 150%） */
  criticalDamagePoints: number
  /** 破招点数（与化解对抗） */
  breakPower: number
  /** 化解点数（与破招对抗） */
  parryResolve: number
  hpRegenPerFiveSeconds: number
  mpRegenPerFiveSeconds: number
  attackIntervalMilliseconds: number
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
  /** 固定伤害提升（不吃抗性与增幅） */
  fixedDamageBonus: number
  /** 固定伤害减免（最低截断为 0） */
  fixedDamageReduction: number
  /** 固定物理伤害提升 */
  fixedPhysicalDamageBonus: number
  /** 固定物理伤害减免 */
  fixedPhysicalDamageReduction: number
  /** 固定法术伤害提升 */
  fixedSpellDamageBonus: number
  /** 固定法术伤害减免 */
  fixedSpellDamageReduction: number
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
  /** 子类型键（左上角品类丝带展示口径） */
  subCategory: string
  /** 拥有数量 */
  amount: number
  /** 色调主题 */
  tone: 'green' | 'violet' | 'gold' | 'cyan' | string
  /** 图标字符/符号 */
  glyph: string
  /** 等阶 */
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
  /** 主类型键（材料/消耗品/功法/货币等大分类） */
  category: ItemCategoryKey | string
  /** 子类型键（药材/矿石/丹药/秘籍/残卷等细分，左上角丝带展示） */
  subCategory?: string | null
  /** 等阶（1~8，映射品质与等级） */
  tier: number
  /** 系统出售价（灵石） */
  sellPrice: number
  /** 是否允许市场挂单交易 */
  isTradable: boolean
  /** 可挂载自动使用槽位的物品栏域集合（自动消耗品准入声明） */
  usableBars: AutoUseBarType[]
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
  /** 耐久型持续消耗品（true 时槽位持有即生效，不消耗不耗竭；如聚灵阵盘） */
  durable?: boolean
  /** 即时打击规格（符箓类消耗品：祭出即耗灵力造成元素术法伤害，无吟唱/命中/暴击掷点） */
  strike?: ConsumableStrikeDefinition | null
}

/**
 * 消耗品即时打击定义（雷击符等：按灵力消耗换算元素术法伤害直取敌首）
 */
export interface ConsumableStrikeDefinition {
  /** 祭出消耗灵力占比（×最大灵力值） */
  manaCostRatio: number
  /** 基础伤害占比（×最大灵力值） */
  manaDamageRatio: number
  /** 术法攻击加成占比（×术法攻击） */
  spellAttackRatio: number
  /** 五行元素类别（metal/wood/water/fire/earth） */
  element: string
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
  /** 气血回复后段比率（生效值 = 基础回复 ×(1+Σ比率)，与 mpRegenPostRatio 同为回复乘区末段） */
  hpRegenPostRatio?: number
  /** 灵力回复后段比率 */
  mpRegenPostRatio?: number
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
  /** 气血回复后段比率（可选，缺省 0；生效值 = 基础回复 ×(1+Σ比率)） */
  hpRegenPostRatio?: number
  /** 灵力回复后段比率（可选，缺省 0） */
  mpRegenPostRatio?: number
  /** 通用属性比率字典（可选） */
  statRatios?: Record<string, number> | null
  /** 威胁增幅点数（仅战内 Buff 投影携带） */
  threatPoints?: number
  /** 伤害增幅点数（仅战内 Buff 投影携带） */
  damageBonusPoints?: number
  /** 每秒威胁成长点数（仅战内 Buff 投影携带） */
  threatPerSecondMaxHpPoints?: number
}

/**
 * 角色历史累计统计数据
 */
export interface Stats {
  /** 服务端权威境界等级（1~127），缺省时由 experience 当前层修为反推 */
  realmLevel?: number
  /** 当前层修为计量条（progression.experience 透传，突破时由服务端按规则清零） */
  experience: number
  /** 累计修为总量（progression.lifetimeExperience 透传：历史修为类收益单调累计，突破不归零、满层沉淀亦计入） */
  lifetimeExperience: number
  /** 底蕴池（progression.foundation 透传：修为圆满后沉淀，修炼时消耗兑换额外修为，突破不清空） */
  foundation: number
  /** 战斗经验池（progression.combatExperience 透传：按修为基础量同步积累，不受增幅影响） */
  combatExperience: number
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
  /** 已击败妖兽 ID 集合（万象图鉴妖兽志解锁） */
  defeatedMonsterIds?: string[]
}

/**
 * 战斗技能槽位配置
 */
export interface CombatAbilitySlot {
  /** 装配的功法 ID（null 为空槽） */
  abilityId: string | null
  /** 功法演练等级 */
  level: number
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
  /** 等阶（配表声明，缺省视为 1） */
  tier?: number
  /** 根骨加成属性键（配表声明） */
  rootAttribute?: string
  /** 支援类：施放后施加的战内 Buff（破阵子等）；修饰字段为点数口径 */
  buff?: { name?: string; durationSeconds?: number; damageAbsorptionPoints?: number; hpRegen?: number; mpRegen?: number; attackSpeedPoints?: number; threatPoints?: number; damageBonusPoints?: number; threatPerSecondMaxHpPoints?: number } | null
  /** 击杀连锁次数：单体技能击杀目标后对另一存活敌方追加一次完整结算（贯星等），缺省 0 */
  chainOnKillCount?: number
  /** 多段打击次数：对同一目标逐段独立结算完整技能伤害（连环等），缺省 1 */
  hitCount?: number
  /** 六维经验分配乘率（仅内功心法有效） */
  rootExpMultipliers?: Partial<Record<string, number>>
  /** 参悟等级每级提升六维经验总效率（缺省 0.02） */
  rootExpEfficiencyPerLevel?: number
  /** 修为类收益增减点数（百分比点数，如 +20 或 -10） */
  cultivationExpBonusPoints?: number
  /** 常驻被动面板效果（复用装备 flatStats 同族点数字段） */
  passive?: Record<string, number>
  /** 参悟等级每级提升常驻被动面板效率（缺省 0.05） */
  passiveEfficiencyPerLevel?: number
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
  /** 击杀入账经验：同额喂修为（经瓶颈/底蕴结算）、六维均分与战斗经验 */
  experience: number
  /** 境界等级（怪物同构建模的等级锚点） */
  level?: number
  /** 等阶（由等级映射的 T 级） */
  tier?: number
  /** 占用刷怪强度预算的点数 */
  budgetCost?: number
  /** 掉落条目列表（先按 dropChance 判定，再在数量区间均匀抽取） */
  drops?: MonsterDropDefinition[]
  /** 该单位可使用的功法槽位（abilityId + 配表施放等级；怪物无装备概念，详情「装配」Tab 只展示功法） */
  skills: CombatAbilitySlot[]
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
  /** 精英波次敌人列表 */
  eliteEnemies?: CombatantDefinition[]
  /** 每多少波普通战斗后进入一次精英波 */
  battlesPerElite?: number
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
  /** 装备描述文案 */
  description?: string
  /** 穿戴槽位 */
  slot: EquipmentSlot
  /** 等阶 */
  tier: number
  /** 每级强化附加的属性增益（配表原始字典） */
  enhancementBonus?: Record<string, number>
  /** 配表原始比例段（服务端透传，供详情面板展示） */
  baseStatRatios?: Record<string, number>
  /** 配表原始固定值段（*Points 键为百分比点数，与固定值同受强化缩放） */
  flatStats?: Record<string, number>
  /** 强化保护材料白名单：配置后可用列表内任一材料代护；缺省时以同装备无强化基础件代护 */
  enhanceProtectionItems?: string[]
  /**
   * 技艺工具加成块（仅 tool-* 槽位工具携带；普通装备为 null/缺省）：
   * technique 命中行动所属技艺时，speedRatio 与技艺等级加成并入同一除数
   * （duration /= 1 + 0.05×技艺等级 + Σ工具speedRatio）。
   */
  techniqueBonus?: { technique: string; speedRatio: number } | null
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
  /** 技艺经验表：TechniqueId -> 累计经验（等级由累计经验按修为曲线导出，默认 0 级） */
  techniqueExperiences?: Record<string, number>
  /** 功法自动释放触发条件：abilityId -> 条件列表（跟随功法而非槽位，卸下再装不丢） */
  abilityTriggers?: Record<string, CombatTrigger[]>
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
  /** 等阶 */
  tier: number
  /** 生命层次（normal/elite/boss 等） */
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
  explorationSpeedRatio?: number
  /** 历练采集行动收益比率 */
  explorationOutputRatio?: number
  /** 炼丹行动加速比率 */
  alchemySpeedRatio?: number
  /** 炼丹行动收益比率 */
  alchemyOutputRatio?: number
  /** 吐纳修炼收益比率 */
  cultivationOutputRatio?: number
  /** 战斗气血自然回复（每 5 秒固定值） */
  combatHitpointRegen?: number
  /** 战斗灵力自然回复（每 5 秒固定值） */
  combatManapointRegen?: number
  /** 战斗额外减伤比率 */
  combatDamageReductionRatio?: number
  /** 怪物削弱比率：双曲阻尼折减此地怪物气血上限与双攻双防（折减系数 = 1 / (1 + ratio)，无上限） */
  monsterWeakenRatio?: number
  /** 采集采伐行动加速比率 */
  gatheringSpeedRatio?: number
  /** 采矿挖掘行动加速比率 */
  miningSpeedRatio?: number
  /** 炼器精炼行动加速比率 */
  forgingSpeedRatio?: number
  /** 符箓绘制行动加速比率 */
  talismanSpeedRatio?: number
  /** 阵盘研制行动加速比率 */
  formationSpeedRatio?: number
  /** 装备强化行动加速比率 */
  enhancementSpeedRatio?: number
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
  /** 1 级初始基准效果 */
  baseModifiers?: BuildingModifiers
  /** 每升 1 级的线性增幅效果 */
  growthModifiers?: BuildingModifiers
  /** 基准效果（兼容旧字段） */
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
  /** 正在使用人数（服务行动类别下 Running 行动的玩家数） */
  activeUsers: number
  /** 当前等级拥挤承载上限 */
  capacity: number
  /** 拥挤衰减后效率系数（1 = 满效，超载时 < 1，下限 0.5） */
  efficiency: number
}

/**
 * 全图节点公共建筑索引条目（`GET /api/v1/world/node-buildings` 下发的摘要形态；
 * 名称/描述/效果文案不在载荷内，由建筑目录 `state.buildings` 按 buildingId 解析）
 */
export interface MapNodeBuilding {
  /** 建筑配置 ID */
  buildingId: string
  /** 已建成有效等级（0 = 尚未建成） */
  level: number
  /** 进行中工程目标等级（0 = 空闲） */
  targetLevel: number
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
  /** 是否为隐藏节点（未达到检定条件时不显示） */
  hidden?: boolean
  /** 检定条件（如神识点数等） */
  requirements?: {
    consciousness?: number
    [key: string]: unknown
  } | null
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
 * 当前节点公共建筑生效效果汇总（服务端 EffectiveNodeBuildingEffects 权威投影）：
 * 产出类为收益增幅比率，速度类为耗时加速比率（实际耗时 = 基准 / (1 + ratio)）。
 */
export interface NodeBuildingEffectSet {
  /** 历练采集耗时加速比率 */
  explorationSpeedRatio: number
  /** 历练采集产出增幅比率 */
  explorationOutputRatio: number
  /** 炼丹/炼器耗时加速比率 */
  alchemySpeedRatio: number
  /** 炼丹/炼器产出增幅比率 */
  alchemyOutputRatio: number
  /** 吐纳修炼产出增幅比率 */
  cultivationOutputRatio: number
  /** 采集采伐耗时加速比率 */
  gatheringSpeedRatio?: number
  /** 采矿挖掘耗时加速比率 */
  miningSpeedRatio?: number
  /** 炼器精炼耗时加速比率 */
  forgingSpeedRatio?: number
  /** 符箓绘制耗时加速比率 */
  talismanSpeedRatio?: number
  /** 阵盘研制耗时加速比率 */
  formationSpeedRatio?: number
  /** 装备强化耗时加速比率 */
  enhancementSpeedRatio?: number
  /** 战斗气血回复 */
  combatHitpointRegen?: number
  /** 战斗灵力回复 */
  combatManapointRegen?: number
  /** 战斗伤害减免 */
  combatDamageReductionRatio?: number
  /** 怪物削弱比率 */
  monsterWeakenRatio?: number
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
  /** 当前节点公共建筑生效效果汇总（缺失/他节点投影视为全零） */
  buildingEffects?: NodeBuildingEffectSet
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
  /** 新手引导里程碑下标（settings 分段权威；0~6 进行中，7 完成/跳过终态） */
  tutorialStep: number
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
  queueEntriesUpdated?: boolean
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
  tutorialStep?: number
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
 * 市场资产变体行情摘要（强化等级盘口一览）
 *
 * 对应 GET /api/v1/market/variants 单条记录：服务端只下发存在活动挂单
 * （买或卖任一侧）的变体簿；variant="+N"/null 已归一为强化等级数字。
 */
export interface MarketVariantSummary {
  /** 强化等级（0 = 基础无强化变体簿） */
  enhancementLevel: number
  /** 当前最低卖价（卖一）；该侧无挂单时为 null */
  bestAsk: number | null
  /** 当前最高买价（买一）；该侧无挂单时为 null */
  bestBid: number | null
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

/**
 * 坊市单品种行情统计（物品悬浮「市场价格」副卡数据源）
 *
 * 对应 GET /api/v1/market/stats 单条记录：服务端 `market.item_stats` 表
 * 随挂单/撤单/到期/成交事务同事务维护；wire variant="+N"/null 已归一为强化等级数字。
 */
export interface MarketItemStats {
  /** 物品配置 ID */
  itemId: string
  /** 强化等级（0 = 基础无强化变体簿） */
  enhancementLevel: number
  /** 当前最低挂售价；无活动卖单为 null */
  bestAskPrice: number | null
  /** 当前活动卖单剩余总量 */
  askQuantity: number
  /** 当前最高收购价；无活动买单为 null */
  bestBidPrice: number | null
  /** 当前活动买单剩余总量 */
  bidQuantity: number
  /** 当日（UTC 自然日）成交数量 */
  dayVolume: number
  /** 当周（UTC 周一为界）成交数量 */
  weekVolume: number
}
