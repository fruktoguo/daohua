/**
 * 同浏览器多标签页互斥守卫。
 *
 * 主路径使用 BroadcastChannel 广播竞逐：新标签页发出 claim，运行中的持有页或
 * 竞逐中占优（建立更早、平局按 tabId 字典序）的页面回 held，探测窗内收到占优
 * 应答的新页自我阻断——无响应即继位，崩溃页天然让位（无法应答）。
 * 被阻断页可通过 requestTakeover 主动接管：向持有页发 takeover，对方让位后本页刷新重逐。
 *
 * 无 BroadcastChannel 的环境降级为 localStorage 租约：持有页周期续写心跳，
 * 新页读到新鲜租约即自我阻断；接管通过写入接管凭据 + 覆盖租约完成。
 */
const CHANNEL_NAME = 'daohua.antiMulti.tabGuard'
const STORAGE_KEY = 'daohua.antiMulti.tabGuard.lease'
const TAKEOVER_KEY = 'daohua.antiMulti.tabGuard.takeover'
const PROBE_WINDOW_MS = 400
const LEASE_TTL_MS = 4_000
const HEARTBEAT_MS = 1_500
const TAKEOVER_RELOAD_DELAY_MS = 250

interface GuardMessage {
  type: 'claim' | 'held' | 'takeover'
  tabId: string
  bornAt: number
}

interface LeaseRecord {
  tabId: string
  bornAt: number
  heartbeatAt: number
}

/** 互斥守卫句柄：供被阻断页请求接管与守卫生命周期清理 */
export interface TabGuardHandle {
  /** 被阻断页请求接管：通知持有页让位，随后刷新本页重新竞逐 */
  requestTakeover: () => void
  /** 释放守卫占用的通道与定时器 */
  dispose: () => void
}

function newTabId(): string {
  try {
    return crypto.randomUUID()
  } catch {
    return `tab-${Date.now()}-${Math.random().toString(36).slice(2)}`
  }
}

function readLease(): LeaseRecord | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<LeaseRecord>
    if (typeof parsed.tabId !== 'string' || typeof parsed.bornAt !== 'number' || typeof parsed.heartbeatAt !== 'number') return null
    return { tabId: parsed.tabId, bornAt: parsed.bornAt, heartbeatAt: parsed.heartbeatAt }
  } catch {
    return null
  }
}

/**
 * 安装标签页互斥守卫。
 * @param onBlocked 本页被判定为多余副本（或被接管让位）时回调，只触发一次
 */
export function installTabGuard(onBlocked: () => void): TabGuardHandle {
  const tabId = newTabId()
  const bornAt = Date.now()
  let blocked = false
  let leader = false

  const markBlocked = () => {
    if (blocked) return
    blocked = true
    leader = false
    onBlocked()
  }

  /** 对端是否比我更优先：建立更早者优先，平局按 tabId 字典序决胜 */
  const peerIsSuperior = (peerId: string, peerBornAt: number) =>
    peerBornAt < bornAt || (peerBornAt === bornAt && peerId < tabId)

  const takeoverReload = () => window.setTimeout(() => window.location.reload(), TAKEOVER_RELOAD_DELAY_MS)

  if (typeof window.BroadcastChannel === 'function') {
    const channel = new BroadcastChannel(CHANNEL_NAME)
    channel.onmessage = event => {
      const message = event.data as Partial<GuardMessage> | null
      if (!message || typeof message.tabId !== 'string' || typeof message.bornAt !== 'number' || message.tabId === tabId) return
      switch (message.type) {
        case 'claim':
          // 已阻断页不得应答（否则新页会被已失效页面再次误杀）。
          // 更早建立的存活页永远优先：leader 收到占优 claim 时让位，保证极端时序下仍只有最老页存活。
          if (blocked) break
          if (peerIsSuperior(message.tabId, message.bornAt)) {
            markBlocked()
            break
          }
          channel.postMessage({ type: 'held', tabId, bornAt } satisfies GuardMessage)
          break
        case 'held':
          if (!blocked && peerIsSuperior(message.tabId, message.bornAt)) markBlocked()
          break
        case 'takeover':
          if (leader) markBlocked()
          break
      }
    }
    channel.postMessage({ type: 'claim', tabId, bornAt } satisfies GuardMessage)
    const probeTimer = window.setTimeout(() => {
      if (!blocked) leader = true
    }, PROBE_WINDOW_MS)

    return {
      requestTakeover: () => {
        if (!blocked) return
        try {
          channel.postMessage({ type: 'takeover', tabId, bornAt } satisfies GuardMessage)
        } catch {
          // 通道关闭时仍刷新重逐：持有页若仍存活会再次把本页阻断。
        }
        takeoverReload()
      },
      dispose: () => {
        window.clearTimeout(probeTimer)
        channel.close()
      },
    }
  }

  // 降级路径：localStorage 租约。新页只在租约过期或接管凭据命中时才写入自身，
  // 因此任何异己写入都意味着接管/夺权——按 bornAt 决胜，后者优先。
  let pendingTakeover: string | null = null
  try {
    pendingTakeover = window.localStorage.getItem(TAKEOVER_KEY)
    if (pendingTakeover) window.localStorage.removeItem(TAKEOVER_KEY)
  } catch {
    // localStorage 不可用时无从裁决，直接放行本页（守卫失效但不阻断正常使用）。
    return { requestTakeover: () => undefined, dispose: () => undefined }
  }

  const existing = readLease()
  const takeoverMatched = existing !== null && pendingTakeover !== null && existing.tabId === pendingTakeover
  if (existing !== null && !takeoverMatched && Date.now() - existing.heartbeatAt <= LEASE_TTL_MS) {
    markBlocked()
    return {
      requestTakeover: () => {
        const claimId = newTabId()
        try {
          window.localStorage.setItem(TAKEOVER_KEY, claimId)
          window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ tabId: claimId, bornAt: Date.now(), heartbeatAt: Date.now() } satisfies LeaseRecord))
        } catch {
          // 写入失败时直接刷新，租约过期后仍可继位。
        }
        takeoverReload()
      },
      dispose: () => undefined,
    }
  }

  leader = true
  const writeLease = () => {
    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ tabId, bornAt, heartbeatAt: Date.now() } satisfies LeaseRecord))
    } catch {
      // 写入失败放弃续期，租约自然过期。
    }
  }
  writeLease()
  const heartbeatTimer = window.setInterval(writeLease, HEARTBEAT_MS)
  const onStorage = (event: StorageEvent) => {
    if (event.key !== STORAGE_KEY || typeof event.newValue !== 'string') return
    try {
      const entry = JSON.parse(event.newValue) as Partial<LeaseRecord>
      // 异己租约接管判定：接管凭据与过期重逐的 bornAt 都比本页新，平局不会出现（tabId 随机）。
      if (typeof entry.tabId === 'string' && entry.tabId !== tabId && typeof entry.bornAt === 'number' && entry.bornAt >= bornAt) markBlocked()
    } catch {
      // 无法解析的写入视为无效数据，不影响本页。
    }
  }
  window.addEventListener('storage', onStorage)

  return {
    requestTakeover: () => undefined,
    dispose: () => {
      window.clearInterval(heartbeatTimer)
      window.removeEventListener('storage', onStorage)
    },
  }
}
