import type { BuffEffectChannel } from '../game/buffChannels'
import { hasActionParticipation } from '../game/types'
import { memo, useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties } from 'react'
import { formatInteger, formatNumber } from '../game/format'
import { UnitNumber } from './UnitNumber'
import { resolvePlayerActorId, useAvatarStore } from '../game/avatarStore'
import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { ACTION_TAB_META, convertTierToLevel, findAbilityInCatalog, findCombatantInCatalog, findEncounterInCatalog, findEquipmentInCatalog, findItemInCatalog, localizeActionDisplayText, resolveActionDefinition, resolveItemMetadata } from '../game/ids'
import { calculateEnhancementYields, ENHANCE_ACTION_ID, SPIRIT_STONE_ITEM_ID } from '../game/combatConstants'
import { calculateActionYieldPreview } from '../game/actionCalculations'
import type { ResourceYieldKind } from '../game/actionCalculations'
import type { ActionDefinition, Buff, CharacterVisual, CombatAbilityDefinition, CombatAbilitySlot, CombatActionState, CombatAttributes, CombatBuffEntry, CombatRecentEvent, EquippedItem, ExpeditionAlly, ExpeditionDuel, ExpeditionEnemy, ExpeditionProgress, MonsterDropDefinition, QueueEntry, WorldMapDefinition, WorldState } from '../game/types'
import { gameClient } from '../network/gameClient'
import { getT, useT } from '../i18n'
import { Modal } from './Modal'
import { useSmoothProgressRef } from '../game/useSmoothProgress'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { CharacterAvatar } from './CharacterAvatar'
import { CombatAttributePanel, type UnitLoadoutView } from './CombatAttributePanel'
import { CombatStatsPanel } from './CombatStatsPanel'
import { ConfirmDialog } from './ConfirmDialog'
import { BonusAsideList, extractBuffBonusItems } from './ItemDisplay'
import { useCombatVfx } from '../vfx/useCombatVfx'
import { StatGlyph } from './StatGlyph'
import { useUiStore } from '../uiStore'
import { COMBAT_RESOURCE_EFFECT_LIMIT, collectScheduledCombatEvents, combatHitDelayMilliseconds } from '../game/combatFeedback'
import { computeArenaFormationScale, resolveOccupiedRows } from '../game/combatScale'

/** 解析队列条目展示名：优先服务端下发的 actionName，缺省时回退本地行动定义名；传入 world+maps 时按当前驻留地点本地化 */
function resolveQueueEntryDisplayName(
  entry: QueueEntry | null | undefined,
  fallback?: string,
  world?: WorldState,
  maps?: readonly WorldMapDefinition[],
): string {
  const name = !entry ? (fallback || '')
    : (entry.actionName && entry.actionName !== entry.actionId)
      ? entry.actionName
      : resolveActionDefinition(entry.actionId)?.name || entry.actionName || fallback || ''
  return world && maps ? localizeActionDisplayText(name, world, maps) : name
}

/** 资源条数值变动特效记录（跳字、特殊博弈标签与血条破裂碎片） */
interface ResourceEffect {
  id: number
  /** 本次数值增量（负值为受损，闪避时为 undefined） */
  delta?: number
  /** 纯文本（如“闪避”或格式化伤害数字） */
  text?: string
  /** 特殊标签胶囊（破招 / 化解 / 暴击 / 破招·暴击） */
  badge?: 'break' | 'parry' | 'crit' | 'break-crit'
  badgeLabel?: string
  kind?: 'damage' | 'heal' | 'dodge' | 'break' | 'parry' | 'crit' | 'break-crit'
  /** 受损区间起点（占最大值比例 0~1），碎片/跳字按它定位 */
  left: number
  /** 受损区间宽度（占最大值比例 0~1） */
  width: number
  /** 跳字横向错位车道（px），错开连续多条跳字避免相互重叠 */
  lane: number
}

/** 实体动作类型（攻击或施法） */
type ActorMotionKind = 'attack' | 'cast'

/** 实体行动动作状态 */
interface ActorMotion {
  id: number
  kind: ActorMotionKind
  /** 突进纵向分量（px）：正 = 朝下方目标偏 */
  aimY: number
  /** 攻击弹道倾斜角（度）：正 = 弹道朝下方偏 */
  aimAngleDeg: number
}

const actorImpactVariants = ['cleave', 'ripple', 'splinter', 'crit', 'break', 'parry'] as const
type ActorImpactVariant = typeof actorImpactVariants[number]

/** 实体受击震颤与冲击特效状态 */
interface ActorImpact {
  id: number
  variant: ActorImpactVariant
  distance: number
  opacity: number
  /** 受击退散纵向分量（px）：沿来向继续推移 */
  hitY: number
  /** 冲击刀光倾斜角（度）：与攻击来向同一斜率 */
  impactAngleDeg: number
  /** 是否为暴击（触发立绘金光闪烁） */
  isCritical?: boolean
}
// 单次损失低于最大值 2% 时不产生断裂碎片(如持续伤害的小额跳数)
const resourceShardMinimumRatio = 0.02

/** 动作前摇时间（毫秒） */
const actorMotionLeadMilliseconds: Record<ActorMotionKind, number> = {
  attack: 170,
  cast: 300,
  }

/** 动作持续时间（毫秒） */
const actorMotionDurationMilliseconds: Record<ActorMotionKind, number> = {
  attack: 520,
  cast: 720,
}

/**
 * 竖屏纵列下量取两点连线的方位斜率（仅 ≤760px 布局启用；桌面九宫横排不需要方向分量）。
 * 返回 slope = tan(方位角) 供位移按横向基准换算纵向分量，angleDeg 供弹道/刀光倾斜；
 * 方位角钳制在 ±35° 内，避免跨行过远时斜率失真。
 */
function measureArenaAim(fromEl: Element | null, toEl: Element | null): { slope: number; angleDeg: number } {
  if (!fromEl || !toEl || fromEl === toEl) return { slope: 0, angleDeg: 0 }
  if (typeof window.matchMedia !== 'function' || !window.matchMedia('(max-width: 760px)').matches) {
    return { slope: 0, angleDeg: 0 }
  }
  const from = fromEl.getBoundingClientRect()
  const to = toEl.getBoundingClientRect()
  const dx = Math.abs(to.left + to.width / 2 - (from.left + from.width / 2)) || 1
  const dy = to.top + to.height / 2 - (from.top + from.height / 2)
  const angle = Math.max(-0.62, Math.min(0.62, Math.atan2(dy, dx)))
  return { slope: Math.tan(angle), angleDeg: angle * 180 / Math.PI }
}

/**
 * 伤害数字格式化：至少保留 1 点量级，避免 <=1 的持续伤害被截断成「0」造成「没有显示伤害」的错觉。
 */
function formatDamageDelta(delta: number): string {
  return `-${formatInteger(Math.max(1, Math.round(Math.abs(delta))))}`
}

/**
 * 校验战斗事件的实体 ID（攻击者或受击目标）是否匹配当前卡片
 */
function isActorMatchingId(entityId: string, actorKey: string): boolean {
  if (actorKey === entityId) return true
  if (actorKey === 'player') {
    return entityId === 'player' || entityId === 'ally:0'
  }
  if (actorKey.startsWith('ally:')) {
    const actorParts = actorKey.split(':')
    const entityParts = entityId.split(':')
    return entityParts[0] === 'ally' && actorParts[1] === entityParts[1]
  }
  if (actorKey.startsWith('enemy:')) {
    if (!entityId.startsWith('enemy:')) return false
    if (actorKey.startsWith('enemy:duel:')) {
      return entityId === 'enemy:0' || entityId === 'enemy'
    }
    const actorParts = actorKey.split(':')
    const entityParts = entityId.split(':')
    // 远征怪 actorKey 格式为 enemy:waveNumber:index:enemyId
    if (actorParts.length >= 4 && entityParts.length >= 2) {
      return actorParts[2] === entityParts[1]
    }
    if (actorParts.length >= 2 && entityParts.length >= 2) {
      return actorParts[1] === entityParts[1]
    }
    return false
  }
  return false
}

/**
 * 战场生命/真元资源条组件 (ArenaResource)
 *
 * 渲染角色气血 (HP) 与灵力 (MP) 仪表盘。
 * 当数值或事件发生变化时：
 * 1. 消费精确的战斗对抗事件流，与攻击方出招动作形成毫秒级击中帧 (Hit-Frame) 锁步：在刀刃/法术触及目标的瞬间卡点爆开飘字、切片与受击反馈；
 * 2. 受到大额伤害时（>= 最大值 2%），在受损区间生成带物理位移与旋转的血条碎裂切片特效 (Shards)；
 * 3. 血量文字数值独立于切片之上渲染，杜绝层级遮挡与跳变；
 * 4. 具有自动清理定时器以防内存泄漏。
 */
/** 渲染战斗浮字前缀内的极简二值属性小图标（成对图标略缩小以压缩宽度） */
function renderFloatBadgeIcon(badge: 'break' | 'parry' | 'crit' | 'break-crit'): React.ReactNode {
  switch (badge) {
    case 'crit':
      return <StatGlyph glyph="crit" size={16.5} />
    case 'break':
      return <StatGlyph glyph="parry-break" size={16.5} />
    case 'parry':
      return <StatGlyph glyph="parry-resolve" size={16.5} />
    case 'break-crit':
      return (
        <>
          <StatGlyph glyph="parry-break" size={15} />
          <StatGlyph glyph="crit" size={15} />
        </>
      )
  }
}

function ArenaResource({
  actorKey,
  value,
  max,
  tone,
  recentEvents,
  frozen = false,
}: {
  /** 实体唯一标识符（切换目标时重置特效） */
  actorKey: string
  /** 当前数值 */
  value: number
  /** 最大上限数值 */
  max: number
  /** 色调类型：hp 气血红 / mp 灵力蓝 */
  tone: 'hp' | 'mp'
  /** 最近产生的战斗对抗事件（普攻/技能命中/闪避/破招/拆招/暴击） */
  recentEvents?: readonly CombatRecentEvent[] | null
  /** 单位已阵亡（尸体）：停止灵力消耗演出与浮字，避免死亡瞬间再播一次掉蓝动画 */
  frozen?: boolean
}): React.JSX.Element {
  const t = useT()
  const normalizedMax = Math.max(1, max)
  const normalizedValue = Math.max(0, Math.min(normalizedMax, value))
  const [displayValue, setDisplayValue] = useState(normalizedValue)
  const previousRef = useRef({ actorKey, max: normalizedMax, value: normalizedValue })
  const nextEffectId = useRef(1)
  const timers = useRef(new Set<number>())
  const [effects, setEffects] = useState<ResourceEffect[]>([])
  const handledEventsRef = useRef(new Set<string>())
  /**
   * 已排期、尚未到击中帧的权威掉血：期间血条等它落地，
   * 杜绝「条先掉、数字后出」的因果错位；同一批次后续快照只修正目标值，不抢先落条。
   */
  const pendingDropRef = useRef<{ id: number; dueAt: number; value: number } | null>(null)
  /** 上一目标键：仅切换单位时重置已消费事件（上限变化不重置，避免同批事件重复播放） */
  const actorKeyRef = useRef(actorKey)

  const pushEffect = useCallback((effect: ResourceEffect) => {
    setEffects(current => [...current.slice(-(COMBAT_RESOURCE_EFFECT_LIMIT - 1)), effect])
    const cleanupTimer = window.setTimeout(() => {
      setEffects(current => current.filter(item => item.id !== effect.id))
      timers.current.delete(cleanupTimer)
    }, 1_100)
    timers.current.add(cleanupTimer)
  }, [])

  // 1. 切换目标/上限变化：清空上一单位在途计时器与待定掉血，状态归位；
  //    切换单位同时重置已消费事件（服务端入场会清空 RecentEvents），避免跨波同值事件碰撞。
  useLayoutEffect(() => {
    if (actorKeyRef.current !== actorKey) {
      actorKeyRef.current = actorKey
      handledEventsRef.current.clear()
    }
    timers.current.forEach(timer => window.clearTimeout(timer))
    timers.current.clear()
    pendingDropRef.current = null
    previousRef.current = { actorKey, max: normalizedMax, value: normalizedValue }
    setDisplayValue(normalizedValue)
    setEffects([])
  }, [actorKey, normalizedMax])

  // 2. 消费战斗事件流：闪避/破招/拆招/暴击浮字与血条切片按击中帧卡点爆发；
  //    同一快照内的多秒事件先由 collectScheduledCombatEvents 压缩为有序重放时间轴。
  useLayoutEffect(() => {
    if (tone !== 'hp' || !recentEvents || recentEvents.length === 0) return
    const handled = handledEventsRef.current
    const scheduled = collectScheduledCombatEvents(
      recentEvents,
      event => isActorMatchingId(event.targetId, actorKey),
      handled,
    )
    if (scheduled.length === 0) return

    for (const { event, key, delay, comboIndex } of scheduled) {
      handled.add(key)
      if (handled.size > 200) {
        const oldest = handled.values().next().value
        if (oldest) handled.delete(oldest)
      }

      const id = nextEffectId.current++
      const hitDelay = delay + combatHitDelayMilliseconds(event)
      if (event.hit && event.damage > 0) {
        pendingDropRef.current = {
          id,
          dueAt: performance.now() + hitDelay,
          value: Math.max(0, Math.min(normalizedMax, event.targetHp ?? normalizedValue)),
        }
      }

      const triggerTimer = window.setTimeout(() => {
        timers.current.delete(triggerTimer)

        if (!event.hit) {
          // 闪避（可同时附带化解标记）
          pushEffect({
            id,
            text: t('combat.arena.dodge'),
            badge: event.parried ? 'parry' : undefined,
            badgeLabel: event.parried ? t('combat.arena.parry') : undefined,
            kind: 'dodge',
            left: 0.35,
            width: 0.3,
            lane: (((id + comboIndex) % 5) - 2) * 14,
          })
          return
        }

        // 命中伤害：在击中瞬间同步扣除血条并爆出数字与切片
        const pending = pendingDropRef.current
        if (pending && pending.id === id) {
          setDisplayValue(Math.max(0, Math.min(normalizedMax, pending.value)))
          pendingDropRef.current = null
        } else if (event.targetHp !== undefined) {
          setDisplayValue(Math.max(0, Math.min(normalizedMax, event.targetHp)))
        }

        if (event.damage <= 0) return

        const isBreakCrit = Boolean(event.broken && event.critical)
        const isCrit = !isBreakCrit && Boolean(event.critical)
        const isBreak = !isBreakCrit && Boolean(event.broken)
        const isParry = Boolean(event.parried)

        const badgeKind: 'break-crit' | 'crit' | 'break' | 'parry' | undefined = isBreakCrit
          ? 'break-crit'
          : isCrit
          ? 'crit'
          : isBreak
          ? 'break'
          : isParry
          ? 'parry'
          : undefined
        const badgeLabel = badgeKind === 'break-crit'
          ? t('combat.arena.breakCrit')
          : badgeKind === 'crit'
          ? t('combat.arena.crit')
          : badgeKind === 'break'
          ? t('combat.arena.break')
          : badgeKind === 'parry'
          ? t('combat.arena.parry')
          : undefined

        const damageDelta = -event.damage
        const sliceEndHp = event.targetHp !== undefined
          ? event.targetHp
          : normalizedValue
        const sliceStartHp = event.targetHp !== undefined
          ? Math.min(normalizedMax, event.targetHp + event.damage)
          : Math.max(sliceEndHp, previousRef.current.value)

        pushEffect({
          id,
          delta: damageDelta,
          text: formatDamageDelta(damageDelta),
          badge: badgeKind,
          badgeLabel,
          kind: badgeKind ?? 'damage',
          left: Math.max(0, Math.min(1, sliceEndHp / normalizedMax)),
          width: Math.max(0, Math.min(1, (sliceStartHp - sliceEndHp) / normalizedMax)),
          lane: (((id + comboIndex) % 5) - 2) * 14,
        })
      }, hitDelay)
      timers.current.add(triggerTimer)
    }
  }, [actorKey, normalizedMax, normalizedValue, recentEvents, t, tone, pushEffect])

  // 3. 数值变动保底：回血/灵力消耗/无事件承接的持续扣血立即落条；
  //    有事件承接的掉血只修正目标值，等击中帧与数字一起落地。
  useLayoutEffect(() => {
    const previous = previousRef.current
    if (previous.actorKey !== actorKey || previous.max !== normalizedMax) {
      previousRef.current = { actorKey, max: normalizedMax, value: normalizedValue }
      return
    }
    if (previous.value === normalizedValue) return

    const delta = normalizedValue - previous.value
    previousRef.current = { actorKey, max: normalizedMax, value: normalizedValue }

    const pending = pendingDropRef.current
    if (delta < 0 && pending && pending.dueAt > performance.now() + 4) {
      // 权威掉血已由击中帧事件承接：把血条目标修到最新真值，等卡点落地
      pending.value = normalizedValue
      return
    }
    if (pending) pendingDropRef.current = null

    setDisplayValue(normalizedValue)
    if (tone === 'mp' && (frozen || delta >= 0)) return
    if (delta === 0) return

    const id = nextEffectId.current++
    pushEffect({
      id,
      delta,
      left: Math.min(previous.value, normalizedValue) / normalizedMax,
      width: Math.min(1, Math.abs(delta) / normalizedMax),
      lane: ((id % 5) - 2) * 14,
    })
  }, [actorKey, frozen, normalizedMax, normalizedValue, pushEffect, tone])

  // 阵亡后不再保留灵力条的浮字与切片：尸体只展示定格状态。
  useEffect(() => {
    if (!frozen) return
    pendingDropRef.current = null
    setEffects([])
  }, [frozen])

  useEffect(() => () => {
    timers.current.forEach(timer => window.clearTimeout(timer))
    timers.current.clear()
  }, [])

  return (
    <div className={`arena-resource arena-resource--${tone}`}>
      <div className="arena-resource-meter">
        <div className="arena-resource-track">
          <i style={{ transform: `scaleX(${displayValue / normalizedMax})` }} />
        </div>
        <span className="arena-resource-shards" aria-hidden="true">
          {tone === 'hp' && effects
            .filter(effect => (effect.delta ?? 0) < 0 && effect.width >= resourceShardMinimumRatio)
            .map(effect => {
              const scale = 1 + Math.min(0.5, effect.width * 1.6)
              const travel = -52 * scale
              const rotate = -(16 + (effect.id % 7))
              const drift = ((effect.id % 5) - 2) * 2
              const backgroundPosition = effect.left / Math.max(0.0001, 1 - effect.width) * 100
              return (
                <b
                  className="arena-resource-slice"
                  key={effect.id}
                  style={{
                    '--slice-background-position': `${backgroundPosition}% 50%`,
                    '--slice-background-size': `${100 / Math.max(0.0001, effect.width)}% 100%`,
                    '--slice-burst-y': `${travel * 0.22}px`,
                    '--slice-drift': `${drift}px`,
                    '--slice-rotate': `${rotate}deg`,
                    '--slice-travel': `${travel}px`,
                    left: `${effect.left * 100}%`,
                    width: `${effect.width * 100}%`,
                  } as CSSProperties}
                />
              )
            })}
        </span>
        <span className="arena-resource-badge">
          <UnitNumber disableTooltip value={displayValue} /> / <UnitNumber disableTooltip value={normalizedMax} />
        </span>
        <span className="arena-resource-floats" aria-live="polite">
          {effects.map(effect => {
            const emClass = effect.kind
              ? `is-${effect.kind}`
              : (effect.delta ?? 0) > 0
              ? 'is-positive'
              : 'is-negative'
            return (
              <em
                className={emClass}
                key={effect.id}
                style={{
                  '--float-drift': `${effect.lane}px`,
                  left: `${Math.max(0.08, Math.min(0.92, effect.left + effect.width / 2)) * 100}%`,
                } as CSSProperties}
              >
                {effect.badge && (
                  <span
                    className={`arena-float-badge arena-float-badge--${effect.badge}`}
                    aria-label={effect.badgeLabel}
                    title={effect.badgeLabel}
                  >
                    {renderFloatBadgeIcon(effect.badge)}
                  </span>
                )}
                <span className="arena-float-text">
                  {effect.text ?? ((effect.delta ?? 0) > 0 ? `+${formatInteger(effect.delta!)}` : formatDamageDelta(effect.delta ?? 0))}
                </span>
              </em>
            )
          })}
        </span>
      </div>
    </div>
  )
}
/**
 * 平滑行动条填充组件 (SmoothActionFill)
 *
 * 采用 Web Animations API (Element.animate) 结合服务端绝对截止时间戳，
 * 在跨帧与网络同步间隔中提供 60fps 丝滑线性进度填充，自动对齐时钟漂移。
 */
function SmoothActionFill({ action }: { action?: CombatActionState | null }): React.JSX.Element {
  const fillRef = useRef<HTMLElement>(null)
  const animationRef = useRef<Animation | null>(null)
  const serverTimeUnixMilliseconds = useGameStore(state => state.serverTimeUnixMilliseconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)
  const anchorRef = useRef<{
    signature: string
    elapsed: number
    duration: number
    lastObservedElapsed: number
    startedAt: number
  } | null>(null)

  useLayoutEffect(() => {
    const fill = fillRef.current
    if (!fill) return
    // 复活等待条件态没有可推进的读条：进度恒为 0，且不排任何动画。
    if (!action || action.durationMilliseconds <= 0 || action.kind === 'respawn-wait') {
      animationRef.current?.cancel()
      fill.style.transform = 'scaleX(0)'
      anchorRef.current = null
      return
    }

    const now = performance.now()
    const duration = Math.max(1, action.durationMilliseconds)
    const relativeElapsed = Math.max(0, Math.min(duration, action.elapsedMilliseconds))
    const clientDateNowAtSync = Date.now() - Math.max(0, performance.now() - lastSyncPerformanceTime)
    const estimatedClockSkew = serverTimeUnixMilliseconds - clientDateNowAtSync
    const estimatedServerNow = Date.now() + estimatedClockSkew
    const hasAbsoluteDeadline = action.completesAtUnixMilliseconds != null
    const remainingAtRender = hasAbsoluteDeadline
      ? Math.max(0, Math.min(duration, action.completesAtUnixMilliseconds! - estimatedServerNow))
      : Math.max(0, duration - relativeElapsed)
    const elapsed = duration - remainingAtRender
    const signature = `${action.name}|${action.kind}|${duration}`
    const anchor = anchorRef.current
    if (anchor?.signature === signature) {
      const expected = Math.min(anchor.duration, anchor.elapsed + now - anchor.startedAt)
      // elapsed 明显回退（相对于上次观测值或当前动画预期位置）说明同一行动进入了新一轮出手，必须重建动画；
      // 否则在 ±450ms 估算误差内沿用运行中的旧动画，避免快照同步重启 WAAPI 造成进度条抖动。
      const startedNewCycle = elapsed + 50 < anchor.lastObservedElapsed || expected - elapsed > 350
      anchor.lastObservedElapsed = elapsed
      if (!startedNewCycle && animationRef.current?.playState === 'running' && Math.abs(elapsed - expected) <= 450) return
    }

    animationRef.current?.cancel()
    const fraction = elapsed / duration
    fill.style.transform = `scaleX(${fraction})`
    anchorRef.current = { signature, elapsed, duration, lastObservedElapsed: elapsed, startedAt: now }
    const remaining = remainingAtRender
    if (remaining === 0 || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    animationRef.current = fill.animate(
      [{ transform: `scaleX(${fraction})` }, { transform: 'scaleX(1)' }],
      { duration: remaining, easing: 'linear', fill: 'forwards' },
    )
  }, [
    action?.completesAtUnixMilliseconds,
    action?.durationMilliseconds,
    action?.elapsedMilliseconds,
    action?.kind,
    action?.name,
    lastSyncPerformanceTime,
    serverTimeUnixMilliseconds,
  ])

  useEffect(() => () => animationRef.current?.cancel(), [])

  return <b ref={fillRef} />
}

/**
 * 行动条倒计时文字徽章 (ActionCountdownBadge)
 *
 * 基于 100ms 低频 interval 更新显示当前技能名称与精确到 0.1s 的倒计时（与显示精度对齐）。
 */
const ActionCountdownBadge = memo(function ActionCountdownBadge({
  action,
  role,
}: {
  /** 当前行动状态 */
  action?: CombatActionState | null
  /** 角色身份：player 己方 / enemy 敌方 */
  role: 'player' | 'enemy'
}): React.JSX.Element {
  const t = useT()
  const badgeRef = useRef<HTMLSpanElement>(null)
  const serverTimeUnixMilliseconds = useGameStore(state => state.serverTimeUnixMilliseconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  useLayoutEffect(() => {
    const badge = badgeRef.current
    if (!badge) return
    // 待机/调息 (kind === 'wait') 或无动作时：敌方显示纯净空条，玩家具名等待显示调息文案
    if (!action || action.durationMilliseconds <= 0 || action.kind === 'wait' || !action.name.trim()) {
      if (role === 'player' && action?.kind === 'wait' && action.name.trim()) {
        badge.textContent = t('combat.arena.regenerating')
        badge.style.display = ''
      } else {
        badge.textContent = ''
        badge.style.display = 'none'
      }
      return
    }

    badge.style.display = ''
    // 复活等待条件态：读条已走完、条件未成立，只展示等待文案，不做倒计时推进。
    if (action.kind === 'respawn-wait') {
      if (badge.textContent !== action.name) badge.textContent = action.name
      return
    }
    const duration = Math.max(1, action.durationMilliseconds)
    // 基础攻击 (kind === 'attack') 统一显示本地化名称，其余直接展示服务端 wire 名称
    let baseName = action.name.replace(/\s*\([\d.]+s\/[\d.]+s\)/, '').trim()
    if (action.kind === 'attack') {
      baseName = t('combat.arena.basicAttack')
    }
    const relativeElapsed = Math.max(0, Math.min(duration, action.elapsedMilliseconds))
    const clientDateNowAtSync = Date.now() - Math.max(0, performance.now() - lastSyncPerformanceTime)
    const estimatedClockSkew = serverTimeUnixMilliseconds - clientDateNowAtSync
    const hasAbsoluteDeadline = action.completesAtUnixMilliseconds != null
    const startPerfTime = performance.now()
    const initialRemaining = hasAbsoluteDeadline
      ? Math.max(0, Math.min(duration, action.completesAtUnixMilliseconds! - (Date.now() + estimatedClockSkew)))
      : Math.max(0, duration - relativeElapsed)

    const tick = () => {
      const estimatedServerNow = Date.now() + estimatedClockSkew
      // 本轮剩余时长以本轮时长 duration 为上界：绝对截止时间戳与本地估算时钟都可能因同步间隔出现偏差，
      // 但「剩余不得超过本轮时长」是行动条本身的语义不变量，越界即说明估算时钟不可信。
      const currentRemaining = hasAbsoluteDeadline
        ? Math.max(0, Math.min(duration, action.completesAtUnixMilliseconds! - estimatedServerNow))
        : Math.max(0, initialRemaining - (performance.now() - startPerfTime))

      const remainingSeconds = Math.max(0, currentRemaining / 1000)
      const formatted = t('combat.arena.countdown', { name: baseName, seconds: remainingSeconds.toFixed(1) })
      if (badge.textContent !== formatted) {
        badge.textContent = formatted
      }

      if (currentRemaining <= 0 && intervalId !== undefined) {
        window.clearInterval(intervalId)
        intervalId = undefined
      }
    }

    // 倒计时文本精度 0.1s：100ms 低频刷新与显示精度对齐，不再占用 rAF 逐帧唤醒
    let intervalId: number | undefined = window.setInterval(tick, 100)
    tick()

    return () => {
      if (intervalId !== undefined) window.clearInterval(intervalId)
    }
  }, [
    action?.completesAtUnixMilliseconds,
    action?.durationMilliseconds,
    action?.elapsedMilliseconds,
    action?.kind,
    action?.name,
    lastSyncPerformanceTime,
    role,
    serverTimeUnixMilliseconds,
    t,
  ])

  return <span className="arena-action-badge" ref={badgeRef} style={{ display: 'none' }} />
})

/**
 * 角色行动条组合容器 (ArenaActionBar)
 */
function ArenaActionBar({ action, role }: { action?: CombatActionState | null; role: 'player' | 'enemy' }): React.JSX.Element {
  // 复活读条与复活等待条件都只属于己方：敌方永不渲染这两态。
  const isRevivalAction = action?.kind === 'respawn' || action?.kind === 'respawn-wait'
  const effectiveAction = role === 'enemy' && isRevivalAction ? null : action
  return (
    <div className={`arena-action-bar arena-action-bar--${role} arena-action-bar--${effectiveAction?.kind ?? 'wait'}`}>
      <div className="arena-action-track">
        <i><SmoothActionFill action={effectiveAction} /></i>
        <ActionCountdownBadge action={effectiveAction} role={role} />
      </div>
    </div>
  )
}

/**
 * 战场 Buff 方块组件 (ArenaBuffSquare)
 *
 * 具备圆形 SVG 环形倒计时动画遮罩与悬浮详细属性气泡卡片。
 */
function ArenaBuffSquare({ buff }: { buff: Buff }): React.JSX.Element {
  const t = useT()
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  // durable 阵盘类永驻 Buff：到期时间投影为有限哨兵（≥1e15），按常驻展示而非倒计时。
  const permanent = buff.expiresAtUnixSeconds >= 1e15
  const remaining = permanent ? 0 : Math.max(0, buff.expiresAtUnixSeconds - serverTime)
  const circleRef = useRef<SVGCircleElement>(null)
  // 环形倒计时基准：服务端同步到该 buff（到期时间变化）时，以同步时刻的剩余时间重建基准，
  // 使 buff 被刷新为更长或更短的持续时间时比例都保持真实；常驻 Buff 不重建倒计时基准。
  const [baseDuration, setBaseDuration] = useState(() => Math.max(10, remaining))
  useEffect(() => {
    if (buff.expiresAtUnixSeconds >= 1e15) return
    const syncedRemaining = buff.expiresAtUnixSeconds - useGameStore.getState().serverTimeUnixSeconds
    setBaseDuration(Math.max(10, Math.max(0, syncedRemaining)))
  }, [buff.expiresAtUnixSeconds])
  const total = baseDuration

  // 效果说明与单字图标：效果行经通道注册表解析；色调按结算语义粗分，字形取 Buff 名首字。
  const bonusItems = extractBuffBonusItems(buff)
  const hasChannel = (channel: BuffEffectChannel) =>
    buff.effects.some(effect => effect.channel === channel && effect.value > 0)
  const tone: 'qi' | 'jade' | 'purple' | 'danger' | 'gold' =
    hasChannel('damageAbsorptionPoints') ? 'jade'
      : (hasChannel('hpRegen') || hasChannel('mpRegen')) ? 'qi' : 'gold'
  const glyph = hasChannel('attackSpeedPoints')
    ? t('combat.arena.glyphSpeed')
    : buff.name.slice(0, 1) || t('combat.arena.glyphGeneric')

  const radius = 9
  const circumference = 2 * Math.PI * radius

  useLayoutEffect(() => {
    const circle = circleRef.current
    if (!circle) return

    circle.style.strokeDasharray = `${circumference}`
    // 常驻 Buff 环形遮罩恒满（offset=0），不启动刷新循环。
    if (buff.expiresAtUnixSeconds >= 1e15) {
      circle.style.strokeDashoffset = '0'
      return
    }
    // 环形倒计时按 200ms 低频刷新：Buff 以秒计剩余时长，逐帧更新没有可读性收益。
    let lastOffset = ''
    let intervalId: number | undefined
    const update = () => {
      const currentRemaining = Math.max(0, buff.expiresAtUnixSeconds - Date.now() / 1000)
      const ratio = Math.min(1, Math.max(0, currentRemaining / total))
      const offset = `${circumference * (1 - ratio)}`
      if (offset !== lastOffset) {
        lastOffset = offset
        circle.style.strokeDashoffset = offset
      }
      if (currentRemaining <= 0 && intervalId !== undefined) {
        window.clearInterval(intervalId)
        intervalId = undefined
      }
    }

    intervalId = window.setInterval(update, 200)
    update()
    return () => window.clearInterval(intervalId!)
  }, [buff.expiresAtUnixSeconds, circumference, total])

  return (
    <FloatingPopover
      content={
        <FloatingInfoCard
          badge={t('combat.arena.buffStacks', { n: formatNumber(buff.stacks) })}
          badgeTone={tone}
          description={bonusItems.length > 0 ? <BonusAsideList items={bonusItems} /> : t('combat.arena.buffGeneric')}
          subtitle={permanent
            ? t('combat.arena.buffPermanent')
            : t('combat.arena.buffRemaining', { n: formatNumber(remaining) })}
          title={buff.name}
        />
      }
      interactive={false}
      openDelay={60}
      placement="bottom"
      size="small"
    >
      <div className={`arena-buff-square arena-buff-square--${tone}`}>
        <span className="arena-buff-glyph">{glyph}</span>
        <svg className="arena-buff-cd-svg" viewBox="0 0 24 24">
          <circle className="arena-buff-cd-bg" cx="12" cy="12" r={radius} />
          <circle
            className="arena-buff-cd-fill"
            cx="12"
            cy="12"
            r={radius}
            ref={circleRef}
          />
        </svg>
        {buff.stacks > 1 && <span className="arena-buff-stack">{buff.stacks}</span>}
      </div>
    </FloatingPopover>
  )
}

/**
 * 实体增益/减益 Buff 列表横条组件
 */
function ArenaActorBuffs({ buffs = [] }: { buffs?: readonly Buff[] }): React.JSX.Element | null {
  const t = useT()
  if (!buffs || buffs.length === 0) return null

  return (
    <div className="arena-actor-buff-list" aria-label={t('combat.arena.buffsAria')}>
      {buffs.map(buff => (
        <ArenaBuffSquare buff={buff} key={buff.buffId} />
      ))}
    </div>
  )
}

/**
 * 历练/搜寻阶段垂直或水平平滑填充条组件
 */
function ArenaPhaseFill({
  expedition,
  orientation = 'vertical',
}: {
  expedition: ExpeditionProgress
  orientation?: 'horizontal' | 'vertical'
}): React.JSX.Element {
  const fillRef = useRef<HTMLElement>(null)
  const animationRef = useRef<Animation | null>(null)
  const lastQueueSyncPerformanceTime = useGameStore(state => state.lastQueueSyncPerformanceTime ?? state.lastSyncPerformanceTime)
  const paused = useGameStore(state => state.queue.paused)
  const anchorRef = useRef<{
    signature: string
    elapsed: number
    duration: number
    startedAt: number
  } | null>(null)

  useLayoutEffect(() => {
    const fill = fillRef.current
    if (!fill) return
    const duration = Math.max(1, expedition.durationSeconds * 1000)
    const baseElapsedMs = Math.max(0, (expedition.elapsedSeconds ?? (expedition.progress * expedition.durationSeconds)) * 1000)
    const elapsedSinceSync = paused ? 0 : Math.max(0, performance.now() - lastQueueSyncPerformanceTime)
    const elapsed = Math.max(0, Math.min(duration, baseElapsedMs + elapsedSinceSync))
    const transformProperty = orientation === 'vertical' ? 'scaleY' : 'scaleX'
    if (paused) {
      animationRef.current?.cancel()
      anchorRef.current = null
      fill.style.transform = `${transformProperty}(${elapsed / duration})`
      return
    }
    const signature = `${expedition.phase}|${expedition.waveNumber ?? 0}|${duration}`
    const now = performance.now()
    const anchor = anchorRef.current
    if (anchor?.signature === signature && animationRef.current?.playState === 'running') {
      const expected = Math.min(anchor.duration, anchor.elapsed + now - anchor.startedAt)
      if (Math.abs(elapsed - expected) <= 500) return
    }

    animationRef.current?.cancel()
    const fraction = elapsed / duration
    fill.style.transform = `${transformProperty}(${fraction})`
    anchorRef.current = { signature, elapsed, duration, startedAt: now }
    const remaining = duration - elapsed
    if (remaining <= 0 || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    animationRef.current = fill.animate(
      [{ transform: `${transformProperty}(${fraction})` }, { transform: `${transformProperty}(1)` }],
      { duration: remaining, easing: 'linear', fill: 'forwards' },
    )
  }, [expedition.waveNumber, expedition.durationSeconds, expedition.elapsedSeconds, expedition.phase, expedition.progress, lastQueueSyncPerformanceTime, orientation, paused])

  useEffect(() => () => animationRef.current?.cancel(), [])

  return <i ref={fillRef} />
}

/**
 * 战场中心历练/搜寻阶段状态告示牌组件
 */
function ArenaPhaseState({ expedition }: { expedition: ExpeditionProgress }): React.JSX.Element {
  const t = useT()
  const timeRef = useRef<HTMLSpanElement>(null)
  const lastQueueSyncPerformanceTime = useGameStore(state => state.lastQueueSyncPerformanceTime ?? state.lastSyncPerformanceTime)
  const paused = useGameStore(state => state.queue.paused)
  const isInitialSearch = expedition.phase === 'initial-search'

  const title = expedition.phase === 'respawn' ? t('combat.arena.phaseDeath') : isInitialSearch
    ? t('combat.arena.phaseSearch')
    : t('combat.arena.phaseHunt')

  const sigil = isInitialSearch ? t('combat.arena.sigilSearch') : t('combat.arena.sigilHunt')
  const baseRemaining = Math.max(0, expedition.durationSeconds - expedition.elapsedSeconds)

  useEffect(() => {
    const el = timeRef.current
    if (!el) return undefined
    const render = () => {
      const elapsedSec = paused ? 0 : Math.max(0, (performance.now() - lastQueueSyncPerformanceTime) / 1000)
      const rem = Math.max(0, baseRemaining - elapsedSec)
      const text = `${rem.toFixed(1)}s`
      if (el.textContent !== text) el.textContent = text
    }
    render()
    if (paused) return undefined
    const id = window.setInterval(render, 100)
    return () => window.clearInterval(id)
  }, [baseRemaining, lastQueueSyncPerformanceTime, paused])

  return (
    <div className={`arena-search-state arena-search-state--${expedition.phase}`} role="status" aria-label={title}>
      <span className="arena-search-sigil-vertical" aria-hidden="true">{sigil}</span>
      <strong className="arena-search-text-vertical">{title}</strong>
      <div className="arena-search-progress-vertical">
        <ArenaPhaseFill expedition={expedition} orientation="vertical" />
      </div>
      <span ref={timeRef} className="arena-search-time-vertical">{baseRemaining.toFixed(1)}s</span>
    </div>
  )
}

/**
 * 战场头像分发渲染组件 (ArenaPortrait)
 *
 * 基于统一 CharacterAvatar，自动适应 WebP/PNG/SVG 与矢量降级，并在敌方角色时自动左右镜像翻转。
 */
function ArenaPortrait({
  fallback,
  visual,
  role = 'player',
  actorId,
  name,
  isDead = false,
}: {
  fallback?: string
  visual?: CharacterVisual | null
  role?: 'player' | 'enemy'
  actorId?: string | null
  name?: string | null
  isDead?: boolean
}): React.JSX.Element {
  return (
    <CharacterAvatar
      actorId={actorId}
      fallback={fallback}
      isDead={isDead}
      isFlipped={role === 'enemy'}
      name={name}
      role={role}
      visual={visual}
    />
  )
}

/**
 * 解析行动对应的动画触发类型 (attack 普攻挥砍 / cast 施法蓄力)
 */
function resolveActorMotionKind(action?: CombatActionState | null): ActorMotionKind | null {
  if (action?.kind === 'cast') return 'cast'
  if (action?.kind === 'attack') return 'attack'
  return null
}

/**
 * 九宫格战斗实体卡片组件 (ArenaActor)
 *
 * 封装单个参战角色（己方或敌方）的全部实时状态渲染：
 * 1. 顶部名字与气血/法力双资源条；
 * 2. 形象立绘台、受击震颤 Impact 粒子、施法/攻击雷光剑气特效 FX；
 * 3. 底部读条进度与生效 Buff 悬浮气泡；
 * 4. 支持键盘与点击唤起属性查看模态窗。
 */
function ArenaActor({
  actorKey,
  actorId,
  name,
  seal,
  role,
  hp,
  maxHp,
  mp,
  maxMp,
  threat = null,
  action,
  buffs = [],
  onInspect,
  visual,
  visible = true,
  compact = false,
  recentEvents,
}: {
  /** 实体唯一键：切换目标/波次时重置跳字与受击特效状态 */
  actorKey: string
  /** 实体标识符（立绘资源检索用） */
  actorId?: string | null
  name: string
  /** 立绘缺失时的单字印章兜底文本 */
  seal: string
  role: 'player' | 'enemy'
  hp: number
  maxHp: number
  mp: number
  maxMp: number
  /** 累积威胁值（灵力条下方实时展示；为 null 时不渲染该行） */
  threat?: number | null
  action?: CombatActionState | null
  buffs?: readonly Buff[]
  /** 点击/回车唤起属性详情模态窗回调；缺省退化为纯展示态（尸体等不可交互单位） */
  onInspect?: () => void
  visual?: CharacterVisual | null
  /** 非激活态（搜寻阶段敌方未入场）时置灰休眠且不可聚焦 */
  visible?: boolean
  compact?: boolean
  recentEvents?: readonly CombatRecentEvent[] | null
}): React.JSX.Element {
  const t = useT()
  const articleRef = useRef<HTMLElement | null>(null)
  const nextMotionId = useRef(1)
  const nextImpactSequence = useRef(0)
  const previousHitpoints = useRef({ actorKey, hp })
  /** 对方阵营最前存活单位（纵列顺序即铺位顺序，前排居中槽优先），作为无目标协议下攻击方位的几何近似 */
  const findOpposingActor = useCallback((): Element | null => {
    const field = articleRef.current?.closest('.combat-arena-field')
    const selector = role === 'enemy'
      ? '.arena-formation--allies .arena-slot--occupied:not(.arena-slot--slain) .arena-actor:not(.is-dormant):not(.is-dead)'
      : '.arena-formation--enemies .arena-slot--occupied:not(.arena-slot--slain):not(.arena-slot--corpse) .arena-actor:not(.is-dormant)'
    return field?.querySelector(selector) ?? null
  }, [role])
  const scheduledAction = useRef<{
    signature: string
    deadline: number
    kind: ActorMotionKind
    timer: number
  } | null>(null)
  const clearActionMotionTimer = useRef<number | null>(null)
  const clearImpactTimer = useRef<number | null>(null)
  const [actionMotion, setActionMotion] = useState<ActorMotion | null>(null)
  const [impact, setImpact] = useState<ActorImpact | null>(null)
  const [dodging, setDodging] = useState(false)
  const clearDodgeTimer = useRef<number | null>(null)
  const handledCombatEventsRef = useRef(new Set<string>())
  const lastEventImpactTime = useRef(0)
  /** 事件驱动出手的排期时刻：与倒计时兜底出手去重，避免同一次攻击挥两刀 */
  const lastEventMotionAt = useRef(0)
  const actorTimers = useRef(new Set<number>())

  const triggerActionMotion = useCallback((kind: ActorMotionKind) => {
    const aim = kind === 'attack'
      ? measureArenaAim(articleRef.current, findOpposingActor())
      : { slope: 0, angleDeg: 0 }
    const nextMotion = { id: nextMotionId.current++, kind, aimY: aim.slope * 16, aimAngleDeg: aim.angleDeg }
    setActionMotion(nextMotion)
    if (clearActionMotionTimer.current !== null) window.clearTimeout(clearActionMotionTimer.current)
    clearActionMotionTimer.current = window.setTimeout(() => setActionMotion(null), actorMotionDurationMilliseconds[kind])
  }, [findOpposingActor])

  // 1. 消费事件流：攻击方按批次重放时刻起手出招，受击方精确在击中帧 (Hit-Frame) 卡点触发受击/闪避
  useEffect(() => {
    if (!recentEvents || recentEvents.length === 0) return
    const handled = handledCombatEventsRef.current
    const scheduled = collectScheduledCombatEvents(
      recentEvents,
      event => isActorMatchingId(event.attackerId, actorKey) || isActorMatchingId(event.targetId, actorKey),
      handled,
    )

    for (const { event, key, delay, comboIndex } of scheduled) {
      handled.add(key)
      if (handled.size > 200) {
        const oldest = handled.values().next().value
        if (oldest) handled.delete(oldest)
      }

      // (1) 若当前单位为攻击方：在批次重放时刻起手（挥砍突进或聚气施法）
      if (isActorMatchingId(event.attackerId, actorKey) && comboIndex === 0) {
        lastEventMotionAt.current = Date.now() + delay
        const motionTimer = window.setTimeout(() => {
          actorTimers.current.delete(motionTimer)
          triggerActionMotion(event.abilityId ? 'cast' : 'attack')
        }, delay)
        actorTimers.current.add(motionTimer)
      }

      // (2) 若当前单位为受击方：在击中帧时刻 (hitDelay) 精确卡点触发受击受挫或闪避身法
      if (isActorMatchingId(event.targetId, actorKey)) {
        const hitDelay = delay + combatHitDelayMilliseconds(event)
        if (!event.hit) {
          // 闪避身法：在刀刃砍到面前的瞬间侧身避开
          const timer = window.setTimeout(() => {
            actorTimers.current.delete(timer)
            setDodging(true)
            if (clearDodgeTimer.current !== null) window.clearTimeout(clearDodgeTimer.current)
            clearDodgeTimer.current = window.setTimeout(() => setDodging(false), 450)
          }, hitDelay)
          actorTimers.current.add(timer)
        } else if (event.damage > 0) {
          // 受击受挫：在刀刃砍到或法术爆发的瞬间闪红/闪金受挫
          lastEventImpactTime.current = Date.now() + hitDelay
          const timer = window.setTimeout(() => {
            actorTimers.current.delete(timer)
            const strength = Math.max(0.25, Math.min(1, event.damage / Math.max(1, maxHp)))
            const aim = measureArenaAim(findOpposingActor(), articleRef.current)
            const variant: ActorImpactVariant = event.critical
              ? 'crit'
              : event.broken
              ? 'break'
              : event.parried
              ? 'parry'
              : actorImpactVariants[nextImpactSequence.current++ % actorImpactVariants.length]

            const nextImpact: ActorImpact = {
              id: nextMotionId.current++,
              variant,
              distance: (event.critical ? 6 : 3) + strength * (event.critical ? 10 : 6),
              opacity: (event.critical ? 0.95 : 0.75) + strength * 0.25,
              hitY: aim.slope * 7,
              impactAngleDeg: aim.angleDeg,
              isCritical: event.critical,
            }
            setImpact(nextImpact)
            if (clearImpactTimer.current !== null) window.clearTimeout(clearImpactTimer.current)
            clearImpactTimer.current = window.setTimeout(() => setImpact(null), 560)
          }, hitDelay)
          actorTimers.current.add(timer)
        }
      }
    }
  }, [recentEvents, actorKey, maxHp, findOpposingActor, triggerActionMotion])

  useLayoutEffect(() => {
    const previous = previousHitpoints.current
    if (previous.actorKey !== actorKey) {
      previousHitpoints.current = { actorKey, hp }
      nextImpactSequence.current = 0
      handledCombatEventsRef.current.clear()
      lastEventMotionAt.current = 0
      lastEventImpactTime.current = 0
      actorTimers.current.forEach(timer => window.clearTimeout(timer))
      actorTimers.current.clear()
      if (clearActionMotionTimer.current !== null) window.clearTimeout(clearActionMotionTimer.current)
      clearActionMotionTimer.current = null
      if (clearDodgeTimer.current !== null) window.clearTimeout(clearDodgeTimer.current)
      clearDodgeTimer.current = null
      setActionMotion(null)
      setDodging(false)
      if (clearImpactTimer.current !== null) window.clearTimeout(clearImpactTimer.current)
      clearImpactTimer.current = null
      setImpact(null)
      return
    }

    // 保底：若非近期显式攻击事件引起的掉血（如灼烧 DoT），且距离上次事件受击超 400ms，立即触发受击
    if (hp < previous.hp && Date.now() - lastEventImpactTime.current > 400) {
      const strength = Math.max(0.25, Math.min(1, (previous.hp - hp) / Math.max(1, maxHp)))
      const aim = measureArenaAim(findOpposingActor(), articleRef.current)
      const nextImpact = {
        id: nextMotionId.current++,
        variant: actorImpactVariants[nextImpactSequence.current++ % actorImpactVariants.length],
        distance: 3 + strength * 6,
        opacity: 0.75 + strength * 0.25,
        hitY: aim.slope * 7,
        impactAngleDeg: aim.angleDeg,
      }
      setImpact(nextImpact)
      if (clearImpactTimer.current !== null) window.clearTimeout(clearImpactTimer.current)
      clearImpactTimer.current = window.setTimeout(() => setImpact(null), 560)
    }
    previousHitpoints.current = { actorKey, hp }
  }, [actorKey, hp, maxHp, findOpposingActor])

  // 行动倒计时保底（主要用于非战斗读条或无显式事件产出的持续动作）
  useEffect(() => {
    const kind = resolveActorMotionKind(action)
    const deadline = action?.completesAtUnixMilliseconds
    if (!visible || hp <= 0 || kind === null || deadline == null) {
      if (scheduledAction.current !== null) window.clearTimeout(scheduledAction.current.timer)
      scheduledAction.current = null
      return
    }

    const signature = `${actorKey}|${kind}|${deadline}`
    if (scheduledAction.current?.signature === signature) return
    if (scheduledAction.current !== null) window.clearTimeout(scheduledAction.current.timer)

    const duration = actorMotionDurationMilliseconds[kind]
    const delay = deadline - actorMotionLeadMilliseconds[kind] - Date.now()
    if (delay < -duration) {
      scheduledAction.current = null
      return
    }
    // 事件驱动出手已在附近排期：倒计时兜底不再重复挥刀（快照批次与行动条预测常落在同一时刻）
    const fireAt = Date.now() + Math.max(0, delay)
    if (lastEventMotionAt.current > 0 && Math.abs(fireAt - lastEventMotionAt.current) < 320) {
      scheduledAction.current = null
      return
    }

    const play = () => {
      triggerActionMotion(kind)
    }
    const timer = window.setTimeout(play, Math.max(0, delay))
    scheduledAction.current = { signature, deadline, kind, timer }
  }, [action?.completesAtUnixMilliseconds, action?.kind, actorKey, hp, visible, triggerActionMotion])

  useEffect(() => () => {
    if (scheduledAction.current !== null) window.clearTimeout(scheduledAction.current.timer)
    if (clearActionMotionTimer.current !== null) window.clearTimeout(clearActionMotionTimer.current)
    if (clearImpactTimer.current !== null) window.clearTimeout(clearImpactTimer.current)
    if (clearDodgeTimer.current !== null) window.clearTimeout(clearDodgeTimer.current)
    actorTimers.current.forEach(timer => window.clearTimeout(timer))
    actorTimers.current.clear()
  }, [])

  const interactive = onInspect !== undefined
  return (
    <article
      aria-label={t('combat.arena.inspectAria', { name: name || t('combat.arena.defaultActorName') })}
      className={`arena-actor arena-actor--${role}${compact ? ' arena-actor--compact' : ''}${visible ? '' : ' is-dormant'}${hp <= 0 ? ' is-dead' : ''}${actionMotion ? ` is-action-${actionMotion.kind}` : ''}${impact ? ' is-hit' : ''}${dodging ? ' is-dodging' : ''}`}
      ref={articleRef}
      style={{
        '--arena-aim-angle': `${actionMotion?.aimAngleDeg ?? 0}deg`,
        '--arena-aim-y': `${actionMotion?.aimY ?? 0}px`,
        '--arena-hit-y': `${impact?.hitY ?? 0}px`,
        '--arena-impact-angle': `${impact?.impactAngleDeg ?? 0}deg`,
      } as CSSProperties}
      onClick={onInspect}
      onKeyDown={interactive ? event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onInspect()
        }
      } : undefined}
      role={interactive ? 'button' : undefined}
      tabIndex={interactive && visible ? 0 : -1}
    >
      <div className="arena-actor-card">
        <header className="arena-overhead">
          <strong>{name || '\u00a0'}</strong>
          <div className="arena-vitals">
            <ArenaResource actorKey={actorKey} max={maxHp} tone="hp" value={hp} recentEvents={recentEvents} />
            <ArenaResource actorKey={actorKey} frozen={hp <= 0} max={maxMp} tone="mp" value={mp} />
            {threat != null && (
              <span aria-label={t('combat.arena.threatAria', { n: formatInteger(threat) })} className="arena-threat">
                <i aria-hidden="true">{t('combat.arena.threatGlyph')}</i>
                <b><UnitNumber disableTooltip value={threat} /></b>
              </span>
            )}
          </div>
        </header>
        <div className="arena-avatar-stage">
          <div className="arena-avatar" aria-label={t('combat.arena.portraitAria', { name })}>
            <ArenaPortrait actorId={actorId} fallback={seal} isDead={hp <= 0} name={name} role={role} visual={visual} />
          </div>
          {actionMotion && (
            <span className={`arena-action-fx arena-action-fx--${actionMotion.kind}`} key={actionMotion.id} aria-hidden="true">
              <i /><i /><i />
            </span>
          )}
          {impact && (
            <span
              className={`arena-impact-fx arena-impact-fx--${impact.variant}`}
              key={impact.id}
              aria-hidden="true"
              style={{
                '--impact-distance': `${impact.distance}px`,
                '--impact-distance-far': `${impact.distance * 1.8}px`,
                '--impact-opacity': impact.opacity,
              } as CSSProperties}
            >
              <i /><i /><i />
            </span>
          )}
        </div>
        <ArenaActionBar action={action} role={role} />
        <ArenaActorBuffs buffs={buffs} />
      </div>
    </article>
  )
}

/** 生活目标卡单条产出展示行（物品 / 修为·灵力·气血资源 / 增益） */
interface LifeYieldRow {
  key: string
  name: string
  glyph: string
  tone: string
  /** 单轮产出（buff 行为层数） */
  perRound: number
  /** 剩余计划总产出（无限循环或不可累计的 buff 为 null） */
  total: number | null
  unit: string
  /** Buff 持续秒数（仅 buff 行有值） */
  buffDurationSeconds?: number
}

/** 资源类产出（修为/灵力/气血）的图标字印与色调 */
const RESOURCE_YIELD_META: Record<ResourceYieldKind, { glyph: string; tone: string }> = {
  experience: { glyph: '修', tone: 'gold' },
  mana: { glyph: '灵', tone: 'cyan' },
  hitpoints: { glyph: '血', tone: 'red' },
}

/**
 * 非战斗生活行动总目标与轮次进度卡片 (ArenaLifeGoalCard)
 *
 * 当玩家队列首项为炼丹、打坐修炼、游历采集等生活类行动时，
 * 右侧敌方九宫格无缝切换为此卡片，显示目标产物大图标、计划总产出、所需原料消耗清单与平滑进度条。
 * 产出明细与行动详情弹窗共用 calculateActionYieldPreview 口径（含持续消耗品/灵脉倍率与按秒速率折算），
 * 行动配表 yields 变更经 actionDef 下发后自动同步，禁止再按类型硬编造假产物。
 */
function ArenaLifeGoalCard({
  queueEntry,
  actionDef,
  onStop,
  hidden = false,
}: {
  /** 当前正在执行的队列条目 */
  queueEntry?: QueueEntry | null
  /** 对应动作定义详情 */
  actionDef?: ActionDefinition | null
  /** 中止当前生活行动回调 */
  onStop?: () => void
  /** 所在页签是否处于隐藏态：隐藏时冻结 world 订阅，保持挂载但不随赶路增量重渲 */
  hidden?: boolean
}): React.JSX.Element {
  const t = useT()
  const itemCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const inventory = useGameStore(state => state.inventory)
  const autoUseSlots = useGameStore(state => state.queue.autoUseSlots)
  const stats = useGameStore(state => state.stats)
  const world = useGameStore(hidden ? () => FROZEN_WORLD : state => state.world)
  const maps = useGameStore(state => state.maps)
  const combat = useGameStore(state => state.combat)

  const actionId = queueEntry?.actionId ?? actionDef?.id ?? ''
  // forge.* 与 craft.* 精炼归炼器，alchemy.* 与 craft.extract-* 提炼归炼丹：两者同为 alchemy 大类，按 id 前缀区分
  const isForging = actionId.startsWith('forge.') || (actionId.startsWith('craft.') && !actionId.startsWith('craft.extract-'))
  const isEnhancement = queueEntry?.type === 'enhancement' || actionDef?.type === 'enhancement' || actionId === ENHANCE_ACTION_ID
  const title = queueEntry?.type === 'cultivation' ? t('combat.life.tagCultivation')
    : isForging ? t('combat.life.tagForging')
    : queueEntry?.type === 'alchemy' ? t('combat.life.tagAlchemy')
    : queueEntry?.type === 'exploration' ? t('combat.life.tagExploration')
    : isEnhancement ? t('insight.technique.enhancement')
    : t('combat.life.tagDefault')
  const actionName = resolveQueueEntryDisplayName(queueEntry, actionDef?.name, world, maps) || actionDef?.name || t('combat.life.fallbackActionName')
  const isInfinite = (queueEntry?.remainingRepeats ?? 0) < 0
  const remainingRepeats = queueEntry?.remainingRepeats ?? 0
  const repeats = isInfinite ? -1 : remainingRepeats

  const yieldPreview = actionDef ? calculateActionYieldPreview({
    action: actionDef,
    inventory,
    items: itemCatalog,
    autoUseSlots,
    world,
    stats,
    combat,
    equipmentCatalog,
  }) : null

  // 产出展示行：首项为主产物（驱动大图标/目标产物/计划总量），其余进「其余产出」清单
  const outputs: LifeYieldRow[] = []
  if (isEnhancement) {
    const targetItemId = queueEntry?.targetItemId
    const targetLevel = queueEntry?.targetLevel ?? 1
    const currentTracked = queueEntry?.enhancementCurrentLevel ?? null
    const targetEq = targetItemId ? findEquipmentInCatalog(equipmentCatalog, targetItemId) : null
    const targetItem = targetItemId ? findItemInCatalog(itemCatalog, targetItemId) : null
    const baseName = targetEq?.name || targetItem?.name || targetItemId || t('app.crafting.enhancement')
    const displayName = currentTracked !== null && currentTracked > 0
      ? `${baseName} +${currentTracked} → +${targetLevel}`
      : `${baseName} +${targetLevel}`
    const meta = targetItemId ? resolveItemMetadata(targetItemId) : null

    outputs.push({
      key: 'enhancement:target',
      name: displayName,
      glyph: meta?.glyph ?? '⚔️',
      tone: meta?.tone ?? 'gold',
      perRound: 1,
      total: isInfinite ? null : 1,
      unit: t('combat.life.unitItem'),
    })

    const eqTier = targetEq?.tier ?? 1
    const itemLvl = convertTierToLevel(eqTier)
    const yields = calculateEnhancementYields(itemLvl, targetLevel)
    outputs.push({
      key: 'rootexp:consciousness',
      name: t('combat.attr.divineSense'),
      glyph: '☯',
      tone: 'crimson',
      perRound: yields.consciousnessExp,
      total: isInfinite ? null : yields.consciousnessExp * repeats,
      unit: t('combat.life.unitPoint'),
    })
    outputs.push({
      key: 'technique:enhancement',
      name: t('insight.technique.enhancement'),
      glyph: '🔨',
      tone: 'gold',
      perRound: yields.techniqueExp,
      total: isInfinite ? null : yields.techniqueExp * repeats,
      unit: t('combat.life.unitPoint'),
    })
  } else if (yieldPreview) {
    for (const output of yieldPreview.itemOutputs) {
      const meta = resolveItemMetadata(output.itemId)
      const definition = findItemInCatalog(itemCatalog, output.itemId)
      outputs.push({
        key: `item:${output.itemId}`,
        name: definition?.name || meta?.label || output.itemId,
        glyph: meta?.glyph ?? '🌿',
        tone: meta?.tone ?? 'gold',
        perRound: output.finalAmount,
        total: isInfinite ? null : output.finalAmount * repeats,
        unit: t('combat.life.unitItem'),
      })
    }
    for (const entry of yieldPreview.resourceYields) {
      const meta = RESOURCE_YIELD_META[entry.kind]
      outputs.push({
        key: `resource:${entry.kind}`,
        name: t(`app.action.yieldKind.${entry.kind}`),
        glyph: meta.glyph,
        tone: meta.tone,
        perRound: entry.finalAmount,
        total: isInfinite ? null : entry.finalAmount * repeats,
        unit: t('combat.life.unitPoint'),
      })
    }
    for (const buff of yieldPreview.buffYields) {
      outputs.push({
        key: `buff:${buff.buffId}`,
        name: buff.name,
        glyph: t('combat.arena.glyphGeneric'),
        tone: 'gold',
        perRound: buff.stacks,
        total: null,
        unit: t('combat.life.unitStack'),
        buffDurationSeconds: buff.durationSeconds,
      })
    }
  }
  const primary = outputs[0] ?? null
  const isPendingBreakthrough = yieldPreview?.cultivation?.isPendingBreakthrough === true

  // 所需原料分析
  const inputs: Record<string, number> = isEnhancement
    ? {
        [SPIRIT_STONE_ITEM_ID]: Math.max(1, queueEntry?.targetLevel ?? 1) * 10,
        ...(queueEntry?.auxItemId ? { [queueEntry.auxItemId]: 1 } : {}),
      }
    : (actionDef?.inputs || {})
  const inputEntries = Object.entries(inputs)

  // 进度条 DOM 引用与全局帧级统一同步
  const barRef = useRef<HTMLDivElement>(null)
  const textRef = useRef<HTMLSpanElement>(null)

  useSmoothProgressRef({
    entry: queueEntry,
    fillRef: barRef,
    textRef: textRef,
    textFormat: (currentSec, dur, pct) => getT()('combat.life.roundProgress', { current: currentSec.toFixed(1), duration: formatNumber(dur), percent: formatNumber(pct) }),
  })

  return (
    <div className="arena-life-card">
      <header className="arena-life-header">
        <span className="arena-life-tag">{title}</span>
        <strong className="arena-life-action-name">{actionName}</strong>
      </header>

      <div className="arena-life-body">
        <div className={`arena-life-icon-box arena-life-icon-box--${primary?.tone ?? 'gold'}`}>
          <span className="arena-life-glyph">{primary?.glyph ?? (queueEntry ? ACTION_TAB_META[queueEntry.type].glyph : '☯')}</span>
          <span className="arena-life-count-badge">
            {isInfinite
              ? '∞'
              : primary === null
                ? '—'
                : primary.total !== null
                  ? `x${formatNumber(primary.total)}`
                  : `×${formatNumber(primary.perRound)}`}
          </span>
        </div>

        <div className="arena-life-meta">
          <div className="arena-life-target-title">
            <span>{t('combat.life.targetOutput')}</span>
            <strong className="tone-gold">{primary?.name ?? '—'}</strong>
          </div>
          {primary && (
            <div className="arena-life-target-total">
              <span>{t('combat.life.plannedTotal')}</span>
              <strong className="tone-bright">
                {isInfinite
                  ? '∞'
                  : primary.total !== null
                    ? `${formatNumber(primary.total)} ${primary.unit}`
                    : t('combat.life.buffPerRound', { n: formatNumber(primary.perRound) })}
              </strong>
            </div>
          )}
          <div className="arena-life-subinfo">
            <span>{primary ? t('combat.life.perBatch', { n: `${formatNumber(primary.perRound)} ${primary.unit}` }) : ''}</span>
            <span>{isInfinite ? t('combat.life.infiniteRounds') : t('combat.life.remainingRounds', { n: formatNumber(remainingRepeats) })}</span>
          </div>
        </div>

        {(outputs.length > 1 || isPendingBreakthrough) && (
          <div className="arena-life-inputs">
            <span className="arena-life-inputs-label">{t('combat.life.extraOutputsLabel')}</span>
            <div className="arena-life-inputs-list">
              {outputs.slice(1).map(row => (
                <span className="arena-life-input-chip" key={row.key}>
                  {row.buffDurationSeconds !== undefined
                    ? t('combat.life.buffChip', { name: row.name, stacks: formatNumber(row.perRound), duration: row.buffDurationSeconds })
                    : row.total !== null
                      ? t('combat.life.yieldChip', { name: row.name, n: formatNumber(row.perRound), total: formatNumber(row.total) })
                      : t('combat.life.yieldChipInfinite', { name: row.name, n: formatNumber(row.perRound) })}
                </span>
              ))}
              {isPendingBreakthrough && (
                <span className="arena-life-input-chip">{t('app.action.pendingBreakthroughTag')} → {t('combat.report.foundation')}</span>
              )}
            </div>
          </div>
        )}

        {inputEntries.length > 0 && (
          <div className="arena-life-inputs">
            <span className="arena-life-inputs-label">{t('combat.life.inputsLabel')}</span>
            <div className="arena-life-inputs-list">
              {inputEntries.map(([inId, inAmt]) => {
                const inDef = findItemInCatalog(itemCatalog, inId)
                const inMeta = resolveItemMetadata(inId)
                const inName = inDef?.name || inMeta?.label || inId
                return (
                  <span className="arena-life-input-chip" key={inId}>
                    {t('combat.life.inputChip', { name: inName, n: formatNumber(inAmt), total: isInfinite ? '∞' : formatNumber(inAmt * repeats) })}
                  </span>
                )
              })}
            </div>
          </div>
        )}
      </div>

      <div className="arena-life-footer">
        <div className="arena-life-progress-bar">
          <div
            className="arena-life-progress-fill"
            ref={barRef}
          >
            <i />
          </div>
          <span className="arena-life-progress-text" ref={textRef} />
        </div>
        {onStop && (
          <button className="arena-life-stop-btn danger" onClick={onStop} type="button">
            {t('combat.life.stopAction')}
          </button>
        )}
      </div>
    </div>
  )
}

/**
 * 赶路/移动总目标与里程卡片 (ArenaTravelCard)
 */
function ArenaTravelCard({
  queueEntry,
  onStop,
  hidden = false,
}: {
  /** 当前正在执行的队列条目 */
  queueEntry?: QueueEntry | null
  /** 中止当前赶路行动回调 */
  onStop?: () => void
  /** 所在页签是否处于隐藏态：隐藏时冻结 world 订阅，保持挂载但不随赶路增量重渲 */
  hidden?: boolean
}): React.JSX.Element {
  const t = useT()
  const expedition = queueEntry?.expedition
  const world = useGameStore(hidden ? () => FROZEN_WORLD : state => state.world)
  const maps = useGameStore(state => state.maps)
  const actionName = resolveQueueEntryDisplayName(queueEntry, t('combat.travel.fallbackActionName'), world, maps)
  // 目的地名称优先按 wire 字段 travelDestinationNodeId 从地图定义解析，不再解析中文前缀
  const destinationNodeName = queueEntry?.travelDestinationNodeId
    ? maps.find(m => m.nodes.some(n => n.nodeId === queueEntry.travelDestinationNodeId))
        ?.nodes.find(n => n.nodeId === queueEntry.travelDestinationNodeId)?.name
    : undefined
  const destinationName = destinationNodeName ?? actionName
  const totalDistance = queueEntry?.travelTotalDistance ?? 0
  const duration = Math.max(1, expedition?.durationSeconds || queueEntry?.durationSeconds || 1)
  const progressSec = expedition?.elapsedSeconds || queueEntry?.progressSeconds || 0

  // 进度条 DOM 引用与全局帧级统一同步
  const barRef = useRef<HTMLDivElement>(null)
  const textRef = useRef<HTMLSpanElement>(null)
  const curDistRef = useRef<HTMLElement>(null)

  const phaseEntry: QueueEntry | null = queueEntry ? {
    ...queueEntry,
    progressSeconds: progressSec,
    durationSeconds: duration,
  } : null

  useSmoothProgressRef({
    entry: phaseEntry,
    fillRef: barRef,
    textRef: textRef,
    metaRef: curDistRef,
    metaFormat: (_sec, _dur, ratio) => {
      const cur = Math.min(totalDistance, Math.max(0, ratio * totalDistance))
      return `${cur.toFixed(1)} ${getT()('combat.travel.distanceUnit')}`
    },
    textFormat: (currentSec, dur, pct) => getT()('combat.travel.roundProgress', {
      current: currentSec.toFixed(1),
      duration: formatNumber(dur),
      percent: formatNumber(pct),
    }),
  })

  const initialRatio = duration > 0 ? Math.min(1, Math.max(0, progressSec / duration)) : 0
  const initialCurDist = Math.min(totalDistance, Math.max(0, initialRatio * totalDistance))

  return (
    <div className="arena-life-card arena-travel-card">
      <header className="arena-life-header">
        <span className="arena-life-tag arena-travel-tag">{t('combat.travel.tag')}</span>
        <strong className="arena-life-action-name">{actionName}</strong>
      </header>

      <div className="arena-life-body">
        <div className="arena-life-icon-box arena-life-icon-box--travel">
          <span className="arena-life-glyph">🧭</span>
          <span className="arena-life-count-badge">{t('combat.travel.badgeMoving')}</span>
        </div>

        <div className="arena-life-meta">
          <div className="arena-life-target-title">
            <span>{t('combat.travel.destination')}</span>
            <strong className="tone-gold">{destinationName}</strong>
          </div>
          <div className="arena-life-target-total">
            <span>{t('combat.travel.totalDistance')}</span>
            <strong className="tone-bright">
              {totalDistance.toFixed(1)} {t('combat.travel.distanceUnit')}
            </strong>
          </div>
          <div className="arena-life-subinfo">
            <span>{t('combat.travel.travelledDistance')}</span>
            <strong className="tone-gold" ref={curDistRef}>
              {initialCurDist.toFixed(1)} {t('combat.travel.distanceUnit')}
            </strong>
          </div>
        </div>
      </div>

      <footer className="arena-life-footer">
        <div className="arena-life-progress-bar">
          <div
            className="arena-life-progress-fill arena-travel-progress-fill"
            ref={barRef}
          >
            <i />
          </div>
          <span className="arena-life-progress-text" ref={textRef} />
        </div>
        {onStop && (
          <button className="arena-life-stop-btn danger" onClick={onStop} type="button">
            {t('combat.travel.stopTravel')}
          </button>
        )}
      </footer>
    </div>
  )
}

/**
 * 战场空闲待机提示卡片 (ArenaIdleCard)
 */
function ArenaIdleCard(): React.JSX.Element {
  const t = useT()
  return (
    <div className="arena-idle-card">
      <div className="arena-idle-sigil">{t('combat.idle.sigil')}</div>
      <strong className="arena-idle-title">{t('combat.idle.title')}</strong>
      <p className="arena-idle-desc">{t('combat.idle.desc')}</p>
    </div>
  )
}

/**
 * 参战单位详细属性与生效 Buff 模态窗组件 (CharacterAttributeModal)
 *
 * 角色、敌怪与斗法镜像共用同一斗法属性面板 (CombatAttributePanel)，
 * 数值统一来自服务端下发的 CombatAttributes 终值，前端不做任何派生估算。
 */
function CharacterAttributeModal({
  actorId,
  attributes,
  loadout,
  name,
  role = 'player',
  visual,
  buffs,
  drops,
  onClose,
  open,
  title,
}: {
  actorId?: string | null
  /** 服务端权威战斗属性终值（缺省时面板不渲染数值区） */
  attributes: CombatAttributes | null
  /** 单位装配（功法槽 + 已穿戴装备）；null/undefined 时属性面板不渲染「装配」Tab */
  loadout?: UnitLoadoutView | null
  name?: string | null
  role?: 'player' | 'enemy'
  visual?: CharacterVisual | null
  buffs: readonly Buff[]
  /** 敌怪掉落条目（仅敌怪详情传入，驱动第四个「掉落」Tab；undefined 不渲染该 Tab） */
  drops?: readonly MonsterDropDefinition[]
  onClose: () => void
  open: boolean
  title: string
}): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  return (
    <Modal className="character-attribute-modal" labelledBy={titleId} onClose={onClose} open={open} size="medium">
      <header className="character-attribute-header">
        <div className="character-attribute-avatar-stage">
          <CharacterAvatar
            actorId={actorId}
            className="character-attribute-avatar"
            isFlipped={role === 'enemy'}
            name={name || title}
            role={role}
            visual={visual}
          />
        </div>
        <div className="character-attribute-title-block">
          <h2 id={titleId}>{title}</h2>
          <span className={`character-attribute-role-badge character-attribute-role-badge--${role}`}>
            {role === 'enemy' ? t('combat.attrs.enemyRole') : t('combat.attrs.playerRole')}
          </span>
        </div>
      </header>
      {attributes
        ? <CombatAttributePanel attributes={attributes} buffs={buffs} drops={drops} loadout={loadout} />
        : <p className="nearby-empty">{t('insight.combat.attributesLoading')}</p>}
    </Modal>
  )
}

/** 敌方单位铺位顺序：纵深优先（前排中→中排中→后排中→前排上→…），同波敌人沿前后轴展开而非上下堆叠 */
const ENEMY_DEPTH_SLOTS: readonly number[] = [1, 4, 7, 2, 5, 8, 3, 6, 9]
/** 己方阵列填入序（§21.13）：首名成员坐镇正中，后续盟友按 成员序→中排中→后排中→中列上→中列下 铺开。 */
const ALLY_DEPTH_SLOTS: readonly number[] = [1, 4, 7, 2, 5, 8, 3, 6, 9]

/** 尸体消散提前量：寻敌倒数最后一秒统一播消散动画，末帧与新一波入场对齐 */
const CORPSE_DISSIPATE_LEAD_MS = 1000

/** 参战单位属性弹窗静态快照（点击时固化一次，不随战斗持续推进或单位死亡改变） */
interface InspectedCharacterSnapshot {
  targetKey: string
  actorId: string | null
  name: string
  role: 'player' | 'enemy'
  title: string
  visual: CharacterVisual | null
  attributes: CombatAttributes | null
  buffs: readonly Buff[]
  loadout?: UnitLoadoutView | null
  drops?: readonly MonsterDropDefinition[]
  enemyId?: string | null
}

/** 展示态空表：尸体/实时波次均无时的稳定引用 */
const EMPTY_DISPLAY_ENTRIES: EnemyDisplayState[] = []
/** 盟友展示态空表：未组队讨伐时的稳定引用 */
const EMPTY_ALLY_ENTRIES: AllyDisplayState[] = []

/** 敌方单位展示态：实时字段来自 wire 快照（enemies[] 或 duel 载荷）；属性面板为按需投影（UnitInspect） */
interface EnemyDisplayState {
  actorKey: string
  enemyId: string | null
  name: string
  seal: string
  hp: number
  maxHp: number
  mp: number
  maxMp: number
  /** 累积威胁值；斗法镜像无威胁语义时为 null（不渲染威胁行） */
  threat: number | null
  action?: CombatActionState | null
  visual?: CharacterVisual | null
  /** 服务端按需投影的完整战斗属性（详情弹窗唯一数值来源；未登记观察目标时为 null） */
  attributes?: CombatAttributes | null
  /** 生效中的战内 Buff（到期时刻已换算到服务端时钟域） */
  buffs?: Buff[]
}

/** 己方盟友展示态（§21.13 组队讨伐）：按 memberIndex 序铺位，倒地灰显、复活读条由 wire 帧合成。 */
interface AllyDisplayState {
  memberIndex: number
  playerId: string
  actorKey: string
  name: string
  avatar?: string
  seal: string
  hp: number
  maxHp: number
  mp: number
  maxMp: number
  isSelf: boolean
  /** 累积威胁值（敌方出手按全队威胁轮盘选目标的权重来源） */
  threat: number
  action?: CombatActionState | null
  /** 开打冻结战斗属性（详情弹窗唯一数值来源；未登记观察目标时为 null） */
  attributes?: CombatAttributes | null
  /** 开打冻结功法槽（按需投影，与 attributes 同帧交付；未观察时为 null） */
  skills?: CombatAbilitySlot[] | null
  /** 开打定格已穿戴装备（按需投影，与 attributes 同帧交付；未观察时为 null） */
  equipment?: EquippedItem[] | null
  /** 生效中的战内 Buff（到期时刻已换算到服务端时钟域） */
  buffs?: Buff[]
}

/**
 * 战内 Buff → 展示 Buff 形状：到期时刻从战斗时钟换算到服务端时钟域（serverNow + 剩余），
 * 与行动条 completesAtUnixMilliseconds 同一口径；永驻哨兵（>1e15 剩余）按常驻展示。
 */
function combatBuffToBuff(entry: CombatBuffEntry, battleElapsedSeconds: number, serverNowUnixMilliseconds: number): Buff {
  const remaining = entry.expiresAtSeconds - battleElapsedSeconds
  return {
    buffId: entry.buffId,
    name: entry.name,
    stacks: entry.stacks,
    expiresAtUnixSeconds: remaining > 1e15 ? 1e15 : serverNowUnixMilliseconds / 1000 + Math.max(0, remaining),
    effects: entry.effects,
    statRatios: entry.statRatios ?? null,
  }
}

/**
 * 由战斗时钟合成出手行动条：服务端下发的是下一次出手的战斗时刻（秒），
 * 用攻速作时长、剩余时间反推已耗时，供 SmoothActionFill/ActionCountdownBadge 平滑推进。
 * completesAtUnixMilliseconds 必须落在服务端时钟域：ArenaActor 的出手动画调度
 * 与徽章倒计时都以它做绝对截止比较，缺省会导致动画永不触发、文字被隐藏。
 */
function attackActionState(
  nextAttackAtSeconds: number,
  battleElapsedSeconds: number,
  intervalMilliseconds: number,
  serverNowUnixMilliseconds: number,
): CombatActionState | null {
  if (!Number.isFinite(nextAttackAtSeconds) || intervalMilliseconds <= 0) return null
  const duration = Math.max(1, intervalMilliseconds)
  const remaining = Math.max(0, (nextAttackAtSeconds - battleElapsedSeconds) * 1000)
  return {
    kind: 'attack',
    // name 仅需非空以通过徽章的待机判定；kind === 'attack' 时统一显示本地化普攻名。
    name: 'attack',
    durationMilliseconds: duration,
    elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
    completesAtUnixMilliseconds: serverNowUnixMilliseconds + remaining,
  }
}

/**
 * 敌方行动条：服务端 pendingActionDurationSeconds 即当前动作总时长（吟唱时长或普攻间隔），
 * pendingAbilityId 区分吟唱技能与普攻；两者共用同一条战斗时间线，前端不再另取配表时长。
 */
function enemyActionState(
  entry: ExpeditionEnemy,
  battleElapsedSeconds: number,
  serverNowUnixMilliseconds: number,
  abilities: readonly CombatAbilityDefinition[],
): CombatActionState | null {
  const remaining = Math.max(0, (entry.nextAttackAtSeconds - battleElapsedSeconds) * 1000)
  const duration = Math.max(1, entry.pendingActionDurationSeconds > 0
    ? entry.pendingActionDurationSeconds * 1000
    : (entry.attributes?.attackIntervalMilliseconds ?? 1))
  if (entry.pendingAbilityId) {
    const ability = findAbilityInCatalog(abilities, entry.pendingAbilityId)
    return {
      kind: 'cast',
      name: ability?.name ?? entry.pendingAbilityId,
      durationMilliseconds: duration,
      elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
      completesAtUnixMilliseconds: serverNowUnixMilliseconds + remaining,
    }
  }
  return {
    kind: 'attack',
    // name 仅需非空以通过徽章的待机判定；kind === 'attack' 时统一显示本地化普攻名。
    name: 'attack',
    durationMilliseconds: duration,
    elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
    completesAtUnixMilliseconds: serverNowUnixMilliseconds + remaining,
  }
}

/**
 * 组队讨伐盟友行动条：与 enemyActionState 同一战斗时钟口径，
 * 但盟友载荷不携带攻击间隔属性，动作时长恒走 pendingActionDurationSeconds 权威值；
 * 倒地时按 respawnAtSeconds（战斗时钟秒）合成复活读条，总时长取遭遇配表复活毫秒数。
 */
function allyActionState(
  ally: ExpeditionAlly,
  battleElapsedSeconds: number,
  serverNowUnixMilliseconds: number,
  abilities: readonly CombatAbilityDefinition[],
  respawnDurationMilliseconds: number,
  respawnName: string,
  respawnWaitName: string,
  phase?: ExpeditionProgress['phase'],
): CombatActionState | null {
  if (ally.hitpoints <= 0) {
    // 读条已走完、策略条件未成立：respawnAtSeconds 已被推到下次复查时刻，
    // 此时不再显示读条倒计时，改为不推进的等待态，等条件成立后自动回到读条/起身。
    if (ally.respawnWaitingUntilSeconds != null) {
      return {
        kind: 'respawn-wait',
        name: respawnWaitName,
        durationMilliseconds: 1,
        elapsedMilliseconds: 0,
        completesAtUnixMilliseconds: null,
      }
    }
    if (ally.respawnAtSeconds == null) return null
    const duration = Math.max(1, respawnDurationMilliseconds)
    const remaining = Math.max(0, (ally.respawnAtSeconds - battleElapsedSeconds) * 1000)
    return {
      kind: 'respawn',
      name: respawnName,
      durationMilliseconds: duration,
      elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
      completesAtUnixMilliseconds: serverNowUnixMilliseconds + remaining,
    }
  }
  // 与敌方一致：仅战斗相位推进出手条；搜寻/复活相位盟友待机不读条。
  if (phase !== 'combat') return null
  const remaining = Math.max(0, (ally.nextAttackAtSeconds - battleElapsedSeconds) * 1000)
  const duration = Math.max(1, ally.pendingActionDurationSeconds > 0 ? ally.pendingActionDurationSeconds * 1000 : 2000)
  if (ally.pendingAbilityId) {
    const ability = findAbilityInCatalog(abilities, ally.pendingAbilityId)
    return {
      kind: 'cast',
      name: ability?.name ?? ally.pendingAbilityId,
      durationMilliseconds: duration,
      elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
      completesAtUnixMilliseconds: serverNowUnixMilliseconds + remaining,
    }
  }
  return {
    kind: 'attack',
    name: 'attack',
    durationMilliseconds: duration,
    elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
    completesAtUnixMilliseconds: serverNowUnixMilliseconds + remaining,
  }
}

/**
 * 斗法剧本行动条：在回放时钟（battleElapsedSeconds）上找指定出手方的下一个事件，
 * 时长取该出手方相邻两次出手的间隔（首个事件回退到兜底攻速）；
 * 携带 abilityId 的剧本事件显示功法名（kind 'cast'），普攻事件走 attack。
 */
function duelActionState(
  duel: ExpeditionDuel | null | undefined,
  battleElapsedSeconds: number,
  attackerIsInitiatorSide: boolean,
  fallbackIntervalMilliseconds: number,
  serverNowUnixMilliseconds: number,
  abilities: readonly CombatAbilityDefinition[],
): CombatActionState | null {
  if (!duel || duel.resolved || duel.events.length === 0) return null
  let previousAt = -1
  for (const duelEvent of duel.events) {
    if (duelEvent.attackerIsInitiator !== attackerIsInitiatorSide) continue
    if (duelEvent.atSeconds > battleElapsedSeconds + 1e-9) {
      const durationSeconds = previousAt >= 0
        ? Math.max(0.1, duelEvent.atSeconds - previousAt)
        : Math.max(0.1, fallbackIntervalMilliseconds / 1000)
      const duration = Math.max(1, durationSeconds * 1000)
      const remaining = Math.max(0, (duelEvent.atSeconds - battleElapsedSeconds) * 1000)
      const ability = duelEvent.abilityId ? findAbilityInCatalog(abilities, duelEvent.abilityId) : undefined
      return {
        kind: ability ? 'cast' : 'attack',
        name: ability?.name ?? 'attack',
        durationMilliseconds: duration,
        elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
        completesAtUnixMilliseconds: serverNowUnixMilliseconds + remaining,
      }
    }
    previousAt = duelEvent.atSeconds
  }
  return null
}

/** 估算当前服务端 unix 毫秒（快照时刻 + 距同步的本地流逝），与徽章/动画调度同一时钟域。 */
function estimateServerNowUnixMilliseconds(): number {
  const state = useGameStore.getState()
  return state.serverTimeUnixMilliseconds + Math.max(0, performance.now() - state.lastSyncPerformanceTime)
}

/**
 * 战场九宫格阵型自适应缩放 Hook
 * 监听阵型容器真实几何空间，根据 3x3 槽位尺寸与角色基准尺寸（宽 96px，高 206px 含 Buff 与上下防撞冗余）
 * 动态推导并下发 --arena-scale，保证在任意分辨率与显示缩放（如 125%、150%、175% 等）的横屏模式下，
 * 双方 18 个角色（3x3 allies + 3x3 enemies）全部完整容纳且绝不发生垂直碰撞踩头。
 */
function useArenaFormationScale(
  formationRef: React.RefObject<HTMLDivElement | null>,
  allyCount: number,
  enemyCount: number
): { allyScale: number; enemyScale: number; allyRows: number; enemyRows: number } {
  const allyRows = resolveOccupiedRows(allyCount)
  const enemyRows = resolveOccupiedRows(enemyCount)

  const [scales, setScales] = useState<{ allyScale: number; enemyScale: number }>({
    allyScale: 1,
    enemyScale: 1,
  })

  useEffect(() => {
    const formationEl = formationRef.current
    if (!formationEl || typeof ResizeObserver === 'undefined') return

    const updateScale = () => {
      const rect = formationEl.getBoundingClientRect()
      const width = rect.width
      const height = rect.height

      if (width <= 0 || height <= 0) return

      const nextAllyScale = computeArenaFormationScale(width, height, window.innerWidth, allyCount, allyRows)
      const nextEnemyScale = computeArenaFormationScale(width, height, window.innerWidth, enemyCount, enemyRows)

      setScales(prev => {
        if (
          Math.abs(prev.allyScale - nextAllyScale) >= 0.01 ||
          Math.abs(prev.enemyScale - nextEnemyScale) >= 0.01
        ) {
          return { allyScale: nextAllyScale, enemyScale: nextEnemyScale }
        }
        return prev
      })
    }

    updateScale()

    const observer = new ResizeObserver(updateScale)
    observer.observe(formationEl)
    window.addEventListener('resize', updateScale)

    return () => {
      observer.disconnect()
      window.removeEventListener('resize', updateScale)
    }
  }, [formationRef, allyCount, enemyCount, allyRows, enemyRows])

  return { allyScale: scales.allyScale, enemyScale: scales.enemyScale, allyRows, enemyRows }
}

/**
 * 主战场竞技场核心组件 (CombatArena)
 *
 * 核心功能与交互流程：
 * 1. 左侧己方 3x3 阵型 (九宫格，1号位在前排中间靠近敌方)；
 * 2. 居中阶段提示：在搜寻 (initial-search / search) 阶段居中展示倒计时罗盘；
 * 3. 右侧动态区域：
 *    - 战斗状态：展示敌方 3x3 阵型与敌方实体状态；
 *    - 生活行动状态：展示炼丹/修炼/探索等行动总目标进度卡片 (ArenaLifeGoalCard)；
 *    - 空闲状态：展示空闲太极符文卡片 (ArenaIdleCard)；
 * 4. 底部快捷操作条：支持快捷终止当前队列或发起可用战斗动作；
 * 5. 点击角色实体即刻呼出详细攻防/博弈/抗性属性模态窗 (CharacterAttributeModal)。
 */
// 隐藏页签时的冻结订阅常量：组件保持挂载以保留详情/动画/观察目标生命周期（见 CombatDetailsPage），
// 但隐藏期间高频战斗分段改由稳定引用代答，战斗增量推送不再触发整棵隐藏子树重渲；
// 页签恢复可见时选择器切回实时状态，无需任何补拉。
const frozenInitialState = useGameStore.getState()
const FROZEN_COMBAT_RESOURCES = frozenInitialState.combatResources
const FROZEN_COMBAT_ATTRIBUTES = frozenInitialState.combatAttributes
const FROZEN_BUFFS = frozenInitialState.buffs
const FROZEN_ACTIONS = frozenInitialState.actions
const FROZEN_QUEUE_ENTRIES = frozenInitialState.queue.entries
const FROZEN_WORLD = frozenInitialState.world

export const CombatArena = memo(function CombatArena({
  onOpenAction,
  hidden = false,
}: {
  onOpenAction: (action: ActionDefinition) => void
  /** 所在页签是否处于隐藏态：隐藏时冻结高频战斗订阅，保持挂载但不随战斗增量重渲 */
  hidden?: boolean
}): React.JSX.Element {
  const playerName = useGameStore(state => state.name)
  const playerVisual = useGameStore(state => state.visual)
  const combatResources = useGameStore(hidden ? () => FROZEN_COMBAT_RESOURCES : state => state.combatResources)
  const combatAttributes = useGameStore(hidden ? () => FROZEN_COMBAT_ATTRIBUTES : state => state.combatAttributes)
  const abilities = useGameStore(state => state.abilities)
  const avatarId = useAvatarStore(state => state.avatarId)
  const selfActorId = resolvePlayerActorId(avatarId)
  const buffs = useGameStore(hidden ? () => FROZEN_BUFFS : state => state.buffs)
  const actions = useGameStore(hidden ? () => FROZEN_ACTIONS : state => state.actions)
  const world = useGameStore(hidden ? () => FROZEN_WORLD : state => state.world)
  const maps = useGameStore(state => state.maps)
  const encounters = useGameStore(state => state.encounters)
  const queue = useGameStore(hidden ? () => FROZEN_QUEUE_ENTRIES : state => state.queue.entries)
  const queuePaused = useGameStore(state => state.queue.paused)
  // 队列进度的采样基准：必须与非战斗进度条（useSmoothProgressRef）完全一致，
  // 否则角色行动条与右侧行动卡片会因「非队列补丁」（入灵气、掉落等）产生 0~1 秒的显示差。
  const lastQueueSyncPerformanceTime = useGameStore(hidden ? () => 0 : state => state.lastQueueSyncPerformanceTime ?? state.lastSyncPerformanceTime)
  // 详情弹窗静态快照：点击单位时获取一次，不随战斗持续推进或单位死亡改变。
  const [inspectedSnapshot, setInspectedSnapshot] = useState<InspectedCharacterSnapshot | null>(null)
  // 玩家本地装配：详情弹窗「装配」Tab 的玩家数据源（功法槽 + 已穿戴装备，实时值）。
  const combatLoadout = useGameStore(state => state.combat)
  // 终止当前行动的二次确认目标（队首 entryId）；目标离开队首（自然完成/被移除）时自动失效收起弹窗
  const [pendingStopEntryId, setPendingStopEntryId] = useState<string | null>(null)
  // 组队讨伐终止确认：参与行动不可 action.remove，「终止」语义=取消参战（party.unready 散场）
  const [pendingQuitSession, setPendingQuitSession] = useState(false)
  // 战斗统计弹窗：入口仅战斗相位可见（悬浮于战场左上角），面板数据源为当前队首行动账本
  const [statsOpen, setStatsOpen] = useState(false)
  const closeStats = useCallback(() => setStatsOpen(false), [])
  const unreadyParty = useSocialStore(state => state.unreadyParty)
  const setSimulatorOpen = useUiStore(state => state.setSimulatorOpen)
  // 单位属性缓存：敌方/镜像属性为定义级静态值、盟友属性为开打冻结面板，服务端仅在 UnitInspect 登记后一次性交付；
  // 按身份缓存使重复点开即显，送达的新值由下方 effect 静默覆盖，key 为 enemy:{enemyId} / duel:{对手名} / ally:{playerId}。
  const inspectedAttributesCacheRef = useRef(new Map<string, CombatAttributes>())
  // 装配缓存：盟友/斗法镜像的功法与装备与属性面板同一按需交付窗口（一次性投影），
  // 按身份键缓存复用，key 口径与属性缓存一致（ally:{playerId} / duel:{对手名}）。
  const inspectedLoadoutCacheRef = useRef(new Map<string, UnitLoadoutView>())
  // 尸体留存：服务端切搜寻相位即清空 enemies[]，这里在战斗相位逐帧缓存当波展示态，
  // 胜利转入搜寻后整波以尸体形态继续渲染，倒数最后一秒统一播消散动画后随新波入场移除。
  const combatWaveRef = useRef<{ entryId: string; entries: EnemyDisplayState[] } | null>(null)
  const prevPhaseRef = useRef<string | null>(null)
  const [corpseWave, setCorpseWave] = useState<EnemyDisplayState[] | null>(null)
  const [corpseDissipating, setCorpseDissipating] = useState(false)
  const t = useT()


  // 战斗特效：overlay 覆盖层 + 模块化 VFX 运行时（演出调度与技能模块注册全部收拢在 src/vfx/）。
  const arenaShellRef = useRef<HTMLDivElement | null>(null)
  const vfxOverlayRef = useRef<HTMLDivElement | null>(null)
  const vfxRuntimeRef = useCombatVfx(arenaShellRef, vfxOverlayRef)
  const alliesFormationRef = useRef<HTMLDivElement | null>(null)

  const current = queue[0] ?? null
  const isCombat = current?.type === 'combat' && current.executionKind === 'combat-encounter'
  const isTravel = current !== null && (current.executionKind === 'travel' || current.actionId === 'world.travel')
  const isLifeAction = current !== null && !isCombat && !isTravel
  const expedition = isCombat ? current.expedition : null
  // 统计入口随战斗行动存亡：行动出队/切换为非战斗行动时自动收起已开弹窗
  useEffect(() => {
    if (!isCombat) setStatsOpen(false)
  }, [isCombat])
  // 一次性交付落账：wire 携带属性时按身份写入缓存，后续快照不再携带也能稳定显示。
  // 一次性交付落账：wire 携带属性时按身份写入缓存，并向正在浏览的对应快照安全补全权威数值。
  useEffect(() => {
    const cache = inspectedAttributesCacheRef.current
    const loadoutCache = inspectedLoadoutCacheRef.current
    for (const enemy of expedition?.enemies ?? []) {
      if (enemy.attributes) {
        cache.set(`enemy:${enemy.enemyId}`, enemy.attributes)
        setInspectedSnapshot(prev => {
          if (prev && prev.role === 'enemy' && prev.enemyId === enemy.enemyId) {
            const staticAttr = findCombatantInCatalog(encounters, maps, enemy.enemyId)?.attributes
            if (!prev.attributes || prev.attributes === staticAttr) {
              return { ...prev, attributes: enemy.attributes ?? null }
            }
          }
          return prev
        })
      }
    }
    for (const ally of expedition?.allies ?? []) {
      if (ally.attributes) cache.set(`ally:${ally.playerId}`, ally.attributes)
      if (ally.skills || ally.equipment)
        loadoutCache.set(`ally:${ally.playerId}`, { skills: ally.skills, equipment: ally.equipment })
      if (ally.attributes) {
        setInspectedSnapshot(prev => {
          if (prev && prev.role === 'player' && prev.targetKey.startsWith(`ally:${ally.memberIndex}:`)) {
            return {
              ...prev,
              attributes: prev.attributes ?? ally.attributes ?? null,
              loadout: (ally.skills || ally.equipment)
                ? { skills: ally.skills ?? prev.loadout?.skills ?? [], equipment: ally.equipment ?? prev.loadout?.equipment ?? null }
                : prev.loadout,
            }
          }
          return prev
        })
      }
    }
    if (expedition?.duel?.opponentAttributes && expedition.duel.opponentName) {
      cache.set(`duel:${expedition.duel.opponentName}`, expedition.duel.opponentAttributes)
      setInspectedSnapshot(prev => {
        if (prev && prev.role === 'enemy' && prev.enemyId === null && !prev.attributes) {
          return { ...prev, attributes: expedition.duel?.opponentAttributes ?? null }
        }
        return prev
      })
    }
    if (expedition?.duel?.opponentName
      && (expedition.duel.opponentSkills || expedition.duel.opponentEquipment))
      loadoutCache.set(`duel:${expedition.duel.opponentName}`, {
        skills: expedition.duel.opponentSkills,
        equipment: expedition.duel.opponentEquipment,
      })
  }, [expedition, encounters, maps])
  const activeAction = current ? actions.find(action => action.id === current.actionId) : null
  const activeEncounterId = expedition?.encounterId ?? activeAction?.combatEncounterId

  const availableActions = useMemo(() => actions.filter(action => (
    action.type === 'combat'
      && (!action.availableNodeIds?.length || (world.nodeId !== null && action.availableNodeIds.includes(world.nodeId)))
  )), [actions, world.nodeId])

  const playerMaxHp = Math.max(1, combatResources?.maxHitpoints ?? combatAttributes?.maxHitpoints ?? 1)
  const playerMaxMp = Math.max(1, combatResources?.maxManapoints ?? combatAttributes?.maxManapoints ?? 1)
  const playerHp = (isCombat ? expedition?.playerHitpoints : undefined) ?? combatResources?.hitpoints ?? playerMaxHp
  const playerMp = (isCombat ? expedition?.playerManapoints : undefined) ?? combatResources?.manapoints ?? playerMaxMp
  const playerThreat = expedition?.playerThreat ?? null
  // 玩家展示 Buff：战斗相位以战内列表（含开战注入的消耗品副本）为准，战外持久 Buff 按 buffId
  // 去重后补充；非战斗相位 PlayerState 为空，退回战外 ActiveBuff 视图。
  const playerDisplayBuffs = useMemo<Buff[]>(() => {
    const combatBuffs = expedition?.playerBuffs
    if (!combatBuffs || combatBuffs.length === 0) return buffs
    const battleElapsed = expedition?.battleElapsedSeconds ?? 0
    const serverNow = estimateServerNowUnixMilliseconds()
    const combatIds = new Set(combatBuffs.map(entry => entry.buffId))
    return [
      ...combatBuffs.map(entry => combatBuffToBuff(entry, battleElapsed, serverNow)),
      ...buffs.filter(buff => !combatIds.has(buff.buffId)),
    ]
  }, [buffs, expedition?.playerBuffs, expedition?.battleElapsedSeconds])
  const phase = expedition?.phase ?? (isTravel ? 'travel' : isLifeAction ? 'life-action' : 'idle')
  const isDuelPhase = phase === 'duel'

  // 同波多敌阵容：名称/立绘/属性/出手时间线全部以 wire 下发的 enemies[] 快照为准。
  // 斗法相位例外：对手为玩家镜像，不走 enemies[]，以 expedition.duel 载荷合成单个对手卡片，
  // 敌人名位置显示「斗法·对手道号」，血条布局不变；duel-pending（撮合搜寻中）不合成占位卡，
  // 敌区自然落到空阵型。
  const enemyEntries = useMemo<EnemyDisplayState[]>(() => {
    const battleElapsed = expedition?.battleElapsedSeconds ?? 0
    const serverNow = estimateServerNowUnixMilliseconds()
    if (expedition?.phase === 'duel') {
      const name = t('combat.duel.opponent', { name: expedition.duel?.opponentName || t('combat.duel.unknownOpponent') })
      // 斗法对手气血/灵力与属性上限由 duel 回放载荷承载（撮合时锁定镜像、随剧本事件更新）。
      const opponentAttributes = expedition.duel?.opponentAttributes ?? null
      // 镜像血蓝条分母走常驻标量；完整属性为按需投影，观察目标命中镜像位才携带。
      const maxHp = Math.max(1, expedition.duel?.opponentMaxHitpoints ?? opponentAttributes?.maxHitpoints ?? 1)
      const maxMp = Math.max(1, expedition.duel?.opponentMaxManapoints ?? opponentAttributes?.maxManapoints ?? 1)
      const hp = Math.max(0, Math.min(maxHp, expedition.duel?.opponentHitpoints ?? maxHp))
      const mp = Math.max(0, Math.min(maxMp, expedition.duel?.opponentManapoints ?? maxMp))
      return [{
        actorKey: `enemy:duel:${expedition.duel?.opponentName ?? 'opponent'}`,
        enemyId: resolvePlayerActorId(expedition.duel?.opponentAvatar),
        name,
        seal: name.slice(0, 1),
        hp,
        maxHp,
        mp,
        maxMp,
        threat: null,
        action: duelActionState(expedition.duel, battleElapsed, !expedition.duel?.isInitiator, 2000, serverNow, abilities),
        visual: null,
        attributes: opponentAttributes,
      }]
    }
    const wave = expedition?.enemies ?? []
    // 敌人行动条只在己方全灭倒地窗口隐藏：引擎此时全场冻结、敌方无目标不出手。
    // 仅自己倒地而仍有队友存活时战斗照常推进，敌人行动必须继续可见（组队讨伐口径）。
    const allAlliesDowned = (expedition?.playerHitpoints ?? 1) <= 0
      && !(expedition?.allies?.some(ally => ally.hitpoints > 0) ?? false)
    return wave.map((entry, index) => {
      const name = entry.name || entry.enemyId
      return {
        actorKey: `enemy:${expedition?.waveNumber ?? 0}:${index}:${entry.enemyId}`,
        enemyId: entry.enemyId,
        name,
        seal: name.slice(0, 1),
        hp: entry.hitpoints,
        maxHp: Math.max(1, entry.maxHitpoints ?? entry.attributes?.maxHitpoints ?? entry.hitpoints),
        mp: entry.manapoints,
        maxMp: Math.max(1, entry.maxManapoints ?? entry.attributes?.maxManapoints ?? entry.manapoints),
        threat: entry.threat,
        action: entry.hitpoints > 0 && expedition?.phase === 'combat' && !allAlliesDowned
          ? enemyActionState(entry, battleElapsed, serverNow, abilities)
          : null,
        visual: entry.visual ?? null,
        attributes: entry.attributes ?? null,
        buffs: entry.buffs?.map(buff => combatBuffToBuff(buff, battleElapsed, serverNow)),
      }
    })
  }, [abilities, expedition?.enemies, expedition?.waveNumber, expedition?.phase, expedition?.duel, expedition?.battleElapsedSeconds, t])

  // 组队讨伐盟友阵列（§21.13）：allies 按 memberIndex 序铺九宫格，memberIndex === selfIndex
  // 的格子复用完整玩家展示态；其余盟友可点详情，属性面板走 UnitInspect side:ally 按需投影。
  const allyEntries = useMemo<AllyDisplayState[]>(() => {
    const allies = expedition?.allies
    if (!allies || allies.length === 0) return EMPTY_ALLY_ENTRIES
    const battleElapsed = expedition?.battleElapsedSeconds ?? 0
    const serverNow = estimateServerNowUnixMilliseconds()
    const selfIndex = expedition?.selfIndex ?? -1
    // 盟友复活读条总时长：优先服务端本次下发的权威秒数，其次遭遇配表复活毫秒数。
    const respawnMs = Math.max(1,
      Math.round((expedition?.playerRespawnDurationSeconds ?? 0) * 1000)
      || (expedition?.encounterId ? findEncounterInCatalog(encounters, expedition.encounterId)?.playerRespawnMilliseconds : undefined)
      || 180000)
    const respawnName = t('combat.arena.phaseDeath')
    const respawnWaitName = t('combat.arena.respawnWaiting')
    return [...allies]
      .sort((a, b) => a.memberIndex - b.memberIndex)
      .slice(0, ALLY_DEPTH_SLOTS.length)
      .map(ally => ({
        memberIndex: ally.memberIndex,
        playerId: ally.playerId,
        name: ally.displayName,
        avatar: ally.avatar ?? 'player',
        actorKey: `ally:${ally.memberIndex}:${ally.displayName}`,
        seal: ally.displayName.slice(0, 1) || '?',
        hp: ally.hitpoints,
        maxHp: Math.max(1, ally.maxHitpoints ?? ally.hitpoints),
        mp: ally.manapoints,
        maxMp: Math.max(1, ally.maxManapoints ?? ally.manapoints),
        isSelf: ally.memberIndex === selfIndex,
        threat: ally.threat,
        action: allyActionState(ally, battleElapsed, serverNow, abilities, respawnMs, respawnName, respawnWaitName, expedition?.phase),
        attributes: ally.attributes ?? null,
        skills: ally.skills ?? null,
        equipment: ally.equipment ?? null,
        buffs: ally.buffs?.map(buff => combatBuffToBuff(buff, battleElapsed, serverNow)),
      }))
  }, [expedition?.allies, expedition?.selfIndex, expedition?.battleElapsedSeconds, expedition?.phase, expedition?.encounterId, expedition?.playerRespawnDurationSeconds, encounters, abilities, t])

  // 尸体缓存生命周期：combat 相位逐帧刷新缓存；combat→search（服务端语义=波次清空，
  // 团灭走 respawn、斗法走 duel-pending/duel 均不经过此迁移）时固化为尸体阵容
  // （气血归零、行动条置空）；相位离开 search 或切换行动实例即丢弃。
  // 尸体与团灭留存生命周期：combat 相位逐帧刷新缓存；
  // combat→search（胜利，全灭）：固化为尸体阵容（气血归零、行动条置空）；
  // combat→respawn（团灭/倒地）：保留击败己方的怪物阵容（行动条置空、保留存活外观），让玩家在复活倒数期间可查阅敌怪；
  // 新一轮战斗（离开 search/respawn）或切换行动实例即丢弃。
  useEffect(() => {
    const entryId = current?.entryId ?? null
    const prevPhase = prevPhaseRef.current
    prevPhaseRef.current = phase
    if (phase === 'combat') {
      if (enemyEntries.length > 0 && entryId !== null)
        combatWaveRef.current = { entryId, entries: enemyEntries }
      if (prevPhase === 'search' || prevPhase === 'respawn') {
        setCorpseWave(null)
        setCorpseDissipating(false)
      }
      return
    }
    if (phase === 'search') {
      if (prevPhase === 'combat') {
        const wave = combatWaveRef.current
        setCorpseWave(wave !== null && wave.entryId === entryId && wave.entries.length > 0
          ? wave.entries.map(entry => ({ ...entry, hp: 0, action: null }))
          : null)
        setCorpseDissipating(false)
      }
      return
    }
    if (phase === 'respawn') {
      if (prevPhase === 'combat') {
        const wave = combatWaveRef.current
        // 团灭/战败进入 respawn：保留击败己方的怪物阵容，行动条置空
        setCorpseWave(wave !== null && wave.entryId === entryId && wave.entries.length > 0
          ? wave.entries.map(entry => ({ ...entry, action: null }))
          : null)
        setCorpseDissipating(false)
      }
      return
    }
    combatWaveRef.current = null
    setCorpseWave(null)
    setCorpseDissipating(false)
  }, [phase, enemyEntries, current?.entryId])

  // 消散调度：尸体保留到寻敌倒数最后一秒；到点置消散标志并触发残魂萤火粒子，
  // 淡出动画时长收敛到剩余寻敌毫秒（触发晚于计划时自动压缩），末帧即新一波入场。
  useEffect(() => {
    if (phase !== 'search' || corpseWave === null || corpseDissipating || expedition == null) return
    const remainingMs = Math.max(0,
      (expedition.durationSeconds - expedition.elapsedSeconds) * 1000
      - Math.max(0, performance.now() - lastQueueSyncPerformanceTime))
    const timer = window.setTimeout(() => {
      // 到点复核：队列暂停期间寻敌时钟冻结（暂停不重排本定时器），跳过触发等恢复后的补丁重排
      const live = useGameStore.getState()
      if (live.queue.paused || live.queue.entries[0]?.expedition?.phase !== 'search') return
      setCorpseDissipating(true)
      vfxRuntimeRef.current?.playCorpseDissipate()
    }, Math.max(0, remainingMs - CORPSE_DISSIPATE_LEAD_MS))
    return () => window.clearTimeout(timer)
  }, [phase, corpseWave, corpseDissipating, expedition, lastQueueSyncPerformanceTime, vfxRuntimeRef])

  // combat→search 过渡帧：服务端切搜寻相位时已清空 enemies[]，而 corpseWave 状态要等 effect
  // 执行后才写入。若这一帧落到空阵型，敌方卡片会整棵卸载再重挂——击杀帧仍在途的跳字/受击
  // 定时器随卸载被清掉（致死一击的伤害数字正是在这一帧丢的），故用缓存波次同步合成尸体阵容，
  // 让同一实例跨越相位继续消费终局事件与血量差额。
  const transitionCorpseEntries = useMemo<readonly EnemyDisplayState[]>(() => {
    if ((phase !== 'search' && phase !== 'respawn') || corpseWave !== null) return EMPTY_DISPLAY_ENTRIES
    const wave = combatWaveRef.current
    if (wave === null || wave.entryId !== (current?.entryId ?? null) || wave.entries.length === 0) {
      return EMPTY_DISPLAY_ENTRIES
    }
    return wave.entries.map(entry => ({
      ...entry,
      hp: phase === 'search' ? 0 : entry.hp,
      action: null,
    }))
  }, [phase, corpseWave, current?.entryId])
  const corpseEntries = (phase === 'search' || phase === 'respawn') ? corpseWave ?? transitionCorpseEntries : EMPTY_DISPLAY_ENTRIES
  const displayEntries = enemyEntries.length > 0 ? enemyEntries : corpseEntries
  const corpseSlotsActive = phase === 'search' && enemyEntries.length === 0 && corpseEntries.length > 0
  const enemyVisible = isCombat && (phase === 'combat' || isDuelPhase || enemyEntries.some(entry => entry.hp > 0) || corpseEntries.length > 0)
  // 空槽只补纵深占位之外的格子：敌怪按 ENEMY_DEPTH_SLOTS 摆位（2 个怪占 1/4 号槽），
  // 若按顺序编号补空会叠进实体槽位，透明空槽在 DOM 后层拦截点击，导致部分怪点不中。
  const occupiedEnemySlots = new Set(ENEMY_DEPTH_SLOTS.slice(0, displayEntries.length))
  // 消散动画时长：实时收敛到寻敌剩余毫秒；未触发时取提前量兜底。
  const corpseFadeMs = corpseDissipating && expedition != null
    ? Math.max(80, (expedition.durationSeconds - expedition.elapsedSeconds) * 1000
      - Math.max(0, performance.now() - lastQueueSyncPerformanceTime))
    : CORPSE_DISSIPATE_LEAD_MS
  const allyCount = Math.max(1, allyEntries.length)
  const enemyCount = Math.max(1, displayEntries.length)
  const { allyScale, enemyScale, allyRows, enemyRows } = useArenaFormationScale(
    alliesFormationRef,
    allyCount,
    enemyCount
  )

  const playerAction: CombatActionState | null = useMemo(() => {
    if (isCombat) {
      if (queuePaused) return null
      const battleElapsed = expedition?.battleElapsedSeconds ?? 0
      const serverNow = estimateServerNowUnixMilliseconds()
      const attackInterval = combatAttributes?.attackIntervalMilliseconds ?? 0
      // 吟唱中的功法优先于普攻条展示；总时长取服务端 castTotalMilliseconds（含施法乘区），剩余由 combat 分段下发。
      const castingId = combatResources?.castingAbilityId
      if (castingId && (expedition?.phase === 'combat' || expedition?.phase === 'duel')) {
        const ability = findAbilityInCatalog(abilities, castingId)
        const duration = Math.max(1, combatResources?.castTotalMilliseconds ?? ability?.castMilliseconds ?? combatResources?.castRemainingMilliseconds ?? 1)
        const remaining = Math.max(0, combatResources?.castRemainingMilliseconds ?? 0)
        return {
          kind: 'cast',
          name: ability?.name ?? castingId,
          durationMilliseconds: duration,
          elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
          completesAtUnixMilliseconds: serverNow + remaining,
        }
      }
      if (expedition?.phase === 'combat') {
        // 倒地窗口：玩家阵亡原地复活，行动条改为复活读条（战斗时钟上的绝对截止点）。
        const respawnAt = expedition.playerRespawnAtSeconds
        if (respawnAt != null) {
          // 读条已走完但复活策略条件未成立：改为不推进的等待态文案。
          if (expedition.playerRespawnWaitingUntilSeconds != null) {
            return {
              kind: 'respawn-wait',
              name: t('combat.arena.respawnWaiting'),
              durationMilliseconds: 1,
              elapsedMilliseconds: 0,
              completesAtUnixMilliseconds: null,
            }
          }
          const duration = Math.max(1, (expedition.playerRespawnDurationSeconds ?? 0) * 1000)
          const remaining = Math.max(0, (respawnAt - battleElapsed) * 1000)
          return {
            kind: 'respawn',
            name: t('combat.arena.phaseDeath'),
            durationMilliseconds: duration,
            elapsedMilliseconds: Math.max(0, Math.min(duration, duration - remaining)),
            completesAtUnixMilliseconds: serverNow + remaining,
          }
        }
        return attackActionState(expedition.playerNextAttackAtSeconds ?? 0, battleElapsed, attackInterval, serverNow)
      }
      if (expedition?.phase === 'duel') {
        return duelActionState(expedition.duel, battleElapsed, expedition.duel?.isInitiator ?? true, attackInterval, serverNow, abilities)
      }
      return null
    }
    if ((isLifeAction || isTravel) && current) {
      if (current.status !== 'running' || queuePaused) return null
      const durSec = (isTravel && current.expedition?.durationSeconds) ? current.expedition.durationSeconds : current.durationSeconds
      const durMs = Math.max(1, durSec * 1000)
      const baseElSec = (isTravel && current.expedition?.elapsedSeconds !== undefined) ? current.expedition.elapsedSeconds : current.progressSeconds
      const baseElMs = Math.max(0, baseElSec * 1000)
      const elapsedSinceSync = Math.max(0, performance.now() - lastQueueSyncPerformanceTime)
      const currentElMs = Math.min(durMs, baseElMs + elapsedSinceSync)
      return {
        kind: 'cast',
        name: resolveQueueEntryDisplayName(current, current.actionName, world, maps),
        durationMilliseconds: durMs,
        elapsedMilliseconds: currentElMs,
        completesAtUnixMilliseconds: null,
      }
    }
    return null
  }, [current, expedition, isCombat, isLifeAction, isTravel, queuePaused, lastQueueSyncPerformanceTime, combatAttributes, combatResources, abilities, world, maps, t])

  // 详情弹窗静态快照构建（点击时获取一次，不随战斗后续推进或单位死亡改变）
  const handleInspectPlayer = useCallback(() => {
    gameClient.sendUnitInspect(null)
    setInspectedSnapshot({
      targetKey: 'player',
      actorId: selfActorId,
      name: playerName,
      role: 'player',
      title: playerName,
      visual: playerVisual ?? null,
      attributes: combatAttributes ?? null,
      buffs: [...playerDisplayBuffs],
      loadout: {
        skills: combatLoadout?.abilitySlots ?? [],
        equipment: combatLoadout?.equipment ?? [],
      },
      drops: undefined,
      enemyId: null,
    })
  }, [selfActorId, playerName, playerVisual, combatAttributes, playerDisplayBuffs, combatLoadout])

  const handleInspectAlly = useCallback((ally: AllyDisplayState) => {
    gameClient.sendUnitInspect({ side: 'ally', index: ally.memberIndex })
    const cachedAttr = inspectedAttributesCacheRef.current.get(`ally:${ally.playerId}`)
    const cachedLoadout = inspectedLoadoutCacheRef.current.get(`ally:${ally.playerId}`)
    setInspectedSnapshot({
      targetKey: ally.actorKey,
      actorId: ally.isSelf ? selfActorId : resolvePlayerActorId(ally.avatar),
      name: ally.name,
      role: 'player',
      title: ally.name,
      visual: null,
      attributes: ally.attributes ?? cachedAttr ?? null,
      buffs: [...(ally.buffs ?? [])],
      loadout: {
        skills: ally.skills ?? cachedLoadout?.skills ?? [],
        equipment: ally.equipment ?? cachedLoadout?.equipment ?? null,
      },
      drops: undefined,
      enemyId: null,
    })
  }, [selfActorId])

  const handleInspectEnemy = useCallback((entry: EnemyDisplayState, index: number) => {
    gameClient.sendUnitInspect({ side: 'enemy', index })
    const staticCombatant = entry.enemyId ? findCombatantInCatalog(encounters, maps, entry.enemyId) : undefined
    const cachedAttr = inspectedAttributesCacheRef.current.get(
      entry.enemyId !== null
        ? `enemy:${entry.enemyId}`
        : `duel:${expedition?.duel?.opponentName ?? 'opponent'}`
    )
    const initialAttributes = entry.attributes ?? cachedAttr ?? staticCombatant?.attributes ?? null
    const drops = staticCombatant?.drops ?? []
    let loadout: UnitLoadoutView | null = null
    if (entry.enemyId !== null) {
      loadout = { skills: staticCombatant?.skills ?? [] }
    } else {
      const cachedLoadout = inspectedLoadoutCacheRef.current.get(`duel:${expedition?.duel?.opponentName ?? 'opponent'}`)
      loadout = {
        skills: expedition?.duel?.opponentSkills ?? cachedLoadout?.skills ?? [],
        equipment: expedition?.duel?.opponentEquipment ?? cachedLoadout?.equipment ?? null,
      }
    }

    setInspectedSnapshot({
      targetKey: entry.actorKey,
      actorId: entry.enemyId ?? activeEncounterId ?? null,
      name: entry.name,
      role: 'enemy',
      title: entry.name,
      visual: entry.visual ?? null,
      attributes: initialAttributes,
      buffs: [...(entry.buffs ?? [])],
      loadout,
      drops,
      enemyId: entry.enemyId ?? null,
    })
  }, [encounters, maps, expedition?.duel, activeEncounterId])

  const handleCloseInspect = useCallback(() => {
    setInspectedSnapshot(null)
    gameClient.sendUnitInspect(null)
  }, [])

  // 待终止目标仅在仍为队首时有效；自然完成或被队列他处移除后，确认弹窗自动收起
  const stopTarget = current && current.entryId === pendingStopEntryId ? current : null
  const recentCombatEvents = useMemo<readonly CombatRecentEvent[]>(() => {
    if (expedition?.phase === 'duel' && expedition.duel) {
      const duel = expedition.duel
      const battleElapsed = expedition.battleElapsedSeconds ?? 0
      const isInitiator = duel.isInitiator
      return (duel.events ?? [])
        .filter(e => e.atSeconds <= battleElapsed && e.atSeconds > battleElapsed - 2)
        .map(e => ({
          at: e.atSeconds,
          attackerId: e.attackerIsInitiator === isInitiator ? 'player' : 'enemy:0',
          targetId: (e.targetIsInitiator === isInitiator) ? 'player' : 'enemy:0',
          damage: e.damage ?? 0,
          hit: e.hit !== false,
          critical: e.critical,
          broken: e.broken,
          parried: e.parried,
          abilityId: e.abilityId,
        }))
    }
    return expedition?.recentEvents ?? []
  }, [expedition?.phase, expedition?.recentEvents, expedition?.duel, expedition?.battleElapsedSeconds])


  return (
    <div
      className="combat-arena-shell"
      ref={arenaShellRef}
    >
      {/* 技能特效覆盖层：绝对定位铺满战场，粒子节点全部渲染于此，零回流不参战布局 */}
      <div className="vfx-stage-overlay" ref={vfxOverlayRef} aria-hidden="true" />
      {/* 战场顶部操作区：统计与模拟器入口 + 终止行动按钮（固定顶栏独立行，绝不遮挡角色名称与行动栏） */}
      <div className="combat-arena-top-actions">
        <div className="combat-arena-top-actions-left">
          {isCombat && (
            <button
              className="ghost-button combat-stats-open"
              onClick={() => setStatsOpen(true)}
              type="button"
            >
              {t('combat.stats.title')}
            </button>
          )}
          <button
            className="ghost-button combat-simulator-open"
            onClick={() => setSimulatorOpen(true)}
            type="button"
          >
            {t('combat.simulator.topbarBtn')}
          </button>
        </div>

        {current && isCombat && (
          <div className="combat-arena-top-actions-right">
            <button
              className="danger combat-stop-btn"
              onClick={() => {
                if (hasActionParticipation(current)) {
                  setPendingQuitSession(true)
                  return
                }
                setPendingStopEntryId(current.entryId)
              }}
              type="button"
            >
              {t('combat.arena.stopCurrent', { name: resolveQueueEntryDisplayName(current, current.actionName, world, maps) })}
            </button>
          </div>
        )}
      </div>
      <div className={`combat-arena-field combat-arena-field--${phase}`}>
        {/* 左侧：己方 3x3 阵型 (左为后排，右为前排，1号位在前排正中靠近敌方)；
            组队讨伐时按 allies 成员序铺满阵列，selfIndex 对应格复用玩家完整展示态 */}
        <div className="arena-side arena-side--allies">
          <div
            className={`arena-formation arena-formation--allies arena-formation--rows-${allyRows}`}
            ref={alliesFormationRef}
            style={{ '--arena-scale': allyScale } as CSSProperties}
          >
            {allyEntries.length > 0 ? (
              <>
                {allyEntries.map((ally, index) => {
                  const slot = ALLY_DEPTH_SLOTS[Math.min(index, ALLY_DEPTH_SLOTS.length - 1)]
                  if (ally.isSelf) {
                    return (
                      <div className={`arena-slot arena-slot--occupied arena-slot--slot-${slot}${ally.hp <= 0 ? ' arena-slot--slain' : ''}`} key={ally.actorKey}>
                        <ArenaActor
                          action={playerAction}
                          actorId={selfActorId}
                          actorKey="player"
                          buffs={playerDisplayBuffs}
                          compact
                          hp={playerHp}
                          maxHp={playerMaxHp}
                          maxMp={playerMaxMp}
                          mp={playerMp}
                          name={playerName}
                          onInspect={handleInspectPlayer}
                          role="player"
                          seal={playerName.slice(0, 1) || t('combat.arena.sealFallback')}
                          threat={playerThreat}
                          visual={playerVisual}
                          recentEvents={recentCombatEvents}
                        />
                      </div>
                    )
                  }
                  return (
                    <div
                      aria-label={t('social.session.allyAria', { name: ally.name })}
                      className={`arena-slot arena-slot--occupied arena-slot--slot-${slot}${ally.hp <= 0 ? ' arena-slot--slain' : ''}`}
                      key={ally.actorKey}
                    >
                      <ArenaActor
                        action={ally.action}
                        actorId={resolvePlayerActorId(ally.avatar)}
                        actorKey={ally.actorKey}
                        compact
                        hp={ally.hp}
                        maxHp={ally.maxHp}
                        maxMp={ally.maxMp}
                        mp={ally.mp}
                        name={ally.name}
                        buffs={ally.buffs}
                        onInspect={() => handleInspectAlly(ally)}
                        role="player"
                        seal={ally.seal}
                        threat={ally.threat}
                        recentEvents={recentCombatEvents}
                      />
                    </div>
                  )
                })}
                {/* 预留空槽维持九宫格与前中后排比例 */}
                {ALLY_DEPTH_SLOTS.slice(allyEntries.length).map(slot => (
                  <div className={`arena-slot arena-slot--empty arena-slot--slot-${slot}`} key={`ally-slot-${slot}`} />
                ))}
              </>
            ) : (
              <>
                <div className="arena-slot arena-slot--occupied arena-slot--slot-1" key="ally-slot-1">
                  <ArenaActor
                    action={playerAction}
                    actorId={selfActorId}
                    actorKey="player"
                    buffs={playerDisplayBuffs}
                    hp={playerHp}
                    maxHp={playerMaxHp}
                    maxMp={playerMaxMp}
                    mp={playerMp}
                    name={playerName}
                    onInspect={handleInspectPlayer}
                    role="player"
                    seal={playerName.slice(0, 1) || t('combat.arena.sealFallback')}
                    threat={playerThreat}
                    visual={playerVisual}
                    recentEvents={recentCombatEvents}
                  />
                </div>
                {/* 预留空槽维持九宫格与前中后排比例 */}
                <div className="arena-slot arena-slot--empty arena-slot--slot-2" key="ally-slot-2" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-3" key="ally-slot-3" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-4" key="ally-slot-4" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-5" key="ally-slot-5" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-6" key="ally-slot-6" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-7" key="ally-slot-7" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-8" key="ally-slot-8" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-9" key="ally-slot-9" />
              </>
            )}
          </div>
        </div>

        {/* 战场居中浮层：仅在搜寻敌人阶段展示 */}
        {isCombat && (phase === 'initial-search' || phase === 'search' || phase === 'respawn') && expedition && (
          <div className="arena-center-stage">
            <ArenaPhaseState expedition={expedition} />
          </div>
        )}

        {/* 右侧：敌方 3x3 阵型 (左为前排，右为后排，1号位在前排正中靠近己方) OR 生活行动总目标 OR 空闲状态 */}
        <div className="arena-side arena-side--enemies">
          {isCombat ? (
            enemyVisible ? (
              <div
                className={`arena-formation arena-formation--enemies arena-formation--rows-${enemyRows}`}
                style={{ '--arena-scale': enemyScale, '--corpse-fade-ms': `${corpseFadeMs}ms` } as CSSProperties}
              >
                {displayEntries.map((entry, index) => (
                  <div
                    className={`arena-slot arena-slot--occupied arena-slot--slot-${ENEMY_DEPTH_SLOTS[Math.min(index, ENEMY_DEPTH_SLOTS.length - 1)]}${entry.hp <= 0 ? ' arena-slot--slain' : ''}${corpseSlotsActive ? ' arena-slot--corpse' : ''}${corpseSlotsActive && corpseDissipating ? ' arena-slot--dissipating' : ''}`}
                    key={`enemy-slot-${index + 1}`}
                  >
                    <ArenaActor
                      action={entry.action}
                      actorId={entry.enemyId ?? activeEncounterId}
                      actorKey={entry.actorKey}
                      buffs={entry.buffs}
                      compact
                      hp={entry.hp}
                      maxHp={entry.maxHp}
                      maxMp={entry.maxMp}
                      mp={entry.mp}
                      name={entry.name}
                      onInspect={() => handleInspectEnemy(entry, index)}
                      role="enemy"
                      seal={entry.seal}
                      threat={entry.threat}
                      visual={entry.visual}
                      recentEvents={recentCombatEvents}
                    />
                  </div>
                ))}
                {/* 预留空槽维持九宫格与前中后排比例 */}
                {[1, 2, 3, 4, 5, 6, 7, 8, 9]
                  .filter(slot => !occupiedEnemySlots.has(slot))
                  .map(slot => (
                    <div
                      className={`arena-slot arena-slot--empty arena-slot--slot-${slot}`}
                      key={`enemy-empty-${slot}`}
                    />
                  ))}
              </div>
            ) : (
              <div
                className={`arena-formation arena-formation--enemies arena-formation--rows-${enemyRows}`}
                style={{ '--arena-scale': enemyScale } as CSSProperties}
              >
                {Array.from({ length: 9 }).map((_, i) => (
                  <div className={`arena-slot arena-slot--empty arena-slot--slot-${i + 1}`} key={`empty-slot-${i + 1}`} />
                ))}
              </div>
            )
          ) : isTravel ? (
            <ArenaTravelCard
              onStop={() => setPendingStopEntryId(current.entryId)}
              queueEntry={current}
              hidden={hidden}
            />
          ) : isLifeAction ? (
            <ArenaLifeGoalCard
              actionDef={activeAction}
              onStop={() => setPendingStopEntryId(current.entryId)}
              queueEntry={current}
              hidden={hidden}
            />
          ) : (
            <ArenaIdleCard />
          )}
        </div>
      </div>

      {!isCombat && availableActions.length > 0 && (
        <div className="combat-arena-controls">
          {availableActions.map(action => (
            <button key={action.id} onClick={() => onOpenAction(action)} type="button">
              {localizeActionDisplayText(action.name, world, maps)}
            </button>
          ))}
        </div>
      )}
      <ConfirmDialog
        confirmLabel={t('combat.arena.stopConfirmBtn')}
        danger
        description={stopTarget ? t('combat.arena.stopConfirmDesc', { name: resolveQueueEntryDisplayName(stopTarget, stopTarget.actionName, world, maps) }) : ''}
        onClose={() => setPendingStopEntryId(null)}
        onConfirm={() => {
          if (stopTarget) gameClient.sendCommand('action.remove', { actionInstanceId: stopTarget.entryId })
        }}
        open={stopTarget !== null}
        title={t('combat.arena.stopConfirmTitle')}
      />
      <ConfirmDialog
        danger
        description={t('social.confirm.quitSessionDesc')}
        onClose={() => setPendingQuitSession(false)}
        onConfirm={() => unreadyParty()}
        open={pendingQuitSession}
        title={t('social.confirm.quitSessionTitle')}
      />
      <Modal
        className="battle-report-modal"
        labelledBy="combat-stats-panel-title"
        onClose={closeStats}
        open={statsOpen}
        size="medium"
      >
        <CombatStatsPanel />
      </Modal>
      <CharacterAttributeModal
        actorId={inspectedSnapshot?.actorId}
        attributes={inspectedSnapshot?.attributes ?? null}
        buffs={inspectedSnapshot?.buffs ?? []}
        loadout={inspectedSnapshot?.loadout}
        drops={inspectedSnapshot?.drops}
        name={inspectedSnapshot?.name}
        onClose={handleCloseInspect}
        open={inspectedSnapshot !== null}
        role={inspectedSnapshot?.role ?? 'player'}
        title={inspectedSnapshot?.title ?? ''}
        visual={inspectedSnapshot?.visual ?? null}
      />
    </div>
  )
})
