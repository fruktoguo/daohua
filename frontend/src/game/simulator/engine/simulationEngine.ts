// 单人确定性模拟主循环引擎（资源自然窗口 + 行动分派 + 复用战斗会话 + 事件驱动即时消耗品）
// 严格对应 DaoHua.Ecs.SimulationEngine

import type { GameDataCatalog } from './catalog.ts'
import {
  advanceCombatPreparationCore,
  completeCombatAdvanceCore,
  restoreCombatBattle,
} from './combatActionRules.ts'
import { CombatBoundaryHookDispatcher, advanceCombatTo } from './combatEngine.ts'
import { CombatOutcome } from './combatModels.ts'
import { addSecondsExactTicks, compareOrdinal, ticksDiffSeconds } from './math.ts'
import {
  LiveCombatBattle,
  activateInstantConsumables,
  advanceBlockedAction,
  advanceIdleSkillResources,
  completeResourceRegeneration,
  prepareResourceBoundaryCore,
  tryStartActionCore,
} from './resourceBoundaries.ts'
import {
  ActionQueueStatus,
  AssetWallet,
  CombatExpeditionPhase,
  ConsumableScheduleMode,
  PlayerSectionKind,
  SimEventTail,
  SimPlayerRuntime,
  addAssetDeltaMap,
  buildSortedAssetDeltasDto,
  splitAssetKey,
  type SimActionState,
  type SimEvent,
} from './simulationState.ts'
import type { CombatFactDelta } from './triggerWake.ts'
import type { SimulatorAssetDto } from './types.ts'

export const MAX_TRANSITIONS_PER_ADVANCE = 100_000

export interface AdvanceSimulationResult {
  fromTicks: bigint
  toTicks: bigint
  elapsedSeconds: number
  completedActions: number
  completedRepeats: number
  assetDeltas: SimulatorAssetDto[]
  events: readonly SimEvent[]
  stateVersion: number
  changed: boolean
  droppedEventCount: number
}

/** 对应 `SimulationEngine.AdvanceTo(player, target, gameData, assets, duel, consumableSchedule)`。 */
export function advanceSimulationTo(
  player: SimPlayerRuntime,
  targetTicks: bigint,
  catalog: GameDataCatalog,
  initialAssets: ReadonlyMap<string, number> | null,
  consumableSchedule: ConsumableScheduleMode = ConsumableScheduleMode.EventDriven,
): AdvanceSimulationResult {
  const fromTicks = player.lastSimulatedAtTicks
  if (targetTicks < fromTicks) {
    throw new Error('目标时间不能早于玩家上次模拟时间。')
  }
  const wallet = new AssetWallet(player.playerId, initialAssets ?? undefined)
  const deltas = new Map<string, number>()
  const events = new SimEventTail()
  let actions = 0
  let repeats = 0
  let changed = false
  const live = new LiveCombatBattle()

  const computeTargetTicks = targetTicks
  while (player.lastSimulatedAtTicks < computeTargetTicks) {
    const nowTicks = player.lastSimulatedAtTicks
    const { plan, instantDeltas } = prepareResourceBoundaryCore(
      player,
      catalog,
      wallet,
      computeTargetTicks,
      events,
      live,
      consumableSchedule,
    )
    if (plan.changed) changed = true
    for (let i = 0; i < instantDeltas.length; i++) {
      const d = instantDeltas[i]
      wallet.apply(d.itemId, d.quantityDelta, d.variant)
      addAssetDeltaMap(deltas, d.itemId, d.quantityDelta, d.variant)
    }
    const step = plan.stepSeconds
    const stepTargetTicks = addSecondsExactTicks(nowTicks, step)
    const coreTarget =
      stepTargetTicks > computeTargetTicks
        ? computeTargetTicks
        : stepTargetTicks <= nowTicks
          ? computeTargetTicks
          : stepTargetTicks
    const result = advanceToCore(
      player,
      coreTarget,
      catalog,
      wallet,
      live,
      consumableSchedule,
    )
    for (let i = 0; i < result.sortedDeltas.length; i++) {
      const [k, v] = result.sortedDeltas[i]
      wallet.applyKey(k, v)
      deltas.set(k, (deltas.get(k) ?? 0) + v)
    }
    events.merge(result.events.view, result.events.dropped)
    actions += result.completedActions
    repeats += result.completedRepeats
    if (result.changed) changed = true
    if (completeResourceRegeneration(player, catalog, step)) changed = true
  }
  live.flush()

  if (changed) player.stateVersion += 1
  return {
    fromTicks,
    toTicks: targetTicks,
    elapsedSeconds: ticksDiffSeconds(fromTicks, targetTicks),
    completedActions: actions,
    completedRepeats: repeats,
    assetDeltas: buildSortedAssetDeltasDto(deltas),
    events: events.view,
    stateVersion: player.stateVersion,
    changed,
    droppedEventCount: events.dropped,
  }
}

function advanceToCore(
  player: SimPlayerRuntime,
  targetTicks: bigint,
  catalog: GameDataCatalog,
  wallet: AssetWallet | null,
  live: LiveCombatBattle | null,
  consumableSchedule: ConsumableScheduleMode,
): {
  sortedDeltas: [string, number][]
  events: SimEventTail
  completedActions: number
  completedRepeats: number
  changed: boolean
} {
  const fromTicks = player.lastSimulatedAtTicks
  const totalSec = ticksDiffSeconds(fromTicks, targetTicks)
  const stateRef = {
    remainingSeconds: totalSec,
    completedActions: 0,
    completedRepeats: 0,
    idleSkillChanged: false,
  }
  let transitions = 0
  const deltas = new Map<string, number>()
  const events = new SimEventTail()
  const blockedActions = new Set<string>()

  while (stateRef.remainingSeconds > 1e-9 && player.actions.length > 0) {
    const action = player.actions[0]
    const currentAtTicks = addSecondsExactTicks(fromTicks, totalSec - stateRef.remainingSeconds)
    if (action.status === ActionQueueStatus.Blocked) {
      if (blockedActions.has(action.id)) break
      blockedActions.add(action.id)
      if (
        advanceBlockedAction(player, action, catalog, wallet ?? new AssetWallet(player.playerId), currentAtTicks, deltas, events)
      ) {
        if (++transitions > MAX_TRANSITIONS_PER_ADVANCE) {
          throw new Error('单次模拟超过最大状态转换数。')
        }
        continue
      }
      break
    }
    if (++transitions > MAX_TRANSITIONS_PER_ADVANCE) {
      throw new Error(
        `单次模拟超过最大状态转换数（action=${action.actionId} phase=${action.expedition?.phase} remaining=${stateRef.remainingSeconds}）。`,
      )
    }
    if (
      !tryStartActionCore(
        player,
        action,
        catalog,
        wallet ?? new AssetWallet(player.playerId),
        currentAtTicks,
        deltas,
        events,
      )
    ) {
      continue
    }
    blockedActions.clear()
    advanceCombatAction(
      player,
      action,
      catalog,
      wallet,
      fromTicks,
      targetTicks,
      stateRef,
      deltas,
      events,
      live,
      consumableSchedule,
    )
  }

  if (stateRef.remainingSeconds > 1e-9 && advanceIdleSkillResources(player, stateRef.remainingSeconds)) {
    stateRef.idleSkillChanged = true
  }

  player.state.ageSeconds += totalSec
  player.lastSimulatedAtTicks = targetTicks
  if (stateRef.remainingSeconds < 0) {
    throw new Error('模拟时间出现负余量。')
  }

  let headActivated = false
  if (player.actions.length > 0 && player.actions[0].status === ActionQueueStatus.Pending) {
    player.actions[0].status = ActionQueueStatus.Running
    headActivated = true
    if (transitions === 0) player.markDirty(PlayerSectionKind.ActionQueue)
  }
  if (transitions > 0 || headActivated) {
    player.stateVersion += 1
    if (transitions > 0) {
      player.markDirty(PlayerSectionKind.Progression)
      player.markDirty(PlayerSectionKind.ActionQueue)
    }
  }

  const sortedDeltas: [string, number][] = []
  for (const [k, v] of deltas) {
    if (Math.abs(v) > 1e-12) sortedDeltas.push([k, v])
  }
  if (sortedDeltas.length > 1) {
    sortedDeltas.sort((a, b) => {
      const ka = splitAssetKey(a[0])
      const kb = splitAssetKey(b[0])
      const order = compareOrdinal(ka.itemId, kb.itemId)
      return order !== 0 ? order : compareOrdinal(ka.variant, kb.variant)
    })
  }

  return {
    sortedDeltas,
    events,
    completedActions: stateRef.completedActions,
    completedRepeats: stateRef.completedRepeats,
    changed: transitions > 0 || headActivated || stateRef.idleSkillChanged,
  }
}

function advanceCombatAction(
  player: SimPlayerRuntime,
  action: SimActionState,
  catalog: GameDataCatalog,
  wallet: AssetWallet | null,
  fromTicks: bigint,
  targetTicks: bigint,
  stateRef: {
    remainingSeconds: number
    completedActions: number
    completedRepeats: number
    idleSkillChanged: boolean
  },
  deltas: Map<string, number>,
  events: SimEventTail,
  live: LiveCombatBattle | null,
  consumableSchedule: ConsumableScheduleMode,
): void {
  if (action.expedition?.phase !== CombatExpeditionPhase.Combat) {
    const prepared = advanceCombatPreparationCore(
      player,
      action,
      catalog,
      wallet,
      fromTicks,
      targetTicks,
      stateRef,
      deltas,
      events,
    )
    if (prepared) {
      live?.adopt(action, prepared, action.expedition!, player.attributeInputRevision)
    }
    return
  }

  const expedition = action.expedition
  const encounter = catalog.encounters.get(expedition.encounterId)
  if (!encounter) throw new Error(`远征状态引用的遭遇 ${expedition.encounterId} 不存在。`)
  const waveEnemies = expedition.enemies
  if (!waveEnemies || waveEnemies.length === 0) {
    throw new Error('战斗远征敌人状态无效。')
  }

  const activeBuffs = player.state.buffs
  let battleState
  const session = live ? live.tryReuse(action, expedition) : null
  if (session) {
    battleState = session.battle
    if (!catalog.baseAttributes || session.attributeRevision !== player.attributeInputRevision) {
      battleState.player.refreshStats(
        player.resolveAttributesForCombat(
          catalog,
          battleState.player.traits,
          battleState.player.traitLevels,
        ),
      )
      session.attributeRevision = player.attributeInputRevision
    }
    battleState.player.externalBuffStacks = (buffId) => {
      let total = 0
      for (let i = 0; i < activeBuffs.length; i++) {
        if (buffId === null || activeBuffs[i].buffId === buffId) total += activeBuffs[i].stacks
      }
      return total
    }
  } else {
    battleState = restoreCombatBattle(player, action, encounter, catalog)
    live?.adopt(action, battleState, expedition, player.attributeInputRevision)
  }

  const battleFrom = battleState.elapsedSeconds
  let combatResult
  if (consumableSchedule === ConsumableScheduleMode.EventDriven && wallet !== null) {
    const callWallTicks = addSecondsExactTicks(targetTicks, -stateRef.remainingSeconds)
    const panelFrozen = action.participation?.spec != null
    const evaluateConsumablesAt = (
      wallAtTicks: bigint,
      liveState: typeof battleState,
      wakeDelta: CombatFactDelta | null = null,
    ) => {
      for (let pass = 0; pass < 256; pass++) {
        const spent = activateInstantConsumables(
          player,
          catalog,
          wallet,
          wallAtTicks,
          events,
          live,
          liveState,
          deltas,
          pass === 0 ? wakeDelta : null,
        )
        if (spent.length === 0) break
        for (let i = 0; i < spent.length; i++) {
          const d = spent[i]
          addAssetDeltaMap(deltas, d.itemId, d.quantityDelta, d.variant)
        }
      }
    }

    evaluateConsumablesAt(callWallTicks, battleState, null)
    const hook = new CombatBoundaryHookDispatcher((ctx, delta, acted) => {
      const wallAtTicks = addSecondsExactTicks(callWallTicks, ctx.elapsedSeconds - battleFrom)
      evaluateConsumablesAt(wallAtTicks, ctx, delta)
      if (
        !acted ||
        panelFrozen ||
        !live?.current ||
        (catalog.baseAttributes !== null &&
          live.current.attributeRevision === player.attributeInputRevision)
      ) {
        return
      }
      ctx.player.refreshStats(
        player.resolveAttributesForCombat(
          catalog,
          ctx.player.traits,
          ctx.player.traitLevels,
        ),
      )
      live.current.attributeRevision = player.attributeInputRevision
      evaluateConsumablesAt(wallAtTicks, ctx, null)
    }, battleState)
    combatResult = advanceCombatTo(battleState, battleFrom + stateRef.remainingSeconds, hook)
  } else {
    combatResult = advanceCombatTo(battleState, battleFrom + stateRef.remainingSeconds)
  }

  if (combatResult.outcome !== CombatOutcome.Running) {
    live?.release(action)
  }
  completeCombatAdvanceCore(
    player,
    action,
    encounter,
    catalog,
    battleState,
    combatResult,
    fromTicks,
    targetTicks,
    stateRef,
    deltas,
    events,
    wallet,
  )
}
