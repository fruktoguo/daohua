import { getQqEmojiUrls, QQ_EMOJI_LIST } from './qqEmojis.ts'
import {
  computeBlobSha256,
  computeTextSha256,
  detectRasterImageMime,
  fetchImageBlobForCaching,
  readChatImageBody,
  uploadChatImage,
} from './chatImageUploadService.ts'
import { getApiUrl } from '../network/apiUrl.ts'
import { getSessionGeneration } from '../network/sessionState.ts'

/**
 * 传音聊天内置小黄脸表情与自定义表情包管理、多端同步与本地存储服务
 *
 * 核心职责：
 * 1. 内置 QQ 动态表情频次统计与自排序（高频在先）及空闲预加载；
 * 2. 自定义表情包收藏上限 100 个管理（按角色隔离本地缓存 + IndexedDB v2 二进制强落盘）；
 * 3. 多端双向 LWW + 软删除墓碑同步（/api/v1/chat/stickers*），支持批量勾选删除；
 * 4. 发送/插入前云端存活性精准探测，过期时自动从本地 IndexedDB 重新上传换链并同步各端，
 *    无本地原图端标记待补传，持有原图设备上线后自动静默修复。
 */

export const MAX_CUSTOM_STICKERS = 100
export const MAX_STICKER_URL_LENGTH = 200

const DB_NAME = 'daohua_chat_stickers'
const DB_VERSION = 2
const STORE_IMAGES = 'images'
const STORE_BLOBS = 'blobs'

const LEGACY_KEY_EMOJI_FREQ = 'daohua_chat_emoji_freq'
const LEGACY_KEY_CUSTOM_STICKERS = 'daohua_chat_custom_stickers'
const STORAGE_PREFIX_EMOJI_FREQ = 'daohua_chat_emoji_freq_'
const STORAGE_PREFIX_STICKERS = 'daohua_chat_custom_stickers_'
const STORAGE_PREFIX_TOMBSTONES = 'daohua_chat_sticker_tombstones_'
const STORAGE_PREFIX_LEGACY_CLAIMED = 'daohua_chat_stickers_legacy_claimed_'

const ALIVE_CACHE_TTL_MS = 45_000
const SYNC_DEBOUNCE_MS = 1200

/** 默认精选内置 QQ 风格小黄脸表情名称列表 */
export const DEFAULT_EMOJIS: readonly string[] = QQ_EMOJI_LIST.map(e => e.name)

/** 自定义表情包条目定义 */
export interface CustomStickerItem {
  stickerId: string
  url: string
  sha256?: string | null
  mime?: string
  count: number
  lastUsedAt: number
  addedAt: number
  updatedAt: number
  needsReupload?: boolean
}

export interface StickerTombstone {
  stickerId: string
  deletedAt: number
}

export interface AddStickerResult {
  ok: boolean
  reason?: 'invalid' | 'duplicate' | 'limit'
  item?: CustomStickerItem
}

export interface EnsureStickerReadyResult {
  url: string
  healed: boolean
  missingLocalBlob: boolean
  stickerId?: string
}

export interface PrepareDraftStickersResult {
  ok: boolean
  body: string
  healedCount: number
  missingLocalBlob?: boolean
}

interface EmojiFrequencyRecord {
  count: number
  lastUsedAt: number
}

interface ServerStickerDto {
  stickerId: string
  url: string
  sha256?: string | null
  mime?: string
  useCount: number
  lastUsedAt: number
  addedAt: number
  updatedAt: number
  needsReupload?: boolean
}

interface ServerStickerSyncResponse {
  maxStickers: number
  serverTimeMs: number
  stickers: ServerStickerDto[]
  tombstones: StickerTombstone[]
}

/**
 * 同步生成 URL 临时 ID（用于在尚未完成二进制 SHA-256 计算时的瞬时唯一标识；
 * 一旦本地完成 Blob 缓存，会自动升级为规范的 64 位 SHA-256 ID）。
 */
function buildTempUrlStickerIdSync(url: string): string {
  let h1 = 0x811c9dc5
  let h2 = 0x9e3779b9
  for (let i = 0; i < url.length; i++) {
    const code = url.charCodeAt(i)
    h1 ^= code
    h1 = Math.imul(h1, 0x01000193)
    h2 ^= code + ((i + 1) << 3)
    h2 = Math.imul(h2, 0x85ebca6b)
  }
  const part1 = (h1 >>> 0).toString(16).padStart(8, '0')
  const part2 = (h2 >>> 0).toString(16).padStart(8, '0')
  return `url_${part1}${part2}${url.length.toString(16).padStart(4, '0')}`
}

function normalizeStickerRecord(raw: Partial<CustomStickerItem> & { url: string }): CustomStickerItem {
  const url = raw.url.trim()
  const sha256 = raw.sha256 && /^[0-9a-f]{64}$/i.test(raw.sha256) ? raw.sha256.toLowerCase() : null
  const stickerId = (raw.stickerId && /^[a-zA-Z0-9_-]{1,64}$/.test(raw.stickerId))
    ? raw.stickerId
    : (sha256 ?? buildTempUrlStickerIdSync(url))
  const addedAt = typeof raw.addedAt === 'number' && raw.addedAt > 0 ? raw.addedAt : Date.now()
  const lastUsedAt = typeof raw.lastUsedAt === 'number' && raw.lastUsedAt > 0 ? raw.lastUsedAt : 0
  const updatedAt = typeof raw.updatedAt === 'number' && raw.updatedAt > 0 ? raw.updatedAt : Math.max(addedAt, lastUsedAt)
  return {
    stickerId,
    url,
    sha256,
    mime: typeof raw.mime === 'string' ? raw.mime : '',
    count: typeof raw.count === 'number' && raw.count >= 0 ? Math.floor(raw.count) : 0,
    lastUsedAt,
    addedAt,
    updatedAt,
    needsReupload: Boolean(raw.needsReupload),
  }
}

const MEMORY_IMAGE_BUDGET = 24 * 1024 * 1024
const MEMORY_IMAGE_COUNT = 256

interface MemoryImage {
  key: string
  sha256: string | null
  bytes: number
  leases: number
  retired: boolean
}

export interface ChatImageLease {
  src: string
  release: () => void
}

class ChatStickerService {
  private dbPromise: Promise<IDBDatabase | null> | null = null
  private generation = 0
  private accountGeneration = getSessionGeneration()
  // ObjectURL 顺序即 LRU，已借用的图（含旧会话尚未卸载的节点）也计入硬上限。
  private readonly memoryImages = new Map<string, MemoryImage>()
  private memoryBytes = 0
  private readonly memoryBlobUrlMap = new Map<string, string>()
  private readonly memorySha256UrlMap = new Map<string, string>()
  private readonly inFlightLoads = new Map<string, Promise<string>>()
  private readonly inFlightHeals = new Map<string, Promise<EnsureStickerReadyResult>>()
  private readonly aliveProbeCache = new Map<string, number>()
  private readonly listeners = new Set<() => void>()

  private activePlayerId: string | null = null
  private emojiFreqCache: Record<string, EmojiFrequencyRecord> | null = null
  private stickerListCache: CustomStickerItem[] | null = null
  private tombstoneCache: StickerTombstone[] | null = null
  private syncTimer: ReturnType<typeof setTimeout> | null = null
  private syncInFlight: Promise<void> | null = null
  private backgroundPassInFlight: Promise<void> | null = null
  private pendingReplacedIds = new Map<string, string>()
  private pendingBrokenReports = new Map<string, string>()
  private lastSyncError: 'limit' | null = null
  private revision = 0

  constructor() {
    this.initDatabase()
  }

  /** 初始化 IndexedDB（v2 新增 blobs 表按 SHA-256 去重保存原始二进制） */
  private initDatabase(): Promise<IDBDatabase | null> {
    if (typeof window === 'undefined' || !window.indexedDB) {
      return Promise.resolve(null)
    }
    if (!this.dbPromise) {
      this.dbPromise = new Promise(resolve => {
        try {
          const req = window.indexedDB.open(DB_NAME, DB_VERSION)
          req.onupgradeneeded = () => {
            const db = req.result
            if (!db.objectStoreNames.contains(STORE_IMAGES)) {
              db.createObjectStore(STORE_IMAGES, { keyPath: 'url' })
            }
            if (!db.objectStoreNames.contains(STORE_BLOBS)) {
              db.createObjectStore(STORE_BLOBS, { keyPath: 'sha256' })
            }
          }
          req.onsuccess = () => resolve(req.result)
          req.onerror = () => {
            resolve(null)
          }
        } catch {
          resolve(null)
        }
      })
    }
    return this.dbPromise
  }

  /** 订阅表情/表情包列表变更 */
  subscribe(listener: () => void): () => void {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  getRevision(): number {
    return this.revision
  }

  private notify(): void {
    this.revision += 1
    for (const listener of this.listeners) {
      try {
        listener()
      } catch (err) {
        console.error('[ChatStickerService] listener error', err)
      }
    }
  }

  getGeneration(): number {
    return this.generation
  }

  /** 绑定当前登录角色，按 playerId 隔离本地列表并触发多端云端同步 */
  setActivePlayer(playerId: string | null): void {
    const normalized = playerId?.trim() || null
    const accountGeneration = getSessionGeneration()
    if (normalized === this.activePlayerId && accountGeneration === this.accountGeneration) return
    this.accountGeneration = accountGeneration
    this.invalidatePendingWork()
    this.activePlayerId = normalized
    this.emojiFreqCache = null
    this.stickerListCache = null
    this.tombstoneCache = null
    this.pendingReplacedIds.clear()
    this.pendingBrokenReports.clear()
    this.lastSyncError = null

    if (this.syncTimer) {
      clearTimeout(this.syncTimer)
      this.syncTimer = null
    }

    if (normalized) {
      this.migrateLegacyStorageIfNeeded(normalized)
      void this.syncWithServer()
    }
    this.notify()
  }

  getLastSyncError(): 'limit' | null {
    return this.lastSyncError
  }

  clearLastSyncError(): void {
    this.lastSyncError = null
    this.notify()
  }

  private getEmojiStorageKey(): string {
    return this.activePlayerId ? `${STORAGE_PREFIX_EMOJI_FREQ}${this.activePlayerId}` : LEGACY_KEY_EMOJI_FREQ
  }

  private getStickerStorageKey(): string {
    return this.activePlayerId ? `${STORAGE_PREFIX_STICKERS}${this.activePlayerId}` : LEGACY_KEY_CUSTOM_STICKERS
  }

  private getTombstoneStorageKey(): string {
    return this.activePlayerId ? `${STORAGE_PREFIX_TOMBSTONES}${this.activePlayerId}` : `${STORAGE_PREFIX_TOMBSTONES}guest`
  }

  /** 将早期未分账号的旧本地表情包一次性迁移至当前首个登录角色 */
  private migrateLegacyStorageIfNeeded(playerId: string): void {
    if (typeof localStorage === 'undefined') return
    try {
      const claimedKey = `${STORAGE_PREFIX_LEGACY_CLAIMED}${playerId}`
      if (localStorage.getItem(claimedKey)) return

      const legacyStickersRaw = localStorage.getItem(LEGACY_KEY_CUSTOM_STICKERS)
      if (legacyStickersRaw) {
        const parsed = JSON.parse(legacyStickersRaw) as Array<Partial<CustomStickerItem> & { url?: string }>
        if (Array.isArray(parsed) && parsed.length > 0) {
          const existing = this.loadStickers()
          const existingUrls = new Set(existing.map(item => item.url))
          const merged = [...existing]
          for (const raw of parsed) {
            if (!raw?.url || typeof raw.url !== 'string') continue
            const item = normalizeStickerRecord({ ...raw, url: raw.url })
            if (!existingUrls.has(item.url) && merged.length < MAX_CUSTOM_STICKERS) {
              merged.push(item)
              existingUrls.add(item.url)
            }
          }
          this.saveStickers(merged)
        }
        localStorage.removeItem(LEGACY_KEY_CUSTOM_STICKERS)
      }

      const legacyEmojiRaw = localStorage.getItem(LEGACY_KEY_EMOJI_FREQ)
      if (legacyEmojiRaw) {
        const playerEmojiKey = `${STORAGE_PREFIX_EMOJI_FREQ}${playerId}`
        if (!localStorage.getItem(playerEmojiKey)) {
          localStorage.setItem(playerEmojiKey, legacyEmojiRaw)
        }
        localStorage.removeItem(LEGACY_KEY_EMOJI_FREQ)
      }

      localStorage.setItem(claimedKey, String(Date.now()))
    } catch {
      // 忽略损坏的旧存储
    }
  }

  // ==========================================
  // Emoji 频次与排序
  // ==========================================

  private loadEmojiFrequencies(): Record<string, EmojiFrequencyRecord> {
    if (this.emojiFreqCache) return this.emojiFreqCache
    if (typeof localStorage === 'undefined') {
      this.emojiFreqCache = {}
      return this.emojiFreqCache
    }
    try {
      const raw = localStorage.getItem(this.getEmojiStorageKey())
      this.emojiFreqCache = raw ? JSON.parse(raw) : {}
    } catch {
      this.emojiFreqCache = {}
    }
    return this.emojiFreqCache ?? {}
  }

  private saveEmojiFrequencies(freq: Record<string, EmojiFrequencyRecord>): void {
    this.emojiFreqCache = freq
    if (typeof localStorage !== 'undefined') {
      try {
        localStorage.setItem(this.getEmojiStorageKey(), JSON.stringify(freq))
      } catch {
        // 忽略 localStorage 配额异常
      }
    }
  }

  /** 记录某内置 Emoji 使用了一次 */
  recordEmojiUsage(emoji: string): void {
    if (!emoji) return
    const freqs = { ...this.loadEmojiFrequencies() }
    const current = freqs[emoji] ?? { count: 0, lastUsedAt: 0 }
    freqs[emoji] = {
      count: current.count + 1,
      lastUsedAt: Date.now(),
    }
    this.saveEmojiFrequencies(freqs)
    this.notify()
  }

  /** 获取排好序的 Emoji 列表：使用越多的越排在前面 */
  getSortedEmojis(): string[] {
    const freqs = this.loadEmojiFrequencies()
    const all = new Set<string>([...DEFAULT_EMOJIS, ...Object.keys(freqs)])
    return Array.from(all).sort((a, b) => {
      const countA = freqs[a]?.count ?? 0
      const countB = freqs[b]?.count ?? 0
      if (countA !== countB) return countB - countA
      const timeA = freqs[a]?.lastUsedAt ?? 0
      const timeB = freqs[b]?.lastUsedAt ?? 0
      if (timeA !== timeB) return timeB - timeA
      return 0
    })
  }

  // ==========================================
  // 自定义表情包列表、上限 100 与本地墓碑
  // ==========================================

  private loadStickers(): CustomStickerItem[] {
    if (this.stickerListCache) return this.stickerListCache
    if (typeof localStorage === 'undefined') {
      this.stickerListCache = []
      return this.stickerListCache
    }
    try {
      const raw = localStorage.getItem(this.getStickerStorageKey())
      const parsed = raw ? (JSON.parse(raw) as Array<Partial<CustomStickerItem> & { url?: string }>) : []
      this.stickerListCache = Array.isArray(parsed)
        ? parsed.filter(item => typeof item?.url === 'string' && item.url.trim().length > 0).map(item => normalizeStickerRecord({ ...item, url: item.url! }))
        : []
    } catch {
      this.stickerListCache = []
    }
    return this.stickerListCache ?? []
  }

  private saveStickers(items: CustomStickerItem[]): void {
    this.stickerListCache = items
    if (typeof localStorage !== 'undefined') {
      try {
        localStorage.setItem(this.getStickerStorageKey(), JSON.stringify(items))
      } catch {
        // 忽略容量异常
      }
    }
  }

  private loadTombstones(): StickerTombstone[] {
    if (this.tombstoneCache) return this.tombstoneCache
    if (typeof localStorage === 'undefined') {
      this.tombstoneCache = []
      return this.tombstoneCache
    }
    try {
      const raw = localStorage.getItem(this.getTombstoneStorageKey())
      const parsed = raw ? (JSON.parse(raw) as StickerTombstone[]) : []
      this.tombstoneCache = Array.isArray(parsed)
        ? parsed.filter(t => typeof t?.stickerId === 'string' && typeof t?.deletedAt === 'number')
        : []
    } catch {
      this.tombstoneCache = []
    }
    return this.tombstoneCache ?? []
  }

  private saveTombstones(items: StickerTombstone[]): void {
    const trimmed = items.slice(-200)
    this.tombstoneCache = trimmed
    if (typeof localStorage !== 'undefined') {
      try {
        localStorage.setItem(this.getTombstoneStorageKey(), JSON.stringify(trimmed))
      } catch {
        // ignore
      }
    }
  }

  /** 获取自适应排序的表情包列表：使用频次越高越靠前 */
  getSortedStickers(): CustomStickerItem[] {
    const list = [...this.loadStickers()]
    return list.sort((a, b) => {
      if (a.count !== b.count) return b.count - a.count
      if (a.lastUsedAt !== b.lastUsedAt) return b.lastUsedAt - a.lastUsedAt
      return b.addedAt - a.addedAt
    })
  }

  /** 根据 URL 或 stickerId 查找收藏条目 */
  findSticker(urlOrId: string): CustomStickerItem | undefined {
    const trimmed = urlOrId.trim()
    if (!trimmed) return undefined
    return this.loadStickers().find(item => item.url === trimmed || item.stickerId === trimmed)
  }

  /** 检查 URL 或内容哈希是否已被收藏 */
  isStickerSaved(url: string, sha256?: string | null): boolean {
    const trimmed = url.trim()
    const normalizedSha = sha256?.trim().toLowerCase() || null
    return this.loadStickers().some(
      item => item.url === trimmed || (normalizedSha !== null && (item.sha256 === normalizedSha || item.stickerId === normalizedSha))
    )
  }

  /** 记录已收藏表情包使用了一次（仅对已收藏条目累加，防止复读他人链接误入收藏） */
  recordStickerUsage(url: string): void {
    const trimmed = url.trim()
    if (!trimmed) return
    const list = this.loadStickers()
    const now = Date.now()
    let found = false
    const next = list.map(item => {
      if (item.url === trimmed) {
        found = true
        return { ...item, count: item.count + 1, lastUsedAt: now, updatedAt: now }
      }
      return item
    })
    if (!found) return
    this.saveStickers(next)
    this.notify()
    this.scheduleSync()
  }

  /** 添加自定义表情包（兼容旧 boolean 返回；详细状态请用 addCustomStickerDetailed） */
  addCustomSticker(
    url: string,
    options?: { sha256?: string | null; mime?: string; blob?: Blob; skipCacheFetch?: boolean }
  ): boolean {
    return this.addCustomStickerDetailed(url, options).ok
  }

  /** 添加自定义表情包并返回明确状态（用于显式提示 100 上限） */
  addCustomStickerDetailed(
    url: string,
    options?: { sha256?: string | null; mime?: string; blob?: Blob; skipCacheFetch?: boolean }
  ): AddStickerResult {
    const trimmed = url.trim()
    if (!trimmed || trimmed.length > MAX_STICKER_URL_LENGTH) {
      return { ok: false, reason: 'invalid' }
    }
    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) {
      return { ok: false, reason: 'invalid' }
    }

    const normalizedSha = options?.sha256 && /^[0-9a-f]{64}$/i.test(options.sha256)
      ? options.sha256.toLowerCase()
      : null
    const list = this.loadStickers()

    const duplicate = list.find(
      item => item.url === trimmed || (normalizedSha !== null && (item.sha256 === normalizedSha || item.stickerId === normalizedSha))
    )
    if (duplicate) {
      // 若传入了已知的更高精度哈希或有效直链（例如刚重传修复完同图），刷新映射而不占用新槽位
      if ((normalizedSha && duplicate.sha256 !== normalizedSha) || duplicate.url !== trimmed || duplicate.needsReupload) {
        const now = Date.now()
        const oldId = duplicate.stickerId
        const newId = normalizedSha ?? duplicate.stickerId
        if (oldId !== newId) {
          this.pendingReplacedIds.set(newId, oldId)
        }
        const updatedList = list.map(item =>
          item.stickerId === oldId
            ? {
                ...item,
                stickerId: newId,
                url: trimmed,
                sha256: normalizedSha ?? item.sha256,
                mime: options?.mime || item.mime,
                needsReupload: false,
                updatedAt: now,
              }
            : item
        )
        this.saveStickers(updatedList)
        if (options?.blob) {
          void this.saveImageBlob(trimmed, options.blob, normalizedSha)
        }
        this.notify()
        this.scheduleSync()
      } else if (options?.blob) {
        void this.saveImageBlob(trimmed, options.blob, normalizedSha)
      }
      return { ok: false, reason: 'duplicate', item: duplicate }
    }

    if (list.length >= MAX_CUSTOM_STICKERS) {
      return { ok: false, reason: 'limit' }
    }

    const now = Date.now()
    const stickerId = normalizedSha ?? buildTempUrlStickerIdSync(trimmed)
    const created: CustomStickerItem = {
      stickerId,
      url: trimmed,
      sha256: normalizedSha,
      mime: options?.mime ?? '',
      count: 0,
      lastUsedAt: 0,
      addedAt: now,
      updatedAt: now,
      needsReupload: false,
    }

    // 若此前删过同一张图，重加时清掉本地旧墓碑并给服务端传递更新后的 updatedAt
    const tombstones = this.loadTombstones().filter(t => t.stickerId !== stickerId)
    this.saveTombstones(tombstones)

    this.saveStickers([...list, created])
    this.notify()
    this.scheduleSync()

    if (options?.blob) {
      void this.saveImageBlob(trimmed, options.blob, normalizedSha)
    } else if (!options?.skipCacheFetch) {
      void this.cacheStickerBlobAndUpgrade(created.stickerId, trimmed)
    }

    return { ok: true, item: created }
  }

  /**
   * 异步从 URL 拉取真实图像二进制并收藏到本地与云端：
   * 保证右键收藏或输入外链时本地 IndexedDB 100% 落盘，同时按图片真实 SHA-256 去重。
   */
  async saveStickerFromUrl(rawUrl: string): Promise<AddStickerResult> {
    const trimmed = rawUrl.trim()
    if (!trimmed || trimmed.length > MAX_STICKER_URL_LENGTH || (!trimmed.startsWith('http://') && !trimmed.startsWith('https://'))) {
      return { ok: false, reason: 'invalid' }
    }

    const generation = this.generation
    const invalid: AddStickerResult = { ok: false, reason: 'invalid' }
    const current = this.loadStickers()
    const existingByUrl = current.find(item => item.url === trimmed)
    if (existingByUrl) {
      if (!existingByUrl.sha256) {
        await this.cacheStickerBlobAndUpgrade(existingByUrl.stickerId, trimmed)
        if (generation !== this.generation) return invalid
      }
      return { ok: false, reason: 'duplicate', item: existingByUrl }
    }

    if (current.length >= MAX_CUSTOM_STICKERS) {
      return { ok: false, reason: 'limit' }
    }

    // 优先使用本地已有 Blob 或拉取远端真实二进制（含 CORS 失败走服务端无状态中继）
    let blob = await this.getStoredBlob(trimmed)
    if (generation !== this.generation) return invalid
    let mime = blob?.type || ''
    if (!blob) {
      const fetched = await fetchImageBlobForCaching(trimmed)
      if (!fetched) {
        return { ok: false, reason: 'invalid' }
      }
      blob = fetched.blob
      mime = fetched.mime
    }

    const sha256 = await computeBlobSha256(blob)
    if (generation !== this.generation) return invalid
    const duplicateByHash = this.loadStickers().find(item => item.sha256 === sha256 || item.stickerId === sha256)
    if (duplicateByHash) {
      await this.saveImageBlob(trimmed, blob, sha256)
      if (generation !== this.generation) return invalid
      if (duplicateByHash.needsReupload) {
        this.updateStickerUrl(duplicateByHash.url, trimmed, sha256, mime)
      }
      return { ok: false, reason: 'duplicate', item: duplicateByHash }
    }

    return this.addCustomStickerDetailed(trimmed, { sha256, mime, blob, skipCacheFetch: true })
  }

  /** 从自定义表情包删除单个条目 */
  removeCustomSticker(urlOrId: string): boolean {
    return this.removeCustomStickers([urlOrId]) > 0
  }

  /** 批量删除自定义表情包（写本地墓碑并立即推送云端） */
  removeCustomStickers(urlsOrIds: readonly string[]): number {
    if (!urlsOrIds || urlsOrIds.length === 0) return 0
    const targets = new Set(urlsOrIds.map(s => s.trim()).filter(Boolean))
    if (targets.size === 0) return 0

    const list = this.loadStickers()
    const removedItems: CustomStickerItem[] = []
    const remaining: CustomStickerItem[] = []

    for (const item of list) {
      if (targets.has(item.stickerId) || targets.has(item.url)) {
        removedItems.push(item)
      } else {
        remaining.push(item)
      }
    }

    if (removedItems.length === 0) return 0

    const now = Date.now()
    const removedIds = removedItems.map(item => item.stickerId)
    const tombMap = new Map(this.loadTombstones().map(t => [t.stickerId, t.deletedAt]))
    for (const item of removedItems) {
      tombMap.set(item.stickerId, now)
      this.deleteMemoryBlobUrl(item.url)
      this.pendingReplacedIds.delete(item.stickerId)
      this.pendingBrokenReports.delete(item.stickerId)
    }

    this.saveStickers(remaining)
    this.saveTombstones(Array.from(tombMap.entries()).map(([stickerId, deletedAt]) => ({ stickerId, deletedAt })))
    this.notify()

    void this.pushDeleteToServer(removedIds, now)
    return removedItems.length
  }

  /** 更新已收藏表情包的 URL（用于过期自动重传后原子更新本地映射与云端状态） */
  updateStickerUrl(oldUrl: string, newUrl: string, sha256?: string | null, mime?: string): boolean {
    const oldTrimmed = oldUrl.trim()
    const newTrimmed = newUrl.trim()
    if (!oldTrimmed || !newTrimmed) return false

    const list = this.loadStickers()
    const now = Date.now()
    let found = false
    const next = list.map(item => {
      if (item.url === oldTrimmed) {
        found = true
        const resolvedSha = sha256 ?? item.sha256 ?? null
        const resolvedId = resolvedSha ?? item.stickerId
        if (item.stickerId !== resolvedId) {
          this.pendingReplacedIds.set(resolvedId, item.stickerId)
        }
        return {
          ...item,
          stickerId: resolvedId,
          url: newTrimmed,
          sha256: resolvedSha,
          mime: mime || item.mime || '',
          needsReupload: false,
          updatedAt: now,
        }
      }
      return item
    })
    if (!found) return false

    this.saveStickers(next)
    this.aliveProbeCache.delete(oldTrimmed)
    this.aliveProbeCache.delete(newTrimmed)

    const generation = this.generation
    void this.initDatabase().then(async db => {
      if (!db || generation !== this.generation) return
      const blob = await this.getFromIndexedDB(db, STORE_IMAGES, oldTrimmed)
        ?? (sha256 ? await this.getBlobFromShaStore(db, sha256) : null)
      if (blob && generation === this.generation) {
        await this.saveToImageStore(db, newTrimmed, blob)
        if (sha256 && generation === this.generation) {
          await this.saveToBlobStore(db, sha256, blob, mime)
        }
      }
    }).catch(() => {})

    this.notify()
    this.scheduleSync()
    return true
  }

  /** 获取某个表情在本地 IndexedDB 存储的原始 Blob（优先按内容 SHA-256 检索，再按 URL 回退） */
  async getStoredBlob(url: string, knownSha256?: string | null): Promise<Blob | null> {
    const trimmed = url.trim()
    const matched = trimmed ? this.findSticker(trimmed) : undefined
    const sha256 = (knownSha256 || matched?.sha256 || '').trim().toLowerCase()

    const db = await this.initDatabase()
    if (!db) return null

    if (sha256) {
      const bySha = await this.getBlobFromShaStore(db, sha256)
      if (bySha) return bySha
    }
    if (trimmed) {
      const byUrl = await this.getFromIndexedDB(db, STORE_IMAGES, trimmed)
      if (byUrl) return byUrl
    }
    return null
  }

  // ==========================================
  // 云端存活性探测 & 过期自动重传自愈
  // ==========================================

  /** 探测远程图片链接是否仍可正常访问（排除 404/410/403/非图片 HTML 落地页） */
  async checkRemoteUrlAlive(url: string): Promise<boolean> {
    const trimmed = url.trim()
    if (!trimmed) return false
    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) {
      return true
    }

    const generation = this.generation
    const cachedCheckedAt = this.aliveProbeCache.get(trimmed)
    if (cachedCheckedAt && Date.now() - cachedCheckedAt < ALIVE_CACHE_TTL_MS) {
      return true
    }

    // 1. 先尝试 HEAD 探活
    if (typeof fetch !== 'undefined') {
      try {
        const res = await fetch(trimmed, {
          method: 'HEAD',
          cache: 'no-store',
          signal: AbortSignal.timeout(4000),
        })
        if (generation !== this.generation) return false
        if (res.ok) {
          const contentType = (res.headers?.get?.('content-type') || '').toLowerCase()
          if (contentType && contentType.startsWith('text/html')) {
            return false
          }
          this.aliveProbeCache.set(trimmed, Date.now())
          return true
        }
        if (res.status === 404 || res.status === 410 || res.status === 403 || res.status >= 500) {
          return false
        }
      } catch {
        // 部分 CDN 拒绝 HEAD 或未配 HEAD CORS，继续走离屏 Image 探活或判定
      }
    }

    // 2. 浏览器环境兜底用离屏 Image 探活（不受 CORS 限制，能真实判定 404/失效）
    if (generation === this.generation && typeof Image !== 'undefined' && typeof window !== 'undefined') {
      const imgAlive = await new Promise<boolean>(resolve => {
        const img = new Image()
        const timer = setTimeout(() => {
          img.onload = null
          img.onerror = null
          resolve(false)
        }, 4500)
        img.onload = () => {
          clearTimeout(timer)
          resolve((img.naturalWidth ?? 0) > 0)
        }
        img.onerror = () => {
          clearTimeout(timer)
          resolve(false)
        }
        const sep = trimmed.includes('?') ? '&' : '?'
        img.src = `${trimmed}${sep}_dh_probe=${Date.now()}`
      })
      if (imgAlive && generation === this.generation) {
        this.aliveProbeCache.set(trimmed, Date.now())
      }
      return imgAlive
    }

    return false
  }

  /**
   * 确保表情包可安全发送：
   * - 云端仍有效：顺便确保本地 IndexedDB 有副本；
   * - 云端已过期且本地有 Blob：自动重新上传并更新本地与云端直链；
   * - 云端已过期且本机无 Blob：向云端标记待原设备上线自动补传。
   */
  async ensureStickerReadyForSend(
    url: string,
    uploader?: (blob: Blob) => Promise<{ url: string; sha256?: string; mime?: string }>
  ): Promise<EnsureStickerReadyResult> {
    const trimmed = url.trim()
    if (!trimmed) {
      return { url: trimmed, healed: false, missingLocalBlob: false }
    }

    const existing = this.inFlightHeals.get(trimmed)
    if (existing) return existing

    const task = this.ensureStickerReadyInternal(trimmed, uploader).finally(() => {
      if (this.inFlightHeals.get(trimmed) === task) this.inFlightHeals.delete(trimmed)
    })
    this.inFlightHeals.set(trimmed, task)
    return task
  }

  private async ensureStickerReadyInternal(
    trimmed: string,
    uploader?: (blob: Blob) => Promise<{ url: string; sha256?: string; mime?: string }>
  ): Promise<EnsureStickerReadyResult> {
    const generation = this.generation
    const stale = { url: trimmed, healed: false, missingLocalBlob: false }
    const sticker = this.findSticker(trimmed)
    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) {
      return { url: trimmed, healed: false, missingLocalBlob: false, stickerId: sticker?.stickerId }
    }

    const isAlive = await this.checkRemoteUrlAlive(trimmed)
    if (generation !== this.generation) return stale
    if (isAlive) {
      // 确保本地缓存不会缺失
      if (sticker) {
        const localBlob = await this.getStoredBlob(trimmed, sticker.sha256)
        if (generation !== this.generation) return stale
        if (!localBlob) {
          void this.cacheStickerBlobAndUpgrade(sticker.stickerId, trimmed)
        }
      }
      return { url: trimmed, healed: false, missingLocalBlob: false, stickerId: sticker?.stickerId }
    }

    // 远端确认失效：检索本地原图二进制
    let blob = await this.getStoredBlob(trimmed, sticker?.sha256)
    if (generation !== this.generation) return stale
    if (!blob) {
      if (sticker) {
        this.markNeedsReupload(sticker.stickerId, trimmed)
      }
      return { url: trimmed, healed: false, missingLocalBlob: true, stickerId: sticker?.stickerId }
    }

    try {
      const uploadFn = uploader ?? uploadChatImage
      const res = await uploadFn(blob)
      if (generation !== this.generation) return stale
      if (res?.url) {
        const resolvedSha = res.sha256 ?? sticker?.sha256 ?? await computeBlobSha256(blob)
        if (generation !== this.generation) return stale
        const resolvedMime = res.mime ?? blob.type ?? sticker?.mime ?? ''
        await this.saveImageBlob(res.url, blob, resolvedSha)
        if (generation !== this.generation) return stale
        if (sticker) {
          if (res.url !== trimmed) {
            this.updateStickerUrl(trimmed, res.url, resolvedSha, resolvedMime)
          } else {
            this.clearNeedsReupload(sticker.stickerId)
          }
        }
        return {
          url: res.url,
          healed: res.url !== trimmed,
          missingLocalBlob: false,
          stickerId: resolvedSha ?? sticker?.stickerId,
        }
      }
    } catch {
      // 重传失败保留现状，等待下次重试
    }

    return { url: trimmed, healed: false, missingLocalBlob: false, stickerId: sticker?.stickerId }
  }

  /** 兼容旧签名：返回自愈后的有效 URL */
  async ensureValidStickerUrl(
    url: string,
    uploader?: (blob: Blob) => Promise<{ url: string; sha256?: string; mime?: string }>
  ): Promise<string> {
    const res = await this.ensureStickerReadyForSend(url, uploader)
    return res.url
  }

  /** 发送聊天草稿前检查其中包含的已收藏表情包链接：若已过期自动重传并替换成新直链 */
  async prepareDraftStickersForSend(draft: string): Promise<PrepareDraftStickersResult> {
    const urlMatches = draft.match(/https?:\/\/[^\s"'<>]+/gi)
    if (!urlMatches || urlMatches.length === 0) {
      return { ok: true, body: draft, healedCount: 0 }
    }

    const generation = this.generation
    const uniqueUrls = Array.from(new Set(urlMatches))
    let updatedBody = draft
    let healedCount = 0

    for (const candidateUrl of uniqueUrls) {
      const saved = this.findSticker(candidateUrl)
      const hasLocal = saved ? true : Boolean(await this.getStoredBlob(candidateUrl))
      if (generation !== this.generation) return { ok: false, body: draft, healedCount }
      if (!saved && !hasLocal) continue

      const res = await this.ensureStickerReadyForSend(candidateUrl)
      if (generation !== this.generation) return { ok: false, body: draft, healedCount }
      if (res.missingLocalBlob) {
        return { ok: false, body: draft, healedCount, missingLocalBlob: true }
      }
      if (res.url && res.url !== candidateUrl) {
        updatedBody = updatedBody.split(candidateUrl).join(res.url)
        healedCount += 1
      }
    }

    return { ok: true, body: updatedBody, healedCount }
  }

  private markNeedsReupload(stickerId: string, brokenUrl: string): void {
    const list = this.loadStickers()
    const now = Date.now()
    let changed = false
    const next = list.map(item => {
      if (item.stickerId === stickerId && item.url === brokenUrl && !item.needsReupload) {
        changed = true
        return { ...item, needsReupload: true, updatedAt: now }
      }
      return item
    })
    if (!changed) return
    this.saveStickers(next)
    this.pendingBrokenReports.set(stickerId, brokenUrl)
    this.notify()
    this.scheduleSync()
  }

  private clearNeedsReupload(stickerId: string): void {
    const list = this.loadStickers()
    const now = Date.now()
    let changed = false
    const next = list.map(item => {
      if (item.stickerId === stickerId && item.needsReupload) {
        changed = true
        return { ...item, needsReupload: false, updatedAt: now }
      }
      return item
    })
    if (!changed) return
    this.saveStickers(next)
    this.pendingBrokenReports.delete(stickerId)
    this.notify()
    this.scheduleSync()
  }

  // ==========================================
  // 多端双向同步 (/api/v1/chat/stickers*)
  // ==========================================

  private scheduleSync(): void {
    if (!this.activePlayerId || typeof fetch === 'undefined') return
    if (this.syncTimer) {
      clearTimeout(this.syncTimer)
    }
    const generation = this.generation
    this.syncTimer = setTimeout(() => {
      if (generation !== this.generation) return
      this.syncTimer = null
      void this.syncWithServer()
    }, SYNC_DEBOUNCE_MS)
  }

  /** 立即与云端同步表情包列表、使用频次、墓碑与待补传状态 */
  async syncWithServer(): Promise<void> {
    if (!this.activePlayerId || typeof fetch === 'undefined') return
    if (this.syncInFlight) return this.syncInFlight

    const task = this.syncWithServerInternal().finally(() => {
      if (this.syncInFlight === task) this.syncInFlight = null
    })
    this.syncInFlight = task
    return task
  }

  private async syncWithServerInternal(): Promise<void> {
    const generation = this.generation

    const localItems = this.loadStickers()
    const localTombstones = this.loadTombstones()

    const payloadItems = localItems.map(item => {
      const replacedStickerId = this.pendingReplacedIds.get(item.stickerId)
      const brokenUrl = item.needsReupload ? (this.pendingBrokenReports.get(item.stickerId) ?? item.url) : undefined
      return {
        stickerId: item.stickerId,
        url: item.url,
        sha256: item.sha256 ?? null,
        mime: item.mime ?? '',
        useCount: item.count,
        lastUsedAt: item.lastUsedAt,
        addedAt: item.addedAt,
        updatedAt: item.updatedAt,
        needsReupload: Boolean(item.needsReupload),
        brokenUrl,
        replacedStickerId,
      }
    })

    try {
      const res = await fetch(getApiUrl('/api/v1/chat/stickers/sync'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        signal: AbortSignal.timeout(12_000),
        body: JSON.stringify({
          items: payloadItems,
          deleted: localTombstones,
        }),
      })

      if (generation !== this.generation) return

      if (res.status === 409) {
        const errBody = (await res.json().catch(() => null)) as { state?: ServerStickerSyncResponse } | null
        if (generation !== this.generation) return
        this.lastSyncError = 'limit'
        if (errBody?.state) {
          this.applyServerState(errBody.state, true)
        }
        this.notify()
        return
      }

      if (!res.ok) return

      const state = (await res.json()) as ServerStickerSyncResponse
      if (generation !== this.generation) return
      this.lastSyncError = null
      this.pendingReplacedIds.clear()
      this.pendingBrokenReports.clear()
      this.applyServerState(state, false)
      this.notify()
      this.scheduleBackgroundMaintenance()
    } catch {
      // 网络离线时静默保留本地变更，下次重连或切回前台自动同步
    }
  }

  private async pushDeleteToServer(stickerIds: string[], deletedAt: number): Promise<void> {
    if (!this.activePlayerId || typeof fetch === 'undefined' || stickerIds.length === 0) return
    const generation = this.generation
    try {
      const res = await fetch(getApiUrl('/api/v1/chat/stickers/delete'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        signal: AbortSignal.timeout(10_000),
        body: JSON.stringify({ stickerIds, deletedAt }),
      })
      if (generation === this.generation && res.ok) {
        const state = (await res.json()) as ServerStickerSyncResponse
        if (generation !== this.generation) return
        this.lastSyncError = null
        this.applyServerState(state, false)
        this.notify()
      }
    } catch {
      // 保留本地墓碑，下次同步自动上送
    }
  }

  private applyServerState(state: ServerStickerSyncResponse, preserveOverflowLocals: boolean): void {
    const serverItems: CustomStickerItem[] = (state.stickers ?? []).map(dto => ({
      stickerId: dto.stickerId,
      url: dto.url,
      sha256: dto.sha256 ?? null,
      mime: dto.mime ?? '',
      count: dto.useCount,
      lastUsedAt: dto.lastUsedAt,
      addedAt: dto.addedAt,
      updatedAt: dto.updatedAt,
      needsReupload: Boolean(dto.needsReupload),
    }))

    const serverTombstones = state.tombstones ?? []
    const tombMap = new Map<string, number>()
    for (const tomb of this.loadTombstones()) {
      tombMap.set(tomb.stickerId, tomb.deletedAt)
    }
    for (const tomb of serverTombstones) {
      const prev = tombMap.get(tomb.stickerId) ?? 0
      if (tomb.deletedAt > prev) tombMap.set(tomb.stickerId, tomb.deletedAt)
    }

    if (preserveOverflowLocals) {
      const serverIds = new Set(serverItems.map(i => i.stickerId))
      const extras = this.loadStickers().filter(
        local => !serverIds.has(local.stickerId) && (tombMap.get(local.stickerId) ?? 0) < local.updatedAt
      )
      this.saveStickers([...serverItems, ...extras])
    } else {
      this.saveStickers(serverItems)
    }

    this.saveTombstones(
      Array.from(tombMap.entries()).map(([stickerId, deletedAt]) => ({ stickerId, deletedAt }))
    )
  }

  /**
   * 后台静默维护（每次同步后低并发执行）：
   * 1. 若云端有表情标记 needsReupload=true，且本机存有对应 Blob -> 自动重传并推送新直链修复云端；
   * 2. 若云端表情在本机 IndexedDB 尚无对应 Blob -> 自动拉取二进制并升级为规范 SHA-256 标识，确保本地永不缺图。
   */
  private scheduleBackgroundMaintenance(): void {
    if (typeof window === 'undefined' || typeof fetch === 'undefined') return
    if (this.backgroundPassInFlight) return

    const generation = this.generation
    const run = async (): Promise<void> => {
      const snapshot = [...this.loadStickers()]
      for (const item of snapshot) {
        if (generation !== this.generation) break

        // 1. 跨端自动补传：其他端标记了云端失效，而当前设备本地保有原图
        if (item.needsReupload) {
          const blob = await this.getStoredBlob(item.url, item.sha256)
          if (generation !== this.generation) return
          if (blob) {
            try {
              const res = await uploadChatImage(blob)
              if (generation !== this.generation) return
              await this.saveImageBlob(res.url, blob, res.sha256)
              if (generation !== this.generation) return
              this.updateStickerUrl(item.url, res.url, res.sha256, res.mime)
            } catch {
              // 图床暂不可用，下轮继续
            }
          }
          continue
        }

        // 2. 本地缺失二进制补齐：保障从另一端同步或早期收藏的图片均在本机落盘
        const localBlob = await this.getStoredBlob(item.url, item.sha256)
        if (generation !== this.generation) return
        if (!localBlob) {
          await this.cacheStickerBlobAndUpgrade(item.stickerId, item.url)
        }
      }
    }

    const task = run().finally(() => {
      if (this.backgroundPassInFlight === task) this.backgroundPassInFlight = null
    })
    this.backgroundPassInFlight = task
  }

  /** 拉取图片二进制写入 IndexedDB blobs/images，并把 url_* 临时标识升级为内容 SHA-256 */
  async cacheStickerBlobAndUpgrade(stickerId: string, url: string, suppliedBlob?: Blob): Promise<void> {
    const trimmedUrl = url.trim()
    if (!trimmedUrl) return

    const generation = this.generation
    let blob = suppliedBlob ?? await this.getStoredBlob(trimmedUrl)
    if (generation !== this.generation) return
    let mime = blob?.type || ''
    if (!blob) {
      const fetched = await fetchImageBlobForCaching(trimmedUrl)
      if (!fetched) return
      blob = fetched.blob
      mime = fetched.mime
    } else {
      const detected = await detectRasterImageMime(blob)
      if (detected) mime = detected
    }

    const sha256 = await computeBlobSha256(blob)
    if (generation !== this.generation) return
    await this.saveImageBlob(trimmedUrl, blob, sha256)
    if (generation !== this.generation) return

    const list = this.loadStickers()
    const target = list.find(item => item.stickerId === stickerId || item.url === trimmedUrl)
    if (!target) return

    if (target.stickerId === sha256 && target.sha256 === sha256 && target.mime === mime) {
      return
    }

    const now = Date.now()
    const duplicateCanonical = list.find(item => item.stickerId === sha256 && item.stickerId !== target.stickerId)
    let next: CustomStickerItem[]
    if (duplicateCanonical) {
      // 与既有同内容表情包合并统计并移除临时占位
      next = list
        .filter(item => item.stickerId !== target.stickerId)
        .map(item =>
          item.stickerId === sha256
            ? {
                ...item,
                count: Math.max(item.count, target.count),
                lastUsedAt: Math.max(item.lastUsedAt, target.lastUsedAt),
                addedAt: Math.min(item.addedAt, target.addedAt),
                updatedAt: now,
              }
            : item
        )
      const tombMap = new Map(this.loadTombstones().map(t => [t.stickerId, t.deletedAt]))
      tombMap.set(target.stickerId, now)
      this.saveTombstones(Array.from(tombMap.entries()).map(([id, deletedAt]) => ({ stickerId: id, deletedAt })))
    } else {
      if (target.stickerId !== sha256) {
        this.pendingReplacedIds.set(sha256, target.stickerId)
      }
      next = list.map(item =>
        item.stickerId === target.stickerId
          ? {
              ...item,
              stickerId: sha256,
              sha256,
              mime: mime || item.mime || '',
              updatedAt: now,
            }
          : item
      )
    }

    this.saveStickers(next)
    this.notify()
    this.scheduleSync()
  }

  // ==========================================
  // IndexedDB 图像离线缓存
  // ==========================================

  private cacheMemoryImage(key: string, blob: Blob, sha256?: string | null): string {
    if (blob.size > MEMORY_IMAGE_BUDGET) return key
    const existing = this.getCachedUrl(key, sha256)
    if (existing) return existing
    for (const [url, entry] of this.memoryImages) {
      if (this.memoryBytes + blob.size <= MEMORY_IMAGE_BUDGET && this.memoryImages.size < MEMORY_IMAGE_COUNT) break
      if (entry.leases === 0) this.retireImage(url, entry)
    }
    if (this.memoryBytes + blob.size > MEMORY_IMAGE_BUDGET || this.memoryImages.size >= MEMORY_IMAGE_COUNT) return key
    const url = URL.createObjectURL(blob)
    const sha = sha256?.toLowerCase() || null
    this.memoryImages.set(url, { key, sha256: sha, bytes: blob.size, leases: 0, retired: false })
    this.memoryBytes += blob.size
    this.memoryBlobUrlMap.set(key, url)
    if (sha) this.memorySha256UrlMap.set(sha, url)
    return url
  }

  private retireImage(url: string, entry: MemoryImage): void {
    entry.retired = true
    if (this.memoryBlobUrlMap.get(entry.key) === url) this.memoryBlobUrlMap.delete(entry.key)
    if (entry.sha256 && this.memorySha256UrlMap.get(entry.sha256) === url) this.memorySha256UrlMap.delete(entry.sha256)
    if (entry.leases > 0 || !this.memoryImages.delete(url)) return
    this.memoryBytes -= entry.bytes
    URL.revokeObjectURL(url)
  }

  private deleteMemoryBlobUrl(key: string): void {
    const url = this.memoryBlobUrlMap.get(key)
    if (url) this.retireImage(url, this.memoryImages.get(url)!)
  }

  private getCachedUrl(url: string, sha256?: string | null): string | null {
    const sha = sha256 || this.findSticker(url)?.sha256
    const hit = this.memoryBlobUrlMap.get(url) ?? (sha ? this.memorySha256UrlMap.get(sha.toLowerCase()) : undefined)
    if (!hit) return null
    const entry = this.memoryImages.get(hit)!
    this.memoryImages.delete(hit)
    this.memoryImages.set(hit, entry)
    return hit
  }

  /** 渲染节点持有租约；退役的旧图等最后一个节点释放后才撤销。 */
  async acquireImage(url: string, sha256?: string | null): Promise<ChatImageLease> {
    const generation = this.generation
    const src = await this.loadImageWithCache(url, sha256)
    const entry = this.memoryImages.get(src)
    if (generation !== this.generation || !entry || entry.retired) {
      return { src: url, release: () => undefined }
    }
    entry.leases += 1
    let released = false
    return { src, release: () => {
      if (released) return
      released = true
      entry.leases -= 1
      if (entry.retired && entry.leases === 0) this.retireImage(src, entry)
    } }
  }

  /** 直接将已持有的 Blob 写入内存与 IndexedDB 双层离线库 */
  async saveImageBlob(url: string, blob: Blob, knownSha256?: string | null): Promise<string> {
    const trimmed = url.trim()
    if (!trimmed) return ''

    const generation = this.generation
    let sha256 = knownSha256?.trim().toLowerCase() || ''
    if (!sha256) {
      try {
        sha256 = await computeBlobSha256(blob)
      } catch {
        sha256 = ''
      }
    }

    if (generation !== this.generation) return ''
    this.cacheMemoryImage(trimmed, blob, sha256)

    const db = await this.initDatabase()
    if (db && generation === this.generation) {
      await this.saveToImageStore(db, trimmed, blob)
      if (sha256 && generation === this.generation) {
        await this.saveToBlobStore(db, sha256, blob, blob.type)
      }
    }
    return trimmed
  }

  /**
   * 加载图片并利用 IndexedDB 离线缓存。
   * 优先命中内存 ObjectURL -> 复用并发请求 -> 命中 IndexedDB（URL/SHA-256）-> CORS/中继拉取并持久化。
   */
  private async loadImageWithCache(url: string, sha256?: string | null): Promise<string> {
    const trimmed = url.trim()
    if (!trimmed) return ''

    const resolvedSha = sha256 || this.findSticker(trimmed)?.sha256 || null
    const memHit = this.getCachedUrl(trimmed, resolvedSha)
    if (memHit) return memHit

    const existing = this.inFlightLoads.get(trimmed)
    if (existing) return existing

    const task = this.loadImageInternal(trimmed, resolvedSha).finally(() => {
      if (this.inFlightLoads.get(trimmed) === task) this.inFlightLoads.delete(trimmed)
    })
    this.inFlightLoads.set(trimmed, task)
    return task
  }

  private async loadImageInternal(trimmed: string, sha256: string | null): Promise<string> {
    const generation = this.generation
    const db = await this.initDatabase()
    if (generation !== this.generation) return trimmed
    if (db) {
      const cached = await this.getFromIndexedDB(db, STORE_IMAGES, trimmed)
        ?? (sha256 ? await this.getBlobFromShaStore(db, sha256) : null)
      if (generation !== this.generation) return trimmed
      if (cached) return this.cacheMemoryImage(trimmed, cached, sha256)
    }

    if (trimmed.startsWith('/')) {
      try {
        const resp = await fetch(trimmed, { signal: AbortSignal.timeout(10_000) })
        if (generation !== this.generation || !resp.ok) return trimmed
        const blob = await readChatImageBody(resp)
        if (!blob || generation !== this.generation) return trimmed
        if (db) await this.saveToImageStore(db, trimmed, blob)
        if (generation === this.generation) return this.cacheMemoryImage(trimmed, blob, sha256)
      } catch {
        // 静态源不可缓存时，交给图片节点处理加载失败。
      }
      return trimmed
    }

    const fetched = await fetchImageBlobForCaching(trimmed)
    if (!fetched || generation !== this.generation) return trimmed
    const computedSha = sha256 || (await computeBlobSha256(fetched.blob).catch(() => ''))
    if (generation !== this.generation) return trimmed
    await this.saveImageBlob(trimmed, fetched.blob, computedSha)
    return generation === this.generation ? this.getCachedUrl(trimmed, computedSha) ?? trimmed : trimmed
  }

  /** 直接查询是否已有缓存 */
  async isImageCached(url: string, sha256?: string | null): Promise<boolean> {
    const trimmed = url.trim()
    if (this.getCachedUrl(trimmed, sha256)) return true
    const blob = await this.getStoredBlob(trimmed, sha256)
    return blob instanceof Blob
  }

  private getFromIndexedDB(db: IDBDatabase, storeName: string, key: string): Promise<Blob | null> {
    return new Promise(resolve => {
      try {
        if (!db.objectStoreNames.contains(storeName)) {
          resolve(null)
          return
        }
        const tx = db.transaction(storeName, 'readonly')
        const store = tx.objectStore(storeName)
        const req = store.get(key)
        req.onsuccess = () => {
          const res = req.result
          resolve(res?.blob ?? null)
        }
        req.onerror = () => resolve(null)
      } catch {
        resolve(null)
      }
    })
  }

  private getBlobFromShaStore(db: IDBDatabase, sha256: string): Promise<Blob | null> {
    return this.getFromIndexedDB(db, STORE_BLOBS, sha256.toLowerCase())
  }

  private saveToImageStore(db: IDBDatabase, url: string, blob: Blob): Promise<void> {
    return new Promise(resolve => {
      try {
        if (!db.objectStoreNames.contains(STORE_IMAGES)) {
          resolve()
          return
        }
        const tx = db.transaction(STORE_IMAGES, 'readwrite')
        const store = tx.objectStore(STORE_IMAGES)
        store.put({ url, blob, addedAt: Date.now() })
        tx.oncomplete = () => resolve()
        tx.onerror = () => resolve()
      } catch {
        resolve()
      }
    })
  }

  private saveToBlobStore(db: IDBDatabase, sha256: string, blob: Blob, mime?: string): Promise<void> {
    return new Promise(resolve => {
      try {
        if (!db.objectStoreNames.contains(STORE_BLOBS)) {
          resolve()
          return
        }
        const tx = db.transaction(STORE_BLOBS, 'readwrite')
        const store = tx.objectStore(STORE_BLOBS)
        store.put({
          sha256: sha256.toLowerCase(),
          blob,
          mime: mime || blob.type || '',
          size: blob.size,
          updatedAt: Date.now(),
        })
        tx.oncomplete = () => resolve()
        tx.onerror = () => resolve()
      } catch {
        resolve()
      }
    })
  }

  private invalidatePendingWork(): void {
    this.generation += 1
    for (const [url, entry] of this.memoryImages) this.retireImage(url, entry)
    this.inFlightLoads.clear()
    this.inFlightHeals.clear()
    this.aliveProbeCache.clear()
    this.syncInFlight = null
    this.backgroundPassInFlight = null
    if (this.syncTimer) clearTimeout(this.syncTimer)
    this.syncTimer = null
  }

  /** 测试辅助：清理内存测试状态 */
  _resetForTest(): void {
    this.invalidatePendingWork()
    this.pendingReplacedIds.clear()
    this.pendingBrokenReports.clear()
    this.activePlayerId = null
    this.emojiFreqCache = null
    this.stickerListCache = null
    this.tombstoneCache = null
    this.lastSyncError = null
  }

  private isPreloading = false
  private hasPreloaded = false

  /**
   * 页面初始化空闲时全量预加载所有内置动态表情到 IndexedDB。
   * 采用滑动窗口受控并发，优先 CF 图床，失败自愈回退本地静态源。
   */
  async preloadAllEmotes(concurrency = 8): Promise<void> {
    if (this.isPreloading || this.hasPreloaded) return
    if (typeof window === 'undefined' || typeof fetch === 'undefined') return

    this.isPreloading = true

    try {
      const items = [...QQ_EMOJI_LIST]
      let currentIndex = 0

      const worker = async (): Promise<void> => {
        while (currentIndex < items.length) {
          const item = items[currentIndex++]
          if (!item) break
          const { cfUrl, localUrl } = getQqEmojiUrls(item.qsid)

          if (this.getCachedUrl(cfUrl) || this.getCachedUrl(localUrl)) {
            continue
          }

          try {
            const resolved = await this.loadImageWithCache(cfUrl)
            if (resolved === cfUrl) {
              await this.loadImageWithCache(localUrl)
            }
          } catch {
            await this.loadImageWithCache(localUrl).catch(() => {})
          }
        }
      }

      const workers = Array.from({ length: Math.min(concurrency, items.length) }, () => worker())
      await Promise.all(workers)
      this.hasPreloaded = true
    } finally {
      this.isPreloading = false
    }
  }

  /** 利用 requestIdleCallback 在浏览器空闲时自动调度预加载 */
  scheduleIdlePreload(): void {
    if (typeof window === 'undefined') return
    if (this.isPreloading || this.hasPreloaded) return

    const run = () => {
      this.preloadAllEmotes().catch(() => {})
    }

    const idleCallback = Reflect.get(window, 'requestIdleCallback')
    if (typeof idleCallback === 'function') {
      idleCallback(run, { timeout: 3000 })
    } else {
      setTimeout(run, 1500)
    }
  }
}

export const chatStickerService = new ChatStickerService()
export { computeTextSha256 }
