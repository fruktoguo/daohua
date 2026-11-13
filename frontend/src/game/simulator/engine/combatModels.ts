// 战斗单位、遭遇与单位统计簿运行时模型（严格对应 Domain.Combat.CombatModels / CombatUnitStatsBook / CombatTraitListeners）

import type { SkillSlotDefinition } from './catalog.ts'
import {
  BATTLE_LIFETIME_SECONDS,
  BUFF_CHANNEL_BY_KEY,
  BUFF_CHANNELS,
  BuffEffectChannel,
  BuffEffectStackMode,
  BuffEffectTickGroup,
  BuffIds,
  CombatDamageKind,
  CombatMonsterRank,
  CombatRespawnPolicy,
  PercentCurveMode,
  TraitIds,
  UNSEEN_BUFF_DURATION_SECONDS,
  containsTrait,
  percentMultiplier,
  traitLevelScale,
  type BuffEffect,
  type CombatantStats,
} from './rules.ts'
import type { SlotWakeMask } from './triggerWake.ts'

export const CombatOutcome = {
  Running: 0,
  PlayerWon: 1,
  EnemyWon: 2,
  Draw: 3,
} as const
export type CombatOutcome = (typeof CombatOutcome)[keyof typeof CombatOutcome]

export const CombatStatUnitKind = {
  Self: 0,
  Ally: 1,
  Monster: 2,
} as const
export type CombatStatUnitKind = (typeof CombatStatUnitKind)[keyof typeof CombatStatUnitKind]

export const BASE_THREAT = 100
export const RESPAWN_RETRY_STEP_SECONDS = 1

export interface CombatBuffState {
  buffId: string
  expiresAtSeconds: number
  stacks: number
  name: string
  resetOnWave: boolean
  missingHitpointsPerStack: number
  effects: BuffEffect[] | null
  statRatios: Record<string, number> | null
  sourceUnitId: string | null
}

export interface CombatantTemplate {
  stats: CombatantStats
  skills?: SkillSlotDefinition[] | null
  definitionId?: string | null
  rank?: CombatMonsterRank | null
  isSummon?: boolean
}

export interface CombatantSnapshot {
  hitpoints: number
  manapoints: number
  nextAttackAtSeconds: number
  attackCount: number
  threat: number
  pendingSkillIndex: number
  threatBonusPoints: number
  bonusAbsorptionPoints: number
  skillCooldownUntil: number[] | null
  skillCastCount: number
  buffs: CombatBuffState[] | null
  respawnAtSeconds: number | null
  respawnPolicy: CombatRespawnPolicy
  respawnTargetIndex: number
  respawnDeadlineSeconds: number | null
  respawnWaitingUntilSeconds: number | null
  traits: string[]
  traitLevels: Record<string, number> | null
}

export interface CombatEvent {
  atSeconds: number
  attackerId: string
  targetId: string
  damage: number
  hit: boolean
  damageKind: CombatDamageKind
  targetHitpointsAfter: number
  critical: boolean
  break: boolean
  parried: boolean
  abilityId: string | null
  healing: number
  attackerManapointsAfter: number | null
  targetManapointsAfter: number | null
}

export interface AllyMark {
  memberIndex: number
  atSeconds: number
}

export interface CombatAdvanceResult {
  encounterId: string
  fromSeconds: number
  toSeconds: number
  outcome: CombatOutcome
  events: CombatEvent[]
  sequence: number
  playerDeathTimestamps: number[] | null
  playerRespawnTimestamps: number[] | null
  allyDeathMarks: AllyMark[] | null
  allyRespawnMarks: AllyMark[] | null
}

const CHANNEL_COUNT = BUFF_CHANNELS.length
const CHANNEL_INFO_LIST = BUFF_CHANNELS.map((c) => BUFF_CHANNEL_BY_KEY.get(c.configKey)!)

export class CombatantState {
  readonly id: string
  entityId = -1
  definitionId: string | null = null
  rank: CombatMonsterRank | null = null
  isSummon = false
  stats: CombatantStats
  traits: string[]
  traitLevels: Record<string, number> | null
  readonly skills: SkillSlotDefinition[]
  private _buffs: CombatBuffState[] = []
  externalBuffStacks: ((buffId: string | null) => number) | null = null

  hitpoints: number
  manapoints: number
  nextAttackAtSeconds: number
  attackCount = 0
  threat: number = BASE_THREAT
  threatBonusPoints = 0
  bonusAbsorptionPoints = 0
  pendingSkillIndex = -1
  skillCooldownUntil: number[] | null = null
  skillWakeMasks: SlotWakeMask[] | null = null
  skillWakePending: SlotWakeMask[] | null = null
  skillTriggerVerdicts: (boolean | null)[] | null = null
  skillVerdictTargets: (CombatantState | null)[] | null = null
  skillCastCount = 0
  lastTianleiTriggerAtSeconds = -1
  lastXueshaTriggerAtSeconds = -1
  respawnAtSeconds = Number.POSITIVE_INFINITY
  respawnPolicy: CombatRespawnPolicy = CombatRespawnPolicy.Immediate
  respawnTargetIndex = -1
  respawnDeadlineSeconds = Number.POSITIVE_INFINITY
  respawnWaitingUntilSeconds = Number.POSITIVE_INFINITY

  private _buffTotals: Float64Array | null = null
  private _buffRegenPending = false
  private _buffTick1sPending = false
  private _effectiveStatsCache: CombatantStats | null = null

  constructor(id: string, stats: CombatantStats, skills?: SkillSlotDefinition[] | null) {
    this.id = id
    this.traits = stats.traits ? [...stats.traits] : []
    this.traitLevels = stats.traitLevels ? { ...stats.traitLevels } : null
    this.stats = {
      ...stats,
      traits: this.traits,
      traitLevels: this.traitLevels,
    }
    this.hitpoints = stats.maxHitpoints
    this.manapoints = stats.maxManapoints
    this.nextAttackAtSeconds = stats.attackIntervalSeconds
    this.skills = skills ? [...skills] : []
  }

  get isPlayer(): boolean {
    return this.id.startsWith('player')
  }

  get buffs(): readonly CombatBuffState[] {
    return this._buffs
  }

  get isAlive(): boolean {
    return this.hitpoints > 1e-12
  }

  get effectiveThreat(): number {
    return (
      this.threat *
      Math.max(
        0,
        1 +
          (this.stats.threatBonusPoints +
            this.threatBonusPoints +
            this.buffTotal(BuffEffectChannel.ThreatPoints)) /
            100,
      )
    )
  }

  invalidateBuffAggregates(): void {
    this._buffTotals = null
    this._effectiveStatsCache = null
  }

  setBuff(buff: CombatBuffState): void {
    const idx = this._buffs.findIndex((b) => b.buffId === buff.buffId)
    if (idx < 0) this._buffs.push(buff)
    else this._buffs[idx] = buff
    this.invalidateBuffAggregates()
  }

  addBuff(buff: CombatBuffState): void {
    this._buffs.push(buff)
    this.invalidateBuffAggregates()
  }

  removeBuffsWhere(predicate: (buff: CombatBuffState) => boolean): void {
    let write = 0
    for (let i = 0; i < this._buffs.length; i++) {
      if (!predicate(this._buffs[i])) {
        this._buffs[write++] = this._buffs[i]
      }
    }
    if (write < this._buffs.length) {
      this._buffs.length = write
      this.invalidateBuffAggregates()
    }
  }

  replaceBuffs(buffs: readonly CombatBuffState[] | null | undefined): void {
    this._buffs.length = 0
    if (buffs) {
      for (let i = 0; i < buffs.length; i++) {
        this._buffs.push({ ...buffs[i] })
      }
    }
    this.invalidateBuffAggregates()
  }

  private ensureBuffTotals(): Float64Array {
    if (this._buffTotals !== null) return this._buffTotals
    const totals = new Float64Array(CHANNEL_COUNT)
    let regenPending = false
    let tick1sPending = false
    for (let i = 0; i < this._buffs.length; i++) {
      const buff = this._buffs[i]
      const effects = buff.effects
      if (!effects) continue
      for (let j = 0; j < effects.length; j++) {
        const eff = effects[j]
        const info = CHANNEL_INFO_LIST[eff.channel]
        totals[eff.channel] +=
          info.stackMode === BuffEffectStackMode.PerStack ? eff.value * buff.stacks : eff.value
        if (eff.value === 0) continue
        if (info.tickGroup === BuffEffectTickGroup.Regen5s) regenPending = true
        else if (info.tickGroup === BuffEffectTickGroup.Tick1s) tick1sPending = true
      }
    }
    this._buffRegenPending = regenPending
    this._buffTick1sPending = tick1sPending
    this._buffTotals = totals
    return totals
  }

  buffTotal(channel: BuffEffectChannel): number {
    return this.ensureBuffTotals()[channel]
  }

  get buffRegenTickPending(): boolean {
    this.ensureBuffTotals()
    return this._buffRegenPending
  }

  get buffSecondTickPending(): boolean {
    this.ensureBuffTotals()
    return this._buffTick1sPending
  }

  get effectiveStats(): CombatantStats {
    if (this._buffs.length === 0) return this.stats
    if (this._effectiveStatsCache !== null) return this._effectiveStatsCache
    const totals = this.ensureBuffTotals()
    const resistanceTotal = totals[BuffEffectChannel.ResistancePoints]
    const resistance =
      resistanceTotal === 0 ? 1 : percentMultiplier(resistanceTotal, PercentCurveMode.Linear)
    const next: CombatantStats = {
      ...this.stats,
      attackPower: Math.max(0, this.stats.attackPower + totals[BuffEffectChannel.AttackPower]),
      accuracy: Math.max(0, this.stats.accuracy + totals[BuffEffectChannel.Accuracy]),
      critical: Math.max(0, this.stats.critical + totals[BuffEffectChannel.Critical]),
      actionSpeedPoints: this.stats.actionSpeedPoints + totals[BuffEffectChannel.ActionSpeedPoints],
      criticalDamagePoints:
        this.stats.criticalDamagePoints + totals[BuffEffectChannel.CriticalDamagePoints],
      effects: {
        ...this.stats.effects,
        healingBonusPoints:
          this.stats.effects.healingBonusPoints + totals[BuffEffectChannel.HealingBonusPoints],
      },
      defense: Math.max(0, this.stats.defense * resistance),
      magicResistance: Math.max(0, this.stats.magicResistance * resistance),
      evasion: Math.max(
        0,
        (this.stats.evasion + totals[BuffEffectChannel.Evasion]) *
          (1 + totals[BuffEffectChannel.EvasionPostRatio]),
      ),
    }
    this._effectiveStatsCache = next
    return next
  }

  refreshStats(stats: CombatantStats): void {
    this.stats = {
      ...stats,
      traits: this.traits,
      traitLevels: this.traitLevels,
    }
    this.invalidateBuffAggregates()
  }

  restoreTraits(traits: string[], traitLevels: Record<string, number> | null): void {
    this.traits = [...traits]
    this.traitLevels = traitLevels ? { ...traitLevels } : null
    this.stats = {
      ...this.stats,
      traits: this.traits,
      traitLevels: this.traitLevels,
    }
    this.invalidateBuffAggregates()
  }

  buffStacks(buffId: string | null): number {
    let total = 0
    for (let i = 0; i < this._buffs.length; i++) {
      const curId = this._buffs[i].buffId
      if (
        buffId === null ||
        curId === buffId ||
        (curId.length > buffId.length &&
          curId.charCodeAt(buffId.length) === 58 /* ':' */ &&
          curId.startsWith(buffId))
      ) {
        total += this._buffs[i].stacks
      }
    }
    return total + (this.externalBuffStacks ? this.externalBuffStacks(buffId) : 0)
  }

  syncDerivedStacks(): void {
    if (this._buffs.length === 0) return
    const missing = Math.max(0, this.stats.maxHitpoints - this.hitpoints)
    for (let i = 0; i < this._buffs.length; i++) {
      const buff = this._buffs[i]
      if (buff.missingHitpointsPerStack <= 0) continue
      const stacks = Math.floor(missing / buff.missingHitpointsPerStack + 1e-9)
      if (buff.stacks === stacks) continue
      this._buffs[i] = { ...buff, stacks }
      this.invalidateBuffAggregates()
    }
  }
}

export function sumBuffEffect(
  buffs: readonly CombatBuffState[] | null | undefined,
  channel: BuffEffectChannel,
): number {
  let total = 0
  if (!buffs) return total
  const info = CHANNEL_INFO_LIST[channel]
  for (let i = 0; i < buffs.length; i++) {
    const effects = buffs[i].effects
    if (!effects) continue
    for (let j = 0; j < effects.length; j++) {
      if (effects[j].channel !== channel) continue
      total +=
        info.stackMode === BuffEffectStackMode.PerStack
          ? effects[j].value * buffs[i].stacks
          : effects[j].value
    }
  }
  return total
}

export function computeEffectiveThreat(
  threat: number,
  statsThreatBonusPoints: number,
  threatBonusPoints: number,
  buffs: readonly CombatBuffState[] | null | undefined,
): number {
  return (
    threat *
    Math.max(
      0,
      1 +
        (statsThreatBonusPoints +
          threatBonusPoints +
          sumBuffEffect(buffs, BuffEffectChannel.ThreatPoints)) /
          100,
    )
  )
}

export function applyBattleStartTraits(unit: CombatantState): void {
  const traits = unit.stats.traits
  const traitLevels = unit.stats.traitLevels
  if (containsTrait(traits, TraitIds.FirstSight)) {
    unit.addBuff({
      buffId: 'buff.chujian',
      expiresAtSeconds: 2,
      stacks: 1,
      name: '初见',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: [
        {
          channel: BuffEffectChannel.EvasionPostRatio,
          value: 3 * traitLevelScale(traitLevels, TraitIds.FirstSight),
        },
      ],
      statRatios: null,
      sourceUnitId: null,
    })
  }
  if (containsTrait(traits, TraitIds.Haste)) {
    unit.addBuff({
      buffId: 'buff.yusuzebuda',
      expiresAtSeconds: BATTLE_LIFETIME_SECONDS,
      stacks: 1,
      name: '欲速则不达',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: [
        {
          channel: BuffEffectChannel.ActionSpeedPoints,
          value: 100 * traitLevelScale(traitLevels, TraitIds.Haste),
        },
        { channel: BuffEffectChannel.DamageBonusPoints, value: -100 },
        { channel: BuffEffectChannel.HealingBonusPoints, value: -100 },
      ],
      statRatios: null,
      sourceUnitId: null,
    })
  }
  if (containsTrait(traits, TraitIds.Unseen)) {
    const lv =
      traitLevels && Object.prototype.hasOwnProperty.call(traitLevels, TraitIds.Unseen)
        ? Math.max(1, traitLevels[TraitIds.Unseen])
        : 1
    const duration = UNSEEN_BUFF_DURATION_SECONDS * (1.0 + 0.05 * (lv - 1))
    unit.addBuff({
      buffId: BuffIds.Unseen,
      expiresAtSeconds: duration,
      stacks: 1,
      name: '隐身',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: [{ channel: BuffEffectChannel.ThreatPoints, value: -100 }],
      statRatios: null,
      sourceUnitId: null,
    })
  }
}

interface TraitCheckerBinding {
  order: number
  unit: CombatantState
  opponents: readonly CombatantState[]
  allies: readonly CombatantState[]
  onChanged: (
    unit: CombatantState,
    ownAliveCount: number,
    opponentAliveCount: number,
  ) => void
}

function countAliveUnits(units: readonly CombatantState[]): number {
  let c = 0
  for (let i = 0; i < units.length; i++) {
    if (units[i].isAlive) c++
  }
  return c
}

function syncOutnumberedBuff(
  unit: CombatantState,
  _ownAliveCount: number,
  opponentAliveCount: number,
): void {
  const stacks = Math.max(0, opponentAliveCount - 1)
  if (stacks <= 0) {
    unit.removeBuffsWhere((b) => b.buffId === BuffIds.Outnumbered)
    return
  }
  const scale = traitLevelScale(unit.stats.traitLevels, TraitIds.Outnumbered)
  const val = 10 * scale
  unit.setBuff({
    buffId: BuffIds.Outnumbered,
    expiresAtSeconds: BATTLE_LIFETIME_SECONDS,
    stacks,
    name: '我要打十个',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [
      { channel: BuffEffectChannel.DamageBonusPoints, value: val },
      { channel: BuffEffectChannel.DamageAbsorptionPoints, value: val },
    ],
    statRatios: null,
    sourceUnitId: null,
  })
}

function syncDuelKingBuff(
  unit: CombatantState,
  ownAliveCount: number,
  opponentAliveCount: number,
): void {
  if (!(ownAliveCount === 1 && opponentAliveCount === 1)) {
    unit.removeBuffsWhere((b) => b.buffId === BuffIds.DuelKing)
    return
  }
  const scale = traitLevelScale(unit.stats.traitLevels, TraitIds.DuelKing)
  const val = 20 * scale
  unit.setBuff({
    buffId: BuffIds.DuelKing,
    expiresAtSeconds: BATTLE_LIFETIME_SECONDS,
    stacks: 1,
    name: '单挑王',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [
      { channel: BuffEffectChannel.DamageBonusPoints, value: val },
      { channel: BuffEffectChannel.DamageAbsorptionPoints, value: val },
      { channel: BuffEffectChannel.CriticalDamagePoints, value: val },
      { channel: BuffEffectChannel.ActionSpeedPoints, value: val },
    ],
    statRatios: null,
    sourceUnitId: null,
  })
}

export class CombatTraitListeners {
  private readonly encounter: CombatEncounterState
  private bindings: TraitCheckerBinding[] = []

  constructor(encounter: CombatEncounterState) {
    this.encounter = encounter
  }

  rebuild(): void {
    this.bindings = []
    this.registerSide(this.encounter.playerAllies, this.encounter.enemies)
    this.registerSide(this.encounter.enemies, this.encounter.playerAllies)
    this.bindings.sort((a, b) => a.order - b.order)
    this.aliveCountsChanged()
  }

  private registerSide(
    allies: readonly CombatantState[],
    opponents: readonly CombatantState[],
  ): void {
    for (let i = 0; i < allies.length; i++) {
      const unit = allies[i]
      if (containsTrait(unit.stats.traits, TraitIds.Outnumbered)) {
        this.bindings.push({
          order: 100,
          unit,
          allies,
          opponents,
          onChanged: syncOutnumberedBuff,
        })
      }
      if (containsTrait(unit.stats.traits, TraitIds.DuelKing)) {
        this.bindings.push({
          order: 200,
          unit,
          allies,
          opponents,
          onChanged: syncDuelKingBuff,
        })
      }
    }
  }

  aliveCountsChanged(): void {
    if (this.bindings.length === 0) return
    const allyAlive = countAliveUnits(this.encounter.playerAllies)
    const enemyAlive = countAliveUnits(this.encounter.enemies)
    for (let i = 0; i < this.bindings.length; i++) {
      const b = this.bindings[i]
      const ownAlive = b.allies === this.encounter.playerAllies ? allyAlive : enemyAlive
      const oppAlive = b.allies === this.encounter.playerAllies ? enemyAlive : allyAlive
      b.onChanged(b.unit, ownAlive, oppAlive)
    }
  }
}

export interface CombatUnitStatEntry {
  unitKey: string
  kind: CombatStatUnitKind
  displayName: string
  damageDealtPhysical: number
  damageDealtSpell: number
  damageDealtTrue: number
  damageTakenTrue: number
  damageTakenPhysical: number
  damageTakenSpell: number
  damageMitigatedPhysical: number
  damageMitigatedSpell: number
  healingRegen: number
  healingOther: number
}

function statKindOf(unitKey: string): CombatStatUnitKind {
  if (unitKey === 'player') return CombatStatUnitKind.Self
  if (unitKey.startsWith('ally:')) return CombatStatUnitKind.Ally
  return CombatStatUnitKind.Monster
}

export type CombatUnitStatDocument = CombatUnitStatEntry

export class CombatUnitStatsBook {
  private readonly _entries: CombatUnitStatEntry[] = []
  private readonly _byKey = new Map<string, CombatUnitStatEntry>()
  dirty = false

  get entries(): readonly CombatUnitStatEntry[] {
    return this._entries
  }

  clearDirty(): void {
    this.dirty = false
  }

  clear(): void {
    this._entries.length = 0
    this._byKey.clear()
    this.dirty = true
  }

  importDoc(doc: CombatUnitStatEntry): void {
    const e = this.register(doc.unitKey, doc.displayName)
    e.damageDealtPhysical += doc.damageDealtPhysical
    e.damageDealtSpell += doc.damageDealtSpell
    e.damageDealtTrue += doc.damageDealtTrue
    e.damageTakenTrue += doc.damageTakenTrue
    e.damageTakenPhysical += doc.damageTakenPhysical
    e.damageTakenSpell += doc.damageTakenSpell
    e.damageMitigatedPhysical += doc.damageMitigatedPhysical
    e.damageMitigatedSpell += doc.damageMitigatedSpell
    e.healingRegen += doc.healingRegen
    e.healingOther += doc.healingOther
    this.dirty = true
  }

  static keyOf(unit: CombatantState): string {
    return unit.id.startsWith('enemy:') && unit.definitionId !== null
      ? `monster:${unit.definitionId}`
      : unit.id
  }

  entry(unitKey: string): CombatUnitStatEntry {
    let e = this._byKey.get(unitKey)
    if (e) return e
    e = {
      unitKey,
      kind: statKindOf(unitKey),
      displayName: '',
      damageDealtPhysical: 0,
      damageDealtSpell: 0,
      damageDealtTrue: 0,
      damageTakenTrue: 0,
      damageTakenPhysical: 0,
      damageTakenSpell: 0,
      damageMitigatedPhysical: 0,
      damageMitigatedSpell: 0,
      healingRegen: 0,
      healingOther: 0,
    }
    this._byKey.set(unitKey, e)
    this._entries.push(e)
    this.dirty = true
    return e
  }

  register(unitKey: string, displayName: string): CombatUnitStatEntry {
    const e = this.entry(unitKey)
    if (e.displayName.length === 0 && displayName.length > 0) {
      e.displayName = displayName
      this.dirty = true
    }
    return e
  }

  recordHitUnit(
    attacker: CombatantState,
    target: CombatantState,
    kind: CombatDamageKind,
    damage: number,
    mitigated: number,
  ): void {
    this.recordHitKey(
      CombatUnitStatsBook.keyOf(attacker),
      CombatUnitStatsBook.keyOf(target),
      kind,
      damage,
      mitigated,
    )
  }

  recordHitKey(
    attackerKey: string,
    targetKey: string,
    kind: CombatDamageKind,
    damage: number,
    mitigated: number,
  ): void {
    if (damage <= 0 && mitigated <= 0) return
    const attacker = this.entry(attackerKey)
    const target = this.entry(targetKey)
    if (kind === CombatDamageKind.True) {
      attacker.damageDealtTrue += damage
      target.damageTakenTrue += damage
    } else if (kind === CombatDamageKind.Spell) {
      attacker.damageDealtSpell += damage
      target.damageTakenSpell += damage
      target.damageMitigatedSpell += mitigated
    } else {
      attacker.damageDealtPhysical += damage
      target.damageTakenPhysical += damage
      target.damageMitigatedPhysical += mitigated
    }
    this.dirty = true
  }

  recordHealingUnit(unit: CombatantState, autoRegen: boolean, amount: number): void {
    this.recordHealingKey(CombatUnitStatsBook.keyOf(unit), '', autoRegen, amount)
  }

  recordHealingKey(
    unitKey: string,
    displayName: string,
    autoRegen: boolean,
    amount: number,
  ): void {
    if (amount <= 0) return
    const e = displayName.length > 0 ? this.register(unitKey, displayName) : this.entry(unitKey)
    if (autoRegen) e.healingRegen += amount
    else e.healingOther += amount
    this.dirty = true
  }
}

export interface PendingCombatTrigger {
  attacker: CombatantState
  target: CombatantState
  hitContext: CombatHitContext
  events: CombatEvent[]
  mainEventIndex: number
}

export interface CombatHitContext {
  encounter: CombatEncounterState
  attacker: CombatantState
  target: CombatantState
  attackStats: CombatantStats
  abilityId: string | null
  side: bigint
  count: number
  atSeconds: number
  broken: boolean
  parried: boolean
  hit: boolean
  critical: boolean
  rawDamage: number
  damage: number
  actualDamage: number
}

export class CombatEncounterState {
  readonly encounterId: string
  wakeTracking = false
  readonly seed: bigint
  elapsedSeconds = 0
  readonly playerAllies: CombatantState[]
  readonly selfIndex: number
  readonly enemies: CombatantState[]
  readonly traitListeners: CombatTraitListeners
  outcome: CombatOutcome = CombatOutcome.Running
  sequence = 0
  readonly playerRespawnSeconds: number
  readonly pendingTriggers: PendingCombatTrigger[] = []
  statsBook: CombatUnitStatsBook | null = null

  private constructor(
    encounterId: string,
    seed: bigint,
    playerAllies: CombatantState[],
    selfIndex: number,
    enemies: CombatantState[],
    playerRespawnSeconds: number,
  ) {
    this.encounterId = encounterId
    this.seed = seed
    this.playerAllies = playerAllies
    this.selfIndex = selfIndex
    this.enemies = enemies
    this.playerRespawnSeconds = playerRespawnSeconds
    this.traitListeners = new CombatTraitListeners(this)
  }

  get player(): CombatantState {
    return this.playerAllies[this.selfIndex]
  }

  get playerRespawnAtSeconds(): number {
    return this.player.respawnAtSeconds
  }

  set playerRespawnAtSeconds(val: number) {
    this.player.respawnAtSeconds = val
  }

  get isPlayerDowned(): boolean {
    return !this.player.isAlive && Number.isFinite(this.player.respawnAtSeconds)
  }

  get isAllAlliesDowned(): boolean {
    for (let i = 0; i < this.playerAllies.length; i++) {
      if (this.playerAllies[i].isAlive) return false
    }
    return true
  }

  get nextAllyRespawnAtSeconds(): number {
    let next = Number.POSITIVE_INFINITY
    for (let i = 0; i < this.playerAllies.length; i++) {
      if (!this.playerAllies[i].isAlive && this.playerAllies[i].respawnAtSeconds < next) {
        next = this.playerAllies[i].respawnAtSeconds
      }
    }
    return next
  }

  get primaryEnemy(): CombatantState | null {
    let primary: CombatantState | null = null
    for (let i = 0; i < this.enemies.length; i++) {
      const e = this.enemies[i]
      if (e.isAlive && (primary === null || e.effectiveThreat > primary.effectiveThreat)) {
        primary = e
      }
    }
    return primary
  }

  initializeTraitListeners(): void {
    this.traitListeners.rebuild()
  }

  notifyAliveCountsChanged(): void {
    this.traitListeners.aliveCountsChanged()
  }

  canRespawn(allyIndex: number): { ok: boolean; retryAtSeconds: number } {
    const ally = this.playerAllies[allyIndex]
    if (ally.respawnDeadlineSeconds <= this.elapsedSeconds + 1e-12) {
      return { ok: true, retryAtSeconds: Number.POSITIVE_INFINITY }
    }
    if (ally.respawnPolicy === CombatRespawnPolicy.WaitAll) {
      if (this.allAlliesRespawnable(allyIndex)) {
        return { ok: true, retryAtSeconds: Number.POSITIVE_INFINITY }
      }
      return { ok: false, retryAtSeconds: this.nextAllyDecisionAt(allyIndex, ally) }
    }
    if (ally.respawnPolicy === CombatRespawnPolicy.WaitTarget) {
      if (
        ally.respawnTargetIndex < 0 ||
        ally.respawnTargetIndex >= this.playerAllies.length ||
        ally.respawnTargetIndex === allyIndex
      ) {
        return { ok: true, retryAtSeconds: Number.POSITIVE_INFINITY }
      }
      if (this.playerAllies[ally.respawnTargetIndex].isAlive) {
        return { ok: true, retryAtSeconds: Number.POSITIVE_INFINITY }
      }
      return { ok: false, retryAtSeconds: this.nextAllyDecisionAt(allyIndex, ally) }
    }
    return { ok: true, retryAtSeconds: Number.POSITIVE_INFINITY }
  }

  private allAlliesRespawnable(selfIndex: number): boolean {
    for (let i = 0; i < this.playerAllies.length; i++) {
      if (i === selfIndex) continue
      const other = this.playerAllies[i]
      if (other.isAlive) continue
      if (!Number.isFinite(other.respawnAtSeconds)) return false
    }
    return true
  }

  private nextAllyDecisionAt(selfIndex: number, ally: CombatantState): number {
    let next = Number.POSITIVE_INFINITY
    for (let i = 0; i < this.playerAllies.length; i++) {
      if (i === selfIndex) continue
      const other = this.playerAllies[i]
      if (other.isAlive) continue
      if (other.respawnAtSeconds < next) next = other.respawnAtSeconds
    }
    next = Math.min(next, ally.respawnDeadlineSeconds)
    return next > this.elapsedSeconds + 1e-12 ? next : this.elapsedSeconds + RESPAWN_RETRY_STEP_SECONDS
  }

  static create(
    encounterId: string,
    seed: bigint,
    player: CombatantTemplate,
    enemies: readonly CombatantTemplate[],
    playerRespawnSeconds = 0,
  ): CombatEncounterState {
    return CombatEncounterState.createAllied(
      encounterId,
      seed,
      [player],
      0,
      enemies,
      playerRespawnSeconds,
    )
  }

  static createAllied(
    encounterId: string,
    seed: bigint,
    allies: readonly CombatantTemplate[],
    selfIndex: number,
    enemies: readonly CombatantTemplate[],
    playerRespawnSeconds = 0,
  ): CombatEncounterState {
    const allyStates: CombatantState[] = new Array(allies.length)
    for (let i = 0; i < allies.length; i++) {
      const t = allies[i]
      const u = new CombatantState(i === selfIndex ? 'player' : `ally:${i}`, t.stats, t.skills)
      u.definitionId = t.definitionId ?? null
      u.rank = t.rank ?? null
      u.isSummon = Boolean(t.isSummon)
      allyStates[i] = u
    }
    const enemyStates: CombatantState[] = new Array(enemies.length)
    for (let i = 0; i < enemies.length; i++) {
      const t = enemies[i]
      const u = new CombatantState(`enemy:${i}`, t.stats, t.skills)
      u.definitionId = t.definitionId ?? null
      u.rank = t.rank ?? null
      u.isSummon = Boolean(t.isSummon)
      enemyStates[i] = u
    }
    for (let i = 0; i < allyStates.length; i++) applyBattleStartTraits(allyStates[i])
    for (let i = 0; i < enemyStates.length; i++) applyBattleStartTraits(enemyStates[i])
    const encounter = new CombatEncounterState(
      encounterId,
      seed,
      allyStates,
      selfIndex,
      enemyStates,
      playerRespawnSeconds,
    )
    encounter.initializeTraitListeners()
    return encounter
  }

  static snapshot(state: CombatantState): CombatantSnapshot {
    return {
      hitpoints: state.hitpoints,
      manapoints: state.manapoints,
      nextAttackAtSeconds: state.nextAttackAtSeconds,
      attackCount: state.attackCount,
      threat: state.threat,
      pendingSkillIndex: state.pendingSkillIndex,
      threatBonusPoints: state.threatBonusPoints,
      bonusAbsorptionPoints: state.bonusAbsorptionPoints,
      skillCooldownUntil: state.skillCooldownUntil ? [...state.skillCooldownUntil] : null,
      skillCastCount: state.skillCastCount,
      buffs: state.buffs.length === 0 ? null : state.buffs.map((b) => ({ ...b })),
      respawnAtSeconds: Number.isFinite(state.respawnAtSeconds) ? state.respawnAtSeconds : null,
      respawnPolicy: state.respawnPolicy,
      respawnTargetIndex: state.respawnTargetIndex,
      respawnDeadlineSeconds: Number.isFinite(state.respawnDeadlineSeconds)
        ? state.respawnDeadlineSeconds
        : null,
      respawnWaitingUntilSeconds: Number.isFinite(state.respawnWaitingUntilSeconds)
        ? state.respawnWaitingUntilSeconds
        : null,
      traits: [...state.traits],
      traitLevels: state.traitLevels ? { ...state.traitLevels } : null,
    }
  }

  static applySnapshot(
    state: CombatantState,
    snap: CombatantSnapshot,
    maxHitpoints: number,
    maxManapoints: number,
  ): void {
    state.hitpoints = Math.max(0, Math.min(snap.hitpoints, maxHitpoints))
    state.manapoints = Math.max(0, Math.min(snap.manapoints, maxManapoints))
    state.nextAttackAtSeconds = snap.nextAttackAtSeconds
    state.attackCount = snap.attackCount
    state.threat = snap.threat
    state.threatBonusPoints = snap.threatBonusPoints
    state.bonusAbsorptionPoints = snap.bonusAbsorptionPoints
    state.pendingSkillIndex =
      snap.pendingSkillIndex >= 0 && snap.pendingSkillIndex < state.skills.length
        ? snap.pendingSkillIndex
        : -1
    if (state.skills.length > 0) {
      const restored = new Array<number>(state.skills.length).fill(0)
      if (snap.skillCooldownUntil) {
        const copyCount = Math.min(snap.skillCooldownUntil.length, state.skills.length)
        for (let i = 0; i < copyCount; i++) restored[i] = snap.skillCooldownUntil[i]
      }
      state.skillCooldownUntil = restored
    } else {
      state.skillCooldownUntil = null
    }
    state.skillCastCount = snap.skillCastCount
    state.respawnAtSeconds = snap.respawnAtSeconds ?? Number.POSITIVE_INFINITY
    state.respawnPolicy = snap.respawnPolicy
    state.respawnTargetIndex = snap.respawnTargetIndex
    state.respawnDeadlineSeconds = snap.respawnDeadlineSeconds ?? Number.POSITIVE_INFINITY
    state.respawnWaitingUntilSeconds = snap.respawnWaitingUntilSeconds ?? Number.POSITIVE_INFINITY
    state.replaceBuffs(snap.buffs)
    state.restoreTraits(snap.traits, snap.traitLevels)
  }

  static restore(
    encounterId: string,
    seed: bigint,
    player: CombatantTemplate,
    playerSnapshot: CombatantSnapshot,
    enemies: readonly CombatantTemplate[],
    enemySnapshots: readonly CombatantSnapshot[],
    elapsedSeconds: number,
    outcome: CombatOutcome,
    sequence = 0,
    playerRespawnSeconds = 0,
    playerRespawnAtSeconds = Number.POSITIVE_INFINITY,
  ): CombatEncounterState {
    const snap: CombatantSnapshot = {
      ...playerSnapshot,
      respawnAtSeconds: Number.isFinite(playerRespawnAtSeconds) ? playerRespawnAtSeconds : null,
    }
    return CombatEncounterState.restoreAllied(
      encounterId,
      seed,
      [player],
      [snap],
      0,
      enemies,
      enemySnapshots,
      elapsedSeconds,
      outcome,
      sequence,
      playerRespawnSeconds,
    )
  }

  static restoreAllied(
    encounterId: string,
    seed: bigint,
    allies: readonly CombatantTemplate[],
    allySnapshots: readonly CombatantSnapshot[],
    selfIndex: number,
    enemies: readonly CombatantTemplate[],
    enemySnapshots: readonly CombatantSnapshot[],
    elapsedSeconds: number,
    outcome: CombatOutcome,
    sequence = 0,
    playerRespawnSeconds = 0,
  ): CombatEncounterState {
    const encounter = CombatEncounterState.createAllied(
      encounterId,
      seed,
      allies,
      selfIndex,
      enemies,
      playerRespawnSeconds,
    )
    for (let i = 0; i < allies.length; i++) {
      CombatEncounterState.applySnapshot(
        encounter.playerAllies[i],
        allySnapshots[i],
        allies[i].stats.maxHitpoints,
        allies[i].stats.maxManapoints,
      )
    }
    for (let i = 0; i < enemies.length; i++) {
      CombatEncounterState.applySnapshot(
        encounter.enemies[i],
        enemySnapshots[i],
        enemies[i].stats.maxHitpoints,
        enemies[i].stats.maxManapoints,
      )
    }
    encounter.elapsedSeconds = elapsedSeconds
    encounter.outcome = outcome
    encounter.sequence = sequence
    encounter.initializeTraitListeners()
    return encounter
  }
}

export function scaleMonsterStats(baseStats: CombatantStats, difficulty: number): CombatantStats {
  const clamped = Math.min(Math.max(Math.trunc(difficulty), 0), 5)
  const mult = Math.pow(1.25, clamped)
  if (mult === 1.0) {
    // 难度 0 也按 Math.Ceiling(x * 1) 执行与 C# 逐位对齐（防止浮点数尾差）
    return {
      ...baseStats,
      maxHitpoints: Math.ceil(baseStats.maxHitpoints),
      attackPower: Math.ceil(baseStats.attackPower),
      defense: Math.ceil(baseStats.defense),
      spellAttack: Math.ceil(baseStats.spellAttack),
      maxManapoints: Math.ceil(baseStats.maxManapoints),
      magicResistance: Math.ceil(baseStats.magicResistance),
      accuracy: Math.ceil(baseStats.accuracy),
      evasion: Math.ceil(baseStats.evasion),
      critical: Math.ceil(baseStats.critical),
      criticalResistance: Math.ceil(baseStats.criticalResistance),
      criticalDamagePoints: Math.ceil(baseStats.criticalDamagePoints),
      breakPower: Math.ceil(baseStats.breakPower),
      parryResolve: Math.ceil(baseStats.parryResolve),
      physicalDamageBonusPoints: Math.ceil(baseStats.physicalDamageBonusPoints),
      spellDamageBonusPoints: Math.ceil(baseStats.spellDamageBonusPoints),
      totalDamageBonusPoints: Math.ceil(baseStats.totalDamageBonusPoints),
      basicAttackBonusPoints: Math.ceil(baseStats.basicAttackBonusPoints),
      skillBonusPoints: Math.ceil(baseStats.skillBonusPoints),
      physicalDamageReductionPoints: Math.ceil(baseStats.physicalDamageReductionPoints),
      spellDamageReductionPoints: Math.ceil(baseStats.spellDamageReductionPoints),
      damageAbsorptionPoints: Math.ceil(baseStats.damageAbsorptionPoints),
      fixedDamageBonus: Math.ceil(baseStats.fixedDamageBonus),
      fixedDamageReduction: Math.ceil(baseStats.fixedDamageReduction),
      fixedPhysicalDamageBonus: Math.ceil(baseStats.fixedPhysicalDamageBonus),
      fixedPhysicalDamageReduction: Math.ceil(baseStats.fixedPhysicalDamageReduction),
      fixedSpellDamageBonus: Math.ceil(baseStats.fixedSpellDamageBonus),
      fixedSpellDamageReduction: Math.ceil(baseStats.fixedSpellDamageReduction),
      attackSpeedPoints: Math.ceil(baseStats.attackSpeedPoints),
      actionSpeedPoints: Math.ceil(baseStats.actionSpeedPoints),
      cooldownReductionPoints: Math.ceil(baseStats.cooldownReductionPoints),
      threatBonusPoints: Math.ceil(baseStats.threatBonusPoints),
      hpRegen: Math.ceil(baseStats.hpRegen),
      mpRegen: Math.ceil(baseStats.mpRegen),
      combatExperience: Math.ceil(baseStats.combatExperience),
    }
  }
  return {
    ...baseStats,
    maxHitpoints: Math.ceil(baseStats.maxHitpoints * mult),
    attackPower: Math.ceil(baseStats.attackPower * mult),
    defense: Math.ceil(baseStats.defense * mult),
    spellAttack: Math.ceil(baseStats.spellAttack * mult),
    maxManapoints: Math.ceil(baseStats.maxManapoints * mult),
    magicResistance: Math.ceil(baseStats.magicResistance * mult),
    accuracy: Math.ceil(baseStats.accuracy * mult),
    evasion: Math.ceil(baseStats.evasion * mult),
    critical: Math.ceil(baseStats.critical * mult),
    criticalResistance: Math.ceil(baseStats.criticalResistance * mult),
    criticalDamagePoints: Math.ceil(baseStats.criticalDamagePoints * mult),
    breakPower: Math.ceil(baseStats.breakPower * mult),
    parryResolve: Math.ceil(baseStats.parryResolve * mult),
    physicalDamageBonusPoints: Math.ceil(baseStats.physicalDamageBonusPoints * mult),
    spellDamageBonusPoints: Math.ceil(baseStats.spellDamageBonusPoints * mult),
    totalDamageBonusPoints: Math.ceil(baseStats.totalDamageBonusPoints * mult),
    basicAttackBonusPoints: Math.ceil(baseStats.basicAttackBonusPoints * mult),
    skillBonusPoints: Math.ceil(baseStats.skillBonusPoints * mult),
    physicalDamageReductionPoints: Math.ceil(baseStats.physicalDamageReductionPoints * mult),
    spellDamageReductionPoints: Math.ceil(baseStats.spellDamageReductionPoints * mult),
    damageAbsorptionPoints: Math.ceil(baseStats.damageAbsorptionPoints * mult),
    fixedDamageBonus: Math.ceil(baseStats.fixedDamageBonus * mult),
    fixedDamageReduction: Math.ceil(baseStats.fixedDamageReduction * mult),
    fixedPhysicalDamageBonus: Math.ceil(baseStats.fixedPhysicalDamageBonus * mult),
    fixedPhysicalDamageReduction: Math.ceil(baseStats.fixedPhysicalDamageReduction * mult),
    fixedSpellDamageBonus: Math.ceil(baseStats.fixedSpellDamageBonus * mult),
    fixedSpellDamageReduction: Math.ceil(baseStats.fixedSpellDamageReduction * mult),
    attackSpeedPoints: Math.ceil(baseStats.attackSpeedPoints * mult),
    actionSpeedPoints: Math.ceil(baseStats.actionSpeedPoints * mult),
    cooldownReductionPoints: Math.ceil(baseStats.cooldownReductionPoints * mult),
    threatBonusPoints: Math.ceil(baseStats.threatBonusPoints * mult),
    hpRegen: Math.ceil(baseStats.hpRegen * mult),
    mpRegen: Math.ceil(baseStats.mpRegen * mult),
    combatExperience: Math.ceil(baseStats.combatExperience * mult),
  }
}
