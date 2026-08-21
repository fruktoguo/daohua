// 与服务端 Server:AccessToken 配对的共享访问令牌。
// 仅保存于 localStorage，用于向自托管服务端证明持有部署者发放的令牌，不代表账号身份。
const STORAGE_KEY = 'xiuxian_access_token'

export function getAccessToken(): string {
  return window.localStorage.getItem(STORAGE_KEY) ?? ''
}

export function setAccessToken(token: string): void {
  const normalized = token.trim()
  if (normalized) window.localStorage.setItem(STORAGE_KEY, normalized)
  else window.localStorage.removeItem(STORAGE_KEY)
}
