import { useSyncExternalStore } from 'react'
import type { EnhancementLevelStat, StagedEnhancementRecord } from './types.ts'

/** 本地存储键名前缀 */
const STORAGE_KEY_PREFIX = 'daohua.enhancementRecords.'

/** 聚合后的单等级统计视图 */
export interface AggregatedEnhancementLevelStat {
  /** 尝试总次数 */
  attempts: number
  /** 成功次数 */
  successes: number
  /** 成功率 (0.0 ~ 1.0) */
  successRate: number
}

/** 本地归档持久化模型 */
export interface StoredEnhancementRecords {
  /** 已结案历史总计：itemId -> level -> { attempts, successes } */
  archived: Record<string, Record<number, EnhancementLevelStat>>
  /** 仍在进行中的暂存快照：actionId -> StagedEnhancementRecord */
  staged: Record<string, StagedEnhancementRecord>
}

/** 内存缓存结构 */
interface EnhancementRecordsCache {
  data: StoredEnhancementRecords
  dirty: boolean
}

const memoryCaches = new Map<string, EnhancementRecordsCache>()
const listeners = new Set<() => void>()
let storeRevision = 0

function notifySubscribers(): void {
  storeRevision++
  for (const listener of listeners) {
    try {
      listener()
    } catch {
      // 抑制监听器异常
    }
  }
}

/**
 * 读取或初始化指定玩家的强化记录本地缓存
 */
function getOrCreateCache(playerId: string): EnhancementRecordsCache {
  if (!playerId) {
    return { data: { archived: {}, staged: {} }, dirty: false }
  }
  let cache = memoryCaches.get(playerId)
  if (cache) return cache

  let data: StoredEnhancementRecords = { archived: {}, staged: {} }
  try {
    const raw = typeof window !== 'undefined' ? window.localStorage.getItem(`${STORAGE_KEY_PREFIX}${playerId}`) : null
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<StoredEnhancementRecords>
      if (parsed && typeof parsed === 'object') {
        data = {
          archived: parsed.archived && typeof parsed.archived === 'object' ? parsed.archived : {},
          staged: parsed.staged && typeof parsed.staged === 'object' ? parsed.staged : {},
        }
      }
    }
  } catch {
    // 忽略 localStorage 损坏或隐私模式不可用
  }

  cache = { data, dirty: false }
  memoryCaches.set(playerId, cache)
  return cache
}

/**
 * 将缓存写入本地存储
 */
function flushCache(playerId: string): void {
  if (!playerId || typeof window === 'undefined') return
  const cache = memoryCaches.get(playerId)
  if (!cache || !cache.dirty) return

  try {
    window.localStorage.setItem(`${STORAGE_KEY_PREFIX}${playerId}`, JSON.stringify(cache.data))
    cache.dirty = false
  } catch {
    // 忽略配额超限或写入错误
  }
}

/**
 * 摄入服务端下发的暂存强化记录（快照/增量路径调用）：
 * 1. 遍历暂存项：
 *    - isCompleted === true：将 levels 增量合并入本地 archived，从 staged 中剔除，并记录待 Ack 列表；
 *    - isCompleted === false：更新 staged[actionId] 为最新进度；
 * 2. 写入本地 localStorage；
 * 3. 若有已完成项，向服务端发送 enhancement.ack_records 命令请求清理已完成数据。
 */
export function ingestStagedEnhancements(playerId: string, records: readonly StagedEnhancementRecord[] | undefined): readonly string[] {
  if (!playerId || !records || records.length === 0) return []

  const cache = getOrCreateCache(playerId)
  const completedToAck: string[] = []
  let changed = false

  for (const rec of records) {
    if (!rec || !rec.actionId || !rec.itemId) continue

    if (rec.isCompleted) {
      // 已完成行动：将全部 levels 累加进本地长期总账
      if (!cache.data.archived[rec.itemId]) {
        cache.data.archived[rec.itemId] = {}
      }
      const itemArchive = cache.data.archived[rec.itemId]
      for (const [lvlStr, stat] of Object.entries(rec.levels)) {
        const lvl = Number(lvlStr)
        if (!Number.isSafeInteger(lvl) || !stat || stat.attempts <= 0) continue
        if (!itemArchive[lvl]) {
          itemArchive[lvl] = { attempts: 0, successes: 0 }
        }
        itemArchive[lvl].attempts += stat.attempts
        itemArchive[lvl].successes += stat.successes
      }

      // 从进行中暂存移除
      if (cache.data.staged[rec.actionId]) {
        delete cache.data.staged[rec.actionId]
      }

      completedToAck.push(rec.actionId)
      changed = true
    } else {
      // 正在进行中：覆盖更新暂存快照
      cache.data.staged[rec.actionId] = rec
      changed = true
    }
  }

  if (changed) {
    cache.dirty = true
    flushCache(playerId)
    notifySubscribers()
  }

  return completedToAck
}

/**
 * 获取指定物品各个强化等级的综合战绩（本地历史归档 + 当前进行中暂存）：
 * 返回键为强化等级（1..20）的聚合战绩。
 */
export function getEnhancementStats(playerId: string, itemId: string): Record<number, AggregatedEnhancementLevelStat> {
  const result: Record<number, AggregatedEnhancementLevelStat> = {}
  if (!playerId || !itemId) return result

  const cache = getOrCreateCache(playerId)

  // 1. 本地已结案归档
  const archived = cache.data.archived[itemId] ?? {}
  for (const [lvlStr, stat] of Object.entries(archived)) {
    const lvl = Number(lvlStr)
    if (!Number.isSafeInteger(lvl)) continue
    result[lvl] = {
      attempts: stat.attempts,
      successes: stat.successes,
      successRate: stat.attempts > 0 ? stat.successes / stat.attempts : 0,
    }
  }

  // 2. 当前进行中的暂存快照叠加
  for (const stagedRec of Object.values(cache.data.staged)) {
    if (stagedRec.itemId !== itemId) continue
    for (const [lvlStr, stat] of Object.entries(stagedRec.levels)) {
      const lvl = Number(lvlStr)
      if (!Number.isSafeInteger(lvl)) continue
      const current = result[lvl] ?? { attempts: 0, successes: 0, successRate: 0 }
      const attempts = current.attempts + stat.attempts
      const successes = current.successes + stat.successes
      result[lvl] = {
        attempts,
        successes,
        successRate: attempts > 0 ? successes / attempts : 0,
      }
    }
  }

  return result
}
function subscribe(callback: () => void): () => void {
  listeners.add(callback)
  return () => {
    listeners.delete(callback)
  }
}
/**
 * React Hook：响应式获取指定玩家指定装备在各个强化等级的综合战绩
 */
export function useEnhancementStats(playerId: string, itemId: string | null | undefined): Record<number, AggregatedEnhancementLevelStat> {
  useSyncExternalStore(subscribe, () => storeRevision)
  if (!playerId || !itemId) return {}
  return getEnhancementStats(playerId, itemId)
}
