import test from 'node:test'
import assert from 'node:assert/strict'
import { marketZh } from '../i18n/domains/market.zh.ts'

test('marketOrders: 订单已成交量推导与明细文案格式化自洽', () => {
  const dict = marketZh.market.status

  // 1. 部分成交订单：原始 10 件，剩余 8 件，推导已成交 2 件
  const orderPartial = {
    originalQuantity: 10,
    remainingQuantity: 8,
    filledQuantity: Math.max(0, 10 - 8),
    status: 'partially-filled',
  }
  assert.equal(orderPartial.filledQuantity, 2)

  const detailPartial = dict.partiallyFilledDetail
    .replace('{filled}', String(orderPartial.filledQuantity))
    .replace('{total}', String(orderPartial.originalQuantity))
    .replace('{remaining}', String(orderPartial.remainingQuantity))
  assert.equal(detailPartial, '已成交 2 / 总数量 10（剩余 8）')
  assert.equal(dict.partiallyFilledPrefix, '已成 ')

  // 2. 全新挂单：原始 100 件，未成交 100 件，已成交 0 件
  const orderActive = {
    originalQuantity: 100,
    remainingQuantity: 100,
    filledQuantity: Math.max(0, 100 - 100),
    status: 'active',
  }
  assert.equal(orderActive.filledQuantity, 0)

  const detailActive = dict.openDetail.replace('{total}', String(orderActive.originalQuantity))
  assert.equal(detailActive, '挂单中（总数量 100）')
  assert.equal(dict.open, '挂单中')

  // 3. 边界防御：异常情况下剩余大于原始时夹取为 0 已成交
  const orderUnderflow = {
    originalQuantity: 5,
    remainingQuantity: 6,
    filledQuantity: Math.max(0, 5 - 6),
    status: 'active',
  }
  assert.equal(orderUnderflow.filledQuantity, 0)
})

test('marketBook: 盘口自身订单按标的、等级、方向与价格精确命中', () => {
  const book = {
    itemId: 'item.gathering-herb-1',
    enhancementLevel: 0,
  }
  const orders = [
    // 1. 本标的自身买单（单价 120）
    { orderId: 'ord-1', itemId: 'item.gathering-herb-1', enhancementLevel: 0, side: 'buy', price: 120 },
    // 2. 本标的强化等级不一致买单（单价 120）
    { orderId: 'ord-2', itemId: 'item.gathering-herb-1', enhancementLevel: 1, side: 'buy', price: 120 },
    // 3. 其他标的买单（单价 120）
    { orderId: 'ord-3', itemId: 'item.ore-iron', enhancementLevel: 0, side: 'buy', price: 120 },
    // 4. 本标的自身卖单（单价 150）
    { orderId: 'ord-4', itemId: 'item.gathering-herb-1', enhancementLevel: 0, side: 'sell', price: 150 },
  ]

  // 模拟 MarketBookPanel 的 ownBidsByPrice 投影
  const ownBids = new Map()
  for (const order of orders) {
    if (order.itemId === book.itemId && order.enhancementLevel === book.enhancementLevel && order.side === 'buy') {
      ownBids.set(order.price, order)
    }
  }

  // 模拟 MarketBookPanel 的 ownAsksByPrice 投影
  const ownAsks = new Map()
  for (const order of orders) {
    if (order.itemId === book.itemId && order.enhancementLevel === book.enhancementLevel && order.side === 'sell') {
      ownAsks.set(order.price, order)
    }
  }

  // 验证求购盘口匹配：单价 120 命中 ord-1，单价 110 未命中，卖单不跨向混入
  assert.equal(ownBids.get(120)?.orderId, 'ord-1')
  assert.equal(ownBids.get(110), undefined)
  assert.equal(ownBids.get(150), undefined)

  // 验证出售盘口匹配：单价 150 命中 ord-4，单价 120 未命中
  assert.equal(ownAsks.get(150)?.orderId, 'ord-4')
  assert.equal(ownAsks.get(120), undefined)

  // 验证取消提示文案替换
  const aria = marketZh.market.book.cancelOwnOrderAria.replace('{price}', '120')
  assert.equal(aria, '撤销自身挂单（单价 120）')
})

test('marketBrowse: 坊市在售、求购总量与参考价格聚合与角标显示模式自洽', () => {
  // 1. 模拟全服行情统计 marketStats：含基础物品与同一装备多强化等级（在售 askQuantity、求购 bidQuantity 与参考价 referencePrice）
  const marketStats = {
    'item.iron-ore|0': {
      itemId: 'item.iron-ore',
      enhancementLevel: 0,
      askQuantity: 50,
      bidQuantity: 200,
      referencePrice: 15,
    },
    'equipment.bronze-sword|0': {
      itemId: 'equipment.bronze-sword',
      enhancementLevel: 0,
      askQuantity: 3,
      bidQuantity: 5,
      referencePrice: 120,
    },
    'equipment.bronze-sword|3': {
      itemId: 'equipment.bronze-sword',
      enhancementLevel: 3,
      askQuantity: 2,
      bidQuantity: 0,
      referencePrice: 280,
    },
    'equipment.bronze-sword|5': {
      itemId: 'equipment.bronze-sword',
      enhancementLevel: 5,
      askQuantity: 1,
      bidQuantity: 2,
      referencePrice: 500,
    },
    'equipment.wooden-staff|3': {
      itemId: 'equipment.wooden-staff',
      enhancementLevel: 3,
      askQuantity: 1,
      bidQuantity: 0,
      referencePrice: 90,
    },
    'item.rare-herb|0': {
      itemId: 'item.rare-herb',
      enhancementLevel: 0,
      askQuantity: 0,
      bidQuantity: 0,
      referencePrice: null,
    },
  }

  // 2. 聚合在售总量字典
  const marketOnSaleMap = new Map()
  for (const stats of Object.values(marketStats)) {
    if (stats.askQuantity > 0) {
      marketOnSaleMap.set(stats.itemId, (marketOnSaleMap.get(stats.itemId) ?? 0) + stats.askQuantity)
    }
  }
  // 2.1 聚合求购总量字典
  const marketBidMap = new Map()
  for (const stats of Object.values(marketStats)) {
    if (stats.bidQuantity > 0) {
      marketBidMap.set(stats.itemId, (marketBidMap.get(stats.itemId) ?? 0) + stats.bidQuantity)
    }
  }
  // 2.2 聚合参考价格字典（优先 0 级变体）
  const marketPriceMap = new Map()
  for (const stats of Object.values(marketStats)) {
    if (stats.referencePrice && stats.referencePrice > 0) {
      if (stats.enhancementLevel === 0 || !marketPriceMap.has(stats.itemId)) {
        marketPriceMap.set(stats.itemId, stats.referencePrice)
      }
    }
  }

  // 验证：普通物品单盘口在售、求购数量与参考价
  assert.equal(marketOnSaleMap.get('item.iron-ore'), 50)
  assert.equal(marketBidMap.get('item.iron-ore'), 200)
  assert.equal(marketPriceMap.get('item.iron-ore'), 15)

  // 验证：装备标的汇总全部变体强化等级（在售：0级3件 + 3级2件 + 5级1件 = 6件；求购：0级5件 + 5级2件 = 7件）
  assert.equal(marketOnSaleMap.get('equipment.bronze-sword'), 6)
  assert.equal(marketBidMap.get('equipment.bronze-sword'), 7)
  // 装备优先取 0 级参考价格（120）
  assert.equal(marketPriceMap.get('equipment.bronze-sword'), 120)
  // 若 0 级无成交，回退变体参考价（90）
  assert.equal(marketPriceMap.get('equipment.wooden-staff'), 90)

  // 验证：无在售、求购活动单或无参考价标的返回 0 或 undefined
  assert.equal(marketOnSaleMap.get('item.rare-herb') ?? 0, 0)
  assert.equal(marketBidMap.get('item.rare-herb') ?? 0, 0)
  assert.equal(marketPriceMap.get('item.rare-herb'), undefined)

  // 3. 验证模式文案插值与自洽
  const dict = marketZh.market.browse
  assert.equal(dict.amountMode.owned, '持有')
  assert.equal(dict.amountMode.onSale, '在售')
  assert.equal(dict.amountMode.bid, '求购')
  assert.equal(dict.amountMode.price, '参考价')

  const ownedAria = dict.itemOwned.replace('{name}', '铁矿石').replace('{count}', '10')
  assert.equal(ownedAria, '铁矿石，拥有 10')

  const onSaleAria = dict.itemOnSale.replace('{name}', '铁矿石').replace('{count}', '50')
  assert.equal(onSaleAria, '铁矿石，在售 50')

  const bidAria = dict.itemBid.replace('{name}', '铁矿石').replace('{count}', '200')
  assert.equal(bidAria, '铁矿石，求购 200')

  const priceAria = dict.itemPrice.replace('{name}', '铁矿石').replace('{price}', '15')
  assert.equal(priceAria, '铁矿石，参考价 15')

  const noPriceAria = dict.itemNoPrice.replace('{name}', '稀有灵草')
  assert.equal(noPriceAria, '稀有灵草，暂无参考价')
})
test('marketBrowse: 标的卡片 ItemTile 必须显式携带 itemId 属性，确保图标资源正常解析', async () => {
  const fs = await import('node:fs/promises')
  const path = await import('node:path')
  const filePath = path.resolve('frontend/src/components/market/MarketBrowseGrid.tsx')
  const content = await fs.readFile(filePath, 'utf8')

  // 必须在 ItemTile 调用点传 itemId={item.itemId}
  assert.match(
    content,
    /<ItemTile[\s\S]*?itemId=\{item\.itemId\}[\s\S]*?\/>/,
    'MarketBrowseCardItem 中的 ItemTile 必须传 itemId={item.itemId}，否则瓦片将无法命中图标回退为纯文字'
  )
})

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
