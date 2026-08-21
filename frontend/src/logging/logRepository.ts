export interface StoredGameLog {
  id: string
  playerId: string
  timestampMs: number
  sequence: number
  template: string
}

export interface GameLogCursor {
  timestampMs: number
  sequence: number
}

const DATABASE_NAME = 'daoxuan-local-history'
const DATABASE_VERSION = 1
const LOG_STORE = 'logs'
const PLAYER_TIME_INDEX = 'playerTime'

function transactionDone(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.addEventListener('complete', () => resolve(), { once: true })
    transaction.addEventListener('abort', () => reject(transaction.error ?? new Error('本地日志事务已中止。')), { once: true })
    transaction.addEventListener('error', () => reject(transaction.error ?? new Error('本地日志事务失败。')), { once: true })
  })
}

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
        store.createIndex(PLAYER_TIME_INDEX, ['playerId', 'timestampMs', 'sequence'], { unique: false })
      }
    })
    request.addEventListener('success', () => resolve(request.result), { once: true })
    request.addEventListener('error', () => reject(request.error ?? new Error('无法打开本地日志库。')), { once: true })
    request.addEventListener('blocked', () => reject(new Error('本地日志库升级被其他页面阻塞。')), { once: true })
  })
}

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

export class GameLogRepository {
  private readonly database = openDatabase()

  async appendBatch(entries: readonly StoredGameLog[]): Promise<void> {
    if (entries.length === 0) return
    const database = await this.database
    const transaction = database.transaction(LOG_STORE, 'readwrite')
    const store = transaction.objectStore(LOG_STORE)
    for (const entry of entries) store.put(entry)
    await transactionDone(transaction)
  }

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
