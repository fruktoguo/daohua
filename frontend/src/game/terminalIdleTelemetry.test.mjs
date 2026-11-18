import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { compileFunction } from 'node:vm'
import { build } from 'esbuild'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'

const { outputFiles: [bundle] } = await build({
  stdin: {
    resolveDir: fileURLToPath(new URL('.', import.meta.url)),
    contents: `
      export * from './terminalIdleTelemetry'
      export { TerminalIdleOverlay } from '../components/TerminalIdleOverlay'
      export { useUiStore } from '../uiStore'
    `,
  },
  bundle: true,
  packages: 'external',
  platform: 'node',
  format: 'cjs',
  jsx: 'automatic',
  define: { 'import.meta.env': '{}' },
  logOverride: { 'empty-import-meta': 'silent' },
  write: false,
})
const evaluate = compileFunction(bundle.text, ['require', 'module', 'exports'])
const require = createRequire(import.meta.url)
function loadTerminal() {
  const module = { exports: {} }
  evaluate(require, module, module.exports)
  return module.exports
}

const {
  deriveTerminalTelemetry,
  formatEnglishDuration,
  formatEnglishNumber,
  formatEnglishRealmDisplay,
  formatPlainLogSegments,
  humanizeEntityId,
  renderAsciiBar,
  renderBrailleSparkline,
  buildHexDumpRows,
} = loadTerminal()

const EMPTY_AUTO_USE = {
  exploration: [],
  alchemy: [],
  forging: [],
  talisman: [],
  formation: [],
  enhancement: [],
  combat: [],
}

test('双语挂机终端遥测：准确聚合行动进度、战斗实时血蓝优先级与网络状态', () => {
  const runningSnap = deriveTerminalTelemetry({
    connection: 'online',
    latencyMs: 18.4,
    onlineCount: 63,
    offlineIdleCount: 240,
    queue: {
      entries: [
        {
          entryId: 'e1',
          actionId: 'alchemy.pill.1',
          actionName: '炼制回春丹',
          type: 'alchemy',
          status: 'running',
          progressSeconds: 4,
          durationSeconds: 10,
          remainingRepeats: -1,
          completedRepeats: 42,
          pvpEnabled: false,
          difficulty: 0,
        },
      ],
      loopEnabled: true,
      autoTravelEnabled: true,
      paused: false,
      autoUseSlots: EMPTY_AUTO_USE,
    },
    combatResources: {
      hitpoints: 900,
      maxHitpoints: 1000,
      manapoints: 800,
      maxManapoints: 1000,
    },
    expedition: {
      phase: 'combat',
      progress: 0.5,
      elapsedSeconds: 3,
      durationSeconds: 6,
      playerHitpoints: 250,
      playerManapoints: 600,
    },
    version: 128,
    elapsedSeconds: 2.5,
    tick: 10,
  })

  assert.equal(runningSnap.netState, 'online')
  assert.equal(runningSnap.netTone, 'ok')
  assert.equal(runningSnap.rttMs, 18)
  assert.equal(runningSnap.onlinePlayers, 63)
  assert.equal(runningSnap.offlineIdlePlayers, 240)
  assert.equal(runningSnap.jobStatus, 'RUN')
  assert.equal(runningSnap.jobTone, 'ok')
  assert.equal(runningSnap.completedCycles, 42)
  assert.ok(Math.abs(runningSnap.jobProgressRatio - 0.65) < 1e-6, '单轮进度应合并平滑流逝秒数')
  assert.equal(runningSnap.hpCurrent, 250, '存在战内气血时应优先反映战内实时气血')
  assert.ok(Math.abs(runningSnap.hpRatio - 0.25) < 1e-6)
  assert.equal(runningSnap.hpTone, 'crit', '气血低于 30% 时应切换危红告警')
  assert.equal(runningSnap.mpCurrent, 600)
  assert.ok(Math.abs(runningSnap.mpRatio - 0.6) < 1e-6)
})

test('双语挂机终端格式化：英文实体名、英文境界、英文数值及双语日志分词无损展开', () => {
  assert.equal(humanizeEntityId('item.ningxue-grass'), 'Ningxue Grass')
  assert.equal(humanizeEntityId('explore.gather-ningxue-grass'), 'Gather Ningxue Grass')
  assert.equal(humanizeEntityId('currency.spirit-stone'), 'Spirit Stone')
  assert.equal(humanizeEntityId('item.tome.blazing-blast'), 'Tome: Blazing Blast')

  assert.equal(formatEnglishRealmDisplay(5), 'Mortal · Martial · Stage 5')
  assert.equal(formatEnglishRealmDisplay(21), 'Qi Refining · Layer 3')
  assert.equal(formatEnglishRealmDisplay(28), 'Qi Refining · Peak')
  assert.equal(formatEnglishRealmDisplay(30), 'Half-Step Foundation')
  assert.equal(formatEnglishRealmDisplay(127), 'Immortal · Ascension')

  assert.equal(formatEnglishNumber(9850), '9,850')
  assert.equal(formatEnglishNumber(25600), '25.6k')
  assert.equal(formatEnglishNumber(3450000), '3.45M')
  assert.equal(formatEnglishDuration(3720), '1h 2m')

  const sampleSegments = [
    { type: 'text', value: '击败 ' },
    { type: 'reference', kind: 'enemy', id: 'monster.qinglin-wolf', label: '青林狼' },
    { type: 'text', value: '，获得 ' },
    { type: 'reference', kind: 'item', id: 'item.ningxue-grass', label: '凝气草', amount: 3 },
    { type: 'text', value: '\n修为 +18' },
  ]
  assert.equal(formatPlainLogSegments(sampleSegments, 'zh'), '击败 [青林狼]，获得 [凝气草]×3 · 修为 +18')
  const enLog = formatPlainLogSegments(sampleSegments, 'en')
  assert.equal(enLog, '[Qinglin Wolf] -> [Ningxue Grass] x3 (+18)')
  assert.doesNotMatch(enLog, /[\u4e00-\u9fff]/, '英文模式日志不得残留任何中文字符')

  for (const ratio of [-0.5, 0, 0.33, 0.48, 0.75, 1, 2.5, Number.NaN]) {
    const bar = renderAsciiBar(ratio, 16)
    assert.equal([...bar].length, 16)
    assert.match(bar, /^[█▓░]+$/)
  }

  const spark = renderBrailleSparkline([0, 0.25, 0.5, 0.75, 1, 0.4], 12)
  assert.equal([...spark].length, 12)
  assert.equal(buildHexDumpRows(0x41a0_0800, 12, 3, 4).length, 4)
})

test('零时长战斗行动使用真实搜寻进度，非计时阶段保持有限的指示进度', () => {
  const input = {
    connection: 'online', latencyMs: 18, onlineCount: 1, offlineIdleCount: 0,
    queue: {
      entries: [{ status: 'running', progressSeconds: 0, durationSeconds: 0, completedRepeats: 2 }],
      paused: false, autoUseSlots: EMPTY_AUTO_USE,
    },
    combatResources: null, version: 1, elapsedSeconds: 30, tick: 8,
  }
  const search = deriveTerminalTelemetry({
    ...input, expedition: { phase: 'search', progress: 0.25, durationSeconds: 12 },
  })
  assert.equal(search.jobStatus, 'RUN')
  assert.equal(search.jobProgressRatio, 0.25, '不能按 0 秒的队列时长计算搜寻进度')
  const combat = deriveTerminalTelemetry({
    ...input, expedition: { phase: 'combat', progress: 0, durationSeconds: 0 },
  })
  assert.ok(combat.jobProgressRatio > 0 && combat.jobProgressRatio < 1)
})

test('连接中、重连和断线不显示上一条在线延迟，并区分告警级别', () => {
  for (const [connection, expectedState, expectedTone] of [
    ['connecting', 'reconnecting', 'warn'],
    ['reconnecting', 'reconnecting', 'warn'],
    ['offline', 'offline', 'crit'],
  ]) {
    const snapshot = deriveTerminalTelemetry({
      connection, latencyMs: 220, onlineCount: null, offlineIdleCount: null,
      queue: { entries: [], paused: false, autoUseSlots: EMPTY_AUTO_USE },
      combatResources: null, version: 1, elapsedSeconds: 0, tick: 8,
    })
    assert.equal(snapshot.netState, expectedState, connection)
    assert.equal(snapshot.netTone, expectedTone, connection)
    assert.equal(snapshot.rttMs, 0, connection)
  }
})

test('真实终端语言切换可持久化，重新进入后渲染对应的中英文终端内容', t => {
  const storage = new Map()
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'window')
  t.after(() => {
    if (descriptor) Object.defineProperty(globalThis, 'window', descriptor)
    else delete globalThis.window
  })
  globalThis.window = {
    localStorage: {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
    },
  }
  const zh = loadTerminal()
  const render = ({ TerminalIdleOverlay }) => renderToStaticMarkup(createElement(TerminalIdleOverlay))
  const zhMarkup = render(zh)
  assert.match(zhMarkup, /\[挂机终端\]/)
  assert.match(zhMarkup, /\[1:队列与补给\*\]/)
  assert.match(zhMarkup, /aria-label="切换挂机终端为英文显示">\[ 中文\* \/ EN \]/)

  zh.useUiStore.getState().toggleIdleTerminalLang()
  assert.equal(zh.useUiStore.getState().idleTerminalLang, 'en')
  const en = loadTerminal()
  assert.equal(en.useUiStore.getState().idleTerminalLang, 'en', '重新初始化时应保留刚选择的语言')
  const enMarkup = render(en)
  assert.match(enMarkup, /DaoHua CLI Console/)
  assert.match(enMarkup, /aria-label="Switch idle terminal to Chinese">\[ ZH \/ EN\* \]/)
  assert.doesNotMatch(enMarkup, /队列与补给/)
  for (const markup of [zhMarkup, enMarkup]) assert.doesNotMatch(markup, /<(img|svg|canvas|picture|video)\b/i)

  en.useUiStore.getState().toggleIdleTerminalLang()
  assert.equal(en.useUiStore.getState().idleTerminalLang, 'zh')
  assert.match(render(loadTerminal()), /\[1:队列与补给\*\]/)
})

test('双语挂机终端样式不包含外部图像', async () => {
  const css = await fs.readFile(new URL('../styles/terminal-idle.css', import.meta.url), 'utf8')
  assert.doesNotMatch(css.replace(/\/\*[\s\S]*?\*\//g, ''), /url\s*\(/i)
})
