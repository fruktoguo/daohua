import test from 'node:test'
import assert from 'node:assert/strict'
import { computeBlobSha256, inferImageExtension } from './chatImageUploadService.ts'

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
