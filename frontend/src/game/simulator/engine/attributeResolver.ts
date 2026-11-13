import type { GameDataCatalog } from './catalog.ts';
import { compareOrdinal, roundAwayFromZero, TICKS_PER_MILLISECOND } from './math.ts';
import {
  abilityLevelForExp,
  accumulateTraitModifiers,
  BUFF_CHANNELS,
  BuffEffectStackMode,
  BuffOutOfCombatPanel,
  CombatDamageKind,
  CombatElement,
  createCombatantStats,
  CRITICAL_DAMAGE_BASE_POINTS,
  EMPTY_LINGGEN,
  linggenIsEmpty,
  linggenOf,
  parseBuffEffects,
  resolveEarthCriticalAvoidanceRatio,
  resolveEarthDefenseRatio,
  resolveEarthFixedDamageReduction,
  resolveFireCriticalChanceRatio,
  resolveFireCriticalDamageBonusPoints,
  resolveLinggenDamagePoints,
  resolveMetalAccuracyRatio,
  resolveMetalAttackRatio,
  resolveMetalBreakRatio,
  resolveThreatOnTakenPoints,
  resolveWaterMaxManapointsRatio,
  resolveWaterParryRatio,
  resolveWaterRegenRatio,
  resolveWaterSearchSpeedPoints,
  resolveWoodHealingBonusPoints,
  resolveWoodMaxHitpointsRatio,
  type CombatantStats,
  type CombatEffectProfile,
  type LinggenValues,
} from './rules.ts';
import {
  EMPTY_BUILDING_EFFECTS,
  SIMULATOR_FORMAT_VERSION,
  type ActiveBuffState,
  type AutoUseSlotState,
  type BuildingEffectSet,
  type CombatBuffCarryState,
  type PlayerAttributes,
  type PlayerState,
  type SimulatorActiveBuffDto,
  type SimulatorAutoUseSlotDto,
  type SimulatorBuildingEffectsDto,
  type SimulatorCombatBuffCarryDto,
  type SimulatorEnvironmentNodeDto,
  type SimulatorProfileDto,
  type SimulatorResolvedProfileDto,
  type SimulatorResolveRequestDto,
  type SimulatorResolveResultDto,
} from './types.ts';
import {
  normalizeMemberId,
  SimulatorValidationError,
  validateEnvironment,
  validateMember,
  validateRespawnTargets,
} from './validator.ts';

function softRatio(ratio: number): number {
  return ratio >= 0 ? 1 + ratio : 1 / (1 - ratio);
}

function normalizeEquipmentStatKey(key: string): string {
  if (key === 'breakPower') return 'break';
  if (key === 'parryResolve') return 'parry';
  return key;
}

const ELEMENT_POINT_CHANNELS: ReadonlyArray<{ element: CombatElement; prefix: string }> = [
  { element: CombatElement.Metal, prefix: 'metal' },
  { element: CombatElement.Wood, prefix: 'wood' },
  { element: CombatElement.Water, prefix: 'water' },
  { element: CombatElement.Fire, prefix: 'fire' },
  { element: CombatElement.Earth, prefix: 'earth' },
];

export interface CoreAttributeLevels {
  stamina: number;
  meridians: number;
  attack: number;
  bone: number;
  agility: number;
  consciousness: number;
}

export function resolveCoreLevels(
  core: {
    staminaExp: number;
    meridiansExp: number;
    attackExp: number;
    boneExp: number;
    agilityExp: number;
    consciousnessExp: number;
  },
  catalog: GameDataCatalog,
): CoreAttributeLevels {
  const prog = catalog.realmProgression;
  return {
    stamina: prog.rootLevelFor(core.staminaExp),
    meridians: prog.rootLevelFor(core.meridiansExp),
    attack: prog.rootLevelFor(core.attackExp),
    bone: prog.rootLevelFor(core.boneExp),
    agility: prog.rootLevelFor(core.agilityExp),
    consciousness: prog.rootLevelFor(core.consciousnessExp),
  };
}

export function resolvePlayerAttributesFromState(
  state: PlayerState,
  catalog: GameDataCatalog,
  overrideTraitIds?: string[] | null,
  overrideTraitLevels?: Record<string, number> | null,
): PlayerAttributes {
  const traitIds = overrideTraitIds !== undefined ? overrideTraitIds : state.traitIds;
  const traitLevels =
    overrideTraitLevels !== undefined ? overrideTraitLevels : state.traitLevelMap;
  return resolveAttributesCore(
    state.realmLevel,
    state.equippedItemIds,
    state.equipment,
    state.abilitySlots,
    state.abilityExperiences,
    state.coreAttributes,
    state.buffs,
    catalog,
    state.nodeBuildingEffects,
    traitIds,
    traitLevels,
    state.effectiveLinggen,
  );
}

export function resolveAttributesCore(
  level: number,
  equippedItemIds: readonly string[],
  equipmentSlots: readonly { itemId: string; enhancementLevel: number }[],
  abilitySlots: readonly { abilityId: string | null; level: number }[],
  abilityExperiences: Record<string, number> | null | undefined,
  coreAttributes: {
    staminaExp: number;
    meridiansExp: number;
    attackExp: number;
    boneExp: number;
    agilityExp: number;
    consciousnessExp: number;
  },
  buffs: readonly ActiveBuffState[],
  catalog: GameDataCatalog,
  nodeBuildingEffects: BuildingEffectSet | null,
  traitIds: string[] | null,
  traitLevels: Record<string, number> | null,
  linggen: LinggenValues,
): PlayerAttributes {
  const traits = traitIds && traitIds.length > 0 ? [...traitIds] : null;
  const threatOnTaken = resolveThreatOnTakenPoints(traits, traitLevels);
  const a = catalog.baseAttributes.at(level);
  const flat = new Map<string, number>();
  const ratios = new Map<string, number>();
  const points = new Map<string, number>();

  const P = (key: string) => points.get(key) ?? 0;
  const addPoint = (key: string, value: number) => {
    if (value !== 0) points.set(key, P(key) + value);
  };
  const F = (key: string) => flat.get(key) ?? 0;
  const R = (key: string) => ratios.get(key) ?? 0;

  accumulateTraitModifiers(traits, traitLevels, ratios, points);

  if (!linggenIsEmpty(linggen)) {
    for (const { element, prefix } of ELEMENT_POINT_CHANNELS) {
      const lp = linggenOf(linggen, element);
      if (lp <= 0) continue;
      addPoint(`${prefix}DamageBonusPoints`, resolveLinggenDamagePoints(lp));
      addPoint(`${prefix}DamageReductionPoints`, resolveLinggenDamagePoints(lp));
    }

    const fire = linggen.fire;
    if (fire > 0) {
      ratios.set(
        'criticalChance',
        R('criticalChance') + resolveFireCriticalChanceRatio(fire),
      );
      addPoint('criticalDamageBonusPoints', resolveFireCriticalDamageBonusPoints(fire));
    }

    const water = linggen.water;
    if (water > 0) {
      ratios.set('maxManapoints', R('maxManapoints') + resolveWaterMaxManapointsRatio(water));
      ratios.set('parry', R('parry') + resolveWaterParryRatio(water));
      const waterRegen = resolveWaterRegenRatio(water);
      ratios.set('hitpointRegen', R('hitpointRegen') + waterRegen);
      ratios.set('manapointRegen', R('manapointRegen') + waterRegen);
      addPoint('searchSpeedPoints', resolveWaterSearchSpeedPoints(water));
    }

    const wood = linggen.wood;
    if (wood > 0) {
      ratios.set('maxHitpoints', R('maxHitpoints') + resolveWoodMaxHitpointsRatio(wood));
      addPoint('healingBonusPoints', resolveWoodHealingBonusPoints(wood));
    }

    const metal = linggen.metal;
    if (metal > 0) {
      const metalAtk = resolveMetalAttackRatio(metal);
      ratios.set('physicalAttack', R('physicalAttack') + metalAtk);
      ratios.set('spellAttack', R('spellAttack') + metalAtk);
      ratios.set('break', R('break') + resolveMetalBreakRatio(metal));
      ratios.set('accuracy', R('accuracy') + resolveMetalAccuracyRatio(metal));
    }

    const earth = linggen.earth;
    if (earth > 0) {
      const earthDef = resolveEarthDefenseRatio(earth);
      ratios.set('physicalArmor', R('physicalArmor') + earthDef);
      ratios.set('spellResistance', R('spellResistance') + earthDef);
      ratios.set(
        'criticalAvoidance',
        R('criticalAvoidance') + resolveEarthCriticalAvoidanceRatio(earth),
      );
      flat.set(
        'fixedDamageReduction',
        F('fixedDamageReduction') + resolveEarthFixedDamageReduction(earth),
      );
    }
  }

  for (const eqId of equippedItemIds) {
    const item = catalog.rawEquipments.get(eqId);
    if (!item) continue;
    let enhancement = 0;
    for (const slot of equipmentSlots) {
      if (slot.itemId === eqId) {
        enhancement = slot.enhancementLevel;
        break;
      }
    }
    const enhancementScale = Math.pow(1.05, enhancement);
    const flatProps = item.flatStats as Record<string, unknown> | undefined;
    if (flatProps && typeof flatProps === 'object') {
      for (const [rawKey, rawVal] of Object.entries(flatProps)) {
        const numberVal = Number(rawVal);
        const statName = normalizeEquipmentStatKey(rawKey);
        if (statName.endsWith('Points')) {
          points.set(statName, P(statName) + numberVal * enhancementScale);
        } else {
          flat.set(statName, F(statName) + numberVal * enhancementScale);
        }
      }
    }
    const baseRatios = item.baseStatRatios as Record<string, unknown> | undefined;
    if (baseRatios && typeof baseRatios === 'object') {
      for (const [rawKey, rawVal] of Object.entries(baseRatios)) {
        const numberVal = Number(rawVal);
        const statName = normalizeEquipmentStatKey(rawKey);
        ratios.set(statName, R(statName) + numberVal * enhancementScale);
      }
    }
  }

  // 内功常驻面板被动
  if (abilitySlots.length > 0 && abilitySlots[0].abilityId) {
    const internalId = abilitySlots[0].abilityId;
    const internalAbility = catalog.abilities.get(internalId);
    if (
      internalAbility &&
      internalAbility.discipline === 'internal' &&
      internalAbility.passive &&
      typeof internalAbility.passive === 'object'
    ) {
      let internalLevel = Math.max(1, abilitySlots[0].level);
      const exp = abilityExperiences?.[internalId];
      if (exp !== undefined && exp > 0) {
        const expLevel = abilityLevelForExp(exp);
        if (expLevel > internalLevel) internalLevel = expLevel;
      }
      let effPerLevel =
        typeof internalAbility.passiveEfficiencyPerLevel === 'number'
          ? internalAbility.passiveEfficiencyPerLevel
          : 0;
      if (effPerLevel <= 0) effPerLevel = 0.05;
      const passiveScale = Math.max(0, 1.0 + effPerLevel * (internalLevel - 1));
      for (const [k, rawVal] of Object.entries(internalAbility.passive)) {
        const numberVal = Number(rawVal);
        if (!Number.isFinite(numberVal)) continue;
        const scaledNumber = numberVal * passiveScale;
        if (k.endsWith('Points')) {
          points.set(k, P(k) + scaledNumber);
        } else {
          flat.set(k, F(k) + scaledNumber);
        }
      }
    }
  }

  const root = resolveCoreLevels(coreAttributes, catalog);

  const rootFlat = (key: string): number => {
    switch (key) {
      case 'maxHitpoints':
        return (root.stamina + root.bone) * 12;
      case 'maxManapoints':
        return (root.meridians + root.bone) * 7;
      case 'physicalArmor':
        return root.stamina;
      case 'hitpointRegen':
        return root.stamina * 0.5;
      case 'spellResistance':
        return root.meridians;
      case 'physicalAttack':
      case 'criticalChance':
      case 'break':
        return root.attack;
      case 'evasion':
      case 'criticalAvoidance':
        return root.agility;
      case 'parry':
        return root.bone + root.agility;
      case 'spellAttack':
        return root.consciousness * 2;
      case 'accuracy':
        return root.consciousness;
      default:
        return 0;
    }
  };

  const rootRatio = (key: string): number => {
    switch (key) {
      case 'maxHitpoints':
        return (root.stamina + root.bone) * 0.01;
      case 'maxManapoints':
        return (root.meridians + root.bone) * 0.01;
      case 'physicalArmor':
      case 'hitpointRegen':
        return root.stamina * 0.01;
      case 'spellResistance':
        return root.meridians * 0.01;
      case 'manapointRegen':
        return root.meridians * 0.02;
      case 'physicalAttack':
      case 'criticalChance':
      case 'break':
        return root.attack * 0.01;
      case 'evasion':
      case 'criticalAvoidance':
        return root.agility * 0.01;
      case 'parry':
        return (root.bone + root.agility) * 0.01;
      case 'spellAttack':
        return root.consciousness * 0.02;
      case 'accuracy':
        return root.consciousness * 0.01;
      default:
        return 0;
    }
  };

  const buffRatios = new Map<string, number>();
  let hpRegenFlat = 0;
  let mpRegenFlat = 0;
  let hpRegenPost = 0;
  let mpRegenPost = 0;

  if (nodeBuildingEffects) {
    hpRegenFlat += nodeBuildingEffects.combatHitpointRegen;
    mpRegenFlat += nodeBuildingEffects.combatManapointRegen;
    addPoint('damageAbsorptionPoints', nodeBuildingEffects.combatDamageReductionRatio * 100);
  }

  for (const buff of buffs) {
    if (buff.effects) {
      for (const effect of buff.effects) {
        const info = BUFF_CHANNELS[effect.channel];
        if (!info) continue;
        const value =
          info.stackMode === BuffEffectStackMode.PerStack
            ? effect.value * buff.stacks
            : effect.value;
        switch (info.outOfCombatPanel) {
          case BuffOutOfCombatPanel.HpRegenFlat:
            hpRegenFlat += value;
            break;
          case BuffOutOfCombatPanel.MpRegenFlat:
            mpRegenFlat += value;
            break;
          case BuffOutOfCombatPanel.HpRegenPost:
            hpRegenPost += value;
            break;
          case BuffOutOfCombatPanel.MpRegenPost:
            mpRegenPost += value;
            break;
          case BuffOutOfCombatPanel.AttackSpeedPoints:
            addPoint('attackSpeedPoints', value);
            break;
          case BuffOutOfCombatPanel.DamageAbsorptionPoints:
            addPoint('damageAbsorptionPoints', value);
            break;
        }
      }
    }
    if (buff.statRatios) {
      for (const [k, v] of Object.entries(buff.statRatios)) {
        buffRatios.set(k, (buffRatios.get(k) ?? 0) + v * buff.stacks);
      }
    }
  }

  const stat = (key: string, value: number): number =>
    ((value + rootFlat(key)) * (1 + rootRatio(key)) + F(key)) *
    softRatio(R(key)) *
    softRatio(buffRatios.get(key) ?? 0);

  const hpRegen = (stat('hitpointRegen', a.hpRegen) + hpRegenFlat) * (1 + hpRegenPost);
  const mpRegen = (stat('manapointRegen', a.mpRegen) + mpRegenFlat) * (1 + mpRegenPost);
  const tier = catalog.baseAttributes.tierAt(level);
  addPoint('criticalDamageBonusPoints', CRITICAL_DAMAGE_BASE_POINTS);

  const physical = stat('physicalAttack', a.ad);
  const spell = stat('spellAttack', a.ap);
  const interval = 1;

  const effects: CombatEffectProfile = {
    hpDrainPoints: P('hpDrainPoints'),
    mpDrainPoints: P('mpDrainPoints'),
    damageReflectPoints: P('damageReflectPoints'),
    healingBonusPoints: P('healingBonusPoints'),
    threatOnTakenPoints: threatOnTaken !== 0 ? threatOnTaken : P('threatOnTakenPoints'),
  };
  // 注意 PlayerAttributeResolver.cs:483 传 P("threatOnTakenPoints"), 而在 Traits 里面 AccumulateModifiers 已经把所有除不缩放外的结果加到 points；Wait: check CombatTraitRules.cs! Wait — ThreatOnTakenPoints IS added to points? No! Wait line 214 computes `threatOnTaken`, line 483 passes `P("threatOnTakenPoints")`! Let's verify in parity test against `resolveCase`!
  effects.threatOnTakenPoints = P('threatOnTakenPoints');

  return {
    maxHitpoints: stat('maxHitpoints', a.hp),
    maxManapoints: stat('maxManapoints', a.mp),
    physicalAttack: physical,
    spellAttack: spell,
    armor: stat('physicalArmor', a.armor),
    magicResistance: stat('spellResistance', a.mr),
    accuracy: stat('accuracy', a.acc),
    evasion: stat('evasion', a.eva),
    critical: stat('criticalChance', a.crit),
    criticalAvoidance: stat('criticalAvoidance', a.cavoid),
    breakPower: stat('break', a.breakPt),
    parry: stat('parry', a.parry),
    hpRegen,
    mpRegen,
    attackIntervalSeconds: interval,
    damageAbsorptionPoints: Math.max(0, P('damageAbsorptionPoints')),
    criticalDamagePoints: P('criticalDamageBonusPoints'),
    threatBonusPoints: P('threatBonusPoints'),
    physicalDamageBonusPoints: P('physicalDamageBonusPoints'),
    spellDamageBonusPoints: P('spellDamageBonusPoints'),
    metalDamageBonusPoints: P('metalDamageBonusPoints'),
    woodDamageBonusPoints: P('woodDamageBonusPoints'),
    waterDamageBonusPoints: P('waterDamageBonusPoints'),
    fireDamageBonusPoints: P('fireDamageBonusPoints'),
    earthDamageBonusPoints: P('earthDamageBonusPoints'),
    totalDamageBonusPoints: P('totalDamageBonusPoints'),
    basicAttackBonusPoints: P('basicAttackBonusPoints'),
    skillBonusPoints: P('skillBonusPoints'),
    physicalDamageReductionPoints: P('physicalDamageReductionPoints'),
    spellDamageReductionPoints: P('spellDamageReductionPoints'),
    metalDamageReductionPoints: P('metalDamageReductionPoints'),
    woodDamageReductionPoints: P('woodDamageReductionPoints'),
    waterDamageReductionPoints: P('waterDamageReductionPoints'),
    fireDamageReductionPoints: P('fireDamageReductionPoints'),
    earthDamageReductionPoints: P('earthDamageReductionPoints'),
    fixedDamageBonus: F('fixedDamageBonus') + P('fixedDamageBonus'),
    fixedDamageReduction: F('fixedDamageReduction') + P('fixedDamageReduction'),
    fixedPhysicalDamageBonus: F('fixedPhysicalDamageBonus') + P('fixedPhysicalDamageBonus'),
    fixedPhysicalDamageReduction:
      F('fixedPhysicalDamageReduction') + P('fixedPhysicalDamageReduction'),
    fixedSpellDamageBonus: F('fixedSpellDamageBonus') + P('fixedSpellDamageBonus'),
    fixedSpellDamageReduction: F('fixedSpellDamageReduction') + P('fixedSpellDamageReduction'),
    attackSpeedPoints: P('attackSpeedPoints'),
    actionSpeedPoints: P('actionSpeedPoints'),
    searchSpeedPoints: P('actionSpeedPoints') + P('searchSpeedPoints'),
    cooldownReductionPoints: P('cooldownReductionPoints'),
    dropBonusPoints: P('dropBonusPoints'),
    cultivationExpBonusPoints: P('cultivationExpBonusPoints'),
    combatExpBonusPoints: P('combatExpBonusPoints'),
    resistanceScale: tier,
    effects,
    traits,
    traitLevels: traitLevels ?? null,
  };
}

export function playerAttributesToCombatantStats(
  attrs: PlayerAttributes,
  combatExperience: number,
): CombatantStats {
  return createCombatantStats(
    attrs.maxHitpoints,
    attrs.physicalAttack,
    attrs.armor,
    attrs.attackIntervalSeconds,
    attrs.accuracy,
    attrs.evasion,
    CombatDamageKind.Physical,
    {
      spellAttack: attrs.spellAttack,
      maxManapoints: attrs.maxManapoints,
      hpRegen: attrs.hpRegen,
      mpRegen: attrs.mpRegen,
      damageAbsorptionPoints: attrs.damageAbsorptionPoints,
      threatBonusPoints: attrs.threatBonusPoints,
      magicResistance: attrs.magicResistance,
      critical: attrs.critical,
      criticalResistance: attrs.criticalAvoidance,
      criticalDamagePoints: attrs.criticalDamagePoints,
      breakPower: attrs.breakPower,
      parryResolve: attrs.parry,
      resistanceScale: attrs.resistanceScale,
      physicalDamageBonusPoints: attrs.physicalDamageBonusPoints,
      spellDamageBonusPoints: attrs.spellDamageBonusPoints,
      metalDamageBonusPoints: attrs.metalDamageBonusPoints,
      woodDamageBonusPoints: attrs.woodDamageBonusPoints,
      waterDamageBonusPoints: attrs.waterDamageBonusPoints,
      fireDamageBonusPoints: attrs.fireDamageBonusPoints,
      earthDamageBonusPoints: attrs.earthDamageBonusPoints,
      totalDamageBonusPoints: attrs.totalDamageBonusPoints,
      basicAttackBonusPoints: attrs.basicAttackBonusPoints,
      skillBonusPoints: attrs.skillBonusPoints,
      physicalDamageReductionPoints: attrs.physicalDamageReductionPoints,
      spellDamageReductionPoints: attrs.spellDamageReductionPoints,
      metalDamageReductionPoints: attrs.metalDamageReductionPoints,
      woodDamageReductionPoints: attrs.woodDamageReductionPoints,
      waterDamageReductionPoints: attrs.waterDamageReductionPoints,
      fireDamageReductionPoints: attrs.fireDamageReductionPoints,
      earthDamageReductionPoints: attrs.earthDamageReductionPoints,
      fixedDamageBonus: attrs.fixedDamageBonus,
      fixedDamageReduction: attrs.fixedDamageReduction,
      fixedPhysicalDamageBonus: attrs.fixedPhysicalDamageBonus,
      fixedPhysicalDamageReduction: attrs.fixedPhysicalDamageReduction,
      fixedSpellDamageBonus: attrs.fixedSpellDamageBonus,
      fixedSpellDamageReduction: attrs.fixedSpellDamageReduction,
      attackSpeedPoints: attrs.attackSpeedPoints,
      actionSpeedPoints: attrs.actionSpeedPoints,
      cooldownReductionPoints: attrs.cooldownReductionPoints,
      effects: { ...attrs.effects },
      traits: attrs.traits ? [...attrs.traits] : null,
      traitLevels: attrs.traitLevels ? { ...attrs.traitLevels } : null,
      combatExperience,
    },
  );
}

export function buildingEffectsFromDto(
  dto: SimulatorBuildingEffectsDto | undefined | null,
): BuildingEffectSet {
  if (!dto) return { ...EMPTY_BUILDING_EFFECTS };
  return {
    explorationSpeedRatio: dto.explorationSpeedRatio ?? 0,
    explorationOutputRatio: dto.explorationOutputRatio ?? 0,
    alchemySpeedRatio: dto.alchemySpeedRatio ?? 0,
    alchemyOutputRatio: dto.alchemyOutputRatio ?? 0,
    cultivationOutputRatio: dto.cultivationOutputRatio ?? 0,
    combatHitpointRegen: dto.combatHitpointRegen ?? 0,
    combatDamageReductionRatio: dto.combatDamageReductionRatio ?? 0,
    monsterWeakenRatio: dto.monsterWeakenRatio ?? 0,
    gatheringSpeedRatio: dto.gatheringSpeedRatio ?? 0,
    miningSpeedRatio: dto.miningSpeedRatio ?? 0,
    forgingSpeedRatio: dto.forgingSpeedRatio ?? 0,
    talismanSpeedRatio: dto.talismanSpeedRatio ?? 0,
    formationSpeedRatio: dto.formationSpeedRatio ?? 0,
    enhancementSpeedRatio: dto.enhancementSpeedRatio ?? 0,
    combatManapointRegen: dto.combatManapointRegen ?? 0,
  };
}

export function createDefaultProfile(catalog: GameDataCatalog): SimulatorProfileDto {
  return {
    formatVersion: SIMULATOR_FORMAT_VERSION,
    gameDataFingerprint: catalog.fingerprint,
    sourcePlayerId: null,
    displayName: '自定义角色',
    avatar: 'player',
    capturedAtUnixSeconds: 0,
    realmLevel: 1,
    experience: 0,
    foundation: 0,
    combatExperience: 0,
    lifetimeExperience: 0,
    ageSeconds: 0,
    completedActions: 0,
    traitIds: null,
    unlockedTraits: null,
    confirmedLinggen: null,
    heavenGateEntered: false,
    coreAttributes: {
      staminaExp: 0,
      meridiansExp: 0,
      attackExp: 0,
      boneExp: 0,
      agilityExp: 0,
      consciousnessExp: 0,
    },
    abilitySlots: [],
    abilityExperiences: null,
    equipment: [],
    techniqueExperiences: null,
    abilityTriggers: null,
    consumableTriggers: null,
    hitpoints: 100,
    maxHitpoints: 100,
    manapoints: 100,
    maxManapoints: 100,
    regenCarrySeconds: 0,
    combatSkills: {
      cooldowns: [],
      castingAbilityId: null,
      castRemainingSeconds: 0,
      castCount: 0,
    },
    combatBuffCarry: [],
    buffs: [],
    combatAutoUseSlots: [],
    assets: [],
    respawnPolicy: 0,
    respawnTargetPlayerId: null,
  };
}

function activeBuffFromDto(
  dto: SimulatorActiveBuffDto,
  sourceAtUnixSeconds: number,
  targetAtUnixSeconds: number,
): ActiveBuffState {
  const remaining = dto.expiresAtUnixSeconds - sourceAtUnixSeconds;
  return {
    buffId: dto.buffId,
    expiresAtUnixSeconds: targetAtUnixSeconds + Math.max(0, remaining),
    stacks: dto.stacks,
    name: dto.name,
    sourceItemId: dto.sourceItemId,
    effects: dto.effects ? dto.effects.map((e) => ({ channel: e.channel, value: e.value })) : null,
    statRatios: dto.statRatios ? { ...dto.statRatios } : null,
  };
}

function combatBuffCarryFromDto(dto: SimulatorCombatBuffCarryDto): CombatBuffCarryState {
  return {
    buffId: dto.buffId,
    remainingSeconds: dto.remainingSeconds,
    stacks: dto.stacks,
    name: dto.name,
    missingHitpointsPerStack: dto.missingHitpointsPerStack,
    effects: dto.effects ? dto.effects.map((e) => ({ channel: e.channel, value: e.value })) : null,
    statRatios: dto.statRatios ? { ...dto.statRatios } : null,
  };
}

function autoUseSlotFromDto(
  dto: SimulatorAutoUseSlotDto,
  sourceAtUnixSeconds: number,
  targetAtUnixSeconds: number,
): AutoUseSlotState {
  const remainingCooldown = dto.cooldownUntilUnixSeconds - sourceAtUnixSeconds;
  return {
    actionType: dto.actionType,
    slotIndex: dto.slotIndex,
    itemId: dto.itemId,
    quantity: dto.quantity,
    enabled: dto.enabled,
    remainingDurationSeconds: dto.remainingDurationSeconds,
    cooldownUntilUnixSeconds: targetAtUnixSeconds + Math.max(0, remainingCooldown),
    group: dto.group,
  };
}

export function expireBuffs(state: PlayerState, atUnixSeconds: number): boolean {
  let changed = false;
  for (let i = state.buffs.length - 1; i >= 0; i--) {
    if (state.buffs[i].expiresAtUnixSeconds <= atUnixSeconds) {
      state.buffs.splice(i, 1);
      changed = true;
    }
  }
  return changed;
}

export function syncDurableConsumableBuffs(
  state: PlayerState,
  catalog: GameDataCatalog,
  actionBarId: string | null,
): boolean {
  const slots = state.autoUseSlots;
  const activeBuffs = state.buffs;
  let managed = false;
  for (let i = 0; i < activeBuffs.length; i++) {
    const exp = activeBuffs[i].expiresAtUnixSeconds;
    if (!Number.isFinite(exp) || exp >= Number.MAX_VALUE) {
      managed = true;
      break;
    }
  }
  for (let i = 0; !managed && i < slots.length; i++) {
    const id = slots[i].itemId;
    if (id && catalog.items.get(id)?.consumable?.durable) {
      managed = true;
    }
  }
  if (!managed) return false;

  const desired: ActiveBuffState[] = [];
  for (let i = 0; i < slots.length; i++) {
    const slot = slots[i];
    if (!slot.itemId) continue;
    const item = catalog.items.get(slot.itemId);
    if (!item || !item.consumable || !item.consumable.durable) continue;
    if (
      actionBarId === null ||
      !slot.enabled ||
      !item.consumable.usableInBars.has(actionBarId) ||
      slot.actionType.toLowerCase() !== actionBarId.toLowerCase() ||
      (state.wallet.get(`${item.id}\0`) ?? 0) < 1
    ) {
      continue;
    }
    const rawItem = catalog.rawItems.get(item.id);
    const rawCons = rawItem?.consumable as Record<string, unknown> | undefined;
    const rawBuffs = rawCons && Array.isArray(rawCons.buffs) ? rawCons.buffs : null;
    if (!rawBuffs) continue;
    for (const buff of rawBuffs as Record<string, unknown>[]) {
      const buffId = String(buff.buffId);
      const name = typeof buff.name === 'string' ? buff.name : item.name;
      desired.push({
        buffId,
        expiresAtUnixSeconds: Number.MAX_VALUE,
        stacks: 1,
        name,
        sourceItemId: item.id,
        effects: parseBuffEffects(buff, 1.0),
        statRatios: null,
      });
    }
  }

  let changed = false;
  for (let i = activeBuffs.length - 1; i >= 0; i--) {
    const cur = activeBuffs[i];
    if (Number.isFinite(cur.expiresAtUnixSeconds) && cur.expiresAtUnixSeconds < Number.MAX_VALUE) {
      continue;
    }
    let kept = false;
    for (let j = 0; j < desired.length; j++) {
      if (desired[j].buffId === cur.buffId && desired[j].sourceItemId === cur.sourceItemId) {
        kept = true;
        break;
      }
    }
    if (!kept) {
      activeBuffs.splice(i, 1);
      changed = true;
    }
  }
  for (const want of desired) {
    let existingIdx = -1;
    for (let i = 0; i < activeBuffs.length; i++) {
      const cur = activeBuffs[i];
      if (
        cur.buffId === want.buffId &&
        cur.sourceItemId === want.sourceItemId &&
        (!Number.isFinite(cur.expiresAtUnixSeconds) || cur.expiresAtUnixSeconds >= Number.MAX_VALUE)
      ) {
        existingIdx = i;
        break;
      }
    }
    if (existingIdx < 0) {
      activeBuffs.push(want);
      changed = true;
    }
  }
  return changed;
}

export function applyResourceAttributes(
  state: PlayerState,
  catalog: GameDataCatalog,
): PlayerAttributes {
  const attrs = resolvePlayerAttributesFromState(state, catalog);
  state.maxHitpoints = attrs.maxHitpoints;
  state.maxManapoints = attrs.maxManapoints;
  state.attackPower = attrs.physicalAttack;
  state.defense = attrs.armor;
  if (state.hitpoints > state.maxHitpoints) state.hitpoints = state.maxHitpoints;
  if (state.manapoints > state.maxManapoints) state.manapoints = state.maxManapoints;
  return attrs;
}

export function restoreToState(
  profile: SimulatorProfileDto,
  memberIndex: number,
  targetNodeId: string,
  envNode: SimulatorEnvironmentNodeDto,
  catalog: GameDataCatalog,
  startAtUnixSeconds: number,
  applyInitialPrepareBoundary = true,
): PlayerState {
  const playerId = normalizeMemberId(profile, memberIndex);
  const startTicks = BigInt(roundAwayFromZero(startAtUnixSeconds * 1000)) * TICKS_PER_MILLISECOND;
  const startUnixMs = Number(startTicks / TICKS_PER_MILLISECOND) / 1000;

  const confirmedLinggen: LinggenValues | null = profile.confirmedLinggen
    ? { ...profile.confirmedLinggen }
    : null;
  const effectiveLinggen: LinggenValues =
    profile.heavenGateEntered && confirmedLinggen ? confirmedLinggen : EMPTY_LINGGEN;

  let traitLevelMap: Record<string, number> | null = null;
  if (profile.unlockedTraits) {
    traitLevelMap = {};
    for (const tr of profile.unlockedTraits) {
      traitLevelMap[tr.id] = tr.level;
    }
  }

  const wallet = new Map<string, number>();
  for (const asset of profile.assets) {
    const key = `${asset.itemId}\0${asset.variant ?? ''}`;
    wallet.set(key, (wallet.get(key) ?? 0) + asset.quantity);
  }

  const state: PlayerState = {
    playerId,
    displayName:
      profile.displayName && profile.displayName.trim().length > 0
        ? profile.displayName
        : playerId.replace(/-/g, '').slice(0, 8),
    avatar: profile.avatar && profile.avatar.trim().length > 0 ? profile.avatar : 'player',
    lastSimulatedAtTicks: startTicks,
    realmLevel: profile.realmLevel,
    experience: profile.experience,
    foundation: profile.foundation,
    combatExperience: profile.combatExperience,
    lifetimeExperience: profile.lifetimeExperience,
    ageSeconds: profile.ageSeconds,
    completedActions: profile.completedActions,
    normalKills: 0,
    eliteKills: 0,
    bossKills: 0,
    totalDefeats: 0,
    defeatedMonsterIds: new Set<string>(),
    traitIds: profile.traitIds ? [...profile.traitIds] : null,
    unlockedTraits: profile.unlockedTraits
      ? profile.unlockedTraits.map((t) => ({ ...t }))
      : null,
    traitLevelMap,
    confirmedLinggen,
    heavenGateEntered: profile.heavenGateEntered,
    effectiveLinggen,
    coreAttributes: { ...profile.coreAttributes },
    abilitySlots: profile.abilitySlots.map((s) => {
      const exp = s.abilityId ? profile.abilityExperiences?.[s.abilityId] : undefined;
      if (s.abilityId && exp !== undefined && exp > 0) {
        return { abilityId: s.abilityId, level: abilityLevelForExp(exp) };
      }
      return { ...s };
    }),
    abilityExperiences: profile.abilityExperiences ? { ...profile.abilityExperiences } : {},
    equipment: profile.equipment.map((e) => ({ ...e })),
    equippedItemIds: profile.equipment.map((e) => e.itemId),
    techniqueExperiences: profile.techniqueExperiences ? { ...profile.techniqueExperiences } : {},
    abilityTriggers: profile.abilityTriggers
      ? Object.fromEntries(
          Object.entries(profile.abilityTriggers).map(([k, v]) => [k, structuredClone(v) as never]),
        )
      : {},
    consumableTriggers: profile.consumableTriggers
      ? Object.fromEntries(
          Object.entries(profile.consumableTriggers).map(([k, v]) => [k, structuredClone(v) as never]),
        )
      : {},
    hitpoints: profile.hitpoints,
    maxHitpoints: profile.maxHitpoints,
    manapoints: profile.manapoints,
    maxManapoints: profile.maxManapoints,
    attackPower: 0,
    defense: 0,
    regenCarrySeconds: profile.regenCarrySeconds,
    combatSkills: {
      cooldowns: profile.combatSkills.cooldowns.map((c) => ({ ...c })),
      castingAbilityId: profile.combatSkills.castingAbilityId,
      castRemainingSeconds: profile.combatSkills.castRemainingSeconds,
      castCount: profile.combatSkills.castCount,
    },
    combatBuffCarry: profile.combatBuffCarry.map(combatBuffCarryFromDto),
    buffs: profile.buffs.map((b) =>
      activeBuffFromDto(b, profile.capturedAtUnixSeconds, startAtUnixSeconds),
    ),
    autoUseSlots: profile.combatAutoUseSlots.map((s) =>
      autoUseSlotFromDto(s, profile.capturedAtUnixSeconds, startAtUnixSeconds),
    ),
    respawnPolicy: profile.respawnPolicy,
    respawnTargetPlayerId: profile.respawnTargetPlayerId
      ? tryParseGuidOrNull(profile.respawnTargetPlayerId)
      : null,
    worldNodeId: targetNodeId,
    nodeBuildingEffects: buildingEffectsFromDto(envNode.effects),
    loopEnabled: false,
    simulationCarrySeconds: 0,
    wallet,
  };

  if (applyInitialPrepareBoundary) {
    // 复刻 SimulatorProfileMapper.RestoreToState：在空行动队列状态下执行 PrepareResourceBoundary
    expireBuffs(state, startUnixMs);
    syncDurableConsumableBuffs(state, catalog, null);
    applyResourceAttributes(state, catalog);
  }
  return state;
}

function tryParseGuidOrNull(text: string): string | null {
  const m = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.exec(text.trim());
  return m ? text.trim().toLowerCase() : null;
}

export function resolveProfiles(
  request: SimulatorResolveRequestDto,
  catalog: GameDataCatalog,
): SimulatorResolvedProfileDto[] {
  if (
    request.formatVersion !== SIMULATOR_FORMAT_VERSION ||
    request.environment.formatVersion !== SIMULATOR_FORMAT_VERSION
  ) {
    throw new SimulatorValidationError('预演契约版本不匹配。');
  }
  if (request.environment.gameDataFingerprint !== catalog.fingerprint) {
    throw new SimulatorValidationError('环境快照配表指纹与当前配表不一致。');
  }
  if (!request.members || request.members.length < 1 || request.members.length > 5) {
    throw new SimulatorValidationError('推演成员数必须位于 1~5。');
  }
  const memberIds: string[] = [];
  for (let i = 0; i < request.members.length; i++) {
    const member = request.members[i];
    if (!member || member.formatVersion !== SIMULATOR_FORMAT_VERSION) {
      throw new SimulatorValidationError('角色档案契约版本不匹配。');
    }
    if (member.gameDataFingerprint !== catalog.fingerprint) {
      throw new SimulatorValidationError('角色档案配表指纹与当前配表不一致。');
    }
    const id = normalizeMemberId(member, i);
    if (memberIds.includes(id)) {
      throw new SimulatorValidationError('推演成员包含重复角色。');
    }
    memberIds.push(id);
    validateMember(member, i, catalog);
  }
  validateRespawnTargets(request.members, memberIds);
  validateEnvironment(request.environment, catalog);

  const worldNode = catalog.worldNodes.get(request.nodeId);
  if (!worldNode || worldNode.encounterIds.length === 0) {
    throw new SimulatorValidationError('目标节点不存在或没有讨伐遭遇池。');
  }
  const envNode = request.environment.nodes.find((n) => compareOrdinal(n.nodeId, request.nodeId) === 0);
  if (!envNode) {
    throw new SimulatorValidationError('环境快照缺少目标节点条目，拒绝推演。');
  }

  return request.members.map((member, index) => {
    const state = restoreToState(
      member,
      index,
      request.nodeId,
      envNode,
      catalog,
      request.environment.capturedAtUnixSeconds,
    );
    const attributes = resolvePlayerAttributesFromState(state, catalog);
    const combatantStats = playerAttributesToCombatantStats(attributes, state.combatExperience);
    return {
      memberIndex: index,
      playerId: state.playerId,
      displayName: state.displayName,
      attributes,
      combatantStats,
    };
  });
}

export function resolveProfilesResult(
  request: SimulatorResolveRequestDto,
  catalog: GameDataCatalog,
): SimulatorResolveResultDto {
  return {
    formatVersion: SIMULATOR_FORMAT_VERSION,
    nodeId: request.nodeId,
    members: resolveProfiles(request, catalog),
  };
}
