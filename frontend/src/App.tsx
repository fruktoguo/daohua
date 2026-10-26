import { hasActionParticipation } from './game/types'
import { lazy, memo, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { PlayerCardModal } from './components/PlayerCardModal'
import { AwakenResultModal } from './components/AwakenResultModal'
import { ConfirmDialog } from './components/ConfirmDialog'
import { AbilityGridPicker } from './components/AbilityGridPicker'
import { StatsPanel } from './components/StatsPanel'
import { RecordsPanel } from './components/RecordsPanel'
import { CombatTriggerEditor } from './components/CombatTriggerEditor'
import { CombatEcologyPanel } from './components/CombatEcologyPanel'
import { ConditionedTile } from './components/ConditionedTile'
import { EnhancementPage } from './components/EnhancementPage'
import { createProgressAsideCard, FloatingInfoCard, FloatingPopover, FloatingTooltip, type FloatingTone } from './components/FloatingPopover'
import { resolveAbilityAsideCards } from './components/EffectTooltip'
import { ValueText } from './components/ValueText'
import { GameLogPanel } from './components/GameLogPanel'
import { ActionDetailItem, ItemTooltipTarget } from './components/ItemDisplay'
import { ItemGridPicker, ItemUseSlot, type ItemPickerEntry } from './components/ItemGridPicker'
import { Modal } from './components/Modal'
import type { PillTone } from './components/PillLabel'
import { RealmBadge } from './components/RealmBadge'
import { NearbyPanel, type NearbyTab } from './components/NearbyPanel'
import { QuantitySelector } from './components/QuantitySelector'
import { WorldMapPanel } from './components/WorldMapPanel'
import { LoginPage } from './components/LoginPage'
import { ConnectingPage, ConnectionErrorPage } from './components/ConnectingPage'
import { RightSidebar } from './components/RightSidebar'
import { TaijiToggle } from './components/TaijiToggle'
import { TopbarCommunityLinks } from './components/TopbarCommunityLinks'
import { TutorialOverlay } from './tutorial/TutorialOverlay'
import { describeAbility, formatChineseSplitNumber, formatNumber, formatProgressText, formatRemainingSeconds } from './game/format'
import { UnitNumber } from './components/UnitNumber'
import { StatGlyph } from './components/StatGlyph'
import { Icon, type IconName } from './components/Icon'
import { calculateBaseExperienceForLevel, calculateRealmInfo, calculateRealmProgress, calculateTechniqueProgress, serverExpToNextCap, TECHNIQUE_IDS, TECHNIQUE_SPEED_RATIO_PER_LEVEL } from './game/combatConstants'
import { ACTION_TAB_META, assignAbilityToSlot, autoUseBarFor, autoUseBarLabel, findAbilityInCatalog, findActionInCatalog, findEncounterInCatalog, findEquipmentInCatalog, findItemInCatalog, learnedAbilityLevel, localizeActionDisplayText, resolveActionDefinition, resolveActionLevel, resolveActionRarity, resolveItemCategoryLabel, resolveItemSubCategory } from './game/ids'
import { useGameStore } from './game/gameStore'
import { useSocialStore } from './game/socialStore'
import { sendLoadoutReplace } from './game/loadoutCommands'
import { resolveItemIconUrl } from './game/itemIcons'
import { calculateActionYieldPreview, isActionAvailableAtNode, ROOT_ASPECT_LABEL_KEYS } from './game/actionCalculations'
import { useSmoothProgressRef } from './game/useSmoothProgress'
import type { ActionDefinition, AutoUseBarType, CombatTrigger, EquipmentSlot, InventoryItem, QueueEntry, WorldMapDefinition, WorldState } from './game/types'
import { gameClient, GAME_DATA_MISMATCH_EVENT } from './network/gameClient'
import { installAntiMultiOpen, disposeAntiMultiOpen, useAntiMultiOpenStore, MultiTabBlockedPage } from './private'
import { useAccountStore } from './network/account'
import { fetchGameCatalog } from './network/apiClient'
import { useUiStore, type TabId, type CraftingMode, type AlchemySubTab, type ForgingSubTab } from './uiStore'
import { MOBILE_NAV_GROUPS, TAB_ICON, mobileNavGroupById, mobileNavGroupIdOf } from './mobileNav'
import { useLogUnreadStore } from './logging/logUnreadStore'
import { useT, getT } from './i18n'

// 非首屏页面按首次进入时拆包；页面组件仍只挂载于 activeTab，保持原有隐藏/卸载生命周期语义。
const InsightPage = lazy(() => import('./components/InsightPage').then(module => ({ default: module.InsightPage })))
const MarketPage = lazy(() => import('./components/MarketPage').then(module => ({ default: module.MarketPage })))
const ShopPage = lazy(() => import('./components/ShopPage').then(module => ({ default: module.ShopPage })))
const SocialPage = lazy(() => import('./components/SocialPage').then(module => ({ default: module.SocialPage })))
const SettingsPage = lazy(() => import('./components/SettingsPage').then(module => ({ default: module.SettingsPage })))
const CodexPage = lazy(() => import('./components/CodexPage').then(module => ({ default: module.CodexPage })))
// 战场与其专属 VFX 只在首次查看详情时加载；访问后仍随原 hidden 页面常驻。
const CombatArena = lazy(() => import('./components/CombatArena').then(module => ({ default: module.CombatArena })))
const CombatSimulatorModal = lazy(() => import('./components/simulator/CombatSimulatorModal').then(module => ({ default: module.CombatSimulatorModal })))

/** 延迟模块的可见加载态；复用已有进度样式和页面名称，不吞掉模块加载异常。 */
function PanelLoading({ label }: { label: string }): React.JSX.Element {
  return <div aria-busy="true" role="status">
    <span>{label}</span>
    <div className="status-progress-bar"><div className="status-progress-glow" /></div>
  </div>
}

/** 全局共享的空冷却表（冻结对象）：避免每次渲染创建新引用触发下游 memo 失效 */
const EMPTY_ABILITY_COOLDOWNS: Readonly<Record<string, number>> = Object.freeze({})

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
    const state = useGameStore.getState()
    const targetName = findEquipmentInCatalog(state.equipment, entry.targetItemId)?.name
      ?? findItemInCatalog(state.items, entry.targetItemId)?.name
      ?? entry.targetItemId
    const currentPart = entry.enhancementCurrentLevel != null && entry.enhancementCurrentLevel > 0
      ? ` +${Math.floor(entry.enhancementCurrentLevel)}`
      : ''
    const targetPart = entry.targetLevel != null && entry.targetLevel > 0 ? ` → +${Math.floor(entry.targetLevel)}` : ''
    const levelSuffix = currentPart ? `${currentPart}${targetPart}` : (targetPart ? ` ${targetPart.trim()}` : '')
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
          <i><b style={{ transform: `scaleX(${playerValue / playerMax})` }} /></i>
          <strong>
            <span>{t('app.queue.combatVitalSelf')}</span>
            <span><UnitNumber value={playerValue} /> / <UnitNumber value={playerMax} />（{Math.round(playerValue / playerMax * 100)}%）</span>
          </strong>
        </div>
        <div className="queue-combat-vital queue-combat-vital--enemy">
          <i><b style={{ transform: `scaleX(${enemyValue / enemyMax})` }} /></i>
          <strong>
            <span>{t('app.queue.combatVitalEnemy')}</span>
            <span><UnitNumber value={enemyValue} /> / <UnitNumber value={enemyMax} />（{Math.round(enemyValue / enemyMax * 100)}%）</span>
          </strong>
        </div>
      </div>
    </div>
  )
})

/**
 * 当前正在执行的行动卡片（支持出场/入场平滑过渡动画）
 */
const CurrentActionCard = memo(function CurrentActionCard({ entry, departing = false, onStop }: { entry: QueueEntry; departing?: boolean; onStop?: () => void }): React.JSX.Element {
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
          {Boolean(entry.parameters?.spiritStone && entry.parameters.spiritStone > 0) && (
            <span className="badge badge-spirit-stone">
              {t('app.action.spiritStoneBadge', { count: entry.parameters!.spiritStone })}
            </span>
          )}
        </div>
        {isCombatExpedition
          ? <CombatActionProgress entry={entry} />
          : entry.expedition
            ? <ExpeditionActionProgress entry={entry} />
            : <SmoothProgressBar entry={entry} />}
      </div>
      {/* 右上角常驻停止钮：直接复用队列移除确认链，无需展开列表或进详情 */}
      {onStop && (
        <button
          aria-label={t('app.queue.stopAria')}
          className="qc-stop danger"
          onClick={onStop}
          title={t('app.queue.stopTitle')}
          type="button"
        >{t('app.queue.stop')}</button>
      )}
    </div>
  )
})

/**
 * 队列中当前行动展示区（支持切换动画）
 */
const QueueCurrentStage = memo(function QueueCurrentStage({ entry, onStop }: { entry?: QueueEntry; onStop?: () => void }): React.JSX.Element {
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
      {entry ? <CurrentActionCard entry={entry} key={entry.entryId} onStop={onStop} /> : <div className="qc-current qc-idle">{t('app.queue.idle')}</div>}
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
  // 组队讨伐伪行动（§21.13）：徽标成员数取自队伍活跃会话，会话可能先于快照到达
  const partyMemberCount = useSocialStore(state => state.party?.activeSession?.memberIds.length ?? 0)
  const [open, setOpen] = useState(false)
  const [pendingRemoval, setPendingRemoval] = useState<QueueEntry | null>(null)
  const [pendingClearAll, setPendingClearAll] = useState(false)
  const [pendingQuitSession, setPendingQuitSession] = useState(false)
  const unreadyParty = useSocialStore(state => state.unreadyParty)
  const current = queue.entries[0]
  const upcoming = queue.entries.slice(1, 3)
  // 主行动卡停止钮与队列列表移除共用同一确认弹窗：确认后 action.remove 移除队首即停止。
  // 组队讨伐伪行动不受 action.remove（服务端 IN_PARTY_EXPEDITION 拒绝）：「停止」语义 = 取消参战，
  // 经散场确认派发 party.unready；散场后挂起的个人队首行动由服务端自动恢复续跑（§21.13）。
  const requestStopCurrent = useCallback(() => {
    if (!current) return
    if (hasActionParticipation(current)) {
      setPendingQuitSession(true)
      return
    }
    setPendingRemoval(current)
  }, [current])

  useEffect(() => {
    if (queue.entries.length === 0) setOpen(false)
  }, [queue.entries.length])

  return (
    <div className="queue-center">
      <QueueCurrentStage entry={current} onStop={current ? requestStopCurrent : undefined} />

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
                {hasActionParticipation(entry) && (
                  <span className="badge badge-party">
                    {partyMemberCount > 0 ? t('social.session.badgeCount', { count: partyMemberCount }) : t('social.session.badge')}
                  </span>
                )}
                {Boolean(entry.parameters?.spiritStone && entry.parameters.spiritStone > 0) && (
                  <span className="badge badge-spirit-stone">
                    {t('app.action.spiritStoneBadge', { count: entry.parameters!.spiritStone })}
                  </span>
                )}
                <span className="queue-repeats">{formatRepeatsLabel(entry.remainingRepeats, t)}</span>
                <div className="queue-actions">
                  <button
                    aria-label={t('app.queue.moveUpAria')}
                    disabled={index === 0 || hasActionParticipation(entry)}
                    onClick={() => gameClient.sendCommand('queue.move', { actionInstanceId: entry.entryId, direction: -1 })}
                    title={hasActionParticipation(entry) ? t('social.session.cancelHint') : t('app.queue.moveUpTitle')}
                    type="button"
                  >↑</button>
                  <button
                    aria-label={t('app.queue.moveDownAria')}
                    disabled={index === queue.entries.length - 1 || hasActionParticipation(entry)}
                    onClick={() => gameClient.sendCommand('queue.move', { actionInstanceId: entry.entryId, direction: 1 })}
                    title={hasActionParticipation(entry) ? t('social.session.cancelHint') : t('app.queue.moveDownTitle')}
                    type="button"
                  >↓</button>
                  <button
                    aria-label={t('app.queue.removeAria')}
                    className="queue-remove"
                    onClick={() => {
                      // 行动参与会话不走 action.remove：「移除」= 取消参战，经散场确认派发 party.unready
                      if (hasActionParticipation(entry)) {
                        setPendingQuitSession(true)
                        return
                      }
                      setPendingRemoval(entry)
                    }}
                    title={hasActionParticipation(entry) ? t('social.action.quitParticipation') : t('app.queue.removeTitle')}
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
      <ConfirmDialog
        danger
        description={t('social.confirm.quitSessionDesc')}
        onClose={() => setPendingQuitSession(false)}
        onConfirm={() => unreadyParty()}
        open={pendingQuitSession}
        title={t('social.confirm.quitSessionTitle')}
      />
    </div>
  )
})

/**
 * 客户端全局顶栏组件（包含道号、境界徽章、修为进度条与队列控制中心）
 */
const TopBar = memo(function TopBar(): React.JSX.Element {
  const t = useT()
  const name = useGameStore(state => state.name)
  const experience = useGameStore(state => state.stats.experience)
  const lifetimeExperience = useGameStore(state => state.stats.lifetimeExperience)
  const realmLevel = useGameStore(state => state.stats.realmLevel)
  const expToNextLinggen = useGameStore(state => state.stats.expToNextLinggen)
  const connection = useGameStore(state => state.connection)
  const latencyMs = useGameStore(state => state.latencyMs)
  const onlineCount = useGameStore(state => state.onlineCount)
  const offlineCount = useGameStore(state => state.offlineCount)
  const offlineIdleCount = useGameStore(state => state.offlineIdleCount)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  // 入天门后本层瓶颈含灵根乘区（总纲 §7.7.5.1），进度条与徽章统一取服务端权威 expToNextLinggen。
  const expToNextCap = serverExpToNextCap(expToNextLinggen, realmLevel)
  const realmProgress = useMemo(
    () => calculateRealmProgress(experience ?? 0, realmLevel, expToNextCap),
    [realmLevel, experience, expToNextCap],
  )
  const realmInfo = useMemo(
    () => calculateRealmInfo(experience ?? 0, realmLevel, expToNextCap),
    [realmLevel, experience, expToNextCap],
  )
  const connectionText = connection === 'online'
    ? t('app.topbar.connected')
    : connection === 'connecting'
      ? t('app.topbar.connecting')
      : connection === 'reconnecting'
        ? t('app.topbar.reconnecting')
        : t('app.topbar.offline')
  const latencyDisplay = connection !== 'online'
    ? connectionText
    : latencyMs !== null
      ? t('app.topbar.latency', { ms: latencyMs })
      : '-'
  const connectionTooltip = (
    <div className="connection-status-tooltip">
      <div className="connection-status-tooltip-row">
        <span className="connection-status-tooltip-label">{t('app.topbar.latencyLabel')}</span>
        <strong className="connection-status-tooltip-value">{latencyDisplay}</strong>
      </div>
      <div className="connection-status-tooltip-row">
        <span className="connection-status-tooltip-label">{t('app.topbar.onlineCountLabel')}</span>
        <strong className="connection-status-tooltip-value connection-status-tooltip-value--online">
          {onlineCount !== null ? t('app.topbar.countPerson', { count: onlineCount }) : '-'}
        </strong>
      </div>
      <div className="connection-status-tooltip-row">
        <span className="connection-status-tooltip-label">{t('app.topbar.offlineIdleCountLabel')}</span>
        <strong className="connection-status-tooltip-value connection-status-tooltip-value--idle">
          {offlineIdleCount !== null ? t('app.topbar.countPerson', { count: offlineIdleCount }) : '-'}
        </strong>
      </div>
      <div className="connection-status-tooltip-row">
        <span className="connection-status-tooltip-label">{t('app.topbar.offlineCountLabel')}</span>
        <strong className="connection-status-tooltip-value connection-status-tooltip-value--offline">
          {offlineCount !== null ? t('app.topbar.countPerson', { count: offlineCount }) : '-'}
        </strong>
      </div>
    </div>
  )
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
            <FloatingTooltip placement="bottom" title={connectionTooltip} tone={connection === 'online' ? 'jade' : 'danger'}>
              <span
                className={`connection-dot connection-dot--${connection}`}
                onMouseEnter={() => gameClient.pingNow()}
              />
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
      <div className="topbar-actions">
        <TopbarCommunityLinks />
        <TaijiToggle />
      </div>
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

const CRAFTING_FLOATING_TONE_MAP: Record<string, FloatingTone> = {
  emerald: 'jade',
  crimson: 'danger',
  amber: 'gold',
  violet: 'purple',
  gold: 'gold',
  jade: 'jade',
  qi: 'qi',
}

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

  // 技艺子菜单等级与经验进度：技艺经验随 combat 快照下发，等级按「修为曲线前 30 级线性削减」的技艺曲线导出（0 级起步）；
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
                const floatingTone = CRAFTING_FLOATING_TONE_MAP[mode.tone] ?? 'gold'
                const techniqueAsideCards = [
                  createProgressAsideCard({
                    id: `technique-progress-${mode.id}`,
                    mark: t('common.progressMark.technique'),
                    title: t('common.upgradeProgress'),
                    level: state.level,
                    currentExp: state.current,
                    maxExp: state.max,
                    maxed: state.maxed,
                    maxedLabel: t('app.crafting.navTooltipMaxed'),
                    tone: floatingTone,
                  }),
                ]
                const mainContent = (
                  <FloatingInfoCard
                    badge={t('app.crafting.navTooltipLevel', { level: state.level })}
                    badgeTone={floatingTone}
                    description={t(`insight.technique.${mode.id}Tooltip`)}
                    lines={[
                      <ValueText
                        key="speed"
                        text={t('app.crafting.navTooltipSpeed', {
                          value: formatNumber(state.speedPercent, { maxDecimals: 0 }),
                        })}
                      />,
                    ]}
                    title={t(`app.crafting.${mode.id}`)}
                  />
                )
                return (
                  <FloatingPopover
                    asideCards={techniqueAsideCards}
                    closeDelay={120}
                    content={mainContent}
                    displayContents
                    gap={8}
                    interactive={false}
                    key={mode.id}
                    openDelay={60}
                    placement="right"
                    size="small"
                    trigger="hover"
                  >
                    <button
                      className={`nav-sub-item${activeTab === 'crafting' && craftingMode === mode.id ? ' active' : ''}`}
                      onClick={() => {
                        setActiveTab('crafting')
                        setCraftingMode(mode.id)
                      }}
                      type="button"
                    >
                      <span className="nav-sub-main">
                        <span className="nav-sub-glyph" aria-hidden="true"><StatGlyph glyph={mode.id} tone={mode.tone} size={16} /></span>
                        <span className="nav-sub-label">{t(`app.crafting.${mode.id}`)}</span>
                        <span className={`nav-sub-level underlined-value pill-tone--${mode.tone}`}>{state.level}</span>
                      </span>
                      <span aria-hidden="true" className={`nav-sub-progress pill-tone--${mode.tone}`}>
                        <i className="compound-stat-progress-fill" style={{ width: `${state.ratio}%` }} />
                      </span>
                    </button>
                  </FloatingPopover>
                )
              })}
            </div>
          )}
        </div>

        <button
          className={`nav-item${activeTab === 'map' ? ' active' : ''}`}
          data-tour="nav-map"
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
        <button
          className={`nav-item${activeTab === 'codex' ? ' active' : ''}`}
          onClick={() => setActiveTab('codex')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="codex" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.codex')}</span>
          </span>
        </button>
        <button
          className={`nav-item${activeTab === 'shop' ? ' active' : ''}`}
          onClick={() => setActiveTab('shop')}
          type="button"
        >
          <span className="nav-glyph" aria-hidden="true"><Icon name="shop" /></span>
          <span className="nav-text">
            <span className="nav-label">{t('app.nav.shop')}</span>
          </span>
        </button>
      </nav>
      <div className="sidebar-footer">
        <button
          className={`nav-item${activeTab === 'records' ? ' active' : ''}`}
          onClick={() => setActiveTab('records')}
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
    </aside>
  )
})

/** 炼丹页顶部子选项卡配置（id 对应 uiStore 的 AlchemySubTab，labelKey 为 i18n 键） */
const ALCHEMY_SUB_TABS: ReadonlyArray<{ id: AlchemySubTab; labelKey: 'alchemyExtraction' | 'alchemyCultivation' | 'alchemyCombat' }> = [
  { id: 'extraction', labelKey: 'alchemyExtraction' },
  { id: 'cultivation', labelKey: 'alchemyCultivation' },
  { id: 'combat', labelKey: 'alchemyCombat' },
]

/** 炼器页顶部子选项卡配置（精炼 + 六大装备部位 + 技艺工具分组） */
const FORGING_SUB_TABS: ReadonlyArray<{ id: ForgingSubTab; labelKey: 'forgeRefining' | 'forgeWeapon' | 'forgeOffHand' | 'forgeHead' | 'forgeArmor' | 'forgeLegs' | 'forgeTrinket' | 'forgeTechnique' }> = [
  { id: 'refining', labelKey: 'forgeRefining' },
  { id: 'weapon', labelKey: 'forgeWeapon' },
  { id: 'off-hand', labelKey: 'forgeOffHand' },
  { id: 'head', labelKey: 'forgeHead' },
  { id: 'armor', labelKey: 'forgeArmor' },
  { id: 'legs', labelKey: 'forgeLegs' },
  { id: 'trinket', labelKey: 'forgeTrinket' },
  { id: 'technique', labelKey: 'forgeTechnique' },
]

/**
 * 竖屏技艺模式切换栏（炼丹/炼器/符箓/阵法/强化）
 * 桌面端由侧边栏可折叠二级菜单承载；竖屏侧边栏整体隐藏，本栏作为底部导航「技艺」组的
 * 子行渲染（与「历练」「市集」「更多」组的子行同层同款），页面主体内不再重复出现。
 */
const CraftingModeTabs = memo(function CraftingModeTabs(): React.JSX.Element {
  const t = useT()
  const craftingMode = useUiStore(state => state.craftingMode)
  const setCraftingMode = useUiStore(state => state.setCraftingMode)
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)
  return (
    <div aria-label={t('app.nav.crafting')} className="bottom-sub-tabs bottom-sub-tabs--crafting" role="tablist">
      {CRAFTING_NAV_MODES.map(mode => {
        const level = calculateTechniqueProgress(techniqueExperiences?.[mode.id] ?? 0).level
        return (
          <button
            aria-selected={craftingMode === mode.id}
            className={`bottom-sub-tab-btn${craftingMode === mode.id ? ' active' : ''}`}
            key={mode.id}
            onClick={() => setCraftingMode(mode.id)}
            role="tab"
            type="button"
          >
            <span className="bottom-sub-tab-glyph" aria-hidden="true"><StatGlyph glyph={mode.id} tone={mode.tone} size={16} /></span>
            {/* 等级数字经 pill-tone 容器对子级 underlined-value 分色，与侧边栏技艺色一致 */}
            <span className={`bottom-sub-tab-text pill-tone--${mode.tone}`}>
              {t(`app.crafting.${mode.id}`)}
              <span className="bottom-sub-tab-level underlined-value">{level}</span>
            </span>
          </button>
        )
      })}
    </div>
  )
})

/**
 * 技艺页面（整合炼丹与炼器两大修仙工艺系统，支持二级模式切换与顶部多子选项卡）
 */
const CraftingPage = memo(function CraftingPage({ hidden }: { hidden: boolean }): React.JSX.Element {
  const t = useT()
  const actions = useGameStore(state => state.actions)
  const equipment = useGameStore(state => state.equipment)
  const worldNodeId = useGameStore(state => state.world.nodeId)
  const realmLevel = useGameStore(state => state.stats.realmLevel) ?? 1
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)
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
      } else if (forgingSubTab === 'technique') {
        // 技艺工具配方：任一产出物在装备目录中命中 tool-* 专属槽位即归入本组
        list = pool.filter(a => (a.yields ?? []).some(yieldEntry =>
          yieldEntry.kind === 'item'
          && yieldEntry.id !== null
          && yieldEntry.id !== undefined
          && (equipmentSlotMap[yieldEntry.id]?.startsWith('tool-') ?? false)
        ))
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
          if (forgingSubTab === 'trinket' && (a.id.includes('pendant') || a.id.includes('talisman') || a.id.includes('ring'))) return true
          return false
        })
      }
    }

    // 预计算候选行动真实等级，同时按图鉴解锁规则过滤：未解锁配方（等级 > 当前境界等级 且 等级 > 对应技艺等级）在制造面板彻底隐藏
    const actionLevelMap: Record<string, number> = {}
    const unlockedList: ActionDefinition[] = []
    for (const action of list) {
      const level = resolveActionLevel(action, itemCatalog, equipmentCatalog, encounters)
      const techLevel = action.technique
        ? calculateTechniqueProgress(techniqueExperiences?.[action.technique] ?? 0).level
        : 0
      const isTechAllowed = !action.foundationCostRatio && Boolean(action.technique && level <= techLevel)
      if (level <= 1 || level <= realmLevel || isTechAllowed) {
        actionLevelMap[action.id] = level
        unlockedList.push(action)
      }
    }
    // 严格按真实等级升序排序（低等级在前，高等级在后），等级相同时按 ID 稳定排列
    return unlockedList.sort((a, b) => {
      const levelA = actionLevelMap[a.id] ?? 1
      const levelB = actionLevelMap[b.id] ?? 1
      if (levelA !== levelB) return levelA - levelB
      return a.id.localeCompare(b.id)
    })
  }, [availableActions, craftingMode, alchemySubTab, forgingSubTab, equipmentSlotMap, itemCatalog, equipmentCatalog, encounters, realmLevel, techniqueExperiences])

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
      <AutoUse bar={craftingMode} />
      {/* 技艺模式切换条由底部导航「技艺」组的子行承载（竖屏），页面内不再重复渲染 */}
      {selectedAction && <ActionModal action={selectedAction} onClose={closeModal} />}
    </section>
  )
})


/**
 * 自动消耗品槽位组选择与配置组件
 */
const AutoUseSlotGroup = memo(function AutoUseSlotGroup({
  bar,
  className = '',
  hideLabel = false,
  label,
  slotGroup = 'general',
}: {
  /** 物品栏域：历练/各制造技艺/战斗，决定本组槽位来源与物品准入 */
  bar: AutoUseBarType
  className?: string
  hideLabel?: boolean
  label: string
  /** 栏位分组：仅战斗栏域区分 potion（药品栏）/general（通用栏） */
  slotGroup?: 'potion' | 'general'
}): React.JSX.Element {
  const t = useT()
  const allSlots = useGameStore(state => state.queue.autoUseSlots[bar])
  // 战斗栏域槽位数组布局：0-2 药品栏，3-5 通用栏；其余栏域整段通用栏（历练 6 槽、技艺 3 槽）。
  const slotOffset = bar === 'combat' && slotGroup === 'general' ? 3 : 0
  const slots = allSlots.slice(slotOffset, slotOffset + (bar === 'combat' ? 3 : allSlots.length))
  const activeEntry = useGameStore(state => {
    const current = state.queue.entries[0]
    if (!current) return null
    const definition = resolveActionDefinition(current.actionId)
    return autoUseBarFor({ type: current.type, technique: definition?.technique ?? null }) === bar ? current : null
  })
  // 消耗品冷却仅 combat 栏域槽位由服务端按双组下标定长下发（药品栏 0-2、通用栏 3-5）。
  const consumableCooldowns = useGameStore(
    state => bar === 'combat' ? state.combatResources?.consumableCooldownMilliseconds : undefined,
  )
  const inventory = useGameStore(state => state.inventory)
  const itemCatalog = useGameStore(state => state.items)
  const isCombat = bar === 'combat'
  const combat = useGameStore(state => state.combat)
  const [picker, setPicker] = useState<{ index: number; anchor: HTMLElement } | null>(null)
  const [editingTriggerItemId, setEditingTriggerItemId] = useState<string | null>(null)

  const saveItemTriggers = useCallback((triggers: CombatTrigger[] | null) => {
    if (!editingTriggerItemId) return
    // 消耗品条件走 consumableTriggers 独立通道：键存在即覆盖配表默认，空数组 = 无条件恒可用。
    // 不得写 abilityTriggers（键必须是已习得功法，服务端会拒绝消耗品 ID）。
    sendLoadoutReplace({
      consumableTriggers: {
        ...(combat?.consumableTriggers ?? {}),
        [editingTriggerItemId]: triggers ?? [],
      },
    })
  }, [editingTriggerItemId, combat?.consumableTriggers])
  const pickerItems = useMemo<ItemPickerEntry[]>(() => itemCatalog
      .filter(item => item.category === 'consumable' && item.usableBars.includes(bar) && itemAmount(inventory, item.itemId) > 0
        // 药品栏只放药品类即时消耗品；通用栏任意适用自动消耗品皆可（手动消耗品不入栏）。
        && (slotGroup !== 'potion' || (item.subCategory === 'pill' && !item.isContinuous)))
      .map(item => ({
        itemId: item.itemId,
        name: item.name,
        description: item.description,
        category: resolveItemCategoryLabel(resolveItemSubCategory(item.itemId, item), t),
        tier: item.tier,
        amount: itemAmount(inventory, item.itemId),
        definition: item,
      })), [inventory, itemCatalog, slotGroup, t, bar])

  const updateSlot = useCallback((index: number, itemId: string | null) => {
    gameClient.sendCommand('queue.auto_use.configure', {
      actionType: bar,
      slotIndex: index,
      itemId,
      quantity: 1,
      enabled: Boolean(itemId),
      group: slotGroup,
    })
  }, [slotGroup, bar])

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
          const itemTriggers = isCombat && slot.itemId
            ? (combat?.consumableTriggers?.[slot.itemId] ?? selectedDefinition?.triggers ?? [])
            : []
          const conditionCount = itemTriggers.length
          return (
            <div className={`auto-use-slot${picker?.index === index ? ' is-picking' : ''}`} key={index}>
              <ItemUseSlot
                activeEntry={activeEntry}
                conditionCount={isCombat ? conditionCount : undefined}
                cooldownOverlay={cooldownMs > 0 && selectedItem?.definition ? (
                  <AbilityCooldownOverlay
                    remainingMilliseconds={cooldownMs}
                    totalMilliseconds={selectedItem.definition.cooldownSeconds * 1000}
                  />
                ) : null}
                item={selectedItem}
                onCondition={isCombat && selectedItem ? () => setEditingTriggerItemId(selectedItem.itemId) : undefined}
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
      {(() => {
        const pickerItemTriggers = isCombat && pickerSlot?.itemId
          ? (combat?.consumableTriggers?.[pickerSlot.itemId] ?? findItemInCatalog(itemCatalog, pickerSlot.itemId)?.triggers ?? [])
          : []
        return (
          <ItemGridPicker
            anchor={picker?.anchor ?? null}
            onClose={() => setPicker(null)}
            onEditCondition={isCombat && pickerSlot?.itemId ? () => setEditingTriggerItemId(pickerSlot.itemId) : undefined}
            onSelect={itemId => {
              if (picker === null) return
              updateSlot(picker.index, itemId)
            }}
            items={pickerItems}
            open={picker !== null}
            title={picker === null ? t('common.selectItem') : t('common.selectItemSlot', { n: picker.index + 1 })}
            triggerCount={isCombat && pickerSlot?.itemId ? pickerItemTriggers.length : undefined}
            value={pickerSlot?.itemId ?? null}
          />
        )
      })()}
      {isCombat && editingTriggerItemId !== null && (
        <CombatTriggerEditor
          defaultTriggers={findItemInCatalog(itemCatalog, editingTriggerItemId)?.triggers ?? []}
          initialTriggers={combat?.consumableTriggers?.[editingTriggerItemId] ?? findItemInCatalog(itemCatalog, editingTriggerItemId)?.triggers ?? []}
          onClose={() => setEditingTriggerItemId(null)}
          onSave={saveItemTriggers}
          open
          title={t('app.combatPanel.triggerEditorTitle', {
            name: findItemInCatalog(itemCatalog, editingTriggerItemId)?.name ?? editingTriggerItemId,
          })}
        />
      )}
    </section>
  )
})

/**
 * 各行动页签下的自动消耗品快捷托盘
 */
const AutoUse = memo(function AutoUse({ bar }: { bar: AutoUseBarType }): React.JSX.Element {
  const t = useT()
  return (
    <div className="auto-use-tray" aria-label={t('app.action.autoUseTrayAria', { name: autoUseBarLabel(bar) })}>
      <AutoUseSlotGroup bar={bar} hideLabel label={t('app.action.autoUseLabel')} />
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
  const realmLevel = useGameStore(state => state.stats.realmLevel) ?? 1
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)

  // 行动名按当前驻留地点本地化（「讨伐 青云仙山」→「讨伐 漱玉溪」），与节点生态实战语义一致
  const displayName = useMemo(() => localizeActionDisplayText(action.name, world, maps), [action.name, world, maps])

  const level = useMemo(
    () => resolveActionLevel(action, itemCatalog, equipmentCatalog, encounters),
    [action, itemCatalog, equipmentCatalog, encounters]
  )

  const techLevel = useMemo(() => {
    if (!action.technique) return 0
    return calculateTechniqueProgress(techniqueExperiences?.[action.technique] ?? 0).level
  }, [action.technique, techniqueExperiences])

  const isUnlocked = level <= 1 || level <= realmLevel || (action.technique ? level <= techLevel : false);
  const isLocked = !isUnlocked;
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
      className={`action-card${isCrafting ? ' action-card--crafting' : ''}${isLocked ? ' action-card--locked' : ''}`}
      data-action-id={action.id}
      data-rarity={rarity}
      onClick={() => onOpen(action)}
      type="button"
    >
      <span className="item-corner item-corner--top-right">
        {isLocked && <span className="action-card-lock-icon" aria-hidden="true">🔒</span>}
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
  const combat = useGameStore(state => state.combat)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const queueLength = useGameStore(state => state.queue.entries.length)
  const encounters = useGameStore(state => state.encounters)
  const realmLevel = useGameStore(state => state.stats.realmLevel) ?? 1
  const duel = useGameStore(state => state.duel)
  const nodeDuelCombatTeamCount = useGameStore(state => state.nodeDuelCombatTeamCount)
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)
  // 制造类行动（炼丹/炼器/精炼/强化）默认单轮：材料逐轮消耗结算，无限默认会烧穿背包；
  // 其余行动（探索/修炼/战斗/赶路）维持无限循环口径不变。
  const isManufacture = action.type === 'alchemy' || action.type === 'enhancement'
  const isDrawQi = action.id === 'cultivation.draw-qi' || action.type === 'cultivation'
  const [repeats, setRepeats] = useState(isManufacture ? 1 : -1)
  const [spiritStoneTier, setSpiritStoneTier] = useState<number>(0)
  const [pvpEnabled, setPvpEnabled] = useState(false)
  const [, setClockTick] = useState(0)

  const actionLevel = useMemo(
    () => resolveActionLevel(action, items, equipmentCatalog, encounters),
    [action, items, equipmentCatalog, encounters]
  )
  const techLevel = useMemo(() => {
    if (!action.technique) return 0
    return calculateTechniqueProgress(combat?.techniqueExperiences?.[action.technique] ?? 0).level
  }, [action.technique, combat?.techniqueExperiences])

  const isUnlocked = actionLevel <= 1 || actionLevel <= realmLevel || (action.technique ? actionLevel <= techLevel : false)
  const isLocked = !isUnlocked
  const yieldPreview = useMemo(() => calculateActionYieldPreview({
    action,
    inventory,
    items,
    autoUseSlots,
    world,
    stats,
    combat,
    equipmentCatalog,
    parameters: spiritStoneTier > 0 ? { spiritStone: spiritStoneTier } : undefined,
  }), [action, inventory, items, autoUseSlots, world, stats, combat, equipmentCatalog, spiritStoneTier])

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

  // 特殊配方的底蕴成本基数：当前升级所需修为（与服务端 ResolveExpCapWithLinggen 折算口径一致，含灵根乘区）。
  const foundationCostExpBase = useMemo(
    () => calculateRealmProgress(stats?.experience ?? 0, stats?.realmLevel,
      serverExpToNextCap(stats?.expToNextLinggen, stats?.realmLevel)).expToNext ?? 0,
    [stats?.experience, stats?.realmLevel, stats?.expToNextLinggen],
  )
  const inputs = useMemo(() => {
    const list = Object.entries(action.inputs ?? {}).map(([itemId, amount]) => ({ itemId, amount }))
    if (isDrawQi && spiritStoneTier > 0) {
      list.push({ itemId: 'currency.spirit-stone', amount: spiritStoneTier })
    }
    return list
  }, [action.inputs, isDrawQi, spiritStoneTier])
  const titleId = `action-modal-title-${action.id}`
  const startsImmediately = action.executionKind === 'combat-encounter'
  useEffect(() => {
    if (startsImmediately) {
      gameClient.pingNow()
    }
  }, [startsImmediately])
  // 行动名/描述按当前驻留地点本地化（「讨伐 青云仙山」→「讨伐 漱玉溪」）
  const displayName = localizeActionDisplayText(action.name, world, maps)
  const displayDescription = action.description ? localizeActionDisplayText(action.description, world, maps) : action.description
  const isCrafting = action.type === 'alchemy' || action.id.startsWith('craft.') || action.id.startsWith('forge.') || action.id.startsWith('alchemy.')
  // 非战斗行动五段式归类：灵力入产出、修为与六维及生活技艺经验入感悟
  const spiritYields = yieldPreview.resourceYields.filter(entry => entry.kind === 'mana')
  const expYields = yieldPreview.resourceYields.filter(entry => entry.kind === 'experience')
  const otherResourceYields = yieldPreview.resourceYields.filter(entry => entry.kind !== 'mana' && entry.kind !== 'experience')
  const hasOutputs = yieldPreview.itemOutputs.length > 0 || spiritYields.length > 0 || otherResourceYields.length > 0 || yieldPreview.buffYields.length > 0
  const techniqueId = typeof action.technique === 'string' && (TECHNIQUE_IDS as readonly string[]).includes(action.technique)
    ? action.technique
    : null
  const techniqueExp = techniqueId
    ? Math.max(1, Math.round(calculateBaseExperienceForLevel(actionLevel) * ((action.durationSeconds || 10) / 10.0)))
    : 0
  const hasInsights = expYields.length > 0 || yieldPreview.rootExpYields.length > 0 || techniqueExp > 0
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
          <dd>{inputs.length === 0 && !(action.foundationCostRatio ?? 0) ? <span className="action-detail-empty">{t('app.action.none')}</span> : inputs.map(input => (
            <ActionDetailItem
              amount={input.amount}
              current={itemAmount(inventory, input.itemId)}
              itemId={input.itemId}
              key={input.itemId}
            />
          ))}
          {(action.foundationCostRatio ?? 0) > 0 && (
            // 底蕴成本按当前升级所需修为折算，是特殊配方（逆命丹）的额外投入。
            <span className="action-detail-extra">
              {t('app.action.foundationCost', {
                amount: Math.max(1, Math.round(foundationCostExpBase * (action.foundationCostRatio ?? 0))),
                current: Math.floor(stats?.foundation ?? 0),
              })}
            </span>
          )}</dd>
        </div>

        {startsImmediately ? (
          <>
            <div>
              <dt>{t('app.action.outputs')}</dt>
              <dd>
                <span className="action-detail-empty">{t('app.action.combatDrops')}</span>
              </dd>
            </div>
            <div>
              <dt>{t('app.action.duration')}</dt>
              <dd><span className="action-detail-value">{t('app.action.combatUnlimited')}</span></dd>
            </div>
          </>
        ) : (
          <>
            <div>
              <dt>{t('app.action.outputs')}</dt>
              <dd>
                {!hasOutputs && <span className="action-detail-empty">{t('app.action.none')}</span>}
                {spiritYields.map(entry => (
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
                {otherResourceYields.map(entry => (
                  <span className={`action-stat-pill action-stat-pill--${entry.kind}`} key={entry.kind}>
                    <span className="action-stat-pill-label">{t(`app.action.yieldKind.${entry.kind}`)}</span>
                    <strong className="action-stat-pill-value">
                      <UnitNumber disableTooltip maxDecimals={1} prefix="×" value={entry.finalAmount} />
                    </strong>
                  </span>
                ))}
                {yieldPreview.buffYields.map(buff => (
                  <span className="action-stat-pill action-stat-pill--buff" key={buff.buffId}>
                    <span className="action-stat-pill-label">{t('app.action.yieldKind.buff')}</span>
                    <strong className="action-stat-pill-value">
                      {t('app.action.yieldBuff', { name: buff.name, stacks: buff.stacks, duration: buff.durationSeconds })}
                    </strong>
                  </span>
                ))}
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
              </dd>
            </div>

            <div>
              <dt>{t('app.action.insights')}</dt>
              <dd>
                {!hasInsights && !isPending && <span className="action-detail-empty">{t('app.action.none')}</span>}
                {expYields.map(entry => (
                  <span className={`action-stat-pill action-stat-pill--${entry.kind}`} key={entry.kind}>
                    <span className="action-stat-pill-label">{t(`app.action.yieldKind.${entry.kind}`)}</span>
                    <strong className="action-stat-pill-value">
                      <UnitNumber disableTooltip maxDecimals={1} prefix="×" value={entry.finalAmount} />
                    </strong>
                  </span>
                ))}
                {yieldPreview.rootExpYields.map(entry => (
                  <span className="action-stat-pill action-stat-pill--rootexp" key={entry.aspect}>
                    <span className="action-stat-pill-label">{t(ROOT_ASPECT_LABEL_KEYS[entry.aspect] ?? 'app.action.yieldKind.rootexp')}</span>
                    <strong className="action-stat-pill-value">
                      <UnitNumber disableTooltip maxDecimals={1} prefix="×" value={entry.amount} />
                    </strong>
                  </span>
                ))}
                {techniqueId && techniqueExp > 0 && (
                  <span className="action-stat-pill action-stat-pill--technique" key={`tech-${techniqueId}`}>
                    <span className="action-stat-pill-label">{t(`insight.technique.${techniqueId}`)}</span>
                    <strong className="action-stat-pill-value">
                      <UnitNumber disableTooltip maxDecimals={1} prefix="×" value={techniqueExp} />
                    </strong>
                  </span>
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

            <div>
              <dt>{t('app.action.duration')}</dt>
              <dd style={{ alignItems: 'center' }}>
                <span className="action-detail-value">
                  {t('app.action.durationPerRound', { seconds: formatNumber(Math.max(0.1, yieldPreview.actualDurationSeconds), { maxDecimals: 1 }) })}
                </span>
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
              </dd>
            </div>
            {isDrawQi && (
              <div>
                <dt>{t('app.action.spiritStoneInject')}</dt>
                <dd>
                  <div className="spirit-stone-tier-group" role="radiogroup" aria-label={t('app.action.spiritStoneInject')}>
                    {[
                      { value: 0, label: t('app.action.spiritStoneTierNone') },
                      { value: 1, label: t('app.action.spiritStoneTier1') },
                      { value: 10, label: t('app.action.spiritStoneTier10') },
                      { value: 100, label: t('app.action.spiritStoneTier100') },
                    ].map(tier => (
                      <button
                        key={tier.value}
                        type="button"
                        className={`spirit-stone-tier-btn${spiritStoneTier === tier.value ? ' is-active' : ''}`}
                        onClick={() => setSpiritStoneTier(tier.value)}
                      >
                        {tier.label}
                      </button>
                    ))}
                  </div>
                </dd>
              </div>
            )}

            <div>
              <dt>{t('app.action.repeats')}</dt>
              <dd>
                <QuantitySelector
                  allowInfinity
                  ariaLabel={t('app.action.repeatsAria')}
                  max={1000}
                  onChange={setRepeats}
                  presets={[1, 10, 100]}
                  value={repeats}
                />
              </dd>
            </div>
          </>
        )}
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
            {nodeDuelCombatTeamCount !== null && (
              <span className="duel-node-teams">{t('app.action.duel.nodeTeams', { count: nodeDuelCombatTeamCount })}</span>
            )}
          </div>
        </section>
      )}

      {startsImmediately && (
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
      )}

      <button
        className={`action-modal-submit${isLocked ? ' is-locked' : ''}`}
        data-tour="action-submit"
        disabled={isLocked}
        onClick={() => {
          if (gameClient.sendCommand('action.enqueue', {
            actionId: action.id,
            repeatCount: repeats,
            pvpEnabled,
            parameters: spiritStoneTier > 0 ? { spiritStone: spiritStoneTier } : undefined,
          })) {
            onClose()
            if (action.type === 'combat' || startsImmediately) {
              useUiStore.getState().setActiveTab('combat')
            }
          }
        }}
        type="button"
      >{isLocked
        ? (action.technique
            ? t('app.action.levelOrTechniqueRequired', {
                level: actionLevel,
                technique: t(`insight.technique.${action.technique}`),
              })
            : t('app.action.levelRequired', { level: actionLevel }))
        : startsImmediately
        ? t('app.action.startCombatNow')
        : t('app.action.enqueueAction', { n: queueLength + 1 })}</button>
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

    // 文本与可见性：0.1s 显示精度只需 100ms 唤醒，内容未变化不写 DOM
    const syncText = () => {
      const remaining = Math.max(0, initialRemaining - (performance.now() - startedAt))
      const text = remaining > 0 ? `${(remaining / 1000).toFixed(1)}s` : ''
      if (overlay.textContent !== text) overlay.textContent = text
      const hidden = remaining <= 0
      if (overlay.hidden !== hidden) overlay.hidden = hidden
      return remaining
    }
    if (syncText() <= 0) return

    // 扇形遮罩经 @property 注册的 --cooldown-ratio 走 WAAPI 动画时间轴，
    // 冷却全程不再逐帧 setProperty 触发主线程样式重算。
    const ratioFrom = Math.min(1, initialRemaining / total)
    overlay.style.setProperty('--cooldown-ratio', String(ratioFrom))
    const anim = overlay.animate(
      [{ '--cooldown-ratio': String(ratioFrom) }, { '--cooldown-ratio': '0' }],
      { duration: initialRemaining, easing: 'linear', fill: 'forwards' },
    )
    const timer = window.setInterval(() => {
      if (syncText() <= 0) window.clearInterval(timer)
    }, 100)

    return () => {
      window.clearInterval(timer)
      anim.cancel()
    }
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
  const [editingTriggerAbilityId, setEditingTriggerAbilityId] = useState<string | null>(null)
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
  // 触发条件跟随功法而非槽位：按功法 ID 读写 combat.abilityTriggers。
  const saveTriggers = (triggers: CombatTrigger[] | null) => {
    if (editingTriggerAbilityId === null) return
    update({
      abilityTriggers: {
        ...(combat.abilityTriggers ?? {}),
        [editingTriggerAbilityId]: triggers ?? [],
      },
    })
  }

  const pickerValue = pickerTarget === null
    ? null
    : combat.abilitySlots[pickerTarget.index]?.abilityId ?? null
  const currentPickerAbility = pickerValue !== null
    ? findAbilityInCatalog(abilities, pickerValue)
    : null
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
        <div className="combat-slot-row combat-slot-row--consumables">
          <AutoUseSlotGroup bar="combat" className="combat-slot-section" label={t('app.combatPanel.consumablesGroup')} slotGroup="potion" />
          <AutoUseSlotGroup bar="combat" className="combat-slot-section" label={t('app.combatPanel.generalGroup')} slotGroup="general" />
        </div>

        <section className="slot-group combat-slot-section combat-slot-section--abilities" aria-label={t('app.combatPanel.abilitiesAria')}>
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
              const triggerCount = ability ? (combat.abilityTriggers?.[ability.abilityId]?.length ?? 0) : 0
              const abilityExp = ability ? (combat.abilityExperiences?.[ability.abilityId] ?? 0) : 0
              const slotAsideCards = ability ? resolveAbilityAsideCards(ability, abilityExp) : undefined
              return (
                <ConditionedTile
                  as="div"
                  asideCards={slotAsideCards}
                  className={`combat-slot-card${pickerTarget?.index === index ? ' is-picking' : ''}`}
                  condition={ability ? {
                    ariaLabel: t('app.combatPanel.editTriggersAria', { name: ability.name, count: triggerCount }),
                    hasTriggers: triggerCount > 0,
                    label: t('app.combatPanel.triggerConditionBtn', { count: triggerCount > 0 ? triggerCount : '' }),
                    onClick: () => setEditingTriggerAbilityId(ability.abilityId),
                    title: t('app.combatPanel.editTriggersTitle'),
                  } : null}
                  content={ability ? describeAbility(ability, abilityLevelFor(ability.abilityId)) : null}
                  extras={ability && (
                    /* 装配等级恒等于已参悟等级（调低无收益），展示为静态徽标而非输入框 */
                    <span className="combat-ability-level-control">
                      <span aria-hidden="true">Lv.</span>
                      <span className="combat-ability-level-value">{abilityLevelFor(ability.abilityId)}</span>
                    </span>
                  )}
                  key={index}
                  triggerClassName="combat-ability-slot-shell"
                  triggerTabIndex={-1}
                >
                  {slotButton}
                </ConditionedTile>
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
        onEditCondition={currentPickerAbility ? () => setEditingTriggerAbilityId(currentPickerAbility.abilityId) : undefined}
        onSelect={selectPickerValue}
        open={pickerTarget !== null}
        slotIndex={pickerTarget?.index}
        title={pickerTitle}
        triggerCount={currentPickerAbility ? (combat.abilityTriggers?.[currentPickerAbility.abilityId]?.length ?? 0) : 0}
        value={pickerValue}
      />
      {editingTriggerAbilityId !== null && (
        <CombatTriggerEditor
          defaultTriggers={findAbilityInCatalog(abilities, editingTriggerAbilityId)?.defaultTriggers ?? []}
          initialTriggers={combat.abilityTriggers?.[editingTriggerAbilityId] ?? findAbilityInCatalog(abilities, editingTriggerAbilityId)?.defaultTriggers ?? []}
          onClose={() => setEditingTriggerAbilityId(null)}
          onSave={saveTriggers}
          open
          title={t('app.combatPanel.triggerEditorTitle', {
            name: findAbilityInCatalog(abilities, editingTriggerAbilityId)?.name ?? editingTriggerAbilityId,
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
  const itemCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const encounters = useGameStore(state => state.encounters)
  const world = useGameStore(state => state.world)
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)
  const realmLevel = useGameStore(state => state.stats.realmLevel)
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
    const cultivations: ActionDefinition[] = []
    const hunts: ActionDefinition[] = []
    const others: ActionDefinition[] = []
    const byTechnique = new Map<string, ActionDefinition[]>()
    for (const action of visibleActions) {
      if (action.type === 'cultivation') { cultivations.push(action); continue }
      if (action.type === 'combat') { hunts.push(action); continue }
      if (action.technique) {
        const list = byTechnique.get(action.technique) ?? []
        list.push(action)
        byTechnique.set(action.technique, list)
        continue
      }
      others.push(action)
    }
    // 自动选取自身等级可以使用的最高等级的修炼，修炼行动只会显示一个
    if (cultivations.length > 0) {
      const currentRealmLevel = realmLevel ?? 1
      const available = cultivations.filter(a => (a.level ?? 1) <= currentRealmLevel)
      const best = available.length > 0
        ? available.reduce((prev, curr) => (curr.level ?? 1) > (prev.level ?? 1) ? curr : prev)
        : cultivations.reduce((prev, curr) => (curr.level ?? 1) < (prev.level ?? 1) ? curr : prev)
      resident.push(best)
    }
    if (resident.length > 0) result.push({ key: 'resident', label: t('app.combatPanel.residentActions'), actions: resident })
    for (const id of TECHNIQUE_IDS) {
      const list = byTechnique.get(id)
      if (!list || list.length === 0) continue
      const level = calculateTechniqueProgress(techniqueExperiences?.[id] ?? 0).level
      const sortedList = [...list].sort((a, b) => {
        const diff = resolveActionLevel(a, itemCatalog, equipmentCatalog, encounters) - resolveActionLevel(b, itemCatalog, equipmentCatalog, encounters)
        if (diff !== 0) return diff
        return a.id.localeCompare(b.id)
      })
      result.push({
        key: `technique:${id}`,
        label: t('app.combatPanel.techniqueGroup', { name: t(`insight.technique.${id}`), level }),
        actions: sortedList,
      })
    }
    if (others.length > 0) result.push({ key: 'other', label: t('app.combatPanel.otherActions'), actions: others })
    if (hunts.length > 0) result.push({ key: 'combat', label: t('app.combatPanel.huntActions'), actions: hunts })
    return result
  }, [realmLevel, techniqueExperiences, t, visibleActions, itemCatalog, equipmentCatalog, encounters])

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
      <AutoUse bar="exploration" />
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
  // 地图本体首次可见才挂载：避免应用启动即构建全图索引、挂视口观测并在后台补丁中重渲染。
  // 首次挂载后常驻不卸载，漫游视口、选中地点与运镜状态与改造前完全一致。
  const [hasBeenVisible, setHasBeenVisible] = useState(!hidden)
  if (!hidden && !hasBeenVisible) setHasBeenVisible(true)

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
        {hasBeenVisible && activeSubTab === 'map' && <WorldMapPanel hidden={hidden} />}
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
  const [hasBeenVisible, setHasBeenVisible] = useState(!hidden)
  // 只延迟首次查看：后续切回其它页签不卸载战场，保留详情、动画与观察目标生命周期。
  if (!hidden && !hasBeenVisible) setHasBeenVisible(true)
  const [selectedAction, setSelectedAction] = useState<ActionDefinition | null>(null)
  const [reportOpen, setReportOpen] = useState(false)
  const closeModal = useCallback(() => setSelectedAction(null), [])
  const closeReport = useCallback(() => setReportOpen(false), [])

  return (
    <section className="panel expedition-page" data-tour="combat-detail" hidden={hidden}>
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
          {hasBeenVisible && (
            <Suspense fallback={<PanelLoading label={t('app.nav.combat')} />}>
              <CombatArena hidden={hidden} onOpenAction={setSelectedAction} />
            </Suspense>
          )}
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



/** 底部导航「更多」角标统计的日志分组：战斗分组恒不计未读，其余计入（与 logUnreadStore 语义一致） */
const LOG_GROUPS_FOR_BADGE = ['all', 'world', 'nearby', 'system', 'friend', 'sect', 'party'] as const

/**
 * 移动端底部导航栏：7 个一级分组 + 组内子行。
 *
 * 分组与组内页签真源在 `mobileNav.ts`；本组件只负责渲染与点击编排：
 * - 点击非当前分组 → 切到该组记忆页签并展开子行（一次点击直达内容）；
 * - 点击当前分组 → 不变签（子行本就展开，重复点可选子项）；
 * - 组内子项点击 → 切换页签，子行保持展开。
 * 子行高度经 `--mobile-nav-offset` 反写页面底部占位，避免内容被遮。
 */
const BottomNav = memo(function BottomNav(): React.JSX.Element {
  const t = useT()
  const activeTab = useUiStore(state => state.activeTab)
  const setActiveTab = useUiStore(state => state.setActiveTab)
  const activateGroup = useUiStore(state => state.activateMobileNavGroup)
  const navRef = useRef<HTMLElement>(null)
  const activeGroupId = mobileNavGroupIdOf(activeTab)
  const activeGroup = mobileNavGroupById(activeGroupId)

  // 未读与邀请角标必须上提到一级分组：日志/同道沉到「更多」子行后，
  // 若不在分组层露出计数，私聊与组队邀请会完全静默。分组角标取两者之和，
  // 子行各自只显示自己的那一份（日志显示未读数、同道显示有多少条邀请）。
  const unreadCounts = useLogUnreadStore(state => state.unreadCounts)
  const partyInvitation = useSocialStore(state => state.partyInvitation)
  const logUnread = useMemo(() => {
    let total = 0
    for (const group of LOG_GROUPS_FOR_BADGE) total += unreadCounts[group] ?? 0
    return total
  }, [unreadCounts])
  const inviteBadge = partyInvitation ? 1 : 0
  const moreBadge = logUnread + inviteBadge
  /** 子行各自角标：日志只报未读条数，同道只报待处理邀请数，避免两项混算误导 */
  const subBadge = (tab: TabId): number => tab === 'log' ? logUnread : tab === 'social' ? inviteBadge : 0

  // 子行高度随内容（技艺条比纯文字子行高）变化，用实测高度反写 CSS 变量驱动底部占位。
  const subVisible = activeGroup.subNav !== 'none'
  useLayoutEffect(() => {
    const node = navRef.current
    if (!node) return undefined
    const root = document.documentElement
    const apply = () => root.style.setProperty('--mobile-nav-offset', `${node.getBoundingClientRect().height}px`)
    apply()
    if (typeof ResizeObserver !== 'function') return undefined
    const observer = new ResizeObserver(apply)
    observer.observe(node)
    return () => observer.disconnect()
  }, [subVisible, activeGroupId])

  return (
    <nav aria-label={t('app.nav.mobileNavAria')} className="bottom-nav" ref={navRef}>
      {subVisible && (
        <div className="bottom-sub-nav">
          {activeGroup.subNav === 'crafting'
            ? <CraftingModeTabs />
            : activeGroup.tabs.length > 1 && (
              <div aria-label={t(`app.nav.${activeGroup.labelKey}`)} className="bottom-sub-tabs" role="tablist">
                {activeGroup.tabs.map(tab => (
                  <button
                    aria-selected={activeTab === tab}
                    className={`bottom-sub-tab-btn${activeTab === tab ? ' active' : ''}`}
                    data-tour={tab === 'map' ? 'nav-map' : undefined}
                    key={tab}
                    onClick={() => setActiveTab(tab)}
                    role="tab"
                    type="button"
                  >
                    <span className="bottom-sub-tab-glyph" aria-hidden="true"><Icon name={TAB_ICON[tab]} /></span>
                    <span className="bottom-sub-tab-text">{t(`app.nav.${tab}`)}</span>
                    {subBadge(tab) > 0 && activeTab !== tab && (
                      <span className="bottom-sub-tab-badge">{subBadge(tab) > 99 ? '99+' : subBadge(tab)}</span>
                    )}
                  </button>
                ))}
              </div>
            )}
        </div>
      )}
      <div className="bottom-nav-row">
        {MOBILE_NAV_GROUPS.map(group => {
          const isActive = group.id === activeGroupId
          const badge = group.id === 'more' ? moreBadge : 0
          return (
            <button
              aria-current={isActive ? 'page' : undefined}
              className={isActive ? 'active' : ''}
              key={group.id}
              onClick={() => activateGroup(group.id)}
              type="button"
            >
              <span className="bottom-glyph" aria-hidden="true">
                <Icon name={group.icon} />
                {badge > 0 && <span className="bottom-glyph-badge">{badge > 99 ? '99+' : badge}</span>}
              </span>
              <span>{t(`app.nav.${group.labelKey}`)}</span>
            </button>
          )
        })}
      </div>
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
  const t = useT()
  const activeTab = useUiStore(state => state.activeTab)
  const mobileLayout = useMobileLayout()
  const connection = useGameStore(state => state.connection)
  const hasReceivedSnapshot = useGameStore(state => state.hasReceivedSnapshot)
  const disconnectReason = useGameStore(state => state.disconnectReason)
  const tabBlocked = useAntiMultiOpenStore(state => state.tabBlocked)
  const currentAccount = useAccountStore(state => state.currentAccount)
  const sessionChecked = useAccountStore(state => state.sessionChecked)
  const restoreSession = useAccountStore(state => state.restoreSession)
  const logout = useAccountStore(state => state.logout)
  const [catalogAttempt, setCatalogAttempt] = useState(0)
  const [catalogError, setCatalogError] = useState<string | null>(null)
  const simulatorOpen = useUiStore(state => state.simulatorOpen)
  const setSimulatorOpen = useUiStore(state => state.setSimulatorOpen)
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
        // 触发条件编辑器的 Buff 词表与规约常量：服务端配表派生，前端不另写一份。
        useGameStore.getState().setTriggerBuffs(catalog.buffs)
        useGameStore.getState().setTriggerSpec(catalog.triggerSpec)
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

  let content: React.JSX.Element
  if (tabBlocked) {
    content = <MultiTabBlockedPage />
  } else if (!currentAccount) {
    content = <LoginPage />
  } else if (catalogError) {
    content = <ConnectionErrorPage message={catalogError} onRetry={() => setCatalogAttempt(attempt => attempt + 1)} onLogout={logout} />
  } else if (!catalogReady) {
    content = <ConnectingPage onCancel={logout} />
  } else if (!hasReceivedSnapshot) {
    if (connection === 'connecting' || connection === 'reconnecting' || connection === 'online') {
      content = <ConnectingPage onCancel={logout} />
    } else {
      content = <ConnectionErrorPage message={disconnectReason ? t(disconnectReason) : undefined} onRetry={() => gameClient.connect()} onLogout={logout} />
    }
  } else if (connection === 'offline') {
    content = <ConnectionErrorPage message={disconnectReason ? t(disconnectReason) : undefined} onRetry={() => gameClient.connect()} onLogout={logout} />
  } else {
    content = (
      <div className="app">
        <div className="motes" aria-hidden="true"><i /><i /><i /><i /><i /><i /></div>
        <TopBar />
        <div className="app-body">
          <Sidebar />
          <div className="center-column">
            <main className="main-content">
              <ExplorationPage hidden={activeTab !== 'exploration'} />
              <CraftingPage hidden={activeTab !== 'crafting'} />
              <MapPage hidden={activeTab !== 'map'} />
              <CombatDetailsPage hidden={activeTab !== 'combat'} />
              <Suspense fallback={null}>
                {activeTab === 'insight' && <InsightPage />}
              </Suspense>
              <Suspense fallback={null}>
                {activeTab === 'market' && <MarketPage />}
              </Suspense>
              <Suspense fallback={null}>
                {activeTab === 'social' && <SocialPage />}
              </Suspense>
              <Suspense fallback={null}>
                {activeTab === 'codex' && <CodexPage />}
              </Suspense>
              <Suspense fallback={null}>
                {activeTab === 'shop' && <ShopPage />}
              </Suspense>
              <Suspense fallback={null}>
                {activeTab === 'settings' && <SettingsPage />}
              </Suspense>
              {activeTab === 'records' && <RecordsPanel />}
              {activeTab === 'inventory' && <RightSidebar className="mobile-tab" />}
              {activeTab === 'log' && mobileLayout && <GameLogPanel className="mobile-tab" />}
            </main>
            <div className="desktop-log">{!mobileLayout && <GameLogPanel />}</div>
          </div>
          <aside className="right-column"><RightSidebar /></aside>
        </div>
        <BottomNav />
        <TutorialOverlay />
        <PlayerCardModal />
        <AwakenResultModal />
        {simulatorOpen && (
          <Suspense fallback={null}>
            <CombatSimulatorModal open={simulatorOpen} onClose={() => setSimulatorOpen(false)} />
          </Suspense>
        )}
      </div>
    )
  }

  return (
    <>
      {content}
      <Notice />
    </>
  )
}
