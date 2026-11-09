import test from 'node:test'
import assert from 'node:assert/strict'
import {
  buildItemChatMarkup,
  buildOrderChatMarkup,
  parseChatMarkup,
} from './chatMarkup.ts'

test('chatMarkup: 纯文本不含标记时解析为单个 text 段', () => {
  const result = parseChatMarkup('道友请留步，一起下副本吗？')
  assert.equal(result.length, 1)
  assert.deepEqual(result[0], {
    type: 'text',
    text: '道友请留步，一起下副本吗？',
  })
})

test('chatMarkup: 基础物品与强化装备标记解析', () => {
  const text = '出售材料 {{item:item.morning-dew}} 与神兵 {{item:equipment.weapon-sword-t1-iron:3}}'
  const result = parseChatMarkup(text)

  assert.equal(result.length, 4)
  assert.equal(result[0].type, 'text')
  assert.equal(result[0].text, '出售材料 ')

  assert.equal(result[1].type, 'item')
  assert.equal(result[1].itemId, 'item.morning-dew')
  assert.equal(result[1].enhancementLevel, 0)

  assert.equal(result[2].type, 'text')
  assert.equal(result[2].text, ' 与神兵 ')

  assert.equal(result[3].type, 'item')
  assert.equal(result[3].itemId, 'equipment.weapon-sword-t1-iron')
  assert.equal(result[3].enhancementLevel, 3)
})

test('chatMarkup: 订单标记完整解析买卖方向、标的、强化、单价与数量', () => {
  const sellText = '我在坊市挂了一单：{{order:ord-100:sell:equipment.weapon-sword-t1-iron:5:200:1}}，欲购从速！'
  const sellResult = parseChatMarkup(sellText)

  assert.equal(sellResult.length, 3)
  assert.equal(sellResult[1].type, 'order')
  assert.equal(sellResult[1].orderId, 'ord-100')
  assert.equal(sellResult[1].side, 'sell')
  assert.equal(sellResult[1].itemId, 'equipment.weapon-sword-t1-iron')
  assert.equal(sellResult[1].enhancementLevel, 5)
  assert.equal(sellResult[1].price, 200)
  assert.equal(sellResult[1].quantity, 1)

  const buyText = '急收草药 {{order:ord-200:buy:item.morning-dew:0:15:100}} 速来'
  const buyResult = parseChatMarkup(buyText)
  assert.equal(buyResult.length, 3)
  assert.equal(buyResult[1].type, 'order')
  assert.equal(buyResult[1].side, 'buy')
  assert.equal(buyResult[1].itemId, 'item.morning-dew')
  assert.equal(buyResult[1].price, 15)
  assert.equal(buyResult[1].quantity, 100)
})

test('chatMarkup: 容错与异常标记降级为纯文本', () => {
  // 缺少字段的 order 标记
  const badOrder = '看这个 {{order:ord-1:buy:item.herb}} 坏标记'
  const r1 = parseChatMarkup(badOrder)
  assert.equal(r1.length, 1)
  assert.equal(r1[0].type, 'text')
  assert.equal(r1[0].text, '看这个 {{order:ord-1:buy:item.herb}} 坏标记')

  // side 非法的 order 标记
  const badSide = '{{order:ord-1:unknown:item.herb:0:10:1}}'
  const r2 = parseChatMarkup(badSide)
  assert.equal(r2.length, 1)
  assert.equal(r2[0].type, 'text')

  // 空 itemId
  const badItem = '{{item:}}'
  const r3 = parseChatMarkup(badItem)
  assert.equal(r3.length, 1)
  assert.equal(r3[0].type, 'text')
})

test('chatMarkup: 构建函数与往返解析一致性', () => {
  const itemTag = buildItemChatMarkup('equipment.heavy-axe', 7)
  assert.equal(itemTag, '{{item:equipment.heavy-axe:7}}')
  const itemParsed = parseChatMarkup(itemTag)
  assert.equal(itemParsed.length, 1)
  assert.equal(itemParsed[0].type, 'item')
  assert.equal(itemParsed[0].itemId, 'equipment.heavy-axe')
  assert.equal(itemParsed[0].enhancementLevel, 7)

  const orderTag = buildOrderChatMarkup({
    orderId: 'guid-abc-123',
    side: 'sell',
    itemId: 'equipment.heavy-axe',
    enhancementLevel: 7,
    price: 888,
    quantity: 1,
  })
  assert.equal(orderTag, '{{order:guid-abc-123:sell:equipment.heavy-axe:7:888:1}}')
  const orderParsed = parseChatMarkup(orderTag)
  assert.equal(orderParsed.length, 1)
  assert.equal(orderParsed[0].type, 'order')
  assert.equal(orderParsed[0].orderId, 'guid-abc-123')
  assert.equal(orderParsed[0].side, 'sell')
  assert.equal(orderParsed[0].price, 888)
  assert.equal(orderParsed[0].quantity, 1)
})

test('chatMarkup: 自动解析独立图片链接与混排表情包', () => {
  // 单独图片链接
  const singleUrl = 'https://example.com/stickers/cat.png'
  const r1 = parseChatMarkup(singleUrl)
  assert.equal(r1.length, 1)
  assert.equal(r1[0].type, 'sticker')
  assert.equal(r1[0].url, singleUrl)

  // 文本混排图片链接
  const mixed = '大家看这个仙符表情：https://example.com/stickers/talisman.webp 太赞了！'
  const r2 = parseChatMarkup(mixed)
  assert.equal(r2.length, 3)
  assert.equal(r2[0].type, 'text')
  assert.equal(r2[0].text, '大家看这个仙符表情：')
  assert.equal(r2[1].type, 'sticker')
  assert.equal(r2[1].url, 'https://example.com/stickers/talisman.webp')
  assert.equal(r2[2].type, 'text')
  assert.equal(r2[2].text, ' 太赞了！')

  // 显式 img/sticker 标记
  const tag = '{{img:https://example.com/stickers/sword.gif}}'
  const r3 = parseChatMarkup(tag)
  assert.equal(r3.length, 1)
  assert.equal(r3[0].type, 'sticker')
  assert.equal(r3[0].url, 'https://example.com/stickers/sword.gif')
})
