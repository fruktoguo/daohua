import { create } from 'zustand'
import { gameClient } from './gameClient'
import { useGameStore } from '../game/gameStore'

/**
 * 存储在本地的已登录账号凭据信息
 */
export interface StoredAccountInfo {
  /** 账号名 */
  account: string
  /** 角色名称 */
  name: string
  /** 绑定的角色唯一 UUID (playerId) */
  playerId: string
  /** 角色编号 */
  sequenceId?: number
  /** 最近登录时间戳（毫秒） */
  lastLoginAt?: number
}

/**
 * 鉴权接口 (注册/登录) 返回的数据传输对象 (DTO)
 */
export interface AuthResponseDto {
  success: boolean
  account?: string | null
  characterName?: string | null
  playerId?: string | null
  sequenceId?: number
  message?: string | null
}

const STORAGE_ACCOUNT_KEY = 'daohua_account'
const STORAGE_CHAR_NAME_KEY = 'daohua_character_name'
const STORAGE_PLAYER_ID_KEY = 'daohua-player-id'
const STORAGE_SAVED_ACCOUNTS_KEY = 'daohua_saved_accounts_list'
const COOKIE_PLAYER_ID_KEY = 'daohua_player_id'

/**
 * 从 Cookie 中读取当前 playerId（作为 localStorage 的备选容灾）
 */
function getCookiePlayerId(): string | null {
  if (typeof document === 'undefined') return null
  const cookieValue = document.cookie
    .split(';')
    .map(part => part.trim())
    .find(part => part.startsWith(`${COOKIE_PLAYER_ID_KEY}=`))
    ?.slice(COOKIE_PLAYER_ID_KEY.length + 1)
  return cookieValue ? decodeURIComponent(cookieValue) : null
}

/**
 * 将 playerId 写入持久化 Cookie (10年有效期)
 */
function setCookiePlayerId(playerId: string): void {
  if (typeof document === 'undefined') return
  document.cookie = `${COOKIE_PLAYER_ID_KEY}=${encodeURIComponent(playerId)}; Path=/; Max-Age=315360000; SameSite=Lax`
}
function isValidAccountString(str?: string | null): boolean {
  if (!str) return false
  const trimmed = str.trim()
  return Boolean(trimmed && trimmed !== 'undefined' && trimmed !== 'null' && trimmed !== '[object Object]')
}

/**
 * 从 localStorage 读取历史上登录过的全部账号列表
 */
function loadSavedAccounts(): StoredAccountInfo[] {
  if (typeof window === 'undefined') return []
  try {
    const raw = window.localStorage.getItem(STORAGE_SAVED_ACCOUNTS_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter((a): a is StoredAccountInfo =>
      Boolean(
        a &&
        typeof a === 'object' &&
        isValidAccountString(a.account) &&
        isValidAccountString(a.playerId) &&
        typeof a.name === 'string',
      ),
    )
  } catch {
    return []
  }
}

/**
 * 将已保存的账号列表序列化持久化至 localStorage
 */
function persistSavedAccounts(list: StoredAccountInfo[]): void {
  if (typeof window === 'undefined') return
  window.localStorage.setItem(STORAGE_SAVED_ACCOUNTS_KEY, JSON.stringify(list))
}

/**
 * 初始化时从 localStorage/Cookie 恢复当前账号会话
 */
function getInitialAccount(): StoredAccountInfo | null {
  if (typeof window === 'undefined') return null
  const account = window.localStorage.getItem(STORAGE_ACCOUNT_KEY)?.trim()
  const name = window.localStorage.getItem(STORAGE_CHAR_NAME_KEY)?.trim()
  const playerId = window.localStorage.getItem(STORAGE_PLAYER_ID_KEY)?.trim() || getCookiePlayerId()

  if (isValidAccountString(account) && isValidAccountString(playerId)) {
    return {
      account: account!,
      name: (isValidAccountString(name) ? name : account)!,
      playerId: playerId!,
      lastLoginAt: Date.now(),
    }
  }

  return null
}

/**
 * 向服务端发起账号注册 HTTP POST 请求
 * @param account 注册账号
 * @param password 登录密码
 * @param characterName 初始道号（可选）
 */
export async function requestRegister(
  account: string,
  password: string,
  characterName?: string,
): Promise<AuthResponseDto> {
  const url = `${window.location.origin}/api/auth/register`
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      account: account.trim(),
      password,
      characterName: characterName?.trim() || undefined,
    }),
  })
  if (!res.ok) {
    return { success: false, message: `服务器响应异常 (${res.status})` }
  }
  return (await res.json()) as AuthResponseDto
}

/**
 * 向服务端发起账号登录 HTTP POST 请求
 * @param account 登录账号
 * @param password 登录密码
 */
export async function requestLogin(
  account: string,
  password: string,
): Promise<AuthResponseDto> {
  const url = `${window.location.origin}/api/auth/login`
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      account: account.trim(),
      password,
    }),
  })
  if (!res.ok) {
    return { success: false, message: `服务器响应异常 (${res.status})` }
  }
  return (await res.json()) as AuthResponseDto
}

/**
 * 账号管理状态机接口
 */
interface AccountStore {
  /** 当前登录的账号信息（为 null 时处于未登录状态） */
  currentAccount: StoredAccountInfo | null
  /** 本地记录的历史账号列表 */
  savedAccounts: StoredAccountInfo[]
  /** 账号密码登录 */
  loginWithAuth: (account: string, password: string) => Promise<AuthResponseDto>
  /** 账号密码注册并自动登录 */
  registerWithAuth: (account: string, password: string, characterName?: string) => Promise<AuthResponseDto>
  /** 退出当前账号（清理会话并断开 WebSocket） */
  logout: () => void
  /** 从历史账号列表中移除指定账号 */
  removeSavedAccount: (account: string) => void
  /** 更新当前账号对应的角色名 */
  updateCharacterName: (newName: string) => void
}

/**
 * 账号管理全局状态机
 * 负责账号登录注册状态流转、历史账号列表管理以及角色名更新
 */
export const useAccountStore = create<AccountStore>((set, get) => ({
  currentAccount: getInitialAccount(),
  savedAccounts: loadSavedAccounts(),

  loginWithAuth: async (account: string, password: string) => {
    const res = await requestLogin(account, password)
    if (!res.success || !res.playerId || !res.account) {
      return res
    }

    const cleanAccount = res.account
    const cleanName = res.characterName || cleanAccount
    const playerId = res.playerId
    const sequenceId = res.sequenceId

    // 持久化存储到 localStorage 和 Cookie
    if (typeof window !== 'undefined') {
      window.localStorage.setItem(STORAGE_ACCOUNT_KEY, cleanAccount)
      window.localStorage.setItem(STORAGE_CHAR_NAME_KEY, cleanName)
      window.localStorage.setItem(STORAGE_PLAYER_ID_KEY, playerId)
      setCookiePlayerId(playerId)
    }

    const info: StoredAccountInfo = {
      account: cleanAccount,
      name: cleanName,
      playerId,
      sequenceId,
      lastLoginAt: Date.now(),
    }

    const filtered = get().savedAccounts.filter(
      a => a.account.toLowerCase() !== cleanAccount.toLowerCase(),
    )
    const updatedList = [info, ...filtered]
    persistSavedAccounts(updatedList)

    set({
      currentAccount: info,
      savedAccounts: updatedList,
    })

    // 登录成功后立即连接 WebSocket
    gameClient.connect()
    return res
  },

  registerWithAuth: async (account: string, password: string, characterName?: string) => {
    const res = await requestRegister(account, password, characterName)
    if (!res.success || !res.playerId || !res.account) {
      return res
    }

    const cleanAccount = res.account
    const cleanName = res.characterName || cleanAccount
    const playerId = res.playerId
    const sequenceId = res.sequenceId

    if (typeof window !== 'undefined') {
      window.localStorage.setItem(STORAGE_ACCOUNT_KEY, cleanAccount)
      window.localStorage.setItem(STORAGE_CHAR_NAME_KEY, cleanName)
      window.localStorage.setItem(STORAGE_PLAYER_ID_KEY, playerId)
      setCookiePlayerId(playerId)
    }

    const info: StoredAccountInfo = {
      account: cleanAccount,
      name: cleanName,
      playerId,
      sequenceId,
      lastLoginAt: Date.now(),
    }

    const filtered = get().savedAccounts.filter(
      a => a.account.toLowerCase() !== cleanAccount.toLowerCase(),
    )
    const updatedList = [info, ...filtered]
    persistSavedAccounts(updatedList)

    set({
      currentAccount: info,
      savedAccounts: updatedList,
    })

    // 注册成功后自动连接 WebSocket
    gameClient.connect()
    return res
  },

  logout: () => {
    if (typeof window !== 'undefined') {
      window.localStorage.removeItem(STORAGE_ACCOUNT_KEY)
      window.localStorage.removeItem(STORAGE_CHAR_NAME_KEY)
      window.localStorage.removeItem(STORAGE_PLAYER_ID_KEY)
      if (typeof document !== 'undefined') {
        document.cookie = `${COOKIE_PLAYER_ID_KEY}=; Path=/; Max-Age=0; SameSite=Lax`
      }
    }

    set({ currentAccount: null })
    gameClient.dispose()
    useGameStore.getState().resetGameStore()
  },

  removeSavedAccount: (account: string) => {
    const updated = get().savedAccounts.filter(
      a => a.account.toLowerCase() !== account.trim().toLowerCase(),
    )
    persistSavedAccounts(updated)
    set({ savedAccounts: updated })
  },

  updateCharacterName: (newName: string) => {
    const current = get().currentAccount
    if (!current) return
    const cleanName = newName.trim()
    if (typeof window !== 'undefined') {
      window.localStorage.setItem(STORAGE_CHAR_NAME_KEY, cleanName)
    }
    const updatedCurrent = { ...current, name: cleanName }
    const updatedSaved = get().savedAccounts.map(a =>
      a.account.toLowerCase() === current.account.toLowerCase()
        ? { ...a, name: cleanName }
        : a,
    )
    persistSavedAccounts(updatedSaved)
    set({ currentAccount: updatedCurrent, savedAccounts: updatedSaved })
  },
}))

/**
 * 同步获取当前保存的账号信息
 */
export function getStoredAccount(): StoredAccountInfo | null {
  return useAccountStore.getState().currentAccount
}

/**
 * 清除当前登录状态（登出）
 */
export function clearStoredAccount(): void {
  useAccountStore.getState().logout()
}

/**
 * 清理所有本地道化太初相关的持久化缓存（用于自愈与数据损坏修复）
 */
export function resetAllLocalData(): void {
  if (typeof window !== 'undefined') {
    try {
      const keysToRemove: string[] = []
      for (let i = 0; i < window.localStorage.length; i++) {
        const key = window.localStorage.key(i)
        if (key && (key.startsWith('daohua') || key.includes('daohua'))) {
          keysToRemove.push(key)
        }
      }
      keysToRemove.forEach(k => window.localStorage.removeItem(k))
    } catch {
      // localStorage 异常容错
    }
    if (typeof document !== 'undefined') {
      document.cookie = `${COOKIE_PLAYER_ID_KEY}=; Path=/; Max-Age=0; SameSite=Lax`
    }
  }
  useGameStore.getState().resetGameStore()
  useAccountStore.setState({ currentAccount: null, savedAccounts: [] })
}
