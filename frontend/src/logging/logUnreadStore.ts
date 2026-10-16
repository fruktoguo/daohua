import { create } from 'zustand'
import type { LogEvent } from '../game/types'
import type { ChatMessageDto } from '../network/protocol'

/**
 * 日志面板分组标识。
 * 综合 = 系统日志 + 好友私聊 + 队伍群聊；世界/附近/好友/宗门/队伍为传音频道；系统/战斗为服务端日志分组。
 */
export type LogGroupId = 'all' | 'world' | 'nearby' | 'system' | 'combat' | 'friend' | 'sect' | 'party'

interface LogUnreadState {
  /** 当前选中的 Tab 分组 */
  activeGroup: LogGroupId
  /** 各分组未读计数（combat 战斗恒为 0） */
  unreadCounts: Record<LogGroupId, number>
  /** 切换当前选中的 Tab 并自动清零该分组未读 */
  setActiveGroup: (group: LogGroupId) => void
  /** 收到新消息时增加未读（combat 恒不增加） */
  incrementUnread: (group: LogGroupId, delta?: number) => void
  /** 清零指定分组未读 */
  clearUnread: (group: LogGroupId) => void
  /** 清空所有未读（登出或切换角色时） */
  resetAll: () => void
}

const initialUnreadCounts: Record<LogGroupId, number> = {
  all: 0,
  world: 0,
  nearby: 0,
  system: 0,
  combat: 0,
  friend: 0,
  sect: 0,
  party: 0,
}

export const useLogUnreadStore = create<LogUnreadState>((set, get) => ({
  activeGroup: 'all',
  unreadCounts: { ...initialUnreadCounts },
  setActiveGroup: (group: LogGroupId) => {
    set(state => ({
      activeGroup: group,
      unreadCounts: {
        ...state.unreadCounts,
        [group]: 0,
      },
    }))
  },
  incrementUnread: (group: LogGroupId, delta = 1) => {
    if (group === 'combat') return // 战斗严格不计未读
    const state = get()
    if (state.activeGroup === group) return // 当前正处于该分组，不需要增加未读
    set(s => ({
      unreadCounts: {
        ...s.unreadCounts,
        [group]: (s.unreadCounts[group] ?? 0) + delta,
      },
    }))
  },
  clearUnread: (group: LogGroupId) => {
    set(state => ({
      unreadCounts: {
        ...state.unreadCounts,
        [group]: 0,
      },
    }))
  },
  resetAll: () => {
    set({
      unreadCounts: { ...initialUnreadCounts },
    })
  },
}))

/**
 * 收到实时传音消息时推进未读
 */
export function notifyIncomingChatMessage(dto: ChatMessageDto, currentUserId?: string | null): void {
  // 自己发送的消息不计未读
  if (currentUserId && dto.senderId === currentUserId) return
  const store = useLogUnreadStore.getState()
  const channel = dto.channel
  if (channel === 'world') {
    store.incrementUnread('world', 1)
  } else if (channel === 'nearby') {
    store.incrementUnread('nearby', 1)
  } else if (channel === 'party') {
    store.incrementUnread('party', 1)
    store.incrementUnread('all', 1)
  } else if (channel === 'friend') {
    store.incrementUnread('friend', 1)
    store.incrementUnread('all', 1)
  } else if (channel === 'sect') {
    store.incrementUnread('sect', 1)
  }
}

/**
 * 收到服务端推送日志事件批次时推进未读（仅 system，combat 严格排除）
 */
export function notifyIncomingLogEvents(events: readonly LogEvent[]): void {
  let systemCount = 0
  for (const event of events) {
    const channel = event.channel ?? 'system'
    if (channel === 'system') {
      systemCount += event.count ?? 1
    }
  }
  if (systemCount > 0) {
    const store = useLogUnreadStore.getState()
    store.incrementUnread('system', systemCount)
    store.incrementUnread('all', systemCount)
  }
}
