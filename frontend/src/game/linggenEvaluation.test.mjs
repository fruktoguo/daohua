import test from 'node:test'
import assert from 'node:assert/strict'
import { evaluateSpiritualRoots } from './linggenEvaluation.ts'
import { getT } from '../i18n/index.ts'

const t = getT()

test('evaluateSpiritualRoots：未抽取时返回未定', () => {
  const result = evaluateSpiritualRoots(null, false, t)
  assert.equal(result.name, '灵根未定')
  assert.equal(result.seal, '凡胎未化')
})

test('evaluateSpiritualRoots：五系全部归零不崩溃并返回五系皆空', () => {
  // 回归：曾因 lowest 在空数组守卫之前计算而崩溃（entries[-1].value）
  const result = evaluateSpiritualRoots({ metal: 0, wood: 0, water: 0, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '五系皆空')
  assert.equal(result.seal, '灵根尽绝')
})

test('evaluateSpiritualRoots：单系满百仍是天灵根（圆满评语）', () => {
  const result = evaluateSpiritualRoots({ metal: 100, wood: 0, water: 0, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金天灵根')
  assert.equal(result.seal, '单灵根 · 天品')
})

test('evaluateSpiritualRoots：单系 ≥90 为天灵根', () => {
  const result = evaluateSpiritualRoots({ fire: 92, wood: 0, water: 0, metal: 0, earth: 0 }, true, t)
  assert.equal(result.name, '火天灵根')
  assert.equal(result.seal, '单灵根 · 天品')
})

test('evaluateSpiritualRoots：单系 60~89 为真灵根', () => {
  const result = evaluateSpiritualRoots({ water: 75, wood: 0, fire: 0, metal: 0, earth: 0 }, true, t)
  assert.equal(result.name, '水真灵根')
  assert.equal(result.seal, '单灵根 · 真品')
})

test('evaluateSpiritualRoots：单系 35~59 为下品灵根', () => {
  const result = evaluateSpiritualRoots({ earth: 40, wood: 0, water: 0, metal: 0, fire: 0 }, true, t)
  assert.equal(result.name, '土下品灵根')
  assert.equal(result.seal, '单灵根 · 下品')
})

test('evaluateSpiritualRoots：单系 <35 为废灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 20, wood: 0, water: 0, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金废灵根')
  assert.equal(result.seal, '单灵根 · 近废')
})

test('evaluateSpiritualRoots：双系皆 ≥90 为双天灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 95, wood: 92, water: 0, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金木双天灵根')
  assert.equal(result.seal, '双灵根 · 天品')
})

test('evaluateSpiritualRoots：双系皆 ≥75 为上品双灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 80, wood: 76, water: 0, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金木上品双灵根')
  assert.equal(result.seal, '双灵根 · 上品')
})

test('evaluateSpiritualRoots：双系皆 ≥55 为中品双灵根', () => {
  const result = evaluateSpiritualRoots({ water: 60, fire: 56, wood: 0, metal: 0, earth: 0 }, true, t)
  assert.equal(result.name, '水火中品双灵根')
  assert.equal(result.seal, '双灵根 · 中品')
})

test('evaluateSpiritualRoots：双系孱弱为废双灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 25, wood: 15, water: 0, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金木废双灵根')
  assert.equal(result.seal, '双灵根 · 近废')
})

test('evaluateSpiritualRoots：三系皆 ≥90 为三天灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 100, wood: 100, water: 100, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金木水三天灵根')
  assert.equal(result.seal, '三灵根 · 天品')
})

test('evaluateSpiritualRoots：三系皆 ≥75 为上品三灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 80, wood: 78, water: 75, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金木水上品三灵根')
  assert.equal(result.seal, '三灵根 · 上品')
})

test('evaluateSpiritualRoots：三系皆 ≥55 为中品三灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 60, wood: 58, water: 55, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金木水中品三灵根')
  assert.equal(result.seal, '三灵根 · 中品')
})

test('evaluateSpiritualRoots：三系孱弱为废三灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 25, wood: 20, water: 15, fire: 0, earth: 0 }, true, t)
  assert.equal(result.name, '金木水废三灵根')
  assert.equal(result.seal, '三灵根 · 近废')
})

test('evaluateSpiritualRoots：四系皆 ≥90 为四象天灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 95, wood: 92, water: 90, fire: 91, earth: 0 }, true, t)
  assert.equal(result.name, '金木火水四象天灵根')
  assert.equal(result.seal, '四灵根 · 天品')
})

test('evaluateSpiritualRoots：四系可观但不成上品定为四系杂灵根', () => {
  // 用户截图案例：水99 火69 木67 金64 → 总值 299，最低 64 ≥ 40 → 杂灵根
  const result = evaluateSpiritualRoots({ metal: 64, wood: 67, water: 99, fire: 69, earth: 0 }, true, t)
  assert.equal(result.name, '水火木金杂灵根')
  assert.equal(result.seal, '四灵根 · 杂品')
})

test('evaluateSpiritualRoots：四系孱弱为废四灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 30, wood: 25, water: 20, fire: 15, earth: 0 }, true, t)
  assert.equal(result.name, '金木水火废四灵根')
  assert.equal(result.seal, '四灵根 · 近废')
})

test('evaluateSpiritualRoots：五系皆强皆全为五行天灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 95, wood: 92, water: 90, fire: 91, earth: 88 }, true, t)
  assert.equal(result.name, '五行上品灵根')
  assert.equal(result.seal, '五灵根 · 上品')
})

test('evaluateSpiritualRoots：五系皆弱皆全定名废灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 30, wood: 35, water: 40, fire: 28, earth: 32 }, true, t)
  assert.equal(result.name, '废灵根')
  assert.equal(result.seal, '五灵根 · 废品')
  assert.match(result.desc, /逆天改命/)
})

test('evaluateSpiritualRoots：五系总值充裕但驳杂为五行杂灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 45, wood: 42, water: 40, fire: 40, earth: 40 }, true, t)
  assert.equal(result.name, '五行杂灵根')
  assert.equal(result.seal, '五灵根 · 杂品')
})
