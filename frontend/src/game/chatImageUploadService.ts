/**
 * 传音图片上传服务
 *
 * 核心流程：
 * 1. 计算图片 SHA-256 摘要；
 * 2. 检查客户端本地缓存（若曾上传过，直接秒回直链，0 网络开销）；
 * 3. 请求道化主服 /api/v1/chat/upload-ticket：
 *    - 主服执行账号鉴权与每日 20 张配额检查；
 *    - 主服执行全服哈希去重预检（若全服已有，秒回直链且不扣配额）；
 *    - 若为新图，签发具有 5 分钟有效期的 HMAC-SHA256 签名 Ticket；
 * 4. 携带 Ticket 直传 Cloudflare Worker，返回短直链并写入本地缓存。
 */
import { chatStickerService } from './chatStickerService.ts'

const STORAGE_KEY_HASH_MAP = 'daohua_chat_hash_urls'

/** 本地哈希去重内存缓存 */
let memoryHashCache: Record<string, string> | null = null

function loadLocalHashCache(): Record<string, string> {
  if (memoryHashCache !== null) return memoryHashCache
  if (typeof window === 'undefined' || !window.localStorage) {
    memoryHashCache = {}
    return memoryHashCache
  }
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY_HASH_MAP)
    memoryHashCache = raw ? JSON.parse(raw) : {}
  } catch {
    memoryHashCache = {}
  }
  return memoryHashCache ?? {}
}

function saveLocalHashUrl(hash: string, url: string): void {
  const cache = loadLocalHashCache()
  cache[hash.toLowerCase()] = url
  if (typeof window !== 'undefined' && window.localStorage) {
    try {
      window.localStorage.setItem(STORAGE_KEY_HASH_MAP, JSON.stringify(cache))
    } catch {
      // 忽略 localStorage 容量异常
    }
  }
}

/**
 * 计算 Blob / File 的 SHA-256 十六进制摘要
 */
export async function computeBlobSha256(blob: Blob): Promise<string> {
  const buffer = await blob.arrayBuffer()
  const digest = await crypto.subtle.digest('SHA-256', buffer)
  return Array.from(new Uint8Array(digest))
    .map(b => b.toString(16).padStart(2, '0'))
    .join('')
}

/**
 * 推断图片文件的合法扩展名
 */
export function inferImageExtension(blob: Blob): string {
  if (blob instanceof File && blob.name) {
    const match = blob.name.match(/\.([a-zA-Z0-9]+)$/)
    if (match) {
      const ext = match[1].toLowerCase()
      if (['png', 'gif', 'webp', 'jpg', 'jpeg'].includes(ext)) {
        return ext === 'jpeg' ? 'jpg' : ext
      }
    }
  }

  const mime = blob.type.toLowerCase()
  if (mime.includes('gif')) return 'gif'
  if (mime.includes('webp')) return 'webp'
  if (mime.includes('jpeg') || mime.includes('jpg')) return 'jpg'
  return 'png'
}

interface UploadTicketResponse {
  cached: boolean
  url: string
  ticket?: string
  uploadEndpoint?: string
  remainingToday: number
}

/**
 * 上传图片文件并获取图片直链
 * @param blob 待上传的图片文件对象（截图 Blob 或 File）
 * @returns 图片直链 URL
 */
export async function uploadChatImage(blob: Blob): Promise<{ url: string; cached: boolean }> {
  // 1. 计算图片内容 SHA-256 哈希
  const sha256 = await computeBlobSha256(blob)
  const ext = inferImageExtension(blob)

  // 2. 客户端本地哈希去重预检（若当前浏览器曾上传过，直接秒开）
  const localCache = loadLocalHashCache()
  if (localCache[sha256]) {
    return { url: localCache[sha256], cached: true }
  }

  // 3. 向道化主服申请上传 Ticket（带会话 Cookie，执行账号鉴权、每日20张上限与全服去重）
  const ticketRes = await fetch('/api/v1/chat/upload-ticket', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sha256, ext }),
  })

  if (!ticketRes.ok) {
    let message = '图片上传凭证申请失败'
    try {
      const errJson = await ticketRes.json()
      message = errJson.detail || errJson.title || message
    } catch {
      message = await ticketRes.text() || message
    }
    throw new Error(message)
  }

  const ticketData: UploadTicketResponse = await ticketRes.json()

  // 4. 若主服全服去重命中（其他玩家已上传过同图），直接复用，不调用 Cloudflare 也无需上传
  if (ticketData.cached || !ticketData.ticket || !ticketData.uploadEndpoint) {
    saveLocalHashUrl(sha256, ticketData.url)
    return { url: ticketData.url, cached: true }
  }

  // 5. 携带 Ticket 直传 Cloudflare Worker
  const formData = new FormData()
  formData.append('file', blob, `image.${ext}`)

  const uploadRes = await fetch(ticketData.uploadEndpoint, {
    method: 'POST',
    headers: {
      'X-Upload-Ticket': ticketData.ticket,
    },
    body: formData,
  })

  if (!uploadRes.ok) {
    const errText = await uploadRes.text()
    throw new Error(`图床存储失败 [${uploadRes.status}]: ${errText}`)
  }

  const directUrl = (await uploadRes.text()).trim()
  if (!directUrl.startsWith('https://')) {
    throw new Error(`图床返回异常地址: ${directUrl}`)
  }

  // 成功上传至 R2 后，异步通知主服登记全服哈希映射
  fetch('/api/v1/chat/upload-confirm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sha256, url: directUrl }),
  }).catch(() => {})
  // 写入客户端本地哈希缓存与 IndexedDB 离线 Blob 缓存，秒级回显且支持未来过期自愈
  saveLocalHashUrl(sha256, directUrl)
  await chatStickerService.saveImageBlob(directUrl, blob).catch(() => {})
  return { url: directUrl, cached: false }
}
