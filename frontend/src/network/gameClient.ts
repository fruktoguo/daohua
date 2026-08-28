import { useGameStore } from '../game/gameStore'
import { getT } from '../i18n'
import { gameLogService } from '../logging/gameLogService'
import { clearStoredAccount } from './account'
import {
  createRequestId,
  decodeEnvelope,
  decodeJsonPayload,
  encodeJsonEnvelope,
  type CommandResultDto,
  type ErrorDto,
  type HelloResponse,
  type LogBatchDto,
  type PlayerSnapshotDeltaDto,
  type PlayerSnapshotDto,
  WireMessageType,
} from './protocol'
import { decodePlayerSnapshotDeltaDto, decodePlayerSnapshotDto } from './snapshot'
import { getSessionAccount } from './sessionState'

const APP_VERSION = String(import.meta.env.VITE_APP_VERSION ?? 'dev')

function getEndpoint(): string {
  const configured = import.meta.env.VITE_WS_URL as string | undefined
  if (configured) return configured
  return `${window.location.protocol === 'https:' ? 'wss' : 'ws'}://${window.location.host}/ws`
}

/**
 * wire.v1 游戏客户端。
 *
 * 连接只继承 HttpOnly 会话 Cookie，不接受 URL 中的 playerId、token 或角色名。
 * 所有上行命令都通过 DHW1 二进制信封发送；服务端推送的状态只接受新的
 * PlayerSnapshotDto/PlayerSnapshotDeltaDto，不再尝试解析旧 JSON 包结构。
 */
export class GameClient {
  private socket: WebSocket | null = null
  private reconnectTimer: number | undefined
  private heartbeatTimer: number | undefined
  private disposed = false
  private reconnectDelay = 500
  private lastServerSequence = 0
  private outboundSequence = 0
  private readonly pendingRequests = new Set<string>()

  playerId = getSessionAccount()?.playerId ?? ''
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

  connect(): void {
    this.clearReconnectTimer()
    const accountInfo = getSessionAccount()
    if (!accountInfo) {
      useGameStore.getState().setConnection('offline')
      return
    }
    if (this.socket && (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING)) return
    this.playerId = accountInfo.playerId
    this.account = accountInfo.account
    this.disposed = false
    this.reconnectDelay = 500
    this.outboundSequence = 0
    this.lastServerSequence = 0
    this.pendingRequests.clear()
    this.open()
  }

  logout(): void {
    clearStoredAccount()
  }

  dispose(): void {
    this.disposed = true
    this.clearReconnectTimer()
    this.stopHeartbeat()
    const socket = this.socket
    this.socket = null
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) socket.close(1000, '客户端断开')
    useGameStore.getState().setConnection('offline')
  }

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
      this.pendingRequests.add(requestId)
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
      this.sendHello()
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
      this.pendingRequests.clear()
      if (this.disposed) return
      const authFailure = event.code === 1008 || (event.code >= 4001 && event.code <= 4004)
      if (authFailure) {
        useGameStore.getState().setConnection('offline')
        useGameStore.getState().setNotice(getT()('common.net.sessionExpired'))
        return
      }
      this.scheduleReconnect()
    })
  }

  private sendHello(): void {
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) return
    try {
      socket.send(encodeJsonEnvelope(WireMessageType.Hello, createRequestId(), this.nextOutboundSequence(), {
        clientVersion: APP_VERSION,
        gameDataFingerprint: null,
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

      switch (envelope.messageType) {
        case WireMessageType.HelloAck: {
          const hello = decodeJsonPayload<HelloResponse>(envelope)
          if (hello.protocolVersion !== 1) throw new Error('服务端协议版本不匹配。')
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
          if (result.requestId !== envelope.requestId || !this.pendingRequests.delete(result.requestId)) return
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
          if (error.requestId) this.pendingRequests.delete(error.requestId)
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
        case WireMessageType.Pong:
        case WireMessageType.AssetView:
        case WireMessageType.MarketBook:
        case WireMessageType.MarketOrder:
        case WireMessageType.ComputeAdvanceRequest:
        case WireMessageType.ComputeAdvanceResponse:
          break
        default:
          throw new Error('收到未知协议消息。')
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : '协议包解析失败。'
      useGameStore.getState().setNotice(message)
      this.requestSnapshotOrResync()
    }
  }
}

export const gameClient = new GameClient()

function isPlayerSnapshotDto(value: unknown): value is PlayerSnapshotDto {
  if (value === null || typeof value !== 'object') return false
  const row = value as Record<string, unknown>
  return typeof row.playerId === 'string'
    && typeof row.displayName === 'string'
    && Array.isArray(row.sections)
    && Array.isArray(row.assets)
}
