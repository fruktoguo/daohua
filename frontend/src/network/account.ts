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
  lastLoginAt: number
}

export interface AuthResponseDto {
  success: boolean
  accountId?: string
  playerId?: string
  account?: string
  displayName?: string
  /** 兼容 UI 组件使用的语义别名，线上响应只由 displayName 填充。 */
  characterName?: string
  message?: string
}

interface ServerAuthDto {
  accountId: string
  playerId: string
  account: string
  displayName: string
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
    characterName: dto.displayName,
  }
}

function authFailure(error: unknown): AuthResponseDto {
  return { success: false, message: error instanceof TypeError ? undefined : error instanceof Error ? error.message : undefined }
}

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

export async function requestSession(): Promise<AuthResponseDto> {
  try {
    const dto = await requestJson<ServerAuthDto>('/api/v1/auth/session')
    return authSuccess(dto)
  } catch {
    return { success: false }
  }
}

interface AccountStore {
  currentAccount: StoredAccountInfo | null
  savedAccounts: StoredAccountInfo[]
  sessionChecked: boolean
  loginWithAuth: (account: string, password: string) => Promise<AuthResponseDto>
  registerWithAuth: (account: string, password: string, displayName?: string) => Promise<AuthResponseDto>
  restoreSession: () => Promise<boolean>
  logout: () => void
  removeSavedAccount: (account: string) => void
  updateCharacterName: (newName: string) => void
}

function accountFromResponse(response: AuthResponseDto): StoredAccountInfo {
  if (!response.accountId || !response.playerId || !response.account) throw new Error('认证响应缺少身份字段。')
  return {
    accountId: response.accountId,
    playerId: response.playerId,
    account: response.account,
    name: response.displayName || response.characterName || response.account,
    lastLoginAt: Date.now(),
  }
}

function rememberAccount(account: StoredAccountInfo, existing: readonly StoredAccountInfo[]): StoredAccountInfo[] {
  const result = [account, ...existing.filter(item => item.account !== account.account)]
  persistAccount(account)
  persistSavedAccounts(result)
  return result
}

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

  updateCharacterName: name => {
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

setSessionAccount(useAccountStore.getState().currentAccount)

export function getStoredAccount(): StoredAccountInfo | null {
  return useAccountStore.getState().currentAccount
}

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
