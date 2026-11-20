import test from 'node:test'
import assert from 'node:assert/strict'
import { collapseLogEntries, filterLogEntries, gameLogService, parseLogTemplate } from './gameLogService.ts'
import { notifyIncomingLogEvents, useLogUnreadStore } from './logUnreadStore.ts'

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
      template: '获得 {{item:ningxue-grass|凝气草|1}}',
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

test('gameLogService: 自动使用与持续时间耗尽等高频消耗品日志静默过滤不入库', () => {
  const playerId = 'test-player-filter-' + Date.now()
  gameLogService.activate(playerId)

  gameLogService.ingestBatch(playerId, [
    { sequence: 1, timestampUnixMilliseconds: 1000, template: '自动使用 引气丹，可生效 300 秒。', channel: 'system' },
    { sequence: 2, timestampUnixMilliseconds: 1001, template: '完成行动 {{action:cultivation.draw-qi|引气入体}}', channel: 'system' },
    { sequence: 3, timestampUnixMilliseconds: 1002, template: '引气丹 的持续时间已耗尽。', channel: 'system' },
  ])

  const snapshot = gameLogService.getSnapshot()
  assert.equal(snapshot.latest.length, 1)
  assert.equal(snapshot.latest[0].template, '完成行动 {{action:cultivation.draw-qi|引气入体}}')
})

test('gameLogService：重连累计回放只补离线新增次数，不重复累加已实时收到的前半段', () => {
  const playerId = 'test-log-reconnect-overlap'
  gameLogService.activate(playerId)
  gameLogService.ingestBatch(playerId, [
    { sequence: 25, timestampUnixMilliseconds: 25000, template: '完成采集', count: 25, channel: 'system' },
  ])
  gameLogService.ingestBatch(playerId, [
    { sequence: 26, timestampUnixMilliseconds: 26000, template: '完成采集', count: 26, channel: 'system' },
  ])
  const replay = [{ sequence: 28, timestampUnixMilliseconds: 28000, template: '完成采集', count: 28, channel: 'system' }]
  gameLogService.ingestSnapshot(playerId, replay)
  gameLogService.ingestBatch(playerId, replay)

  assert.equal(gameLogService.getSnapshot().latest[0].count, 28)
  assert.equal(gameLogService.getSnapshot().latest[0].latestTimestampMs, 28000)
})

test('gameLogService：真实批量日志与序号缺口在重放时各累计一次', () => {
  const playerId = 'test-log-batch-overlap'
  gameLogService.activate(playerId)
  gameLogService.ingestBatch(playerId, [
    { sequence: 4, timestampUnixMilliseconds: 4000, template: '采集', count: 4, channel: 'system' },
    { sequence: 9, timestampUnixMilliseconds: 9000, template: '采集', count: 3, channel: 'system' },
  ])
  assert.equal(gameLogService.getSnapshot().latest[0].count, 7)

  gameLogService.ingestBatch(playerId, [
    { sequence: 10, timestampUnixMilliseconds: 10000, template: '采集', count: 10, channel: 'system' },
  ])
  assert.equal(gameLogService.getSnapshot().latest[0].count, 10)
  gameLogService.ingestBatch(playerId, [
    { sequence: 12, timestampUnixMilliseconds: 12000, template: '采集', count: 2, channel: 'system' },
  ])
  assert.equal(gameLogService.getSnapshot().latest[0].count, 12)
})

test('gameLogService：跨模板频道的旧回放不新增条目，切换玩家后独立重新计数', () => {
  const firstPlayer = 'test-log-scope-first'
  gameLogService.activate(firstPlayer)
  const events = [
    { sequence: 3, timestampUnixMilliseconds: 3000, template: '采集', count: 3, channel: 'system' },
    { sequence: 5, timestampUnixMilliseconds: 5000, template: '施放', count: 2, channel: 'combat' },
  ]
  gameLogService.ingestBatch(firstPlayer, events)
  gameLogService.ingestBatch(firstPlayer, [
    { sequence: 2, timestampUnixMilliseconds: 2000, template: '采集', count: 2, channel: 'system' },
    ...events,
  ])
  assert.deepEqual(gameLogService.getSnapshot().latest.map(entry => entry.count), [3, 2])

  const secondPlayer = 'test-log-scope-second'
  gameLogService.ingestBatch(secondPlayer, events)
  assert.deepEqual(gameLogService.getSnapshot().latest.map(entry => entry.count), [3, 2])
})

test('gameLogService：原始表现订阅与未读计数共同消费去重后的真实新增次数', () => {
  const playerId = 'test-log-replay-consumers'
  gameLogService.activate(playerId)
  useLogUnreadStore.getState().resetAll()
  useLogUnreadStore.getState().setActiveGroup('world')
  const delivered = []
  const unsubscribe = gameLogService.subscribeRawBatch((id, events) => delivered.push({ id, events }))
  const receive = events => notifyIncomingLogEvents(gameLogService.ingestBatch(playerId, events))
  try {
    receive([{ sequence: 25, timestampUnixMilliseconds: 25000, template: '采集', count: 25, channel: 'system' }])
    const replay = [{ sequence: 26, timestampUnixMilliseconds: 26000, template: '采集', count: 26, channel: 'system' }]
    receive(replay)
    receive(replay)
    receive([{ sequence: 29, timestampUnixMilliseconds: 29000, template: '施放', count: 3, channel: 'combat' }])

    assert.deepEqual(delivered.map(batch => batch.events.map(event => event.count)), [[25], [1], [3]])
    assert.equal(useLogUnreadStore.getState().unreadCounts.system, 26)
    assert.equal(useLogUnreadStore.getState().unreadCounts.all, 26)
    assert.equal(useLogUnreadStore.getState().unreadCounts.combat, 0)
    assert.deepEqual(gameLogService.getSnapshot().latest.map(entry => entry.count), [26, 3])
  } finally {
    unsubscribe()
  }
})

test('gameLogService：同玩家日志缓冲重建后接收新流低序号，并保留旧显示历史', () => {
  const playerId = 'test-log-stream-recreated'
  gameLogService.activate(playerId)
  const oldStream = '49f35f6d-4fa8-4591-81f2-2376be7f5256'
  const newStream = '827c9ce8-1835-4b95-bf6e-730eebcf0c17'
  const oldEvents = [{ streamId: oldStream, sequence: 9, timestampUnixMilliseconds: 9000, template: '采集', count: 9, channel: 'system' }]
  const newEvents = [{ streamId: newStream, sequence: 2, timestampUnixMilliseconds: 20000, template: '采集', count: 2, channel: 'system' }]
  gameLogService.ingestBatch(playerId, oldEvents)
  gameLogService.ingestBatch(playerId, newEvents)
  assert.deepEqual(gameLogService.ingestBatch(playerId, newEvents), [])
  gameLogService.ingestBatch(playerId, [
    { streamId: newStream, sequence: 4, timestampUnixMilliseconds: 22000, template: '采集', count: 4, channel: 'system' },
  ])

  const entries = gameLogService.getSnapshot().latest
  assert.deepEqual(entries.map(entry => [entry.streamId, entry.count]), [[oldStream, 9], [newStream, 4]])
  assert.notEqual(entries[0].id, entries[1].id)
  assert.ok(entries[0].id.includes(oldStream))
  assert.ok(entries[1].id.includes(newStream))
})

test('gameLogService：同版本重启的新流序号和旧流一致时仍推进未读与表现一次', () => {
  const playerId = 'test-log-stream-restarted'
  gameLogService.activate(playerId)
  useLogUnreadStore.getState().resetAll()
  useLogUnreadStore.getState().setActiveGroup('world')
  const counts = []
  const unsubscribe = gameLogService.subscribeRawBatch((_, events) => counts.push(...events.map(event => event.count)))
  const receive = (streamId, sequence, count) => notifyIncomingLogEvents(gameLogService.ingestBatch(playerId, [
    { streamId, sequence, count, timestampUnixMilliseconds: 1000, template: '采集', channel: 'system' },
  ]))
  try {
    receive('stream-before-restart', 1, 1)
    receive('stream-after-restart', 1, 1)
    receive('stream-after-restart', 1, 1)
    receive('stream-after-restart', 3, 3)
    assert.deepEqual(counts, [1, 1, 2])
    assert.equal(useLogUnreadStore.getState().unreadCounts.system, 4)
    assert.deepEqual(gameLogService.getSnapshot().latest.map(entry => entry.count), [1, 3])
  } finally {
    unsubscribe()
  }
})

test('parseLogTemplate：引用缺少名称时仍保留实体 ID，供展示层恢复名称', () => {
  assert.deepEqual(parseLogTemplate('强化成功：{{item:equipment.juling-pendant|}} 提升至 +1。'), [
    { type: 'text', value: '强化成功：' },
    { type: 'reference', kind: 'item', id: 'equipment.juling-pendant', label: '' },
    { type: 'text', value: ' 提升至 +1。' },
  ])
})

test('parseLogTemplate：正确解析特质、建筑、状态、行动、地点与数量引用', () => {
  const segments = parseLogTemplate(
    '激活 {{trait:trait.jianyuxin|剑与心}} 付与 {{buff:buff.yixing|移形}}，建造 {{building:building.mana-regen-array|回元阵}} 于 {{location:node.rock-beach|清石水滩}} 并执行 {{action:cultivation.ningqi-guiyuan|凝气归元}} 获得 {{item:item.zhixue-powder|止血散|3}}',
  )
  const refs = segments.filter(segment => segment.type === 'reference')
  assert.deepEqual(refs, [
    { type: 'reference', kind: 'trait', id: 'trait.jianyuxin', label: '剑与心' },
    { type: 'reference', kind: 'buff', id: 'buff.yixing', label: '移形' },
    { type: 'reference', kind: 'building', id: 'building.mana-regen-array', label: '回元阵' },
    { type: 'reference', kind: 'location', id: 'node.rock-beach', label: '清石水滩' },
    { type: 'reference', kind: 'action', id: 'cultivation.ningqi-guiyuan', label: '凝气归元' },
    { type: 'reference', kind: 'item', id: 'item.zhixue-powder', label: '止血散', amount: 3, extra: '3' },
  ])
})


