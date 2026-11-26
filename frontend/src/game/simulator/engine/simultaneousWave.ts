// 对称战斗（isSymmetric）的同刻同时生效波次缓冲区
// 严格对应 DaoHua.Ecs.SimultaneousCombatWave

import {
  CombatEncounterState,
  CombatantState,
  type CombatEvent,
} from './combatModels.ts'
import { BuffIds, TraitIds, containsTrait, traitLevelScale } from './rules.ts'

interface PendingDamageEntry {
  attacker: CombatantState
  target: CombatantState
  damage: number
  accumulateXuyi: boolean
}

interface PendingHealEntry {
  source: CombatantState
  target: CombatantState
  amount: number
  defId: string | null
  combatEventIndex: number
}

/** 欲速则不达留一裁决（与 combatEngine.ts 共享口径）。 */
export function limitDamageForHaste(
  attacker: CombatantState,
  hitpoints: number,
  damage: number,
): number {
  if (hitpoints > 1 && damage >= hitpoints && containsTrait(attacker.stats.traits, TraitIds.Haste)) {
    return hitpoints - 1
  }
  return damage
}

/**
 * 同刻同时生效波次缓冲区：
 * 阶段 1 基于波初冻结快照求值全部伤害/治疗/吸血/反伤/阵盘反击/Buff；
 * 阶段 2 在波末按「统一落伤 → 仍存活者结算治疗与吸血 → 灵力/威胁/Buff 与派生层数 → 刷新存活人数 → 回填事件资源」原子提交。
 */
export class SimultaneousCombatWave {
  private readonly _localVirtualHp = new Map<CombatantState, number>()
  private readonly _damages: PendingDamageEntry[] = []
  private readonly _heals: PendingHealEntry[] = []
  private readonly _manaDeltas = new Map<CombatantState, number>()
  private readonly _threatDeltas = new Map<CombatantState, number>()
  private readonly _threatBonusDeltas = new Map<CombatantState, number>()
  private readonly _buffOps: Array<() => void> = []
  private readonly _xuyiConsumed = new Set<CombatantState>()
  private readonly _eventsStartIndex: number

  constructor(eventsStartIndex: number) {
    this._eventsStartIndex = eventsStartIndex
  }

  /** 切换至本波次下一名出手单位前重置本地虚拟气血。 */
  beginActor(): void {
    this._localVirtualHp.clear()
  }

  /** 读取目标在当前出手者视角下的剩余气血。 */
  getLocalVirtualHp(target: CombatantState): number {
    return this._localVirtualHp.get(target) ?? target.hitpoints
  }

  /** 判断目标在当前出手者本次动作内是否仍存活。 */
  isLocallyAlive(target: CombatantState): boolean {
    return this.getLocalVirtualHp(target) > 1e-12
  }

  /** 登记一次同刻伤害并返回按当前出手者虚拟气血裁决的伤害与实伤。 */
  recordDamage(
    attacker: CombatantState,
    target: CombatantState,
    damage: number,
    accumulateXuyi: boolean,
  ): { damage: number; actual: number } {
    const virtualHp = this.getLocalVirtualHp(target)
    if (virtualHp <= 1e-12) return { damage: 0, actual: 0 }
    const limited = limitDamageForHaste(attacker, virtualHp, Math.max(0, damage))
    const nextVirtualHp = Math.max(0, virtualHp - limited)
    const actual = virtualHp - nextVirtualHp
    this._localVirtualHp.set(target, nextVirtualHp)
    this._damages.push({ attacker, target, damage: limited, accumulateXuyi })
    return { damage: limited, actual }
  }

  /** 登记一次同刻治疗/吸血（波末扣伤后仅对存活目标按缺血上限落账）。 */
  recordHeal(
    source: CombatantState,
    target: CombatantState,
    amount: number,
    defId: string | null = null,
    combatEventIndex = -1,
  ): void {
    if (!Number.isFinite(amount) || amount <= 1e-9) return
    this._heals.push({ source, target, amount, defId, combatEventIndex })
  }

  /** 登记灵力净变动。 */
  addManaDelta(unit: CombatantState, delta: number): void {
    if (delta === 0) return
    this._manaDeltas.set(unit, (this._manaDeltas.get(unit) ?? 0) + delta)
  }

  /** 读取单位在波初快照上的可用灵力（叠加本波已发生的灵力变动）。 */
  getEffectiveMana(unit: CombatantState): number {
    const next = unit.manapoints + (this._manaDeltas.get(unit) ?? 0)
    return Math.max(0, Math.min(unit.stats.maxManapoints, next))
  }

  /** 登记基础威胁变动。 */
  addThreatDelta(unit: CombatantState, delta: number): void {
    if (delta === 0) return
    this._threatDeltas.set(unit, (this._threatDeltas.get(unit) ?? 0) + delta)
  }

  /** 登记威胁增幅点数变动。 */
  addThreatBonusDelta(unit: CombatantState, delta: number): void {
    if (delta === 0) return
    this._threatBonusDeltas.set(unit, (this._threatBonusDeltas.get(unit) ?? 0) + delta)
  }

  /** 延迟 Buff 变更至波末统一应用。 */
  deferBuffOp(op: () => void): void {
    this._buffOps.push(op)
  }

  /** 同刻波次内每名单位至多消费一次「蓄意」爆发。 */
  tryConsumeXuyi(attacker: CombatantState): boolean {
    if (this._xuyiConsumed.has(attacker)) return false
    this._xuyiConsumed.add(attacker)
    return true
  }

  /** 波末原子提交全部同刻状态变更并回填事件资源快照。 */
  commit(encounter: CombatEncounterState, events: CombatEvent[]): void {
    const xuyiGains = new Map<CombatantState, number>()
    const wasAliveBefore = new Map<CombatantState, boolean>()
    for (let i = 0; i < encounter.sideA.length; i++) {
      wasAliveBefore.set(encounter.sideA[i], encounter.sideA[i].isAlive)
    }
    for (let i = 0; i < encounter.sideB.length; i++) {
      wasAliveBefore.set(encounter.sideB[i], encounter.sideB[i].isAlive)
    }

    // 1. 统一应用全部同刻伤害
    for (let i = 0; i < this._damages.length; i++) {
      const entry = this._damages[i]
      if (!entry.target.isAlive) continue
      const applied = limitDamageForHaste(entry.attacker, entry.target.hitpoints, entry.damage)
      const before = entry.target.hitpoints
      entry.target.hitpoints = Math.max(0, before - applied)
      const actual = before - entry.target.hitpoints
      if (
        entry.accumulateXuyi &&
        actual > 0 &&
        containsTrait(entry.target.stats.traits, TraitIds.Xuyi)
      ) {
        xuyiGains.set(
          entry.target,
          (xuyiGains.get(entry.target) ?? 0) +
            actual * traitLevelScale(entry.target.stats.traitLevels, TraitIds.Xuyi),
        )
      }
    }

    // 2. 仍存活单位结算蓄意层数积累与同刻治疗/吸血
    for (const [unit, gain] of xuyiGains.entries()) {
      if (!unit.isAlive || gain <= 0) continue
      const existing = unit.buffs.find((b) => b.buffId === BuffIds.Xuyi)
      unit.setBuff({
        buffId: BuffIds.Xuyi,
        expiresAtSeconds: Number.MAX_VALUE,
        stacks: (existing ? existing.stacks : 0) + gain,
        name: '蓄意',
        resetOnWave: true,
        missingHitpointsPerStack: 0,
        effects: null,
        statRatios: null,
        sourceUnitId: null,
      })
    }

    for (let i = 0; i < this._heals.length; i++) {
      const heal = this._heals[i]
      let actualHealed = 0
      if (heal.target.isAlive) {
        actualHealed = Math.min(
          heal.target.stats.maxHitpoints - heal.target.hitpoints,
          Math.max(0, heal.amount),
        )
        if (actualHealed > 1e-9) {
          heal.target.hitpoints += actualHealed
          encounter.statsBook?.recordHealingUnit(heal.source, false, actualHealed)
        }
      }
      if (heal.combatEventIndex >= 0 && heal.combatEventIndex < events.length) {
        events[heal.combatEventIndex].healing = actualHealed
        events[heal.combatEventIndex].targetHitpointsAfter = heal.target.hitpoints
      } else if (Math.abs(actualHealed - heal.amount) > 1e-9) {
        for (let eIdx = this._eventsStartIndex; eIdx < events.length; eIdx++) {
          const ev = events[eIdx]
          if (
            ev.attackerId === heal.source.id &&
            (ev.targetId === heal.target.id || (heal.defId !== null && ev.abilityId === heal.defId)) &&
            Math.abs(ev.healing - heal.amount) <= 1e-9
          ) {
            ev.healing = actualHealed
            if (ev.targetId === heal.target.id) {
              ev.targetHitpointsAfter = heal.target.hitpoints
            }
            break
          }
        }
      }
    }

    // 3. 统一应用灵力、威胁、延迟 Buff 与派生层数
    for (const [unit, delta] of this._manaDeltas.entries()) {
      unit.manapoints = Math.max(0, Math.min(unit.stats.maxManapoints, unit.manapoints + delta))
    }
    for (const [unit, delta] of this._threatDeltas.entries()) {
      unit.threat = Math.max(0, unit.threat + delta)
    }
    for (const [unit, delta] of this._threatBonusDeltas.entries()) {
      unit.threatBonusPoints += delta
    }
    for (let i = 0; i < this._buffOps.length; i++) {
      this._buffOps[i]()
    }

    let aliveChanged = false
    for (let i = 0; i < encounter.sideA.length; i++) {
      const u = encounter.sideA[i]
      u.syncDerivedStacks()
      if (wasAliveBefore.get(u) !== u.isAlive) aliveChanged = true
    }
    for (let i = 0; i < encounter.sideB.length; i++) {
      const u = encounter.sideB[i]
      u.syncDerivedStacks()
      if (wasAliveBefore.get(u) !== u.isAlive) aliveChanged = true
    }
    if (aliveChanged) encounter.notifyAliveCountsChanged()

    // 4. 回填本波次事件的气血与灵力终态快照
    for (let i = this._eventsStartIndex; i < events.length; i++) {
      const ev = events[i]
      const attacker = encounter.findUnit(ev.attackerId)
      const target = encounter.findUnit(ev.targetId)
      if (target) ev.targetHitpointsAfter = target.hitpoints
      if (ev.attackerManapointsAfter !== null && attacker) {
        ev.attackerManapointsAfter = attacker.manapoints
      }
      if (ev.targetManapointsAfter !== null && target) {
        ev.targetManapointsAfter = target.manapoints
      }
    }
  }
}
