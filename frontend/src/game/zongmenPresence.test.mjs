import test from 'node:test'
import assert from 'node:assert/strict'
import { formatOfflineDurationCompact, resolveZongmenMemberPresence } from './zongmenPresence.ts'

test('formatOfflineDurationCompact 按 xdxh 格式化，整天保留 0h，不足 1 天写 xh', () => {
  assert.equal(formatOfflineDurationCompact(0), '0h')
  assert.equal(formatOfflineDurationCompact(3599), '0h')
  assert.equal(formatOfflineDurationCompact(5 * 3600), '5h')
  assert.equal(formatOfflineDurationCompact(23 * 3600 + 59 * 60), '23h')
  assert.equal(formatOfflineDurationCompact(24 * 3600), '1d0h')
  assert.equal(formatOfflineDurationCompact(48 * 3600), '2d0h')
  assert.equal(formatOfflineDurationCompact(50 * 3600 + 1800), '2d2h')
  assert.equal(formatOfflineDurationCompact(72 * 3600), '3d0h')
  assert.equal(formatOfflineDurationCompact((15 * 24 + 9) * 3600), '15d9h')
  assert.equal(formatOfflineDurationCompact(-100), '0h')
  assert.equal(formatOfflineDurationCompact(Number.NaN), '0h')
})

test('resolveZongmenMemberPresence 严格遵循服务端三态，不硬编码离线时长阈值', () => {
  assert.deepEqual(
    resolveZongmenMemberPresence({ online: true, presenceStatus: 'online', offlineDurationSeconds: null }),
    { status: 'online', compactOfflineDuration: null },
  )
  // 挂机中即使携带历史值也不显示离线时长
  assert.deepEqual(
    resolveZongmenMemberPresence({ online: false, presenceStatus: 'idle', offlineDurationSeconds: 10000 }),
    { status: 'idle', compactOfflineDuration: null },
  )
  // 刚满最大离线挂机时长（如当前 48h）即从 2d0h 起算
  assert.deepEqual(
    resolveZongmenMemberPresence({ online: false, presenceStatus: 'offline', offlineDurationSeconds: 48 * 3600 }),
    { status: 'offline', compactOfflineDuration: '2d0h' },
  )
  // 未来若最大离线时长调整为非 48h（例如 72h+ 或更短），仍直接按服务端秒数渲染
  assert.deepEqual(
    resolveZongmenMemberPresence({ online: false, presenceStatus: 'offline', offlineDurationSeconds: 75 * 3600 }),
    { status: 'offline', compactOfflineDuration: '3d3h' },
  )
  assert.deepEqual(
    resolveZongmenMemberPresence({ online: false, presenceStatus: 'offline', offlineDurationSeconds: 12 * 3600 }),
    { status: 'offline', compactOfflineDuration: '12h' },
  )
  // 缺少秒数时降级为无时长离线
  assert.deepEqual(
    resolveZongmenMemberPresence({ online: false, presenceStatus: 'offline', offlineDurationSeconds: null }),
    { status: 'offline', compactOfflineDuration: null },
  )
})
