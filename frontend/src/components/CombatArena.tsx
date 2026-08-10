import { memo, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import type { ActionDefinition, Buff, CharacterVisual, CombatActionState, CombatantDefinition, ExpeditionProgress } from '../game/types'
import { gameClient } from '../network/gameClient'
import { Modal } from './Modal'
import { ValueText } from './ValueText'

interface ResourceEffect {
  id: number
  delta: number
  left: number
  width: number
}

function ArenaResource({
  actorKey,
  label,
  value,
  max,
  tone,
}: {
  actorKey: string
  label: string
  value: number
  max: number
  tone: 'hp' | 'mp'
}): React.JSX.Element {
  const normalizedMax = Math.max(1, max)
  const normalizedValue = Math.max(0, Math.min(normalizedMax, value))
  const previousRef = useRef({ actorKey, max: normalizedMax, value: normalizedValue })
  const nextEffectId = useRef(1)
  const timers = useRef<number[]>([])
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
    const effect: ResourceEffect = {
      id: nextEffectId.current++,
      delta,
      left: Math.min(previous.value, normalizedValue) / normalizedMax,
      width: Math.abs(delta) / normalizedMax,
    }
    previousRef.current = { actorKey, max: normalizedMax, value: normalizedValue }
    setEffects(current => [...current.slice(-5), effect])
    const timer = window.setTimeout(() => {
      setEffects(current => current.filter(item => item.id !== effect.id))
    }, 900)
    timers.current.push(timer)
  }, [actorKey, normalizedMax, normalizedValue])

  useEffect(() => () => {
    timers.current.forEach(timer => window.clearTimeout(timer))
  }, [])

  return (
    <div className={`arena-resource arena-resource--${tone}`}>
      <span>{label}</span>
      <div className="arena-resource-meter">
        <div className="arena-resource-track">
          <i style={{ transform: `scaleX(${normalizedValue / normalizedMax})` }} />
          {effects.filter(effect => effect.delta < 0).map(effect => (
            <b
              className="arena-resource-slice"
              key={effect.id}
              style={{ left: `${effect.left * 100}%`, width: `${effect.width * 100}%` }}
            />
          ))}
        </div>
        <span className="arena-resource-floats" aria-live="polite">
          {effects.map(effect => (
            <em className={effect.delta > 0 ? 'is-positive' : 'is-negative'} key={effect.id}>
              {effect.delta > 0 ? '+' : ''}{formatNumber(effect.delta)}
            </em>
          ))}
        </span>
      </div>
      <strong>{formatNumber(normalizedValue)} / {formatNumber(normalizedMax)}</strong>
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

function ArenaActionBar({ action, role }: { action?: CombatActionState | null; role: 'player' | 'enemy' }): React.JSX.Element {
  return (
    <div className={`arena-action-bar arena-action-bar--${role} arena-action-bar--${action?.kind ?? 'wait'}`}>
      <strong>{action?.name ?? '\u00a0'}</strong>
      <i><SmoothActionFill action={action} /></i>
    </div>
  )
}

function ArenaPhaseFill({ expedition }: { expedition: ExpeditionProgress }): React.JSX.Element {
  const fillRef = useRef<HTMLElement>(null)
  const animationRef = useRef<Animation | null>(null)
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
    const elapsed = Math.max(0, Math.min(duration, expedition.progress * duration))
    const signature = `${expedition.phase}|${expedition.battleSequence ?? 0}|${duration}`
    const now = performance.now()
    const anchor = anchorRef.current
    if (anchor?.signature === signature) {
      const expected = Math.min(anchor.duration, anchor.elapsed + now - anchor.startedAt)
      if (Math.abs(elapsed - expected) <= 300) return
    }

    animationRef.current?.cancel()
    const fraction = elapsed / duration
    fill.style.transform = `scaleX(${fraction})`
    anchorRef.current = { signature, elapsed, duration, startedAt: now }
    const remaining = duration - elapsed
    if (remaining <= 0 || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    animationRef.current = fill.animate(
      [{ transform: `scaleX(${fraction})` }, { transform: 'scaleX(1)' }],
      { duration: remaining, easing: 'linear', fill: 'forwards' },
    )
  }, [expedition.battleSequence, expedition.durationSeconds, expedition.phase, expedition.progress])

  useEffect(() => () => animationRef.current?.cancel(), [])

  return <i ref={fillRef} />
}

function ArenaPhaseState({ expedition }: { expedition: ExpeditionProgress }): React.JSX.Element {
  const isInitialSearch = expedition.phase === 'initial-search'
  const isDeath = expedition.phase === 'death'
  const label = isDeath ? '复活中' : isInitialSearch ? '寻找敌人' : '重新搜寻'
  const sigil = isDeath ? '生' : isInitialSearch ? '寻' : '觅'
  return (
    <div className={`arena-search-state arena-search-state--${expedition.phase}`} role="status" aria-label={label}>
      <span className="arena-search-sigil" aria-hidden="true"><i /><i /><b>{sigil}</b></span>
      <strong>{label}</strong>
      {!isDeath && <span>{expedition.enemyName ?? '未知敌群'}</span>}
      <div className="arena-search-progress"><ArenaPhaseFill expedition={expedition} /></div>
      <span>{expedition.elapsedSeconds}s / {expedition.durationSeconds}s</span>
    </div>
  )
}

function ArenaPortrait({ fallback, visual }: { fallback: string; visual?: CharacterVisual | null }): React.JSX.Element {
  const renderer = visual?.renderer ?? 'text'
  const imageSource = renderer === 'image' ? visual?.source : visual?.poster
  const [failedSource, setFailedSource] = useState<string | null>(null)
  if (imageSource && failedSource !== imageSource) {
    return <img alt="" data-renderer={renderer} draggable={false} onError={() => setFailedSource(imageSource)} src={imageSource} />
  }
  return <span>{renderer === 'text' && visual?.source ? visual.source : fallback}</span>
}

function ArenaActor({
  actorKey,
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
}: {
  actorKey: string
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
}): React.JSX.Element {
  return (
    <article
      aria-label={`查看${name || '角色'}属性`}
      className={`arena-actor arena-actor--${role}${visible ? '' : ' is-dormant'}${hp <= 0 ? ' is-dead' : ''}`}
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
          <ArenaResource actorKey={actorKey} label="生命" max={maxHp} tone="hp" value={hp} />
          <ArenaResource actorKey={actorKey} label="灵力" max={maxMp} tone="mp" value={mp} />
        </div>
        <div className="arena-actor-buffs" aria-label="当前效果">
          {buffs.slice(0, 4).map(buff => (
            <span key={buff.buffId}>{buff.name}{buff.stacks > 1 ? ` ${buff.stacks}` : ''}</span>
          ))}
        </div>
      </header>
      <div className="arena-avatar" aria-label={`${name}形象`}>
        <ArenaPortrait fallback={seal} visual={visual} />
      </div>
      <ArenaActionBar action={action} role={role} />
    </article>
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
  const titleId = useId()
  return (
    <Modal className="character-attribute-modal" labelledBy={titleId} onClose={onClose} open={open} size="medium">
      <header className="character-attribute-header"><h2 id={titleId}>{title}</h2></header>
      <section className="character-attribute-section">
        <h3>基础属性</h3>
        <dl>{baseRows.map(row => <div key={row.label}><dt>{row.label}</dt><dd><ValueText text={row.value} /></dd></div>)}</dl>
      </section>
      {specialRows.length > 0 && (
        <section className="character-attribute-section">
          <h3>特殊属性</h3>
          <dl>{specialRows.map(row => <div key={row.label}><dt>{row.label}</dt><dd><ValueText text={row.value} /></dd></div>)}</dl>
        </section>
      )}
      {buffs.length > 0 && (
        <section className="character-attribute-section">
          <h3>当前效果</h3>
          <div className="character-buff-list">
            {buffs.map(buff => <span key={buff.buffId}>{buff.name}{buff.stacks > 1 ? ` ×${buff.stacks}` : ''}</span>)}
          </div>
        </section>
      )}
    </Modal>
  )
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
  const [inspectedActor, setInspectedActor] = useState<'player' | 'enemy' | null>(null)
  const current = queue[0]
  const activeCombat = current?.type === 'combat' && current.executionKind === 'combat-encounter' ? current : null
  const expedition = activeCombat?.expedition
  const activeAction = activeCombat ? actions.find(action => action.id === activeCombat.actionId) : null
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
  const playerHp = expedition?.playerHitpoints ?? combatResources?.hitpoints ?? playerMaxHp
  const playerMp = expedition?.playerManapoints ?? combatResources?.manapoints ?? playerMaxMp
  const phase = expedition?.phase ?? 'idle'
  const enemyVisible = phase === 'combat'
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

  if (enemyVisible) {
    const enemyName = expedition?.enemyName ?? '未知敌群'
    enemyRef.current = {
      actorKey: `enemy:${expedition?.battleSequence ?? 0}:${enemyName}`,
      name: enemyName,
      seal: enemyName.slice(0, 1),
      hp: expedition?.enemyHitpoints ?? 0,
      maxHp: expedition?.enemyMaxHitpoints ?? 1,
      mp: expedition?.enemyManapoints ?? 0,
      maxMp: expedition?.enemyMaxManapoints ?? 1,
      action: expedition?.enemyAction,
      visual: activeEncounter?.enemy?.visual ?? null,
    }
  }
  const enemy = enemyRef.current
  const playerBaseRows: AttributeRow[] = [
    { label: '生命', value: `${formatNumber(playerHp)} / ${formatNumber(playerMaxHp)}` },
    { label: '灵力', value: `${formatNumber(playerMp)} / ${formatNumber(playerMaxMp)}` },
    { label: '体魄', value: formatNumber(combat?.staminaLevel ?? 0) },
    { label: '神识', value: formatNumber(combat?.intelligenceLevel ?? 0) },
    { label: '攻击', value: formatNumber(combat?.attackLevel ?? 0) },
    { label: '防御', value: formatNumber(combat?.defenseLevel ?? 0) },
    { label: '剑道', value: formatNumber(combat?.swordsmanshipLevel ?? 0) },
    { label: '符箓', value: formatNumber(combat?.talismanLevel ?? 0) },
    { label: '五行', value: formatNumber(combat?.fiveElementsLevel ?? 0) },
    { label: '命中', value: formatNumber(combatAttributes?.accuracyRating ?? 0) },
    { label: '闪避', value: formatNumber(combatAttributes?.evasionRating ?? 0) },
    { label: '最大伤害', value: formatNumber(combatAttributes?.maxDamage ?? 0) },
    { label: '攻击间隔', value: `${((combatAttributes?.attackIntervalMilliseconds ?? 0) / 1000).toFixed(2)} 秒` },
    { label: '护甲', value: formatNumber(combatAttributes?.armor ?? 0) },
    { label: '水抗', value: formatNumber(combatAttributes?.waterResistance ?? 0) },
    { label: '木抗', value: formatNumber(combatAttributes?.natureResistance ?? 0) },
    { label: '火抗', value: formatNumber(combatAttributes?.fireResistance ?? 0) },
  ]
  const playerSpecialCandidates: Array<[string, number, 'percent' | 'number']> = [
    ['控制抗性', combatAttributes?.tenacity ?? 0, 'number'],
    ['生命恢复', combatAttributes?.hpRegenPerTenSeconds ?? 0, 'percent'],
    ['灵力恢复', combatAttributes?.mpRegenPerTenSeconds ?? 0, 'percent'],
    ['抗性穿透', combatAttributes?.penetrationRatio ?? 0, 'percent'],
    ['穿透概率', combatAttributes?.pierceChance ?? 0, 'percent'],
    ['蚀心', combatAttributes?.curseRatio ?? 0, 'percent'],
    ['沸血', combatAttributes?.furyRatio ?? 0, 'percent'],
    ['衰弱', combatAttributes?.weakenRatio ?? 0, 'percent'],
    ['生命偷取', combatAttributes?.lifeStealRatio ?? 0, 'percent'],
    ['护甲增伤', combatAttributes?.armorDamageRatio ?? 0, 'percent'],
    ['生命汲取', combatAttributes?.hpDrainRatio ?? 0, 'percent'],
    ['承受伤害', combatAttributes?.damageTakenRatio ?? 0, 'percent'],
    ['暴击率', combatAttributes?.criticalChance ?? 0, 'percent'],
    ['暴击增伤', combatAttributes?.criticalDamageRatio ?? 0, 'percent'],
    ['免暴率', combatAttributes?.criticalAvoidance ?? 0, 'percent'],
    ['伤害吸收', combatAttributes?.damageAbsorptionRatio ?? 0, 'percent'],
    ['掉落率', combat?.combatDropRate ?? 0, 'percent'],
    ['掉落数量', combat?.combatDropQuantity ?? 0, 'percent'],
    ['稀有发现', combat?.combatRareFind ?? 0, 'percent'],
    ['重寻速度', combat?.searchSpeedRatio ?? 0, 'percent'],
  ]
  const playerSpecialRows = playerSpecialCandidates
    .filter(([, value]) => Math.abs(value) > 0.000001)
    .map(([label, value, kind]) => ({
      label,
      value: kind === 'percent' ? `${value > 0 ? '+' : ''}${Math.round(value * 1000) / 10}%` : formatNumber(value),
    }))
  const enemyDefinition: CombatantDefinition | null = activeEncounter?.enemy ?? null
  const enemyBaseRows: AttributeRow[] = [
    { label: '生命', value: `${formatNumber(enemy.hp)} / ${formatNumber(enemy.maxHp)}` },
    { label: '灵力', value: `${formatNumber(enemy.mp)} / ${formatNumber(enemy.maxMp)}` },
    { label: '命中', value: formatNumber(enemyDefinition?.accuracyRating ?? 0) },
    { label: '闪避', value: formatNumber(enemyDefinition?.evasionRating ?? 0) },
    { label: '最大伤害', value: formatNumber(enemyDefinition?.maxDamage ?? 0) },
    { label: '攻击间隔', value: `${((enemyDefinition?.attackIntervalMilliseconds ?? 0) / 1000).toFixed(2)} 秒` },
    { label: '护甲', value: formatNumber(enemyDefinition?.armor ?? 0) },
    { label: '水抗', value: formatNumber(enemyDefinition?.waterResistance ?? 0) },
    { label: '木抗', value: formatNumber(enemyDefinition?.natureResistance ?? 0) },
    { label: '火抗', value: formatNumber(enemyDefinition?.fireResistance ?? 0) },
  ]
  const enemySpecialCandidates: Array<[string, number, 'percent' | 'number' | 'milliseconds']> = [
    ['控制抗性', enemyDefinition?.tenacity ?? 0, 'number'],
    ['生命恢复', enemyDefinition?.hpRegenPerTenSeconds ?? 0, 'percent'],
    ['灵力恢复', enemyDefinition?.mpRegenPerTenSeconds ?? 0, 'percent'],
    ['穿透概率', enemyDefinition?.pierceChance ?? 0, 'percent'],
    ['蚀心', enemyDefinition?.curseRatio ?? 0, 'percent'],
    ['沸血', enemyDefinition?.furyRatio ?? 0, 'percent'],
    ['衰弱', enemyDefinition?.weakenRatio ?? 0, 'percent'],
    ['生命偷取', enemyDefinition?.lifeStealRatio ?? 0, 'percent'],
    ['护甲增伤', enemyDefinition?.armorDamageRatio ?? 0, 'percent'],
    ['生命汲取', enemyDefinition?.hpDrainRatio ?? 0, 'percent'],
    ['暴击率', enemyDefinition?.criticalChance ?? 0, 'percent'],
    ['暴击增伤', enemyDefinition?.criticalDamageRatio ?? 0, 'percent'],
    ['免暴率', enemyDefinition?.criticalAvoidance ?? 0, 'percent'],
    ['伤害吸收', enemyDefinition?.damageAbsorptionRatio ?? 0, 'percent'],
    ['狂暴周期', enemyDefinition?.enrageTimeMilliseconds ?? 0, 'milliseconds'],
  ]
  const enemySpecialRows = enemySpecialCandidates
    .filter(([, value]) => Math.abs(value) > 0.000001)
    .map(([label, value, kind]) => ({
      label,
      value: kind === 'percent'
        ? `${value > 0 ? '+' : ''}${Math.round(value * 1000) / 10}%`
        : kind === 'milliseconds'
          ? `${(value / 1000).toFixed(1)} 秒`
          : formatNumber(value),
    }))

  return (
    <div className="combat-arena-shell">
      <div className={`combat-arena-field combat-arena-field--${phase}`}>
        <ArenaActor
          action={phase === 'combat' || phase === 'death' ? expedition?.playerAction : null}
          actorKey="player"
          hp={playerHp}
          maxHp={playerMaxHp}
          maxMp={playerMaxMp}
          mp={playerMp}
          name={playerName}
          buffs={buffs}
          onInspect={() => setInspectedActor('player')}
          role="player"
          seal={playerName.slice(0, 1) || '修'}
          visual={playerVisual}
        />
        {enemyVisible ? (
          <ArenaActor
            action={enemy.action}
            actorKey={enemy.actorKey}
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
        ) : expedition ? <ArenaPhaseState expedition={expedition} /> : <span aria-hidden="true" />}
      </div>

      <div className="combat-arena-controls">
        {activeCombat ? (
          <button className="danger" onClick={() => gameClient.sendCommand('remove_action', { entryId: activeCombat.entryId })} type="button">
            退出历练
          </button>
        ) : availableActions.length > 0 ? availableActions.map(action => (
          <button key={action.id} onClick={() => onOpenAction(action)} type="button">{action.name}</button>
        )) : null}
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
