// 确定性战斗推进、出手决策、伤害结算、特质与触发器引擎
// 严格对应 DaoHua.Ecs.Combat.DeterministicCombatEngine / CombatActSystem / CombatPhaseSystems / CombatEventAdvanceSystem / CombatTriggerMath

import { deterministicRollNext } from './deterministicRoll.ts'
import {
  CombatEncounterState,
  CombatOutcome,
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
  containsTrait,
  parseBuffEffects,
  parseCombatElement,
  percentMultiplier,
  traitLevelScale,
  type CombatantStats,
} from './rules.ts'
import { evaluateTriggers } from './triggerEvaluator.ts'
import {
  CombatFactTracker,
  EMPTY_WAKE_MASK,
  FULL_FACT_DELTA,
  FULL_WAKE_MASK,
  foldPendingWake,
  maskIntersects,
  resolveTriggerListWakeMask,
  type CombatFactDelta,
  type SlotWakeMask,
} from './triggerWake.ts'
import type { AbilityBuffJson, AbilityJson } from './types.ts'

export const MAX_EVENTS_PER_ADVANCE = 100_000
const SKILL_SIDE_OFFSET = 0x1000n
const ALLY_SIDE_BASE = 0x400n
const BUFF_TICK_SECONDS = 5
const THREAT_TICK_SECONDS = 1

export function allySide(memberIndex: number): bigint {
  return memberIndex === 0 ? 0n : ALLY_SIDE_BASE + BigInt(memberIndex)
}

function abilityText(obj: Record<string, unknown>, key: string): string | null {
  const v = obj[key]
  return typeof v === 'string' ? v : null
}

function abilityNumber(obj: Record<string, unknown>, key: string): number {
  const v = obj[key]
  return typeof v === 'number' && Number.isFinite(v) ? v : 0
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
  if (total <= 1e-12) {
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
    if (total <= 1e-12) {
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

  let accuracy = attackEffective.accuracy * (broken ? 2 : 1)
  let evasion = defenseEffective.evasion * (parried ? 2 : 1)
  ;[accuracy, evasion] = applyCombatExperienceContest(
    accuracy,
    evasion,
    attack.combatExperience,
    defense.combatExperience,
  )

  const hit =
    roll(encounter.seed, side, context.count, 1n) >= evasion / (evasion + accuracy + 100)
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

export function resolveStrikeDamage(attack: CombatantStats, target: CombatantState): number {
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

// ---------- 伤害扣血裁决与蓄意/燃魂 ----------

function limitDamage(attacker: CombatantState, hitpoints: number, damage: number): number {
  if (hitpoints > 1 && damage >= hitpoints && containsTrait(attacker.stats.traits, TraitIds.Haste)) {
    return hitpoints - 1
  }
  return damage
}

export function applyCombatDamage(
  encounter: CombatEncounterState | null,
  attacker: CombatantState,
  target: CombatantState,
  damage: number,
  accumulateXuyi = true,
): { damage: number; actual: number } {
  if (!target.isAlive) return { damage: 0, actual: 0 }
  const limited = limitDamage(attacker, target.hitpoints, Math.max(0, damage))
  const before = target.hitpoints
  target.hitpoints = Math.max(0, before - limited)
  const actual = before - target.hitpoints
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
  return { damage: limited, actual }
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

export function applyRanhun(
  attacker: CombatantState,
  target: CombatantState,
  element: CombatElement,
  actual: number,
  at: number,
): void {
  if (
    actual <= 0 ||
    !target.isAlive ||
    element !== CombatElement.Fire ||
    !containsTrait(attacker.stats.traits, TraitIds.Ranhun)
  ) {
    return
  }
  const stacks = Math.max(
    1,
    Math.floor(actual * 0.02 * traitLevelScale(attacker.stats.traitLevels, TraitIds.Ranhun)),
  )
  const existing = target.buffs.find((b) => b.buffId === BuffIds.Ranhun)
  target.setBuff({
    buffId: BuffIds.Ranhun,
    expiresAtSeconds: at + 5,
    stacks: stacks + (existing ? existing.stacks : 0),
    name: '燃魂',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [{ channel: BuffEffectChannel.FireSpellDamagePerSecond, value: 1 }],
    statRatios: null,
    sourceUnitId: attacker.id,
  })
}

function emitTraitOnHit(
  context: CombatHitContext,
  derivedEvents: CombatEvent[],
): void {
  if (!context.hit || !context.attacker.isAlive) return
  applyRanhun(
    context.attacker,
    context.target,
    context.attackStats.element,
    context.actualDamage,
    context.atSeconds,
  )
  const attacker = context.attacker
  if (!containsTrait(attacker.stats.traits, TraitIds.Xuyi)) return
  const buff = attacker.buffs.find((b) => b.buffId === BuffIds.Xuyi)
  if (!buff || buff.stacks <= attacker.stats.maxHitpoints) return
  attacker.removeBuffsWhere((b) => b.buffId === BuffIds.Xuyi)
  const opponents = context.encounter.playerAllies.includes(attacker)
    ? context.encounter.enemies
    : context.encounter.playerAllies

  const burst = (target: CombatantState, primary: boolean) => {
    const rawDamage = primary
      ? buff.stacks
      : resolveStrikeDamage(
          buildStrikeStats(attacker, buff.stacks, CombatDamageKind.Physical, CombatElement.None),
          target,
        )
    const settled = applyCombatDamage(context.encounter, attacker, target, rawDamage, false)
    attacker.threat += settled.damage
    context.encounter.statsBook?.recordHitUnit(
      attacker,
      target,
      primary ? CombatDamageKind.True : CombatDamageKind.Physical,
      settled.damage,
      primary ? 0 : Math.max(0, buff.stacks - settled.damage),
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

  if (context.target.isAlive) burst(context.target, true)
  for (let i = 0; i < opponents.length; i++) {
    const opp = opponents[i]
    if (opp.isAlive && opp !== context.target) burst(opp, false)
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
    let source: CombatantState | undefined = encounter.playerAllies.find(
      (u) => u.id === buff.sourceUnitId,
    )
    if (!source) source = encounter.enemies.find((u) => u.id === buff.sourceUnitId)
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
    })
    return
  }
}

// ---------- 内联命中触发器管线（Strict Order: 50 -> 100 -> 110 -> 200 -> 300 -> 400） ----------

function triggerHpDrain(context: CombatHitContext, points: number, derivedEvents: CombatEvent[]): void {
  if (!context.hit || context.damage <= 0 || !context.attacker.isAlive) return
  if (points <= 0) return
  const healing = Math.min(
    context.attacker.stats.maxHitpoints - context.attacker.hitpoints,
    ((context.damage * points) / 100) *
      percentMultiplier(context.attacker.effectiveStats.effects.healingBonusPoints, PercentCurveMode.Linear),
  )
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
  context.target.threat += reflected
  context.encounter.statsBook?.recordHitUnit(
    context.target,
    context.attacker,
    context.attackStats.damageKind,
    reflected,
    0,
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
  context.target.threat += gained
}

function triggerFormationReactiveStrike(
  context: CombatHitContext,
  derivedEvents: CombatEvent[],
  rollState: { ch: bigint },
): void {
  if (!context.hit || context.damage <= 0 || !context.target.isAlive) return
  const encounter = context.encounter
  if (encounter.playerAllies.includes(context.target)) return

  const allies = encounter.playerAllies
  for (let i = 0; i < allies.length; i++) {
    const ally = allies[i]
    if (!ally.isAlive || !context.target.isAlive) continue

    if (ally.buffStacks('formation.tianlei') > 0) {
      const canTrigger =
        ally.lastTianleiTriggerAtSeconds < 0 ||
        context.atSeconds - ally.lastTianleiTriggerAtSeconds >= 1.0 - 1e-9
      const manaCost = ally.stats.maxManapoints * 0.01
      if (
        canTrigger &&
        ally.manapoints >= manaCost &&
        roll(encounter.seed, context.side, context.count, rollState.ch++) < 0.2
      ) {
        ally.manapoints = Math.max(0, ally.manapoints - manaCost)
        ally.lastTianleiTriggerAtSeconds = context.atSeconds
        const rawDamage = ally.effectiveStats.spellAttack * 0.5 + 10
        const strikeStats: CombatantStats = {
          ...ally.effectiveStats,
          attackPower: rawDamage,
          damageKind: CombatDamageKind.Spell,
          element: CombatElement.Wood,
          totalDamageBonusPoints:
            ally.stats.totalDamageBonusPoints + ally.buffTotal(BuffEffectChannel.DamageBonusPoints),
        }
        const rawDmg = Math.max(0, resolveStrikeDamage(strikeStats, context.target))
        if (rawDmg > 0) {
          const hpBefore = context.target.hitpoints
          applyCombatDamage(encounter, ally, context.target, rawDmg)
          const actualDamage = hpBefore - context.target.hitpoints
          ally.threat += actualDamage
          encounter.statsBook?.recordHitUnit(ally, context.target, CombatDamageKind.Spell, actualDamage, 0)
          derivedEvents.push({
            atSeconds: context.atSeconds,
            attackerId: ally.id,
            targetId: context.target.id,
            damage: actualDamage,
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

    if (!context.target.isAlive) break

    if (ally.buffStacks('formation.xuesha') > 0) {
      const canTrigger =
        ally.lastXueshaTriggerAtSeconds < 0 ||
        context.atSeconds - ally.lastXueshaTriggerAtSeconds >= 1.0 - 1e-9
      if (canTrigger && roll(encounter.seed, context.side, context.count, rollState.ch++) < 0.2) {
        ally.lastXueshaTriggerAtSeconds = context.atSeconds
        const rawDamage = ally.stats.maxHitpoints * 0.1
        const strikeStats: CombatantStats = {
          ...ally.effectiveStats,
          attackPower: rawDamage,
          damageKind: CombatDamageKind.Physical,
          element: CombatElement.None,
          totalDamageBonusPoints:
            ally.stats.totalDamageBonusPoints + ally.buffTotal(BuffEffectChannel.DamageBonusPoints),
        }
        const rawDmg = Math.max(0, resolveStrikeDamage(strikeStats, context.target))
        if (rawDmg > 0) {
          const hpBefore = context.target.hitpoints
          applyCombatDamage(encounter, ally, context.target, rawDmg)
          const actualDamage = hpBefore - context.target.hitpoints
          const heal = Math.min(
            ally.stats.maxHitpoints - ally.hitpoints,
            actualDamage *
              percentMultiplier(
                ally.effectiveStats.effects.healingBonusPoints,
                PercentCurveMode.Linear,
              ),
          )
          if (heal > 0) {
            ally.hitpoints += heal
            encounter.statsBook?.recordHealingUnit(ally, false, heal)
          }
          ally.threat += actualDamage
          encounter.statsBook?.recordHitUnit(
            ally,
            context.target,
            CombatDamageKind.Physical,
            actualDamage,
            0,
          )
          derivedEvents.push({
            atSeconds: context.atSeconds,
            attackerId: ally.id,
            targetId: context.target.id,
            damage: actualDamage,
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

function abilityBuffSpecs(ability: AbilityJson): AbilityBuffJson[] {
  if (Array.isArray(ability.buffs)) {
    return ability.buffs.filter((b): b is AbilityBuffJson => b !== null && typeof b === 'object')
  }
  if (ability.buff && typeof ability.buff === 'object') {
    return [ability.buff]
  }
  return []
}

function initialBuffStacks(
  buff: AbilityBuffJson,
  caster: CombatantState,
  target: CombatantState,
  growth: number,
): number {
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
    const totalStacks = (1 + extraStacks) * healingMult
    return Math.max(1, Math.floor(totalStacks + 1e-9))
  }
  const resistancePerStack = abilityNumber(buff, 'resistancePerStack')
  if (resistancePerStack > 0) {
    const resistance = caster.stats.defense + caster.stats.magicResistance
    const ratio = abilityNumber(buff, 'resistanceStackRatio')
    return 1 + Math.floor((resistance * ratio * growth) / resistancePerStack + 1e-9)
  }
  return 1.0
}

function grantAbilityBuff(
  buff: AbilityBuffJson,
  caster: CombatantState,
  target: CombatantState,
  grantedAtSeconds: number,
  levelGrowth: number,
  abilityId: string,
): void {
  const baseBuffId = abilityText(buff, 'buffId') ?? `ability-buff:${abilityId}`
  const policy = abilityText(buff, 'stackPolicy') ?? 'capped'
  const buffId = policy === 'perCaster' ? `${baseBuffId}:${caster.id}` : baseBuffId
  const missingPerStack = abilityNumber(buff, 'missingHitpointsPerStack')
  const spellStackPercent = abilityNumber(buff, 'spellAttackStackPercent')
  const resistancePerStack = abilityNumber(buff, 'resistancePerStack')
  const hasStackDerivation = missingPerStack > 0 || spellStackPercent > 0 || resistancePerStack > 0

  const growth =
    levelGrowth * percentMultiplier(caster.stats.skillBonusPoints, PercentCurveMode.Linear)
  const effectGrowth = hasStackDerivation ? 1.0 : levelGrowth
  const initialStacks = initialBuffStacks(buff, caster, target, growth)

  const grant: CombatBuffState = {
    buffId,
    expiresAtSeconds: grantedAtSeconds + Math.max(0, abilityNumber(buff, 'durationSeconds')),
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
  target.setBuff({ ...grant, stacks: nextStacks })
  target.syncDerivedStacks()
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
): void {
  if (!attacker.isAlive || !target.isAlive) return
  const context: CombatHitContext = {
    encounter,
    attacker,
    target,
    attackStats: statsOverride ?? attacker.stats,
    abilityId,
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
  encounter.statsBook?.recordHitUnit(
    attacker,
    target,
    context.attackStats.damageKind,
    context.damage,
    context.hit ? Math.max(0, context.rawDamage - context.damage) : 0,
  )
  attacker.attackCount += 1
  attacker.threat += context.damage
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
    healing: 0,
    attackerManapointsAfter: null,
    targetManapointsAfter: null,
  })
  encounter.pendingTriggers.push(
    { attacker, target, hitContext: context, events, mainEventIndex },
    { attacker: target, target: attacker, hitContext: context, events, mainEventIndex },
  )
  drainPendingTriggers(encounter)
}

function castSkill(
  encounter: CombatEncounterState,
  caster: CombatantState,
  allies: readonly CombatantState[],
  opponents: readonly CombatantState[],
  target: CombatantState | null,
  slot: SkillSlotDefinition,
  side: bigint,
  events: CombatEvent[],
): void {
  const ability = slot.ability
  const spell = abilityText(ability, 'category') === 'spell'
  const attackBase = spell ? caster.stats.spellAttack : caster.stats.attackPower
  const id = abilityText(ability, 'id') ?? ''
  const levelGrowth = 1 + 0.02 * (Math.max(1, slot.level) - 1)
  const targetType = abilityText(ability, 'targetType')

  caster.threat += abilityNumber(ability, 'threatGain') * levelGrowth
  caster.threatBonusPoints += abilityNumber(ability, 'threatPoints') * levelGrowth

  const healOutput =
    attackBase *
      percentMultiplier(caster.stats.skillBonusPoints, PercentCurveMode.Linear) *
      abilityNumber(ability, 'healingRatio') *
      levelGrowth +
    abilityNumber(ability, 'flatHealing') * levelGrowth

  if (abilityText(ability, 'effectType') === 'heal' || healOutput > 0) {
    const output =
      healOutput *
      percentMultiplier(caster.effectiveStats.effects.healingBonusPoints, PercentCurveMode.Linear)
    const applyHeal = (ally: CombatantState) => {
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

  const hasDamage = abilityNumber(ability, 'damageRatio') + abilityNumber(ability, 'flatDamage') > 0
  if (targetType === 'self' || (!hasDamage && targetType !== 'targetEnemy' && targetType !== 'allEnemies')) {
    const buffSpecs = abilityBuffSpecs(ability)
    if (buffSpecs.length > 0) {
      if (targetType === 'lowestHitpointsAlly') {
        const weakest = lowestHitpointsRatioAlly(allies)
        if (weakest) {
          for (let b = 0; b < buffSpecs.length; b++) {
            grantAbilityBuff(buffSpecs[b], caster, weakest, encounter.elapsedSeconds, levelGrowth, id)
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
              )
            }
          }
        }
      } else {
        for (let b = 0; b < buffSpecs.length; b++) {
          grantAbilityBuff(buffSpecs[b], caster, caster, encounter.elapsedSeconds, levelGrowth, id)
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
    })
    return
  }

  const stats: CombatantStats = {
    ...caster.stats,
    attackPower:
      attackBase *
        percentMultiplier(caster.stats.skillBonusPoints, PercentCurveMode.Linear) *
        abilityNumber(ability, 'damageRatio') *
        levelGrowth +
      abilityNumber(ability, 'flatDamage') * levelGrowth,
    damageKind: spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
    element: abilityElement(ability),
  }

  const count = caster.skillCastCount + 1
  const rawMaxTargets = ability.maxTargets
  const maxTargets =
    typeof rawMaxTargets === 'number' && Number.isFinite(rawMaxTargets)
      ? Math.trunc(rawMaxTargets)
      : 0
  if (targetType === 'allEnemies') {
    const picked = selectTargets(
      opponents,
      maxTargets > 0 ? maxTargets : 0x7fffffff,
      encounter.seed,
      side,
      count,
      17n,
    )
    for (let i = 0; i < picked.length; i++) {
      resolveAttack(encounter, caster, picked[i], side, encounter.elapsedSeconds, events, stats, count, id)
    }
    return
  }

  let single = target && target.isAlive ? target : selectTarget(opponents, encounter.seed, side, count, 16n)
  if (single === null) return
  resolveAttack(encounter, caster, single, side, encounter.elapsedSeconds, events, stats, count, id)

  const hitCount = Math.max(1, Math.trunc(abilityNumber(ability, 'hitCount')))
  for (let hitIndex = 1; hitIndex < hitCount && single.isAlive; hitIndex++) {
    resolveAttack(encounter, caster, single, side, encounter.elapsedSeconds, events, stats, null, id)
  }

  const chainMax = Math.trunc(abilityNumber(ability, 'chainOnKillCount'))
  for (let chainIndex = 0; chainIndex < chainMax && !single.isAlive; chainIndex++) {
    single = selectTarget(
      opponents,
      encounter.seed,
      side,
      count,
      20n + BigInt(chainIndex),
    )
    if (single === null) break
    resolveAttack(encounter, caster, single, side, encounter.elapsedSeconds, events, stats, count, id)
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
      side + SKILL_SIDE_OFFSET,
      events,
    )
    unit.skillCastCount += 1
    return
  }
  if (target !== null) {
    resolveAttack(encounter, unit, target, side, encounter.elapsedSeconds, events)
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
    const tracking = encounter.wakeTracking
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
      const manaCost = abilityNumber(slot.ability, 'manaCost')
      if (unit.manapoints < manaCost) continue
      unit.manapoints -= manaCost
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
  unit.nextAttackAtSeconds =
    encounter.elapsedSeconds +
    unit.stats.attackIntervalSeconds *
      percentMultiplier(
        unit.stats.attackSpeedPoints + unit.buffTotal(BuffEffectChannel.AttackSpeedPoints),
        PercentCurveMode.NonLinear,
      ) *
      percentMultiplier(unit.effectiveStats.actionSpeedPoints, PercentCurveMode.NonLinear)
}

export function decideInitialActions(encounter: CombatEncounterState): void {
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
  if (regenTick || secondTick) {
    const statsBook = encounter.statsBook
    for (let i = 0; i < encounter.playerAllies.length; i++) {
      const unit = encounter.playerAllies[i]
      const aliveBefore = unit.isAlive
      tickUnitBuffEffects(unit, statsBook, regenTick, secondTick)
      if (aliveBefore !== unit.isAlive) encounter.notifyAliveCountsChanged()
      if (secondTick) tickRanhun(encounter, unit, effectAt, events)
    }
    for (let i = 0; i < encounter.enemies.length; i++) {
      const unit = encounter.enemies[i]
      const aliveBefore = unit.isAlive
      tickUnitBuffEffects(unit, statsBook, regenTick, secondTick)
      if (aliveBefore !== unit.isAlive) encounter.notifyAliveCountsChanged()
      if (secondTick) tickRanhun(encounter, unit, effectAt, events)
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
}

function interruptUnitCast(unit: CombatantState): void {
  const pending = unit.pendingSkillIndex
  unit.pendingSkillIndex = -1
  if (pending < 0 || pending >= unit.skills.length) return
  unit.manapoints = Math.min(
    unit.stats.maxManapoints,
    unit.manapoints + abilityNumber(unit.skills[pending].ability, 'manaCost'),
  )
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

function applyOutcomePhase(encounter: CombatEncounterState): void {
  const alliesAlive = anyAliveUnits(encounter.playerAllies)
  const enemiesAlive = anyAliveUnits(encounter.enemies)
  if (!alliesAlive && Number.isFinite(encounter.nextAllyRespawnAtSeconds)) {
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
  if (!encounter.isAllAlliesDowned) {
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
  applyOutcomePhase(encounter)
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
    applyOutcomePhase(encounter)
    hook?.onOutcome(encounter, false)
  }

  let eventCount = 0
  while (
    encounter.outcome === CombatOutcome.Running &&
    encounter.elapsedSeconds < targetSeconds - 1e-12
  ) {
    if (++eventCount > MAX_EVENTS_PER_ADVANCE) {
      throw new Error('战斗推进超过最大事件数。')
    }
    if (
      !advanceCombatBoundary(
        encounter,
        targetSeconds,
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
