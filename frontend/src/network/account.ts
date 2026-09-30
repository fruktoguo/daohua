import { create } from 'zustand'
import { useGameStore } from '../game/gameStore'
import { requestJson } from './apiClient'
import { gameClient } from './gameClient'
import { setSessionAccount } from './sessionState'

/** 浏览器仅保存非敏感的账号显示信息；认证凭据永远由 HttpOnly Cookie 持有。 */
export interface StoredAccountInfo {
  accountId: string
  playerId: string
  account: string
  name: string
  /** 角色序号（服务端下发的玩家自增编号，旧缓存缺省为 0） */
  sequenceId?: number
  lastLoginAt: number
}

/** 认证接口归一化响应（register/login/session 统一形态；message 为可展示的服务端错误文案） */
export interface AuthResponseDto {
  /** 认证是否成功 */
  success: boolean
  /** 账号 ID */
  accountId?: string
  /** 绑定的玩家 ID */
  playerId?: string
  /** 登录账号名 */
  account?: string
  /** 显示道号 */
  displayName?: string
  /** 角色序号（服务端自增编号） */
  sequenceId?: number
  /** 服务端返回的错误/提示文案 */
  message?: string
}

interface ServerAuthDto {
  accountId: string
  playerId: string
  account: string
  displayName: string
  sequenceId?: number
}

const CURRENT_ACCOUNT_KEY = 'daohua.account.v1'
const SAVED_ACCOUNTS_KEY = 'daohua.accounts.v1'

function isAccount(value: unknown): value is StoredAccountInfo {
  if (value === null || typeof value !== 'object') return false
  const row = value as Record<string, unknown>
  return typeof row.accountId === 'string'
    && typeof row.playerId === 'string'
    && typeof row.account === 'string'
    && typeof row.name === 'string'
    && row.accountId.length > 0
    && row.playerId.length > 0
}

function loadJson<T>(key: string, fallback: T): T {
  if (typeof window === 'undefined') return fallback
  try {
    const value: unknown = JSON.parse(window.localStorage.getItem(key) ?? 'null')
    return value as T
  } catch {
    return fallback
  }
}

function loadCurrentAccount(): StoredAccountInfo | null {
  const value = loadJson<unknown>(CURRENT_ACCOUNT_KEY, null)
  return isAccount(value) ? value : null
}

function loadSavedAccounts(): StoredAccountInfo[] {
  const value = loadJson<unknown>(SAVED_ACCOUNTS_KEY, [])
  return Array.isArray(value) ? value.filter(isAccount) : []
}

function persistAccount(account: StoredAccountInfo | null): void {
  if (typeof window === 'undefined') return
  try {
    if (account) window.localStorage.setItem(CURRENT_ACCOUNT_KEY, JSON.stringify(account))
    else window.localStorage.removeItem(CURRENT_ACCOUNT_KEY)
  } catch {
    // 私密模式或存储配额不足时，认证仍由 HttpOnly Cookie 维持。
  }
}

function persistSavedAccounts(accounts: readonly StoredAccountInfo[]): void {
  if (typeof window === 'undefined') return
  try { window.localStorage.setItem(SAVED_ACCOUNTS_KEY, JSON.stringify(accounts)) } catch { /* 非关键显示缓存 */ }
}

function authSuccess(dto: ServerAuthDto): AuthResponseDto {
  return {
    success: true,
    accountId: dto.accountId,
    playerId: dto.playerId,
    account: dto.account,
    displayName: dto.displayName,
    sequenceId: dto.sequenceId,
  }
}

/** 认证失败归一化；TypeError 为 fetch 层网络故障（无可展示文案），其余异常透传 message */
function authFailure(error: unknown): AuthResponseDto {
  return { success: false, message: error instanceof TypeError ? undefined : error instanceof Error ? error.message : undefined }
}

/** 注册账号并建立会话（HttpOnly Cookie 由服务端下发）；UI 层按返回 message 展示错误 */
export async function requestRegister(account: string, password: string, displayName?: string): Promise<AuthResponseDto> {
  try {
    const dto = await requestJson<ServerAuthDto>('/api/v1/auth/register', {
      method: 'POST',
      body: JSON.stringify({ account: account.trim(), password, displayName: displayName?.trim() || null }),
    })
    return authSuccess(dto)
  } catch (error) {
    return authFailure(error)
  }
}

/** 账号密码登录；成功即由服务端写入会话 Cookie */
export async function requestLogin(account: string, password: string): Promise<AuthResponseDto> {
  try {
    const dto = await requestJson<ServerAuthDto>('/api/v1/auth/login', {
      method: 'POST',
      body: JSON.stringify({ account: account.trim(), password }),
    })
    return authSuccess(dto)
  } catch (error) {
    return authFailure(error)
  }
}

/** 修改当前账号登录密码；账号身份由会话 Cookie 承载，成功返回 204。服务端会吊销其他设备会话。 */
export async function requestChangePassword(currentPassword: string, newPassword: string): Promise<AuthResponseDto> {
  try {
    await requestJson('/api/v1/auth/change-password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    })
    return { success: true }
  } catch (error) {
    return authFailure(error)
  }
}

/** 用现有会话 Cookie 探测登录态（启动时静默调用，不带任何凭据参数） */
export async function requestSession(): Promise<AuthResponseDto> {
  try {
    const dto = await requestJson<ServerAuthDto>('/api/v1/auth/session')
    return authSuccess(dto)
  } catch {
    return { success: false }
  }
}

/** 账号会话状态 Store 契约 */
interface AccountStore {
  /** 当前登录账号的本地显示信息（null 表示未登录） */
  currentAccount: StoredAccountInfo | null
  /** 本机曾登录过的账号列表（供登录页快捷切换，按最近登录排序） */
  savedAccounts: StoredAccountInfo[]
  /** 启动时的会话探测是否已完成（App 据此决定是否展示登录页而非加载页） */
  sessionChecked: boolean
  /** 登录并同步会话状态；成功后立即触发 gameClient 连接 */
  loginWithAuth: (account: string, password: string) => Promise<AuthResponseDto>
  /** 注册并登录；成功后立即触发 gameClient 连接 */
  registerWithAuth: (account: string, password: string, displayName?: string) => Promise<AuthResponseDto>
  /** 启动时用 Cookie 恢复会话；返回是否恢复成功 */
  restoreSession: () => Promise<boolean>
  /** 登出：通知服务端注销、清空本地账号态、断开游戏连接并重置游戏 Store */
  logout: () => void
  /** 从快捷账号列表中移除指定账号 */
  removeSavedAccount: (account: string) => void
  /** 更新当前账号道号显示名（本地缓存与会话态同步刷新） */
  updateDisplayName: (newName: string) => void
}

function accountFromResponse(response: AuthResponseDto): StoredAccountInfo {
  if (!response.accountId || !response.playerId || !response.account) throw new Error('认证响应缺少身份字段。')
  return {
    accountId: response.accountId,
    playerId: response.playerId,
    account: response.account,
    name: response.displayName || response.account,
    sequenceId: response.sequenceId ?? 0,
    lastLoginAt: Date.now(),
  }
}

function rememberAccount(account: StoredAccountInfo, existing: readonly StoredAccountInfo[]): StoredAccountInfo[] {
  const result = [account, ...existing.filter(item => item.account !== account.account)]
  persistAccount(account)
  persistSavedAccounts(result)
  return result
}

/** 账号会话全局 Store（登录态、快捷账号列表、会话探测） */
export const useAccountStore = create<AccountStore>((set, get) => ({
  currentAccount: loadCurrentAccount(),
  savedAccounts: loadSavedAccounts(),
  sessionChecked: false,

  loginWithAuth: async (account, password) => {
    const response = await requestLogin(account, password)
    if (!response.success) return response
    const info = accountFromResponse(response)
    setSessionAccount(info)
    set({ currentAccount: info, savedAccounts: rememberAccount(info, get().savedAccounts), sessionChecked: true })
    gameClient.connect()
    return response
  },

  registerWithAuth: async (account, password, displayName) => {
    const response = await requestRegister(account, password, displayName)
    if (!response.success) return response
    const info = accountFromResponse(response)
    setSessionAccount(info)
    set({ currentAccount: info, savedAccounts: rememberAccount(info, get().savedAccounts), sessionChecked: true })
    gameClient.connect()
    return response
  },

  restoreSession: async () => {
    const response = await requestSession()
    if (!response.success) {
      setSessionAccount(null)
      set({ currentAccount: null, sessionChecked: true })
      persistAccount(null)
      return false
    }
    const info = accountFromResponse(response)
    setSessionAccount(info)
    set({ currentAccount: info, savedAccounts: rememberAccount(info, get().savedAccounts), sessionChecked: true })
    gameClient.connect()
    return true
  },

  logout: () => {
    void requestJson('/api/v1/auth/logout', { method: 'POST' }).catch(() => undefined)
    persistAccount(null)
    setSessionAccount(null)
    set({ currentAccount: null, sessionChecked: true })
    gameClient.dispose()
    useGameStore.getState().resetGameStore()
  },

  removeSavedAccount: account => {
    const updated = get().savedAccounts.filter(item => item.account !== account.trim())
    persistSavedAccounts(updated)
    set({ savedAccounts: updated })
  },

  updateDisplayName: name => {
    const current = get().currentAccount
    if (!current) return
    const updatedCurrent = { ...current, name: name.trim() }
    const updatedSaved = get().savedAccounts.map(item => item.account === current.account ? updatedCurrent : item)
    persistAccount(updatedCurrent)
    persistSavedAccounts(updatedSaved)
    setSessionAccount(updatedCurrent)
    set({ currentAccount: updatedCurrent, savedAccounts: updatedSaved })
  },
}))

// 模块加载即把持久化账号灌入 sessionState，保证首次握手前网络层已持有玩家身份
setSessionAccount(useAccountStore.getState().currentAccount)

/** 读取当前登录账号（供网络层握手等非 React 路径使用） */
export function getStoredAccount(): StoredAccountInfo | null {
  return useAccountStore.getState().currentAccount
}

/** 清空当前账号并登出（语义等同 logout） */
export function clearStoredAccount(): void {
  useAccountStore.getState().logout()
}

/** 清理本应用浏览器缓存；不触碰服务端资产和会话以外的站点数据。 */
export function resetAllLocalData(): void {
  if (typeof window !== 'undefined') {
    try {
      window.localStorage.removeItem(CURRENT_ACCOUNT_KEY)
      window.localStorage.removeItem(SAVED_ACCOUNTS_KEY)
    } catch { /* 非关键显示缓存 */ }
  }
  gameClient.dispose()
  useAccountStore.setState({ currentAccount: null, savedAccounts: [], sessionChecked: true })
  setSessionAccount(null)
  useGameStore.getState().resetGameStore()
}
