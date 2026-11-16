/**
 * 传音图片与表情包直传及拉取服务
 *
 * 核心职责：
 * 1. 计算图片 SHA-256 摘要，提供格式识别与超限/不支持格式自适应 WebP 压缩转码；
 * 2. 动态探测蜜蜂图床（www.beeimg.cn）当前可用的游客存储策略 ID，并按并发度 1 串行排队直传；
 * 3. 提供浏览器 CORS 优先 + 服务端无状态防 SSRF 中继兜底的图片二进制拉取能力，
 *    保证本地 IndexedDB 强落盘，彻底消除永久图床依赖与过期死链锁死问题。
 */
import { getApiUrl } from '../network/apiUrl.ts'

const BEEIMG_GROUP_ENDPOINT = 'https://www.beeimg.cn/api/v2/group'
const BEEIMG_UPLOAD_ENDPOINT = 'https://www.beeimg.cn/api/v2/upload'
const GROUP_CACHE_TTL_MS = 10 * 60 * 1000
const IMAGE_HOST_UPLOAD_TIMEOUT_MS = 30_000
const IMAGE_FETCH_TIMEOUT_MS = 10_000
const DEFAULT_MAX_UPLOAD_BYTES = Math.floor(4.8 * 1024 * 1024)
const FALLBACK_GUEST_STORAGE_ID = '4'

interface BeeimgGroupResponse {
  status: 'success' | 'error'
  data?: {
    group?: {
      options?: {
        max_upload_size?: number
        allow_file_types?: string[]
      }
    }
    storages?: Array<{
      id: number | string
      name?: string
    }>
  }
}

interface BeeimgUploadResponse {
  status: 'success' | 'error'
  message?: string
  data?: {
    public_url?: string
  }
}

export interface UploadChatImageResult {
  url: string
  sha256: string
  mime: string
  blob: Blob
  cached: boolean
  transcodedFromGif?: boolean
}

let cachedStorageId: string | null = null
let cachedMaxUploadBytes = DEFAULT_MAX_UPLOAD_BYTES
let cachedAllowedTypes = new Set(['jpg', 'jpeg', 'png', 'webp'])
let groupFetchedAt = 0

/** 串行队列尾节点：严格贴合图床游客策略 limit_concurrent_upload=1 */
let uploadQueueTail: Promise<void> = Promise.resolve()

function enqueueUpload<T>(task: () => Promise<T>): Promise<T> {
  const run = uploadQueueTail.then(task, task)
  uploadQueueTail = run.then(() => undefined, () => undefined)
  return run
}

/** 计算 Blob / File 的 SHA-256 小写十六进制摘要 */
export async function computeBlobSha256(blob: Blob): Promise<string> {
  const buffer = await blob.arrayBuffer()
  const digest = await crypto.subtle.digest('SHA-256', buffer)
  return Array.from(new Uint8Array(digest))
    .map(b => b.toString(16).padStart(2, '0'))
    .join('')
}

/** 计算字符串的 SHA-256 小写十六进制摘要 */
export async function computeTextSha256(text: string): Promise<string> {
  const encoded = new TextEncoder().encode(text)
  const digest = await crypto.subtle.digest('SHA-256', encoded)
  return Array.from(new Uint8Array(digest))
    .map(b => b.toString(16).padStart(2, '0'))
    .join('')
}

/** 推断图片文件的合法扩展名 */
export function inferImageExtension(blob: Blob): string {
  if (typeof File !== 'undefined' && blob instanceof File && blob.name) {
    const match = blob.name.match(/\.([a-zA-Z0-9]+)$/)
    if (match) {
      const ext = match[1].toLowerCase()
      if (['png', 'gif', 'webp', 'jpg', 'jpeg'].includes(ext)) {
        return ext === 'jpeg' ? 'jpg' : ext
      }
    }
  }

  const mime = (blob.type || '').toLowerCase()
  if (mime.includes('gif')) return 'gif'
  if (mime.includes('webp')) return 'webp'
  if (mime.includes('jpeg') || mime.includes('jpg')) return 'jpg'
  return 'png'
}

/** 从二进制魔数识别安全的常见光栅图片 MIME（PNG/JPEG/GIF/WebP） */
export async function detectRasterImageMime(blob: Blob): Promise<string | null> {
  if (!blob || blob.size < 4) return null
  try {
    const header = new Uint8Array(await blob.slice(0, 12).arrayBuffer())
    if (
      header.length >= 8 &&
      header[0] === 0x89 && header[1] === 0x50 && header[2] === 0x4e && header[3] === 0x47 &&
      header[4] === 0x0d && header[5] === 0x0a && header[6] === 0x1a && header[7] === 0x0a
    ) {
      return 'image/png'
    }
    if (header.length >= 3 && header[0] === 0xff && header[1] === 0xd8 && header[2] === 0xff) {
      return 'image/jpeg'
    }
    if (
      header.length >= 6 &&
      header[0] === 0x47 && header[1] === 0x49 && header[2] === 0x46 && header[3] === 0x38 &&
      (header[4] === 0x37 || header[4] === 0x39) && header[5] === 0x61
    ) {
      return 'image/gif'
    }
    if (
      header.length >= 12 &&
      header[0] === 0x52 && header[1] === 0x49 && header[2] === 0x46 && header[3] === 0x46 &&
      header[8] === 0x57 && header[9] === 0x45 && header[10] === 0x42 && header[11] === 0x50
    ) {
      return 'image/webp'
    }
  } catch {
    return null
  }
  return null
}

/** 动态拉取蜜蜂图床当前可用的游客 storage_id 与限制策略 */
async function resolveBeeimgStrategy(forceRefresh = false): Promise<{
  storageId: string
  maxUploadBytes: number
  allowedTypes: Set<string>
}> {
  const now = Date.now()
  if (!forceRefresh && cachedStorageId && now - groupFetchedAt < GROUP_CACHE_TTL_MS) {
    return {
      storageId: cachedStorageId,
      maxUploadBytes: cachedMaxUploadBytes,
      allowedTypes: cachedAllowedTypes,
    }
  }

  try {
    const res = await fetch(BEEIMG_GROUP_ENDPOINT, {
      method: 'GET',
      headers: { Accept: 'application/json' },
      signal: AbortSignal.timeout(8000),
    })
    if (res.ok) {
      const json = (await res.json()) as BeeimgGroupResponse
      const storageId = json.data?.storages?.[0]?.id
      if (storageId !== undefined && storageId !== null && String(storageId).trim() !== '') {
        cachedStorageId = String(storageId)
        groupFetchedAt = now
      }
      const maxKb = json.data?.group?.options?.max_upload_size
      if (typeof maxKb === 'number' && maxKb > 128) {
        cachedMaxUploadBytes = Math.floor(maxKb * 1024 * 0.94)
      }
      const allowTypes = json.data?.group?.options?.allow_file_types
      if (Array.isArray(allowTypes) && allowTypes.length > 0) {
        cachedAllowedTypes = new Set(allowTypes.map(item => String(item).toLowerCase()))
      }
    }
  } catch {
    // 探测失败时回退既有缓存或默认 ID
  }

  return {
    storageId: cachedStorageId ?? FALLBACK_GUEST_STORAGE_ID,
    maxUploadBytes: cachedMaxUploadBytes,
    allowedTypes: cachedAllowedTypes,
  }
}

/** 将不兼容格式或超大图片在浏览器内转码压缩为 WebP */
async function transcodeToWebp(blob: Blob, maxBytes: number): Promise<Blob | null> {
  if (typeof document === 'undefined' || typeof Image === 'undefined' || typeof URL === 'undefined') {
    return null
  }
  const objectUrl = URL.createObjectURL(blob)
  try {
    const img = await new Promise<HTMLImageElement>((resolve, reject) => {
      const el = new Image()
      el.onload = () => resolve(el)
      el.onerror = () => reject(new Error('image_decode_failed'))
      el.src = objectUrl
    })
    const naturalW = img.naturalWidth || img.width || 0
    const naturalH = img.naturalHeight || img.height || 0
    if (naturalW <= 0 || naturalH <= 0) return null

    const maxEdge = 1024
    const scale = Math.min(1, maxEdge / Math.max(naturalW, naturalH))
    const width = Math.max(1, Math.round(naturalW * scale))
    const height = Math.max(1, Math.round(naturalH * scale))

    const canvas = document.createElement('canvas')
    canvas.width = width
    canvas.height = height
    const ctx = canvas.getContext('2d')
    if (!ctx) return null
    ctx.drawImage(img, 0, 0, width, height)

    for (const quality of [0.86, 0.72, 0.55]) {
      const encoded = await new Promise<Blob | null>(resolve => {
        canvas.toBlob(result => resolve(result), 'image/webp', quality)
      })
      if (encoded && encoded.size > 0 && encoded.size <= maxBytes) {
        return encoded
      }
    }
    return null
  } catch {
    return null
  } finally {
    URL.revokeObjectURL(objectUrl)
  }
}

async function prepareUploadPayload(
  sourceBlob: Blob,
  maxUploadBytes: number,
  allowedTypes: Set<string>
): Promise<{ blob: Blob; ext: string; mime: string; transcodedFromGif: boolean }> {
  const magicMime = await detectRasterImageMime(sourceBlob)
  const inferredExt = magicMime
    ? (magicMime === 'image/jpeg' ? 'jpg' : magicMime.replace('image/', ''))
    : inferImageExtension(sourceBlob)
  const normalizedExt = inferredExt === 'jpeg' ? 'jpg' : inferredExt

  const isTypeAllowed = allowedTypes.has(normalizedExt) || (normalizedExt === 'jpg' && allowedTypes.has('jpeg'))
  const isWithinSize = sourceBlob.size <= maxUploadBytes

  if (isTypeAllowed && isWithinSize) {
    return {
      blob: sourceBlob,
      ext: normalizedExt,
      mime: magicMime ?? (sourceBlob.type || `image/${normalizedExt === 'jpg' ? 'jpeg' : normalizedExt}`),
      transcodedFromGif: false,
    }
  }

  const webpBlob = await transcodeToWebp(sourceBlob, maxUploadBytes)
  if (webpBlob) {
    return {
      blob: webpBlob,
      ext: 'webp',
      mime: 'image/webp',
      transcodedFromGif: normalizedExt === 'gif',
    }
  }

  if (!isWithinSize) {
    throw new Error('IMAGE_TOO_LARGE')
  }

  return {
    blob: sourceBlob,
    ext: normalizedExt,
    mime: magicMime ?? (sourceBlob.type || 'image/png'),
    transcodedFromGif: false,
  }
}

async function postToBeeimg(blob: Blob, ext: string, storageId: string): Promise<BeeimgUploadResponse> {
  const formData = new FormData()
  formData.append('file', blob, `sticker.${ext}`)
  formData.append('storage_id', storageId)

  const uploadRes = await fetch(BEEIMG_UPLOAD_ENDPOINT, {
    method: 'POST',
    headers: { Accept: 'application/json' },
    signal: AbortSignal.timeout(IMAGE_HOST_UPLOAD_TIMEOUT_MS),
    body: formData,
  })

  if (!uploadRes.ok) {
    const errText = await uploadRes.text().catch(() => '')
    throw new Error(`UPLOAD_HTTP_${uploadRes.status}: ${errText || 'rejected'}`)
  }

  return (await uploadRes.json()) as BeeimgUploadResponse
}

/**
 * 上传图片至开放 CORS 临时图床（串行队列，自动适配 storage_id 与格式压缩）。
 * 返回新生成的图床直链及对应 Blob/SHA-256，供调用方同步写入本地 IndexedDB 与云端表情包同步表。
 */
export async function uploadChatImage(sourceBlob: Blob): Promise<UploadChatImageResult> {
  if (!sourceBlob || sourceBlob.size === 0) {
    throw new Error('EMPTY_IMAGE')
  }

  return enqueueUpload(async () => {
    const strategy = await resolveBeeimgStrategy(false)
    const prepared = await prepareUploadPayload(sourceBlob, strategy.maxUploadBytes, strategy.allowedTypes)
    const sha256 = await computeBlobSha256(prepared.blob)

    let uploadJson = await postToBeeimg(prepared.blob, prepared.ext, strategy.storageId)
    // 若图床刚好更换了游客 storage_id，立即刷新一次策略并自动重试
    if (uploadJson.status !== 'success' && uploadJson.message?.includes('储存驱动')) {
      const refreshed = await resolveBeeimgStrategy(true)
      uploadJson = await postToBeeimg(prepared.blob, prepared.ext, refreshed.storageId)
    }

    const directUrl = uploadJson.data?.public_url?.trim() ?? ''
    if (uploadJson.status !== 'success' || !directUrl) {
      throw new Error(uploadJson.message || 'UPLOAD_HOST_ERROR')
    }
    if (!directUrl.startsWith('https://') && !directUrl.startsWith('http://')) {
      throw new Error('INVALID_UPLOAD_URL')
    }

    return {
      url: directUrl,
      sha256,
      mime: prepared.mime,
      blob: prepared.blob,
      cached: false,
      transcodedFromGif: prepared.transcodedFromGif,
    }
  })
}

/**
 * 拉取图片原始二进制 Blob 用于写入本地 IndexedDB：
 * 1. 优先尝试浏览器直连 CORS fetch；
 * 2. 若第三方图床未开放 CORS 头导致拦截，自动回退至服务端无状态防 SSRF 中继接口。
 */
export async function fetchImageBlobForCaching(rawUrl: string): Promise<{ blob: Blob; mime: string } | null> {
  const trimmed = rawUrl.trim()
  if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) {
    return null
  }
  if (typeof fetch === 'undefined') {
    return null
  }

  // 1. 浏览器直连 CORS 拉取
  try {
    const resp = await fetch(trimmed, {
      mode: 'cors',
      cache: 'no-store',
      signal: AbortSignal.timeout(IMAGE_FETCH_TIMEOUT_MS),
    })
    if (resp.ok) {
      const blob = await resp.blob()
      const detectedMime = await detectRasterImageMime(blob)
      if (detectedMime && blob.size > 0 && blob.size <= 10 * 1024 * 1024) {
        return { blob, mime: detectedMime }
      }
    }
  } catch {
    // 跨域无 CORS 或网络受限时降级走中继
  }

  // 2. 服务端无状态防 SSRF 中继拉取（仅支持 HTTPS 外链，不落盘）
  if (trimmed.startsWith('https://')) {
    try {
      const proxyUrl = getApiUrl(`/api/v1/chat/sticker-fetch?url=${encodeURIComponent(trimmed)}`)
      const resp = await fetch(proxyUrl, {
        credentials: 'include',
        cache: 'no-store',
        signal: AbortSignal.timeout(IMAGE_FETCH_TIMEOUT_MS),
      })
      if (resp.ok) {
        const blob = await resp.blob()
        const detectedMime = await detectRasterImageMime(blob)
        if (detectedMime && blob.size > 0) {
          return { blob, mime: detectedMime }
        }
      }
    } catch {
      // 中继不可用或外链已过期
    }
  }

  return null
}

/** 重置图床策略缓存（仅测试辅助使用） */
export function _resetImageUploadStateForTest(): void {
  cachedStorageId = null
  cachedMaxUploadBytes = DEFAULT_MAX_UPLOAD_BYTES
  cachedAllowedTypes = new Set(['jpg', 'jpeg', 'png', 'webp'])
  groupFetchedAt = 0
  uploadQueueTail = Promise.resolve()
}
