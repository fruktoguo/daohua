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

test('marketBrowse: 坊市在售总量聚合与角标显示模式自洽', () => {
  // 1. 模拟全服行情统计 marketStats：含基础物品与同一装备多强化等级
  const marketStats = {
    'item.iron-ore|0': {
      itemId: 'item.iron-ore',
      enhancementLevel: 0,
      askQuantity: 50,
    },
    'equipment.bronze-sword|0': {
      itemId: 'equipment.bronze-sword',
      enhancementLevel: 0,
      askQuantity: 3,
    },
    'equipment.bronze-sword|3': {
      itemId: 'equipment.bronze-sword',
      enhancementLevel: 3,
      askQuantity: 2,
    },
    'equipment.bronze-sword|5': {
      itemId: 'equipment.bronze-sword',
      enhancementLevel: 5,
      askQuantity: 1,
    },
    'item.rare-herb|0': {
      itemId: 'item.rare-herb',
      enhancementLevel: 0,
      askQuantity: 0,
    },
  }

  // 2. 聚合在售总量字典
  const marketOnSaleMap = new Map()
  for (const stats of Object.values(marketStats)) {
    if (stats.askQuantity > 0) {
      marketOnSaleMap.set(stats.itemId, (marketOnSaleMap.get(stats.itemId) ?? 0) + stats.askQuantity)
    }
  }

  // 验证：普通物品单盘口在售数量
  assert.equal(marketOnSaleMap.get('item.iron-ore'), 50)
  // 验证：装备标的汇总全部变体强化等级（0级3件 + 3级2件 + 5级1件 = 6件）
  assert.equal(marketOnSaleMap.get('equipment.bronze-sword'), 6)
  // 验证：无在售活动单标的返回 0
  assert.equal(marketOnSaleMap.get('item.rare-herb') ?? 0, 0)

  // 3. 验证模式文案插值与自洽
  const dict = marketZh.market.browse
  assert.equal(dict.amountMode.owned, '持有')
  assert.equal(dict.amountMode.onSale, '在售')

  const ownedAria = dict.itemOwned.replace('{name}', '铁矿石').replace('{count}', '10')
  assert.equal(ownedAria, '铁矿石，拥有 10')

  const onSaleAria = dict.itemOnSale.replace('{name}', '铁矿石').replace('{count}', '50')
  assert.equal(onSaleAria, '铁矿石，在售 50')
})
