import type { LogEvent } from '../game/types'

export type LogReferenceKind =
  | 'item'
  | 'character'
  | 'mechanic'
  | 'action'
  | 'location'
  | 'ability'
  | 'encounter'
  | 'enemy'

export type LogSegment =
  | { type: 'text'; value: string }
  | { type: 'reference'; kind: LogReferenceKind; id: string; label: string; amount?: number; extra?: string }

export interface StoredGameLog {
  id: string
  playerId: string
  timestampMs: number
  sequence: number
  template: string
}

export interface GameLogEntry extends StoredGameLog {
  segments: readonly LogSegment[]
  /** 连续相同模板日志的重复发生次数，默认为 1 */
  count: number
  /** 该条重复日志最新一次发生的时间戳（毫秒） */
  latestTimestampMs: number
}

/** 日志只保留当前页面内的短环形缓冲，永不写入 IndexedDB、localStorage 或服务端历史表。 */
export type LogPersistenceState = 'memory'

export interface GameLogSnapshot {
  playerId: string | null
  latest: readonly GameLogEntry[]
  revision: number
  receivedCount: number
  persistence: LogPersistenceState
}

export interface GameLogCursor {
  timestampMs: number
  sequence: number
}

const VISIBLE_LOG_LIMIT = 100
const REFERENCE_PATTERN = /\{\{(item|character|mechanic|action|location|ability|encounter|enemy):([^|{}]+)\|([^{}|]+)(?:\|([^{}]+))?}}/g
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
 */
export function collapseLogEntries(entries: readonly StoredGameLog[]): GameLogEntry[] {
  const collapsed: GameLogEntry[] = []
  for (const entry of entries) {
    const last = collapsed[collapsed.length - 1]
    if (last && last.template === entry.template) {
      last.count += 1
      last.latestTimestampMs = Math.max(last.latestTimestampMs, entry.timestampMs)
    } else {
      collapsed.push({
        ...entry,
        segments: parseLogTemplate(entry.template),
        count: 1,
        latestTimestampMs: entry.timestampMs,
      })
    }
  }
  return collapsed
}

function materialize(entry: StoredGameLog): GameLogEntry {
  return {
    ...entry,
    segments: parseLogTemplate(entry.template),
    count: 1,
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
  }
}

/**
 * 战报与传音道历全局单例服务。
 * 纯内存维护最近 100 条折叠后的日志，提供响应式订阅能力（对外暴露 subscribe 与 getSnapshot）。
 * 连续完全相同（template 一致）的日志自动合并递增 count，不挤占历史缓冲。
 */
class GameLogService {
  private readonly listeners = new Set<() => void>()
  private snapshot: GameLogSnapshot = { playerId: null, latest: [], revision: 0, receivedCount: 0, persistence: 'memory' }
  private collapsedEntries: GameLogEntry[] = []
  private seenIds = new Set<string>()

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  readonly getSnapshot = (): GameLogSnapshot => this.snapshot

  activate(playerId: string): void {
    if (this.snapshot.playerId === playerId) return
    this.collapsedEntries = []
    this.seenIds.clear()
    this.snapshot = { playerId, latest: [], revision: this.snapshot.revision + 1, receivedCount: 0, persistence: 'memory' }
    this.notify()
  }

  ingestSnapshot(playerId: string, events: readonly LogEvent[] | undefined): void {
    this.ingest(playerId, (events ?? []).map(event => eventToStored(playerId, event)))
  }

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
      if (last && last.template === entry.template) {
        last.count += 1
        last.latestTimestampMs = Math.max(last.latestTimestampMs, entry.timestampMs)
      } else {
        this.collapsedEntries.push(materialize(entry))
        if (this.collapsedEntries.length > VISIBLE_LOG_LIMIT) {
          this.collapsedEntries.shift()
        }
      }
    }

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

export const gameLogService = new GameLogService()
