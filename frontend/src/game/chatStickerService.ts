import { getQqEmojiUrls, QQ_EMOJI_LIST } from './qqEmojis.ts'

/**
 * 传音聊天内置小黄脸表情与表情包（Sticker）本地存储与排序管理服务
 *
 * 核心职责：
 * 1. 原生 Emoji 频次统计与自排序（高频在先）；
 * 2. 自定义表情包（网络图片链接）收藏库管理与使用频次自排序；
 * 3. 基于浏览器 IndexedDB 的图片二进制离线存储与加载（解决 LocalStorage 5MB 限制），
 *    相同链接从本地 IndexedDB 取出 Blob 快速转换为 Object URL 秒开；
 * 4. 具备非浏览器（Node.js 单测 / SSR）优雅降级容错。
 */

const DB_NAME = 'daohua_chat_stickers'
const DB_VERSION = 1
const STORE_IMAGES = 'images'

const STORAGE_KEY_EMOJI_FREQ = 'daohua_chat_emoji_freq'
const STORAGE_KEY_CUSTOM_STICKERS = 'daohua_chat_custom_stickers'

/** 默认精选内置 QQ 风格小黄脸表情名称列表 */
export const DEFAULT_EMOJIS: readonly string[] = QQ_EMOJI_LIST.map(e => e.name)

/** 自定义表情包条目定义 */
export interface CustomStickerItem {
  url: string
  count: number
  lastUsedAt: number
  addedAt: number
}

interface EmojiFrequencyRecord {
  count: number
  lastUsedAt: number
}

class ChatStickerService {
  private dbPromise: Promise<IDBDatabase | null> | null = null
  private readonly memoryBlobUrlMap = new Map<string, string>()
  private readonly listeners = new Set<() => void>()

  // 内存中保持一份最新排序列表的缓存，避免高频读取 localStorage
  private emojiFreqCache: Record<string, EmojiFrequencyRecord> | null = null
  private stickerListCache: CustomStickerItem[] | null = null

  constructor() {
    this.initDatabase()
  }

  /** 初始化 IndexedDB */
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
          }
          req.onsuccess = () => resolve(req.result)
          req.onerror = () => {
            console.warn('[ChatStickerService] IndexedDB 打开失败，降级为直显模式')
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

  private notify(): void {
    for (const listener of this.listeners) {
      try {
        listener()
      } catch (err) {
        console.error('[ChatStickerService] listener error', err)
      }
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
      const raw = localStorage.getItem(STORAGE_KEY_EMOJI_FREQ)
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
        localStorage.setItem(STORAGE_KEY_EMOJI_FREQ, JSON.stringify(freq))
      } catch (err) {
        console.warn('[ChatStickerService] 写入 Emoji 频次失败', err)
      }
    }
  }

  /** 记录某 Emoji 使用了一次 */
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
  // 自定义表情包列表与排序
  // ==========================================

  private loadStickers(): CustomStickerItem[] {
    if (this.stickerListCache) return this.stickerListCache
    if (typeof localStorage === 'undefined') {
      this.stickerListCache = []
      return this.stickerListCache
    }
    try {
      const raw = localStorage.getItem(STORAGE_KEY_CUSTOM_STICKERS)
      this.stickerListCache = raw ? JSON.parse(raw) : []
    } catch {
      this.stickerListCache = []
    }
    return this.stickerListCache ?? []
  }

  private saveStickers(items: CustomStickerItem[]): void {
    this.stickerListCache = items
    if (typeof localStorage !== 'undefined') {
      try {
        localStorage.setItem(STORAGE_KEY_CUSTOM_STICKERS, JSON.stringify(items))
      } catch (err) {
        console.warn('[ChatStickerService] 写入表情包失败', err)
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

  /** 记录表情包使用了一次 */
  recordStickerUsage(url: string): void {
    const trimmed = url.trim()
    if (!trimmed) return
    const list = this.loadStickers()
    let found = false
    const now = Date.now()
    const next = list.map(item => {
      if (item.url === trimmed) {
        found = true
        return { ...item, count: item.count + 1, lastUsedAt: now }
      }
      return item
    })
    if (!found) {
      next.push({ url: trimmed, count: 1, lastUsedAt: now, addedAt: now })
    }
    this.saveStickers(next)
    this.notify()
  }

  /** 将 URL 添加到自定义表情包（收藏） */
  addCustomSticker(url: string): boolean {
    const trimmed = url.trim()
    if (!trimmed) return false
    const list = this.loadStickers()
    if (list.some(item => item.url === trimmed)) {
      return false
    }
    const now = Date.now()
    const updated = [...list, { url: trimmed, count: 0, lastUsedAt: 0, addedAt: now }]
    this.saveStickers(updated)
    this.notify()
    return true
  }

  /** 从自定义表情包删除 */
  removeCustomSticker(url: string): boolean {
    const trimmed = url.trim()
    const list = this.loadStickers()
    const updated = list.filter(item => item.url !== trimmed)
    if (updated.length === list.length) return false
    this.saveStickers(updated)
    this.notify()
    return true
  }

  /** 检查是否已被收藏 */
  isStickerSaved(url: string): boolean {
    const trimmed = url.trim()
    return this.loadStickers().some(item => item.url === trimmed)
  }

  // ==========================================
  // IndexedDB 图像离线缓存
  // ==========================================

  /** 获取当前内存已转换的离线 Object URL（命中即同步直出） */
  getCachedUrl(url: string): string | null {
    const trimmed = url.trim()
    return this.memoryBlobUrlMap.get(trimmed) ?? null
  }

  /**
   * 加载图片并利用 IndexedDB 离线缓存。
   * 优先命中内存 ObjectURL -> 命中 IndexedDB Blob -> 发起网络 Fetch 并持久化。
   * 若 Fetch 失败或受 CORS 限制，降级返回原始 url。
   */
  async loadImageWithCache(url: string): Promise<string> {
    const trimmed = url.trim()
    if (!trimmed) return ''

    // 1. 命中内存中的 Object URL
    const memHit = this.getCachedUrl(trimmed)
    if (memHit) return memHit

    // 2. 尝试从 IndexedDB 检索
    const db = await this.initDatabase()
    if (db) {
      try {
        const cachedBlob = await this.getFromIndexedDB(db, trimmed)
        if (cachedBlob instanceof Blob) {
          const blobUrl = URL.createObjectURL(cachedBlob)
          this.memoryBlobUrlMap.set(trimmed, blobUrl)
          return blobUrl
        }
      } catch (err) {
        console.warn('[ChatStickerService] IndexedDB 查找异常', err)
      }
    }

    // 3. 发起网络请求获取 Blob
    if (typeof fetch !== 'undefined') {
      const controller = typeof AbortController !== 'undefined' ? new AbortController() : null
      const timer = controller ? setTimeout(() => controller.abort(), 6000) : undefined
      try {
        const resp = await fetch(trimmed, {
          mode: 'cors',
          signal: controller?.signal,
        })
        clearTimeout(timer)
        if (resp.ok) {
          const blob = await resp.blob()
          if (blob && blob.size > 0) {
            // 存入 IndexedDB
            if (db) {
              await this.saveToIndexedDB(db, trimmed, blob)
            }
            if (typeof URL !== 'undefined' && URL.createObjectURL) {
              const blobUrl = URL.createObjectURL(blob)
              this.memoryBlobUrlMap.set(trimmed, blobUrl)
              return blobUrl
            }
          }
        }
      } catch {
        clearTimeout(timer)
        // CORS 拦截、超时或跨域失败，正常降级返回原 url，交由 <img> 标签直接展示
      }
    }
    return trimmed
  }

  /** 直接查询是否已有缓存 */
  async isImageCached(url: string): Promise<boolean> {
    const trimmed = url.trim()
    if (this.memoryBlobUrlMap.has(trimmed)) return true
    const db = await this.initDatabase()
    if (!db) return false
    try {
      const blob = await this.getFromIndexedDB(db, trimmed)
      return blob instanceof Blob
    } catch {
      return false
    }
  }

  private getFromIndexedDB(db: IDBDatabase, url: string): Promise<Blob | null> {
    return new Promise(resolve => {
      try {
        const tx = db.transaction(STORE_IMAGES, 'readonly')
        const store = tx.objectStore(STORE_IMAGES)
        const req = store.get(url)
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

  private saveToIndexedDB(db: IDBDatabase, url: string, blob: Blob): Promise<void> {
    return new Promise(resolve => {
      try {
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

  /** 清空内存中的 ObjectURL（防止内存泄漏） */
  releaseMemoryUrls(): void {
    for (const blobUrl of this.memoryBlobUrlMap.values()) {
      try {
        URL.revokeObjectURL(blobUrl)
      } catch {
        // ignore
      }
    }
    this.memoryBlobUrlMap.clear()
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

          // 若已在内存缓存中，无需重复拉取
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

  /**
   * 利用 requestIdleCallback 在浏览器空闲时自动调度预加载
   */
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
