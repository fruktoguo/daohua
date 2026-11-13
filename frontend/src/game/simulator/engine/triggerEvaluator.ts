// 技能与消耗品触发条件求值与默认条件推断（严格对应 Domain.Combat.SkillTriggerEvaluator / SkillSlot / ConsumableTriggerResolver）

import type { SkillSlotDefinition } from './catalog.ts'
import type { CombatantState } from './combatModels.ts'
import { CombatMonsterRank } from './rules.ts'
import type { AbilityJson, ConsumableDefinition, TriggerConditionJson } from './types.ts'

const EQUAL_TOLERANCE = 1e-6

function isBoolSetCondition(condition: string | undefined): boolean {
  return (
    condition === 'has-player' ||
    condition === 'has-elite' ||
    condition === 'has-boss' ||
    condition === 'has-summon'
  )
}

function isBoolUnitCondition(condition: string | undefined): boolean {
  return (
    condition === 'is-player' ||
    condition === 'is-elite' ||
    condition === 'is-boss' ||
    condition === 'is-summon' ||
    condition === 'stun-status' ||
    condition === 'blind-status' ||
    condition === 'silence-status'
  )
}

function countAlive(units: readonly CombatantState[]): number {
  let count = 0
  for (let i = 0; i < units.length; i++) {
    if (units[i].isAlive) count++
  }
  return count
}

function anyAlive(
  units: readonly CombatantState[],
  predicate: (unit: CombatantState) => boolean,
): boolean {
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    if (unit.isAlive && predicate(unit)) return true
  }
  return false
}

function totalHpPercentage(units: readonly CombatantState[]): number {
  let sumHp = 0
  let sumMaxHp = 0
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    sumHp += Math.max(0, unit.hitpoints)
    sumMaxHp += Math.max(0, unit.stats.maxHitpoints)
  }
  return sumMaxHp > 0 ? (sumHp / sumMaxHp) * 100 : 0
}

function totalManaPercentage(units: readonly CombatantState[]): number {
  let sumMp = 0
  let sumMaxMp = 0
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    sumMp += Math.max(0, unit.manapoints)
    sumMaxMp += Math.max(0, unit.stats.maxManapoints)
  }
  return sumMaxMp > 0 ? (sumMp / sumMaxMp) * 100 : 0
}

function minAliveHpPercentage(units: readonly CombatantState[]): number {
  let min = Number.POSITIVE_INFINITY
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    if (!unit.isAlive) continue
    const maxHp = unit.stats.maxHitpoints
    const pct = maxHp > 0 ? (unit.hitpoints / maxHp) * 100 : 0
    if (pct < min) min = pct
  }
  return min === Number.POSITIVE_INFINITY ? Number.NaN : min
}

function maxAliveHpPercentage(units: readonly CombatantState[]): number {
  let max = Number.NEGATIVE_INFINITY
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    if (!unit.isAlive) continue
    const maxHp = unit.stats.maxHitpoints
    const pct = maxHp > 0 ? (unit.hitpoints / maxHp) * 100 : 0
    if (pct > max) max = pct
  }
  return max === Number.NEGATIVE_INFINITY ? Number.NaN : max
}

function minAliveBy(
  units: readonly CombatantState[],
  key: (unit: CombatantState) => number,
): CombatantState | null {
  let best: CombatantState | null = null
  let bestKey = 0
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    if (!unit.isAlive) continue
    const k = key(unit)
    // 与 C# double.CompareTo 一致：NaN 排在任意有限值之前
    if (best === null || (Number.isNaN(k) ? !Number.isNaN(bestKey) : k < bestKey)) {
      best = unit
      bestKey = k
    }
  }
  return best
}

function maxAliveBy(
  units: readonly CombatantState[],
  key: (unit: CombatantState) => number,
): CombatantState | null {
  let best: CombatantState | null = null
  let bestKey = 0
  for (let i = 0; i < units.length; i++) {
    const unit = units[i]
    if (!unit.isAlive) continue
    const k = key(unit)
    if (best === null || (Number.isNaN(bestKey) ? !Number.isNaN(k) : k > bestKey)) {
      best = unit
      bestKey = k
    }
  }
  return best
}

function resolveDependency(
  dependency: string,
  caster: CombatantState,
  primaryTarget: CombatantState | null,
  allies: readonly CombatantState[],
  opponents: readonly CombatantState[],
): CombatantState | null {
  switch (dependency) {
    case 'self':
      return caster
    case 'target':
    case 'target-enemy':
      return primaryTarget && primaryTarget.isAlive ? primaryTarget : null
    case 'ally-lowest-hp':
      return minAliveBy(allies, (u) => u.hitpoints / u.stats.maxHitpoints)
    case 'ally-highest-hp':
      return maxAliveBy(allies, (u) => u.hitpoints)
    case 'enemy-lowest-hp':
      return minAliveBy(opponents, (u) => u.hitpoints / u.stats.maxHitpoints)
    case 'enemy-highest-hp':
      return maxAliveBy(opponents, (u) => u.hitpoints)
    default:
      return null
  }
}

function readCondition(trigger: TriggerConditionJson, unit: CombatantState): number {
  const maxHp = unit.stats.maxHitpoints
  const maxMana = unit.stats.maxManapoints
  switch (trigger.condition) {
    case 'current-hp':
      return unit.hitpoints
    case 'missing-hp':
      return maxHp - unit.hitpoints
    case 'current-hp-percentage':
      return (unit.hitpoints / maxHp) * 100
    case 'missing-hp-percentage':
      return ((maxHp - unit.hitpoints) / maxHp) * 100
    case 'current-mana':
      return unit.manapoints
    case 'missing-mana':
      return maxMana - unit.manapoints
    case 'current-mana-percentage':
      return maxMana > 0 ? (unit.manapoints / maxMana) * 100 : 0
    case 'missing-mana-percentage':
      return maxMana > 0 ? ((maxMana - unit.manapoints) / maxMana) * 100 : 0
    case 'buff-stacks':
      return unit.buffStacks(typeof trigger.buffId === 'string' ? trigger.buffId : null)
    case 'stun-status':
    case 'blind-status':
    case 'silence-status':
      return 0
    default:
      return Number.NaN
  }
}

function matchesSingleTrigger(
  trigger: TriggerConditionJson,
  caster: CombatantState,
  primaryTarget: CombatantState | null,
  allies: readonly CombatantState[],
  opponents: readonly CombatantState[],
  elapsedSeconds: number,
): boolean {
  const dependency = typeof trigger.dependency === 'string' ? trigger.dependency : 'self'
  const condition = trigger.condition
  let value: number

  if (dependency === 'battle') {
    value = condition === 'elapsed-seconds' ? elapsedSeconds : Number.NaN
  } else if (
    dependency === 'ally' ||
    dependency === 'allies' ||
    dependency === 'enemy' ||
    dependency === 'enemies'
  ) {
    const set = dependency === 'ally' || dependency === 'allies' ? allies : opponents
    if (isBoolSetCondition(condition)) {
      let matched = false
      switch (condition) {
        case 'has-player':
          matched = anyAlive(set, (u) => u.isPlayer)
          break
        case 'has-elite':
          matched = anyAlive(set, (u) => u.rank === CombatMonsterRank.Elite)
          break
        case 'has-boss':
          matched = anyAlive(set, (u) => u.rank === CombatMonsterRank.Boss)
          break
        case 'has-summon':
          matched = anyAlive(set, (u) => u.isSummon)
          break
      }
      value = matched ? 1 : 0
    } else {
      switch (condition) {
        case 'alive-count':
          value = countAlive(set)
          break
        case 'dead-count':
          value = set.length - countAlive(set)
          break
        case 'total-count':
          value = set.length
          break
        case 'total-hp-percentage':
          value = totalHpPercentage(set)
          break
        case 'total-mana-percentage':
          value = totalManaPercentage(set)
          break
        case 'lowest-hp-percentage':
          value = minAliveHpPercentage(set)
          break
        case 'highest-hp-percentage':
          value = maxAliveHpPercentage(set)
          break
        default:
          value = Number.NaN
          break
      }
    }
  } else {
    const unit = resolveDependency(dependency, caster, primaryTarget, allies, opponents)
    if (unit === null) return false
    if (isBoolUnitCondition(condition)) {
      let matched = false
      switch (condition) {
        case 'is-player':
          matched = unit.isPlayer
          break
        case 'is-elite':
          matched = unit.rank === CombatMonsterRank.Elite
          break
        case 'is-boss':
          matched = unit.rank === CombatMonsterRank.Boss
          break
        case 'is-summon':
          matched = unit.isSummon
          break
        case 'stun-status':
        case 'blind-status':
        case 'silence-status':
          matched = false
          break
      }
      value = matched ? 1 : 0
    } else {
      value = readCondition(trigger, unit)
    }
  }

  if (!Number.isFinite(value)) return false
  const threshold = typeof trigger.value === 'number' && Number.isFinite(trigger.value) ? trigger.value : 0
  switch (trigger.comparator) {
    case 'greater-than-equal':
      return value >= threshold
    case 'less-than-equal':
      return value <= threshold
    case 'greater-than':
      return value > threshold
    case 'less-than':
      return value < threshold
    case 'equal':
      return Math.abs(value - threshold) <= EQUAL_TOLERANCE
    case 'is-active':
      return value > 0
    case 'is-inactive':
      return value <= 0
    default:
      return false
  }
}

export function evaluateTriggers(
  triggers: readonly TriggerConditionJson[] | null | undefined,
  caster: CombatantState,
  primaryTarget: CombatantState | null,
  allies: readonly CombatantState[],
  opponents: readonly CombatantState[],
  elapsedSeconds: number,
): boolean {
  if (!triggers || triggers.length === 0) return true
  for (let i = 0; i < triggers.length; i++) {
    if (!matchesSingleTrigger(triggers[i], caster, primaryTarget, allies, opponents, elapsedSeconds)) {
      return false
    }
  }
  return true
}

export function inferDefaultTriggers(ability: AbilityJson): TriggerConditionJson[] {
  const effectType = typeof ability.effectType === 'string' ? ability.effectType.toLowerCase() : ''
  if (effectType === 'heal') {
    return [
      {
        dependency: 'allies',
        condition: 'lowest-hp-percentage',
        comparator: 'less-than',
        value: 75.0,
      },
    ]
  }
  let buffId: string | undefined
  if (ability.buff && typeof ability.buff.buffId === 'string') {
    buffId = ability.buff.buffId
  } else if (Array.isArray(ability.buffs) && ability.buffs.length > 0 && typeof ability.buffs[0]?.buffId === 'string') {
    buffId = ability.buffs[0].buffId
  }
  const damageRatio = typeof ability.damageRatio === 'number' ? ability.damageRatio : 0
  const flatDamage = typeof ability.flatDamage === 'number' ? ability.flatDamage : 0
  if (buffId && buffId.length > 0 && damageRatio <= 0 && flatDamage <= 0) {
    const dep =
      ability.targetType === 'targetEnemy' || ability.targetType === 'allEnemies'
        ? 'target-enemy'
        : 'self'
    return [
      {
        dependency: dep,
        condition: 'buff-stacks',
        comparator: 'equal',
        value: 0.0,
        buffId,
      },
    ]
  }
  return [
    {
      dependency: 'target-enemy',
      condition: 'current-hp',
      comparator: 'greater-than',
      value: 1.0,
    },
  ]
}

export function defaultAbilityTriggers(ability: AbilityJson): TriggerConditionJson[] {
  if (Array.isArray(ability.defaultTriggers)) {
    return ability.defaultTriggers
  }
  return inferDefaultTriggers(ability)
}

export function buildSkillSlotsFromAbilitySlots(
  slots: readonly { abilityId: string | null; level: number }[],
  abilities: ReadonlyMap<string, AbilityJson>,
  abilityTriggers?: ReadonlyMap<string, TriggerConditionJson[]> | Record<string, TriggerConditionJson[]> | null,
): SkillSlotDefinition[] {
  const skills: SkillSlotDefinition[] = []
  for (let i = 0; i < slots.length; i++) {
    const slot = slots[i]
    if (!slot.abilityId) continue
    const ability = abilities.get(slot.abilityId)
    if (!ability || ability.discipline === 'internal') continue
    let triggers: TriggerConditionJson[] | null = null
    if (abilityTriggers instanceof Map) {
      triggers = abilityTriggers.get(slot.abilityId) ?? null
    } else if (abilityTriggers && Object.prototype.hasOwnProperty.call(abilityTriggers, slot.abilityId)) {
      triggers = (abilityTriggers as Record<string, TriggerConditionJson[]>)[slot.abilityId] ?? null
    }
    skills.push({
      ability,
      abilityId: slot.abilityId,
      level: slot.level,
      triggers: triggers ?? defaultAbilityTriggers(ability),
    })
  }
  return skills
}

export function resolveConsumableTriggers(
  itemId: string,
  consumable: ConsumableDefinition,
  playerTriggers?: ReadonlyMap<string, TriggerConditionJson[]> | Record<string, TriggerConditionJson[]> | null,
): readonly TriggerConditionJson[] {
  if (playerTriggers instanceof Map) {
    const custom = playerTriggers.get(itemId)
    if (custom) return custom
  } else if (playerTriggers && Object.prototype.hasOwnProperty.call(playerTriggers, itemId)) {
    return (playerTriggers as Record<string, TriggerConditionJson[]>)[itemId] ?? []
  }
  return consumable.triggers ?? []
}
