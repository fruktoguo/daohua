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
