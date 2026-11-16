import { useEffect, useRef, type RefObject } from 'react'
import { collectScheduledCombatEvents } from '../game/combatFeedback'
import { isDocumentCurrentlyVisible, useDocumentVisible } from '../game/combatVfxStore'
import { useGameStore } from '../game/gameStore'
import type {
  CombatAbilityDefinition,
  CombatRecentEvent,
  ExpeditionProgress,
} from '../game/types'
import { gameLogService } from '../logging/gameLogService'
import {
  CombatVfxRuntime,
  type ActiveCastSpec,
  type ActiveFormationSpec,
} from './index'
import {
  FORMATION_BUFF_TO_VFX,
  INSTANT_CONSUMABLE_ITEM_IDS,
  PILL_BUFF_TO_ITEM,
  detectDuelZhanyuMorph,
  detectZhanyuTayunMorph,
  groupCombatEventBatches,
  normalizeActorCanonicalKey,
  parseAutoUseActivatedItemId,
  resolveZeroDamageSupportExpansion,
} from './vfxRouting'

const CONSUMABLE_DEDUP_WINDOW_MS = 1200
const YUEQIAN_MORPH_WINDOW_MS = 5200
const TAYUN_LOG_WINDOW_MS = 2000

function resolveHighestThreatEnemyTargetId(expedition: ExpeditionProgress | null): string | null {
  const enemies = expedition?.enemies
  if (!enemies || enemies.length === 0) return null
  let bestIdx = -1
  let bestThreat = -Infinity
  for (let i = 0; i < enemies.length; i++) {
    const enemy = enemies[i]
    if (enemy.hitpoints <= 0) continue
    if (enemy.threat > bestThreat) {
      bestThreat = enemy.threat
      bestIdx = i
    }
  }
  return bestIdx >= 0 ? `enemy:${bestIdx}` : null
}

function resolveLowestHpAllyId(
  expedition: ExpeditionProgress | null,
  state: ReturnType<typeof useGameStore.getState>,
  side: 'ally' | 'enemy',
  selfIndex: number | null,
): string | null {
  if (!expedition) return null
  if (side === 'enemy') {
    const enemies = expedition.enemies ?? []
    let bestId: string | null = null
    let bestRatio = Infinity
    for (let i = 0; i < enemies.length; i++) {
      const e = enemies[i]
      if (e.hitpoints <= 0) continue
      const maxHp = Math.max(1, e.maxHitpoints ?? e.hitpoints)
      const ratio = e.hitpoints / maxHp
      if (ratio < bestRatio) {
        bestRatio = ratio
        bestId = `enemy:${i}`
      }
    }
    return bestId
  }

  let bestId: string | null = null
  let bestRatio = Infinity
  const playerHp = expedition.playerHitpoints ?? state.combatResources?.hitpoints ?? 0
  const playerMaxHp = Math.max(1, state.combatResources?.maxHitpoints ?? playerHp)
  if (playerHp > 0) {
    bestRatio = playerHp / playerMaxHp
    bestId = 'player'
  }
  for (const ally of expedition.allies ?? []) {
    if (selfIndex !== null && ally.memberIndex === selfIndex) continue
    if (ally.hitpoints <= 0) continue
    const maxHp = Math.max(1, ally.maxHitpoints ?? ally.hitpoints)
    const ratio = ally.hitpoints / maxHp
    if (ratio < bestRatio) {
      bestRatio = ratio
      bestId = `ally:${ally.memberIndex}`
    }
  }
  return bestId
}

/**
 * 正式战斗页 Canvas V2 特效联动 Hook。
 *
 * 将 `useGameStore` 战斗/远征/斗法状态与 `gameLogService` 原始增量日志桥接到
 * `CombatVfxRuntime`，覆盖全部 55 个共享注册特效：
 * 1. `feedback.castRing`：本机 / 队友 / 敌方 / 斗法镜像的吟唱地面法阵同步；
 * 2. 4 种常驻阵盘（聚灵 / 迷踪 / 天雷 / 血煞）跟随战内存活状态自动启停 + 2 种反击触发态联动；
 * 3. 21 主动功法 22 态（含同帧 AoE 批聚拢、吸血自疗排重、群辅全队展开与「斩玉 → 踏云」移形化态）及普攻 / 暴击 / 破拆招反馈；
 * 4. 10 门内功心法入战/斗法首帧周天护体演出 + `trait.xuyi`/`trait.ranhun` 特质反应特效；
 * 5. `item.leiji-talisman` + 6 种战斗丹药的双通道去重激活、战内倒地复活（`feedback.revive`）与搜寻散尸退场（`feedback.defeat`）。
 */
export interface UseCombatVfxOptions {
  /** 详情页签是否处于隐藏态（应用内切换至其它页签） */
  readonly hidden?: boolean
  /** 用户是否在左上角开启战斗特效 */
  readonly enabled?: boolean
}

export function useCombatVfx(
  shellRef: RefObject<HTMLDivElement | null>,
  overlayRef: RefObject<HTMLDivElement | null>,
  groundOverlayRef?: RefObject<HTMLDivElement | null>,
  options?: UseCombatVfxOptions,
): RefObject<CombatVfxRuntime | null> {
  const hidden = options?.hidden ?? false
  const enabled = options?.enabled ?? true
  const documentVisible = useDocumentVisible()
  const active = !hidden && enabled && documentVisible
  const activeRef = useRef(active)
  activeRef.current = active
  const syncActiveStateRef = useRef<((nextActive: boolean) => void) | null>(null)
  const runtimeRef = useRef<CombatVfxRuntime | null>(null)

  useEffect(() => {
    const shellEl = shellRef.current
    const skyOverlayEl = overlayRef.current
    const groundOverlayEl = groundOverlayRef?.current ?? null
    if (!shellEl || !skyOverlayEl) return

    const runtime = new CombatVfxRuntime(shellEl, skyOverlayEl, groundOverlayEl)
    runtimeRef.current = runtime

    let playbackActive = activeRef.current
    runtime.setPlaybackActive(playbackActive)

    let abilitiesSource: readonly CombatAbilityDefinition[] | null = null
    let abilitiesMap = new Map<string, CombatAbilityDefinition>()

    let prevEntryId: string | null = null
    let prevPhase: string | null = null
    let prevWaveNumber: number | null = null
    let duelLastElapsed: number | null = null
    let lastTayunLogAtMs = -Infinity
    let introPlayedScopeKey: string | null = null

    const handledEventKeys = new Set<string>()
    let prevSlotStates: Array<{ itemId: string | null; cdMs: number }> = []
    let prevPillBuffs = new Map<string, number>()
    let prevYixingStacks = new Map<string, number>()
    const recentYueqianAt = new Map<string, number>()
    let prevHp = new Map<string, number>()
    const lastConsumablePlayAt = new Map<string, number>()

    let pendingSyncRafId: number | null = null
    let pendingSyncCasts: ActiveCastSpec[] = []
    let pendingSyncSelfIndex: number | null = null
    let pendingSyncFormActive = false
    let pendingSyncFormations: ActiveFormationSpec[] = []

    function getAbilityMap(list: readonly CombatAbilityDefinition[]): Map<string, CombatAbilityDefinition> {
      if (abilitiesSource !== list) {
        abilitiesSource = list
        abilitiesMap = new Map(list.map(item => [item.abilityId, item]))
      }
      return abilitiesMap
    }

    function flushDomSync(): void {
      pendingSyncRafId = null
      if (!playbackActive) return
      runtime.syncCastRings(pendingSyncCasts, pendingSyncSelfIndex)
      runtime.syncFormations(pendingSyncFormActive, pendingSyncFormations, pendingSyncSelfIndex)
    }

    function queueDomSync(
      casts: ActiveCastSpec[],
      formActive: boolean,
      formations: ActiveFormationSpec[],
      selfIndex: number | null,
    ): void {
      if (!playbackActive) return
      pendingSyncCasts = casts
      pendingSyncFormActive = formActive
      pendingSyncFormations = formations
      pendingSyncSelfIndex = selfIndex
      if (typeof requestAnimationFrame !== 'function') {
        flushDomSync()
        return
      }
      if (pendingSyncRafId === null) {
        pendingSyncRafId = requestAnimationFrame(flushDomSync)
      }
    }

    function cancelPendingDomSync(): void {
      if (pendingSyncRafId !== null && typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(pendingSyncRafId)
      }
      pendingSyncRafId = null
    }

    function resetEncounterState(): void {
      handledEventKeys.clear()
      prevSlotStates = []
      prevPillBuffs.clear()
      prevYixingStacks.clear()
      recentYueqianAt.clear()
      prevHp.clear()
      lastConsumablePlayAt.clear()
      duelLastElapsed = null
      lastTayunLogAtMs = -Infinity
      introPlayedScopeKey = null
      runtime.resetComboCounters()
    }

    function emitConsumableWithDedup(
      itemId: string,
      actorId: string,
      selfIndex: number | null,
      preferredEnemyTargetId: string | null,
    ): void {
      const canonicalActor = normalizeActorCanonicalKey(actorId, selfIndex)
      if (!canonicalActor) return
      const dedupKey = `${canonicalActor}|${itemId}`
      const nowMs = performance.now()
      const lastAt = lastConsumablePlayAt.get(dedupKey) ?? -Infinity
      if (nowMs - lastAt < CONSUMABLE_DEDUP_WINDOW_MS) return
      lastConsumablePlayAt.set(dedupKey, nowMs)
      runtime.schedule(0, () => {
        runtime.playConsumable(itemId, actorId, selfIndex, preferredEnemyTargetId)
      })
    }

    function snapshotCurrentYixing(
      expedition: ExpeditionProgress | null,
      selfIndex: number | null,
    ): Map<string, number> {
      const map = new Map<string, number>()
      const playerBuff = expedition?.playerBuffs?.find(b => b.buffId === 'buff.yixing')
      if (playerBuff && playerBuff.stacks > 0) {
        map.set('player', playerBuff.stacks)
      }
      for (const ally of expedition?.allies ?? []) {
        if (selfIndex !== null && ally.memberIndex === selfIndex) continue
        const allyBuff = ally.buffs?.find(b => b.buffId === 'buff.yixing')
        if (allyBuff && allyBuff.stacks > 0) {
          map.set(`ally:${ally.memberIndex}`, allyBuff.stacks)
        }
      }
      return map
    }

    function collectActiveCasts(
      state: ReturnType<typeof useGameStore.getState>,
      expedition: ExpeditionProgress,
      phase: string,
      selfIndex: number | null,
      abilityById: Map<string, CombatAbilityDefinition>,
    ): ActiveCastSpec[] {
      const casts: ActiveCastSpec[] = []
      const battleElapsed = expedition.battleElapsedSeconds ?? 0

      if (phase === 'combat') {
        const playerHp = expedition.playerHitpoints ?? state.combatResources?.hitpoints ?? 0
        const playerCastId = state.combatResources?.castingAbilityId ?? null
        if (playerCastId && playerHp > 0) {
          const ability = abilityById.get(playerCastId) ?? null
          const castMs = state.combatResources?.castTotalMilliseconds ?? ability?.castMilliseconds ?? 1200
          const remainingMs = state.combatResources?.castRemainingMilliseconds ?? castMs
          casts.push({
            actorId: 'player',
            abilityId: playerCastId,
            castMs,
            remainingMs,
            element: ability?.element,
            ability,
          })
        }

        for (const ally of expedition.allies ?? []) {
          if (selfIndex !== null && ally.memberIndex === selfIndex) continue
          if (ally.hitpoints <= 0 || !ally.pendingAbilityId) continue
          const ability = abilityById.get(ally.pendingAbilityId) ?? null
          const castMs = Math.max(
            200,
            Math.round(ally.pendingActionDurationSeconds * 1000 || ability?.castMilliseconds || 1200),
          )
          const remainingMs = Math.max(0, Math.round((ally.nextAttackAtSeconds - battleElapsed) * 1000))
          casts.push({
            actorId: `ally:${ally.memberIndex}`,
            abilityId: ally.pendingAbilityId,
            castMs,
            remainingMs,
            element: ability?.element,
            ability,
          })
        }

        const enemies = expedition.enemies ?? []
        for (let i = 0; i < enemies.length; i++) {
          const enemy = enemies[i]
          if (enemy.hitpoints <= 0 || !enemy.pendingAbilityId) continue
          const ability = abilityById.get(enemy.pendingAbilityId) ?? null
          const castMs = Math.max(
            200,
            Math.round(enemy.pendingActionDurationSeconds * 1000 || ability?.castMilliseconds || 1200),
          )
          const remainingMs = Math.max(0, Math.round((enemy.nextAttackAtSeconds - battleElapsed) * 1000))
          casts.push({
            actorId: `enemy:${i}`,
            abilityId: enemy.pendingAbilityId,
            castMs,
            remainingMs,
            element: ability?.element,
            ability,
          })
        }
      } else if (phase === 'duel' && expedition.duel) {
        const duel = expedition.duel
        const events = duel.events ?? []
        const seenSide = new Set<string>()
        for (const ev of events) {
          if (ev.atSeconds <= battleElapsed) continue
          if (ev.abilityId && (ev.abilityId.startsWith('item.') || ev.abilityId.startsWith('trait.'))) continue
          if (!ev.abilityId && ev.attackerIsInitiator === ev.targetIsInitiator) continue
          const sideActorId = ev.attackerIsInitiator === duel.isInitiator ? 'player' : 'enemy:0'
          if (seenSide.has(sideActorId)) continue
          seenSide.add(sideActorId)
          if (!ev.abilityId) continue
          const ability = abilityById.get(ev.abilityId) ?? null
          const castMs = ability?.castMilliseconds ?? 0
          if (castMs <= 0) continue
          const castSec = castMs / 1000
          if (battleElapsed >= ev.atSeconds - castSec) {
            casts.push({
              actorId: sideActorId,
              abilityId: ev.abilityId,
              castMs,
              remainingMs: Math.max(0, Math.round((ev.atSeconds - battleElapsed) * 1000)),
              element: ability?.element,
              ability,
            })
          }
          if (seenSide.size >= 2) break
        }
      }

      return casts
    }

    function collectActiveFormations(
      state: ReturnType<typeof useGameStore.getState>,
      expedition: ExpeditionProgress,
      phase: string,
      selfIndex: number | null,
    ): ActiveFormationSpec[] {
      if (phase !== 'combat' && phase !== 'duel') return []
      const result: ActiveFormationSpec[] = []
      const playerHp = expedition.playerHitpoints ?? state.combatResources?.hitpoints ?? 0

      if (playerHp > 0) {
        const seenSelfBuffs = new Set<string>()
        for (const buff of expedition.playerBuffs ?? []) {
          if (FORMATION_BUFF_TO_VFX[buff.buffId] && !seenSelfBuffs.has(buff.buffId)) {
            seenSelfBuffs.add(buff.buffId)
            result.push({ actorId: 'player', buffId: buff.buffId })
          }
        }
        for (const buff of state.buffs ?? []) {
          if (FORMATION_BUFF_TO_VFX[buff.buffId] && !seenSelfBuffs.has(buff.buffId)) {
            seenSelfBuffs.add(buff.buffId)
            result.push({ actorId: 'player', buffId: buff.buffId })
          }
        }
      }

      for (const ally of expedition.allies ?? []) {
        if (selfIndex !== null && ally.memberIndex === selfIndex) continue
        if (ally.hitpoints <= 0) continue
        for (const buff of ally.buffs ?? []) {
          if (FORMATION_BUFF_TO_VFX[buff.buffId]) {
            result.push({ actorId: `ally:${ally.memberIndex}`, buffId: buff.buffId })
          }
        }
      }

      return result
    }

    function processSnapshot(
      state: ReturnType<typeof useGameStore.getState>,
      isInitialBaseline: boolean,
    ): void {
      if (!playbackActive) return
      const current = state.queue.entries[0] ?? null
      const isCombat = current?.type === 'combat' && current.executionKind === 'combat-encounter'
      const expedition = isCombat ? (current.expedition ?? null) : null
      const entryId = isCombat ? (current?.entryId ?? null) : null
      const phase = expedition?.phase ?? null
      const waveNumber = expedition?.waveNumber ?? null
      const selfIndex = expedition?.selfIndex ?? null
      const abilityById = getAbilityMap(state.abilities)

      // 1. 离战或切回初搜/斗法撮合等待：彻底清台并重置追踪器
      if (!isCombat || !expedition || !phase || phase === 'initial-search') {
        if (prevEntryId !== null || prevPhase === 'combat' || prevPhase === 'duel') {
          cancelPendingDomSync()
          runtime.clearBattle()
          resetEncounterState()
        }
        prevEntryId = entryId
        prevPhase = phase
        prevWaveNumber = waveNumber
        return
      }

      // 2. 切换战斗行动条目：清理上一场残留并重建基线
      if (prevEntryId !== null && entryId !== prevEntryId) {
        cancelPendingDomSync()
        runtime.clearBattle()
        resetEncounterState()
      }

      const justLeftCombat = (prevPhase === 'combat' || prevPhase === 'duel') && (phase === 'search' || phase === 'respawn')
      const enteredNewWave =
        phase === 'combat' &&
        (prevPhase === 'search' ||
          prevPhase === 'respawn' ||
          (prevWaveNumber !== null && waveNumber !== null && waveNumber !== prevWaveNumber))

      if (enteredNewWave) {
        runtime.clearScheduled()
        handledEventKeys.clear()
        recentYueqianAt.clear()
        prevYixingStacks.clear()
      }

      // 2.5 入战 / 进入斗法首帧内功心法护体运转演出（装配 0 号位心法的我方 / 队友 / 斗法镜像各演 1 次）
      if (phase === 'combat' || phase === 'duel') {
        const introScopeKey = `${entryId ?? 'none'}|${phase}|${waveNumber ?? 0}`
        if (isInitialBaseline) {
          introPlayedScopeKey = introScopeKey
        } else if (
          ((phase === 'combat' && prevPhase !== 'combat') || (phase === 'duel' && prevPhase !== 'duel')) &&
          introPlayedScopeKey !== introScopeKey
        ) {
          introPlayedScopeKey = introScopeKey
          const actors: Array<{ actorId: string; abilityId: string | null | undefined }> = []
          const playerHpNow = expedition.playerHitpoints ?? state.combatResources?.hitpoints ?? 0
          if (playerHpNow > 0) {
            actors.push({ actorId: 'player', abilityId: state.combat?.abilitySlots?.[0]?.abilityId })
          }
          for (const ally of expedition.allies ?? []) {
            if (selfIndex !== null && ally.memberIndex === selfIndex) continue
            if (ally.hitpoints <= 0) continue
            actors.push({ actorId: `ally:${ally.memberIndex}`, abilityId: ally.skills?.[0]?.abilityId })
          }
          if (phase === 'duel' && expedition.duel && expedition.duel.opponentHitpoints > 0) {
            actors.push({
              actorId: 'enemy:0',
              abilityId: expedition.duel.opponentSkills?.[0]?.abilityId,
            })
          }
          let staggerIdx = 0
          for (const entry of actors) {
            if (!entry.abilityId) continue
            const internalAbility = abilityById.get(entry.abilityId)
            if (internalAbility?.discipline !== 'internal') continue
            if (!runtime.hasVfx(entry.abilityId)) continue
            const actorId = entry.actorId
            const abilityId = entry.abilityId
            runtime.schedule(staggerIdx * 140, () => {
              runtime.playInternalArtIntro(abilityId, actorId, selfIndex, internalAbility)
            })
            staggerIdx++
          }
        }
      }

      // 3. 移形栈快照与远征最近事件处理（含结束一击尾帧，确保斩杀波不被吞）
      const currYixingStacks = snapshotCurrentYixing(expedition, selfIndex)
      const shouldConsumeRecentEvents =
        (phase === 'combat' || justLeftCombat) && (expedition.recentEvents?.length ?? 0) > 0

      if (shouldConsumeRecentEvents && expedition.recentEvents) {
        const scheduled = collectScheduledCombatEvents(
          expedition.recentEvents,
          () => true,
          handledEventKeys,
        )
        for (const item of scheduled) {
          handledEventKeys.add(item.key)
        }
        if (handledEventKeys.size > 1200) {
          const tail = Array.from(handledEventKeys).slice(-600)
          handledEventKeys.clear()
          for (const key of tail) handledEventKeys.add(key)
        }

        if (!isInitialBaseline && scheduled.length > 0) {
          const batches = groupCombatEventBatches(scheduled, selfIndex)
          const nowMs = performance.now()
          for (const batch of batches) {
            if (batch.abilityId === 'ability.yueqian') {
              recentYueqianAt.set(batch.canonicalAttackerKey, nowMs)
            }
            const ability = batch.abilityId ? (abilityById.get(batch.abilityId) ?? null) : null
            let morphed = false
            if (batch.abilityId === 'ability.zhanyu') {
              morphed = detectZhanyuTayunMorph({
                targetCount: batch.targetIds.length,
                hasYixingBuff: (currYixingStacks.get(batch.canonicalAttackerKey) ?? 0) > 0,
                yixingWasConsumed:
                  (prevYixingStacks.get(batch.canonicalAttackerKey) ?? 0) >
                  (currYixingStacks.get(batch.canonicalAttackerKey) ?? 0),
                hasRecentYueqian:
                  nowMs - (recentYueqianAt.get(batch.canonicalAttackerKey) ?? -Infinity) <= YUEQIAN_MORPH_WINDOW_MS,
                hasRecentTayunLog:
                  batch.canonicalAttackerKey === 'player' && nowMs - lastTayunLogAtMs <= TAYUN_LOG_WINDOW_MS,
              })
            }
            const sideForTarget: 'ally' | 'enemy' =
              batch.attackerId === 'enemy' || batch.attackerId.startsWith('enemy:') ? 'enemy' : 'ally'
            const lowestHpAllyId =
              resolveZeroDamageSupportExpansion(batch, ability?.targetType, selfIndex) === 'lowest-hp-ally'
                ? resolveLowestHpAllyId(expedition, state, sideForTarget, selfIndex)
                : null
            runtime.schedule(batch.delayMs, () => {
              if (batch.abilityId && INSTANT_CONSUMABLE_ITEM_IDS.has(batch.abilityId)) {
                const targetId = batch.targetIds[0] ?? (batch.canonicalAttackerKey === 'player' ? 'enemy:0' : 'player')
                if (batch.canonicalAttackerKey === 'player') {
                  emitConsumableWithDedup(batch.abilityId, 'player', selfIndex, targetId)
                } else {
                  runtime.playConsumable(batch.abilityId, batch.attackerId, selfIndex, targetId)
                }
                return
              }
              runtime.playCombatBatch(batch, { selfIndex, ability, morphed, lowestHpAllyId })
            })
          }
        }
      }
      prevYixingStacks = currYixingStacks

      // 4. 斗法（PVP）剧本回放事件调度
      if (phase === 'duel' && expedition.duel) {
        const duel = expedition.duel
        const battleElapsed = expedition.battleElapsedSeconds ?? 0
        if (isInitialBaseline) {
          duelLastElapsed = battleElapsed
        } else {
          if (prevPhase !== 'duel' || duelLastElapsed === null) {
            duelLastElapsed = battleElapsed <= 2.5 ? -1e-6 : battleElapsed
          }
          const rawEvents = duel.events ?? []
          const crossed: Array<{ index: number; mapped: CombatRecentEvent }> = []
          for (let i = 0; i < rawEvents.length; i++) {
            const ev = rawEvents[i]
            if (!ev.abilityId && ev.attackerIsInitiator === ev.targetIsInitiator && (ev.damage ?? 0) <= 0) continue
            if (ev.atSeconds > duelLastElapsed && ev.atSeconds <= battleElapsed) {
              crossed.push({
                index: i,
                mapped: {
                  at: ev.atSeconds,
                  attackerId: ev.attackerIsInitiator === duel.isInitiator ? 'player' : 'enemy:0',
                  targetId: ev.targetIsInitiator === duel.isInitiator ? 'player' : 'enemy:0',
                  damage: ev.damage ?? 0,
                  hit: ev.hit !== false,
                  critical: ev.critical,
                  broken: ev.broken,
                  parried: ev.parried,
                  abilityId: ev.abilityId ?? null,
                  targetHp: ev.targetHitpointsAfter,
                },
              })
            }
          }
          if (crossed.length > 0) {
            const windowCrossed = crossed.length > 16 ? crossed.slice(-16) : crossed
            const mappedList = windowCrossed.map(item => item.mapped)
            const scheduled = collectScheduledCombatEvents(mappedList, () => true, handledEventKeys)
            for (const item of scheduled) {
              handledEventKeys.add(item.key)
            }
            if (handledEventKeys.size > 1200) {
              const tail = Array.from(handledEventKeys).slice(-600)
              handledEventKeys.clear()
              for (const key of tail) handledEventKeys.add(key)
            }
            // 将每条 ScheduledCombatEvent 对应回原始 duel.events 下标以推算「跃迁→踏云」
            const origIndexByEvent = new Map<CombatRecentEvent, number>()
            for (const item of windowCrossed) {
              origIndexByEvent.set(item.mapped, item.index)
            }
            const batches = groupCombatEventBatches(scheduled, selfIndex)
            for (const batch of batches) {
              const ability = batch.abilityId ? (abilityById.get(batch.abilityId) ?? null) : null
              const firstEvent = batch.events[0]
              const origIndex = firstEvent ? origIndexByEvent.get(firstEvent) : undefined
              const morphed =
                batch.abilityId === 'ability.zhanyu' && origIndex !== undefined
                  ? detectDuelZhanyuMorph(rawEvents, origIndex)
                  : false
              const sideForTarget: 'ally' | 'enemy' =
                batch.attackerId === 'enemy' || batch.attackerId.startsWith('enemy:') ? 'enemy' : 'ally'
              const lowestHpAllyId =
                resolveZeroDamageSupportExpansion(batch, ability?.targetType, selfIndex) === 'lowest-hp-ally'
                  ? resolveLowestHpAllyId(expedition, state, sideForTarget, selfIndex)
                  : null
              runtime.schedule(batch.delayMs, () => {
                if (batch.abilityId && INSTANT_CONSUMABLE_ITEM_IDS.has(batch.abilityId)) {
                  const targetId = batch.canonicalAttackerKey === 'player' ? 'enemy:0' : 'player'
                  if (batch.canonicalAttackerKey === 'player') {
                    emitConsumableWithDedup(batch.abilityId, 'player', selfIndex, targetId)
                  } else {
                    runtime.playConsumable(batch.abilityId, 'enemy:0', selfIndex, targetId)
                  }
                  return
                }
                runtime.playCombatBatch(batch, { selfIndex, ability, morphed, lowestHpAllyId })
              })
            }
          }
          duelLastElapsed = battleElapsed
        }
      } else {
        duelLastElapsed = null
      }

      // 5. 战斗消耗品双通道追踪：槽位冷却跳升（雷击符+丹药）与丹药 Buff 新增/续期（玩家+队友）
      const slots = state.queue.autoUseSlots?.combat ?? []
      const cooldowns = state.combatResources?.consumableCooldownMilliseconds ?? []
      const nextSlotStates: Array<{ itemId: string | null; cdMs: number }> = []
      const bestEnemyTargetId = resolveHighestThreatEnemyTargetId(expedition)

      for (let i = 0; i < slots.length; i++) {
        const itemId = slots[i]?.itemId ?? null
        const cdMs = cooldowns[i] ?? 0
        nextSlotStates.push({ itemId, cdMs })
        if (isInitialBaseline || phase !== 'combat') continue
        const prevSlot = prevSlotStates[i]
        if (
          itemId &&
          INSTANT_CONSUMABLE_ITEM_IDS.has(itemId) &&
          prevSlot &&
          prevSlot.itemId === itemId &&
          cdMs > prevSlot.cdMs + 250
        ) {
          emitConsumableWithDedup(itemId, 'player', selfIndex, bestEnemyTargetId)
        }
      }
      prevSlotStates = nextSlotStates

      const nextPillBuffs = new Map<string, number>()
      if (phase === 'combat') {
        for (const buff of expedition.playerBuffs ?? []) {
          const itemId = PILL_BUFF_TO_ITEM[buff.buffId]
          if (!itemId) continue
          const key = `player|${buff.buffId}`
          nextPillBuffs.set(key, buff.expiresAtSeconds)
          if (!isInitialBaseline) {
            const prevExp = prevPillBuffs.get(key)
            if (prevExp === undefined || buff.expiresAtSeconds > prevExp + 0.5) {
              emitConsumableWithDedup(itemId, 'player', selfIndex, bestEnemyTargetId)
            }
          }
        }
        for (const ally of expedition.allies ?? []) {
          if (selfIndex !== null && ally.memberIndex === selfIndex) continue
          if (ally.hitpoints <= 0) continue
          for (const buff of ally.buffs ?? []) {
            const itemId = PILL_BUFF_TO_ITEM[buff.buffId]
            if (!itemId) continue
            const actorId = `ally:${ally.memberIndex}`
            const key = `${actorId}|${buff.buffId}`
            nextPillBuffs.set(key, buff.expiresAtSeconds)
            if (!isInitialBaseline) {
              const prevExp = prevPillBuffs.get(key)
              if (prevExp === undefined || buff.expiresAtSeconds > prevExp + 0.5) {
                emitConsumableWithDedup(itemId, actorId, selfIndex, bestEnemyTargetId)
              }
            }
          }
        }
      }
      prevPillBuffs = nextPillBuffs

      // 6. 战内倒地复活光效（玩家 / 队友 HP 从 <=0 回到 >0）
      const nextHp = new Map<string, number>()
      const playerHp = expedition.playerHitpoints ?? state.combatResources?.hitpoints ?? 0
      nextHp.set('player', playerHp)
      for (const ally of expedition.allies ?? []) {
        if (selfIndex !== null && ally.memberIndex === selfIndex) continue
        nextHp.set(`ally:${ally.memberIndex}`, ally.hitpoints)
      }
      if (!isInitialBaseline && phase === 'combat' && entryId === prevEntryId) {
        for (const [actorId, hp] of nextHp.entries()) {
          const prevVal = prevHp.get(actorId)
          if (prevVal !== undefined && prevVal <= 0 && hp > 0) {
            runtime.schedule(0, () => {
              runtime.playRevive(actorId, selfIndex)
            })
          }
        }
      }
      prevHp = nextHp

      // 7. 施法圈与持续阵盘状态同步（combat -> search / respawn 立即停阵与收环，但不清空散尸粒子）
      if (justLeftCombat) {
        cancelPendingDomSync()
        runtime.stopAllCastRings()
        runtime.stopAllFormations()
      } else {
        const activeCasts = collectActiveCasts(state, expedition, phase, selfIndex, abilityById)
        const activeFormations = collectActiveFormations(state, expedition, phase, selfIndex)
        const formActive = phase === 'combat' || phase === 'duel'
        queueDomSync(activeCasts, formActive, activeFormations, selfIndex)
      }

      prevEntryId = entryId
      prevPhase = phase
      prevWaveNumber = waveNumber
    }

    function syncActiveState(nextActive: boolean): void {
      if (nextActive === playbackActive) return
      playbackActive = nextActive
      if (!nextActive) {
        cancelPendingDomSync()
        runtime.setPlaybackActive(false)
        resetEncounterState()
        prevEntryId = null
        prevPhase = null
        prevWaveNumber = null
        return
      }
      resetEncounterState()
      prevEntryId = null
      prevPhase = null
      prevWaveNumber = null
      runtime.setPlaybackActive(true)
      processSnapshot(useGameStore.getState(), true)
    }

    syncActiveStateRef.current = syncActiveState

    if (playbackActive) {
      processSnapshot(useGameStore.getState(), true)
    }

    const unsubscribeStore = useGameStore.subscribe((state) => {
      if (!playbackActive) return
      processSnapshot(state, false)
    })

    const unsubscribeRawLogs = gameLogService.subscribeRawBatch((playerId, events) => {
      if (!playbackActive) return
      const state = useGameStore.getState()
      if (state.playerId !== playerId) return
      const current = state.queue.entries[0] ?? null
      const expedition =
        current?.type === 'combat' && current.executionKind === 'combat-encounter'
          ? (current.expedition ?? null)
          : null
      if (!expedition || (expedition.phase !== 'combat' && expedition.phase !== 'duel')) return

      const selfIndex = expedition.selfIndex ?? null
      const bestEnemyTargetId = resolveHighestThreatEnemyTargetId(expedition)

      for (const ev of events) {
        if (ev.template.includes('踏云')) {
          lastTayunLogAtMs = performance.now()
        }
        const itemId = parseAutoUseActivatedItemId(ev.template)
        if (itemId) {
          emitConsumableWithDedup(itemId, 'player', selfIndex, bestEnemyTargetId)
        }
      }
    })

    const handleVisibilityChange = (): void => {
      // 浏览器切后台瞬间同步清场，防止 rAF 暂停期间继续累积回调或定时器
      if (!isDocumentCurrentlyVisible()) {
        syncActiveState(false)
      }
    }
    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', handleVisibilityChange)
    }

    return () => {
      if (typeof document !== 'undefined') {
        document.removeEventListener('visibilitychange', handleVisibilityChange)
      }
      syncActiveStateRef.current = null
      unsubscribeStore()
      unsubscribeRawLogs()
      cancelPendingDomSync()
      runtime.dispose()
      runtimeRef.current = null
    }
  }, [shellRef, overlayRef, groundOverlayRef])

  useEffect(() => {
    syncActiveStateRef.current?.(active)
  }, [active])

  return runtimeRef
}
