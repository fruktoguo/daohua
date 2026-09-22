import test from 'node:test'
import assert from 'node:assert/strict'
import { collapseLogEntries, filterLogEntries, gameLogService } from './gameLogService.ts'

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

test('日志分组：系统与战斗频道即使模板相同也不折叠，filterLogEntries 按频道分流', () => {
  const entries = [
    { id: '1', playerId: 'p1', timestampMs: 1000, sequence: 1, template: '同一模板', channel: 'system' },
    { id: '2', playerId: 'p1', timestampMs: 2000, sequence: 2, template: '同一模板', channel: 'combat' },
    { id: '3', playerId: 'p1', timestampMs: 3000, sequence: 3, template: '同一模板', channel: 'combat' },
  ]

  const collapsed = collapseLogEntries(entries)
  assert.equal(collapsed.length, 2)
  assert.equal(collapsed[0].channel, 'system')
  assert.equal(collapsed[0].count, 1)
  assert.equal(collapsed[1].channel, 'combat')
  assert.equal(collapsed[1].count, 2)

  assert.deepEqual(filterLogEntries(collapsed, 'system').map(entry => entry.id), ['1'])
  assert.deepEqual(filterLogEntries(collapsed, 'combat').map(entry => entry.id), ['2'])
})

test('日志分组：缺省 channel 视为 system，combat 事件不落入系统分组', () => {
  gameLogService.activate('test-player-channel')
  gameLogService.ingestBatch('test-player-channel', [
    { sequence: 1, timestampUnixMilliseconds: 1000, template: '自动使用回气丹。' },
    { sequence: 2, timestampUnixMilliseconds: 2000, template: '遭遇妖兽：裂隙魔狼。', channel: 'combat' },
  ])

  const snapshot = gameLogService.getSnapshot()
  assert.deepEqual(filterLogEntries(snapshot.latest, 'system').map(entry => entry.channel), ['system'])
  assert.deepEqual(filterLogEntries(snapshot.latest, 'combat').map(entry => entry.channel), ['combat'])
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

test('gameLogService: 200 条相同日志合并后严格只占 1 条历史记录槽位，完整保留之前的历史记录', () => {
  const playerId = 'test-player-history-' + Date.now()
  gameLogService.activate(playerId)

  // 1. 先写入 10 条不同的历史事件
  for (let i = 1; i <= 10; i++) {
    gameLogService.ingestBatch(playerId, [
      { sequence: i, timestampUnixMilliseconds: 1000 + i * 10, template: `历史日志_${i}` }
    ])
  }
  let snapshot = gameLogService.getSnapshot()
  assert.equal(snapshot.latest.length, 10)

  // 2. 模拟连续涌入 200 条完全相同的采草日志
  const repeated = []
  for (let i = 11; i <= 210; i++) {
    repeated.push({
      sequence: i,
      timestampUnixMilliseconds: 2000 + i * 10,
      template: '获得 {{item:ningxue-grass|凝血草|1}}',
    })
  }
  gameLogService.ingestBatch(playerId, repeated)

  snapshot = gameLogService.getSnapshot()
  // 3. 核心断言：这 200 条相同日志合并后只占 1 条槽位，加上之前的 10 条，总共只有 11 条！
  assert.equal(snapshot.latest.length, 11)
  assert.equal(snapshot.latest[0].template, '历史日志_1')
  assert.equal(snapshot.latest[10].count, 200)
  assert.equal(snapshot.latest[10].latestTimestampMs, 2000 + 210 * 10)
})

