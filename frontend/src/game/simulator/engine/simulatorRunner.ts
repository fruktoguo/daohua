// 战斗预演总入口（严格对应 DaoHua.Simulator.SimulatorRunner）

import {
  createDefaultProfile,
  resolvePlayerAttributesFromState,
  resolveProfilesResult,
  restoreToState,
} from './attributeResolver.ts'
import type { CombatActionDefinition, CombatEncounterDefinition, GameDataCatalog } from './catalog.ts'
import { createCombatSearchState } from './combatActionRules.ts'
import {
  addDurationTruncTicks,
  compareOrdinal,
  deriveSessionGuidN,
  seedModulo,
  ticksFromUnixSeconds,
} from './math.ts'
import {
  ActionParticipationSessionEngine,
  applyActionParticipationSettlement,
  buildActionParticipantSnapshot,
  createActionParticipationSpec,
} from './partySessionEngine.ts'
import { AutoUseBar } from './rules.ts'
import { advanceSimulationTo } from './simulationEngine.ts'
import {
  ActionQueueStatus,
  CombatExpeditionPhase,
  assetKeyString,
  combatBuffCarryToDto,
  combatSkillsToDto,
  createSimPlayerRuntime,
  simEventToDto,
  type ActionParticipantSnapshot,
  type SimActionState,
  type SimCombatExpeditionState,
} from './simulationState.ts'
import {
  SIMULATOR_FORMAT_VERSION,
  type AutoUseSlotState,
  type PlayerState,
  type SimulatorAssetDto,
  type SimulatorAutoUseSlotDto,
  type SimulatorEnvironmentDto,
  type SimulatorEnvironmentNodeDto,
  type SimulatorExpeditionEnemyDto,
  type SimulatorMemberResultDto,
  type SimulatorProfileDto,
  type SimulatorRootExpDto,
  type SimulatorRunRequestDto,
  type SimulatorRunResultDto,
} from './types.ts'
import {
  SimulatorValidationError,
  normalizeMemberId,
  validateEnvironment,
  validateMember,
  validateRespawnTargets,
} from './validator.ts'

export const MIN_DURATION_SECONDS = 1
export const MAX_DURATION_SECONDS = 86400

const INT64_MIN = -(1n << 63n)
const INT64_MAX = (1n << 63n) - 1n

export { createDefaultProfile, resolveProfilesResult }

function parseInt64Seed(raw: string): bigint {
  if (typeof raw !== 'string') {
    throw new SimulatorValidationError('推演种子必须是 64 位整数十进制字符串。')
  }
  const trimmed = raw.trim()
  if (!/^[+-]?\d+$/.test(trimmed)) {
    throw new SimulatorValidationError('推演种子必须是 64 位整数十进制字符串。')
  }
  const value = BigInt(trimmed)
  if (value < INT64_MIN || value > INT64_MAX) {
    throw new SimulatorValidationError('推演种子必须是 64 位整数十进制字符串。')
  }
  return value
}

function validateCommon(
  formatVersion: number,
  environment: SimulatorEnvironmentDto,
  members: readonly SimulatorProfileDto[],
  nodeId: string,
  catalog: GameDataCatalog,
): void {
  if (!environment || !members) {
    throw new SimulatorValidationError('推演请求缺少环境或成员参数。')
  }
  if (
    formatVersion !== SIMULATOR_FORMAT_VERSION ||
    environment.formatVersion !== SIMULATOR_FORMAT_VERSION
  ) {
    throw new SimulatorValidationError('预演契约版本不匹配。')
  }
  if (environment.gameDataFingerprint !== catalog.fingerprint) {
    throw new SimulatorValidationError('环境快照配表指纹与当前配表不一致。')
  }
  if (members.length < 1 || members.length > 5) {
    throw new SimulatorValidationError('推演成员数必须位于 1~5。')
  }
  const memberIds: string[] = []
  for (let i = 0; i < members.length; i++) {
    const member = members[i]
    if (!member || member.formatVersion !== SIMULATOR_FORMAT_VERSION) {
      throw new SimulatorValidationError('角色档案契约版本不匹配。')
    }
    if (member.gameDataFingerprint !== catalog.fingerprint) {
      throw new SimulatorValidationError('角色档案配表指纹与当前配表不一致。')
    }
    const id = normalizeMemberId(member, i)
    if (memberIds.includes(id)) {
      throw new SimulatorValidationError('推演成员包含重复角色。')
    }
    memberIds.push(id)
    validateMember(member, i, catalog)
  }
  validateRespawnTargets(members, memberIds)
  validateEnvironment(environment, catalog)
  const worldNode = catalog.worldNodes.get(nodeId)
  if (!worldNode || worldNode.encounterIds.length === 0) {
    throw new SimulatorValidationError('目标节点不存在或没有讨伐遭遇池。')
  }
}

function requireEnvironmentNode(
  environment: SimulatorEnvironmentDto,
  nodeId: string,
): SimulatorEnvironmentNodeDto {
  const node = environment.nodes.find((entry) => compareOrdinal(entry.nodeId, nodeId) === 0)
  if (!node) {
    throw new SimulatorValidationError('环境快照缺少目标节点条目，拒绝推演。')
  }
  return node
}

function resolveCombatAction(nodeId: string, catalog: GameDataCatalog): CombatActionDefinition {
  const matches: CombatActionDefinition[] = []
  for (const def of catalog.actions.values()) {
    if (def.allowedNodeIds !== null && !def.allowedNodeIds.has(nodeId)) continue
    if (def.excludedNodeIds !== null && def.excludedNodeIds.has(nodeId)) continue
    matches.push(def)
  }
  if (matches.length === 0) {
    throw new SimulatorValidationError('目标节点没有可用讨伐行动。')
  }
  matches.sort((a, b) => compareOrdinal(a.id, b.id))
  return matches[0]
}

function resolveEncounter(
  nodeId: string,
  seed: bigint,
  catalog: GameDataCatalog,
): CombatEncounterDefinition {
  const worldNode = catalog.worldNodes.get(nodeId)
  const pool = worldNode?.encounterIds ?? []
  if (pool.length === 0) {
    throw new SimulatorValidationError('目标节点不存在或没有讨伐遭遇池。')
  }
  const encounterId = pool[seedModulo(seed, pool.length)]
  const encounter = catalog.encounters.get(encounterId)
  if (!encounter) {
    throw new SimulatorValidationError('目标节点遭遇池引用了缺失的遭遇定义。')
  }
  return encounter
}

function autoUseSlotToDto(slot: AutoUseSlotState): SimulatorAutoUseSlotDto {
  return {
    actionType: slot.actionType,
    slotIndex: slot.slotIndex,
    itemId: slot.itemId,
    quantity: slot.quantity,
    enabled: slot.enabled,
    remainingDurationSeconds: slot.remainingDurationSeconds,
    cooldownUntilUnixSeconds: slot.cooldownUntilUnixSeconds,
    group: slot.group,
  }
}

function toEnemyDtos(expedition: SimCombatExpeditionState | null): SimulatorExpeditionEnemyDto[] {
  if (!expedition || !expedition.enemies || expedition.enemies.length === 0) return []
  return expedition.enemies.map((e) => ({
    enemyId: e.enemyId,
    hitpoints: e.state.hitpoints,
    manapoints: e.state.manapoints,
  }))
}

function rebuildRemainingAssets(
  initial: readonly SimulatorAssetDto[],
  deltas: readonly SimulatorAssetDto[],
): SimulatorAssetDto[] {
  const remaining = new Map<string, number>()
  const keyMeta = new Map<string, { itemId: string; variant: string }>()
  for (const asset of initial) {
    const variant = asset.variant ?? ''
    const k = assetKeyString(asset.itemId, variant)
    remaining.set(k, (remaining.get(k) ?? 0) + asset.quantity)
    keyMeta.set(k, { itemId: asset.itemId, variant })
  }
  for (const delta of deltas) {
    const variant = delta.variant ?? ''
    const k = assetKeyString(delta.itemId, variant)
    remaining.set(k, (remaining.get(k) ?? 0) + delta.quantity)
    if (!keyMeta.has(k)) keyMeta.set(k, { itemId: delta.itemId, variant })
  }
  const out: SimulatorAssetDto[] = []
  for (const [k, qty] of remaining) {
    if (qty > 0) {
      const meta = keyMeta.get(k)!
      out.push({ itemId: meta.itemId, variant: meta.variant, quantity: qty })
    }
  }
  return out
}

function buildWalletMap(assets: readonly SimulatorAssetDto[]): Map<string, number> {
  const wallet = new Map<string, number>()
  for (const asset of assets) {
    const k = assetKeyString(asset.itemId, asset.variant ?? '')
    wallet.set(k, (wallet.get(k) ?? 0) + asset.quantity)
  }
  return wallet
}

function runSolo(
  profile: SimulatorProfileDto,
  request: SimulatorRunRequestDto,
  environmentNode: SimulatorEnvironmentNodeDto,
  actionDef: CombatActionDefinition,
  encounter: CombatEncounterDefinition,
  seed: bigint,
  catalog: GameDataCatalog,
  startTicks: bigint,
  targetTicks: bigint,
): SimulatorMemberResultDto {
  const state = restoreToState(
    profile,
    0,
    request.nodeId,
    environmentNode,
    catalog,
    request.environment.capturedAtUnixSeconds,
  )
  state.lastSimulatedAtTicks = startTicks
  const player = createSimPlayerRuntime(state)
  const wallet = buildWalletMap(profile.assets)

  const queued: SimActionState = {
    id: `sim:solo:${actionDef.id}`,
    actionId: actionDef.id,
    difficulty: request.difficulty,
    pvpEnabled: false,
    remainingRepeats: -1,
    completedRepeats: 0,
    progressSeconds: 0,
    status: ActionQueueStatus.Running,
    expedition: null,
    participation: null,
  }
  player.actions.push(queued)
  player.ensureActionReport(queued.id, actionDef.id, actionDef.name)
  queued.expedition = createCombatSearchState(
    player,
    encounter,
    CombatExpeditionPhase.InitialSearch,
    catalog,
    queued.completedRepeats,
    seed,
  )

  const result = advanceSimulationTo(player, targetTicks, catalog, wallet)
  const finalState = player.state
  const report = player.report
  const expedition = queued.expedition

  const rootExp: SimulatorRootExpDto = report
    ? {
        stamina: report.rootExp.stamina,
        meridians: report.rootExp.meridians,
        attack: report.rootExp.attack,
        bone: report.rootExp.bone,
        agility: report.rootExp.agility,
        consciousness: report.rootExp.consciousness,
      }
    : { stamina: 0, meridians: 0, attack: 0, bone: 0, agility: 0, consciousness: 0 }

  return {
    memberIndex: 0,
    playerId: player.playerId,
    displayName: finalState.displayName,
    hitpoints: finalState.hitpoints,
    maxHitpoints: finalState.maxHitpoints,
    manapoints: finalState.manapoints,
    maxManapoints: finalState.maxManapoints,
    regenCarrySeconds: finalState.regenCarrySeconds,
    combatSkills: combatSkillsToDto(finalState.combatSkills),
    combatBuffCarry: finalState.combatBuffCarry.map(combatBuffCarryToDto),
    completedRepeats: queued.completedRepeats,
    waveNumber: expedition?.waveNumber ?? 1,
    phase: expedition?.phase ?? CombatExpeditionPhase.InitialSearch,
    experience: report?.experience ?? 0,
    foundation: report?.foundation ?? 0,
    combatExperience: report?.combatExperience ?? 0,
    lifetimeExperience: finalState.lifetimeExperience - profile.lifetimeExperience,
    rootExp,
    cycles: report?.cycles ?? 0,
    encounters: report?.encounters ?? 0,
    normalKills: report?.normalKills ?? 0,
    eliteKills: report?.eliteKills ?? 0,
    bossKills: report?.bossKills ?? 0,
    deaths: report?.deaths ?? 0,
    items: report ? report.toItemsDto() : [],
    itemsLost: report ? report.toItemsLostDto() : [],
    assetDeltas: result.assetDeltas,
    remainingConsumableBalances: rebuildRemainingAssets(profile.assets, result.assetDeltas),
    combatAutoUseSlots: finalState.autoUseSlots
      .filter((slot) => slot.actionType === AutoUseBar.Combat)
      .map(autoUseSlotToDto),
    combatStats: report ? report.toCombatStatDtos() : [],
    enemies: toEnemyDtos(expedition),
    events: result.events.map(simEventToDto),
    droppedEventCount: result.droppedEventCount,
  }
}

function runParty(
  request: SimulatorRunRequestDto,
  environmentNode: SimulatorEnvironmentNodeDto,
  actionDef: CombatActionDefinition,
  encounter: CombatEncounterDefinition,
  seed: bigint,
  catalog: GameDataCatalog,
  _startTicks: bigint,
  targetTicks: bigint,
): SimulatorMemberResultDto[] {
  const count = request.members.length
  const memberStates: PlayerState[] = new Array(count)
  const snapshots: ActionParticipantSnapshot[] = new Array(count)
  for (let i = 0; i < count; i++) {
    memberStates[i] = restoreToState(
      request.members[i],
      i,
      request.nodeId,
      environmentNode,
      catalog,
      request.environment.capturedAtUnixSeconds,
    )
    snapshots[i] = buildActionParticipantSnapshot(memberStates[i], catalog)
  }

  const sessionId = `action:${deriveSessionGuidN(seed, request.nodeId, request.difficulty)}`
  const spec = createActionParticipationSpec(
    sessionId,
    encounter.id,
    request.nodeId,
    seed,
    request.environment.capturedAtUnixSeconds,
    snapshots,
    actionDef.id,
    environmentNode.effects.monsterWeakenRatio ?? 0,
    request.difficulty,
  )

  const workingWallet: { memberIndex: number; itemId: string; variant: string; quantity: number }[] =
    []
  for (let i = 0; i < count; i++) {
    for (const asset of request.members[i].assets) {
      if (asset.quantity <= 0) continue
      const item = catalog.items.get(asset.itemId)
      if (!item || !item.consumable) continue
      workingWallet.push({
        memberIndex: i,
        itemId: asset.itemId,
        variant: asset.variant ?? '',
        quantity: asset.quantity,
      })
    }
  }

  const engine = ActionParticipationSessionEngine.create(spec, catalog, workingWallet)
  const settlements = engine.advanceTo(targetTicks)
  const sessionAssetDeltas = engine.takeAssetDeltas()

  const results: SimulatorMemberResultDto[] = new Array(count)
  for (let i = 0; i < count; i++) {
    const memberPlayer = createSimPlayerRuntime(memberStates[i])
    const queued: SimActionState = {
      id: `${sessionId}:member:${i}`,
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
    memberPlayer.actions.push(queued)

    const playerId = memberPlayer.playerId
    const memberDeltas: SimulatorAssetDto[] = []
    const memberEvents: ReturnType<typeof simEventToDto>[] = []
    let droppedEvents = 0

    const matching = settlements
      .filter((entry) => entry.playerId === playerId)
      .sort((a, b) => a.sequence - b.sequence)
    for (const settlement of matching) {
      const applied = applyActionParticipationSettlement(memberPlayer, settlement, catalog)
      memberDeltas.push(...applied.assetDeltas)
      for (const ev of applied.events) memberEvents.push(simEventToDto(ev))
      droppedEvents += applied.droppedEventCount
    }

    for (const delta of sessionAssetDeltas) {
      if (delta.memberIndex !== i) continue
      memberDeltas.push({
        itemId: delta.itemId,
        variant: delta.variant,
        quantity: delta.quantityDelta,
      })
    }

    const finalState = memberPlayer.state
    const report = memberPlayer.report
    const expedition = memberPlayer.actions[0].expedition
    const core = finalState.coreAttributes
    const baseCore = request.members[i].coreAttributes
    const remaining = rebuildRemainingAssets(request.members[i].assets, memberDeltas)

    results[i] = {
      memberIndex: i,
      playerId,
      displayName: finalState.displayName,
      hitpoints: finalState.hitpoints,
      maxHitpoints: finalState.maxHitpoints,
      manapoints: finalState.manapoints,
      maxManapoints: finalState.maxManapoints,
      regenCarrySeconds: finalState.regenCarrySeconds,
      combatSkills: combatSkillsToDto(finalState.combatSkills),
      combatBuffCarry: finalState.combatBuffCarry.map(combatBuffCarryToDto),
      completedRepeats: queued.completedRepeats,
      waveNumber: expedition?.waveNumber ?? 1,
      phase: expedition?.phase ?? CombatExpeditionPhase.InitialSearch,
      experience: finalState.experience - request.members[i].experience,
      foundation: finalState.foundation - request.members[i].foundation,
      combatExperience: finalState.combatExperience - request.members[i].combatExperience,
      lifetimeExperience: finalState.lifetimeExperience - request.members[i].lifetimeExperience,
      rootExp: {
        stamina: core.staminaExp - baseCore.staminaExp,
        meridians: core.meridiansExp - baseCore.meridiansExp,
        attack: core.attackExp - baseCore.attackExp,
        bone: core.boneExp - baseCore.boneExp,
        agility: core.agilityExp - baseCore.agilityExp,
        consciousness: core.consciousnessExp - baseCore.consciousnessExp,
      },
      cycles: report?.cycles ?? 0,
      encounters: report?.encounters ?? 0,
      normalKills: report?.normalKills ?? 0,
      eliteKills: report?.eliteKills ?? 0,
      bossKills: report?.bossKills ?? 0,
      deaths: report?.deaths ?? 0,
      items: report ? report.toItemsDto() : [],
      itemsLost: report ? report.toItemsLostDto() : [],
      assetDeltas: memberDeltas,
      remainingConsumableBalances: remaining,
      combatAutoUseSlots: finalState.autoUseSlots
        .filter((slot) => slot.actionType === AutoUseBar.Combat)
        .map(autoUseSlotToDto),
      combatStats: report ? report.toCombatStatDtos() : [],
      enemies: toEnemyDtos(expedition),
      events: memberEvents,
      droppedEventCount: droppedEvents,
    }
  }
  return results
}

/** 列出配表中同时拥有遭遇池且匹配有效讨伐行动的节点 ID。 */
export function listCombatNodeIds(catalog: GameDataCatalog): string[] {
  const nodeIds: string[] = []
  for (const [nodeId, worldNode] of catalog.worldNodes) {
    if (worldNode.encounterIds.length === 0) continue
    try {
      resolveCombatAction(nodeId, catalog)
      nodeIds.push(nodeId)
    } catch {
      // 未绑定可用讨伐行动的地点不作为可模拟战斗节点
    }
  }
  nodeIds.sort(compareOrdinal)
  return nodeIds
}

export function runSimulation(
  request: SimulatorRunRequestDto,
  catalog: GameDataCatalog,
): SimulatorRunResultDto {
  validateCommon(request.formatVersion, request.environment, request.members, request.nodeId, catalog)
  if (request.difficulty < 0 || request.difficulty > 5) {
    throw new SimulatorValidationError('讨伐难度必须位于 0~5。')
  }
  if (
    !Number.isFinite(request.durationSeconds) ||
    request.durationSeconds < MIN_DURATION_SECONDS ||
    request.durationSeconds > MAX_DURATION_SECONDS
  ) {
    throw new SimulatorValidationError('推演时长必须位于 1~86400 秒。')
  }
  const seed = parseInt64Seed(request.seed)
  const environmentNode = requireEnvironmentNode(request.environment, request.nodeId)
  const actionDef = resolveCombatAction(request.nodeId, catalog)
  const encounter = resolveEncounter(request.nodeId, seed, catalog)
  const startTicks = ticksFromUnixSeconds(request.environment.capturedAtUnixSeconds)
  const targetTicks = addDurationTruncTicks(startTicks, request.durationSeconds)

  const members =
    request.members.length === 1
      ? [
          runSolo(
            request.members[0],
            request,
            environmentNode,
            actionDef,
            encounter,
            seed,
            catalog,
            startTicks,
            targetTicks,
          ),
        ]
      : runParty(
          request,
          environmentNode,
          actionDef,
          encounter,
          seed,
          catalog,
          startTicks,
          targetTicks,
        )

  return {
    formatVersion: SIMULATOR_FORMAT_VERSION,
    nodeId: request.nodeId,
    difficulty: request.difficulty,
    seed: request.seed,
    requestedDurationSeconds: request.durationSeconds,
    simulatedSeconds: request.durationSeconds,
    members,
  }
}

void resolvePlayerAttributesFromState
