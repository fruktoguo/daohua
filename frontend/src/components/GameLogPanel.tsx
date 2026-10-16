import { memo, useMemo, useSyncExternalStore } from 'react'
import { useT } from '../i18n'
import { filterLogEntries, gameLogService, type LogChannel } from '../logging/gameLogService'
import { useLogUnreadStore, type LogGroupId } from '../logging/logUnreadStore'
import { AggregatePanel, ChatChannelPanel, FriendChannelPanel, PartyChannelPanel } from './ChatPanel'
import { FeedList, toLogFeedItems, useStickyScroll } from './LogFeed'

/**
 * 日志面板分组标识。
 * 综合 = 系统日志 + 好友私聊 + 队伍群聊；世界/附近/好友/宗门/队伍为传音频道；系统/战斗为服务端日志分组。
 */
/** 分组顺序固定，与设计真源 docs/design/总纲/14-传音频道与日志分组.md 一致 */
const LOG_GROUPS: readonly { id: LogGroupId; labelKey: string }[] = [
  { id: 'all', labelKey: 'combat.chat.tabAll' },
  { id: 'world', labelKey: 'combat.chat.tabWorld' },
  { id: 'nearby', labelKey: 'combat.chat.tabNearby' },
  { id: 'system', labelKey: 'combat.chat.tabSystem' },
  { id: 'combat', labelKey: 'combat.chat.tabCombat' },
  { id: 'friend', labelKey: 'combat.chat.tabFriend' },
  { id: 'sect', labelKey: 'combat.chat.tabSect' },
  { id: 'party', labelKey: 'combat.chat.tabParty' },
]

/**
 * 服务端日志分组面板 (LogGroupPanel)
 *
 * 只消费 gameLogService 中对应频道的日志事件；综合分组不在此渲染（走 AggregatePanel）。
 */
const LogGroupPanel = memo(function LogGroupPanel({ channel }: { channel: LogChannel }): React.JSX.Element {
  const t = useT()
  const logState = useSyncExternalStore(gameLogService.subscribe, gameLogService.getSnapshot)
  const items = useMemo(() => toLogFeedItems(filterLogEntries(logState.latest, channel)), [logState, channel])
  const { listRef, onScroll } = useStickyScroll(logState.revision)

  return (
    <ol className="event-log" onScroll={onScroll} ref={listRef}>
      {items.length === 0 ? <li className="log-state-row">{t('combat.chat.empty')}</li> : <FeedList items={items} />}
    </ol>
  )
})

/**
 * 游戏战报与传音日志主面板组件 (GameLogPanel)
 *
 * 核心机制与交互：
 * 1. 8 个分组 Tab：综合 / 世界 / 附近 / 系统 / 战斗 / 好友 / 宗门 / 队伍；
 * 2. 每个分组独立订阅各自数据源（服务端日志事件或传音消息），各自保持贴底滚动；
 * 3. 仅展示当前会话内存缓冲，不提供历史网络查询或本地持久化。
 */
export function GameLogPanel({ className = '' }: { className?: string }): React.JSX.Element {
  const t = useT()
  const group = useLogUnreadStore(state => state.activeGroup)
  const setActiveGroup = useLogUnreadStore(state => state.setActiveGroup)
  const unreadCounts = useLogUnreadStore(state => state.unreadCounts)

  return (
    <section className={`panel log-panel ${className}`.trim()}>
      <div className="log-tabs" role="tablist" aria-label={t('combat.log.messagesAria')}>
        {LOG_GROUPS.map(entry => {
          const isActive = group === entry.id
          const unread = entry.id === 'combat' ? 0 : (unreadCounts[entry.id] ?? 0)
          return (
            <button
              aria-selected={isActive}
              className={`log-tab-btn ${isActive ? 'active' : ''}`}
              key={entry.id}
              onClick={() => setActiveGroup(entry.id)}
              role="tab"
              type="button"
            >
              <span>{t(entry.labelKey)}</span>
              {!isActive && unread > 0 && (
                <span className="log-tab-badge" title={`${unread}`}>
                  {unread > 99 ? '99+' : unread}
                </span>
              )}
            </button>
          )
        })}
      </div>
      {group === 'all' && <AggregatePanel />}
      {group === 'world' && <ChatChannelPanel channel="world" />}
      {group === 'nearby' && <ChatChannelPanel channel="nearby" />}
      {group === 'system' && <LogGroupPanel channel="system" />}
      {group === 'combat' && <LogGroupPanel channel="combat" />}
      {group === 'friend' && <FriendChannelPanel />}
      {group === 'sect' && <div className="chat-placeholder">{t('combat.chat.sectUnavailable')}</div>}
      {group === 'party' && <PartyChannelPanel />}
    </section>
  )
}
