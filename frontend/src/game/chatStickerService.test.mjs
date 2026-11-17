import test from 'node:test'
import assert from 'node:assert/strict'
import { chatStickerService, DEFAULT_EMOJIS, MAX_CUSTOM_STICKERS } from './chatStickerService.ts'
import { computeBlobSha256 } from './chatImageUploadService.ts'
import { setSessionAccount } from '../network/sessionState.ts'

const originalFetch = globalThis.fetch
// 所有外链都由用例给出本地 fixture；未指定的请求不得真的访问网络。
test.before(() => { globalThis.fetch = async input => { throw new Error(`未模拟的请求：${input}`) } })
test.after(() => { globalThis.fetch = originalFetch })

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

const flush = () => new Promise(resolve => setImmediate(resolve))
function deferredResponse() {
  let resolve
  const promise = new Promise(res => { resolve = res })
  return { promise, resolve }
}

for (const sameAccount of [false, true]) {
  test(`收藏同步正文和删除响应晚到时不污染${sameAccount ? '同账号重登' : '新账号'}或解除新同步锁`, async t => {
    const service = chatStickerService
    service._resetForTest()
    const syncs = []
    const deletion = deferredResponse()
    t.mock.method(globalThis, 'fetch', (input, init) => {
      const url = String(input)
      if (url.endsWith('/stickers/delete')) return deletion.promise
      if (!url.endsWith('/stickers/sync')) return Promise.reject(new Error('仅使用本地模拟'))
      const reply = deferredResponse()
      syncs.push({ ...reply, payload: JSON.parse(init.body) })
      return reply.promise
    })
    t.after(() => { service._resetForTest(); setSessionAccount(null) })
    const old = deferredResponse()
    const bodyStarted = deferredResponse()
    const oldId = 'old-player'
    const newId = sameAccount ? oldId : 'new-player'
    service.setActivePlayer(oldId)
    const oldSync = service.syncWithServer()
    syncs[0].resolve({ ok: true, status: 200, json() { bodyStarted.resolve(); return old.promise } })
    await bodyStarted.promise
    const removed = 'https://fixture.invalid/removed.png'
    service.addCustomStickerDetailed(removed, { skipCacheFetch: true })
    assert.equal(service.removeCustomSticker(removed), true)
    // 同 playerId 也必须因真实登录世代变化作废。
    setSessionAccount({ playerId: newId, accountId: newId, account: newId, name: newId })
    service.setActivePlayer(newId)
    assert.equal(syncs.length, 2)
    const fresh = service.syncWithServer()
    const staleUrl = 'https://fixture.invalid/stale.png'
    const stale = { stickers: [{ stickerId: 'stale', url: staleUrl, updatedAt: 1 }], tombstones: [{ stickerId: 'late-tomb', deletedAt: 1 }] }
    old.resolve(stale)
    deletion.resolve(Response.json(stale))
    await oldSync
    await flush()
    const deduped = service.syncWithServer()
    assert.equal(syncs.length, 2, '旧 finally 不得清空当前账号的在途同步')
    assert.equal(service.isStickerSaved(staleUrl), false)
    syncs[1].resolve(Response.json({ stickers: [], tombstones: [] }))
    await Promise.all([fresh, deduped])
    const sync = service.syncWithServer()
    assert.equal(syncs[2].payload.items.some(item => item.stickerId === 'stale'), false)
    assert.equal(syncs[2].payload.deleted.some(item => item.stickerId === 'late-tomb'), false)
    syncs[2].resolve(Response.json({ stickers: [], tombstones: [] }))
    await sync
  })
}

test('Blob URL 按字节预算回收：切会话后旧租约不撤销新图', async t => {
  const service = chatStickerService
  service._resetForTest()
  t.after(() => service._resetForTest())
  t.mock.method(globalThis, 'fetch', async () => Response.json({ stickers: [], tombstones: [] }))
  const revoked = new Set()
  const revoke = URL.revokeObjectURL.bind(URL)
  t.mock.method(URL, 'revokeObjectURL', url => { revoked.add(url); revoke(url) })
  const blob = new Blob([new Uint8Array(8 * 1024 * 1024)])
  await service.saveImageBlob('/a.png', blob, 'a')
  const old = await service.acquireImage('/a.png')
  await service.saveImageBlob('/b.png', blob, 'b')
  const expendable = await service.acquireImage('/b.png')
  expendable.release()
  await service.saveImageBlob('/c.png', blob, 'c')
  await service.saveImageBlob('/d.png', blob, 'd')
  assert.equal(revoked.has(expendable.src), true)
  assert.equal(revoked.has(old.src), false)
  service.setActivePlayer('new-player')
  await service.syncWithServer()
  await service.saveImageBlob('/a.png', blob, 'a')
  const fresh = await service.acquireImage('/a.png')
  assert.notEqual(fresh.src, old.src)
  old.release()
  assert.equal(revoked.has(old.src), true)
  assert.equal(revoked.has(fresh.src), false)
  fresh.release()
})
