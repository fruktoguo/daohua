import test from 'node:test'
import assert from 'node:assert/strict'
import { evaluateSpiritualRoots } from './linggenEvaluation.ts'

test('evaluateSpiritualRoots：未抽取时返回未定', () => {
  const result = evaluateSpiritualRoots(null, false)
  assert.equal(result.name, '灵根未定')
  assert.equal(result.seal, '凡胎未化')
})

test('evaluateSpiritualRoots：五系全百评为神灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 100, wood: 100, water: 100, fire: 100, earth: 100 }, true)
  assert.equal(result.name, '神灵根')
  assert.equal(result.seal, '五灵根 · 神品')
})

test('evaluateSpiritualRoots：单系百数评为先天道胎灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 100, wood: 0, water: 0, fire: 0, earth: 0 }, true)
  assert.equal(result.name, '金先天道胎灵根')
  assert.equal(result.seal, '单灵根 · 极境')
})

test('evaluateSpiritualRoots：双系过九十评为双系天灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 95, wood: 92, water: 0, fire: 0, earth: 0 }, true)
  assert.equal(result.name, '金木天灵根')
  assert.equal(result.seal, '双灵根 · 天品')
})

test('evaluateSpiritualRoots：常见五行分布评为上品或极品五灵根', () => {
  const result = evaluateSpiritualRoots({ metal: 53, wood: 57, water: 95, fire: 36, earth: 0 }, true)
  // 四系有效，含一系95
  assert.match(result.name, /四灵根|真四灵根|四象/)
})
