import type { CombatTrigger } from '../types'

/**
 * 触发条件求值所需的轻量单位抽象接口
 */
export interface SimUnitView {
  isAlive: boolean
  hitpoints: number
  maxHitpoints: number
  manapoints: number
  maxManapoints: number
  buffs: ReadonlyArray<{ id: string; stacks: number }>
}

/**
 * 统一技能触发条件求值器（与 C# Domain.Combat.SkillTriggerEvaluator 完全一致）
 */
export class SimSkillTriggerEvaluator {
  /**
   * 全部条件满足返回 true，空列表短路返回 true
   */
  public static matchesAll(
    triggers: readonly CombatTrigger[] | undefined | null,
    caster: SimUnitView,
    primaryTarget: SimUnitView | null,
    allies: readonly SimUnitView[],
    opponents: readonly SimUnitView[]
  ): boolean {
    if (!triggers || triggers.length === 0) return true
    for (const trigger of triggers) {
      if (!this.matches(trigger, caster, primaryTarget, allies, opponents)) {
        return false
      }
    }
    return true
  }

  public static matches(
    trigger: CombatTrigger,
    caster: SimUnitView,
    primaryTarget: SimUnitView | null,
    allies: readonly SimUnitView[],
    opponents: readonly SimUnitView[]
  ): boolean {
    const dependency = (trigger.dependency as string) || 'self'
    let value: number

    if (dependency === 'ally' || dependency === 'enemy') {
      // 集合对象依赖
      const set = dependency === 'ally' ? allies : opponents
      const condition = trigger.condition as string
      if (condition === 'alive-count') {
        value = set.filter(u => u.isAlive).length
      } else if (condition === 'dead-count') {
        value = set.filter(u => !u.isAlive).length
      } else {
        return false
      }
    } else {
      // 单体对象依赖
      const unit = this.resolveUnit(dependency, caster, primaryTarget, allies, opponents)
      if (!unit || !unit.isAlive) return false
      value = this.readCondition(trigger, unit)
    }

    if (!Number.isFinite(value)) return false
    const threshold = trigger.value ?? 0

    switch (trigger.comparator) {
      case 'greater-than-equal':
        return value >= threshold
      case 'less-than-equal':
        return value <= threshold
      case 'greater-than':
        return value > threshold
      case 'less-than':
        return value < threshold
      case 'is-active':
        return value > 0
      case 'is-inactive':
        return value <= 0
      default:
        if ((trigger.comparator as string) === 'equal') {
          return Math.abs(value - threshold) < 1e-9
        }
        return false
    }
  }

  private static resolveUnit(
    dependency: string,
    caster: SimUnitView,
    primaryTarget: SimUnitView | null,
    allies: readonly SimUnitView[],
    opponents: readonly SimUnitView[]
  ): SimUnitView | null {
    switch (dependency) {
      case 'self':
        return caster
      case 'target-enemy':
        return primaryTarget && primaryTarget.isAlive ? primaryTarget : null
      case 'ally-lowest-hp':
        return this.minAliveBy(allies, u => u.hitpoints)
      case 'ally-highest-hp':
        return this.maxAliveBy(allies, u => u.hitpoints)
      case 'enemy-lowest-hp':
        return this.minAliveBy(opponents, u => u.hitpoints)
      case 'enemy-highest-hp':
        return this.maxAliveBy(opponents, u => u.hitpoints)
      default:
        return null
    }
  }

  private static readCondition(trigger: CombatTrigger, unit: SimUnitView): number {
    const maxHp = unit.maxHitpoints
    const maxMana = unit.maxManapoints

    switch (trigger.condition) {
      case 'current-hp':
        return unit.hitpoints
      case 'missing-hp':
        return Math.max(0, maxHp - unit.hitpoints)
      case 'current-hp-percentage':
        return maxHp > 0 ? (unit.hitpoints / maxHp) * 100 : 0
      case 'missing-hp-percentage':
        return maxHp > 0 ? (Math.max(0, maxHp - unit.hitpoints) / maxHp) * 100 : 0
      case 'current-mana':
        return unit.manapoints
      case 'missing-mana':
        return Math.max(0, maxMana - unit.manapoints)
      case 'current-mana-percentage':
        return maxMana > 0 ? (unit.manapoints / maxMana) * 100 : 0
      case 'missing-mana-percentage':
        return maxMana > 0 ? (Math.max(0, maxMana - unit.manapoints) / maxMana) * 100 : 0
      case 'buff-stacks': {
        const buffId = trigger.buffId
        if (!buffId) return 0
        const found = unit.buffs.find(b => b.id === buffId)
        return found ? found.stacks : 0
      }
      case 'stun-status':
      case 'blind-status':
      case 'silence-status':
        return 0
      default:
        return Number.NaN
    }
  }

  private static minAliveBy(units: readonly SimUnitView[], keyFn: (u: SimUnitView) => number): SimUnitView | null {
    let best: SimUnitView | null = null
    let bestKey = Number.POSITIVE_INFINITY
    for (const u of units) {
      if (!u.isAlive) continue
      const k = keyFn(u)
      if (best === null || k < bestKey) {
        best = u
        bestKey = k
      }
    }
    return best
  }

  private static maxAliveBy(units: readonly SimUnitView[], keyFn: (u: SimUnitView) => number): SimUnitView | null {
    let best: SimUnitView | null = null
    let bestKey = Number.NEGATIVE_INFINITY
    for (const u of units) {
      if (!u.isAlive) continue
      const k = keyFn(u)
      if (best === null || k > bestKey) {
        best = u
        bestKey = k
      }
    }
    return best
  }
}
