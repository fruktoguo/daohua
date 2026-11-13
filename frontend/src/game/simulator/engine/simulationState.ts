// 单人/组队模拟共享的运行时聚合状态、资产钱包、事件尾窗与统计账本
// 严格对应 Domain.Player.PlayerAggregate / AssetWallet / ActionReportBook / SimulationEventTail

import type { GameDataCatalog } from './catalog.ts'
import {
  CombatOutcome,
  CombatUnitStatsBook,
  CombatantState,
  type CombatEvent,
  type CombatantSnapshot,
} from './combatModels.ts'
import {
  addSecondsExactTicks,
  compareOrdinal,
  roundToEven,
  ticksFromUnixSeconds,
  ticksToUnixSeconds,
} from './math.ts'
import {
  CombatRespawnPolicy,
  abilityLevelForExp,
  linggenEffectiveCount,
  resolveLinggenCultivationMultiplier,
  type BuffEffect,
  type CombatantStats,
} from './rules.ts'
import {
  applyResourceAttributes,
  expireBuffs,
  playerAttributesToCombatantStats,
  resolvePlayerAttributesFromState,
  syncDurableConsumableBuffs,
} from './attributeResolver.ts'
import type {
  ActiveBuffState,
  CombatBuffCarryState,
  PlayerAttributes,
  PlayerState,
  SimulatorAssetDto,
  SimulatorCombatBuffCarryDto,
  SimulatorCombatSkillCooldownDto,
  SimulatorCombatSkillsDto,
  SimulatorCombatUnitStatDto,
  SimulatorEventDto,
  SimulatorReportItemDto,
  SimulatorRootExpDto,
  TriggerConditionJson,
} from './types.ts'

export const CombatExpeditionPhase = {
  InitialSearch: 'InitialSearch',
  Search: 'Search',
  Combat: 'Combat',
  Respawning: 'Respawning',
  DuelPending: 'DuelPending',
  Duel: 'Duel',
} as const
export type CombatExpeditionPhase =
  (typeof CombatExpeditionPhase)[keyof typeof CombatExpeditionPhase]

export const ActionQueueStatus = {
  Pending: 'Pending',
  Running: 'Running',
  Blocked: 'Blocked',
} as const
export type ActionQueueStatus = (typeof ActionQueueStatus)[keyof typeof ActionQueueStatus]

export type CoreAspectKind =
  | 'Stamina'
  | 'Meridians'
  | 'Attack'
  | 'Bone'
  | 'Agility'
  | 'Consciousness'

export const CORE_ASPECT_KINDS: readonly CoreAspectKind[] = [
  'Stamina',
  'Meridians',
  'Attack',
  'Bone',
  'Agility',
  'Consciousness',
]

export const PlayerSectionKind = {
  Progression: 'Progression',
  World: 'World',
  Combat: 'Combat',
  Buffs: 'Buffs',
  Settings: 'Settings',
  Loadout: 'Loadout',
  ActionQueue: 'ActionQueue',
  Duel: 'Duel',
} as const
export type PlayerSectionKind = (typeof PlayerSectionKind)[keyof typeof PlayerSectionKind]

export const ConsumableScheduleMode = {
  EventDriven: 'EventDriven',
  PeriodicTick: 'PeriodicTick',
} as const
export type ConsumableScheduleMode =
  (typeof ConsumableScheduleMode)[keyof typeof ConsumableScheduleMode]

export interface SimEvent {
  atTicks: bigint
  code: string
  message: string
  actionId: string | null
  itemDeltas: Record<string, number> | null
}

/**
 * 模拟事件有界尾窗（严格对齐 `SimulationEngine.SimulationEventTail`）：
 * TailCapacity = 256，TrimThreshold = 512。
 */
export class SimEventTail {
  static readonly TailCapacity = 256
  static readonly TrimThreshold = 512

  private readonly _events: SimEvent[] = []
  dropped = 0

  get count(): number {
    return this._events.length
  }

  get view(): readonly SimEvent[] {
    return this._events
  }

  add(entry: SimEvent): void {
    if (this._events.length === SimEventTail.TrimThreshold) {
      this._events.splice(0, SimEventTail.TailCapacity)
      this.dropped += SimEventTail.TailCapacity
    }
    this._events.push(entry)
  }

  merge(innerEvents: readonly SimEvent[], innerDropped: number): void {
    this.dropped += innerDropped
    for (let i = 0; i < innerEvents.length; i++) {
      this.add(innerEvents[i])
    }
  }

  fold(count: number): void {
    this.dropped += count
  }

  toDtos(): SimulatorEventDto[] {
    return this._events.map(simEventToDto)
  }
}

export function simEventToDto(ev: SimEvent): SimulatorEventDto {
  let itemDeltas: Record<string, number> | null = null
  if (ev.itemDeltas) {
    itemDeltas = { ...ev.itemDeltas }
  }
  return {
    atUnixSeconds: ticksToUnixSeconds(ev.atTicks),
    code: ev.code,
    message: ev.message,
    actionId: ev.actionId,
    itemDeltas,
  }
}

export function assetKeyString(itemId: string, variant = ''): string {
  return `${itemId}\0${variant}`
}

export function splitAssetKey(key: string): { itemId: string; variant: string } {
  const idx = key.indexOf('\0')
  if (idx < 0) return { itemId: key, variant: '' }
  return { itemId: key.slice(0, idx), variant: key.slice(idx + 1) }
}

/** 对应 Domain.Economy.AssetWallet。 */
export class AssetWallet {
  readonly playerId: string
  readonly balances = new Map<string, number>()

  constructor(playerId: string, initial?: ReadonlyMap<string, number>) {
    this.playerId = playerId
    if (initial) {
      for (const [k, v] of initial) {
        if (v !== 0) this.balances.set(k, v)
      }
    }
  }

  get(itemId: string, variant = ''): number {
    return this.balances.get(assetKeyString(itemId, variant)) ?? 0
  }

  applyKey(key: string, quantityDelta: number): void {
    const next = (this.balances.get(key) ?? 0) + quantityDelta
    if (next < -1e-9) {
      throw new Error(`资产 ${key} 余额不能为负: ${next}`)
    }
    if (Math.abs(next) <= 1e-12) {
      this.balances.delete(key)
    } else {
      this.balances.set(key, next)
    }
  }

  apply(itemId: string, quantityDelta: number, variant = ''): void {
    this.applyKey(assetKeyString(itemId, variant), quantityDelta)
  }
}

export interface CombatSkillStateModel {
  cooldowns: SimulatorCombatSkillCooldownDto[]
  castingAbilityId: string | null
  castRemainingSeconds: number
  castCount: number
}

export function createEmptyCombatSkillState(): CombatSkillStateModel {
  return {
    cooldowns: [],
    castingAbilityId: null,
    castRemainingSeconds: 0,
    castCount: 0,
  }
}

export function isCombatSkillStateEmpty(skills: CombatSkillStateModel): boolean {
  return (
    skills.cooldowns.length === 0 &&
    skills.castingAbilityId === null &&
    skills.castRemainingSeconds === 0 &&
    skills.castCount === 0
  )
}

/** 对应 `PlayerCombatSkillState.AdvanceIdle`。 */
export function advanceIdleCombatSkills(
  skills: CombatSkillStateModel,
  seconds: number,
): CombatSkillStateModel {
  const cooldowns: SimulatorCombatSkillCooldownDto[] = []
  for (let i = 0; i < skills.cooldowns.length; i++) {
    const c = skills.cooldowns[i]
    if (c.remainingSeconds > seconds + 1e-9) {
      cooldowns.push({
        abilityId: c.abilityId,
        remainingSeconds: c.remainingSeconds - seconds,
      })
    }
  }
  return {
    cooldowns,
    castingAbilityId: null,
    castRemainingSeconds: 0,
    castCount: skills.castCount,
  }
}

/** 对应 `PlayerCombatSkillState.ApplyTo`。 */
export function applyCombatSkillsToCombatant(
  skills: CombatSkillStateModel,
  unit: CombatantState,
): void {
  if (
    skills.cooldowns.length === 0 &&
    skills.castingAbilityId === null &&
    skills.castCount === 0
  ) {
    return
  }
  const untils =
    unit.skillCooldownUntil ?? (unit.skillCooldownUntil = new Array<number>(unit.skills.length).fill(0))
  for (let i = 0; i < unit.skills.length; i++) {
    const id = typeof unit.skills[i].ability.id === 'string' ? unit.skills[i].ability.id : null
    if (!id) continue
    let remaining = 0
    for (let j = 0; j < skills.cooldowns.length; j++) {
      if (skills.cooldowns[j].abilityId === id) {
        remaining = skills.cooldowns[j].remainingSeconds
        break
      }
    }
    if (remaining > 0) untils[i] = remaining
  }
  unit.skillCastCount = skills.castCount
  if (skills.castingAbilityId !== null) {
    for (let i = 0; i < unit.skills.length; i++) {
      if (unit.skills[i].ability.id === skills.castingAbilityId) {
        unit.pendingSkillIndex = i
        unit.nextAttackAtSeconds = Math.max(1e-9, skills.castRemainingSeconds)
        break
      }
    }
  }
}

/** 对应 `PlayerCombatSkillState.FromCombatant`。 */
export function combatSkillsFromCombatant(
  unit: CombatantState,
  elapsedSeconds: number,
): CombatSkillStateModel {
  let cooldowns: SimulatorCombatSkillCooldownDto[] | null = null
  if (unit.skillCooldownUntil) {
    for (let i = 0; i < unit.skillCooldownUntil.length && i < unit.skills.length; i++) {
      if (unit.skillCooldownUntil[i] > elapsedSeconds + 1e-9) {
        const abilityId = unit.skills[i].ability.id
        if (typeof abilityId === 'string' && abilityId.length > 0) {
          ;(cooldowns ??= []).push({
            abilityId,
            remainingSeconds: unit.skillCooldownUntil[i] - elapsedSeconds,
          })
        }
      }
    }
  }
  let castingId: string | null = null
  let castRemaining = 0
  if (unit.pendingSkillIndex >= 0 && unit.pendingSkillIndex < unit.skills.length) {
    const id = unit.skills[unit.pendingSkillIndex].ability.id
    if (typeof id === 'string' && id.length > 0) {
      castingId = id
      castRemaining = Math.max(0, unit.nextAttackAtSeconds - elapsedSeconds)
    }
  }
  if (cooldowns === null && castingId === null && unit.skillCastCount === 0) {
    return createEmptyCombatSkillState()
  }
  return {
    cooldowns: cooldowns ?? [],
    castingAbilityId: castingId,
    castRemainingSeconds: castRemaining,
    castCount: unit.skillCastCount,
  }
}

/** 对应 `PlayerCombatBuffState.ApplyTo`。 */
export function applyCombatBuffsToCombatant(
  carries: readonly CombatBuffCarryState[],
  unit: CombatantState,
  battleElapsedSeconds: number,
): void {
  for (let i = 0; i < carries.length; i++) {
    const carry = carries[i]
    const expiresAt = battleElapsedSeconds + carry.remainingSeconds
    unit.setBuff({
      buffId: carry.buffId,
      expiresAtSeconds: Number.isFinite(expiresAt) ? expiresAt : Number.MAX_VALUE,
      stacks: carry.stacks,
      name: carry.name,
      resetOnWave: false,
      missingHitpointsPerStack: carry.missingHitpointsPerStack,
      effects: carry.effects,
      statRatios: carry.statRatios,
      sourceUnitId: null,
    })
  }
  unit.syncDerivedStacks()
}

/** 对应 `PlayerCombatBuffState.FromCombatant`。 */
export function combatBuffsFromCombatant(
  unit: CombatantState,
  elapsedSeconds: number,
): CombatBuffCarryState[] {
  const carries: CombatBuffCarryState[] = []
  for (let i = 0; i < unit.buffs.length; i++) {
    const buff = unit.buffs[i]
    if (buff.resetOnWave) continue
    const remaining = buff.expiresAtSeconds - elapsedSeconds
    if (!(remaining > 1e-9)) continue
    carries.push({
      buffId: buff.buffId,
      remainingSeconds: remaining,
      stacks: buff.stacks,
      name: buff.name,
      missingHitpointsPerStack: buff.missingHitpointsPerStack,
      effects: buff.effects,
      statRatios: buff.statRatios,
    })
  }
  return carries
}

export function combatBuffCarryToDto(carry: CombatBuffCarryState): SimulatorCombatBuffCarryDto {
  return {
    buffId: carry.buffId,
    remainingSeconds: carry.remainingSeconds,
    stacks: carry.stacks,
    name: carry.name,
    missingHitpointsPerStack: carry.missingHitpointsPerStack,
    effects: carry.effects
      ? carry.effects.map((e) => ({ channel: e.channel, value: e.value }))
      : undefined,
    statRatios: carry.statRatios ? { ...carry.statRatios } : undefined,
  }
}

export interface ExpeditionEnemyState {
  enemyId: string
  state: CombatantSnapshot
  pendingAction: string | null
  pendingActionDurationSeconds: number
}

export interface ExpeditionAllyState {
  memberIndex: number
  state: CombatantSnapshot
  pendingAction: string | null
  pendingActionDurationSeconds: number
  combatSkills: CombatSkillStateModel
  combatBuffs: CombatBuffCarryState[]
}

export interface SimCombatExpeditionState {
  encounterId: string
  phase: CombatExpeditionPhase
  phaseElapsedSeconds: number
  phaseDurationSeconds: number
  seed: bigint
  battleElapsedSeconds: number
  playerHitpoints: number
  playerManapoints: number
  playerNextAttackAtSeconds: number
  playerAttackCount: number
  playerThreat: number
  outcome: CombatOutcome
  waveNumber: number
  enemies: ExpeditionEnemyState[] | null
  recentEvents: CombatEvent[] | null
  playerRespawnAtSeconds: number | null
  playerState: CombatantSnapshot | null
  originNodeId: string | null
  isPinned: boolean
  selfIndex: number
  allies: ExpeditionAllyState[] | null
  duelSeekFailed: boolean
}

export interface ActionSustainedBuff {
  buffId: string
  remainingSeconds: number
  stacks: number
  name: string
  teamWide: boolean
  effects: BuffEffect[] | null
  statRatios: Record<string, number> | null
}

export interface ActionConsumableSlot {
  group: string
  slotIndex: number
  itemId: string
  cooldownUntilUnixSeconds: number
  sustainedExpiresAtUnixSeconds: number
}

export interface ActionParticipantSnapshot {
  playerId: string
  displayName: string
  realmLevel: number
  stats: CombatantStats
  skills: import('./catalog.ts').SkillSlotDefinition[]
  initialHitpoints: number
  initialManapoints: number
  searchSpeedPoints: number
  dropBonusPoints: number
  levelMalus: number
  externalBuffs: Record<string, number> | null
  sustainedBuffs: ActionSustainedBuff[] | null
  combatSkills: CombatSkillStateModel | null
  combatBuffs: CombatBuffCarryState[] | null
  respawnPolicy: CombatRespawnPolicy
  respawnTargetPlayerId: string | null
  regenCarrySeconds: number
  consumableSlots: ActionConsumableSlot[] | null
  consumableTriggers: Record<string, TriggerConditionJson[]> | null
  internalArtId: string | null
  internalArtLevel: number
}

export interface SimActionParticipationSpec {
  sessionId: string
  encounterId: string
  nodeId: string
  seed: bigint
  searchSpeedPoints: number
  startedAtUnixSeconds: number
  members: ActionParticipantSnapshot[]
  actionId: string
  pvpEnabled: boolean
  monsterWeakenRatio: number
  difficulty: number
  totalDropBonusPoints: number
  levelMaluses: number[]
  assignmentWeights: number[]
}

export interface SimActionParticipationBinding {
  sessionId: string
  spec: SimActionParticipationSpec
  appliedSequence: number
  endsAtUnixSeconds: number
}

export interface SimActionState {
  id: string
  actionId: string
  difficulty: number
  pvpEnabled: boolean
  remainingRepeats: number
  completedRepeats: number
  progressSeconds: number
  status: ActionQueueStatus
  expedition: SimCombatExpeditionState | null
  participation: SimActionParticipationBinding | null
}

export interface PlayerStatsDelta {
  experience?: number
  foundation?: number
  combatExperience?: number
  cultivationExperience?: number
  cycles?: number
  encounters?: number
  normalKills?: number
  eliteKills?: number
  bossKills?: number
  deaths?: number
  rootAspect?: CoreAspectKind
  rootExperience?: number
  abilityId?: string
  abilityExperience?: number
  gainedItem?: string
  gainedQuantity?: number
  lostItem?: string
  lostQuantity?: number
}

export class SimBattleReport {
  readonly actionInstanceId: string
  readonly actionId: string
  readonly actionName: string
  experience = 0
  foundation = 0
  combatExperience = 0
  cycles = 0
  encounters = 0
  normalKills = 0
  eliteKills = 0
  bossKills = 0
  deaths = 0
  readonly rootExp: SimulatorRootExpDto = {
    stamina: 0,
    meridians: 0,
    attack: 0,
    bone: 0,
    agility: 0,
    consciousness: 0,
  }
  readonly itemsMap = new Map<string, number>()
  readonly itemsLostMap = new Map<string, number>()
  readonly combatStats = new CombatUnitStatsBook()

  constructor(actionInstanceId: string, actionId: string, actionName: string) {
    this.actionInstanceId = actionInstanceId
    this.actionId = actionId
    this.actionName = actionName
  }

  addItem(itemId: string, qty: number): void {
    if (qty <= 0) return
    this.itemsMap.set(itemId, (this.itemsMap.get(itemId) ?? 0) + qty)
  }

  addItemLost(itemId: string, qty: number): void {
    if (qty <= 0) return
    this.itemsLostMap.set(itemId, (this.itemsLostMap.get(itemId) ?? 0) + qty)
  }

  applyDelta(delta: PlayerStatsDelta): void {
    if (delta.experience) this.experience += delta.experience
    if (delta.foundation) this.foundation += delta.foundation
    if (delta.combatExperience) this.combatExperience += delta.combatExperience
    if (delta.cycles) this.cycles += delta.cycles
    if (delta.encounters) this.encounters += delta.encounters
    for (let i = 0; i < (delta.normalKills ?? 0); i++) this.normalKills += 1
    for (let i = 0; i < (delta.eliteKills ?? 0); i++) this.eliteKills += 1
    for (let i = 0; i < (delta.bossKills ?? 0); i++) this.bossKills += 1
    if (delta.deaths) this.deaths += delta.deaths
    if (delta.rootAspect && delta.rootExperience) {
      addRootExpToDto(this.rootExp, delta.rootAspect, delta.rootExperience)
    }
    if (delta.gainedItem) this.addItem(delta.gainedItem, delta.gainedQuantity ?? 0)
    if (delta.lostItem) this.addItemLost(delta.lostItem, delta.lostQuantity ?? 0)
  }

  toItemsDto(): SimulatorReportItemDto[] {
    const out: SimulatorReportItemDto[] = []
    for (const [itemId, quantity] of this.itemsMap) {
      out.push({ itemId, variant: '', quantity })
    }
    return out
  }

  toItemsLostDto(): SimulatorReportItemDto[] {
    const out: SimulatorReportItemDto[] = []
    for (const [itemId, quantity] of this.itemsLostMap) {
      out.push({ itemId, variant: '', quantity })
    }
    return out
  }

  toCombatStatDtos(): SimulatorCombatUnitStatDto[] {
    return this.combatStats.entries.map((unit) => ({
      unitKey: unit.unitKey,
      kind: unit.kind,
      displayName: unit.displayName,
      damageDealtPhysical: unit.damageDealtPhysical,
      damageDealtSpell: unit.damageDealtSpell,
      damageTakenPhysical: unit.damageTakenPhysical,
      damageTakenSpell: unit.damageTakenSpell,
      damageMitigatedPhysical: unit.damageMitigatedPhysical,
      damageMitigatedSpell: unit.damageMitigatedSpell,
      healingRegen: unit.healingRegen,
      healingOther: unit.healingOther,
      damageDealtTrue: unit.damageDealtTrue,
      damageTakenTrue: unit.damageTakenTrue,
      damageDealtByElement: { ...unit.damageDealtByElement },
      damageTakenByElement: { ...unit.damageTakenByElement },
    }))
  }
}

export function addRootExpToDto(dto: SimulatorRootExpDto, aspect: CoreAspectKind, amount: number): void {
  switch (aspect) {
    case 'Stamina':
      dto.stamina += amount
      break
    case 'Meridians':
      dto.meridians += amount
      break
    case 'Attack':
      dto.attack += amount
      break
    case 'Bone':
      dto.bone += amount
      break
    case 'Agility':
      dto.agility += amount
      break
    case 'Consciousness':
      dto.consciousness += amount
      break
  }
}

export function coreAspectExp(state: PlayerState, aspect: CoreAspectKind): number {
  switch (aspect) {
    case 'Stamina':
      return state.coreAttributes.staminaExp
    case 'Meridians':
      return state.coreAttributes.meridiansExp
    case 'Attack':
      return state.coreAttributes.attackExp
    case 'Bone':
      return state.coreAttributes.boneExp
    case 'Agility':
      return state.coreAttributes.agilityExp
    case 'Consciousness':
      return state.coreAttributes.consciousnessExp
  }
}

export function addCoreAspectExp(state: PlayerState, aspect: CoreAspectKind, amount: number): void {
  switch (aspect) {
    case 'Stamina':
      state.coreAttributes.staminaExp += amount
      break
    case 'Meridians':
      state.coreAttributes.meridiansExp += amount
      break
    case 'Attack':
      state.coreAttributes.attackExp += amount
      break
    case 'Bone':
      state.coreAttributes.boneExp += amount
      break
    case 'Agility':
      state.coreAttributes.agilityExp += amount
      break
    case 'Consciousness':
      state.coreAttributes.consciousnessExp += amount
      break
  }
}

/** 对应 `HeavenGateRules.ResolveExpCapWithLinggen`。 */
export function resolveExpCapWithLinggen(
  state: PlayerState,
  catalog: GameDataCatalog,
): number {
  const realmProg = catalog.realmProgression
  if (!realmProg) return Number.POSITIVE_INFINITY
  const baseCap = realmProg.upgradeExpCapFor(state.realmLevel)
  if (state.realmLevel < 1 || state.realmLevel > realmProg.maxLevel) return 0
  if (!state.heavenGateEntered || baseCap <= 0) return baseCap
  const effective = state.effectiveLinggen
  if (linggenEffectiveCount(effective) <= 0) return baseCap
  const raw = baseCap * resolveLinggenCultivationMultiplier(effective, true)
  return roundToEven(raw)
}

/** 对应 `PlayerProgressionState.ComputeExperienceCredit`。 */
export function computeExperienceCredit(
  amplifiedAmount: number,
  cap: number,
  experience: number,
  foundation: number,
): number {
  return Math.min(
    3.0 * amplifiedAmount,
    Math.min(amplifiedAmount + Math.max(0.0, foundation), Math.max(0, cap - experience)),
  )
}

/** 模拟内完整玩家运行时聚合（单人与组队成员推进共用）。 */
export class SimPlayerRuntime {
  readonly state: PlayerState
  readonly learnedAbilities: Set<string>
  readonly actions: SimActionState[] = []
  report: SimBattleReport | null = null
  stateVersion = 1
  attributeInputRevision = 1
  isParticipationSessionContainer = false
  statsSink: ((delta: PlayerStatsDelta, atUnixSeconds: number) => void) | null = null

  private _cachedAttributes: PlayerAttributes | null = null
  private _cachedAttrRev = -1

  constructor(state: PlayerState, learnedAbilities?: Set<string>) {
    this.state = state
    if (learnedAbilities) {
      this.learnedAbilities = new Set(learnedAbilities)
    } else {
      const seen = new Set<string>()
      for (const slot of state.abilitySlots) {
        if (slot.abilityId) seen.add(slot.abilityId)
      }
      for (const id of Object.keys(state.abilityExperiences)) {
        seen.add(id)
      }
      this.learnedAbilities = seen
    }
  }

  get playerId(): string {
    return this.state.playerId
  }

  get displayName(): string {
    return this.state.displayName
  }

  get lastSimulatedAtTicks(): bigint {
    return this.state.lastSimulatedAtTicks
  }

  set lastSimulatedAtTicks(ticks: bigint) {
    this.state.lastSimulatedAtTicks = ticks
  }

  markDirty(section: PlayerSectionKind): void {
    if (
      section === PlayerSectionKind.Progression ||
      section === PlayerSectionKind.World ||
      section === PlayerSectionKind.Buffs ||
      section === PlayerSectionKind.Loadout
    ) {
      this.attributeInputRevision++
    }
  }

  resolveAttributes(catalog: GameDataCatalog): PlayerAttributes {
    if (this._cachedAttributes !== null && this._cachedAttrRev === this.attributeInputRevision) {
      return this._cachedAttributes
    }
    const attrs = resolvePlayerAttributesFromState(this.state, catalog)
    this._cachedAttributes = attrs
    this._cachedAttrRev = this.attributeInputRevision
    return attrs
  }

  resolveAttributesForCombat(
    catalog: GameDataCatalog,
    overrideTraits?: string[] | null,
    overrideTraitLevels?: Record<string, number> | null,
  ): CombatantStats {
    if (overrideTraits === undefined && overrideTraitLevels === undefined) {
      return playerAttributesToCombatantStats(
        this.resolveAttributes(catalog),
        this.state.combatExperience,
      )
    }
    const attrs = resolvePlayerAttributesFromState(
      this.state,
      catalog,
      overrideTraits,
      overrideTraitLevels,
    )
    return playerAttributesToCombatantStats(attrs, this.state.combatExperience)
  }

  expireBuffs(atUnixSeconds: number): boolean {
    const changed = expireBuffs(this.state, atUnixSeconds)
    if (changed) this.markDirty(PlayerSectionKind.Buffs)
    return changed
  }

  syncDurableConsumableBuffs(catalog: GameDataCatalog, actionBarId: string | null): boolean {
    const changed = syncDurableConsumableBuffs(this.state, catalog, actionBarId)
    if (changed) this.markDirty(PlayerSectionKind.Buffs)
    return changed
  }

  applyResourceAttributes(catalog: GameDataCatalog): PlayerAttributes {
    const r = this.state
    const prevMaxHp = r.maxHitpoints
    const prevMaxMp = r.maxManapoints
    const prevAtk = r.attackPower
    const prevDef = r.defense
    const attrs = applyResourceAttributes(r, catalog)
    // 注意 C# 中只 markDirty Combat（不递增 AttributeInputRevision）
    void (
      prevMaxHp !== r.maxHitpoints ||
      prevMaxMp !== r.maxManapoints ||
      prevAtk !== r.attackPower ||
      prevDef !== r.defense
    )
    const head = this.actions[0]
    if (
      head &&
      head.participation !== null &&
      !this.isParticipationSessionContainer &&
      head.expedition
    ) {
      // 会话投影保持成员资源不被外层实时上限钳制
    }
    return attrs
  }

  ensureActionReport(actionInstanceId: string, actionId: string, actionName: string): void {
    if (this.report && this.report.actionInstanceId === actionInstanceId) return
    this.report = new SimBattleReport(actionInstanceId, actionId, actionName)
  }

  recordStats(atUnixSeconds: number, delta: PlayerStatsDelta): void {
    if (this.statsSink) {
      this.statsSink(delta, atUnixSeconds)
      return
    }
    this.report?.applyDelta(delta)
  }

  creditExperience(
    amplifiedAmount: number,
    cap: number,
    combatExperience = 0,
  ): number {
    const credited = computeExperienceCredit(
      amplifiedAmount,
      cap,
      this.state.experience,
      this.state.foundation,
    )
    this.state.experience += credited
    this.state.foundation = Math.max(0.0, this.state.foundation + amplifiedAmount - credited)
    this.state.combatExperience += combatExperience
    this.state.lifetimeExperience += amplifiedAmount
    return credited
  }

  grantRootExperience(
    aspect: CoreAspectKind,
    amount: number,
    _catalog: GameDataCatalog,
    atUnixSeconds: number,
  ): void {
    if (amount <= 0) return
    addCoreAspectExp(this.state, aspect, amount)
    this.recordStats(atUnixSeconds, {
      rootAspect: aspect,
      rootExperience: amount,
    })
    this.markDirty(PlayerSectionKind.Loadout)
  }

  grantAbilityUseExperience(abilityId: string, atUnixSeconds: number): void {
    if (!abilityId || !this.learnedAbilities.has(abilityId)) return
    const prevExp = this.state.abilityExperiences[abilityId] ?? 0
    const nextExp = prevExp + 1
    this.state.abilityExperiences[abilityId] = nextExp
    const levelBefore = abilityLevelForExp(prevExp)
    const levelAfter = abilityLevelForExp(nextExp)
    if (levelAfter > levelBefore) {
      for (let i = 0; i < this.state.abilitySlots.length; i++) {
        const slot = this.state.abilitySlots[i]
        if (slot.abilityId === abilityId && slot.level < levelAfter) {
          this.state.abilitySlots[i] = { abilityId: slot.abilityId, level: levelAfter }
        }
      }
    }
    this.recordStats(atUnixSeconds, {
      abilityId,
      abilityExperience: 1,
    })
    this.markDirty(PlayerSectionKind.Loadout)
  }

  addCombatEvent(
    events: SimEventTail | null,
    fromTicks: bigint,
    targetTicks: bigint,
    remainingSeconds: number,
    code: string,
    message: string,
    actionId: string | null,
    itemDeltas: Record<string, number> | null = null,
  ): void {
    if (!events) return
    const totalSec = Number(targetTicks - fromTicks) / 10_000_000
    const elapsed = Math.max(0, totalSec - remainingSeconds)
    events.add({
      atTicks: addSecondsExactTicks(fromTicks, elapsed),
      code,
      message,
      actionId,
      itemDeltas,
    })
  }
}

export function createSimPlayerRuntime(state: PlayerState): SimPlayerRuntime {
  return new SimPlayerRuntime(state)
}

export function buildStartTicks(startAtUnixSeconds: number): bigint {
  return ticksFromUnixSeconds(startAtUnixSeconds)
}

/** 对应 C# `string.Format("{0:0.##}", v)` / `string.Format("{0:0.######}", v)`（含 -0 归零）。 */
export function formatTrimmedFixed(value: number, decimals: number): string {
  if (!Number.isFinite(value)) return String(value)
  const scale = decimals === 2 ? 100 : 1_000_000
  const scaled = roundToEven(value * scale)
  const rounded = scaled === 0 ? 0 : scaled / scale
  return rounded
    .toFixed(decimals)
    .replace(/(\.\d*?[1-9])0+$/, '$1')
    .replace(/\.0+$/, '')
}

export function combatSkillsToDto(skills: CombatSkillStateModel): SimulatorCombatSkillsDto {
  return {
    cooldowns: skills.cooldowns.map((c) => ({
      abilityId: c.abilityId,
      remainingSeconds: c.remainingSeconds,
    })),
    castingAbilityId: skills.castingAbilityId,
    castRemainingSeconds: skills.castRemainingSeconds,
    castCount: skills.castCount,
  }
}

export function buildSortedAssetDeltasDto(
  deltas: ReadonlyMap<string, number>,
): SimulatorAssetDto[] {
  const valid: Array<{ itemId: string; variant: string; quantity: number }> = []
  for (const [key, value] of deltas) {
    if (value !== 0) {
      const { itemId, variant } = splitAssetKey(key)
      valid.push({ itemId, variant, quantity: value })
    }
  }
  return valid
}

export function sumActiveBuffStacks(
  actives: readonly ActiveBuffState[],
  buffId: string | null,
): number {
  let total = 0
  for (let i = 0; i < actives.length; i++) {
    if (buffId === null || actives[i].buffId === buffId) {
      total += actives[i].stacks
    }
  }
  return total
}

/**
 * 战外 Buff 层数求值（去重并可选过期过滤）：跳过已存在于战内单位 Buff 列表中的同名即时药水副本，
 * 并在提供当前墙钟时忽略已到期的战外条目，防止 buff-stacks 在战内被双倍计入或到期后残留。
 */
export function sumUnmergedActiveBuffStacks(
  inBattle: readonly { buffId: string }[],
  actives: readonly ActiveBuffState[],
  buffId: string | null,
  nowUnix?: number,
): number {
  let total = 0
  for (let i = 0; i < actives.length; i++) {
    const entry = actives[i]
    const entryId = entry.buffId
    if (buffId !== null && entryId !== buffId) continue
    if (nowUnix !== undefined && entry.expiresAtUnixSeconds <= nowUnix) continue
    let duplicated = false
    for (let j = 0; j < inBattle.length; j++) {
      const curId = inBattle[j].buffId
      if (
        curId === entryId ||
        (curId.length > entryId.length &&
          curId.charCodeAt(entryId.length) === 58 /* ':' */ &&
          curId.startsWith(entryId))
      ) {
        duplicated = true
        break
      }
    }
    if (!duplicated) total += entry.stacks
  }
  return total
}

export function sumFrozenBuffStacks(
  frozen: Record<string, number> | null | undefined,
  buffId: string | null,
): number {
  if (!frozen) return 0
  if (buffId !== null) return frozen[buffId] ?? 0
  let total = 0
  for (const k of Object.keys(frozen)) {
    total += frozen[k]
  }
  return total
}

export function addAssetDeltaMap(
  deltas: Map<string, number>,
  itemId: string,
  amount: number,
  variant = '',
): void {
  if (amount === 0) return
  const key = assetKeyString(itemId, variant)
  deltas.set(key, (deltas.get(key) ?? 0) + amount)
}
