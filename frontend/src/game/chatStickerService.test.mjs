import test from 'node:test'
import assert from 'node:assert/strict'
import { chatStickerService, DEFAULT_EMOJIS } from './chatStickerService.ts'

test('chatStickerService: Emoji 频次累加与自排序', () => {
  const initial = chatStickerService.getSortedEmojis()
  assert.ok(initial.length >= DEFAULT_EMOJIS.length)

  // 记录特定修仙表情高频使用
  chatStickerService.recordEmojiUsage('🧘')
  chatStickerService.recordEmojiUsage('🧘')
  chatStickerService.recordEmojiUsage('🧘')

  chatStickerService.recordEmojiUsage('🗡️')
  chatStickerService.recordEmojiUsage('🗡️')

  const sorted = chatStickerService.getSortedEmojis()
  // '🧘' 次数最多，应位列第一
  assert.equal(sorted[0], '🧘')
  // '🗡️' 次数第二多，应位列第二
  assert.equal(sorted[1], '🗡️')
})

test('chatStickerService: 自定义表情包添加、频次自排序与右键删除', () => {
  const stickerA = 'https://example.com/stickers/cat.png'
  const stickerB = 'https://example.com/stickers/dog.png'

  // 初始未收藏
  assert.equal(chatStickerService.isStickerSaved(stickerA), false)

  // 添加表情包 A 和 B
  assert.equal(chatStickerService.addCustomSticker(stickerA), true)
  assert.equal(chatStickerService.addCustomSticker(stickerB), true)
  assert.equal(chatStickerService.addCustomSticker(stickerA), false) // 重复添加返回 false

  assert.equal(chatStickerService.isStickerSaved(stickerA), true)
  assert.equal(chatStickerService.isStickerSaved(stickerB), true)

  // 模拟多次使用 B
  chatStickerService.recordStickerUsage(stickerB)
  chatStickerService.recordStickerUsage(stickerB)

  const sorted = chatStickerService.getSortedStickers()
  assert.equal(sorted[0].url, stickerB)
  assert.equal(sorted[0].count, 2)

  // 删除表情包 A
  assert.equal(chatStickerService.removeCustomSticker(stickerA), true)
  assert.equal(chatStickerService.isStickerSaved(stickerA), false)

  // 删除不存在的表情包
  assert.equal(chatStickerService.removeCustomSticker('https://not-exist.com/1.png'), false)
})
