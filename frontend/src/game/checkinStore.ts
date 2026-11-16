import { create } from 'zustand'
import { getT } from '../i18n'
import { claimDailyCheckin, fetchCheckinStatus } from '../network/apiClient'
import type { CheckinClaimResponseDto, CheckinStatusDto, CheckinSummaryDto } from '../network/protocol'
import { useGameStore } from './gameStore'

/** UTC+8 偏移秒数（与服务端 ShopDailyWindow / DailyCheckinRules 一致） */
export const CHINA_OFFSET_SECONDS = 28_800
/** 一自然日秒数 */
export const SECONDS_PER_DAY = 86_400
/** 签到奖励物品 ID（绑定功德） */
export const CHECKIN_REWARD_ITEM_ID = 'item.gongde-bound'

const DISMISSED_STORAGE_PREFIX = 'daohua.checkin.dismissed.'

/** 由 Unix 秒戳换算 UTC+8 自然日序号 */
export function getChinaDayIndex(unixSeconds: number): number {
  return Math.floor((unixSeconds + CHINA_OFFSET_SECONDS) / SECONDS_PER_DAY)
}

/** 计算指定时刻在 UTC+8 自然周内的偏移索引（周一=0 .. 周日=6） */
export function getChinaDayOfWeekIndexMonday(unixSeconds: number): number {
  const utcDay = new Date((unixSeconds + CHINA_OFFSET_SECONDS) * 1000).getUTCDay()
  return (utcDay + 6) % 7
}

/** 计算指定时刻之后的下一个 UTC+8 零点 Unix 秒戳 */
export function getNextChinaResetUnixSeconds(unixSeconds: number): number {
  return (getChinaDayIndex(unixSeconds) + 1) * SECONDS_PER_DAY - CHINA_OFFSET_SECONDS
}

/** 计算距离下一个 UTC+8 零点的延迟毫秒数（附带 1.5s 缓冲，确保服务端时钟同步跨日） */
export function computeMsUntilNextChinaMidnight(serverNowUnixSeconds: number): number {
  const remainingSec = getNextChinaResetUnixSeconds(serverNowUnixSeconds) - serverNowUnixSeconds
  return Math.max(1_000, Math.ceil(remainingSec * 1000) + 1_500)
}

/** 判断入口是否应当展示「今日可签到」红点 */
export function hasCheckinRedDot(
  summary: CheckinSummaryDto | null,
  status: CheckinStatusDto | null,
): boolean {
  if (summary?.isBot) return false
  if (status) return status.canClaimToday
  if (summary) return summary.canClaimToday
  return false
}

/** 格式化剩余倒计时为 HH:MM:SS */
export function formatCountdownClock(remainingSeconds: number): string {
  const clamped = Math.max(0, Math.floor(remainingSeconds))
  const hours = Math.floor(clamped / 3600)
  const minutes = Math.floor((clamped % 3600) / 60)
  const seconds = clamped % 60
  return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
}

/** 道劫签运档位键集合 */
export type CheckinFortuneTier =
  | 'very_bad'
  | 'bad'
  | 'neutral'
  | 'good'
  | 'great'
  | 'transcendent_1'
  | 'transcendent_2'
  | 'transcendent_3'
  | 'transcendent_4'
  | 'perfect'

/** 规范化后端下发的签运档位（兜底 null） */
export function normalizeCheckinFortuneTier(tier: string | null | undefined): CheckinFortuneTier | null {
  switch (tier) {
    case 'very_bad':
    case 'bad':
    case 'neutral':
    case 'good':
    case 'great':
    case 'transcendent_1':
    case 'transcendent_2':
    case 'transcendent_3':
    case 'transcendent_4':
    case 'perfect':
      return tier
    default:
      return null
  }
}

/** 签运档位对应的修仙色系类别 */
export function resolveFortuneVisualTone(tier: CheckinFortuneTier | null): 'bad' | 'neutral' | 'good' | 'gold' {
  switch (tier) {
    case 'very_bad':
    case 'bad':
      return 'bad'
    case 'neutral':
      return 'neutral'
    case 'good':
    case 'great':
      return 'good'
    case 'transcendent_1':
    case 'transcendent_2':
    case 'transcendent_3':
    case 'transcendent_4':
    case 'perfect':
      return 'gold'
    default:
      return 'neutral'
  }
}

function dismissedStorageKey(playerId: string | null): string | null {
  if (!playerId) return null
  return `${DISMISSED_STORAGE_PREFIX}${playerId}`
}

/** 读取指定玩家上次在本地主动关闭签到自动弹窗的 UTC+8 自然日序号 */
export function readDismissedCheckinDay(playerId: string | null): number | null {
  const key = dismissedStorageKey(playerId)
  if (!key || typeof window === 'undefined') return null
  try {
    const raw = window.localStorage.getItem(key)
    if (!raw) return null
    const parsed = Number.parseInt(raw, 10)
    return Number.isFinite(parsed) ? parsed : null
  } catch {
    return null
  }
}

/** 记录指定玩家已在今日主动关闭自动弹窗，本日后续刷新不再打扰，次日/过零点自动恢复 */
export function writeDismissedCheckinDay(playerId: string | null, dayIndex: number): void {
  const key = dismissedStorageKey(playerId)
  if (!key || typeof window === 'undefined') return
  try {
    window.localStorage.setItem(key, String(dayIndex))
  } catch {
    // 隐私模式或存储满时忽略，至少保留内存态记忆
  }
}

function summaryFromStatus(status: CheckinStatusDto): CheckinSummaryDto {
  return {
    canClaimToday: status.canClaimToday,
    todayDate: status.todayDate,
    dayIndex: status.dayIndex,
    streakDays: status.streakDays,
    totalDays: status.totalDays,
    lastRewardMerit: status.lastRewardMerit,
    isBot: false,
  }
}

export interface CheckinStore {
  /** WS 握手下发的轻量签到摘要 */
  summary: CheckinSummaryDto | null
  /** 完整每日签到状态（打开弹窗或跨日刷新时拉取） */
  status: CheckinStatusDto | null
  /** 状态拉取中 */
  loading: boolean
  /** 领奖提交中 */
  claiming: boolean
  /** 最近一次错误消息 */
  error: string | null
  /** 本次刚签到成功的回执（用于在弹窗内高亮反馈） */
  lastClaimResult: CheckinClaimResponseDto | null
  /** 当前会话/本地已关闭自动弹窗的自然日序号 */
  dismissedDayIndex: number | null

  /** 应用 WS Hello 握手下发的签到摘要 */
  applySummary: (summary: CheckinSummaryDto) => void
  /** 拉取完整签到状态，并同步刷新红点摘要 */
  refreshStatus: (options?: { silent?: boolean }) => Promise<CheckinStatusDto | null>
  /** 执行签到领奖 */
  claim: () => Promise<CheckinClaimResponseDto | null>
  /** 标记当日自动弹窗已被玩家关闭 */
  markDismissedForDay: (playerId: string | null, dayIndex: number) => void
  /** 清理上次领取结果高亮 */
  clearLastClaimResult: () => void
  /** 登出或重置缓存 */
  reset: () => void
}

let activeRefreshPromise: Promise<CheckinStatusDto | null> | null = null

export const useCheckinStore = create<CheckinStore>((set, get) => ({
  summary: null,
  status: null,
  loading: false,
  claiming: false,
  error: null,
  lastClaimResult: null,
  dismissedDayIndex: null,

  applySummary: summary => {
    const prevStatus = get().status
    const rolledDay = prevStatus !== null && prevStatus.dayIndex !== summary.dayIndex
    set({
      summary,
      ...(rolledDay ? { status: null, lastClaimResult: null } : {}),
    })
  },

  refreshStatus: async options => {
    if (activeRefreshPromise) return activeRefreshPromise
    if (!options?.silent) {
      set({ loading: true, error: null })
    }
    const promise = (async () => {
      try {
        const status = await fetchCheckinStatus()
        const prevDay = get().status?.dayIndex
        set({
          status,
          summary: summaryFromStatus(status),
          loading: false,
          error: null,
          ...(prevDay !== undefined && prevDay !== status.dayIndex ? { lastClaimResult: null } : {}),
        })
        return status
      } catch (err) {
        const message = err instanceof Error ? err.message : getT()('app.checkin.loadFailed')
        set({ loading: false, error: message })
        return null
      } finally {
        activeRefreshPromise = null
      }
    })()
    activeRefreshPromise = promise
    return promise
  },

  claim: async () => {
    if (get().claiming) return null
    set({ claiming: true, error: null })
    try {
      const result = await claimDailyCheckin()
      set({
        claiming: false,
        status: result.status,
        summary: summaryFromStatus(result.status),
        lastClaimResult: result.claimed ? result : get().lastClaimResult,
      })
      if (result.claimed) {
        useGameStore.getState().setNotice(
          getT()('app.checkin.claimSuccessNotice', { amount: result.rewardMerit }),
        )
      }
      return result
    } catch (err) {
      const message = err instanceof Error ? err.message : getT()('app.checkin.claimFailed')
      set({ claiming: false, error: message })
      useGameStore.getState().setNotice(message)
      return null
    }
  },

  markDismissedForDay: (playerId, dayIndex) => {
    writeDismissedCheckinDay(playerId, dayIndex)
    set({ dismissedDayIndex: dayIndex })
  },

  clearLastClaimResult: () => set({ lastClaimResult: null }),

  reset: () => {
    activeRefreshPromise = null
    set({
      summary: null,
      status: null,
      loading: false,
      claiming: false,
      error: null,
      lastClaimResult: null,
      dismissedDayIndex: null,
    })
  },
}))
