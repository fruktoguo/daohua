const STORAGE_ACCOUNT_KEY = 'xiuxian_account'
const STORAGE_CHAR_NAME_KEY = 'xiuxian_character_name'
const STORAGE_PLAYER_ID_KEY = 'xiuxian-player-id'
const COOKIE_PLAYER_ID_KEY = 'xiuxian_player_id'

export interface StoredAccountInfo {
  account: string
  name: string
  playerId: string
}

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

export function getStoredAccount(): StoredAccountInfo | null {
  if (typeof window === 'undefined') return null
  const account = window.localStorage.getItem(STORAGE_ACCOUNT_KEY)?.trim()
  const name = window.localStorage.getItem(STORAGE_CHAR_NAME_KEY)?.trim()
  const playerId = window.localStorage.getItem(STORAGE_PLAYER_ID_KEY)?.trim() || getCookiePlayerId()

  if (account && playerId) {
    return {
      account,
      name: name || account,
      playerId,
    }
  }

  // 兼容老玩家（已有 playerId 但尚未记录账号名，自动平滑升级为默认账号）
  if (playerId) {
    const defaultAccount = 'YuoHira'
    const defaultName = 'YuoHira'
    window.localStorage.setItem(STORAGE_ACCOUNT_KEY, defaultAccount)
    window.localStorage.setItem(STORAGE_CHAR_NAME_KEY, defaultName)
    window.localStorage.setItem(STORAGE_PLAYER_ID_KEY, playerId)
    setCookiePlayerId(playerId)
    return {
      account: defaultAccount,
      name: defaultName,
      playerId,
    }
  }

  return null
}

export function setStoredAccount(account: string, name?: string, explicitPlayerId?: string): StoredAccountInfo {
  const cleanAccount = account.trim()
  const cleanName = (name || cleanAccount).trim()
  const accountKey = `xiuxian_account_player_id_${cleanAccount.toLowerCase()}`

  let playerId = explicitPlayerId?.trim()
  if (!playerId) {
    playerId = window.localStorage.getItem(accountKey)?.trim()
      || crypto.randomUUID()
  }

  window.localStorage.setItem(accountKey, playerId)
  window.localStorage.setItem(STORAGE_ACCOUNT_KEY, cleanAccount)
  window.localStorage.setItem(STORAGE_CHAR_NAME_KEY, cleanName)
  window.localStorage.setItem(STORAGE_PLAYER_ID_KEY, playerId)
  setCookiePlayerId(playerId)

  return {
    account: cleanAccount,
    name: cleanName,
    playerId,
  }
}

export function clearStoredAccount(): void {
  if (typeof window === 'undefined') return
  window.localStorage.removeItem(STORAGE_ACCOUNT_KEY)
  window.localStorage.removeItem(STORAGE_CHAR_NAME_KEY)
  window.localStorage.removeItem(STORAGE_PLAYER_ID_KEY)
  if (typeof document !== 'undefined') {
    document.cookie = `${COOKIE_PLAYER_ID_KEY}=; Path=/; Max-Age=0; SameSite=Lax`
  }
}
