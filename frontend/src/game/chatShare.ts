import { useGameStore } from './gameStore'
import { useSocialStore } from './socialStore'
import { useZongmenStore } from './zongmenStore'
import { getT } from '../i18n'
import { conversationKey, friendScopeKey, type ChatMessage } from '../logging/chatService'
import { useChatDraftStore } from '../logging/chatDraftStore'
import { useLogUnreadStore, type LogGroupId } from '../logging/logUnreadStore'
import type { ChatChannel } from '../network/protocol'
import { useUiStore } from '../uiStore'

export const MOBILE_LAYOUT_QUERY = '(max-width: 960px)'

function resolveDestination(group: LogGroupId, friendId = useSocialStore.getState().activeFriendId): { channel: ChatChannel; scope: string; targetPlayerId?: string } | null {
  switch (group) {
    case 'world': return { channel: group, scope: '' }
    case 'nearby': {
      const scope = useGameStore.getState().world.nodeId
      return scope ? { channel: group, scope } : null
    }
    case 'sect': {
      const scope = useZongmenStore.getState().zongmen?.zongmenId
      return scope ? { channel: group, scope } : null
    }
    case 'party': {
      const scope = useSocialStore.getState().party?.partyId
      return scope ? { channel: group, scope } : null
    }
    case 'friend': {
      const playerId = useGameStore.getState().playerId
      if (!playerId || !friendId || !useSocialStore.getState().friends.some(friend => friend.playerId === friendId)) return null
      return { channel: group, scope: friendScopeKey(playerId, friendId), targetPlayerId: friendId }
    }
    default: return null
  }
}

/** 从原会话反查私聊对象，不使用当前活动好友，以免综合页复读或回复串线。 */
export function resolveMessageDestination(message: ChatMessage) {
  const playerId = useGameStore.getState().playerId
  const friendId = message.channel !== 'friend' || !playerId ? undefined
    : message.scopeKey.split(':').find(id => id !== playerId.toLowerCase())
  const destination = resolveDestination(message.channel, friendId)
  return destination?.scope === message.scopeKey ? destination : null
}

function openComposer(channel: ChatChannel, key: string): void {
  useLogUnreadStore.getState().setActiveGroup(channel)
  if (window.matchMedia(MOBILE_LAYOUT_QUERY).matches) useUiStore.getState().setActiveTab('log')
  useChatDraftStore.getState().requestFocus(key)
}

/** 先写草稿再切页；两者都不依赖输入框是否已挂载。分享只填入，不发送。 */
export function shareToChat(markup: string): void {
  const currentGroup = useLogUnreadStore.getState().activeGroup
  const destination = resolveDestination(currentGroup) ?? { channel: 'world' as const, scope: '' }
  const key = conversationKey(destination.channel, destination.scope)
  const inserted = useChatDraftStore.getState().append(key, markup)
  openComposer(destination.channel, key)
  useGameStore.getState().setNotice(getT()(inserted ? 'combat.chat.linkInsertedNotice' : 'combat.chat.tooLong'))
}

export function quoteInChat(message: ChatMessage): void {
  const destination = resolveMessageDestination(message)
  if (!destination) {
    useGameStore.getState().setNotice(getT()('combat.chat.messageUnavailable'))
    return
  }
  const key = conversationKey(destination.channel, destination.scope)
  useChatDraftStore.getState().edit(key, draft => ({
    ...draft,
    quote: { messageId: message.id, senderName: message.senderName, body: message.body },
  }))
  if (destination.targetPlayerId) useSocialStore.getState().setActiveFriend(destination.targetPlayerId)
  openComposer(destination.channel, key)
}
