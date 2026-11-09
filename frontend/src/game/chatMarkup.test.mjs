import test from 'node:test'
import assert from 'node:assert/strict'
import {
  buildItemChatMarkup,
  buildOrderChatMarkup,
  buildPartyChatMarkup,
  buildLocationChatMarkup,
  parseChatMarkup,
} from './chatMarkup.ts'
import { getQqEmojiUrls, QQ_EMOJI_LIST } from './qqEmojis.ts'

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

test('chatMarkup: 队伍与地点标记解析与往返一致性', () => {
  // 1. 队伍完整字段构建与解析
  const partyTag = buildPartyChatMarkup({
    partyId: 'f47ac10b-58cc-4372-a567-0e02b2c3d479',
    name: '九霄诛仙队',
    activityKind: 'combat',
    nodeId: 'node.cloud-peak',
    minValuation: 10000,
    memberCount: 2,
    maxSlots: 3,
  })
  assert.equal(
    partyTag,
    '{{party:f47ac10b-58cc-4372-a567-0e02b2c3d479:九霄诛仙队:combat:node.cloud-peak:10000:2:3}}',
  )
  const partyParsed = parseChatMarkup(`招募队友：${partyTag}，满员即开！`)
  assert.equal(partyParsed.length, 3)
  assert.equal(partyParsed[1].type, 'party')
  if (partyParsed[1].type === 'party') {
    assert.equal(partyParsed[1].partyId, 'f47ac10b-58cc-4372-a567-0e02b2c3d479')
    assert.equal(partyParsed[1].name, '九霄诛仙队')
    assert.equal(partyParsed[1].activityKind, 'combat')
    assert.equal(partyParsed[1].nodeId, 'node.cloud-peak')
    assert.equal(partyParsed[1].minValuation, 10000)
    assert.equal(partyParsed[1].memberCount, 2)
    assert.equal(partyParsed[1].maxSlots, 3)
  }

  // 2. 地点标记构建与解析
  const locTag = buildLocationChatMarkup('node.cloud-peak', '碧云峰')
  assert.equal(locTag, '{{location:node.cloud-peak:碧云峰}}')
  const locParsed = parseChatMarkup(`速来 ${locTag} 集合`)
  assert.equal(locParsed.length, 3)
  assert.equal(locParsed[1].type, 'location')
  if (locParsed[1].type === 'location') {
    assert.equal(locParsed[1].nodeId, 'node.cloud-peak')
    assert.equal(locParsed[1].name, '碧云峰')
  }

  // 3. 地点 map 别名与缺省名称解析
  const mapTag = '{{map:node.lake-center}}'
  const mapParsed = parseChatMarkup(mapTag)
  assert.equal(mapParsed.length, 1)
  assert.equal(mapParsed[0].type, 'location')
  if (mapParsed[0].type === 'location') {
    assert.equal(mapParsed[0].nodeId, 'node.lake-center')
    assert.equal(mapParsed[0].name, undefined)
  }

  // 4. 空 partyId 或空 nodeId 容错降级
  const badParty = parseChatMarkup('{{party:}}')
  assert.equal(badParty.length, 1)
  assert.equal(badParty[0].type, 'text')

  const badLoc = parseChatMarkup('{{location:}}')
  assert.equal(badLoc.length, 1)
  assert.equal(badLoc[0].type, 'text')
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
  // 中文紧随混排无空格图片链接
  const tightMixed = '看这个图https://img.yuohira.com/43be52f97ed30891.png好搞笑'
  const rTight = parseChatMarkup(tightMixed)
  assert.equal(rTight.length, 3)
  assert.equal(rTight[0].type, 'text')
  assert.equal(rTight[0].text, '看这个图')
  assert.equal(rTight[1].type, 'sticker')
  assert.equal(rTight[1].url, 'https://img.yuohira.com/43be52f97ed30891.png')
  assert.equal(rTight[2].type, 'text')
  assert.equal(rTight[2].text, '好搞笑')

  // 显式 img/sticker 标记
  const tag = '{{img:https://example.com/stickers/sword.gif}}'
  const r3 = parseChatMarkup(tag)
  assert.equal(r3.length, 1)
  assert.equal(r3[0].type, 'sticker')
  assert.equal(r3[0].url, 'https://example.com/stickers/sword.gif')
})

test('chatMarkup: 自动解析 QQ 风格经典小黄脸表情 [微笑] [呲牙]', () => {
  const text = '道友你好[微笑]，一起下本[偷笑]！'
  const result = parseChatMarkup(text)
  assert.equal(result.length, 5)
  assert.equal(result[0].type, 'text')
  assert.equal(result[0].text, '道友你好')
  assert.equal(result[1].type, 'qqEmoji')
  assert.equal(result[1].name, '微笑')
  assert.equal(result[2].type, 'text')
  assert.equal(result[2].text, '，一起下本')
  assert.equal(result[3].type, 'qqEmoji')
  assert.equal(result[3].name, '偷笑')
  assert.equal(result[4].type, 'text')
  assert.equal(result[4].text, '！')

  // 未收录的伪中括号标记保持纯文本
  const unk = '测试[未知标记]保持文本'
  const r2 = parseChatMarkup(unk)
  assert.equal(r2.length, 1)
  assert.equal(r2[0].type, 'text')
  assert.equal(r2[0].text, '测试[未知标记]保持文本')
})

test('chatMarkup: 205 个经典内置动态表情名称与 QSid 唯一且支持双源分发解析', () => {
  assert.equal(QQ_EMOJI_LIST.length, 205)
  assert.equal(new Set(QQ_EMOJI_LIST.map((emoji) => emoji.name)).size, 205)
  assert.equal(new Set(QQ_EMOJI_LIST.map((emoji) => emoji.qsid)).size, 205)
  assert.equal(new Set(QQ_EMOJI_LIST.map((emoji) => emoji.id)).size, 205)

  for (const emoji of QQ_EMOJI_LIST) {
    const [segment] = parseChatMarkup(`[${emoji.name}]`)
    assert.equal(segment?.type, 'qqEmoji', `[${emoji.name}] 应识别为内置表情`)
    assert.equal(segment?.name, emoji.name)
    const urls = getQqEmojiUrls(emoji.qsid)
    assert.ok(urls.cfUrl.startsWith('https://img.yuohira.com/emotes/s'))
    assert.ok(urls.localUrl.startsWith('/emotes/s'))
  }
})
