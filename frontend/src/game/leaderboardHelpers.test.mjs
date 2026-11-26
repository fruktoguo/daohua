import test from 'node:test'
import assert from 'node:assert/strict'
import { existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import {
  ALL_LEADERBOARD_BOARD_IDS,
  LEADERBOARD_CATEGORIES,
  resolveBoardId,
  resolveRankTier,
  splitBoardId,
  formatLeaderboardBoardName,
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

test('全服排行榜辅助：全部 12 榜均配备运行时二值图腾且名次档位与榜名解析准确', () => {
  assert.equal(ALL_LEADERBOARD_BOARD_IDS.length, 12)
  for (const boardId of ALL_LEADERBOARD_BOARD_IDS) {
    const glyphPath = fileURLToPath(new URL(`../assets/glyphs/lb.${boardId}.webp`, import.meta.url))
    assert.equal(existsSync(glyphPath), true, `缺少排行榜图腾资产: lb.${boardId}.webp`)
  }

  assert.equal(resolveRankTier(1), 'top1')
  assert.equal(resolveRankTier(2), 'top2')
  assert.equal(resolveRankTier(3), 'top3')
  assert.equal(resolveRankTier(4), 'ranked')
  assert.equal(resolveRankTier(50), 'ranked')

  const dict = {
    'app.leaderboard.tabRealm': '境界榜',
    'app.leaderboard.tabDuel': '斗法榜',
    'app.leaderboard.tabValuation': '身家榜',
    'app.leaderboard.tabSpiritStone': '灵石榜',
    'app.leaderboard.tabZongmen': '宗门榜',
    'app.leaderboard.tabTechnique': '技艺榜',
    'app.leaderboard.techniqueBoardName': '{name}榜',
    'insight.technique.alchemy': '炼丹',
  }
  const t = (key, params) => {
    let text = dict[key] ?? key
    if (params) {
      for (const [k, v] of Object.entries(params)) text = text.replace(`{${k}}`, String(v))
    }
    return text
  }
  assert.equal(formatLeaderboardBoardName({ boardId: 'realm', kind: 'realm' }, t), '境界榜')
  assert.equal(formatLeaderboardBoardName({ boardId: 'duel', kind: 'duel' }, t), '斗法榜')
  assert.equal(formatLeaderboardBoardName({ boardId: 'technique.alchemy', kind: 'technique', techniqueId: 'alchemy' }, t), '炼丹榜')
  assert.equal(formatLeaderboardBoardName({ boardId: 'technique.alchemy' }, t), '炼丹榜')
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
