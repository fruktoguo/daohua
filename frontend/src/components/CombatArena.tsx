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
import { VectorCharacter, resolveCharacterKind } from './VectorCharacter'

interface ResourceEffect {
  id: number
  delta: number
  left: number
  width: number
  lane: number
}

type ActorMotionKind = 'attack' | 'cast'

interface ActorMotion {
  id: number
  kind: ActorMotionKind
}

interface ActorImpact {
  id: number
  distance: number
  opacity: number
}

// 单次损失低于最大值 2% 时不产生断裂碎片(如持续伤害的小额跳数)
const resourceShardMinimumRatio = 0.02

const actorMotionLeadMilliseconds: Record<ActorMotionKind, number> = {
  attack: 170,
  cast: 300,
}

const actorMotionDurationMilliseconds: Record<ActorMotionKind, number> = {
  attack: 520,
  cast: 720,
}

function ArenaResource({
  actorKey,
  value,
  max,
  tone,
}: {
  actorKey: string
  value: number
  max: number
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

function SmoothActionFill({ action }: { action?: CombatActionState | null }): React.JSX.Element {
  const fillRef = useRef<HTMLElement>(null)
  const animationRef = useRef<Animation | null>(null)
  const serverTimeUnixMilliseconds = useGameStore(state => state.serverTimeUnixMilliseconds)
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
    const localClockOffset = Math.abs(serverTimeUnixMilliseconds - Date.now())
    const canUseAbsoluteDeadline = action.completesAtUnixMilliseconds != null && localClockOffset <= 5_000
    const remainingAtRender = canUseAbsoluteDeadline
      ? Math.max(0, Math.min(duration, action.completesAtUnixMilliseconds! - Date.now()))
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
    serverTimeUnixMilliseconds,
  ])

  useEffect(() => () => animationRef.current?.cancel(), [])

  return <b ref={fillRef} />
}

function ActionCountdownBadge({
  action,
  role,
}: {
  action?: CombatActionState | null
  role: 'player' | 'enemy'
}): React.JSX.Element | null {
  const t = useT()
  const [displayText, setDisplayText] = useState<string>('')
  const serverTimeUnixMilliseconds = useGameStore(state => state.serverTimeUnixMilliseconds)

  useEffect(() => {
    // 怪物待机或无动作时：显示纯净空条，不渲染文字
    if (!action || action.durationMilliseconds <= 0 || action.name === '待机' || !action.name.trim()) {
      if (role === 'player' && action?.name === '调息中') {
        setDisplayText(t('combat.arena.regenerating'))
      } else {
        setDisplayText('')
      }
      return
    }

    let animationFrameId: number
    const duration = Math.max(1, action.durationMilliseconds)
    let baseName = action.name.replace(/回气$/, '').replace(/\s*\([\d.]+s\/[\d.]+s\)/, '').trim()
    if (baseName === '灵击' || baseName === '攻击') {
      baseName = t('combat.arena.basicAttack')
    }
    const relativeElapsed = Math.max(0, Math.min(duration, action.elapsedMilliseconds))
    const localClockOffset = Math.abs(serverTimeUnixMilliseconds - Date.now())
    const canUseAbsoluteDeadline = action.completesAtUnixMilliseconds != null && localClockOffset <= 5_000
    const startPerfTime = performance.now()
    const initialRemaining = canUseAbsoluteDeadline
      ? Math.max(0, Math.min(duration, action.completesAtUnixMilliseconds! - Date.now()))
      : Math.max(0, duration - relativeElapsed)

    const tick = () => {
      const currentRemaining = canUseAbsoluteDeadline
        ? Math.max(0, action.completesAtUnixMilliseconds! - Date.now())
        : Math.max(0, initialRemaining - (performance.now() - startPerfTime))

      const remainingSeconds = Math.max(0, currentRemaining / 1000)
      const formatted = t('combat.arena.countdown', { name: baseName, seconds: remainingSeconds.toFixed(1) })
      setDisplayText(formatted)

      if (currentRemaining > 0) {
        animationFrameId = requestAnimationFrame(tick)
      }
    }

    tick()

    return () => {
      cancelAnimationFrame(animationFrameId)
    }
  }, [
    action?.completesAtUnixMilliseconds,
    action?.durationMilliseconds,
    action?.elapsedMilliseconds,
    action?.name,
    role,
    t,
    serverTimeUnixMilliseconds,
  ])

  if (!displayText) return null
  return <span className="arena-action-badge">{displayText}</span>
}

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

function ArenaBuffSquare({ buff }: { buff: Buff }): React.JSX.Element {
  const t = useT()
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const remaining = Math.max(0, buff.expiresAtUnixSeconds - serverTime)
  const circleRef = useRef<SVGCircleElement>(null)
  const initialDurationRef = useRef<number>(Math.max(10, remaining))

  if (remaining > initialDurationRef.current) {
    initialDurationRef.current = remaining
  }
  const total = initialDurationRef.current

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

function ArenaPhaseState({ expedition }: { expedition: ExpeditionProgress }): React.JSX.Element {
  const t = useT()
  const isInitialSearch = expedition.phase === 'initial-search'
  const isDeath = expedition.phase === 'death'

  const title = isDeath
    ? t('combat.arena.phaseDeath')
    : isInitialSearch
      ? t('combat.arena.phaseSearch')
      : t('combat.arena.phaseHunt')

  const sigil = isDeath ? t('combat.arena.sigilDeath') : isInitialSearch ? t('combat.arena.sigilSearch') : t('combat.arena.sigilHunt')
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
  const renderer = visual?.renderer ?? 'vector'
  const imageSource = renderer === 'image' ? visual?.source : visual?.poster
  const [failedSource, setFailedSource] = useState<string | null>(null)
  if (imageSource && failedSource !== imageSource) {
    return <img alt="" data-renderer={renderer} draggable={false} onError={() => setFailedSource(imageSource)} src={imageSource} />
  }
  if (renderer === 'text' && visual?.source && visual.source.length > 2) {
    return <span className="arena-portrait-text">{visual.source || fallback}</span>
  }
  const kind = resolveCharacterKind(role, actorId ?? (visual?.source || null), name)
  return <VectorCharacter isDead={isDead} kind={kind} role={role} />
}

function resolveActorMotionKind(action?: CombatActionState | null): ActorMotionKind | null {
  if (action?.kind === 'cast') return 'cast'
  if (action?.kind === 'attack') return 'attack'
  return null
}

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

function ArenaLifeGoalCard({
  queueEntry,
  actionDef,
  onStop,
}: {
  queueEntry?: QueueEntry | null
  actionDef?: ActionDefinition | null
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
  const repeats = isInfinite ? -1 : remainingRepeats + 1

  // 目标产物分析
  const outputs = actionDef?.outputs || {}
  const outputEntries = Object.entries(outputs)
  const primaryOutput = outputEntries[0]
  const primaryItemId = primaryOutput ? primaryOutput[0] : null
  const primaryAmountPerBatch = primaryOutput ? primaryOutput[1] : 1
  const totalTargetDisplay = isInfinite ? '∞' : formatNumber(primaryAmountPerBatch * repeats)

  const itemMeta = primaryItemId ? resolveItemMetadata(primaryItemId) : null
  const itemDefinition = primaryItemId ? itemCatalog.find(i => i.itemId === primaryItemId) : null
  const itemName = itemDefinition?.name || itemMeta?.label || (primaryItemId === 'resource.qi' ? t('combat.life.qiName') : t('combat.life.fallbackItemName'))
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
              {totalTargetDisplay} {primaryItemId === 'resource.qi' ? t('combat.life.unitQi') : t('combat.life.unitItem')}
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

interface AttributeRow {
  label: string
  value: string
}

function CharacterAttributeModal({
  baseRows,
  buffs,
  onClose,
  open,
  specialRows,
  title,
}: {
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
      <header className="character-attribute-header"><h2 id={titleId}>{title}</h2></header>
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
  const isLifeAction = current !== null && !isCombat
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

  const fallbackMaxHp = 100 + (combat?.staminaLevel ?? 1) * 12
  const fallbackMaxMp = 100 + (combat?.intelligenceLevel ?? 1) * 10
  const playerMaxHp = expedition?.playerMaxHitpoints ?? combatResources?.maxHitpoints ?? fallbackMaxHp
  const playerMaxMp = expedition?.playerMaxManapoints ?? combatResources?.maxManapoints ?? fallbackMaxMp
  const playerHp = (isCombat ? expedition?.playerHitpoints : undefined) ?? combatResources?.hitpoints ?? playerMaxHp
  const playerMp = (isCombat ? expedition?.playerManapoints : undefined) ?? combatResources?.manapoints ?? playerMaxMp
  const phase = expedition?.phase ?? (isLifeAction ? 'life-action' : 'idle')
  const enemyVisible = isCombat && (phase === 'combat' || phase === 'death' || ((expedition?.enemyHitpoints ?? 0) > 0))

  const playerAction: CombatActionState | null = useMemo(() => {
    if (phase === 'death') {
      const durMs = Math.max(1, (expedition?.durationSeconds ?? 1) * 1000)
      const baseElMs = Math.max(0, (expedition?.elapsedSeconds ?? ((expedition?.progress ?? 0) * (expedition?.durationSeconds ?? 1))) * 1000)
      const elapsedSinceSync = Math.max(0, performance.now() - lastSyncPerformanceTime)
      const currentElMs = Math.min(durMs, baseElMs + elapsedSinceSync)
      return {
        kind: 'wait',
        name: t('combat.arena.phaseDeath'),
        durationMilliseconds: durMs,
        elapsedMilliseconds: currentElMs,
        completesAtUnixMilliseconds: Date.now() + Math.max(0, durMs - currentElMs),
      }
    }
    if (isCombat) {
      return expedition?.playerAction ?? null
    }
    if (isLifeAction && current) {
      const durMs = Math.max(1, current.durationSeconds * 1000)
      const baseElMs = Math.max(0, current.progressSeconds * 1000)
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
  }, [current, expedition, isCombat, isLifeAction, lastSyncPerformanceTime, phase, t])

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
      action: phase === 'death' ? null : expedition?.enemyAction,
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
    { label: t('combat.attr.waterRes'), value: formatNumber(combatAttributes?.waterResistance ?? 0) },
    { label: t('combat.attr.natureRes'), value: formatNumber(combatAttributes?.natureResistance ?? 0) },
    { label: t('combat.attr.fireRes'), value: formatNumber(combatAttributes?.fireResistance ?? 0) },
    { label: t('combat.attr.attackInterval'), value: t('combat.attr.seconds', { n: formatNumber((combatAttributes?.attackIntervalMilliseconds ?? 0) / 1000) }) },
  ]
  const playerSpecialCandidates: Array<[string, number, 'percent' | 'number' | 'regen' | 'ratio_regen']> = [
    [t('combat.special.tenacity'), combatAttributes?.tenacity ?? 0, 'number'],
    [t('combat.special.hpRegen'), combatAttributes?.hpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.mpRegen'), combatAttributes?.mpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.penetration'), combatAttributes?.penetrationRatio ?? 0, 'percent'],
    [t('combat.special.pierceChance'), combatAttributes?.pierceChance ?? 0, 'percent'],
    [t('combat.special.criticalChance'), combatAttributes?.criticalChance ?? 0, 'percent'],
    [t('combat.special.criticalDamage'), combatAttributes?.criticalDamageRatio ?? 0, 'percent'],
    [t('combat.special.criticalAvoidance'), combatAttributes?.criticalAvoidance ?? 0, 'percent'],
    [t('combat.special.damageAbsorption'), combatAttributes?.damageAbsorptionRatio ?? 0, 'percent'],
    [t('combat.special.curse'), combatAttributes?.curseRatio ?? 0, 'percent'],
    [t('combat.special.fury'), combatAttributes?.furyRatio ?? 0, 'percent'],
    [t('combat.special.weaken'), combatAttributes?.weakenRatio ?? 0, 'percent'],
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
    { label: t('combat.attr.waterRes'), value: formatNumber(enemyDefinition?.waterResistance ?? 0) },
    { label: t('combat.attr.natureRes'), value: formatNumber(enemyDefinition?.natureResistance ?? 0) },
    { label: t('combat.attr.fireRes'), value: formatNumber(enemyDefinition?.fireResistance ?? 0) },
  ]
  const enemySpecialCandidates: Array<[string, number, 'percent' | 'number' | 'milliseconds' | 'ratio_regen']> = [
    [t('combat.special.tenacity'), enemyDefinition?.tenacity ?? 0, 'number'],
    [t('combat.special.hpRegen'), enemyDefinition?.hpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.mpRegen'), enemyDefinition?.mpRegenPerTenSeconds ?? 0, 'ratio_regen'],
    [t('combat.special.pierceChance'), enemyDefinition?.pierceChance ?? 0, 'percent'],
    [t('combat.special.curse'), enemyDefinition?.curseRatio ?? 0, 'percent'],
    [t('combat.special.fury'), enemyDefinition?.furyRatio ?? 0, 'percent'],
    [t('combat.special.weaken'), enemyDefinition?.weakenRatio ?? 0, 'percent'],
    [t('combat.special.lifeSteal'), enemyDefinition?.lifeStealRatio ?? 0, 'percent'],
    [t('combat.special.armorDamage'), enemyDefinition?.armorDamageRatio ?? 0, 'percent'],
    [t('combat.special.hpDrain'), enemyDefinition?.hpDrainRatio ?? 0, 'percent'],
    [t('combat.special.criticalChance'), enemyDefinition?.criticalChance ?? 0, 'percent'],
    [t('combat.special.criticalDamage'), enemyDefinition?.criticalDamageRatio ?? 0, 'percent'],
    [t('combat.special.criticalAvoidance'), enemyDefinition?.criticalAvoidance ?? 0, 'percent'],
    [t('combat.special.damageAbsorption'), enemyDefinition?.damageAbsorptionRatio ?? 0, 'percent'],
    [t('combat.special.enragePeriod'), enemyDefinition?.enrageTimeMilliseconds ?? 0, 'milliseconds'],
  ]
  const enemySpecialRows = enemySpecialCandidates
    .filter(([, value]) => Math.abs(value) > 0.000001)
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
                    action={phase === 'death' ? null : enemy.action}
                    actorId={activeEncounter?.enemy?.enemyId ?? activeEncounterId}
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
          ) : isLifeAction ? (
            <ArenaLifeGoalCard
              actionDef={activeAction}
              onStop={() => gameClient.sendCommand('remove_action', { entryId: current.entryId })}
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
            onClick={() => gameClient.sendCommand('remove_action', { entryId: current.entryId })}
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
        baseRows={inspectedActor === 'enemy' ? enemyBaseRows : playerBaseRows}
        buffs={inspectedActor === 'player' ? buffs : []}
        onClose={() => setInspectedActor(null)}
        open={inspectedActor !== null}
        specialRows={inspectedActor === 'enemy' ? enemySpecialRows : playerSpecialRows}
        title={inspectedActor === 'enemy' ? enemy.name : playerName}
      />
    </div>
  )
})
