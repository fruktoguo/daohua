import test from 'node:test'
import assert from 'node:assert/strict'
import {
  _resetImageUploadStateForTest,
  computeBlobSha256,
  detectRasterImageMime,
  inferImageExtension,
  fetchImageBlobForCaching,
  uploadChatImage,
} from './chatImageUploadService.ts'
import { setSessionAccount } from '../network/sessionState.ts'

test('chatImageUploadService: inferImageExtension 文件名与 MIME 识别', () => {
  const blobPng = new Blob(['123'], { type: 'image/png' })
  assert.equal(inferImageExtension(blobPng), 'png')

  const blobGif = new Blob(['123'], { type: 'image/gif' })
  assert.equal(inferImageExtension(blobGif), 'gif')

  const blobWebp = new Blob(['123'], { type: 'image/webp' })
  assert.equal(inferImageExtension(blobWebp), 'webp')

  const blobJpg = new Blob(['123'], { type: 'image/jpeg' })
  assert.equal(inferImageExtension(blobJpg), 'jpg')

  const fileCustom = new File(['123'], 'avatar.webp', { type: 'application/octet-stream' })
  assert.equal(inferImageExtension(fileCustom), 'webp')
})

test('chatImageUploadService: computeBlobSha256 计算一致性', async () => {
  const blobA = new Blob(['daohua-chat-image-test-1'])
  const blobB = new Blob(['daohua-chat-image-test-1'])
  const blobC = new Blob(['daohua-chat-image-test-2'])

  const hashA = await computeBlobSha256(blobA)
  const hashB = await computeBlobSha256(blobB)
  const hashC = await computeBlobSha256(blobC)

  assert.equal(hashA.length, 64)
  assert.equal(hashA, hashB, '相同二进制内容必须产生完全相同的 SHA-256')
  assert.notEqual(hashA, hashC, '不同二进制内容哈希不可碰撞')
})

test('chatImageUploadService: detectRasterImageMime 魔数检测拒绝伪装文本', async () => {
  const pngBytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00])
  const htmlBytes = new TextEncoder().encode('<html>404</html>')
  assert.equal(await detectRasterImageMime(new Blob([pngBytes])), 'image/png')
  assert.equal(await detectRasterImageMime(new Blob([htmlBytes])), null)
})

test('chatImageUploadService: 动态探测 storage_id、失效刷新重试与重复上传不锁死旧死链', async () => {
  _resetImageUploadStateForTest()
  const originalFetch = globalThis.fetch
  let groupFetchCount = 0
  let uploadAttempt = 0
  const uploadedStorageIds = []

  globalThis.fetch = async (input, init) => {
    const url = String(input)
    if (url.includes('/api/v2/group')) {
      groupFetchCount += 1
      const id = groupFetchCount === 1 ? 2 : 4
      return new Response(JSON.stringify({
        status: 'success',
        data: {
          group: { options: { max_upload_size: 5120, allow_file_types: ['png', 'jpg', 'webp'] } },
          storages: [{ id }],
        },
      }), { status: 200 })
    }
    if (url.includes('/api/v2/upload')) {
      uploadAttempt += 1
      const fd = init?.body
      const sid = fd && typeof fd.get === 'function' ? fd.get('storage_id') : null
      uploadedStorageIds.push(String(sid))
      if (sid === '2') {
        return new Response(JSON.stringify({ status: 'error', message: '不存在的储存驱动' }), { status: 200 })
      }
      return new Response(JSON.stringify({
        status: 'success',
        data: { public_url: `https://beeimg.us.ci/temp/upload-${uploadAttempt}.png` },
      }), { status: 200 })
    }
    throw new Error(`unexpected fetch: ${url}`)
  }

  try {
    const pngBlob = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01])], { type: 'image/png' })
    const first = await uploadChatImage(pngBlob)
    assert.equal(first.url, 'https://beeimg.us.ci/temp/upload-2.png')
    assert.deepEqual(uploadedStorageIds, ['2', '4'])
    assert.equal(first.sha256.length, 64)

    // 同一 Blob 再次调用 uploadChatImage（如云端过期重传）不得命中旧死链缓存，必须真实重传获取新 URL
    const second = await uploadChatImage(pngBlob)
    assert.equal(second.url, 'https://beeimg.us.ci/temp/upload-3.png')
    assert.notEqual(second.url, first.url)
  } finally {
    globalThis.fetch = originalFetch
    _resetImageUploadStateForTest()
  }
})

test('直连无 Content-Length 的超限图像立即取消读取且不重走中继', async t => {
  let reads = 0
  let cancelled = false
  const stream = new ReadableStream({
    pull(controller) { reads++; controller.enqueue(new Uint8Array(6 * 1024 * 1024)) },
    cancel() { cancelled = true },
  }, { highWaterMark: 0 })
  const fetch = t.mock.method(globalThis, 'fetch', async () => new Response(stream))
  assert.equal(await fetchImageBlobForCaching('https://fixture.invalid/large.png'), null)
  assert.equal(reads, 2)
  assert.equal(cancelled, true)
  assert.equal(fetch.mock.callCount(), 1)
})

test('会话切换作废已排队上传，以及发出后迟到且需要重传的结果', async t => {
  _resetImageUploadStateForTest()
  t.after(() => { _resetImageUploadStateForTest(); setSessionAccount(null) })
  let postStarted, resolvePost
  const started = new Promise(resolve => { postStarted = resolve })
  const response = new Promise(resolve => { resolvePost = resolve })
  let uploads = 0, strategyLoads = 0
  t.mock.method(globalThis, 'fetch', async input => {
    const url = String(input)
    if (url.endsWith('/group')) {
      strategyLoads++
      return Response.json({ status: 'success', data: { storages: [{ id: 4 }] } })
    }
    if (url.endsWith('/upload')) {
      uploads++
      postStarted()
      return response
    }
    assert.fail('未预期的请求')
  })
  const png = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])], { type: 'image/png' })
  const first = assert.rejects(uploadChatImage(png), { name: 'AbortError' })
  const queued = assert.rejects(uploadChatImage(png), { name: 'AbortError' })
  await started
  setSessionAccount(null)
  resolvePost(Response.json({ status: 'error', message: '不存在的储存驱动' }))
  await Promise.all([first, queued])
  assert.equal(uploads, 1)
  assert.equal(strategyLoads, 1, '旧任务不再刷新图床策略并重传')
})
