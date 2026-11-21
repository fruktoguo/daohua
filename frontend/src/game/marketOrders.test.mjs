import test from 'node:test'
import assert from 'node:assert/strict'

test('marketPricing: normalizeMarketPrice 严格遵循系统回收底价与价格步长网格防套利', async () => {
  const { normalizeMarketPrice, getMarketPriceIncrement } = await import('./marketPricing.ts')

  // 1. 全局绝对下限保底（2 灵石，防止卖方 2% 印花税向上取整导致 0 收益）
  assert.equal(normalizeMarketPrice(0, 0, false), 2)
  assert.equal(normalizeMarketPrice(1, 0, false), 2)
  assert.equal(normalizeMarketPrice(1, 1, false), 2)
  assert.equal(normalizeMarketPrice(2, 2, false), 2)

  // 2. 普通材料（如灵草回收价 5 灵石）：挂单不得低于 5 灵石
  assert.equal(normalizeMarketPrice(2, 5, false), 5)
  assert.equal(normalizeMarketPrice(5, 5, true), 5)
  assert.equal(normalizeMarketPrice(6, 5, false), 6)

  // 3. 装备白板（以青锋剑基准回收价 432 为例）：步长在 400 价位为 10，向上归一为 440
  assert.equal(getMarketPriceIncrement(432), 10)
  assert.equal(normalizeMarketPrice(2, 432, false), 440)
  assert.equal(normalizeMarketPrice(100, 432, false), 440)
  assert.equal(normalizeMarketPrice(432, 432, true), 440)
  // 高于底价的正常挂单落网格
  assert.equal(normalizeMarketPrice(452, 432, false), 450)
  assert.equal(normalizeMarketPrice(452, 432, true), 460)

  // 4. 强化变体装备（以 +5 青锋剑回收价 2002 为例）：步长在 2000 价位按 1/2/5 阶梯为 50，向上归一为 2050
  assert.equal(getMarketPriceIncrement(2002), 50)
  assert.equal(normalizeMarketPrice(2, 2002, false), 2050)
  assert.equal(normalizeMarketPrice(500, 2002, false), 2050)
  assert.equal(normalizeMarketPrice(2002, 2002, true), 2050)
  assert.equal(normalizeMarketPrice(2120, 2002, false), 2100)
})
