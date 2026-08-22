import type { LogEvent } from '../game/types'
import { GameLogRepository, type GameLogCursor, type StoredGameLog } from './logRepository'

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
  | {
      type: 'reference'
      kind: LogReferenceKind
      id: string
      label: string
      amount?: number
      extra?: string
    }

export interface GameLogEntry extends StoredGameLog {
  segments: readonly LogSegment[]
}

export type LogPersistenceState = 'loading' | 'ready' | 'memory'

export interface GameLogSnapshot {
  playerId: string | null
  latest: readonly GameLogEntry[]
  revision: number
  receivedCount: number
  persistence: LogPersistenceState
}

const VISIBLE_LOG_LIMIT = 100
const WRITE_DELAY_MS = 200
const WRITE_BATCH_LIMIT = 500
const VIEW_NOTIFY_DELAY_MS = 80
const REFERENCE_PATTERN = /\{\{(item|character|mechanic|action|location|ability|encounter|enemy):([^|{}]+)\|([^{}|]+)(?:\|([^{}]+))?}}/g
const TRAILING_AMOUNT_PATTERN = /^\s*[×x*]\s*(\d+)/

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

function materialize(entry: StoredGameLog): GameLogEntry {
  return { ...entry, segments: parseLogTemplate(entry.template) }
}

function fromStructuredEvent(playerId: string, event: LogEvent): StoredGameLog {
  return {
    id: `${playerId}:server:${event.sequence}`,
    playerId,
    timestampMs: event.timestampUnixMilliseconds,
    sequence: event.sequence,
    template: event.template,
  }
}

function compareLogs(left: StoredGameLog, right: StoredGameLog): number {
  return left.timestampMs - right.timestampMs || left.sequence - right.sequence || left.id.localeCompare(right.id)
}

function mergeLatest(current: readonly GameLogEntry[], additions: readonly GameLogEntry[]): readonly GameLogEntry[] {
  const byId = new Map<string, GameLogEntry>()
  for (const entry of current) byId.set(entry.id, entry)
  for (const entry of additions) byId.set(entry.id, entry)
  return Array.from(byId.values()).sort(compareLogs).slice(-VISIBLE_LOG_LIMIT)
}

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
    window.addEventListener('pagehide', () => { void this.flush() })
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') void this.flush()
    })
  }

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  readonly getSnapshot = (): GameLogSnapshot => this.snapshot

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

  ingestSnapshot(
    playerId: string,
    logEvents: readonly LogEvent[] | undefined,
  ): void {
    const structured = (logEvents ?? []).map(event => fromStructuredEvent(playerId, event))
    this.ingestStored(playerId, structured)
  }

  ingestBatch(playerId: string, events: readonly LogEvent[]): void {
    const stored = events.map(event => fromStructuredEvent(playerId, event))
    this.ingestStored(playerId, stored)
  }

  async loadBefore(playerId: string, cursor: GameLogCursor, limit = VISIBLE_LOG_LIMIT): Promise<readonly GameLogEntry[]> {
    if (!this.storageEnabled) return []
    try {
      return (await this.repository.loadBefore(playerId, cursor, Math.min(VISIBLE_LOG_LIMIT, limit))).map(materialize)
    } catch {
      this.disableStorage()
      return []
    }
  }

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

  private notifySoon(): void {
    if (this.notifyTimer !== undefined) return
    this.notifyTimer = window.setTimeout(() => {
      this.notifyTimer = undefined
      for (const listener of this.listeners) listener()
    }, VIEW_NOTIFY_DELAY_MS)
  }

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

export const gameLogService = new GameLogService()
