import type { BuffEffect, CombatantStats, CombatEffectProfile, LinggenValues } from './rules.ts';

export interface SimulatorTraitProgressDto {
  id: string;
  level: number;
  exp: number;
}

export interface SimulatorLinggenDto {
  metal: number;
  wood: number;
  water: number;
  fire: number;
  earth: number;
}

export interface SimulatorCoreAttributesDto {
  staminaExp: number;
  meridiansExp: number;
  attackExp: number;
  boneExp: number;
  agilityExp: number;
  consciousnessExp: number;
}

export interface SimulatorAbilitySlotDto {
  abilityId: string | null;
  level: number;
}

export interface SimulatorEquipmentSlotDto {
  itemId: string;
  enhancementLevel: number;
}

export interface SimulatorBuffEffectDto {
  channel: number;
  value: number;
}

export interface SimulatorCombatSkillCooldownDto {
  abilityId: string;
  remainingSeconds: number;
}

export interface SimulatorCombatSkillsDto {
  cooldowns: SimulatorCombatSkillCooldownDto[];
  castingAbilityId: string | null;
  castRemainingSeconds: number;
  castCount: number;
}

export interface SimulatorCombatBuffCarryDto {
  buffId: string;
  remainingSeconds: number;
  stacks: number;
  name: string;
  missingHitpointsPerStack: number;
  effects?: SimulatorBuffEffectDto[];
  statRatios?: Record<string, number>;
}

export interface SimulatorActiveBuffDto {
  buffId: string;
  expiresAtUnixSeconds: number;
  stacks: number;
  name: string;
  sourceItemId: string | null;
  effects?: SimulatorBuffEffectDto[];
  statRatios?: Record<string, number>;
}

export interface SimulatorAutoUseSlotDto {
  actionType: string;
  slotIndex: number;
  itemId: string | null;
  quantity: number;
  enabled: boolean;
  remainingDurationSeconds: number;
  cooldownUntilUnixSeconds: number;
  group: string;
}

export interface SimulatorAssetDto {
  itemId: string;
  variant: string;
  quantity: number;
}

export interface SimulatorProfileDto {
  formatVersion: number;
  gameDataFingerprint: string;
  sourcePlayerId: string | null;
  displayName: string;
  avatar: string;
  capturedAtUnixSeconds: number;
  realmLevel: number;
  experience: number;
  foundation: number;
  combatExperience: number;
  lifetimeExperience: number;
  ageSeconds: number;
  completedActions: number;
  traitIds?: string[] | null;
  unlockedTraits?: SimulatorTraitProgressDto[] | null;
  confirmedLinggen: SimulatorLinggenDto | null;
  heavenGateEntered: boolean;
  coreAttributes: SimulatorCoreAttributesDto;
  abilitySlots: SimulatorAbilitySlotDto[];
  abilityExperiences?: Record<string, number> | null;
  equipment: SimulatorEquipmentSlotDto[];
  techniqueExperiences?: Record<string, number> | null;
  abilityTriggers?: Record<string, unknown[]> | null;
  consumableTriggers?: Record<string, unknown[]> | null;
  hitpoints: number;
  maxHitpoints: number;
  manapoints: number;
  maxManapoints: number;
  regenCarrySeconds: number;
  combatSkills: SimulatorCombatSkillsDto;
  combatBuffCarry: SimulatorCombatBuffCarryDto[];
  buffs: SimulatorActiveBuffDto[];
  combatAutoUseSlots: SimulatorAutoUseSlotDto[];
  assets: SimulatorAssetDto[];
  respawnPolicy: number;
  respawnTargetPlayerId: string | null;
}

export interface SimulatorNodeBuildingDto {
  buildingId: string;
  level: number;
  targetLevel: number;
  startedAtUnixSeconds: number;
  contributedMaterials?: Record<string, number> | null;
}

export interface SimulatorNodeBuildingUsageDto {
  buildingId: string;
  activeUsers: number;
  capacity: number;
  efficiency: number;
}

export interface SimulatorBuildingEffectsDto {
  explorationSpeedRatio?: number;
  explorationOutputRatio?: number;
  alchemySpeedRatio?: number;
  alchemyOutputRatio?: number;
  cultivationOutputRatio?: number;
  combatHitpointRegen?: number;
  combatDamageReductionRatio?: number;
  monsterWeakenRatio?: number;
  gatheringSpeedRatio?: number;
  miningSpeedRatio?: number;
  forgingSpeedRatio?: number;
  talismanSpeedRatio?: number;
  formationSpeedRatio?: number;
  enhancementSpeedRatio?: number;
  combatManapointRegen?: number;
}

export interface SimulatorEnvironmentNodeDto {
  nodeId: string;
  buildings: SimulatorNodeBuildingDto[];
  effects: SimulatorBuildingEffectsDto;
  usages: SimulatorNodeBuildingUsageDto[];
}

export interface SimulatorEnvironmentDto {
  formatVersion: number;
  gameDataFingerprint: string;
  capturedAtUnixSeconds: number;
  nodes: SimulatorEnvironmentNodeDto[];
}

export interface SimulatorResolveRequestDto {
  formatVersion: number;
  nodeId: string;
  members: SimulatorProfileDto[];
  environment: SimulatorEnvironmentDto;
}

export interface SimulatorRunRequestDto {
  formatVersion: number;
  nodeId: string;
  difficulty: number;
  durationSeconds: number;
  seed: string;
  members: SimulatorProfileDto[];
  environment: SimulatorEnvironmentDto;
}

export interface SimulatorRootExpDto {
  stamina: number;
  meridians: number;
  attack: number;
  bone: number;
  agility: number;
  consciousness: number;
}

export interface SimulatorReportItemDto {
  itemId: string;
  variant: string;
  quantity: number;
}

export interface SimulatorCombatUnitStatDto {
  unitKey: string;
  kind: number;
  displayName: string;
  damageDealtPhysical: number;
  damageDealtSpell: number;
  damageTakenPhysical: number;
  damageTakenSpell: number;
  damageMitigatedPhysical: number;
  damageMitigatedSpell: number;
  healingRegen: number;
  healingOther: number;
  damageDealtTrue: number;
  damageTakenTrue: number;
}

export interface SimulatorExpeditionEnemyDto {
  enemyId: string;
  hitpoints: number;
  manapoints: number;
}

export interface SimulatorEventDto {
  atUnixSeconds: number;
  code: string;
  message: string;
  actionId?: string | null;
  itemDeltas?: Record<string, number> | null;
}

export interface SimulatorMemberResultDto {
  memberIndex: number;
  playerId: string;
  displayName: string;
  hitpoints: number;
  maxHitpoints: number;
  manapoints: number;
  maxManapoints: number;
  regenCarrySeconds: number;
  combatSkills: SimulatorCombatSkillsDto;
  combatBuffCarry: SimulatorCombatBuffCarryDto[];
  completedRepeats: number;
  waveNumber: number;
  phase: string;
  experience: number;
  foundation: number;
  combatExperience: number;
  lifetimeExperience: number;
  rootExp: SimulatorRootExpDto;
  cycles: number;
  encounters: number;
  normalKills: number;
  eliteKills: number;
  bossKills: number;
  deaths: number;
  items: SimulatorReportItemDto[];
  itemsLost: SimulatorReportItemDto[];
  assetDeltas: SimulatorAssetDto[];
  remainingConsumableBalances: SimulatorAssetDto[];
  combatAutoUseSlots: SimulatorAutoUseSlotDto[];
  combatStats: SimulatorCombatUnitStatDto[];
  enemies: SimulatorExpeditionEnemyDto[];
  events: SimulatorEventDto[];
  droppedEventCount: number;
}

export interface SimulatorRunResultDto {
  formatVersion: number;
  nodeId: string;
  difficulty: number;
  seed: string;
  requestedDurationSeconds: number;
  simulatedSeconds: number;
  members: SimulatorMemberResultDto[];
}

export interface PlayerAttributes {
  maxHitpoints: number;
  maxManapoints: number;
  physicalAttack: number;
  spellAttack: number;
  armor: number;
  magicResistance: number;
  accuracy: number;
  evasion: number;
  critical: number;
  criticalAvoidance: number;
  breakPower: number;
  parry: number;
  hpRegen: number;
  mpRegen: number;
  attackIntervalSeconds: number;
  damageAbsorptionPoints: number;
  criticalDamagePoints: number;
  threatBonusPoints: number;
  physicalDamageBonusPoints: number;
  spellDamageBonusPoints: number;
  metalDamageBonusPoints: number;
  woodDamageBonusPoints: number;
  waterDamageBonusPoints: number;
  fireDamageBonusPoints: number;
  earthDamageBonusPoints: number;
  totalDamageBonusPoints: number;
  basicAttackBonusPoints: number;
  skillBonusPoints: number;
  physicalDamageReductionPoints: number;
  spellDamageReductionPoints: number;
  metalDamageReductionPoints: number;
  woodDamageReductionPoints: number;
  waterDamageReductionPoints: number;
  fireDamageReductionPoints: number;
  earthDamageReductionPoints: number;
  fixedDamageBonus: number;
  fixedDamageReduction: number;
  fixedPhysicalDamageBonus: number;
  fixedPhysicalDamageReduction: number;
  fixedSpellDamageBonus: number;
  fixedSpellDamageReduction: number;
  attackSpeedPoints: number;
  actionSpeedPoints: number;
  searchSpeedPoints: number;
  cooldownReductionPoints: number;
  dropBonusPoints: number;
  cultivationExpBonusPoints: number;
  combatExpBonusPoints: number;
  resistanceScale: number;
  effects: CombatEffectProfile;
  traits: string[] | null;
  traitLevels: Record<string, number> | null;
}

export interface SimulatorResolvedProfileDto {
  memberIndex: number;
  playerId: string;
  displayName: string;
  attributes: PlayerAttributes;
  combatantStats: CombatantStats;
}

export interface SimulatorResolveResultDto {
  formatVersion: number;
  nodeId: string;
  members: SimulatorResolvedProfileDto[];
}

export interface BuildingEffectSet {
  explorationSpeedRatio: number;
  explorationOutputRatio: number;
  alchemySpeedRatio: number;
  alchemyOutputRatio: number;
  cultivationOutputRatio: number;
  combatHitpointRegen: number;
  combatDamageReductionRatio: number;
  monsterWeakenRatio: number;
  gatheringSpeedRatio: number;
  miningSpeedRatio: number;
  forgingSpeedRatio: number;
  talismanSpeedRatio: number;
  formationSpeedRatio: number;
  enhancementSpeedRatio: number;
  combatManapointRegen: number;
}

export interface ActiveBuffState {
  buffId: string;
  expiresAtUnixSeconds: number;
  stacks: number;
  name: string;
  sourceItemId: string | null;
  effects: BuffEffect[] | null;
  statRatios: Record<string, number> | null;
}

export interface CombatBuffCarryState {
  buffId: string;
  remainingSeconds: number;
  stacks: number;
  name: string;
  missingHitpointsPerStack: number;
  effects: BuffEffect[] | null;
  statRatios: Record<string, number> | null;
}

export interface AutoUseSlotState {
  actionType: string;
  slotIndex: number;
  itemId: string | null;
  quantity: number;
  enabled: boolean;
  remainingDurationSeconds: number;
  cooldownUntilUnixSeconds: number;
  group: string;
}

export interface TriggerConditionJson {
  dependency?: string;
  condition?: string;
  comparator?: string;
  value?: number;
  buffId?: string | null;
}

export type AbilityBuffJson = Record<string, unknown>;

export interface AbilityJson {
  id: string;
  name?: string;
  discipline?: string;
  effectType?: string;
  targetType?: string;
  category?: string;
  element?: string;
  damageRatio?: number;
  flatDamage?: number;
  healingRatio?: number;
  flatHealing?: number;
  castMilliseconds?: number;
  cooldownMilliseconds?: number;
  manaCost?: number;
  threatGain?: number;
  threatPoints?: number;
  rootAttribute?: string;
  rootExpEfficiencyPerLevel?: number;
  rootExpMultipliers?: Record<string, number>;
  cultivationExpBonusPoints?: number;
  passiveEfficiencyPerLevel?: number;
  passive?: Record<string, number>;
  defaultTriggers?: TriggerConditionJson[];
  buff?: Record<string, unknown>;
  buffs?: Record<string, unknown>[];
  [key: string]: unknown;
}

export interface ConsumableStrike {
  manaCostRatio: number;
  manaDamageRatio: number;
  spellAttackRatio: number;
  element: number;
}

export interface ConsumableDefinition {
  usableInBars: Set<string>;
  durationSeconds: number;
  isContinuous: boolean;
  cultivationMultiplier: number;
  gatheringMultiplier: number;
  alchemyMultiplier: number;
  triggers: TriggerConditionJson[] | null;
  durable: boolean;
  strike: ConsumableStrike | null;
  cooldownSeconds: number;
  techniqueSpeedRatio: number;
  enhancementSuccessBonus: number;
  stackableSlotCount: number;
}

export interface ItemDefinition {
  id: string;
  name: string;
  description: string;
  isTradable: boolean;
  sellPrice: number;
  consumable: ConsumableDefinition | null;
  category: string;
  subCategory: string | null;
  displaySubCategory: string;
}

export interface PlayerState {
  playerId: string;
  displayName: string;
  avatar: string;
  lastSimulatedAtTicks: bigint;
  realmLevel: number;
  experience: number;
  foundation: number;
  combatExperience: number;
  lifetimeExperience: number;
  ageSeconds: number;
  completedActions: number;
  normalKills: number;
  eliteKills: number;
  bossKills: number;
  totalDefeats: number;
  defeatedMonsterIds: Set<string>;
  traitIds: string[] | null;
  unlockedTraits: SimulatorTraitProgressDto[] | null;
  traitLevelMap: Record<string, number> | null;
  confirmedLinggen: LinggenValues | null;
  heavenGateEntered: boolean;
  effectiveLinggen: LinggenValues;
  coreAttributes: SimulatorCoreAttributesDto;
  abilitySlots: SimulatorAbilitySlotDto[];
  abilityExperiences: Record<string, number>;
  equipment: SimulatorEquipmentSlotDto[];
  equippedItemIds: string[];
  techniqueExperiences: Record<string, number>;
  abilityTriggers: Record<string, TriggerConditionJson[]>;
  consumableTriggers: Record<string, TriggerConditionJson[]>;
  hitpoints: number;
  maxHitpoints: number;
  manapoints: number;
  maxManapoints: number;
  attackPower: number;
  defense: number;
  regenCarrySeconds: number;
  combatSkills: {
    cooldowns: SimulatorCombatSkillCooldownDto[];
    castingAbilityId: string | null;
    castRemainingSeconds: number;
    castCount: number;
  };
  combatBuffCarry: CombatBuffCarryState[];
  buffs: ActiveBuffState[];
  autoUseSlots: AutoUseSlotState[];
  respawnPolicy: number;
  respawnTargetPlayerId: string | null;
  worldNodeId: string;
  nodeBuildingEffects: BuildingEffectSet;
  loopEnabled: boolean;
  simulationCarrySeconds: number;
  wallet: Map<string, number>; // key = `${itemId}\0${variant}`
}

export const SIMULATOR_FORMAT_VERSION = 1;
export const EMPTY_BUILDING_EFFECTS: BuildingEffectSet = {
  explorationSpeedRatio: 0,
  explorationOutputRatio: 0,
  alchemySpeedRatio: 0,
  alchemyOutputRatio: 0,
  cultivationOutputRatio: 0,
  combatHitpointRegen: 0,
  combatDamageReductionRatio: 0,
  monsterWeakenRatio: 0,
  gatheringSpeedRatio: 0,
  miningSpeedRatio: 0,
  forgingSpeedRatio: 0,
  talismanSpeedRatio: 0,
  formationSpeedRatio: 0,
  enhancementSpeedRatio: 0,
  combatManapointRegen: 0,
};
