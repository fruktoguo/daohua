import { getBrowserFingerprint } from '../private'

/** 认证入口上传的风险信号；身份和来源 IP 由服务端独立推导。 */
export interface AccountSignalsDto {
  deviceConfigHash: string | null
  deviceId: string | null
  networkHash: string | null
  browserFingerprint: string | null
}

interface CoarseNetworkConnection {
  type?: string
  effectiveType?: string
}

interface SignalNavigator extends Navigator {
  connection?: CoarseNetworkConnection
  mozConnection?: CoarseNetworkConnection
  webkitConnection?: CoarseNetworkConnection
  userAgentData?: { platform?: string }
}

const DEVICE_STORAGE_KEY = 'daohua.device.id'
const DEVICE_COOKIE_NAME = 'daohua_device_id'
const DEVICE_COOKIE_MAX_AGE = 365 * 24 * 60 * 60
const FINGERPRINT_TIMEOUT_MS = 2_000
const DEVICE_ID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

let deviceId: string | null | undefined
let deviceConfigHash: Promise<string | null> | null = null

/** 每次认证采集当前网络特征，稳定的设备特征在标签页内复用。缺失信号由服务端按策略处理。 */
export async function collectAccountSignals(): Promise<AccountSignalsDto> {
  deviceConfigHash ??= collectDeviceConfigHash()
  const [configHash, networkHash, browserFingerprint] = await Promise.all([
    deviceConfigHash,
    collectNetworkHash(),
    collectBrowserFingerprint(),
  ])
  return { deviceConfigHash: configHash, deviceId: getStableDeviceId(), networkHash, browserFingerprint }
}

/** 本地存储和普通 Cookie 双份保留安装标识；它属于可伪造的关联信号，不是认证凭据。 */
function getStableDeviceId(): string | null {
  if (deviceId !== undefined) return deviceId
  let stored: string | null = null
  try {
    stored = window.localStorage.getItem(DEVICE_STORAGE_KEY)
  } catch {
    // 浏览器禁用存储时仍尝试 Cookie 与本标签页标识。
  }
  let cookieId: string | null = null
  try {
    const cookieValue = document.cookie.split(';').map(part => part.trim()).find(part => part.startsWith(`${DEVICE_COOKIE_NAME}=`))
    cookieId = cookieValue?.slice(DEVICE_COOKIE_NAME.length + 1) ?? null
  } catch {
    // Cookie 被浏览器策略禁止时，仍使用本地存储或新生成的标识。
  }
  deviceId = stored && DEVICE_ID_PATTERN.test(stored)
    ? stored
    : cookieId && DEVICE_ID_PATTERN.test(cookieId) ? cookieId : createDeviceId()
  if (!deviceId) return null
  try {
    window.localStorage.setItem(DEVICE_STORAGE_KEY, deviceId)
  } catch {
    // 私密模式或配额不足时，Cookie 副本和进程内值仍可用。
  }
  try {
    const secure = window.location.protocol === 'https:' ? '; Secure' : ''
    document.cookie = `${DEVICE_COOKIE_NAME}=${deviceId}; Path=/; Max-Age=${DEVICE_COOKIE_MAX_AGE}; SameSite=Lax${secure}`
  } catch {
    // Cookie 不可写时保留本地存储副本；全部存储受限时只保证本标签页一致。
  }
  return deviceId
}

function createDeviceId(): string | null {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  if (typeof crypto.getRandomValues !== 'function') return null
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

/** 只对相对稳定的硬件和平台参数取摘要；屏幕旋转不改变规格，浏览器版本不参与。 */
async function collectDeviceConfigHash(): Promise<string | null> {
  const { width, height, colorDepth } = window.screen
  return hashSignal(JSON.stringify({
    version: 1,
    screen: [Math.min(width, height), Math.max(width, height), colorDepth],
    cpu: navigator.hardwareConcurrency ?? null,
    gpu: collectGpu(),
    platform: collectPlatform(),
  }))
}

/** 浏览器分别使用 macOS、MacIntel 等名称，只保留归一化的系统族。 */
function collectPlatform(): string {
  const signalNavigator = navigator as SignalNavigator
  const platform = (signalNavigator.userAgentData?.platform ?? navigator.platform).toLowerCase()
  const userAgent = navigator.userAgent.toLowerCase()
  if (platform.includes('android') || userAgent.includes('android')) return 'android'
  if (platform.includes('iphone') || platform.includes('ipad') || platform.includes('ipod')
    || (platform.includes('mac') && navigator.maxTouchPoints > 1)) return 'ios'
  if (platform.includes('mac')) return 'macos'
  if (platform.includes('win')) return 'windows'
  if (platform.includes('linux')) return 'linux'
  return platform
}

function collectGpu(): { vendor: string; renderer: string } | null {
  try {
    const canvas = document.createElement('canvas')
    const gl = canvas.getContext('webgl')
    if (!gl) return null
    const debugInfo = gl.getExtension('WEBGL_debug_renderer_info')
    const result = {
      vendor: String(gl.getParameter(debugInfo ? debugInfo.UNMASKED_VENDOR_WEBGL : gl.VENDOR)),
      renderer: String(gl.getParameter(debugInfo ? debugInfo.UNMASKED_RENDERER_WEBGL : gl.RENDERER)),
    }
    gl.getExtension('WEBGL_lose_context')?.loseContext()
    return result
  } catch {
    // WebGL 被禁用或隐私策略拒绝时，仅使用其他设备维度。
    return null
  }
}

/** 网络类型会随网络切换变化，只是软关联特征，不包含地址、精确带宽或延迟。 */
async function collectNetworkHash(): Promise<string | null> {
  const signalNavigator = navigator as SignalNavigator
  const connection = signalNavigator.connection ?? signalNavigator.mozConnection ?? signalNavigator.webkitConnection
  if (!connection || (!connection.type && !connection.effectiveType)) return null
  return hashSignal(JSON.stringify({ version: 1, type: connection.type ?? null, effectiveType: connection.effectiveType ?? null }))
}

/** 某些浏览器会延迟离线音频渲染，认证不能无限等待辅助指纹。 */
async function collectBrowserFingerprint(): Promise<string | null> {
  let timeoutId: number | undefined
  try {
    return await Promise.race([
      getBrowserFingerprint(),
      new Promise<null>(resolve => {
        timeoutId = window.setTimeout(() => resolve(null), FINGERPRINT_TIMEOUT_MS)
      }),
    ])
  } finally {
    window.clearTimeout(timeoutId)
  }
}

/** 不支持安全摘要时返回缺失信号，避免把原始配置或弱散列作为设备摘要上报。 */
async function hashSignal(value: string): Promise<string | null> {
  if (!crypto.subtle) return null
  try {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value))
    return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
  } catch {
    return null
  }
}
