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
  formatPlainLogSegments,
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

test('中文挂机终端遥测：准确聚合行动进度、战斗实时血蓝优先级与网络状态', () => {
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

  const blockedOfflineSnap = deriveTerminalTelemetry({
    connection: 'offline',
    latencyMs: null,
    onlineCount: null,
    offlineIdleCount: null,
    queue: {
      entries: [
        {
          entryId: 'e2',
          actionId: 'forging.sword',
          actionName: '锻造青锋剑',
          type: 'enhancement',
          status: 'blocked',
          progressSeconds: 0,
          durationSeconds: 12,
          remainingRepeats: 5,
          pvpEnabled: false,
          difficulty: 0,
        },
      ],
      loopEnabled: false,
      autoTravelEnabled: false,
      paused: false,
      autoUseSlots: EMPTY_AUTO_USE,
    },
    combatResources: null,
    version: 129,
    elapsedSeconds: 0,
    tick: 11,
  })

  assert.equal(blockedOfflineSnap.netState, 'offline')
  assert.equal(blockedOfflineSnap.netTone, 'crit')
  assert.equal(blockedOfflineSnap.jobStatus, 'BLOCKED')
  assert.equal(blockedOfflineSnap.jobTone, 'crit')
})

test('中文挂机终端日志与字符绘制：富文本分词纯文本展开、进度条与盲文波形定宽不变量', () => {
  const plainText = formatPlainLogSegments([
    { type: 'text', value: '击败 ' },
    { type: 'reference', kind: 'enemy', id: 'monster.wolf', label: '青林狼' },
    { type: 'text', value: '，获得 ' },
    { type: 'reference', kind: 'item', id: 'item.ningqi', label: '凝气草', amount: 3 },
    { type: 'text', value: '\n修为 +18' },
  ])
  assert.equal(plainText, '击败 [青林狼]，获得 [凝气草]×3 · 修为 +18')

  for (const ratio of [-0.5, 0, 0.33, 0.48, 0.75, 1, 2.5, Number.NaN]) {
    const bar = renderAsciiBar(ratio, 16)
    assert.equal([...bar].length, 16)
    assert.match(bar, /^[█▓░]+$/)
  }

  const spark = renderBrailleSparkline([0, 0.25, 0.5, 0.75, 1, 0.4], 12)
  const chars = [...spark]
  assert.equal(chars.length, 12)
  for (const ch of chars) {
    const cp = ch.codePointAt(0) ?? 0
    assert.ok(cp >= 0x2800 && cp <= 0x28ff, `字符 ${ch} 必须属于 Unicode Braille 区块`)
  }

  const hexRows = buildHexDumpRows(0x41a0_0800, 12, 3, 4)
  assert.equal(hexRows.length, 4)
})

test('中文挂机终端静态契约：零图像元素、全中文 i18n 闭环且遵循设置进入与按钮退出契约', async () => {
  const overlayPath = path.resolve(process.cwd(), 'frontend/src/components/TerminalIdleOverlay.tsx')
  const telemetryPath = path.resolve(process.cwd(), 'frontend/src/game/terminalIdleTelemetry.ts')
  const cssPath = path.resolve(process.cwd(), 'frontend/src/styles/terminal-idle.css')
  const settingsPath = path.resolve(process.cwd(), 'frontend/src/components/SettingsPage.tsx')
  const appZhPath = path.resolve(process.cwd(), 'frontend/src/i18n/domains/app.zh.ts')

  const [overlaySrc, telemetrySrc, cssSrc, settingsSrc, appZhSrc] = await Promise.all([
    fs.readFile(overlayPath, 'utf8'),
    fs.readFile(telemetryPath, 'utf8'),
    fs.readFile(cssPath, 'utf8'),
    fs.readFile(settingsPath, 'utf8'),
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

  assert.match(appZhSrc, /idleTerminal:\s*\{[\s\S]*?exitBtn:\s*'\[ × 退出挂机 \]'/, 'app.zh.ts 必须提供完整的中文挂机终端词条')
  assert.match(overlaySrc, /t\('app\.idleTerminal\.pane01Title'\)/, 'TerminalIdleOverlay 必须通过 app.idleTerminal.* 渲染中文面板标题')
  assert.doesNotMatch(overlaySrc, /requestFullscreen/, '挂机模式仅铺满视口，不得触发浏览器原生 Fullscreen')
  assert.match(overlaySrc, /setIdleModeOpen\(false\)/, 'TerminalIdleOverlay 必须提供退出挂机按钮动作')
  assert.match(settingsSrc, /setIdleModeOpen\(true\)/, 'SettingsPage 必须提供进入挂机模式入口')
})
