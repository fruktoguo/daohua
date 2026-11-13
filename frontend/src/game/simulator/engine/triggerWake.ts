// 触发条件订阅掩码与边界事实追踪（严格对应 Domain.Combat.TriggerWake / CombatFactTracker）

import type { CombatantState, CombatEncounterState } from './combatModels.ts'
import type { CombatantStats } from './rules.ts'
import type { TriggerConditionJson } from './types.ts'

/** 单体事实通道位掩码 */
export const UnitChannel = {
  None: 0,
  Hp: 1 << 0,
  Mana: 1 << 1,
  Buff: 1 << 2,
  Alive: 1 << 3,
  Threat: 1 << 4,
  Stats: 1 << 5,
  All: 0x3f,
} as const

/** 全局/集合级事实位掩码 */
export const BattleFact = {
  None: 0,
  Elapsed: 1 << 0,
  AllyList: 1 << 1,
  EnemyList: 1 << 2,
  External: 1 << 3,
  All: 0x0f,
} as const

export interface SlotWakeMask {
  readonly self: number
  readonly allySide: number
  readonly enemySide: number
  readonly facts: number
  readonly selfInEnemies: boolean
}

export const EMPTY_WAKE_MASK: SlotWakeMask = {
  self: 0,
  allySide: 0,
  enemySide: 0,
  facts: 0,
  selfInEnemies: false,
}

export const FULL_WAKE_MASK: SlotWakeMask = {
  self: UnitChannel.All,
  allySide: UnitChannel.All,
  enemySide: UnitChannel.All,
  facts: BattleFact.All,
  selfInEnemies: false,
}

export interface CombatFactDelta {
  readonly allies: Uint8Array | null
  readonly enemies: Uint8Array | null
  readonly allyUnion: number
  readonly enemyUnion: number
  readonly facts: number
}

export const FULL_FACT_DELTA: CombatFactDelta = {
  allies: null,
  enemies: null,
  allyUnion: UnitChannel.All,
  enemyUnion: UnitChannel.All,
  facts: BattleFact.All,
}

export function unionWakeMask(a: SlotWakeMask, b: SlotWakeMask): SlotWakeMask {
  return {
    self: a.self | b.self,
    allySide: a.allySide | b.allySide,
    enemySide: a.enemySide | b.enemySide,
    facts: a.facts | b.facts,
    selfInEnemies: a.selfInEnemies || b.selfInEnemies,
  }
}

export function maskIntersects(a: SlotWakeMask, b: SlotWakeMask): boolean {
  return (
    (a.self & b.self) !== 0 ||
    (a.allySide & b.allySide) !== 0 ||
    (a.enemySide & b.enemySide) !== 0 ||
    (a.facts & b.facts) !== 0
  )
}

export function wakeMaskWakesOn(mask: SlotWakeMask, delta: CombatFactDelta, selfIndex: number): boolean {
  if ((mask.facts & delta.facts) !== 0) return true
  if ((mask.allySide & delta.allyUnion) !== 0) return true
  if ((mask.enemySide & delta.enemyUnion) !== 0) return true
  if (mask.self !== 0) {
    const side = mask.selfInEnemies ? delta.enemies : delta.allies
    if (!side || selfIndex < 0 || selfIndex >= side.length) return true
    if ((mask.self & side[selfIndex]) !== 0) return true
  }
  return false
}

export function resolveTriggerListWakeMask(
  triggers: readonly TriggerConditionJson[],
  selfInEnemies: boolean,
): SlotWakeMask {
  let mask = EMPTY_WAKE_MASK
  for (let i = 0; i < triggers.length; i++) {
    mask = unionWakeMask(mask, resolveSingleTriggerMask(triggers[i], selfInEnemies))
  }
  if (selfInEnemies && mask.selfInEnemies !== selfInEnemies) {
    mask = { ...mask, selfInEnemies }
  }
  return mask
}

function resolveSingleTriggerMask(trigger: TriggerConditionJson, selfInEnemies: boolean): SlotWakeMask {
  const dependency = typeof trigger.dependency === 'string' ? trigger.dependency : ''
  const condition = typeof trigger.condition === 'string' ? trigger.condition : ''
  const opponentSideIsAllies = selfInEnemies

  if (dependency === 'battle') {
    return condition === 'elapsed-seconds'
      ? { ...EMPTY_WAKE_MASK, facts: BattleFact.Elapsed, selfInEnemies }
      : FULL_WAKE_MASK
  }

  if (dependency === 'ally' || dependency === 'allies' || dependency === 'enemy' || dependency === 'enemies') {
    const isAllySet = dependency === 'ally' || dependency === 'allies'
    const targetSideIsEnemies = isAllySet ? selfInEnemies : !selfInEnemies
    const factBit = targetSideIsEnemies ? BattleFact.EnemyList : BattleFact.AllyList
    let ch = 0
    if (condition === 'alive-count' || condition === 'dead-count') ch = UnitChannel.Alive
    else if (condition === 'total-count') ch = 0
    else if (condition === 'total-hp-percentage') ch = UnitChannel.Alive | UnitChannel.Hp | UnitChannel.Stats
    else if (condition === 'total-mana-percentage') ch = UnitChannel.Mana | UnitChannel.Stats
    else if (condition === 'lowest-hp-percentage' || condition === 'highest-hp-percentage') {
      ch = UnitChannel.Alive | UnitChannel.Hp | UnitChannel.Stats
    } else if (
      condition === 'has-player' ||
      condition === 'has-elite' ||
      condition === 'has-boss' ||
      condition === 'has-summon'
    ) {
      ch = UnitChannel.Alive
    } else {
      return FULL_WAKE_MASK
    }
    return targetSideIsEnemies
      ? { ...EMPTY_WAKE_MASK, enemySide: ch, facts: factBit, selfInEnemies }
      : { ...EMPTY_WAKE_MASK, allySide: ch, facts: factBit, selfInEnemies }
  }

  let condCh = 0
  if (condition === 'current-hp') condCh = UnitChannel.Hp
  else if (condition === 'missing-hp' || condition === 'current-hp-percentage' || condition === 'missing-hp-percentage') {
    condCh = UnitChannel.Hp | UnitChannel.Stats
  } else if (condition === 'current-mana') condCh = UnitChannel.Mana
  else if (
    condition === 'missing-mana' ||
    condition === 'current-mana-percentage' ||
    condition === 'missing-mana-percentage'
  ) {
    condCh = UnitChannel.Mana | UnitChannel.Stats
  } else if (condition === 'buff-stacks') {
    condCh = UnitChannel.Buff
  } else if (
    condition === 'is-player' ||
    condition === 'is-elite' ||
    condition === 'is-boss' ||
    condition === 'is-summon' ||
    condition === 'stun-status' ||
    condition === 'blind-status' ||
    condition === 'silence-status'
  ) {
    condCh = UnitChannel.None
  } else {
    return FULL_WAKE_MASK
  }

  let baseMask: SlotWakeMask
  if (dependency === 'self') {
    baseMask = { ...EMPTY_WAKE_MASK, self: condCh, selfInEnemies }
  } else if (dependency === 'target' || dependency === 'target-enemy') {
    const ch = UnitChannel.Alive | UnitChannel.Threat | condCh
    baseMask = opponentSideIsAllies
      ? { ...EMPTY_WAKE_MASK, allySide: ch, facts: BattleFact.AllyList, selfInEnemies }
      : { ...EMPTY_WAKE_MASK, enemySide: ch, facts: BattleFact.EnemyList, selfInEnemies }
  } else if (dependency === 'ally-lowest-hp' || dependency === 'ally-highest-hp') {
    const ch = UnitChannel.Alive | UnitChannel.Hp | UnitChannel.Stats | condCh
    baseMask = selfInEnemies
      ? { ...EMPTY_WAKE_MASK, enemySide: ch, facts: BattleFact.EnemyList, selfInEnemies }
      : { ...EMPTY_WAKE_MASK, allySide: ch, facts: BattleFact.AllyList, selfInEnemies }
  } else if (dependency === 'enemy-lowest-hp' || dependency === 'enemy-highest-hp') {
    const ch = UnitChannel.Alive | UnitChannel.Hp | UnitChannel.Stats | condCh
    baseMask = opponentSideIsAllies
      ? { ...EMPTY_WAKE_MASK, allySide: ch, facts: BattleFact.AllyList, selfInEnemies }
      : { ...EMPTY_WAKE_MASK, enemySide: ch, facts: BattleFact.EnemyList, selfInEnemies }
  } else {
    return FULL_WAKE_MASK
  }

  if (condition === 'buff-stacks') {
    baseMask = unionWakeMask(baseMask, { ...EMPTY_WAKE_MASK, facts: BattleFact.External })
  }
  return baseMask
}

interface UnitSnap {
  hp: number
  mana: number
  alive: boolean
  buffCount: number
  buffStackSum: number
  buffExpirySum: number
  threat: number
  stats: CombatantStats | null
}

function captureUnitSnap(unit: CombatantState): UnitSnap {
  let stackSum = 0
  let expirySum = 0
  const buffs = unit.buffs
  for (let i = 0; i < buffs.length; i++) {
    stackSum += buffs[i].stacks
    expirySum += buffs[i].expiresAtSeconds
  }
  return {
    hp: unit.hitpoints,
    mana: unit.manapoints,
    alive: unit.isAlive,
    buffCount: buffs.length,
    buffStackSum: stackSum,
    buffExpirySum: expirySum,
    threat: unit.effectiveThreat,
    stats: unit.stats,
  }
}

function diffUnitList(
  units: readonly CombatantState[],
  snaps: UnitSnap[],
  delta: Uint8Array,
): number {
  let union = 0
  for (let i = 0; i < units.length; i++) {
    const snap = captureUnitSnap(units[i])
    const prev = snaps[i]
    let ch = 0
    if (!prev || prev.stats === null) {
      ch = 0xff
    } else {
      if (prev.stats !== snap.stats) ch |= UnitChannel.Stats | UnitChannel.Hp | UnitChannel.Mana
      if (prev.hp !== snap.hp) ch |= UnitChannel.Hp
      if (prev.mana !== snap.mana) ch |= UnitChannel.Mana
      if (prev.alive !== snap.alive) ch |= UnitChannel.Alive | UnitChannel.Hp
      if (prev.threat !== snap.threat) ch |= UnitChannel.Threat
      if (
        prev.buffCount !== snap.buffCount ||
        prev.buffStackSum !== snap.buffStackSum ||
        prev.buffExpirySum !== snap.buffExpirySum
      ) {
        ch |= UnitChannel.Buff
      }
    }
    snaps[i] = snap
    delta[i] = ch
    union |= ch
  }
  return union
}

function invalidateVerdicts(units: readonly CombatantState[]): void {
  for (let i = 0; i < units.length; i++) {
    units[i].skillWakePending = null
    units[i].skillTriggerVerdicts = null
    units[i].skillVerdictTargets = null
  }
}

export class CombatFactTracker {
  private allies: UnitSnap[]
  private enemies: UnitSnap[]
  private allyDelta: Uint8Array
  private enemyDelta: Uint8Array
  private pendingExternal = 0

  constructor(state: CombatEncounterState) {
    state.wakeTracking = true
    invalidateVerdicts(state.playerAllies)
    invalidateVerdicts(state.enemies)
    this.allies = state.playerAllies.map(captureUnitSnap)
    this.enemies = state.enemies.map(captureUnitSnap)
    this.allyDelta = new Uint8Array(this.allies.length)
    this.enemyDelta = new Uint8Array(this.enemies.length)
  }

  markExternal(): void {
    this.pendingExternal |= BattleFact.External
  }

  advance(state: CombatEncounterState): CombatFactDelta {
    let facts = this.pendingExternal | BattleFact.Elapsed
    this.pendingExternal = 0
    if (state.playerAllies.length !== this.allies.length) {
      facts |= BattleFact.AllyList
      this.allies.length = state.playerAllies.length
      this.allyDelta = new Uint8Array(state.playerAllies.length)
    }
    if (state.enemies.length !== this.enemies.length) {
      facts |= BattleFact.EnemyList
      this.enemies.length = state.enemies.length
      this.enemyDelta = new Uint8Array(state.enemies.length)
    }
    const allyUnion = diffUnitList(state.playerAllies, this.allies, this.allyDelta)
    const enemyUnion = diffUnitList(state.enemies, this.enemies, this.enemyDelta)
    return {
      allies: this.allyDelta,
      enemies: this.enemyDelta,
      allyUnion,
      enemyUnion,
      facts,
    }
  }
}

export function foldPendingWake(encounter: CombatEncounterState, delta: CombatFactDelta): void {
  foldSide(encounter.playerAllies, delta.allies, delta.allyUnion, delta.enemyUnion, delta.facts)
  foldSide(encounter.enemies, delta.enemies, delta.allyUnion, delta.enemyUnion, delta.facts)
}

function foldSide(
  units: readonly CombatantState[],
  selfDelta: Uint8Array | null,
  allyUnion: number,
  enemyUnion: number,
  facts: number,
): void {
  if (!selfDelta) return
  const len = Math.min(selfDelta.length, units.length)
  for (let i = 0; i < len; i++) {
    const pending = units[i].skillWakePending
    if (!pending) continue
    const dSelf = selfDelta[i]
    for (let s = 0; s < pending.length; s++) {
      const p = pending[s]
      pending[s] = {
        self: p.self | dSelf,
        allySide: p.allySide | allyUnion,
        enemySide: p.enemySide | enemyUnion,
        facts: p.facts | facts,
        selfInEnemies: p.selfInEnemies,
      }
    }
  }
}
