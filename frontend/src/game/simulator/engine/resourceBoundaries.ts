// 资源时钟自然窗口、即时消耗品（药品/雷击符）、持久增益同步、自然回复与战态镜像捕获
// 严格对应 DaoHua.Ecs.Systems.ActionExecutionRules.Resources

import { playerAttributesToCombatantStats } from './attributeResolver.ts'
import type { GameDataCatalog } from './catalog.ts'
import {
  applyCombatDamage,
  applyRanhun,
  resolveStrikeDamage,
} from './combatEngine.ts'
import {
  CombatEncounterState,
  CombatOutcome,
  CombatantState,
  scaleMonsterStats,
  type CombatBuffState,
} from './combatModels.ts'
import { TICKS_PER_SECOND, ticksToUnixSeconds } from './math.ts'
import {
  BuffEffectChannel,
  CombatDamageKind,
  CombatElement,
  PercentCurveMode,
  parseBuffEffects,
  percentMultiplier,
  weakenMonsterStats,
  type CombatantStats,
} from './rules.ts'
import {
  ActionQueueStatus,
  AssetWallet,
  CombatExpeditionPhase,
  ConsumableScheduleMode,
  PlayerSectionKind,
  SimEventTail,
  SimPlayerRuntime,
  advanceIdleCombatSkills,
  assetKeyString,
  combatBuffsFromCombatant,
  combatSkillsFromCombatant,
  formatTrimmedFixed,
  sumUnmergedActiveBuffStacks,
  type CombatSkillStateModel,
  type ExpeditionAllyState,
  type ExpeditionEnemyState,
  type SimActionState,
  type SimCombatExpeditionState,
} from './simulationState.ts'
import { evaluateTriggers, resolveConsumableTriggers } from './triggerEvaluator.ts'
import {
  BattleFact,
  EMPTY_WAKE_MASK,
  UnitChannel,
  resolveTriggerListWakeMask,
  unionWakeMask,
  wakeMaskWakesOn,
  type CombatFactDelta,
} from './triggerWake.ts'

export interface LiveCombatBattleEntry {
  action: SimActionState
  battle: CombatEncounterState
  waveNumber: number
  attributeRevision: number
}

/**
 * 外层推进循环内跨步复用的进行中战斗会话（对应 `SimulationEngine.LiveCombatBattle`）。
 */
export class LiveCombatBattle {
  current: LiveCombatBattleEntry | null = null

  private static matches(
    entry: LiveCombatBattleEntry,
    action: SimActionState,
    expedition: SimCombatExpeditionState | null,
  ): boolean {
    return (
      entry.action === action &&
      expedition !== null &&
      expedition.phase === CombatExpeditionPhase.Combat &&
      expedition.encounterId === entry.battle.encounterId &&
      expedition.seed === entry.battle.seed &&
      expedition.waveNumber === entry.waveNumber &&
      expedition.battleElapsedSeconds === entry.battle.elapsedSeconds
    )
  }

  tryReuse(
    action: SimActionState,
    expedition: SimCombatExpeditionState | null,
  ): LiveCombatBattleEntry | null {
    const cur = this.current
    if (cur && LiveCombatBattle.matches(cur, action, expedition)) {
      return cur
    }
    return null
  }

  adopt(
    action: SimActionState,
    battle: CombatEncounterState,
    expedition: SimCombatExpeditionState,
    attributeRevision: number,
  ): void {
    this.flush()
    this.current = {
      action,
      battle,
      waveNumber: expedition.waveNumber,
      attributeRevision,
    }
  }

  release(action: SimActionState): void {
    if (this.current && this.current.action === action) {
      this.current = null
    }
  }

  flush(): void {
    const cur = this.current
    if (!cur) return
    const doc = cur.action.expedition
    if (!LiveCombatBattle.matches(cur, cur.action, doc)) return
    const exported = captureCombatState(cur.battle, doc!)
    const playerState = exported.playerState
      ? {
          ...exported.playerState,
          hitpoints: doc!.playerHitpoints,
          manapoints: doc!.playerManapoints,
        }
      : null
    cur.action.expedition = {
      ...exported,
      playerHitpoints: doc!.playerHitpoints,
      playerManapoints: doc!.playerManapoints,
      playerState,
    }
  }
}

export function pendingActionText(unit: CombatantState): string | null {
  if (unit.pendingSkillIndex >= 0 && unit.pendingSkillIndex < unit.skills.length) {
    const id = unit.skills[unit.pendingSkillIndex].ability.id
    return typeof id === 'string' ? id : null
  }
  return null
}

export function pendingActionDurationOf(unit: CombatantState): number {
  if (unit.pendingSkillIndex >= 0 && unit.pendingSkillIndex < unit.skills.length) {
    const raw = unit.skills[unit.pendingSkillIndex].ability.castMilliseconds
    const castMs = typeof raw === 'number' && Number.isFinite(raw) ? raw : 0
    return (castMs / 1000) * percentMultiplier(unit.stats.actionSpeedPoints, PercentCurveMode.NonLinear)
  }
  return (
    unit.stats.attackIntervalSeconds *
    percentMultiplier(unit.stats.attackSpeedPoints, PercentCurveMode.NonLinear) *
    percentMultiplier(unit.stats.actionSpeedPoints, PercentCurveMode.NonLinear)
  )
}

export function carryAllies(battle: CombatEncounterState): ExpeditionAllyState[] {
  const allies = battle.playerAllies
  const list: ExpeditionAllyState[] = []
  for (let i = 0; i < allies.length; i++) {
    if (i === battle.selfIndex) continue
    const ally = allies[i]
    list.push({
      memberIndex: i,
      state: CombatEncounterState.snapshot(ally),
      pendingAction: pendingActionText(ally),
      pendingActionDurationSeconds: pendingActionDurationOf(ally),
      combatSkills: combatSkillsFromCombatant(ally, battle.elapsedSeconds),
      combatBuffs: combatBuffsFromCombatant(ally, battle.elapsedSeconds),
    })
  }
  return list
}

export function carryAlliesToSearch(battle: CombatEncounterState): ExpeditionAllyState[] {
  const allies = battle.playerAllies
  const list: ExpeditionAllyState[] = []
  for (let i = 0; i < allies.length; i++) {
    if (i === battle.selfIndex) continue
    const ally = allies[i]
    let snapshot = CombatEncounterState.snapshot(ally)
    if (!ally.isAlive && Number.isFinite(ally.respawnAtSeconds)) {
      const remaining = Math.max(0, ally.respawnAtSeconds - battle.elapsedSeconds)
      snapshot = { ...snapshot, respawnAtSeconds: remaining }
    }
    list.push({
      memberIndex: i,
      state: snapshot,
      pendingAction: pendingActionText(ally),
      pendingActionDurationSeconds: pendingActionDurationOf(ally),
      combatSkills: combatSkillsFromCombatant(ally, battle.elapsedSeconds),
      combatBuffs: combatBuffsFromCombatant(ally, battle.elapsedSeconds),
    })
  }
  return list
}

export function captureCombatState(
  battle: CombatEncounterState,
  template: SimCombatExpeditionState,
): SimCombatExpeditionState {
  const enemies: ExpeditionEnemyState[] = new Array(battle.enemies.length)
  for (let i = 0; i < battle.enemies.length; i++) {
    const enemy = battle.enemies[i]
    enemies[i] = {
      enemyId: enemy.definitionId ?? enemy.id,
      state: CombatEncounterState.snapshot(enemy),
      pendingAction: pendingActionText(enemy),
      pendingActionDurationSeconds: pendingActionDurationOf(enemy),
    }
  }
  return {
    ...template,
    battleElapsedSeconds: battle.elapsedSeconds,
    playerHitpoints: battle.player.hitpoints,
    playerManapoints: battle.player.manapoints,
    playerNextAttackAtSeconds: battle.player.nextAttackAtSeconds,
    playerAttackCount: battle.player.attackCount,
    playerThreat: battle.player.threat,
    outcome: battle.outcome,
    recentEvents: template.recentEvents,
    playerRespawnAtSeconds: Number.isFinite(battle.playerRespawnAtSeconds)
      ? battle.playerRespawnAtSeconds
      : null,
    playerState: CombatEncounterState.snapshot(battle.player),
    selfIndex: battle.selfIndex,
    allies: battle.playerAllies.length > 1 ? carryAllies(battle) : null,
    enemies,
  }
}

export interface ResourceBoundaryPlan {
  stepSeconds: number
  isNaturalBoundary: boolean
  changed: boolean
}

export interface AssetDeltaEntry {
  itemId: string
  variant: string
  quantityDelta: number
}

/** 获取当前队首行动所属自动使用栏位域（模拟器中只运行讨伐遭遇行动 -> 'combat'）。 */
function getHeadActionBarId(player: SimPlayerRuntime, catalog: GameDataCatalog): string | null {
  if (player.actions.length === 0) return null
  const def = catalog.actions.get(player.actions[0].actionId)
  if (!def) return null
  return 'combat'
}

/** 对应 `ActionExecutionRules.ApplyResourceAttributes`。 */
export function applyResourceAttributesRuntime(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
) {
  const attrs = player.resolveAttributes(catalog)
  const r = player.state
  r.maxHitpoints = attrs.maxHitpoints
  r.maxManapoints = attrs.maxManapoints
  r.attackPower = attrs.physicalAttack
  r.defense = attrs.armor
  const head = player.actions[0]
  if (head && head.participation !== null && !player.isParticipationSessionContainer) {
    return attrs
  }
  const spec = head?.participation?.spec
  const selfIdx = head?.expedition?.selfIndex ?? -1
  const sessionMember =
    spec && selfIdx >= 0 && selfIdx < spec.members.length ? spec.members[selfIdx] : null
  const capHp = sessionMember ? sessionMember.stats.maxHitpoints : r.maxHitpoints
  const capMp = sessionMember ? sessionMember.stats.maxManapoints : r.maxManapoints
  if (r.hitpoints > capHp) r.hitpoints = capHp
  if (r.manapoints > capMp) r.manapoints = capMp
  return attrs
}

/** 同步耐久消耗品造成的 `player.state.wallet` 查询（兼容 SimWallet 与 PlayerState.wallet）。 */
export function syncWalletToStateMap(state: SimPlayerRuntime['state'], wallet: AssetWallet): void {
  state.wallet = wallet.balances
}

/** 对应 `ActionExecutionRules.PrepareResourceBoundaryCore`。 */
export function prepareResourceBoundaryCore(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  wallet: AssetWallet,
  computeTargetTicks: bigint,
  events: SimEventTail,
  live: LiveCombatBattle | null,
  consumableSchedule: ConsumableScheduleMode = ConsumableScheduleMode.EventDriven,
): { plan: ResourceBoundaryPlan; instantDeltas: AssetDeltaEntry[] } {
  const nowTicks = player.lastSimulatedAtTicks
  const nowUnix = ticksToUnixSeconds(nowTicks)
  let nextBuffExpiry = Number.POSITIVE_INFINITY
  let changed = false
  const activeBuffs = player.state.buffs
  if (activeBuffs.length > 0) {
    for (let i = 0; i < activeBuffs.length; i++) {
      if (activeBuffs[i].expiresAtUnixSeconds < nextBuffExpiry) {
        nextBuffExpiry = activeBuffs[i].expiresAtUnixSeconds
      }
    }
    if (nextBuffExpiry <= nowUnix) {
      if (player.expireBuffs(nowUnix)) changed = true
      nextBuffExpiry = Number.POSITIVE_INFINITY
      for (let i = 0; i < activeBuffs.length; i++) {
        if (activeBuffs[i].expiresAtUnixSeconds < nextBuffExpiry) {
          nextBuffExpiry = activeBuffs[i].expiresAtUnixSeconds
        }
      }
    }
  }
  syncWalletToStateMap(player.state, wallet)
  const barId = getHeadActionBarId(player, catalog)
  if (player.syncDurableConsumableBuffs(catalog, barId)) changed = true
  applyResourceAttributesRuntime(player, catalog)

  const instantDeltas =
    consumableSchedule === ConsumableScheduleMode.EventDriven
      ? []
      : activateInstantConsumables(player, catalog, wallet, nowTicks, events, live)
  if (instantDeltas.length > 0) changed = true

  const waitsForSession =
    player.actions.length > 0 &&
    player.actions[0].participation !== null &&
    !player.isParticipationSessionContainer
  let naturalStep = waitsForSession
    ? Number.POSITIVE_INFINITY
    : 5 - player.state.regenCarrySeconds
  if (Number.isFinite(nextBuffExpiry)) {
    naturalStep = Math.min(naturalStep, nextBuffExpiry - nowUnix)
  }
  const totalSec = Number(computeTargetTicks - nowTicks) / Number(TICKS_PER_SECOND)
  let step = Math.min(totalSec, naturalStep)
  const slots = player.state.autoUseSlots
  for (let i = 0; i < slots.length; i++) {
    const slot = slots[i]
    if (!slot.enabled) continue
    const untilCooldown = slot.cooldownUntilUnixSeconds - nowUnix
    if (untilCooldown > 1e-9) {
      naturalStep = Math.min(naturalStep, untilCooldown)
      step = Math.min(step, untilCooldown)
    }
  }
  const participation = player.actions[0]?.participation
  if (participation && Number.isFinite(participation.endsAtUnixSeconds)) {
    const untilSessionEnd = participation.endsAtUnixSeconds - nowUnix
    if (untilSessionEnd > 1e-9) {
      naturalStep = Math.min(naturalStep, untilSessionEnd)
      step = Math.min(step, untilSessionEnd)
    }
  }
  return {
    plan: {
      stepSeconds: step,
      isNaturalBoundary: naturalStep <= step + 1e-9,
      changed,
    },
    instantDeltas,
  }
}

/** 对应 `ActionExecutionRules.AdvanceIdleSkillResources`。 */
export function advanceIdleSkillResources(
  player: SimPlayerRuntime,
  stepSeconds: number,
): boolean {
  const head = player.actions[0]
  if (head && head.participation !== null && !player.isParticipationSessionContainer) {
    return false
  }
  const expedition = head?.expedition ?? null
  if (expedition?.phase === CombatExpeditionPhase.Combat) return false
  let changed = false
  if (expedition?.allies && expedition.allies.length > 0) {
    const allies = expedition.allies
    const updated: ExpeditionAllyState[] = new Array(allies.length)
    for (let i = 0; i < allies.length; i++) {
      const ally = allies[i]
      const allySkills: CombatSkillStateModel = ally.combatSkills
      if (
        allySkills.cooldowns.length > 0 ||
        allySkills.castingAbilityId !== null
      ) {
        updated[i] = {
          ...ally,
          combatSkills: advanceIdleCombatSkills(allySkills, stepSeconds),
        }
        changed = true
      } else {
        updated[i] = ally
      }
    }
    if (changed && head) {
      head.expedition = { ...expedition, allies: updated }
    }
  }
  const skills = player.state.combatSkills
  if (skills.cooldowns.length === 0 && skills.castingAbilityId === null) {
    return changed
  }
  player.state.combatSkills = advanceIdleCombatSkills(skills, stepSeconds)
  player.markDirty(PlayerSectionKind.Combat)
  return true
}

/** 对应 `ActionExecutionRules.CompleteResourceRegeneration`。 */
export function completeResourceRegeneration(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  stepSeconds: number,
  regenerateAllies = true,
): boolean {
  const head = player.actions[0]
  if (head && head.participation !== null && !player.isParticipationSessionContainer) {
    return false
  }
  player.state.regenCarrySeconds += stepSeconds
  if (player.state.regenCarrySeconds < 5 - 1e-7) return false
  player.state.regenCarrySeconds = 0

  const participationSpec = head?.participation?.spec ?? null
  const selfIdx = head?.expedition?.selfIndex ?? -1
  const participant =
    participationSpec && selfIdx >= 0 && selfIdx < participationSpec.members.length
      ? participationSpec.members[selfIdx]
      : null

  let maxHitpoints: number
  let maxManapoints: number
  let hpRegen: number
  let mpRegen: number
  if (participant) {
    maxHitpoints = participant.stats.maxHitpoints
    maxManapoints = participant.stats.maxManapoints
    hpRegen = participant.stats.hpRegen
    mpRegen = participant.stats.mpRegen
  } else {
    const attributes = applyResourceAttributesRuntime(player, catalog)
    maxHitpoints = attributes.maxHitpoints
    maxManapoints = attributes.maxManapoints
    hpRegen = attributes.hpRegen
    mpRegen = attributes.mpRegen
  }

  const hitpoints = player.state.hitpoints
  const manapoints = player.state.manapoints
  const statsBook =
    head?.expedition && player.report ? player.report.combatStats : null

  if (player.state.hitpoints > 0) {
    player.state.hitpoints = Math.min(maxHitpoints, player.state.hitpoints + hpRegen)
  }
  player.state.manapoints = Math.min(maxManapoints, player.state.manapoints + mpRegen)
  if (player.state.hitpoints > hitpoints && statsBook) {
    statsBook.recordHealingKey('player', player.displayName, true, player.state.hitpoints - hitpoints)
  }
  if (
    head?.expedition &&
    (head.expedition.playerHitpoints !== player.state.hitpoints ||
      head.expedition.playerManapoints !== player.state.manapoints)
  ) {
    head.expedition = {
      ...head.expedition,
      playerHitpoints: player.state.hitpoints,
      playerManapoints: player.state.manapoints,
    }
  }

  let anyAllyChange = false
  if (
    regenerateAllies &&
    participationSpec &&
    head?.expedition &&
    head.expedition.phase !== CombatExpeditionPhase.Combat &&
    head.expedition.allies &&
    head.expedition.allies.length > 0
  ) {
    const regenAllies = head.expedition.allies
    const updated: ExpeditionAllyState[] = new Array(regenAllies.length)
    for (let i = 0; i < regenAllies.length; i++) {
      const ally = regenAllies[i]
      if (ally.memberIndex >= participationSpec.members.length) {
        updated[i] = ally
        continue
      }
      const member = participationSpec.members[ally.memberIndex]
      const memberStats = member.stats
      const allyHp =
        ally.state.hitpoints > 0
          ? Math.min(memberStats.maxHitpoints, ally.state.hitpoints + memberStats.hpRegen)
          : ally.state.hitpoints
      const allyMp = Math.min(memberStats.maxManapoints, ally.state.manapoints + memberStats.mpRegen)
      if (allyHp > ally.state.hitpoints && statsBook) {
        statsBook.recordHealingKey(
          `ally:${ally.memberIndex}`,
          member.displayName,
          true,
          allyHp - ally.state.hitpoints,
        )
      }
      if (allyHp !== ally.state.hitpoints || allyMp !== ally.state.manapoints) {
        updated[i] = {
          ...ally,
          state: { ...ally.state, hitpoints: allyHp, manapoints: allyMp },
        }
        anyAllyChange = true
      } else {
        updated[i] = ally
      }
    }
    if (anyAllyChange && head.expedition) {
      head.expedition = { ...head.expedition, allies: updated }
    }
  }
  if (player.state.hitpoints === hitpoints && player.state.manapoints === manapoints) {
    return anyAllyChange
  }
  player.markDirty(PlayerSectionKind.Combat)
  return true
}

function syncLiveBattleToEvalDocs(player: SimPlayerRuntime, battle: CombatEncounterState): void {
  player.state.hitpoints = battle.player.hitpoints
  player.state.manapoints = battle.player.manapoints
  const head = player.actions[0]
  if (head?.expedition?.phase === CombatExpeditionPhase.Combat) {
    head.expedition = captureCombatState(battle, head.expedition)
  }
}

export function syncConsumableResourcesToBattle(
  player: SimPlayerRuntime,
  action: SimActionState,
  liveBattle: CombatEncounterState | null,
): void {
  if (liveBattle) {
    const caster = liveBattle.player
    caster.hitpoints = Math.max(0, Math.min(player.state.hitpoints, caster.stats.maxHitpoints))
    caster.manapoints = Math.max(0, Math.min(player.state.manapoints, caster.stats.maxManapoints))
    return
  }
  const snap = action.expedition
  if (!snap || snap.phase !== CombatExpeditionPhase.Combat) return
  const clampedHp = Math.max(0, Math.min(player.state.hitpoints, player.state.maxHitpoints))
  const clampedMp = Math.max(0, Math.min(player.state.manapoints, player.state.maxManapoints))
  action.expedition = {
    ...snap,
    playerHitpoints: clampedHp,
    playerManapoints: clampedMp,
    playerState: snap.playerState
      ? { ...snap.playerState, hitpoints: clampedHp, manapoints: clampedMp }
      : null,
  }
}

function buildConsumableEvalUnits(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  nowUnix?: number,
): {
  caster: CombatantState
  target: CombatantState | null
  allies: CombatantState[]
  enemies: CombatantState[]
} {
  const caster = new CombatantState(
    'player',
    playerAttributesToCombatantStats(player.resolveAttributes(catalog), player.state.combatExperience),
  )
  caster.hitpoints = player.state.hitpoints
  caster.manapoints = player.state.manapoints
  const activeBuffs = player.state.buffs
  caster.externalBuffStacks = (buffId) =>
    sumUnmergedActiveBuffStacks(caster.buffs, activeBuffs, buffId, nowUnix)
  const head = player.actions[0]
  const battleBuffs = head?.expedition?.playerState?.buffs
  if (battleBuffs) caster.replaceBuffs(battleBuffs)
  const allies = [caster]
  const enemies: CombatantState[] = []
  const weaken = player.state.nodeBuildingEffects.monsterWeakenRatio
  const difficulty = head?.participation?.spec?.difficulty ?? head?.difficulty ?? 0
  if (
    head?.expedition?.phase === CombatExpeditionPhase.Combat &&
    head.expedition.enemies &&
    head.expedition.enemies.length > 0
  ) {
    const waveEnemies = head.expedition.enemies
    for (let i = 0; i < waveEnemies.length; i++) {
      const enemy = waveEnemies[i]
      const monster = catalog.monsters.get(enemy.enemyId)
      if (!monster) continue
      const scaledStats = scaleMonsterStats(monster.stats, difficulty)
      const unit = new CombatantState(
        `enemy:${i}`,
        weakenMonsterStats(scaledStats, weaken),
      )
      unit.hitpoints = enemy.state.hitpoints
      unit.manapoints = enemy.state.manapoints
      unit.threat = enemy.state.threat
      unit.threatBonusPoints = enemy.state.threatBonusPoints
      unit.definitionId = enemy.enemyId
      unit.rank = monster.rank
      if (enemy.state.buffs && enemy.state.buffs.length > 0) {
        unit.replaceBuffs(enemy.state.buffs)
      }
      enemies.push(unit)
    }
  }
  let best: CombatantState | null = null
  for (let i = 0; i < enemies.length; i++) {
    if (enemies[i].isAlive && (best === null || enemies[i].effectiveThreat > best.effectiveThreat)) {
      best = enemies[i]
    }
  }
  return { caster, target: best, allies, enemies }
}

function applyStrikeDamageToBattle(
  action: SimActionState,
  targetIndex: number,
  damage: number,
  liveBattle: CombatEncounterState | null,
  attacker: CombatantState,
  target: CombatantState,
  element: number,
): number {
  if (liveBattle && targetIndex < liveBattle.enemies.length) {
    const unit = liveBattle.enemies[targetIndex]
    const settled = applyCombatDamage(liveBattle, liveBattle.player, unit, damage)
    applyRanhun(
      liveBattle.player,
      unit,
      element as CombatElement,
      settled.actual,
      liveBattle.elapsedSeconds,
    )
    if (!liveBattle.enemies.some((e) => e.isAlive)) {
      liveBattle.outcome = CombatOutcome.PlayerWon
    }
    return settled.damage
  }
  const snap = action.expedition
  if (!snap || snap.phase !== CombatExpeditionPhase.Combat || !snap.enemies || targetIndex >= snap.enemies.length) {
    return 0
  }
  const updated = snap.enemies.slice()
  const enemy = updated[targetIndex]
  const res = applyCombatDamage(null, attacker, target, damage)
  applyRanhun(attacker, target, element as CombatElement, res.actual, snap.battleElapsedSeconds)
  updated[targetIndex] = {
    ...enemy,
    state: {
      ...enemy.state,
      hitpoints: target.hitpoints,
      buffs: target.buffs.map((b) => ({ ...b })),
    },
  }
  let allDead = true
  for (let i = 0; i < updated.length; i++) {
    if (updated[i].state.hitpoints > 0) {
      allDead = false
      break
    }
  }
  action.expedition = {
    ...snap,
    enemies: updated,
    outcome: allDead ? CombatOutcome.PlayerWon : snap.outcome,
  }
  return res.damage
}

/** 对应 `ActionExecutionRules.ActivateInstantConsumables`。 */
export function activateInstantConsumables(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  wallet: AssetWallet,
  atTicks: bigint,
  events: SimEventTail,
  live: LiveCombatBattle | null = null,
  liveState: CombatEncounterState | null = null,
  pendingSpends: ReadonlyMap<string, number> | null = null,
  wakeDelta: CombatFactDelta | null = null,
): AssetDeltaEntry[] {
  if (player.actions.length === 0 || player.state.hitpoints <= 0) return []
  const head = player.actions[0]
  if (head.participation !== null) return []

  const slots = player.state.autoUseSlots
  let anyEnabled = false
  for (let i = 0; i < slots.length; i++) {
    if (slots[i].enabled) {
      anyEnabled = true
      break
    }
  }
  if (!anyEnabled) return []

  let liveBattle: CombatEncounterState | null = liveState
  if (!liveBattle && live && head.expedition) {
    const entry = live.tryReuse(head, head.expedition)
    if (entry) liveBattle = entry.battle
  }
  const actionBarId = getHeadActionBarId(player, catalog)
  const deltas: AssetDeltaEntry[] = []
  const now = ticksToUnixSeconds(atTicks)

  let evalCaster: CombatantState | null = null
  let evalTarget: CombatantState | null = null
  let evalAllies: CombatantState[] | null = null
  let evalEnemies: CombatantState[] | null = null

  const ensureEvalContext = () => {
    if (evalCaster !== null) return
    if (liveBattle) syncLiveBattleToEvalDocs(player, liveBattle)
    else live?.flush()
    const ctx = buildConsumableEvalUnits(player, catalog, now)
    evalCaster = ctx.caster
    evalTarget = ctx.target
    evalAllies = ctx.allies
    evalEnemies = ctx.enemies
  }

  for (let i = 0; i < slots.length; i++) {
    const slot = slots[i]
    if (
      !slot.enabled ||
      slot.itemId === null ||
      actionBarId === null ||
      slot.actionType.toLowerCase() !== actionBarId.toLowerCase() ||
      slot.cooldownUntilUnixSeconds > now
    ) {
      continue
    }
    const item = catalog.items.get(slot.itemId)
    if (!item || !item.consumable || item.consumable.isContinuous || item.consumable.durable) {
      continue
    }
    const definition = item.consumable
    if (!definition.usableInBars.has(actionBarId)) continue
    const rawItem = catalog.rawItems.get(item.id)
    const rawCons = rawItem?.consumable as Record<string, unknown> | undefined
    if (!rawCons) continue

    const triggers = resolveConsumableTriggers(
      item.id,
      definition,
      player.state.consumableTriggers,
    )
    if (wakeDelta !== null) {
      const baseMask = resolveTriggerListWakeMask(triggers, false)
      const withStrike =
        definition.strike !== null
          ? unionWakeMask(baseMask, { ...EMPTY_WAKE_MASK, self: UnitChannel.Mana })
          : baseMask
      const mask = unionWakeMask(withStrike, { ...EMPTY_WAKE_MASK, facts: BattleFact.External })
      const selfIdx = liveBattle?.selfIndex ?? 0
      if (!wakeMaskWakesOn(mask, wakeDelta, selfIdx)) continue
    }

    if (triggers.length > 0) {
      ensureEvalContext()
      const elapsedSec = head.expedition?.battleElapsedSeconds ?? 0
      if (!evaluateTriggers(triggers, evalCaster!, evalTarget, evalAllies!, evalEnemies!, elapsedSec)) {
        continue
      }
    }

    let consumed = 0
    for (let j = 0; j < deltas.length; j++) {
      if (deltas[j].itemId === item.id && deltas[j].variant === '') {
        consumed += deltas[j].quantityDelta
      }
    }
    if (pendingSpends) {
      const pending = pendingSpends.get(assetKeyString(item.id, ''))
      if (pending !== undefined) consumed += pending
    }
    if (wallet.get(item.id, '') + consumed < 1) continue

    const hp = typeof rawCons.hitpointRestore === 'number' ? rawCons.hitpointRestore : 0
    const mp = typeof rawCons.manapointRestore === 'number' ? rawCons.manapointRestore : 0
    const rawBuffs = Array.isArray(rawCons.buffs)
      ? (rawCons.buffs as Record<string, unknown>[])
      : null
    const cooldown = definition.cooldownSeconds
    if (!rawBuffs && hp === 0 && mp === 0 && definition.strike === null) continue

    if (definition.strike !== null) {
      const strike = definition.strike
      ensureEvalContext()
      const manaCost = strike.manaCostRatio * evalCaster!.stats.maxManapoints
      if (evalTarget === null || player.state.manapoints < manaCost - 1e-9) continue
      const targetIndex = evalEnemies!.indexOf(evalTarget)
      if (targetIndex < 0) continue

      const attackPower =
        (strike.manaDamageRatio * evalCaster!.stats.maxManapoints +
          strike.spellAttackRatio * evalCaster!.stats.spellAttack) *
        percentMultiplier(evalCaster!.stats.skillBonusPoints, PercentCurveMode.Linear)
      const strikeStats: CombatantStats = {
        ...evalCaster!.stats,
        attackPower,
        damageKind: CombatDamageKind.Spell,
        element: strike.element as CombatElement,
        totalDamageBonusPoints:
          evalCaster!.stats.totalDamageBonusPoints +
          evalCaster!.buffTotal(BuffEffectChannel.DamageBonusPoints),
      }
      const rawDamage = resolveStrikeDamage(strikeStats, evalTarget)
      player.state.manapoints = Math.max(0, player.state.manapoints - manaCost)
      syncConsumableResourcesToBattle(player, head, liveBattle)
      const damage = applyStrikeDamageToBattle(
        head,
        targetIndex,
        rawDamage,
        liveBattle,
        evalCaster!,
        evalTarget,
        strike.element,
      )
      const strikeDefId = evalTarget!.definitionId
      if (strikeDefId && player.report) {
        const book = player.report.combatStats
        book.register('player', player.displayName)
        const strikeMonster = catalog.monsters.get(strikeDefId)
        book.register(`monster:${strikeDefId}`, strikeMonster ? strikeMonster.name : strikeDefId)
        book.recordHitKey(
          'player',
          `monster:${strikeDefId}`,
          CombatDamageKind.Spell,
          damage,
          Math.max(0, strikeStats.attackPower - damage),
          strike.element as CombatElement,
        )
      }
      slots[i] = { ...slot, cooldownUntilUnixSeconds: now + cooldown }
      player.markDirty(PlayerSectionKind.Settings)
      player.markDirty(PlayerSectionKind.Combat)
      player.markDirty(PlayerSectionKind.ActionQueue)
      deltas.push({ itemId: item.id, variant: '', quantityDelta: -1 })
      evalCaster = null
      player.recordStats(now, { lostItem: item.id, lostQuantity: 1 })
      events.add({
        atTicks,
        code: 'auto-use.activated',
        message: `自动使用 ${item.name}，造成 ${formatTrimmedFixed(damage, 2)} 点伤害。`,
        actionId: null,
        itemDeltas: { [item.id]: -1 },
      })
      continue
    }

    const hpBefore = player.state.hitpoints
    player.state.hitpoints = Math.min(player.state.maxHitpoints, player.state.hitpoints + hp)
    player.state.manapoints = Math.min(player.state.maxManapoints, player.state.manapoints + mp)
    if (player.state.hitpoints > hpBefore) {
      const def = catalog.actions.get(head.actionId)
      player.ensureActionReport(head.id, head.actionId, def ? def.name : head.actionId)
      if (player.report) {
        player.report.combatStats.recordHealingKey(
          'player',
          player.displayName,
          false,
          player.state.hitpoints - hpBefore,
        )
      }
    }
    syncConsumableResourcesToBattle(player, head, liveBattle)
    if (rawBuffs) {
      for (const buff of rawBuffs) {
        const buffId = String(buff.buffId)
        const duration = typeof buff.durationSeconds === 'number' ? buff.durationSeconds : 0
        const name = typeof buff.name === 'string' ? buff.name : buffId
        for (let k = player.state.buffs.length - 1; k >= 0; k--) {
          if (player.state.buffs[k].buffId === buffId) player.state.buffs.splice(k, 1)
        }
        player.state.buffs.push({
          buffId,
          expiresAtUnixSeconds: now + duration,
          stacks: 1,
          name,
          sourceItemId: item.id,
          effects: parseBuffEffects(buff, 1.0),
          statRatios: null,
        })
      }
      if (head.expedition?.phase === CombatExpeditionPhase.Combat) {
        const battleExp = head.expedition
        const battleBuffs: CombatBuffState[] = []
        for (const buff of rawBuffs) {
          const duration = typeof buff.durationSeconds === 'number' ? buff.durationSeconds : 0
          if (duration <= 0) continue
          const buffId = String(buff.buffId)
          const name = typeof buff.name === 'string' ? buff.name : buffId
          battleBuffs.push({
            buffId,
            expiresAtSeconds: battleExp.battleElapsedSeconds + duration,
            stacks: 1,
            name,
            resetOnWave: true,
            missingHitpointsPerStack: 0,
            effects: parseBuffEffects(buff, 1.0),
            statRatios: null,
            sourceUnitId: null,
          })
        }
        if (battleBuffs.length > 0) {
          if (liveBattle) {
            const liveUnit = liveBattle.player
            for (const bb of battleBuffs) {
              liveUnit.setBuff({
                ...bb,
                expiresAtSeconds:
                  liveBattle.elapsedSeconds + (bb.expiresAtSeconds - battleExp.battleElapsedSeconds),
              })
            }
          }
          if (head.expedition.playerState) {
            const ps = head.expedition.playerState
            const merged = ps.buffs && ps.buffs.length > 0 ? ps.buffs.slice() : []
            for (const bb of battleBuffs) {
              for (let k = merged.length - 1; k >= 0; k--) {
                if (merged[k].buffId === bb.buffId) merged.splice(k, 1)
              }
              merged.push(bb)
            }
            head.expedition = {
              ...head.expedition,
              playerState: { ...ps, buffs: merged },
            }
            player.markDirty(PlayerSectionKind.ActionQueue)
          }
        }
      }
    }
    slots[i] = { ...slot, cooldownUntilUnixSeconds: now + cooldown }
    player.markDirty(PlayerSectionKind.Settings)
    player.markDirty(PlayerSectionKind.Buffs)
    player.markDirty(PlayerSectionKind.Combat)
    deltas.push({ itemId: item.id, variant: '', quantityDelta: -1 })
    evalCaster = null
    player.recordStats(now, { lostItem: item.id, lostQuantity: 1 })
    events.add({
      atTicks,
      code: 'auto-use.activated',
      message: `自动使用 ${item.name}。`,
      actionId: null,
      itemDeltas: { [item.id]: -1 },
    })
  }
  return deltas
}

/** 对应 `ActionExecutionRules.AdvanceBlockedAction` & `TryStartActionCore`（讨伐专用准入）。 */
export function tryStartActionCore(
  player: SimPlayerRuntime,
  action: SimActionState,
  catalog: GameDataCatalog,
  _wallet: AssetWallet,
  atTicks: bigint,
  _deltas: Map<string, number> | null,
  events: SimEventTail,
): boolean {
  const def = catalog.actions.get(action.actionId)
  if (!def) return false
  const nodeId = player.state.worldNodeId
  const availableHere =
    (!def.allowedNodeIds || def.allowedNodeIds.size === 0 || def.allowedNodeIds.has(nodeId)) &&
    (!def.excludedNodeIds || def.excludedNodeIds.size === 0 || !def.excludedNodeIds.has(nodeId))
  if (!availableHere) {
    if (action.status !== ActionQueueStatus.Blocked) {
      action.status = ActionQueueStatus.Blocked
      events.add({
        atTicks,
        code: 'action.blocked',
        message: '条件不足，行动受阻。',
        actionId: def.id,
        itemDeltas: null,
      })
    }
    return false
  }
  player.ensureActionReport(action.id, def.id, def.name)
  action.status = ActionQueueStatus.Running
  return true
}

export function advanceBlockedAction(
  player: SimPlayerRuntime,
  action: SimActionState,
  catalog: GameDataCatalog,
  wallet: AssetWallet,
  atTicks: bigint,
  deltas: Map<string, number> | null,
  events: SimEventTail,
): boolean {
  const def = catalog.actions.get(action.actionId)
  if (def && player.state.hitpoints <= 0) {
    player.state.hitpoints = 1
    player.markDirty(PlayerSectionKind.Combat)
  }
  if (tryStartActionCore(player, action, catalog, wallet, atTicks, deltas, events)) {
    return true
  }
  return false
}
