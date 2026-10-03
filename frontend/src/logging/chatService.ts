import type { ChatChannel, ChatHistoryDto, ChatMessageDto, PartyLinkDto } from '../network/protocol'

/** 单条传音消息的本地内存形态 */
export interface ChatMessage {
  /** 消息唯一标识（服务端 messageId） */
  id: string
  /** 频道标识 */
  channel: ChatChannel
  /** 频道作用域键 */
  scopeKey: string
  /** 发送者玩家 ID */
  senderId: string
  /** 发送者道号 */
  senderName: string
  /** 消息正文 */
  body: string
  /** 发送时刻（Unix 毫秒） */
  sentAtMs: number
  /** 分享时的队伍快照（队伍链接卡） */
  partyLink?: PartyLinkDto | null
}

/** 传音服务对外只读快照（配合 subscribe 的 revision 变化驱动 UI 刷新） */
export interface ChatSnapshot {
  /** 当前激活玩家 ID */
  playerId: string | null
  /** 快照版本号，每次写入递增 */
  revision: number
  /** 按会话键分组的消息，组内按时间正序 */
  conversations: ReadonlyMap<string, readonly ChatMessage[]>
  /** 单会话频道的当前作用域键（world/nearby/party），由最近一次历史回放确定 */
  channelScopes: ReadonlyMap<string, string>
  /** 好友私聊会话的作用域键（好友玩家 ID → scopeKey），由历史回放确定 */
  friendScopes: ReadonlyMap<string, string>
  /** 累计接收（含历史回放）消息条数 */
  receivedCount: number
}

/** 每个会话保留的消息条数（与服务端保留口径一致）。 */
const MESSAGES_PER_CONVERSATION = 100
/** 同时缓存的会话数上限：好友私聊会话可能很多，按最近活跃淘汰。 */
const MAX_CONVERSATIONS = 24

/** 会话键：频道 + 作用域。 */
export function conversationKey(channel: string, scopeKey: string): string {
  return `${channel}\u0000${scopeKey}`
}

function toMessage(dto: ChatMessageDto): ChatMessage {
  return {
    id: dto.messageId,
    channel: dto.channel,
    scopeKey: dto.scopeKey,
    senderId: dto.senderId,
    senderName: dto.senderName,
    body: dto.body,
    sentAtMs: dto.sentAtUnixMilliseconds,
    partyLink: dto.partyLink ?? null,
  }
}

function compareMessage(left: ChatMessage, right: ChatMessage): number {
  return left.sentAtMs - right.sentAtMs || left.id.localeCompare(right.id)
}

/**
 * 传音消息全局单例服务。
 * 纯内存维护各频道会话的最近消息（每会话 100 条，最多 24 个会话），
 * 权威历史仍以服务端 PostgreSQL（每作用域 100 条）为准，切换频道或重连时通过历史回放重新对齐。
 */
class ChatService {
  private readonly listeners = new Set<() => void>()
  private snapshot: ChatSnapshot = {
    playerId: null, revision: 0, conversations: new Map(),
    channelScopes: new Map(), friendScopes: new Map(), receivedCount: 0,
  }
  private conversations = new Map<string, ChatMessage[]>()
  private channelScopes = new Map<string, string>()
  private friendScopes = new Map<string, string>()
  private seen = new Set<string>()

  /** 订阅快照变更；返回取消订阅函数 */
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  /** 读取当前快照（不可变引用，配合 revision 判断变更） */
  readonly getSnapshot = (): ChatSnapshot => this.snapshot

  /** 切换激活玩家：清空全部会话；同一玩家重复调用为空操作 */
  activate(playerId: string): void {
    if (this.snapshot.playerId === playerId) return
    this.clear(playerId)
  }

  /** 清空全部传音缓存（登出） */
  reset(): void {
    if (this.snapshot.playerId === null && this.conversations.size === 0) return
    this.clear(null)
  }

  /** 追加一条实时广播消息 */
  appendMessage(dto: ChatMessageDto): void {
    const message = toMessage(dto)
    if (this.seen.has(message.id)) return
    this.seen.add(message.id)
    this.append(message, 1)
  }

  /** 应用服务端历史回放：整段替换该会话缓存（服务端是权威口径），并登记作用域键。 */
  applyHistory(dto: ChatHistoryDto): void {
    if (!Array.isArray(dto?.messages)) return
    const key = conversationKey(dto.channel, dto.scopeKey)
    const messages = dto.messages.map(toMessage).sort(compareMessage).slice(-MESSAGES_PER_CONVERSATION)
    for (const message of messages) this.seen.add(message.id)
    this.conversations.set(key, messages)
    if (dto.channel === 'friend' && dto.targetPlayerId) this.friendScopes.set(dto.targetPlayerId, dto.scopeKey)
    else this.channelScopes.set(dto.channel, dto.scopeKey)
    this.trimConversations()
    this.publish(messages.length)
  }

  private clear(playerId: string | null): void {
    this.conversations = new Map()
    this.channelScopes = new Map()
    this.friendScopes = new Map()
    this.seen.clear()
    this.snapshot = {
      playerId,
      revision: this.snapshot.revision + 1,
      conversations: new Map(),
      channelScopes: new Map(),
      friendScopes: new Map(),
      receivedCount: 0,
    }
    this.notify()
  }

  private append(message: ChatMessage, received: number): void {
    const key = conversationKey(message.channel, message.scopeKey)
    const list = this.conversations.get(key)
    if (list === undefined) {
      this.conversations.set(key, [message])
    } else {
      const last = list[list.length - 1]
      if (last && compareMessage(last, message) > 0) {
        // 实时帧偶尔乱序：按时间插回正确位置，保持组内正序。
        const index = list.findIndex(entry => compareMessage(entry, message) > 0)
        list.splice(index < 0 ? list.length : index, 0, message)
      } else {
        list.push(message)
      }
      if (list.length > MESSAGES_PER_CONVERSATION) list.splice(0, list.length - MESSAGES_PER_CONVERSATION)
    }
    this.trimConversations()
    this.publish(received)
  }

  /** 会话数超上限时淘汰最近消息最旧的会话。 */
  private trimConversations(): void {
    if (this.conversations.size <= MAX_CONVERSATIONS) return
    let oldestKey: string | null = null
    let oldestTime = Number.POSITIVE_INFINITY
    for (const [key, messages] of this.conversations) {
      const latest = messages[messages.length - 1]?.sentAtMs ?? 0
      if (latest < oldestTime) {
        oldestTime = latest
        oldestKey = key
      }
    }
    if (oldestKey !== null) this.conversations.delete(oldestKey)
  }

  private publish(received: number): void {
    const snapshot: ChatSnapshot = {
      playerId: this.snapshot.playerId,
      revision: this.snapshot.revision + 1,
      conversations: new Map(this.conversations),
      channelScopes: new Map(this.channelScopes),
      friendScopes: new Map(this.friendScopes),
      receivedCount: this.snapshot.receivedCount + received,
    }
    this.snapshot = snapshot
    this.notify()
  }

  private notify(): void {
    for (const listener of this.listeners) listener()
  }
}

/** 全局传音服务单例（供 gameClient 写入、日志面板订阅消费） */
export const chatService = new ChatService()

/** 收集某频道的全部缓存消息（时间正序）。 */
function collectChannel(snapshot: ChatSnapshot, channel: string): ChatMessage[] {
  const result: ChatMessage[] = []
  for (const messages of snapshot.conversations.values()) {
    for (const message of messages) {
      if (message.channel === channel) result.push(message)
    }
  }
  return result.sort(compareMessage)
}

/**
 * 读取单会话频道（世界/附近/队伍）的消息。
 * 作用域键由最近一次历史回放确定：换节点或换队伍后旧作用域的消息不会再混入当前视图。
 */
export function selectChannelMessages(snapshot: ChatSnapshot, channel: string): readonly ChatMessage[] {
  const messages = collectChannel(snapshot, channel)
  if (!snapshot.channelScopes.has(channel)) return messages
  const scopeKey = snapshot.channelScopes.get(channel)
  return messages.filter(message => message.scopeKey === scopeKey)
}

/** 读取与指定好友的私聊消息；尚未拿到作用域键时退化为该频道全部消息。 */
export function selectFriendMessages(snapshot: ChatSnapshot, friendId: string): readonly ChatMessage[] {
  const messages = collectChannel(snapshot, 'friend')
  const scopeKey = snapshot.friendScopes.get(friendId)
  return scopeKey === undefined ? messages : messages.filter(message => message.scopeKey === scopeKey)
}

/** 读取「综合」分组的传音部分：好友私聊 + 队伍群聊，按时间正序合并。 */
export function selectAggregateMessages(snapshot: ChatSnapshot): readonly ChatMessage[] {
  return [...collectChannel(snapshot, 'friend'), ...selectChannelMessages(snapshot, 'party')].sort(compareMessage)
}
