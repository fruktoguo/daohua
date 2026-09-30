import { memo, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties } from 'react'
import { formatInteger, formatNumber } from '../game/format'
import { UnitNumber } from './UnitNumber'
import { useGameStore } from '../game/gameStore'
import { findAbilityInCatalog, findCombatantInCatalog, findItemInCatalog, localizeActionDisplayText, resolveActionDefinition, resolveItemMetadata } from '../game/ids'
import type { ActionDefinition, Buff, CharacterVisual, CombatAbilityDefinition, CombatActionState, CombatAttributes, ExpeditionDuel, ExpeditionEnemy, ExpeditionProgress, MonsterDropDefinition, QueueEntry, WorldMapDefinition, WorldState } from '../game/types'
import { gameClient } from '../network/gameClient'
import { getT, useT } from '../i18n'
import { Modal } from './Modal'
import { useSmoothProgressRef } from '../game/useSmoothProgress'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { CharacterAvatar } from './CharacterAvatar'
import { CombatAttributePanel } from './CombatAttributePanel'
import { describeBuffEffectLines } from './ItemDisplay'
import { useCombatVfx } from '../vfx/useCombatVfx'

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

/** 资源条数值变动特效记录（跳字与血条破裂碎片） */
interface ResourceEffect {
  id: number
  /** 本次数值增量（负值为受损） */
  delta: number
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
}

/** 实体受击震颤与冲击特效状态 */
interface ActorImpact {
  id: number
  variant: typeof actorImpactVariants[number]
  distance: number
  opacity: number
}

const actorImpactVariants = ['cleave', 'ripple', 'splinter'] as const

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
            <UnitNumber disableTooltip maxDecimals={0} value={normalizedValue} /> / <UnitNumber disableTooltip maxDecimals={0} value={normalizedMax} />
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
              {effect.delta > 0 ? '+' : ''}{formatInteger(effect.delta)}
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
      // elapsed 明显回退说明同一行动进入了新一轮出手，必须重建动画；
      // 否则在 ±250ms 估算误差内沿用旧动画，避免每次快照同步都重启 WAAPI 造成进度条抖动。
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
    let animationFrameId = 0
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
  const isRevivalAction = action?.kind === 'respawn'
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

  // 效果说明与单字图标：效果行统一由唯一 Buff 形状（hpRegen/mpRegen/attackSpeedPoints/
  // damageAbsorptionPoints/statRatios）解析；色调按结算语义粗分，字形取 Buff 名首字。
  const effectLines = describeBuffEffectLines(buff)
  const effectDesc = effectLines.length > 0 ? effectLines.join('；') : t('combat.arena.buffGeneric')
  const tone: 'qi' | 'jade' | 'purple' | 'danger' | 'gold' =
    buff.damageAbsorptionPoints > 0 ? 'jade' : (buff.hpRegen > 0 || buff.mpRegen > 0) ? 'qi' : 'gold'
  const glyph = buff.attackSpeedPoints > 0
    ? t('combat.arena.glyphSpeed')
    : buff.name.slice(0, 1) || t('combat.arena.glyphGeneric')

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
    const signature = `${expedition.phase}|${expedition.waveNumber ?? 0}|${duration}`
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
  }, [expedition.waveNumber, expedition.durationSeconds, expedition.elapsedSeconds, expedition.phase, expedition.progress, lastSyncPerformanceTime, orientation])

  useEffect(() => () => animationRef.current?.cancel(), [])

  return <i ref={fillRef} />
}

/**
 * 战场中心历练/搜寻阶段状态告示牌组件
 */
function ArenaPhaseState({ expedition }: { expedition: ExpeditionProgress }): React.JSX.Element {
  const t = useT()
  const isInitialSearch = expedition.phase === 'initial-search'

  const title = expedition.phase === 'respawn' ? t('combat.arena.phaseDeath') : isInitialSearch
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
  action?: CombatActionState | null
  buffs?: readonly Buff[]
  /** 点击/回车唤起属性详情模态窗回调 */
  onInspect: () => void
  visual?: CharacterVisual | null
  /** 非激活态（搜寻阶段敌方未入场）时置灰休眠且不可聚焦 */
  visible?: boolean
  compact?: boolean
}): React.JSX.Element {
  const t = useT()
  const nextMotionId = useRef(1)
  const nextImpactSequence = useRef(0)
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
      nextImpactSequence.current = 0
      if (clearImpactTimer.current !== null) window.clearTimeout(clearImpactTimer.current)
      clearImpactTimer.current = null
      setImpact(null)
      return
    }

    if (hp < previous.hp) {
      const strength = Math.max(0.25, Math.min(1, (previous.hp - hp) / Math.max(1, maxHp)))
      const nextImpact = {
        id: nextMotionId.current++,
        variant: actorImpactVariants[nextImpactSequence.current++ % actorImpactVariants.length],
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
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const isAlchemy = queueEntry?.type === 'alchemy'
  const isCultivation = queueEntry?.type === 'cultivation'
  const isExploration = queueEntry?.type === 'exploration'

  const title = isAlchemy ? t('combat.life.titleAlchemy') : isCultivation ? t('combat.life.titleCultivation') : isExploration ? t('combat.life.titleExploration') : t('combat.life.titleDefault')
  const actionName = resolveQueueEntryDisplayName(queueEntry, actionDef?.name, world, maps) || actionDef?.name || t('combat.life.fallbackActionName')
  const isInfinite = (queueEntry?.remainingRepeats ?? 0) < 0
  const remainingRepeats = queueEntry?.remainingRepeats ?? 0
  const repeats = isInfinite ? -1 : remainingRepeats

  // 目标产物分析（取 yields 中首个物品收益）
  const primaryYield = actionDef?.yields?.find(yieldEntry => yieldEntry.kind === 'item' && yieldEntry.id)
  const primaryItemId = primaryYield?.id ?? null
  const primaryAmountPerBatch = primaryYield?.amount ?? 1
  const totalTargetDisplay = isInfinite ? '∞' : formatNumber(primaryAmountPerBatch * repeats)

  const itemMeta = primaryItemId ? resolveItemMetadata(primaryItemId) : null
  const itemDefinition = primaryItemId ? findItemInCatalog(itemCatalog, primaryItemId) : null
  const itemName = itemDefinition?.name || itemMeta?.label || (isCultivation ? t('combat.life.qiName') : t('combat.life.fallbackItemName'))
  const itemGlyph = itemMeta?.glyph || (isAlchemy ? '⚗️' : isCultivation ? '☯' : '🌿')
  const itemTone = itemMeta?.tone || 'gold'

  // 所需原料分析
  const inputs = actionDef?.inputs || {}
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
  const world = useGameStore(state => state.world)
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
        ? <CombatAttributePanel attributes={attributes} buffs={buffs} drops={drops} />
        : <p className="nearby-empty">{t('insight.combat.attributesLoading')}</p>}
    </Modal>
  )
}

/** 敌方单位铺位顺序：纵深优先（前排中→中排中→后排中→前排上→…），同波敌人沿前后轴展开而非上下堆叠 */
const ENEMY_DEPTH_SLOTS: readonly number[] = [1, 4, 7, 2, 5, 8, 3, 6, 9]

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
  action?: CombatActionState | null
  visual?: CharacterVisual | null
  /** 服务端按需投影的完整战斗属性（详情弹窗唯一数值来源；未登记观察目标时为 null） */
  attributes?: CombatAttributes | null
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
  const combatResources = useGameStore(state => state.combatResources)
  const combatAttributes = useGameStore(state => state.combatAttributes)
  const abilities = useGameStore(state => state.abilities)
  const buffs = useGameStore(state => state.buffs)
  const actions = useGameStore(state => state.actions)
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const encounters = useGameStore(state => state.encounters)
  const queue = useGameStore(state => state.queue.entries)
  // 队列进度的采样基准：必须与非战斗进度条（useSmoothProgressRef）完全一致，
  // 否则角色行动条与右侧行动卡片会因「非队列补丁」（入灵气、掉落等）产生 0~1 秒的显示差。
  const lastQueueSyncPerformanceTime = useGameStore(state => state.lastQueueSyncPerformanceTime ?? state.lastSyncPerformanceTime)
  // 详情弹窗目标：'player' 或 `enemy:{下标}`（同波多敌人逐只可查）。
  const [inspectedActor, setInspectedActor] = useState<string | null>(null)
  // 单位属性缓存：敌方/镜像属性为定义级静态值，服务端仅在 UnitInspect 登记后一次性交付；
  // 按身份缓存使重复点开即显，送达的新值由下方 effect 静默覆盖，key 为 enemy:{enemyId} / duel:{对手名}。
  const inspectedAttributesCacheRef = useRef(new Map<string, CombatAttributes>())
  const t = useT()

  // 观察目标同步（UnitInspect 火忘帧）：敌方属性面板为按需投影，点开详情才向服务端登记；
  // 选玩家/关闭弹窗即清除，未点开的单位不占快照带宽；组件常驻战斗页，会话断开自然失效。
  useEffect(() => {
    const match = inspectedActor?.match(/^enemy:(\d+)$/)
    gameClient.sendUnitInspect(match ? { side: 'enemy', index: Number(match[1]) } : null)
  }, [inspectedActor])

  // 战斗特效：overlay 覆盖层 + 模块化 VFX 运行时（演出调度与技能模块注册全部收拢在 src/vfx/）。
  const arenaShellRef = useRef<HTMLDivElement | null>(null)
  const vfxOverlayRef = useRef<HTMLDivElement | null>(null)
  useCombatVfx(arenaShellRef, vfxOverlayRef)

  const current = queue[0] ?? null
  const isCombat = current?.type === 'combat' && current.executionKind === 'combat-encounter'
  const isTravel = current !== null && (current.executionKind === 'travel' || current.actionId === 'world.travel')
  const isLifeAction = current !== null && !isCombat && !isTravel
  const expedition = isCombat ? current.expedition : null
  // 一次性交付落账：wire 携带属性时按身份写入缓存，后续快照不再携带也能稳定显示。
  useEffect(() => {
    const cache = inspectedAttributesCacheRef.current
    for (const enemy of expedition?.enemies ?? []) {
      if (enemy.attributes) cache.set(`enemy:${enemy.enemyId}`, enemy.attributes)
    }
    if (expedition?.duel?.opponentAttributes && expedition.duel.opponentName)
      cache.set(`duel:${expedition.duel.opponentName}`, expedition.duel.opponentAttributes)
  }, [expedition])
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
        enemyId: null,
        name,
        seal: name.slice(0, 1),
        hp,
        maxHp,
        mp,
        maxMp,
        action: duelActionState(expedition.duel, battleElapsed, !expedition.duel?.isInitiator, 2000, serverNow, abilities),
        visual: null,
        attributes: opponentAttributes,
      }]
    }
    const wave = expedition?.enemies ?? []
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
        action: entry.hitpoints > 0 && expedition?.phase === 'combat' && expedition.playerRespawnAtSeconds == null
          ? enemyActionState(entry, battleElapsed, serverNow, abilities)
          : null,
        visual: entry.visual ?? null,
        attributes: entry.attributes ?? null,
      }
    })
  }, [abilities, expedition?.enemies, expedition?.waveNumber, expedition?.phase, expedition?.duel, expedition?.battleElapsedSeconds, t])

  const inspectedEnemyIndex = inspectedActor?.startsWith('enemy:')
    ? Number(inspectedActor.slice(6))
    : null
  const inspectedEnemy = inspectedEnemyIndex !== null && Number.isInteger(inspectedEnemyIndex)
    ? enemyEntries[inspectedEnemyIndex] ?? null
    : null
  const enemyVisible = isCombat && (phase === 'combat' || isDuelPhase || enemyEntries.some(entry => entry.hp > 0))
  // 空槽只补纵深占位之外的格子：敌怪按 ENEMY_DEPTH_SLOTS 摆位（2 个怪占 1/4 号槽），
  // 若按顺序编号补空会叠进实体槽位，透明空槽在 DOM 后层拦截点击，导致部分怪点不中。
  const occupiedEnemySlots = new Set(ENEMY_DEPTH_SLOTS.slice(0, enemyEntries.length))

  const playerAction: CombatActionState | null = useMemo(() => {
    if (isCombat) {
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
      const durSec = (isTravel && current.expedition?.durationSeconds) ? current.expedition.durationSeconds : current.durationSeconds
      const durMs = Math.max(1, durSec * 1000)
      const baseElSec = (isTravel && current.expedition?.elapsedSeconds !== undefined) ? current.expedition.elapsedSeconds : current.progressSeconds
      const baseElMs = Math.max(0, baseElSec * 1000)
      const isRunning = current.status === 'running'
      const elapsedSinceSync = isRunning ? Math.max(0, performance.now() - lastQueueSyncPerformanceTime) : 0
      const currentElMs = Math.min(durMs, baseElMs + elapsedSinceSync)
      return {
        kind: 'cast',
        name: resolveQueueEntryDisplayName(current, current.actionName, world, maps),
        durationMilliseconds: durMs,
        elapsedMilliseconds: currentElMs,
        completesAtUnixMilliseconds: Date.now() + Math.max(0, durMs - currentElMs),
      }
    }
    return null
  }, [current, expedition, isCombat, isLifeAction, isTravel, lastQueueSyncPerformanceTime, combatAttributes, combatResources, abilities, world, maps, t])

  // 仅在玩家显式点击头像唤起详情弹窗 (inspectedActor !== null) 时取用属性；
  // 角色/敌怪/斗法镜像全部渲染服务端下发的 CombatAttributes 终值，无任何本地估算。
  // 敌方属性为一次性交付：wire 未携带时回退身份缓存（重复点开即显，命中缓存不触发加载态）。
  const inspectedAttributes: CombatAttributes | null = inspectedActor === null
    ? null
    : inspectedEnemy
      ? inspectedEnemy.attributes
        ?? inspectedAttributesCacheRef.current.get(
          inspectedEnemy.enemyId !== null
            ? `enemy:${inspectedEnemy.enemyId}`
            : `duel:${expedition?.duel?.opponentName ?? 'opponent'}`)
        ?? null
      : combatAttributes ?? null

  // 敌怪掉落明细：静态配表查询（与生态面板同根同源）；斗法镜像 enemyId=null 不解析。
  const inspectedDrops = useMemo<readonly MonsterDropDefinition[] | undefined>(() => {
    if (inspectedEnemy?.enemyId == null) return undefined
    const combatant = findCombatantInCatalog(encounters, maps, inspectedEnemy.enemyId)
    return combatant?.drops ?? []
  }, [inspectedEnemy?.enemyId, encounters, maps])

  return (
    <div className="combat-arena-shell" ref={arenaShellRef}>
      {/* 技能特效覆盖层：绝对定位铺满战场，粒子节点全部渲染于此，零回流不参战布局 */}
      <div className="vfx-stage-overlay" ref={vfxOverlayRef} aria-hidden="true" />
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
        {isCombat && (phase === 'initial-search' || phase === 'search' || phase === 'respawn') && expedition && (
          <div className="arena-center-stage">
            <ArenaPhaseState expedition={expedition} />
          </div>
        )}

        {/* 右侧：敌方 3x3 阵型 (左为前排，右为后排，1号位在前排正中靠近己方) OR 生活行动总目标 OR 空闲状态 */}
        <div className="arena-side arena-side--enemies">
          {isCombat ? (
            enemyVisible ? (
              <div className="arena-formation arena-formation--enemies">
                {enemyEntries.map((entry, index) => (
                  <div
                    className={`arena-slot arena-slot--occupied arena-slot--slot-${ENEMY_DEPTH_SLOTS[Math.min(index, ENEMY_DEPTH_SLOTS.length - 1)]}${entry.hp <= 0 ? ' arena-slot--slain' : ''}`}
                    key={`enemy-slot-${index + 1}`}
                  >
                    <ArenaActor
                      action={entry.action}
                      actorId={entry.enemyId ?? activeEncounterId}
                      actorKey={entry.actorKey}
                      compact
                      hp={entry.hp}
                      maxHp={entry.maxHp}
                      maxMp={entry.maxMp}
                      mp={entry.mp}
                      name={entry.name}
                      onInspect={() => setInspectedActor(`enemy:${index}`)}
                      role="enemy"
                      seal={entry.seal}
                      visual={entry.visual}
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
            {t('combat.arena.stopCurrent', { name: resolveQueueEntryDisplayName(current, current.actionName, world, maps) })}
          </button>
        ) : availableActions.length > 0 ? (
          availableActions.map(action => (
            <button key={action.id} onClick={() => onOpenAction(action)} type="button">
              {localizeActionDisplayText(action.name, world, maps)}
            </button>
          ))
        ) : null}
      </div>
      <CharacterAttributeModal
        actorId={inspectedEnemy ? (inspectedEnemy.enemyId ?? activeEncounterId) : 'player'}
        attributes={inspectedAttributes}
        buffs={inspectedActor === 'player' ? buffs : []}
        drops={inspectedDrops}
        name={inspectedEnemy?.name ?? playerName}
        onClose={() => setInspectedActor(null)}
        open={inspectedActor !== null}
        role={inspectedEnemy ? 'enemy' : 'player'}
        title={inspectedEnemy?.name ?? playerName}
        visual={inspectedEnemy?.visual ?? null}
      />
    </div>
  )
})
