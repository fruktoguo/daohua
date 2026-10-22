import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { getT } from '../i18n'
import { chatService } from '../logging/chatService'
import { gameLogService } from '../logging/gameLogService'
import { notifyIncomingChatMessage, notifyIncomingLogEvents, useLogUnreadStore } from '../logging/logUnreadStore'
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
  CommandResultSnapshotForm,
  type ErrorDto,
  type HelloResponse,
  type LogBatchDto,
  type PartyLobbyDto,
  type PartyLobbyRequestDto,
  type PartyProfileDto,
  type PlayerCardDto,
  type PlayerCardRequestDto,
  type PlayerSnapshotDeltaDto,
  type PlayerSnapshotDto,
  type PongResponse,
  type SocialAction,
  type SocialActionDto,
  type SocialStateDto,
  WireMessageType,
} from './protocol'
import { decodePlayerSnapshotDeltaDto, decodePlayerSnapshotDto } from './snapshot'
import { fetchMarketStats, getCatalogFingerprint } from './apiClient'
import { getSessionAccount } from './sessionState'
import { useUiStore } from '../uiStore'
import type { AwakenedTraitResult } from '../game/types'
import { collectAccountSignals } from '../antiMulti/accountSignals'
import {
  ANTI_MULTI_CLOSE_NOTICE_KEYS,
  applyAntiMultiOpenConfig,
  isTabBlocked,
  registerBlockedTeardown,
} from '../antiMulti'
import { notifyServerVersion } from './versionManager'

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
  /** 会话世代：账号切换、连接重建和 dispose 后，旧 socket 的异步回调一律失效。 */
  private sessionGeneration = 0
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
  /** 最近一次登记的单位详情观察目标；重连握手完成后按原样重放，恢复按需属性投影。 */
  private lastInspectTarget: { side: 'enemy' | 'ally'; index: number } | null = null
  /** 待处理请求计时器；连接关闭后保留元数据，避免把可能已执行的命令静默判定为失败。 */
  private readonly pendingRequests = new Map<string, number>()
  /** 在途 Ping 的发送时间（requestId -> Date.now()），Pong 回包时计算 RTT。 */
  private readonly pendingPings = new Map<string, number>()
  /** 手动测速的最小间隔，避免悬停抖动刷 Ping。 */
  private lastManualPingAt = 0
  private readonly awaitingReconnectRequests = new Set<string>()
  /** 坊市行情统计轮询计时器：仅在会话在线期间运行，连接断开即停。 */
  private marketStatsTimer: number | undefined
  /** 行情统计在途请求标记：上一轮未返回时跳过一次 tick，避免慢网叠加并发请求。 */
  private marketStatsInFlight = false
  /**
   * 入站帧串行链：delta 的 section checksum 解码是真异步（crypto.subtle），
   * 并发处理会让多帧共享同一旧基线互相覆盖应用结果；串行后应用顺序恒等于通道到达序。
   */
  private messageChain: Promise<void> = Promise.resolve()
  private static readonly REQUEST_TIMEOUT_MS = 15_000
  /** 坊市行情统计轮询间隔（15 秒）：悬浮行情卡为低频只读面，不需要盘口级实时性。 */
  private static readonly MARKET_STATS_REFRESH_INTERVAL_MS = 15_000
  /** 行情轮询的页面可见性监听句柄：随 start/stop 成对挂载卸载。 */
  private marketStatsVisibilityHandler: (() => void) | undefined

  /** 当前登录的角色 ID */
  playerId = getSessionAccount()?.playerId ?? ''
  /** 当前登录的账号名 */
  account = getSessionAccount()?.account ?? ''
  /** 当前角色的服务端序号（快照注入用，缺省 0） */
  sequenceId = getSessionAccount()?.sequenceId ?? 0

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== undefined) {
      window.clearTimeout(this.reconnectTimer)
      this.reconnectTimer = undefined
    }
  }

  private startHeartbeat(sourceSocket: WebSocket, sourceGeneration: number, sourcePlayerId: string): void {
    this.stopHeartbeat()
    this.heartbeatTimer = window.setInterval(() => {
      if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId) || sourceSocket.readyState !== WebSocket.OPEN) return
      this.sendPing(sourceSocket)
    }, 25_000)
  }

  /** 发送一次 Ping 并登记发送时间；Pong 回包时按 requestId 计算 RTT 写入 latencyMs。 */
  private sendPing(sourceSocket: WebSocket): void {
    const requestId = createRequestId()
    this.pendingPings.set(requestId, Date.now())
    try {
      sourceSocket.send(encodeJsonEnvelope(WireMessageType.Ping, requestId, this.nextOutboundSequence(), { clientTime: Date.now() }))
    } catch {
      this.pendingPings.delete(requestId)
      // 发送失败由 WebSocket close 事件统一处理。
    }
  }

  /** 立即发送一次 Ping 测往返延迟（悬停连接灯触发）；仅连接已打开时生效，1 秒内重复调用去重。 */
  pingNow(): void {
    const socket = this.socket
    if (!socket || socket.readyState !== WebSocket.OPEN) return
    const now = Date.now()
    if (now - this.lastManualPingAt < 1000) return
    this.lastManualPingAt = now
    this.sendPing(socket)
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== undefined) {
      window.clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = undefined
    }
  }

  /**
   * 坊市行情统计后台轮询：HelloAck 会话就绪后启动，每 15 秒全量拉取 market.item_stats
   * 小表投影写入 gameStore.marketStats，供物品悬浮「市场价格」副卡零请求渲染；
   * 页面转后台时跳过 tick（与坊市页轮询同一节能口径），回前台时立即补一轮。
   */
  private startMarketStatsSync(): void {
    this.stopMarketStatsSync()
    const tick = async () => {
      if (this.disposed || this.marketStatsInFlight || document.visibilityState !== 'visible') return
      this.marketStatsInFlight = true
      try {
        const stats = await fetchMarketStats()
        if (!this.disposed) useGameStore.getState().setMarketStats(stats)
      } catch {
        // 行情为只读背景数据：单轮失败静默等待下一周期，不产生用户态噪音。
      } finally {
        this.marketStatsInFlight = false
      }
    }
    const onVisible = () => {
      if (document.visibilityState === 'visible') void tick()
    }
    this.marketStatsVisibilityHandler = onVisible
    document.addEventListener('visibilitychange', onVisible)
    void tick()
    this.marketStatsTimer = window.setInterval(() => void tick(), GameClient.MARKET_STATS_REFRESH_INTERVAL_MS)
  }

  private stopMarketStatsSync(): void {
    if (this.marketStatsTimer !== undefined) {
      window.clearInterval(this.marketStatsTimer)
      this.marketStatsTimer = undefined
    }
    if (this.marketStatsVisibilityHandler !== undefined) {
      document.removeEventListener('visibilitychange', this.marketStatsVisibilityHandler)
      this.marketStatsVisibilityHandler = undefined
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
    const accountChanged = accountInfo.playerId !== this.playerId
      || accountInfo.account !== this.account
      || (accountInfo.sequenceId ?? 0) !== this.sequenceId
    if (accountChanged) {
      this.sessionGeneration += 1
      const staleSocket = this.socket
      this.socket = null
      this.stopHeartbeat()
      if (staleSocket && (staleSocket.readyState === WebSocket.OPEN || staleSocket.readyState === WebSocket.CONNECTING)) {
        staleSocket.close(1000, '账号会话已切换')
      }
    }
    if (this.socket && (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING)) return
    this.sessionGeneration += 1
    this.playerId = accountInfo.playerId
    this.account = accountInfo.account
    this.sequenceId = accountInfo.sequenceId ?? 0
    chatService.activate(this.playerId)
    useLogUnreadStore.getState().resetAll()
    this.disposed = false
    this.catalogRefreshPending = false
    this.reconnectDelay = 500
    this.outboundSequence = 0
    this.lastServerSequence = 0
    // 新会话起一条干净的入站串行链；旧链残留任务由 handleMessage 的 socket 一致性检查拦截。
    this.messageChain = Promise.resolve()
    this.open()
  }

  /** 登出：断开连接并清空本地账号态 */
  logout(): void {
    this.dispose()
    clearStoredAccount()
  }

  /** 彻底停用客户端：停心跳/重连/挂起请求并关闭 socket；dispose 后不再自动重连 */
  dispose(): void {
    this.sessionGeneration += 1
    this.disposed = true
    this.clearReconnectTimer()
    this.stopHeartbeat()
    this.stopMarketStatsSync()
    this.clearPendingRequests()
    this.awaitingReconnectRequests.clear()
    chatService.reset()
    useLogUnreadStore.getState().resetAll()
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

  /**
   * 发送新的通用命令信封；命令名和载荷由 wire.v1 直接定义。
   * @param command 领域命令名（如 action.enqueue、loadout.replace 等）
   * @param payload 命令载荷
   * @param expectedStateVersion 期望状态版本；缺省为 0 表示不做乐观并发校验（契约定义）。
   *   常规业务操作单写者隔离与内部不变量由服务端保证，不与后台持续模拟推进的 StateVersion 竞争，
   *   避免频繁误报「玩家状态版本已变化」。
   */
  sendCommand<T extends object>(command: string, payload: T, expectedStateVersion = 0): boolean {
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
    try {
      socket.send(encodeJsonEnvelope(WireMessageType.Command, requestId, this.nextOutboundSequence(), {
        requestId,
        command,
        payload: commandPayload,
        expectedStateVersion,
      }))
      this.awaitingReconnectRequests.delete(requestId)
      this.pendingRequests.set(requestId, window.setTimeout(() => {
        if (!this.pendingRequests.delete(requestId)) return
        this.awaitingReconnectRequests.add(requestId)
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

  /**
   * 登记/清除单位详情观察目标（UnitInspect 火忘帧，无回执）。
   * 服务端仅对命中下标的敌怪/斗法镜像/盟友单位随快照投影属性面板；target=null 清除。
   */
  sendUnitInspect(target: { side: 'enemy' | 'ally'; index: number } | null): boolean {
    // 相同目标去重：属性为登记后一次性交付，重复登记只会让服务端无谓重投。
    const last = this.lastInspectTarget
    if (target === last || (target !== null && last !== null && target.side === last.side && target.index === last.index))
      return true
    // 先记账再发送：离线时记录目标，HelloAck 重放恢复观察态。
    this.lastInspectTarget = target
    const socket = this.ensureOpenSocket()
    if (!socket) return false
    return this.trySend(socket, WireMessageType.UnitInspect, createRequestId(), { side: target?.side ?? null, index: target?.index ?? null })
  }

  /**
   * 发送社交指令（SocialAction 信封）；服务端以带 requestId 的 SocialState 应答，失败走 Error 帧。
   * 队伍类指令通过 extras 携带扩展载荷：party.join 用 partyId，party.share 用 channel，
   * party.create/party.update/party.match.set 用 party 档案补丁。
   */
  sendSocialAction(
    action: SocialAction,
    targetPlayerId?: string | null,
    targetName?: string | null,
    extras?: { partyId?: string | null; channel?: ChatChannel | null; party?: PartyProfileDto | null },
  ): boolean {
    const socket = this.ensureOpenSocket()
    if (!socket) return false
    const requestId = createRequestId()
    const payload: SocialActionDto = {
      requestId,
      action,
      ...(targetPlayerId ? { targetPlayerId } : {}),
      ...(targetName ? { targetName } : {}),
      ...(extras?.partyId ? { partyId: extras.partyId } : {}),
      ...(extras?.channel ? { channel: extras.channel } : {}),
      ...(extras?.party ? { party: extras.party } : {}),
    }
    if (!this.trySend(socket, WireMessageType.SocialAction, requestId, payload)) return false
    // 社交成功应答是带 requestId 的 SocialState（必达）、失败是 Error 帧：登记超时计时器做兜底，
    // 超时仅提示——状态由重连全量握手或下一次 StateChanged 广播最终一致。
    this.awaitingReconnectRequests.delete(requestId)
    this.pendingRequests.set(requestId, window.setTimeout(() => {
      if (!this.pendingRequests.delete(requestId)) return
      this.awaitingReconnectRequests.add(requestId)
      useGameStore.getState().setNotice(getT()('common.net.requestTimeout'))
    }, GameClient.REQUEST_TIMEOUT_MS))
    return true
  }

  /** 请求组队大厅视图（PartyLobbyRequest 信封）；服务端以 PartyLobby 应答，结果整体替换 socialStore.lobby。 */
  requestPartyLobby(filter: { activityKind?: string | null; nodeId?: string | null; minValuation?: number | null } = {}): boolean {
    const socket = this.ensureOpenSocket()
    if (!socket) return false
    const payload: PartyLobbyRequestDto = {
      requestId: createRequestId(),
      ...(filter.activityKind ? { activityKind: filter.activityKind as PartyLobbyRequestDto['activityKind'] } : {}),
      ...(filter.nodeId ? { nodeId: filter.nodeId } : {}),
      ...(filter.minValuation !== undefined && filter.minValuation !== null ? { minValuation: filter.minValuation } : {}),
    }
    return this.trySend(socket, WireMessageType.PartyLobbyRequest, payload.requestId, payload)
  }

  /**
   * 请求队伍成员角色信息卡（PlayerCardRequest 信封）；服务端以 PlayerCard 必达应答。
   * 返回本次请求 ID（用于和应答 requestId 配对）；发送失败为 null。
   */
  requestPlayerCard(playerId: string): string | null {
    const socket = this.ensureOpenSocket()
    if (!socket) {
      useGameStore.getState().setNotice(getT()('common.net.queueOffline'))
      return null
    }
    const payload: PlayerCardRequestDto = { requestId: createRequestId(), playerId }
    if (!this.trySend(socket, WireMessageType.PlayerCardRequest, payload.requestId, payload)) {
      useGameStore.getState().setNotice(getT()('common.net.queueOffline'))
      return null
    }
    return payload.requestId
  }

  private isCurrentSession(sourceSocket: WebSocket, sourceGeneration: number, sourcePlayerId: string): boolean {
    return !this.disposed
      && this.socket === sourceSocket
      && this.sessionGeneration === sourceGeneration
      && this.playerId === sourcePlayerId
      && getSessionAccount()?.playerId === sourcePlayerId
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
    const session = getSessionAccount()
    if (this.disposed || !session || session.playerId !== this.playerId || session.account !== this.account || (session.sequenceId ?? 0) !== this.sequenceId) return
    this.sessionGeneration += 1
    this.stopHeartbeat()
    this.stopMarketStatsSync()
    this.pendingPings.clear()
    this.messageChain = Promise.resolve()
    this.outboundSequence = 0
    this.lastServerSequence = 0
    // 新连接发起即清除上一次的终态断连原因，避免旧原因残留到下一次断连。
    useGameStore.getState().setDisconnectReason(null)
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
    const sourceGeneration = this.sessionGeneration
    const sourcePlayerId = this.playerId

    socket.addEventListener('open', () => {
      if (!this.isCurrentSession(socket, sourceGeneration, sourcePlayerId)) {
        socket.close(1000, '客户端已停止')
        return
      }
      // WebSocket transport open 仅代表 TCP 已建立；在合法 HelloAck 前仍保持 connecting。
      useGameStore.getState().setConnection('connecting')
      this.startHeartbeat(socket, sourceGeneration, sourcePlayerId)
      void this.sendHello(socket, sourceGeneration, sourcePlayerId)
    })
    socket.addEventListener('message', event => {
      const data = event.data
      // handleMessage 自身消化协议解析异常；链上兜底防意外异常永久阻塞后续全部帧。
      this.messageChain = this.messageChain
        .then(() => this.handleMessage(data, socket, sourceGeneration, sourcePlayerId))
        .catch(error => {
          if (this.isCurrentSession(socket, sourceGeneration, sourcePlayerId)) this.handleParseFailure(error)
        })
    })
    socket.addEventListener('error', () => {
      // close 事件统一重连，避免重复提示。
    })
    socket.addEventListener('close', event => {
      // 旧连接关闭不能停止新连接心跳，也不能改变新会话状态。
      if (!this.isCurrentSession(socket, sourceGeneration, sourcePlayerId)) return
      this.stopHeartbeat()
      this.stopMarketStatsSync()
      this.socket = null
      this.markPendingRequestsAwaitingReconnect()
      // 连接中断时在途角色信息卡请求必然失效：直接收起弹窗，避免加载态永久悬挂。
      useSocialStore.getState().closePlayerCard()
      if (this.disposed) return
      // 配表指纹不匹配：挂起自动重连，等待 App 重新拉取配表成功后显式 connect() 放行。
      if (this.catalogRefreshPending) {
        useGameStore.getState().setConnection('reconnecting')
        return
      }
      const authFailure = event.code === 1008 || (event.code >= 4001 && event.code <= 4004)
      if (authFailure) {
        useGameStore.getState().setConnection('offline')
        useGameStore.getState().setDisconnectReason('common.net.sessionExpired')
        useGameStore.getState().setNotice(getT()('common.net.sessionExpired'))
        return
      }
      // 防多开关闭码（4005-4008）：被顶替/配额拒绝/重复连接均禁止自动重连，否则两端互踢形成乒乓风暴。
      const antiMultiNoticeKey = ANTI_MULTI_CLOSE_NOTICE_KEYS[event.code]
      if (antiMultiNoticeKey) {
        useGameStore.getState().setConnection('offline')
        useGameStore.getState().setDisconnectReason(antiMultiNoticeKey)
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

  private markPendingRequestsAwaitingReconnect(): void {
    for (const [requestId, timer] of this.pendingRequests) {
      window.clearTimeout(timer)
      this.pendingRequests.delete(requestId)
      this.awaitingReconnectRequests.add(requestId)
    }
    if (this.awaitingReconnectRequests.size > 0) {
      useGameStore.getState().setNotice(getT()('common.net.requestAwaitingReconnect'))
    }
  }

  private async sendHello(sourceSocket: WebSocket, sourceGeneration: number, sourcePlayerId: string): Promise<void> {
    const socket = sourceSocket
    if (!this.isCurrentSession(socket, sourceGeneration, sourcePlayerId) || socket.readyState !== WebSocket.OPEN) return
    // 设备指纹采集是异步的：等待其完成再发送 Hello，避免配额开启时因缺指纹被拒。
    const accountSignals = await collectAccountSignals()
    const deviceFingerprint = accountSignals.browserFingerprint
    if (!this.isCurrentSession(socket, sourceGeneration, sourcePlayerId) || socket.readyState !== WebSocket.OPEN) return
    try {
      socket.send(encodeJsonEnvelope(WireMessageType.Hello, createRequestId(), this.nextOutboundSequence(), {
        clientVersion: APP_VERSION,
        gameDataFingerprint: getCatalogFingerprint(),
        deviceFingerprint,
        deviceConfigHash: accountSignals.deviceConfigHash,
        deviceId: accountSignals.deviceId,
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

  private async handleMessage(data: unknown, sourceSocket: WebSocket, sourceGeneration: number, sourcePlayerId: string): Promise<void> {
    if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) return
    let bytes: Uint8Array
    if (data instanceof ArrayBuffer) bytes = new Uint8Array(data)
    else if (data instanceof Blob) bytes = new Uint8Array(await data.arrayBuffer())
    else throw new Error('协议帧载荷类型无效。')
    if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) return

    try {
      const envelope = decodeEnvelope(bytes)
      if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) return
      if (envelope.sequence <= this.lastServerSequence) return
      this.lastServerSequence = envelope.sequence
      let appliedBusinessPayload = false

      switch (envelope.messageType) {
        case WireMessageType.HelloAck: {
          const hello = decodeJsonPayload<HelloResponse>(envelope)
          const expectedFingerprint = getCatalogFingerprint()
          if (!hello || hello.protocolVersion !== 1 || typeof hello.serverVersion !== 'string' || hello.serverVersion.length === 0
            || typeof hello.gameDataFingerprint !== 'string' || hello.gameDataFingerprint.length === 0
            || !Number.isSafeInteger(hello.serverSequence) || hello.serverSequence !== envelope.sequence || hello.serverSequence <= 0) {
            throw new Error('服务端 HelloAck 载荷无效。')
          }
          notifyServerVersion(hello.serverVersion)
          if (expectedFingerprint && hello.gameDataFingerprint !== expectedFingerprint) {
            this.catalogRefreshPending = true
            sourceSocket.close(1000, '服务端配表指纹不匹配')
            window.dispatchEvent(new Event(GAME_DATA_MISMATCH_EVENT))
            return
          }
          if (hello.presenceStats) {
            useGameStore.getState().setPresenceStats(
              hello.presenceStats.onlineCount,
              hello.presenceStats.offlineIdleCount,
              hello.presenceStats.nodeDuelCombatTeamCount,
              hello.presenceStats.offlineCount,
            )
          }
          applyAntiMultiOpenConfig(hello.antiMultiOpen)
          useGameStore.getState().setConnection('online')
          this.sendPing(sourceSocket)
          this.startMarketStatsSync()
          // 先清账再登记绕开相同目标去重，保证新会话重新武装一次性交付。
          const inspectReplay = this.lastInspectTarget
          this.lastInspectTarget = null
          if (inspectReplay) this.sendUnitInspect(inspectReplay)
          break
        }
        case WireMessageType.Snapshot: {
          const dto = decodeJsonPayload<PlayerSnapshotDto>(envelope)
          // 传入信封原始字节：section checksum 校验直接对线上 data 成员字节区间复算。
          const snapshot = await decodePlayerSnapshotDto(dto, envelope.payload, this.account, this.sequenceId)
          if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) return
          useGameStore.getState().applySnapshot(snapshot)
          this.reconnectDelay = 500
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.SnapshotDelta: {
          const dto = decodeJsonPayload<PlayerSnapshotDeltaDto>(envelope)
          // 首帧全量快照未到达前（store 归属尚未建立），任何增量都无处合并：
          // 正常握手服务端保证 Snapshot 先行；此分支兜底极端竞态（旧会话残留帧等），
          // 静默丢弃等待服务端重连全量，不再抛「归属不一致」红字打断登录流程。
          if (!useGameStore.getState().hasReceivedSnapshot) break
          const current = useGameStore.getState().toSnapshotView()
          const patch = await decodePlayerSnapshotDeltaDto(dto, current, envelope.payload)
          if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) return
          useGameStore.getState().applyPatch(patch)
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.CommandResult: {
          const result = decodeJsonPayload<CommandResultDto>(envelope)
          if (result.requestId !== envelope.requestId) return
          const timer = this.pendingRequests.get(result.requestId)
          if (timer !== undefined) {
            window.clearTimeout(timer)
            this.pendingRequests.delete(result.requestId)
          }
          this.awaitingReconnectRequests.delete(result.requestId)
          // 超 15s 晚到的回执仍要合入快照：pending 计时器只负责超时提示，资产差额仅由回执
          // 携带（推送基线已前移后不再重发），整帧丢弃会造成永久缺失；过旧/重复内容
          // 由 applyPatch/applySnapshot 的版本门禁幂等丢弃，requestId 必须与信封一致。
          if (result.snapshotForm === CommandResultSnapshotForm.Delta && isPlayerSnapshotDeltaDto(result.snapshot)) {
            // 全量快照未到达前的命令回执增量无处合并（同 SnapshotDelta 分支兜底口径）；
            // 仅跳过快照合并，回执的 message/notice 照常展示，状态差额由随后握手全量快照覆盖。
            if (useGameStore.getState().hasReceivedSnapshot) {
              const current = useGameStore.getState().toSnapshotView()
              const patch = await decodePlayerSnapshotDeltaDto(result.snapshot, current, envelope.payload, ['snapshot', 'changedSections'])
              if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) return
              // 钱包类命令（market.*/item.*）不推状态版本：同版本回执仍携带资产差额必须合入，仅丢弃严格更旧的回执。
              useGameStore.getState().applyPatch(patch, { allowSameVersion: true })
              appliedBusinessPayload = true
            }
          } else if (result.snapshotForm === CommandResultSnapshotForm.Snapshot && isPlayerSnapshotDto(result.snapshot)) {
            // 内嵌全量快照的 sections 位于信封 JSON 的 snapshot 成员内，按路径定位 data 字节区间。
            const snapshot = await decodePlayerSnapshotDto(result.snapshot, envelope.payload, this.account, this.sequenceId, ['snapshot', 'sections'])
            if (!this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) return
            useGameStore.getState().applySnapshot(snapshot)
            appliedBusinessPayload = true
          }
           if (result.message) useGameStore.getState().setNotice(result.message)
          if (result.succeeded && result.data && typeof result.data === 'object') {
            const custom = result.data as Record<string, unknown>
            if (Array.isArray(custom.awakenedTraits) && custom.awakenedTraits.length > 0) {
              useUiStore.getState().openAwakenResult(custom.awakenedTraits as AwakenedTraitResult[])
            }
          }
           appliedBusinessPayload = true
          break
        }
        case WireMessageType.ActionResult: {
          // ActionIntent 的专用回执只确认排队结果；权威状态随后由 snapshot/delta 推送。
          const actionResult = decodeJsonPayload<{ requestId?: unknown }>(envelope)
          if (!actionResult || actionResult.requestId !== envelope.requestId) return
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.Error: {
          const error = decodeJsonPayload<ErrorDto>(envelope)
          if (!error || typeof error.code !== 'string' || typeof error.message !== 'string') throw new Error('协议错误载荷无效。')
          if (error.requestId && error.requestId !== envelope.requestId) return
          if (error.requestId) {
            const timer = this.pendingRequests.get(error.requestId)
            if (timer !== undefined) window.clearTimeout(timer)
            this.pendingRequests.delete(error.requestId)
            this.awaitingReconnectRequests.delete(error.requestId)
          }
          if (error.code === 'GAME_DATA_MISMATCH') {
            // 客户端配表落后于服务端：服务端保持连接但不放行握手，
            // 此处主动断开并挂起自动重连，通知 App 重新拉取配表后再 connect()。
            this.catalogRefreshPending = true
            sourceSocket.close(1000, '客户端配表指纹不匹配')
            window.dispatchEvent(new Event(GAME_DATA_MISMATCH_EVENT))
          }
          useGameStore.getState().setNotice(error.message || error.code)
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.LogBatch: {
          const batch = decodeJsonPayload<LogBatchDto>(envelope)
          if (!batch || !Array.isArray(batch.events)) throw new Error('日志批次载荷无效。')
          if (batch.events.length > 0) {
            gameLogService.ingestBatch(this.playerId, batch.events)
            notifyIncomingLogEvents(batch.events)
          }
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.ChatMessage: {
          const message = decodeJsonPayload<ChatMessageDto>(envelope)
          if (!message || typeof message.messageId !== 'string' || !message.messageId) throw new Error('传音消息载荷无效。')
          chatService.appendMessage(message)
          notifyIncomingChatMessage(message, this.playerId)
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.ChatHistory: {
          const history = decodeJsonPayload<ChatHistoryDto>(envelope)
          if (!history || typeof history.channel !== 'string' || !Array.isArray(history.messages)) throw new Error('传音历史载荷无效。')
          chatService.applyHistory(history)
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.SocialState: {
          const state = decodeJsonPayload<SocialStateDto>(envelope)
          // 带 requestId 的应答即社交指令成功回执：配对清除超时计时器；主动推送无 requestId。
          if (state?.requestId) {
            const timer = this.pendingRequests.get(state.requestId)
            if (timer !== undefined) window.clearTimeout(timer)
            this.pendingRequests.delete(state.requestId)
            this.awaitingReconnectRequests.delete(state.requestId)
          }
          if (!state) throw new Error('社交状态载荷无效。')
          useSocialStore.getState().applyState(state)
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.PartyLobby: {
          const lobby = decodeJsonPayload<PartyLobbyDto>(envelope)
          if (!lobby) throw new Error('组队大厅载荷无效。')
          useSocialStore.getState().applyLobby(lobby)
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.PlayerCard: {
          const card = decodeJsonPayload<PlayerCardDto>(envelope)
          if (!card) throw new Error('角色信息卡载荷无效。')
          useSocialStore.getState().applyPlayerCard(card)
          appliedBusinessPayload = true
          break
        }
        case WireMessageType.Pong: {
          const sentAt = this.pendingPings.get(envelope.requestId)
          if (sentAt !== undefined) {
            this.pendingPings.delete(envelope.requestId)
            useGameStore.getState().setLatencyMs(Math.max(0, Date.now() - sentAt))
          }
          const pong = decodeJsonPayload<PongResponse>(envelope)
          if (pong && typeof pong.onlineCount === 'number') {
            useGameStore.getState().setPresenceStats(
              pong.onlineCount,
              pong.offlineIdleCount ?? 0,
              pong.nodeDuelCombatTeamCount,
              pong.offlineCount,
            )
          }
          return
        }
        case WireMessageType.AssetView:
        case WireMessageType.MarketBook:
        case WireMessageType.MarketOrder:
        case WireMessageType.ComputeAdvanceRequest:
        case WireMessageType.ComputeAdvanceResponse:
        case WireMessageType.ChatSend:
        case WireMessageType.ChatHistoryRequest:
        case WireMessageType.SocialAction:
        case WireMessageType.PartyLobbyRequest:
        case WireMessageType.PlayerCardRequest:
          // 这些类型是客户端上行回显，服务端不会作为业务帧下发；合法 envelope 不代表业务成功。
          return
        default:
          throw new Error('收到未知协议消息。')
      }
      if (appliedBusinessPayload) this.resetParseFailureState()
    } catch (error) {
      // 解析失败不主动断开：丢弃该帧，仅按阈值渐进升级（提示 -> resync -> 断开重连），
      // 避免坏帧与"close -> 重连 -> 再失败"形成无限循环。
      if (this.isCurrentSession(sourceSocket, sourceGeneration, sourcePlayerId)) this.handleParseFailure(error)
    }
  }

  private resetParseFailureState(): void {
    this.parseFailureCount = 0
    this.parseFailureWindowStart = 0
    this.parseFailureResynced = false
    this.lastParseErrorKey = ''
  }

  private handleParseFailure(error: unknown): void {
    if (this.disposed || !this.socket) return
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

/** 识别 CommandResult.snapshot 是否为全量快照（snapshotForm=snapshot 时应用） */
function isPlayerSnapshotDto(value: unknown): value is PlayerSnapshotDto {
  if (value === null || typeof value !== 'object') return false
  const row = value as Record<string, unknown>
  return typeof row.playerId === 'string'
    && typeof row.displayName === 'string'
    && Array.isArray(row.sections)
    && Array.isArray(row.assets)
}

/** 识别 CommandResult.snapshot 是否为增量快照（snapshotForm=delta 时应用） */
function isPlayerSnapshotDeltaDto(value: unknown): value is PlayerSnapshotDeltaDto {
  if (value === null || typeof value !== 'object') return false
  const row = value as Record<string, unknown>
  return typeof row.playerId === 'string' && Array.isArray(row.changedSections)
}
