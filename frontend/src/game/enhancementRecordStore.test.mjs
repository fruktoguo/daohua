import test from 'node:test'
import assert from 'node:assert/strict'
import {
  ingestStagedEnhancements,
  getEnhancementStats,
} from './enhancementRecordStore.ts'

test('enhancementRecordStore: 暂存记录摄入、已完成项累加归档、进行中项动态覆盖与完成项回执清单输出', () => {
  const playerId = 'test-player-' + Date.now()
  const swordId = 'equipment.test-sword'
  const actionA = 'action-a-uuid'
  const actionB = 'action-b-uuid'

  // 1. 模拟收到正在进行中的行动 A（尚未完成）：+1 尝试 10 次成功 7 次
  const ack1 = ingestStagedEnhancements(playerId, [
    {
      actionId: actionA,
      itemId: swordId,
      levels: { 1: { attempts: 10, successes: 7 } },
      isCompleted: false,
      updatedAtUnixSeconds: 1000,
    },
  ])

  // 未完成行动不触发 Ack 清理
  assert.equal(ack1.length, 0)

  // 读取此时的聚合战绩：+1 应该是 10 次，成了 7 次，成功率 70%
  let stats = getEnhancementStats(playerId, swordId)
  assert.equal(stats[1].attempts, 10)
  assert.equal(stats[1].successes, 7)
  assert.equal(stats[1].successRate, 0.7)

  // 2. 模拟进行中行动 A 又强了几次，再次收到更新（尝试 15 次，成功 10 次，仍未完成）
  const ack2 = ingestStagedEnhancements(playerId, [
    {
      actionId: actionA,
      itemId: swordId,
      levels: { 1: { attempts: 15, successes: 10 } },
      isCompleted: false,
      updatedAtUnixSeconds: 1010,
    },
  ])

  // 依然不触发 Ack
  assert.equal(ack2.length, 0)

  // 验证：动态覆盖未完成项，绝对不出现 10 + 15 = 25 的重复累加！
  stats = getEnhancementStats(playerId, swordId)
  assert.equal(stats[1].attempts, 15)
  assert.equal(stats[1].successes, 10)
  assert.equal(+(stats[1].successRate).toFixed(4), +(10 / 15).toFixed(4))

  // 3. 模拟行动 A 最终完成（isCompleted: true，终态 20 次成了 14 次），且新行动 B 开始（进行中，+2 尝试 5 次成了 2 次）
  const ack3 = ingestStagedEnhancements(playerId, [
    {
      actionId: actionA,
      itemId: swordId,
      levels: { 1: { attempts: 20, successes: 14 } },
      isCompleted: true,
      updatedAtUnixSeconds: 1020,
    },
    {
      actionId: actionB,
      itemId: swordId,
      levels: { 2: { attempts: 5, successes: 2 } },
      isCompleted: false,
      updatedAtUnixSeconds: 1025,
    },
  ])

  // 4. 验证：行动 A 已完成，返回且仅返回已完成的 actionA 供回发 Ack
  assert.deepEqual(ack3, [actionA])

  // 5. 验证综合统计：
  // +1（已归档入本地历史）：20 次，14 次成功 (70%)
  // +2（当前进行中暂存）：5 次，2 次成功 (40%)
  stats = getEnhancementStats(playerId, swordId)
  assert.equal(stats[1].attempts, 20)
  assert.equal(stats[1].successes, 14)
  assert.equal(stats[1].successRate, 0.7)
  assert.equal(stats[2].attempts, 5)
  assert.equal(stats[2].successes, 2)
  assert.equal(stats[2].successRate, 0.4)
})
