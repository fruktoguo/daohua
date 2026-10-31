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
