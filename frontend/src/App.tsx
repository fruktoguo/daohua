import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { ConfirmDialog } from './components/ConfirmDialog'
import { AbilityGridPicker } from './components/AbilityGridPicker'
import { StatsPanel } from './components/StatsPanel'
import { RecordsPanel } from './components/RecordsPanel'
import { CombatArena } from './components/CombatArena'
import { CombatTriggerEditor } from './components/CombatTriggerEditor'
import { CombatEncounterPanel } from './components/CombatEncounterPanel'
import { EffectTooltipTarget } from './components/EffectTooltip'
import { FloatingInfoCard, FloatingPopover, FloatingTooltip } from './components/FloatingPopover'
import { GameLogPanel } from './components/GameLogPanel'
import { ItemReference, ItemTooltipTarget, describeBuffEffectLines } from './components/ItemDisplay'
import { ItemGridPicker, ItemUseSlot, type ItemPickerEntry } from './components/ItemGridPicker'
import { MarketPage } from './components/MarketPage'
import { Modal } from './components/Modal'
import { InsightPage } from './components/InsightPage'
import { RealmBadge } from './components/RealmBadge'
import { NearbyPanel, type NearbyTab } from './components/NearbyPanel'
import { QuantitySelector } from './components/QuantitySelector'
import { WorldMapPanel } from './components/WorldMapPanel'
import { SettingsPage } from './components/SettingsPage'
import { LoginPage } from './components/LoginPage'
import { ConnectingPage, ConnectionErrorPage } from './components/ConnectingPage'
import { RightSidebar } from './components/RightSidebar'
import { describeAbility, formatChineseSplitNumber, formatProgressText, formatRemainingSeconds } from './game/format'
import { UnitNumber } from './components/UnitNumber'
import { calculateRealmInfo, calculateRealmProgress } from './game/combatConstants'
import { ACTION_TAB_META, actionTabLabel, assignAbilityToSlot, findAbilityInCatalog, findActionInCatalog, findEncounterInCatalog, findItemInCatalog, learnedAbilityLevel, resolveActionDefinition, resolveActionLevel, resolveActionRarity, resolveItemCategoryLabel, resolveItemMetadata } from './game/ids'
import { useGameStore } from './game/gameStore'
import { sendLoadoutReplace } from './game/loadoutCommands'
import { resolveItemIconUrl } from './game/itemIcons'
import { calculateActionYieldPreview } from './game/actionCalculations'
import { useSmoothProgressRef } from './game/useSmoothProgress'
import type { ActionDefinition, ActionType, CombatTrigger, EquipmentSlot, InventoryItem, QueueEntry } from './game/types'
import { gameClient, GAME_DATA_MISMATCH_EVENT } from './network/gameClient'
import { installAntiMultiOpen, useAntiMultiOpenStore } from './antiMulti'
import { MultiTabBlockedPage } from './antiMulti/MultiTabBlockedPage'
import { useAccountStore } from './network/account'
import { fetchGameCatalog } from './network/apiClient'
import { initUiScale } from './utils/uiScale'
import { useUiStore, type TabId, type AlchemySubTab, type ForgingSubTab } from './uiStore'
import { useT, getT } from './i18n'

const EMPTY_ABILITY_COOLDOWNS: Readonly<Record<string, number>> = Object.freeze({})

type IconName = 'insight' | ActionType | 'crafting' | 'forging' | 'map' | 'market' | 'inventory' | 'log' | 'settings' | 'spirit' | 'buff'

/**
 * 矢量图标组件（按名称渲染内联 SVG）
 */
const Icon = memo(function Icon({ name }: { name: IconName }): React.JSX.Element {
  const common = {
    fill: 'none',
    stroke: 'currentColor',
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    strokeWidth: 1.7,
    viewBox: '0 0 24 24',
  }

  switch (name) {
    case 'insight':
      return <svg {...common}><circle cx="12" cy="12" r="9" /><circle cx="12" cy="12" r="3.5" /><path d="M12 3v3M12 18v3M3 12h3M18 12h3" /></svg>
    case 'cultivation':
      return <svg {...common}><path d="M12 4.5c2 2.4 2 6.2 0 8.6-2-2.4-2-6.2 0-8.6Z" /><path d="M4.8 8.8c3 .3 5.4 2.4 6.1 5.4-3.2-.2-5.7-2.3-6.1-5.4Z" /><path d="M19.2 8.8c-3 .3-5.4 2.4-6.1 5.4 3.2-.2 5.7-2.3 6.1-5.4Z" /><path d="M3.5 15.5c2.6 2.8 5.4 4 8.5 4s5.9-1.2 8.5-4" /></svg>
    case 'exploration':
      return <svg {...common}><circle cx="17.5" cy="6" r="2.2" /><path d="M3 19 9.5 7l4.5 7 2.5-3.5L21 19H3Z" /></svg>
    case 'alchemy':
      return <svg {...common}><path d="M8.5 10V7.5M15.5 10V7.5" /><path d="M5.5 10.5h13v3.5a6.5 6.5 0 0 1-13 0v-3.5Z" /><path d="M9 21v-2M15 21v-2" /></svg>
    case 'crafting':
      return <svg {...common}><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76Z" /></svg>
    case 'forging':
      return <svg {...common}><path d="M15 12l-8.5 8.5a2.12 2.12 0 1 1-3-3L12 9" /><path d="M17.64 4.36a3 3 0 0 0-4.24 0L12 5.76l4.24 4.24 1.4-1.4a3 3 0 0 0 0-4.24Z" /><path d="m14 7 3 3" /></svg>
    case 'map':
      return <svg {...common}><path d="M3 6l6-3 6 3 6-3v15l-6 3-6-3-6 3V6Z" /><path d="M9 3v15M15 6v15" /></svg>
    case 'combat':
      return <svg {...common}><path d="m5 4 15 15M19 4 4 19" /><path d="m7 7 3-3 3 3M17 17l-3 3-3-3" /></svg>
    case 'market':
      return <svg {...common}><path d="M4 8.5h16M6 8.5V20h12V8.5" /><path d="m5 4h14l1 4.5H4L5 4Z" /><path d="M9 12h6M9 15.5h6" /></svg>
    case 'inventory':
      return <svg {...common}><path d="M9 7c0-1.8 1.2-2.8 3-2.8s3 1 3 2.8" /><path d="M6.5 9.5C5.5 14.5 7.5 20 12 20s6.5-5.5 5.5-10.5c-1.7 1-3.6 1.5-5.5 1.5s-3.8-.5-5.5-1.5Z" /><path d="M9.5 13.5h5" /></svg>
    case 'log':
      return <svg {...common}><path d="M8 4.5h9.5a2 2 0 0 1 2 2V18a2 2 0 0 1-2 2H8" /><path d="M8 4.5a2 2 0 0 0-2 2V18a2 2 0 0 0 2 2" /><path d="M11 9.5h5M11 13.5h5" /></svg>
    case 'settings':
      return <svg {...common}><circle cx="12" cy="12" r="3.2" /><path d="M12 3v2.6M12 18.4V21M3 12h2.6M18.4 12H21M5.6 5.6l1.9 1.9M16.5 16.5l1.9 1.9M18.4 5.6l-1.9 1.9M7.5 16.5l-1.9 1.9" /></svg>
    case 'spirit':
      return <svg {...common}><path d="M12 2.5 18.5 8 12 21.5 5.5 8 12 2.5Z" /><path d="M5.5 8h13M12 2.5 9.5 8 12 21.5" /></svg>
    case 'buff':
      return <svg {...common}><path d="M7 16a4.2 4.2 0 0 1-.6-8.3 5.2 5.2 0 0 1 10-1.3A3.8 3.8 0 0 1 17.5 16H7Z" /></svg>
  }
})

/** 查询背包中指定物品的持有数量（未持有返回 0）；只计基础变体，与服务端 AssetKey(itemId) 空变体校验一致 */
function itemAmount(inventory: InventoryItem[], itemId: string): number {
  return inventory.find(item => item.itemId === itemId && !item.variant)?.amount ?? 0
}

/** 解析队列条目显示名：优先 wire 携带的 actionName，否则按 actionId 回查配表 */
function resolveQueueEntryName(entry: QueueEntry, actions?: readonly ActionDefinition[]): string {
  if (entry.actionName && entry.actionName !== entry.actionId) {
    return entry.actionName
  }
  const def = (actions ? findActionInCatalog(actions, entry.actionId) : undefined) ?? resolveActionDefinition(entry.actionId)
  return def?.name || entry.actionName || entry.actionId
}

function actionDescription(entry: QueueEntry, actions?: readonly ActionDefinition[]): string {
  const def = (actions ? findActionInCatalog(actions, entry.actionId) : undefined) ?? resolveActionDefinition(entry.actionId)
  return def?.description ?? resolveQueueEntryName(entry, actions)
}

/** 剩余轮次文案：负值（协议约定 -1）表示无限循环 */
function formatRepeatsLabel(remainingRepeats: number, t: (key: string, params?: Record<string, string | number>) => string): string {
  if (remainingRepeats < 0) return t('app.queue.repeatsInfinite')
  return t('common.repeatsCount', { n: remainingRepeats })
}

/**
 * 响应式监听移动端断点 (< 960px) 的自定义 Hook
 */
function useMobileLayout(): boolean {
  const [mobile, setMobile] = useState(() => typeof window !== 'undefined' && window.matchMedia('(max-width: 960px)').matches)
  useEffect(() => {
    const media = window.matchMedia('(max-width: 960px)')
    const update = () => setMobile(media.matches)
    media.addEventListener('change', update)
    return () => media.removeEventListener('change', update)
  }, [])
  return mobile
}

/**
 * 平滑进度条组件
 */
const SmoothProgressBar = memo(function SmoothProgressBar({ entry }: { entry: QueueEntry }): React.JSX.Element {
  const t = useT()
  const fillRef = useRef<HTMLDivElement>(null)
  const textRef = useRef<HTMLSpanElement>(null)

  useSmoothProgressRef({
    entry,
    fillRef,
    textRef,
  })

  return (
    <div
      aria-label={t('app.queue.progressAria')}
      aria-valuemax={100}
      aria-valuemin={0}
      className="progress"
      role="progressbar"
    >
      <div className="progress-fill" ref={fillRef}><i /></div>
      <span className="progress-text" ref={textRef} />
    </div>
  )
})

/**
 * 远征行动进度组件（包含行进里程与搜寻状态）
 */
const ExpeditionActionProgress = memo(function ExpeditionActionProgress({ entry }: { entry: QueueEntry }): React.JSX.Element | null {
  const t = useT()
  const expedition = entry.expedition
  const encounters = useGameStore(state => state.encounters)
  const fillRef = useRef<HTMLDivElement>(null)
  const textRef = useRef<HTMLSpanElement>(null)
  const metaRef = useRef<HTMLSpanElement>(null)
  const percentRef = useRef<HTMLElement>(null)

  // 搜寻目标名：expedition 载荷不再带 enemyName，按 encounterId 从遭遇配表解析主敌名。
  const searchTargetName = expedition?.encounterId
    ? findEncounterInCatalog(encounters, expedition.encounterId)?.enemy?.name
    : null

  const phaseEntry: QueueEntry = useMemo(() => ({
    ...entry,
    progressSeconds: expedition?.elapsedSeconds ?? 0,
    durationSeconds: expedition?.durationSeconds ?? 1,
  }), [entry, expedition?.elapsedSeconds, expedition?.durationSeconds])

  useSmoothProgressRef({
    entry: phaseEntry,
    fillRef,
    textRef,
    percentRef,
    metaRef,
    metaFormat: () => getT()('app.queue.searchTarget', { name: searchTargetName ?? getT()('app.queue.defaultEnemy') }),
  })

  if (!expedition || entry.type === 'combat') return null
  return (
    <div className={`qc-expedition qc-expedition--${expedition.phase}`}>
      <div className="qc-expedition-meta">
        <span ref={metaRef}>
          {t('app.queue.searchTarget', { name: searchTargetName ?? t('app.queue.defaultEnemy') })}
        </span>
        <strong ref={percentRef} />
      </div>
      <div
        aria-label={t('app.queue.expeditionProgressAria')}
        aria-valuemax={100}
        aria-valuemin={0}
        className="progress"
        role="progressbar"
      >
        <div className="progress-fill" ref={fillRef}><i /></div>
        <span className="progress-text" ref={textRef} />
      </div>
    </div>
  )
})

function getCombatPhaseLabel(phase: string | undefined, t: (key: string) => string): string {
  switch (phase) {
    case 'initial-search': return t('app.queue.phaseSearch')
    case 'combat': return t('app.queue.phaseCombat')
    case 'search': return t('app.queue.phaseResearch')
    case 'respawn': return t('app.queue.phaseDeath')
    case 'duel-pending': return t('app.queue.phaseDuelPending')
    case 'duel': return t('app.queue.phaseDuel')
    default: return t('app.queue.phaseDefault')
  }
}

const CombatActionPhaseProgress = memo(function CombatActionPhaseProgress({
  entry,
  phase,
}: {
  entry: QueueEntry
  phase: string
}): React.JSX.Element {
  const t = useT()
  const fillRef = useRef<HTMLDivElement>(null)
  const textRef = useRef<HTMLSpanElement>(null)
  const percentRef = useRef<HTMLElement>(null)

  useSmoothProgressRef({
    entry,
    fillRef,
    textRef,
    percentRef,
  })

  return (
    <div className={`queue-combat-state queue-combat-state--${phase}`}>
      <div className={`qc-expedition qc-expedition--${phase}`}>
        <div className="qc-expedition-meta">
          <span>{getCombatPhaseLabel(phase, t)}</span>
          <strong ref={percentRef} />
        </div>
        <div
          aria-label={getCombatPhaseLabel(phase, t)}
          aria-valuemax={100}
          aria-valuemin={0}
          className="progress"
          role="progressbar"
        >
          <div className="progress-fill" ref={fillRef}><i /></div>
          <span className="progress-text" ref={textRef} />
        </div>
      </div>
    </div>
  )
})

/**
 * 战斗中顶部紧凑血量条与行动进度展示组件
 */
const CombatActionProgress = memo(function CombatActionProgress({ entry }: { entry: QueueEntry }): React.JSX.Element {
  const t = useT()
  const expedition = entry.expedition
  const combatResources = useGameStore(state => state.combatResources)
  const combatAttributes = useGameStore(state => state.combatAttributes)
  // 斗法回放相位同样展示双方实时气血；duel-pending 等其余阶段走通用相位进度。
  if (expedition && expedition.phase !== 'combat' && expedition.phase !== 'duel') {
    const phaseEntry: QueueEntry = {
      ...entry,
      progressSeconds: expedition.elapsedSeconds,
      durationSeconds: expedition.durationSeconds,
    }
    return <CombatActionPhaseProgress entry={phaseEntry} phase={expedition.phase} />
  }

  // 己方上限取自战斗资源分段；敌方总量为同波全体敌人当前/最大血量之和，
  // 斗法相位取回放载荷中的对手镜像血量（均无 wire 标量可直接用）。
  const playerMax = Math.max(1, combatResources?.maxHitpoints ?? combatAttributes?.maxHitpoints ?? 1)
  const playerValue = Math.max(0, Math.min(playerMax, expedition?.playerHitpoints ?? playerMax))
  let enemyValue = 0
  let enemyMax = 0
  if (expedition?.phase === 'duel') {
    enemyMax = Math.max(1, expedition.duel?.opponentAttributes.maxHitpoints ?? 1)
    enemyValue = Math.max(0, Math.min(enemyMax, expedition.duel?.opponentHitpoints ?? enemyMax))
  } else {
    // 敌方上限直连快照下发的权威属性，不再反查怪物目录，也不以当前血量充当上限。
    for (const enemy of expedition?.enemies ?? []) {
      enemyMax += Math.max(1, enemy.attributes?.maxHitpoints ?? 1)
      enemyValue += Math.max(0, enemy.hitpoints)
    }
    enemyMax = Math.max(1, enemyMax)
  }
  return (
    <div className="queue-combat-state queue-combat-state--combat">
      <div className="queue-combat-vitals" role="status" aria-label={t('app.queue.combatVitalsAria')}>
        <div className="queue-combat-vital queue-combat-vital--player">
          <span>{t('app.queue.combatVitalSelf')}</span>
          <i><b style={{ transform: `scaleX(${playerValue / playerMax})` }} /></i>
          <strong><UnitNumber maxDecimals={0} value={playerValue} /> / <UnitNumber maxDecimals={0} value={playerMax} />（{Math.round(playerValue / playerMax * 100)}%）</strong>
        </div>
        <div className="queue-combat-vital queue-combat-vital--enemy">
          <span>{t('app.queue.combatVitalEnemy')}</span>
          <i><b style={{ transform: `scaleX(${enemyValue / enemyMax})` }} /></i>
          <strong><UnitNumber maxDecimals={0} value={enemyValue} /> / <UnitNumber maxDecimals={0} value={enemyMax} />（{Math.round(enemyValue / enemyMax * 100)}%）</strong>
        </div>
      </div>
    </div>
  )
})

/**
 * 当前正在执行的行动卡片（支持出场/入场平滑过渡动画）
 */
const CurrentActionCard = memo(function CurrentActionCard({ entry, departing = false }: { entry: QueueEntry; departing?: boolean }): React.JSX.Element {
  const t = useT()
  const isCombatExpedition = entry.type === 'combat'
  const phaseLabel = isCombatExpedition
    ? getCombatPhaseLabel(entry.expedition?.phase, t)
    : entry.executionKind === 'travel'
    ? t('app.queue.phaseTravel')
    : entry.expedition?.phase === 'initial-search' || entry.expedition?.phase === 'search'
      ? t('app.queue.phasePrepare')
      : entry.expedition?.phase === 'combat'
        ? t('app.queue.phaseEngage')
        : t('app.queue.phaseProgress')
  return (
    <div
      aria-hidden={departing || undefined}
      className={`qc-current${entry.status === 'blocked' ? ' is-blocked' : ''}${departing ? ' qc-current--departing' : ' qc-current--entering'}`}
      data-entry-id={entry.entryId}
    >
      <span className={`item-glyph qc-glyph tone-${ACTION_TAB_META[entry.type].accent}`} aria-hidden="true">
        <Icon name={entry.type} />
      </span>
      <div className="qc-main">
        <div className="qc-title">
          <strong>{resolveQueueEntryName(entry)}</strong>
          <span className="qc-repeats">{formatRepeatsLabel(entry.remainingRepeats, t)}</span>
          {(entry.status === 'blocked' || phaseLabel) && (
            <span className={entry.status === 'blocked' ? 'badge badge-danger' : 'badge'}>
              {entry.status === 'blocked' ? t('app.queue.blocked') : phaseLabel}
            </span>
          )}
          {entry.pvpEnabled && <span className="badge badge-duel">{t('app.queue.duelBadge')}</span>}
        </div>
        {isCombatExpedition
          ? <CombatActionProgress entry={entry} />
          : entry.expedition
            ? <ExpeditionActionProgress entry={entry} />
            : <SmoothProgressBar entry={entry} />}
      </div>
    </div>
  )
})

/**
 * 队列中当前行动展示区（支持切换动画）
 */
const QueueCurrentStage = memo(function QueueCurrentStage({ entry }: { entry?: QueueEntry }): React.JSX.Element {
  const t = useT()
  const previousRef = useRef<QueueEntry | undefined>(entry)
  const exitTimerRef = useRef<number | undefined>(undefined)
  const [departing, setDeparting] = useState<QueueEntry | null>(null)

  useLayoutEffect(() => {
    const previous = previousRef.current
    if (previous && previous.entryId !== entry?.entryId) {
      setDeparting(previous)
      if (exitTimerRef.current !== undefined) window.clearTimeout(exitTimerRef.current)
      exitTimerRef.current = window.setTimeout(() => setDeparting(null), 190)
    }
    previousRef.current = entry
  }, [entry])

  useEffect(() => () => {
    if (exitTimerRef.current !== undefined) window.clearTimeout(exitTimerRef.current)
  }, [])

  return (
    <div className="qc-current-stage">
      {entry ? <CurrentActionCard entry={entry} key={entry.entryId} /> : <div className="qc-current qc-idle">{t('app.queue.idle')}</div>}
      {departing && <CurrentActionCard departing entry={departing} key={`departing-${departing.entryId}`} />}
    </div>
  )
})

/**
 * 顶部行动队列总控中心组件（展示当前行动、后续队列预览、循环开关与展开下拉抽屉）
 */
const QueueCenter = memo(function QueueCenter(): React.JSX.Element {
  const t = useT()
  const queue = useGameStore(state => state.queue)
  const [open, setOpen] = useState(false)
  const [pendingRemoval, setPendingRemoval] = useState<QueueEntry | null>(null)
  const current = queue.entries[0]
  const upcoming = queue.entries.slice(1, 3)

  useEffect(() => {
    if (queue.entries.length === 0) setOpen(false)
  }, [queue.entries.length])

  return (
    <div className="queue-center">
      <QueueCurrentStage entry={current} />

      <div className="qc-upcoming" aria-label={t('app.queue.upcomingAria')}>
        {upcoming.map(entry => (
          <FloatingTooltip
            displayContents
            key={entry.entryId}
            placement="bottom"
            title={actionDescription(entry)}
            tone="gold"
          >
            <div className="qc-next">
              <span className={`item-glyph tone-${ACTION_TAB_META[entry.type].accent}`} aria-hidden="true">
                <Icon name={entry.type} />
              </span>
              <div className="qc-next-text">
                <span className="qc-next-name">{resolveQueueEntryName(entry)}</span>
                <span className="qc-next-repeats">{formatRepeatsLabel(entry.remainingRepeats, t)}</span>
              </div>
            </div>
          </FloatingTooltip>
        ))}
      </div>

      <div className="qc-controls">
        <label className="loop-toggle">
          <input
            checked={queue.loopEnabled}
            onChange={event => gameClient.sendCommand('queue.configure', { loopEnabled: event.target.checked })}
            type="checkbox"
          />
          {t('app.queue.loop')}
        </label>
        <button
          aria-expanded={open}
          className="qc-expand"
          disabled={queue.entries.length === 0}
          onClick={() => setOpen(value => !value)}
          type="button"
        >
          {t('app.queue.queueButton', { count: queue.entries.length, arrow: open ? '▴' : '▾' })}
        </button>
      </div>

      {open && (
        <>
          <button aria-label={t('app.queue.closeQueueAria')} className="queue-backdrop" onClick={() => setOpen(false)} type="button" />
          <ol className="queue-dropdown">
            {queue.entries.map((entry, index) => (
              <li
                className={`queue-entry${index === 0 ? ' active' : ''}${entry.status === 'blocked' ? ' blocked' : ''}`}
                key={entry.entryId}
              >
                <span className="queue-order">{index + 1}</span>
                <span className={`item-glyph tone-${ACTION_TAB_META[entry.type].accent}`} aria-hidden="true">
                  <Icon name={entry.type} />
                </span>
                <div className="queue-main">
                  <div className="queue-title">
                    <span className="queue-name">{resolveQueueEntryName(entry)}</span>
                    <span className="queue-repeats">{formatRepeatsLabel(entry.remainingRepeats, t)}</span>
                    {entry.status === 'blocked' && <span className="badge badge-danger">{t('app.queue.blocked')}</span>}
                    {index === 0 && entry.status === 'running' && <span className="badge">{t('app.queue.running')}</span>}
                    {entry.pvpEnabled && <span className="badge badge-duel">{t('app.queue.duelBadge')}</span>}
                  </div>
                  {actionDescription(entry) && actionDescription(entry) !== resolveQueueEntryName(entry) ? <div className="queue-sub">{actionDescription(entry)}</div> : null}
                </div>
                <div className="queue-actions">
                  <button
                    aria-label={t('app.queue.moveUpAria')}
                    disabled={index === 0}
                    onClick={() => gameClient.sendCommand('queue.move', { actionInstanceId: entry.entryId, direction: -1 })}
                    title={t('app.queue.moveUpTitle')}
                    type="button"
                  >↑</button>
                  <button
                    aria-label={t('app.queue.moveDownAria')}
                    disabled={index === queue.entries.length - 1}
                    onClick={() => gameClient.sendCommand('queue.move', { actionInstanceId: entry.entryId, direction: 1 })}
                    title={t('app.queue.moveDownTitle')}
                    type="button"
                  >↓</button>
                  <button
                    aria-label={t('app.queue.removeAria')}
                    className="queue-remove"
                    onClick={() => setPendingRemoval(entry)}
                    title={t('app.queue.removeTitle')}
                    type="button"
                  >×</button>
                </div>
              </li>
            ))}
          </ol>
        </>
      )}
      <ConfirmDialog
        confirmLabel={t('app.queue.removeConfirmBtn')}
        danger
        description={pendingRemoval ? t('app.queue.removeConfirmDesc', { name: resolveQueueEntryName(pendingRemoval) }) : ''}
        onClose={() => setPendingRemoval(null)}
        onConfirm={() => {
          if (pendingRemoval) gameClient.sendCommand('action.remove', { actionInstanceId: pendingRemoval.entryId })
        }}
        open={pendingRemoval !== null}
        title={t('app.queue.removeConfirmTitle')}
      />
    </div>
  )
})

/**
 * 顶部 Buff 与灵脉环境增益状态栏
 */
const BuffBar = memo(function BuffBar(): React.JSX.Element | null {
  const t = useT()
  const buffs = useGameStore(state => state.buffs)
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const world = useGameStore(state => state.world)
  const hasLocationEffect = Math.abs(world.cultivationMultiplier - 1) > 0.001
  if (buffs.length === 0 && !hasLocationEffect) return null

  return (
    <div className="buff-bar" aria-label={t('app.buff.barAria')}>
      {buffs.map(buff => {
        const remaining = Math.max(0, buff.expiresAtUnixSeconds - serverTime)
        // Buff 描述唯一来源：权威修饰器字段（regen/攻速/免伤/statRatios），不再读收益倍率幻影字段。
        const desc = describeBuffEffectLines(buff).join('；') || t('app.buff.combatBuff')

        return (
          <FloatingPopover
            closeDelay={80}
            content={
              <FloatingInfoCard
                badge={t('app.buff.stackAria', { n: buff.stacks })}
                badgeTone="gold"
                description={desc}
                subtitle={t('app.buff.remainingStacks', { remaining, stacks: buff.stacks })}
                title={buff.name}
              />
            }
            interactive={false}
            key={buff.buffId}
            openDelay={60}
            placement="bottom"
            size="small"
          >
            <span className="buff-chip">
              <Icon name="buff" />
              <span className="buff-stack" aria-label={t('app.buff.stackAria', { n: buff.stacks })}>{buff.stacks}</span>
            </span>
          </FloatingPopover>
        )
      })}
      {hasLocationEffect && (
        <FloatingPopover
          closeDelay={80}
          content={
            <FloatingInfoCard
              badge={t('app.buff.locationMark')}
              badgeTone="qi"
              description={t('app.buff.locationEfficiency', {
                name: world.locationName,
                sign: world.cultivationMultiplier > 1 ? '+' : '',
                percent: Math.round((world.cultivationMultiplier - 1) * 100),
              })}
              subtitle={t('app.buff.locationOnly')}
              title={world.locationName}
            />
          }
          interactive={false}
          openDelay={60}
          placement="bottom"
          size="small"
        >
          <span className="buff-chip buff-chip--location">
            <Icon name="spirit" />
            <span className="buff-stack" aria-label={t('app.buff.locationStackAria')}>{t('app.buff.locationMark')}</span>
          </span>
        </FloatingPopover>
      )}
    </div>
  )
})

/**
 * 客户端全局顶栏组件（包含道号、境界徽章、修为进度条、队列控制中心与 Buff 栏）
 */
const TopBar = memo(function TopBar(): React.JSX.Element {
  const t = useT()
  const name = useGameStore(state => state.name)
  const spirit = useGameStore(state => state.stats.spirit)
  const realmLevel = useGameStore(state => state.stats.realmLevel)
  const connection = useGameStore(state => state.connection)
  const presence = useGameStore(state => state.presenceMode)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  const realmProgress = useMemo(() => calculateRealmProgress(spirit ?? 0, realmLevel), [realmLevel, spirit])
  const realmInfo = useMemo(() => calculateRealmInfo(spirit ?? 0, realmLevel), [realmLevel, spirit])
  const connectionText = connection === 'online'
    ? t('app.topbar.connected')
    : connection === 'connecting'
      ? t('app.topbar.connecting')
      : connection === 'reconnecting'
        ? t('app.topbar.reconnecting')
        : t('app.topbar.offline')
  const presenceText = presence === 'online'
    ? t('app.topbar.presenceOnline')
    : presence === 'idle-offline'
      ? t('app.topbar.presenceIdle')
      : t('app.topbar.presenceOffline')

  const progressDisplay = formatProgressText(realmProgress.currentLayerExp, realmProgress.expToNext, {
    completedText: t('app.topbar.ascendedComplete'),
    percentDecimals: 0,
  })

  return (
    <header className="topbar">
      <div
        aria-label={t('app.topbar.insightAria')}
        className="player-block"
        onClick={() => setActiveTab('insight')}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') setActiveTab('insight') }}
        role="button"
        tabIndex={0}
      >
        <div className="player-info">
          <div className="player-line">
            <strong className="player-name">{name}</strong>
            <RealmBadge realmInfo={realmInfo} />
            <FloatingTooltip placement="bottom" title={`${connectionText} · ${presenceText}`} tone={connection === 'online' ? 'jade' : 'danger'}>
              <span className={`connection-dot connection-dot--${connection}`} />
            </FloatingTooltip>
          </div>
          <FloatingTooltip
            placement="bottom"
            title={
              <div className="unit-number-tooltip-inline">
                <span className="unit-number-tooltip-label">{t('app.topbar.totalSpirit')}</span>
                <strong className="unit-number-tooltip-split">{formatChineseSplitNumber(spirit)}</strong>
              </div>
            }
            tone="qi"
          >
            <div className="realm-progress">
              <i style={{ width: `${realmInfo.layerProgress}%` }} />
              <span>{progressDisplay}</span>
            </div>
          </FloatingTooltip>
        </div>
      </div>
      <QueueCenter />
      <BuffBar />
    </header>
  )
})

/**
 * 桌面端主侧边导航栏组件
 */
const Sidebar = memo(function Sidebar(): React.JSX.Element {
  const t = useT()
  const activeTab = useUiStore(state => state.activeTab)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  const craftingMode = useUiStore(state => state.craftingMode)
  const setCraftingMode = useUiStore(state => state.setCraftingMode)
  const craftingNavExpanded = useUiStore(state => state.craftingNavExpanded)
  const setCraftingNavExpanded = useUiStore(state => state.setCraftingNavExpanded)
  const [recordsOpen, setRecordsOpen] = useState(false)

  return (
    <aside className="sidebar panel" aria-label={t('app.nav.sidebarAria')}>
      <nav className="sidebar-nav">
        <button
          className={`nav-item${activeTab === 'insight' ? ' active' : ''}`}
          onClick={() => setActiveTab('insight')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="insight" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.insight')}</span>
          </span>
        </button>
        <button
          className={`nav-item${activeTab === 'market' ? ' active' : ''}`}
          onClick={() => setActiveTab('market')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="market" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.market')}</span>
          </span>
        </button>
        <button
          className={`nav-item${activeTab === 'cultivation' ? ' active' : ''}`}
          onClick={() => setActiveTab('cultivation')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="cultivation" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.cultivation')}</span>
          </span>
        </button>
        <button
          className={`nav-item${activeTab === 'exploration' ? ' active' : ''}`}
          onClick={() => setActiveTab('exploration')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="exploration" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.exploration')}</span>
          </span>
        </button>

        {/* 技艺（可展开/折叠二级菜单：炼丹、炼器） */}
        <div className="nav-group">
          <button
            className={`nav-item nav-item--collapsible${activeTab === 'crafting' ? ' active' : ''}`}
            onClick={() => {
              if (activeTab !== 'crafting') {
                setActiveTab('crafting')
                setCraftingNavExpanded(true)
              } else {
                setCraftingNavExpanded(expanded => !expanded)
              }
            }}
            type="button"
          >
            <span className="nav-glyph" aria-hidden="true"><Icon name="crafting" /></span>
            <span className="nav-text">
              <span className="nav-label">{t('app.nav.crafting')}</span>
            </span>
            <span
              className={`nav-chevron${craftingNavExpanded ? ' is-expanded' : ''}`}
              onClick={e => {
                e.stopPropagation()
                setCraftingNavExpanded(expanded => !expanded)
              }}
              aria-hidden="true"
            >
              ›
            </span>
          </button>
          {craftingNavExpanded && (
            <div className="nav-sub-menu">
              <button
                className={`nav-sub-item${activeTab === 'crafting' && craftingMode === 'alchemy' ? ' active' : ''}`}
                onClick={() => {
                  setActiveTab('crafting')
                  setCraftingMode('alchemy')
                }}
                type="button"
              >
                <span className="nav-sub-glyph" aria-hidden="true"><Icon name="alchemy" /></span>
                <span className="nav-sub-label">{t('app.crafting.alchemy')}</span>
              </button>
              <button
                className={`nav-sub-item${activeTab === 'crafting' && craftingMode === 'forging' ? ' active' : ''}`}
                onClick={() => {
                  setActiveTab('crafting')
                  setCraftingMode('forging')
                }}
                type="button"
              >
                <span className="nav-sub-glyph" aria-hidden="true"><Icon name="forging" /></span>
                <span className="nav-sub-label">{t('app.crafting.forging')}</span>
              </button>
            </div>
          )}
        </div>

        <button
          className={`nav-item${activeTab === 'map' ? ' active' : ''}`}
          onClick={() => setActiveTab('map')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="map" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.map')}</span>
          </span>
        </button>
        <button
          className={`nav-item${activeTab === 'combat' ? ' active' : ''}`}
          onClick={() => setActiveTab('combat')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="combat" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.combat')}</span>
          </span>
        </button>
      </nav>
      <div className="sidebar-footer">
        <button
          className="nav-item"
          onClick={() => setRecordsOpen(true)}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="log" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.records')}</span>
          </span>
        </button>
        <button
          className={`nav-item${activeTab === 'settings' ? ' active' : ''}`}
          onClick={() => setActiveTab('settings')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="settings" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.settings')}</span>
          </span>
        </button>
      </div>
      <Modal
        className="battle-report-modal"
        labelledBy="records-panel-title"
        onClose={() => setRecordsOpen(false)}
        open={recordsOpen}
        size="medium"
      >
        <RecordsPanel />
      </Modal>
    </aside>
  )
})

const ALCHEMY_SUB_TABS: ReadonlyArray<{ id: AlchemySubTab; labelKey: 'alchemyExtraction' | 'alchemyCultivation' | 'alchemyCombat' }> = [
  { id: 'extraction', labelKey: 'alchemyExtraction' },
  { id: 'cultivation', labelKey: 'alchemyCultivation' },
  { id: 'combat', labelKey: 'alchemyCombat' },
]

const FORGING_SUB_TABS: ReadonlyArray<{ id: ForgingSubTab; labelKey: 'forgeRefining' | 'forgeWeapon' | 'forgeHead' | 'forgeArmor' | 'forgeLegs' | 'forgeBoots' | 'forgeTrinket' }> = [
  { id: 'refining', labelKey: 'forgeRefining' },
  { id: 'weapon', labelKey: 'forgeWeapon' },
  { id: 'head', labelKey: 'forgeHead' },
  { id: 'armor', labelKey: 'forgeArmor' },
  { id: 'legs', labelKey: 'forgeLegs' },
  { id: 'boots', labelKey: 'forgeBoots' },
  { id: 'trinket', labelKey: 'forgeTrinket' },
]

/**
 * 技艺页面（整合炼丹与炼器两大修仙工艺系统，支持二级模式切换与顶部多子选项卡）
 */
const CraftingPage = memo(function CraftingPage({ hidden }: { hidden: boolean }): React.JSX.Element {
  const t = useT()
  const actions = useGameStore(state => state.actions)
  const equipment = useGameStore(state => state.equipment)
  const worldNodeId = useGameStore(state => state.world.nodeId)

  const craftingMode = useUiStore(state => state.craftingMode)
  const alchemySubTab = useUiStore(state => state.alchemySubTab)
  const setAlchemySubTab = useUiStore(state => state.setAlchemySubTab)
  const forgingSubTab = useUiStore(state => state.forgingSubTab)
  const setForgingSubTab = useUiStore(state => state.setForgingSubTab)

  const [selectedAction, setSelectedAction] = useState<ActionDefinition | null>(null)
  const closeModal = useCallback(() => setSelectedAction(null), [])

  // 装备 ID -> 装备部位映射表
  const equipmentSlotMap = useMemo<Record<string, EquipmentSlot>>(() => {
    const map: Record<string, EquipmentSlot> = {}
    for (const eq of equipment) {
      map[eq.equipmentId] = eq.slot
    }
    return map
  }, [equipment])
  // 当前世界节点可执行的所有工艺类行动
  const availableActions = useMemo(() => actions.filter(action => (
    (action.type === 'alchemy' || action.id.startsWith('craft.') || action.id.startsWith('forge.') || action.id.startsWith('alchemy.'))
      && (!action.availableNodeIds?.length || (worldNodeId !== null && action.availableNodeIds.includes(worldNodeId)))
  )), [actions, worldNodeId])

  const itemCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const encounters = useGameStore(state => state.encounters)

  // 当前模式与当前子选项卡下的行动列表过滤与等级升序排列
  const filteredActions = useMemo(() => {
    let list: ActionDefinition[] = []
    if (craftingMode === 'alchemy') {
      if (alchemySubTab === 'extraction') {
        // 提炼：草木精华、伴灵精华等纯药材提炼（排除锻造精炼）
        list = availableActions.filter(a =>
          (a.id === 'craft.extract-herb-essence' ||
           a.id === 'craft.extract-banling-essence' ||
           a.id.startsWith('craft.extract-') ||
           a.id.includes('essence') ||
           a.id.includes('extract')) &&
          !a.id.startsWith('craft.refine-')
        )
      } else if (alchemySubTab === 'cultivation') {
        // 修炼丹药：引气丹、通脉丹
        list = availableActions.filter(a =>
          a.id === 'alchemy.refine-qi-pill' ||
          a.id === 'alchemy.refine-tongmai-pill' ||
          (a.type === 'alchemy' && !a.id.includes('zhixue') && !a.id.includes('shunqi') && !a.id.startsWith('craft.extract-') && !a.id.startsWith('forge.') && !a.id.startsWith('craft.refine-'))
        )
      } else {
        // 斗法药剂：止血散、顺气丸
        list = availableActions.filter(a =>
          a.id === 'alchemy.refine-zhixue-powder' ||
          a.id === 'alchemy.refine-shunqi-pill' ||
          a.id.includes('zhixue') ||
          a.id.includes('shunqi')
        )
      }
    } else {
      // 炼器：精炼、武器、头部、衣服、腿部、鞋履、饰品
      if (forgingSubTab === 'refining') {
        // 精炼：百炼铁、桃木晶、赤铜精、灵松晶等锻造灵材精炼
        list = availableActions.filter(a =>
          a.id === 'craft.refine-iron' ||
          a.id === 'craft.refine-peach-wood-crystal' ||
          a.id === 'craft.refine-copper' ||
          a.id === 'craft.refine-spirit-pine-crystal' ||
          a.id.startsWith('craft.refine-') ||
          (a.id.startsWith('craft.') && !a.id.startsWith('craft.extract-'))
        )
      } else {
        list = availableActions.filter(a => {
          for (const yieldEntry of a.yields ?? []) {
            if (yieldEntry.kind !== 'item' || !yieldEntry.id) continue
            const slot = equipmentSlotMap[yieldEntry.id]
            if (slot === forgingSubTab) return true
          }
          if (forgingSubTab === 'weapon' && (a.id.includes('sword') || a.id.includes('blade') || a.id.includes('staff') || a.id.includes('zither'))) return true
          if (forgingSubTab === 'head' && (a.id.includes('scarf') || a.id.includes('helmet') || a.id.includes('crown'))) return true
          if (forgingSubTab === 'armor' && (a.id.includes('armor') || a.id.includes('robe') || a.id.includes('shirt'))) return true
          if (forgingSubTab === 'legs' && (a.id.includes('pants') || a.id.includes('greaves') || a.id.includes('skirt'))) return true
          if (forgingSubTab === 'boots' && (a.id.includes('boots') || a.id.includes('shoes'))) return true
          if (forgingSubTab === 'trinket' && (a.id.includes('pendant') || a.id.includes('talisman') || a.id.includes('ring'))) return true
          return false
        })
      }
    }

    // 预计算所有候选行动的真实等级，将 O(N log N) 冗余解析降为 O(N) 线性预查
    const actionLevelMap = new Map<string, number>()
    for (const action of list) {
      actionLevelMap.set(action.id, resolveActionLevel(action, itemCatalog, equipmentCatalog, encounters))
    }

    // 严格按真实等级升序排序（低等级在前，高等级在后），等级相同时按 ID 稳定排列
    return [...list].sort((a, b) => {
      const levelA = actionLevelMap.get(a.id) ?? 1
      const levelB = actionLevelMap.get(b.id) ?? 1
      if (levelA !== levelB) return levelA - levelB
      return a.id.localeCompare(b.id)
    })
  }, [availableActions, craftingMode, alchemySubTab, forgingSubTab, equipmentSlotMap, itemCatalog, equipmentCatalog, encounters])

  return (
    <section className="panel skill-page crafting-page" hidden={hidden}>
      <header className="panel-header crafting-page-header">
        <h2>{t(`app.crafting.${craftingMode}`)}</h2>

        {/* 顶部子选项卡 */}
        {craftingMode === 'alchemy' ? (
          <div className="crafting-sub-tabs" role="tablist">
            {ALCHEMY_SUB_TABS.map(tab => (
              <button
                aria-selected={alchemySubTab === tab.id}
                key={tab.id}
                className={`crafting-sub-tab-btn${alchemySubTab === tab.id ? ' active' : ''}`}
                onClick={() => setAlchemySubTab(tab.id)}
                role="tab"
                type="button"
              >
                {t(`app.crafting.${tab.labelKey}`)}
              </button>
            ))}
          </div>
        ) : (
          <div className="crafting-sub-tabs" role="tablist">
            {FORGING_SUB_TABS.map(tab => (
              <button
                aria-selected={forgingSubTab === tab.id}
                key={tab.id}
                className={`crafting-sub-tab-btn${forgingSubTab === tab.id ? ' active' : ''}`}
                onClick={() => setForgingSubTab(tab.id)}
                role="tab"
                type="button"
              >
                {t(`app.crafting.${tab.labelKey}`)}
              </button>
            ))}
          </div>
        )}
      </header>

      <div className="action-cards">
        {filteredActions.map(action => <ActionCard action={action} key={action.id} onOpen={setSelectedAction} />)}
        {filteredActions.length === 0 && <p className="location-action-empty">{t('app.crafting.empty')}</p>}
      </div>
      <AutoUse type="alchemy" />
      {selectedAction && <ActionModal action={selectedAction} onClose={closeModal} />}
    </section>
  )
})


/**
 * 自动消耗品槽位组选择与配置组件
 */
const AutoUseSlotGroup = memo(function AutoUseSlotGroup({
  className = '',
  hideLabel = false,
  label,
  type,
}: {
  className?: string
  hideLabel?: boolean
  label: string
  type: ActionType
}): React.JSX.Element {
  const t = useT()
  const slots = useGameStore(state => state.queue.autoUseSlots[type])
  const activeEntry = useGameStore(state => {
    const current = state.queue.entries[0]
    return current?.type === type ? current : null
  })
  // 消耗品冷却仅 combat 类槽位由服务端按槽位下标定长下发。
  const consumableCooldowns = useGameStore(
    state => type === 'combat' ? state.combatResources?.consumableCooldownMilliseconds : undefined,
  )
  const inventory = useGameStore(state => state.inventory)
  const itemCatalog = useGameStore(state => state.items)
  const [pickerIndex, setPickerIndex] = useState<number | null>(null)
  const pickerItems = useMemo<ItemPickerEntry[]>(() => itemCatalog
      .filter(item => item.usableActionTypes.includes(type) && itemAmount(inventory, item.itemId) > 0)
      .map(item => ({
        itemId: item.itemId,
        name: item.name,
        description: item.description,
        category: resolveItemCategoryLabel(item.category, t),
        tier: item.tier,
        amount: itemAmount(inventory, item.itemId),
        definition: item,
      })), [inventory, itemCatalog, t, type])

  const updateSlot = useCallback((index: number, itemId: string | null) => {
    gameClient.sendCommand('queue.auto_use.configure', {
      actionType: type,
      slotIndex: index,
      itemId,
      quantity: 1,
      enabled: Boolean(itemId),
    })
  }, [type])

  const pickerSlot = pickerIndex === null ? null : slots[pickerIndex] ?? null

  return (
    <section aria-label={label} className={`slot-group ${className}`.trim()}>
      {!hideLabel && <span className="slot-group-label">{label}</span>}
      <div className="auto-use-slots">
        {slots.map((slot, index) => {
          const selectedDefinition = slot.itemId
            ? findItemInCatalog(itemCatalog, slot.itemId) ?? null
            : null
          const selectedItem = selectedDefinition ? {
            itemId: selectedDefinition.itemId,
            name: selectedDefinition.name,
            description: selectedDefinition.description,
            category: resolveItemCategoryLabel(selectedDefinition.category, t),
            tier: selectedDefinition.tier,
            amount: itemAmount(inventory, selectedDefinition.itemId),
            definition: selectedDefinition,
          } : null
          const cooldownMs = consumableCooldowns?.[index] ?? 0
          return (
            <div className="auto-use-slot" key={index}>
              <ItemUseSlot
                activeEntry={activeEntry}
                cooldownOverlay={cooldownMs > 0 && selectedItem?.definition ? (
                  <AbilityCooldownOverlay
                    remainingMilliseconds={cooldownMs}
                    totalMilliseconds={selectedItem.definition.cooldownSeconds * 1000}
                  />
                ) : null}
                item={selectedItem}
                remainingDurationSeconds={slot.remainingDurationSeconds}
                totalDurationSeconds={selectedDefinition?.durationSeconds}
                onClick={() => setPickerIndex(index)}
              />
            </div>
          )
        })}
      </div>
      <ItemGridPicker
        emptyLabel={t('common.pickerEmpty')}
        onClose={() => setPickerIndex(null)}
        onSelect={itemId => {
          if (pickerIndex === null) return
          updateSlot(pickerIndex, itemId)
        }}
        items={pickerItems}
        open={pickerIndex !== null}
        title={pickerIndex === null ? t('common.selectItem') : t('common.selectItemSlot', { n: pickerIndex + 1 })}
        value={pickerSlot?.itemId ?? null}
      />
    </section>
  )
})

/**
 * 各行动页签下的自动消耗品快捷托盘
 */
const AutoUse = memo(function AutoUse({ type }: { type: ActionType }): React.JSX.Element {
  const t = useT()
  return (
    <div className="auto-use-tray" aria-label={t('app.action.autoUseTrayAria', { name: actionTabLabel(type) })}>
      <AutoUseSlotGroup hideLabel label={t('app.action.autoUseLabel')} type={type} />
    </div>
  )
})

/**
 * 单个行动卡片组件（纯净形态：仅显示名称与右上角 Lv. 胶囊角标，品质色彩渲染整张卡片）
 */
const ActionCard = memo(function ActionCard({ action, onOpen }: { action: ActionDefinition; onOpen: (action: ActionDefinition) => void }): React.JSX.Element {
  const isCrafting = action.type === 'alchemy' || action.id.startsWith('craft.') || action.id.startsWith('forge.') || action.id.startsWith('alchemy.')
  const itemCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const encounters = useGameStore(state => state.encounters)

  const level = useMemo(
    () => resolveActionLevel(action, itemCatalog, equipmentCatalog, encounters),
    [action, itemCatalog, equipmentCatalog, encounters]
  )

  const rarity = useMemo(
    () => resolveActionRarity(action, itemCatalog, equipmentCatalog, encounters),
    [action, itemCatalog, equipmentCatalog, encounters]
  )

  const primaryOutputId = useMemo(
    () => action.yields?.find(yieldEntry => yieldEntry.kind === 'item' && yieldEntry.id)?.id ?? null,
    [action.yields]
  )
  const itemDef = useMemo(() => {
    if (!primaryOutputId) return undefined
    return findItemInCatalog(itemCatalog, primaryOutputId)
  }, [primaryOutputId, itemCatalog])

  const cardButton = (
    <button
      aria-haspopup="dialog"
      className={`action-card${isCrafting ? ' action-card--crafting' : ''}`}
      data-rarity={rarity}
      onClick={() => onOpen(action)}
      type="button"
    >
      <span className="item-corner item-corner--top-right">
        Lv. <UnitNumber disableTooltip value={level} />
      </span>
      <strong className="action-card-name">{action.name}</strong>
    </button>
  )

  if (isCrafting && primaryOutputId) {
    return (
      <ItemTooltipTarget
        className="action-card-tooltip-wrapper"
        definition={itemDef}
        itemId={primaryOutputId}
      >
        {cardButton}
      </ItemTooltipTarget>
    )
  }

  return cardButton
})

/**
 * 行动详情弹窗中的单个材料消耗/产出条目
 */
const ActionDetailItem = memo(function ActionDetailItem({ itemId, amount, current }: { itemId: string; amount: number; current?: number }): React.JSX.Element {
  const metadata = resolveItemMetadata(itemId)
  const insufficient = current !== undefined && current < amount
  return (
    <span className="action-detail-item">
      <ItemReference amount={current} itemId={itemId} label={metadata?.label ?? itemId} />
      <strong className={insufficient ? 'is-insufficient' : ''}>
        {current === undefined ? <UnitNumber maxDecimals={0} prefix="×" value={amount} /> : <><UnitNumber maxDecimals={0} value={current} /> / <UnitNumber maxDecimals={0} value={amount} /></>}
      </strong>
    </span>
  )
})

/**
 * 行动执行配置模态弹窗（设置循环轮次、查看消耗产出并加入行动队列）
 */
const ActionModal = memo(function ActionModal({ action, onClose }: { action: ActionDefinition; onClose: () => void }): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const items = useGameStore(state => state.items)
  const autoUseSlots = useGameStore(state => state.queue.autoUseSlots)
  const world = useGameStore(state => state.world)
  const stats = useGameStore(state => state.stats)
  const encounter = useGameStore(state => action.combatEncounterId ? findEncounterInCatalog(state.encounters, action.combatEncounterId) : undefined)
  const queueLength = useGameStore(state => state.queue.entries.length)
  const duel = useGameStore(state => state.duel)
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)
  const [repeats, setRepeats] = useState(-1)
  const [pvpEnabled, setPvpEnabled] = useState(false)
  const [, setClockTick] = useState(0)

  const yieldPreview = useMemo(() => calculateActionYieldPreview({
    action,
    inventory,
    items,
    autoUseSlots,
    world,
    stats,
  }), [action, inventory, items, autoUseSlots, world, stats])

  const rateBonuses = useMemo(
    () => yieldPreview.bonuses.filter(b => b.type !== 'foundation'),
    [yieldPreview.bonuses]
  )
  const totalMultiplier = yieldPreview.totalMultiplier
  const totalPercent = Math.round((totalMultiplier - 1.0) * 100)
  const hasRateBonus = rateBonuses.length > 0 && Math.abs(totalMultiplier - 1.0) > 0.001
  const isPending = action.type === 'cultivation' && Boolean(yieldPreview.cultivation?.isPendingBreakthrough)
  const hasBonuses = hasRateBonus || isPending

  const inputs = Object.entries(action.inputs ?? {}).map(([itemId, amount]) => ({ itemId, amount }))
  const titleId = `action-modal-title-${action.id}`
  const startsImmediately = action.executionKind === 'combat-encounter'
  const hasYields = (action.yields?.length ?? 0) > 0
  const isCrafting = action.type === 'alchemy' || action.id.startsWith('craft.') || action.id.startsWith('forge.') || action.id.startsWith('alchemy.')
  // 斗法冷却倒计时：以「服务端时刻 + 本地流逝」估算服务端当前时刻，仅冷却生效期间每秒重渲染。
  const estimatedServerNow = serverTime + Math.max(0, performance.now() - lastSyncPerformanceTime) / 1000
  const duelCooldownRemaining = Math.max(0, duel.cooldownUntilUnixSeconds - estimatedServerNow)
  useEffect(() => {
    if (!startsImmediately || duelCooldownRemaining <= 0) return undefined
    const timer = window.setInterval(() => setClockTick(value => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [startsImmediately, duelCooldownRemaining > 0])
  return (
    <Modal className="action-modal" labelledBy={titleId} onClose={onClose} open size="medium">
      <header className="action-modal-header">
        <span className={`action-glyph tone-${ACTION_TAB_META[action.type].accent}`} aria-hidden="true"><Icon name={action.type} /></span>
        <div>
          <h2 id={titleId}>{action.name}</h2>
          {!isCrafting && action.description ? <p>{action.description}</p> : null}
        </div>
      </header>

      <dl className="action-detail-list">
        <div>
          <dt>{t('app.action.needs')}</dt>
          <dd>{inputs.length === 0 ? <span className="action-detail-empty">{t('app.action.none')}</span> : inputs.map(input => (
            <ActionDetailItem
              amount={input.amount}
              current={itemAmount(inventory, input.itemId)}
              itemId={input.itemId}
              key={input.itemId}
            />
          ))}</dd>
        </div>
        {(hasYields || startsImmediately) && (
          <div>
            <dt>{t('app.action.outputs')}</dt>
            <dd>
              {yieldPreview.resourceYields.map((entry, index) => (
                <span className={`action-stat-pill action-stat-pill--${entry.kind}`} key={`${entry.kind}-${index}`}>
                  <span className="action-stat-pill-label">{t(`app.action.yieldKind.${entry.kind}`)}</span>
                  <strong className="action-stat-pill-value">
                    +<UnitNumber disableTooltip maxDecimals={0} value={entry.finalAmount} />{entry.per === 'second' ? t('app.action.yieldPerSecond') : t('app.action.yieldPerRepeat')}
                  </strong>
                </span>
              ))}
              {yieldPreview.itemOutputs.map(output => (
                <ActionDetailItem
                  amount={output.finalAmount}
                  itemId={output.itemId}
                  key={output.itemId}
                />
              ))}
              {yieldPreview.buffYields.map(buff => (
                <span className="action-stat-pill action-stat-pill--buff" key={buff.buffId}>
                  <span className="action-stat-pill-label">{t('app.action.yieldKind.buff')}</span>
                  <strong className="action-stat-pill-value">
                    {t('app.action.yieldBuff', { name: buff.name, stacks: buff.stacks, duration: buff.durationSeconds })}
                  </strong>
                </span>
              ))}
              {!hasYields && startsImmediately && (
                <span className="action-detail-empty">{t('app.action.combatDrops')}</span>
              )}
            </dd>
          </div>
        )}

        {hasBonuses && (
          <div>
            <dt>{t('app.action.totalBonus')}</dt>
            <dd className="action-bonuses-container">
              {hasRateBonus && (
                <FloatingTooltip
                  placement="top"
                  tone="gold"
                  title={
                    <div className="action-bonus-tooltip-content">
                      <div className="action-bonus-tooltip-title">{t('app.action.bonusBreakdownTitle')}</div>
                      <div className="action-bonus-tooltip-list">
                        {rateBonuses.map((bonus, idx) => (
                          <div className="action-bonus-tooltip-row" key={idx}>
                            <span className="action-bonus-tooltip-name">{bonus.sourceName}</span>
                            <strong className="action-bonus-tooltip-tag">{bonus.tag}</strong>
                          </div>
                        ))}
                      </div>
                    </div>
                  }
                >
                  <span className="action-bonus-pill action-bonus-pill--total">
                    <span className="action-bonus-pill-source">{t('app.action.totalBonus')}</span>
                    <strong className="action-bonus-pill-tag">
                      {totalPercent >= 0 ? `+${totalPercent}%` : `${totalPercent}%`}
                    </strong>
                  </span>
                </FloatingTooltip>
              )}

              {isPending && (
                <FloatingTooltip
                  placement="top"
                  tone="gold"
                  title={t('app.action.pendingBreakthroughDesc')}
                >
                  <span className="action-bonus-pill action-bonus-pill--pending">
                    <span className="action-bonus-pill-source">{t('app.action.pendingBreakthroughTag')}</span>
                  </span>
                </FloatingTooltip>
              )}
            </dd>
          </div>
        )}

        <div>
          <dt>{t('app.action.duration')}</dt>
          <dd><span className="action-detail-value">
            {startsImmediately ? t('app.action.combatUnlimited') : t('app.action.durationPerRound', { seconds: action.durationSeconds })}
          </span></dd>
        </div>
      </dl>

      {encounter && <CombatEncounterPanel compact encounter={encounter} />}

      {startsImmediately && (
        <section className="duel-option" aria-label={t('app.action.duel.title')}>
          <label className="duel-option-toggle">
            <input
              checked={pvpEnabled}
              onChange={event => setPvpEnabled(event.target.checked)}
              type="checkbox"
            />
            <strong>{t('app.action.duel.title')}</strong>
            <span className="badge badge-duel">{t('app.queue.duelBadge')}</span>
          </label>
          <ul className="duel-option-desc">
            <li>{t('app.action.duel.descMatch')}</li>
            <li>{t('app.action.duel.descDrop')}</li>
            <li>{t('app.action.duel.descStaging')}</li>
            <li>{t('app.action.duel.descPenalty')}</li>
          </ul>
          <div className="duel-option-status">
            <span>{t('app.action.duel.charges', { n: duel.encounterCharges })}</span>
            {duelCooldownRemaining > 0 && (
              <span className="duel-cooldown">{t('app.action.duel.cooldown', { time: formatRemainingSeconds(duelCooldownRemaining) })}</span>
            )}
          </div>
        </section>
      )}

      <div className="repeat-row">
        <span>{t('app.action.repeats')}</span>
        <QuantitySelector
          allowInfinity
          ariaLabel={t('app.action.repeatsAria')}
          max={1000}
          onChange={setRepeats}
          presets={[1, 10, 100]}
          value={repeats}
        />
      </div>

      <button
        className="action-modal-submit"
        onClick={() => {
          if (gameClient.sendCommand('action.enqueue', {
            actionId: action.id,
            repeatCount: repeats,
            pvpEnabled,
          })) {
            onClose()
            if (action.type === 'combat' || startsImmediately) {
              useUiStore.getState().setActiveTab('combat')
            }
          }
        }}
        type="button"
      >{startsImmediately ? t('app.action.startCombatNow') : t('app.action.enqueueAction', { n: queueLength + 1 })}</button>
    </Modal>
  )
})

type CombatPickerTarget = {
  kind: 'ability'
  index: number
}

/**
 * 技能与物品槽位冷却扇形遮罩覆盖层
 */
const AbilityCooldownOverlay = memo(function AbilityCooldownOverlay({
  remainingMilliseconds,
  totalMilliseconds,
}: {
  remainingMilliseconds: number
  totalMilliseconds: number
}): React.JSX.Element {
  const overlayRef = useRef<HTMLSpanElement>(null)

  useLayoutEffect(() => {
    const overlay = overlayRef.current
    if (!overlay) return
    const startedAt = performance.now()
    const initialRemaining = Math.max(0, remainingMilliseconds)
    const total = Math.max(1, totalMilliseconds)
    let animationFrame = 0

    const update = () => {
      const remaining = Math.max(0, initialRemaining - (performance.now() - startedAt))
      overlay.style.setProperty('--cooldown-ratio', String(Math.min(1, remaining / total)))
      overlay.textContent = remaining > 0 ? `${(remaining / 1000).toFixed(1)}s` : ''
      overlay.hidden = remaining <= 0
      if (remaining > 0) animationFrame = window.requestAnimationFrame(update)
    }

    update()
    return () => window.cancelAnimationFrame(animationFrame)
  }, [remainingMilliseconds, totalMilliseconds])

  return <span className="combat-ability-cooldown" ref={overlayRef} />
})

/**
 * 战斗装配面板（配置功法技能槽与释放条件触发器；战斗消耗品槽统一走
 * queue.autoUseSlots 通道，由 AutoUseSlotGroup 渲染，loadout 不再重复携带）
 */
const CombatLoadoutPanel = memo(function CombatLoadoutPanel(): React.JSX.Element | null {
  const t = useT()
  const combat = useGameStore(state => state.combat)
  const abilities = useGameStore(state => state.abilities)
  const abilityCooldowns = useGameStore(
    state => state.combatResources?.abilityCooldownMilliseconds,
  ) ?? EMPTY_ABILITY_COOLDOWNS
  const [editingTrigger, setEditingTrigger] = useState<{
    kind: 'ability'
    index: number
  } | null>(null)
  const [pickerTarget, setPickerTarget] = useState<CombatPickerTarget | null>(null)
  // hooks 必须在早退之前调用：combat 在 undefined 与对象间切换时保持 hook 数量稳定。
  const learnedAbilityIds = useMemo(() => new Set(combat?.learnedAbilityIds ?? []), [combat?.learnedAbilityIds])
  const learnedAbilities = useMemo(() => {
    return abilities.filter(ability => learnedAbilityIds.has(ability.abilityId))
  }, [abilities, learnedAbilityIds])
  if (!combat) return null

  // 装配变更统一经 sendLoadoutReplace 发送：内部完成槽位类别互斥与等级归一化。
  const update = (patch: Partial<typeof combat>) => sendLoadoutReplace(patch)
  // 已参悟等级由功法经验经立方曲线导出，与服务端 AbilityExpCurve 同式。
  const abilityLevelFor = (abilityId: string) => learnedAbilityLevel(combat.abilityExperiences, abilityId)
  // 重复装配按「移动」处理：assignAbilityToSlot 会把功法从原槽位移到新槽位。
  const withAbility = (index: number, abilityId: string | null) => assignAbilityToSlot(combat.abilitySlots, index, abilityId)
  const getTriggers = (target: NonNullable<typeof editingTrigger>): CombatTrigger[] =>
    combat.abilitySlots[target.index]?.triggers ?? []
  const saveTriggers = (triggers: CombatTrigger[] | null) => {
    if (!editingTrigger) return
    const nextTriggers = triggers ?? []
    update({ abilitySlots: combat.abilitySlots.map((slot, index) => index === editingTrigger.index ? { ...slot, triggers: nextTriggers } : slot) })
  }

  const pickerValue = pickerTarget === null
    ? null
    : combat.abilitySlots[pickerTarget.index]?.abilityId ?? null
  const pickerTitle = pickerTarget === null
      ? ''
      : (pickerTarget.index === 0 ? t('app.combatPanel.chooseInternalTitle') : t('app.combatPanel.chooseSpellTitle', { slot: pickerTarget.index }))
  const selectPickerValue = (value: string | null) => {
    if (!pickerTarget) return
    update({ abilitySlots: withAbility(pickerTarget.index, value) })
  }

  return (
    <div className="combat-loadout" aria-label={t('app.combatPanel.loadoutAria')}>
      <div className="combat-slot-tray">
        <AutoUseSlotGroup className="combat-slot-section" label={t('app.combatPanel.consumablesGroup')} type="combat" />

        <section className="slot-group combat-slot-section" aria-label={t('app.combatPanel.abilitiesAria')}>
          <span className="slot-group-label">{t('app.combatPanel.abilitiesGroup')}</span>
          <div className="combat-slot-grid combat-slot-grid--abilities">
            {combat.abilitySlots.map((slot, index) => {
              const isInternal = index === 0
              const ability = slot.abilityId ? findAbilityInCatalog(abilities, slot.abilityId) : undefined
              const abilityIconUrl = resolveItemIconUrl(ability?.abilityId)
              const slotTypeTag = isInternal
                ? t('app.combatPanel.slotInternal')
                : t('app.combatPanel.slotSpell', { n: index })
              const ribbonLabel = isInternal
                ? t('app.combatPanel.internalRibbon')
                : t('app.combatPanel.spellRibbon')
              const emptyBtnLabel = isInternal
                ? t('app.combatPanel.chooseInternal')
                : t('app.combatPanel.chooseSpell')
              const slotAriaLabel = ability
                ? t('app.combatPanel.currentAbilityAria', { name: ability.name })
                : isInternal
                  ? t('app.combatPanel.chooseInternalSlotAria')
                  : t('app.combatPanel.chooseSpellSlotAria', { slot: index })

              const slotContent = (
                <>
                  <button
                    aria-label={slotAriaLabel}
                    className={`combat-ability-slot${isInternal ? ' is-internal-slot' : ' is-spell-slot'}${ability ? '' : ' combat-ability-slot--empty'}`}
                    onClick={() => setPickerTarget({ kind: 'ability', index })}
                    type="button"
                  >
                    <span className={`abilities-slot-tag-badge${isInternal ? ' is-internal' : ''}`}>
                      {slotTypeTag}
                    </span>
                    {ability ? (
                      <span className="item-tile combat-ability-cell" data-rarity={isInternal ? 'emperor' : 'mystic'}>
                        <span className="item-type-ribbon" aria-hidden="true"><span>{ribbonLabel}</span></span>
                        {abilityIconUrl
                          ? <img alt={ability.name} className="item-tile-icon" decoding="async" draggable={false} src={abilityIconUrl} />
                          : <strong className="item-tile-name">{ability.name}</strong>}
                        <span className="item-corner item-corner--bottom-right">{t('app.combatPanel.manaCost', { mana: Math.round(ability.manaCost ?? 0) })}</span>
                        {(abilityCooldowns[ability.abilityId] ?? 0) > 0 && (
                          <AbilityCooldownOverlay
                            remainingMilliseconds={abilityCooldowns[ability.abilityId]}
                            totalMilliseconds={ability.cooldownMilliseconds ?? 0}
                          />
                        )}
                        <span aria-hidden="true" className="item-tile-glow" />
                      </span>
                    ) : (
                      <span className="abilities-slot-empty-label">{emptyBtnLabel}</span>
                    )}
                  </button>
                  {ability && (
                    <>
                      {/* 装配等级恒等于已参悟等级（调低无收益），展示为静态徽标而非输入框 */}
                      <span className="combat-ability-level-control">
                        <span aria-hidden="true">Lv.</span>
                        <span className="combat-ability-level-value">{abilityLevelFor(ability.abilityId)}</span>
                      </span>
                      <button
                        aria-label={t('app.combatPanel.editTriggersAria', { name: ability.name, count: slot.triggers?.length ?? 0 })}
                        className="item-slot-inline-action item-slot-inline-action--condition"
                        onClick={() => setEditingTrigger({ kind: 'ability', index })}
                        title={t('app.combatPanel.editTriggersTitle')}
                        type="button"
                      >{t('app.combatPanel.triggerConditionBtn', { count: (slot.triggers?.length ?? 0) > 0 ? (slot.triggers?.length ?? 0) : '' })}</button>
                    </>
                  )}
                </>
              )
              return (
                <div className="combat-slot-card" key={index}>
                  {ability ? (
                    <EffectTooltipTarget className="combat-ability-slot-shell" content={describeAbility(ability, abilityLevelFor(ability.abilityId))} tabIndex={-1}>
                      {slotContent}
                    </EffectTooltipTarget>
                  ) : <span className="combat-ability-slot-shell">{slotContent}</span>}
                </div>
              )
            })}
          </div>
        </section>
      </div>

      <AbilityGridPicker
        abilities={learnedAbilities}
        allowEmpty
        levelFor={abilityLevelFor}
        onClose={() => setPickerTarget(null)}
        onSelect={selectPickerValue}
        open={pickerTarget !== null}
        slotIndex={pickerTarget?.index}
        title={pickerTitle}
        value={pickerValue}
      />
      {editingTrigger && (
        <CombatTriggerEditor
          initialTriggers={getTriggers(editingTrigger)}
          onClose={() => setEditingTrigger(null)}
          onSave={saveTriggers}
          open
          title={t('app.combatPanel.triggerEditorTitle', {
            kind: t('app.rightTab.abilities'),
            n: editingTrigger.index + 1,
          })}
        />
      )}
    </div>
  )
})

/**
 * 通用单类行动列表页面（修炼、炼丹）
 */
const SkillPage = memo(function SkillPage({ type, hidden }: { type: ActionType; hidden: boolean }): React.JSX.Element {
  const t = useT()
  const actions = useGameStore(state => state.actions)
  const worldNodeId = useGameStore(state => state.world.nodeId)
  const [selectedAction, setSelectedAction] = useState<ActionDefinition | null>(null)
  const visibleActions = useMemo(() => actions.filter(action => (
    action.type === type
      && (!action.availableNodeIds?.length || (worldNodeId !== null && action.availableNodeIds.includes(worldNodeId)))
  )), [actions, type, worldNodeId])
  const closeModal = useCallback(() => setSelectedAction(null), [])

  return (
    <section className="panel skill-page" hidden={hidden}>
      <header className="panel-header">
        <h2>{t(`app.nav.${type}`)}</h2>
      </header>
      <div className="action-cards">
        {visibleActions.map(action => <ActionCard action={action} key={action.id} onOpen={setSelectedAction} />)}
        {visibleActions.length === 0 && <p className="location-action-empty">{t('app.action.locationActionEmpty')}</p>}
      </div>
      <AutoUse type={type} />
      {selectedAction && <ActionModal action={selectedAction} onClose={closeModal} />}
    </section>
  )
})

/**
 * 历练页面：显示当前地点的可选特殊行动（采集等）与妖兽历练行动
 */
const ExplorationPage = memo(function ExplorationPage({ hidden }: { hidden: boolean }): React.JSX.Element {
  const t = useT()
  const actions = useGameStore(state => state.actions)
  const worldNodeId = useGameStore(state => state.world.nodeId)
  const world = useGameStore(state => state.world)
  const [selectedAction, setSelectedAction] = useState<ActionDefinition | null>(null)

  const visibleActions = useMemo(() => actions.filter(action => (
    !action.hiddenFromCatalog
      && action.executionKind !== 'travel'
      && (action.type === 'exploration' || action.type === 'combat')
      && (!action.availableNodeIds?.length || (worldNodeId !== null && action.availableNodeIds.includes(worldNodeId)))
  )), [actions, worldNodeId])

  const specialActions = useMemo(() => visibleActions.filter(action => action.type === 'exploration'), [visibleActions])
  const combatActions = useMemo(() => visibleActions.filter(action => action.type === 'combat'), [visibleActions])

  const closeModal = useCallback(() => setSelectedAction(null), [])

  return (
    <section className="panel skill-page" hidden={hidden}>
      <header className="panel-header">
        <h2>{t('app.nav.exploration')}</h2>
        {world.locationName && <span className="location-sub-badge">{world.locationName}</span>}
      </header>
      <div className="action-sections">
        {specialActions.length > 0 && (
          <div className="action-group-section">
            <span className="slot-group-label">{t('app.combatPanel.locationSpecialActions')}</span>
            <div className="action-group-list">
              {specialActions.map(action => <ActionCard action={action} key={action.id} onOpen={setSelectedAction} />)}
            </div>
          </div>
        )}
        {combatActions.length > 0 && (
          <div className="action-group-section">
            <span className="slot-group-label">{t('app.combatPanel.combatTrialActions')}</span>
            <div className="action-group-list">
              {combatActions.map(action => <ActionCard action={action} key={action.id} onOpen={setSelectedAction} />)}
            </div>
          </div>
        )}
        {visibleActions.length === 0 && <p className="location-action-empty">{t('app.action.locationActionEmpty')}</p>}
      </div>
      <AutoUse type="exploration" />
      {selectedAction && <ActionModal action={selectedAction} onClose={closeModal} />}
    </section>
  )
})

type MapSubTab = 'map' | NearbyTab

const MAP_TABS: ReadonlyArray<{ id: MapSubTab; labelKey: 'tabMap' | 'tabMonsters' | 'tabPeople' | 'tabBuildings' }> = [
  { id: 'map', labelKey: 'tabMap' },
  { id: 'monsters', labelKey: 'tabMonsters' },
  { id: 'people', labelKey: 'tabPeople' },
  { id: 'buildings', labelKey: 'tabBuildings' },
]

/**
 * 大地图与周边环境页面（包含修仙大地图与当前节点同域道友/灵地建筑）
 */
const MapPage = memo(function MapPage({ hidden }: { hidden: boolean }): React.JSX.Element {
  const t = useT()
  const [activeSubTab, setActiveSubTab] = useState<MapSubTab>('map')

  return (
    <section className="panel expedition-page" hidden={hidden}>
      <header className="panel-header expedition-header">
        <nav aria-label={t('app.combatPanel.tabsAria')} className="expedition-tabs" role="tablist">
          {MAP_TABS.map(tab => (
            <button
              aria-selected={activeSubTab === tab.id}
              className={activeSubTab === tab.id ? 'active' : ''}
              key={tab.id}
              onClick={() => setActiveSubTab(tab.id)}
              role="tab"
              type="button"
            >{t(`app.combatPanel.${tab.labelKey}`)}</button>
          ))}
        </nav>
      </header>
      <div className="expedition-content">
        {activeSubTab === 'map' && <WorldMapPanel hidden={hidden} />}
        {activeSubTab !== 'map' && <NearbyPanel tab={activeSubTab} />}
      </div>
    </section>
  )
})

/**
 * 战斗与战备详情页面（包含九宫格战场对决、战况实时监控、功法与战斗消耗品装配）
 */
const CombatDetailsPage = memo(function CombatDetailsPage({ hidden }: { hidden: boolean }): React.JSX.Element {
  const t = useT()
  const [selectedAction, setSelectedAction] = useState<ActionDefinition | null>(null)
  const [reportOpen, setReportOpen] = useState(false)
  const closeModal = useCallback(() => setSelectedAction(null), [])
  const closeReport = useCallback(() => setReportOpen(false), [])

  return (
    <section className="panel expedition-page" hidden={hidden}>
      <header className="panel-header">
        <h2>{t('app.nav.combat')}</h2>
        <button
          className="ghost-button battle-report-open"
          onClick={() => setReportOpen(true)}
          type="button"
        >
          {t('combat.report.title')}
        </button>
      </header>
      <div className="expedition-content expedition-content--duel">
        <div className="expedition-duel">
          <CombatArena onOpenAction={setSelectedAction} />
          <div className="expedition-duel-config">
            <CombatLoadoutPanel />
          </div>
        </div>
      </div>
      <Modal
        className="battle-report-modal"
        labelledBy="battle-report-panel-title"
        onClose={closeReport}
        open={reportOpen}
        size="medium"
      >
        <StatsPanel />
      </Modal>
      {selectedAction && <ActionModal action={selectedAction} onClose={closeModal} />}
    </section>
  )
})



const BOTTOM_TABS: ReadonlyArray<{ tab: TabId; labelKey: 'insight' | 'market' | 'cultivation' | 'exploration' | 'crafting' | 'map' | 'combat' | 'inventory' | 'log' | 'settings'; icon: IconName }> = [
  { tab: 'insight', labelKey: 'insight', icon: 'insight' },
  { tab: 'market', labelKey: 'market', icon: 'market' },
  { tab: 'cultivation', labelKey: 'cultivation', icon: 'cultivation' },
  { tab: 'exploration', labelKey: 'exploration', icon: 'exploration' },
  { tab: 'crafting', labelKey: 'crafting', icon: 'crafting' },
  { tab: 'map', labelKey: 'map', icon: 'map' },
  { tab: 'combat', labelKey: 'combat', icon: 'combat' },
  { tab: 'inventory', labelKey: 'inventory', icon: 'inventory' },
  { tab: 'log', labelKey: 'log', icon: 'log' },
  { tab: 'settings', labelKey: 'settings', icon: 'settings' },
]

/**
 * 移动端底部快捷导航栏
 */
const BottomNav = memo(function BottomNav(): React.JSX.Element {
  const t = useT()
  const activeTab = useUiStore(state => state.activeTab)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  return (
    <nav className="bottom-nav" aria-label={t('app.nav.mobileNavAria')}>
      {BOTTOM_TABS.map(item => (
        <button
          className={activeTab === item.tab ? 'active' : ''}
          key={item.tab}
          onClick={() => setActiveTab(item.tab)}
          type="button"
        >
          <span className="bottom-glyph" aria-hidden="true"><Icon name={item.icon} /></span>
          <span>{t(`app.nav.${item.labelKey}`)}</span>
        </button>
      ))}
    </nav>
  )
})

/**
 * 全局浮动操作提示气泡（自动定时消失）
 */
const Notice = memo(function Notice(): React.JSX.Element | null {
  const notice = useGameStore(state => state.notice)
  const setNotice = useGameStore(state => state.setNotice)

  useEffect(() => {
    if (!notice) return undefined
    const timer = window.setTimeout(() => setNotice(null), 2600)
    return () => window.clearTimeout(timer)
  }, [notice, setNotice])

  return notice ? <div className="notice" role="status">{notice}</div> : null
})

/**
 * 道化太初客户端顶层根组件
 * 负责应用初始化、鉴权状态路由（登录页 / 连接加载页 / 错误重试页 / 游戏主舞台）以及响应式多列布局渲染
 */
export default function App(): React.JSX.Element {
  const activeTab = useUiStore(state => state.activeTab)
  const mobileLayout = useMobileLayout()
  const connection = useGameStore(state => state.connection)
  const hasReceivedSnapshot = useGameStore(state => state.hasReceivedSnapshot)
  const tabBlocked = useAntiMultiOpenStore(state => state.tabBlocked)
  const currentAccount = useAccountStore(state => state.currentAccount)
  const sessionChecked = useAccountStore(state => state.sessionChecked)
  const restoreSession = useAccountStore(state => state.restoreSession)
  const logout = useAccountStore(state => state.logout)
  const [catalogAttempt, setCatalogAttempt] = useState(0)
  const [catalogError, setCatalogError] = useState<string | null>(null)
  const [catalogReady, setCatalogReady] = useState(false)

  useEffect(() => {
    installAntiMultiOpen()
    initUiScale()
    void restoreSession()
    return () => gameClient.dispose()
  }, [restoreSession])

  // 服务端拒绝 Hello（配表指纹不匹配）时重新拉取配表：成功后经 catalogReady 路径自动重连。
  useEffect(() => {
    const onGameDataMismatch = () => setCatalogAttempt(attempt => attempt + 1)
    window.addEventListener(GAME_DATA_MISMATCH_EVENT, onGameDataMismatch)
    return () => window.removeEventListener(GAME_DATA_MISMATCH_EVENT, onGameDataMismatch)
  }, [])

  useEffect(() => {
    if (!sessionChecked || !currentAccount) return undefined
    const controller = new AbortController()
    setCatalogError(null)
    setCatalogReady(false)
    void fetchGameCatalog(controller.signal)
      .then(catalog => {
        const localizedActions = catalog.actions.map(action => {
          const fallback = resolveActionDefinition(action.id)
          return fallback ? { ...action, name: fallback.name, description: fallback.description } : action
        })
        useGameStore.getState().setActions(localizedActions)
        useGameStore.getState().setMaps(catalog.maps)
        useGameStore.getState().setEncounters(catalog.encounters)
        useGameStore.getState().setAbilities(catalog.abilities)
        useGameStore.getState().setBuildings(catalog.buildings)
        if (catalog.items && catalog.items.length > 0) {
          useGameStore.getState().setItems(catalog.items)
        }
        if (catalog.equipment && catalog.equipment.length > 0) {
          useGameStore.getState().setEquipment(catalog.equipment)
        }
        setCatalogReady(true)
        gameClient.connect()
      })
      .catch(error => {
        if (!controller.signal.aborted) {
          setCatalogError(error instanceof Error ? error.message : getT()('app.messages.catalogLoadFailed'))
          useGameStore.getState().setNotice(
            error instanceof Error
              ? getT()('app.messages.catalogLoadFailedDetail', { message: error.message })
              : getT()('app.messages.catalogLoadFailed')
          )
        }
      })
    return () => controller.abort()
  }, [currentAccount, sessionChecked, catalogAttempt])

  if (tabBlocked) {
    return <MultiTabBlockedPage />
  }

  if (!currentAccount) {
    return <LoginPage />
  }

  if (catalogError) {
    return <ConnectionErrorPage message={catalogError} onRetry={() => setCatalogAttempt(attempt => attempt + 1)} onLogout={logout} />
  }

  if (!catalogReady) return <ConnectingPage onCancel={logout} />

  if (!hasReceivedSnapshot) {
    if (connection === 'connecting' || connection === 'reconnecting' || connection === 'online') {
      return <ConnectingPage onCancel={logout} />
    }
    return <ConnectionErrorPage onRetry={() => gameClient.connect()} onLogout={logout} />
  }

  return (
    <div className="app">
      <div className="motes" aria-hidden="true"><i /><i /><i /><i /><i /><i /></div>
      <TopBar />
      <div className="app-body">
        <Sidebar />
        <div className="center-column">
          <main className="main-content">
            {activeTab === 'insight' && <InsightPage />}
            <SkillPage hidden={activeTab !== 'cultivation'} type="cultivation" />
            <ExplorationPage hidden={activeTab !== 'exploration'} />
            <CraftingPage hidden={activeTab !== 'crafting'} />
            <MapPage hidden={activeTab !== 'map'} />
            <CombatDetailsPage hidden={activeTab !== 'combat'} />
            {activeTab === 'market' && <MarketPage />}
            {activeTab === 'settings' && <SettingsPage />}
            {activeTab === 'inventory' && <RightSidebar className="mobile-tab" />}
            {activeTab === 'log' && mobileLayout && <GameLogPanel className="mobile-tab" />}
          </main>
          <div className="desktop-log">{!mobileLayout && <GameLogPanel />}</div>
        </div>
        <aside className="right-column"><RightSidebar /></aside>
      </div>
      <BottomNav />
      <Notice />
    </div>
  )
}
