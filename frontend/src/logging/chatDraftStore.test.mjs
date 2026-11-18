import test from 'node:test'
import assert from 'node:assert/strict'
import { EMPTY_CHAT_DRAFT, useChatDraftStore } from './chatDraftStore.ts'
import { conversationKey } from './chatService.ts'

test('手机切页前写入的链接和引文不丢失、不会串会话或跨账号', () => {
  const store = useChatDraftStore.getState
  const world = conversationKey('world', '')
  const party = conversationKey('party', 'party-a')
  const quote = { messageId: 'm1', senderName: '道友', body: '早上好' }
  store().activate('player-a')
  store().edit(world, () => ({ body: '已有草稿', quote }))
  assert.equal(store().append(world, '{{item:item.example:0}}'), true)
  store().requestFocus(world)
  assert.equal(store().focusKey, world)
  store().consumeFocus(party)
  assert.equal(store().focusKey, world)
  store().consumeFocus(world)
  assert.equal(store().focusKey, null)
  assert.deepEqual(store().drafts.get(world), { body: '已有草稿 {{item:item.example:0}}', quote })
  assert.equal(store().append(world, '道'.repeat(200)), false)
  assert.deepEqual(store().drafts.get(party) ?? EMPTY_CHAT_DRAFT, EMPTY_CHAT_DRAFT)
  store().edit(world, current => ({ ...current, quote: null }))
  assert.equal(store().drafts.get(world).body, '已有草稿 {{item:item.example:0}}')
  store().activate('player-b')
  assert.equal(store().drafts.size, 0)
})
