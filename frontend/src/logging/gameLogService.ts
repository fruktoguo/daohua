import type { LogEvent } from '../game/types'
import { GameLogRepository, type GameLogCursor, type StoredGameLog } from './logRepository'

export type LogReferenceKind = 'item' | 'character' | 'mechanic' | 'action' | 'location'

export type LogSegment =
  | { type: 'text'; value: string }
  | { type: 'reference'; kind: LogReferenceKind; id: string; label: string }

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
const REFERENCE_PATTERN = /\{\{(item|character|mechanic|action|location):([^|{}]+)\|([^{}]+)}}/g
const FULL_TIME_PATTERN = /^\[(?:(\d{4})-)?(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2})]\s*/
const SHORT_TIME_PATTERN = /^\[(\d{2}):(\d{2}):(\d{2})]\s*/

function hashText(value: string): number {
  let hash = 2_166_136_261
  for (let index = 0; index < value.length; index++) {
    hash ^= value.charCodeAt(index)
    hash = Math.imul(hash, 16_777_619)
  }
  return hash >>> 0
}

export function parseLogTemplate(template: string): readonly LogSegment[] {
  const segments: LogSegment[] = []
  let lastIndex = 0
  REFERENCE_PATTERN.lastIndex = 0
  for (const match of template.matchAll(REFERENCE_PATTERN)) {
    const index = match.index
    if (index > lastIndex) segments.push({ type: 'text', value: template.slice(lastIndex, index) })
    segments.push({
      type: 'reference',
      kind: match[1] as LogReferenceKind,
      id: match[2],
      label: match[3],
    })
    lastIndex = index + match[0].length
  }
  if (lastIndex < template.length) segments.push({ type: 'text', value: template.slice(lastIndex) })
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

function snapshotEventKey(entry: StoredGameLog): string {
  return `${Math.floor(entry.timestampMs / 1000)}\u0000${entry.template}`
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

function parseLegacyEvent(
  playerId: string,
  raw: string,
  serverTimeSeconds: number,
  position: number,
  occurrence: number,
): StoredGameLog {
  const fallback = new Date(serverTimeSeconds * 1000)
  let timestampMs = fallback.getTime()
  let template = raw
  const full = raw.match(FULL_TIME_PATTERN)
  const short = raw.match(SHORT_TIME_PATTERN)
  if (full) {
    const year = full[1] ? Number(full[1]) : fallback.getUTCFullYear()
    timestampMs = Date.UTC(year, Number(full[2]) - 1, Number(full[3]), Number(full[4]), Number(full[5]), Number(full[6]))
    template = raw.slice(full[0].length)
  } else if (short) {
    timestampMs = Date.UTC(
      fallback.getUTCFullYear(),
      fallback.getUTCMonth(),
      fallback.getUTCDate(),
      Number(short[1]),
      Number(short[2]),
      Number(short[3]),
    )
    template = raw.slice(short[0].length)
  } else if (raw.startsWith('[现在]')) {
    template = raw.slice(4).trimStart()
  }
  const hash = hashText(raw)
  return {
    id: `${playerId}:legacy:${hash}:${occurrence}`,
    playerId,
    timestampMs,
    sequence: position,
    template,
  }
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
    events: readonly string[],
    logEvents: readonly LogEvent[] | undefined,
    serverTimeSeconds: number,
  ): void {
    const structured = (logEvents ?? []).map(event => fromStructuredEvent(playerId, event))
    const represented = new Map<string, number>()
    for (const entry of structured) {
      const key = snapshotEventKey(entry)
      represented.set(key, (represented.get(key) ?? 0) + 1)
    }
    const occurrences = new Map<string, number>()
    const legacy = events
      .map((raw, position) => {
        const occurrence = occurrences.get(raw) ?? 0
        occurrences.set(raw, occurrence + 1)
        return parseLegacyEvent(playerId, raw, serverTimeSeconds, position, occurrence)
      })
      .filter(entry => {
        const key = snapshotEventKey(entry)
        const count = represented.get(key) ?? 0
        if (count === 0) return true
        represented.set(key, count - 1)
        return false
      })
    this.ingestStored(playerId, [...legacy, ...structured])
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
