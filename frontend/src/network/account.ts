import { create } from 'zustand'
import { gameClient } from './gameClient'
import { useGameStore } from '../game/gameStore'

export interface StoredAccountInfo {
  account: string
  name: string
  playerId: string
  lastLoginAt?: number
}

const STORAGE_ACCOUNT_KEY = 'xiuxian_account'
const STORAGE_CHAR_NAME_KEY = 'xiuxian_character_name'
const STORAGE_PLAYER_ID_KEY = 'xiuxian-player-id'
const STORAGE_SAVED_ACCOUNTS_KEY = 'xiuxian_saved_accounts_list'
const COOKIE_PLAYER_ID_KEY = 'xiuxian_player_id'

function getCookiePlayerId(): string | null {
  if (typeof document === 'undefined') return null
  const cookieValue = document.cookie
    .split(';')
    .map(part => part.trim())
    .find(part => part.startsWith(`${COOKIE_PLAYER_ID_KEY}=`))
    ?.slice(COOKIE_PLAYER_ID_KEY.length + 1)
  return cookieValue ? decodeURIComponent(cookieValue) : null
}

function setCookiePlayerId(playerId: string): void {
  if (typeof document === 'undefined') return
  document.cookie = `${COOKIE_PLAYER_ID_KEY}=${encodeURIComponent(playerId)}; Path=/; Max-Age=315360000; SameSite=Lax`
}

function loadSavedAccounts(): StoredAccountInfo[] {
  if (typeof window === 'undefined') return []
  try {
    const raw = window.localStorage.getItem(STORAGE_SAVED_ACCOUNTS_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
}

function persistSavedAccounts(list: StoredAccountInfo[]): void {
  if (typeof window === 'undefined') return
  window.localStorage.setItem(STORAGE_SAVED_ACCOUNTS_KEY, JSON.stringify(list))
}

function getInitialAccount(): StoredAccountInfo | null {
  if (typeof window === 'undefined') return null
  const account = window.localStorage.getItem(STORAGE_ACCOUNT_KEY)?.trim()
  const name = window.localStorage.getItem(STORAGE_CHAR_NAME_KEY)?.trim()
  const playerId = window.localStorage.getItem(STORAGE_PLAYER_ID_KEY)?.trim() || getCookiePlayerId()

  if (account && playerId) {
    return {
      account,
      name: name || account,
      playerId,
      lastLoginAt: Date.now(),
    }
  }

  // 兼容老玩家（已有 playerId 但尚未记录账号名，自动升级为默认账号）
  if (playerId) {
    const defaultAccount = 'YuoHira'
    const defaultName = 'YuoHira'
    window.localStorage.setItem(STORAGE_ACCOUNT_KEY, defaultAccount)
    window.localStorage.setItem(STORAGE_CHAR_NAME_KEY, defaultName)
    window.localStorage.setItem(STORAGE_PLAYER_ID_KEY, playerId)
    setCookiePlayerId(playerId)
    const info: StoredAccountInfo = {
      account: defaultAccount,
      name: defaultName,
      playerId,
      lastLoginAt: Date.now(),
    }
    const saved = loadSavedAccounts()
    if (!saved.some(a => a.account.toLowerCase() === defaultAccount.toLowerCase())) {
      persistSavedAccounts([info, ...saved])
    }
    return info
  }

  return null
}

interface AccountStore {
  currentAccount: StoredAccountInfo | null
  savedAccounts: StoredAccountInfo[]
  login: (account: string, name?: string, explicitPlayerId?: string) => StoredAccountInfo
  logout: () => void
  removeSavedAccount: (account: string) => void
  updateCharacterName: (newName: string) => void
}

export const useAccountStore = create<AccountStore>((set, get) => ({
  currentAccount: getInitialAccount(),
  savedAccounts: loadSavedAccounts(),

  login: (account: string, name?: string, explicitPlayerId?: string) => {
    const cleanAccount = account.trim()
    const cleanName = (name || cleanAccount).trim()
    const accountKey = `xiuxian_account_player_id_${cleanAccount.toLowerCase()}`

    let playerId = explicitPlayerId?.trim()
    if (!playerId && typeof window !== 'undefined') {
      playerId = window.localStorage.getItem(accountKey)?.trim()
        || crypto.randomUUID()
    } else if (!playerId) {
      playerId = crypto.randomUUID()
    }

    if (typeof window !== 'undefined') {
      window.localStorage.setItem(accountKey, playerId)
      window.localStorage.setItem(STORAGE_ACCOUNT_KEY, cleanAccount)
      window.localStorage.setItem(STORAGE_CHAR_NAME_KEY, cleanName)
      window.localStorage.setItem(STORAGE_PLAYER_ID_KEY, playerId)
      setCookiePlayerId(playerId)
    }

    const info: StoredAccountInfo = {
      account: cleanAccount,
      name: cleanName,
      playerId,
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

    gameClient.connect()
    return info
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

export function getStoredAccount(): StoredAccountInfo | null {
  return useAccountStore.getState().currentAccount
}

export function setStoredAccount(account: string, name?: string, explicitPlayerId?: string): StoredAccountInfo {
  return useAccountStore.getState().login(account, name, explicitPlayerId)
}

export function clearStoredAccount(): void {
  useAccountStore.getState().logout()
}
