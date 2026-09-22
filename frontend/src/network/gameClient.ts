import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { getT } from '../i18n'
import { chatService } from '../logging/chatService'
import { gameLogService } from '../logging/gameLogService'
import { clearStoredAccount } from './account'
import {
  createRequestId,
  decodeEnvelope,
  decodeJsonPayload,
  encodeJsonEnvelope,
  type ChatChannel,
  type ChatHistoryDto,
  type ChatHistoryRequestDto,
  type ChatMessageDto,
  type ChatSendDto,
  type CommandResultDto,
  type ErrorDto,
  type HelloResponse,
  type LogBatchDto,
  type PlayerSnapshotDeltaDto,
  type PlayerSnapshotDto,
  type SocialAction,
  type SocialActionDto,
  type SocialStateDto,
  WireMessageType,
} from './protocol'
import { decodePlayerSnapshotDeltaDto, decodePlayerSnapshotDto } from './snapshot'
import { getCatalogFingerprint } from './apiClient'
import { getSessionAccount } from './sessionState'
import {
  ANTI_MULTI_CLOSE_NOTICE_KEYS,
  applyAntiMultiOpenConfig,
  getHelloDeviceFingerprint,
  isTabBlocked,
  registerBlockedTeardown,
} from '../antiMulti'

const APP_VERSION = String(import.meta.env.VITE_APP_VERSION ?? 'dev')

/** 配表指纹不匹配事件：服务端拒绝 Hello 后派发，App 监听该事件重新拉取配表并放行重连。 */
export const GAME_DATA_MISMATCH_EVENT = 'daohua:game-data-mismatch'

/** 时间窗内连续解析失败达到该帧数时，触发一次快照重建（resync）。 */
const PARSE_FAILURE_RESYNC_THRESHOLD = 5
/** 时间窗内连续解析失败达到该帧数时，断开并交给指数退避重连。 */
const PARSE_FAILURE_FATAL_COUNT = 20
/** 解析失败计数的滑动时间窗长度。 */
const PARSE_FAILURE_WINDOW_MS = 30_000
/** 同类解析错误提示的节流间隔，避免坏帧连发刷屏。 */
const PARSE_FAILURE_NOTICE_THROTTLE_MS = 4_000

/** WebSocket 端点：默认同源 /ws（https 自动切 wss），仅外部后端场景用 VITE_WS_URL 覆盖 */
function getEndpoint(): string {
  const configured = import.meta.env.VITE_WS_URL as string | undefined
  if (configured) return configured
  return `${window.location.protocol === 'https:' ? 'wss' : 'ws'}://${window.location.host}/ws`
}

/**
 * 道化太初客户端核心网络管理器（基于 wire.v1 二进制信封）。
 *
 * 核心架构原则：
 * 1. 鉴权通过 HttpOnly 会话 Cookie 维护，URL 中绝不携带 token 或明文密码；
 * 2. 状态下发只接受全量 SnapshotDto 或增量 SnapshotDeltaDto；
 * 3. 具备指数退避自动重连（500ms -> 8000ms）、心跳探活（25s Ping）与解析异常自愈重建机制。
 */
export class GameClient {
  private socket: WebSocket | null = null
  private reconnectTimer: number | undefined
  private heartbeatTimer: number | undefined
  private disposed = false
  private reconnectDelay = 500
  private lastServerSequence = 0
  private outboundSequence = 0
  private parseFailureCount = 0
  private parseFailureWindowStart = 0
  private parseFailureResynced = false
  private lastParseErrorKey = ''
  private lastParseErrorAt = 0
  /** 配表指纹不匹配后挂起自动重连，等待 App 重新拉取配表（connect() 时清除）。 */
  private catalogRefreshPending = false
  private readonly pendingRequests = new Map<string, number>()
  private static readonly REQUEST_TIMEOUT_MS = 15_000

  /** 当前登录的角色 ID */
  playerId = getSessionAccount()?.playerId ?? ''
  /** 当前登录的账号名 */
  account = getSessionAccount()?.account ?? ''

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== undefined) {
      window.clearTimeout(this.reconnectTimer)
      this.reconnectTimer = undefined
    }
  }

  private startHeartbeat(): void {
    this.stopHeartbeat()
    this.heartbeatTimer = window.setInterval(() => {
      if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return
      try {
        this.socket.send(encodeJsonEnvelope(WireMessageType.Ping, createRequestId(), this.nextOutboundSequence(), { clientTime: Date.now() }))
      } catch {
        // 发送失败由 WebSocket close 事件统一处理。
      }
    }, 25_000)
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== undefined) {
      window.clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = undefined
    }
  }

  private scheduleReconnect(): void {
    if (this.disposed || this.reconnectTimer !== undefined) return
    useGameStore.getState().setConnection('reconnecting')
    const delay = this.reconnectDelay
    this.reconnectDelay = Math.min(8_000, this.reconnectDelay * 2)
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = undefined
      this.open()
    }, delay)
  }

  /** 建立/恢复游戏连接：重置退避与序列号后打开 socket；被互斥阻断或未登录时静默置为 offline */
  connect(): void {
    this.clearReconnectTimer()
    // 标签页被互斥守卫阻断时禁止任何连接尝试（含重试按钮触发的手动重连）。
    if (isTabBlocked()) {
      useGameStore.getState().setConnection('offline')
      return
    }
    const accountInfo = getSessionAccount()
    if (!accountInfo) {
      useGameStore.getState().setConnection('offline')
      return
    }
    if (this.socket && (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING)) return
    this.playerId = accountInfo.playerId
    this.account = accountInfo.account
    chatService.activate(this.playerId)
    this.disposed = false
    this.catalogRefreshPending = false
    this.reconnectDelay = 500
    this.outboundSequence = 0
    this.lastServerSequence = 0
    this.clearPendingRequests()
    this.open()
  }

  /** 登出：断开连接并清空本地账号态 */
  logout(): void {
    this.dispose()
    clearStoredAccount()
  }

  /** 彻底停用客户端：停心跳/重连/挂起请求并关闭 socket；dispose 后不再自动重连 */
  dispose(): void {
    this.disposed = true
    this.clearReconnectTimer()
    this.stopHeartbeat()
    this.clearPendingRequests()
    chatService.reset()
    useSocialStore.getState().reset()
    const socket = this.socket
    this.socket = null
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) socket.close(1000, '客户端断开')
    useGameStore.getState().setConnection('offline')
  }

  /** 触发快照重建：断开当前连接（走统一重连路径换取全量 Snapshot），未连接时直接重开 */
  requestSnapshotOrResync(): void {
    if (this.disposed) return
    const socket = this.socket
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
      socket.close(1000, '协议序列断层，重新建立快照')
    } else {
      this.open()
    }
  }

  /** 发送新的通用命令信封；命令名和载荷由 wire.v1 直接定义。 */
  sendCommand<T extends object>(command: string, payload: T): boolean {
    if (!command || command.length > 96) {
      useGameStore.getState().setNotice(getT()('common.net.invalidCommand'))
      return false
    }
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      useGameStore.getState().setNotice(getT()('common.net.queueOffline'))
      return false
    }
    const requestId = createRequestId()
    const commandPayload = { ...payload, requestId }
    const expectedStateVersion = useGameStore.getState().version
    try {
      socket.send(encodeJsonEnvelope(WireMessageType.Command, requestId, this.nextOutboundSequence(), {
        requestId,
        command,
        payload: commandPayload,
        expectedStateVersion,
      }))
      this.pendingRequests.set(requestId, window.setTimeout(() => {
        if (!this.pendingRequests.delete(requestId)) return
        useGameStore.getState().setNotice(getT()('common.net.requestTimeout'))
      }, GameClient.REQUEST_TIMEOUT_MS))
      return true
    } catch {
      useGameStore.getState().setNotice(getT()('common.net.queueOffline'))
      return false
    }
  }

  /**
   * 发送传音发言（ChatSend 信封）。
   * 消息权威落库在服务端，本方法只负责上行；回显由服务端广播的 ChatMessage 帧驱动。
   */
  sendChat(channel: ChatChannel, body: string, targetPlayerId?: string | null): boolean {
    const socket = this.ensureOpenSocket()
    if (!socket) return false
    const requestId = createRequestId()
    const payload: ChatSendDto = { requestId, channel, body, ...(targetPlayerId ? { targetPlayerId } : {}) }
    return this.trySend(socket, WireMessageType.ChatSend, requestId, payload)
  }

  /** 请求某频道历史回放（ChatHistoryRequest 信封）；服务端以 ChatHistory 应答。 */
  requestChatHistory(channel: ChatChannel, targetPlayerId?: string | null): boolean {
    const socket = this.ensureOpenSocket()
    if (!socket) return false
    const payload: ChatHistoryRequestDto = { channel, ...(targetPlayerId ? { targetPlayerId } : {}) }
    return this.trySend(socket, WireMessageType.ChatHistoryRequest, createRequestId(), payload)
  }

  /** 发送社交指令（SocialAction 信封）；服务端以带 requestId 的 SocialState 应答，失败走 Error 帧。 */
  sendSocialAction(action: SocialAction, targetPlayerId?: string | null, targetName?: string | null): boolean {
    const socket = this.ensureOpenSocket()
    if (!socket) return false
    const requestId = createRequestId()
    const payload: SocialActionDto = {
      requestId,
      action,
      ...(targetPlayerId ? { targetPlayerId } : {}),
      ...(targetName ? { targetName } : {}),
    }
    return this.trySend(socket, WireMessageType.SocialAction, requestId, payload)
  }

  private ensureOpenSocket(): WebSocket | null {
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      useGameStore.getState().setNotice(getT()('common.net.queueOffline'))
      return null
    }
    return socket
  }

  private trySend<T extends object>(socket: WebSocket, messageType: WireMessageType, requestId: string, payload: T): boolean {
    try {
      socket.send(encodeJsonEnvelope(messageType, requestId, this.nextOutboundSequence(), payload))
      return true
    } catch {
      useGameStore.getState().setNotice(getT()('common.net.queueOffline'))
      return false
    }
  }

  private open(): void {
    this.clearReconnectTimer()
    if (this.disposed || !getSessionAccount()) return
    this.outboundSequence = 0
    this.lastServerSequence = 0
    useGameStore.getState().setConnection(this.reconnectDelay > 500 ? 'reconnecting' : 'connecting')
    let socket: WebSocket
    try {
      socket = new WebSocket(getEndpoint())
    } catch {
      // 配置错误或浏览器拒绝建立连接时，转入与 close 事件相同的退避路径。
      this.scheduleReconnect()
      return
    }
    socket.binaryType = 'arraybuffer'
    this.socket = socket

    socket.addEventListener('open', () => {
      if (this.disposed || this.socket !== socket) {
        socket.close(1000, '客户端已停止')
        return
      }
      this.reconnectDelay = 500
      useGameStore.getState().setConnection('online')
      this.startHeartbeat()
      void this.sendHello()
    })
    socket.addEventListener('message', event => {
      void this.handleMessage(event.data, socket)
    })
    socket.addEventListener('error', () => {
      // close 事件统一重连，避免重复提示。
    })
    socket.addEventListener('close', event => {
      this.stopHeartbeat()
      if (this.socket !== socket) return
      this.socket = null
      this.clearPendingRequests()
      if (this.disposed) return
      // 配表指纹不匹配：挂起自动重连，等待 App 重新拉取配表成功后显式 connect() 放行。
      if (this.catalogRefreshPending) {
        useGameStore.getState().setConnection('reconnecting')
        return
      }
      const authFailure = event.code === 1008 || (event.code >= 4001 && event.code <= 4004)
      if (authFailure) {
        useGameStore.getState().setConnection('offline')
        useGameStore.getState().setNotice(getT()('common.net.sessionExpired'))
        return
      }
      // 防多开关闭码（4005-4008）：被顶替/配额拒绝/重复连接均禁止自动重连，否则两端互踢形成乒乓风暴。
      const antiMultiNoticeKey = ANTI_MULTI_CLOSE_NOTICE_KEYS[event.code]
      if (antiMultiNoticeKey) {
        useGameStore.getState().setConnection('offline')
        useGameStore.getState().setNotice(getT()(antiMultiNoticeKey))
        return
      }
      this.scheduleReconnect()
    })
  }

  private clearPendingRequests(): void {
    for (const timer of this.pendingRequests.values()) window.clearTimeout(timer)
    this.pendingRequests.clear()
  }

  private async sendHello(): Promise<void> {
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) return
    // 设备指纹采集是异步的：等待其完成再发送 Hello，避免配额开启时因缺指纹被拒。
    const deviceFingerprint = await getHelloDeviceFingerprint().catch(() => null)
    if (this.socket !== socket || socket.readyState !== WebSocket.OPEN) return
    try {
      socket.send(encodeJsonEnvelope(WireMessageType.Hello, createRequestId(), this.nextOutboundSequence(), {
        clientVersion: APP_VERSION,
        gameDataFingerprint: getCatalogFingerprint(),
        deviceFingerprint,
      }))
    } catch {
      socket.close(1011, '握手发送失败')
    }
  }

  private nextOutboundSequence(): number {
    const next = this.outboundSequence + 1
    if (!Number.isSafeInteger(next)) throw new Error('客户端协议序列号耗尽。')
    this.outboundSequence = next
    return next
  }

  private async handleMessage(data: unknown, sourceSocket: WebSocket): Promise<void> {
    if (this.socket !== sourceSocket) return
    let bytes: Uint8Array
    if (data instanceof ArrayBuffer) bytes = new Uint8Array(data)
    else if (data instanceof Blob) bytes = new Uint8Array(await data.arrayBuffer())
    else {
      useGameStore.getState().setNotice(getT()('app.login.errorNetworkFailed'))
      return
    }
    if (this.socket !== sourceSocket) return

    try {
      const envelope = decodeEnvelope(bytes)
      if (envelope.sequence <= this.lastServerSequence) return
      this.lastServerSequence = envelope.sequence
      // 帧结构解析成功即视为连接恢复健康，清空解析失败计数。
      this.resetParseFailureState()

      switch (envelope.messageType) {
        case WireMessageType.HelloAck: {
          const hello = decodeJsonPayload<HelloResponse>(envelope)
          if (hello.protocolVersion !== 1) throw new Error('服务端协议版本不匹配。')
          applyAntiMultiOpenConfig(hello.antiMultiOpen)
          break
        }
        case WireMessageType.Snapshot: {
          const dto = decodeJsonPayload<PlayerSnapshotDto>(envelope)
          const snapshot = await decodePlayerSnapshotDto(dto, this.account)
          useGameStore.getState().applySnapshot(snapshot)
          break
        }
        case WireMessageType.SnapshotDelta: {
          const dto = decodeJsonPayload<PlayerSnapshotDeltaDto>(envelope)
          const current = useGameStore.getState().toSnapshotView()
          const patch = await decodePlayerSnapshotDeltaDto(dto, current)
          useGameStore.getState().applyPatch(patch)
          break
        }
        case WireMessageType.CommandResult: {
          const result = decodeJsonPayload<CommandResultDto>(envelope)
          if (result.requestId !== envelope.requestId) return
          const timer = this.pendingRequests.get(result.requestId)
          if (timer === undefined) return
          window.clearTimeout(timer)
          this.pendingRequests.delete(result.requestId)
          if (isPlayerSnapshotDto(result.payload)) {
            const snapshot = await decodePlayerSnapshotDto(result.payload, this.account)
            useGameStore.getState().applySnapshot(snapshot)
          }
          if (result.message) useGameStore.getState().setNotice(result.message)
          break
        }
        case WireMessageType.ActionResult:
          // ActionIntent 的专用回执只确认排队结果；权威状态随后由 snapshot/delta 推送。
          break
        case WireMessageType.Error: {
          const error = decodeJsonPayload<ErrorDto>(envelope)
          if (error.requestId) {
            const timer = this.pendingRequests.get(error.requestId)
            if (timer !== undefined) window.clearTimeout(timer)
            this.pendingRequests.delete(error.requestId)
          }
          if (error.code === 'GAME_DATA_MISMATCH') {
            // 客户端配表落后于服务端：服务端保持连接但不放行握手，
            // 此处主动断开并挂起自动重连，通知 App 重新拉取配表后再 connect()。
            this.catalogRefreshPending = true
            sourceSocket.close(1000, '客户端配表指纹不匹配')
            window.dispatchEvent(new Event(GAME_DATA_MISMATCH_EVENT))
          }
          useGameStore.getState().setNotice(error.message || error.code)
          break
        }
        case WireMessageType.LogBatch: {
          const batch = decodeJsonPayload<LogBatchDto>(envelope)
          if (batch?.events && Array.isArray(batch.events) && batch.events.length > 0) {
            gameLogService.ingestBatch(this.playerId, batch.events)
          }
          break
        }
        case WireMessageType.ChatMessage: {
          const message = decodeJsonPayload<ChatMessageDto>(envelope)
          if (message?.messageId) chatService.appendMessage(message)
          break
        }
        case WireMessageType.ChatHistory: {
          const history = decodeJsonPayload<ChatHistoryDto>(envelope)
          if (history?.channel) chatService.applyHistory(history)
          break
        }
        case WireMessageType.SocialState: {
          const state = decodeJsonPayload<SocialStateDto>(envelope)
          if (state) useSocialStore.getState().applyState(state)
          break
        }
        case WireMessageType.Pong:
        case WireMessageType.AssetView:
        case WireMessageType.MarketBook:
        case WireMessageType.MarketOrder:
        case WireMessageType.ComputeAdvanceRequest:
        case WireMessageType.ComputeAdvanceResponse:
        case WireMessageType.ChatSend:
        case WireMessageType.ChatHistoryRequest:
        case WireMessageType.SocialAction:
          break
        default:
          throw new Error('收到未知协议消息。')
      }
    } catch (error) {
      // 解析失败不主动断开：丢弃该帧，仅按阈值渐进升级（提示 -> resync -> 断开重连），
      // 避免坏帧与"close -> 重连 -> 再失败"形成无限循环。
      this.handleParseFailure(error)
    }
  }

  private resetParseFailureState(): void {
    this.parseFailureCount = 0
    this.parseFailureWindowStart = 0
    this.parseFailureResynced = false
    this.lastParseErrorKey = ''
  }

  private handleParseFailure(error: unknown): void {
    const now = Date.now()
    if (now - this.parseFailureWindowStart > PARSE_FAILURE_WINDOW_MS) {
      this.parseFailureWindowStart = now
      this.parseFailureCount = 0
      this.parseFailureResynced = false
    }
    this.parseFailureCount += 1

    const message = error instanceof Error ? error.message : '协议包解析失败。'
    if (message !== this.lastParseErrorKey || now - this.lastParseErrorAt > PARSE_FAILURE_NOTICE_THROTTLE_MS) {
      this.lastParseErrorKey = message
      this.lastParseErrorAt = now
      useGameStore.getState().setNotice(message)
    }

    if (this.parseFailureCount >= PARSE_FAILURE_FATAL_COUNT) {
      this.resetParseFailureState()
      useGameStore.getState().setNotice(getT()('common.net.parseUnstable'))
      // close 事件统一进入指数退避重连路径，重连成功后若仍有好帧到达即恢复。
      this.socket?.close(1000, '协议解析持续失败，重建连接')
      return
    }
    if (this.parseFailureCount >= PARSE_FAILURE_RESYNC_THRESHOLD && !this.parseFailureResynced) {
      this.parseFailureResynced = true
      this.requestSnapshotOrResync()
    }
  }
}

/** 全局游戏网络客户端单例（UI 与账号 Store 统一经它收发指令与状态） */
export const gameClient = new GameClient()

// 标签页被互斥守卫阻断时拆除网络层：断开 socket、停止心跳并禁止自动重连。
registerBlockedTeardown(() => gameClient.dispose())

/** 识别 CommandResult 附带 payload 是否为全量快照（指令回执可随带最新快照就地应用） */
function isPlayerSnapshotDto(value: unknown): value is PlayerSnapshotDto {
  if (value === null || typeof value !== 'object') return false
  const row = value as Record<string, unknown>
  return typeof row.playerId === 'string'
    && typeof row.displayName === 'string'
    && Array.isArray(row.sections)
    && Array.isArray(row.assets)
}
