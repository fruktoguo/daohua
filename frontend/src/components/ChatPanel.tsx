import { memo, useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { useT } from '../i18n'
import {
  chatService,
  selectAggregateMessages,
  selectChannelMessages,
  selectFriendMessages,
} from '../logging/chatService'
import { filterLogEntries, gameLogService } from '../logging/gameLogService'
import type { ChatChannel } from '../network/protocol'
import { gameClient } from '../network/gameClient'
import { FeedList, mergeFeedItems, toChatFeedItems, toLogFeedItems, useStickyScroll } from './LogFeed'

/** 单条消息正文长度上限（与服务端 ChatRuntime.MaxBodyLength 一致） */
const MAX_CHAT_BODY_LENGTH = 200

/**
 * 传音输入框组件 (ChatComposer)
 *
 * 独立持有草稿状态：聊天列表的高频刷新不会重建输入框，输入焦点与选区保持稳定。
 */
const ChatComposer = memo(function ChatComposer({
  channel,
  targetPlayerId,
  hint,
}: {
  channel: ChatChannel
  /** 好友频道的私聊对象 */
  targetPlayerId?: string | null
  /** 频道不可用时的提示文案；提供时输入框整体禁用 */
  hint?: string
}): React.JSX.Element {
  const t = useT()
  const [draft, setDraft] = useState('')
  const disabled = hint !== undefined

  const submit = (): void => {
    const body = draft.trim()
    if (body.length === 0) return
    if (gameClient.sendChat(channel, body, targetPlayerId ?? undefined)) setDraft('')
  }

  if (disabled) return <p className="chat-composer-hint">{hint}</p>

  return (
    <form
      className="chat-composer"
      onSubmit={event => {
        event.preventDefault()
        submit()
      }}
    >
      <input
        aria-label={t('combat.chat.inputAria')}
        maxLength={MAX_CHAT_BODY_LENGTH}
        onChange={event => setDraft(event.target.value)}
        placeholder={t('combat.chat.placeholder')}
        value={draft}
      />
      <button disabled={draft.trim().length === 0} type="submit">{t('combat.chat.send')}</button>
    </form>
  )
})

/**
 * 传音频道面板 (ChatChannelPanel)
 *
 * 世界 / 附近 / 队伍三个「单会话」频道共用同一模板：历史回放 + 实时消息 + 输入框。
 */
export function ChatChannelPanel({ channel }: { channel: ChatChannel }): React.JSX.Element {
  const t = useT()
  const nodeId = useGameStore(state => state.world.nodeId)
  const partyId = useSocialStore(state => state.party?.partyId ?? null)
  const chatState = useSyncExternalStore(chatService.subscribe, chatService.getSnapshot)
  const messages = useMemo(() => selectChannelMessages(chatState, channel), [chatState, channel])
  const items = useMemo(() => toChatFeedItems(messages), [messages])
  const { listRef, onScroll } = useStickyScroll(chatState.revision)

  useEffect(() => {
    gameClient.requestChatHistory(channel)
  }, [channel, nodeId, partyId])

  const hint = channel === 'nearby' && !nodeId
    ? t('combat.chat.nearbyUnavailable')
    : channel === 'party' && !partyId
      ? t('combat.chat.partyUnavailable')
      : undefined

  return (
    <>
      <ol className="event-log" onScroll={onScroll} ref={listRef}>
        {items.length === 0 ? <li className="log-state-row">{t('combat.chat.empty')}</li> : <FeedList items={items} />}
      </ol>
      <ChatComposer channel={channel} hint={hint} />
    </>
  )
}

/**
 * 好友私聊频道面板 (FriendChannelPanel)
 *
 * 左列为好友列表与好友申请处理，右列为与当前选中好友的私聊记录与输入框。
 */
export function FriendChannelPanel(): React.JSX.Element {
  const t = useT()
  const friends = useSocialStore(state => state.friends)
  const incoming = useSocialStore(state => state.incomingRequests)
  const outgoing = useSocialStore(state => state.outgoingRequests)
  const activeFriendId = useSocialStore(state => state.activeFriendId)
  const setActiveFriend = useSocialStore(state => state.setActiveFriend)
  const chatState = useSyncExternalStore(chatService.subscribe, chatService.getSnapshot)
  const [searchName, setSearchName] = useState('')

  const activeFriend = friends.find(friend => friend.playerId === activeFriendId) ?? null
  const messages = useMemo(
    () => (activeFriendId === null ? [] : selectFriendMessages(chatState, activeFriendId)),
    [chatState, activeFriendId],
  )
  const items = useMemo(() => toChatFeedItems(messages), [messages])
  const { listRef, onScroll } = useStickyScroll(chatState.revision)

  useEffect(() => {
    if (activeFriendId !== null) gameClient.requestChatHistory('friend', activeFriendId)
  }, [activeFriendId])

  const submitRequest = (): void => {
    const name = searchName.trim()
    if (name.length === 0) return
    if (gameClient.sendSocialAction('friend.request', undefined, name)) setSearchName('')
  }

  return (
    <div className="chat-friend-layout">
      <aside className="chat-friend-side">
        <section className="chat-side-section">
          <h4>{t('combat.chat.addFriendHeading')}</h4>
          <form
            className="chat-composer chat-composer--compact"
            onSubmit={event => {
              event.preventDefault()
              submitRequest()
            }}
          >
            <input
              aria-label={t('combat.chat.addFriendAria')}
              maxLength={64}
              onChange={event => setSearchName(event.target.value)}
              placeholder={t('combat.chat.addFriendPlaceholder')}
              value={searchName}
            />
            <button disabled={searchName.trim().length === 0} type="submit">{t('combat.chat.addFriendAction')}</button>
          </form>
        </section>

        {incoming.length > 0 && (
          <section className="chat-side-section">
            <h4>{t('combat.chat.incomingHeading')}</h4>
            <ul className="chat-entry-list">
              {incoming.map(request => (
                <li key={request.requestId}>
                  <span className="chat-entry-name">{request.displayName}</span>
                  <span className="chat-entry-actions">
                    <button onClick={() => gameClient.sendSocialAction('friend.accept', request.playerId)} type="button">
                      {t('combat.chat.accept')}
                    </button>
                    <button onClick={() => gameClient.sendSocialAction('friend.reject', request.playerId)} type="button">
                      {t('combat.chat.reject')}
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          </section>
        )}

        {outgoing.length > 0 && (
          <section className="chat-side-section">
            <h4>{t('combat.chat.outgoingHeading')}</h4>
            <ul className="chat-entry-list">
              {outgoing.map(request => (
                <li key={request.requestId}>
                  <span className="chat-entry-name">{t('combat.chat.outgoingPending', { name: request.displayName })}</span>
                </li>
              ))}
            </ul>
          </section>
        )}

        <section className="chat-side-section">
          <h4>{t('combat.chat.friendsHeading')}</h4>
          <ul className="chat-entry-list">
            {friends.map(friend => (
              <li className={friend.playerId === activeFriendId ? 'is-active' : ''} key={friend.playerId}>
                <button
                  className="chat-entry-select"
                  onClick={() => setActiveFriend(friend.playerId)}
                  type="button"
                >
                  <span className="chat-entry-name">{friend.displayName}</span>
                  <span className={`chat-presence chat-presence--${friend.online ? 'online' : 'offline'}`}>
                    {friend.online ? t('combat.chat.online') : t('combat.chat.offline')}
                  </span>
                </button>
                <span className="chat-entry-actions">
                  <button onClick={() => gameClient.sendSocialAction('friend.remove', friend.playerId)} type="button">
                    {t('combat.chat.removeFriend')}
                  </button>
                </span>
              </li>
            ))}
            {friends.length === 0 && <li className="chat-entry-empty">{t('combat.chat.noFriends')}</li>}
          </ul>
        </section>
      </aside>

      <section className="chat-friend-main">
        <ol className="event-log" onScroll={onScroll} ref={listRef}>
          {activeFriend === null
            ? <li className="log-state-row">{t('combat.chat.selectFriend')}</li>
            : items.length === 0
              ? <li className="log-state-row">{t('combat.chat.empty')}</li>
              : <FeedList items={items} />}
        </ol>
        <ChatComposer
          channel="friend"
          hint={activeFriend === null ? t('combat.chat.selectFriend') : undefined}
          targetPlayerId={activeFriendId}
        />
      </section>
    </div>
  )
}

/**
 * 队伍频道面板 (PartyChannelPanel)
 *
 * 展示队伍成员与队伍群聊；未组队时给出通过好友邀请组队的入口。
 */
export function PartyChannelPanel(): React.JSX.Element {
  const t = useT()
  const party = useSocialStore(state => state.party)
  const invitation = useSocialStore(state => state.partyInvitation)
  const friends = useSocialStore(state => state.friends)
  const chatState = useSyncExternalStore(chatService.subscribe, chatService.getSnapshot)
  const messages = useMemo(() => selectChannelMessages(chatState, 'party'), [chatState])
  const items = useMemo(() => toChatFeedItems(messages), [messages])
  const { listRef, onScroll } = useStickyScroll(chatState.revision)

  useEffect(() => {
    if (party) gameClient.requestChatHistory('party')
  }, [party])

  const partyMemberIds = new Set(party?.members.map(member => member.playerId) ?? [])
  const invitableFriends = friends.filter(friend => !partyMemberIds.has(friend.playerId))

  return (
    <div className="chat-party-layout">
      {invitation && (
        <div className="chat-invitation">
          <span>{t('combat.chat.partyInvitedBy', { name: invitation.inviterName })}</span>
          <span className="chat-entry-actions">
            <button onClick={() => gameClient.sendSocialAction('party.accept')} type="button">{t('combat.chat.accept')}</button>
            <button onClick={() => gameClient.sendSocialAction('party.decline')} type="button">{t('combat.chat.reject')}</button>
          </span>
        </div>
      )}

      <aside className="chat-party-side">
        <section className="chat-side-section">
          <h4>{t('combat.chat.partyMembersHeading')}</h4>
          <ul className="chat-entry-list">
            {(party?.members ?? []).map(member => (
              <li key={member.playerId}>
                <span className="chat-entry-name">
                  {member.displayName}
                  {party && member.playerId === party.leaderId && <em className="chat-leader-mark">{t('combat.chat.partyLeader')}</em>}
                </span>
                <span className={`chat-presence chat-presence--${member.online ? 'online' : 'offline'}`}>
                  {member.online ? t('combat.chat.online') : t('combat.chat.offline')}
                </span>
              </li>
            ))}
            {party === null && <li className="chat-entry-empty">{t('combat.chat.partyEmpty')}</li>}
          </ul>
        </section>

        {party === null && friends.length > 0 && (
          <section className="chat-side-section">
            <h4>{t('combat.chat.partyInviteHeading')}</h4>
            <ul className="chat-entry-list">
              {invitableFriends.map(friend => (
                <li key={friend.playerId}>
                  <span className="chat-entry-name">{friend.displayName}</span>
                  <span className="chat-entry-actions">
                    <button onClick={() => gameClient.sendSocialAction('party.invite', friend.playerId)} type="button">
                      {t('combat.chat.partyInvite')}
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          </section>
        )}

        {party !== null && (
          <section className="chat-side-section">
            <button
              className="chat-leave-button"
              onClick={() => gameClient.sendSocialAction('party.leave')}
              type="button"
            >{t('combat.chat.partyLeave')}</button>
          </section>
        )}
      </aside>

      <section className="chat-party-main">
        <ol className="event-log" onScroll={onScroll} ref={listRef}>
          {party === null
            ? <li className="log-state-row">{t('combat.chat.partyUnavailable')}</li>
            : items.length === 0
              ? <li className="log-state-row">{t('combat.chat.empty')}</li>
              : <FeedList items={items} />}
        </ol>
        <ChatComposer channel="party" hint={party === null ? t('combat.chat.partyUnavailable') : undefined} />
      </section>
    </div>
  )
}

/**
 * 综合分组面板 (AggregatePanel)
 *
 * 只读合并三源：系统日志 + 好友私聊 + 队伍群聊，按时间正序统一渲染。
 */
export function AggregatePanel(): React.JSX.Element {
  const t = useT()
  const logState = useSyncExternalStore(gameLogService.subscribe, gameLogService.getSnapshot)
  const chatState = useSyncExternalStore(chatService.subscribe, chatService.getSnapshot)

  const items = useMemo(() => {
    const systemLogs = filterLogEntries(logState.latest, 'system')
    return mergeFeedItems(toLogFeedItems(systemLogs), toChatFeedItems(selectAggregateMessages(chatState)))
  }, [logState.revision, chatState.revision])

  const { listRef, onScroll } = useStickyScroll(logState.revision + chatState.revision)

  return (
    <ol className="event-log" onScroll={onScroll} ref={listRef}>
      {items.length === 0 ? <li className="log-state-row">{t('combat.chat.empty')}</li> : <FeedList items={items} />}
    </ol>
  )
}
