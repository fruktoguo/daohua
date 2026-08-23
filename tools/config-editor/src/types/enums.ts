// ==============================================================================
// 文件名称: enums.ts
// 主要职责: 游戏全量枚举定义、中文语义标签与下拉选项元数据
// ==============================================================================

export interface EnumOption<T = string> {
  value: T;
  label: string;
  en: string;
  description?: string;
  badgeColor?: string;
}

// 1. 流派体系 (CombatStyle)
export const COMBAT_STYLES: EnumOption[] = [
  { value: 'body', label: '炼体道 (肉身成圣)', en: 'Body', badgeColor: 'border-amber-700/60 bg-amber-950/40 text-amber-300' },
  { value: 'fiveElements', label: '五行道 (术法通天)', en: 'Five Elements', badgeColor: 'border-cyan-700/60 bg-cyan-950/40 text-cyan-300' },
  { value: 'sword', label: '纯阳剑 (万剑归宗)', en: 'Sword', badgeColor: 'border-sky-700/60 bg-sky-950/40 text-sky-300' },
  { value: 'chaos', label: '混元道 (万法归一)', en: 'Chaos', badgeColor: 'border-purple-700/60 bg-purple-950/40 text-purple-300' },
  { value: 'talisman', label: '神符道 (符箓镇煞)', en: 'Talisman', badgeColor: 'border-yellow-700/60 bg-yellow-950/40 text-yellow-300' },
  { value: 'none', label: '无特定流派', en: 'None', badgeColor: 'border-zinc-700/60 bg-zinc-900/40 text-zinc-400' },
];

// 2. 伤害大类 (DamageCategory)
export const DAMAGE_CATEGORIES: EnumOption[] = [
  { value: 'physical', label: '物理伤害 (受护甲与破招影响)', en: 'Physical', badgeColor: 'border-orange-700/60 bg-orange-950/40 text-orange-300' },
  { value: 'magic', label: '术法伤害 (受法抗与五行抗性影响)', en: 'Magic', badgeColor: 'border-blue-700/60 bg-blue-950/40 text-blue-300' },
  { value: 'true', label: '真实伤害 (无视抗性与护甲)', en: 'True Damage', badgeColor: 'border-rose-700/60 bg-rose-950/40 text-rose-300' },
];

// 3. 五行元素 (Element)
export const ELEMENTS: EnumOption[] = [
  { value: 'none', label: '无属性 (纯净本源)', en: 'None', badgeColor: 'border-zinc-700/60 bg-zinc-900/40 text-zinc-400' },
  { value: 'metal', label: '金行 (锐利穿透)', en: 'Metal', badgeColor: 'border-amber-500/60 bg-amber-950/40 text-amber-200' },
  { value: 'wood', label: '木行 (生生不息/毒)', en: 'Wood', badgeColor: 'border-emerald-600/60 bg-emerald-950/40 text-emerald-300' },
  { value: 'water', label: '水行 (润物极寒/控)', en: 'Water', badgeColor: 'border-cyan-600/60 bg-cyan-950/40 text-cyan-300' },
  { value: 'fire', label: '火行 (烈焰爆裂/灼)', en: 'Fire', badgeColor: 'border-red-600/60 bg-red-950/40 text-red-300' },
  { value: 'earth', label: '土行 (厚重坚韧/防)', en: 'Earth', badgeColor: 'border-yellow-700/60 bg-yellow-950/40 text-yellow-300' },
];

// 4. 物品大类 (ItemKind)
export const ITEM_KINDS: EnumOption[] = [
  { value: 'material', label: '基础材料 (采集/掉落/生产用)', en: 'Material', badgeColor: 'border-teal-700/60 bg-teal-950/40 text-teal-300' },
  { value: 'consumable', label: '消耗品/丹药 (服食增益/回复)', en: 'Consumable', badgeColor: 'border-emerald-700/60 bg-emerald-950/40 text-emerald-300' },
  { value: 'equipment', label: '法宝装备 (穿戴属性提升)', en: 'Equipment', badgeColor: 'border-indigo-700/60 bg-indigo-950/40 text-indigo-300' },
  { value: 'manual', label: '功法秘籍 (参悟解锁技能/被动)', en: 'Manual', badgeColor: 'border-purple-700/60 bg-purple-950/40 text-purple-300' },
  { value: 'quest', label: '奇物任务 (机缘触发道具)', en: 'Quest', badgeColor: 'border-amber-700/60 bg-amber-950/40 text-amber-300' },
];

// 5. 物品分类 (ItemCategory)
export const ITEM_CATEGORIES: EnumOption[] = [
  { value: 'herb', label: '灵植草药', en: 'Herb' },
  { value: 'ore', label: '灵矿晶石', en: 'Ore' },
  { value: 'monsterDrop', label: '妖兽材料/妖丹', en: 'Monster Drop' },
  { value: 'elixir', label: '灵丹妙药', en: 'Elixir' },
  { value: 'weapon', label: '本命法器/兵刃', en: 'Weapon' },
  { value: 'armor', label: '道袍护甲', en: 'Armor' },
  { value: 'accessory', label: '仙家佩饰', en: 'Accessory' },
  { value: 'special', label: '天地奇珍', en: 'Special' },
];

// 6. 装备部位 (EquipmentSlot)
export const EQUIPMENT_SLOTS: EnumOption[] = [
  { value: 'weapon', label: '兵刃法宝 (主武器)', en: 'Weapon', badgeColor: 'border-red-700/60 text-red-300' },
  { value: 'head', label: '发冠宝顶 (头部)', en: 'Head', badgeColor: 'border-blue-700/60 text-blue-300' },
  { value: 'body', label: '乾坤道袍 (身体)', en: 'Body', badgeColor: 'border-indigo-700/60 text-indigo-300' },
  { value: 'legs', label: '玄罗法裙 (腿部)', en: 'Legs', badgeColor: 'border-cyan-700/60 text-cyan-300' },
  { value: 'feet', label: '踏云仙靴 (脚部)', en: 'Feet', badgeColor: 'border-emerald-700/60 text-emerald-300' },
  { value: 'ring', label: '纳戒指环 (戒指)', en: 'Ring', badgeColor: 'border-purple-700/60 text-purple-300' },
  { value: 'neck', label: '玲珑仙玉 (项链)', en: 'Neck', badgeColor: 'border-yellow-700/60 text-yellow-300' },
  { value: 'artifact', label: '随身本命 (法宝)', en: 'Artifact', badgeColor: 'border-pink-700/60 text-pink-300' },
];

// 7. 单位品质 (UnitRank)
export const UNIT_RANKS: EnumOption[] = [
  { value: 'minion', label: '寻常杂役 (小卒)', en: 'Minion', badgeColor: 'border-zinc-700 bg-zinc-900 text-zinc-300' },
  { value: 'normal', label: '普通精怪 (寻常)', en: 'Normal', badgeColor: 'border-slate-700 bg-slate-900 text-slate-200' },
  { value: 'elite', label: '洞天精英 (强力)', en: 'Elite', badgeColor: 'border-amber-700 bg-amber-950 text-amber-300' },
  { value: 'boss', label: '一方首领 (妖王/Boss)', en: 'Boss', badgeColor: 'border-rose-700 bg-rose-950 text-rose-300' },
  { value: 'worldBoss', label: '旷古绝煞 (世界巨擘)', en: 'World Boss', badgeColor: 'border-purple-700 bg-purple-950 text-purple-300' },
];

// 8. 行动类型 (ActionType)
export const ACTION_TYPES: EnumOption[] = [
  { value: 'cultivate', label: '吐纳修炼 (修为增长)', en: 'Cultivate', badgeColor: 'border-blue-700 bg-blue-950 text-blue-300' },
  { value: 'gathering', label: '灵植采集 (产出药材/矿物)', en: 'Gathering', badgeColor: 'border-emerald-700 bg-emerald-950 text-emerald-300' },
  { value: 'alchemy', label: '鼎炉炼丹 (消耗材料制作丹药)', en: 'Alchemy', badgeColor: 'border-purple-700 bg-purple-950 text-purple-300' },
  { value: 'combat', label: '密境历练 (刷怪战斗与掉落)', en: 'Combat', badgeColor: 'border-rose-700 bg-rose-950 text-rose-300' },
  { value: 'travel', label: '御风游历 (节点移动与探索)', en: 'Travel', badgeColor: 'border-amber-700 bg-amber-950 text-amber-300' },
];

// 9. 行动执行模式 (ActionExecutionKind)
export const ACTION_EXECUTION_KINDS: EnumOption[] = [
  { value: 'timed', label: '周期性结算 (固定间隔产生结算)', en: 'Timed' },
  { value: 'continuous', label: '持续消耗结算 (挂机按秒平滑扣减/产出)', en: 'Continuous' },
  { value: 'instant', label: '即时单次结算', en: 'Instant' },
];

// 10. 技能效果类型 (CombatAbilityEffectType)
export const COMBAT_ABILITY_EFFECT_TYPES: EnumOption[] = [
  { value: 'damage', label: '直接伤害 (Damage)', en: 'Damage', badgeColor: 'border-red-700 text-red-300' },
  { value: 'heal', label: '生命恢复 (Heal)', en: 'Heal', badgeColor: 'border-emerald-700 text-emerald-300' },
  { value: 'buff', label: '增益状态 (Buff)', en: 'Buff', badgeColor: 'border-cyan-700 text-cyan-300' },
  { value: 'debuff', label: '负面状态 (Debuff)', en: 'Debuff', badgeColor: 'border-purple-700 text-purple-300' },
  { value: 'shield', label: '灵气护盾 (Shield)', en: 'Shield', badgeColor: 'border-blue-700 text-blue-300' },
];

// 11. 技能目标类型 (CombatAbilityTargetType)
export const COMBAT_ABILITY_TARGET_TYPES: EnumOption[] = [
  { value: 'enemy', label: '敌方单体 (Single Enemy)', en: 'Enemy' },
  { value: 'allEnemies', label: '敌方全体 (All Enemies AoE)', en: 'All Enemies' },
  { value: 'self', label: '施法者自身 (Self)', en: 'Self' },
  { value: 'allAllies', label: '友方全体 (All Allies)', en: 'All Allies' },
];

// 12. 视觉渲染器类型 (VisualRendererKind)
export const VISUAL_RENDERER_KINDS: EnumOption[] = [
  { value: 'text', label: '文字单字标识 (纯文本字首)', en: 'Text' },
  { value: 'vector', label: '矢量折面图形 (SVG/Planar)', en: 'Vector' },
  { value: 'texture', label: '图片贴图 (Sprite)', en: 'Texture' },
  { value: 'poster', label: '大尺寸立绘海报', en: 'Poster' },
];

// 13. 战斗触发器条件 (CombatTriggerCondition)
export const COMBAT_TRIGGER_CONDITIONS: EnumOption[] = [
  { value: 'hpBelow', label: '自身生命低于百分比 (< HP%)', en: 'HP Below' },
  { value: 'hpAbove', label: '自身生命高于百分比 (> HP%)', en: 'HP Above' },
  { value: 'mpBelow', label: '自身灵力低于百分比 (< MP%)', en: 'MP Below' },
  { value: 'mpAbove', label: '自身灵力高于百分比 (> MP%)', en: 'MP Above' },
  { value: 'onCombatStart', label: '战斗开局瞬间触发', en: 'On Combat Start' },
  { value: 'targetHpBelow', label: '锁定目标生命低于百分比', en: 'Target HP Below' },
  { value: 'hasBuff', label: '自身存在指定 Buff 时', en: 'Has Buff' },
  { value: 'notHasBuff', label: '自身缺失指定 Buff 时', en: 'Not Has Buff' },
];

// 14. 战斗触发器动作 (CombatTriggerAction)
export const COMBAT_TRIGGER_ACTIONS: EnumOption[] = [
  { value: 'useAbility', label: '立即施放技能', en: 'Use Ability' },
  { value: 'useItem', label: '服食消耗品道具', en: 'Use Item' },
];

// 15. 属性字典全量清单 (14 维基础 + 战斗/高级属性)
export interface StatMetadata {
  key: string;
  label: string;
  category: 'core' | 'element' | 'rating' | 'special';
  unit: 'flat' | 'percentage' | 'multiplier' | 'milliseconds';
  description: string;
}

export const STAT_METADATA_LIST: StatMetadata[] = [
  // 核心攻防
  { key: 'maxHitpoints', label: '最大生命值', category: 'core', unit: 'flat', description: '角色的总气血上限' },
  { key: 'maxManapoints', label: '最大灵力值', category: 'core', unit: 'flat', description: '角色的总真元灵力上限' },
  { key: 'physicalAttack', label: '物理攻击力', category: 'core', unit: 'flat', description: '基础物理面板输出' },
  { key: 'magicAttack', label: '术法攻击力', category: 'core', unit: 'flat', description: '基础术法面板输出' },
  { key: 'armor', label: '物理护甲', category: 'core', unit: 'flat', description: '抵御物理伤害的有效防御值' },
  { key: 'magicResistance', label: '法术抗性', category: 'core', unit: 'flat', description: '抵御全系术法伤害的基础防御值' },

  // 五行抗性
  { key: 'metalResistance', label: '金行抗性', category: 'element', unit: 'flat', description: '专门抵御金系穿透伤害' },
  { key: 'woodResistance', label: '木行抗性', category: 'element', unit: 'flat', description: '专门抵御木系持续毒伤' },
  { key: 'waterResistance', label: '水行抗性', category: 'element', unit: 'flat', description: '专门抵御水系寒冰控制' },
  { key: 'fireResistance', label: '火行抗性', category: 'element', unit: 'flat', description: '专门抵御火系灼烧爆发' },
  { key: 'earthResistance', label: '土行抗性', category: 'element', unit: 'flat', description: '专门抵御土系重击' },

  // 评级与战斗概率
  { key: 'accuracyRating', label: '命中等级 (Accuracy)', category: 'rating', unit: 'flat', description: '与闪避对抗决定是否命中' },
  { key: 'evasionRating', label: '闪避等级 (Evasion)', category: 'rating', unit: 'flat', description: '躲避物理/术法攻击的判定等级' },
  { key: 'criticalRating', label: '暴击等级 (Critical)', category: 'rating', unit: 'flat', description: '造成致命暴击的概率基数' },
  { key: 'criticalAvoidance', label: '暴击抗性 (Crit Avoid)', category: 'rating', unit: 'flat', description: '降低被敌方暴击的概率' },
  { key: 'breakRating', label: '破招等级 (Break)', category: 'rating', unit: 'flat', description: '破除招架并造成高额穿防' },
  { key: 'parryRating', label: '招架等级 (Parry)', category: 'rating', unit: 'flat', description: '格挡伤害并化解攻势' },

  // 特殊机制与战斗高级属性
  { key: 'criticalDamageRatio', label: '暴击伤害倍率', category: 'special', unit: 'multiplier', description: '暴击时造成的最终伤害倍率 (默认 1.50)' },
  { key: 'criticalChance', label: '固定暴击率', category: 'special', unit: 'percentage', description: '额外附加的暴击百分比' },
  { key: 'pierceChance', label: '贯穿穿透概率', category: 'special', unit: 'percentage', description: '直接穿透护甲/抗性的概率' },
  { key: 'tenacity', label: '坚韧度 (减控)', category: 'special', unit: 'percentage', description: '缩短受到眩晕/定身等控制时长的百分比' },
  { key: 'hpRegen', label: '生命秒回', category: 'special', unit: 'flat', description: '每秒自然恢复的气血点数' },
  { key: 'mpRegen', label: '灵力秒回', category: 'special', unit: 'flat', description: '每秒自然恢复的真元点数' },
  { key: 'lifeStealRatio', label: '气血吸取率', category: 'special', unit: 'percentage', description: '将造成伤害按比例转化为自身气血' },
  { key: 'armorDamageRatio', label: '破甲削减比例', category: 'special', unit: 'percentage', description: '削弱目标物理防御的比例' },
  { key: 'hpDrainRatio', label: '敌方气血汲取比例', category: 'special', unit: 'percentage', description: '技能直接剥夺目标气血' },
  { key: 'cooldownReductionRatio', label: '冷却缩减 (CDR)', category: 'special', unit: 'percentage', description: '缩短主动技能释放冷却时间' },
  { key: 'actionSpeedReductionRatio', label: '动作加速/吟唱缩减 (ATR)', category: 'special', unit: 'percentage', description: '缩减动作间隔与技能吟唱时间' },
  { key: 'damageAbsorptionRatio', label: '伤害吸收比例', category: 'special', unit: 'percentage', description: '百分比化解承受的全部伤害' },
];

export const STAT_METADATA_MAP = new Map<string, StatMetadata>(
  STAT_METADATA_LIST.map(item => [item.key, item])
);

// 阶位列表 (Tier 1 ~ 12)
export const TIER_OPTIONS: EnumOption<number>[] = [
  { value: 1, label: '一阶 · 凡胎 (练肉)', en: 'Tier 1 - Mortal Body', badgeColor: 'text-zinc-300' },
  { value: 2, label: '二阶 · 叩仙门 (练气前)', en: 'Tier 2 - Gate Knock', badgeColor: 'text-emerald-400' },
  { value: 3, label: '三阶 · 练气 (引气入体)', en: 'Tier 3 - Qi Gathering', badgeColor: 'text-cyan-400' },
  { value: 4, label: '四阶 · 筑基 (筑大道基)', en: 'Tier 4 - Foundation', badgeColor: 'text-blue-400' },
  { value: 5, label: '五阶 · 金丹 (一品金丹)', en: 'Tier 5 - Golden Core', badgeColor: 'text-indigo-400' },
  { value: 6, label: '六阶 · 元婴 (破丹成婴)', en: 'Tier 6 - Nascent Soul', badgeColor: 'text-purple-400' },
  { value: 7, label: '七阶 · 化神 (元神显化)', en: 'Tier 7 - Soul Formation', badgeColor: 'text-pink-400' },
  { value: 8, label: '八阶 · 炼虚 (合道通虚)', en: 'Tier 8 - Void Refining', badgeColor: 'text-rose-400' },
  { value: 9, label: '九阶 · 合体 (身神合一)', en: 'Tier 9 - Integration', badgeColor: 'text-amber-400' },
  { value: 10, label: '十阶 · 大乘 (大乘圆满)', en: 'Tier 10 - Mahayana', badgeColor: 'text-orange-400' },
  { value: 11, label: '十一阶 · 渡劫 (九九天劫)', en: 'Tier 11 - Tribulation', badgeColor: 'text-red-400' },
  { value: 12, label: '十二阶 · 真仙 (道化太初)', en: 'Tier 12 - True Immortal', badgeColor: 'text-yellow-300 font-bold' },
];
