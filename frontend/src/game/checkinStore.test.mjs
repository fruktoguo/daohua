import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'

const mockStorage = new Map()
const mockSession = new Map()
globalThis.window = {
  localStorage: {
    getItem: key => mockStorage.get(key) ?? null,
    setItem: (key, val) => mockStorage.set(key, String(val)),
    removeItem: key => mockStorage.delete(key),
  },
  sessionStorage: {
    getItem: key => mockSession.get(key) ?? null,
    setItem: (key, val) => mockSession.set(key, String(val)),
    removeItem: key => mockSession.delete(key),
  },
  location: { origin: 'http://localhost', pathname: '/', reload: () => {} },
  addEventListener: () => {},
  removeEventListener: () => {},
  dispatchEvent: () => true,
}
globalThis.sessionStorage = globalThis.window.sessionStorage

const require = createRequire(import.meta.url)
const esbuild = require('esbuild')
const tempOut = path.join(tmpdir(), `daohua-checkin-store-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/checkinStore.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
  define: {
    'import.meta.env.DEV': 'true',
    'import.meta.env.VITE_APP_VERSION': '"dev"',
    'import.meta.env.VITE_API_URL': 'undefined',
    'import.meta.env.VITE_WS_URL': 'undefined',
  },
})
const {
  getChinaDayIndex,
  getChinaDayOfWeekIndexMonday,
  getNextChinaResetUnixSeconds,
  computeMsUntilNextChinaMidnight,
  normalizeCheckinFortuneTier,
  resolveFortuneVisualTone,
  hasCheckinRedDot,
  formatCountdownClock,
  readDismissedCheckinDay,
  writeDismissedCheckinDay,
  useCheckinStore,
} = await import(pathToFileURL(tempOut).href)

const uiOut = path.join(tmpdir(), `daohua-checkin-uistore-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/uiStore.ts')],
  bundle: true,
  format: 'esm',
  outfile: uiOut,
  logLevel: 'error',
})
const { useUiStore } = await import(pathToFileURL(uiOut).href)

test('UTC+8 自然日切换、零点倒计时与周偏移及签运归一化正确', () => {
  // 2026-11-16 23:59:59 UTC+8 == 1794844799 (周一)
  const beforeMidnight = Date.UTC(2026, 10, 16, 15, 59, 59) / 1000
  const atMidnight = Date.UTC(2026, 10, 16, 16, 0, 0) / 1000
  assert.equal(getChinaDayIndex(atMidnight), getChinaDayIndex(beforeMidnight) + 1)
  assert.equal(getChinaDayOfWeekIndexMonday(beforeMidnight), 0)
  assert.equal(getChinaDayOfWeekIndexMonday(atMidnight), 1)
  assert.equal(getNextChinaResetUnixSeconds(beforeMidnight), atMidnight)
  assert.equal(computeMsUntilNextChinaMidnight(beforeMidnight), 2500)
  assert.equal(formatCountdownClock(3661), '01:01:01')

  assert.equal(normalizeCheckinFortuneTier('perfect'), 'perfect')
  assert.equal(normalizeCheckinFortuneTier('invalid'), null)
  assert.equal(resolveFortuneVisualTone('transcendent_2'), 'gold')
  assert.equal(resolveFortuneVisualTone('great'), 'good')
  assert.equal(resolveFortuneVisualTone('very_bad'), 'bad')
})

test('checkinStore 红点判定、跨日清理与本地关闭记忆隔离', () => {
  useCheckinStore.getState().reset()
  mockStorage.clear()

  assert.equal(hasCheckinRedDot(null, null), false)
  useCheckinStore.getState().applySummary({
    canClaimToday: true,
    todayDate: '2026-11-16',
    dayIndex: 20774,
    streakDays: 3,
    totalDays: 10,
    lastRewardMerit: 32,
    isBot: false,
  })
  assert.equal(hasCheckinRedDot(useCheckinStore.getState().summary, null), true)

  useCheckinStore.getState().markDismissedForDay('player-a', 20774)
  assert.equal(readDismissedCheckinDay('player-a'), 20774)
  assert.equal(readDismissedCheckinDay('player-b'), null)
  writeDismissedCheckinDay('player-a', 20775)
  assert.equal(readDismissedCheckinDay('player-a'), 20775)

  assert.equal(hasCheckinRedDot({
    canClaimToday: true,
    todayDate: '2026-11-16',
    dayIndex: 20774,
    streakDays: 0,
    totalDays: 0,
    lastRewardMerit: null,
    isBot: true,
  }, null), false)
})

test('uiStore setActiveTab(checkin) 打开独立签到弹窗且保留底层页签', () => {
  useUiStore.setState({ activeTab: 'codex', checkinModalOpen: false })
  useUiStore.getState().setActiveTab('checkin')
  assert.equal(useUiStore.getState().checkinModalOpen, true)
  assert.equal(useUiStore.getState().activeTab, 'codex')

  useUiStore.getState().setActiveTab('exploration')
  assert.equal(useUiStore.getState().checkinModalOpen, false)
  assert.equal(useUiStore.getState().activeTab, 'exploration')
})
