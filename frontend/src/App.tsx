import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { ConfirmDialog } from './components/ConfirmDialog'
import { AbilityGridPicker } from './components/AbilityGridPicker'
import { StatsPanel } from './components/StatsPanel'
import { RecordsPanel } from './components/RecordsPanel'
import { CombatArena } from './components/CombatArena'
import { CombatTriggerEditor } from './components/CombatTriggerEditor'
import { CombatEcologyPanel } from './components/CombatEcologyPanel'
import { EffectTooltipTarget } from './components/EffectTooltip'
import { EnhancementPage } from './components/EnhancementPage'
import { FloatingInfoCard, FloatingPopover, FloatingTooltip } from './components/FloatingPopover'
import { GameLogPanel } from './components/GameLogPanel'
import { ActionDetailItem, ItemTooltipTarget, describeBuffEffectLines } from './components/ItemDisplay'
import { ItemGridPicker, ItemUseSlot, type ItemPickerEntry } from './components/ItemGridPicker'
import { MarketPage } from './components/MarketPage'
import { Modal } from './components/Modal'
import { InsightPage } from './components/InsightPage'
import type { PillTone } from './components/PillLabel'
import { RealmBadge } from './components/RealmBadge'
import { NearbyPanel, type NearbyTab } from './components/NearbyPanel'
import { QuantitySelector } from './components/QuantitySelector'
import { WorldMapPanel } from './components/WorldMapPanel'
import { SettingsPage } from './components/SettingsPage'
import { LoginPage } from './components/LoginPage'
import { ConnectingPage, ConnectionErrorPage } from './components/ConnectingPage'
import { RightSidebar } from './components/RightSidebar'
import { SocialPage } from './components/SocialPage'
import { TaijiToggle } from './components/TaijiToggle'
import { describeAbility, formatChineseSplitNumber, formatNumber, formatProgressText, formatRemainingSeconds } from './game/format'
import { UnitNumber } from './components/UnitNumber'
import { calculateRealmInfo, calculateRealmProgress, calculateTechniqueProgress, TECHNIQUE_IDS, TECHNIQUE_SPEED_RATIO_PER_LEVEL } from './game/combatConstants'
import { ACTION_TAB_META, actionTabLabel, assignAbilityToSlot, findAbilityInCatalog, findActionInCatalog, findEncounterInCatalog, findItemInCatalog, learnedAbilityLevel, localizeActionDisplayText, resolveActionDefinition, resolveActionLevel, resolveActionRarity, resolveItemCategoryLabel, resolveItemSubCategory } from './game/ids'
import { useGameStore } from './game/gameStore'
import { sendLoadoutReplace } from './game/loadoutCommands'
import { resolveItemIconUrl } from './game/itemIcons'
import { calculateActionYieldPreview, isActionAvailableAtNode } from './game/actionCalculations'
import { useSmoothProgressRef } from './game/useSmoothProgress'
import type { ActionDefinition, ActionType, CombatTrigger, EquipmentSlot, InventoryItem, QueueEntry, WorldMapDefinition, WorldState } from './game/types'
import { gameClient, GAME_DATA_MISMATCH_EVENT } from './network/gameClient'
import { installAntiMultiOpen, disposeAntiMultiOpen, useAntiMultiOpenStore } from './antiMulti'
import { MultiTabBlockedPage } from './antiMulti/MultiTabBlockedPage'
import { useAccountStore } from './network/account'
import { fetchGameCatalog } from './network/apiClient'
import { useUiStore, type TabId, type CraftingMode, type AlchemySubTab, type ForgingSubTab } from './uiStore'
import { useT, getT } from './i18n'

/** 全局共享的空冷却表（冻结对象）：避免每次渲染创建新引用触发下游 memo 失效 */
const EMPTY_ABILITY_COOLDOWNS: Readonly<Record<string, number>> = Object.freeze({})

type IconName = 'insight' | ActionType | 'crafting' | 'forging' | 'talisman' | 'formation' | 'map' | 'market' | 'social' | 'inventory' | 'log' | 'settings' | 'spirit' | 'buff' | 'loop' | 'travel'

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
    case 'talisman':
      return <svg {...common}><path d="M7 3h10v18l-2.5-1.8L12 21l-2.5-1.8L7 21V3Z" /><path d="m10 7 4 4-4 4" /></svg>
    case 'formation':
      return <svg {...common}><circle cx="12" cy="12" r="8" /><path d="M12 4v3M12 17v3M4 12h3M17 12h3" /><path d="M12 8.5 15.5 12 12 15.5 8.5 12 12 8.5Z" /></svg>
    case 'enhancement':
      return <svg {...common}><path d="m12 3 6 6h-3.5v6h-5V9H6l6-6Z" /><path d="M5 20h14" /></svg>
    case 'map':
      return <svg {...common}><path d="M3 6l6-3 6 3 6-3v15l-6 3-6-3-6 3V6Z" /><path d="M9 3v15M15 6v15" /></svg>
    case 'combat':
      return <svg {...common}><path d="m5 4 15 15M19 4 4 19" /><path d="m7 7 3-3 3 3M17 17l-3 3-3-3" /></svg>
    case 'market':
      return <svg {...common}><path d="M4 8.5h16M6 8.5V20h12V8.5" /><path d="m5 4h14l1 4.5H4L5 4Z" /><path d="M9 12h6M9 15.5h6" /></svg>
    case 'social':
      return <svg {...common}><circle cx="8.5" cy="8" r="3.2" /><circle cx="16.5" cy="9.5" r="2.6" /><path d="M3.5 19.5c.6-3.4 2.6-5.4 5-5.4s4.4 2 5 5.4M13.5 19.5c.4-2.6 1.6-4.2 3.2-4.2 1.5 0 2.7 1.4 3.3 3.6" /></svg>
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
    case 'loop':
      return <svg {...common}><path d="M5.5 12.5a6.5 6.5 0 0 1 11.5-4.2" /><path d="M17.5 3.5v4h-4" /><path d="M18.5 11.5a6.5 6.5 0 0 1-11.5 4.2" /><path d="M6.5 20.5v-4h4" /></svg>
    case 'travel':
      return <svg {...common}><circle cx="5" cy="18.5" r="1.8" /><path d="M7.8 18.5h4.7a4 4 0 0 0 4-4v-2a3 3 0 0 1 3-3" /><path d="m16.5 6.5 3 3-3 3" /></svg>
  }
})

/** 查询背包中指定物品的持有数量（未持有返回 0）；只计基础变体，与服务端 AssetKey(itemId) 空变体校验一致 */
function itemAmount(inventory: InventoryItem[], itemId: string): number {
  return inventory.find(item => item.itemId === itemId && !item.variant)?.amount ?? 0
}

/** 解析队列条目显示名：优先 wire 携带的 actionName，否则按 actionId 回查配表；传入 world+maps 时按当前驻留地点本地化 */
function resolveQueueEntryName(
  entry: QueueEntry,
  actions?: readonly ActionDefinition[],
  world?: WorldState,
  maps?: readonly WorldMapDefinition[],
): string {
  const name = (entry.actionName && entry.actionName !== entry.actionId)
    ? entry.actionName
    : ((actions ? findActionInCatalog(actions, entry.actionId) : undefined) ?? resolveActionDefinition(entry.actionId))?.name
      || entry.actionName || entry.actionId
  const localized = world && maps ? localizeActionDisplayText(name, world, maps) : name
  // 目标型行动（装备强化）：名称后缀拼接目标装备名与目标强化等级
  if (entry.targetItemId) {
    const targetName = useGameStore.getState().items.find(item => item.itemId === entry.targetItemId)?.name ?? entry.targetItemId
    const levelSuffix = entry.targetLevel != null && entry.targetLevel > 0 ? ` +${Math.floor(entry.targetLevel)}` : ''
    return `${localized} · ${targetName}${levelSuffix}`
  }
  return localized
}

/** 解析队列条目的描述文本（配表 description），并按当前驻留地点做地点化文本替换 */
function actionDescription(entry: QueueEntry, actions?: readonly ActionDefinition[], world?: WorldState, maps?: readonly WorldMapDefinition[]): string {
  const def = (actions ? findActionInCatalog(actions, entry.actionId) : undefined) ?? resolveActionDefinition(entry.actionId)
  const description = def?.description ?? resolveQueueEntryName(entry, actions)
  return world && maps ? localizeActionDisplayText(description, world, maps) : description
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

/** 历练遭遇战相位名 → i18n 词条（搜索敌/战斗中/研读/死亡回城/斗法等待/斗法中） */
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

/** 遭遇战相位进度条：按相位名展示标签 + 平滑进度（复用 useSmoothProgressRef 直写 DOM） */
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

  // 己方上限取自战斗资源分段；敌方总量为同波全体敌人当前/最大血量之和（上限走 wire 常驻标量），
  // 斗法相位取回放载荷中的对手镜像血量。
  const playerMax = Math.max(1, combatResources?.maxHitpoints ?? combatAttributes?.maxHitpoints ?? 1)
  const playerValue = Math.max(0, Math.min(playerMax, expedition?.playerHitpoints ?? playerMax))
  let enemyValue = 0
  let enemyMax = 0
  if (expedition?.phase === 'duel') {
    enemyMax = Math.max(1, expedition.duel?.opponentMaxHitpoints ?? expedition.duel?.opponentAttributes?.maxHitpoints ?? 1)
    enemyValue = Math.max(0, Math.min(enemyMax, expedition.duel?.opponentHitpoints ?? enemyMax))
  } else {
    // 敌方上限取快照常驻标量（血蓝条分母），不再反查怪物目录，也不以当前血量充当上限。
    for (const enemy of expedition?.enemies ?? []) {
      enemyMax += Math.max(1, enemy.maxHitpoints ?? enemy.attributes?.maxHitpoints ?? 1)
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
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const queuePaused = useGameStore(state => state.queue.paused)
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
          <strong>{resolveQueueEntryName(entry, undefined, world, maps)}</strong>
          <span className="qc-repeats">{formatRepeatsLabel(entry.remainingRepeats, t)}</span>
          {(entry.status === 'blocked' || phaseLabel) && (
            <span className={entry.status === 'blocked' ? 'badge badge-danger' : 'badge'}>
              {entry.status === 'blocked' ? t('app.queue.blocked') : phaseLabel}
            </span>
          )}
          {queuePaused && <span className="badge">{t('app.queue.paused')}</span>}
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
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const [open, setOpen] = useState(false)
  const [pendingRemoval, setPendingRemoval] = useState<QueueEntry | null>(null)
  const [pendingClearAll, setPendingClearAll] = useState(false)
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
            title={actionDescription(entry, undefined, world, maps)}
            tone="gold"
          >
            <div className="qc-next">
              <span className={`item-glyph tone-${ACTION_TAB_META[entry.type].accent}`} aria-hidden="true">
                <Icon name={entry.type} />
              </span>
              <div className="qc-next-text">
                <span className="qc-next-name">{resolveQueueEntryName(entry, undefined, world, maps)}</span>
                <span className="qc-next-repeats">{formatRepeatsLabel(entry.remainingRepeats, t)}</span>
              </div>
            </div>
          </FloatingTooltip>
        ))}
      </div>

      <div className="qc-controls">
        <div className="qc-toggles">
          <FloatingTooltip displayContents placement="bottom" title={t('app.queue.loopTip')} tone="gold">
            <label className="loop-toggle">
              <input
                aria-label={t('app.queue.loop')}
                checked={queue.loopEnabled}
                onChange={event => gameClient.sendCommand('queue.configure', { loopEnabled: event.target.checked })}
                type="checkbox"
              />
              <span className="loop-toggle-icon" aria-hidden="true"><Icon name="loop" /></span>
              <span className="loop-toggle-text">{t('app.queue.loop')}</span>
            </label>
          </FloatingTooltip>
          <FloatingTooltip displayContents placement="bottom" title={t('app.queue.autoTravelTip')} tone="gold">
            <label className="loop-toggle">
              <input
                aria-label={t('app.queue.autoTravel')}
                checked={queue.autoTravelEnabled}
                onChange={event => gameClient.sendCommand('queue.configure', { autoTravelEnabled: event.target.checked })}
                type="checkbox"
              />
              <span className="loop-toggle-icon" aria-hidden="true"><Icon name="travel" /></span>
              <span className="loop-toggle-text">{t('app.queue.autoTravel')}</span>
            </label>
          </FloatingTooltip>
        </div>
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
                <span className={`item-glyph tone-${ACTION_TAB_META[entry.type].accent}`} aria-hidden="true">
                  <Icon name={entry.type} />
                </span>
                <span className="queue-name">{resolveQueueEntryName(entry, undefined, world, maps)}</span>
                <span className="queue-repeats">{formatRepeatsLabel(entry.remainingRepeats, t)}</span>
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
            <li className="queue-footer">
              <button
                aria-label={queue.paused ? t('app.queue.resumeAria') : t('app.queue.pauseAria')}
                className={queue.paused ? 'is-paused' : undefined}
                onClick={() => gameClient.sendCommand('queue.configure', { paused: !queue.paused })}
                title={queue.paused ? t('app.queue.resumeTitle') : t('app.queue.pauseTitle')}
                type="button"
              >{queue.paused ? t('app.queue.resume') : t('app.queue.pause')}</button>
              <button
                aria-label={t('app.queue.clearAllAria')}
                className="danger"
                onClick={() => setPendingClearAll(true)}
                type="button"
              >{t('app.queue.clearAll')}</button>
            </li>
          </ol>
        </>
      )}
      <ConfirmDialog
        confirmLabel={t('app.queue.removeConfirmBtn')}
        danger
        description={pendingRemoval ? t('app.queue.removeConfirmDesc', { name: resolveQueueEntryName(pendingRemoval, undefined, world, maps) }) : ''}
        onClose={() => setPendingRemoval(null)}
        onConfirm={() => {
          if (pendingRemoval) gameClient.sendCommand('action.remove', { actionInstanceId: pendingRemoval.entryId })
        }}
        open={pendingRemoval !== null}
        title={t('app.queue.removeConfirmTitle')}
      />
      <ConfirmDialog
        confirmLabel={t('app.queue.clearAllConfirmBtn')}
        danger
        description={t('app.queue.clearAllConfirmDesc', { count: queue.entries.length })}
        onClose={() => setPendingClearAll(false)}
        onConfirm={() => {
          if (pendingClearAll) gameClient.sendCommand('queue.clear', {})
        }}
        open={pendingClearAll}
        title={t('app.queue.clearAllConfirmTitle')}
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
        // durable 阵盘类永驻 Buff：到期时间投影为有限哨兵（≥1e15），按常驻展示而非倒计时。
        const permanent = buff.expiresAtUnixSeconds >= 1e15
        const remaining = permanent ? 0 : Math.max(0, buff.expiresAtUnixSeconds - serverTime)
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
                subtitle={permanent
                  ? t('app.buff.permanentStacks', { stacks: buff.stacks })
                  : t('app.buff.remainingStacks', { remaining, stacks: buff.stacks })}
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
  const experience = useGameStore(state => state.stats.experience)
  const lifetimeExperience = useGameStore(state => state.stats.lifetimeExperience)
  const realmLevel = useGameStore(state => state.stats.realmLevel)
  const connection = useGameStore(state => state.connection)
  const presence = useGameStore(state => state.presenceMode)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  const realmProgress = useMemo(() => calculateRealmProgress(experience ?? 0, realmLevel), [realmLevel, experience])
  const realmInfo = useMemo(() => calculateRealmInfo(experience ?? 0, realmLevel), [realmLevel, experience])
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
                <strong className="unit-number-tooltip-split">{formatChineseSplitNumber(lifetimeExperience ?? 0)}</strong>
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
      <TaijiToggle />
    </header>
  )
})

/** 技艺二级导航固定项：顺序与图标同技艺页签（采集/挖掘无技艺页签入口，不入导航）；
 * tone 与内视技艺 Tab 的分色一致，经验条与等级数字共用同一修仙色调。 */
const CRAFTING_NAV_MODES: ReadonlyArray<{ id: CraftingMode; icon: IconName; tone: PillTone }> = [
  { id: 'alchemy', icon: 'alchemy', tone: 'emerald' },
  { id: 'forging', icon: 'forging', tone: 'crimson' },
  { id: 'talisman', icon: 'talisman', tone: 'amber' },
  { id: 'formation', icon: 'formation', tone: 'purple' },
  { id: 'enhancement', icon: 'enhancement', tone: 'gold' },
]

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

  // 技艺子菜单等级与经验进度：技艺经验随 combat 快照下发，等级按修为曲线导出（0 级起步）；
  // 满级（expToNext=0）时进度条恒显满格，悬浮气泡经验行改显「已臻化境」。
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)
  const techniqueStates = useMemo(() => {
    const states = {} as Record<CraftingMode, {
      level: number
      ratio: number
      maxed: boolean
      current: number
      max: number
      speedPercent: number
    }>
    for (const mode of CRAFTING_NAV_MODES) {
      const progress = calculateTechniqueProgress(techniqueExperiences?.[mode.id] ?? 0)
      states[mode.id] = {
        level: progress.level,
        ratio: progress.expToNext > 0 ? Math.min(100, (progress.currentLayerExp / progress.expToNext) * 100) : 100,
        maxed: progress.expToNext <= 0 && progress.level > 0,
        current: Math.floor(progress.currentLayerExp),
        max: Math.max(1, Math.floor(progress.expToNext)),
        speedPercent: progress.level * TECHNIQUE_SPEED_RATIO_PER_LEVEL * 100,
      }
    }
    return states
  }, [techniqueExperiences])

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
          className={`nav-item${activeTab === 'exploration' ? ' active' : ''}`}
          onClick={() => setActiveTab('exploration')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="exploration" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.exploration')}</span>
          </span>
        </button>

        {/* 技艺（可展开/折叠二级菜单：炼丹、炼器、符箓、阵法、强化） */}
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
              {CRAFTING_NAV_MODES.map(mode => {
                const state = techniqueStates[mode.id]
                // 悬浮气泡：技艺名 · 等级 / 本级经验进度（满级显「已臻化境」）/ 行动速度加成
                const tooltipText = [
                  `${t(`app.crafting.${mode.id}`)} · ${t('app.crafting.navTooltipLevel', { level: state.level })}`,
                  state.maxed
                    ? t('app.crafting.navTooltipMaxed')
                    : t('app.crafting.navTooltipExp', {
                        current: formatNumber(state.current),
                        max: formatNumber(state.max),
                      }),
                  t('app.crafting.navTooltipSpeed', { value: formatNumber(state.speedPercent, { maxDecimals: 0 }) }),
                ].join('\n')
                return (
                  <FloatingTooltip displayContents key={mode.id} placement="right" title={tooltipText} tone="gold">
                    <button
                      className={`nav-sub-item${activeTab === 'crafting' && craftingMode === mode.id ? ' active' : ''}`}
                      onClick={() => {
                        setActiveTab('crafting')
                        setCraftingMode(mode.id)
                      }}
                      type="button"
                    >
                      <span className="nav-sub-main">
                        <span className="nav-sub-glyph" aria-hidden="true"><Icon name={mode.icon} /></span>
                        <span className="nav-sub-label">{t(`app.crafting.${mode.id}`)}</span>
                        <span className={`nav-sub-level underlined-value pill-tone--${mode.tone}`}>{state.level}</span>
                      </span>
                      <span aria-hidden="true" className={`nav-sub-progress pill-tone--${mode.tone}`}>
                        <i className="compound-stat-progress-fill" style={{ width: `${state.ratio}%` }} />
                      </span>
                    </button>
                  </FloatingTooltip>
                )
              })}
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
        <button
          className={`nav-item${activeTab === 'social' ? ' active' : ''}`}
          onClick={() => setActiveTab('social')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="social" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.social')}</span>
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

/** 炼丹页顶部子选项卡配置（id 对应 uiStore 的 AlchemySubTab，labelKey 为 i18n 键） */
const ALCHEMY_SUB_TABS: ReadonlyArray<{ id: AlchemySubTab; labelKey: 'alchemyExtraction' | 'alchemyCultivation' | 'alchemyCombat' }> = [
  { id: 'extraction', labelKey: 'alchemyExtraction' },
  { id: 'cultivation', labelKey: 'alchemyCultivation' },
  { id: 'combat', labelKey: 'alchemyCombat' },
]

/** 炼器页顶部子选项卡配置（精炼 + 六大装备部位） */
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
      && isActionAvailableAtNode(action, worldNodeId)
  )), [actions, worldNodeId])

  const itemCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const encounters = useGameStore(state => state.encounters)

  // 当前模式与当前子选项卡下的行动列表过滤与等级升序排列
  const filteredActions = useMemo(() => {
    let list: ActionDefinition[] = []
    if (craftingMode === 'talisman' || craftingMode === 'formation') {
      // 符箓/阵法制造行动复用 alchemy 行动类型，按 technique 归属独立页签展示
      list = availableActions.filter(a => a.technique === craftingMode)
    } else if (craftingMode === 'alchemy') {
      // 炼丹行动池须剥离符箓/阵法/强化技艺行动，避免经 type=alchemy 串入炼丹列表
      const pool = availableActions.filter(a => a.technique !== 'talisman' && a.technique !== 'formation' && a.technique !== 'enhancement')
      if (alchemySubTab === 'extraction') {
        // 提炼：草木精华、伴灵精华等纯药材提炼（排除锻造精炼）
        list = pool.filter(a =>
          (a.id === 'craft.extract-herb-essence' ||
           a.id === 'craft.extract-banling-essence' ||
           a.id.startsWith('craft.extract-') ||
           a.id.includes('essence') ||
           a.id.includes('extract')) &&
          !a.id.startsWith('craft.refine-')
        )
      } else if (alchemySubTab === 'cultivation') {
        // 修炼丹药：引气丹、通脉丹
        list = pool.filter(a =>
          a.id === 'alchemy.refine-qi-pill' ||
          a.id === 'alchemy.refine-tongmai-pill' ||
          (a.type === 'alchemy' && !a.id.includes('zhixue') && !a.id.includes('shunqi') && !a.id.startsWith('craft.extract-') && !a.id.startsWith('forge.') && !a.id.startsWith('craft.refine-'))
        )
      } else {
        // 斗法药剂：止血散、顺气丸
        list = pool.filter(a =>
          a.id === 'alchemy.refine-zhixue-powder' ||
          a.id === 'alchemy.refine-shunqi-pill' ||
          a.id.includes('zhixue') ||
          a.id.includes('shunqi')
        )
      }
    } else if (craftingMode === 'forging') {
      // 炼器：精炼、武器、头部、衣服、腿部、鞋履、饰品
      const pool = availableActions.filter(a => a.technique !== 'talisman' && a.technique !== 'formation' && a.technique !== 'enhancement')
      if (forgingSubTab === 'refining') {
        // 精炼：百炼铁、桃木晶、赤铜精、灵松晶等锻造灵材精炼
        list = pool.filter(a =>
          a.id === 'craft.refine-iron' ||
          a.id === 'craft.refine-peach-wood-crystal' ||
          a.id === 'craft.refine-copper' ||
          a.id === 'craft.refine-spirit-pine-crystal' ||
          a.id.startsWith('craft.refine-') ||
          (a.id.startsWith('craft.') && !a.id.startsWith('craft.extract-'))
        )
      } else {
        list = pool.filter(a => {
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

        {/* 顶部子选项卡（符箓/阵法/强化模式无二级分类） */}
        {craftingMode === 'alchemy' && (
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
        )}
        {craftingMode === 'forging' && (
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

      {craftingMode === 'enhancement' ? (
        <EnhancementPage />
      ) : (
        <div className="action-cards">
          {filteredActions.map(action => <ActionCard action={action} key={action.id} onOpen={setSelectedAction} />)}
          {filteredActions.length === 0 && <p className="location-action-empty">{t('app.crafting.empty')}</p>}
        </div>
      )}
      <AutoUse type={craftingMode === 'enhancement' ? 'enhancement' : 'alchemy'} />
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
  slotGroup = 'general',
  type,
}: {
  className?: string
  hideLabel?: boolean
  label: string
  /** 栏位分组：仅战斗域区分 potion（药品栏）/general（通用栏） */
  slotGroup?: 'potion' | 'general'
  type: ActionType
}): React.JSX.Element {
  const t = useT()
  const allSlots = useGameStore(state => state.queue.autoUseSlots[type])
  // 战斗域槽位数组布局：0-2 药品栏，3-5 通用栏；非战斗域恒为通用栏 0-2。
  const slotOffset = type === 'combat' && slotGroup === 'general' ? 3 : 0
  const slots = allSlots.slice(slotOffset, slotOffset + 3)
  const activeEntry = useGameStore(state => {
    const current = state.queue.entries[0]
    return current?.type === type ? current : null
  })
  // 消耗品冷却仅 combat 类槽位由服务端按双组下标定长下发（药品栏 0-2、通用栏 3-5）。
  const consumableCooldowns = useGameStore(
    state => type === 'combat' ? state.combatResources?.consumableCooldownMilliseconds : undefined,
  )
  const inventory = useGameStore(state => state.inventory)
  const itemCatalog = useGameStore(state => state.items)
  const [picker, setPicker] = useState<{ index: number; anchor: HTMLElement } | null>(null)
  const pickerItems = useMemo<ItemPickerEntry[]>(() => itemCatalog
      .filter(item => item.usableActionTypes.includes(type) && itemAmount(inventory, item.itemId) > 0
        // 药品栏只放药品类即时消耗品；通用栏任意适用消耗品皆可。
        && (slotGroup !== 'potion' || (item.subCategory === 'pill' && !item.isContinuous)))
      .map(item => ({
        itemId: item.itemId,
        name: item.name,
        description: item.description,
        category: resolveItemCategoryLabel(resolveItemSubCategory(item.itemId, item), t),
        tier: item.tier,
        amount: itemAmount(inventory, item.itemId),
        definition: item,
      })), [inventory, itemCatalog, slotGroup, t, type])

  const updateSlot = useCallback((index: number, itemId: string | null) => {
    gameClient.sendCommand('queue.auto_use.configure', {
      actionType: type,
      slotIndex: index,
      itemId,
      quantity: 1,
      enabled: Boolean(itemId),
      group: slotGroup,
    })
  }, [slotGroup, type])

  const pickerSlot = picker === null ? null : slots[picker.index] ?? null

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
            category: resolveItemCategoryLabel(resolveItemSubCategory(selectedDefinition.itemId, selectedDefinition), t),
            tier: selectedDefinition.tier,
            amount: itemAmount(inventory, selectedDefinition.itemId),
            definition: selectedDefinition,
          } : null
          const cooldownMs = consumableCooldowns?.[slotOffset + index] ?? 0
          return (
            <div className={`auto-use-slot${picker?.index === index ? ' is-picking' : ''}`} key={index}>
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
                onClick={event => {
                  // currentTarget 派发后会被 React 重置为 null，必须在 updater 外同步取值
                  const anchor = event.currentTarget
                  setPicker(prev => prev?.index === index ? null : { index, anchor })
                }}
              />
            </div>
          )
        })}
      </div>
      <ItemGridPicker
        anchor={picker?.anchor ?? null}
        emptyLabel={t('common.pickerEmpty')}
        onClose={() => setPicker(null)}
        onSelect={itemId => {
          if (picker === null) return
          updateSlot(picker.index, itemId)
        }}
        items={pickerItems}
        open={picker !== null}
        title={picker === null ? t('common.selectItem') : t('common.selectItemSlot', { n: picker.index + 1 })}
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
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)

  // 行动名按当前驻留地点本地化（「讨伐 青云仙山」→「讨伐 漱玉溪」），与节点生态实战语义一致
  const displayName = useMemo(() => localizeActionDisplayText(action.name, world, maps), [action.name, world, maps])

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
      <strong className="action-card-name">{displayName}</strong>
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
 * 行动执行配置模态弹窗（设置循环轮次、查看消耗产出并加入行动队列）
 */
const ActionModal = memo(function ActionModal({ action, onClose }: { action: ActionDefinition; onClose: () => void }): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const items = useGameStore(state => state.items)
  const autoUseSlots = useGameStore(state => state.queue.autoUseSlots)
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const stats = useGameStore(state => state.stats)
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

  // 耗时加速类加成不乘产出倍率：拆出单独徽标常显，不并入「总加成」构成明细
  const multiplierBonuses = useMemo(
    () => yieldPreview.bonuses.filter(b => b.type !== 'foundation' && b.type !== 'speed'),
    [yieldPreview.bonuses]
  )
  const speedBonuses = useMemo(
    () => yieldPreview.bonuses.filter(b => b.type === 'speed'),
    [yieldPreview.bonuses]
  )
  const totalMultiplier = yieldPreview.totalMultiplier
  const totalPercent = Math.round((totalMultiplier - 1.0) * 100)
  const hasRateBonus = multiplierBonuses.length > 0 && Math.abs(totalMultiplier - 1.0) > 0.001
  const isPending = action.type === 'cultivation' && Boolean(yieldPreview.cultivation?.isPendingBreakthrough)
  const hasBonuses = hasRateBonus || speedBonuses.length > 0 || isPending

  const inputs = Object.entries(action.inputs ?? {}).map(([itemId, amount]) => ({ itemId, amount }))
  const titleId = `action-modal-title-${action.id}`
  const startsImmediately = action.executionKind === 'combat-encounter'
  // 行动名/描述按当前驻留地点本地化（「讨伐 青云仙山」→「讨伐 漱玉溪」）
  const displayName = localizeActionDisplayText(action.name, world, maps)
  const displayDescription = action.description ? localizeActionDisplayText(action.description, world, maps) : action.description
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
          <h2 id={titleId}>{displayName}</h2>
          {!isCrafting && displayDescription ? <p>{displayDescription}</p> : null}
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
              {yieldPreview.resourceYields.map(entry => (
                <span className={`action-stat-pill action-stat-pill--${entry.kind}`} key={entry.kind}>
                  <span className="action-stat-pill-label">{t(`app.action.yieldKind.${entry.kind}`)}</span>
                  <strong className="action-stat-pill-value">
                    <UnitNumber disableTooltip maxDecimals={1} prefix="×" value={entry.finalAmount} />
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
                        {multiplierBonuses.map((bonus, idx) => (
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

              {speedBonuses.map((bonus, idx) => (
                <FloatingTooltip
                  key={`speed-${idx}`}
                  placement="top"
                  title={bonus.description}
                  tone="gold"
                >
                  <span className="action-bonus-pill action-bonus-pill--world">
                    <span className="action-bonus-pill-source">{bonus.sourceName}</span>
                    <strong className="action-bonus-pill-tag">{bonus.tag}</strong>
                  </span>
                </FloatingTooltip>
              ))}

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
            {startsImmediately
              ? t('app.action.combatUnlimited')
              : t('app.action.durationPerRound', { seconds: Math.max(1, Math.ceil(yieldPreview.actualDurationSeconds - 1e-9)) })}
          </span></dd>
        </div>
      </dl>

      {startsImmediately && <CombatEcologyPanel action={action} />}

      {startsImmediately && (
        <section className="duel-option" aria-label={t('app.action.duel.title')}>
          <FloatingPopover
            content={
              <FloatingInfoCard
                badge={t('app.queue.duelBadge')}
                badgeTone="purple"
                lines={[
                  t('app.action.duel.descMatch'),
                  t('app.action.duel.descDrop'),
                  t('app.action.duel.descStaging'),
                  t('app.action.duel.descPenalty'),
                ]}
                title={t('app.action.duel.title')}
              />
            }
            displayContents
            interactive={false}
            placement="top"
            size="medium"
            trigger="hover"
          >
            <label className="duel-option-toggle">
              <input
                checked={pvpEnabled}
                onChange={event => setPvpEnabled(event.target.checked)}
                type="checkbox"
              />
              <strong>{t('app.action.duel.title')}</strong>
              <span className="badge badge-duel">{t('app.queue.duelBadge')}</span>
            </label>
          </FloatingPopover>
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
  /** 待选列表贴靠的槽位锚点元素 */
  anchor: HTMLElement
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
        <AutoUseSlotGroup className="combat-slot-section" label={t('app.combatPanel.consumablesGroup')} slotGroup="potion" type="combat" />
        <AutoUseSlotGroup className="combat-slot-section" label={t('app.combatPanel.generalGroup')} slotGroup="general" type="combat" />

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

              const slotButton = (
                <button
                  aria-label={slotAriaLabel}
                  className={`combat-ability-slot${isInternal ? ' is-internal-slot' : ' is-spell-slot'}${ability ? '' : ' combat-ability-slot--empty'}`}
                  onClick={event => {
                    const anchor = event.currentTarget
                    setPickerTarget(prev => prev?.index === index ? null : { kind: 'ability', index, anchor })
                  }}
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
              )
              const slotExtras = ability && (
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
              )
              return (
                <div className={`combat-slot-card${pickerTarget?.index === index ? ' is-picking' : ''}`} key={index}>
                  {ability ? (
                    <EffectTooltipTarget className="combat-ability-slot-shell" content={describeAbility(ability, abilityLevelFor(ability.abilityId))} tabIndex={-1}>
                      {slotButton}
                    </EffectTooltipTarget>
                  ) : <span className="combat-ability-slot-shell">{slotButton}</span>}
                  {/* Lv 徽标与条件按钮移出 tooltip 触发域：避免点开触发器编辑器时 tooltip 叠在 modal 上 */}
                  {slotExtras}
                </div>
              )
            })}
          </div>
        </section>
      </div>

      <AbilityGridPicker
        abilities={learnedAbilities}
        allowEmpty
        anchor={pickerTarget?.anchor ?? null}
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
 * 历练页面：常驻行动（修炼类，默认全图可行、地点可用 excludedNodeIds 裁剪）+
 * 按技艺归属分组的地点行动（采集/挖掘等，组名跟随技艺并展示当前技艺等级）+ 讨伐战斗行动。
 */
const ExplorationPage = memo(function ExplorationPage({ hidden }: { hidden: boolean }): React.JSX.Element {
  const t = useT()
  const actions = useGameStore(state => state.actions)
  const worldNodeId = useGameStore(state => state.world.nodeId)
  const world = useGameStore(state => state.world)
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)
  const [selectedAction, setSelectedAction] = useState<ActionDefinition | null>(null)

  const visibleActions = useMemo(() => actions.filter(action => (
    !action.hiddenFromCatalog
      && action.executionKind !== 'travel'
      && (action.type === 'exploration' || action.type === 'combat' || action.type === 'cultivation')
      && isActionAvailableAtNode(action, worldNodeId)
  )), [actions, worldNodeId])

  // 分组顺序：常驻（cultivation）→ 技艺组（technique 归属，随技艺等级）→ 历练（无技艺探索）→ 讨伐（combat）
  const groups = useMemo(() => {
    interface ActionGroup { key: string; label: string; actions: ActionDefinition[] }
    const result: ActionGroup[] = []
    const resident: ActionDefinition[] = []
    const hunts: ActionDefinition[] = []
    const others: ActionDefinition[] = []
    const byTechnique = new Map<string, ActionDefinition[]>()
    for (const action of visibleActions) {
      if (action.type === 'cultivation') { resident.push(action); continue }
      if (action.type === 'combat') { hunts.push(action); continue }
      if (action.technique) {
        const list = byTechnique.get(action.technique) ?? []
        list.push(action)
        byTechnique.set(action.technique, list)
        continue
      }
      others.push(action)
    }
    if (resident.length > 0) result.push({ key: 'resident', label: t('app.combatPanel.residentActions'), actions: resident })
    for (const id of TECHNIQUE_IDS) {
      const list = byTechnique.get(id)
      if (!list || list.length === 0) continue
      const level = calculateTechniqueProgress(techniqueExperiences?.[id] ?? 0).level
      result.push({
        key: `technique:${id}`,
        label: t('app.combatPanel.techniqueGroup', { name: t(`insight.technique.${id}`), level }),
        actions: list,
      })
    }
    if (others.length > 0) result.push({ key: 'other', label: t('app.combatPanel.otherActions'), actions: others })
    if (hunts.length > 0) result.push({ key: 'combat', label: t('app.combatPanel.huntActions'), actions: hunts })
    return result
  }, [techniqueExperiences, t, visibleActions])

  const closeModal = useCallback(() => setSelectedAction(null), [])

  return (
    <section className="panel skill-page" hidden={hidden}>
      <header className="panel-header">
        <h2>{t('app.nav.exploration')}</h2>
        {world.locationName && <span className="location-sub-badge">{world.locationName}</span>}
      </header>
      <div className="action-sections">
        {groups.map(group => (
          <div className="action-group-section" key={group.key}>
            <span className="slot-group-label">{group.label}</span>
            <div className="action-group-list">
              {group.actions.map(action => <ActionCard action={action} key={action.id} onOpen={setSelectedAction} />)}
            </div>
          </div>
        ))}
        {visibleActions.length === 0 && <p className="location-action-empty">{t('app.action.locationActionEmpty')}</p>}
      </div>
      <AutoUse type="cultivation" />
      <AutoUse type="exploration" />
      {selectedAction && <ActionModal action={selectedAction} onClose={closeModal} />}
    </section>
  )
})

type MapSubTab = 'map' | NearbyTab

/** 地图页子选项卡配置：地图本体 + 附近面板的妖兽/修士/建筑三视图 */
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
  const [recordsOpen, setRecordsOpen] = useState(false)
  const closeModal = useCallback(() => setSelectedAction(null), [])
  const closeReport = useCallback(() => setReportOpen(false), [])
  const closeRecords = useCallback(() => setRecordsOpen(false), [])

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
        {/* 侧边栏在移动端整体隐藏，详情页补充「记录」入口保证竖屏可达 */}
        <button
          className="ghost-button records-open-mobile"
          onClick={() => setRecordsOpen(true)}
          type="button"
        >
          {t('app.nav.records')}
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
      <Modal
        className="battle-report-modal"
        labelledBy="records-panel-title-mobile"
        onClose={closeRecords}
        open={recordsOpen}
        size="medium"
      >
        <RecordsPanel titleId="records-panel-title-mobile" />
      </Modal>
      {selectedAction && <ActionModal action={selectedAction} onClose={closeModal} />}
    </section>
  )
})



/** 移动端底部导航栏页签配置（顺序即展示顺序，labelKey/icon 均与 TabId 同名） */
const BOTTOM_TABS: ReadonlyArray<{ tab: TabId; labelKey: 'insight' | 'market' | 'exploration' | 'crafting' | 'map' | 'combat' | 'social' | 'inventory' | 'log' | 'settings'; icon: IconName }> = [
  { tab: 'insight', labelKey: 'insight', icon: 'insight' },
  { tab: 'market', labelKey: 'market', icon: 'market' },
  { tab: 'exploration', labelKey: 'exploration', icon: 'exploration' },
  { tab: 'crafting', labelKey: 'crafting', icon: 'crafting' },
  { tab: 'map', labelKey: 'map', icon: 'map' },
  { tab: 'combat', labelKey: 'combat', icon: 'combat' },
  { tab: 'social', labelKey: 'social', icon: 'social' },
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
  const noticeSeq = useGameStore(state => state.noticeSeq)
  const setNotice = useGameStore(state => state.setNotice)

  useEffect(() => {
    if (!notice) return undefined
    const timer = window.setTimeout(() => setNotice(null), 2600)
    return () => window.clearTimeout(timer)
  }, [notice, noticeSeq, setNotice])

  return notice ? <div className="notice" key={noticeSeq} role="status">{notice}</div> : null
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
    void restoreSession()
    const onUnblocked = () => gameClient.connect()
    window.addEventListener('daohua:tab-unblocked', onUnblocked)
    return () => {
      window.removeEventListener('daohua:tab-unblocked', onUnblocked)
      gameClient.dispose()
      disposeAntiMultiOpen()
    }
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
            <ExplorationPage hidden={activeTab !== 'exploration'} />
            <CraftingPage hidden={activeTab !== 'crafting'} />
            <MapPage hidden={activeTab !== 'map'} />
            <CombatDetailsPage hidden={activeTab !== 'combat'} />
            {activeTab === 'market' && <MarketPage />}
            {activeTab === 'social' && <SocialPage />}
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
