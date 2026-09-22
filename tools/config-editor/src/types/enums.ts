// ==============================================================================
// 文件名称: enums.ts
// 主要职责: 游戏数值与规则全量枚举定义、中文语义选项与现代扁平化彩色 Badge
// ==============================================================================

export interface EnumOption<T extends string | number = string> {
  value: T;
  label: string;
  en: string;
  badgeColor?: string;
}

// 1. 战斗流派 (CombatStyle)
export const COMBAT_STYLES: EnumOption[] = [
  { value: 'body', label: '炼体 (体修)', en: 'Body', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'sword', label: '剑修 (纯阳剑罡)', en: 'Sword', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'blade', label: '刀修 (霸刀狂刃)', en: 'Blade', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 'fiveElements', label: '五行 (灵道玄法)', en: 'FiveElements', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'talisman', label: '符修 (万符归宗)', en: 'Talisman', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'zither', label: '琴修 (九霄天音)', en: 'Zither', badgeColor: 'bg-pink-500/10 text-pink-300 border-pink-500/20' }
];

// 2. 伤害大类 (DamageCategory) —— ValidateAbility 只接受 physical / spell
export const DAMAGE_CATEGORIES: EnumOption[] = [
  { value: 'physical', label: '物理伤害 (Physical)', en: 'Physical', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 'spell', label: '术法伤害 (Spell)', en: 'Spell', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' }
];

// 3. 五行属性 (Element)
export const ELEMENTS: EnumOption[] = [
  { value: 'none', label: '无属性 (None)', en: 'None', badgeColor: 'bg-zinc-500/10 text-zinc-400 border-zinc-500/20' },
  { value: 'metal', label: '金系 (Metal)', en: 'Metal', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' },
  { value: 'wood', label: '木系 (Wood)', en: 'Wood', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'water', label: '水系 (Water)', en: 'Water', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'fire', label: '火系 (Fire)', en: 'Fire', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'earth', label: '土系 (Earth)', en: 'Earth', badgeColor: 'bg-amber-600/10 text-amber-300 border-amber-600/20' }
];

// 4. 道具大类 (ItemKind)
export const ITEM_KINDS: EnumOption[] = [
  { value: 'material', label: '天材地宝 / 原料 (Material)', en: 'Material', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'consumable', label: '灵丹妙药 / 消耗品 (Consumable)', en: 'Consumable', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'equipment', label: '法宝神兵 / 装备 (Equipment)', en: 'Equipment', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'manual', label: '功法秘籍 / 典籍 (Manual)', en: 'Manual', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'currency', label: '灵石硬通货 (Currency)', en: 'Currency', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' }
];

// 5. 道具子类 (ItemCategory)
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
export const EQUIPMENT_SLOTS: EnumOption[] = [
  { value: 'weapon', label: '本命神兵 (Weapon)', en: 'Weapon', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 'head', label: '道冠护额 (Head)', en: 'Head', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'chest', label: '护心道袍 (Chest)', en: 'Chest', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'legs', label: '玄法道履 (Legs)', en: 'Legs', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'ring', label: '储灵戒 (Ring)', en: 'Ring', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'necklace', label: '太虚佩 (Necklace)', en: 'Necklace', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 'belt', label: '束灵带 (Belt)', en: 'Belt', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'boots', label: '踏云靴 (Boots)', en: 'Boots', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' }
];

// 7. 怪物品阶 (CombatMonsterRank) —— ParseMonsterDefinitions: elite/boss 之外一律 minion
export const UNIT_RANKS: EnumOption[] = [
  { value: 'minion', label: '寻常小妖 (Minion)', en: 'Minion', badgeColor: 'bg-zinc-500/10 text-zinc-300 border-zinc-500/20' },
  { value: 'elite', label: '天地精英 (Elite)', en: 'Elite', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 'boss', label: '秘境首领 (Boss)', en: 'Boss', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' }
];

// 8. 行动类型 (ActionKind) —— ParseKind/ParseExplicitKind 词汇表
export const ACTION_TYPES: EnumOption[] = [
  { value: 'cultivation', label: '打坐吐纳 / 修炼 (Cultivation)', en: 'Cultivation', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'exploration', label: '灵山采撷 / 探索 (Exploration)', en: 'Exploration', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'alchemy', label: '开炉炼丹 / 炼制 (Alchemy)', en: 'Alchemy', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'combat', label: '除魔历练 / 战斗 (Combat)', en: 'Combat', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'travel', label: '御剑腾云 / 赶路 (Travel)', en: 'Travel', badgeColor: 'bg-teal-500/10 text-teal-300 border-teal-500/20' }
];

// 9. 行动执行模式 (ActionExecutionKind) —— ParseExecutionKind 词汇表；缺省按行动类型推导
export const ACTION_EXECUTION_KINDS: EnumOption[] = [
  { value: 'timed', label: '周期性结算 (Timed)', en: 'Timed', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'combatEncounter', label: '遭遇战结算 (CombatEncounter)', en: 'CombatEncounter', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'travel', label: '赶路移动 (Travel)', en: 'Travel', badgeColor: 'bg-teal-500/10 text-teal-300 border-teal-500/20' }
];

// 10. 技能效果类型 —— ValidateAbility/CastSkill 只判定 heal 与 buff，缺省为伤害
export const COMBAT_ABILITY_EFFECT_TYPES: EnumOption[] = [
  { value: 'damage', label: '神通杀伐 (缺省 Damage)', en: 'Damage', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'heal', label: '回春回血 (Heal)', en: 'Heal', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'buff', label: '玄光护体 / 增益 (Buff)', en: 'Buff', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' }
];

// 11. 技能目标类型 —— ValidateAbility 合法集
export const COMBAT_ABILITY_TARGET_TYPES: EnumOption[] = [
  { value: 'targetEnemy', label: '单体敌方 (TargetEnemy)', en: 'TargetEnemy', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'allEnemies', label: '全体敌方 (AllEnemies)', en: 'AllEnemies', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'self', label: '自身 (Self)', en: 'Self', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'allAllies', label: '全体友方 (AllAllies)', en: 'AllAllies', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 'lowestHitpointsAlly', label: '血量最低友方 (LowestHitpointsAlly)', en: 'LowestHitpointsAlly', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' }
];

// 12. 视觉渲染器类型 (VisualRendererKind)
export const VISUAL_RENDERER_KINDS: EnumOption[] = [
  { value: 'text', label: '道貌字符画 (Text)', en: 'Text' },
  { value: 'svg', label: '折面矢量图 (Svg)', en: 'Svg' },
  { value: 'image', label: '位图立绘 (Image)', en: 'Image' }
];

// 13. 技能触发条件五元组词汇表 —— CombatSkillEngine.ValidateTrigger / DuelTriggerEvaluator
export const TRIGGER_DEPENDENCIES: EnumOption[] = [
  { value: 'self', label: '自身 (Self)', en: 'Self' },
  { value: 'target-enemy', label: '当前敌方目标 (TargetEnemy)', en: 'TargetEnemy' },
  { value: 'ally-lowest-hp', label: '血量最低友方 (AllyLowestHp)', en: 'AllyLowestHp' },
  { value: 'ally-highest-hp', label: '血量最高友方 (AllyHighestHp)', en: 'AllyHighestHp' },
  { value: 'enemy-lowest-hp', label: '血量最低敌方 (EnemyLowestHp)', en: 'EnemyLowestHp' },
  { value: 'enemy-highest-hp', label: '血量最高敌方 (EnemyHighestHp)', en: 'EnemyHighestHp' }
];

export const TRIGGER_CONDITIONS: EnumOption[] = [
  { value: 'current-hp', label: '当前气血 (CurrentHp)', en: 'CurrentHp' },
  { value: 'missing-hp', label: '已损气血 (MissingHp)', en: 'MissingHp' },
  { value: 'current-hp-percentage', label: '当前气血百分比 (CurrentHp%)', en: 'CurrentHp%' },
  { value: 'missing-hp-percentage', label: '已损气血百分比 (MissingHp%)', en: 'MissingHp%' },
  { value: 'current-mana', label: '当前灵力 (CurrentMana)', en: 'CurrentMana' },
  { value: 'missing-mana', label: '已损灵力 (MissingMana)', en: 'MissingMana' },
  { value: 'current-mana-percentage', label: '当前灵力百分比 (CurrentMana%)', en: 'CurrentMana%' },
  { value: 'missing-mana-percentage', label: '已损灵力百分比 (MissingMana%)', en: 'MissingMana%' },
  { value: 'buff-stacks', label: 'Buff 层数 (BuffStacks)', en: 'BuffStacks' }
];

export const TRIGGER_COMPARATORS: EnumOption[] = [
  { value: 'greater-than-equal', label: '大于等于 (>=)', en: '>=' },
  { value: 'less-than-equal', label: '小于等于 (<=)', en: '<=' },
  { value: 'greater-than', label: '大于 (>)', en: '>' },
  { value: 'less-than', label: '小于 (<)', en: '<' }
];

// 13b. 功法类别 (discipline)：internal=内功心法（0 号槽位），spell=神通术法（缺省）
export const ABILITY_DISCIPLINES: EnumOption[] = [
  { value: 'internal', label: '内功心法 (Internal)', en: 'Internal', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'spell', label: '神通术法 (Spell)', en: 'Spell', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' }
];

// 13c. 六维根骨 (CoreAspectKind) —— rootAttribute 经验分配维度
export const ROOT_ATTRIBUTES: EnumOption[] = [
  { value: 'stamina', label: '体魄 (Stamina)', en: 'Stamina', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'meridians', label: '经脉 (Meridians)', en: 'Meridians', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 'attack', label: '力道 (Attack)', en: 'Attack', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 'bone', label: '根骨 (Bone)', en: 'Bone', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'agility', label: '身法 (Agility)', en: 'Agility', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'consciousness', label: '神识 (Consciousness)', en: 'Consciousness', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' }
];

// 13d. 怪物倾向权重键 —— ParseMonsterDefinitions Weight(...) 实际消费键
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
  { key: 'parry', label: '招架 (Parry)' },
  { key: 'metalResistance', label: '金抗点数 (×10)' },
  { key: 'woodResistance', label: '木抗点数 (×10)' },
  { key: 'waterResistance', label: '水抗点数 (×10)' },
  { key: 'fireResistance', label: '火抗点数 (×10)' },
  { key: 'earthResistance', label: '土抗点数 (×10)' }
];

// 13e. 行动收益类型与结算频率 —— ParseYields 词汇表
export const ACTION_YIELD_KINDS: EnumOption[] = [
  { value: 'qi', label: '修为灵气 (Qi)', en: 'Qi', badgeColor: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20' },
  { value: 'mana', label: '灵力 (Mana)', en: 'Mana', badgeColor: 'bg-blue-500/10 text-blue-300 border-blue-500/20' },
  { value: 'hitpoints', label: '气血 (Hitpoints)', en: 'Hitpoints', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 'experience', label: '经验 (Experience)', en: 'Experience', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 'item', label: '道具物品 (Item)', en: 'Item', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 'buff', label: '增益状态 (Buff)', en: 'Buff', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' }
];

export const ACTION_YIELD_PERS: EnumOption[] = [
  { value: 'second', label: '每秒结算 (PerSecond)', en: 'PerSecond' },
  { value: 'repeat', label: '每轮结算 (PerRepeat)', en: 'PerRepeat' }
];

// 13f. 节点可建模板 —— NodeBuildings.ResolveBuildableIds / DataTool 校验词汇表
export const BUILDABLE_TEMPLATES: EnumOption[] = [
  { value: 'all', label: '全部建筑 (All)', en: 'All' },
  { value: 'production', label: '生产建筑 (Production)', en: 'Production' },
  { value: 'combat', label: '战斗建筑 (Combat)', en: 'Combat' },
  { value: 'other', label: '其他建筑 (Other)', en: 'Other' }
];

// 15. 品阶阶位 (T1 ~ T12)
export const TIER_OPTIONS: EnumOption<number>[] = [
  { value: 1, label: 'T1 · 练气境', en: 'Tier 1', badgeColor: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20' },
  { value: 2, label: 'T2 · 筑基境', en: 'Tier 2', badgeColor: 'bg-sky-500/10 text-sky-300 border-sky-500/20' },
  { value: 3, label: 'T3 · 金丹境', en: 'Tier 3', badgeColor: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/20' },
  { value: 4, label: 'T4 · 元婴境', en: 'Tier 4', badgeColor: 'bg-purple-500/10 text-purple-300 border-purple-500/20' },
  { value: 5, label: 'T5 · 化神境', en: 'Tier 5', badgeColor: 'bg-amber-500/10 text-amber-300 border-amber-500/20' },
  { value: 6, label: 'T6 · 炼虚境', en: 'Tier 6', badgeColor: 'bg-rose-500/10 text-rose-300 border-rose-500/20' },
  { value: 7, label: 'T7 · 合体境', en: 'Tier 7', badgeColor: 'bg-pink-500/10 text-pink-300 border-pink-500/20' },
  { value: 8, label: 'T8 · 大乘境', en: 'Tier 8', badgeColor: 'bg-red-500/10 text-red-300 border-red-500/20' },
  { value: 9, label: 'T9 · 渡劫境', en: 'Tier 9', badgeColor: 'bg-orange-500/10 text-orange-300 border-orange-500/20' },
  { value: 10, label: 'T10 · 真仙境', en: 'Tier 10', badgeColor: 'bg-yellow-500/10 text-yellow-300 border-yellow-500/20' },
  { value: 11, label: 'T11 · 金仙境', en: 'Tier 11', badgeColor: 'bg-teal-500/10 text-teal-300 border-teal-500/20' },
  { value: 12, label: 'T12 · 太乙太初境', en: 'Tier 12', badgeColor: 'bg-fuchsia-500/10 text-fuchsia-300 border-fuchsia-500/20' }
];

// 16. 14维基础属性与特殊机制元数据
export interface StatMetadata {
  key: string;
  label: string;
  category: 'core' | 'attack' | 'defense' | 'special';
  unit?: string;
  description: string;
}

export const STAT_METADATA_LIST: StatMetadata[] = [
  { key: 'maxHitpoints', label: '最大气血 (HP)', category: 'core', unit: '点', description: '角色的生命上限' },
  { key: 'maxManapoints', label: '最大真元 (MP)', category: 'core', unit: '点', description: '施展法术与神通的灵力上限' },
  { key: 'physicalAttack', label: '物理攻击力 (AD)', category: 'attack', unit: '点', description: '炼体与剑修物理系杀伤' },
  { key: 'magicAttack', label: '法术攻击力 (AP)', category: 'attack', unit: '点', description: '五行道法法术伤害' },
  { key: 'armor', label: '护甲 (Armor)', category: 'defense', unit: '点', description: '减免受到的物理伤害' },
  { key: 'magicResistance', label: '法抗 (MagicResistance)', category: 'defense', unit: '点', description: '减免受到的术法伤害' },
  { key: 'metalResistance', label: '金系抗性', category: 'defense', unit: '点', description: '金系特殊伤害减免' },
  { key: 'woodResistance', label: '木系抗性', category: 'defense', unit: '点', description: '木系特殊伤害减免' },
  { key: 'waterResistance', label: '水系抗性', category: 'defense', unit: '点', description: '水系特殊伤害减免' },
  { key: 'fireResistance', label: '火系抗性', category: 'defense', unit: '点', description: '火系特殊伤害减免' },
  { key: 'earthResistance', label: '土系抗性', category: 'defense', unit: '点', description: '土系特殊伤害减免' },
  { key: 'accuracyRating', label: '命中等级 (Accuracy)', category: 'attack', unit: '点', description: '对抗目标闪避几率' },
  { key: 'evasionRating', label: '闪避等级 (Evasion)', category: 'defense', unit: '点', description: '规避攻击几率' },
  { key: 'criticalRating', label: '暴击等级 (Crit)', category: 'attack', unit: '点', description: '触发暴击伤害几率' },
  { key: 'criticalAvoidance', label: '暴击抗性 (CritAvoid)', category: 'defense', unit: '点', description: '降低被暴击几率' },
  { key: 'breakRating', label: '破招等级 (Break)', category: 'attack', unit: '点', description: '无视防御与招架' },
  { key: 'parryRating', label: '招架等级 (Parry)', category: 'defense', unit: '点', description: '化解部分伤害' },
  { key: 'cooldownReduction', label: '冷却缩减 (CDR)', category: 'special', unit: '%', description: '缩减神通技能冷却' },
  { key: 'actionSpeedRatio', label: '动作加速 (ATR)', category: 'special', unit: '%', description: '缩短施法与普攻前摇' },
  { key: 'penetrationRatio', label: '护甲/抗性穿透率', category: 'special', unit: '%', description: '穿透目标防御抗性' },
  { key: 'hpDrainRatio', label: '汲血率 (吸血)', category: 'special', unit: '%', description: '攻击命中吸取生命' },
  { key: 'mpDrainRatio', label: '汲蓝率 (吸蓝)', category: 'special', unit: '%', description: '攻击命中偷取目标灵力' },
  { key: 'damageReflectRatio', label: '反伤率', category: 'special', unit: '%', description: '受击时按比例反弹伤害' }
];

export const STAT_METADATA_MAP = new Map<string, StatMetadata>(
  STAT_METADATA_LIST.map(s => [s.key, s])
);
