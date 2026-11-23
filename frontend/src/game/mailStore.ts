import { getSessionGeneration } from '../network/sessionState'
import { create } from 'zustand'
import { getT } from '../i18n'
import {
  claimAllMailAttachments,
  claimMailAttachments,
  deleteMail,
  deleteReadMails,
  fetchMailDetail,
  fetchMailList,
} from '../network/apiClient'
import type { MailAttachmentDto, MailDetailDto, MailListItemDto, MailSummaryDto } from '../network/protocol'
import { formatNumber } from './format'
import { useGameStore } from './gameStore'
import { findEquipmentInCatalog, findItemInCatalog, resolveCatalogItemMetadata } from './ids'

/** 邮件列表筛选器：全部 / 仅未读 / 仅可领附件 */
export type MailFilter = 'all' | 'unread' | 'claimable'

/** 初始空闲摘要状态（无红点） */
export const EMPTY_MAIL_SUMMARY: MailSummaryDto = {
  unreadCount: 0,
  claimableCount: 0,
  forceRedDot: false,
}

/** 判断顶栏邮件角标是否应当亮起红点（存在未读、可领附件，或全服广播冷推强制红点时为 true）。 */
export function hasMailRedDot(summary: MailSummaryDto): boolean {
  return summary.unreadCount > 0 || summary.claimableCount > 0 || Boolean(summary.forceRedDot)
}

/** 按指定筛选条件过滤邮件列表。 */
export function filterMailItems(mails: readonly MailListItemDto[], filter: MailFilter): MailListItemDto[] {
  if (filter === 'unread') return mails.filter(item => !item.isRead)
  if (filter === 'claimable') return mails.filter(item => item.hasAttachments && !item.isClaimed)
  return [...mails]
}

/** 邮件全局状态接口 */
export interface MailStore {
  /** 顶栏红点与待办计数摘要 */
  summary: MailSummaryDto
  /** 当前列表投影（不含正文 body） */
  mails: MailListItemDto[]
  /** 已按需拉取并缓存的邮件详情映射（mailKey -> detail） */
  detailsByKey: Record<string, MailDetailDto>
  /** 当前选中的邮件键 */
  selectedMailKey: string | null
  /** 当前筛选页签 */
  filter: MailFilter
  /** 列表加载状态 */
  loadingList: boolean
  /** 当前正在拉取详情的邮件键 */
  loadingDetailKey: string | null
  /** 当前正在执行领取/删除动作的键标识（含 'claim-all' / 'delete-read' / 'claim:<key>' / 'delete:<key>'） */
  actionBusyKey: string | null
  /** 是否已完成至少一次列表加载 */
  listLoaded: boolean
  /** 最近一次错误信息 */
  error: string | null

  /** 应用 WebSocket 或接口返回的新邮件摘要 */
  applySummary: (summary: MailSummaryDto) => void
  /** 切换列表筛选并自动对齐当前选中项 */
  setFilter: (filter: MailFilter) => void
  /** 从服务端刷新邮件列表（list 接口同时返回最新精确 summary，可消除零查库广播 forceRedDot） */
  refreshList: (options?: { silent?: boolean }) => Promise<void>
  /** 选中一封邮件并在需要时按需拉取正文详情（首读时后端同步置为已读） */
  selectMail: (mailKey: string) => Promise<void>
  /** 清空当前选中的邮件 */
  clearSelection: () => void
  /** 领取单封邮件全部附件 */
  claimMail: (mailKey: string) => Promise<void>
  /** 一键领取当前可领的全部邮件附件 */
  claimAllMails: () => Promise<void>
  /** 删除单封已无未领附件的邮件 */
  deleteOneMail: (mailKey: string) => Promise<void>
  /** 批量删除已读且无可领附件的邮件 */
  deleteAllReadMails: () => Promise<void>
  /** 清空邮件内存缓存（登出或重置本地状态时调用） */
  reset: () => void
}

function resolveAttachmentLabel(attachment: MailAttachmentDto): string {
  const { items, equipment } = useGameStore.getState()
  const meta = resolveCatalogItemMetadata(attachment.itemId, {
    itemDefinition: findItemInCatalog(items, attachment.itemId) ?? null,
    equipmentDefinition: findEquipmentInCatalog(equipment, attachment.itemId) ?? null,
  })
  return `${meta.label} ×${formatNumber(attachment.quantity, { maxDecimals: 0 })}`
}

function formatClaimedSummaryNotice(granted: readonly MailAttachmentDto[]): string {
  const t = getT()
  if (granted.length === 0) return t('app.mail.claimEmptyNotice')
  const preview = granted.slice(0, 3).map(resolveAttachmentLabel).join('、')
  return granted.length > 3
    ? t('app.mail.claimSuccessMoreNotice', { items: preview, total: granted.length })
    : t('app.mail.claimSuccessNotice', { items: preview })
}

function extractErrorMessage(cause: unknown, fallback: string): string {
  return cause instanceof Error && cause.message ? cause.message : fallback
}

/**
 * 列表刷新、切换筛选或删除后对齐当前选中键：
 * 仅当原选中键仍在当前可见列表中时予以保留，绝不擅自回退选中未点击的邮件（避免静默标为已读或悬空无正文）。
 */
function resolveRetainedSelection(
  currentKey: string | null,
  mails: readonly MailListItemDto[],
  filter: MailFilter,
): string | null {
  if (!currentKey) return null
  const visible = filterMailItems(mails, filter)
  return visible.some(item => item.mailKey === currentKey) ? currentKey : null
}

// 用模块级递增序号取消/忽略乱序响应，避免旧请求覆盖新列表或新选定邮件。
let listRequestSeq = 0
let detailRequestSeq = 0
let mailResetEpoch = 0

/**
 * 传书邮件前端状态机：
 * 1. 未打开邮件界面前只持有轻量 `summary`（来自 Hello 与广播推送 `MailSummary`）；
 * 2. 打开邮件面板才拉取列表（列表 SQL 不传输正文 `body`），点击具体信件才按需拉取详情并缓存；
 * 3. 领取附件成功后前端等待或配合 WS Zero-StateVersion `SnapshotDelta` 刷新资产面板，并通过 notice 反馈到账道具。
 */
export const useMailStore = create<MailStore>((set, get) => ({
  summary: EMPTY_MAIL_SUMMARY,
  mails: [],
  detailsByKey: {},
  selectedMailKey: null,
  filter: 'all',
  loadingList: false,
  loadingDetailKey: null,
  actionBusyKey: null,
  listLoaded: false,
  error: null,

  applySummary: summary => {
    set({ summary })
  },

  setFilter: filter => {
    set(state => ({
      filter,
      selectedMailKey: resolveRetainedSelection(state.selectedMailKey, state.mails, filter),
    }))
  },

  refreshList: async (options) => {
    const generation = getSessionGeneration()
    const seq = ++listRequestSeq
    if (!options?.silent) {
      set({ loadingList: true, error: null })
    }
    try {
      const response = await fetchMailList()
      if (generation !== getSessionGeneration()) return
      if (seq !== listRequestSeq) return
      const nextMails = response.mails
      set(state => {
        const nextDetails: Record<string, MailDetailDto> = {}
        const byKey = new Map(nextMails.map(m => [m.mailKey, m]))
        // 同步缓存详情中的已读/已领标记，清除已过期或已删邮件的正文缓存
        for (const [key, detail] of Object.entries(state.detailsByKey)) {
          const updated = byKey.get(key)
          if (updated) {
            nextDetails[key] = { ...detail, ...updated }
          }
        }
        return {
          mails: nextMails,
          detailsByKey: nextDetails,
          summary: response.summary,
          selectedMailKey: resolveRetainedSelection(state.selectedMailKey, nextMails, state.filter),
          loadingList: false,
          listLoaded: true,
          error: null,
        }
      })
    } catch (cause) {
      if (generation !== getSessionGeneration()) return
      if (seq !== listRequestSeq) return
      const message = extractErrorMessage(cause, getT()('app.mail.loadFailed'))
      set({ loadingList: false, error: message })
    }
  },

  clearSelection: () => {
    set({ selectedMailKey: null })
  },

  selectMail: async mailKey => {
    const generation = getSessionGeneration()
    const current = get()
    set({ selectedMailKey: mailKey, error: null })

    const cachedDetail = current.detailsByKey[mailKey]
    const listItem = current.mails.find(m => m.mailKey === mailKey)
    // 已缓存详情且早已标为已读时零网络请求直接切过去
    if (cachedDetail && (cachedDetail.isRead || listItem?.isRead)) {
      return
    }

    const seq = ++detailRequestSeq
    const resetEpoch = mailResetEpoch
    set({ loadingDetailKey: mailKey })
    try {
      const response = await fetchMailDetail(mailKey)
      if (generation !== getSessionGeneration() || resetEpoch !== mailResetEpoch) return
      const detail = response.mail
      set(state => {
        const existingItem = state.mails.find(item => item.mailKey === mailKey)
        // 若飞行途中该信件已被删除，绝不借迟到详情响应复活列表或缓存，仅收敛当前加载态。
        if (!existingItem) {
          return {
            loadingDetailKey: seq === detailRequestSeq && state.loadingDetailKey === mailKey ? null : state.loadingDetailKey,
          }
        }
        // 单封邮件状态合并：保证已读/已领取标记单调不回退，连续快速点击多封未读信件时先发响应不丢已读状态。
        const mergedIsRead = Boolean(existingItem.isRead || detail.isRead)
        const mergedIsClaimed = Boolean(existingItem.isClaimed || detail.isClaimed)
        const mergedDetail: MailDetailDto = {
          ...detail,
          isRead: mergedIsRead,
          isClaimed: mergedIsClaimed,
        }
        const nextMails = state.mails.map(item =>
          item.mailKey === mailKey ? { ...item, ...mergedDetail } : item,
        )
        const nextSummary: MailSummaryDto = seq === detailRequestSeq
          ? {
              ...response.summary,
              unreadCount: Math.min(response.summary.unreadCount, nextMails.filter(item => !item.isRead).length || response.summary.unreadCount),
            }
          : {
              ...state.summary,
              unreadCount: Math.min(state.summary.unreadCount, response.summary.unreadCount),
            }
        return {
          summary: nextSummary,
          detailsByKey: { ...state.detailsByKey, [mailKey]: mergedDetail },
          mails: nextMails,
          selectedMailKey: resolveRetainedSelection(state.selectedMailKey, nextMails, state.filter),
          loadingDetailKey: seq === detailRequestSeq && state.loadingDetailKey === mailKey ? null : state.loadingDetailKey,
        }
      })
    } catch (cause) {
      if (generation !== getSessionGeneration()) return
      if (seq !== detailRequestSeq) return
      const message = extractErrorMessage(cause, getT()('app.mail.detailFailed'))
      set({ loadingDetailKey: null, error: message })
      useGameStore.getState().setNotice(message)
    }
  },

  claimMail: async mailKey => {
    const generation = getSessionGeneration()
    const busyKey = `claim:${mailKey}`
    if (get().actionBusyKey) return
    set({ actionBusyKey: busyKey, error: null })
    try {
      const response = await claimMailAttachments(mailKey)
      if (generation !== getSessionGeneration()) return
      const claimedSet = new Set(response.claimedMailKeys)
      set(state => {
        const nextMails = state.mails.map(item =>
          claimedSet.has(item.mailKey) ? { ...item, isRead: true, isClaimed: true } : item,
        )
        const nextDetails = { ...state.detailsByKey }
        for (const key of claimedSet) {
          const existing = nextDetails[key]
          if (existing) {
            nextDetails[key] = { ...existing, isRead: true, isClaimed: true }
          }
        }
        return {
          summary: response.summary,
          mails: nextMails,
          detailsByKey: nextDetails,
          actionBusyKey: null,
        }
      })
      useGameStore.getState().setNotice(formatClaimedSummaryNotice(response.grantedAttachments))
    } catch (cause) {
      if (generation !== getSessionGeneration()) return
      const message = extractErrorMessage(cause, getT()('app.mail.claimFailed'))
      set({ actionBusyKey: null, error: message })
      useGameStore.getState().setNotice(message)
    }
  },

  claimAllMails: async () => {
    const generation = getSessionGeneration()
    if (get().actionBusyKey) return
    set({ actionBusyKey: 'claim-all', error: null })
    try {
      const response = await claimAllMailAttachments()
      if (generation !== getSessionGeneration()) return
      const claimedSet = new Set(response.claimedMailKeys)
      set(state => {
        const nextMails = state.mails.map(item =>
          claimedSet.has(item.mailKey) ? { ...item, isRead: true, isClaimed: true } : item,
        )
        const nextDetails = { ...state.detailsByKey }
        for (const key of claimedSet) {
          const existing = nextDetails[key]
          if (existing) {
            nextDetails[key] = { ...existing, isRead: true, isClaimed: true }
          }
        }
        return {
          summary: response.summary,
          mails: nextMails,
          detailsByKey: nextDetails,
          actionBusyKey: null,
        }
      })
      useGameStore.getState().setNotice(formatClaimedSummaryNotice(response.grantedAttachments))
    } catch (cause) {
      if (generation !== getSessionGeneration()) return
      const message = extractErrorMessage(cause, getT()('app.mail.claimFailed'))
      set({ actionBusyKey: null, error: message })
      useGameStore.getState().setNotice(message)
    }
  },

  deleteOneMail: async mailKey => {
    const generation = getSessionGeneration()
    const stateSnapshot = get()
    if (stateSnapshot.actionBusyKey) return
    const target = stateSnapshot.detailsByKey[mailKey] ?? stateSnapshot.mails.find(m => m.mailKey === mailKey)
    if (target?.hasAttachments && !target.isClaimed) {
      useGameStore.getState().setNotice(getT()('app.mail.deleteBlockedUnclaimed'))
      return
    }

    set({ actionBusyKey: `delete:${mailKey}`, error: null })
    try {
      const response = await deleteMail(mailKey)
      if (generation !== getSessionGeneration()) return
      const deletedSet = new Set(response.deletedMailKeys)
      set(state => {
        const nextMails = state.mails.filter(item => !deletedSet.has(item.mailKey))
        const nextDetails = { ...state.detailsByKey }
        for (const key of deletedSet) delete nextDetails[key]
        const nextSelected = resolveRetainedSelection(state.selectedMailKey, nextMails, state.filter)
        return {
          summary: response.summary,
          mails: nextMails,
          detailsByKey: nextDetails,
          selectedMailKey: nextSelected,
          actionBusyKey: null,
        }
      })
      useGameStore.getState().setNotice(getT()('app.mail.deleteSuccessNotice'))
    } catch (cause) {
      if (generation !== getSessionGeneration()) return
      const message = extractErrorMessage(cause, getT()('app.mail.deleteFailed'))
      set({ actionBusyKey: null, error: message })
      useGameStore.getState().setNotice(message)
    }
  },

  deleteAllReadMails: async () => {
    const generation = getSessionGeneration()
    if (get().actionBusyKey) return
    set({ actionBusyKey: 'delete-read', error: null })
    try {
      const response = await deleteReadMails()
      if (generation !== getSessionGeneration()) return
      const deletedSet = new Set(response.deletedMailKeys)
      set(state => {
        const nextMails = state.mails.filter(item => !deletedSet.has(item.mailKey))
        const nextDetails = { ...state.detailsByKey }
        for (const key of deletedSet) delete nextDetails[key]
        const nextSelected = resolveRetainedSelection(state.selectedMailKey, nextMails, state.filter)
        return {
          summary: response.summary,
          mails: nextMails,
          detailsByKey: nextDetails,
          selectedMailKey: nextSelected,
          actionBusyKey: null,
        }
      })
      const t = getT()
      const deletedCount = response.deletedMailKeys.length
      if (deletedCount === 0 && response.skippedUnclaimedCount > 0) {
        useGameStore.getState().setNotice(t('app.mail.deleteReadAllSkippedNotice', { skipped: response.skippedUnclaimedCount }))
      } else if (response.skippedUnclaimedCount > 0) {
        useGameStore.getState().setNotice(t('app.mail.deleteReadPartialNotice', { deleted: deletedCount, skipped: response.skippedUnclaimedCount }))
      } else if (deletedCount > 0) {
        useGameStore.getState().setNotice(t('app.mail.deleteReadSuccessNotice', { deleted: deletedCount }))
      } else {
        useGameStore.getState().setNotice(t('app.mail.deleteReadNoneNotice'))
      }
    } catch (cause) {
      if (generation !== getSessionGeneration()) return
      const message = extractErrorMessage(cause, getT()('app.mail.deleteFailed'))
      set({ actionBusyKey: null, error: message })
      useGameStore.getState().setNotice(message)
    }
  },

  reset: () => {
    listRequestSeq += 1
    detailRequestSeq += 1
    mailResetEpoch += 1
    set({
      summary: EMPTY_MAIL_SUMMARY,
      mails: [],
      detailsByKey: {},
      selectedMailKey: null,
      filter: 'all',
      loadingList: false,
      loadingDetailKey: null,
      actionBusyKey: null,
      listLoaded: false,
      error: null,
    })
  },
}))
