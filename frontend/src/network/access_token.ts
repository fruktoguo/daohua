/**
 * 与服务端 Server:AccessToken 配对的共享访问令牌管理器。
 * 仅保存于本地 localStorage，用于向自托管服务端证明持有部署者发放的访问令牌，不代表具体账号身份。
 */
const STORAGE_KEY = 'daohua_access_token'

/**
 * 从 localStorage 读取当前配置的服务端访问令牌
 * @returns 令牌字符串（未配置时返回空字符串）
 */
export function getAccessToken(): string {
  return window.localStorage.getItem(STORAGE_KEY) ?? ''
}

/**
 * 设置或移除本地存储的服务端访问令牌
 * @param token 访问令牌字符串
 */
export function setAccessToken(token: string): void {
  const normalized = token.trim()
  if (normalized) window.localStorage.setItem(STORAGE_KEY, normalized)
  else window.localStorage.removeItem(STORAGE_KEY)
}
