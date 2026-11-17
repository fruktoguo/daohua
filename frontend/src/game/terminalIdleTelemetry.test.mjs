import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const esbuild = require('esbuild')
const tempOut = path.join(tmpdir(), `daohua-terminal-idle-${Date.now()}.mjs`)
await esbuild.build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/terminalIdleTelemetry.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
})

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
} = await import(pathToFileURL(tempOut).href)

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

test('双语挂机终端静态契约：右上角中英文切换、零图像元素与 i18n 闭环', async () => {
  const overlayPath = path.resolve(process.cwd(), 'frontend/src/components/TerminalIdleOverlay.tsx')
  const telemetryPath = path.resolve(process.cwd(), 'frontend/src/game/terminalIdleTelemetry.ts')
  const cssPath = path.resolve(process.cwd(), 'frontend/src/styles/terminal-idle.css')
  const uiStorePath = path.resolve(process.cwd(), 'frontend/src/uiStore.ts')
  const appZhPath = path.resolve(process.cwd(), 'frontend/src/i18n/domains/app.zh.ts')

  const [overlaySrc, telemetrySrc, cssSrc, uiStoreSrc, appZhSrc] = await Promise.all([
    fs.readFile(overlayPath, 'utf8'),
    fs.readFile(telemetryPath, 'utf8'),
    fs.readFile(cssPath, 'utf8'),
    fs.readFile(uiStorePath, 'utf8'),
    fs.readFile(appZhPath, 'utf8'),
  ])

  const stripComments = text => text
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/.*$/gm, '')

  for (const [name, src] of [
    ['TerminalIdleOverlay.tsx', overlaySrc],
    ['terminalIdleTelemetry.ts', telemetrySrc],
    ['terminal-idle.css', cssSrc],
  ]) {
    assert.doesNotMatch(
      stripComments(src),
      /<img\b|<svg\b|<canvas\b|<picture\b|<video\b|url\s*\(/i,
      `${name} 严禁包含任何图像标签或外部图片 url()`,
    )
  }

  assert.match(uiStoreSrc, /toggleIdleTerminalLang/, 'uiStore 必须提供挂机终端中英文切换方法')
  assert.match(overlaySrc, /onClick=\{toggleLang\}/, 'TerminalIdleOverlay 右上角必须提供中英文切换按钮')
  assert.match(overlaySrc, /\[ ZH \/ EN\* \]/, '英文激活态切换标签必须清晰可见')
  assert.match(appZhSrc, /langToggleZhActive:\s*'\[ 中文\* \/ EN \]'/, '中文激活态切换标签必须登记至 app.zh.ts')
})
