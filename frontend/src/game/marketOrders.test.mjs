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

test('marketPricing: 切换强化等级仅初始化一次默认价，后续盘口轮询与参考价变动不得回退用户单价', async () => {
  const { resolveMarketModalPriceAfterBook, resolveMarketOrderDefaultPrice } = await import('./marketPricing.ts')

  // 复现真实场景：在 +0 盘口详情页打开求购弹窗，切到 +10（回收底价 313900，网格底价 320000，无买盘参考价 660000）
  const initialPlus0 = resolveMarketOrderDefaultPrice({
    side: 'buy',
    bestBid: 40_000,
    referenceDefault: 30_000,
    vendorPrice: 20_000,
    minimumPrice: 20_000,
  })
  assert.equal(initialPlus0, 40_000)

  // 首次切到 +10：pricedLevel(0) !== targetLevel(10)，应应用 +10 默认参考价 66万 并标记已定价
  const switchedToPlus10 = resolveMarketModalPriceAfterBook({
    currentPrice: initialPlus0,
    pricedLevel: 0,
    targetLevel: 10,
    side: 'buy',
    bestBid: undefined,
    referenceDefault: 660_000,
    vendorPrice: 313_900,
    minimumPrice: 320_000,
  })
  assert.deepEqual(switchedToPlus10, {
    price: 660_000,
    pricedLevel: 10,
    changed: true,
  })

  // 玩家手动步进调高到 90万 后，再次收到盘口刷新或市场参考价波动：pricedLevel(10) === targetLevel(10)，绝不能回退回 66万
  const afterPolling = resolveMarketModalPriceAfterBook({
    currentPrice: 900_000,
    pricedLevel: switchedToPlus10.pricedLevel,
    targetLevel: 10,
    side: 'buy',
    bestBid: 640_000,
    referenceDefault: 660_000,
    vendorPrice: 313_900,
    minimumPrice: 320_000,
  })
  assert.deepEqual(afterPolling, {
    price: 900_000,
    pricedLevel: 10,
    changed: false,
  })

  // 玩家切回 +0 时：pricedLevel(10) !== targetLevel(0)，应重新应用 +0 的盘口买一价
  const switchedBackToPlus0 = resolveMarketModalPriceAfterBook({
    currentPrice: 900_000,
    pricedLevel: afterPolling.pricedLevel,
    targetLevel: 0,
    side: 'buy',
    bestBid: 42_000,
    referenceDefault: 30_000,
    vendorPrice: 20_000,
    minimumPrice: 20_000,
  })
  assert.deepEqual(switchedBackToPlus0, {
    price: 42_000,
    pricedLevel: 0,
    changed: true,
  })
})
