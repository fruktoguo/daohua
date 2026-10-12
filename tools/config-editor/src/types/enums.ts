// ==============================================================================
// 文件名称: enums.ts
// 主要职责: 游戏数值与规则全量枚举定义、中文语义选项与现代扁平化彩色 Badge
// ==============================================================================

/** 枚举选项统一结构：value 为写回配表的存储值，label 为中文展示文案，en 为徽章英文短标签，badgeColor 为 Tailwind 徽章配色类。 */
export interface EnumOption<T extends string | number = string> {
  value: T;
  label: string;
  en: string;
  badgeColor?: string;
}

// 1. 战斗流派 (CombatStyle)
/** 战斗流派展示标签词表：仅作前端徽章/过滤展示，引擎不校验该字段取值。 */
export const COMBAT_STYLES: EnumOption[] = [
  { value: 'body', label: '炼体 (体修)', en: 'Body', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'sword', label: '剑修 (纯阳剑罡)', en: 'Sword', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'blade', label: '刀修 (霸刀狂刃)', en: 'Blade', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 'fiveElements', label: '五行 (灵道玄法)', en: 'FiveElements', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'talisman', label: '符修 (万符归宗)', en: 'Talisman', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'zither', label: '琴修 (九霄天音)', en: 'Zither', badgeColor: 'bg-pink-500/10 text-pink-300 border-pink-500/20' }
];

// 2. 伤害大类 (DamageCategory) —— ValidateAbility 只接受 physical / spell
/** 伤害大类选项：与引擎 ValidateAbility 白名单一致，写回非法值会导致配表校验失败。 */
export const DAMAGE_CATEGORIES: EnumOption[] = [
  { value: 'physical', label: '物理伤害 (Physical)', en: 'Physical', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 'spell', label: '术法伤害 (Spell)', en: 'Spell', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' }
];

// 3. 五行属性 (Element)
/** 五行元素词表：none 表示无元素，其余与引擎 CombatElement 词汇一致。 */
export const ELEMENTS: EnumOption[] = [
  { value: 'none', label: '无属性 (None)', en: 'None', badgeColor: 'bg-zinc-500/10 text-zinc-400 border-zinc-500/20' },
  { value: 'metal', label: '金系 (Metal)', en: 'Metal', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' },
  { value: 'wood', label: '木系 (Wood)', en: 'Wood', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'water', label: '水系 (Water)', en: 'Water', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'fire', label: '火系 (Fire)', en: 'Fire', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'earth', label: '土系 (Earth)', en: 'Earth', badgeColor: 'bg-amber-600/10 text-amber-300 border-amber-600/20' }
];

// 4. 道具大类 (ItemKind)
/** 道具大类词表：决定物品在坊市/背包中的归类与可用编辑面板。 */
export const ITEM_KINDS: EnumOption[] = [
  { value: 'material', label: '天材地宝 / 原料 (Material)', en: 'Material', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'consumable', label: '灵丹妙药 / 消耗品 (Consumable)', en: 'Consumable', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'equipment', label: '法宝神兵 / 装备 (Equipment)', en: 'Equipment', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'manual', label: '功法秘籍 / 典籍 (Manual)', en: 'Manual', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'currency', label: '灵石硬通货 (Currency)', en: 'Currency', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' }
];

// 5. 道具子类 (ItemCategory)
/** 道具细分子类词表：kind 之下的展示分类（灵草/矿石/丹药/武器等），引擎不强制校验。 */
export const ITEM_CATEGORIES: EnumOption[] = [
  { value: 'herb', label: '灵植灵草 (Herb)', en: 'Herb', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'ore', label: '灵矿晶石 (Ore)', en: 'Ore', badgeColor: 'bg-slate-500/10 text-slate-300 border-slate-500/20' },
  { value: 'monsterDrop', label: '妖兽材料 (MonsterDrop)', en: 'MonsterDrop', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'pill', label: '丹药 (Pill)', en: 'Pill', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'weapon', label: '武器 (Weapon)', en: 'Weapon', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 'armor', label: '防具 (Armor)', en: 'Armor', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'accessory', label: '饰品 (Accessory)', en: 'Accessory', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'quest', label: '任务特殊道具 (Quest)', en: 'Quest', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' },
  { value: 'other', label: '杂项 (Other)', en: 'Other', badgeColor: 'bg-zinc-500/10 text-zinc-400 border-zinc-500/20' }
];

// 6. 装备槽位 (EquipmentSlot)
/** 装备槽位词表：LoadoutCommand 强制读取 slot 字段，非法槽位会导致装配失败。 */
export const EQUIPMENT_SLOTS: EnumOption[] = [
  { value: 'weapon', label: '本命神兵 (Weapon)', en: 'Weapon', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 'two-hand', label: '双手重器 (Two-Hand)', en: 'Two-Hand', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 'off-hand', label: '副手法器 (Off-Hand)', en: 'Off-Hand', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 'head', label: '道冠护额 (Head)', en: 'Head', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'armor', label: '护心道袍 (Armor)', en: 'Armor', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'legs', label: '玄法道履 (Legs)', en: 'Legs', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'ring', label: '储灵戒 (Ring)', en: 'Ring', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'neck', label: '太虚佩 (Neck)', en: 'Neck', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'trinket', label: '法宝饰品 (Trinket)', en: 'Trinket', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'talisman', label: '符箓灵宝 (Talisman)', en: 'Talisman', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'tool-gathering', label: '采药法器 (Tool·Gathering)', en: 'Tool·Gathering', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'tool-mining', label: '探矿法器 (Tool·Mining)', en: 'Tool·Mining', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'tool-alchemy', label: '炼丹法器 (Tool·Alchemy)', en: 'Tool·Alchemy', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'tool-forging', label: '炼器法器 (Tool·Forging)', en: 'Tool·Forging', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'tool-talisman', label: '符箓法器 (Tool·Talisman)', en: 'Tool·Talisman', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'tool-formation', label: '阵法法器 (Tool·Formation)', en: 'Tool·Formation', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'tool-enhancement', label: '强化法器 (Tool·Enhancement)', en: 'Tool·Enhancement', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' }
];

// 7. 怪物生命层次 (CombatMonsterRank) —— ParseMonsterDefinitions: elite/boss 之外一律 minion
/** 怪物生命层次选项：引擎 ParseMonsterDefinitions 中 elite/boss 之外一律按 minion 处理。 */
export const UNIT_RANKS: EnumOption[] = [
  { value: 'minion', label: '寻常小妖 (Minion)', en: 'Minion', badgeColor: 'bg-zinc-500/10 text-zinc-300 border-zinc-500/20' },
  { value: 'elite', label: '天地精英 (Elite)', en: 'Elite', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 'boss', label: '秘境首领 (Boss)', en: 'Boss', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' }
];

// 8. 行动类型 (ActionKind) —— ParseKind 词汇表
/** 行动类型词表：与引擎 ParseKind 同口径，用于行动 type 字段的合法性校验。 */
export const ACTION_TYPES: EnumOption[] = [
  { value: 'cultivation', label: '打坐吐纳 / 修炼 (Cultivation)', en: 'Cultivation', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'exploration', label: '灵山采撷 / 探索 (Exploration)', en: 'Exploration', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'alchemy', label: '开炉炼丹 / 炼制 (Alchemy)', en: 'Alchemy', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'combat', label: '除魔历练 / 战斗 (Combat)', en: 'Combat', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'travel', label: '御剑腾云 / 赶路 (Travel)', en: 'Travel', badgeColor: 'bg-teal-500/10 text-teal-300 border-teal-500/20' },
  { value: 'enhancement', label: '铭纹强化 / 强化 (Enhancement)', en: 'Enhancement', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' }
];

// 8b. 自动使用物品栏域 (AutoUseBar) —— 消耗品 usableInActionTypes 词表（总纲 §6.2.3）
/** 物品栏域词表：消耗品 usableInActionTypes 声明可装配的物品栏域；历练栏涵盖修炼/历练两大类行动。 */
export const AUTO_USE_BARS: EnumOption[] = [
  { value: 'exploration', label: '历练物品栏 (Exploration)', en: 'Exploration', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'alchemy', label: '炼丹物品栏 (Alchemy)', en: 'Alchemy', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'forging', label: '炼器物品栏 (Forging)', en: 'Forging', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 'talisman', label: '符箓物品栏 (Talisman)', en: 'Talisman', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'formation', label: '阵法物品栏 (Formation)', en: 'Formation', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 'enhancement', label: '强化物品栏 (Enhancement)', en: 'Enhancement', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' },
  { value: 'combat', label: '战斗物品栏 (Combat)', en: 'Combat', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' }
];

// 9. 行动执行模式 (ActionExecutionKind) —— ParseExecutionKind 词汇表；缺省按行动类型推导
/** 行动执行模式词表：与引擎 ParseExecutionKind 同口径；缺省时按行动类型推导（combat→combatEncounter、travel→travel、其余→timed）。 */
export const ACTION_EXECUTION_KINDS: EnumOption[] = [
  { value: 'timed', label: '周期性结算 (Timed)', en: 'Timed', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'combatEncounter', label: '遭遇战结算 (CombatEncounter)', en: 'CombatEncounter', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'travel', label: '赶路移动 (Travel)', en: 'Travel', badgeColor: 'bg-teal-500/10 text-teal-300 border-teal-500/20' }
];

// 10. 技能效果类型 —— ValidateAbility/CastSkill 只判定 heal 与 buff，缺省为伤害
/** 技能效果类型选项：引擎 ValidateAbility/CastSkill 只判定 heal 与 buff，其余（含缺省）一律按伤害结算。 */
export const COMBAT_ABILITY_EFFECT_TYPES: EnumOption[] = [
  { value: 'damage', label: '神通杀伐 (缺省 Damage)', en: 'Damage', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'heal', label: '回春回血 (Heal)', en: 'Heal', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'buff', label: '玄光护体 / 增益 (Buff)', en: 'Buff', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' }
];

// 11. 技能目标类型 —— ValidateAbility 合法集
/** 技能目标类型选项：与引擎 ValidateAbility 合法集一致，缺省按单体敌方结算。 */
export const COMBAT_ABILITY_TARGET_TYPES: EnumOption[] = [
  { value: 'targetEnemy', label: '单体敌方 (TargetEnemy)', en: 'TargetEnemy', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'allEnemies', label: '全体敌方 (AllEnemies)', en: 'AllEnemies', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'self', label: '自身 (Self)', en: 'Self', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'allAllies', label: '全体友方 (AllAllies)', en: 'AllAllies', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 'lowestHitpointsAlly', label: '血量最低友方 (LowestHitpointsAlly)', en: 'LowestHitpointsAlly', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' }
];

// 12. 视觉渲染器类型 (VisualRendererKind)
/** 视觉渲染器类型词表：决定怪物立绘走字符画/矢量/位图哪条渲染通道。 */
export const VISUAL_RENDERER_KINDS: EnumOption[] = [
  { value: 'text', label: '道貌字符画 (Text)', en: 'Text' },
  { value: 'svg', label: '折面矢量图 (Svg)', en: 'Svg' },
  { value: 'image', label: '位图立绘 (Image)', en: 'Image' }
];

// 13. 技能触发条件五元组词汇表 —— CombatSkillEngine.ValidateTrigger / DuelTriggerEvaluator
/** 触发条件依赖对象词表：单体/集合/极值目标全量枚举，与引擎 ValidateTrigger/DuelTriggerEvaluator 消费集一致。 */
export const TRIGGER_DEPENDENCIES: EnumOption[] = [
  { value: 'self', label: '自身 (Self)', en: 'Self' },
  { value: 'target-enemy', label: '当前敌方目标 (TargetEnemy)', en: 'TargetEnemy' },
  { value: 'ally', label: '友方集合 (Ally)', en: 'Ally' },
  { value: 'enemy', label: '敌方集合 (Enemy)', en: 'Enemy' },
  { value: 'ally-lowest-hp', label: '血量最低友方 (AllyLowestHp)', en: 'AllyLowestHp' },
  { value: 'ally-highest-hp', label: '血量最高友方 (AllyHighestHp)', en: 'AllyHighestHp' },
  { value: 'enemy-lowest-hp', label: '血量最低敌方 (EnemyLowestHp)', en: 'EnemyLowestHp' },
  { value: 'enemy-highest-hp', label: '血量最高敌方 (EnemyHighestHp)', en: 'EnemyHighestHp' }
];

/** 触发条件字段词表：数值条件（气血/灵力及其百分比、buff 层数、存活/阵亡数）与 Boolean 状态条件（眩晕/致盲/沉默）全量枚举。 */
export const TRIGGER_CONDITIONS: EnumOption[] = [
  { value: 'current-hp', label: '当前气血 (CurrentHp)', en: 'CurrentHp' },
  { value: 'missing-hp', label: '已损气血 (MissingHp)', en: 'MissingHp' },
  { value: 'current-hp-percentage', label: '当前气血百分比 (CurrentHp%)', en: 'CurrentHp%' },
  { value: 'missing-hp-percentage', label: '已损气血百分比 (MissingHp%)', en: 'MissingHp%' },
  { value: 'current-mana', label: '当前灵力 (CurrentMana)', en: 'CurrentMana' },
  { value: 'missing-mana', label: '已损灵力 (MissingMana)', en: 'MissingMana' },
  { value: 'current-mana-percentage', label: '当前灵力百分比 (CurrentMana%)', en: 'CurrentMana%' },
  { value: 'missing-mana-percentage', label: '已损灵力百分比 (MissingMana%)', en: 'MissingMana%' },
  { value: 'buff-stacks', label: 'Buff 层数 (BuffStacks)', en: 'BuffStacks' },
  { value: 'stun-status', label: '眩晕状态 (Stun)', en: 'Stun' },
  { value: 'blind-status', label: '致盲状态 (Blind)', en: 'Blind' },
  { value: 'silence-status', label: '沉默状态 (Silence)', en: 'Silence' },
  { value: 'alive-count', label: '剩余存活数量 (AliveCount)', en: 'AliveCount' },
  { value: 'dead-count', label: '已阵亡数量 (DeadCount)', en: 'DeadCount' }
];

/** 触发比较方式词表：数值条件用大小/等于比对，Boolean 状态条件仅可用 is-active/is-inactive。 */
export const TRIGGER_COMPARATORS: EnumOption[] = [
  { value: 'greater-than-equal', label: '大于等于 (>=)', en: '>=' },
  { value: 'less-than-equal', label: '小于等于 (<=)', en: '<=' },
  { value: 'equal', label: '等于 (=)', en: '=' },
  { value: 'greater-than', label: '大于 (>)', en: '>' },
  { value: 'less-than', label: '小于 (<)', en: '<' },
  { value: 'is-active', label: '是 (IsActive)', en: 'IsActive' },
  { value: 'is-inactive', label: '否 (IsInactive)', en: 'IsInactive' }
];

/** 集合对象依赖：仅可用群体聚合条件（alive-count/dead-count）。 */
export const TRIGGER_SET_DEPENDENCIES = new Set(['ally', 'enemy']);
/** 集合对象可选条件词表。 */
export const TRIGGER_SET_CONDITIONS = new Set(['alive-count', 'dead-count']);
/** Boolean 条件词表：仅可配 is-active/is-inactive 比对。 */
export const TRIGGER_BOOL_CONDITIONS = new Set(['stun-status', 'blind-status', 'silence-status']);

// 13b. 功法类别 (discipline)：internal=内功心法（0 号槽位），spell=神通术法（缺省）
/** 功法类别词表：internal 内功心法占 0 号内功槽位，spell 神通术法为缺省类别。 */
export const ABILITY_DISCIPLINES: EnumOption[] = [
  { value: 'internal', label: '内功心法 (Internal)', en: 'Internal', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'spell', label: '神通术法 (Spell)', en: 'Spell', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' }
];

// 13c. 六维根骨 (CoreAspectKind) —— rootAttribute 经验分配维度
/** 六维根骨词表：技能 rootAttribute 决定修炼经验分摊到哪个维度，缺省全修（六维均分）。 */
export const ROOT_ATTRIBUTES: EnumOption[] = [
  { value: 'stamina', label: '体魄 (Stamina)', en: 'Stamina', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'meridians', label: '经脉 (Meridians)', en: 'Meridians', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 'attack', label: '力道 (Attack)', en: 'Attack', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 'bone', label: '根骨 (Bone)', en: 'Bone', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'agility', label: '身法 (Agility)', en: 'Agility', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'consciousness', label: '神识 (Consciousness)', en: 'Consciousness', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' }
];

// 13d. 怪物倾向权重键 —— ParseMonsterDefinitions Weight(...) 实际消费键
/** 怪物倾向权重键清单：weights 字典的合法键及中文标签，引擎按这些键加权派生战斗属性。 */
export const MONSTER_WEIGHT_KEYS: { key: string; label: string }[] = [
  { key: 'hp', label: '气血 (Hp)' },
  { key: 'mp', label: '灵力 (Mp)' },
  { key: 'ad', label: '物理攻击 (Ad)' },
  { key: 'ap', label: '术法攻击 (Ap)' },
  { key: 'armor', label: '护甲 (Armor)' },
  { key: 'mres', label: '法抗 (Mres)' },
  { key: 'hit', label: '命中 (Hit)' },
  { key: 'eva', label: '闪避 (Eva)' },
  { key: 'crit', label: '暴击 (Crit)' },
  { key: 'cavoid', label: '免暴 (Cavoid)' },
  { key: 'break', label: '破招 (Break)' },
  { key: 'parry', label: '招架 (Parry)' }
];

// 13d-2. 怪物百分比点数修饰键 —— ParseMonsterDefinitions modifiers 段消费键（战斗设计 §7.5 词汇表）
/** 怪物百分比点数修饰键清单：modifiers 字典合法键（DataTool 词汇表强制校验），100 点 = 100% 效果。 */
export const MONSTER_MODIFIER_KEYS: { key: string; label: string }[] = [
  { key: 'physicalDamageBonusPoints', label: '物理增伤点' },
  { key: 'spellDamageBonusPoints', label: '法术增伤点' },
  { key: 'metalDamageBonusPoints', label: '金系增伤点' },
  { key: 'woodDamageBonusPoints', label: '木系增伤点' },
  { key: 'waterDamageBonusPoints', label: '水系增伤点' },
  { key: 'fireDamageBonusPoints', label: '火系增伤点' },
  { key: 'earthDamageBonusPoints', label: '土系增伤点' },
  { key: 'totalDamageBonusPoints', label: '总增伤点' },
  { key: 'basicAttackBonusPoints', label: '普攻增幅点' },
  { key: 'skillBonusPoints', label: '技能增幅点' },
  { key: 'physicalDamageReductionPoints', label: '物理减伤点' },
  { key: 'spellDamageReductionPoints', label: '法术减伤点' },
  { key: 'metalDamageReductionPoints', label: '金系减伤点' },
  { key: 'woodDamageReductionPoints', label: '木系减伤点' },
  { key: 'waterDamageReductionPoints', label: '水系减伤点' },
  { key: 'fireDamageReductionPoints', label: '火系减伤点' },
  { key: 'earthDamageReductionPoints', label: '土系减伤点' },
  { key: 'damageAbsorptionPoints', label: '总减伤/免伤点' },
  { key: 'attackSpeedPoints', label: '攻击速度点' },
  { key: 'actionSpeedPoints', label: '行动速度点' },
  { key: 'cooldownReductionPoints', label: '冷却缩减点' },
  { key: 'threatBonusPoints', label: '威胁增幅点' },
  { key: 'criticalDamageBonusPoints', label: '暴击伤害点' }
];

// 13e. 行动收益类型与结算频率 —— ParseYields 词汇表
/** 行动收益类型词表：与引擎 ParseYields 消费集一致，决定单条收益结算的资源种类。 */
export const ACTION_YIELD_KINDS: EnumOption[] = [
  { value: 'qi', label: '修为灵气 (Qi)', en: 'Qi', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'mana', label: '灵力 (Mana)', en: 'Mana', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'hitpoints', label: '气血 (Hitpoints)', en: 'Hitpoints', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'experience', label: '经验 (Experience)', en: 'Experience', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'item', label: '道具物品 (Item)', en: 'Item', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'buff', label: '增益状态 (Buff)', en: 'Buff', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' }
];

/** 行动收益结算频率词表：second 按秒平滑发放，repeat 按行动每轮一次性发放。 */
export const ACTION_YIELD_PERS: EnumOption[] = [
  { value: 'second', label: '每秒结算 (PerSecond)', en: 'PerSecond' },
  { value: 'repeat', label: '每轮结算 (PerRepeat)', en: 'PerRepeat' }
];

// 13f. 节点可建模板 —— NodeBuildings.ResolveBuildableIds / DataTool 校验词汇表
/** 节点可建模板词表：buildableTemplate 的合法值，与 buildableList 自定义列表二选一。 */
export const BUILDABLE_TEMPLATES: EnumOption[] = [
  { value: 'all', label: '全部建筑 (All)', en: 'All' },
  { value: 'production', label: '生产建筑 (Production)', en: 'Production' },
  { value: 'combat', label: '战斗建筑 (Combat)', en: 'Combat' },
  { value: 'other', label: '其他建筑 (Other)', en: 'Other' }
];

// 13g. 团灭规则 (PartyWipeRule) —— CombatEncounterDefinition.OnPartyWipe / GameDataCatalog.ParseEncounterDefinitions
/** 团灭规则词表：respawn 原地复活继续挂机，defeat 团灭即终局失败；缺省 respawn。 */
export const PARTY_WIPE_RULES: EnumOption[] = [
  { value: 'respawn', label: '原地复活 (Respawn)', en: 'Respawn', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'defeat', label: '团灭失败 (Defeat)', en: 'Defeat', badgeColor: 'bg-red-500/10 text-red-300 border-red-500/20' }
];

// 15. 等阶 (T1 ~ T12) —— 以各等阶属性锚点小境界命名（锚点 Lv：1/18/30/42/54/66/78/90/102/114/126/127，
// 与服务端 BaseAttributeCatalog.TierAt 及总纲 §10.2「代表大境界」列一致）
/** 等阶选项（T1~T12）：数值以各等阶属性锚点小境界命名，与服务端 BaseAttributeCatalog.TierAt 及总纲 §10.2 对齐。 */
export const TIER_OPTIONS: EnumOption<number>[] = [
  { value: 1, label: 'T1 · 凡俗 凡胎', en: 'Tier 1', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 2, label: 'T2 · 凡俗 叩仙门', en: 'Tier 2', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 3, label: 'T3 · 练气 半步筑基', en: 'Tier 3', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 4, label: 'T4 · 筑基 半步金丹', en: 'Tier 4', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 5, label: 'T5 · 金丹 半步元婴', en: 'Tier 5', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 6, label: 'T6 · 元婴 半步化神', en: 'Tier 6', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 7, label: 'T7 · 化神 半步炼虚', en: 'Tier 7', badgeColor: 'bg-pink-500/10 text-pink-300 border-pink-500/20' },
  { value: 8, label: 'T8 · 炼虚 半步合体', en: 'Tier 8', badgeColor: 'bg-red-500/10 text-red-300 border-red-500/20' },
  { value: 9, label: 'T9 · 合体 半步大乘', en: 'Tier 9', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 10, label: 'T10 · 大乘 半步渡劫', en: 'Tier 10', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' },
  { value: 11, label: 'T11 · 渡劫 半步飞升', en: 'Tier 11', badgeColor: 'bg-teal-500/10 text-teal-300 border-teal-500/20' },
  { value: 12, label: 'T12 · 飞升', en: 'Tier 12', badgeColor: 'bg-fuchsia-500/10 text-fuchsia-300 border-fuchsia-500/20' }
];

// 16. 14维基础属性与特殊机制元数据
/** 属性元数据单条结构：key 为配表存储键，category 决定分组展示，unit 为量纲（点/百分比），description 为一句话业务含义。 */
export interface StatMetadata {
  key: string;
  label: string;
  category: 'core' | 'attack' | 'defense' | 'special';
  unit?: string;
  description: string;
}

/** 全量属性元数据清单：14 维基础属性与特殊机制属性（汲取/反弹/威胁等）的中文标签、量纲与结算口径说明，供属性字典编辑器展示。 */
export const STAT_METADATA_LIST: StatMetadata[] = [
  { key: 'maxHitpoints', label: '最大气血 (HP)', category: 'core', unit: '点', description: '角色的生命上限' },
  { key: 'maxManapoints', label: '最大真元 (MP)', category: 'core', unit: '点', description: '施展法术与神通的灵力上限' },
  { key: 'physicalAttack', label: '物理攻击力 (AD)', category: 'attack', unit: '点', description: '炼体与剑修物理系杀伤' },
  { key: 'magicAttack', label: '法术攻击力 (AP)', category: 'attack', unit: '点', description: '五行道法法术伤害' },
  { key: 'armor', label: '护甲 (Armor)', category: 'defense', unit: '点', description: '减免受到的物理伤害' },
  { key: 'magicResistance', label: '法抗 (MagicResistance)', category: 'defense', unit: '点', description: '减免受到的术法伤害' },
  { key: 'accuracyRating', label: '命中等级 (Accuracy)', category: 'attack', unit: '点', description: '对抗目标闪避几率' },
  { key: 'evasionRating', label: '闪避等级 (Evasion)', category: 'defense', unit: '点', description: '规避攻击几率' },
  { key: 'criticalRating', label: '暴击等级 (Crit)', category: 'attack', unit: '点', description: '触发暴击伤害几率' },
  { key: 'criticalAvoidance', label: '免暴点数 (CritAvoid)', category: 'defense', unit: '点', description: '降低被暴击几率' },
  { key: 'breakRating', label: '破招等级 (Break)', category: 'attack', unit: '点', description: '无视防御与招架' },
  { key: 'parryRating', label: '招架等级 (Parry)', category: 'defense', unit: '点', description: '化解部分伤害' },
  { key: 'cooldownReductionPoints', label: '冷却缩减 (CDR)', category: 'special', unit: '点', description: 'NonLinear 缩减全部冷却' },
  { key: 'attackSpeedPoints', label: '攻击速度', category: 'special', unit: '点', description: 'NonLinear 缩短普攻读条' },
  { key: 'actionSpeedPoints', label: '行动速度', category: 'special', unit: '点', description: 'NonLinear 缩短全部读条' },
  { key: 'damageAbsorptionPoints', label: '总减伤/免伤', category: 'special', unit: '点', description: 'NonLinear 末位收口减伤' },
  { key: 'threatBonusPoints', label: '威胁增幅', category: 'special', unit: '点', description: 'Linear 放大有效威胁' },
  { key: 'hpDrainPoints', label: '气血汲取', category: 'special', unit: '点', description: '攻击命中按点数/100 回血' },
  { key: 'mpDrainPoints', label: '灵力汲取', category: 'special', unit: '点', description: '攻击命中按点数/100 偷取灵力' },
  { key: 'damageReflectPoints', label: '伤害反弹', category: 'special', unit: '点', description: '受击按点数/100 反弹伤害' },
  { key: 'healingBonusPoints', label: '治疗增幅', category: 'special', unit: '点', description: 'Linear 放大主动治疗量' },
  { key: 'threatOnTakenPoints', label: '受击涨威胁', category: 'special', unit: '点', description: '受击按减免前原始伤害 ×点数/100 累积自身威胁' }
];

/** 属性键 → 元数据的 O(1) 查询映射，供表格/字典编辑器按 key 反查中文标签与量纲。 */
export const STAT_METADATA_MAP = new Map<string, StatMetadata>(
  STAT_METADATA_LIST.map(s => [s.key, s])
);
