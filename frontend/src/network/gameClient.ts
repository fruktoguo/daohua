import { useGameStore } from '../game/gameStore'
import type { AckPayload, ErrorPayload, Packet, PlayerPatch, PlayerSnapshot } from '../game/types'
import { getT } from '../i18n'
import { gameLogService } from '../logging/gameLogService'
import { getAccessToken } from './access_token'
import { clearStoredAccount, getStoredAccount } from './account'
import { fetchActionDefinitions, fetchCombatAbilities, fetchCombatEncounters, fetchCombatEquipment, fetchItemDefinitions, fetchWorldMaps } from './apiClient'

const decoder = new TextDecoder()

/**
 * 构造带有玩家标识、账号与访问令牌的 WebSocket 连接地址
 */
function getEndpoint(playerId: string, account?: string, name?: string): string {
  const configured = import.meta.env.VITE_WS_URL as string | undefined
  const fallback = `${window.location.protocol === 'https:' ? 'wss' : 'ws'}://${window.location.host}/ws`
  const endpoint = new URL(configured || fallback)
  endpoint.searchParams.set('playerId', playerId)
  if (account) endpoint.searchParams.set('account', account)
  endpoint.searchParams.set('name', name || account || '无名修士')
  const token = getAccessToken()
  if (token) endpoint.searchParams.set('token', token)
  return endpoint.toString()
}

/**
 * 道化太初核心 WebSocket 游戏客户端
 * 负责维护与服务端的长连接、断线指数退避重连、静态目录加载、双向心跳与二进制/JSON 数据包流转
 */
export class GameClient {
  private socket: WebSocket | null = null
  private reconnectTimer: number | undefined
  private heartbeatTimer: number | undefined
  private requestSequence = 0
  private disposed = false
  private reconnectDelay = 500
  private catalogAbortController: AbortController | null = null
  private stoppedByRejection = false

  playerId = getStoredAccount()?.playerId ?? ''
  account = getStoredAccount()?.account ?? ''
  characterName = getStoredAccount()?.name ?? ''

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== undefined) {
      window.clearTimeout(this.reconnectTimer)
      this.reconnectTimer = undefined
    }
  }

  private startHeartbeat(): void {
    this.stopHeartbeat()
    this.heartbeatTimer = window.setInterval(() => {
      if (this.socket && this.socket.readyState === WebSocket.OPEN) {
        const pingPacket: Packet = {
          kind: 'ping',
          requestId: `${this.playerId.slice(0, 8)}-ping-${++this.requestSequence}`,
          sequence: 0,
          payload: { timestamp: Date.now() },
        }
        this.socket.send(new TextEncoder().encode(JSON.stringify(pingPacket)))
      }
    }, 25_000)
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== undefined) {
      window.clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = undefined
    }
  }

  /**
   * 启动游戏网络连接
   */
  connect(): void {
    this.clearReconnectTimer()
    const accountInfo = getStoredAccount()
    if (!accountInfo) {
      useGameStore.getState().setConnection('offline')
      return
    }
    this.playerId = accountInfo.playerId
    this.account = accountInfo.account
    this.characterName = accountInfo.name
    this.disposed = false
    this.stoppedByRejection = false
    this.reconnectDelay = 500
    void gameLogService.activate(this.playerId)
    this.open()
  }

  /**
   * 退出登录并清理账号
   */
  logout(): void {
    clearStoredAccount()
  }

  /**
   * 释放网络连接与后台轮询资源
   */
  dispose(): void {
    this.disposed = true
    this.clearReconnectTimer()
    this.stopHeartbeat()
    this.socket?.close(1000, '客户端断开')
    this.socket = null
    this.catalogAbortController?.abort()
    this.catalogAbortController = null
    void gameLogService.flush()
    useGameStore.getState().setConnection('offline')
  }

  /**
   * 当检测到版本空洞或断层时触发重拉全量快照
   */
  requestSnapshotOrResync(): void {
    if (this.disposed) return
    if (this.socket && this.socket.readyState === WebSocket.OPEN) {
      this.socket.close(1000, '版本断层，重拉快照')
    } else {
      this.open()
    }
  }

  /**
   * 通知访问令牌已变更（用户在设置中填入新 Token 时唤醒重试）
   */
  notifyAccessTokenChanged(): void {
    if (this.disposed || !this.stoppedByRejection) return
    this.stoppedByRejection = false
    this.reconnectDelay = 500
    this.open()
  }

  /**
   * 向服务端发送业务操作指令
   */
  sendCommand<T extends object>(type: string, payload: T): boolean {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      useGameStore.getState().setNotice(getT()('common.notice.reconnecting'))
      return false
    }
    const packet: Packet<{ command: string; payload: T }> = {
      kind: 'command',
      requestId: `${this.playerId.slice(0, 8)}-${type}-${++this.requestSequence}`,
      sequence: 0,
      payload: {
        command: type,
        payload,
      },
    }
    this.socket.send(new TextEncoder().encode(JSON.stringify(packet)))
    return true
  }

  /**
   * 打开底层 WebSocket 连接
   */
  private open(): void {
    this.clearReconnectTimer()
    if (this.disposed || !this.playerId) return
    useGameStore.getState().setConnection(this.reconnectDelay > 500 ? 'reconnecting' : 'connecting')
    const socket = new WebSocket(getEndpoint(this.playerId, this.account, this.characterName))
    socket.binaryType = 'arraybuffer'
    this.socket = socket

    socket.addEventListener('open', () => {
      this.reconnectDelay = 500
      useGameStore.getState().setConnection('online')
      this.startHeartbeat()
      void this.loadCatalogs()
      this.sendHello()
    })
    socket.addEventListener('message', (event) => {
      void this.handleMessage(event.data)
    })
    socket.addEventListener('error', () => {
      // close 事件负责统一重连，避免重复提示和重复刷新。
    })
    socket.addEventListener('close', (event) => {
      this.stopHeartbeat()
      if (this.socket === socket) this.socket = null
      if (this.disposed) return

      // 检查 WebSocket close code 精准熔断认证/权限错误 (1008 或 4001~4004)
      const isAuthFailure = event.code === 1008 || (event.code >= 4001 && event.code <= 4004)
      if (isAuthFailure) {
        this.stoppedByRejection = true
        useGameStore.getState().setConnection('offline')
        return
      }

      useGameStore.getState().setConnection('reconnecting')
      const delay = this.reconnectDelay
      this.reconnectDelay = Math.min(8000, this.reconnectDelay * 2)
      this.reconnectTimer = window.setTimeout(() => this.open(), delay)
    })
  }

  /**
   * 连接建立后异步并发拉取最新的各类静态数据目录
   */
  private async loadCatalogs(): Promise<void> {
    this.catalogAbortController?.abort()
    const controller = new AbortController()
    this.catalogAbortController = controller
    try {
      const [actions, items, abilities, equipment, encounters, maps] = await Promise.all([
        fetchActionDefinitions(controller.signal),
        fetchItemDefinitions(controller.signal),
        fetchCombatAbilities(controller.signal),
        fetchCombatEquipment(controller.signal),
        fetchCombatEncounters(controller.signal),
        fetchWorldMaps(controller.signal),
      ])
      if (!controller.signal.aborted) {
        if (actions.length > 0) useGameStore.getState().setActions(actions)
        if (items.length > 0) useGameStore.getState().setItems(items)
        if (abilities.length > 0) useGameStore.getState().setAbilities(abilities)
        if (equipment.length > 0) useGameStore.getState().setEquipment(equipment)
        if (encounters.length > 0) useGameStore.getState().setEncounters(encounters)
        if (maps.length > 0) useGameStore.getState().setMaps(maps)
      }
    } catch {
      // 服务端尚未启动时保留静态目录，WebSocket 建立后仍可正常预览。
    } finally {
      if (this.catalogAbortController === controller) this.catalogAbortController = null
    }
  }

  /**
   * 向服务端发送初始 Hello 握手包
   */
  private sendHello(): void {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return
    const packet: Packet = {
      kind: 'hello',
      requestId: `${this.playerId.slice(0, 8)}-hello-${++this.requestSequence}`,
      sequence: 0,
      payload: {},
    }
    this.socket.send(new TextEncoder().encode(JSON.stringify(packet)))
  }

  /**
   * 处理服务端下发的 WebSocket 消息包
   */
  private async handleMessage(data: unknown): Promise<void> {
    const bytes = data instanceof Blob
      ? new Uint8Array(await data.arrayBuffer())
      : data instanceof ArrayBuffer
        ? new Uint8Array(data)
        : new TextEncoder().encode(String(data))
    let packet: Packet
    try {
      packet = JSON.parse(decoder.decode(bytes)) as Packet
    } catch {
      useGameStore.getState().setNotice('收到无法解析的数据包。')
      return
    }

    switch (packet.kind) {
      case 'snapshot':
      {
        const snapshot = packet.payload as PlayerSnapshot
        gameLogService.ingestSnapshot(
          snapshot.playerId,
          snapshot.logEvents,
        )
        useGameStore.getState().applySnapshot(snapshot)
        break
      }
      case 'delta':
      {
        const patch = packet.payload as PlayerPatch
        if (patch.eventBatch && patch.eventBatch.length > 0) {
          gameLogService.ingestBatch(patch.playerId, patch.eventBatch)
        }
        useGameStore.getState().applyPatch(patch)
        break
      }
      case 'ack': {
        const ack = packet.payload as AckPayload
        useGameStore.getState().setNotice(ack.message)
        break
      }
      case 'pong': {
        // 心跳回执，静默确认
        break
      }
      case 'error': {
        const error = packet.payload as ErrorPayload
        useGameStore.getState().setNotice(error.message)
        break
      }
      default:
        break
    }
  }
}

/** 全局单例游戏网络客户端 */
export const gameClient = new GameClient()
