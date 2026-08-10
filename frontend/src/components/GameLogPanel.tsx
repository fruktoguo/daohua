import { memo, useCallback, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react'
import { ItemReference } from './ItemDisplay'
import { gameLogService, type GameLogEntry, type LogSegment } from '../logging/gameLogService'
import { ValueText } from './ValueText'

function formatLogTime(timestampMs: number): string {
  const date = new Date(timestampMs)
  const part = (value: number) => String(value).padStart(2, '0')
  return `${part(date.getMonth() + 1)}-${part(date.getDate())} ${part(date.getHours())}:${part(date.getMinutes())}:${part(date.getSeconds())}`
}

function LogContent({ segment }: { segment: LogSegment }): React.JSX.Element {
  if (segment.type === 'text') return <ValueText className="log-value-capsule" text={segment.value} />
  if (segment.kind === 'item') {
    return <ItemReference className="log-reference log-reference--item" itemId={segment.id} label={segment.label} />
  }
  return (
    <span className={`log-reference log-reference--${segment.kind}`} title={`${segment.kind}: ${segment.id}`}>
      {segment.label}
    </span>
  )
}

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

export function GameLogPanel({ className = '' }: { className?: string }): React.JSX.Element {
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
      <div className="log-tabs" role="tablist" aria-label="消息">
        <button className={tab === 'log' ? 'active' : ''} onClick={() => setTab('log')} role="tab" type="button">日志</button>
        <button className={tab === 'chat' ? 'active' : ''} onClick={() => setTab('chat')} role="tab" type="button">聊天</button>
        <span
          aria-label={logState.persistence === 'memory' ? '日志仅保留在内存' : '日志已在本地保存'}
          className={`log-storage-status log-storage-status--${logState.persistence}`}
          title={logState.persistence === 'memory' ? '本地存储不可用，当前仅保留最近 100 条' : '日志已在本地保存'}
        />
        {history !== null && (
          <button className="log-latest-button" onClick={showLatest} type="button">
            {unseenCount > 0 ? `最新 +${unseenCount}` : '回到最新'}
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
          {loadingHistory && <li className="log-state-row">正在读取更早记录…</li>}
          {reachedStart && <li className="log-state-row">已到最早记录</li>}
          {visibleEntries.map(entry => <LogRow entry={entry} key={entry.id} />)}
        </ol>
      ) : <div className="chat-placeholder">聊天频道尚未开放</div>}
    </section>
  )
}
