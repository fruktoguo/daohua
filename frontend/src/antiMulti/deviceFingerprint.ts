/**
 * 轻量设备指纹采集：只取跨浏览器稳定的硬件/系统级特征（WebGL 显卡、CPU 线程数、
 * 屏幕物理规格、时区、音频渲染浮点特性），使同一物理设备在无痕窗口或不同浏览器下
 * 仍得到同一指纹。指纹只用于服务端并发账号配额，是软对抗手段而非身份证明。
 * 结果在进程内记忆化，整个会话只计算一次。
 */

let cached: Promise<string | null> | null = null

/** 获取设备指纹哈希；采集全部失败或环境不支持时返回 null */
export function getDeviceFingerprint(): Promise<string | null> {
  cached ??= collectDeviceFingerprint().catch(() => null)
  return cached
}

async function collectDeviceFingerprint(): Promise<string | null> {
  const parts: string[] = []

  try {
    const canvas = document.createElement('canvas')
    const gl = (canvas.getContext('webgl') ?? canvas.getContext('experimental-webgl')) as WebGLRenderingContext | null
    if (gl) {
      const debugInfo = gl.getExtension('WEBGL_debug_renderer_info')
      const renderer = debugInfo ? debugInfo.UNMASKED_RENDERER_WEBGL : gl.RENDERER
      const vendor = debugInfo ? debugInfo.UNMASKED_VENDOR_WEBGL : gl.VENDOR
      parts.push(`gpu:${String(gl.getParameter(renderer))}|${String(gl.getParameter(vendor))}`)
    }
  } catch {
    // WebGL 被禁用或探测失败时跳过该维度。
  }

  try {
    parts.push(`cpu:${navigator.hardwareConcurrency ?? 0}`)
    parts.push(`screen:${window.screen.width}x${window.screen.height}x${window.screen.colorDepth}`)
    parts.push(`tz:${Intl.DateTimeFormat().resolvedOptions().timeZone ?? ''}`)
    parts.push(`platform:${navigator.platform ?? ''}`)
  } catch {
    // 基础环境参数读取失败时依赖其余维度。
  }

  try {
    const audio = await collectAudioFingerprint()
    if (audio) parts.push(`audio:${audio}`)
  } catch {
    // 音频指纹不可用时跳过。
  }

  if (parts.length === 0) return null
  return hashFingerprint(parts.join('|'))
}

/** 音频渲染指纹：DynamicsCompressor 对固定波形的浮点处理结果随声卡/驱动存在微差 */
async function collectAudioFingerprint(): Promise<string> {
  const contextCtor = window.OfflineAudioContext
    ?? (window as unknown as { webkitOfflineAudioContext?: typeof OfflineAudioContext }).webkitOfflineAudioContext
  if (!contextCtor) return ''
  const context = new contextCtor(1, 5000, 44100)
  const oscillator = context.createOscillator()
  oscillator.type = 'triangle'
  oscillator.frequency.value = 10_000
  const compressor = context.createDynamicsCompressor()
  oscillator.connect(compressor)
  compressor.connect(context.destination)
  oscillator.start(0)
  const rendered = await context.startRendering()
  const channel = rendered.getChannelData(0)
  let sum = 0
  for (let index = 0; index < channel.length; index += 50) sum += Math.abs(channel[index])
  return sum.toFixed(6)
}

async function hashFingerprint(raw: string): Promise<string> {
  try {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(raw))
    return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
  } catch {
    // 非安全上下文降级为 FNV-1a，仍满足配额判等的稳定性要求。
    let hash = 0x811c9dc5 >>> 0
    for (let index = 0; index < raw.length; index += 1) {
      hash ^= raw.charCodeAt(index)
      hash = Math.imul(hash, 0x01000193) >>> 0
    }
    return `fnv-${hash.toString(16).padStart(8, '0')}-${raw.length}`
  }
}
