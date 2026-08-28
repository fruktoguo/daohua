import { memo, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties } from 'react'
import { formatNumber } from '../game/format'
import { UnitNumber } from './UnitNumber'
import { useGameStore } from '../game/gameStore'
import { resolveItemMetadata } from '../game/ids'
import type { ActionDefinition, Buff, CharacterVisual, CombatActionState, CombatEncounterDefinition, CombatantDefinition, ExpeditionProgress, QueueEntry } from '../game/types'
import { gameClient } from '../network/gameClient'
import { getT, useT } from '../i18n'
import { Modal } from './Modal'
import { ValueText } from './ValueText'
import { useSmoothProgressRef } from '../game/useSmoothProgress'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { BASELINE_COMBAT_ATTRIBUTES } from '../game/combatConstants'
import { CharacterAvatar } from './CharacterAvatar'

/** 资源条数值变动特效记录（跳字与血条破裂碎片） */
interface ResourceEffect {
  id: number
  delta: number
  left: number
  width: number
  lane: number
}

/** 实体动作类型（攻击或施法） */
type ActorMotionKind = 'attack' | 'cast'

/** 实体行动动作状态 */
interface ActorMotion {
  id: number
  kind: ActorMotionKind
}

/** 实体受击震颤与冲击特效状态 */
interface ActorImpact {
  id: number
  distance: number
  opacity: number
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
 * 战场生命/真元资源条组件 (ArenaResource)
 *
 * 渲染角色气血 (HP) 与灵力 (MP) 仪表盘。
 * 当数值发生变化时：
 * 1. 产生向上飘散的增减跳字浮动动画 (+/- delta)；
 * 2. 受到大额伤害时（>= 最大值 2%），在受损区间生成带物理位移与旋转的血条碎裂切片特效 (Shards)；
 * 3. 具有自动清理定时器以防内存泄漏。
 */
function ArenaResource({
  actorKey,
  value,
  max,
  tone,
}: {
  /** 实体唯一标识符（切换目标时重置特效） */
  actorKey: string
  /** 当前数值 */
  value: number
  /** 最大上限数值 */
  max: number
  /** 色调类型：hp 气血红 / mp 灵力蓝 */
  tone: 'hp' | 'mp'
}): React.JSX.Element {
  const normalizedMax = Math.max(1, max)
  const normalizedValue = Math.max(0, Math.min(normalizedMax, value))
  const previousRef = useRef({ actorKey, max: normalizedMax, value: normalizedValue })
  const nextEffectId = useRef(1)
  const timers = useRef(new Set<number>())
  const [effects, setEffects] = useState<ResourceEffect[]>([])

  useLayoutEffect(() => {
    const previous = previousRef.current
    if (previous.actorKey !== actorKey || previous.max !== normalizedMax) {
      previousRef.current = { actorKey, max: normalizedMax, value: normalizedValue }
      setEffects([])
      return
    }
    if (previous.value === normalizedValue) return

    const delta = normalizedValue - previous.value
    const id = nextEffectId.current++
    const effect: ResourceEffect = {
      id,
      delta,
      left: Math.min(previous.value, normalizedValue) / normalizedMax,
      width: Math.abs(delta) / normalizedMax,
      lane: ((id % 3) - 1) * 8,
    }
    previousRef.current = { actorKey, max: normalizedMax, value: normalizedValue }
    setEffects(current => [...current.slice(-5), effect])
    const timer = window.setTimeout(() => {
      setEffects(current => current.filter(item => item.id !== effect.id))
      timers.current.delete(timer)
    }, 1_100)
    timers.current.add(timer)
  }, [actorKey, normalizedMax, normalizedValue])

  useEffect(() => () => {
    timers.current.forEach(timer => window.clearTimeout(timer))
    timers.current.clear()
  }, [])

  return (
    <div className={`arena-resource arena-resource--${tone}`}>
      <div className="arena-resource-meter">
        <div className="arena-resource-track">
          <i style={{ transform: `scaleX(${normalizedValue / normalizedMax})` }} />
          <span className="arena-resource-badge">
            <UnitNumber disableTooltip value={normalizedValue} /> / <UnitNumber disableTooltip value={normalizedMax} />
          </span>
        </div>
        <span className="arena-resource-shards" aria-hidden="true">
          {effects
            .filter(effect => effect.delta < 0 && effect.width >= resourceShardMinimumRatio)
            .map(effect => {
              const scale = 1 + Math.min(0.5, effect.width * 1.6)
              const travel = (tone === 'hp' ? -52 : 52) * scale
              const rotate = (tone === 'hp' ? -1 : 1) * (16 + (effect.id % 7))
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
        <span className="arena-resource-floats" aria-live="polite">
          {effects.map(effect => (
            <em
              className={effect.delta > 0 ? 'is-positive' : 'is-negative'}
              key={effect.id}
              style={{
                '--float-drift': `${effect.lane}px`,
                left: `${Math.max(0.08, Math.min(0.92, effect.left + effect.width / 2)) * 100}%`,
              } as CSSProperties}
            >
              {effect.delta > 0 ? '+' : ''}{formatNumber(effect.delta)}
            </em>
          ))}
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
    if (!action || action.durationMilliseconds <= 0) {
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
      const startedNewCycle = elapsed + 50 < anchor.lastObservedElapsed
      anchor.lastObservedElapsed = elapsed
      if (!startedNewCycle && Math.abs(elapsed - expected) <= 250) return
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
 * 基于 requestAnimationFrame 高频更新显示当前技能名称与精确到 0.1s 的倒计时。
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
    // 怪物待机或无动作时：显示纯净空条，不渲染文字
    if (!action || action.durationMilliseconds <= 0 || action.name === '待机' || !action.name.trim()) {
      if (role === 'player' && action?.name === '调息中') {
        badge.textContent = t('combat.arena.regenerating')
        badge.style.display = ''
      } else {
        badge.textContent = ''
        badge.style.display = 'none'
      }
      return
    }

    badge.style.display = ''
    let animationFrameId = 0
    const duration = Math.max(1, action.durationMilliseconds)
    let baseName = action.name.replace(/回气$/, '').replace(/\s*\([\d.]+s\/[\d.]+s\)/, '').trim()
    if (baseName === '灵击' || baseName === '攻击') {
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
      const currentRemaining = hasAbsoluteDeadline
        ? Math.max(0, action.completesAtUnixMilliseconds! - estimatedServerNow)
        : Math.max(0, initialRemaining - (performance.now() - startPerfTime))

      const remainingSeconds = Math.max(0, currentRemaining / 1000)
      const formatted = t('combat.arena.countdown', { name: baseName, seconds: remainingSeconds.toFixed(1) })
      if (badge.textContent !== formatted) {
        badge.textContent = formatted
      }

      if (currentRemaining > 0) {
        animationFrameId = requestAnimationFrame(tick)
      }
    }

    tick()

    return () => {
      if (animationFrameId) cancelAnimationFrame(animationFrameId)
    }
  }, [
    action?.completesAtUnixMilliseconds,
    action?.durationMilliseconds,
    action?.elapsedMilliseconds,
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
  const isRevivalAction = action?.kind === 'respawn' || action?.name?.includes('复活') || action?.name?.includes('复起') || action?.name?.includes('还魂')
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
  const remaining = Math.max(0, buff.expiresAtUnixSeconds - serverTime)
  const circleRef = useRef<SVGCircleElement>(null)
  // 环形倒计时基准：服务端同步到该 buff（到期时间变化）时，以同步时刻的剩余时间重建基准，
  // 使 buff 被刷新为更长或更短的持续时间时比例都保持真实。
  const [baseDuration, setBaseDuration] = useState(() => Math.max(10, remaining))
  useEffect(() => {
    const syncedRemaining = buff.expiresAtUnixSeconds - useGameStore.getState().serverTimeUnixSeconds
    setBaseDuration(Math.max(10, Math.max(0, syncedRemaining)))
  }, [buff.expiresAtUnixSeconds])
  const total = baseDuration

  // 效果说明与单字图标（说明文案与字形均随语言切换）
  let tone: 'qi' | 'jade' | 'purple' | 'danger' | 'gold' = 'gold'
  let glyph = buff.name.slice(0, 1) || t('combat.arena.glyphGeneric')
  let effectDesc = t('combat.arena.buffGeneric')

  if (buff.qiGainMultiplier > 1) {
    tone = 'qi'
    glyph = t('combat.arena.glyphQi')
    effectDesc = t('combat.arena.buffQiGain', { n: formatNumber(Math.round((buff.qiGainMultiplier - 1) * 100)) })
  } else if (buff.gatheringOutputMultiplier && buff.gatheringOutputMultiplier > 1) {
    tone = 'jade'
    glyph = t('combat.arena.glyphGathering')
    effectDesc = t('combat.arena.buffGathering', { n: formatNumber(Math.round((buff.gatheringOutputMultiplier - 1) * 100)) })
  } else if (buff.alchemyOutputMultiplier && buff.alchemyOutputMultiplier > 1) {
    tone = 'purple'
    glyph = t('combat.arena.glyphAlchemy')
    effectDesc = t('combat.arena.buffAlchemy', { n: formatNumber(Math.round((buff.alchemyOutputMultiplier - 1) * 100)) })
  } else if (buff.damageRatio && buff.damageRatio > 0) {
    tone = 'danger'
    glyph = t('combat.arena.glyphDamage')
    effectDesc = t('combat.arena.buffDamage', { n: formatNumber(Math.round(buff.damageRatio * 100)) })
  } else if (buff.attackSpeedRatio && buff.attackSpeedRatio > 0) {
    tone = 'gold'
    glyph = t('combat.arena.glyphSpeed')
    effectDesc = t('combat.arena.buffAttackSpeed', { n: formatNumber(Math.round(buff.attackSpeedRatio * 100)) })
  }

  const radius = 9
  const circumference = 2 * Math.PI * radius

  useLayoutEffect(() => {
    const circle = circleRef.current
    if (!circle) return

    circle.style.strokeDasharray = `${circumference}`
    let animationFrameId: number

    const update = () => {
      const currentRemaining = Math.max(0, buff.expiresAtUnixSeconds - Date.now() / 1000)
      const ratio = Math.min(1, Math.max(0, currentRemaining / total))
      const offset = circumference * (1 - ratio)
      circle.style.strokeDashoffset = `${offset}`

      if (currentRemaining > 0) {
        animationFrameId = requestAnimationFrame(update)
      }
    }

    update()
    return () => cancelAnimationFrame(animationFrameId)
  }, [buff.expiresAtUnixSeconds, circumference, total])

  return (
    <FloatingPopover
      content={
        <FloatingInfoCard
          badge={t('combat.arena.buffStacks', { n: formatNumber(buff.stacks) })}
          badgeTone={tone}
          description={effectDesc}
          subtitle={t('combat.arena.buffRemaining', { n: formatNumber(remaining) })}
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
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)
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
    const elapsedSinceSync = Math.max(0, performance.now() - lastSyncPerformanceTime)
    const elapsed = Math.max(0, Math.min(duration, baseElapsedMs + elapsedSinceSync))
    const signature = `${expedition.phase}|${expedition.battleSequence ?? 0}|${duration}`
    const now = performance.now()
    const anchor = anchorRef.current
    if (anchor?.signature === signature) {
      const expected = Math.min(anchor.duration, anchor.elapsed + now - anchor.startedAt)
      if (Math.abs(elapsed - expected) <= 300) return
    }

    animationRef.current?.cancel()
    const fraction = elapsed / duration
    const transformProperty = orientation === 'vertical' ? 'scaleY' : 'scaleX'
    fill.style.transform = `${transformProperty}(${fraction})`
    anchorRef.current = { signature, elapsed, duration, startedAt: now }
    const remaining = duration - elapsed
    if (remaining <= 0 || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    animationRef.current = fill.animate(
      [{ transform: `${transformProperty}(${fraction})` }, { transform: `${transformProperty}(1)` }],
      { duration: remaining, easing: 'linear', fill: 'forwards' },
    )
  }, [expedition.battleSequence, expedition.durationSeconds, expedition.elapsedSeconds, expedition.phase, expedition.progress, lastSyncPerformanceTime, orientation])

  useEffect(() => () => animationRef.current?.cancel(), [])

  return <i ref={fillRef} />
}

/**
 * 战场中心历练/搜寻阶段状态告示牌组件
 */
function ArenaPhaseState({ expedition }: { expedition: ExpeditionProgress }): React.JSX.Element {
  const t = useT()
  const isInitialSearch = expedition.phase === 'initial-search'

  const title = isInitialSearch
    ? t('combat.arena.phaseSearch')
    : t('combat.arena.phaseHunt')

  const sigil = isInitialSearch ? t('combat.arena.sigilSearch') : t('combat.arena.sigilHunt')
  const remaining = Math.max(0, expedition.durationSeconds - expedition.elapsedSeconds)

  return (
    <div className={`arena-search-state arena-search-state--${expedition.phase}`} role="status" aria-label={title}>
      <span className="arena-search-sigil-vertical" aria-hidden="true">{sigil}</span>
      <strong className="arena-search-text-vertical">{title}</strong>
      <div className="arena-search-progress-vertical">
        <ArenaPhaseFill expedition={expedition} orientation="vertical" />
      </div>
      <span className="arena-search-time-vertical">{remaining.toFixed(1)}s</span>
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
  action,
  buffs = [],
  onInspect,
  visual,
  visible = true,
  compact = false,
}: {
  actorKey: string
  actorId?: string | null
  name: string
  seal: string
  role: 'player' | 'enemy'
  hp: number
  maxHp: number
  mp: number
  maxMp: number
  action?: CombatActionState | null
  buffs?: readonly Buff[]
  onInspect: () => void
  visual?: CharacterVisual | null
  visible?: boolean
  compact?: boolean
}): React.JSX.Element {
  const t = useT()
  const nextMotionId = useRef(1)
  const previousHitpoints = useRef({ actorKey, hp })
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

  useLayoutEffect(() => {
    const previous = previousHitpoints.current
    if (previous.actorKey !== actorKey) {
      previousHitpoints.current = { actorKey, hp }
      setImpact(null)
      return
    }

    if (hp < previous.hp) {
      const strength = Math.max(0.25, Math.min(1, (previous.hp - hp) / Math.max(1, maxHp)))
      const nextImpact = {
        id: nextMotionId.current++,
        distance: 3 + strength * 6,
        opacity: 0.75 + strength * 0.25,
      }
      setImpact(nextImpact)
      if (clearImpactTimer.current !== null) window.clearTimeout(clearImpactTimer.current)
      clearImpactTimer.current = window.setTimeout(() => setImpact(null), 560)
    }
    previousHitpoints.current = { actorKey, hp }
  }, [actorKey, hp, maxHp])

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

    const play = () => {
      const nextMotion = { id: nextMotionId.current++, kind }
      setActionMotion(nextMotion)
      if (clearActionMotionTimer.current !== null) window.clearTimeout(clearActionMotionTimer.current)
      clearActionMotionTimer.current = window.setTimeout(() => setActionMotion(null), duration)
    }
    const timer = window.setTimeout(play, Math.max(0, delay))
    scheduledAction.current = { signature, deadline, kind, timer }
  }, [action?.completesAtUnixMilliseconds, action?.kind, actorKey, hp, visible])

  useEffect(() => () => {
    if (scheduledAction.current !== null) window.clearTimeout(scheduledAction.current.timer)
    if (clearActionMotionTimer.current !== null) window.clearTimeout(clearActionMotionTimer.current)
    if (clearImpactTimer.current !== null) window.clearTimeout(clearImpactTimer.current)
  }, [])

  return (
    <article
      aria-label={t('combat.arena.inspectAria', { name: name || t('combat.arena.defaultActorName') })}
      className={`arena-actor arena-actor--${role}${compact ? ' arena-actor--compact' : ''}${visible ? '' : ' is-dormant'}${hp <= 0 ? ' is-dead' : ''}${actionMotion ? ` is-action-${actionMotion.kind}` : ''}${impact ? ' is-hit' : ''}`}
      onClick={onInspect}
      onKeyDown={event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onInspect()
        }
      }}
      role="button"
      tabIndex={visible ? 0 : -1}
    >
      <header className="arena-overhead">
        <strong>{name || '\u00a0'}</strong>
        <div className="arena-vitals">
          <ArenaResource actorKey={actorKey} max={maxHp} tone="hp" value={hp} />
          <ArenaResource actorKey={actorKey} max={maxMp} tone="mp" value={mp} />
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
            className="arena-impact-fx"
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
    </article>
  )
}

/**
 * 非战斗生活行动总目标与轮次进度卡片 (ArenaLifeGoalCard)
 *
 * 当玩家队列首项为炼丹、打坐修炼、游历采集等生活类行动时，
 * 右侧敌方九宫格无缝切换为此卡片，显示目标产物大图标、计划总产出、所需原料消耗清单与平滑进度条。
 */
function ArenaLifeGoalCard({
  queueEntry,
  actionDef,
  onStop,
}: {
  /** 当前正在执行的队列条目 */
  queueEntry?: QueueEntry | null
  /** 对应动作定义详情 */
  actionDef?: ActionDefinition | null
  /** 中止当前生活行动回调 */
  onStop?: () => void
}): React.JSX.Element {
  const t = useT()
  const itemCatalog = useGameStore(state => state.items)
  const isAlchemy = queueEntry?.type === 'alchemy'
  const isCultivation = queueEntry?.type === 'cultivation'
  const isExploration = queueEntry?.type === 'exploration'

  const title = isAlchemy ? t('combat.life.titleAlchemy') : isCultivation ? t('combat.life.titleCultivation') : isExploration ? t('combat.life.titleExploration') : t('combat.life.titleDefault')
  const actionName = queueEntry?.actionName || actionDef?.name || t('combat.life.fallbackActionName')
  const isInfinite = (queueEntry?.remainingRepeats ?? 0) < 0
  const remainingRepeats = queueEntry?.remainingRepeats ?? 0
  const repeats = isInfinite ? -1 : remainingRepeats

  // 目标产物分析
  const outputs = actionDef?.outputs || {}
  const outputEntries = Object.entries(outputs)
  const primaryOutput = outputEntries[0]
  const primaryItemId = primaryOutput ? primaryOutput[0] : null
  const primaryAmountPerBatch = primaryOutput ? primaryOutput[1] : 1
  const totalTargetDisplay = isInfinite ? '∞' : formatNumber(primaryAmountPerBatch * repeats)

  const itemMeta = primaryItemId ? resolveItemMetadata(primaryItemId) : null
  const itemDefinition = primaryItemId ? itemCatalog.find(i => i.itemId === primaryItemId) : null
  const itemName = itemDefinition?.name || itemMeta?.label || (isCultivation ? t('combat.life.qiName') : t('combat.life.fallbackItemName'))
  const itemGlyph = itemMeta?.glyph || (isAlchemy ? '⚗️' : isCultivation ? '☯' : '🌿')
  const itemTone = itemMeta?.tone || 'gold'

  // 所需原料分析
  const inputs = actionDef?.inputs || {}
  const inputEntries = Object.entries(inputs)

  // 进度条 DOM 引用与全局帧级统一同步
  const barRef = useRef<HTMLDivElement>(null)
  const textRef = useRef<HTMLSpanElement>(null)
  const duration = Math.max(1, queueEntry?.durationSeconds || actionDef?.durationSeconds || 10)
  const progressSec = queueEntry?.progressSeconds || 0

  useSmoothProgressRef({
    entry: queueEntry,
    fillRef: barRef,
    textRef: textRef,
    textFormat: (currentSec, dur, pct) => getT()('combat.life.roundProgress', { current: currentSec.toFixed(1), duration: formatNumber(dur), percent: formatNumber(pct) }),
  })

  const initialRatio = duration > 0 ? Math.min(1, Math.max(0, progressSec / duration)) : 0

  return (
    <div className="arena-life-card">
      <header className="arena-life-header">
        <span className="arena-life-tag">{title}</span>
        <strong className="arena-life-action-name">{actionName}</strong>
      </header>

      <div className="arena-life-body">
        <div className={`arena-life-icon-box arena-life-icon-box--${itemTone}`}>
          <span className="arena-life-glyph">{itemGlyph}</span>
          <span className="arena-life-count-badge">{isInfinite ? '∞' : `x${totalTargetDisplay}`}</span>
        </div>

        <div className="arena-life-meta">
          <div className="arena-life-target-title">
            <span>{t('combat.life.targetOutput')}</span>
            <strong className="tone-gold">{itemName}</strong>
          </div>
          <div className="arena-life-target-total">
            <span>{t('combat.life.plannedTotal')}</span>
            <strong className="tone-bright">
              {totalTargetDisplay} {isCultivation ? t('combat.life.unitQi') : t('combat.life.unitItem')}
            </strong>
          </div>
          <div className="arena-life-subinfo">
            <span>{t('combat.life.perBatch', { n: formatNumber(primaryAmountPerBatch) })}</span>
            <span>{isInfinite ? t('combat.life.infiniteRounds') : t('combat.life.remainingRounds', { n: formatNumber(remainingRepeats) })}</span>
          </div>
        </div>

        {inputEntries.length > 0 && (
          <div className="arena-life-inputs">
            <span className="arena-life-inputs-label">{t('combat.life.inputsLabel')}</span>
            <div className="arena-life-inputs-list">
              {inputEntries.map(([inId, inAmt]) => {
                const inDef = itemCatalog.find(i => i.itemId === inId)
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
            style={{ transform: `scaleX(${initialRatio})` }}
          >
            <i />
          </div>
          <span className="arena-life-progress-text" ref={textRef}>
            {t('combat.life.roundProgress', { current: progressSec.toFixed(1), duration: formatNumber(duration), percent: formatNumber(Math.round(initialRatio * 100)) })}
          </span>
        </div>
        {onStop && (
          <button className="arena-life-stop-btn danger" onClick={onStop} type="button">
            {isAlchemy ? t('combat.life.stopAlchemy') : isCultivation ? t('combat.life.stopCultivation') : t('combat.life.stopAction')}
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
}: {
  /** 当前正在执行的队列条目 */
  queueEntry?: QueueEntry | null
  /** 中止当前赶路行动回调 */
  onStop?: () => void
}): React.JSX.Element {
  const t = useT()
  const expedition = queueEntry?.expedition
  const actionName = queueEntry?.actionName || t('combat.travel.fallbackActionName')
  const destinationName = actionName.startsWith('赶往') ? actionName.slice(2).trim() : actionName
  const totalDistance = expedition?.totalDistance ?? 0
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
            style={{ transform: `scaleX(${initialRatio})` }}
          >
            <i />
          </div>
          <span className="arena-life-progress-text" ref={textRef}>
            {t('combat.travel.roundProgress', {
              current: progressSec.toFixed(1),
              duration: formatNumber(duration),
              percent: formatNumber(Math.round(initialRatio * 100)),
            })}
          </span>
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

/** 角色属性行数据 */
interface AttributeRow {
  label: string
  value: string
}

/**
 * 参战角色详细属性与生效 Buff 模态窗组件 (CharacterAttributeModal)
 */
function CharacterAttributeModal({
  actorId,
  name,
  role = 'player',
  visual,
  baseRows,
  buffs,
  onClose,
  open,
  specialRows,
  title,
}: {
  actorId?: string | null
  name?: string | null
  role?: 'player' | 'enemy'
  visual?: CharacterVisual | null
  baseRows: readonly AttributeRow[]
  buffs: readonly Buff[]
  onClose: () => void
  open: boolean
  specialRows: readonly AttributeRow[]
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
      <section className="character-attribute-section">
        <h3>{t('combat.attrs.base')}</h3>
        <dl>{baseRows.map(row => <div key={row.label}><dt>{row.label}</dt><dd><ValueText text={row.value} /></dd></div>)}</dl>
      </section>
      {specialRows.length > 0 && (
        <section className="character-attribute-section">
          <h3>{t('combat.attrs.special')}</h3>
          <dl>{specialRows.map(row => <div key={row.label}><dt>{row.label}</dt><dd><ValueText text={row.value} /></dd></div>)}</dl>
        </section>
      )}
      {buffs.length > 0 && (
        <section className="character-attribute-section">
          <h3>{t('combat.attrs.effects')}</h3>
          <div className="character-buff-list">
            {buffs.map(buff => <span key={buff.buffId}>{buff.name}{buff.stacks > 1 ? ` ×${buff.stacks}` : ''}</span>)}
          </div>
        </section>
      )}
    </Modal>
  )
}

/**
 * 根据敌怪名称或遭遇定义解析出具体的敌怪数值模型 (CombatantDefinition)
 */
function resolveEnemyDefinition(
  enemyName: string,
  activeEncounter: CombatEncounterDefinition | null | undefined,
  encounters: readonly CombatEncounterDefinition[]
): CombatantDefinition | null {
  if (!enemyName && !activeEncounter) return null
  if (activeEncounter) {
    if (activeEncounter.enemy?.name === enemyName) {
      return activeEncounter.enemy
    }
    const boss = activeEncounter.bossEnemies?.find(b => b.name === enemyName)
    if (boss) return boss
    const spawn = activeEncounter.randomSpawns?.find(s => s.enemy?.name === enemyName)
    if (spawn) return spawn.enemy
  }
  for (const enc of encounters) {
    if (enc.enemy?.name === enemyName) return enc.enemy
    const boss = enc.bossEnemies?.find(b => b.name === enemyName)
    if (boss) return boss
    const spawn = enc.randomSpawns?.find(s => s.enemy?.name === enemyName)
    if (spawn) return spawn.enemy
  }
  return activeEncounter?.enemy ?? null
}

interface EnemyDisplayState {
  actorKey: string
  name: string
  seal: string
  hp: number
  maxHp: number
  mp: number
  maxMp: number
  action?: CombatActionState | null
  visual?: CharacterVisual | null
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
export const CombatArena = memo(function CombatArena({
  onOpenAction,
}: {
  onOpenAction: (action: ActionDefinition) => void
}): React.JSX.Element {
  const playerName = useGameStore(state => state.name)
  const playerVisual = useGameStore(state => state.visual)
  const combat = useGameStore(state => state.combat)
  const combatResources = useGameStore(state => state.combatResources)
  const combatAttributes = useGameStore(state => state.combatAttributes)
  const buffs = useGameStore(state => state.buffs)
  const actions = useGameStore(state => state.actions)
  const encounters = useGameStore(state => state.encounters)
  const world = useGameStore(state => state.world)
  const queue = useGameStore(state => state.queue.entries)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)
  const [inspectedActor, setInspectedActor] = useState<'player' | 'enemy' | null>(null)
  const t = useT()

  const current = queue[0] ?? null
  const isCombat = current?.type === 'combat' && current.executionKind === 'combat-encounter'
  const isTravel = current !== null && (current.executionKind === 'travel' || current.expedition?.phase === 'travel' || current.actionId === 'world.travel')
  const isLifeAction = current !== null && !isCombat && !isTravel
  const expedition = isCombat ? current.expedition : null
  const activeAction = current ? actions.find(action => action.id === current.actionId) : null
  const activeEncounterId = expedition?.encounterId ?? activeAction?.combatEncounterId
  const activeEncounter = activeEncounterId
    ? encounters.find(encounter => encounter.encounterId === activeEncounterId)
    : null

  const availableActions = useMemo(() => actions.filter(action => (
    action.type === 'combat'
      && (!action.availableNodeIds?.length || (world.nodeId !== null && action.availableNodeIds.includes(world.nodeId)))
  )), [actions, world.nodeId])

  const fallbackMaxHp = combatAttributes?.maxHitpoints ?? BASELINE_COMBAT_ATTRIBUTES.maxHp
  const fallbackMaxMp = combatAttributes?.maxManapoints ?? BASELINE_COMBAT_ATTRIBUTES.maxMp
  const playerMaxHp = expedition?.playerMaxHitpoints ?? combatResources?.maxHitpoints ?? fallbackMaxHp
  const playerMaxMp = expedition?.playerMaxManapoints ?? combatResources?.maxManapoints ?? fallbackMaxMp
  const playerHp = (isCombat ? expedition?.playerHitpoints : undefined) ?? combatResources?.hitpoints ?? playerMaxHp
  const playerMp = (isCombat ? expedition?.playerManapoints : undefined) ?? combatResources?.manapoints ?? playerMaxMp
  const phase = expedition?.phase ?? (isTravel ? 'travel' : isLifeAction ? 'life-action' : 'idle')
  const enemyVisible = isCombat && (phase === 'combat' || ((expedition?.enemyHitpoints ?? 0) > 0))

  const playerAction: CombatActionState | null = useMemo(() => {
    if (isCombat) {
      return expedition?.playerAction ?? null
    }
    if ((isLifeAction || isTravel) && current) {
      const durSec = (isTravel && current.expedition?.durationSeconds) ? current.expedition.durationSeconds : current.durationSeconds
      const durMs = Math.max(1, durSec * 1000)
      const baseElSec = (isTravel && current.expedition?.elapsedSeconds !== undefined) ? current.expedition.elapsedSeconds : current.progressSeconds
      const baseElMs = Math.max(0, baseElSec * 1000)
      const isRunning = current.status === 'running'
      const elapsedSinceSync = isRunning ? Math.max(0, performance.now() - lastSyncPerformanceTime) : 0
      const currentElMs = Math.min(durMs, baseElMs + elapsedSinceSync)
      return {
        kind: 'cast',
        name: current.actionName,
        durationMilliseconds: durMs,
        elapsedMilliseconds: currentElMs,
        completesAtUnixMilliseconds: Date.now() + Math.max(0, durMs - currentElMs),
      }
    }
    return null
  }, [current, expedition, isCombat, isLifeAction, isTravel, lastSyncPerformanceTime])

  const enemyRef = useRef<EnemyDisplayState>({
    actorKey: 'enemy:idle',
    name: '',
    seal: '',
    hp: 0,
    maxHp: 1,
    mp: 0,
    maxMp: 1,
    action: null,
    visual: null,
  })

  const enemyName = expedition?.enemyName ?? activeEncounter?.enemy?.name ?? t('combat.arena.unknownEnemy')
  const enemyDefinition = useMemo(
    () => resolveEnemyDefinition(enemyName, activeEncounter, encounters),
    [enemyName, activeEncounter, encounters]
  )

  if (enemyVisible) {
    enemyRef.current = {
      actorKey: `enemy:${expedition?.battleSequence ?? 0}:${enemyName}`,
      name: enemyName,
      seal: enemyName.slice(0, 1),
      hp: expedition?.enemyHitpoints ?? enemyDefinition?.maxHitpoints ?? activeEncounter?.enemy?.maxHitpoints ?? 0,
      maxHp: expedition?.enemyMaxHitpoints ?? enemyDefinition?.maxHitpoints ?? activeEncounter?.enemy?.maxHitpoints ?? 1,
      mp: expedition?.enemyManapoints ?? enemyDefinition?.maxManapoints ?? activeEncounter?.enemy?.maxManapoints ?? 0,
      maxMp: expedition?.enemyMaxManapoints ?? enemyDefinition?.maxManapoints ?? activeEncounter?.enemy?.maxManapoints ?? 1,
      action: expedition?.enemyAction ?? null,
      visual: enemyDefinition?.visual ?? activeEncounter?.enemy?.visual ?? null,
    }
  }
  const enemy = enemyRef.current
  const playerBaseRows: AttributeRow[] = [
    { label: t('combat.attr.hp'), value: `${formatNumber(playerHp)} / ${formatNumber(playerMaxHp)}` },
    { label: t('combat.attr.mp'), value: `${formatNumber(playerMp)} / ${formatNumber(playerMaxMp)}` },
    { label: t('combat.attr.stamina'), value: formatNumber(Math.max(10, combat?.staminaLevel ?? 10)) },
    { label: t('combat.attr.meridian'), value: formatNumber(Math.max(10, combat?.intelligenceLevel ?? 10)) },
    { label: t('combat.attr.defense'), value: formatNumber(Math.max(10, combat?.defenseLevel ?? 10)) },
    { label: t('combat.attr.attack'), value: formatNumber(Math.max(10, combat?.attackLevel ?? 10)) },
    { label: t('combat.attr.divineSense'), value: formatNumber(Math.max(10, combat?.swordsmanshipLevel ?? 10)) },
    { label: t('combat.attr.movement'), value: formatNumber(Math.max(10, combat?.talismanLevel ?? 10)) },
    { label: t('combat.attr.power'), value: formatNumber(combatAttributes?.maxDamage ?? 0) },
    { label: t('combat.attr.accuracy'), value: formatNumber(combatAttributes?.accuracyRating ?? 0) },
    { label: t('combat.attr.evasion'), value: formatNumber(combatAttributes?.evasionRating ?? 0) },
    { label: t('combat.attr.armor'), value: formatNumber(combatAttributes?.armor ?? 0) },
    { label: t('combat.attr.magicRes'), value: formatNumber(combatAttributes?.magicResistance ?? 0) },
    { label: t('combat.attr.metalRes'), value: formatNumber(combatAttributes?.metalResistance ?? 0) },
    { label: t('combat.attr.woodRes'), value: formatNumber(combatAttributes?.woodResistance ?? 0) },
    { label: t('combat.attr.waterRes'), value: formatNumber(combatAttributes?.waterResistance ?? 0) },
    { label: t('combat.attr.fireRes'), value: formatNumber(combatAttributes?.fireResistance ?? 0) },
    { label: t('combat.attr.earthRes'), value: formatNumber(combatAttributes?.earthResistance ?? 0) },
    { label: t('combat.attr.attackInterval'), value: t('combat.attr.seconds', { n: formatNumber((combatAttributes?.attackIntervalMilliseconds ?? 0) / 1000) }) },
  ]
  const playerSpecialCandidates: Array<[string, number, 'percent' | 'number' | 'regen' | 'ratio_regen']> = [
    [t('combat.special.tenacity'), combatAttributes?.tenacity ?? 0, 'number'],
    [t('combat.special.hpRegen'), combatAttributes?.hpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.mpRegen'), combatAttributes?.mpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.penetration'), combatAttributes?.penetrationRatio ?? 0, 'percent'],
    [t('combat.special.criticalChance'), combatAttributes?.criticalChance ?? 0, 'percent'],
    [t('combat.special.criticalDamage'), combatAttributes?.criticalDamageRatio ?? 0, 'percent'],
    [t('combat.special.criticalAvoidance'), combatAttributes?.criticalAvoidance ?? 0, 'percent'],
    [t('combat.special.damageAbsorption'), combatAttributes?.damageAbsorptionRatio ?? 0, 'percent'],
    [t('combat.special.lifeSteal'), combatAttributes?.lifeStealRatio ?? 0, 'percent'],
    [t('combat.special.armorDamage'), combatAttributes?.armorDamageRatio ?? 0, 'percent'],
    [t('combat.special.hpDrain'), combatAttributes?.hpDrainRatio ?? 0, 'percent'],
    [t('combat.special.damageTaken'), combatAttributes?.damageTakenRatio ?? 0, 'percent'],
    [t('combat.special.dropRate'), combat?.combatDropRate ?? 0, 'percent'],
    [t('combat.special.dropQuantity'), combat?.combatDropQuantity ?? 0, 'percent'],
    [t('combat.special.rareFind'), combat?.combatRareFind ?? 0, 'percent'],
    [t('combat.special.searchSpeed'), combat?.searchSpeedRatio ?? 0, 'percent'],
  ]
  const playerSpecialRows = playerSpecialCandidates
    .filter(([, value]) => Math.abs(value) > 0.000001)
    .map(([label, value, kind]) => ({
      label,
      value: kind === 'percent'
        ? `${value > 0 ? '+' : ''}${Math.round(value * 1000) / 10}%`
        : kind === 'regen'
          ? `+${formatNumber(value)} / 10s`
          : kind === 'ratio_regen'
            ? `+${Math.round(value * 1000) / 10}% / 10s`
            : formatNumber(value),
    }))
  const enemyBaseRows: AttributeRow[] = [
    { label: t('combat.attr.hp'), value: `${formatNumber(enemy.hp)} / ${formatNumber(enemy.maxHp)}` },
    { label: t('combat.attr.mp'), value: `${formatNumber(enemy.mp)} / ${formatNumber(enemy.maxMp)}` },
    { label: t('combat.attr.accuracy'), value: formatNumber(enemyDefinition?.accuracyRating ?? 0) },
    { label: t('combat.attr.evasion'), value: formatNumber(enemyDefinition?.evasionRating ?? 0) },
    { label: t('combat.attr.maxDamage'), value: formatNumber(enemyDefinition?.maxDamage ?? 0) },
    { label: t('combat.attr.attackInterval'), value: t('combat.attr.seconds', { n: formatNumber((enemyDefinition?.attackIntervalMilliseconds ?? 0) / 1000) }) },
    { label: t('combat.attr.armor'), value: formatNumber(enemyDefinition?.armor ?? 0) },
    { label: t('combat.attr.magicRes'), value: formatNumber(enemyDefinition?.magicResistance ?? 0) },
    { label: t('combat.attr.metalRes'), value: formatNumber(enemyDefinition?.metalResistance ?? 0) },
    { label: t('combat.attr.woodRes'), value: formatNumber(enemyDefinition?.woodResistance ?? 0) },
    { label: t('combat.attr.waterRes'), value: formatNumber(enemyDefinition?.waterResistance ?? 0) },
    { label: t('combat.attr.fireRes'), value: formatNumber(enemyDefinition?.fireResistance ?? 0) },
    { label: t('combat.attr.earthRes'), value: formatNumber(enemyDefinition?.earthResistance ?? 0) },
  ]
  const enemySpecialCandidates: Array<[string, number, 'percent' | 'number' | 'milliseconds' | 'ratio_regen']> = [
    [t('combat.special.tenacity'), enemyDefinition?.tenacity ?? 0, 'number'],
    [t('combat.special.hpRegen'), enemyDefinition?.hpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.mpRegen'), enemyDefinition?.mpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.lifeSteal'), enemyDefinition?.lifeStealRatio ?? 0, 'percent'],
    [t('combat.special.armorDamage'), enemyDefinition?.armorDamageRatio ?? 0, 'percent'],
    [t('combat.special.hpDrain'), enemyDefinition?.hpDrainRatio ?? 0, 'percent'],
    [t('combat.special.criticalChance'), enemyDefinition?.criticalChance ?? 0, 'percent'],
    [t('combat.special.criticalDamage'), enemyDefinition?.criticalDamageRatio ?? 0, 'percent'],
    [t('combat.special.criticalAvoidance'), enemyDefinition?.criticalAvoidance ?? 0, 'percent'],
    [t('combat.special.damageAbsorption'), enemyDefinition?.damageAbsorptionRatio ?? 0, 'percent'],
  ]
  const enemySpecialRows = enemySpecialCandidates
    .map(([label, value, kind]) => ({
      label,
      value: kind === 'percent'
        ? `${value > 0 ? '+' : ''}${Math.round(value * 1000) / 10}%`
        : kind === 'ratio_regen'
          ? `+${Math.round(value * 1000) / 10}% / 10s`
          : kind === 'milliseconds'
            ? t('combat.attr.seconds', { n: formatNumber(value / 1000) })
            : formatNumber(value),
    }))

  return (
    <div className="combat-arena-shell">
      <div className={`combat-arena-field combat-arena-field--${phase}`}>
        {/* 左侧：己方 3x3 阵型 (左为后排，右为前排，1号位在前排正中靠近敌方) */}
        <div className="arena-side arena-side--allies">
          <div className="arena-formation arena-formation--allies">
            <div className="arena-slot arena-slot--occupied arena-slot--slot-1" key="ally-slot-1">
              <ArenaActor
                action={playerAction}
                actorId="player"
                actorKey="player"
                buffs={buffs}
                compact
                hp={playerHp}
                maxHp={playerMaxHp}
                maxMp={playerMaxMp}
                mp={playerMp}
                name={playerName}
                onInspect={() => setInspectedActor('player')}
                role="player"
                seal={playerName.slice(0, 1) || t('combat.arena.sealFallback')}
                visual={playerVisual}
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
          </div>
        </div>

        {/* 战场居中浮层：仅在搜寻敌人阶段展示 */}
        {isCombat && (phase === 'initial-search' || phase === 'search') && expedition && (
          <div className="arena-center-stage">
            <ArenaPhaseState expedition={expedition} />
          </div>
        )}

        {/* 右侧：敌方 3x3 阵型 (左为前排，右为后排，1号位在前排正中靠近己方) OR 生活行动总目标 OR 空闲状态 */}
        <div className="arena-side arena-side--enemies">
          {isCombat ? (
            enemyVisible ? (
              <div className="arena-formation arena-formation--enemies">
                <div className="arena-slot arena-slot--occupied arena-slot--slot-1" key="enemy-slot-1">
                  <ArenaActor
                    action={enemy.action}
                    actorId={expedition?.enemyId ?? activeEncounter?.enemy?.enemyId ?? activeEncounterId}
                    actorKey={enemy.actorKey}
                    compact
                    hp={enemy.hp}
                    maxHp={enemy.maxHp}
                    maxMp={enemy.maxMp}
                    mp={enemy.mp}
                    name={enemy.name}
                    onInspect={() => setInspectedActor('enemy')}
                    role="enemy"
                    seal={enemy.seal}
                    visual={enemy.visual}
                  />
                </div>
                {/* 预留空槽维持九宫格与前中后排比例 */}
                <div className="arena-slot arena-slot--empty arena-slot--slot-2" key="enemy-slot-2" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-3" key="enemy-slot-3" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-4" key="enemy-slot-4" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-5" key="enemy-slot-5" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-6" key="enemy-slot-6" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-7" key="enemy-slot-7" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-8" key="enemy-slot-8" />
                <div className="arena-slot arena-slot--empty arena-slot--slot-9" key="enemy-slot-9" />
              </div>
            ) : (
              <div className="arena-formation arena-formation--enemies">
                {Array.from({ length: 9 }).map((_, i) => (
                  <div className={`arena-slot arena-slot--empty arena-slot--slot-${i + 1}`} key={`empty-slot-${i + 1}`} />
                ))}
              </div>
            )
          ) : isTravel ? (
            <ArenaTravelCard
              onStop={() => gameClient.sendCommand('action.remove', { actionInstanceId: current.entryId })}
              queueEntry={current}
            />
          ) : isLifeAction ? (
            <ArenaLifeGoalCard
              actionDef={activeAction}
              onStop={() => gameClient.sendCommand('action.remove', { actionInstanceId: current.entryId })}
              queueEntry={current}
            />
          ) : (
            <ArenaIdleCard />
          )}
        </div>
      </div>

      <div className="combat-arena-controls">
        {current ? (
          <button
            className="danger"
            onClick={() => gameClient.sendCommand('action.remove', { actionInstanceId: current.entryId })}
            type="button"
          >
            {t('combat.arena.stopCurrent', { name: current.actionName })}
          </button>
        ) : availableActions.length > 0 ? (
          availableActions.map(action => (
            <button key={action.id} onClick={() => onOpenAction(action)} type="button">
              {action.name}
            </button>
          ))
        ) : null}
      </div>
      <CharacterAttributeModal
        actorId={inspectedActor === 'enemy' ? (expedition?.enemyId ?? activeEncounter?.enemy?.enemyId ?? activeEncounterId) : 'player'}
        baseRows={inspectedActor === 'enemy' ? enemyBaseRows : playerBaseRows}
        buffs={inspectedActor === 'player' ? buffs : []}
        name={inspectedActor === 'enemy' ? enemy.name : playerName}
        onClose={() => setInspectedActor(null)}
        open={inspectedActor !== null}
        role={inspectedActor ?? 'player'}
        specialRows={inspectedActor === 'enemy' ? enemySpecialRows : playerSpecialRows}
        title={inspectedActor === 'enemy' ? enemy.name : playerName}
        visual={inspectedActor === 'enemy' ? enemy.visual : null}
      />
    </div>
  )
})
