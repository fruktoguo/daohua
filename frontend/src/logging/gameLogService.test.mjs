import test from 'node:test'
import assert from 'node:assert/strict'
import { collapseLogEntries, gameLogService } from './gameLogService.ts'

test('collapseLogEntries 纯函数：连续完全相同模板的日志正确合并折叠', () => {
  const entries = [
    { id: '1', playerId: 'p1', timestampMs: 1000, sequence: 1, template: '获得 {{item:herb.spirit-grass|伴灵草}} × 1' },
    { id: '2', playerId: 'p1', timestampMs: 2000, sequence: 2, template: '获得 {{item:herb.spirit-grass|伴灵草}} × 1' },
    { id: '3', playerId: 'p1', timestampMs: 3000, sequence: 3, template: '获得 {{item:herb.spirit-grass|伴灵草}} × 1' },
  ]

  const collapsed = collapseLogEntries(entries)
  assert.equal(collapsed.length, 1)
  assert.equal(collapsed[0].count, 3)
  assert.equal(collapsed[0].timestampMs, 1000)
  assert.equal(collapsed[0].latestTimestampMs, 3000)
  assert.equal(collapsed[0].template, '获得 {{item:herb.spirit-grass|伴灵草}} × 1')
})

test('collapseLogEntries 纯函数：中间穿插不同日志时断开折叠，开启新条目', () => {
  const entries = [
    { id: '1', playerId: 'p1', timestampMs: 1000, sequence: 1, template: '获得 {{item:herb.spirit-grass|伴灵草}} × 1' },
    { id: '2', playerId: 'p1', timestampMs: 2000, sequence: 2, template: '获得 {{item:herb.spirit-grass|伴灵草}} × 1' },
    { id: '3', playerId: 'p1', timestampMs: 3000, sequence: 3, template: '击败妖兽 {{enemy:rift-wolf|裂隙魔狼}}' },
    { id: '4', playerId: 'p1', timestampMs: 4000, sequence: 4, template: '获得 {{item:herb.spirit-grass|伴灵草}} × 1' },
  ]

  const collapsed = collapseLogEntries(entries)
  assert.equal(collapsed.length, 3)
  assert.equal(collapsed[0].count, 2)
  assert.equal(collapsed[0].latestTimestampMs, 2000)
  assert.equal(collapsed[1].count, 1)
  assert.equal(collapsed[1].template, '击败妖兽 {{enemy:rift-wolf|裂隙魔狼}}')
  assert.equal(collapsed[2].count, 1)
  assert.equal(collapsed[2].timestampMs, 4000)
})

test('collapseLogEntries 边界条件：空数组与单条日志', () => {
  assert.deepEqual(collapseLogEntries([]), [])

  const single = [{ id: '1', playerId: 'p1', timestampMs: 1000, sequence: 1, template: '突破境界成功' }]
  const res = collapseLogEntries(single)
  assert.equal(res.length, 1)
  assert.equal(res[0].count, 1)
  assert.equal(res[0].latestTimestampMs, 1000)
})

test('gameLogService 单例服务：增量接收连续重复项合并递增与容量保持', () => {
  gameLogService.activate('test-player-1')
  
  // 批次 1: 连续 3 条相同采集
  gameLogService.ingestBatch('test-player-1', [
    { sequence: 1, timestampUnixMilliseconds: 1000, template: '获得伴灵草' },
    { sequence: 2, timestampUnixMilliseconds: 2000, template: '获得伴灵草' },
    { sequence: 3, timestampUnixMilliseconds: 3000, template: '获得伴灵草' },
  ])

  let snapshot = gameLogService.getSnapshot()
  assert.equal(snapshot.latest.length, 1)
  assert.equal(snapshot.latest[0].count, 3)
  assert.equal(snapshot.latest[0].timestampMs, 1000)
  assert.equal(snapshot.latest[0].latestTimestampMs, 3000)

  // 批次 2: 再次传入 2 条相同采集，累计递增到 5
  gameLogService.ingestBatch('test-player-1', [
    { sequence: 4, timestampUnixMilliseconds: 4000, template: '获得伴灵草' },
    { sequence: 5, timestampUnixMilliseconds: 5000, template: '获得伴灵草' },
  ])

  snapshot = gameLogService.getSnapshot()
  assert.equal(snapshot.latest.length, 1)
  assert.equal(snapshot.latest[0].count, 5)
  assert.equal(snapshot.latest[0].latestTimestampMs, 5000)

  // 批次 3: 插入一条不同日志
  gameLogService.ingestBatch('test-player-1', [
    { sequence: 6, timestampUnixMilliseconds: 6000, template: '遭遇强敌' },
  ])

  snapshot = gameLogService.getSnapshot()
  assert.equal(snapshot.latest.length, 2)
  assert.equal(snapshot.latest[0].count, 5)
  assert.equal(snapshot.latest[1].count, 1)
  assert.equal(snapshot.latest[1].template, '遭遇强敌')

  // 重复推送已有 sequence 必须幂等跳过
  gameLogService.ingestBatch('test-player-1', [
    { sequence: 5, timestampUnixMilliseconds: 5000, template: '获得伴灵草' },
  ])
  snapshot = gameLogService.getSnapshot()
  assert.equal(snapshot.latest.length, 2)
  assert.equal(snapshot.latest[0].count, 5)
})
