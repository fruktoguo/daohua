import { memo, useMemo, useState, useSyncExternalStore } from 'react'
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
 * 3. 仅展示当前会话内存缓冲，不提供历史网络查询或本地持久化；
 * 4. 桌面端右下角提供扩展/收起按钮，展开时面板向上长开占满中列并覆盖主内容区
 *    （过渡由 .center-column 的 flex 插值承担，见 styles.css）；
 *    竖屏日志本身就是全屏 Tab，expandable=false 时不渲染该按钮。
 */
export function GameLogPanel({ className = '', expandable = true }: { className?: string; expandable?: boolean }): React.JSX.Element {
  const t = useT()
  const group = useLogUnreadStore(state => state.activeGroup)
  const setActiveGroup = useLogUnreadStore(state => state.setActiveGroup)
  const unreadCounts = useLogUnreadStore(state => state.unreadCounts)
  const [expanded, setExpanded] = useState(false)

  return (
    <section className={`panel log-panel ${expanded ? 'is-expanded' : ''} ${className}`.trim()}>
      <div className="log-head">
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
                <span className="log-tab-label">{t(entry.labelKey)}</span>
                {!isActive && unread > 0 && (
                  <span className="log-tab-badge" title={`${unread}`}>
                    {unread > 99 ? '99+' : unread}
                  </span>
                )}
              </button>
            )
          })}
        </div>
        {expandable && (
          <button
            aria-expanded={expanded}
            aria-label={t(expanded ? 'combat.chat.collapsePanel' : 'combat.chat.expandPanel')}
            className={`log-expand-btn ${expanded ? 'is-expanded' : ''}`}
            onClick={() => setExpanded(prev => !prev)}
            title={t(expanded ? 'combat.chat.collapsePanel' : 'combat.chat.expandPanel')}
            type="button"
          >
            {expanded ? (
              /* 收起符号：双箭头向内收拢 */
              <svg aria-hidden="true" fill="none" viewBox="0 0 16 16">
                <path
                  d="M2.7 9.3h4v4M13.3 6.7h-4v-4M9.3 6.7l4.7-4.7M2 14l4.7-4.7"
                  stroke="currentColor"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth="1.5"
                />
              </svg>
            ) : (
              /* 展开符号：双箭头向外扩张 */
              <svg aria-hidden="true" fill="none" viewBox="0 0 16 16">
                <path
                  d="M10 2h4v4M6 14H2v-4M14 2L9.3 6.7M2 14l4.7-4.7"
                  stroke="currentColor"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth="1.5"
                />
              </svg>
            )}
          </button>
        )}
      </div>
      {group === 'all' && <AggregatePanel />}
      {group === 'world' && <ChatChannelPanel channel="world" />}
      {group === 'nearby' && <ChatChannelPanel channel="nearby" />}
      {group === 'system' && <LogGroupPanel channel="system" />}
      {group === 'combat' && <LogGroupPanel channel="combat" />}
      {group === 'friend' && <FriendChannelPanel />}
      {group === 'sect' && <ChatChannelPanel channel="sect" />}
      {group === 'party' && <PartyChannelPanel />}
    </section>
  )
}
