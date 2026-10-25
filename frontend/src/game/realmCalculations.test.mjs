import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

// combatConstants 走 `import { getT } from '../i18n'` 的目录导入（Vite 解析），
// Node 原生 ESM 不支持目录导入，必须先用 esbuild 打包成单文件再加载。
const tempOut = path.join(tmpdir(), `daohua-realm-test-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/combatConstants.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
})
const { calculateRealmInfo, calculateRealmProgress, serverExpToNextCap } = await import(pathToFileURL(tempOut).href)
test.after(() => {
  try { fs.unlinkSync(tempOut) } catch { /* 临时文件可能已被清理 */ }
})

test('境界判定：Lv.18（叩天门）在有本层修为经验时不跃迁为修真段或飞升', () => {
  // 零经验 Lv.18
  const info0 = calculateRealmInfo(0, 18)
  assert.equal(info0.stageName, '凡俗')
  assert.equal(info0.phaseName, '天')
  assert.equal(info0.minorName, '叩天门')
  assert.equal(info0.rankChar, '天')
  assert.equal(info0.fullDisplayName, '凡俗 [天] 叩天门')

  // 有经验（如 0.3% 进度）的 Lv.18，绝不可因浮点数比较下溢成“飞升”与“game.realm.digit.0”
  const infoWithExp = calculateRealmInfo(2400, 18)
  assert.equal(infoWithExp.stageName, '凡俗')
  assert.equal(infoWithExp.phaseName, '天')
  assert.equal(infoWithExp.minorName, '叩天门')
  assert.equal(infoWithExp.rankChar, '天')
  assert.equal(infoWithExp.fullDisplayName, '凡俗 [天] 叩天门')
  assert.ok(!infoWithExp.minorName.includes('飞升'), '小境界不应包含飞升')
  assert.ok(!infoWithExp.minorName.includes('digit.0'), '小境界不应包含未本地化的 digit.0')
})

test('境界判定：凡俗段内（武/玄/天）阶段字不因本层有微量经验而提前跳段', () => {
  // Lv.6 凡俗（武阶段第 6 级，养气）：有经验时仍保持“武”，不能提前跳到“玄”
  const lv6 = calculateRealmInfo(100, 6)
  assert.equal(lv6.stageName, '凡俗')
  assert.equal(lv6.phaseName, '武')
  assert.equal(lv6.rankChar, '武')
  assert.equal(lv6.minorName, '养气')

  // Lv.7 凡俗（跨入玄阶段第 1 级，通脉）
  const lv7 = calculateRealmInfo(0, 7)
  assert.equal(lv7.phaseName, '玄')
  assert.equal(lv7.rankChar, '玄')
  assert.equal(lv7.minorName, '通脉')

  // Lv.12 凡俗（玄阶段第 6 级，天玑）：有经验时仍保持“玄”，不能提前跳到“天”
  const lv12 = calculateRealmInfo(100, 12)
  assert.equal(lv12.phaseName, '玄')
  assert.equal(lv12.rankChar, '玄')
  assert.equal(lv12.minorName, '天玑')

  // Lv.13 凡俗（跨入天阶段第 1 级，天璇）
  const lv13 = calculateRealmInfo(0, 13)
  assert.equal(lv13.phaseName, '天')
  assert.equal(lv13.rankChar, '天')
  assert.equal(lv13.minorName, '天璇')
})

test('境界判定：修真九大境界与终极飞升命名与阶段自洽', () => {
  // Lv.19 练气一层
  const lv19 = calculateRealmInfo(0, 19)
  assert.equal(lv19.stageName, '练气')
  assert.equal(lv19.minorName, '练气一层')
  assert.equal(lv19.phaseName, '初')
  assert.equal(lv19.rankChar, '初')

  // Lv.28 练气巅峰（第10层）
  const lv28 = calculateRealmInfo(0, 28)
  assert.equal(lv28.stageName, '练气')
  assert.equal(lv28.minorName, '练气巅峰')
  assert.equal(lv28.rankChar, '极')

  // Lv.29 练气大圆满（第11层）
  const lv29 = calculateRealmInfo(0, 29)
  assert.equal(lv29.stageName, '练气')
  assert.equal(lv29.minorName, '练气大圆满')
  assert.equal(lv29.rankChar, '极')

  // Lv.30 半步筑基（第12层）
  const lv30 = calculateRealmInfo(0, 30)
  assert.equal(lv30.stageName, '练气')
  assert.equal(lv30.minorName, '半步筑基')
  assert.equal(lv30.rankChar, '极')

  // Lv.31 筑基一层
  const lv31 = calculateRealmInfo(0, 31)
  assert.equal(lv31.stageName, '筑基')
  assert.equal(lv31.minorName, '筑基一层')
  assert.equal(lv31.rankChar, '初')

  // Lv.126 半步飞升
  const lv126 = calculateRealmInfo(0, 126)
  assert.equal(lv126.stageName, '渡劫')
  assert.equal(lv126.minorName, '半步飞升')
  assert.equal(lv126.rankChar, '极')

  // Lv.127 飞升
  const lv127 = calculateRealmInfo(0, 127)
  assert.equal(lv127.stageName, '飞升')
  assert.equal(lv127.minorName, '飞升')
  assert.equal(lv127.rankChar, '极')
  assert.equal(lv127.fullDisplayName, '飞升 [极] 飞升')
})

test('入天门乘区瓶颈：进度与满修判定必须用服务端权威 expToNextLinggen，而非本地裸曲线', () => {
  // 复现证据：Lv.18 配表裸瓶颈 800000，五系齐全灵根乘区 3.0 → 权威瓶颈 2400000。
  // 修前前端只读本地静态表，800000/800000 就已显示圆满并允许点突破，服务端却回「修为未满」。
  const bare = calculateRealmProgress(800000, 18)
  assert.equal(bare.expToNext, 800000)
  assert.equal(bare.progressPercentage, 100)

  const cap = serverExpToNextCap(2400000, 18)
  assert.equal(cap, 2400000)
  const authoritative = calculateRealmProgress(800000, 18, cap)
  assert.equal(authoritative.expToNext, 2400000)
  assert.equal(authoritative.currentLayerExp, 800000)
  assert.ok(Math.abs(authoritative.progressPercentage - 100 / 3) < 1e-9)
  assert.ok(authoritative.progressPercentage < 99.99, '满修误判会让按钮在未满时可用')

  // 达到权威瓶颈后才算圆满
  const full = calculateRealmProgress(2400000, 18, cap)
  assert.equal(full.currentLayerExp, 2400000)
  assert.equal(full.progressPercentage, 100)
  assert.equal(full.totalLevel, 19)

  // 缺省/非法授权值回退本地曲线：老快照或满级时不得把进度算成 0 或 NaN
  assert.equal(serverExpToNextCap(undefined, 18), undefined)
  assert.equal(serverExpToNextCap(0, 18), undefined)
  assert.equal(serverExpToNextCap(Number.NaN, 18), undefined)
  assert.equal(serverExpToNextCap(2400000, 127), undefined)
  assert.equal(calculateRealmProgress(800000, 18, undefined).progressPercentage, 100)
})
