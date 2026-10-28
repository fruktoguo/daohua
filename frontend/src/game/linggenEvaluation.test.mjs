import test from 'node:test'
import assert from 'node:assert/strict'
import { evaluateSpiritualRoots } from './linggenEvaluation.ts'
import { getT } from '../i18n/index.ts'

const t = getT()

/** 便捷构造：按金木水火土顺序传点数后直接评定。 */
const judge = (metal, wood = 0, water = 0, fire = 0, earth = 0) =>
  evaluateSpiritualRoots({ metal, wood, water, fire, earth }, true, t)

test('未抽取时返回灵根未定', () => {
  const result = evaluateSpiritualRoots(null, false, t)
  assert.equal(result.name, '灵根未定')
  assert.equal(result.seal, '凡胎未化')
})

test('五系全部归零不崩溃并返回五系皆空', () => {
  // 回归：曾因最低值在空数组守卫之前计算而崩溃（entries[-1].value）
  const result = judge(0)
  assert.equal(result.name, '五系皆空')
  assert.equal(result.seal, '灵根尽绝')
})

// ── 单灵根：先天道胎 / 极品 / 天品 / 真品 / 偏枯 / 近废 ─────────────────────
test('单系满百为先天道胎灵根', () => {
  const result = judge(100)
  assert.equal(result.name, '金先天道胎灵根')
  assert.equal(result.seal, '单灵根 · 极境资质')
})

test('单系 95~99 为极品天灵根', () => {
  const result = judge(0, 0, 0, 0, 95)
  assert.equal(result.name, '土极品天灵根')
  assert.equal(result.seal, '单灵根 · 极品')
})

test('单系 90~94 为天灵根', () => {
  const result = judge(0, 0, 0, 92)
  assert.equal(result.name, '火天灵根')
  assert.equal(result.seal, '单灵根 · 天品')
})

test('单系 80~89 为真灵根', () => {
  const result = judge(0, 0, 85)
  assert.equal(result.name, '水真灵根')
  assert.equal(result.seal, '单灵根 · 上品')
})

test('单系 35~79 为偏枯单灵根', () => {
  const result = judge(0, 40)
  assert.equal(result.name, '木偏枯单灵根')
  assert.equal(result.seal, '单灵根 · 偏弱')
})

test('单系 <35 为废灵根', () => {
  const result = judge(20)
  assert.equal(result.name, '金废灵根')
  assert.equal(result.seal, '单灵根 · 近废')
})

// ── 双灵根：天品 / 上品 / 均衡 / 近废 / 杂驳 / 常规 ─────────────────────────
test('双系皆 ≥90 为双天灵根', () => {
  const result = judge(95, 92)
  assert.equal(result.name, '金木双天灵根')
  assert.equal(result.seal, '双灵根 · 天品')
})

test('双系皆 80~89 为真双灵根', () => {
  const result = judge(85, 82)
  assert.equal(result.name, '金木真双灵根')
  assert.equal(result.seal, '双灵根 · 上品')
})

test('双系离散 ≤8 且次强 ≥50 为均衡双灵根', () => {
  const result = judge(64, 60)
  assert.equal(result.name, '金木均衡双灵根')
  assert.equal(result.seal, '双灵根 · 均衡')
})

test('双系总值 ≤40 为废双灵根', () => {
  const result = judge(25, 15)
  assert.equal(result.name, '金木废双灵根')
  assert.equal(result.seal, '双灵根 · 近废')
})

test('双系总值 41~80 为杂双灵根', () => {
  const result = judge(45, 30)
  assert.equal(result.name, '金木杂双灵根')
  assert.equal(result.seal, '双灵根 · 杂驳')
})

test('双系总值 >80 且不成上品为常规双灵根', () => {
  const result = judge(75, 20)
  assert.equal(result.name, '金木双灵根')
  assert.equal(result.seal, '双灵根 · 常规')
})

// ── 三灵根：天品 / 上品 / 均衡 / 近废 / 杂驳 / 常规 ─────────────────────────
test('三系皆 ≥90 为三系天灵根', () => {
  const result = judge(100, 100, 100)
  assert.equal(result.name, '金木水三系天灵根')
  assert.equal(result.seal, '三灵根 · 天品')
})

test('三系皆 75~89 为真三灵根', () => {
  const result = judge(80, 78, 75)
  assert.equal(result.name, '金木水真三灵根')
  assert.equal(result.seal, '三灵根 · 上品')
})

test('三系离散 ≤10 且末位 ≥45 为均衡三灵根', () => {
  const result = judge(58, 52, 49)
  assert.equal(result.name, '金木水均衡三灵根')
  assert.equal(result.seal, '三灵根 · 均衡')
})

test('三系总值 ≤60 为废三灵根', () => {
  const result = judge(25, 20, 15)
  assert.equal(result.name, '金木水废三灵根')
  assert.equal(result.seal, '三灵根 · 近废')
})

test('三系总值 61~120 为杂三灵根', () => {
  const result = judge(60, 30, 25)
  assert.equal(result.name, '金木水杂三灵根')
  assert.equal(result.seal, '三灵根 · 杂驳')
})

test('三系总值 >120 且不成上品为常规三灵根', () => {
  const result = judge(95, 20, 15)
  assert.equal(result.name, '金木水三灵根')
  assert.equal(result.seal, '三灵根 · 常规')
})

// ── 四灵根：天品 / 上品 / 近废 / 杂驳 / 常规 ───────────────────────────────
test('四系皆 ≥90 为四象天灵根', () => {
  // 名称按点数降序拼接：金95 木92 火91 水90
  const result = judge(95, 92, 90, 91)
  assert.equal(result.name, '金木火水四象天灵根')
  assert.equal(result.seal, '四灵根 · 天品')
})

test('四系皆 70~89 为真四灵根', () => {
  const result = judge(80, 76, 72, 70)
  assert.equal(result.name, '金木水火真四灵根')
  assert.equal(result.seal, '四灵根 · 上品')
})

test('四系总值 ≤90 为废四灵根', () => {
  const result = judge(30, 25, 20, 15)
  assert.equal(result.name, '金木水火废四灵根')
  assert.equal(result.seal, '四灵根 · 近废')
})

test('四系总值 91~160 为杂四灵根', () => {
  // 用户截图案例：水99 火69 木67 金64 → 总值 299 已超 160，属常规四灵根
  const result = judge(64, 67, 99, 69)
  assert.equal(result.seal, '四灵根 · 常规')
  // 真正落入杂驳区间的分布
  const mixed = judge(60, 50, 30, 15)
  assert.equal(mixed.name, '金木水火杂四灵根')
  assert.equal(mixed.seal, '四灵根 · 杂驳')
})

test('四系总值 >160 且不成上品为常规四灵根', () => {
  const result = judge(64, 67, 99, 69)
  assert.equal(result.name, '水火木金四灵根')
  assert.equal(result.seal, '四灵根 · 常规')
})

// ── 五灵根：神品 / 天品 / 极上 / 均衡 / 近废 / 杂驳 / 普通 / 上品 / 极品 ────
test('五系皆满百为神灵根', () => {
  const result = judge(100, 100, 100, 100, 100)
  assert.equal(result.name, '神灵根')
  assert.equal(result.seal, '五灵根 · 神品')
})

test('五系皆 ≥90 为五行天灵根', () => {
  const result = judge(95, 92, 90, 91, 90)
  assert.equal(result.name, '五行天灵根')
  assert.equal(result.seal, '五灵根 · 天品')
})

test('五系皆 80~89 为五行真灵根', () => {
  const result = judge(88, 85, 83, 81, 80)
  assert.equal(result.name, '五行真灵根')
  assert.equal(result.seal, '五灵根 · 极上')
})

test('五系离散 ≤10 且末位 ≥55 为五行均衡灵根', () => {
  const result = judge(64, 60, 58, 56, 55)
  assert.equal(result.name, '五行均衡灵根')
  assert.equal(result.seal, '五灵根 · 均衡')
})

test('五系皆弱皆全（总值 ≤45）定名废灵根', () => {
  const result = judge(10, 9, 9, 8, 8)
  assert.equal(result.name, '废灵根')
  assert.equal(result.seal, '五灵根 · 近废')
  assert.match(result.desc, /逆天改命/)
})

test('五系总值 46~120 为杂灵根', () => {
  const result = judge(45, 30, 20, 15, 10)
  assert.equal(result.name, '杂灵根')
  assert.equal(result.seal, '五灵根 · 杂驳')
})

test('五系总值 121~220 为下品五灵根', () => {
  const result = judge(60, 50, 40, 30, 20)
  assert.equal(result.name, '下品五灵根')
  assert.equal(result.seal, '五灵根 · 普通')
})

test('五系总值 221~320 为上品五灵根', () => {
  const result = judge(70, 60, 50, 40, 30)
  assert.equal(result.name, '上品五灵根')
  assert.equal(result.seal, '五灵根 · 上品')
})

test('五系总值 >320 为极品五行灵根', () => {
  const result = judge(90, 80, 70, 60, 30)
  assert.equal(result.name, '极品五行灵根')
  assert.equal(result.seal, '五灵根 · 极品')
})

test('回归：四系天品加一条微末残系不得跌为废灵根', () => {
  // 用户实例：96/94/92/90/15，总值 387 —— 旧实现因「最低 ≥40」硬门槛误判为废灵根
  const result = judge(96, 94, 92, 90, 15)
  assert.equal(result.name, '极品五行灵根')
  assert.equal(result.seal, '五灵根 · 极品')
})

test('回归：高总值残系不得比去掉残系后降级', () => {
  // 旧口径下 96/94/92/90/15（废灵根）反而弱于 96/94/92/90（四象天灵根），评价非单调
  const withWeak = judge(96, 94, 92, 90, 15)
  const withoutWeak = judge(96, 94, 92, 90)
  assert.equal(withoutWeak.seal, '四灵根 · 天品')
  assert.equal(withWeak.seal, '五灵根 · 极品')
})
