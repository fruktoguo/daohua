import test from 'node:test'
import assert from 'node:assert/strict'
import { chatStickerService, DEFAULT_EMOJIS, MAX_CUSTOM_STICKERS } from './chatStickerService.ts'
import { computeBlobSha256 } from './chatImageUploadService.ts'

test('chatStickerService: QQ 小黄脸表情频次累加与自排序', () => {
  chatStickerService._resetForTest()
  const initial = chatStickerService.getSortedEmojis()
  assert.ok(initial.length >= DEFAULT_EMOJIS.length)
  assert.ok(initial.includes('微笑'))
  assert.ok(initial.includes('呲牙'))

  chatStickerService.recordEmojiUsage('滑稽')
  chatStickerService.recordEmojiUsage('滑稽')
  chatStickerService.recordEmojiUsage('滑稽')

  chatStickerService.recordEmojiUsage('偷笑')
  chatStickerService.recordEmojiUsage('偷笑')

  const sorted = chatStickerService.getSortedEmojis()
  assert.equal(sorted[0], '滑稽')
  assert.equal(sorted[1], '偷笑')
})

test('chatStickerService: 自定义表情包添加、频次自排序、未收藏链接不污染与批量删除', () => {
  chatStickerService._resetForTest()
  const stickerA = 'https://example.com/stickers/cat.png'
  const stickerB = 'https://example.com/stickers/dog.png'

  assert.equal(chatStickerService.isStickerSaved(stickerA), false)
  assert.equal(chatStickerService.addCustomSticker(stickerA), true)
  assert.equal(chatStickerService.addCustomSticker(stickerB), true)
  assert.equal(chatStickerService.addCustomSticker(stickerA), false)

  assert.equal(chatStickerService.isStickerSaved(stickerA), true)
  assert.equal(chatStickerService.isStickerSaved(stickerB), true)

  chatStickerService.recordStickerUsage(stickerB)
  chatStickerService.recordStickerUsage(stickerB)

  // 未收藏的外部图片（如复读他人发言）不得自动写入表情包列表
  chatStickerService.recordStickerUsage('https://example.com/others/stranger.png')
  assert.equal(chatStickerService.isStickerSaved('https://example.com/others/stranger.png'), false)

  const sorted = chatStickerService.getSortedStickers()
  assert.equal(sorted[0].url, stickerB)
  assert.equal(sorted[0].count, 2)

  assert.equal(chatStickerService.removeCustomSticker(stickerA), true)
  assert.equal(chatStickerService.isStickerSaved(stickerA), false)
  assert.equal(chatStickerService.removeCustomSticker('https://not-exist.com/1.png'), false)

  const removedBatch = chatStickerService.removeCustomStickers([stickerB])
  assert.equal(removedBatch, 1)
  assert.equal(chatStickerService.getSortedStickers().length, 0)
})

test('chatStickerService: 达到 100 个上限时明确拦截，不静默删除旧表情', () => {
  chatStickerService._resetForTest()
  for (let i = 0; i < MAX_CUSTOM_STICKERS; i++) {
    const res = chatStickerService.addCustomStickerDetailed(`https://example.com/stickers/${i}.png`, {
      skipCacheFetch: true,
    })
    assert.equal(res.ok, true, `第 ${i + 1} 个应添加成功`)
  }

  assert.equal(chatStickerService.getSortedStickers().length, MAX_CUSTOM_STICKERS)

  const overflow = chatStickerService.addCustomStickerDetailed('https://example.com/stickers/overflow.png', {
    skipCacheFetch: true,
  })
  assert.equal(overflow.ok, false)
  assert.equal(overflow.reason, 'limit')
  assert.equal(chatStickerService.getSortedStickers().length, MAX_CUSTOM_STICKERS)
  assert.equal(chatStickerService.isStickerSaved('https://example.com/stickers/0.png'), true)
  assert.equal(chatStickerService.isStickerSaved('https://example.com/stickers/overflow.png'), false)
})

test('chatStickerService: 云端过期探测自动从本地重传并替换发送草稿直链', async () => {
  chatStickerService._resetForTest()
  const originalFetch = globalThis.fetch
  const oldUrl = 'https://beeimg.us.ci/temp/expired-1.png'
  const newUrl = 'https://beeimg.us.ci/temp/reupload-2.png'
  const pngBlob = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])], { type: 'image/png' })
  const fakeSha = await computeBlobSha256(pngBlob)

  const addRes = chatStickerService.addCustomStickerDetailed(oldUrl, {
    sha256: fakeSha,
    mime: 'image/png',
    blob: pngBlob,
    skipCacheFetch: true,
  })
  assert.equal(addRes.ok, true)

  // Node 单测无 IndexedDB，通过覆写 getStoredBlob 模拟本地已存储的原始二进制
  const origGetStoredBlob = chatStickerService.getStoredBlob.bind(chatStickerService)
  chatStickerService.getStoredBlob = async (url, sha) => {
    if (url === oldUrl || sha === fakeSha) return pngBlob
    return origGetStoredBlob(url, sha)
  }

  globalThis.fetch = async (input) => {
    const u = String(input)
    if (u === oldUrl) {
      return new Response('', { status: 404 })
    }
    if (u === newUrl) {
      return new Response('', { status: 200, headers: { 'content-type': 'image/png' } })
    }
    throw new Error(`unexpected fetch: ${u}`)
  }

  try {
    const ready = await chatStickerService.ensureStickerReadyForSend(oldUrl, async () => ({
      url: newUrl,
      sha256: fakeSha,
      mime: 'image/png',
    }))
    assert.equal(ready.healed, true)
    assert.equal(ready.missingLocalBlob, false)
    assert.equal(ready.url, newUrl)
    assert.equal(chatStickerService.findSticker(fakeSha)?.url, newUrl)

    // 发送草稿替换测试：模拟 newUrl 失效且本地有 Blob，prepareDraftStickersForSend 应自动重传并替换草稿链接
    const thirdUrl = 'https://beeimg.us.ci/temp/reupload-3.png'
    globalThis.fetch = async (input) => {
      const u = String(input)
      if (u === newUrl) return new Response('', { status: 410 })
      if (u.includes('/api/v2/group')) {
        return new Response(JSON.stringify({ status: 'success', data: { storages: [{ id: 4 }] } }), { status: 200 })
      }
      if (u.includes('/api/v2/upload')) {
        return new Response(JSON.stringify({ status: 'success', data: { public_url: thirdUrl } }), { status: 200 })
      }
      throw new Error(`unexpected fetch: ${u}`)
    }
    const draftHealed = await chatStickerService.prepareDraftStickersForSend(`看图 ${newUrl}`)
    assert.equal(draftHealed.ok, true)
    assert.equal(draftHealed.healedCount, 1)
    assert.equal(draftHealed.body, `看图 ${thirdUrl}`)
    assert.equal(chatStickerService.findSticker(fakeSha)?.url, thirdUrl)

    // 再测试缺失本地 Blob 时应阻断发送并标为待补传
    globalThis.fetch = async (input) => {
      if (String(input) === thirdUrl) return new Response('', { status: 404 })
      throw new Error(`unexpected fetch: ${String(input)}`)
    }
    chatStickerService.getStoredBlob = async () => null
    const draftMissing = await chatStickerService.prepareDraftStickersForSend(`看图 ${thirdUrl}`)
    assert.equal(draftMissing.ok, false)
    assert.equal(draftMissing.missingLocalBlob, true)
    assert.equal(chatStickerService.findSticker(fakeSha)?.needsReupload, true)
  } finally {
    chatStickerService.getStoredBlob = origGetStoredBlob
    globalThis.fetch = originalFetch
    chatStickerService._resetForTest()
  }
})

test('chatStickerService: preloadAllEmotes 与 scheduleIdlePreload 在无 DOM / 单测环境下安全运行', async () => {
  await chatStickerService.preloadAllEmotes()
  chatStickerService.scheduleIdlePreload()
  assert.ok(true)
})
