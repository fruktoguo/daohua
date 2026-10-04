/**
 * 运维面板 API 客户端（Development-only）。
 *
 * 只读视图走同源 `/ops/api/*`；写操作额外要求一次性令牌，令牌保存在 sessionStorage，
 * 由面板顶部输入框写入，绝不写入 localStorage（关闭标签页即失效）。
 */

/** 令牌在 sessionStorage 中的键名。 */
const TOKEN_STORAGE_KEY = 'daohua.ops.token'

/** 面板 API 前缀（与 Ops:Path 对齐）。 */
const API_BASE = '/ops/api'

/** 面板 API 调用失败。 */
export class OpsApiError extends Error {
  /** HTTP 状态码。 */
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'OpsApiError'
    this.status = status
  }
}

/** 读取当前会话中的运维令牌。 */
export function getOpsToken(): string {
  try {
    return window.sessionStorage.getItem(TOKEN_STORAGE_KEY) ?? ''
  } catch {
    return ''
  }
}

/** 写入运维令牌（空串表示清除）。 */
export function setOpsToken(token: string): void {
  try {
    if (token) window.sessionStorage.setItem(TOKEN_STORAGE_KEY, token)
    else window.sessionStorage.removeItem(TOKEN_STORAGE_KEY)
  } catch {
    // 隐私模式下 sessionStorage 不可写：面板仍可只读使用，写操作会因缺少令牌被拒。
  }
}

/** 读取只读视图数据。 */
export async function opsGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(API_BASE + path, { headers: { Accept: 'application/json' }, signal })
  if (!response.ok) throw new OpsApiError(await readMessage(response), response.status)
  return (await response.json()) as T
}

/** 执行运维写操作（携带一次性令牌）。 */
export async function opsPost<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(API_BASE + path, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      'X-Ops-Token': getOpsToken(),
    },
    body: JSON.stringify(body),
  })
  if (!response.ok) throw new OpsApiError(await readMessage(response), response.status)
  return (await response.json()) as T
}

/** 提取错误响应的可读文案（message 优先，其次 code，最后回退状态码）。 */
async function readMessage(response: Response): Promise<string> {
  try {
    const payload = (await response.json()) as { message?: string; code?: string }
    return payload.message ?? payload.code ?? `HTTP ${response.status}`
  } catch {
    return `HTTP ${response.status}`
  }
}
