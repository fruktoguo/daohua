// 战斗准备、入场、恢复、收尾结算、奖励分配与持续消耗品规则
// 严格对应 DaoHua.Ecs.Systems.CombatActionRules

import {
  type CombatEncounterDefinition,
  type CombatMonsterDefinition,
  type GameDataCatalog,
} from './catalog.ts'
import { decideInitialActions } from './combatEngine.ts'
import {
  CombatEncounterState,
  CombatOutcome,
  CombatantState,
  type CombatantSnapshot,
  type CombatantTemplate,
  scaleMonsterStats,
} from './combatModels.ts'
import { selectEncounterWave } from './encounterWaveSelector.ts'
import {
  TICKS_PER_MILLISECOND,
  addSecondsExactTicks,
  clamp,
  createSeed,
  nextSeed,
  seedModulo,
  ticksDiffSeconds,
  ticksToUnixSeconds,
} from './math.ts'
import {
  advanceIdleSkillResources,
  captureCombatState,
  carryAllies,
  carryAlliesToSearch,
} from './resourceBoundaries.ts'
import {
  ALL_TRAITS,
  CombatMonsterRank,
  CombatRespawnPolicy,
  PartyWipeRule,
  PercentCurveMode,
  abilityLevelForExp,
  calculateStorageBagDropChance,
  BROKEN_STORAGE_BAG_ITEM_ID,
  difficultyDropQuantityMultiplier,
  difficultyDropRateMultiplier,
  difficultyExperienceMultiplier,
  parseBuffEffects,
  percentMultiplier,
  weakenMonsterStats,
} from './rules.ts'
import {
  ActionQueueStatus,
  AssetWallet,
  CORE_ASPECT_KINDS,
  CombatExpeditionPhase,
  PlayerSectionKind,
  SimEventTail,
  SimPlayerRuntime,
  addAssetDeltaMap,
  applyCombatBuffsToCombatant,
  applyCombatSkillsToCombatant,
  combatBuffsFromCombatant,
  combatSkillsFromCombatant,
  createEmptyCombatSkillState,
  formatTrimmedFixed,
  resolveExpCapWithLinggen,
  sumActiveBuffStacks,
  sumFrozenBuffStacks,
  sumUnmergedActiveBuffStacks,
  type ExpeditionAllyState,
  type ExpeditionEnemyState,
  type SimActionParticipationSpec,
  type SimActionState,
  type SimCombatExpeditionState,
} from './simulationState.ts'
import {
  buildSkillSlotsFromAbilitySlots,
  evaluateTriggers,
  resolveConsumableTriggers,
} from './triggerEvaluator.ts'
import type { ConsumableDefinition, ItemDefinition } from './types.ts'

const DROP_QUANTITY_CHANNEL_SALT = BigInt.asIntN(64, 0x9e3779b97f4a7c15n)
const UNIVERSAL_RARE_DROP_ROLL_SALT = BigInt.asIntN(64, 0x5a17b4c83d9e2f1an)
const UNIVERSAL_RARE_DROP_ASSIGNMENT_SALT = BigInt.asIntN(64, 0x3d9e2f1a5a17b4c8n)
const DROP_ASSIGNMENT_CHANNEL_SALT = BigInt.asIntN(64, 0xc2b2ae3d27d4eb4fn)

export interface CombatEntryPreparation {
  battle: CombatEncounterState | null
  state: SimCombatExpeditionState
  assetDeltas: Map<string, number>
  blocked: boolean
}

export function resolveCombatEncounterForAction(
  player: SimPlayerRuntime,
  actionId: string,
  completedRepeats: number,
  catalog: GameDataCatalog,
): CombatEncounterDefinition {
  const def = catalog.actions.get(actionId)
  if (!def) {
    throw new Error(`未知行动: ${actionId}`)
  }
  let encounterId = def.combatEncounterId
  const node = catalog.worldNodes.get(player.state.worldNodeId)
  const pool = node ? node.encounterIds : []
  if (pool.length > 0) {
    const idx = pool.length === 1 ? 0 : seedModulo(createSeed(def.id, completedRepeats), pool.length)
    encounterId = pool[idx]
  }
  const encounter = encounterId ? catalog.encounters.get(encounterId) : undefined
  if (!encounter) {
    throw new Error(`战斗行动 ${def.id} 在节点 ${player.state.worldNodeId} 无可用遭遇。`)
  }
  return encounter
}

function inBattleRespawnSeconds(encounter: CombatEncounterDefinition): number {
  return encounter.onPartyWipe === PartyWipeRule.Retry ? encounter.playerRespawnSeconds : 0
}

function findAllyCarry(
  carried: readonly ExpeditionAllyState[] | null | undefined,
  memberIndex: number,
): ExpeditionAllyState | null {
  if (!carried) return null
  for (let i = 0; i < carried.length; i++) {
    if (carried[i].memberIndex === memberIndex) return carried[i]
  }
  return null
}

function buildAllyCarry(
  spec: SimActionParticipationSpec,
  selfIndex: number,
): ExpeditionAllyState[] {
  const allies: ExpeditionAllyState[] = []
  for (let i = 0; i < spec.members.length; i++) {
    if (i === selfIndex) continue
    const member = spec.members[i]
    allies.push({
      memberIndex: i,
      state: {
        hitpoints: member.initialHitpoints,
        manapoints: member.initialManapoints,
        nextAttackAtSeconds: 0,
        attackCount: 0,
        threat: 0,
        pendingSkillIndex: -1,
        threatBonusPoints: 0,
        bonusAbsorptionPoints: 0,
        skillCooldownUntil: null,
        skillCastCount: 0,
        buffs: null,
        respawnAtSeconds: null,
        respawnPolicy: CombatRespawnPolicy.Immediate,
        respawnTargetIndex: -1,
        respawnDeadlineSeconds: null,
        respawnWaitingUntilSeconds: null,
        traits: [],
        traitLevels: null,
      },
      pendingAction: null,
      pendingActionDurationSeconds: 0,
      combatSkills: member.combatSkills ?? createEmptyCombatSkillState(),
      combatBuffs: member.combatBuffs ?? [],
    })
  }
  return allies
}

function resetAllyCarry(
  spec: SimActionParticipationSpec,
  selfIndex: number,
  previous: readonly ExpeditionAllyState[] | null | undefined,
): ExpeditionAllyState[] {
  const allies: ExpeditionAllyState[] = []
  for (let i = 0; i < spec.members.length; i++) {
    if (i === selfIndex) continue
    const member = spec.members[i]
    allies.push({
      memberIndex: i,
      state: {
        hitpoints: member.stats.maxHitpoints,
        manapoints: member.stats.maxManapoints,
        nextAttackAtSeconds: 0,
        attackCount: 0,
        threat: 0,
        pendingSkillIndex: -1,
        threatBonusPoints: 0,
        bonusAbsorptionPoints: 0,
        skillCooldownUntil: null,
        skillCastCount: 0,
        buffs: null,
        respawnAtSeconds: null,
        respawnPolicy: CombatRespawnPolicy.Immediate,
        respawnTargetIndex: -1,
        respawnDeadlineSeconds: null,
        respawnWaitingUntilSeconds: null,
        traits: [],
        traitLevels: null,
      },
      pendingAction: null,
      pendingActionDurationSeconds: 0,
      combatSkills: createEmptyCombatSkillState(),
      combatBuffs: findAllyCarry(previous, i)?.combatBuffs ?? [],
    })
  }
  return allies
}

export function createCombatSearchState(
  player: SimPlayerRuntime,
  encounter: CombatEncounterDefinition,
  phase: CombatExpeditionPhase,
  catalog: GameDataCatalog,
  completedRepeats: number,
  explicitSeed: bigint | null = null,
  waveNumber = 1,
  spec: SimActionParticipationSpec | null = null,
  allies: ExpeditionAllyState[] | null = null,
): SimCombatExpeditionState {
  const searchSpeedPoints = spec
    ? spec.searchSpeedPoints
    : player.resolveAttributes(catalog).searchSpeedPoints
  const duration =
    phase === CombatExpeditionPhase.InitialSearch
      ? encounter.searchSeconds
      : encounter.respawnSeconds *
        percentMultiplier(searchSpeedPoints, PercentCurveMode.NonLinear)
  const seed = explicitSeed ?? spec?.seed ?? createSeed(encounter.id, completedRepeats)
  let selfIndex = 0
  if (spec) {
    selfIndex = spec.members.findIndex((m) => m.playerId === player.playerId)
    if (selfIndex < 0) selfIndex = 0
  }
  return {
    encounterId: encounter.id,
    phase,
    phaseElapsedSeconds: 0,
    phaseDurationSeconds: duration,
    seed,
    battleElapsedSeconds: 0,
    playerHitpoints: player.state.hitpoints,
    playerManapoints: player.state.manapoints,
    playerNextAttackAtSeconds: 0,
    playerAttackCount: 0,
    playerThreat: 0,
    outcome: CombatOutcome.Running,
    waveNumber,
    enemies: null,
    recentEvents: null,
    playerRespawnAtSeconds: null,
    playerState: null,
    originNodeId: spec ? spec.nodeId : player.state.worldNodeId,
    isPinned: spec !== null,
    selfIndex,
    allies:
      spec === null || spec.members.length === 1
        ? null
        : allies ?? buildAllyCarry(spec, selfIndex),
    duelSeekFailed: false,
  }
}

export function initializeCombatAction(
  player: SimPlayerRuntime,
  action: SimActionState,
  catalog: GameDataCatalog,
  atTicks: bigint,
  events: SimEventTail,
): void {
  if (action.expedition !== null) return
  const def = catalog.actions.get(action.actionId)
  if (!def) throw new Error(`未知行动: ${action.actionId}`)
  player.ensureActionReport(action.id, def.id, def.name)
  const spec = action.participation?.spec ?? null
  if (spec) {
    const pinned = catalog.encounters.get(spec.encounterId)
    if (!pinned) throw new Error(`参与会话遭遇 ${spec.encounterId} 不存在。`)
    action.expedition = createCombatSearchState(
      player,
      pinned,
      CombatExpeditionPhase.InitialSearch,
      catalog,
      action.completedRepeats,
      null,
      1,
      spec,
    )
    events.add({
      atTicks,
      code: 'combat.search.started',
      message: `参与搜寻：${pinned.name}。`,
      actionId: def.id,
      itemDeltas: null,
    })
    return
  }
  const encounter = resolveCombatEncounterForAction(
    player,
    def.id,
    action.completedRepeats,
    catalog,
  )
  action.expedition = createCombatSearchState(
    player,
    encounter,
    CombatExpeditionPhase.InitialSearch,
    catalog,
    action.completedRepeats,
  )
  events.add({
    atTicks,
    code: 'combat.search.started',
    message: `开始搜寻：${encounter.name}。`,
    actionId: def.id,
    itemDeltas: null,
  })
}

function advanceCombatSearchPhase(action: SimActionState, availableSeconds: number): number {
  const expedition = action.expedition
  if (!expedition) throw new Error('战斗搜寻状态缺失。')
  const remaining = Math.max(0, expedition.phaseDurationSeconds - expedition.phaseElapsedSeconds)
  const step = Math.min(availableSeconds, remaining)
  if (step <= 0 && remaining > 0 && !expedition.duelSeekFailed) {
    throw new Error('战斗搜寻阶段没有可推进的时间。')
  }
  action.expedition = {
    ...expedition,
    phaseElapsedSeconds: expedition.phaseElapsedSeconds + step,
  }
  action.progressSeconds = action.expedition.phaseElapsedSeconds
  return step
}

function resolveRespawnTargetIndex(
  spec: SimActionParticipationSpec,
  member: SimActionParticipationSpec['members'][number],
): number {
  if (
    member.respawnPolicy !== CombatRespawnPolicy.WaitTarget ||
    !member.respawnTargetPlayerId ||
    member.respawnTargetPlayerId.trim().length === 0
  ) {
    return -1
  }
  for (let i = 0; i < spec.members.length; i++) {
    if (spec.members[i].playerId === member.respawnTargetPlayerId) {
      return spec.members[i].playerId === member.playerId ? -1 : i
    }
  }
  return -1
}

function attachCombatStats(
  player: SimPlayerRuntime,
  battle: CombatEncounterState,
  spec: SimActionParticipationSpec | null,
  catalog: GameDataCatalog,
): void {
  const book = player.report?.combatStats ?? null
  battle.statsBook = book
  if (!book) return
  const selfIndex = battle.selfIndex
  for (let i = 0; i < battle.playerAllies.length; i++) {
    book.register(
      i === selfIndex ? 'player' : `ally:${i}`,
      spec?.members[i].displayName ?? (i === selfIndex ? player.displayName : ''),
    )
  }
  for (let i = 0; i < battle.enemies.length; i++) {
    const defId = battle.enemies[i].definitionId
    if (defId) {
      const m = catalog.monsters.get(defId)
      book.register(`monster:${defId}`, m ? m.name : defId)
    }
  }
}

function tryGetCombatSustainedSlot(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  index: number,
): {
  slot: (typeof player.state.autoUseSlots)[number]
  item: ItemDefinition
  consumable: ConsumableDefinition
} | null {
  const slot = player.state.autoUseSlots[index]
  if (
    !slot.enabled ||
    slot.itemId === null ||
    slot.group !== 'general' ||
    slot.actionType !== 'combat'
  ) {
    return null
  }
  const item = catalog.items.get(slot.itemId)
  if (
    !item ||
    !item.consumable ||
    !item.consumable.isContinuous ||
    !(item.consumable.durationSeconds > 0) ||
    item.consumable.durable ||
    !item.consumable.usableInBars.has('combat')
  ) {
    return null
  }
  return { slot, item, consumable: item.consumable }
}

function rechargeCombatSustainedSlots(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  wallet: AssetWallet | null,
  atUnix: number,
  battle: CombatEncounterState,
  deltas: Map<string, number>,
  events: SimEventTail,
): boolean {
  let recharged = false
  for (let index = 0; index < player.state.autoUseSlots.length; index++) {
    const match = tryGetCombatSustainedSlot(player, catalog, index)
    if (!match) continue
    const { slot, item, consumable } = match
    if (slot.remainingDurationSeconds > 0) continue

    const triggerList = resolveConsumableTriggers(
      item.id,
      consumable,
      player.state.consumableTriggers,
    )
    if (triggerList.length > 0) {
      let triggerTarget: CombatantState | null = null
      for (let i = 0; i < battle.enemies.length; i++) {
        const e = battle.enemies[i]
        if (
          e.isAlive &&
          (triggerTarget === null || e.effectiveThreat > triggerTarget.effectiveThreat)
        ) {
          triggerTarget = e
        }
      }
      if (
        !evaluateTriggers(
          triggerList,
          battle.player,
          triggerTarget,
          battle.playerAllies,
          battle.enemies,
          battle.elapsedSeconds,
        )
      ) {
        continue
      }
    }
    const available =
      (wallet ? wallet.get(item.id, '') : 0) + (deltas.get(`${item.id}\0`) ?? 0)
    if (available < 1) continue
    addAssetDeltaMap(deltas, item.id, -1)
    player.recordStats(atUnix, { lostItem: item.id, lostQuantity: 1 })
    player.state.autoUseSlots[index] = {
      ...slot,
      remainingDurationSeconds: consumable.durationSeconds,
    }
    player.markDirty(PlayerSectionKind.Settings)
    events.add({
      atTicks: BigInt(Math.trunc(atUnix * 1000)) * TICKS_PER_MILLISECOND,
      code: 'auto-use.activated',
      message: `自动使用 ${item.name}，可生效 ${formatTrimmedFixed(consumable.durationSeconds, 6)} 秒。`,
      actionId: null,
      itemDeltas: { [item.id]: -1 },
    })
    recharged = true
  }
  return recharged
}

function injectSustainedCombatBuffs(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  battle: CombatEncounterState,
): void {
  for (let index = 0; index < player.state.autoUseSlots.length; index++) {
    const match = tryGetCombatSustainedSlot(player, catalog, index)
    if (!match) continue
    const { slot, item } = match
    if (slot.remainingDurationSeconds <= 0) continue
    const rawItem = catalog.rawItems.get(item.id)
    const rawCons = rawItem?.consumable as Record<string, unknown> | undefined
    const rawBuffs = rawCons && Array.isArray(rawCons.buffs)
      ? (rawCons.buffs as Record<string, unknown>[])
      : null
    if (!rawBuffs) continue

    const baseTeamWide = item.subCategory === 'formation'
    for (const buff of rawBuffs) {
      const teamWide = baseTeamWide && buff.teamWide !== false
      const buffId = String(buff.buffId)
      const effects = parseBuffEffects(buff, 1.0)
      const name = typeof buff.name === 'string' ? buff.name : buffId
      for (let u = 0; u < battle.playerAllies.length; u++) {
        const unit = battle.playerAllies[u]
        if (!teamWide && unit !== battle.player) continue
        unit.setBuff({
          buffId,
          expiresAtSeconds: battle.elapsedSeconds + slot.remainingDurationSeconds,
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

function drainCombatSustainedSlots(
  player: SimPlayerRuntime,
  catalog: GameDataCatalog,
  consumedBattleSeconds: number,
  atTicks: bigint,
  actionId: string,
  events: SimEventTail,
): void {
  if (consumedBattleSeconds <= 0) return
  for (let index = 0; index < player.state.autoUseSlots.length; index++) {
    const match = tryGetCombatSustainedSlot(player, catalog, index)
    if (!match) continue
    const { slot, item } = match
    if (slot.remainingDurationSeconds <= 0) continue
    const remaining = Math.max(0, slot.remainingDurationSeconds - consumedBattleSeconds)
    player.state.autoUseSlots[index] = {
      ...slot,
      remainingDurationSeconds: remaining,
    }
    player.markDirty(PlayerSectionKind.Settings)
    if (remaining > 0) continue
    events.add({
      atTicks,
      code: 'auto-use.depleted',
      message: `${item.name} 的持续时间已耗尽。`,
      actionId,
      itemDeltas: null,
    })
  }
}

export function prepareCombatEntry(
  player: SimPlayerRuntime,
  action: SimActionState,
  encounter: CombatEncounterDefinition,
  completedSearch: SimCombatExpeditionState,
  catalog: GameDataCatalog,
  wallet: AssetWallet | null,
  entryAtTicks: bigint,
  events: SimEventTail,
): CombatEntryPreparation {
  const def = catalog.actions.get(action.actionId)
  if (!def) throw new Error(`未知行动: ${action.actionId}`)
  const spec = action.participation?.spec ?? null
  const partySize = spec ? spec.members.length : 1
  const wave = selectEncounterWave(
    encounter,
    catalog.monsters,
    completedSearch.seed,
    completedSearch.waveNumber,
    partySize,
  )
  const weaken = player.state.nodeBuildingEffects.monsterWeakenRatio
  const difficulty = spec ? spec.difficulty : action.difficulty
  const enemyTemplates: CombatantTemplate[] = wave.map((monster) => {
    const scaled = scaleMonsterStats(monster.stats, difficulty)
    return {
      stats: weakenMonsterStats(scaled, weaken),
      skills: monster.skills,
      definitionId: monster.id,
      rank: monster.rank,
    }
  })
  const respawnSeconds = inBattleRespawnSeconds(encounter)
  let battle: CombatEncounterState

  if (spec) {
    const selfIndex = completedSearch.selfIndex
    const allyTemplates: CombatantTemplate[] = spec.members.map((m) => ({
      stats: m.stats,
      skills: m.skills,
    }))
    battle = CombatEncounterState.createAllied(
      encounter.id,
      completedSearch.seed,
      allyTemplates,
      selfIndex,
      enemyTemplates,
      respawnSeconds,
    )
    const carried = completedSearch.allies
    const entryAtUnix = ticksToUnixSeconds(entryAtTicks)
    const sessionElapsed = Math.max(0, entryAtUnix - spec.startedAtUnixSeconds)

    for (let i = 0; i < spec.members.length; i++) {
      const member = spec.members[i]
      const unit = battle.playerAllies[i]
      unit.respawnPolicy = member.respawnPolicy
      unit.respawnTargetIndex = resolveRespawnTargetIndex(spec, member)
      if (i === selfIndex) {
        unit.hitpoints = clamp(player.state.hitpoints, 0, member.stats.maxHitpoints)
        unit.manapoints = clamp(player.state.manapoints, 0, member.stats.maxManapoints)
        applyCombatSkillsToCombatant(player.state.combatSkills, unit)
        applyCombatBuffsToCombatant(player.state.combatBuffCarry, unit, battle.elapsedSeconds)
      } else {
        const carry = findAllyCarry(carried, i)
        unit.hitpoints = clamp(
          carry?.state.hitpoints ?? member.initialHitpoints,
          0,
          member.stats.maxHitpoints,
        )
        unit.manapoints = clamp(
          carry?.state.manapoints ?? member.initialManapoints,
          0,
          member.stats.maxManapoints,
        )
        const cSkills = carry?.combatSkills ?? member.combatSkills
        if (cSkills) applyCombatSkillsToCombatant(cSkills, unit)
        const cBuffs = carry?.combatBuffs ?? member.combatBuffs
        if (cBuffs) applyCombatBuffsToCombatant(cBuffs, unit, battle.elapsedSeconds)
      }

      if (!unit.isAlive && respawnSeconds > 0) {
        const carryRemaining =
          i === selfIndex
            ? completedSearch.playerRespawnAtSeconds
            : findAllyCarry(carried, i)?.state.respawnAtSeconds ?? null
        if (carryRemaining !== null && carryRemaining <= 0) {
          unit.hitpoints = member.stats.maxHitpoints
          unit.respawnAtSeconds = Number.POSITIVE_INFINITY
          unit.respawnDeadlineSeconds = Number.POSITIVE_INFINITY
          if (i === selfIndex) player.state.hitpoints = member.stats.maxHitpoints
        } else {
          unit.respawnAtSeconds =
            battle.elapsedSeconds +
            (carryRemaining !== null && carryRemaining > 0
              ? Math.min(carryRemaining, respawnSeconds)
              : respawnSeconds)
          unit.respawnDeadlineSeconds = battle.elapsedSeconds + respawnSeconds * 2
        }
      }

      const extBuffs = member.externalBuffs
      unit.externalBuffStacks = (buffId) => sumFrozenBuffStacks(extBuffs, buffId)

      if (member.sustainedBuffs) {
        for (const frozen of member.sustainedBuffs) {
          if (frozen.teamWide) continue
          const sustainedRemaining = frozen.remainingSeconds - sessionElapsed
          if (sustainedRemaining <= 0) continue
          unit.setBuff({
            buffId: frozen.buffId,
            expiresAtSeconds: battle.elapsedSeconds + sustainedRemaining,
            stacks: frozen.stacks,
            name: frozen.name,
            resetOnWave: true,
            missingHitpointsPerStack: 0,
            effects: frozen.effects,
            statRatios: frozen.statRatios,
            sourceUnitId: null,
          })
        }
      }
    }

    for (let srcIdx = 0; srcIdx < spec.members.length; srcIdx++) {
      const sustained = spec.members[srcIdx].sustainedBuffs
      if (!sustained) continue
      for (const frozen of sustained) {
        if (!frozen.teamWide) continue
        const sustainedRemaining = frozen.remainingSeconds - sessionElapsed
        if (sustainedRemaining <= 0) continue
        for (let unitIdx = 0; unitIdx < battle.playerAllies.length; unitIdx++) {
          battle.playerAllies[unitIdx].setBuff({
            buffId: frozen.buffId,
            expiresAtSeconds: battle.elapsedSeconds + sustainedRemaining,
            stacks: frozen.stacks,
            name: frozen.name,
            resetOnWave: true,
            missingHitpointsPerStack: 0,
            effects: frozen.effects,
            statRatios: frozen.statRatios,
            sourceUnitId: null,
          })
        }
      }
    }
  } else {
    const activeBuffs = player.state.buffs
    const playerSkills = buildSkillSlotsFromAbilitySlots(
      player.state.abilitySlots,
      catalog.abilities,
      player.state.abilityTriggers,
    )
    battle = CombatEncounterState.create(
      encounter.id,
      completedSearch.seed,
      {
        stats: player.resolveAttributesForCombat(catalog),
        skills: playerSkills,
      },
      enemyTemplates,
      respawnSeconds,
    )
    battle.player.hitpoints = clamp(player.state.hitpoints, 0, player.state.maxHitpoints)
    battle.player.manapoints = clamp(player.state.manapoints, 0, player.state.maxManapoints)
    const entryUnix = ticksToUnixSeconds(entryAtTicks)
    battle.player.externalBuffStacks = (buffId) =>
      sumUnmergedActiveBuffStacks(battle.player.buffs, activeBuffs, buffId, entryUnix + battle.elapsedSeconds)
    applyCombatSkillsToCombatant(player.state.combatSkills, battle.player)
    applyCombatBuffsToCombatant(player.state.combatBuffCarry, battle.player, battle.elapsedSeconds)
  }

  for (let i = 0; i < wave.length; i++) {
    battle.enemies[i].definitionId = wave[i].id
  }
  attachCombatStats(player, battle, spec, catalog)

  const assetDeltas = new Map<string, number>()
  const waveNames = wave.map((m) => m.name).join('、')

  if (battle.player.hitpoints <= 0 && spec === null) {
    action.status = ActionQueueStatus.Blocked
    action.expedition = completedSearch
    events.add({
      atTicks: entryAtTicks,
      code: 'combat.downed',
      message: `气血衰竭，无法迎战：${waveNames}。`,
      actionId: def.id,
      itemDeltas: null,
    })
    return { battle: null, state: completedSearch, assetDeltas, blocked: true }
  }

  if (battle.player.hitpoints <= 0 && spec !== null && respawnSeconds > 0) {
    const carryRemaining = completedSearch.playerRespawnAtSeconds
    if (carryRemaining !== null && carryRemaining <= 0) {
      battle.player.hitpoints = player.state.maxHitpoints
      battle.player.respawnAtSeconds = Number.POSITIVE_INFINITY
      battle.player.respawnDeadlineSeconds = Number.POSITIVE_INFINITY
      player.state.hitpoints = player.state.maxHitpoints
    } else {
      battle.player.respawnAtSeconds =
        battle.elapsedSeconds +
        (carryRemaining !== null && carryRemaining > 0
          ? Math.min(carryRemaining, respawnSeconds)
          : respawnSeconds)
      battle.player.respawnDeadlineSeconds = battle.elapsedSeconds + respawnSeconds * 2
      events.add({
        atTicks: entryAtTicks,
        code: 'combat.downed',
        message: `你以倒地状态随队迎战：${waveNames}。`,
        actionId: def.id,
        itemDeltas: null,
      })
    }
  }

  const atUnix = ticksToUnixSeconds(entryAtTicks)
  if (spec === null) {
    rechargeCombatSustainedSlots(player, catalog, wallet, atUnix, battle, assetDeltas, events)
    injectSustainedCombatBuffs(player, catalog, battle)
  }
  battle.initializeTraitListeners()
  decideInitialActions(battle)
  const combatState = captureCombatState(battle, {
    ...completedSearch,
    phase: CombatExpeditionPhase.Combat,
    phaseElapsedSeconds: 0,
    phaseDurationSeconds: 0,
    outcome: CombatOutcome.Running,
    duelSeekFailed: false,
    recentEvents: null,
  })
  action.progressSeconds = 0
  action.expedition = combatState
  player.recordStats(atUnix, { encounters: 1 })
  events.add({
    atTicks: entryAtTicks,
    code: 'combat.started',
    message: `遭遇妖兽：${waveNames}。`,
    actionId: def.id,
    itemDeltas: null,
  })
  return { battle, state: combatState, assetDeltas, blocked: false }
}

export function restoreCombatBattle(
  player: SimPlayerRuntime,
  action: SimActionState,
  encounter: CombatEncounterDefinition,
  catalog: GameDataCatalog,
): CombatEncounterState {
  const expedition = action.expedition!
  const waveEnemies = expedition.enemies!
  const weaken = player.state.nodeBuildingEffects.monsterWeakenRatio
  const spec = action.participation?.spec ?? null
  const difficulty = spec ? spec.difficulty : action.difficulty
  const templates: CombatantTemplate[] = waveEnemies.map((enemy) => {
    const monster = catalog.monsters.get(enemy.enemyId)!
    const scaled = scaleMonsterStats(monster.stats, difficulty)
    return {
      stats: weakenMonsterStats(scaled, weaken),
      skills: monster.skills,
      definitionId: monster.id,
      rank: monster.rank,
    }
  })

  const selfSnapshot: CombatantSnapshot = expedition.playerState ?? {
    hitpoints: expedition.playerHitpoints,
    manapoints: expedition.playerManapoints,
    nextAttackAtSeconds: expedition.playerNextAttackAtSeconds,
    attackCount: expedition.playerAttackCount,
    threat: expedition.playerThreat,
    pendingSkillIndex: -1,
    threatBonusPoints: 0,
    bonusAbsorptionPoints: 0,
    skillCooldownUntil: null,
    skillCastCount: 0,
    buffs: null,
    respawnAtSeconds: null,
    respawnPolicy: CombatRespawnPolicy.Immediate,
    respawnTargetIndex: -1,
    respawnDeadlineSeconds: null,
    respawnWaitingUntilSeconds: null,
    traits: [],
    traitLevels: null,
  }

  let battleState: CombatEncounterState
  if (spec) {
    const selfIndex = expedition.selfIndex
    const allyTemplates: CombatantTemplate[] = new Array(spec.members.length)
    const allySnapshots: CombatantSnapshot[] = new Array(spec.members.length)
    let allyAttacks = 0
    for (let i = 0; i < spec.members.length; i++) {
      allyTemplates[i] = { stats: spec.members[i].stats, skills: spec.members[i].skills }
      let snap =
        i === selfIndex
          ? { ...selfSnapshot, respawnAtSeconds: expedition.playerRespawnAtSeconds }
          : findAllyCarry(expedition.allies, i)?.state ?? null
      if (!snap) throw new Error(`组队远征缺少成员 ${i} 的战斗快照。`)
      if (snap.hitpoints > 0) {
        snap = { ...snap, respawnAtSeconds: null }
      } else if (
        snap.respawnAtSeconds !== null &&
        snap.respawnAtSeconds < expedition.battleElapsedSeconds
      ) {
        snap = { ...snap, respawnAtSeconds: expedition.battleElapsedSeconds }
      }
      allySnapshots[i] = snap
      allyAttacks += snap.attackCount
    }
    let enemyAttacks = 0
    const enemySnaps: CombatantSnapshot[] = new Array(waveEnemies.length)
    for (let i = 0; i < waveEnemies.length; i++) {
      enemySnaps[i] = waveEnemies[i].state
      enemyAttacks += waveEnemies[i].state.attackCount
    }
    battleState = CombatEncounterState.restoreAllied(
      encounter.id,
      expedition.seed,
      allyTemplates,
      allySnapshots,
      selfIndex,
      templates,
      enemySnaps,
      expedition.battleElapsedSeconds,
      expedition.outcome,
      allyAttacks + enemyAttacks,
      inBattleRespawnSeconds(encounter),
    )
    for (let i = 0; i < spec.members.length; i++) {
      const extBuffs = spec.members[i].externalBuffs
      battleState.playerAllies[i].externalBuffStacks = (buffId) =>
        sumFrozenBuffStacks(extBuffs, buffId)
    }
  } else {
    let enemyAttacks = 0
    const enemySnaps: CombatantSnapshot[] = new Array(waveEnemies.length)
    for (let i = 0; i < waveEnemies.length; i++) {
      enemySnaps[i] = waveEnemies[i].state
      enemyAttacks += waveEnemies[i].state.attackCount
    }
    const playerTemplate: CombatantTemplate = {
      stats: player.resolveAttributesForCombat(catalog, selfSnapshot.traits),
      skills: buildSkillSlotsFromAbilitySlots(
        player.state.abilitySlots,
        catalog.abilities,
        player.state.abilityTriggers,
      ),
    }
    battleState = CombatEncounterState.restore(
      encounter.id,
      expedition.seed,
      playerTemplate,
      selfSnapshot,
      templates,
      enemySnaps,
      expedition.battleElapsedSeconds,
      expedition.outcome,
      expedition.playerAttackCount + enemyAttacks,
      inBattleRespawnSeconds(encounter),
      expedition.playerRespawnAtSeconds ?? Number.POSITIVE_INFINITY,
    )
    const activeBuffs = player.state.buffs
    battleState.player.externalBuffStacks = (buffId) =>
      sumUnmergedActiveBuffStacks(battleState.player.buffs, activeBuffs, buffId)
  }

  for (let i = 0; i < waveEnemies.length; i++) {
    battleState.enemies[i].definitionId = waveEnemies[i].enemyId
  }
  attachCombatStats(player, battleState, spec, catalog)
  return battleState
}

export function advanceCombatPreparationCore(
  player: SimPlayerRuntime,
  action: SimActionState,
  catalog: GameDataCatalog,
  wallet: AssetWallet | null,
  fromTicks: bigint,
  targetTicks: bigint,
  stateRef: {
    remainingSeconds: number
    idleSkillChanged: boolean
  },
  deltas: Map<string, number>,
  events: SimEventTail,
): CombatEncounterState | null {
  const def = catalog.actions.get(action.actionId)
  if (!def) throw new Error(`未知行动: ${action.actionId}`)
  const totalSec = ticksDiffSeconds(fromTicks, targetTicks)
  let expedition = action.expedition
  let encounter: CombatEncounterDefinition
  if (expedition === null) {
    initializeCombatAction(
      player,
      action,
      catalog,
      addSecondsExactTicks(fromTicks, totalSec - stateRef.remainingSeconds),
      events,
    )
    expedition = action.expedition!
    encounter = catalog.encounters.get(expedition.encounterId)!
  } else {
    const found = catalog.encounters.get(expedition.encounterId)
    if (!found) throw new Error(`远征状态引用的遭遇 ${expedition.encounterId} 不存在。`)
    encounter = found
  }

  if (expedition.phase === CombatExpeditionPhase.Respawning) {
    const phaseRemaining = Math.max(0, expedition.phaseDurationSeconds - expedition.phaseElapsedSeconds)
    const step = Math.min(stateRef.remainingSeconds, phaseRemaining)
    stateRef.remainingSeconds -= step
    expedition = {
      ...expedition,
      phaseElapsedSeconds: expedition.phaseElapsedSeconds + step,
    }
    action.expedition = expedition
    action.progressSeconds = expedition.phaseElapsedSeconds
    if (action.progressSeconds + 1e-9 < expedition.phaseDurationSeconds) {
      if (step > 0 && advanceIdleSkillResources(player, step)) {
        stateRef.idleSkillChanged = true
      }
      return null
    }
    const respawnSpec = action.participation?.spec ?? null
    const respawnMember =
      respawnSpec && expedition.selfIndex < respawnSpec.members.length
        ? respawnSpec.members[expedition.selfIndex]
        : null
    player.state.hitpoints = respawnMember
      ? respawnMember.stats.maxHitpoints
      : player.state.maxHitpoints
    player.state.manapoints = respawnMember
      ? respawnMember.stats.maxManapoints
      : player.state.maxManapoints
    player.state.combatSkills = createEmptyCombatSkillState()
    for (let i = 0; i < player.state.autoUseSlots.length; i++) {
      player.state.autoUseSlots[i] = {
        ...player.state.autoUseSlots[i],
        cooldownUntilUnixSeconds: 0,
      }
    }
    player.markDirty(PlayerSectionKind.Settings)
    player.markDirty(PlayerSectionKind.Combat)
    action.expedition = createCombatSearchState(
      player,
      encounter,
      CombatExpeditionPhase.Search,
      catalog,
      action.completedRepeats,
      nextSeed(expedition.seed),
      1,
      respawnSpec,
      respawnSpec
        ? resetAllyCarry(respawnSpec, expedition.selfIndex, expedition.allies)
        : null,
    )
    action.progressSeconds = 0
    player.addCombatEvent(
      events,
      fromTicks,
      targetTicks,
      stateRef.remainingSeconds,
      'combat.respawned',
      '复活完成，气血与灵力恢复。',
      def.id,
    )
    return null
  }

  if (
    expedition.phase === CombatExpeditionPhase.InitialSearch ||
    expedition.phase === CombatExpeditionPhase.Search
  ) {
    const step = advanceCombatSearchPhase(action, stateRef.remainingSeconds)
    stateRef.remainingSeconds -= step
    if (step > 0 && advanceIdleSkillResources(player, step)) {
      stateRef.idleSkillChanged = true
    }
    const updated = action.expedition!
    if (updated.phaseElapsedSeconds + 1e-9 < updated.phaseDurationSeconds) return null

    const entryAtTicks = addSecondsExactTicks(fromTicks, totalSec - stateRef.remainingSeconds)
    const preparation = prepareCombatEntry(
      player,
      action,
      encounter,
      updated,
      catalog,
      wallet,
      entryAtTicks,
      events,
    )
    for (const [k, v] of preparation.assetDeltas) {
      deltas.set(k, (deltas.get(k) ?? 0) + v)
    }
    return preparation.battle
  }

  throw new Error(`战斗准备不能推进相位 ${expedition.phase}。`)
}

function abilityDisplayName(catalog: GameDataCatalog, abilityId: string): string {
  const ab = catalog.abilities.get(abilityId)
  if (ab && typeof ab.name === 'string' && ab.name.length > 0) return ab.name
  if (ab) return abilityId
  const tr = ALL_TRAITS.get(abilityId)
  return tr ? tr.name : abilityId
}

function formatCombatantLabel(
  combatantId: string,
  waveEnemies: readonly ExpeditionEnemyState[],
  spec: SimActionParticipationSpec | null,
  catalog: GameDataCatalog,
): string {
  if (combatantId === 'player') return '你'
  if (combatantId.startsWith('ally:')) {
    const idx = Number.parseInt(combatantId.slice(5), 10)
    if (spec && Number.isInteger(idx) && idx >= 0 && idx < spec.members.length) {
      return spec.members[idx].displayName
    }
  }
  if (combatantId.startsWith('enemy:')) {
    const idx = Number.parseInt(combatantId.slice(6), 10)
    if (Number.isInteger(idx) && idx >= 0 && idx < waveEnemies.length) {
      const defId = waveEnemies[idx].enemyId
      const name = catalog.monsters.get(defId)?.name ?? defId
      return `{{enemy:${defId}|${name}}}`
    }
  }
  return combatantId
}

function formatWaveMonsterNames(
  waveEnemies: readonly ExpeditionEnemyState[],
  catalog: GameDataCatalog,
): string {
  if (waveEnemies.length === 1) {
    const id = waveEnemies[0].enemyId
    return catalog.monsters.get(id)?.name ?? id
  }
  return waveEnemies.map((e) => catalog.monsters.get(e.enemyId)?.name ?? e.enemyId).join('、')
}

function appendCastClause(
  clauses: string[],
  targetLabel: string,
  hit: boolean,
  damage: number,
  healing: number,
  critical: boolean,
  broken: boolean,
  parried: boolean,
): void {
  if (!hit) {
    clauses.push(`被${targetLabel} {{mechanic:dodge|闪避}}`)
    return
  }
  if (damage > 0) {
    let mechanics = ''
    if (broken) mechanics += ' {{mechanic:break|破招}}'
    if (parried) mechanics += ' {{mechanic:parry|化解}}'
    if (critical) mechanics += ' {{mechanic:crit|暴击}}'
    clauses.push(`对${targetLabel}造成 ${formatTrimmedFixed(damage, 2)} 点伤害${mechanics}`)
  }
  if (healing > 0) {
    const critSuffix = critical ? ' {{mechanic:crit|暴击}}' : ''
    clauses.push(`为${targetLabel}回复 ${formatTrimmedFixed(healing, 2)} 点气血${critSuffix}`)
  }
}

function buildCastMessage(
  attackerLabel: string,
  abilityId: string,
  catalog: GameDataCatalog,
  clauses: string[],
): string {
  const prefix = ALL_TRAITS.has(abilityId)
    ? `${attackerLabel}触发 ${abilityDisplayName(catalog, abilityId)}`
    : `${attackerLabel}施放 {{ability:${abilityId}|${abilityDisplayName(catalog, abilityId)}}}`
  if (clauses.length === 0) {
    return `${prefix}，。`
  }
  if (clauses.length === 1) {
    return `${prefix}，${clauses[0]}。`
  }
  return `${prefix}，\n${clauses.join('\n')}。`
}

interface DownedMark {
  at: number
  memberIndex: number
  respawn: boolean
}

function rollDropAssignment(spec: SimActionParticipationSpec, seed: bigint): number {
  const roll = Number(BigInt.asUintN(64, nextSeed(seed)) % 1_000_000n) / 1_000_000
  let total = 0
  for (let i = 0; i < spec.members.length; i++) {
    total += spec.assignmentWeights[i]
  }
  if (total <= 1e-12) {
    return Math.min(spec.members.length - 1, Math.floor(roll * spec.members.length))
  }
  const threshold = roll * total
  let cumulative = 0
  for (let i = 0; i < spec.members.length; i++) {
    cumulative += spec.assignmentWeights[i]
    if (threshold < cumulative) return i
  }
  return spec.members.length - 1
}

function distributeRootExp(
  player: SimPlayerRuntime,
  amount: number,
  catalog: GameDataCatalog,
  atUnixSeconds: number,
): void {
  let internalAbility = null
  let level = 1
  if (player.state.abilitySlots.length > 0) {
    const slot0 = player.state.abilitySlots[0]
    if (slot0.abilityId) {
      const ab = catalog.abilities.get(slot0.abilityId)
      if (ab && ab.discipline === 'internal') {
        internalAbility = ab
        level = Math.max(1, slot0.level)
        const exp = player.state.abilityExperiences[slot0.abilityId] ?? 0
        if (exp > 0) {
          level = Math.max(level, abilityLevelForExp(exp))
        }
      }
    }
  }
  if (!internalAbility) {
    for (const aspect of CORE_ASPECT_KINDS) {
      player.grantRootExperience(aspect, amount, catalog, atUnixSeconds)
    }
    return
  }

  let effPerLevel =
    typeof internalAbility.rootExpEfficiencyPerLevel === 'number'
      ? internalAbility.rootExpEfficiencyPerLevel
      : 0
  if (effPerLevel <= 0) effPerLevel = 0.02
  const eff = Math.max(0, 1.0 + effPerLevel * (Math.max(1, level) - 1))

  if (
    internalAbility.rootExpMultipliers &&
    typeof internalAbility.rootExpMultipliers === 'object'
  ) {
    const mults = internalAbility.rootExpMultipliers
    for (const aspect of CORE_ASPECT_KINDS) {
      const key = aspect.toLowerCase()
      const mVal = mults[key]
      const mult = typeof mVal === 'number' && Number.isFinite(mVal) ? mVal : 1.0
      const share = amount * mult * eff
      if (share > 0) {
        player.grantRootExperience(aspect, share, catalog, atUnixSeconds)
      }
    }
    return
  }

  const rootAttr =
    typeof internalAbility.rootAttribute === 'string'
      ? internalAbility.rootAttribute.toLowerCase()
      : null
  const matched = CORE_ASPECT_KINDS.find((a) => a.toLowerCase() === rootAttr)
  const targets = matched ? [matched] : CORE_ASPECT_KINDS
  for (const aspect of targets) {
    player.grantRootExperience(aspect, amount * eff, catalog, atUnixSeconds)
  }
}

function applyCombatRewards(
  player: SimPlayerRuntime,
  monster: CombatMonsterDefinition,
  seed: bigint,
  pvpEnabled: boolean,
  killAtUnixSeconds: number,
  catalog: GameDataCatalog,
  deltas: Map<string, number>,
  spec: SimActionParticipationSpec | null,
  selfIndex: number,
  difficulty: number,
): void {
  const attributes =
    monster.experience > 0 || spec === null ? player.resolveAttributes(catalog) : null
  if (monster.experience > 0) {
    const diffExpMult = difficultyExperienceMultiplier(difficulty)
    const experience =
      (spec === null
        ? monster.experience
        : (monster.experience / spec.members.length) * (1 - spec.levelMaluses[selfIndex])) *
      diffExpMult
    const rawExp = experience
    const cultivationBonus =
      attributes!.cultivationExpBonusPoints + attributes!.combatExpBonusPoints
    const cultivationExp =
      cultivationBonus !== 0
        ? Math.max(0, experience * (1 + cultivationBonus / 100))
        : experience
    const expCap = resolveExpCapWithLinggen(player.state, catalog)
    const creditedExp = player.creditExperience(cultivationExp, expCap, experience)
    player.recordStats(killAtUnixSeconds, {
      experience: creditedExp,
      foundation: cultivationExp - creditedExp,
      combatExperience: experience,
      cultivationExperience: cultivationExp,
    })
    distributeRootExp(player, rawExp, catalog, killAtUnixSeconds)
  }

  const baseDropBonus = spec
    ? 1 + spec.totalDropBonusPoints / 100
    : Math.max(0, 1 + attributes!.dropBonusPoints / 100)
  const dropBonus = pvpEnabled
    ? baseDropBonus *
      percentMultiplier(catalog.duel.dropChanceBonusPercent, PercentCurveMode.Linear)
    : baseDropBonus
  const diffDropRateMult = difficultyDropRateMultiplier(difficulty)
  const diffDropQtyMult = difficultyDropQuantityMultiplier(difficulty)

  for (const drop of monster.dropTable) {
    if (!drop.itemId || drop.minQuantity <= 0 || drop.maxQuantity < drop.minQuantity || drop.dropChance <= 0) {
      continue
    }
    const roll = Number(BigInt.asUintN(64, nextSeed(seed)) % 1_000_000n) / 1_000_000
    const quantityRoll =
      Number(BigInt.asUintN(64, nextSeed(BigInt.asIntN(64, seed ^ DROP_QUANTITY_CHANNEL_SALT))) % 1_000_000n) /
      1_000_000
    seed = nextSeed(seed)

    let effectiveDropChance = drop.dropChance
    let minQty = drop.minQuantity
    let maxQty = drop.maxQuantity
    if (drop.dropChance >= 1.0 - 1e-9) {
      minQty = Math.ceil(minQty * diffDropQtyMult)
      maxQty = Math.ceil(maxQty * diffDropQtyMult)
      if (maxQty < minQty) maxQty = minQty
    } else {
      effectiveDropChance = effectiveDropChance * diffDropRateMult
    }

    if (roll > Math.min(1, effectiveDropChance * dropBonus)) continue
    let quantity = Math.floor(minQty + (maxQty - minQty + 1) * quantityRoll)
    quantity = clamp(quantity, minQty, maxQty)
    if (
      spec !== null &&
      rollDropAssignment(spec, BigInt.asIntN(64, seed ^ DROP_ASSIGNMENT_CHANNEL_SALT)) !==
        selfIndex
    ) {
      continue
    }

    addAssetDeltaMap(deltas, drop.itemId, quantity)
    player.recordStats(killAtUnixSeconds, { gainedItem: drop.itemId, gainedQuantity: quantity })
  }

  const rareDropChance = calculateStorageBagDropChance(monster.level, monster.rank)
  if (rareDropChance > 0) {
    const rareRoll =
      Number(
        BigInt.asUintN(64, nextSeed(BigInt.asIntN(64, seed ^ UNIVERSAL_RARE_DROP_ROLL_SALT))) %
          1_000_000n,
      ) / 1_000_000
    const effectiveRareChance = rareDropChance * diffDropRateMult
    if (rareRoll <= Math.min(1, effectiveRareChance * dropBonus)) {
      const bagItemId = BROKEN_STORAGE_BAG_ITEM_ID
      const bagQty = 1.0
      if (
        spec === null ||
        rollDropAssignment(
          spec,
          BigInt.asIntN(64, seed ^ UNIVERSAL_RARE_DROP_ASSIGNMENT_SALT),
        ) === selfIndex
      ) {
        addAssetDeltaMap(deltas, bagItemId, bagQty)
        player.recordStats(killAtUnixSeconds, { gainedItem: bagItemId, gainedQuantity: bagQty })
      }
    }
  }
}

export function completeCombatAdvanceCore(
  player: SimPlayerRuntime,
  action: SimActionState,
  encounter: CombatEncounterDefinition,
  catalog: GameDataCatalog,
  battleState: CombatEncounterState,
  combatResult: import('./combatModels.ts').CombatAdvanceResult,
  fromTicks: bigint,
  targetTicks: bigint,
  stateRef: {
    remainingSeconds: number
    completedActions: number
    completedRepeats: number
  },
  deltas: Map<string, number>,
  events: SimEventTail,
  wallet: AssetWallet | null = null,
): void {
  const def = catalog.actions.get(action.actionId)!
  const expedition = action.expedition!
  const waveEnemies = expedition.enemies!
  const battleFrom = combatResult.fromSeconds
  const totalSec = ticksDiffSeconds(fromTicks, targetTicks)
  const actionElapsedBeforeCombat = totalSec - stateRef.remainingSeconds
  const consumed = clamp(battleState.elapsedSeconds - battleFrom, 0, stateRef.remainingSeconds)
  const spec = action.participation?.spec ?? null

  if (spec === null) {
    const sustainedAtTicks = addSecondsExactTicks(
      fromTicks,
      actionElapsedBeforeCombat + consumed,
    )
    drainCombatSustainedSlots(player, catalog, consumed, sustainedAtTicks, def.id, events)
    const sustainedAtUnix = ticksToUnixSeconds(sustainedAtTicks)
    if (
      rechargeCombatSustainedSlots(
        player,
        catalog,
        wallet,
        sustainedAtUnix,
        battleState,
        deltas,
        events,
      )
    ) {
      injectSustainedCombatBuffs(player, catalog, battleState)
    }
  }

  player.state.combatSkills = combatSkillsFromCombatant(
    battleState.player,
    battleState.elapsedSeconds,
  )
  player.state.combatBuffCarry = combatBuffsFromCombatant(
    battleState.player,
    battleState.elapsedSeconds,
  )
  stateRef.remainingSeconds -= consumed
  player.state.hitpoints = battleState.player.hitpoints
  player.state.manapoints = battleState.player.manapoints
  player.markDirty(PlayerSectionKind.Combat)

  const updatedBattle: SimCombatExpeditionState = {
    ...expedition,
    battleElapsedSeconds: battleState.elapsedSeconds,
    playerHitpoints: battleState.player.hitpoints,
    playerManapoints: battleState.player.manapoints,
    playerNextAttackAtSeconds: battleState.player.nextAttackAtSeconds,
    playerAttackCount: battleState.player.attackCount,
    playerThreat: battleState.player.threat,
    playerRespawnAtSeconds: Number.isFinite(battleState.playerRespawnAtSeconds)
      ? battleState.playerRespawnAtSeconds
      : null,
    phaseElapsedSeconds: Math.min(
      expedition.phaseDurationSeconds,
      expedition.phaseElapsedSeconds + consumed,
    ),
    outcome: combatResult.outcome,
    recentEvents: combatResult.events.length === 0 ? null : combatResult.events,
  }
  action.expedition = updatedBattle
  action.progressSeconds = updatedBattle.phaseElapsedSeconds

  let waveNames: string | null = null
  const getWaveNames = () => {
    if (waveNames === null) waveNames = formatWaveMonsterNames(waveEnemies, catalog)
    return waveNames
  }

  const deaths = combatResult.playerDeathTimestamps ?? []
  const respawns = combatResult.playerRespawnTimestamps ?? []
  const allyDeaths = combatResult.allyDeathMarks ?? []
  const allyRespawns = combatResult.allyRespawnMarks ?? []
  const selfIndex = expedition.selfIndex
  let downedEvents: DownedMark[] | null = null
  if (deaths.length + respawns.length + allyDeaths.length + allyRespawns.length > 0) {
    downedEvents = []
    for (const at of deaths) downedEvents.push({ at, memberIndex: selfIndex, respawn: false })
    for (const at of respawns) downedEvents.push({ at, memberIndex: selfIndex, respawn: true })
    for (const m of allyDeaths)
      downedEvents.push({ at: m.atSeconds, memberIndex: m.memberIndex, respawn: false })
    for (const m of allyRespawns)
      downedEvents.push({ at: m.atSeconds, memberIndex: m.memberIndex, respawn: true })
    downedEvents.sort((left, right) => {
      if (left.at !== right.at) return left.at < right.at ? -1 : 1
      if (left.respawn !== right.respawn) return left.respawn ? 1 : -1
      return left.memberIndex - right.memberIndex
    })
  }
  let downedCursor = 0
  const flushDowned = (uptoBattleSeconds: number) => {
    if (!downedEvents) return
    while (
      downedCursor < downedEvents.length &&
      downedEvents[downedCursor].at <= uptoBattleSeconds + 1e-9
    ) {
      const mark = downedEvents[downedCursor++]
      const atTicks = addSecondsExactTicks(
        fromTicks,
        actionElapsedBeforeCombat + Math.max(0, mark.at - battleFrom),
      )
      const isSelf = mark.memberIndex === selfIndex
      const name = spec ? spec.members[mark.memberIndex].displayName : `队友${mark.memberIndex + 1}`
      if (mark.respawn) {
        events.add({
          atTicks,
          code: 'combat.respawned',
          message: isSelf ? '原地起身：气血回满，战斗继续。' : `队友 ${name} 原地起身，气血回满。`,
          actionId: def.id,
          itemDeltas: null,
        })
      } else {
        if (isSelf) {
          player.state.totalDefeats += 1
          player.markDirty(PlayerSectionKind.Progression)
          player.recordStats(ticksToUnixSeconds(atTicks), { deaths: 1 })
        }
        events.add({
          atTicks,
          code: 'combat.lost',
          message: isSelf
            ? `阵亡：被 ${getWaveNames()} 击倒，原地调息等待复活。`
            : `队友 ${name} 阵亡，原地调息等待复活。`,
          actionId: def.id,
          itemDeltas: null,
        })
      }
    }
  }

  for (let i = 0; i < combatResult.events.length; i++) {
    const ce = combatResult.events[i]
    if (ce.attackerId === 'player' && ce.abilityId !== null) {
      const abAtTicks = addSecondsExactTicks(
        fromTicks,
        actionElapsedBeforeCombat + Math.max(0, ce.atSeconds - battleFrom),
      )
      player.grantAbilityUseExperience(ce.abilityId, ticksToUnixSeconds(abAtTicks))
    }
  }

  let emittedIndex = 0
  let keepFrom = Number.NEGATIVE_INFINITY
  let emittable = 0
  for (let i = combatResult.events.length - 1; i >= 0 && emittable < SimEventTail.TailCapacity; i--) {
    const frame = combatResult.events[i]
    if (frame.abilityId !== null && ++emittable === SimEventTail.TailCapacity) {
      keepFrom = frame.atSeconds
    }
  }

  let eventIndex = 0
  while (eventIndex < combatResult.events.length) {
    const combatEvent = combatResult.events[eventIndex]
    if (combatEvent.atSeconds < keepFrom) {
      events.fold(emittedIndex)
      emittedIndex = events.count
      flushDowned(combatEvent.atSeconds)
      emittedIndex = events.count
      eventIndex++
      continue
    }
    const abilityId = combatEvent.abilityId
    if (abilityId === null) {
      flushDowned(combatEvent.atSeconds)
      emittedIndex = events.count
      eventIndex++
      continue
    }
    let castEnd = eventIndex + 1
    while (
      castEnd < combatResult.events.length &&
      combatResult.events[castEnd].abilityId === abilityId &&
      combatResult.events[castEnd].attackerId === combatEvent.attackerId &&
      combatResult.events[castEnd].atSeconds === combatEvent.atSeconds
    ) {
      castEnd++
    }
    const eventAtTicks = addSecondsExactTicks(
      fromTicks,
      actionElapsedBeforeCombat + Math.max(0, combatEvent.atSeconds - battleFrom),
    )
    const clauses: string[] = []
    for (let i = eventIndex; i < castEnd; i++) {
      const frame = combatResult.events[i]
      appendCastClause(
        clauses,
        formatCombatantLabel(frame.targetId, waveEnemies, spec, catalog),
        frame.hit,
        frame.damage,
        frame.healing,
        frame.critical,
        frame.break,
        frame.parried,
      )
    }
    const msg = buildCastMessage(
      formatCombatantLabel(combatEvent.attackerId, waveEnemies, spec, catalog),
      abilityId,
      catalog,
      clauses,
    )
    events.add({
      atTicks: eventAtTicks,
      code: 'combat.skill',
      message: msg,
      actionId: def.id,
      itemDeltas: null,
    })
    emittedIndex = events.count
    flushDowned(combatEvent.atSeconds)
    emittedIndex = events.count
    eventIndex = castEnd
  }
  flushDowned(Number.MAX_VALUE)

  if (combatResult.outcome === CombatOutcome.Running) return

  if (combatResult.outcome !== CombatOutcome.PlayerWon) {
    player.state.totalDefeats += 1
    player.markDirty(PlayerSectionKind.Progression)
    const lostAtTicks = addSecondsExactTicks(fromTicks, totalSec - stateRef.remainingSeconds)
    player.recordStats(ticksToUnixSeconds(lostAtTicks), { deaths: 1 })
    const search = createCombatSearchState(
      player,
      encounter,
      CombatExpeditionPhase.Search,
      catalog,
      action.completedRepeats,
      updatedBattle.seed,
      1,
      spec,
      spec ? carryAllies(battleState) : null,
    )
    action.expedition = {
      ...search,
      phase: CombatExpeditionPhase.Respawning,
      phaseDurationSeconds: encounter.playerRespawnSeconds,
      recentEvents: updatedBattle.recentEvents,
    }
    action.progressSeconds = 0
    player.addCombatEvent(
      events,
      fromTicks,
      targetTicks,
      stateRef.remainingSeconds,
      'combat.lost',
      `讨伐失败：${getWaveNames()}。`,
      def.id,
    )
    return
  }

  if (spec === null && !battleState.player.isAlive) {
    battleState.player.hitpoints = battleState.player.stats.maxHitpoints
    player.state.hitpoints = battleState.player.hitpoints
    battleState.player.respawnAtSeconds = Number.POSITIVE_INFINITY
    battleState.player.respawnDeadlineSeconds = Number.POSITIVE_INFINITY
    player.addCombatEvent(
      events,
      fromTicks,
      targetTicks,
      stateRef.remainingSeconds,
      'combat.respawned',
      '复活完成，气血与灵力恢复。',
      def.id,
    )
  }

  const pvpEnabled = action.pvpEnabled
  const killAtTicks = addSecondsExactTicks(fromTicks, totalSec - stateRef.remainingSeconds)
  const killAtUnix = ticksToUnixSeconds(killAtTicks)
  let killSeed = updatedBattle.seed
  const difficulty = spec ? spec.difficulty : action.difficulty
  let normalKills = 0
  let eliteKills = 0
  let bossKills = 0

  for (const enemy of waveEnemies) {
    const monster = catalog.monsters.get(enemy.enemyId)!
    applyCombatRewards(
      player,
      monster,
      killSeed,
      pvpEnabled,
      killAtUnix,
      catalog,
      deltas,
      spec,
      expedition.selfIndex,
      difficulty,
    )
    killSeed = nextSeed(killSeed)
    if (monster.rank === CombatMonsterRank.Elite) {
      player.state.eliteKills += 1
      eliteKills += 1
    } else if (monster.rank === CombatMonsterRank.Boss) {
      player.state.bossKills += 1
      bossKills += 1
    } else {
      player.state.normalKills += 1
      normalKills += 1
    }
    player.state.defeatedMonsterIds.add(enemy.enemyId)
  }

  player.recordStats(killAtUnix, {
    cycles: 1,
    normalKills,
    eliteKills,
    bossKills,
  })
  player.markDirty(PlayerSectionKind.Loadout)
  player.markDirty(PlayerSectionKind.Progression)
  player.addCombatEvent(
    events,
    fromTicks,
    targetTicks,
    stateRef.remainingSeconds,
    'combat.won',
    `讨伐成功：${getWaveNames()}。`,
    def.id,
  )

  stateRef.completedActions += 1
  stateRef.completedRepeats += 1
  action.completedRepeats += 1
  if (action.remainingRepeats > 0) action.remainingRepeats -= 1
  player.state.completedActions += 1
  player.markDirty(PlayerSectionKind.Progression)
  player.markDirty(PlayerSectionKind.Combat)
  player.markDirty(PlayerSectionKind.ActionQueue)

  const remainingSelfOnWave =
    spec !== null &&
    !battleState.player.isAlive &&
    Number.isFinite(battleState.playerRespawnAtSeconds)
      ? Math.max(0, battleState.playerRespawnAtSeconds - battleState.elapsedSeconds)
      : null

  const nextWaveSearch = createCombatSearchState(
    player,
    encounter,
    CombatExpeditionPhase.Search,
    catalog,
    action.completedRepeats,
    nextSeed(updatedBattle.seed),
    updatedBattle.waveNumber + 1,
    spec,
    spec ? carryAlliesToSearch(battleState) : null,
  )
  action.expedition = {
    ...nextWaveSearch,
    recentEvents: updatedBattle.recentEvents,
    playerRespawnAtSeconds: remainingSelfOnWave,
  }
}

export { sumActiveBuffStacks, sumFrozenBuffStacks }
