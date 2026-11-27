// 确定性战斗推进、出手决策、伤害结算、特质与触发器引擎
// 严格对应 DaoHua.Ecs.Combat.DeterministicCombatEngine / CombatActSystem / CombatPhaseSystems / CombatEventAdvanceSystem / CombatTriggerMath

import { deterministicRollNext } from './deterministicRoll.ts'
import {
  CombatEncounterState,
  CombatOutcome,
  CombatSide,
  CombatantState,
  CombatUnitStatsBook,
  type AllyMark,
  type CombatAdvanceResult,
  type CombatBuffState,
  type CombatEvent,
  type CombatHitContext,
} from './combatModels.ts'
import type { SkillSlotDefinition } from './catalog.ts'
import {
  BuffEffectChannel,
  BuffIds,
  CombatDamageKind,
  CombatElement,
  PercentCurveMode,
  TraitIds,
  canTalismanCrit,
  containsTrait,
  mergeBuffExpiry,
  parseBuffEffects,
  parseCombatElement,
  percentMultiplier,
  resolveDamageElement,
  shanshenbuCooldownSeconds,
  talismanCooldownSpeedPoints,
  talismanManaCostMultiplier,
  traitLevelScale,
  xueqihuadunConversionRatio,
  type CombatantStats,
} from './rules.ts'
import { evaluateTriggers } from './triggerEvaluator.ts'
import {
  CombatFactTracker,
  EMPTY_WAKE_MASK,
  FULL_FACT_DELTA,
  FULL_WAKE_MASK,
  UnitChannel,
  foldPendingWake,
  maskIntersects,
  resolveTriggerListWakeMask,
  type CombatFactDelta,
  type SlotWakeMask,
} from './triggerWake.ts'
import {
  SimultaneousCombatWave,
  limitDamageForHaste,
} from './simultaneousWave.ts'
import type { AbilityBuffJson, AbilityJson, ConsumableDefinition, ConsumableStrike } from './types.ts'

export const MAX_EVENTS_PER_ADVANCE = 100_000
const SKILL_SIDE_OFFSET = 0x1000n
const ALLY_SIDE_BASE = 0x400n
const SYMMETRIC_SIDE_A_OFFSET = 0x2000n
const SYMMETRIC_SIDE_B_OFFSET = 0x3000n
const SYMMETRIC_RESOLVE_TARGET_CHANNEL = 18n
const SYMMETRIC_DECIDE_TARGET_CHANNEL = 19n
const BUFF_TICK_SECONDS = 5
const THREAT_TICK_SECONDS = 1

export function allySide(memberIndex: number): bigint {
  return memberIndex === 0 ? 0n : ALLY_SIDE_BASE + BigInt(memberIndex)
}

/** 对称模式随机流分组标识：按 seed 最低位在 SideA/SideB 间公平翻转，消除固定侧位偏移。 */
export function symmetricUnitSide(seed: bigint, side: CombatSide, sideIndex: number): bigint {
  const flipped = (seed & 1n) !== 0n
  const effectiveSide = flipped ? CombatEncounterState.opposite(side) : side
  const base = effectiveSide === CombatSide.SideA ? SYMMETRIC_SIDE_A_OFFSET : SYMMETRIC_SIDE_B_OFFSET
  return base + BigInt(sideIndex)
}

function getActiveWave(encounter: CombatEncounterState | null): SimultaneousCombatWave | null {
  return encounter?.activeWave instanceof SimultaneousCombatWave ? encounter.activeWave : null
}

function isTargetAlive(encounter: CombatEncounterState | null, target: CombatantState): boolean {
  const wave = getActiveWave(encounter)
  return wave ? wave.isLocallyAlive(target) : target.isAlive
}

function abilityText(obj: Record<string, unknown>, key: string): string | null {
  const v = obj[key]
  return typeof v === 'string' ? v : null
}

function abilityNumber(obj: Record<string, unknown>, key: string): number {
  const v = obj[key]
  return typeof v === 'number' && Number.isFinite(v) ? v : 0
}

function abilityFlag(obj: Record<string, unknown>, key: string): boolean {
  return obj[key] === true
}

function abilityElement(ability: AbilityJson): CombatElement {
  return parseCombatElement(typeof ability.element === 'string' ? ability.element : 'none')
}

function roll(seed: bigint, side: bigint, count: number, channel: bigint): number {
  return deterministicRollNext(seed, side, count, channel)
}

export function selectTarget(
  opponents: readonly CombatantState[],
  seed: bigint,
  side: bigint,
  count: number,
  channel: bigint,
  ignoreThreat = false,
): CombatantState | null {
  let aliveCount = 0
  let total = 0
  let first: CombatantState | null = null
  for (let i = 0; i < opponents.length; i++) {
    const unit = opponents[i]
    if (!unit.isAlive) continue
    if (first === null) first = unit
    aliveCount++
    total += unit.effectiveThreat
  }
  if (aliveCount === 0) return null
  if (aliveCount === 1) return first
  const r = roll(seed, side, count, channel)
  if (ignoreThreat || total <= 1e-12) {
    return pickAliveAt(opponents, Math.min(aliveCount - 1, Math.trunc(r * aliveCount)))
  }
  let ticket = r * total
  let last: CombatantState | null = null
  for (let i = 0; i < opponents.length; i++) {
    const unit = opponents[i]
    if (!unit.isAlive) continue
    last = unit
    if (ticket < unit.effectiveThreat) return unit
    ticket -= unit.effectiveThreat
  }
  return last
}

export function selectTargets(
  opponents: readonly CombatantState[],
  maxTargets: number,
  seed: bigint,
  side: bigint,
  count: number,
  channel: bigint,
  ignoreThreatOnPrimary = false,
): CombatantState[] {
  const result: CombatantState[] = []
  while (result.length < maxTargets) {
    const rollCh = channel + BigInt(result.length)
    const r = roll(seed, side, count, rollCh)
    let total = 0
    let aliveRemaining = 0
    for (let i = 0; i < opponents.length; i++) {
      const unit = opponents[i]
      if (!unit.isAlive || result.includes(unit)) continue
      aliveRemaining++
      total += unit.effectiveThreat
    }
    if (aliveRemaining === 0) break
    let chosen: CombatantState | null = null
    if ((ignoreThreatOnPrimary && result.length === 0) || total <= 1e-12) {
      chosen = pickAliveExcluding(
        opponents,
        result,
        Math.min(aliveRemaining - 1, Math.trunc(r * aliveRemaining)),
      )
    } else {
      let ticket = r * total
      for (let i = 0; i < opponents.length; i++) {
        const unit = opponents[i]
        if (!unit.isAlive || result.includes(unit)) continue
        chosen = unit
        if (ticket < unit.effectiveThreat) break
        ticket -= unit.effectiveThreat
      }
    }
    if (chosen === null) break
    result.push(chosen)
  }
  return result
}

function pickAliveAt(opponents: readonly CombatantState[], wanted: number): CombatantState | null {
  let seen = 0
  for (let i = 0; i < opponents.length; i++) {
    const unit = opponents[i]
    if (!unit.isAlive) continue
    if (seen++ === wanted) return unit
  }
  return null
}

function pickAliveExcluding(
  opponents: readonly CombatantState[],
  picked: readonly CombatantState[],
  wanted: number,
): CombatantState | null {
  let seen = 0
  for (let i = 0; i < opponents.length; i++) {
    const unit = opponents[i]
    if (!unit.isAlive || picked.includes(unit)) continue
    if (seen++ === wanted) return unit
  }
  return null
}

export function selectEnemyTarget(
  encounter: CombatEncounterState,
  side: bigint,
  attackerCount: number,
  channel: bigint,
): CombatantState | null {
  return selectTarget(encounter.enemies, encounter.seed, side, attackerCount, channel)
}

export function lowestHitpointsRatioAlly(allies: readonly CombatantState[]): CombatantState | null {
  let weakest: CombatantState | null = null
  let weakestRatio = Number.POSITIVE_INFINITY
  for (let i = 0; i < allies.length; i++) {
    const ally = allies[i]
    if (!ally.isAlive) continue
    const ratio = ally.hitpoints / Math.max(1, ally.stats.maxHitpoints)
    if (weakest === null || ratio < weakestRatio) {
      weakest = ally
      weakestRatio = ratio
    }
  }
  return weakest
}

function elementBonusPoints(stats: CombatantStats, element: CombatElement): number {
  switch (element) {
    case CombatElement.Metal:
      return stats.metalDamageBonusPoints
    case CombatElement.Wood:
      return stats.woodDamageBonusPoints
    case CombatElement.Water:
      return stats.waterDamageBonusPoints
    case CombatElement.Fire:
      return stats.fireDamageBonusPoints
    case CombatElement.Earth:
      return stats.earthDamageBonusPoints
    default:
      return 0
  }
}

function elementReductionPoints(stats: CombatantStats, element: CombatElement): number {
  switch (element) {
    case CombatElement.Metal:
      return stats.metalDamageReductionPoints
    case CombatElement.Wood:
      return stats.woodDamageReductionPoints
    case CombatElement.Water:
      return stats.waterDamageReductionPoints
    case CombatElement.Fire:
      return stats.fireDamageReductionPoints
    case CombatElement.Earth:
      return stats.earthDamageReductionPoints
    default:
      return 0
  }
}

const DOUBLE_MIN_POSITIVE = 4.9406564584124654e-324 // C# double.Epsilon (Subnormal min positive)

export function applyCombatExperienceContest(
  accuracy: number,
  evasion: number,
  attackerCombatExp: number,
  defenderCombatExp: number,
): [number, number] {
  if (attackerCombatExp <= 0 && defenderCombatExp <= 0) return [accuracy, evasion]
  const denom = Math.max(defenderCombatExp, DOUBLE_MIN_POSITIVE)
  let r = attackerCombatExp / denom
  if (r < 0.1) r = 0.1
  else if (r > 10) r = 10
  if (r >= 1) {
    return [accuracy * Math.min(2, 1 + (r - 1) / 8), evasion * Math.max(0.5, 1 - (r - 1) / 16)]
  }
  const s = 1 / r
  return [accuracy * Math.max(0.5, 1 - (s - 1) / 16), evasion * Math.min(2, 1 + (s - 1) / 8)]
}

export function resolveHit(context: CombatHitContext): void {
  const encounter = context.encounter
  const target = context.target
  const side = context.side
  const abilityId = context.abilityId
  const attack = context.attackStats
  const defense = target.stats
  const attackEffective = context.attacker.effectiveStats
  const defenseEffective = target.effectiveStats

  const contestRoll = roll(encounter.seed, side, context.count, 0n)
  const broken =
    attackEffective.breakPower > defenseEffective.parryResolve &&
    contestRoll <
      attackEffective.breakPower /
        (attackEffective.breakPower + defenseEffective.parryResolve + 100)
  const parried =
    defenseEffective.parryResolve > attackEffective.breakPower &&
    contestRoll <
      defenseEffective.parryResolve /
        (defenseEffective.parryResolve + attackEffective.breakPower + 100)

  let accuracy = attackEffective.accuracy * (broken ? 2 : 1) * (context.doubleAccuracy ? 2 : 1)
  let evasion = defenseEffective.evasion * (parried ? 2 : 1)
  ;[accuracy, evasion] = applyCombatExperienceContest(
    accuracy,
    evasion,
    attack.combatExperience,
    defense.combatExperience,
  )

  const hit =
    roll(encounter.seed, side, context.count, 1n) >= evasion / (evasion + accuracy + 100)
  if (context.zeroDirectDamage) {
    context.hit = hit
    context.critical = false
    context.broken = broken
    context.parried = parried
    context.rawDamage = 0
    context.damage = 0
    return
  }
  const criticalPoints = attackEffective.critical * (broken ? 2 : 1)
  const critical =
    hit &&
    roll(encounter.seed, side, context.count, 2n) <
      criticalPoints / (criticalPoints + defenseEffective.criticalResistance + 100)
  let resistance =
    attack.damageKind === CombatDamageKind.Physical
      ? defenseEffective.defense
      : defenseEffective.magicResistance
  resistance *= parried ? 2 : 1

  const rawDamage =
    abilityId === null
      ? attackEffective.attackPower *
        percentMultiplier(attack.basicAttackBonusPoints, PercentCurveMode.Linear)
      : attack.attackPower + context.attacker.buffTotal(BuffEffectChannel.AttackPower)
  context.rawDamage = rawDamage

  let damage = hit
    ? rawDamage * (rawDamage / (rawDamage + resistance * defenseEffective.resistanceScale + 1))
    : 0
  damage *= percentMultiplier(
    attack.damageKind === CombatDamageKind.Physical
      ? attack.physicalDamageBonusPoints
      : attack.spellDamageBonusPoints,
    PercentCurveMode.Linear,
  )
  if (attack.element !== CombatElement.None) {
    damage *= percentMultiplier(
      elementBonusPoints(attack, attack.element),
      PercentCurveMode.Linear,
    )
  }
  damage *= percentMultiplier(
    attack.totalDamageBonusPoints + context.attacker.buffTotal(BuffEffectChannel.DamageBonusPoints),
    PercentCurveMode.Linear,
  )
  damage *= percentMultiplier(
    attack.damageKind === CombatDamageKind.Physical
      ? defense.physicalDamageReductionPoints
      : defense.spellDamageReductionPoints,
    PercentCurveMode.NonLinear,
  )
  if (attack.element !== CombatElement.None) {
    damage *= percentMultiplier(
      elementReductionPoints(defense, attack.element),
      PercentCurveMode.NonLinear,
    )
  }
  if (critical) {
    damage *= percentMultiplier(
      attack.criticalDamagePoints +
        context.attacker.buffTotal(BuffEffectChannel.CriticalDamagePoints),
      PercentCurveMode.Linear,
    )
  }
  damage *= percentMultiplier(
    defense.damageAbsorptionPoints +
      target.bonusAbsorptionPoints +
      target.buffTotal(BuffEffectChannel.DamageAbsorptionPoints),
    PercentCurveMode.NonLinear,
  )
  if (
    context.postFinalDamageMultiplier !== undefined &&
    context.postFinalDamageMultiplier !== 1.0
  ) {
    damage *= context.postFinalDamageMultiplier
  }
  if (hit) {
    const fixedBonus =
      attack.fixedDamageBonus +
      (attack.damageKind === CombatDamageKind.Physical
        ? attack.fixedPhysicalDamageBonus
        : attack.fixedSpellDamageBonus)
    const fixedReduction =
      defense.fixedDamageReduction +
      (attack.damageKind === CombatDamageKind.Physical
        ? defense.fixedPhysicalDamageReduction
        : defense.fixedSpellDamageReduction)
    damage = Math.max(0, damage + fixedBonus - fixedReduction)
  }
  context.hit = hit
  context.critical = critical
  context.broken = broken
  context.parried = parried
  context.damage = Math.max(0, damage)
}

/**
 * 计算即时打击消耗品的抗性穿甲前原始威力（与服务端 DeterministicCombatEngine.ResolveConsumableStrikePower 1:1 镜像）：
 * 归属符箓（talisman）时并入 talismanDamageBonusPoints（Linear 曲线，符箓技艺每级 +1 点）。
 */
export function resolveConsumableStrikePower(
  item: { displaySubCategory: string },
  strike: Pick<
    ConsumableStrike,
    'manaDamageRatio' | 'spellAttackRatio' | 'physicalAttackRatio' | 'flatDamage'
  >,
  casterStats: CombatantStats,
): number {
  const basePower =
    strike.manaDamageRatio * casterStats.maxManapoints +
    strike.spellAttackRatio * casterStats.spellAttack +
    strike.physicalAttackRatio * casterStats.attackPower +
    strike.flatDamage
  if (item.displaySubCategory !== 'talisman') return basePower
  return (
    basePower * percentMultiplier(casterStats.talismanDamageBonusPoints, PercentCurveMode.Linear)
  )
}

/** 计算怒焰符类即时消耗品施加的燃烧层数（受符箓伤害增幅 Linear 放大并向下取整，保底 1 层）。 */
export function resolveConsumableRanhunStacks(
  item: { displaySubCategory: string },
  strike: Pick<ConsumableStrike, 'ranhunBaseStacks' | 'ranhunSpellAttackRatio'>,
  casterStats: CombatantStats,
): number {
  if (strike.ranhunBaseStacks <= 0 && strike.ranhunSpellAttackRatio <= 0) return 0
  let rawStacks =
    Math.max(0, casterStats.spellAttack) * strike.ranhunSpellAttackRatio + strike.ranhunBaseStacks
  if (item.displaySubCategory === 'talisman') {
    rawStacks *= percentMultiplier(casterStats.talismanDamageBonusPoints, PercentCurveMode.Linear)
  }
  return Math.max(1, Math.floor(rawStacks + 1e-9))
}

/** 计算消耗品实际冷却秒数（归属符箓时叠加「符法·疾」冷却加速 NonLinear 曲线）。 */
export function resolveConsumableCooldownSeconds(
  item: { displaySubCategory: string },
  consumable: Pick<ConsumableDefinition, 'cooldownSeconds'>,
  traits: readonly string[] | null | undefined,
  traitLevels?: Record<string, number> | null,
): number {
  if (item.displaySubCategory !== 'talisman') return consumable.cooldownSeconds
  const speedPoints = talismanCooldownSpeedPoints(traits, traitLevels)
  return speedPoints !== 0
    ? consumable.cooldownSeconds * percentMultiplier(speedPoints, PercentCurveMode.NonLinear)
    : consumable.cooldownSeconds
}

/** 计算消耗品实际灵力消耗（归属符箓且携带「符法·疾」时灵力消耗提升 300% 即 ×4.0）。 */
export function resolveConsumableManaCost(
  item: { displaySubCategory: string },
  baseManaCostRatio: number,
  casterStats: CombatantStats,
): number {
  if (baseManaCostRatio <= 0) return 0
  const mult =
    item.displaySubCategory === 'talisman' ? talismanManaCostMultiplier(casterStats.traits) : 1.0
  return baseManaCostRatio * mult * casterStats.maxManapoints
}

/** 判定直伤符箓是否触发暴击（仅携带「符法·破阵」时开启，走独立通道 25）。 */
export function rollTalismanCrit(
  seed: bigint,
  side: bigint,
  rollIndex: number,
  caster: CombatantState,
  target: CombatantState,
): boolean {
  if (!canTalismanCrit(caster.stats.traits)) return false
  const attackEff = caster.effectiveStats
  const defenseEff = target.effectiveStats
  if (attackEff.critical <= 0) return false
  return (
    roll(seed, side, rollIndex, 25n) <
    attackEff.critical / (attackEff.critical + defenseEff.criticalResistance + 100)
  )
}

export function resolveStrikeDamage(
  attack: CombatantStats,
  target: CombatantState,
  critical = false,
): number {
  const defense = target.effectiveStats
  const resistance =
    attack.damageKind === CombatDamageKind.Physical ? defense.defense : defense.magicResistance
  const rawDamage = attack.attackPower
  let damage = rawDamage * (rawDamage / (rawDamage + resistance * defense.resistanceScale + 1))
  damage *= percentMultiplier(
    attack.damageKind === CombatDamageKind.Physical
      ? attack.physicalDamageBonusPoints
      : attack.spellDamageBonusPoints,
    PercentCurveMode.Linear,
  )
  if (attack.element !== CombatElement.None) {
    damage *= percentMultiplier(
      elementBonusPoints(attack, attack.element),
      PercentCurveMode.Linear,
    )
  }
  damage *= percentMultiplier(attack.totalDamageBonusPoints, PercentCurveMode.Linear)
  damage *= percentMultiplier(
    attack.damageKind === CombatDamageKind.Physical
      ? defense.physicalDamageReductionPoints
      : defense.spellDamageReductionPoints,
    PercentCurveMode.NonLinear,
  )
  if (attack.element !== CombatElement.None) {
    damage *= percentMultiplier(
      elementReductionPoints(defense, attack.element),
      PercentCurveMode.NonLinear,
    )
  }
  if (critical) {
    damage *= percentMultiplier(attack.criticalDamagePoints, PercentCurveMode.Linear)
  }
  damage *= percentMultiplier(
    defense.damageAbsorptionPoints +
      target.bonusAbsorptionPoints +
      target.buffTotal(BuffEffectChannel.DamageAbsorptionPoints),
    PercentCurveMode.NonLinear,
  )
  const fixedBonus =
    attack.fixedDamageBonus +
    (attack.damageKind === CombatDamageKind.Physical
      ? attack.fixedPhysicalDamageBonus
      : attack.fixedSpellDamageBonus)
  const fixedReduction =
    defense.fixedDamageReduction +
    (attack.damageKind === CombatDamageKind.Physical
      ? defense.fixedPhysicalDamageReduction
      : defense.fixedSpellDamageReduction)
  return Math.max(0, damage + fixedBonus - fixedReduction)
}

// ---------- 伤害扣血裁决与蓄意/燃魂/血气化盾/养剑术 ----------

export function applyCombatDamage(
  encounter: CombatEncounterState | null,
  attacker: CombatantState,
  target: CombatantState,
  damage: number,
  accumulateXuyi = true,
  bypassShield = false,
): { damage: number; actual: number } {
  const wave = getActiveWave(encounter)
  if (wave) {
    return wave.recordDamage(attacker, target, damage, accumulateXuyi, bypassShield)
  }
  if (!target.isAlive) return { damage: 0, actual: 0 }
  let remaining = Math.max(0, damage)
  let shieldAbsorbed = 0
  if (!bypassShield && target.shield > 0 && remaining > 0) {
    shieldAbsorbed = Math.min(target.shield, remaining)
    target.shield = Math.max(0, target.shield - shieldAbsorbed)
    remaining -= shieldAbsorbed
  }
  const hpDamage = remaining > 0 ? limitDamageForHaste(attacker, target.hitpoints, remaining) : 0
  const before = target.hitpoints
  target.hitpoints = Math.max(0, before - hpDamage)
  const actual = before - target.hitpoints
  if (!target.isAlive) target.shield = 0
  target.syncDerivedStacks()
  if (
    accumulateXuyi &&
    actual > 0 &&
    target.isAlive &&
    containsTrait(target.stats.traits, TraitIds.Xuyi)
  ) {
    const existing = target.buffs.find((b) => b.buffId === BuffIds.Xuyi)
    const addStacks = actual * traitLevelScale(target.stats.traitLevels, TraitIds.Xuyi)
    target.setBuff({
      buffId: BuffIds.Xuyi,
      expiresAtSeconds: Number.MAX_VALUE,
      stacks: (existing ? existing.stacks : 0) + addStacks,
      name: '蓄意',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: null,
      statRatios: null,
      sourceUnitId: null,
    })
  }
  if (!target.isAlive && encounter) encounter.notifyAliveCountsChanged()
  return { damage: shieldAbsorbed + hpDamage, actual }
}

function buildStrikeStats(
  unit: CombatantState,
  power: number,
  kind: CombatDamageKind,
  element: CombatElement,
): CombatantStats {
  return {
    ...unit.effectiveStats,
    attackPower: power,
    damageKind: kind,
    element,
    totalDamageBonusPoints:
      unit.stats.totalDamageBonusPoints + unit.buffTotal(BuffEffectChannel.DamageBonusPoints),
  }
}

export function addRanhunStacks(
  attacker: CombatantState,
  target: CombatantState,
  stacks: number,
  at: number,
  encounter: CombatEncounterState | null = null,
): void {
  if (stacks <= 0 || !isTargetAlive(encounter, target)) return
  const wave = getActiveWave(encounter)
  const apply = () => {
    if (!target.isAlive) return
    const existing = target.buffs.find((b) => b.buffId === BuffIds.Ranhun)
    target.setBuff({
      buffId: BuffIds.Ranhun,
      expiresAtSeconds: mergeBuffExpiry(existing?.expiresAtSeconds, at + 5),
      stacks: stacks + (existing ? existing.stacks : 0),
      name: '燃烧',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: [{ channel: BuffEffectChannel.FireSpellDamagePerSecond, value: 1 }],
      statRatios: null,
      sourceUnitId: attacker.id,
    })
  }
  if (wave) wave.deferBuffOp(apply)
  else apply()
}

export function applyRanhun(
  attacker: CombatantState,
  target: CombatantState,
  element: CombatElement,
  actual: number,
  at: number,
  encounter: CombatEncounterState | null = null,
): void {
  if (
    actual <= 0 ||
    !isTargetAlive(encounter, target) ||
    element !== CombatElement.Fire ||
    !containsTrait(attacker.stats.traits, TraitIds.Ranhun)
  ) {
    return
  }
  const stacks = Math.max(
    1,
    Math.floor(actual * 0.02 * traitLevelScale(attacker.stats.traitLevels, TraitIds.Ranhun)),
  )
  addRanhunStacks(attacker, target, stacks, at, encounter)
}

export function applyXueqihuadun(
  encounter: CombatEncounterState | null,
  unit: CombatantState,
): number {
  if (!unit.isAlive || !containsTrait(unit.stats.traits, TraitIds.Xueqihuadun)) return 0
  const threshold = unit.stats.maxHitpoints * 0.2
  if (unit.hitpoints <= threshold + 1e-9) return 0
  const excess = unit.hitpoints - threshold
  const settled = applyCombatDamage(encounter, unit, unit, excess, true, true)
  if (settled.actual <= 0 || !unit.isAlive) return 0
  const threatOnTaken = unit.stats.effects.threatOnTakenPoints
  if (threatOnTaken > 0) {
    unit.threat += (excess * threatOnTaken) / 100.0
  }
  const shieldGain =
    settled.actual * xueqihuadunConversionRatio(unit.stats.traits, unit.stats.traitLevels)
  unit.shield += shieldGain
  return shieldGain
}

export function applyBattleStartReactions(encounter: CombatEncounterState): void {
  if (encounter.battleStartReactionsApplied || encounter.elapsedSeconds > 1e-12) return
  encounter.battleStartReactionsApplied = true
  for (let i = 0; i < encounter.playerAllies.length; i++) {
    applyXueqihuadun(encounter, encounter.playerAllies[i])
  }
  for (let i = 0; i < encounter.enemies.length; i++) {
    applyXueqihuadun(encounter, encounter.enemies[i])
  }
}

export function settleWaveEndShields(
  encounter: CombatEncounterState,
  events: CombatEvent[] | null = null,
): void {
  if (encounter.waveEndShieldsSettled || encounter.outcome === CombatOutcome.Running) return
  encounter.waveEndShieldsSettled = true
  const settleSide = (units: readonly CombatantState[]) => {
    for (let i = 0; i < units.length; i++) {
      const unit = units[i]
      const remainingShield = unit.shield
      unit.shield = 0
      if (
        remainingShield <= 1e-9 ||
        !unit.isAlive ||
        !containsTrait(unit.stats.traits, TraitIds.Xueqihuadun)
      ) {
        continue
      }
      const rawHeal =
        remainingShield *
        percentMultiplier(unit.effectiveStats.effects.healingBonusPoints, PercentCurveMode.Linear)
      const healed = Math.min(unit.stats.maxHitpoints - unit.hitpoints, Math.max(0, rawHeal))
      if (healed <= 1e-9) continue
      unit.hitpoints += healed
      unit.syncDerivedStacks()
      encounter.statsBook?.recordHealingUnit(unit, false, healed)
      events?.push({
        atSeconds: encounter.elapsedSeconds,
        attackerId: unit.id,
        targetId: unit.id,
        damage: 0,
        hit: true,
        damageKind: CombatDamageKind.Physical,
        targetHitpointsAfter: unit.hitpoints,
        critical: false,
        break: false,
        parried: false,
        abilityId: TraitIds.Xueqihuadun,
        healing: healed,
        attackerManapointsAfter: unit.manapoints,
        targetManapointsAfter: unit.manapoints,
        attackerShieldAfter: 0,
        targetShieldAfter: 0,
      })
    }
  }
  settleSide(encounter.playerAllies)
  settleSide(encounter.enemies)
}

function emitTraitOnHit(
  context: CombatHitContext,
  derivedEvents: CombatEvent[],
): void {
  if (!context.hit || !context.attacker.isAlive) return
  const wave = getActiveWave(context.encounter)
  applyRanhun(
    context.attacker,
    context.target,
    context.attackStats.element,
    context.actualDamage,
    context.atSeconds,
    context.encounter,
  )
  const attacker = context.attacker
  if (!containsTrait(attacker.stats.traits, TraitIds.Xuyi)) return
  const buff = attacker.buffs.find((b) => b.buffId === BuffIds.Xuyi)
  if (!buff || buff.stacks <= attacker.stats.maxHitpoints) return
  if (wave && !wave.tryConsumeXuyi(attacker)) return
  if (wave) {
    wave.deferBuffOp(() => attacker.removeBuffsWhere((b) => b.buffId === BuffIds.Xuyi))
  } else {
    attacker.removeBuffsWhere((b) => b.buffId === BuffIds.Xuyi)
  }
  const opponents = context.encounter.opponentsOf(attacker)

  const burstElement = resolveDamageElement(attacker.stats.traits, CombatElement.None)
  const burst = (target: CombatantState, primary: boolean) => {
    const rawDamage = primary
      ? buff.stacks
      : resolveStrikeDamage(
          buildStrikeStats(attacker, buff.stacks, CombatDamageKind.Physical, burstElement),
          target,
        )
    const settled = applyCombatDamage(context.encounter, attacker, target, rawDamage, false)
    applyRanhun(
      attacker,
      target,
      burstElement,
      settled.actual,
      context.atSeconds,
      context.encounter,
    )
    if (wave) wave.addThreatDelta(attacker, settled.damage)
    else attacker.threat += settled.damage
    context.encounter.statsBook?.recordHitUnit(
      attacker,
      target,
      primary ? CombatDamageKind.True : CombatDamageKind.Physical,
      settled.damage,
      primary ? 0 : Math.max(0, buff.stacks - settled.damage),
      burstElement,
    )
    derivedEvents.push({
      atSeconds: context.atSeconds,
      attackerId: attacker.id,
      targetId: target.id,
      damage: settled.damage,
      hit: true,
      damageKind: primary ? CombatDamageKind.True : CombatDamageKind.Physical,
      targetHitpointsAfter: target.hitpoints,
      critical: false,
      break: false,
      parried: false,
      abilityId: TraitIds.Xuyi,
      healing: 0,
      attackerManapointsAfter: null,
      targetManapointsAfter: null,
    })
  }

  if (isTargetAlive(context.encounter, context.target)) burst(context.target, true)
  for (let i = 0; i < opponents.length; i++) {
    const opp = opponents[i]
    if (isTargetAlive(context.encounter, opp) && opp !== context.target) burst(opp, false)
  }
}

function tickRanhun(
  encounter: CombatEncounterState,
  target: CombatantState,
  at: number,
  events: CombatEvent[],
): void {
  if (!target.isAlive) return
  const buffs = target.buffs
  for (let i = 0; i < buffs.length; i++) {
    const buff = buffs[i]
    let dotValue = 0
    if (buff.effects) {
      for (let j = 0; j < buff.effects.length; j++) {
        if (buff.effects[j].channel === BuffEffectChannel.FireSpellDamagePerSecond) {
          dotValue = buff.effects[j].value
          break
        }
      }
    }
    const power = dotValue * buff.stacks
    if (power <= 0 || buff.expiresAtSeconds + 1e-9 < at) continue
    const source = encounter.findUnit(buff.sourceUnitId)
    if (!source) {
      throw new Error(`持续伤害缺少战斗来源: ${buff.sourceUnitId ?? 'null'}`)
    }
    const attack = buildStrikeStats(source, power, CombatDamageKind.Spell, CombatElement.Fire)
    const damage = resolveStrikeDamage(attack, target)
    const settled = applyCombatDamage(encounter, source, target, damage)
    source.threat += settled.damage
    encounter.statsBook?.recordHitUnit(
      source,
      target,
      CombatDamageKind.Spell,
      settled.damage,
      Math.max(0, power - settled.damage),
      CombatElement.Fire,
    )
    events.push({
      atSeconds: at,
      attackerId: source.id,
      targetId: target.id,
      damage: settled.damage,
      hit: true,
      damageKind: CombatDamageKind.Spell,
      targetHitpointsAfter: target.hitpoints,
      critical: false,
      break: false,
      parried: false,
      abilityId: TraitIds.Ranhun,
      healing: 0,
      attackerManapointsAfter: source.manapoints,
      targetManapointsAfter: target.manapoints,
      attackerShieldAfter: source.shield,
      targetShieldAfter: target.shield,
    })
    break
  }
  if (
    at > 1e-9 &&
    target.isAlive &&
    containsTrait(target.stats.traits, TraitIds.Yangjianshu) &&
    (target.lastDirectDamageAtSeconds < 0 || at - target.lastDirectDamageAtSeconds >= 1.0 - 1e-9)
  ) {
    const scale = traitLevelScale(target.stats.traitLevels, TraitIds.Yangjianshu)
    const existing = target.buffs.find((b) => b.buffId === BuffIds.Jianyi)
    const nextStacks = (existing ? existing.stacks : 0) + 1
    const expiresAt = mergeBuffExpiry(existing?.expiresAtSeconds, at + 10.0)
    target.setBuff({
      buffId: BuffIds.Jianyi,
      expiresAtSeconds: expiresAt,
      stacks: nextStacks,
      name: '剑意',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: [
        { channel: BuffEffectChannel.AttackPowerPostRatio, value: 0.3 * scale },
        { channel: BuffEffectChannel.CriticalPostRatio, value: 0.3 * scale },
        { channel: BuffEffectChannel.CriticalDamagePoints, value: 30.0 * scale },
      ],
      statRatios: null,
      sourceUnitId: null,
    })
  }
}

// ---------- 内联命中触发器管线（Strict Order: 50 -> 100 -> 110 -> 200 -> 300 -> 400） ----------

function triggerHpDrain(context: CombatHitContext, points: number, derivedEvents: CombatEvent[]): void {
  if (!context.hit || context.damage <= 0 || !context.attacker.isAlive) return
  if (points <= 0) return
  const rawHeal =
    ((context.damage * points) / 100) *
    percentMultiplier(context.attacker.effectiveStats.effects.healingBonusPoints, PercentCurveMode.Linear)
  if (rawHeal <= 0) return
  const wave = getActiveWave(context.encounter)
  if (wave) {
    wave.recordHeal(context.attacker, context.attacker, rawHeal)
    derivedEvents.push({
      atSeconds: context.atSeconds,
      attackerId: context.attacker.id,
      targetId: context.attacker.id,
      damage: 0,
      hit: true,
      damageKind: context.attackStats.damageKind,
      targetHitpointsAfter: context.attacker.hitpoints,
      critical: false,
      break: false,
      parried: false,
      abilityId: context.abilityId,
      healing: rawHeal,
      attackerManapointsAfter: null,
      targetManapointsAfter: null,
    })
    return
  }
  const healing = Math.min(context.attacker.stats.maxHitpoints - context.attacker.hitpoints, rawHeal)
  if (healing <= 0) return
  context.attacker.hitpoints += healing
  context.encounter.statsBook?.recordHealingUnit(context.attacker, false, healing)
  derivedEvents.push({
    atSeconds: context.atSeconds,
    attackerId: context.attacker.id,
    targetId: context.attacker.id,
    damage: 0,
    hit: true,
    damageKind: context.attackStats.damageKind,
    targetHitpointsAfter: context.attacker.hitpoints,
    critical: false,
    break: false,
    parried: false,
    abilityId: context.abilityId,
    healing,
    attackerManapointsAfter: null,
    targetManapointsAfter: null,
  })
}

function triggerManaDrain(context: CombatHitContext, points: number): void {
  if (!context.hit || context.damage <= 0 || !context.attacker.isAlive) return
  if (points <= 0) return
  const wave = getActiveWave(context.encounter)
  if (wave) {
    const drainedWave = Math.min(wave.getEffectiveMana(context.target), (context.damage * points) / 100)
    if (drainedWave <= 0) return
    wave.addManaDelta(context.target, -drainedWave)
    wave.addManaDelta(context.attacker, drainedWave)
    return
  }
  const drained = Math.min(context.target.manapoints, (context.damage * points) / 100)
  if (drained <= 0) return
  context.target.manapoints -= drained
  context.attacker.manapoints = Math.min(
    context.attacker.stats.maxManapoints,
    context.attacker.manapoints + drained,
  )
}

function triggerDamageReflect(
  context: CombatHitContext,
  points: number,
  derivedEvents: CombatEvent[],
): void {
  if (!context.hit || context.damage <= 0 || !context.attacker.isAlive) return
  if (points <= 0) return
  let reflected = (context.damage * points) / 100
  if (reflected <= 0) return
  reflected = applyCombatDamage(context.encounter, context.target, context.attacker, reflected).damage
  const wave = getActiveWave(context.encounter)
  if (wave) wave.addThreatDelta(context.target, reflected)
  else context.target.threat += reflected
  context.encounter.statsBook?.recordHitUnit(
    context.target,
    context.attacker,
    context.attackStats.damageKind,
    reflected,
    0,
    context.attackStats.element,
  )
  derivedEvents.push({
    atSeconds: context.atSeconds,
    attackerId: context.target.id,
    targetId: context.attacker.id,
    damage: reflected,
    hit: true,
    damageKind: context.attackStats.damageKind,
    targetHitpointsAfter: context.attacker.hitpoints,
    critical: false,
    break: false,
    parried: false,
    abilityId: null,
    healing: 0,
    attackerManapointsAfter: null,
    targetManapointsAfter: null,
  })
}

function triggerThreatOnTaken(context: CombatHitContext, points: number): void {
  if (!context.hit || points <= 0) return
  const gained = (context.rawDamage * points) / 100
  if (gained <= 0) return
  const wave = getActiveWave(context.encounter)
  if (wave) wave.addThreatDelta(context.target, gained)
  else context.target.threat += gained
}

function triggerFormationReactiveStrike(
  context: CombatHitContext,
  derivedEvents: CombatEvent[],
  rollState: { ch: bigint },
): void {
  const encounter = context.encounter
  if (!context.hit || context.damage <= 0 || !isTargetAlive(encounter, context.target)) return
  const wave = getActiveWave(encounter)
  const allies = encounter.opponentsOf(context.target)
  for (let i = 0; i < allies.length; i++) {
    const ally = allies[i]
    if (!ally.isAlive || !isTargetAlive(encounter, context.target)) continue

    if (ally.buffStacks('formation.tianlei') > 0) {
      const canTrigger =
        ally.lastTianleiTriggerAtSeconds < 0 ||
        context.atSeconds - ally.lastTianleiTriggerAtSeconds >= 1.0 - 1e-9
      const manaCost = ally.stats.maxManapoints * 0.01
      const availMana = wave ? wave.getEffectiveMana(ally) : ally.manapoints
      if (
        canTrigger &&
        availMana >= manaCost &&
        roll(encounter.seed, context.side, context.count, rollState.ch++) < 0.2
      ) {
        if (wave) wave.addManaDelta(ally, -manaCost)
        else ally.manapoints = Math.max(0, ally.manapoints - manaCost)
        ally.lastTianleiTriggerAtSeconds = context.atSeconds
        // 阵法伤害增幅在抗性穿甲前并入第一层原始威力（阵法技艺每级 +1 点）；不放大耗蓝代价。
        const rawDamage =
          (ally.effectiveStats.spellAttack * 0.5 + 10) *
          percentMultiplier(ally.stats.formationDamageBonusPoints, PercentCurveMode.Linear)
        const strikeElement = resolveDamageElement(ally.stats.traits, CombatElement.Wood)
        const strikeStats: CombatantStats = {
          ...ally.effectiveStats,
          attackPower: rawDamage,
          damageKind: CombatDamageKind.Spell,
          element: strikeElement,
          totalDamageBonusPoints:
            ally.stats.totalDamageBonusPoints + ally.buffTotal(BuffEffectChannel.DamageBonusPoints),
        }
        const rawDmg = Math.max(0, resolveStrikeDamage(strikeStats, context.target))
        if (rawDmg > 0) {
          const settled = applyCombatDamage(encounter, ally, context.target, rawDmg)
          const actualDamage = settled.actual
          applyRanhun(ally, context.target, strikeElement, actualDamage, context.atSeconds, encounter)
          if (wave) wave.addThreatDelta(ally, settled.damage)
          else ally.threat += settled.damage
          encounter.statsBook?.recordHitUnit(
            ally,
            context.target,
            CombatDamageKind.Spell,
            settled.damage,
            0,
            strikeElement,
          )
          derivedEvents.push({
            atSeconds: context.atSeconds,
            attackerId: ally.id,
            targetId: context.target.id,
            damage: settled.damage,
            hit: true,
            damageKind: CombatDamageKind.Spell,
            targetHitpointsAfter: context.target.hitpoints,
            critical: false,
            break: false,
            parried: false,
            abilityId: 'item.tianlei-formation-disk',
            healing: 0,
            attackerManapointsAfter: null,
            targetManapointsAfter: null,
          })
        }
      }
    }

    if (!isTargetAlive(encounter, context.target)) break

    if (ally.buffStacks('formation.xuesha') > 0) {
      const canTrigger =
        ally.lastXueshaTriggerAtSeconds < 0 ||
        context.atSeconds - ally.lastXueshaTriggerAtSeconds >= 1.0 - 1e-9
      if (canTrigger && roll(encounter.seed, context.side, context.count, rollState.ch++) < 0.2) {
        ally.lastXueshaTriggerAtSeconds = context.atSeconds
        // 阵法伤害增幅在抗性穿甲前并入第一层原始威力（阵法技艺每级 +1 点）；不放大自损代价，吸血随实伤自然联动。
        const rawDamage =
          ally.stats.maxHitpoints *
          0.1 *
          percentMultiplier(ally.stats.formationDamageBonusPoints, PercentCurveMode.Linear)
        const strikeElement = resolveDamageElement(ally.stats.traits, CombatElement.None)
        const strikeStats: CombatantStats = {
          ...ally.effectiveStats,
          attackPower: rawDamage,
          damageKind: CombatDamageKind.Physical,
          element: strikeElement,
          totalDamageBonusPoints:
            ally.stats.totalDamageBonusPoints + ally.buffTotal(BuffEffectChannel.DamageBonusPoints),
        }
        const rawDmg = Math.max(0, resolveStrikeDamage(strikeStats, context.target))
        if (rawDmg > 0) {
          const settled = applyCombatDamage(encounter, ally, context.target, rawDmg)
          const actualDamage = settled.actual
          applyRanhun(ally, context.target, strikeElement, actualDamage, context.atSeconds, encounter)
          const rawHeal =
            actualDamage *
            percentMultiplier(
              ally.effectiveStats.effects.healingBonusPoints,
              PercentCurveMode.Linear,
            )
          let heal: number
          if (wave) {
            heal = rawHeal
            wave.recordHeal(ally, ally, rawHeal, 'item.xuesha-formation-disk')
          } else {
            heal = Math.min(ally.stats.maxHitpoints - ally.hitpoints, rawHeal)
            if (heal > 0) {
              ally.hitpoints += heal
              encounter.statsBook?.recordHealingUnit(ally, false, heal)
            }
          }
          if (wave) wave.addThreatDelta(ally, settled.damage)
          else ally.threat += settled.damage
          encounter.statsBook?.recordHitUnit(
            ally,
            context.target,
            CombatDamageKind.Physical,
            settled.damage,
            0,
            strikeElement,
          )
          derivedEvents.push({
            atSeconds: context.atSeconds,
            attackerId: ally.id,
            targetId: context.target.id,
            damage: settled.damage,
            hit: true,
            damageKind: CombatDamageKind.Physical,
            targetHitpointsAfter: context.target.hitpoints,
            critical: false,
            break: false,
            parried: false,
            abilityId: 'item.xuesha-formation-disk',
            healing: heal,
            attackerManapointsAfter: null,
            targetManapointsAfter: null,
          })
        }
      }
    }
  }
}

function drainPendingTriggers(encounter: CombatEncounterState): void {
  const pending = encounter.pendingTriggers
  if (pending.length === 0) return
  // 同次命中的 Attacker 与 Target 可能各自衍生事件，保持同一个 _rollChannel (自 64 起) 与 DerivedEvents 共享
  const derivedByContext = new Map<CombatHitContext, { events: CombatEvent[]; rollState: { ch: bigint } }>()

  for (let i = 0; i < pending.length; i++) {
    const trig = pending[i]
    let state = derivedByContext.get(trig.hitContext)
    if (!state) {
      state = { events: [], rollState: { ch: 64n } }
      derivedByContext.set(trig.hitContext, state)
    }
    const ctx = trig.hitContext
    const isAttacker = trig.attacker === ctx.attacker
    // 注意：EmitHitTriggers 入队序先 Attacker 条目（Order 50, 100, 110）再 Target 条目（Order 200, 300, 400）
    if (isAttacker) {
      emitTraitOnHit(ctx, state.events)
      triggerHpDrain(ctx, ctx.attacker.stats.effects.hpDrainPoints, state.events)
      triggerManaDrain(ctx, ctx.attacker.stats.effects.mpDrainPoints)
    } else {
      triggerDamageReflect(ctx, ctx.target.stats.effects.damageReflectPoints, state.events)
      triggerThreatOnTaken(ctx, ctx.target.stats.effects.threatOnTakenPoints)
      triggerFormationReactiveStrike(ctx, state.events, state.rollState)
    }

    // Attacker 处理不会写入主事件灵力，Target 是该 Hit 的末尾条（等价于 Settle）
    if (!isAttacker) {
      const mainEv = trig.events[trig.mainEventIndex]
      mainEv.attackerManapointsAfter = ctx.attacker.manapoints
      mainEv.targetManapointsAfter = ctx.target.manapoints
      for (let d = 0; d < state.events.length; d++) {
        trig.events.push(state.events[d])
      }
    }
  }
  pending.length = 0
}

// ---------- 出手系统（CombatActSystem） ----------

function abilityBuffSpecs(
  ability: AbilityJson,
  singleKey: 'buff' | 'onHitBuff' | 'selfBuff' = 'buff',
  arrayKey: 'buffs' | 'onHitBuffs' | 'selfBuffs' = 'buffs',
): AbilityBuffJson[] {
  const arr = ability[arrayKey]
  if (Array.isArray(arr)) {
    return arr.filter((b): b is AbilityBuffJson => b !== null && typeof b === 'object')
  }
  const single = ability[singleKey]
  if (single && typeof single === 'object') {
    return [single]
  }
  return []
}

function initialBuffStacks(
  buff: AbilityBuffJson,
  caster: CombatantState,
  target: CombatantState,
  growth: number,
): number {
  const baseStacks =
    typeof buff.baseStacks === 'number' && Number.isFinite(buff.baseStacks)
      ? Math.max(1, buff.baseStacks)
      : 1.0
  const missingPerStack = abilityNumber(buff, 'missingHitpointsPerStack')
  if (missingPerStack > 0) {
    const missing = Math.max(0, target.stats.maxHitpoints - target.hitpoints)
    return Math.floor((missing * growth) / missingPerStack + 1e-9)
  }
  const spellAttackPercent = abilityNumber(buff, 'spellAttackStackPercent')
  if (spellAttackPercent > 0) {
    const extraStacks = Math.max(0, caster.stats.spellAttack) * spellAttackPercent * growth
    const healingMult = percentMultiplier(
      caster.effectiveStats.effects.healingBonusPoints,
      PercentCurveMode.Linear,
    )
    const totalStacks = (baseStacks + extraStacks) * healingMult
    return Math.max(1, Math.floor(totalStacks + 1e-9))
  }
  const resistancePerStack = abilityNumber(buff, 'resistancePerStack')
  if (resistancePerStack > 0) {
    const resistance = caster.stats.defense + caster.stats.magicResistance
    const ratio = abilityNumber(buff, 'resistanceStackRatio')
    return baseStacks + Math.floor((resistance * ratio * growth) / resistancePerStack + 1e-9)
  }
  return baseStacks
}

export function grantAbilityBuff(
  buff: AbilityBuffJson,
  caster: CombatantState,
  target: CombatantState,
  grantedAtSeconds: number,
  levelGrowth: number,
  abilityId: string,
  encounter: CombatEncounterState | null = null,
  includeCasterSkillBonus = true,
): void {
  const wave = getActiveWave(encounter)
  const apply = () => {
    if (!target.isAlive) return
    const baseBuffId = abilityText(buff, 'buffId') ?? `ability-buff:${abilityId}`
    const policy = abilityText(buff, 'stackPolicy') ?? 'capped'
    const buffId = policy === 'perCaster' ? `${baseBuffId}:${caster.id}` : baseBuffId
    const missingPerStack = abilityNumber(buff, 'missingHitpointsPerStack')
    const spellStackPercent = abilityNumber(buff, 'spellAttackStackPercent')
    const resistancePerStack = abilityNumber(buff, 'resistancePerStack')
    const hasStackDerivation = missingPerStack > 0 || spellStackPercent > 0 || resistancePerStack > 0
    const effectiveLevelGrowth = abilityFlag(buff, 'noLevelGrowth') ? 1.0 : levelGrowth

    const skillMult = includeCasterSkillBonus
      ? percentMultiplier(caster.stats.skillBonusPoints, PercentCurveMode.Linear)
      : 1.0
    const growth = effectiveLevelGrowth * skillMult
    const effectGrowth = hasStackDerivation ? 1.0 : effectiveLevelGrowth
    const initialStacks = initialBuffStacks(buff, caster, target, growth)
    const proposedExpiry = grantedAtSeconds + Math.max(0, abilityNumber(buff, 'durationSeconds'))

    const grant: CombatBuffState = {
      buffId,
      expiresAtSeconds: proposedExpiry,
      stacks: initialStacks,
      name: abilityText(buff, 'name') ?? baseBuffId,
      resetOnWave: buff.resetOnWave === true,
      missingHitpointsPerStack: missingPerStack > 0 ? missingPerStack / growth : 0,
      effects: parseBuffEffects(buff, effectGrowth),
      statRatios: null,
      sourceUnitId: caster.id,
    }
    const stackCap = Math.max(1, abilityNumber(buff, 'maxStacks'))
    const existing = target.buffs.find((b) => b.buffId === buffId)
    let nextStacks = initialStacks
    if (existing) {
      nextStacks =
        policy === 'maxKeep'
          ? Math.max(existing.stacks, initialStacks)
          : Math.min(stackCap, existing.stacks + initialStacks)
    }
    const mergedExpiry = mergeBuffExpiry(existing?.expiresAtSeconds, proposedExpiry)
    target.setBuff({ ...grant, stacks: nextStacks, expiresAtSeconds: mergedExpiry })
    if (caster !== target && caster.skillWakePending) {
      for (let i = 0; i < caster.skillWakePending.length; i++) {
        const cur = caster.skillWakePending[i]
        caster.skillWakePending[i] = {
          ...cur,
          allySide: cur.allySide | UnitChannel.Buff,
          enemySide: cur.enemySide | UnitChannel.Buff,
        }
      }
    }
    target.syncDerivedStacks()
  }
  if (wave) wave.deferBuffOp(apply)
  else apply()
}

export function notifyUnitAttacked(
  encounter: CombatEncounterState | null,
  _attacker: CombatantState,
  target: CombatantState,
  at: number,
): void {
  if (!isTargetAlive(encounter, target)) return
  const pending = target.pendingSkillIndex
  if (pending < 0 || pending >= target.skills.length) return
  const slot = target.skills[pending]
  const buffSpec = slot.ability.channelingAttackedBuff
  if (!buffSpec || typeof buffSpec !== 'object') return
  const abilityId = abilityText(slot.ability, 'id') ?? ''
  const levelGrowth = 1 + 0.02 * (Math.max(1, slot.level) - 1)
  grantAbilityBuff(buffSpec as AbilityBuffJson, target, target, at, levelGrowth, abilityId, encounter)
}

function consumeJianyiAfterDirectDamage(
  encounter: CombatEncounterState,
  caster: CombatantState,
): void {
  caster.lastDirectDamageAtSeconds = encounter.elapsedSeconds
  if (caster.buffStacks(BuffIds.Jianyi) <= 0) return
  const wave = getActiveWave(encounter)
  if (wave) wave.deferBuffOp(() => caster.removeBuffsWhere((b) => b.buffId === BuffIds.Jianyi))
  else caster.removeBuffsWhere((b) => b.buffId === BuffIds.Jianyi)
}

function resolveAttack(
  encounter: CombatEncounterState,
  attacker: CombatantState,
  target: CombatantState,
  side: bigint,
  at: number,
  events: CombatEvent[],
  statsOverride: CombatantStats | null = null,
  countOverride: number | null = null,
  abilityId: string | null = null,
  doubleAccuracy = false,
  abilityNameOverride: string | null = null,
  extraRanhunStacks = 0,
  zeroDirectDamage = false,
  onHitBuffs: readonly AbilityBuffJson[] | null = null,
  levelGrowth = 1,
  postFinalDamageMultiplier = 1.0,
): boolean {
  if (!attacker.isAlive || !isTargetAlive(encounter, target)) return false
  const wave = getActiveWave(encounter)
  const rawAttackStats = statsOverride ?? attacker.stats
  const resolvedElement = resolveDamageElement(attacker.stats.traits, rawAttackStats.element)
  const attackStats =
    resolvedElement === rawAttackStats.element
      ? rawAttackStats
      : { ...rawAttackStats, element: resolvedElement }
  const context: CombatHitContext = {
    encounter,
    attacker,
    target,
    attackStats,
    abilityId,
    doubleAccuracy,
    zeroDirectDamage,
    postFinalDamageMultiplier,
    side,
    count: countOverride ?? attacker.attackCount,
    atSeconds: at,
    broken: false,
    parried: false,
    hit: false,
    critical: false,
    rawDamage: 0,
    damage: 0,
    actualDamage: 0,
  }
  resolveHit(context)
  const settled = applyCombatDamage(encounter, attacker, target, context.damage)
  context.damage = settled.damage
  context.actualDamage = settled.actual
  const dealtDirectDamage = context.hit && context.damage > 0
  encounter.statsBook?.recordHitUnit(
    attacker,
    target,
    context.attackStats.damageKind,
    context.damage,
    context.hit ? Math.max(0, context.rawDamage - context.damage) : 0,
    context.attackStats.element,
  )
  attacker.attackCount += 1
  if (wave) wave.addThreatDelta(attacker, context.damage)
  else attacker.threat += context.damage
  const mainEventIndex = events.length
  events.push({
    atSeconds: at,
    attackerId: attacker.id,
    targetId: target.id,
    damage: context.damage,
    hit: context.hit,
    damageKind: context.attackStats.damageKind,
    targetHitpointsAfter: target.hitpoints,
    critical: context.critical,
    break: context.broken,
    parried: context.parried,
    abilityId,
    abilityNameOverride,
    healing: 0,
    attackerManapointsAfter: null,
    targetManapointsAfter: null,
    attackerShieldAfter: attacker.shield,
    targetShieldAfter: target.shield,
  })
  encounter.pendingTriggers.push(
    { attacker, target, hitContext: context, events, mainEventIndex },
    { attacker: target, target: attacker, hitContext: context, events, mainEventIndex },
  )
  drainPendingTriggers(encounter)
  if (
    !context.hit &&
    isTargetAlive(encounter, target) &&
    containsTrait(target.stats.traits, TraitIds.Shanshenbu)
  ) {
    const shanshenCd = shanshenbuCooldownSeconds(target.stats.traits, target.stats.traitLevels)
    if (
      target.lastShanshenbuTriggerAtSeconds < 0 ||
      at - target.lastShanshenbuTriggerAtSeconds >= shanshenCd - 1e-9
    ) {
      target.lastShanshenbuTriggerAtSeconds = at
      const grantYixing = () => {
        if (!target.isAlive) return
        const cur = target.buffs.find((b) => b.buffId === BuffIds.Yixing)
        const nextStacks = Math.min(99, (cur ? cur.stacks : 0) + 1)
        const exp = mergeBuffExpiry(cur?.expiresAtSeconds, at + 5.0)
        target.setBuff({
          buffId: BuffIds.Yixing,
          expiresAtSeconds: exp,
          stacks: nextStacks,
          name: '移形',
          resetOnWave: true,
          missingHitpointsPerStack: 0,
          effects: [{ channel: BuffEffectChannel.EvasionPostRatio, value: 1.0 }],
          statRatios: null,
          sourceUnitId: null,
        })
      }
      if (wave) wave.deferBuffOp(grantYixing)
      else grantYixing()
    }
  }
  if (context.hit && extraRanhunStacks > 0) {
    addRanhunStacks(attacker, target, extraRanhunStacks, at, encounter)
  }
  if (context.hit && isTargetAlive(encounter, target) && onHitBuffs && onHitBuffs.length > 0) {
    for (let i = 0; i < onHitBuffs.length; i++) {
      grantAbilityBuff(onHitBuffs[i], attacker, target, at, levelGrowth, abilityId ?? '', encounter)
    }
  }
  return dealtDirectDamage
}

function castSkill(
  encounter: CombatEncounterState,
  caster: CombatantState,
  allies: readonly CombatantState[],
  opponents: readonly CombatantState[],
  target: CombatantState | null,
  slot: SkillSlotDefinition,
  slotIndex: number,
  side: bigint,
  events: CombatEvent[],
): void {
  const wave = getActiveWave(encounter)
  const ability = slot.ability
  const spell = abilityText(ability, 'category') === 'spell'
  const attackBase = spell ? caster.stats.spellAttack : caster.stats.attackPower
  const id = abilityText(ability, 'id') ?? ''
  const levelGrowth = 1 + 0.02 * (Math.max(1, slot.level) - 1)
  const targetType = abilityText(ability, 'targetType')

  if (wave) {
    wave.addThreatDelta(caster, abilityNumber(ability, 'threatGain') * levelGrowth)
    wave.addThreatBonusDelta(caster, abilityNumber(ability, 'threatPoints') * levelGrowth)
  } else {
    caster.threat += abilityNumber(ability, 'threatGain') * levelGrowth
    caster.threatBonusPoints += abilityNumber(ability, 'threatPoints') * levelGrowth
  }

  const skillBonusMult = percentMultiplier(caster.stats.skillBonusPoints, PercentCurveMode.Linear)
  const healOutput =
    attackBase *
      skillBonusMult *
      abilityNumber(ability, 'healingRatio') *
      levelGrowth +
    abilityNumber(ability, 'flatHealing') * levelGrowth

  if (abilityText(ability, 'effectType') === 'heal' || healOutput > 0) {
    const output =
      healOutput *
      percentMultiplier(caster.effectiveStats.effects.healingBonusPoints, PercentCurveMode.Linear)
    const applyHeal = (ally: CombatantState) => {
      if (wave) {
        const eventIdx = events.length
        const rawHeal = Math.max(0, output)
        wave.recordHeal(caster, ally, rawHeal, id, eventIdx)
        events.push({
          atSeconds: encounter.elapsedSeconds,
          attackerId: caster.id,
          targetId: ally.id,
          damage: 0,
          hit: true,
          damageKind: spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
          targetHitpointsAfter: ally.hitpoints,
          critical: false,
          break: false,
          parried: false,
          abilityId: id,
          healing: rawHeal,
          attackerManapointsAfter: caster.manapoints,
          targetManapointsAfter: ally.manapoints,
          attackerShieldAfter: caster.shield,
          targetShieldAfter: ally.shield,
        })
        return
      }
      const healed = Math.min(ally.stats.maxHitpoints - ally.hitpoints, Math.max(0, output))
      ally.hitpoints += healed
      if (healed > 0) encounter.statsBook?.recordHealingUnit(caster, false, healed)
      events.push({
        atSeconds: encounter.elapsedSeconds,
        attackerId: caster.id,
        targetId: ally.id,
        damage: 0,
        hit: true,
        damageKind: spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
        targetHitpointsAfter: ally.hitpoints,
        critical: false,
        break: false,
        parried: false,
        abilityId: id,
        healing: healed,
        attackerManapointsAfter: caster.manapoints,
        targetManapointsAfter: ally.manapoints,
        attackerShieldAfter: caster.shield,
        targetShieldAfter: ally.shield,
      })
    }
    if (targetType === 'self') {
      applyHeal(caster)
    } else if (targetType === 'allAllies') {
      for (let i = 0; i < allies.length; i++) {
        if (allies[i].isAlive) applyHeal(allies[i])
      }
    } else {
      const weakest = lowestHitpointsRatioAlly(allies)
      if (weakest) applyHeal(weakest)
    }
    return
  }

  const maxHpDamageRatio = abilityNumber(ability, 'maxHpDamageRatio')
  const shieldDamageRatio = abilityNumber(ability, 'shieldDamageRatio')
  const hasDamage =
    abilityNumber(ability, 'damageRatio') +
      abilityNumber(ability, 'flatDamage') +
      maxHpDamageRatio +
      shieldDamageRatio >
    0
  const ranhunBase = abilityNumber(ability, 'ranhunBaseStacks')
  const ranhunRatio = abilityNumber(ability, 'ranhunSpellAttackRatio')
  const onHitBuffSpecs = abilityBuffSpecs(ability, 'onHitBuff', 'onHitBuffs')
  const selfBuffSpecs = abilityBuffSpecs(ability, 'selfBuff', 'selfBuffs')
  const hasOffensivePayload =
    hasDamage || ranhunBase > 0 || ranhunRatio > 0 || onHitBuffSpecs.length > 0
  if (targetType === 'self' || (!hasOffensivePayload && targetType !== 'targetEnemy' && targetType !== 'allEnemies')) {
    const shieldMaxHpRatio = abilityNumber(ability, 'shieldMaxHpRatio')
    if (shieldMaxHpRatio > 0) {
      const shieldGain = caster.stats.maxHitpoints * shieldMaxHpRatio * levelGrowth * skillBonusMult
      if (shieldGain > 0) {
        if (wave) wave.addShieldGain(caster, shieldGain)
        else caster.shield += shieldGain
      }
    }
    const buffSpecs = abilityBuffSpecs(ability)
    if (buffSpecs.length > 0) {
      if (targetType === 'lowestHitpointsAlly') {
        const weakest = lowestHitpointsRatioAlly(allies)
        if (weakest) {
          for (let b = 0; b < buffSpecs.length; b++) {
            grantAbilityBuff(buffSpecs[b], caster, weakest, encounter.elapsedSeconds, levelGrowth, id, encounter)
          }
        }
      } else if (targetType === 'allAllies') {
        for (let i = 0; i < allies.length; i++) {
          if (allies[i].isAlive) {
            for (let b = 0; b < buffSpecs.length; b++) {
              grantAbilityBuff(
                buffSpecs[b],
                caster,
                allies[i],
                encounter.elapsedSeconds,
                levelGrowth,
                id,
                encounter,
              )
            }
          }
        }
      } else {
        for (let b = 0; b < buffSpecs.length; b++) {
          grantAbilityBuff(buffSpecs[b], caster, caster, encounter.elapsedSeconds, levelGrowth, id, encounter)
        }
      }
    }
    events.push({
      atSeconds: encounter.elapsedSeconds,
      attackerId: caster.id,
      targetId: caster.id,
      damage: 0,
      hit: true,
      damageKind: spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
      targetHitpointsAfter: caster.hitpoints,
      critical: false,
      break: false,
      parried: false,
      abilityId: id,
      healing: 0,
      attackerManapointsAfter: caster.manapoints,
      targetManapointsAfter: caster.manapoints,
      attackerShieldAfter: caster.shield,
      targetShieldAfter: caster.shield,
    })
    return
  }

  const consumeAllShield = abilityFlag(ability, 'consumeAllShield')
  const currentShield = caster.shield
  if (consumeAllShield && currentShield > 0) {
    if (wave) wave.consumeAllShield(caster)
    else caster.shield = 0
  }
  let skillPower =
    attackBase *
      skillBonusMult *
      abilityNumber(ability, 'damageRatio') *
      levelGrowth +
    abilityNumber(ability, 'flatDamage') * levelGrowth
  if (maxHpDamageRatio > 0 || shieldDamageRatio > 0) {
    skillPower +=
      (caster.stats.maxHitpoints * maxHpDamageRatio +
        (consumeAllShield ? currentShield : caster.shield) * shieldDamageRatio) *
      skillBonusMult *
      levelGrowth
  }

  const stats: CombatantStats = {
    ...caster.stats,
    attackPower: skillPower,
    damageKind: spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
    element: abilityElement(ability),
  }

  const consumeBuffId = abilityText(ability, 'consumeBuffId')
  const consumeBuffExtraPerStack = abilityNumber(ability, 'consumeBuffExtraDamagePerStack')
  const consumedBuffStacks = consumeBuffId ? caster.buffStacks(consumeBuffId) : 0
  const postFinalDamageMultiplier =
    consumedBuffStacks > 0 && consumeBuffExtraPerStack > 0
      ? 1.0 + consumeBuffExtraPerStack * consumedBuffStacks
      : 1.0

  const count = caster.skillCastCount + 1
  const hasYixing = caster.buffStacks(BuffIds.Yixing) > 0
  const abilityNameOverride = hasYixing ? abilityText(ability, 'yixingMorphName') : null
  const effectiveTargetType = hasYixing
    ? (abilityText(ability, 'yixingMorphTargetType') ?? targetType)
    : targetType
  const ignorePrimaryThreat =
    hasYixing && containsTrait(caster.stats.traits, TraitIds.Jianyuxin)
  const extraRanhunStacks =
    ranhunBase > 0 || ranhunRatio > 0
      ? Math.max(
          1,
          Math.floor(
            (Math.max(0, attackBase) * skillBonusMult * ranhunRatio + ranhunBase) * levelGrowth +
              1e-9,
          ),
        )
      : 0
  const rawMaxTargets = ability.maxTargets
  const maxTargets =
    typeof rawMaxTargets === 'number' && Number.isFinite(rawMaxTargets)
      ? Math.trunc(rawMaxTargets)
      : 0
  const zeroDirectDamage = !hasDamage
  let killedAnyTarget = false
  let dealtAnyDirectDamage = false
  const attackedTargets: CombatantState[] = []
  const strike = (foe: CombatantState, rollCountOverride: number | null) => {
    if (!attackedTargets.includes(foe)) attackedTargets.push(foe)
    const wasAlive = isTargetAlive(encounter, foe)
    const dealt = resolveAttack(
      encounter,
      caster,
      foe,
      side,
      encounter.elapsedSeconds,
      events,
      stats,
      rollCountOverride,
      id,
      hasYixing,
      abilityNameOverride,
      extraRanhunStacks,
      zeroDirectDamage,
      onHitBuffSpecs,
      levelGrowth,
      postFinalDamageMultiplier,
    )
    if (dealt) dealtAnyDirectDamage = true
    if (wasAlive && !isTargetAlive(encounter, foe)) killedAnyTarget = true
  }

  if (effectiveTargetType === 'allEnemies') {
    const picked = selectTargets(
      opponents,
      maxTargets > 0 ? maxTargets : 0x7fffffff,
      encounter.seed,
      side,
      count,
      17n,
      ignorePrimaryThreat,
    )
    if (picked.length === 0) return
    for (let i = 0; i < picked.length; i++) {
      strike(picked[i], count)
    }
  } else {
    let single =
      !ignorePrimaryThreat && target && isTargetAlive(encounter, target)
        ? target
        : selectTarget(opponents, encounter.seed, side, count, 16n, ignorePrimaryThreat)
    if (single === null) return
    strike(single, count)

    const hitCount = Math.max(1, Math.trunc(abilityNumber(ability, 'hitCount')))
    for (let hitIndex = 1; hitIndex < hitCount && isTargetAlive(encounter, single); hitIndex++) {
      strike(single, null)
    }

    const chainMax = Math.trunc(abilityNumber(ability, 'chainOnKillCount'))
    for (let chainIndex = 0; chainIndex < chainMax && !isTargetAlive(encounter, single); chainIndex++) {
      single = selectTarget(
        opponents,
        encounter.seed,
        side,
        count,
        20n + BigInt(chainIndex),
      )
      if (single === null || !isTargetAlive(encounter, single)) break
      strike(single, count)
    }
  }
  for (let i = 0; i < attackedTargets.length; i++) {
    notifyUnitAttacked(encounter, caster, attackedTargets[i], encounter.elapsedSeconds)
  }
  if (consumeBuffId && consumedBuffStacks > 0) {
    if (wave) wave.deferBuffOp(() => caster.removeBuffsWhere((b) => b.buffId === consumeBuffId))
    else caster.removeBuffsWhere((b) => b.buffId === consumeBuffId)
  }
  if (dealtAnyDirectDamage) {
    consumeJianyiAfterDirectDamage(encounter, caster)
  }
  if (hasYixing) {
    if (wave) wave.deferBuffOp(() => caster.consumeOneBuffStack(BuffIds.Yixing))
    else caster.consumeOneBuffStack(BuffIds.Yixing)
  }
  if (
    hasYixing &&
    killedAnyTarget &&
    abilityFlag(ability, 'resetCooldownOnYixingKill') &&
    caster.skillCooldownUntil &&
    slotIndex >= 0 &&
    slotIndex < caster.skillCooldownUntil.length
  ) {
    caster.skillCooldownUntil[slotIndex] = 0
  }
  if (isTargetAlive(encounter, caster) && selfBuffSpecs.length > 0) {
    for (let i = 0; i < selfBuffSpecs.length; i++) {
      const spec = selfBuffSpecs[i]
      if (abilityFlag(spec, 'skipIfHasYixing') && hasYixing) continue
      grantAbilityBuff(spec, caster, caster, encounter.elapsedSeconds, levelGrowth, id, encounter)
    }
  }
}

function resolvePending(
  encounter: CombatEncounterState,
  unit: CombatantState,
  allies: readonly CombatantState[],
  opponents: readonly CombatantState[],
  target: CombatantState | null,
  side: bigint,
  events: CombatEvent[],
): void {
  const pending = unit.pendingSkillIndex
  unit.pendingSkillIndex = -1
  if (pending >= 0 && pending < unit.skills.length) {
    castSkill(
      encounter,
      unit,
      allies,
      opponents,
      target,
      unit.skills[pending],
      pending,
      side + SKILL_SIDE_OFFSET,
      events,
    )
    unit.skillCastCount += 1
    return
  }
  if (target !== null && !containsTrait(unit.stats.traits, TraitIds.Cangfeng)) {
    const dealtBasicDamage = resolveAttack(
      encounter,
      unit,
      target,
      side,
      encounter.elapsedSeconds,
      events,
    )
    notifyUnitAttacked(encounter, unit, target, encounter.elapsedSeconds)
    if (dealtBasicDamage) {
      consumeJianyiAfterDirectDamage(encounter, unit)
    }
  }
}

function buildSkillWakeMasks(
  skills: readonly SkillSlotDefinition[],
  selfInEnemies: boolean,
): SlotWakeMask[] {
  const masks: SlotWakeMask[] = new Array(skills.length)
  for (let i = 0; i < skills.length; i++) {
    const trigs = skills[i].triggers
    masks[i] = trigs && trigs.length > 0 ? resolveTriggerListWakeMask(trigs, selfInEnemies) : EMPTY_WAKE_MASK
  }
  return masks
}

function buildFullPending(count: number): SlotWakeMask[] {
  const arr: SlotWakeMask[] = new Array(count)
  for (let i = 0; i < count; i++) arr[i] = FULL_WAKE_MASK
  return arr
}

export function decideNextAction(
  encounter: CombatEncounterState,
  unit: CombatantState,
  allies: readonly CombatantState[],
  opponents: readonly CombatantState[],
  target: CombatantState | null,
): void {
  const skills = unit.skills
  if (skills.length > 0) {
    let cooldowns = unit.skillCooldownUntil
    if (!cooldowns) {
      cooldowns = new Array<number>(skills.length).fill(0)
      unit.skillCooldownUntil = cooldowns
    }
    const selfInEnemies = allies === encounter.enemies
    let masks = unit.skillWakeMasks
    if (!masks) {
      masks = buildSkillWakeMasks(skills, selfInEnemies)
      unit.skillWakeMasks = masks
    }
    let pending = unit.skillWakePending
    if (!pending) {
      pending = buildFullPending(skills.length)
      unit.skillWakePending = pending
    }
    let verdicts = unit.skillTriggerVerdicts
    if (!verdicts) {
      verdicts = new Array<boolean | null>(skills.length).fill(null)
      unit.skillTriggerVerdicts = verdicts
    }
    let verdictTargets = unit.skillVerdictTargets
    if (!verdictTargets) {
      verdictTargets = new Array<CombatantState | null>(skills.length).fill(null)
      unit.skillVerdictTargets = verdictTargets
    }
    const tracking =
      encounter.wakeTracking && !containsTrait(unit.stats.traits, TraitIds.Cangfeng)
    for (let i = 0; i < skills.length; i++) {
      const slot = skills[i]
      if (cooldowns[i] > encounter.elapsedSeconds + 1e-12) continue
      const triggers = slot.triggers
      if (triggers && triggers.length > 0) {
        const cached = verdicts[i]
        if (
          cached !== null &&
          tracking &&
          verdictTargets[i] === target &&
          !maskIntersects(masks[i], pending[i])
        ) {
          if (!cached) continue
        } else {
          const matched = evaluateTriggers(
            triggers,
            unit,
            target,
            allies,
            opponents,
            encounter.elapsedSeconds,
          )
          verdicts[i] = matched
          verdictTargets[i] = target
          pending[i] = EMPTY_WAKE_MASK
          if (!matched) continue
        }
      }
      const hpCostRatio = abilityNumber(slot.ability, 'hpCostMaxHpRatio')
      const hpCost = hpCostRatio > 0 ? unit.stats.maxHitpoints * hpCostRatio : 0
      if (hpCost > 0 && unit.hitpoints <= hpCost + 1e-9) continue
      const manaCost = abilityNumber(slot.ability, 'manaCost')
      if (unit.manapoints < manaCost) continue
      unit.manapoints -= manaCost
      if (hpCost > 0) {
        unit.hitpoints = Math.max(1e-9, unit.hitpoints - hpCost)
        unit.syncDerivedStacks()
      }
      const castSeconds = Math.max(
        1e-9,
        (abilityNumber(slot.ability, 'castMilliseconds') / 1000) *
          percentMultiplier(unit.effectiveStats.actionSpeedPoints, PercentCurveMode.NonLinear),
      )
      unit.pendingSkillIndex = i
      unit.nextAttackAtSeconds = encounter.elapsedSeconds + castSeconds
      const cdMs = abilityNumber(slot.ability, 'cooldownMilliseconds')
      const cdMult = percentMultiplier(
        unit.stats.cooldownReductionPoints,
        PercentCurveMode.NonLinear,
      )
      cooldowns[i] = unit.nextAttackAtSeconds + (cdMs / 1000) * cdMult
      return
    }
  }
  unit.pendingSkillIndex = -1
  if (containsTrait(unit.stats.traits, TraitIds.Cangfeng)) {
    let idleNextAt = Math.floor(encounter.elapsedSeconds + 1e-9) + 1.0
    if (unit.skillCooldownUntil) {
      for (let i = 0; i < unit.skillCooldownUntil.length && i < skills.length; i++) {
        if (
          unit.skillCooldownUntil[i] > encounter.elapsedSeconds + 1e-9 &&
          unit.skillCooldownUntil[i] < idleNextAt
        ) {
          idleNextAt = unit.skillCooldownUntil[i]
        }
      }
    }
    unit.nextAttackAtSeconds = Math.max(encounter.elapsedSeconds + 1e-6, idleNextAt)
    return
  }
  unit.nextAttackAtSeconds =
    encounter.elapsedSeconds +
    unit.stats.attackIntervalSeconds *
      percentMultiplier(
        unit.stats.attackSpeedPoints + unit.buffTotal(BuffEffectChannel.AttackSpeedPoints),
        PercentCurveMode.NonLinear,
      ) *
      percentMultiplier(unit.effectiveStats.actionSpeedPoints, PercentCurveMode.NonLinear)
}

function decideNextActionsSimultaneously(
  encounter: CombatEncounterState,
  units: readonly CombatantState[],
): void {
  const plans: Array<{
    unit: CombatantState
    skillIndex: number
    manaCost: number
    hpCost: number
    nextAttackAt: number
    cdUntil: number
  }> = []
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    if (!unit.isAlive) continue
    const allies = encounter.alliesOf(unit)
    const opponents = encounter.opponentsOf(unit)
    const side = symmetricUnitSide(encounter.seed, unit.side, unit.sideIndex)
    const target = selectTarget(
      opponents,
      encounter.seed,
      side,
      unit.attackCount,
      SYMMETRIC_DECIDE_TARGET_CHANNEL,
    )
    const savedMana = unit.manapoints
    const savedHp = unit.hitpoints
    const savedPending = unit.pendingSkillIndex
    const savedNext = unit.nextAttackAtSeconds
    const savedCd = unit.skillCooldownUntil ? [...unit.skillCooldownUntil] : null
    decideNextAction(encounter, unit, allies, opponents, target)
    const chosenSkill = unit.pendingSkillIndex
    const spentMana = savedMana - unit.manapoints
    const spentHp = savedHp - unit.hitpoints
    const chosenNext = unit.nextAttackAtSeconds
    const chosenCd = chosenSkill >= 0 && unit.skillCooldownUntil ? unit.skillCooldownUntil[chosenSkill] : 0
    unit.manapoints = savedMana
    if (unit.hitpoints !== savedHp) {
      unit.hitpoints = savedHp
      unit.syncDerivedStacks()
    }
    unit.pendingSkillIndex = savedPending
    unit.nextAttackAtSeconds = savedNext
    if (savedCd) unit.skillCooldownUntil = savedCd
    plans.push({
      unit,
      skillIndex: chosenSkill,
      manaCost: spentMana,
      hpCost: spentHp,
      nextAttackAt: chosenNext,
      cdUntil: chosenCd,
    })
  }
  for (let i = 0; i < plans.length; i++) {
    const p = plans[i]
    if (p.skillIndex >= 0) {
      p.unit.manapoints -= p.manaCost
      if (p.hpCost > 0) {
        p.unit.hitpoints = Math.max(1e-9, p.unit.hitpoints - p.hpCost)
        p.unit.syncDerivedStacks()
      }
      p.unit.pendingSkillIndex = p.skillIndex
      p.unit.nextAttackAtSeconds = p.nextAttackAt
      if (!p.unit.skillCooldownUntil) {
        p.unit.skillCooldownUntil = new Array<number>(p.unit.skills.length).fill(0)
      }
      p.unit.skillCooldownUntil[p.skillIndex] = p.cdUntil
    } else {
      p.unit.pendingSkillIndex = -1
      p.unit.nextAttackAtSeconds = p.nextAttackAt
    }
  }
}

export function decideInitialActions(encounter: CombatEncounterState): void {
  applyBattleStartReactions(encounter)
  if (encounter.isSymmetric) {
    const units: CombatantState[] = []
    for (let i = 0; i < encounter.sideA.length; i++) {
      if (encounter.sideA[i].isAlive && encounter.sideA[i].pendingSkillIndex < 0) units.push(encounter.sideA[i])
    }
    for (let i = 0; i < encounter.sideB.length; i++) {
      if (encounter.sideB[i].isAlive && encounter.sideB[i].pendingSkillIndex < 0) units.push(encounter.sideB[i])
    }
    decideNextActionsSimultaneously(encounter, units)
    return
  }
  for (let i = 0; i < encounter.enemies.length; i++) {
    const enemy = encounter.enemies[i]
    if (enemy.isAlive && enemy.pendingSkillIndex < 0) {
      decideNextAction(
        encounter,
        enemy,
        encounter.enemies,
        encounter.playerAllies,
        selectTarget(encounter.playerAllies, encounter.seed, BigInt(1 + i), enemy.attackCount, 19n),
      )
    }
  }
  for (let i = 0; i < encounter.playerAllies.length; i++) {
    const ally = encounter.playerAllies[i]
    if (ally.isAlive && ally.pendingSkillIndex < 0) {
      decideNextAction(
        encounter,
        ally,
        encounter.playerAllies,
        encounter.enemies,
        selectEnemyTarget(encounter, allySide(i), 0, 3n),
      )
    }
  }
}

// ---------- 相位系统（BuffTick / AllyRespawn / AllyAct / EnemyAct / AllyDowned / Outcome） ----------

function tickUnitBuffEffects(
  unit: CombatantState,
  statsBook: CombatUnitStatsBook | null,
  regenTick: boolean,
  secondTick: boolean,
): void {
  const buffs = unit.buffs
  if (secondTick) {
    let threatPoints = 0
    let hpPerSecond = 0
    for (let i = 0; i < buffs.length; i++) {
      const effects = buffs[i].effects
      if (!effects) continue
      const stacks = buffs[i].stacks
      for (let j = 0; j < effects.length; j++) {
        const eff = effects[j]
        if (eff.channel === BuffEffectChannel.ThreatPerSecondMaxHpPoints) {
          threatPoints += eff.value * stacks
        } else if (eff.channel === BuffEffectChannel.HpRegenPerSecond) {
          hpPerSecond += eff.value * stacks
        }
      }
    }
    if (threatPoints !== 0) {
      unit.threat = Math.max(0, unit.threat + (unit.stats.maxHitpoints * threatPoints) / 100)
    }
    if (hpPerSecond !== 0 && unit.isAlive) {
      const hpBefore = unit.hitpoints
      unit.hitpoints = Math.max(0, Math.min(unit.stats.maxHitpoints, unit.hitpoints + hpPerSecond))
      if (unit.hitpoints > hpBefore) {
        statsBook?.recordHealingUnit(unit, true, unit.hitpoints - hpBefore)
      }
      unit.syncDerivedStacks()
    }
  }
  if (!regenTick) return
  let hp = unit.stats.hpRegen
  let mp = unit.stats.mpRegen
  let hpPost = 0
  let mpPost = 0
  for (let i = 0; i < buffs.length; i++) {
    const effects = buffs[i].effects
    if (!effects) continue
    const stacks = buffs[i].stacks
    for (let j = 0; j < effects.length; j++) {
      const eff = effects[j]
      if (eff.channel === BuffEffectChannel.HpRegen) hp += eff.value * stacks
      else if (eff.channel === BuffEffectChannel.HpRegenPostRatio) hpPost += eff.value * stacks
      else if (eff.channel === BuffEffectChannel.MpRegen) mp += eff.value * stacks
      else if (eff.channel === BuffEffectChannel.MpRegenPostRatio) mpPost += eff.value * stacks
    }
  }
  hp *= 1 + hpPost
  mp *= 1 + mpPost
  if (unit.isAlive && hp !== 0) {
    const hpBefore = unit.hitpoints
    unit.hitpoints = Math.max(0, Math.min(unit.stats.maxHitpoints, unit.hitpoints + hp))
    if (unit.hitpoints > hpBefore) {
      statsBook?.recordHealingUnit(unit, true, unit.hitpoints - hpBefore)
    }
    unit.syncDerivedStacks()
  }
  if (mp !== 0) {
    unit.manapoints = Math.max(0, Math.min(unit.stats.maxManapoints, unit.manapoints + mp))
  }
  if (unit.isAlive && unit.buffStacks('formation.tianlei') > 0) {
    unit.manapoints = Math.max(0, unit.manapoints - unit.stats.maxManapoints * 0.01)
  }
  if (unit.isAlive && unit.buffStacks('formation.xuesha') > 0) {
    unit.hitpoints = Math.max(1, unit.hitpoints - unit.stats.maxHitpoints * 0.01)
  }
}

function applyBuffTickPhase(encounter: CombatEncounterState, effectAt: number, events: CombatEvent[]): void {
  const regenTick = Math.abs(effectAt / BUFF_TICK_SECONDS - Math.round(effectAt / BUFF_TICK_SECONDS)) <= 1e-9
  const secondTick =
    Math.abs(effectAt / THREAT_TICK_SECONDS - Math.round(effectAt / THREAT_TICK_SECONDS)) <= 1e-9
  if (encounter.isSymmetric) encounter.deferAliveCountNotifications = true
  try {
    if (regenTick || secondTick) {
      const statsBook = encounter.statsBook
      for (let i = 0; i < encounter.playerAllies.length; i++) {
        const unit = encounter.playerAllies[i]
        const aliveBefore = unit.isAlive
        tickUnitBuffEffects(unit, statsBook, regenTick, secondTick)
        if (regenTick) applyXueqihuadun(encounter, unit)
        if (aliveBefore !== unit.isAlive) encounter.notifyAliveCountsChanged()
        if (secondTick) {
          tickRanhun(encounter, unit, effectAt, events)
          if (effectAt > 1e-9) unit.decayBuffStacksPerSecond()
        }
      }
      for (let i = 0; i < encounter.enemies.length; i++) {
        const unit = encounter.enemies[i]
        const aliveBefore = unit.isAlive
        tickUnitBuffEffects(unit, statsBook, regenTick, secondTick)
        if (regenTick) applyXueqihuadun(encounter, unit)
        if (aliveBefore !== unit.isAlive) encounter.notifyAliveCountsChanged()
        if (secondTick) {
          tickRanhun(encounter, unit, effectAt, events)
          if (effectAt > 1e-9) unit.decayBuffStacksPerSecond()
        }
      }
    }
    for (let i = 0; i < encounter.playerAllies.length; i++) {
      const ally = encounter.playerAllies[i]
      if (ally.buffs.length > 0) {
        ally.removeBuffsWhere((b) => b.expiresAtSeconds <= effectAt + 1e-9)
      }
    }
    for (let i = 0; i < encounter.enemies.length; i++) {
      const enemy = encounter.enemies[i]
      if (enemy.buffs.length > 0) {
        enemy.removeBuffsWhere((b) => b.expiresAtSeconds <= effectAt + 1e-9)
      }
    }
  } finally {
    if (encounter.isSymmetric) encounter.flushAliveCountNotifications()
  }
}

function interruptUnitCast(unit: CombatantState): void {
  const pending = unit.pendingSkillIndex
  unit.pendingSkillIndex = -1
  if (pending < 0 || pending >= unit.skills.length) return
  const ability = unit.skills[pending].ability
  unit.manapoints = Math.min(
    unit.stats.maxManapoints,
    unit.manapoints + abilityNumber(ability, 'manaCost'),
  )
  const hpCostRatio = abilityNumber(ability, 'hpCostMaxHpRatio')
  if (hpCostRatio > 0 && unit.isAlive) {
    unit.hitpoints = Math.min(
      unit.stats.maxHitpoints,
      unit.hitpoints + unit.stats.maxHitpoints * hpCostRatio,
    )
    unit.syncDerivedStacks()
  }
  if (unit.skillCooldownUntil && pending < unit.skillCooldownUntil.length) {
    unit.skillCooldownUntil[pending] = 0
  }
}

function interruptAllCasts(encounter: CombatEncounterState): void {
  for (let i = 0; i < encounter.playerAllies.length; i++) interruptUnitCast(encounter.playerAllies[i])
  for (let i = 0; i < encounter.enemies.length; i++) interruptUnitCast(encounter.enemies[i])
}

function anyAliveUnits(units: readonly CombatantState[]): boolean {
  for (let i = 0; i < units.length; i++) {
    if (units[i].isAlive) return true
  }
  return false
}

function applyOutcomePhase(encounter: CombatEncounterState, events: CombatEvent[]): void {
  const alliesAlive = anyAliveUnits(encounter.playerAllies)
  const enemiesAlive = anyAliveUnits(encounter.enemies)
  if (!alliesAlive && !encounter.wipeEndsBattle && Number.isFinite(encounter.nextAllyRespawnAtSeconds)) {
    encounter.outcome = CombatOutcome.Running
  } else if (alliesAlive && enemiesAlive) {
    encounter.outcome = CombatOutcome.Running
  } else if (alliesAlive && !enemiesAlive) {
    encounter.outcome = CombatOutcome.PlayerWon
  } else if (!alliesAlive && enemiesAlive) {
    encounter.outcome = CombatOutcome.EnemyWon
  } else {
    encounter.outcome = CombatOutcome.Draw
  }
  if (encounter.outcome !== CombatOutcome.Running) {
    settleWaveEndShields(encounter, events)
  }
}

// ---------- 边界推进与调度 ----------

function scanBuffUnit(
  unit: CombatantState,
  state: { next: number; regenPending: boolean; threatTickPending: boolean },
): void {
  if (unit.stats.hpRegen !== 0 || unit.stats.mpRegen !== 0) state.regenPending = true
  const buffs = unit.buffs
  if (buffs.length > 0) {
    if (unit.buffRegenTickPending) state.regenPending = true
    if (unit.buffSecondTickPending) state.threatTickPending = true
    for (let i = 0; i < buffs.length; i++) {
      if (buffs[i].expiresAtSeconds < state.next) state.next = buffs[i].expiresAtSeconds
    }
  }
  if (
    !state.regenPending &&
    (buffs.length > 0 || unit.externalBuffStacks !== null) &&
    (unit.buffStacks('formation.tianlei') > 0 || unit.buffStacks('formation.xuesha') > 0)
  ) {
    state.regenPending = true
  }
  if (!state.regenPending && containsTrait(unit.stats.traits, TraitIds.Xueqihuadun)) {
    state.regenPending = true
  }
  if (!state.threatTickPending && containsTrait(unit.stats.traits, TraitIds.Yangjianshu)) {
    state.threatTickPending = true
  }
}

function nextBuffBoundary(encounter: CombatEncounterState): number {
  const state = {
    next: Number.POSITIVE_INFINITY,
    regenPending: false,
    threatTickPending: false,
  }
  for (let i = 0; i < encounter.playerAllies.length; i++) {
    scanBuffUnit(encounter.playerAllies[i], state)
  }
  for (let i = 0; i < encounter.enemies.length; i++) {
    scanBuffUnit(encounter.enemies[i], state)
  }
  if (state.regenPending) {
    state.next = Math.min(
      state.next,
      (Math.floor(encounter.elapsedSeconds / BUFF_TICK_SECONDS) + 1) * BUFF_TICK_SECONDS,
    )
  }
  if (state.threatTickPending) {
    state.next = Math.min(
      state.next,
      (Math.floor(encounter.elapsedSeconds / THREAT_TICK_SECONDS) + 1) * THREAT_TICK_SECONDS,
    )
  }
  return state.next
}

function naturalNextAttackOrRespawn(encounter: CombatEncounterState): number {
  if (encounter.isAllAlliesDowned) return encounter.nextAllyRespawnAtSeconds
  let next = Number.POSITIVE_INFINITY
  for (let i = 0; i < encounter.playerAllies.length; i++) {
    const ally = encounter.playerAllies[i]
    if (ally.isAlive && ally.nextAttackAtSeconds < next) next = ally.nextAttackAtSeconds
    else if (!ally.isAlive && ally.respawnAtSeconds < next) next = ally.respawnAtSeconds
  }
  for (let i = 0; i < encounter.enemies.length; i++) {
    const enemy = encounter.enemies[i]
    if (enemy.isAlive && enemy.nextAttackAtSeconds < next) next = enemy.nextAttackAtSeconds
  }
  return next
}

export function nextCombatBoundarySeconds(
  encounter: CombatEncounterState,
  targetSeconds: number,
): number {
  const next = naturalNextAttackOrRespawn(encounter)
  const effectAt = nextBuffBoundary(encounter)
  return Math.min(targetSeconds, Math.min(next, effectAt))
}

export type CombatBoundaryAfterHook = (
  encounter: CombatEncounterState,
  delta: CombatFactDelta,
  acted: boolean,
) => void

export class CombatBoundaryHookDispatcher {
  private readonly afterBoundary: CombatBoundaryAfterHook
  private readonly tracker: CombatFactTracker | null

  constructor(afterBoundary: CombatBoundaryAfterHook, tracked: CombatEncounterState | null = null) {
    this.afterBoundary = afterBoundary
    this.tracker = tracked ? new CombatFactTracker(tracked) : null
  }

  markExternal(): void {
    this.tracker?.markExternal()
  }

  onOutcome(encounter: CombatEncounterState, acted: boolean): void {
    const delta = this.tracker ? this.tracker.advance(encounter) : FULL_FACT_DELTA
    foldPendingWake(encounter, delta)
    this.afterBoundary(encounter, delta, acted)
  }
}

function advanceCombatBoundary(
  encounter: CombatEncounterState,
  targetSeconds: number,
  events: CombatEvent[],
  playerDeaths: { list: number[] | null },
  playerRespawns: { list: number[] | null },
  allyDeaths: { list: AllyMark[] | null },
  allyRespawns: { list: AllyMark[] | null },
  hook: CombatBoundaryHookDispatcher | null,
): boolean {
  const nextAtkOrRespawn = naturalNextAttackOrRespawn(encounter)
  const effectAt = nextBuffBoundary(encounter)
  const naturalBoundary = Math.min(nextAtkOrRespawn, effectAt)
  if (!Number.isFinite(naturalBoundary)) {
    throw new Error('战斗攻击时间无效。')
  }
  if (naturalBoundary > targetSeconds) {
    encounter.elapsedSeconds = targetSeconds
    return false
  }
  encounter.elapsedSeconds = Math.max(encounter.elapsedSeconds, naturalBoundary)

  let acted = false
  if (effectAt <= encounter.elapsedSeconds + 1e-12) {
    applyBuffTickPhase(encounter, effectAt, events)
  }

  // 倒地复活
  for (let i = 0; i < encounter.playerAllies.length; i++) {
    const ally = encounter.playerAllies[i]
    if (
      ally.isAlive ||
      !Number.isFinite(ally.respawnAtSeconds) ||
      ally.respawnAtSeconds > encounter.elapsedSeconds + 1e-12
    ) {
      continue
    }
    const can = encounter.canRespawn(i)
    if (!can.ok) {
      ally.respawnAtSeconds = Math.max(can.retryAtSeconds, ally.respawnAtSeconds)
      ally.respawnWaitingUntilSeconds = ally.respawnAtSeconds
      continue
    }
    ally.respawnWaitingUntilSeconds = Number.POSITIVE_INFINITY
    ally.hitpoints = ally.stats.maxHitpoints
    ally.respawnAtSeconds = Number.POSITIVE_INFINITY
    ally.respawnDeadlineSeconds = Number.POSITIVE_INFINITY
    encounter.notifyAliveCountsChanged()
    decideInitialActions(encounter)
    if (i === encounter.selfIndex) {
      ;(playerRespawns.list ??= []).push(encounter.elapsedSeconds)
    } else {
      ;(allyRespawns.list ??= []).push({ memberIndex: i, atSeconds: encounter.elapsedSeconds })
    }
    acted = true
  }

  // 出手与倒地
  if (encounter.isSymmetric) {
    const dueUnits: CombatantState[] = []
    const flip = (encounter.seed & 1n) !== 0n
    const firstSide = flip ? encounter.sideB : encounter.sideA
    const secondSide = flip ? encounter.sideA : encounter.sideB
    for (let i = 0; i < firstSide.length; i++) {
      if (firstSide[i].isAlive && firstSide[i].nextAttackAtSeconds <= encounter.elapsedSeconds + 1e-12) {
        dueUnits.push(firstSide[i])
      }
    }
    for (let i = 0; i < secondSide.length; i++) {
      if (secondSide[i].isAlive && secondSide[i].nextAttackAtSeconds <= encounter.elapsedSeconds + 1e-12) {
        dueUnits.push(secondSide[i])
      }
    }
    if (dueUnits.length === 1) {
      const u = dueUnits[0]
      const allies = encounter.alliesOf(u)
      const opponents = encounter.opponentsOf(u)
      const side = symmetricUnitSide(encounter.seed, u.side, u.sideIndex)
      const target = selectTarget(
        opponents,
        encounter.seed,
        side,
        u.attackCount,
        SYMMETRIC_RESOLVE_TARGET_CHANNEL,
      )
      resolvePending(encounter, u, allies, opponents, target, side, events)
      if (u.isAlive) {
        decideNextAction(
          encounter,
          u,
          allies,
          opponents,
          selectTarget(opponents, encounter.seed, side, u.attackCount, SYMMETRIC_DECIDE_TARGET_CHANNEL),
        )
      }
      acted = true
    } else if (dueUnits.length > 1) {
      const wave = new SimultaneousCombatWave(events.length)
      encounter.activeWave = wave
      encounter.deferAliveCountNotifications = true
      try {
        for (let i = 0; i < dueUnits.length; i++) {
          wave.beginActor()
          const u = dueUnits[i]
          const allies = encounter.alliesOf(u)
          const opponents = encounter.opponentsOf(u)
          const side = symmetricUnitSide(encounter.seed, u.side, u.sideIndex)
          const target = selectTarget(
            opponents,
            encounter.seed,
            side,
            u.attackCount,
            SYMMETRIC_RESOLVE_TARGET_CHANNEL,
          )
          resolvePending(encounter, u, allies, opponents, target, side, events)
        }
      } finally {
        encounter.activeWave = null
        wave.commit(encounter, events)
        encounter.flushAliveCountNotifications()
      }
      decideNextActionsSimultaneously(encounter, dueUnits)
      acted = true
    }
  } else if (!encounter.isAllAlliesDowned) {
    for (let i = 0; i < encounter.playerAllies.length; i++) {
      const ally = encounter.playerAllies[i]
      if (ally.isAlive && ally.nextAttackAtSeconds <= encounter.elapsedSeconds + 1e-12) {
        const side = allySide(i)
        resolvePending(
          encounter,
          ally,
          encounter.playerAllies,
          encounter.enemies,
          selectEnemyTarget(encounter, side, ally.attackCount, 3n),
          side,
          events,
        )
        decideNextAction(
          encounter,
          ally,
          encounter.playerAllies,
          encounter.enemies,
          selectEnemyTarget(encounter, side, ally.attackCount, 3n),
        )
        acted = true
      }
    }
    for (let i = 0; i < encounter.enemies.length; i++) {
      const enemy = encounter.enemies[i]
      if (enemy.isAlive && enemy.nextAttackAtSeconds <= encounter.elapsedSeconds + 1e-12) {
        const side = BigInt(1 + i)
        const target = selectTarget(
          encounter.playerAllies,
          encounter.seed,
          side,
          enemy.attackCount,
          18n,
        )
        resolvePending(
          encounter,
          enemy,
          encounter.enemies,
          encounter.playerAllies,
          target,
          side,
          events,
        )
        decideNextAction(
          encounter,
          enemy,
          encounter.enemies,
          encounter.playerAllies,
          selectTarget(encounter.playerAllies, encounter.seed, side, enemy.attackCount, 19n),
        )
        acted = true
      }
    }
    for (let i = 0; i < encounter.playerAllies.length; i++) {
      const ally = encounter.playerAllies[i]
      if (ally.isAlive || Number.isFinite(ally.respawnAtSeconds) || encounter.playerRespawnSeconds <= 0) {
        continue
      }
      ally.respawnAtSeconds = encounter.elapsedSeconds + encounter.playerRespawnSeconds
      ally.respawnDeadlineSeconds =
        encounter.playerRespawnSeconds > 0
          ? encounter.elapsedSeconds + encounter.playerRespawnSeconds * 2
          : Number.POSITIVE_INFINITY
      if (encounter.isAllAlliesDowned) interruptAllCasts(encounter)
      else interruptUnitCast(ally)
      if (i === encounter.selfIndex) {
        ;(playerDeaths.list ??= []).push(encounter.elapsedSeconds)
      } else {
        ;(allyDeaths.list ??= []).push({ memberIndex: i, atSeconds: encounter.elapsedSeconds })
      }
    }
  }

  encounter.sequence += 1
  applyOutcomePhase(encounter, events)
  hook?.onOutcome(encounter, acted)
  return true
}

export function advanceCombatTo(
  encounter: CombatEncounterState,
  targetSeconds: number,
  hook: CombatBoundaryHookDispatcher | null = null,
): CombatAdvanceResult {
  const from = encounter.elapsedSeconds
  const events: CombatEvent[] = []
  const playerDeaths: { list: number[] | null } = { list: null }
  const playerRespawns: { list: number[] | null } = { list: null }
  const allyDeaths: { list: AllyMark[] | null } = { list: null }
  const allyRespawns: { list: AllyMark[] | null } = { list: null }

  if (encounter.outcome === CombatOutcome.Running) {
    applyOutcomePhase(encounter, events)
    hook?.onOutcome(encounter, false)
  }

  const cappedTarget = Math.min(targetSeconds, encounter.maxBattleSeconds)
  let eventCount = 0
  while (
    encounter.outcome === CombatOutcome.Running &&
    encounter.elapsedSeconds < cappedTarget - 1e-12
  ) {
    if (++eventCount > MAX_EVENTS_PER_ADVANCE) {
      throw new Error('战斗推进超过最大事件数。')
    }
    if (
      !advanceCombatBoundary(
        encounter,
        cappedTarget,
        events,
        playerDeaths,
        playerRespawns,
        allyDeaths,
        allyRespawns,
        hook,
      )
    ) {
      break
    }
  }

  if (
    encounter.outcome === CombatOutcome.Running &&
    Number.isFinite(encounter.maxBattleSeconds) &&
    encounter.elapsedSeconds >= encounter.maxBattleSeconds - 1e-12
  ) {
    encounter.outcome = CombatOutcome.EnemyWon
    encounter.timedOut = true
  }

  return {
    encounterId: encounter.encounterId,
    fromSeconds: from,
    toSeconds: targetSeconds,
    outcome: encounter.outcome,
    events,
    sequence: encounter.sequence,
    playerDeathTimestamps: playerDeaths.list,
    playerRespawnTimestamps: playerRespawns.list,
    allyDeathMarks: allyDeaths.list,
    allyRespawnMarks: allyRespawns.list,
  }
}
