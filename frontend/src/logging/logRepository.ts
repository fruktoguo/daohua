/**
 * 存储在本地 IndexedDB 中的原始日志数据结构
 */
export interface StoredGameLog {
  /** 日志唯一 ID (格式如 "${playerId}:server:${sequence}") */
  id: string
  /** 归属玩家 ID */
  playerId: string
  /** 发生时间戳（毫秒） */
  timestampMs: number
  /** 事件全局序列号 */
  sequence: number
  /** 原始日志模板字符串 */
  template: string
}

/**
 * 分页游标定义（按时间戳与序列号复合检索）
 */
export interface GameLogCursor {
  timestampMs: number
  sequence: number
}

const DATABASE_NAME = 'daohua-local-history'
const DATABASE_VERSION = 1
const LOG_STORE = 'logs'
const PLAYER_TIME_INDEX = 'playerTime'

/**
 * 等待 IndexedDB 事务完成的 Promise 包装
 */
function transactionDone(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.addEventListener('complete', () => resolve(), { once: true })
    transaction.addEventListener('abort', () => reject(transaction.error ?? new Error('本地日志事务已中止。')), { once: true })
    transaction.addEventListener('error', () => reject(transaction.error ?? new Error('本地日志事务失败。')), { once: true })
  })
}

/**
 * 初始化并打开 IndexedDB 本地日志数据库
 */
function openDatabase(): Promise<IDBDatabase> {
  if (!('indexedDB' in window)) return Promise.reject(new Error('当前浏览器不支持 IndexedDB。'))
  return new Promise((resolve, reject) => {
    const request = window.indexedDB.open(DATABASE_NAME, DATABASE_VERSION)
    request.addEventListener('upgradeneeded', () => {
      const database = request.result
      const store = database.objectStoreNames.contains(LOG_STORE)
        ? request.transaction?.objectStore(LOG_STORE)
        : database.createObjectStore(LOG_STORE, { keyPath: 'id' })
      if (store && !store.indexNames.contains(PLAYER_TIME_INDEX)) {
        // 创建复合索引，支持按玩家、时间戳与序列号快速倒序查询
        store.createIndex(PLAYER_TIME_INDEX, ['playerId', 'timestampMs', 'sequence'], { unique: false })
      }
    })
    request.addEventListener('success', () => resolve(request.result), { once: true })
    request.addEventListener('error', () => reject(request.error ?? new Error('无法打开本地日志库。')), { once: true })
    request.addEventListener('blocked', () => reject(new Error('本地日志库升级被其他页面阻塞。')), { once: true })
  })
}

/**
 * 逆序读取游标范围内的指定条数记录并按时间升序返回
 */
function readCursor(
  index: IDBIndex,
  range: IDBKeyRange,
  limit: number,
): Promise<StoredGameLog[]> {
  return new Promise((resolve, reject) => {
    const entries: StoredGameLog[] = []
    const request = index.openCursor(range, 'prev')
    request.addEventListener('success', () => {
      const cursor = request.result
      if (!cursor || entries.length >= limit) {
        entries.reverse()
        resolve(entries)
        return
      }
      entries.push(cursor.value as StoredGameLog)
      cursor.continue()
    })
    request.addEventListener('error', () => reject(request.error ?? new Error('读取本地日志失败。')), { once: true })
  })
}

/**
 * 本地 IndexedDB 日志存储仓储
 */
export class GameLogRepository {
  private readonly database = openDatabase()

  /**
   * 批量持久化日志记录
   */
  async appendBatch(entries: readonly StoredGameLog[]): Promise<void> {
    if (entries.length === 0) return
    const database = await this.database
    const transaction = database.transaction(LOG_STORE, 'readwrite')
    const store = transaction.objectStore(LOG_STORE)
    for (const entry of entries) store.put(entry)
    await transactionDone(transaction)
  }

  /**
   * 加载指定玩家最新的 limit 条日志
   */
  async loadLatest(playerId: string, limit: number): Promise<StoredGameLog[]> {
    const database = await this.database
    const transaction = database.transaction(LOG_STORE, 'readonly')
    const index = transaction.objectStore(LOG_STORE).index(PLAYER_TIME_INDEX)
    const range = IDBKeyRange.bound(
      [playerId, 0, Number.MIN_SAFE_INTEGER],
      [playerId, Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER],
    )
    return readCursor(index, range, limit)
  }

  /**
   * 分页向上拉取早于游标 cursor 的历史日志记录
   */
  async loadBefore(playerId: string, cursor: GameLogCursor, limit: number): Promise<StoredGameLog[]> {
    const database = await this.database
    const transaction = database.transaction(LOG_STORE, 'readonly')
    const index = transaction.objectStore(LOG_STORE).index(PLAYER_TIME_INDEX)
    const range = IDBKeyRange.bound(
      [playerId, 0, Number.MIN_SAFE_INTEGER],
      [playerId, cursor.timestampMs, cursor.sequence],
      false,
      true,
    )
    return readCursor(index, range, limit)
  }
}
