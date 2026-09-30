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

let current: SessionAccount | null = null

/** 读取当前会话身份（gameClient 握手时取 playerId 等字段） */
export function getSessionAccount(): SessionAccount | null {
  return current
}

/** 写入会话身份；登录/登出/道号变更时由 account Store 同步调用 */
export function setSessionAccount(value: SessionAccount | null): void {
  current = value
}
