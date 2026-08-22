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
  private requestSequence = 0
  private disposed = false
  private reconnectDelay = 500
  private catalogAbortController: AbortController | null = null
  private immediateFailures = 0
  private stoppedByRejection = false

  playerId = getStoredAccount()?.playerId ?? ''
  account = getStoredAccount()?.account ?? ''
  characterName = getStoredAccount()?.name ?? ''

  /**
   * 启动游戏网络连接
   */
  connect(): void {
    const accountInfo = getStoredAccount()
    if (!accountInfo) {
      useGameStore.getState().setConnection('offline')
      return
    }
    this.playerId = accountInfo.playerId
    this.account = accountInfo.account
    this.characterName = accountInfo.name
    this.disposed = false
    this.immediateFailures = 0
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
   * 释放客户端资源并优雅关闭 WebSocket
   */
  dispose(): void {
    this.disposed = true
    if (this.reconnectTimer !== undefined) {
      window.clearTimeout(this.reconnectTimer)
      this.reconnectTimer = undefined
    }
    this.socket?.close(1000, '客户端断开')
    this.socket = null
    this.catalogAbortController?.abort()
    this.catalogAbortController = null
    void gameLogService.flush()
    useGameStore.getState().setConnection('offline')
  }

  /** 用户保存新访问令牌后调用；仅在因鉴权被拒而停止重连时立即恢复连接。 */
  notifyAccessTokenChanged(): void {
    if (this.disposed || !this.stoppedByRejection) return
    this.stoppedByRejection = false
    this.immediateFailures = 0
    this.reconnectDelay = 500
    this.open()
  }

  /**
   * 向服务端发送业务操作命令 (Command Packet)
   * @param command 命令名称（如 'enqueue_action', 'set_combat_loadout' 等）
   * @param payload 命令参数载荷
   * @returns 是否成功发出
   */
  sendCommand(command: string, payload: unknown): boolean {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      useGameStore.getState().setNotice(getT()('common.net.queueOffline'))
      return false
    }

    const requestId = `${this.playerId.slice(0, 8)}-${++this.requestSequence}`
    const packet: Packet = {
      kind: 'command',
      requestId,
      sequence: 0,
      payload: { command, payload },
    }
    const bytes = new TextEncoder().encode(JSON.stringify(packet))
    this.socket.send(bytes)
    return true
  }

  /**
   * 内部打开 WebSocket 连接并配置事件监听
   */
  private open(): void {
    if (this.disposed || !this.playerId) return
    useGameStore.getState().setConnection(this.reconnectDelay > 500 ? 'reconnecting' : 'connecting')
    const attemptStartedAt = Date.now()
    let openedThisSocket = false
    const socket = new WebSocket(getEndpoint(this.playerId, this.account, this.characterName))
    socket.binaryType = 'arraybuffer'
    this.socket = socket

    socket.addEventListener('open', () => {
      openedThisSocket = true
      this.reconnectDelay = 500
      this.immediateFailures = 0
      useGameStore.getState().setConnection('online')
      void this.loadCatalogs()
      this.sendHello()
    })
    socket.addEventListener('message', (event) => {
      void this.handleMessage(event.data)
    })
    socket.addEventListener('error', () => {
      // close 事件负责统一重连，避免重复提示和重复刷新。
    })
    socket.addEventListener('close', () => {
      if (this.socket === socket) this.socket = null
      if (this.disposed) return
      // 从未 open 且快速失败，通常是服务端拒绝了令牌；连续多次后停止自动重连，
      // 等待用户在设置中保存新令牌后再恢复，避免无限循环撞墙。
      const failedFastWithoutOpen = !openedThisSocket && Date.now() - attemptStartedAt < 2000
      if (failedFastWithoutOpen) this.immediateFailures += 1
      else this.immediateFailures = 0
      if (failedFastWithoutOpen && this.immediateFailures >= 3) {
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
