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
  renderAsciiBar,
  renderBrailleSparkline,
  buildHexDumpRows,
  generateDaemonLogLine,
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

test('挂机终端遥测：准确将行动队列进度、血蓝占比与网络连接态映射为运维指标', () => {
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
        },
      ],
      loopEnabled: true,
      autoTravelEnabled: true,
      paused: false,
      autoUseSlots: EMPTY_AUTO_USE,
    },
    combatResources: {
      hitpoints: 250,
      maxHitpoints: 1000,
      manapoints: 800,
      maxManapoints: 1000,
    },
    version: 128,
    sequenceId: 7,
    elapsedSeconds: 2.5,
    tick: 10,
  })

  assert.equal(runningSnap.hostTag, 'srv-prod-07')
  assert.equal(runningSnap.netLink, 'ESTABLISHED')
  assert.equal(runningSnap.netTone, 'ok')
  assert.equal(runningSnap.rttMs, 18)
  assert.equal(runningSnap.jobStatus, 'RUN')
  assert.equal(runningSnap.jobTone, 'ok')
  assert.equal(runningSnap.daemonUnit, 'etl-compactor.service')
  assert.equal(runningSnap.schedulePolicy, 'DAEMON_INF')
  assert.equal(runningSnap.completedCycles, 42)
  assert.ok(Math.abs(runningSnap.jobProgressRatio - 0.65) < 1e-6, '单轮进度应合并平滑经过秒数')
  assert.ok(Math.abs(runningSnap.memResidentRatio - 0.25) < 1e-6, 'MEM_RES 应精确反映当前气血比例')
  assert.equal(runningSnap.memTone, 'crit', '气血低于 30% 时内存条应切换危红告警')
  assert.ok(Math.abs(runningSnap.bufKernelRatio - 0.8) < 1e-6, 'BUF_KRN 应精确反映当前灵力比例')

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
        },
      ],
      loopEnabled: false,
      autoTravelEnabled: false,
      paused: false,
      autoUseSlots: EMPTY_AUTO_USE,
    },
    combatResources: null,
    version: 129,
    sequenceId: 1,
    elapsedSeconds: 0,
    tick: 11,
  })

  assert.equal(blockedOfflineSnap.netLink, 'LINK_DOWN')
  assert.equal(blockedOfflineSnap.netTone, 'crit')
  assert.equal(blockedOfflineSnap.jobStatus, 'BLOCKED')
  assert.equal(blockedOfflineSnap.jobTone, 'crit')
  assert.equal(blockedOfflineSnap.schedulePolicy, 'BATCH_FIN')

  const logLine = generateDaemonLogLine(1, runningSnap, '06:45:00.120', true)
  assert.match(logLine.text, /\[pg_wal\/commit\]/)
  assert.doesNotMatch(logLine.text, /[\u4e00-\u9fff]/, '日志行严禁包含任何中文游戏词汇')
})

test('挂机终端字符渲染：进度条、Braille 盲文波形与 Hex 转储满足纯字符定宽不变量', () => {
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
  for (const row of hexRows) {
    assert.match(row, /^00007ff8[0-9a-f]{8} {2}(?:[0-9a-f]{2} ){7}[0-9a-f]{2} {2}(?:[0-9a-f]{2} ){7}[0-9a-f]{2} {2}\|[\x20-\x7e]{16}\|$/)
  }
})

test('挂机终端静态契约：零图像元素、零中文穿帮且遵循设置进入与按钮退出契约', async () => {
  const overlayPath = path.resolve(process.cwd(), 'frontend/src/components/TerminalIdleOverlay.tsx')
  const telemetryPath = path.resolve(process.cwd(), 'frontend/src/game/terminalIdleTelemetry.ts')
  const cssPath = path.resolve(process.cwd(), 'frontend/src/styles/terminal-idle.css')
  const settingsPath = path.resolve(process.cwd(), 'frontend/src/components/SettingsPage.tsx')

  const [overlaySrc, telemetrySrc, cssSrc, settingsSrc] = await Promise.all([
    fs.readFile(overlayPath, 'utf8'),
    fs.readFile(telemetryPath, 'utf8'),
    fs.readFile(cssPath, 'utf8'),
    fs.readFile(settingsPath, 'utf8'),
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

  // 剥离块注释与单行注释后，挂机终端代码与模板字符串不得残留任何中文字符
  for (const [name, src] of [
    ['TerminalIdleOverlay.tsx', overlaySrc],
    ['terminalIdleTelemetry.ts', telemetrySrc],
  ]) {
    assert.doesNotMatch(stripComments(src), /[\u4e00-\u9fff]/, `${name} 剥离注释后不得残留任何中文字符`)
  }

  assert.doesNotMatch(overlaySrc, /requestFullscreen/, '挂机模式仅铺满视口，不得触发浏览器原生 Fullscreen')
  assert.match(overlaySrc, /setIdleModeOpen\(false\)/, 'TerminalIdleOverlay 必须提供退出挂机按钮动作')
  assert.match(settingsSrc, /setIdleModeOpen\(true\)/, 'SettingsPage 必须提供进入挂机模式入口')
})
