/**
 * 进程内会话身份快照。它只用于连接参数和 UI 状态，不承担认证；真正的
 * 凭据由服务端签发的 HttpOnly Cookie 管理。
 */
export interface SessionAccount {
  /** 账号 ID */
  accountId: string
  /** 绑定的玩家 ID（握手与命令路由使用） */
  playerId: string
  /** 登录账号名 */
  account: string
  /** 显示道号 */
  name: string
  /** 角色序号（服务端自增编号，缺省为 0） */
  sequenceId?: number
}

let generation = 0
let current: SessionAccount | null = null

/** 读取当前会话身份（gameClient 握手时取 playerId 等字段） */
export function getSessionAccount(): SessionAccount | null {
  return current
}

/** 登录世代独立于角色 ID：同角色重登也会失效旧请求。 */
export function getSessionGeneration(): number {
  return generation
}

/** 建立或清除登录会话；显示名改动不更换会话。 */
export function setSessionAccount(value: SessionAccount | null): void {
  generation += 1
  current = value
}

export function updateSessionDisplayName(name: string): void {
  if (current) current = { ...current, name }
}
