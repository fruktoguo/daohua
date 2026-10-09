// ==============================================================================
// 文件名称: config.ts
// 主要职责: 游戏全量 9 大配置文件的 TypeScript 强类型接口定义
// 说明: 字段集与 src/Domain/GameData/GameDataCatalog.cs、CombatSkillEngine.cs、
//       CombatModels.cs 的真实消费键一一对齐；引擎不读的幻影字段一律不建模。
// ==============================================================================

// 1. Manifest
/** 游戏数据版本清单（manifest.json 根对象）。schemaVersion 必须与服务端 SupportedSchemaVersion 常量一致，否则启动一致性校验失败；contentVersion 为纯展示用的发行版本号。 */
export interface GameDataManifest {
  schemaVersion: number;
  contentVersion: string;
}

// 2. Actions (actions.json)
// 行动收益条目：ParseYields 消费 kind/per/amount，item 与 buff 另读 id，
// buff 再读 name/durationSeconds/modifiers.{hpRegen,mpRegen,attackSpeedPoints,damageAbsorptionPoints,statRatios}
/** 行动 buff 收益的战外修正块：仅 kind=buff 的收益条目会读取，键集与 DataTool ConsumableBuffKeys 授权词汇表同口径。 */
export interface ActionYieldModifiers {
  hpRegen?: number;
  mpRegen?: number;
  attackSpeedPoints?: number;
  damageAbsorptionPoints?: number;
  statRatios?: Record<string, number>;
}

/** 行动单条声明式收益。kind 决定结算资源，per 决定按秒还是按轮发放；item/buff 收益必须另带 id（buff 还需 name），缺 amount 视为 0。 */
export interface ActionYieldConfig {
  kind: string; // qi | mana | hitpoints | experience | item | buff
  per: string; // second | repeat
  amount?: number;
  id?: string; // kind=item 时为 itemId；kind=buff 时为 buffId
  name?: string; // kind=buff 时的展示名
  durationSeconds?: number; // kind=buff 时的持续秒数
  modifiers?: ActionYieldModifiers; // kind=buff 时的修正块
}

/** 行动定义（actions.json 条目）：玩家挂机执行的最小调度单元。inputs/yields 为声明式配方与收益；availableNodeIds 与 excludedNodeIds 互斥（DataTool 强制，同配语义含糊直接拒绝）。 */
export interface ActionConfig {
  id: string;
  name: string;
  description: string;
  type: string; // ActionKind: cultivation | exploration | alchemy | combat | travel
  durationSeconds: number;
  executionKind?: string; // ActionExecutionKind: timed | combatEncounter | travel（缺省按 type 推导）
  combatEncounterId?: string; // 关联遭遇战 ID
  availableNodeIds?: string[]; // 可用大地图节点 ID
  hiddenFromCatalog?: boolean;
  inputs?: Record<string, number>; // 消耗配方: itemId -> 数量
  yields?: ActionYieldConfig[]; // 声明式收益列表（物品/修为/灵力/气血/经验/增益）
}

// 3. Items (items.json)
// 消耗品 Buff：SimulationEngine.ActivateInstantConsumables 消费
// buffId/name（必需）、durationSeconds/hpRegen/mpRegen/attackSpeedPoints/damageAbsorptionPoints
/** 消耗品战外 Buff 单条效果：由 SimulationEngine.ActivateInstantConsumables 激活；buffId/name/durationSeconds 为必需三元组，其余为可选数值修饰。 */
export interface BuffEffectConfig {
  buffId: string;
  name: string;
  durationSeconds: number;
  hpRegen?: number;
  mpRegen?: number;
  attackSpeedPoints?: number;
  damageAbsorptionPoints?: number;
}

/** 消耗品服食规格（ItemConfig.consumable）。usableInActionTypes 声明可装配的物品栏域（历练/各制造技艺/战斗，总纲 §6.2.3）；顶层键受 DataTool ConsumableKeys 白名单约束，未知键直接校验失败。 */
export interface ConsumableConfig {
  usableInActionTypes: string[]; // ActionKind set
  durationSeconds?: number;
  isContinuous?: boolean;
  cultivationMultiplier?: number;
  gatheringMultiplier?: number;
  alchemyMultiplier?: number;
  cooldownSeconds?: number; // 即时消耗品自动使用冷却（必需为正）
  hitpointRestore?: number;
  manapointRestore?: number;
  buffs?: BuffEffectConfig[];
  triggers?: CombatTrigger[]; // 使用条件：与技能共用「对象-类型-比对」词表
}

/** 物品定义（items.json 条目）。kind/category 决定坊市与背包归类；consumable 存在时该物品可服食，其 cooldownSeconds 必须为正（自动使用防连点的硬约束）。 */
export interface ItemConfig {
  id: string;
  name: string;
  description: string;
  kind: string; // ItemKind
  category?: string; // ItemCategory
  tier?: number;
  sellPrice?: number;
  isTradable?: boolean;
  consumable?: ConsumableConfig;
}

// 4. Abilities (abilities.json)
// 触发条件五元组：CombatSkillEngine.ValidateTrigger / LoadoutCommand.ReadTriggers 词汇表
/** 技能/消耗品共用的战内触发条件五元组。dependency/condition/comparator 三段词表由 CombatSkillEngine.ValidateTrigger 强制；condition=buff-stacks 时 buffId 必填，否则触发器无效。 */
export interface CombatTrigger {
  dependency: string; // self | target-enemy | ally-lowest-hp | ally-highest-hp | enemy-lowest-hp | enemy-highest-hp
  condition: string; // current-hp | missing-hp | *-percentage | current-mana | missing-mana | buff-stacks
  comparator: string; // greater-than-equal | less-than-equal | greater-than | less-than
  value: number;
  buffId?: string | null; // condition=buff-stacks 时必需
}

// 技能增益块：ApplySupport / CastSkill 消费
// buffId/name/durationSeconds/hpRegen/mpRegen/attackSpeedPoints/damageAbsorptionPoints/threatPoints
/** 技能战内 Buff 块（AbilityConfig.buff）：CastSkill/ApplySupport 消费；键集受 DataTool AbilityBuffKeys 白名单约束，buffId 缺省时引擎按 ability-buff:技能ID 规则派生。 */
export interface AbilityBuffConfig {
  buffId?: string;
  name?: string;
  durationSeconds?: number;
  hpRegen?: number;
  mpRegen?: number;
  attackSpeedPoints?: number;
  damageAbsorptionPoints?: number;
  threatPoints?: number;
}

/** 神通技能定义（abilities.json 条目）。category 只接受 physical/spell；effectType 缺省为伤害，heal/buff 时伤害字段无效而治疗/buff 字段生效；defaultTriggers 为装配槽未覆写时的自动施放条件。 */
export interface AbilityConfig {
  id: string;
  name: string;
  description?: string;
  style?: string; // CombatStyle 展示标签（fiveElements/talisman/body/sword/blade 等）
  category: string; // physical | spell（ValidateAbility 强制）
  element: string; // CombatElement: none/metal/wood/water/fire/earth
  discipline?: string; // internal | spell；缺省视为 spell（0 号内功位只收 internal）
  rootAttribute?: string; // CoreAspectKind: stamina/meridians/attack/bone/agility/consciousness
  tier?: number;
  cooldownMilliseconds?: number;
  castMilliseconds?: number;
  manaCost?: number;
  effectType?: string; // heal | buff；缺省为伤害技能
  targetType?: string; // targetEnemy | allEnemies | self | allAllies | lowestHitpointsAlly
  maxTargets?: number; // targetType=allEnemies 时的目标上限
  damageRatio?: number;
  flatDamage?: number;
  healingRatio?: number;
  flatHealing?: number;
  threatGain?: number;
  threatPoints?: number;
  buff?: AbilityBuffConfig;
  defaultTriggers?: CombatTrigger[];
}

// 5. Equipment (equipment.json)
// 真实消费集：LoadoutCommand 读 slot；PlayerAttributeResolver 读 flatStats/baseStatRatios/
// specialMechanics 全量百分比点数键（战斗设计 §7.5 词汇表，DataTool 强制校验）；
// StaticGameCatalogMapper 投影 baseStatRatios/flatStats/specialMechanics/enhancementBonus。
// stats/style/category/element/attackIntervalMilliseconds/intensity/budget/weights 均无消费方。
/** 装备定义（equipment.json 条目）。slot 为 LoadoutCommand 强制读取的装配槽位；三个属性字典的键集受 DataTool 百分比点数词汇表校验，未知键直接校验失败。 */
export interface EquipmentConfig {
  id: string;
  name: string;
  slot: string; // EquipmentSlot（LoadoutCommand 强制读取）
  tier?: number;
  quality?: number;
  baseStatRatios?: Record<string, number>; // 百分比加成，强化每级 +2%
  flatStats?: Record<string, number>; // 固定点数，强化每级 +2%
  specialMechanics?: Record<string, number>; // 特殊机制点数键：threatBonusPoints/hpDrainPoints/damageReflectPoints/各增减伤 Points 等
  enhancementBonus?: Record<string, number>; // 每级强化额外加成（映射层透传展示）
  description?: string;
}

// 6. Monsters (monsters.json)
// 怪物与玩家同构建模：战斗属性全部由「等级锚点 × 生命层次倍率 × weights 倾向权重」派生，
// 配表只声明 level/rank/budgetCost/weights/element/attackIntervalMilliseconds/汲取反弹/skills/dropTable/三项奖励。
/** 战斗单位立绘/图标渲染声明。renderer 决定前端渲染通道（text 字符画 / svg 矢量 / image 位图），source 与 poster 分别为对应素材来源。 */
export interface CharacterVisualDefinition {
  renderer: string; // VisualRendererKind
  source?: string;
  poster?: string;
}

// 掉落条目：ParseMonsterDefinitions 消费 itemId/dropChance/minQuantity/maxQuantity
/** 怪物单条掉落定义。dropChance 必须落在 (0,1] 区间（DataTool 强制），min/maxQuantity 夹住单次掉落数量区间。 */
export interface CombatDropDefinition {
  itemId: string;
  dropChance: number;
  minQuantity?: number;
  maxQuantity?: number;
}

/** 怪物单条技能装配。abilityId 必须存在于 abilities.json（DataTool 强制）；triggerHpRatio 为自身气血低于该比例时的起手血线，缺省 1 表示随时可放。 */
export interface MonsterSkillConfig {
  abilityId: string;
  level?: number;
  triggerHpRatio?: number;
}

/** 战斗单位定义（monsters.json 条目，玩家与怪物同构）。战斗属性不由配表直书，而是由「等级锚点 × 生命层次倍率 × weights 倾向权重」派生；modifiers 键集受 DataTool 百分比点数词汇表校验。 */
export interface CombatantConfig {
  id: string;
  name: string;
  visual?: CharacterVisualDefinition;
  style?: string; // 展示用流派标签（StaticGameCatalogMapper 投影）
  category?: string; // 展示用伤害类别（physical | spell）
  element?: string; // CombatElement
  level?: number;
  rank?: string; // minion | elite | boss（缺省 minion）
  budgetCost?: number;
  weights?: Record<string, number>; // 倾向权重：hp/mp/ad/ap/armor/mres/hit/eva/crit/cavoid/break/parry
  modifiers?: Record<string, number>; // 百分比点数修饰：增减伤/攻速/冷却/威胁等（DataTool 词汇表校验）
  attackIntervalMilliseconds?: number;
  hpDrainPoints?: number;
  mpDrainPoints?: number;
  damageReflectPoints?: number;
  healingBonusPoints?: number;
  dropTable?: CombatDropDefinition[];
  skills?: MonsterSkillConfig[];
  qiReward?: number;
  experienceReward?: number;
  rootExpReward?: number;
}

// 7. Encounters (encounters.json)
// ParseEncounterDefinitions 消费集
/** 遭遇战随机刷怪池单条定义。weight 为按权重随机抽取的相对权重，strength 为该怪的体量强度（计入 maxTotalStrength 上限），两者必须为正数（DataTool 强制）。 */
export interface CombatSpawnConfig {
  enemyId: string;
  weight: number;
  strength: number;
}

/** 秘境遭遇战定义（encounters.json 条目）。minBudget ≤ maxBudget 为硬约束（DataTool 强制）；onPartyWipe 只接受 respawn/defeat，缺省 respawn（原地复活）。 */
export interface CombatEncounterConfig {
  id: string;
  name: string;
  minBudget?: number;
  maxBudget?: number;
  maxTotalStrength?: number;
  randomSpawns?: CombatSpawnConfig[];
  eliteEnemyIds?: string[];
  bossEnemyIds?: string[];
  battlesPerElite?: number;
  battlesPerBoss?: number;
  searchMilliseconds?: number;
  respawnMilliseconds?: number;
  playerRespawnMilliseconds?: number;
  onPartyWipe?: string; // respawn 原地复活 | defeat 团灭失败，缺省 respawn
}

// 8. Maps (maps.json)
// 节点 NPC/建筑实体：ReadEntities 消费 id/name/kind/description/effect
/** 世界地图节点上的 NPC/建筑实体（ReadEntities 消费 id/name/kind/description/effect）。 */
export interface WorldMapEntityDefinition {
  id: string;
  name: string;
  kind?: string; // npc | building
  description?: string;
  effect?: string;
}

/** 世界地图节点定义。节点是行动/遭遇/建筑可建性的挂载点：availableActionIds 与 encounterIds 引用必须存在（DataTool 强制）；buildableList 与 buildableTemplate 二选一，皆空则不可建造。 */
export interface WorldMapNodeDefinition {
  id: string;
  name: string;
  description: string;
  x: number;
  y: number;
  cultivationMultiplier?: number;
  availableActionIds?: string[];
  encounterIds?: string[];
  npcs?: WorldMapEntityDefinition[];
  buildings?: WorldMapEntityDefinition[];
  buildableTemplate?: string; // all | production | combat | other
  buildableList?: string[]; // 自定义可建建筑 ID 列表（优先于 buildableTemplate）
  minBudget?: number; // 节点卡片展示用覆盖（StaticGameCatalogMapper 投影）
  maxBudget?: number;
  eliteInterval?: number;
  bossInterval?: number;
}

// 边：BuildWorldGraph 消费 id/fromNodeId/toNodeId/durationSeconds（无 id 的边会被丢弃）
/** 世界地图路径边（BuildWorldGraph 消费 id/fromNodeId/toNodeId/durationSeconds）。id 为引擎建图必需——无 id 的边会被静默丢弃，因此编辑器强制非空。 */
export interface WorldMapEdgeDefinition {
  id: string;
  fromNodeId: string;
  toNodeId: string;
  durationSeconds: number;
}

/** 世界大地图定义（maps.json 条目）。nodes/edges 构成无向世界图，DataTool 校验边端点必须为本图已登记节点且全图从起始村连通。 */
export interface WorldMapDefinition {
  id: string;
  name: string;
  description: string;
  backgroundSeed?: number;
  nodes: WorldMapNodeDefinition[];
  edges: WorldMapEdgeDefinition[];
}

// 9. Expected Attributes (expected-attributes.json)
/** 期望数值矩阵（expected-attributes.json 根对象）：各等阶四大乘区（基础白字/灵根/装备/丹药 Buff）解算基准与双流派仿真锚点。tiers 内 rules/各 baseline 为自由结构，仅作数值设计对照展示。 */
export interface ExpectedAttributesConfig {
  version: string;
  description: string;
  fourMultiplierFormula: string;
  rules: Record<string, any>;
  tiers: Array<{
    tier: number;
    level: number;
    realmName: string;
    baseFlat: Record<string, number>;
    rootMultiplier: { rootPercentage: number; multiplier: number };
    equipBaseline: Record<string, any>;
    buffBaseline: Record<string, any>;
    compoundMultiplier: number;
    finalExpected: Record<string, number>;
    combatSimulationBaseline?: Record<string, any>;
    combatSimulationDualStyle?: Record<string, any>;
    advancedMechanicsQuadratic?: Record<string, any>;
    combatEfficiencyRatio?: Record<string, any>;
    expectedSpecialAttributes?: Record<string, any>;
  }>;
}

// 全局所有配置文件类型映射
/** 编辑器内存态的 9 大配置文件聚合视图。键为逻辑文件名（actions.json 等），实际磁盘上按子目录拆分多文件，由 Vite API 插件聚合/写回。 */
export interface AllGameConfigFiles {
  'manifest.json': GameDataManifest;
  'actions.json': ActionConfig[];
  'items.json': ItemConfig[];
  'abilities.json': AbilityConfig[];
  'equipment.json': EquipmentConfig[];
  'monsters.json': CombatantConfig[];
  'encounters.json': CombatEncounterConfig[];
  'maps.json': WorldMapDefinition[];
  'expected-attributes.json': ExpectedAttributesConfig;
}

/** 逻辑配置文件名（AllGameConfigFiles 的键），也是侧边栏导航与保存 API 的路由键。 */
export type ConfigFileName = keyof AllGameConfigFiles;

/** 侧边栏导航清单：逻辑文件名 → 中文标签 / 图标名（lucide-react 图标组件名）。 */
export const CONFIG_FILE_LIST: { name: ConfigFileName; label: string; icon: string; countKey?: string }[] = [
  { name: 'manifest.json', label: '版本清单 (Manifest)', icon: 'FileCode2' },
  { name: 'actions.json', label: '游戏行动 (Actions)', icon: 'Activity' },
  { name: 'items.json', label: '物品道具 (Items)', icon: 'Package' },
  { name: 'abilities.json', label: '神通技能 (Abilities)', icon: 'Flame' },
  { name: 'equipment.json', label: '法宝装备 (Equipment)', icon: 'Shield' },
  { name: 'monsters.json', label: '精怪妖兽 (Monsters)', icon: 'Skull' },
  { name: 'encounters.json', label: '秘境遭遇 (Encounters)', icon: 'Swords' },
  { name: 'maps.json', label: '世界大地图 (Maps)', icon: 'Compass' },
  { name: 'expected-attributes.json', label: '期望数值矩阵 (Expected)', icon: 'TrendingUp' },
];
