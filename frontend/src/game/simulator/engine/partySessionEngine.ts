// 组队协同推演会话容器与个人结算引擎（严格对应 DaoHua.Ecs.ActionParticipationSessionEngine 与 PlayerAggregate.Participation）

import type { GameDataCatalog } from './catalog.ts'
import {
  advanceCombatPreparationCore,
  completeCombatAdvanceCore,
  initializeCombatAction,
  restoreCombatBattle,
  sumFrozenBuffStacks,
} from './combatActionRules.ts'
import {
  CombatBoundaryHookDispatcher,
  advanceCombatTo,
  applyRanhun,
  applyCombatDamage,
  resolveStrikeDamage,
} from './combatEngine.ts'
import {
  CombatOutcome,
  CombatantState,
  CombatEncounterState,
  type AllyMark,
  type CombatAdvanceResult,
  type CombatUnitStatDocument,
  type CombatantTemplate,
} from './combatModels.ts'
import {
  addSecondsExactTicks,
  compareOrdinal,
  roundAwayFromZero,
  ticksDiffSeconds,
  ticksFromUnixSeconds,
  ticksToUnixSeconds,
} from './math.ts'
import { captureCombatState, completeResourceRegeneration } from './resourceBoundaries.ts'
import {
  AutoUseBar,
  BuffEffectChannel,
  CombatDamageKind,
  CombatElement,
  SlotGroupGeneral,
  SlotGroupPotion,
  abilityLevelForExp,
  parseBuffEffects,
  percentMultiplier,
  PercentCurveMode,
  type CombatRespawnPolicy,
} from './rules.ts'
import {
  ActionQueueStatus,
  CombatExpeditionPhase,
  ConsumableScheduleMode,
  SimEventTail,
  SimPlayerRuntime,
  addCoreAspectExp,
  combatBuffsFromCombatant,
  combatSkillsFromCombatant,
  createEmptyCombatSkillState,
  formatTrimmedFixed,
  resolveExpCapWithLinggen,
  splitAssetKey,
  type ActionConsumableSlot,
  type ActionParticipantSnapshot,
  type ActionSustainedBuff,
  type CombatSkillStateModel,
  type PlayerStatsDelta,
  type SimActionParticipationSpec,
  type SimActionState,
  type SimCombatExpeditionState,
  type SimEvent,
} from './simulationState.ts'
import { buildSkillSlotsFromAbilitySlots, evaluateTriggers, resolveConsumableTriggers } from './triggerEvaluator.ts'
import {
  BattleFact,
  EMPTY_WAKE_MASK,
  UnitChannel,
  resolveTriggerListWakeMask,
  unionWakeMask,
  wakeMaskWakesOn,
  type CombatFactDelta,
  type SlotWakeMask,
} from './triggerWake.ts'
import {
  EMPTY_BUILDING_EFFECTS,
  type CombatBuffCarryState,
  type PlayerState,
  type SimulatorAssetDto,
} from './types.ts'

const MAX_CONSUMABLE_CASCADE_PASSES = 256
const MAX_TRANSITIONS_PER_ADVANCE = 100_000
const OFFLINE_IDLE_LIMIT_SECONDS = 48 * 3600

export interface ActionParticipationTimedStats {
  atUnixSeconds: number
  delta: PlayerStatsDelta
}

export interface ActionParticipationMemberSettlement {
  sessionId: string
  sequence: number
  playerId: string
  fromTicks: bigint
  toTicks: bigint
  hitpoints: number
  manapoints: number
  regenCarrySeconds: number
  skills: CombatSkillStateModel
  buffs: CombatBuffCarryState[]
  expedition: SimCombatExpeditionState
  progressSeconds: number
  completedRepeats: number
  stats: ActionParticipationTimedStats[]
  assetDeltas: SimulatorAssetDto[]
  events: SimEvent[]
  combatStats: CombatUnitStatDocument[]
  droppedEventCount: number
  consumableSlots: ActionConsumableSlot[] | null
}

interface SessionConsumableSlot {
  memberIndex: number
  itemId: string
  group: string
  slotIndex: number
  cooldownUntilUnixSeconds: number
  sustainedExpiresAtUnixSeconds: number
}

// ---------- 等级惩罚与权重（严格对应 Domain.Player.ActionParticipationSpec） ----------

export function calculateLevelMalus(memberLevel: number, highestLevel: number): number {
  if (highestLevel <= 0 || memberLevel <= 0) return 0
  const gap = highestLevel - memberLevel
  const exemptGap = Math.max(memberLevel * 0.2, 12.0)
  if (gap <= exemptGap) return 0
  return Math.min(0.9, 0.03 * (gap - exemptGap))
}

export function captureSustainedBuffs(
  slots: readonly PlayerState['autoUseSlots'][number][],
  catalog: GameDataCatalog,
): ActionSustainedBuff[] | null {
  let list: ActionSustainedBuff[] | null = null
  for (let i = 0; i < slots.length; i++) {
    const slot = slots[i]
    if (
      !slot.enabled ||
      slot.itemId === null ||
      (slot.group !== SlotGroupGeneral && slot.group !== SlotGroupPotion) ||
      slot.actionType !== AutoUseBar.Combat ||
      slot.remainingDurationSeconds <= 0
    ) {
      continue
    }
    const item = catalog.items.get(slot.itemId)
    if (
      !item ||
      item.category !== 'consumable' ||
      !item.consumable ||
      !item.consumable.isContinuous ||
      item.consumable.durationSeconds <= 0 ||
      item.consumable.durable ||
      !item.consumable.usableInBars.has(AutoUseBar.Combat)
    ) {
      continue
    }
    const rawItem = catalog.rawItems.get(item.id)
    const rawCons = rawItem?.consumable as Record<string, unknown> | undefined
    const rawBuffs = rawCons && Array.isArray(rawCons.buffs)
      ? (rawCons.buffs as Record<string, unknown>[])
      : null
    if (!rawBuffs) continue

    for (const buff of rawBuffs) {
      const buffId = String(buff.buffId)
      if (!list) list = []
      list = list.filter((entry) => entry.buffId !== buffId)
      const name = typeof buff.name === 'string' ? buff.name : buffId
      const teamWide =
        item.subCategory === 'formation' && buff.teamWide !== false
      list.push({
        buffId,
        remainingSeconds: slot.remainingDurationSeconds,
        stacks: 1,
        name,
        teamWide,
        effects: parseBuffEffects(buff, 1.0),
        statRatios: null,
      })
    }
  }
  return list
}

export function captureConsumableSlots(
  slots: readonly PlayerState['autoUseSlots'][number][],
  catalog: GameDataCatalog,
): ActionConsumableSlot[] | null {
  let list: ActionConsumableSlot[] | null = null
  for (let i = 0; i < slots.length; i++) {
    const slot = slots[i]
    if (
      !slot.enabled ||
      slot.itemId === null ||
      (slot.group !== SlotGroupGeneral && slot.group !== SlotGroupPotion) ||
      slot.actionType !== AutoUseBar.Combat
    ) {
      continue
    }
    const item = catalog.items.get(slot.itemId)
    if (
      !item ||
      item.category !== 'consumable' ||
      !item.consumable ||
      item.consumable.durable ||
      !item.consumable.usableInBars.has(AutoUseBar.Combat)
    ) {
      continue
    }
    const c = item.consumable
    const isSustained = c.isContinuous && c.durationSeconds > 0 && !c.durable
    if (!list) list = []
    // 注意：capture 时借用 sustainedExpiresAtUnixSeconds 暂存 RemainingSeconds，引擎构造时加 StartedAtUnixSeconds
    list.push({
      group: slot.group,
      slotIndex: slot.slotIndex,
      itemId: slot.itemId,
      cooldownUntilUnixSeconds: slot.cooldownUntilUnixSeconds,
      sustainedExpiresAtUnixSeconds: isSustained ? slot.remainingDurationSeconds : 0,
    })
  }
  return list
}

export function captureInternalArt(
  abilitySlots: readonly PlayerState['abilitySlots'][number][],
  abilityExperiences: Record<string, number>,
  catalog: GameDataCatalog,
): { abilityId: string; level: number } | null {
  if (abilitySlots.length === 0) return null
  const firstId = abilitySlots[0].abilityId
  if (!firstId) return null
  const ability = catalog.abilities.get(firstId)
  if (!ability || ability.discipline !== 'internal') return null
  let level = Math.max(1, abilitySlots[0].level)
  const exp = abilityExperiences[firstId] ?? 0
  if (exp > 0) level = Math.max(level, abilityLevelForExp(exp))
  return { abilityId: firstId, level }
}

/** 对应 `ActionParticipantSnapshot.FromState`。 */
export function buildActionParticipantSnapshot(
  state: PlayerState,
  catalog: GameDataCatalog,
): ActionParticipantSnapshot {
  const runtime = new SimPlayerRuntime(state)
  const attributes = runtime.resolveAttributes(catalog)
  const combatantStats = runtime.resolveAttributesForCombat(catalog)
  const externalBuffs: Record<string, number> = {}
  let hasExternal = false
  for (const b of state.buffs) {
    externalBuffs[b.buffId] = (externalBuffs[b.buffId] ?? 0) + b.stacks
    hasExternal = true
  }
  const skills = buildSkillSlotsFromAbilitySlots(
    state.abilitySlots,
    catalog.abilities,
    state.abilityTriggers,
  )
  const internalArt = captureInternalArt(state.abilitySlots, state.abilityExperiences, catalog)
  const consumableTriggers: Record<string, typeof state.consumableTriggers[string]> = {}
  let hasConsumableTriggers = false
  for (const [k, v] of Object.entries(state.consumableTriggers)) {
    consumableTriggers[k] = structuredClone(v)
    hasConsumableTriggers = true
  }

  return {
    playerId: state.playerId,
    displayName: state.displayName,
    realmLevel: state.realmLevel,
    stats: combatantStats,
    skills,
    initialHitpoints: state.hitpoints,
    initialManapoints: state.manapoints,
    searchSpeedPoints: attributes.searchSpeedPoints,
    dropBonusPoints: attributes.dropBonusPoints,
    levelMalus: 0, // 由 createActionParticipationSpec 统一计算填充
    externalBuffs: hasExternal ? externalBuffs : null,
    sustainedBuffs: captureSustainedBuffs(state.autoUseSlots, catalog),
    combatSkills: {
      cooldowns: state.combatSkills.cooldowns.map((c) => ({ ...c })),
      castingAbilityId: state.combatSkills.castingAbilityId,
      castRemainingSeconds: state.combatSkills.castRemainingSeconds,
      castCount: state.combatSkills.castCount,
    },
    combatBuffs: state.combatBuffCarry.map((b) => ({ ...b })),
    respawnPolicy: state.respawnPolicy as CombatRespawnPolicy,
    respawnTargetPlayerId: state.respawnTargetPlayerId,
    regenCarrySeconds: state.regenCarrySeconds,
    consumableSlots: captureConsumableSlots(state.autoUseSlots, catalog),
    consumableTriggers: hasConsumableTriggers ? consumableTriggers : null,
    internalArtId: internalArt?.abilityId ?? null,
    internalArtLevel: internalArt?.level ?? 0,
  }
}

export function createActionParticipationSpec(
  sessionId: string,
  encounterId: string,
  nodeId: string,
  seed: bigint,
  startedAtUnixSeconds: number,
  snapshots: readonly ActionParticipantSnapshot[],
  actionId: string,
  monsterWeakenRatio: number,
  difficulty: number,
): SimActionParticipationSpec {
  let highest = 0
  let sumSearchSpeed = 0
  let totalDropBonusPoints = 0
  for (let i = 0; i < snapshots.length; i++) {
    if (snapshots[i].realmLevel > highest) highest = snapshots[i].realmLevel
    sumSearchSpeed += snapshots[i].searchSpeedPoints
    totalDropBonusPoints += snapshots[i].dropBonusPoints
  }
  const searchSpeedPoints = snapshots.length > 0 ? sumSearchSpeed / snapshots.length : 0

  const levelMaluses: number[] = []
  const assignmentWeights: number[] = []
  const members: ActionParticipantSnapshot[] = []
  for (let i = 0; i < snapshots.length; i++) {
    const m = snapshots[i]
    const malus = calculateLevelMalus(m.realmLevel, highest)
    levelMaluses.push(malus)
    const w = Math.max(0, 1 + m.dropBonusPoints / 100) * (1 - malus)
    assignmentWeights.push(w <= 0 ? 0 : w)
    members.push({ ...m, levelMalus: malus })
  }

  return {
    sessionId,
    encounterId,
    nodeId,
    seed,
    searchSpeedPoints,
    startedAtUnixSeconds,
    members,
    actionId,
    pvpEnabled: false,
    monsterWeakenRatio,
    difficulty,
    totalDropBonusPoints,
    levelMaluses,
    assignmentWeights,
  }
}

class SettlementCollector {
  readonly stats: ActionParticipationTimedStats[] = []
  readonly assets = new Map<string, number>()
  events = new SimEventTail()

  record = (delta: PlayerStatsDelta, atUnixSeconds: number): void => {
    // 容器只投递原始份额：个人修为瓶颈、底蕴兑换和升级记录须在成员单写者内结算。
    this.stats.push({
      atUnixSeconds,
      delta: { ...delta, experience: 0, foundation: 0 },
    })
  }

  addMutation(events: SimEventTail, deltas: Map<string, number>, includeAssets = true): void {
    this.events.fold(events.dropped)
    for (let i = 0; i < events.view.length; i++) {
      this.events.add(events.view[i])
    }
    if (!includeAssets) return
    for (const [k, qty] of deltas) {
      if (qty === 0) continue
      this.assets.set(k, (this.assets.get(k) ?? 0) + qty)
    }
  }

  reset(): void {
    this.stats.length = 0
    this.assets.clear()
    this.events = new SimEventTail()
  }
}

/**
 * 队伍会话唯一计算容器（严格对应 `DaoHua.Ecs.ActionParticipationSessionEngine`）。
 */
export class ActionParticipationSessionEngine {
  private readonly _spec: SimActionParticipationSpec
  private readonly _catalog: GameDataCatalog
  private readonly _members: SimPlayerRuntime[]
  private readonly _collectors: SettlementCollector[]
  private _battle: CombatEncounterState | null = null
  private _consumables: SessionConsumableSlot[] | null = null
  private _wakeMasks: SlotWakeMask[] | null = null
  private _walletDirty = false
  // key = `${memberIndex}\0${itemId}\0${variant}`
  private readonly _workingWallet = new Map<string, number>()
  private readonly _assetDeltas = new Map<string, number>()
  private readonly _scheduleMode: ConsumableScheduleMode
  private _nextConsumableEvaluation: number
  private _advancedAtTicks: bigint
  private _sequence = 0
  private _endsAt: number | null = null

  private get canonical(): SimPlayerRuntime {
    return this._members[0]
  }

  private get action(): SimActionState {
    return this.canonical.actions[0]
  }

  private constructor(
    spec: SimActionParticipationSpec,
    catalog: GameDataCatalog,
    scheduleMode: ConsumableScheduleMode,
  ) {
    this._spec = spec
    this._catalog = catalog
    this._scheduleMode = scheduleMode
    this._advancedAtTicks = ticksFromUnixSeconds(spec.startedAtUnixSeconds)
    this._nextConsumableEvaluation = spec.startedAtUnixSeconds + 5
    this._members = new Array(spec.members.length)
    this._collectors = new Array(spec.members.length)

    const actionDef = catalog.actions.get(spec.actionId)
    if (!actionDef) {
      throw new Error(`未知行动: ${spec.actionId}`)
    }

    for (let i = 0; i < spec.members.length; i++) {
      const frozen = spec.members[i]
      const baseState = createEmptyMemberState(
        frozen,
        spec,
        this._advancedAtTicks,
        catalog.fingerprint,
      )
      const member = new SimPlayerRuntime(baseState)
      member.isParticipationSessionContainer = true
      if (frozen.consumableTriggers) {
        for (const [k, v] of Object.entries(frozen.consumableTriggers)) {
          member.state.consumableTriggers[k] = structuredClone(v)
        }
      }
      member.state.abilitySlots.length = 0
      member.learnedAbilities.clear()
      if (frozen.internalArtId) {
        member.state.abilitySlots.push({
          abilityId: frozen.internalArtId,
          level: frozen.internalArtLevel,
        })
        member.learnedAbilities.add(frozen.internalArtId)
      }
      for (const skill of frozen.skills) {
        const abilityId = skill.ability.id
        if (!abilityId) continue
        member.state.abilitySlots.push({ abilityId, level: skill.level })
        member.learnedAbilities.add(abilityId)
      }
      const actionInst: SimActionState = {
        id: `${spec.sessionId}:${i}`,
        actionId: spec.actionId,
        difficulty: spec.difficulty,
        pvpEnabled: false,
        remainingRepeats: -1,
        completedRepeats: 0,
        progressSeconds: 0,
        status: ActionQueueStatus.Running,
        expedition: null,
        participation: {
          sessionId: spec.sessionId,
          spec,
          appliedSequence: 0,
          endsAtUnixSeconds: Number.POSITIVE_INFINITY,
        },
      }
      member.actions.push(actionInst)
      member.ensureActionReport(actionInst.id, spec.actionId, actionDef.name)
      const collector = new SettlementCollector()
      member.statsSink = collector.record
      this._members[i] = member
      this._collectors[i] = collector
    }

    const initEvents = new SimEventTail()
    const initDeltas = new Map<string, number>()
    initializeCombatAction(this.canonical, this.action, catalog, this._advancedAtTicks, initEvents)
    for (let i = 0; i < this._collectors.length; i++) {
      this._collectors[i].addMutation(initEvents, initDeltas, false)
    }

    const ledger: SessionConsumableSlot[] = []
    for (let i = 0; i < spec.members.length; i++) {
      const slots = spec.members[i].consumableSlots
      if (!slots || slots.length === 0) continue
      for (const slot of slots) {
        ledger.push({
          memberIndex: i,
          itemId: slot.itemId,
          group: slot.group,
          slotIndex: slot.slotIndex,
          cooldownUntilUnixSeconds: slot.cooldownUntilUnixSeconds,
          sustainedExpiresAtUnixSeconds:
            slot.sustainedExpiresAtUnixSeconds > 0
              ? spec.startedAtUnixSeconds + slot.sustainedExpiresAtUnixSeconds
              : 0,
        })
      }
    }
    this._consumables = ledger.length > 0 ? ledger : null
    this.buildWakeMasks()
    this.synchronizeViews()
  }

  static create(
    spec: SimActionParticipationSpec,
    catalog: GameDataCatalog,
    workingWallet: readonly { memberIndex: number; itemId: string; variant: string; quantity: number }[] = [],
    scheduleMode: ConsumableScheduleMode = ConsumableScheduleMode.EventDriven,
  ): ActionParticipationSessionEngine {
    const engine = new ActionParticipationSessionEngine(spec, catalog, scheduleMode)
    engine.setWorkingWallet(workingWallet)
    engine.evaluateConsumables(engine._advancedAtTicks)
    engine.synchronizeViews()
    return engine
  }

  private buildWakeMasks(): void {
    this._wakeMasks = null
    if (!this._consumables) return
    const masks: SlotWakeMask[] = new Array(this._consumables.length)
    for (let s = 0; s < this._consumables.length; s++) {
      const row = this._consumables[s]
      let mask = EMPTY_WAKE_MASK
      const item = this._catalog.items.get(row.itemId)
      if (item && item.consumable) {
        const triggers = resolveConsumableTriggers(
          row.itemId,
          item.consumable,
          this._members[row.memberIndex].state.consumableTriggers,
        )
        mask = resolveTriggerListWakeMask(triggers, false)
        if (item.consumable.strike !== null) {
          mask = unionWakeMask(mask, { ...EMPTY_WAKE_MASK, self: UnitChannel.Mana })
        }
      }
      masks[s] = unionWakeMask(mask, { ...EMPTY_WAKE_MASK, facts: BattleFact.External })
    }
    this._wakeMasks = masks
  }

  setWorkingWallet(
    balances: readonly { memberIndex: number; itemId: string; variant: string; quantity: number }[],
  ): void {
    this._workingWallet.clear()
    for (const b of balances) {
      if (b.memberIndex < 0 || b.memberIndex >= this._members.length) continue
      if (!this.isRelevantConsumable(b.memberIndex, b.itemId)) continue
      const key = `${b.memberIndex}\0${b.itemId}\0${b.variant}`
      this._workingWallet.set(key, b.quantity)
    }
    this._walletDirty = true
  }

  private isRelevantConsumable(memberIndex: number, itemId: string): boolean {
    if (!this._consumables) return false
    for (const row of this._consumables) {
      if (row.memberIndex === memberIndex && row.itemId === itemId) return true
    }
    return false
  }

  takeAssetDeltas(): { memberIndex: number; itemId: string; variant: string; quantityDelta: number }[] {
    const out: { memberIndex: number; itemId: string; variant: string; quantityDelta: number }[] = []
    for (const [k, qty] of this._assetDeltas) {
      if (Math.abs(qty) <= 1e-12) continue
      const firstSep = k.indexOf('\0')
      const secondSep = k.indexOf('\0', firstSep + 1)
      const memberIndex = Number(k.slice(0, firstSep))
      const itemId = k.slice(firstSep + 1, secondSep)
      const variant = k.slice(secondSep + 1)
      out.push({ memberIndex, itemId, variant, quantityDelta: qty })
    }
    out.sort((a, b) => {
      if (a.memberIndex !== b.memberIndex) return a.memberIndex - b.memberIndex
      const itemCmp = compareOrdinal(a.itemId, b.itemId)
      if (itemCmp !== 0) return itemCmp
      return compareOrdinal(a.variant, b.variant)
    })
    this._assetDeltas.clear()
    return out
  }

  advanceTo(targetTicks: bigint): ActionParticipationMemberSettlement[] {
    const fromTicks = this._advancedAtTicks
    if (targetTicks < fromTicks || this._endsAt !== null) {
      throw new Error('会话目标时间倒退或已经结束。')
    }
    const maxCapTicks = fromTicks + BigInt(OFFLINE_IDLE_LIMIT_SECONDS) * 10_000_000n
    const cappedTargetTicks = targetTicks > maxCapTicks ? maxCapTicks : targetTicks
    let cursorTicks = fromTicks

    while (cursorTicks < cappedTargetTicks) {
      const cursorUnix = ticksToUnixSeconds(cursorTicks)
      let untilRegen = 5.0
      for (let i = 0; i < this._members.length; i++) {
        untilRegen = Math.min(untilRegen, 5 - this._members[i].state.regenCarrySeconds)
      }
      untilRegen = Math.min(untilRegen, this.consumableWakeUnix(cursorUnix) - cursorUnix)

      const remainingTargetTicks = cappedTargetTicks - cursorTicks
      const stepTicksCandidate = BigInt(roundAwayFromZero(untilRegen * 10_000_000))
      const clampedStep = stepTicksCandidate < 1n ? 1n : stepTicksCandidate
      const ticks = clampedStep < remainingTargetTicks ? clampedStep : remainingTargetTicks
      const windowEndTicks = cursorTicks + ticks
      let remaining = ticksDiffSeconds(cursorTicks, windowEndTicks)
      let transitions = 0

      while (remaining > 1e-9) {
        if (++transitions > MAX_TRANSITIONS_PER_ADVANCE) {
          throw new Error('队伍会话超过单窗口转换上限。')
        }
        if (this.action.expedition!.phase === CombatExpeditionPhase.Combat) {
          remaining = this.advanceBattle(cursorTicks, windowEndTicks, remaining)
        } else {
          remaining = this.advancePreparation(cursorTicks, windowEndTicks, remaining)
        }
      }

      cursorTicks = windowEndTicks
      this._advancedAtTicks = cursorTicks
      const advancedUnix = ticksToUnixSeconds(cursorTicks)
      if (this._scheduleMode === ConsumableScheduleMode.PeriodicTick) {
        if (advancedUnix >= this._nextConsumableEvaluation - 1e-9) {
          this._nextConsumableEvaluation += 5
          this.evaluateConsumables(cursorTicks)
        }
      } else if (this.pendingConsumableWake(advancedUnix)) {
        this.evaluateConsumableCascade(cursorTicks)
      }
      this.flushBattle()
      this.synchronizeViews()
    }

    this._advancedAtTicks = targetTicks
    const targetUnix = ticksToUnixSeconds(targetTicks)
    if (this._scheduleMode === ConsumableScheduleMode.PeriodicTick) {
      while (this._nextConsumableEvaluation <= targetUnix) this._nextConsumableEvaluation += 5
    } else {
      this._nextConsumableEvaluation = Math.max(this._nextConsumableEvaluation, targetUnix + 5)
    }

    if (targetTicks > cappedTargetTicks) {
      for (const collector of this._collectors) {
        collector.events.add({
          atTicks: targetTicks,
          code: 'offline.capped',
          message: '离线补算达到 48 小时上限，超出部分未累积收益。',
          actionId: null,
          itemDeltas: null,
        })
      }
    }

    this.flushBattle()
    this.synchronizeViews()

    this._sequence += 1
    const canonicalStats = this.canonical.report!.combatStats.entries.map((row) => ({ ...row }))
    const results: ActionParticipationMemberSettlement[] = new Array(this._members.length)
    for (let i = 0; i < this._members.length; i++) {
      const member = this._members[i]
      const collector = this._collectors[i]
      const action = member.actions[0]
      const expedition = action.expedition!
      const memberConsumables = this._consumables
        ? this._consumables
            .filter((row) => row.memberIndex === i)
            .map((row) => ({
              group: row.group,
              slotIndex: row.slotIndex,
              itemId: row.itemId,
              cooldownUntilUnixSeconds: row.cooldownUntilUnixSeconds,
              sustainedExpiresAtUnixSeconds: row.sustainedExpiresAtUnixSeconds,
            }))
        : null

      const assetDeltas: SimulatorAssetDto[] = []
      for (const [k, qty] of collector.assets) {
        const { itemId, variant } = splitAssetKey(k)
        assetDeltas.push({ itemId, variant, quantity: qty })
      }

      results[i] = {
        sessionId: this._spec.sessionId,
        sequence: this._sequence,
        playerId: member.playerId,
        fromTicks,
        toTicks: targetTicks,
        hitpoints: member.state.hitpoints,
        manapoints: member.state.manapoints,
        regenCarrySeconds: member.state.regenCarrySeconds,
        skills: {
          cooldowns: member.state.combatSkills.cooldowns.map((c) => ({ ...c })),
          castingAbilityId: member.state.combatSkills.castingAbilityId,
          castRemainingSeconds: member.state.combatSkills.castRemainingSeconds,
          castCount: member.state.combatSkills.castCount,
        },
        buffs: member.state.combatBuffCarry.map((b) => ({ ...b })),
        expedition,
        progressSeconds: action.progressSeconds,
        completedRepeats: action.completedRepeats,
        stats: [...collector.stats],
        assetDeltas,
        events: [...collector.events.view],
        combatStats: projectStats(canonicalStats, i),
        droppedEventCount: collector.events.dropped,
        consumableSlots: memberConsumables,
      }
      collector.reset()
    }
    return results
  }

  private advancePreparation(fromTicks: bigint, targetTicks: bigint, remaining: number): number {
    const events = new SimEventTail()
    const deltas = new Map<string, number>()
    const expedition = this.action.expedition!
    const preparationSeconds = Math.min(
      remaining,
      Math.max(0, expedition.phaseDurationSeconds - expedition.phaseElapsedSeconds),
    )
    this.advancePreparationResources(preparationSeconds)

    const stateRef = {
      remainingSeconds: remaining,
      idleSkillChanged: false,
    }
    const battle = advanceCombatPreparationCore(
      this.canonical,
      this.action,
      this._catalog,
      null,
      fromTicks,
      targetTicks,
      stateRef,
      deltas,
      events,
    )

    const entryElapsed = ticksDiffSeconds(fromTicks, targetTicks) - stateRef.remainingSeconds
    const entryAtTicks = addSecondsExactTicks(fromTicks, entryElapsed)
    const entryAtUnix = ticksToUnixSeconds(entryAtTicks)

    for (let i = 0; i < this._collectors.length; i++) {
      if (i === 0) {
        this._collectors[i].addMutation(events, deltas, true)
      } else {
        this._collectors[i].events.fold(events.dropped)
        for (let e = 0; e < events.view.length; e++) {
          const entry = events.view[e]
          if (entry.code !== 'combat.lost' && entry.code !== 'combat.downed') {
            this._collectors[i].events.add(entry)
          }
        }
        if (battle && !battle.playerAllies[i].isAlive) {
          const names = battle.enemies
            .map((enemy) => this._catalog.monsters.get(enemy.definitionId!)!.name)
            .join('、')
          this._collectors[i].events.add({
            atTicks: entryAtTicks,
            code: 'combat.downed',
            message: `你以倒地状态随队迎战：${names}。`,
            actionId: this._spec.actionId,
            itemDeltas: null,
          })
        }
        if (battle) {
          this._members[i].recordStats(entryAtUnix, { encounters: 1 })
        }
      }
    }

    this._battle = battle
    if (this._battle) {
      this.canonical.state.hitpoints = this._battle.player.hitpoints
      this.canonical.state.manapoints = this._battle.player.manapoints
      this.canonical.state.combatSkills = combatSkillsFromCombatant(
        this._battle.player,
        this._battle.elapsedSeconds,
      )
      this.canonical.state.combatBuffCarry = combatBuffsFromCombatant(
        this._battle.player,
        this._battle.elapsedSeconds,
      )
      if (
        this._scheduleMode === ConsumableScheduleMode.PeriodicTick &&
        entryAtUnix >= this._nextConsumableEvaluation - 1e-9
      ) {
        this._nextConsumableEvaluation += 5
      }
      this.evaluateConsumables(entryAtTicks)
      this.overlaySessionSustained(entryAtUnix)
    }
    this.synchronizeViews()
    return stateRef.remainingSeconds
  }

  private evaluateConsumables(atTicks: bigint, wakeDelta: CombatFactDelta | null = null): number {
    if (!this._consumables) return 0
    if (this._scheduleMode === ConsumableScheduleMode.EventDriven && this._battle === null) return 0
    let activated = 0
    const now = ticksToUnixSeconds(atTicks)
    for (let s = 0; s < this._consumables.length; s++) {
      const row = this._consumables[s]
      if (
        wakeDelta !== null &&
        this._wakeMasks !== null &&
        s < this._wakeMasks.length &&
        !wakeMaskWakesOn(this._wakeMasks[s], wakeDelta, row.memberIndex)
      ) {
        continue
      }
      const item = this._catalog.items.get(row.itemId)
      if (
        !item ||
        item.category !== 'consumable' ||
        !item.consumable ||
        item.consumable.durable ||
        !item.consumable.usableInBars.has(AutoUseBar.Combat)
      ) {
        continue
      }
      const c = item.consumable
      if (c.isContinuous && c.durationSeconds > 0) {
        if (this.evaluateSustained(s, row, item.id, item.name, c.durationSeconds, atTicks, now)) {
          activated++
        }
      } else if (this.evaluateInstant(s, row, item.id, item.name, c, atTicks, now)) {
        activated++
      }
    }
    return activated
  }

  private evaluateSustained(
    index: number,
    row: SessionConsumableSlot,
    itemId: string,
    itemName: string,
    durationSeconds: number,
    atTicks: bigint,
    now: number,
  ): boolean {
    if (row.sustainedExpiresAtUnixSeconds > 0 && row.sustainedExpiresAtUnixSeconds <= now) {
      row = {
        ...row,
        sustainedExpiresAtUnixSeconds: 0,
        cooldownUntilUnixSeconds: 0,
      }
      this._consumables![index] = row
      this._collectors[row.memberIndex].events.add({
        atTicks,
        code: 'auto-use.depleted',
        message: `${itemName} 的持续时间已耗尽。`,
        actionId: this._spec.actionId,
        itemDeltas: null,
      })
    }
    if (
      this._battle === null ||
      row.sustainedExpiresAtUnixSeconds > 0 ||
      this.availableQuantity(row.memberIndex, row.itemId) < 1
    ) {
      return false
    }
    const caster = this._battle.playerAllies[row.memberIndex]
    if (!caster.isAlive) return false
    const item = this._catalog.items.get(itemId)!
    const triggerList = resolveConsumableTriggers(
      itemId,
      item.consumable!,
      this._members[row.memberIndex].state.consumableTriggers,
    )
    if (triggerList.length > 0) {
      const target = highestThreatAlive(this._battle.enemies)
      if (
        !evaluateTriggers(
          triggerList,
          caster,
          target,
          this._battle.playerAllies,
          this._battle.enemies,
          this._battle.elapsedSeconds,
        )
      ) {
        return false
      }
    }
    this.spendBalance(row.memberIndex, row.itemId)
    this._consumables![index] = {
      ...row,
      sustainedExpiresAtUnixSeconds: now + durationSeconds,
      cooldownUntilUnixSeconds: 0,
    }
    this.injectSustained(row.memberIndex, itemId, durationSeconds)
    this.spendConsumable(
      row.memberIndex,
      itemId,
      atTicks,
      now,
      `自动使用 ${itemName}，可生效 ${formatTrimmedFixed(durationSeconds, 6)} 秒。`,
    )
    return true
  }

  private evaluateInstant(
    index: number,
    row: SessionConsumableSlot,
    itemId: string,
    itemName: string,
    consumable: NonNullable<ReturnType<GameDataCatalog['items']['get']>>['consumable'] & {},
    atTicks: bigint,
    now: number,
  ): boolean {
    if (
      row.cooldownUntilUnixSeconds > now ||
      this.availableQuantity(row.memberIndex, row.itemId) < 1
    ) {
      return false
    }
    const rawItem = this._catalog.rawItems.get(itemId)
    const rawCons = rawItem?.consumable as Record<string, unknown> | undefined
    if (!rawCons) return false
    const hp = typeof rawCons.hitpointRestore === 'number' ? rawCons.hitpointRestore : 0
    const mp = typeof rawCons.manapointRestore === 'number' ? rawCons.manapointRestore : 0
    const rawBuffs = Array.isArray(rawCons.buffs)
      ? (rawCons.buffs as Record<string, unknown>[])
      : null
    const hasBuffs = rawBuffs !== null
    const strike = consumable.strike
    if (!hasBuffs && hp === 0 && mp === 0 && strike === null) return false

    const memberIndex = row.memberIndex
    const caster = this._battle ? this._battle.playerAllies[memberIndex] : null
    if (caster !== null ? !caster.isAlive : this._members[memberIndex].state.hitpoints <= 0) {
      return false
    }
    const target = this._battle ? highestThreatAlive(this._battle.enemies) : null
    const triggerList = resolveConsumableTriggers(
      itemId,
      consumable,
      this._members[memberIndex].state.consumableTriggers,
    )
    if (triggerList.length > 0) {
      const evalCaster = caster ?? this.buildSessionEvalCaster(memberIndex)
      const allies = this._battle ? this._battle.playerAllies : [evalCaster]
      const enemies = this._battle ? this._battle.enemies : []
      if (
        !evaluateTriggers(
          triggerList,
          evalCaster,
          target,
          allies,
          enemies,
          this._battle?.elapsedSeconds ?? 0,
        )
      ) {
        return false
      }
    }
    const cooldown = consumable.cooldownSeconds
    if (cooldown <= 0) {
      throw new Error('自动即时消耗品必须配置正冷却。')
    }

    if (strike !== null) {
      if (caster === null) return false
      const manaCost = strike.manaCostRatio * caster.stats.maxManapoints
      if (target === null || caster.manapoints < manaCost - 1e-9) return false
      const skillMult = percentMultiplier(caster.stats.skillBonusPoints, PercentCurveMode.Linear)
      const totalDmgBonus =
        caster.stats.totalDamageBonusPoints + caster.buffTotal(BuffEffectChannel.DamageBonusPoints)
      const strikeStats = {
        ...caster.stats,
        attackPower:
          (strike.manaDamageRatio * caster.stats.maxManapoints +
            strike.spellAttackRatio * caster.stats.spellAttack) *
          skillMult,
        damageKind: CombatDamageKind.Spell,
        element: strike.element as CombatElement,
        totalDamageBonusPoints: totalDmgBonus,
      }
      let damage = resolveStrikeDamage(strikeStats, target)
      caster.manapoints = Math.max(0, caster.manapoints - manaCost)
      const settled = applyCombatDamage(this._battle, caster, target, damage)
      damage = settled.damage
      applyRanhun(
        caster,
        target,
        strike.element as CombatElement,
        settled.actual,
        this._battle!.elapsedSeconds,
      )
      if (this._battle) {
        let anyAlive = false
        for (let e = 0; e < this._battle.enemies.length; e++) {
          if (this._battle.enemies[e].isAlive) {
            anyAlive = true
            break
          }
        }
        if (!anyAlive) this._battle.outcome = CombatOutcome.PlayerWon
      }
      if (target.definitionId && this.canonical.report) {
        const book = this.canonical.report.combatStats
        const attackerKey = memberIndex === 0 ? 'player' : `ally:${memberIndex}`
        book.register(attackerKey, this._spec.members[memberIndex].displayName)
        const monster = this._catalog.monsters.get(target.definitionId)
        book.register(`monster:${target.definitionId}`, monster ? monster.name : target.definitionId)
        book.recordHitKey(
          attackerKey,
          `monster:${target.definitionId}`,
          CombatDamageKind.Spell,
          damage,
          Math.max(0, strikeStats.attackPower - damage),
        )
      }
      this.spendBalance(memberIndex, row.itemId)
      this._consumables![index] = { ...row, cooldownUntilUnixSeconds: now + cooldown }
      this.spendConsumable(
        memberIndex,
        itemId,
        atTicks,
        now,
        `自动使用 ${itemName}，造成 ${formatTrimmedFixed(damage, 2)} 点伤害。`,
      )
      return true
    }

    let healed = 0
    if (caster !== null) {
      const before = caster.hitpoints
      caster.hitpoints = Math.min(caster.stats.maxHitpoints, caster.hitpoints + hp)
      caster.manapoints = Math.min(caster.stats.maxManapoints, caster.manapoints + mp)
      healed = caster.hitpoints - before
    } else {
      const member = this._members[memberIndex]
      const before = member.state.hitpoints
      member.state.hitpoints = Math.min(member.state.maxHitpoints, member.state.hitpoints + hp)
      member.state.manapoints = Math.min(member.state.maxManapoints, member.state.manapoints + mp)
      healed = member.state.hitpoints - before
      this.syncSessionExpeditionResources(memberIndex)
    }
    if (healed > 0 && this.canonical.report) {
      this.canonical.report.combatStats.recordHealingKey(
        memberIndex === 0 ? 'player' : `ally:${memberIndex}`,
        this._spec.members[memberIndex].displayName,
        false,
        healed,
      )
    }
    if (rawBuffs) {
      for (const buff of rawBuffs) {
        const buffId = String(buff.buffId)
        const duration = typeof buff.durationSeconds === 'number' ? buff.durationSeconds : 0
        if (duration <= 0) continue
        const effects = parseBuffEffects(buff, 1.0)
        const name = typeof buff.name === 'string' ? buff.name : buffId
        if (caster !== null) {
          caster.setBuff({
            buffId,
            expiresAtSeconds: this._battle!.elapsedSeconds + duration,
            stacks: 1,
            name,
            resetOnWave: false,
            missingHitpointsPerStack: 0,
            effects,
            statRatios: null,
            sourceUnitId: null,
          })
        } else {
          this.addCarryBuff(memberIndex, {
            buffId,
            remainingSeconds: duration,
            stacks: 1,
            name,
            missingHitpointsPerStack: 0,
            effects: effects ? effects.map((e) => ({ channel: e.channel, value: e.value })) : null,
            statRatios: null,
          })
        }
      }
    }
    this.spendBalance(memberIndex, row.itemId)
    this._consumables![index] = { ...row, cooldownUntilUnixSeconds: now + cooldown }
    this.spendConsumable(memberIndex, itemId, atTicks, now, `自动使用 ${itemName}。`)
    return true
  }

  private consumableWakeUnix(now: number): number {
    if (this._scheduleMode === ConsumableScheduleMode.PeriodicTick) {
      return this._nextConsumableEvaluation
    }
    if (this._battle === null) return Number.POSITIVE_INFINITY
    let wake = Number.POSITIVE_INFINITY
    if (this._consumables) {
      for (let i = 0; i < this._consumables.length; i++) {
        const row = this._consumables[i]
        if (row.cooldownUntilUnixSeconds > now) wake = Math.min(wake, row.cooldownUntilUnixSeconds)
        if (row.sustainedExpiresAtUnixSeconds > now) {
          wake = Math.min(wake, row.sustainedExpiresAtUnixSeconds)
        }
      }
    }
    return wake
  }

  private pendingConsumableWake(now: number): boolean {
    if (!this._consumables) return false
    for (let i = 0; i < this._consumables.length; i++) {
      const row = this._consumables[i]
      if (
        (row.cooldownUntilUnixSeconds > 0 && row.cooldownUntilUnixSeconds <= now + 1e-9) ||
        (row.sustainedExpiresAtUnixSeconds > 0 && row.sustainedExpiresAtUnixSeconds <= now + 1e-9)
      ) {
        return true
      }
    }
    return false
  }

  private evaluateConsumableCascade(
    atTicks: bigint,
    wakeDelta: CombatFactDelta | null = null,
  ): void {
    if (this.evaluateConsumables(atTicks, wakeDelta) === 0) return
    for (
      let pass = 1;
      pass < MAX_CONSUMABLE_CASCADE_PASSES && this.evaluateConsumables(atTicks) > 0;
      pass++
    ) {
      // 同刻级联
    }
  }

  private availableQuantity(memberIndex: number, itemId: string): number {
    const prefix = `${memberIndex}\0${itemId}\0`
    let total = 0
    for (const [k, qty] of this._workingWallet) {
      if (k.startsWith(prefix)) total += qty
    }
    return total
  }

  private spendBalance(memberIndex: number, itemId: string): void {
    const prefix = `${memberIndex}\0${itemId}\0`
    let remaining = 1
    while (remaining > 1e-9) {
      let selectedKey: string | null = null
      let selectedVariant = ''
      for (const [k, qty] of this._workingWallet) {
        if (!k.startsWith(prefix) || qty <= 0) continue
        const variant = k.slice(prefix.length)
        if (selectedKey === null || compareOrdinal(variant, selectedVariant) < 0) {
          selectedKey = k
          selectedVariant = variant
        }
      }
      if (selectedKey === null) break
      const cur = this._workingWallet.get(selectedKey) ?? 0
      const spent = Math.min(remaining, cur)
      this._workingWallet.set(selectedKey, cur - spent)
      this._assetDeltas.set(selectedKey, (this._assetDeltas.get(selectedKey) ?? 0) - spent)
      remaining -= spent
      if (remaining <= 1e-9) return
    }
    throw new Error('会话消耗品共享余额不足。')
  }

  private overlaySessionSustained(nowUnix: number): void {
    if (!this._consumables || !this._battle) return
    for (let s = 0; s < this._consumables.length; s++) {
      const row = this._consumables[s]
      const item = this._catalog.items.get(row.itemId)
      if (
        !item ||
        !item.consumable ||
        !item.consumable.isContinuous ||
        item.consumable.durationSeconds <= 0 ||
        item.consumable.durable
      ) {
        continue
      }
      this.injectSustained(row.memberIndex, item.id, row.sustainedExpiresAtUnixSeconds - nowUnix)
    }
  }

  private injectSustained(memberIndex: number, itemId: string, remainingSeconds: number): void {
    if (!this._battle) return
    const item = this._catalog.items.get(itemId)
    const rawItem = this._catalog.rawItems.get(itemId)
    const rawCons = rawItem?.consumable as Record<string, unknown> | undefined
    const rawBuffs = rawCons && Array.isArray(rawCons.buffs)
      ? (rawCons.buffs as Record<string, unknown>[])
      : null
    if (!item || !rawBuffs) return
    const teamWide = item.subCategory === 'formation'
    const expiresAt = this._battle.elapsedSeconds + remainingSeconds
    for (const buff of rawBuffs) {
      const buffId = String(buff.buffId)
      const effects = remainingSeconds > 0 ? parseBuffEffects(buff, 1.0) : null
      const name = typeof buff.name === 'string' ? buff.name : buffId
      for (let u = 0; u < this._battle.playerAllies.length; u++) {
        if (!teamWide && u !== memberIndex) continue
        const unit = this._battle.playerAllies[u]
        unit.removeBuffsWhere((current) => current.buffId === buffId)
        if (remainingSeconds > 0) {
          unit.addBuff({
            buffId,
            expiresAtSeconds: expiresAt,
            stacks: 1,
            name,
            resetOnWave: true,
            missingHitpointsPerStack: 0,
            effects,
            statRatios: null,
            sourceUnitId: null,
          })
        }
      }
    }
  }

  private spendConsumable(
    memberIndex: number,
    itemId: string,
    atTicks: bigint,
    nowUnix: number,
    message: string,
  ): void {
    const member = this._members[memberIndex]
    member.recordStats(nowUnix, { lostItem: itemId, lostQuantity: 1 })
    this._collectors[memberIndex].events.add({
      atTicks,
      code: 'auto-use.activated',
      message,
      actionId: this._spec.actionId,
      itemDeltas: null,
    })
  }

  private buildSessionEvalCaster(memberIndex: number): CombatantState {
    const member = this._members[memberIndex]
    const frozen = this._spec.members[memberIndex]
    const caster = new CombatantState(
      memberIndex === 0 ? 'player' : `ally:${memberIndex}`,
      frozen.stats,
      null,
    )
    caster.hitpoints = member.state.hitpoints
    caster.manapoints = member.state.manapoints
    const external = frozen.externalBuffs
    caster.externalBuffStacks = (buffId) => sumFrozenBuffStacks(external, buffId)
    return caster
  }

  private syncSessionExpeditionResources(memberIndex: number): void {
    const expedition = this.action.expedition
    if (!expedition) return
    const member = this._members[memberIndex]
    if (memberIndex === 0) {
      this.action.expedition = {
        ...expedition,
        playerHitpoints: member.state.hitpoints,
        playerManapoints: member.state.manapoints,
        playerState: expedition.playerState
          ? {
              ...expedition.playerState,
              hitpoints: member.state.hitpoints,
              manapoints: member.state.manapoints,
            }
          : null,
      }
      return
    }
    if (!expedition.allies) return
    const allies = expedition.allies.map((ally) =>
      ally.memberIndex === memberIndex
        ? {
            ...ally,
            state: {
              ...ally.state,
              hitpoints: member.state.hitpoints,
              manapoints: member.state.manapoints,
            },
          }
        : ally,
    )
    this.action.expedition = { ...expedition, allies }
  }

  private addCarryBuff(memberIndex: number, carry: CombatBuffCarryState): void {
    if (memberIndex === 0) {
      this.canonical.state.combatBuffCarry = [
        ...this.canonical.state.combatBuffCarry.filter((e) => e.buffId !== carry.buffId),
        carry,
      ]
      return
    }
    const expedition = this.action.expedition
    if (!expedition || !expedition.allies) return
    const allies = expedition.allies.map((ally) => {
      if (ally.memberIndex !== memberIndex) return ally
      const current = ally.combatBuffs ?? []
      return {
        ...ally,
        combatBuffs: [...current.filter((e) => e.buffId !== carry.buffId), carry],
      }
    })
    this.action.expedition = { ...expedition, allies }
  }

  private advancePreparationResources(seconds: number): void {
    let expedition = this.action.expedition!
    let allies = expedition.allies ? [...expedition.allies] : []
    for (let i = 0; i < this._members.length; i++) {
      const member = this._members[i]
      const hpBefore = member.state.hitpoints
      completeResourceRegeneration(member, this._catalog, seconds, false)
      if (i === 0) {
        if (member.state.hitpoints <= 0 && expedition.playerRespawnAtSeconds !== null) {
          const next = expedition.playerRespawnAtSeconds - seconds
          if (next <= 0) {
            member.state.hitpoints = member.state.maxHitpoints
            expedition = {
              ...expedition,
              playerHitpoints: member.state.maxHitpoints,
              playerRespawnAtSeconds: null,
            }
            this._collectors[0].events.add({
              atTicks: this._advancedAtTicks,
              code: 'combat.respawned',
              message: '复活完成，气血与灵力恢复。',
              actionId: this._spec.actionId,
              itemDeltas: null,
            })
          } else {
            expedition = { ...expedition, playerRespawnAtSeconds: next }
          }
        }
        continue
      }
      if (member.state.hitpoints > hpBefore) {
        this.canonical.report!.combatStats.recordHealingKey(
          `ally:${i}`,
          member.displayName,
          true,
          member.state.hitpoints - hpBefore,
        )
      }
      const idx = allies.findIndex((a) => a.memberIndex === i)
      if (idx >= 0) {
        let allyState = allies[idx].state
        if (member.state.hitpoints <= 0 && allyState.respawnAtSeconds !== null) {
          const next = allyState.respawnAtSeconds - seconds
          if (next <= 0) {
            member.state.hitpoints = member.state.maxHitpoints
            allyState = {
              ...allyState,
              hitpoints: member.state.maxHitpoints,
              manapoints: member.state.manapoints,
              respawnAtSeconds: null,
            }
            this._collectors[i].events.add({
              atTicks: this._advancedAtTicks,
              code: 'combat.respawned',
              message: '复活完成，气血与灵力恢复。',
              actionId: this._spec.actionId,
              itemDeltas: null,
            })
          } else {
            allyState = {
              ...allyState,
              hitpoints: member.state.hitpoints,
              manapoints: member.state.manapoints,
              respawnAtSeconds: next,
            }
          }
        } else {
          allyState = {
            ...allyState,
            hitpoints: member.state.hitpoints,
            manapoints: member.state.manapoints,
          }
        }
        allies[idx] = { ...allies[idx], state: allyState }
      }
    }
    this.action.expedition = allies.length > 0 ? { ...expedition, allies } : expedition
  }

  private advanceBattle(fromTicks: bigint, targetTicks: bigint, remaining: number): number {
    const encounter = this._catalog.encounters.get(this.action.expedition!.encounterId)!
    if (this._battle === null) {
      this._battle = restoreCombatBattle(this.canonical, this.action, encounter, this._catalog)
    }
    const before = captureCombatStateForSession(this._battle, this.action.expedition!)

    let result: CombatAdvanceResult
    if (this._scheduleMode === ConsumableScheduleMode.EventDriven) {
      const battleFrom = this._battle.elapsedSeconds
      const hook = new CombatBoundaryHookDispatcher((encounterCtx, delta) => {
        const atTicks = addSecondsExactTicks(fromTicks, encounterCtx.elapsedSeconds - battleFrom)
        this.evaluateConsumableCascade(atTicks, delta)
      }, this._battle)
      if (this._walletDirty) {
        hook.markExternal()
        this._walletDirty = false
      }
      result = advanceCombatTo(this._battle, battleFrom + remaining, hook)
    } else {
      result = advanceCombatTo(this._battle, this._battle.elapsedSeconds + remaining)
    }

    for (let i = 1; i < this._members.length; i++) {
      const member = this._members[i]
      const action = member.actions[0]
      action.expedition = projectExpedition(
        before,
        i,
        this.canonical.state.combatSkills,
        this.canonical.state.combatBuffCarry,
      )
      const view = this.projectBattle(this._battle, i)
      const mEvents = new SimEventTail()
      const mDeltas = new Map<string, number>()
      const mRef = { remainingSeconds: remaining, completedActions: 0, completedRepeats: 0 }
      completeCombatAdvanceCore(
        member,
        action,
        encounter,
        this._catalog,
        view,
        projectResult(result, i),
        fromTicks,
        targetTicks,
        mRef,
        mDeltas,
        mEvents,
        null,
      )
      this._collectors[i].addMutation(mEvents, mDeltas, true)
    }

    const canonEvents = new SimEventTail()
    const canonDeltas = new Map<string, number>()
    const canonRef = { remainingSeconds: remaining, completedActions: 0, completedRepeats: 0 }
    completeCombatAdvanceCore(
      this.canonical,
      this.action,
      encounter,
      this._catalog,
      this._battle,
      result,
      fromTicks,
      targetTicks,
      canonRef,
      canonDeltas,
      canonEvents,
      null,
    )
    const next = canonRef.remainingSeconds
    this._collectors[0].addMutation(canonEvents, canonDeltas, true)

    for (let i = 0; i < this._members.length; i++) {
      const m = this._members[i]
      m.state.regenCarrySeconds += remaining - next
      if (m.state.regenCarrySeconds >= 5 - 1 / 10_000_000) {
        m.state.regenCarrySeconds = 0
      }
    }
    if (this._battle.outcome !== CombatOutcome.Running) this._battle = null
    this.flushBattle()
    this.synchronizeViews()
    return next
  }

  private flushBattle(): void {
    if (this._battle === null) return
    this.action.expedition = captureCombatStateForSession(this._battle, this.action.expedition!)
    this.canonical.state.hitpoints = this._battle.player.hitpoints
    this.canonical.state.manapoints = this._battle.player.manapoints
    this.canonical.state.combatSkills = combatSkillsFromCombatant(
      this._battle.player,
      this._battle.elapsedSeconds,
    )
    this.canonical.state.combatBuffCarry = combatBuffsFromCombatant(
      this._battle.player,
      this._battle.elapsedSeconds,
    )
  }

  private synchronizeViews(): void {
    const canonical = this.action.expedition!
    for (let i = 0; i < this._members.length; i++) {
      const member = this._members[i]
      member.lastSimulatedAtTicks = this._advancedAtTicks
      const action = member.actions[0]
      const view = projectExpedition(
        canonical,
        i,
        this.canonical.state.combatSkills,
        this.canonical.state.combatBuffCarry,
      )
      action.expedition = view
      action.progressSeconds = this.action.progressSeconds
      action.completedRepeats = this.action.completedRepeats
      if (i === 0) continue
      member.state.hitpoints = view.playerHitpoints
      member.state.manapoints = view.playerManapoints
      const allyRow = canonical.allies!.find((a) => a.memberIndex === i)!
      member.state.combatSkills =
        allyRow.combatSkills ?? this._spec.members[i].combatSkills ?? createEmptyCombatSkillState()
      member.state.combatBuffCarry =
        allyRow.combatBuffs ?? this._spec.members[i].combatBuffs ?? []
    }
  }

  private projectBattle(canonical: CombatEncounterState, memberIndex: number): CombatEncounterState {
    const allyTemplates: CombatantTemplate[] = this._spec.members.map((m) => ({
      stats: m.stats,
      skills: m.skills,
    }))
    const allySnaps = canonical.playerAllies.map((u) => CombatEncounterState.snapshot(u))
    const enemyTemplates: CombatantTemplate[] = canonical.enemies.map((e) => ({
      stats: e.stats,
      skills: e.skills,
    }))
    const enemySnaps = canonical.enemies.map((e) => CombatEncounterState.snapshot(e))
    const view = CombatEncounterState.restoreAllied(
      canonical.encounterId,
      canonical.seed,
      allyTemplates,
      allySnaps,
      memberIndex,
      enemyTemplates,
      enemySnaps,
      canonical.elapsedSeconds,
      canonical.outcome,
      canonical.sequence,
      canonical.playerRespawnSeconds,
    )
    for (let i = 0; i < view.enemies.length; i++) {
      view.enemies[i].definitionId = canonical.enemies[i].definitionId
    }
    return view
  }
}

function captureCombatStateForSession(
  battle: CombatEncounterState,
  current: SimCombatExpeditionState,
): SimCombatExpeditionState {
  return captureCombatState(battle, current)
}

function highestThreatAlive(enemies: readonly CombatantState[]): CombatantState | null {
  let best: CombatantState | null = null
  for (let i = 0; i < enemies.length; i++) {
    const e = enemies[i]
    if (e.isAlive && (best === null || e.effectiveThreat > best.effectiveThreat)) {
      best = e
    }
  }
  return best
}

function projectExpedition(
  canonical: SimCombatExpeditionState,
  memberIndex: number,
  canonicalSkills: CombatSkillStateModel,
  canonicalBuffs: CombatBuffCarryState[],
): SimCombatExpeditionState {
  if (memberIndex === 0) return canonical
  const self = canonical.allies!.find((a) => a.memberIndex === memberIndex)!
  const previousSelf = canonical.playerState ?? {
    hitpoints: canonical.playerHitpoints,
    manapoints: canonical.playerManapoints,
    nextAttackAtSeconds: canonical.playerNextAttackAtSeconds,
    attackCount: canonical.playerAttackCount,
    threat: canonical.playerThreat,
    pendingSkillIndex: -1,
    threatBonusPoints: 0,
    bonusAbsorptionPoints: 0,
    skillCooldownUntil: null,
    skillCastCount: 0,
    buffs: null,
    respawnAtSeconds: null,
    respawnPolicy: 0,
    respawnTargetIndex: -1,
    respawnDeadlineSeconds: null,
    respawnWaitingUntilSeconds: null,
    traits: [],
    traitLevels: null,
  }
  const others = canonical.allies!.filter((a) => a.memberIndex !== memberIndex)
  others.push({
    memberIndex: 0,
    state: previousSelf,
    combatSkills: canonicalSkills,
    combatBuffs: canonicalBuffs,
    pendingAction: null,
    pendingActionDurationSeconds: 0,
  })
  others.sort((left, right) => left.memberIndex - right.memberIndex)
  return {
    ...canonical,
    selfIndex: memberIndex,
    playerHitpoints: self.state.hitpoints,
    playerManapoints: self.state.manapoints,
    playerNextAttackAtSeconds: self.state.nextAttackAtSeconds,
    playerAttackCount: self.state.attackCount,
    playerThreat: self.state.threat,
    playerState: canonical.phase === CombatExpeditionPhase.Combat ? self.state : null,
    playerRespawnAtSeconds:
      canonical.phase === CombatExpeditionPhase.Combat ? self.state.respawnAtSeconds : null,
    allies: others,
  }
}

function projectUnitKey(key: string, memberIndex: number): string {
  const idx =
    key === 'player'
      ? 0
      : key.startsWith('ally:')
        ? Number.parseInt(key.slice(5), 10)
        : -1
  if (Number.isNaN(idx) || idx < 0) return key
  return idx === memberIndex ? 'player' : `ally:${idx}`
}

function projectResult(canonical: CombatAdvanceResult, memberIndex: number): CombatAdvanceResult {
  const deaths: AllyMark[] = canonical.allyDeathMarks ? [...canonical.allyDeathMarks] : []
  if (canonical.playerDeathTimestamps) {
    for (const at of canonical.playerDeathTimestamps) deaths.push({ memberIndex: 0, atSeconds: at })
  }
  const respawns: AllyMark[] = canonical.allyRespawnMarks ? [...canonical.allyRespawnMarks] : []
  if (canonical.playerRespawnTimestamps) {
    for (const at of canonical.playerRespawnTimestamps) {
      respawns.push({ memberIndex: 0, atSeconds: at })
    }
  }
  return {
    ...canonical,
    events: canonical.events.map((frame) => ({
      ...frame,
      attackerId: projectUnitKey(frame.attackerId, memberIndex),
      targetId: projectUnitKey(frame.targetId, memberIndex),
    })),
    playerDeathTimestamps: deaths
      .filter((m) => m.memberIndex === memberIndex)
      .map((m) => m.atSeconds),
    playerRespawnTimestamps: respawns
      .filter((m) => m.memberIndex === memberIndex)
      .map((m) => m.atSeconds),
    allyDeathMarks: deaths
      .filter((m) => m.memberIndex !== memberIndex)
      .sort((a, b) => a.atSeconds - b.atSeconds),
    allyRespawnMarks: respawns
      .filter((m) => m.memberIndex !== memberIndex)
      .sort((a, b) => a.atSeconds - b.atSeconds),
  }
}

function projectStats(
  stats: readonly CombatUnitStatDocument[],
  memberIndex: number,
): CombatUnitStatDocument[] {
  return stats.map((row) => {
    const key = projectUnitKey(row.unitKey, memberIndex)
    return {
      ...row,
      unitKey: key,
      kind: key === 'player' ? 0 : key.startsWith('ally:') ? 1 : 2,
    }
  })
}

function createEmptyMemberState(
  frozen: ActionParticipantSnapshot,
  spec: SimActionParticipationSpec,
  atTicks: bigint,
  _fingerprint: string,
): PlayerState {
  return {
    playerId: frozen.playerId,
    displayName: frozen.displayName,
    avatar: 'player',
    lastSimulatedAtTicks: atTicks,
    realmLevel: frozen.realmLevel,
    experience: 0,
    foundation: 0,
    combatExperience: 0,
    lifetimeExperience: 0,
    ageSeconds: 0,
    completedActions: 0,
    normalKills: 0,
    eliteKills: 0,
    bossKills: 0,
    totalDefeats: 0,
    defeatedMonsterIds: new Set<string>(),
    traitIds: null,
    unlockedTraits: null,
    traitLevelMap: null,
    confirmedLinggen: null,
    heavenGateEntered: false,
    effectiveLinggen: { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 },
    coreAttributes: {
      staminaExp: 0,
      meridiansExp: 0,
      attackExp: 0,
      boneExp: 0,
      agilityExp: 0,
      consciousnessExp: 0,
    },
    abilitySlots: [],
    abilityExperiences: {},
    equipment: [],
    equippedItemIds: [],
    techniqueExperiences: {},
    abilityTriggers: {},
    consumableTriggers: {},
    hitpoints: frozen.initialHitpoints,
    maxHitpoints: frozen.stats.maxHitpoints,
    manapoints: frozen.initialManapoints,
    maxManapoints: frozen.stats.maxManapoints,
    attackPower: 0,
    defense: 0,
    regenCarrySeconds: frozen.regenCarrySeconds,
    combatSkills: frozen.combatSkills ?? createEmptyCombatSkillState(),
    combatBuffCarry: frozen.combatBuffs ? [...frozen.combatBuffs] : [],
    buffs: [],
    autoUseSlots: [],
    respawnPolicy: frozen.respawnPolicy,
    respawnTargetPlayerId: frozen.respawnTargetPlayerId,
    worldNodeId: spec.nodeId,
    nodeBuildingEffects: { ...EMPTY_BUILDING_EFFECTS, monsterWeakenRatio: spec.monsterWeakenRatio },
    loopEnabled: false,
    simulationCarrySeconds: 0,
    wallet: new Map<string, number>(),
  }
}

/** 对应 `PlayerAggregate.ApplyActionParticipationSettlement`。 */
export function applyActionParticipationSettlement(
  player: SimPlayerRuntime,
  settlement: ActionParticipationMemberSettlement,
  catalog: GameDataCatalog,
): {
  assetDeltas: SimulatorAssetDto[]
  events: SimEvent[]
  droppedEventCount: number
} {
  if (player.actions.length === 0 || !player.actions[0].participation) {
    throw new Error('会话结算与成员绑定不一致。')
  }
  const binding = player.actions[0].participation!
  if (binding.sessionId !== settlement.sessionId) {
    throw new Error('会话结算与成员绑定不一致。')
  }
  if (settlement.sequence <= binding.appliedSequence) {
    return { assetDeltas: [], events: [], droppedEventCount: 0 }
  }
  if (settlement.sequence !== binding.appliedSequence + 1) {
    throw new Error('会话个人结算序号不连续。')
  }

  const action = player.actions[0]
  const actionDef = catalog.actions.get(action.actionId)
  const actionName = player.report?.actionName ?? actionDef?.name ?? action.actionId
  player.ensureActionReport(action.id, action.actionId, actionName)

  for (let i = 0; i < settlement.stats.length; i++) {
    const entry = settlement.stats[i]
    let delta = entry.delta
    if (delta.combatExperience && delta.combatExperience > 0) {
      const combatExp = delta.combatExperience
      const amplified =
        delta.cultivationExperience && delta.cultivationExperience > 0
          ? delta.cultivationExperience
          : combatExp
      const cap = resolveExpCapWithLinggen(player.state, catalog)
      const credited = player.creditExperience(amplified, cap, combatExp)
      delta = {
        ...delta,
        experience: credited,
        foundation: amplified - credited,
        cultivationExperience: 0,
      }
    }
    if (delta.rootAspect && delta.rootExperience) {
      addCoreAspectExp(player.state, delta.rootAspect, delta.rootExperience)
    }
    if (delta.abilityId) {
      if (!player.learnedAbilities.has(delta.abilityId)) continue
      const before = player.state.abilityExperiences[delta.abilityId] ?? 0
      const after = before + (delta.abilityExperience ?? 0)
      player.state.abilityExperiences[delta.abilityId] = after
      const fromLevel = abilityLevelForExp(before)
      const toLevel = abilityLevelForExp(after)
      if (toLevel > fromLevel) {
        for (let s = 0; s < player.state.abilitySlots.length; s++) {
          const slot = player.state.abilitySlots[s]
          if (slot.abilityId === delta.abilityId && slot.level < toLevel) {
            player.state.abilitySlots[s] = { abilityId: slot.abilityId, level: toLevel }
          }
        }
      }
    }
    player.state.completedActions += delta.cycles ?? 0
    for (let k = 0; k < (delta.normalKills ?? 0); k++) player.state.normalKills += 1
    for (let k = 0; k < (delta.eliteKills ?? 0); k++) player.state.eliteKills += 1
    for (let k = 0; k < (delta.bossKills ?? 0); k++) player.state.bossKills += 1
    player.state.totalDefeats += delta.deaths ?? 0
    player.recordStats(entry.atUnixSeconds, delta)
  }

  player.state.hitpoints = settlement.hitpoints
  player.state.manapoints = settlement.manapoints
  player.state.regenCarrySeconds = settlement.regenCarrySeconds
  player.state.combatSkills = settlement.skills
  player.state.combatBuffCarry = settlement.buffs

  if (settlement.consumableSlots) {
    const now = Math.max(
      ticksToUnixSeconds(settlement.toTicks),
      ticksToUnixSeconds(player.lastSimulatedAtTicks),
    )
    for (const clock of settlement.consumableSlots) {
      const idx = player.state.autoUseSlots.findIndex(
        (slot) =>
          slot.actionType === AutoUseBar.Combat &&
          slot.group === clock.group &&
          slot.slotIndex === clock.slotIndex &&
          slot.itemId === clock.itemId,
      )
      if (idx < 0) continue
      const slot = player.state.autoUseSlots[idx]
      const remaining = Math.max(0, clock.sustainedExpiresAtUnixSeconds - now)
      if (
        slot.cooldownUntilUnixSeconds === clock.cooldownUntilUnixSeconds &&
        slot.remainingDurationSeconds === remaining
      ) {
        continue
      }
      player.state.autoUseSlots[idx] = {
        ...slot,
        cooldownUntilUnixSeconds: clock.cooldownUntilUnixSeconds,
        remainingDurationSeconds: remaining,
      }
    }
  }

  action.expedition = settlement.expedition
  action.progressSeconds = settlement.progressSeconds
  action.completedRepeats = settlement.completedRepeats
  action.participation = {
    ...binding,
    appliedSequence: settlement.sequence,
  }

  const book = player.report!.combatStats
  book.clear()
  for (let i = 0; i < settlement.combatStats.length; i++) {
    book.importDoc(settlement.combatStats[i])
  }

  return {
    assetDeltas: settlement.assetDeltas,
    events: settlement.events,
    droppedEventCount: settlement.droppedEventCount,
  }
}
