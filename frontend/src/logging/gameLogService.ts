import type { LogEvent } from '../game/types'

/** 日志分组频道（与服务端 Protocol.LogChannels 严格一致） */
export type LogChannel = 'system' | 'combat'

/** 日志模板引用标记的实体类别（{{kind:id|label}} 中的 kind） */
export type LogReferenceKind =
  | 'item'
  | 'character'
  | 'mechanic'
  | 'action'
  | 'location'
  | 'ability'
  | 'encounter'
  | 'enemy'

/** 日志分词：纯文本段或带交互悬浮卡片能力的实体引用段 */
export type LogSegment =
  | { type: 'text'; value: string }
  | { type: 'reference'; kind: LogReferenceKind; id: string; label: string; amount?: number; extra?: string }

/** 服务端日志事件落入内存缓冲的归一化存储形态 */
export interface StoredGameLog {
  /** 去重 ID（playerId + sequence 组合） */
  id: string
  /** 所属玩家 ID */
  playerId: string
  /** 发生时刻（Unix 毫秒） */
  timestampMs: number
  /** 服务端日志序号 */
  sequence: number
  /** 语义模板文本（含 {{kind:id|label}} 引用标记） */
  template: string
  /** 日志分组频道（system/combat） */
  channel: LogChannel
  /** 服务端侧已折叠的重复次数 */
  count?: number
}

export interface GameLogEntry extends StoredGameLog {
  /** 模板解析后的富文本分词（渲染期直接消费） */
  segments: readonly LogSegment[]
  /** 连续相同模板日志的重复发生次数，默认为 1 */
  count: number
  /** 该条重复日志最新一次发生的时间戳（毫秒） */
  latestTimestampMs: number
}

/** 日志服务对外只读快照（配合 subscribe 的 revision 变化驱动 UI 刷新） */
export interface GameLogSnapshot {
  /** 当前激活玩家 ID */
  playerId: string | null
  /** 折叠后的最新日志列表（按时间正序，上限 VISIBLE_LOG_LIMIT） */
  latest: readonly GameLogEntry[]
  /** 快照版本号，每次写入递增 */
  revision: number
  /** 累计接收（未去重前）日志条数 */
  receivedCount: number
}

/** 历史翻页游标（当前无历史接口，保留契约位） */
export interface GameLogCursor {
  timestampMs: number
  sequence: number
}

/** 内存环形缓冲上限：仅保留最近 200 条折叠后日志 */
const VISIBLE_LOG_LIMIT = 200
/** 模板引用标记语法：{{kind:id|label}} 或 {{kind:id|label|extra}}（extra 常为数量） */
const REFERENCE_PATTERN = /\{\{(item|character|mechanic|action|location|ability|encounter|enemy):([^|{}]+)\|([^{}|]+)(?:\|([^{}]+))?}}/g
/** 引用标记后紧随的「×N」数量后缀（服务端旧模板未内嵌数量时的兼容形态） */
const TRAILING_AMOUNT_PATTERN = /^\s*[×x*]\s*(\d+)/

/**
 * 战报与传音日志模板分词解析器。
 * 将服务端下发的语义模板字符串（如 `获得 {{item:ningxue-grass|凝血草|3}}`）
 * 解析为富文本分词列表（LogSegment），供 UI 渲染带交互悬浮卡片的道具标签或地点链接。
 */
export function parseLogTemplate(template: string): readonly LogSegment[] {
  const segments: LogSegment[] = []
  let lastIndex = 0
  REFERENCE_PATTERN.lastIndex = 0
  let match: RegExpExecArray | null
  while ((match = REFERENCE_PATTERN.exec(template)) !== null) {
    if (match.index > lastIndex) segments.push({ type: 'text', value: template.slice(lastIndex, match.index) })
    const kind = match[1] as LogReferenceKind
    const id = match[2]
    const label = match[3]
    const extra = match[4]
    let amount: number | undefined
    if (extra !== undefined) {
      const parsed = Number(extra.replace(/[^\d]/g, ''))
      if (Number.isFinite(parsed) && parsed > 0) amount = parsed
    }
    let end = match.index + match[0].length
    if (kind === 'item' && amount === undefined) {
      const trailing = template.slice(end).match(TRAILING_AMOUNT_PATTERN)
      if (trailing) {
        const parsed = Number(trailing[1])
        if (Number.isFinite(parsed) && parsed > 0) {
          amount = parsed
          end += trailing[0].length
        }
      }
    }
    segments.push({ type: 'reference', kind, id, label, ...(amount === undefined ? {} : { amount }), ...(extra === undefined ? {} : { extra }) })
    lastIndex = end
    REFERENCE_PATTERN.lastIndex = end
  }
  if (lastIndex < template.length) segments.push({ type: 'text', value: template.slice(lastIndex) })
  return segments.length > 0 ? segments : [{ type: 'text', value: template }]
}

/**
 * 将连续相同语义模板（template）的日志合并折叠。
 * 紧邻且 template 完全相同的日志将合并为单条 GameLogEntry，
 * 累计 count 并在 latestTimestampMs 记录最近触发时间戳。
 * 无论合并多少条，在最大历史记录中均只占 1 个槽位。
 */
export function collapseLogEntries(entries: readonly StoredGameLog[]): GameLogEntry[] {
  const collapsed: GameLogEntry[] = []
  for (const entry of entries) {
    const entryCount = typeof entry.count === 'number' && entry.count > 0 ? entry.count : 1
    const last = collapsed[collapsed.length - 1]
    if (last && last.template === entry.template && last.channel === entry.channel) {
      last.count += entryCount
      last.latestTimestampMs = Math.max(last.latestTimestampMs, entry.timestampMs)
    } else {
      collapsed.push({
        ...entry,
        segments: parseLogTemplate(entry.template),
        count: entryCount,
        latestTimestampMs: entry.timestampMs,
      })
    }
  }
  return collapsed
}

function materialize(entry: StoredGameLog): GameLogEntry {
  const count = typeof entry.count === 'number' && entry.count > 0 ? entry.count : 1
  return {
    ...entry,
    segments: parseLogTemplate(entry.template),
    count,
    latestTimestampMs: entry.timestampMs,
  }
}

function compare(left: StoredGameLog, right: StoredGameLog): number {
  return left.timestampMs - right.timestampMs || left.sequence - right.sequence || left.id.localeCompare(right.id)
}

function eventToStored(playerId: string, event: LogEvent): StoredGameLog {
  return {
    id: `${playerId}:memory:${event.sequence}`,
    playerId,
    timestampMs: event.timestampUnixMilliseconds,
    sequence: event.sequence,
    template: event.template,
    channel: event.channel === 'combat' ? 'combat' : 'system',
    count: event.count ?? 1,
  }
}

/**
 * 按日志分组频道筛选折叠后的日志条目。
 * 综合分组不消费日志事件（只合并系统日志与好友/队伍传音），因此这里只服务「系统」与「战斗」两个分组。
 */
export function filterLogEntries(entries: readonly GameLogEntry[], channel: LogChannel): GameLogEntry[] {
  return entries.filter(entry => entry.channel === channel)
}

/**
 * 战报与传音道历全局单例服务。
 * 纯内存维护最近 200 条折叠后的日志，提供响应式订阅能力（对外暴露 subscribe 与 getSnapshot）。
 * 连续完全相同（template 一致）的日志自动合并递增 count，不挤占历史缓冲。
 */
class GameLogService {
  private readonly listeners = new Set<() => void>()
  private snapshot: GameLogSnapshot = { playerId: null, latest: [], revision: 0, receivedCount: 0 }
  private collapsedEntries: GameLogEntry[] = []
  private seenIds = new Set<string>()

  /** 订阅快照变更；返回取消订阅函数 */
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  /** 读取当前快照（不可变引用，配合 revision 判断变更） */
  readonly getSnapshot = (): GameLogSnapshot => this.snapshot

  /** 切换激活玩家：清空缓冲与去重集合；同一玩家重复调用为空操作 */
  activate(playerId: string): void {
    if (this.snapshot.playerId === playerId) return
    this.collapsedEntries = []
    this.seenIds.clear()
    this.snapshot = { playerId, latest: [], revision: this.snapshot.revision + 1, receivedCount: 0 }
    this.notify()
  }

  /** 摄入快照携带的全量日志（events 缺省视为空） */
  ingestSnapshot(playerId: string, events: readonly LogEvent[] | undefined): void {
    this.ingest(playerId, (events ?? []).map(event => eventToStored(playerId, event)))
  }

  /** 摄入增量日志批次（LogBatch 信封路径） */
  ingestBatch(playerId: string, events: readonly LogEvent[]): void {
    this.ingest(playerId, events.map(event => eventToStored(playerId, event)))
  }

  /** 历史消息不落盘；该接口固定返回空集，保持日志面板无网络历史依赖。 */
  loadBefore(_playerId: string, _cursor: GameLogCursor, _limit = VISIBLE_LOG_LIMIT): Promise<readonly GameLogEntry[]> {
    return Promise.resolve([])
  }

  flush(): Promise<void> {
    return Promise.resolve()
  }

  private ingest(playerId: string, additions: readonly StoredGameLog[]): void {
    if (additions.length === 0) return
    if (this.snapshot.playerId !== playerId) this.activate(playerId)

    const sortedAdditions = additions.slice().sort(compare)
    let received = 0

    for (const entry of sortedAdditions) {
      if (this.seenIds.has(entry.id)) continue
      this.seenIds.add(entry.id)
      received += 1

      const last = this.collapsedEntries[this.collapsedEntries.length - 1]
      if (last && last.template === entry.template && last.channel === entry.channel) {
        last.count += 1
        last.latestTimestampMs = Math.max(last.latestTimestampMs, entry.timestampMs)
      } else {
        this.collapsedEntries.push(materialize(entry))
        if (this.collapsedEntries.length > VISIBLE_LOG_LIMIT) {
          this.collapsedEntries.shift()
        }
      }
    }

    // 去重集合无界增长时保留最近一半，避免长跑会话内存膨胀（牺牲极旧 ID 的重放判定）
    if (this.seenIds.size > 2000) {
      const idsArray = Array.from(this.seenIds)
      this.seenIds = new Set(idsArray.slice(-1000))
    }

    this.snapshot = {
      ...this.snapshot,
      latest: this.collapsedEntries.slice(),
      revision: this.snapshot.revision + 1,
      receivedCount: this.snapshot.receivedCount + received,
    }
    this.notify()
  }

  private notify(): void {
    for (const listener of this.listeners) listener()
  }
}

/** 全局日志服务单例（供 gameClient 写入、传音面板订阅消费） */
export const gameLogService = new GameLogService()
