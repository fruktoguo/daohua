import type { LogEvent } from '../game/types'
import { GameLogRepository, type GameLogCursor, type StoredGameLog } from './logRepository'

/**
 * 日志模板富文本实体引用种类：
 * 包含物品、角色、机制、行动、地点、功法、遭遇、敌人
 */
export type LogReferenceKind =
  | 'item'
  | 'character'
  | 'mechanic'
  | 'action'
  | 'location'
  | 'ability'
  | 'encounter'
  | 'enemy'

/**
 * 单条日志经过模板解析后拆解出的图文片段：
 * - 'text': 普通纯文本片段
 * - 'reference': 结构化实体引用片段（可渲染带品阶颜色、悬浮 Tooltip 或交互弹窗的标签）
 */
export type LogSegment =
  | { type: 'text'; value: string }
  | {
      type: 'reference'
      kind: LogReferenceKind
      id: string
      label: string
      amount?: number
      extra?: string
    }

/**
 * 带有解析后富文本片段的完整日志条目
 */
export interface GameLogEntry extends StoredGameLog {
  segments: readonly LogSegment[]
}

/**
 * 日志存储就绪状态：
 * - 'loading': 正在从 IndexedDB 异步加载历史
 * - 'ready': 本地存储已就绪
 * - 'memory': 本地存储不可用，降级为纯内存模式
 */
export type LogPersistenceState = 'loading' | 'ready' | 'memory'

/**
 * 供 UI 消费的日志服务快照对象
 */
export interface GameLogSnapshot {
  playerId: string | null
  latest: readonly GameLogEntry[]
  revision: number
  receivedCount: number
  persistence: LogPersistenceState
}

/** 内存中保留的最大可见最新日志条数 */
const VISIBLE_LOG_LIMIT = 100
/** 批量写入 IndexedDB 的防抖延迟（毫秒） */
const WRITE_DELAY_MS = 200
/** 触发立即写入的最大待存日志积压数 */
const WRITE_BATCH_LIMIT = 500
/** 通知 UI 监听器的节流防抖时间（毫秒） */
const VIEW_NOTIFY_DELAY_MS = 80
/** 匹配模板实体引用的正则表达式，如 {{item:item.spirit-herb|灵草|3}} */
const REFERENCE_PATTERN = /\{\{(item|character|mechanic|action|location|ability|encounter|enemy):([^|{}]+)\|([^{}|]+)(?:\|([^{}]+))?}}/g
/** 匹配紧随在实体后方的数量尾缀，如 " × 5" */
const TRAILING_AMOUNT_PATTERN = /^\s*[×x*]\s*(\d+)/

/**
 * 解析服务端日志模板字符串为结构化 LogSegment 数组
 * 例如输入："获得 {{item:item.spirit-herb|灵草}} × 3"
 * 输出：[{ type: 'text', value: '获得 ' }, { type: 'reference', kind: 'item', id: 'item.spirit-herb', label: '灵草', amount: 3 }]
 * 
 * @param template 服务端下发的日志模板
 * @returns 结构化图文片段数组
 */
export function parseLogTemplate(template: string): readonly LogSegment[] {
  const segments: LogSegment[] = []
  let lastIndex = 0
  REFERENCE_PATTERN.lastIndex = 0
  let match: RegExpExecArray | null
  while ((match = REFERENCE_PATTERN.exec(template)) !== null) {
    const index = match.index
    if (index > lastIndex) {
      segments.push({ type: 'text', value: template.slice(lastIndex, index) })
    }
    const kind = match[1] as LogReferenceKind
    const id = match[2]
    const label = match[3]
    const extra = match[4]
    let amount: number | undefined
    if (extra !== undefined) {
      const parsed = Number(extra.replace(/[^\d]/g, ''))
      if (!Number.isNaN(parsed) && parsed > 0) {
        amount = parsed
      }
    }
    let matchEnd = index + match[0].length

    // 如果是 item 且尚未指定 amount，检查紧随其后的文本是否是 " × 数量"
    if (kind === 'item' && amount === undefined) {
      const rest = template.slice(matchEnd)
      const trailingMatch = rest.match(TRAILING_AMOUNT_PATTERN)
      if (trailingMatch) {
        const parsed = Number(trailingMatch[1])
        if (!Number.isNaN(parsed) && parsed > 0) {
          amount = parsed
          matchEnd += trailingMatch[0].length
        }
      }
    }

    segments.push({
      type: 'reference',
      kind,
      id,
      label,
      ...(amount !== undefined ? { amount } : {}),
      ...(extra !== undefined ? { extra } : {}),
    })
    lastIndex = matchEnd
    REFERENCE_PATTERN.lastIndex = matchEnd
  }
  if (lastIndex < template.length) {
    segments.push({ type: 'text', value: template.slice(lastIndex) })
  }
  return segments.length > 0 ? segments : [{ type: 'text', value: template }]
}

/**
 * 将原始持久化日志对象转换为带 segments 片段的 GameLogEntry
 */
function materialize(entry: StoredGameLog): GameLogEntry {
  return { ...entry, segments: parseLogTemplate(entry.template) }
}

/**
 * 将服务端传输的 LogEvent 结构转换为本地存储格式 StoredGameLog
 */
function fromStructuredEvent(playerId: string, event: LogEvent): StoredGameLog {
  return {
    id: `${playerId}:server:${event.sequence}`,
    playerId,
    timestampMs: event.timestampUnixMilliseconds,
    sequence: event.sequence,
    template: event.template,
  }
}

/**
 * 排序比较两条日志的先后顺序（按时间戳、序号与唯一 ID）
 */
function compareLogs(left: StoredGameLog, right: StoredGameLog): number {
  return left.timestampMs - right.timestampMs || left.sequence - right.sequence || left.id.localeCompare(right.id)
}

/**
 * 合并去重并保留最新的 VISIBLE_LOG_LIMIT 条日志
 */
function mergeLatest(current: readonly GameLogEntry[], additions: readonly GameLogEntry[]): readonly GameLogEntry[] {
  const byId = new Map<string, GameLogEntry>()
  for (const entry of current) byId.set(entry.id, entry)
  for (const entry of additions) byId.set(entry.id, entry)
  return Array.from(byId.values()).sort(compareLogs).slice(-VISIBLE_LOG_LIMIT)
}

/**
 * 游戏日志服务类
 * 统一协调内存日志缓存、实时网络事件解析、批量防抖写入 IndexedDB 与页面卸载前持久化
 */
class GameLogService {
  private readonly repository = new GameLogRepository()
  private readonly listeners = new Set<() => void>()
  private readonly pendingWrites = new Map<string, StoredGameLog>()
  private writeTimer: number | undefined
  private notifyTimer: number | undefined
  private writeChain: Promise<void> = Promise.resolve()
  private activationToken = 0
  private storageEnabled = true
  private snapshot: GameLogSnapshot = {
    playerId: null,
    latest: [],
    revision: 0,
    receivedCount: 0,
    persistence: 'loading',
  }

  constructor() {
    // 监听窗口离开或后台切换事件，确保待写日志立即 flush 到 IndexedDB
    window.addEventListener('pagehide', () => { void this.flush() })
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') void this.flush()
    })
  }

  /**
   * 订阅日志状态变更事件（供 React 组件 useSyncExternalStore 使用）
   */
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  /**
   * 获取当前日志服务快照
   */
  readonly getSnapshot = (): GameLogSnapshot => this.snapshot

  /**
   * 激活指定玩家的日志会话并从 IndexedDB 加载历史日志
   * @param playerId 玩家 ID
   */
  async activate(playerId: string): Promise<void> {
    if (this.snapshot.playerId === playerId) return
    const token = ++this.activationToken
    this.snapshot = {
      playerId,
      latest: [],
      revision: this.snapshot.revision + 1,
      receivedCount: 0,
      persistence: this.storageEnabled ? 'loading' : 'memory',
    }
    this.notifySoon()
    if (!this.storageEnabled) return
    try {
      const stored = await this.repository.loadLatest(playerId, VISIBLE_LOG_LIMIT)
      if (token !== this.activationToken || this.snapshot.playerId !== playerId) return
      const loaded = stored.map(materialize)
      this.snapshot = {
        ...this.snapshot,
        latest: mergeLatest(loaded, this.snapshot.latest),
        revision: this.snapshot.revision + 1,
        persistence: 'ready',
      }
      this.notifySoon()
    } catch {
      this.disableStorage()
    }
  }

  /**
   * 接收全量快照中的日志事件
   */
  ingestSnapshot(
    playerId: string,
    logEvents: readonly LogEvent[] | undefined,
  ): void {
    const structured = (logEvents ?? []).map(event => fromStructuredEvent(playerId, event))
    this.ingestStored(playerId, structured)
  }

  /**
   * 接收增量同步包中的日志事件批次
   */
  ingestBatch(playerId: string, events: readonly LogEvent[]): void {
    const stored = events.map(event => fromStructuredEvent(playerId, event))
    this.ingestStored(playerId, stored)
  }

  /**
   * 分页向上加载历史日志
   */
  async loadBefore(playerId: string, cursor: GameLogCursor, limit = VISIBLE_LOG_LIMIT): Promise<readonly GameLogEntry[]> {
    if (!this.storageEnabled) return []
    try {
      return (await this.repository.loadBefore(playerId, cursor, Math.min(VISIBLE_LOG_LIMIT, limit))).map(materialize)
    } catch {
      this.disableStorage()
      return []
    }
  }

  /**
   * 立即将积压的待写日志批量提交至 IndexedDB
   */
  async flush(): Promise<void> {
    if (this.writeTimer !== undefined) {
      window.clearTimeout(this.writeTimer)
      this.writeTimer = undefined
    }
    if (!this.storageEnabled || this.pendingWrites.size === 0) return this.writeChain
    const batch = Array.from(this.pendingWrites.values())
    this.pendingWrites.clear()
    this.writeChain = this.writeChain
      .then(() => this.repository.appendBatch(batch))
      .then(() => {
        if (this.snapshot.persistence !== 'ready') {
          this.snapshot = { ...this.snapshot, persistence: 'ready' }
          this.notifySoon()
        }
      })
      .catch(() => this.disableStorage())
    return this.writeChain
  }

  /**
   * 内部统一接收结构化日志数据、更新内存快照并加入防抖写缓冲
   */
  private ingestStored(playerId: string, entries: readonly StoredGameLog[]): void {
    if (entries.length === 0) return
    if (this.snapshot.playerId !== playerId) void this.activate(playerId)
    const materialized = entries.map(materialize)
    if (this.snapshot.playerId === playerId) {
      const previousIds = new Set(this.snapshot.latest.map(entry => entry.id))
      const received = materialized.reduce((count, entry) => count + (previousIds.has(entry.id) ? 0 : 1), 0)
      this.snapshot = {
        ...this.snapshot,
        latest: mergeLatest(this.snapshot.latest, materialized),
        revision: this.snapshot.revision + 1,
        receivedCount: this.snapshot.receivedCount + received,
      }
      this.notifySoon()
    }
    if (!this.storageEnabled) return
    for (const entry of entries) this.pendingWrites.set(entry.id, entry)
    if (this.pendingWrites.size >= WRITE_BATCH_LIMIT) void this.flush()
    else if (this.writeTimer === undefined) {
      this.writeTimer = window.setTimeout(() => {
        this.writeTimer = undefined
        void this.flush()
      }, WRITE_DELAY_MS)
    }
  }

  /**
   * 防抖节流通知 UI 观察者更新
   */
  private notifySoon(): void {
    if (this.notifyTimer !== undefined) return
    this.notifyTimer = window.setTimeout(() => {
      this.notifyTimer = undefined
      for (const listener of this.listeners) listener()
    }, VIEW_NOTIFY_DELAY_MS)
  }

  /**
   * 当 IndexedDB 出错时安全降级到纯内存模式
   */
  private disableStorage(): void {
    this.storageEnabled = false
    this.pendingWrites.clear()
    if (this.writeTimer !== undefined) window.clearTimeout(this.writeTimer)
    this.writeTimer = undefined
    if (this.snapshot.persistence !== 'memory') {
      this.snapshot = { ...this.snapshot, persistence: 'memory' }
      this.notifySoon()
    }
  }
}

/** 全局单例游戏日志服务 */
export const gameLogService = new GameLogService()
