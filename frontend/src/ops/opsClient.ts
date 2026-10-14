import type { OpsAccountAbuseOverview, OpsAccountAbusePolicy } from './opsTypes'

/**
 * 运维监控面板 API 客户端。
 *
 * 自动从当前 URL 提取面板随机路径前缀（`/ops-<hex>` 或本地 `/ops`）；
 * 所有只读视图与写操作请求统一携带 `X-Ops-Token` 密码请求头，密码保存在 sessionStorage，
 * 关闭标签页即失效，绝不写入 localStorage。
 */

/** 密码在 sessionStorage 中的键名。 */
const TOKEN_STORAGE_KEY = 'daohua.ops.token'

/** 认证失效事件名（任一接口返回 401 时触发，通知外壳退回密码登录页）。 */
export const OPS_UNAUTHORIZED_EVENT = 'daohua:ops-unauthorized'

/** 解析当前激活的运维面板路径前缀（如 `/ops-a1b2c3d4e5f60718` 或 `/ops`）。 */
export function getOpsBasePath(): string {
  const match = window.location.pathname.match(/^(\/ops(?:-[a-zA-Z0-9_-]+)?)(?:\/|$)/)
  return match ? match[1] : '/ops'
}

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

/** 读取当前会话中的运维密码。 */
export function getOpsToken(): string {
  try {
    return window.sessionStorage.getItem(TOKEN_STORAGE_KEY) ?? ''
  } catch {
    return ''
  }
}

/** 写入运维密码（空串表示清除）。 */
export function setOpsToken(token: string): void {
  try {
    if (token) window.sessionStorage.setItem(TOKEN_STORAGE_KEY, token)
    else window.sessionStorage.removeItem(TOKEN_STORAGE_KEY)
  } catch {
    // 隐私模式下 sessionStorage 不可写
  }
}

/** 读取只读视图数据（携带访问密码）。 */
export async function opsGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${getOpsBasePath()}/api${path}`, {
    headers: {
      Accept: 'application/json',
      'X-Ops-Token': getOpsToken(),
    },
    signal,
  })
  if (!response.ok) {
    if (response.status === 401) window.dispatchEvent(new Event(OPS_UNAUTHORIZED_EVENT))
    throw new OpsApiError(await readMessage(response), response.status)
  }
  return (await response.json()) as T
}

/** 执行运维写操作（携带访问密码）。 */
export async function opsPost<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${getOpsBasePath()}/api${path}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      'X-Ops-Token': getOpsToken(),
    },
    body: JSON.stringify(body),
  })
  if (!response.ok) {
    if (response.status === 401) window.dispatchEvent(new Event(OPS_UNAUTHORIZED_EVENT))
    throw new OpsApiError(await readMessage(response), response.status)
  }
  return (await response.json()) as T
}

/** 读取账号检测总览（策略、统计、近期事件与脱敏关联）。 */
export function opsGetAccountAbuse(signal?: AbortSignal): Promise<OpsAccountAbuseOverview> {
  return opsGet<OpsAccountAbuseOverview>('/account-abuse', signal)
}

/** 更新账号检测策略；服务端负责令牌校验、范围校验和审计。 */
export function opsUpdateAccountAbusePolicy(policy: Omit<OpsAccountAbusePolicy, 'updatedAt'>): Promise<unknown> {
  return opsPost('/account-abuse/policy', policy)
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
