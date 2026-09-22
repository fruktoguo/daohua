// ==============================================================================
// 文件名称: config.ts
// 主要职责: 游戏全量 9 大配置文件的 TypeScript 强类型接口定义
// ==============================================================================

// 1. Manifest
export interface GameDataManifest {
  schemaVersion: number;
  contentVersion: string;
}

// 2. Actions (actions.json)
export interface ActionConfig {
  id: string;
  name: string;
  description: string;
  type: string; // ActionType (cultivate, gathering, alchemy, combat, travel)
  durationSeconds: number;
  executionKind?: string; // ActionExecutionKind (timed, continuous, instant)
  combatEncounterId?: string; // 关联遭遇战 ID
  availableNodeIds?: string[]; // 可用大地图节点 ID
  hiddenFromCatalog?: boolean;
  inputs?: Record<string, number>; // 消耗配方: itemId -> 数量
  outputs?: Record<string, number>; // 产出配方: itemId -> 数量
}

// 3. Items (items.json)
export interface BuffEffectConfig {
  buffId: string;
  name: string;
  durationSeconds: number;
  stackable?: boolean;
  maxStacks?: number;
  qiGainMultiplier?: number;
  attackLevelRatio?: number;
  damageRatio?: number;
  attackSpeedRatio?: number;
  gatheringOutputMultiplier?: number;
  alchemyOutputMultiplier?: number;
  intensity?: number;
  weights?: Record<string, number>;
}

export interface ConsumableConfig {
  usableInActionTypes: string[]; // ActionType set
  durationSeconds?: number;
  isContinuous?: boolean;
  cultivationMultiplier?: number;
  gatheringMultiplier?: number;
  alchemyMultiplier?: number;
  cooldownSeconds?: number;
  recoverySeconds?: number;
  triggerHpRatio?: number;
  triggerAtCombatStart?: boolean;
  hitpointRestore?: number;
  manapointRestore?: number;
  buffs?: BuffEffectConfig[];
  defaultCombatTriggers?: CombatTrigger[];
  intensity?: number;
  weights?: Record<string, number>;
}

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
export interface CombatTrigger {
  condition: string; // CombatTriggerCondition
  thresholdRatio: number;
  action: string; // CombatTriggerAction
  targetAbilityId?: string;
  targetItemId?: string;
}

export interface AbilityConfig {
  id: string;
  name: string;
  description?: string;
  style: string; // CombatStyle
  category: string; // DamageCategory
  element: string; // Element
  cooldownMilliseconds?: number;
  castMilliseconds?: number;
  manaCostRatio?: number;
  effectType?: string; // CombatAbilityEffectType
  targetType?: string; // CombatAbilityTargetType
  damageRatio?: number;
  flatDamage?: number;
  healingRatio?: number;
  flatHealing?: number;
  damageOverTimeRatio?: number;
  damageOverTimeMilliseconds?: number;
  damageOverTimeTickMilliseconds?: number;
  stunChance?: number;
  stunMilliseconds?: number;
  blindChance?: number;
  blindMilliseconds?: number;
  silenceChance?: number;
  silenceMilliseconds?: number;
  penetrationRatio?: number;
  armorDamageRatio?: number;
  hpDrainRatio?: number;
  pierceChance?: number;
  triggerHpRatio?: number;
  defaultTriggers?: CombatTrigger[];
  tier?: number;
}

// 5. Equipment (equipment.json)
export interface EquipmentCombatStats {
  damageRatio?: number;
  armor?: number;
  magicResistance?: number;
  accuracyRatio?: number;
  evasionRatio?: number;
  criticalChance?: number;
  criticalDamageRatio?: number;
  maxHitpoints?: number;
  maxManapoints?: number;
  style?: string;
  category?: string;
  element?: string;
  attackIntervalMilliseconds?: number;
}

export interface EquipmentConfig {
  id: string;
  name: string;
  slot: string; // EquipmentSlot
  tier?: number;
  quality?: number;
  style?: string; // CombatStyle
  category?: string; // DamageCategory
  element?: string; // Element
  attackIntervalMilliseconds?: number;
  baseStatRatios?: Record<string, number>;
  flatStats?: Record<string, number>;
  specialMechanics?: Record<string, number>;
  stats?: EquipmentCombatStats;
  enhancementBonus?: EquipmentCombatStats;
  intensity?: number;
  budget?: number;
  weights?: Record<string, number>;
  description?: string;
}

// 6. Monsters (monsters.json)
export interface CharacterVisualDefinition {
  renderer: string; // VisualRendererKind
  source?: string;
  poster?: string;
}

export interface CombatDropDefinition {
  itemId: string;
  chance: number;
  minAmount?: number;
  maxAmount?: number;
}

export interface MonsterSkillConfig {
  abilityId: string;
  level?: number;
  triggerHpRatio?: number;
}

export interface CombatantConfig {
  id: string;
  name: string;
  visual?: CharacterVisualDefinition;
  style: string; // CombatStyle
  category: string; // DamageCategory
  element: string; // Element
  level?: number;
  tier?: number;
  rank?: string; // UnitRank
  weights?: Record<string, number>;
  maxHitpoints?: number;
  maxManapoints?: number;
  attackIntervalMilliseconds?: number;
  accuracyRating?: number;
  evasionRating?: number;
  maxDamage?: number;
  defensiveMaxDamage?: number;
  armor?: number;
  magicResistance?: number;
  metalResistance?: number;
  woodResistance?: number;
  waterResistance?: number;
  fireResistance?: number;
  earthResistance?: number;
  tenacity?: number;
  pierceChance?: number;
  curseRatio?: number;
  furyRatio?: number;
  weakenRatio?: number;
  lifeStealRatio?: number;
  armorDamageRatio?: number;
  hpDrainRatio?: number;
  mpDrainRatio?: number;
  damageReflectRatio?: number;
  criticalRating?: number;
  criticalChance?: number;
  criticalDamageRatio?: number;
  criticalAvoidance?: number;
  damageAbsorptionRatio?: number;
  hpRegen?: number;
  mpRegen?: number;
  hpRegenPerTenSeconds?: number;
  mpRegenPerTenSeconds?: number;
  enrageTimeMilliseconds?: number;
  experienceMultiplier?: number;
  experience?: number;
  spiritMin?: number;
  spiritMax?: number;
  dropTable?: CombatDropDefinition[];
  rareDropTable?: CombatDropDefinition[];
  skills?: MonsterSkillConfig[];
  breakRating?: number;
  parryRating?: number;
  skillIntensity?: number;
  dynamicResistanceFactorK?: number;
  actionSpeedReductionRatio?: number;
  cooldownReductionRatio?: number;
  budgetCost?: number;
}

// 7. Encounters (encounters.json)
export interface CombatSpawnConfig {
  enemyId: string;
  weight: number;
  strength: number;
}

export interface CombatEncounterConfig {
  id: string;
  name: string;
  randomSpawns?: CombatSpawnConfig[];
  maxSpawnCount?: number;
  maxTotalStrength?: number;
  bossEnemyIds?: string[];
  battlesPerBoss?: number;
  searchMilliseconds?: number;
  respawnMilliseconds?: number;
  playerRespawnMilliseconds?: number;
  minBudget?: number;
  maxBudget?: number;
  eliteInterval?: number;
  eliteEnemyIds?: string[];
  experiencePerWin?: number;
  spiritMinPerWin?: number;
  spiritMaxPerWin?: number;
  enrageTimeMilliseconds?: number;
  enrageTickMilliseconds?: number;
  maxEnrageStacks?: number;
  enrageRatioPerStack?: number;
  dropTable?: CombatDropDefinition[];
  rareDropTable?: CombatDropDefinition[];
}

// 8. Maps (maps.json)
export interface WorldMapNodeDefinition {
  id: string;
  name: string;
  description: string;
  x: number;
  y: number;
  cultivationMultiplier?: number;
  availableActionIds?: string[];
  encounterIds?: string[];
  npcs?: string[];
  buildings?: string[];
}

export interface WorldMapEdgeDefinition {
  fromNodeId: string;
  toNodeId: string;
  distance: number;
}

export interface WorldMapDefinition {
  id: string;
  name: string;
  description: string;
  backgroundSeed?: number;
  nodes: WorldMapNodeDefinition[];
  edges: WorldMapEdgeDefinition[];
}

// 9. Expected Attributes (expected-attributes.json)
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

export type ConfigFileName = keyof AllGameConfigFiles;

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
