import test from 'node:test'
import assert from 'node:assert/strict'
import {
  LEADERBOARD_CATEGORIES,
  resolveBoardId,
  splitBoardId,
  formatLeaderboardTimestamp,
  getRemainingSecondsUntil,
} from './leaderboardHelpers.ts'

test('全服排行榜辅助：主类与技艺子榜 boardId 双向映射无损', () => {
  assert.deepEqual([...LEADERBOARD_CATEGORIES], [
    'realm',
    'technique',
    'valuation',
    'spirit-stone',
    'zongmen',
    'duel',
  ])

  assert.equal(resolveBoardId('realm', 'alchemy'), 'realm')
  assert.equal(resolveBoardId('technique', 'forging'), 'technique.forging')
  assert.equal(resolveBoardId('spirit-stone', 'alchemy'), 'spirit-stone')

  assert.deepEqual(splitBoardId('technique.alchemy'), { category: 'technique', techniqueId: 'alchemy' })
  assert.deepEqual(splitBoardId('valuation'), { category: 'valuation' })
  assert.deepEqual(splitBoardId('duel'), { category: 'duel' })
  assert.deepEqual(splitBoardId('unknown'), { category: 'realm' })
})

test('全服排行榜辅助：时间格式与剩余整点秒数非负兜底', () => {
  assert.equal(formatLeaderboardTimestamp(null), '—')
  assert.equal(formatLeaderboardTimestamp('not-a-date'), '—')
  assert.match(formatLeaderboardTimestamp('2026-11-14T12:00:00Z'), /^\d{2}-\d{2} \d{2}:\d{2}$/)

  const base = Date.parse('2026-11-14T12:00:00Z')
  assert.equal(getRemainingSecondsUntil('2026-11-14T12:05:30Z', base), 330)
  assert.equal(getRemainingSecondsUntil('2026-11-14T11:50:00Z', base), 0)
  assert.equal(getRemainingSecondsUntil('bad', base), null)
})
