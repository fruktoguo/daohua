import test from 'node:test'
import assert from 'node:assert/strict'
import { DEFAULT_STAGED_SETTLE_SECONDS, summarizeStagedLoot } from './stagedLoot.ts'

test('summarizeStagedLoot: 空列表或无有效数量条目返回 null', () => {
  assert.equal(summarizeStagedLoot([], 1000), null)
  assert.equal(
    summarizeStagedLoot([
      { itemId: 'item.rough-wolf-hide', quantity: 0, acquiredAtUnixSeconds: 1000 },
      { itemId: 'item.beast-bone', quantity: -3, acquiredAtUnixSeconds: 1000 },
      { itemId: '', quantity: 5, acquiredAtUnixSeconds: 1000 },
    ], 1000),
    null,
  )
})

test('summarizeStagedLoot: 多桶同类物品跨桶聚合且准确计算批次数与最早/最晚倒计时', () => {
  // 模拟玩家在 18:40 / 18:45 / 18:50 三个 5 分钟桶暂存狼皮与兽骨，当前时刻为 18:55
  const bucket1 = 10000
  const bucket2 = 10300
  const bucket3 = 10600
  const now = bucket1 + 900 // 距 bucket1 到账还剩 300s，距 bucket3 到账还剩 900s

  const summary = summarizeStagedLoot(
    [
      { itemId: 'item.rough-wolf-hide', quantity: 10, acquiredAtUnixSeconds: bucket1 },
      { itemId: 'item.beast-bone', quantity: 5, acquiredAtUnixSeconds: bucket1 },
      { itemId: 'item.rough-wolf-hide', quantity: 9, acquiredAtUnixSeconds: bucket2 },
      { itemId: 'item.beast-bone', quantity: 5, acquiredAtUnixSeconds: bucket2 },
      { itemId: 'item.rough-wolf-hide', quantity: 7, acquiredAtUnixSeconds: bucket3 },
      { itemId: 'item.beast-bone', quantity: 7, acquiredAtUnixSeconds: bucket3 },
    ],
    now,
    DEFAULT_STAGED_SETTLE_SECONDS,
  )

  assert.ok(summary)
  assert.equal(summary.batchCount, 3)
  assert.equal(summary.nextSettleAtUnixSeconds, bucket1 + 1200)
  assert.equal(summary.allSettledAtUnixSeconds, bucket3 + 1200)
  assert.equal(summary.nextSettleRemainingSeconds, 300)
  assert.equal(summary.allSettledRemainingSeconds, 900)

  assert.equal(summary.items.length, 2)
  assert.deepEqual(summary.items[0], {
    itemId: 'item.rough-wolf-hide',
    quantity: 26,
    earliestSettleAtUnixSeconds: bucket1 + 1200,
    latestSettleAtUnixSeconds: bucket3 + 1200,
    nextRemainingSeconds: 300,
  })
  assert.deepEqual(summary.items[1], {
    itemId: 'item.beast-bone',
    quantity: 17,
    earliestSettleAtUnixSeconds: bucket1 + 1200,
    latestSettleAtUnixSeconds: bucket3 + 1200,
    nextRemainingSeconds: 300,
  })
})

test('summarizeStagedLoot: 已超过结算点的批次剩余时间钳位至 0', () => {
  const summary = summarizeStagedLoot(
    [{ itemId: 'item.rough-wolf-hide', quantity: 4, acquiredAtUnixSeconds: 1000 }],
    5000,
    1200,
  )

  assert.ok(summary)
  assert.equal(summary.batchCount, 1)
  assert.equal(summary.nextSettleRemainingSeconds, 0)
  assert.equal(summary.allSettledRemainingSeconds, 0)
  assert.equal(summary.items[0].nextRemainingSeconds, 0)
})
