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
  | 'trait'
  | 'building'
  | 'buff'

/** 日志分词：纯文本段或带交互悬浮卡片能力的实体引用段 */
export type LogSegment =
  | { type: 'text'; value: string }
  | { type: 'reference'; kind: LogReferenceKind; id: string; label: string; amount?: number; extra?: string }

/** 服务端日志事件落入内存缓冲的归一化存储形态 */
export interface StoredGameLog {
  /** 唯一 ID（playerId + streamId + sequence 组合） */
  id: string
  /** 所属玩家 ID */
  playerId: string
  /** 当前日志流代际；保留历史时避免新旧缓冲的低序号条目键碰撞 */
  streamId?: string | null
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

/** 渲染就绪的游戏日志条目：原始模板已解析为富文本分词并完成相邻重复折叠 */
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
const REFERENCE_PATTERN = /\{\{(item|character|mechanic|action|location|ability|encounter|enemy|trait|building|buff):([^|{}]+)\|([^{}|]*)(?:\|([^{}]+))?}}/g
/** 引用标记后紧随的「×N」数量后缀（服务端旧模板未内嵌数量时的兼容形态） */
const TRAILING_AMOUNT_PATTERN = /^\s*[×x*]\s*(\d+)/

/**
 * 战报与传音日志模板分词解析器。
 * 将服务端下发的语义模板字符串（如 `获得 {{item:ningxue-grass|凝气草|3}}`）
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
 * 按 \n 把已解析的日志分词切成逐行分词组。
 * 服务端多目标施放合并行以 \n 分隔各目标子句（首行=施放前缀行，后续每行一条子句）；
 * text 分段内的 \n 在切分处一分为二，引用分段天然不跨界。无 \n 时返回 null（调用方走平铺渲染）。
 */
export function splitLogSegmentsByLines(segments: readonly LogSegment[]): LogSegment[][] | null {
  let sawNewline = false
  const lines: LogSegment[][] = []
  let current: LogSegment[] = []
  for (const segment of segments) {
    if (segment.type === 'text' && segment.value.includes('\n')) {
      const parts = segment.value.split('\n')
      for (let i = 0; i < parts.length; i++) {
        if (i > 0) {
          lines.push(current)
          current = []
        }
        if (parts[i].length > 0) current.push({ type: 'text', value: parts[i] })
      }
      sawNewline = true
    } else {
      current.push(segment)
    }
  }
  if (!sawNewline) return null
  lines.push(current)
  return lines
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
    if (last && last.template === entry.template && last.channel === entry.channel && last.streamId === entry.streamId) {
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
    id: `${playerId}:memory:${event.streamId ?? ''}:${event.sequence}`,
    playerId,
    streamId: event.streamId,
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
  private readonly rawBatchListeners = new Set<(playerId: string, events: readonly LogEvent[]) => void>()
  private snapshot: GameLogSnapshot = { playerId: null, latest: [], revision: 0, receivedCount: 0 }
  private collapsedEntries: GameLogEntry[] = []
  /** 已摄入的实际日志序号闭区间；相邻区间合并，避免逐条展开服务端累计折叠项。 */
  private seenSequenceRanges: { start: number; end: number }[] = []
  private seenSequenceFloor = 0
  private currentStreamId: string | null | undefined

  /** 订阅快照变更；返回取消订阅函数 */
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  /**
   * 订阅服务端增量日志原始批次（LogBatch 信封入口）：
   * 在去重后、展示过滤/折叠前触发，避免重连累计回放再次触发已经播放的表现。
   */
  readonly subscribeRawBatch = (listener: (playerId: string, events: readonly LogEvent[]) => void): (() => void) => {
    this.rawBatchListeners.add(listener)
    return () => this.rawBatchListeners.delete(listener)
  }

  /** 读取当前快照（不可变引用，配合 revision 判断变更） */
  readonly getSnapshot = (): GameLogSnapshot => this.snapshot

  /** 切换激活玩家：清空缓冲与去重集合；同一玩家重复调用为空操作 */
  activate(playerId: string): void {
    if (this.snapshot.playerId === playerId) return
    this.collapsedEntries = []
    this.seenSequenceRanges = []
    this.seenSequenceFloor = 0
    this.currentStreamId = undefined
    this.snapshot = { playerId, latest: [], revision: this.snapshot.revision + 1, receivedCount: 0 }
    this.notify()
  }

  /** 摄入快照携带的全量日志（events 缺省视为空） */
  ingestSnapshot(playerId: string, events: readonly LogEvent[] | undefined): void {
    this.ingest(playerId, this.selectNewEvents(playerId, events ?? []).map(event => eventToStored(playerId, event)))
  }

  /** 摄入增量日志批次，返回真正新增的次数，供未读角标与表现消费同一去重结果。 */
  ingestBatch(playerId: string, events: readonly LogEvent[]): readonly LogEvent[] {
    const additions = this.selectNewEvents(playerId, events)
    if (additions.length > 0 && this.rawBatchListeners.size > 0) {
      for (const listener of this.rawBatchListeners) listener(playerId, additions)
    }
    this.ingest(playerId, additions.map(event => eventToStored(playerId, event)))
    return additions
  }

  /** 历史消息不落盘；该接口固定返回空集，保持日志面板无网络历史依赖。 */
  loadBefore(_playerId: string, _cursor: GameLogCursor, _limit = VISIBLE_LOG_LIMIT): Promise<readonly GameLogEntry[]> {
    return Promise.resolve([])
  }

  flush(): Promise<void> {
    return Promise.resolve()
  }

  /** Count 覆盖末序号之前的连续实际事件；累计回放只领取尚未摄入的区间。 */
  private selectNewEvents(playerId: string, events: readonly LogEvent[]): LogEvent[] {
    if (events.length === 0) return []
    if (this.snapshot.playerId !== playerId) this.activate(playerId)
    const additions: LogEvent[] = []
    for (const event of events) {
      if (event.streamId !== this.currentStreamId) {
        this.currentStreamId = event.streamId
        this.seenSequenceRanges = []
        this.seenSequenceFloor = 0
      }
      const count = typeof event.count === 'number' && event.count > 0 ? event.count : 1
      const start = Math.max(this.seenSequenceFloor + 1, event.sequence - count + 1)
      const end = event.sequence
      if (start > end) continue
      let unseenCount = end - start + 1
      let mergedStart = start
      let mergedEnd = end
      let first = 0
      while (first < this.seenSequenceRanges.length && this.seenSequenceRanges[first].end < start - 1) first += 1
      let after = first
      while (after < this.seenSequenceRanges.length && this.seenSequenceRanges[after].start <= mergedEnd + 1) {
        const seen = this.seenSequenceRanges[after]
        unseenCount -= Math.max(0, Math.min(end, seen.end) - Math.max(start, seen.start) + 1)
        mergedStart = Math.min(mergedStart, seen.start)
        mergedEnd = Math.max(mergedEnd, seen.end)
        after += 1
      }
      this.seenSequenceRanges.splice(first, after - first, { start: mergedStart, end: mergedEnd })
      if (unseenCount > 0) additions.push(unseenCount === count ? event : { ...event, count: unseenCount })
    }
    // 与原先 2000 项去重窗口保持同量级上界；被淘汰的极旧区间不再重新计入显示。
    if (this.seenSequenceRanges.length > 2000) {
      const removed = this.seenSequenceRanges.splice(0, this.seenSequenceRanges.length - 1000)
      this.seenSequenceFloor = removed[removed.length - 1].end
    }
    return additions
  }

  private ingest(playerId: string, additions: readonly StoredGameLog[]): void {
    if (additions.length === 0) return
    if (this.snapshot.playerId !== playerId) this.activate(playerId)

    const sortedAdditions = additions.slice().sort(compare)
    let received = 0

    for (const entry of sortedAdditions) {
      // 高频消耗品自动使用与到期耗尽不入日志展示
      if (entry.template.startsWith('自动使用 ') || entry.template.endsWith('的持续时间已耗尽。')) continue
      received += 1

      const incomingCount = typeof entry.count === 'number' && entry.count > 0 ? entry.count : 1
      const last = this.collapsedEntries[this.collapsedEntries.length - 1]
      if (last && last.template === entry.template && last.channel === entry.channel && last.streamId === entry.streamId) {
        this.collapsedEntries[this.collapsedEntries.length - 1] = {
          ...last,
          count: last.count + incomingCount,
          latestTimestampMs: Math.max(last.latestTimestampMs, entry.timestampMs),
        }
      } else {
        this.collapsedEntries.push(materialize(entry))
        if (this.collapsedEntries.length > VISIBLE_LOG_LIMIT) {
          this.collapsedEntries.shift()
        }
      }
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
