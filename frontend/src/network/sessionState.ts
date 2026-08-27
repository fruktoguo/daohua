/**
 * 进程内会话身份快照。它只用于连接参数和 UI 状态，不承担认证；真正的
 * 凭据由服务端签发的 HttpOnly Cookie 管理。
 */
export interface SessionAccount {
  accountId: string
  playerId: string
  account: string
  name: string
}

let current: SessionAccount | null = null

export function getSessionAccount(): SessionAccount | null {
  return current
}

export function setSessionAccount(value: SessionAccount | null): void {
  current = value
}
