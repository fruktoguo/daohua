import type { CombatAbilityDefinition } from '../game/types'
import {
  VfxEngine,
  isVfxPersistentHandle,
  type VfxPersistentHandle,
  type VfxPlayContext,
  type VfxSkillDescriptor,
} from './engine'
import {
  FORMATION_BUFF_TO_VFX,
  FORMATION_ITEM_TO_BUFF,
  isActorIdMatchingDomKey,
  normalizeActorCanonicalKey,
  resolveRegisteredVfxId,
  resolveZeroDamageSupportExpansion,
  type CombatEventBatch,
} from './vfxRouting'

export type { VfxPersistentHandle, VfxPlayContext, VfxSkillDescriptor } from './engine'

/** 吟唱法阵同步项 */
export interface ActiveCastSpec {
  readonly actorId: string
  readonly abilityId: string
  readonly castMs: number
  readonly remainingMs: number
  readonly element?: string
  readonly ability?: CombatAbilityDefinition | null
}

/** 持续阵盘同步项 */
export interface ActiveFormationSpec {
  readonly actorId: string
  readonly buffId: string
}

/** 战斗事件批播放参数 */
export interface CombatBatchPlaybackOptions {
  readonly selfIndex?: number | null
  readonly ability?: CombatAbilityDefinition | null
  readonly morphed: boolean
  readonly lowestHpAllyId?: string | null
}

interface CastRingEntry {
  readonly sig: string
  readonly isEnemy: boolean
  readonly handle: VfxPersistentHandle
  lastRemainingMs: number
  safetyTimerId: ReturnType<typeof setTimeout> | null
}

interface FormationEntry {
  readonly vfxId: string
  readonly casterEl: HTMLElement
  readonly handle: VfxPersistentHandle
}

function isActorDeadOrDormant(el: HTMLElement): boolean {
  if (el.classList.contains('is-dead') || el.classList.contains('is-dormant')) return true
  return Boolean(el.closest('.arena-slot--slain, .arena-slot--corpse'))
}

function isEnemySideId(actorId: string): boolean {
  return actorId === 'enemy' || actorId.startsWith('enemy:')
}

function toSkillDescriptor(ability: CombatAbilityDefinition | null | undefined): VfxSkillDescriptor | undefined {
  if (!ability) return undefined
  return {
    id: ability.abilityId,
    name: ability.name,
    element: ability.element,
    skillGroup: ability.style,
    targetType: ability.targetType,
    castMilliseconds: ability.castMilliseconds,
    cooldownMilliseconds: ability.cooldownMilliseconds,
  }
}

/**
 * 正式战斗双层 Canvas VFX 调度运行时。
 *
 * 负责将服务端战斗快照与实时事件流路由到共享 `DaohuaVFX` 核心，并管理：
 * - 单位 DOM 锚点精确解析（单人/组队 3v3 九宫格/斗法镜像/竖屏重排/散尸瞬间）；
 * - `feedback.castRing` 吟唱光环生命周期（脉动、淡出与异常兜底回收）；
 * - 四种常驻阵盘（聚灵/迷踪/天雷/血煞）跟随单位与波次存亡，以及反击触发联动；
 * - 21 主动功法 22 态（含移形化态「斩玉→踏云」与同帧 AoE 批聚拢）、10 内功心法入战护体、特质反应、符箓与 6 种战斗丹药；
 * - 战内复活（`feedback.revive`）与退场散尸（`feedback.defeat`）。
 */
export class CombatVfxRuntime {
  private readonly shellEl: HTMLElement
  private readonly engine: VfxEngine
  private readonly activeCastRings = new Map<string, CastRingEntry>()
  private readonly activeFormations = new Map<string, FormationEntry>()
  private readonly basicAttackComboByActor = new Map<string, number>()
  private readonly scheduledTimers = new Set<ReturnType<typeof setTimeout>>()
  private readonly scheduledRafs = new Set<number>()
  private playbackActive = true
  private disposed = false

  constructor(
    shellEl: HTMLElement,
    skyOverlayEl: HTMLElement,
    groundOverlayEl: HTMLElement | null = null,
  ) {
    this.shellEl = shellEl
    this.engine = new VfxEngine(shellEl, skyOverlayEl, groundOverlayEl)
  }

  /** 当前运行时是否允许播放特效（关闭开关、切页隐藏或浏览器后台时为 false） */
  isPlaybackActive(): boolean {
    return !this.disposed && this.playbackActive
  }

  /** 切换特效播放启停状态；停用时立即清场并清空在途调度，恢复时同步画布尺寸 */
  setPlaybackActive(active: boolean): void {
    if (this.disposed) return
    if (!active) {
      this.playbackActive = false
      this.clearBattle()
      return
    }
    if (!this.playbackActive) {
      this.playbackActive = true
      this.engine.resize(true)
    }
  }

  /** 主动刷新画布尺寸（页签从隐藏恢复可见时调用） */
  resize(notify = true): void {
    if (this.disposed) return
    this.engine.resize(notify)
  }

  hasVfx(id: string): boolean {
    return !this.disposed && this.engine.has(id)
  }

  /** 根据事件实体 ID 查找战场卡片 DOM（优先返回存活卡片，波次全灭尾帧可回落至尸体格以落准终结技） */
  findActorEl(entityId: string, selfIndex?: number | null): HTMLElement | null {
    if (this.disposed || !entityId) return null
    const actors = this.shellEl.querySelectorAll<HTMLElement>('.arena-actor[data-actor-key]')
    let corpseFallback: HTMLElement | null = null
    for (let i = 0; i < actors.length; i++) {
      const el = actors[i]
      const domKey = el.getAttribute('data-actor-key') ?? ''
      if (!isActorIdMatchingDomKey(entityId, domKey, selfIndex)) continue
      if (!el.closest('.arena-slot--corpse')) return el
      if (!corpseFallback) corpseFallback = el
    }
    return corpseFallback
  }

  /** 收集指定阵营所有 `.arena-actor` 卡片节点；`aliveOnly` 为真时过滤阵亡/休眠/尸体格 */
  collectSideEls(side: 'ally' | 'enemy', aliveOnly: boolean): HTMLElement[] {
    if (this.disposed) return []
    const selector =
      side === 'enemy'
        ? '.arena-actor.is-enemy, .arena-actor.arena-actor--enemy'
        : '.arena-actor.is-ally, .arena-actor.arena-actor--player'
    const nodes = this.shellEl.querySelectorAll<HTMLElement>(selector)
    const list: HTMLElement[] = []
    for (let i = 0; i < nodes.length; i++) {
      const el = nodes[i]
      if (aliveOnly && isActorDeadOrDormant(el)) continue
      list.push(el)
    }
    return list
  }

  /** 按当前所有正在吟唱的单位（本机 / 队友 / 野怪 / 斗法双方）全量同步地面蓄力法阵 */
  syncCastRings(casts: readonly ActiveCastSpec[], selfIndex?: number | null): void {
    if (this.disposed || !this.playbackActive) return
    const activeKeys = new Set<string>()

    for (const cast of casts) {
      if (!cast.abilityId) continue
      const canonicalKey = normalizeActorCanonicalKey(cast.actorId, selfIndex)
      if (!canonicalKey) continue
      const casterEl = this.findActorEl(cast.actorId, selfIndex)
      if (!casterEl || isActorDeadOrDormant(casterEl)) continue

      const castMs = Math.max(200, Math.round(cast.castMs || cast.ability?.castMilliseconds || 1200))
      const sig = `${cast.abilityId}|${castMs}`
      activeKeys.add(canonicalKey)

      const existing = this.activeCastRings.get(canonicalKey)
      if (existing && existing.sig === sig) {
        if (Math.abs(cast.remainingMs - existing.lastRemainingMs) > 5) {
          existing.lastRemainingMs = cast.remainingMs
          if (existing.safetyTimerId !== null) clearTimeout(existing.safetyTimerId)
          existing.safetyTimerId = this.createCastRingSafetyTimer(canonicalKey, cast.remainingMs)
        }
        continue
      }

      if (existing) {
        this.stopSingleCastRing(canonicalKey, existing, 120)
      }

      if (!this.engine.has('feedback.castRing')) continue
      const result = this.engine.play('feedback.castRing', {
        side: isEnemySideId(cast.actorId) ? 'enemy' : 'ally',
        casterEl,
        targetEl: casterEl,
        ability: cast.ability?.name ?? cast.abilityId,
        skill: toSkillDescriptor(cast.ability),
        element: cast.element ?? cast.ability?.element ?? 'none',
        castMs,
        durationMs: castMs,
      })

      if (isVfxPersistentHandle(result)) {
        this.activeCastRings.set(canonicalKey, {
          sig,
          isEnemy: isEnemySideId(cast.actorId),
          handle: result,
          lastRemainingMs: cast.remainingMs,
          safetyTimerId: this.createCastRingSafetyTimer(canonicalKey, cast.remainingMs),
        })
      }
    }

    for (const [key, entry] of this.activeCastRings.entries()) {
      if (!activeKeys.has(key)) {
        this.stopSingleCastRing(key, entry, 180)
      }
    }
  }

  /** 停止全部活跃的施法吟唱光环 */
  stopAllCastRings(onlyEnemy = false): void {
    for (const [key, entry] of this.activeCastRings.entries()) {
      if (onlyEnemy && !entry.isEnemy) continue
      this.stopSingleCastRing(key, entry, 180)
    }
  }

  /** 同步 4 种持续阵盘特效（仅在 combat / duel 战斗进行中存活，阵亡/换波/脱战即时淡出销毁） */
  syncFormations(
    active: boolean,
    formations: readonly ActiveFormationSpec[],
    selfIndex?: number | null,
  ): void {
    if (this.disposed || !this.playbackActive) return
    if (!active || formations.length === 0) {
      this.stopAllFormations()
      return
    }

    const allyEls = this.collectSideEls('ally', true)
    const desired = new Map<string, { vfxId: string; casterEl: HTMLElement }>()

    for (const spec of formations) {
      const meta = FORMATION_BUFF_TO_VFX[spec.buffId]
      if (!meta) continue
      const casterEl = this.findActorEl(spec.actorId, selfIndex)
      if (!casterEl || isActorDeadOrDormant(casterEl)) continue

      const canonicalKey = normalizeActorCanonicalKey(spec.actorId, selfIndex)
      if (!canonicalKey) continue
      const entryKey = meta.teamWide ? `team:ally|${spec.buffId}` : `${canonicalKey}|${spec.buffId}`
      if (!desired.has(entryKey)) {
        desired.set(entryKey, { vfxId: meta.vfxId, casterEl })
      }
    }

    for (const [key, entry] of this.activeFormations.entries()) {
      const next = desired.get(key)
      if (!next || next.casterEl !== entry.casterEl) {
        entry.handle.stop?.(280)
        this.activeFormations.delete(key)
      }
    }

    for (const [key, spec] of desired.entries()) {
      if (this.activeFormations.has(key)) continue
      if (!this.engine.has(spec.vfxId)) continue
      const result = this.engine.play(spec.vfxId, {
        side: 'ally',
        casterEl: spec.casterEl,
        targetEl: spec.casterEl,
        targetEls: [spec.casterEl],
        allyEls,
      })
      if (isVfxPersistentHandle(result)) {
        this.activeFormations.set(key, {
          vfxId: spec.vfxId,
          casterEl: spec.casterEl,
          handle: result,
        })
      }
    }
  }

  /** 淡出并清空全部常驻阵盘特效 */
  stopAllFormations(): void {
    for (const entry of this.activeFormations.values()) {
      entry.handle.stop?.(280)
    }
    this.activeFormations.clear()
  }

  /** 注册与运行时生命周期绑定的延迟回调（`delayMs <= 0` 在下一渲染帧执行，确保 React DOM 提交完成） */
  schedule(delayMs: number, fn: () => void): void {
    if (this.disposed || !this.playbackActive) return
    if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return
    if (delayMs <= 0 && typeof requestAnimationFrame === 'function') {
      const rafId = requestAnimationFrame(() => {
        this.scheduledRafs.delete(rafId)
        if (!this.disposed && this.playbackActive) fn()
      })
      this.scheduledRafs.add(rafId)
      return
    }
    const timerId = setTimeout(() => {
      this.scheduledTimers.delete(timerId)
      if (!this.disposed && this.playbackActive) fn()
    }, Math.max(0, delayMs))
    this.scheduledTimers.add(timerId)
  }

  /** 清空所有待执行延时回调 */
  clearScheduled(): void {
    for (const timerId of this.scheduledTimers) {
      clearTimeout(timerId)
    }
    this.scheduledTimers.clear()
    if (typeof cancelAnimationFrame === 'function') {
      for (const rafId of this.scheduledRafs) {
        cancelAnimationFrame(rafId)
      }
    }
    this.scheduledRafs.clear()
  }

  /** 播放聚合后的单次攻击/技能/阵法反击演出批次 */
  playCombatBatch(batch: CombatEventBatch, options: CombatBatchPlaybackOptions): void {
    if (this.disposed || !this.playbackActive) return
    const { selfIndex, ability, morphed, lowestHpAllyId } = options

    const side: 'ally' | 'enemy' = isEnemySideId(batch.attackerId) ? 'enemy' : 'ally'
    const casterEl = this.findActorEl(batch.attackerId, selfIndex)
    const allyEls = this.collectSideEls('ally', true)
    const livingEnemyEls = this.collectSideEls('enemy', true)
    const enemyEls = livingEnemyEls.length > 0 ? livingEnemyEls : this.collectSideEls('enemy', false)

    const targetEls: HTMLElement[] = []
    const seenEls = new Set<HTMLElement>()
    for (const tid of batch.targetIds) {
      const el = this.findActorEl(tid, selfIndex)
      if (el && !seenEls.has(el)) {
        seenEls.add(el)
        targetEls.push(el)
      }
    }

    // 后端 CombatActSystem 对零伤害 `all-allies` / `lowest-hp-ally` 技能以施法者自身作为占位目标下发，
    // 这里按功法 targetType 展开到同阵营全体存活单位或最低血比例友方，使山鬼谣等群辅可覆盖全队。
    const supportExpansion = resolveZeroDamageSupportExpansion(batch, ability?.targetType, selfIndex)
    if (supportExpansion === 'all-allies') {
      const friendlyPool = side === 'enemy' ? livingEnemyEls : allyEls
      if (friendlyPool.length > 0) {
        targetEls.length = 0
        seenEls.clear()
        for (const el of friendlyPool) {
          if (!seenEls.has(el)) {
            seenEls.add(el)
            targetEls.push(el)
          }
        }
      }
    } else if (supportExpansion === 'lowest-hp-ally' && lowestHpAllyId) {
      const injuredEl = this.findActorEl(lowestHpAllyId, selfIndex)
      if (injuredEl && !isActorDeadOrDormant(injuredEl)) {
        targetEls.length = 0
        targetEls.push(injuredEl)
      }
    }

    const primaryTargetEl = targetEls[0] ?? null
    if (!casterEl && targetEls.length === 0) return

    const abilityId = batch.abilityId
    const isFormationProc = Boolean(abilityId && FORMATION_ITEM_TO_BUFF[abilityId])

    // 主动功法出手释放瞬间，触发施法阵脉动并收束
    if (abilityId && !isFormationProc) {
      const ring = this.activeCastRings.get(batch.canonicalAttackerKey)
      if (ring) {
        ring.handle.pulse?.()
        this.stopSingleCastRing(batch.canonicalAttackerKey, ring, 150)
      }
    }

    const vfxId = resolveRegisteredVfxId(abilityId, morphed, id => this.engine.has(id))
    let formationHandle: VfxPersistentHandle | null = null
    if (abilityId && FORMATION_ITEM_TO_BUFF[abilityId]) {
      const buffId = FORMATION_ITEM_TO_BUFF[abilityId]
      formationHandle =
        this.activeFormations.get(`${batch.canonicalAttackerKey}|${buffId}`)?.handle ??
        this.activeFormations.get(`team:ally|${buffId}`)?.handle ??
        null
    }

    const attackVariant = !abilityId
      ? this.nextBasicAttackVariant(batch.canonicalAttackerKey)
      : undefined

    const ctx: VfxPlayContext = {
      side,
      attackerKey: !abilityId ? batch.canonicalAttackerKey : undefined,
      attackVariant,
      casterEl,
      targetEl: primaryTargetEl,
      targetEls,
      allyEls,
      enemyEls,
      ability: ability?.name ?? abilityId ?? undefined,
      skill: toSkillDescriptor(ability),
      element: ability?.element ?? 'none',
      castMs: ability?.castMilliseconds,
      durationMs: ability?.castMilliseconds,
      crit: batch.anyCrit,
      broken: batch.anyBroken,
      parried: batch.anyParried,
      hit: batch.anyHit,
      damage: batch.totalDamage,
      perTargetDamage: batch.perTargetDamage,
      morphed,
      formationHandle,
    }

    if (this.engine.has(vfxId)) {
      this.engine.play(vfxId, ctx)
    }

    // 普攻暴击额外叠加金耀暴击爆发反馈（各独立技能内部已自行处理 ctx.crit）
    if (!abilityId && batch.anyCrit && batch.anyHit && primaryTargetEl && this.engine.has('feedback.crit')) {
      this.engine.play('feedback.crit', {
        side,
        casterEl,
        targetEl: primaryTargetEl,
        targetEls,
        crit: true,
        damage: batch.totalDamage,
      })
    }

    // 破招 / 拆招补充短促受击激荡波纹
    if ((batch.anyBroken || batch.anyParried) && primaryTargetEl && this.engine.has('feedback.impact')) {
      this.engine.play('feedback.impact', {
        side,
        casterEl,
        targetEl: primaryTargetEl,
        targetEls,
        crit: batch.anyCrit,
        broken: batch.anyBroken,
        parried: batch.anyParried,
        element: ability?.element ?? 'none',
        damage: batch.totalDamage,
      })
    }
  }

  /** 播放雷击符与 6 种战斗丹药的即时激活特效（支持本机/盟友与斗法敌方镜像双侧施放） */
  playConsumable(
    itemId: string,
    actorId: string,
    selfIndex?: number | null,
    preferredEnemyTargetId?: string | null,
  ): void {
    if (this.disposed || !this.playbackActive || !this.engine.has(itemId)) return
    const isEnemy = isEnemySideId(actorId)
    const side: 'ally' | 'enemy' = isEnemy ? 'enemy' : 'ally'
    const casterEl = this.findActorEl(actorId, selfIndex)
    const allyEls = this.collectSideEls('ally', true)
    const enemyEls = this.collectSideEls('enemy', true)

    if (itemId === 'item.leiji-talisman') {
      const targetPool = isEnemy ? allyEls : enemyEls
      const targetEl =
        (preferredEnemyTargetId ? this.findActorEl(preferredEnemyTargetId, selfIndex) : null) ??
        targetPool[0] ??
        null
      if (!casterEl && !targetEl) return
      this.engine.play(itemId, {
        side,
        casterEl,
        targetEl,
        targetEls: targetEl ? [targetEl] : [],
        allyEls,
        enemyEls,
        element: 'wood',
      })
      return
    }

    if (!casterEl) return
    this.engine.play(itemId, {
      side,
      casterEl,
      targetEl: casterEl,
      targetEls: [casterEl],
      allyEls,
      enemyEls,
    })
  }

  /** 播放入战/斗法首帧内功心法周天护体演出 */
  playInternalArtIntro(
    abilityId: string,
    actorId: string,
    selfIndex?: number | null,
    ability?: CombatAbilityDefinition | null,
  ): void {
    if (this.disposed || !this.playbackActive || !this.engine.has(abilityId)) return
    const casterEl = this.findActorEl(actorId, selfIndex)
    if (!casterEl || isActorDeadOrDormant(casterEl)) return
    const side: 'ally' | 'enemy' = isEnemySideId(actorId) ? 'enemy' : 'ally'
    const allyEls = this.collectSideEls('ally', true)
    const livingEnemyEls = this.collectSideEls('enemy', true)
    const enemyEls = livingEnemyEls.length > 0 ? livingEnemyEls : this.collectSideEls('enemy', false)

    this.engine.play(abilityId, {
      side,
      casterEl,
      targetEl: casterEl,
      targetEls: [casterEl],
      allyEls,
      enemyEls,
      ability: ability?.name ?? abilityId,
      skill: toSkillDescriptor(ability),
      element: ability?.element ?? 'none',
    })
  }

  /** 播放战内复活降临光效（`feedback.revive`） */
  playRevive(actorId: string, selfIndex?: number | null): void {
    if (this.disposed || !this.playbackActive || !this.engine.has('feedback.revive')) return
    const targetEl = this.findActorEl(actorId, selfIndex)
    if (!targetEl) return
    this.engine.play('feedback.revive', {
      side: 'ally',
      casterEl: targetEl,
      targetEl,
      targetEls: [targetEl],
    })
  }

  /** 搜寻期敌人尸体退场消散（触发 `feedback.defeat` 并清理敌方残留施法阵） */
  playCorpseDissipate(): void {
    if (this.disposed || !this.playbackActive) return
    this.stopAllCastRings(true)
    const enemies = this.collectSideEls('enemy', false)
    if (enemies.length === 0 || !this.engine.has('feedback.defeat')) return
    this.engine.play('feedback.defeat', {
      side: 'enemy',
      targetEl: enemies[0],
      targetEls: enemies,
      enemyEls: enemies,
    })
  }

  /** 重置各出手单位的普攻五式连段计数（开战/切波/切遭遇时调用） */
  resetComboCounters(): void {
    this.basicAttackComboByActor.clear()
  }

  /** 切行动或脱战重置全盘特效状态 */
  clearBattle(): void {
    this.clearScheduled()
    this.stopAllCastRings()
    this.stopAllFormations()
    if (!this.disposed) {
      this.engine.clear()
    }
  }

  /** 销毁运行时与 Canvas 宿主的全部资源绑定 */
  dispose(): void {
    if (this.disposed) return
    this.clearScheduled()
    this.stopAllCastRings()
    this.stopAllFormations()
    this.basicAttackComboByActor.clear()
    this.disposed = true
    this.engine.dispose()
  }

  private nextBasicAttackVariant(canonicalAttackerKey: string): number {
    const key = canonicalAttackerKey || 'default'
    const current = this.basicAttackComboByActor.get(key) ?? 0
    this.basicAttackComboByActor.set(key, (current + 1) % 5)
    return current
  }

  private createCastRingSafetyTimer(canonicalKey: string, remainingMs: number): ReturnType<typeof setTimeout> {
    const timeoutMs = Math.max(450, Math.min(18000, remainingMs + 500))
    return setTimeout(() => {
      const entry = this.activeCastRings.get(canonicalKey)
      if (!entry) return
      entry.safetyTimerId = null
      this.stopSingleCastRing(canonicalKey, entry, 180)
    }, timeoutMs)
  }

  private stopSingleCastRing(canonicalKey: string, entry: CastRingEntry, fadeMs: number): void {
    if (entry.safetyTimerId !== null) {
      clearTimeout(entry.safetyTimerId)
      entry.safetyTimerId = null
    }
    entry.handle.stop?.(fadeMs)
    this.activeCastRings.delete(canonicalKey)
  }
}
