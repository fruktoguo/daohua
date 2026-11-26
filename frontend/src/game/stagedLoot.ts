import type { DuelStagedItem } from './types'

/** 斗法暂存区默认延迟入账秒数（与配表 config/game-data/duel.json 的 stagedSettleSeconds = 1200 对齐） */
export const DEFAULT_STAGED_SETTLE_SECONDS = 1200

/** 按 itemId 聚合后的单个暂存物品摘要 */
export interface StagedLootItemSummary {
  /** 物品定义 ID */
  readonly itemId: string
  /** 跨桶累计的待入袋总数量 */
  readonly quantity: number
  /** 该物品最早一批到账的服务端 Unix 秒 */
  readonly earliestSettleAtUnixSeconds: number
  /** 该物品最晚一批到账的服务端 Unix 秒 */
  readonly latestSettleAtUnixSeconds: number
  /** 该物品距离下一批到账的剩余秒数（非负） */
  readonly nextRemainingSeconds: number
}

/** 整个斗法暂存区的聚合视图，供储物袋、统计面板与记录页共享消费 */
export interface StagedLootSummary {
  /** 按 itemId 首次出现顺序聚合的物品清单 */
  readonly items: readonly StagedLootItemSummary[]
  /** 不同桶尾去重后的待入账批次数 */
  readonly batchCount: number
  /** 最早一批自动入账的服务端 Unix 秒 */
  readonly nextSettleAtUnixSeconds: number
  /** 最后一批全部入账的服务端 Unix 秒 */
  readonly allSettledAtUnixSeconds: number
  /** 距离下一批自动入账的剩余秒数（非负） */
  readonly nextSettleRemainingSeconds: number
  /** 距离全部暂存物品入账的剩余秒数（非负） */
  readonly allSettledRemainingSeconds: number
}

/**
 * 汇总斗法暂存区条目为界面展示模型：
 * - 按 itemId 合并跨桶数量，同时记录最早/最晚到期时刻；
 * - 按桶尾时间戳（acquiredAtUnixSeconds）统计独立批次数；
 * - 无有效条目时返回 null，调用方可据此整块跳过渲染。
 */
export function summarizeStagedLoot(
  stagedItems: readonly DuelStagedItem[],
  nowUnixSeconds: number,
  settleSeconds: number = DEFAULT_STAGED_SETTLE_SECONDS,
): StagedLootSummary | null {
  if (stagedItems.length === 0) return null

  const safeNow = Number.isFinite(nowUnixSeconds) ? nowUnixSeconds : 0
  const safeSettle = Number.isFinite(settleSeconds) && settleSeconds >= 0 ? settleSeconds : DEFAULT_STAGED_SETTLE_SECONDS

  const aggregated = new Map<string, { quantity: number; earliestSettleAt: number; latestSettleAt: number }>()
  const batchBuckets = new Set<number>()
  let globalEarliest = Number.POSITIVE_INFINITY
  let globalLatest = Number.NEGATIVE_INFINITY

  for (const entry of stagedItems) {
    if (!entry.itemId || !Number.isFinite(entry.quantity) || entry.quantity <= 0) continue
    if (!Number.isFinite(entry.acquiredAtUnixSeconds)) continue

    const settleAt = entry.acquiredAtUnixSeconds + safeSettle
    batchBuckets.add(entry.acquiredAtUnixSeconds)
    if (settleAt < globalEarliest) globalEarliest = settleAt
    if (settleAt > globalLatest) globalLatest = settleAt

    const existing = aggregated.get(entry.itemId)
    if (existing) {
      existing.quantity += entry.quantity
      if (settleAt < existing.earliestSettleAt) existing.earliestSettleAt = settleAt
      if (settleAt > existing.latestSettleAt) existing.latestSettleAt = settleAt
    } else {
      aggregated.set(entry.itemId, {
        quantity: entry.quantity,
        earliestSettleAt: settleAt,
        latestSettleAt: settleAt,
      })
    }
  }

  if (aggregated.size === 0) return null

  const items: StagedLootItemSummary[] = []
  for (const [itemId, bucket] of aggregated) {
    items.push({
      itemId,
      quantity: bucket.quantity,
      earliestSettleAtUnixSeconds: bucket.earliestSettleAt,
      latestSettleAtUnixSeconds: bucket.latestSettleAt,
      nextRemainingSeconds: Math.max(0, bucket.earliestSettleAt - safeNow),
    })
  }

  return {
    items,
    batchCount: batchBuckets.size,
    nextSettleAtUnixSeconds: globalEarliest,
    allSettledAtUnixSeconds: globalLatest,
    nextSettleRemainingSeconds: Math.max(0, globalEarliest - safeNow),
    allSettledRemainingSeconds: Math.max(0, globalLatest - safeNow),
  }
}
