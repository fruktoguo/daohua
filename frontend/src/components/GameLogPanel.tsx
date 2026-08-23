import { memo, useCallback, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react'
import { DAMAGE_CATEGORY_NAMES, ELEMENT_NAMES, describeAbility, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { FloatingInfoCard, FloatingPopover, FloatingTooltip, type FloatingTone } from './FloatingPopover'
import { ItemReference } from './ItemDisplay'
import { gameLogService, type GameLogEntry, type LogSegment } from '../logging/gameLogService'
import { ValueText } from './ValueText'

/** 格式化日志时间戳为月-日 时:分:秒 */
function formatLogTime(timestampMs: number): string {
  const date = new Date(timestampMs)
  const part = (value: number) => String(value).padStart(2, '0')
  return `${part(date.getMonth() + 1)}-${part(date.getDate())} ${part(date.getHours())}:${part(date.getMinutes())}:${part(date.getSeconds())}`
}

/**
 * 战报中的功法/神通引用组件 (AbilityReference)
 *
 * 鼠标悬停展示功法详细效果、伤害加成与冷却机制气泡。
 */
function AbilityReference({ abilityId, label }: { abilityId: string; label: string }): React.JSX.Element {
  const abilities = useGameStore((state) => state.abilities)
  const ability = abilities.find((entry) => entry.abilityId === abilityId)

  if (!ability) {
    return (
      <FloatingTooltip placement="top" title={`功法技能：${label}`} tone="qi">
        <span className="log-reference log-reference--ability">{label}</span>
      </FloatingTooltip>
    )
  }

  const tooltip = describeAbility(ability, 1)

  return (
    <FloatingPopover
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={tooltip.badge}
          badgeTone="qi"
          description={tooltip.description}
          lines={tooltip.lines?.map((line) => (
            <ValueText className="effect-value-capsule" key={line} text={line} />
          ))}
          title={tooltip.title}
        />
      }
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      trigger="hover"
      triggerClassName="log-reference log-reference--ability"
    >
      {label}
    </FloatingPopover>
  )
}

/**
 * 战报中的妖兽/敌方遭遇引用组件 (EnemyReference)
 *
 * 悬停展示敌怪流派、属性、气血真元等面板数据。
 */
function EnemyReference({
  enemyId,
  kind,
  label,
}: {
  enemyId: string
  kind: 'enemy' | 'encounter'
  label: string
}): React.JSX.Element {
  const encounters = useGameStore((state) => state.encounters)
  const encounter = encounters.find(
    (entry) => entry.encounterId === enemyId || entry.enemy?.enemyId === enemyId
  )
  const enemy = encounter?.enemy

  if (encounter || enemy) {
    const stats: { label: string; value: React.ReactNode; tone?: FloatingTone }[] = []
    if (enemy?.category) stats.push({ label: '大类', value: DAMAGE_CATEGORY_NAMES[enemy.category] ?? enemy.category, tone: 'gold' })
    if (enemy?.element && enemy.element !== 'none') stats.push({ label: '五行', value: ELEMENT_NAMES[enemy.element] ?? enemy.element, tone: 'purple' })
    if (enemy?.maxHitpoints) stats.push({ label: '气血', value: formatNumber(enemy.maxHitpoints), tone: 'danger' })
    if (enemy?.maxManapoints) stats.push({ label: '真元', value: formatNumber(enemy.maxManapoints), tone: 'qi' })

    return (
      <FloatingPopover
        closeDelay={120}
        content={
          <FloatingInfoCard
            badge={kind === 'encounter' ? '遭遇敌怪' : '妖兽敌人'}
            badgeTone="gold"
            description={encounter ? '出没于修仙界中的敌方遭遇。' : '战斗中的妖兽敌人。'}
            stats={stats.length > 0 ? stats : undefined}
            title={label || enemy?.name || encounter?.name || enemyId}
          />
        }
        gap={8}
        interactive={false}
        openDelay={60}
        placement="bottom"
        trigger="hover"
        triggerClassName={`log-reference log-reference--${kind}`}
      >
        {label}
      </FloatingPopover>
    )
  }

  return (
    <FloatingTooltip placement="top" title={`妖兽敌怪：${label}`} tone="gold">
      <span className={`log-reference log-reference--${kind}`}>{label}</span>
    </FloatingTooltip>
  )
}

/**
 * 日志单条分段内容解析分发组件 (LogContent)
 *
 * 识别文本、物品掉落、功法施放、妖兽击败等分段类型并附加富文本高亮与交互气泡。
 */
function LogContent({ segment }: { segment: LogSegment }): React.JSX.Element {
  if (segment.type === 'text') return <ValueText className="log-value-capsule" text={segment.value} />
  if (segment.kind === 'item') {
    return (
      <ItemReference
        amount={segment.amount}
        className="log-reference log-reference--item"
        itemId={segment.id}
        label={segment.label}
      />
    )
  }
  if (segment.kind === 'ability') {
    return <AbilityReference abilityId={segment.id} label={segment.label} />
  }
  if (segment.kind === 'enemy' || segment.kind === 'encounter') {
    return <EnemyReference enemyId={segment.id} kind={segment.kind} label={segment.label} />
  }
  return (
    <span className={`log-reference log-reference--${segment.kind}`} title={`${segment.kind}: ${segment.id}`}>
      {segment.label}
    </span>
  )
}

/**
 * 单行游戏日志展示组件 (LogRow)
 */
const LogRow = memo(function LogRow({ entry }: { entry: GameLogEntry }): React.JSX.Element {
  const displayTime = formatLogTime(entry.timestampMs)
  return (
    <li>
      <time dateTime={new Date(entry.timestampMs).toISOString()}>{displayTime}</time>
      <span className="log-message">
        {entry.segments.map((segment, index) => <LogContent key={`${entry.id}-${index}`} segment={segment} />)}
      </span>
    </li>
  )
})

/**
 * 游戏战报与系统日志主面板组件 (GameLogPanel)
 *
 * 核心机制与交互：
 * 1. 订阅全局 gameLogService 状态快照 (useSyncExternalStore)；
 * 2. 自动贴底滚动：当玩家未向上滚动翻查历史时，新日志产生自动滑动到最新行；
 * 3. 向上滚动懒加载：滑动到顶部触碰阈值时异步向服务端拉取更早的历史日志；
 * 4. 新日志计数悬浮：向上浏览历史时提示“回到底部 (N条新动态)”按钮。
 */
export function GameLogPanel({ className = '' }: { className?: string }): React.JSX.Element {
  const t = useT()
  const logState = useSyncExternalStore(gameLogService.subscribe, gameLogService.getSnapshot)
  const [tab, setTab] = useState<'log' | 'chat'>('log')
  const [history, setHistory] = useState<readonly GameLogEntry[] | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [reachedStart, setReachedStart] = useState(false)
  const historyReceivedCount = useRef(0)
  const listRef = useRef<HTMLOListElement>(null)
  const scrollToBottom = useRef(true)
  const loadingHistoryRef = useRef(false)
  const visibleEntries = (history ?? logState.latest).slice(-100)
  const unseenCount = history === null ? 0 : Math.max(0, logState.receivedCount - historyReceivedCount.current)

  useLayoutEffect(() => {
    const list = listRef.current
    if (!list || tab !== 'log' || (history !== null && !scrollToBottom.current)) return
    list.scrollTop = list.scrollHeight
    scrollToBottom.current = false
  }, [history, logState.revision, tab])

  const showLatest = useCallback(() => {
    setHistory(null)
    setReachedStart(false)
    scrollToBottom.current = true
  }, [])

  const loadOlder = useCallback(async (entries: readonly GameLogEntry[]) => {
    if (loadingHistoryRef.current || reachedStart || !logState.playerId || entries.length === 0) return
    loadingHistoryRef.current = true
    setLoadingHistory(true)
    try {
      const first = entries[0]
      const older = await gameLogService.loadBefore(logState.playerId, {
        timestampMs: first.timestampMs,
        sequence: first.sequence,
      })
      if (older.length === 0) {
        setReachedStart(true)
      } else {
        setHistory(older)
        historyReceivedCount.current = logState.receivedCount
        scrollToBottom.current = true
      }
    } finally {
      loadingHistoryRef.current = false
      setLoadingHistory(false)
    }
  }, [logState.playerId, logState.receivedCount, reachedStart])

  return (
    <section className={`panel log-panel ${className}`.trim()}>
      <div className="log-tabs" role="tablist" aria-label={t('combat.log.messagesAria')}>
        <button className={tab === 'log' ? 'active' : ''} onClick={() => setTab('log')} role="tab" type="button">{t('combat.log.tabLog')}</button>
        <button className={tab === 'chat' ? 'active' : ''} onClick={() => setTab('chat')} role="tab" type="button">{t('combat.log.tabChat')}</button>
        <span
          aria-label={logState.persistence === 'memory' ? t('combat.log.memoryOnly') : t('combat.log.saved')}
          className={`log-storage-status log-storage-status--${logState.persistence}`}
          title={logState.persistence === 'memory' ? t('combat.log.memoryOnlyTitle') : t('combat.log.saved')}
        />
        {history !== null && (
          <button className="log-latest-button" onClick={showLatest} type="button">
            {unseenCount > 0 ? t('combat.log.latestWithCount', { n: formatNumber(unseenCount) }) : t('combat.log.backToLatest')}
          </button>
        )}
      </div>
      {tab === 'log' ? (
        <ol
          className="event-log"
          onScroll={event => {
            const list = event.currentTarget
            const awayFromBottom = list.scrollHeight - list.scrollTop - list.clientHeight >= 32
            if (awayFromBottom && history === null) {
              setHistory(logState.latest)
              historyReceivedCount.current = logState.receivedCount
            }
            if (list.scrollTop <= 24) void loadOlder(history ?? logState.latest)
          }}
          ref={listRef}
        >
          {loadingHistory && <li className="log-state-row">{t('combat.log.loadingOlder')}</li>}
          {reachedStart && <li className="log-state-row">{t('combat.log.reachedStart')}</li>}
          {visibleEntries.map(entry => <LogRow entry={entry} key={entry.id} />)}
        </ol>
      ) : <div className="chat-placeholder">{t('combat.log.chatUnavailable')}</div>}
    </section>
  )
}
