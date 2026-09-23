/**
 * 战斗特效 React 桥接层 (useCombatVfx)
 *
 * 职责：
 * - 管理 CombatVfxRuntime 生命周期（随 CombatArena 挂载创建、卸载 dispose）；
 * - 把真实战斗快照中的「出手事件」翻译成特效播放调度：
 *   · 玩家吟唱：combatResources.castingAbilityId 出现即播聚灵法阵，
 *     完成时刻（serverNow + castRemainingMilliseconds）播技能粒子演出；
 *   · 敌方行动：ExpeditionEnemy.pendingAbilityId 出现即播法阵，
 *     nextAttackAtSeconds 到达时播技能演出（pendingAbilityId 为空则普攻爆闪）；
 *   · 玩家普攻：expedition.playerNextAttackAtSeconds 到达时播普攻爆闪；
 *   · 斗法回放：duel.events[].abilityId + atSeconds 按剧本时刻逐条调度；
 * - signature 防重：同一出手事件在任意 rerender/快照增量下只播放一次；
 * - 纯表现层：不读写任何战斗状态，结算由服务端快照驱动既有组件完成。
 */
import { useEffect, useRef, type RefObject } from 'react'
import { useGameStore } from '../game/gameStore'
import { findAbilityInCatalog } from '../game/ids'
import { createCombatVfx, type CombatVfxRuntime } from './index'

/** 与 CombatArena 同一时钟域：快照时刻 + 距同步的本地流逝 */
function estimateServerNowUnixMilliseconds(): number {
  const state = useGameStore.getState()
  return state.serverTimeUnixMilliseconds + Math.max(0, performance.now() - state.lastSyncPerformanceTime)
}

/** 快照过老（出手时刻已过去超过该阈值）不再补播，避免刷新时回溯演出 */
const STALE_THRESHOLD_MS = 600

/**
 * 挂载战斗特效运行时并调度出手演出。
 * @param shellRef   .combat-arena-shell 元素 ref
 * @param overlayRef .vfx-stage-overlay 元素 ref
 */
export function useCombatVfx(
  shellRef: RefObject<HTMLElement | null>,
  overlayRef: RefObject<HTMLElement | null>,
): void {
  const runtimeRef = useRef<CombatVfxRuntime | null>(null)
  /** signature -> timeout id：同一出手事件只调度一次 */
  const pendingRef = useRef(new Map<string, number>())

  const playerCastingId = useGameStore(state => {
    const entry = state.queue.entries[0]
    const exp = entry?.expedition
    if (!entry || entry.executionKind !== 'combat-encounter') return null
    // 斗法相位玩家出手由 duel.events 剧本调度（含 abilityId），此处只覆盖历练遭遇战，避免双触发
    if (exp?.phase !== 'combat') return null
    return state.combatResources?.castingAbilityId ?? null
  })
  /** 敌方行动签名：pendingAbilityId/nextAttackAtSeconds/存活翻转任一变化才重跑调度 */
  const enemyScheduleSig = useGameStore(state => {
    const entry = state.queue.entries[0]
    const exp = entry?.expedition
    if (!entry || entry.executionKind !== 'combat-encounter' || exp?.phase !== 'combat') return ''
    return (exp.enemies ?? [])
      .map((enemy, i) => `${i}|${enemy.pendingAbilityId ?? ''}|${enemy.nextAttackAtSeconds}|${enemy.hitpoints > 0 ? 1 : 0}`)
      .join(';')
  })
  /** 玩家普攻出手时刻（战斗时间线秒） */
  const playerAttackAt = useGameStore(state => {
    const entry = state.queue.entries[0]
    const exp = entry?.expedition
    if (!entry || entry.executionKind !== 'combat-encounter' || exp?.phase !== 'combat') return 0
    return exp.playerNextAttackAtSeconds ?? 0
  })
  /** 斗法剧本签名：全部事件编码（at/abilityId/出手方），增量不变则不重跑 */
  const duelScheduleSig = useGameStore(state => {
    const entry = state.queue.entries[0]
    const exp = entry?.expedition
    if (!entry || entry.executionKind !== 'combat-encounter' || exp?.phase !== 'duel' || !exp.duel) return ''
    return `${exp.duel.isInitiator ? 1 : 0}::` + exp.duel.events
      .map((ev, i) => `${i}|${ev.atSeconds}|${ev.abilityId ?? ''}|${ev.attackerIsInitiator ? 1 : 0}`)
      .join(';')
  })

  /** 引擎生命周期：shell/overlay 就绪即创建，卸载整体清理 */
  useEffect(() => {
    const shellEl = shellRef.current
    const overlayEl = overlayRef.current
    if (!shellEl || !overlayEl) return
    const runtime = createCombatVfx(shellEl, overlayEl)
    runtimeRef.current = runtime
    return () => {
      runtime.dispose()
      runtimeRef.current = null
      pendingRef.current.forEach(timer => window.clearTimeout(timer))
      pendingRef.current.clear()
    }
  }, [shellRef, overlayRef])

  /**
   * 按服务器时刻调度一次演出：signature 命中已排程则跳过；
   * fireAtUnixMs 与 ArenaActor 的 completesAtUnixMilliseconds 同一时钟域。
   */
  const schedule = (signature: string, fireAtUnixMs: number, fire: () => void): void => {
    const pending = pendingRef.current
    if (pending.has(signature)) return
    const delay = fireAtUnixMs - Date.now()
    if (delay < -STALE_THRESHOLD_MS) return
    const timer = window.setTimeout(() => {
      pending.delete(signature)
      fire()
    }, Math.max(0, delay))
    pending.set(signature, timer)
  }

  /** 玩家吟唱：立即播聚灵法阵 + 完成时刻播技能演出 */
  useEffect(() => {
    const runtime = runtimeRef.current
    if (!runtime || !playerCastingId) return
    const state = useGameStore.getState()
    const remaining = Math.max(0, state.combatResources?.castRemainingMilliseconds ?? 0)
    const ability = findAbilityInCatalog(state.abilities, playerCastingId) ?? null
    const castMs = Math.max(1, state.combatResources?.castTotalMilliseconds ?? ability?.castMilliseconds ?? remaining)
    const fireAt = estimateServerNowUnixMilliseconds() + remaining
    schedule(`ring|player|${playerCastingId}|${Math.round(fireAt / 100)}`, Date.now(), () => {
      runtime.playCastRing({ side: 'ally', casterIndex: 0, castMs, ability })
    })
    schedule(`player|${playerCastingId}|${Math.round(fireAt / 100)}`, fireAt, () => {
      runtime.playCast({ abilityId: playerCastingId, side: 'ally', casterIndex: 0, castMs, ability })
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [playerCastingId])

  /** 敌方行动：pendingAbilityId 出现即播法阵，出手时刻播技能/普攻演出 */
  useEffect(() => {
    const runtime = runtimeRef.current
    if (!runtime || !enemyScheduleSig) return
    const state = useGameStore.getState()
    const exp = state.queue.entries[0]?.expedition
    if (!exp || exp.phase !== 'combat') return
    const battleElapsed = exp.battleElapsedSeconds ?? 0
    const serverNow = estimateServerNowUnixMilliseconds()
    ;(exp.enemies ?? []).forEach((enemy, index) => {
      if (enemy.hitpoints <= 0) return
      const fireAt = serverNow + Math.max(0, (enemy.nextAttackAtSeconds - battleElapsed) * 1000)
      if (enemy.pendingAbilityId) {
        const castMs = Math.max(1, enemy.pendingActionDurationSeconds * 1000)
        const ability = findAbilityInCatalog(state.abilities, enemy.pendingAbilityId) ?? null
        // 法阵走 signature 防重：敌人 signature 变化重跑本 effect 时，吟唱中的敌人不重复播放
        schedule(`ring|enemy|${index}|${enemy.pendingAbilityId}|${enemy.nextAttackAtSeconds}`, Date.now(), () => {
          runtime.playCastRing({ side: 'enemy', casterIndex: index, castMs, ability })
        })
        schedule(`enemy|${index}|${enemy.pendingAbilityId}|${enemy.nextAttackAtSeconds}`, fireAt, () => {
          runtime.playCast({ abilityId: enemy.pendingAbilityId ?? null, side: 'enemy', casterIndex: index, castMs, ability })
        })
      } else {
        schedule(`enemy|${index}|attack|${enemy.nextAttackAtSeconds}`, fireAt, () => {
          runtime.playCast({ abilityId: null, side: 'enemy', casterIndex: index })
        })
      }
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enemyScheduleSig])

  /** 玩家普攻：出手时刻播命中爆闪 */
  useEffect(() => {
    const runtime = runtimeRef.current
    if (!runtime || playerAttackAt <= 0) return
    const exp = useGameStore.getState().queue.entries[0]?.expedition
    const battleElapsed = exp?.battleElapsedSeconds ?? 0
    const fireAt = estimateServerNowUnixMilliseconds() + Math.max(0, (playerAttackAt - battleElapsed) * 1000)
    schedule(`player|attack|${playerAttackAt}`, fireAt, () => {
      runtime.playCast({ abilityId: null, side: 'ally', casterIndex: 0 })
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [playerAttackAt])

  /** 斗法回放：剧本事件按 atSeconds 逐条调度（带 abilityId 播技能，否则普攻爆闪） */
  useEffect(() => {
    const runtime = runtimeRef.current
    if (!runtime || !duelScheduleSig) return
    const state = useGameStore.getState()
    const exp = state.queue.entries[0]?.expedition
    const duel = exp?.duel
    if (!duel || exp?.phase !== 'duel') return
    const battleElapsed = exp.battleElapsedSeconds ?? 0
    const serverNow = estimateServerNowUnixMilliseconds()
    duel.events.forEach((ev, i) => {
      if (ev.atSeconds <= battleElapsed) return
      const fireAt = serverNow + (ev.atSeconds - battleElapsed) * 1000
      const side: 'ally' | 'enemy' = ev.attackerIsInitiator === duel.isInitiator ? 'ally' : 'enemy'
      const ability = ev.abilityId ? findAbilityInCatalog(state.abilities, ev.abilityId) ?? null : null
      schedule(`duel|${i}|${ev.atSeconds}`, fireAt, () => {
        runtime.playCast({ abilityId: ev.abilityId ?? null, side, casterIndex: 0, ability })
      })
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [duelScheduleSig])
}
