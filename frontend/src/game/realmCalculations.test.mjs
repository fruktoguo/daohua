import test from 'node:test'
import assert from 'node:assert/strict'
import { calculateRealmInfo, calculateRealmProgress } from './combatConstants.ts'

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
