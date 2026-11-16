import { useEffect, useId, useMemo, useState } from 'react'
import {
  CHECKIN_REWARD_ITEM_ID,
  formatCountdownClock,
  getChinaDayOfWeekIndexMonday,
  getNextChinaResetUnixSeconds,
  normalizeCheckinFortuneTier,
  resolveFortuneVisualTone,
  useCheckinStore,
} from '../game/checkinStore'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findItemInCatalog, resolveCatalogItemMetadata } from '../game/ids'
import { useT } from '../i18n'
import { Icon } from './Icon'
import { ItemIcon, ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { Modal } from './Modal'

export interface CheckinModalProps {
  /** 弹窗开启状态 */
  open: boolean
  /** 关闭弹窗回调 */
  onClose: () => void
}

const WEEKDAY_KEYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const
const NORMAL_MERIT_ITEM_ID = 'item.gongde'

/**
 * 每日签到独立高密度弹窗：
 * - 复用公共 Modal + ItemTile / ItemTooltipTarget / ItemIcon，适配深浅双主题与横竖屏自适应布局；
 * - 展示今日签到获得功德、当前持有功德、今日气运与本周气运（道劫签文）、连续与累计签到天数、本周/本月统计与跨日倒计时；
 * - 界面保持简洁清晰，不展示底层概率区间与数值公式细节。
 */
export function CheckinModal({ open, onClose }: CheckinModalProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const status = useCheckinStore(state => state.status)
  const summary = useCheckinStore(state => state.summary)
  const loading = useCheckinStore(state => state.loading)
  const claiming = useCheckinStore(state => state.claiming)
  const error = useCheckinStore(state => state.error)
  const lastClaimResult = useCheckinStore(state => state.lastClaimResult)
  const refreshStatus = useCheckinStore(state => state.refreshStatus)
  const claim = useCheckinStore(state => state.claim)

  const itemsCatalog = useGameStore(state => state.items)
  const inventory = useGameStore(state => state.inventory)
  const serverTimeUnixSeconds = useGameStore(state => state.serverTimeUnixSeconds)

  // 打开弹窗时拉取最新权威签到状态；若恰好跨日则同步校正。
  useEffect(() => {
    if (!open) return
    void refreshStatus()
  }, [open, refreshStatus])

  // 每秒更新距离明日 UTC+8 零点的倒计时（以最近一次服务端基准时间校准）。
  const [nowSeconds, setNowSeconds] = useState(() => Math.floor(Date.now() / 1000))
  useEffect(() => {
    if (!open) return undefined
    const baseClientMs = Date.now()
    const baseServerSec = serverTimeUnixSeconds > 0 ? serverTimeUnixSeconds : Math.floor(baseClientMs / 1000)
    const syncNow = (): void => {
      const elapsedSec = (Date.now() - baseClientMs) / 1000
      setNowSeconds(Math.floor(baseServerSec + elapsedSec))
    }
    syncNow()
    const timer = window.setInterval(syncNow, 1000)
    return () => window.clearInterval(timer)
  }, [open, serverTimeUnixSeconds])

  const rewardDef = useMemo(
    () => findItemInCatalog(itemsCatalog, CHECKIN_REWARD_ITEM_ID) ?? null,
    [itemsCatalog],
  )
  const rewardMeta = useMemo(
    () => resolveCatalogItemMetadata(CHECKIN_REWARD_ITEM_ID, { itemDefinition: rewardDef, t }),
    [rewardDef, t],
  )

  const boundMeritOwned = useMemo(
    () => inventory.find(entry => entry.itemId === CHECKIN_REWARD_ITEM_ID)?.amount ?? 0,
    [inventory],
  )
  const normalMeritOwned = useMemo(
    () => inventory.find(entry => entry.itemId === NORMAL_MERIT_ITEM_ID)?.amount ?? 0,
    [inventory],
  )
  const totalMeritOwned = boundMeritOwned + normalMeritOwned

  if (!open) return null

  const canClaimToday = status?.canClaimToday ?? summary?.canClaimToday ?? false
  const todayDate = status?.todayDate ?? summary?.todayDate ?? '--'
  const streakDays = status?.streakDays ?? summary?.streakDays ?? 0
  const totalDays = status?.totalDays ?? summary?.totalDays ?? 0
  const lastRewardMerit = status?.lastRewardMerit ?? summary?.lastRewardMerit ?? null
  const todayRewardMerit = !canClaimToday
    ? (lastClaimResult?.rewardMerit ?? status?.todayRewardMerit ?? lastRewardMerit)
    : null
  const isFreshClaim = Boolean(lastClaimResult?.claimed && !canClaimToday)

  const todayFortuneTier = !canClaimToday ? normalizeCheckinFortuneTier(status?.todayFortuneTier) : null
  const weekFortuneTier = normalizeCheckinFortuneTier(status?.weekFortuneTier)
  const todayFortuneTone = resolveFortuneVisualTone(todayFortuneTier)
  const weekFortuneTone = resolveFortuneVisualTone(weekFortuneTier)

  const weekDays = status?.weekDays ?? 0
  const weekMerit = status?.weekMerit ?? 0
  const weekDaysMask = status?.weekDaysMask ?? 0
  const monthDays = status?.monthDays ?? 0
  const monthMerit = status?.monthMerit ?? 0

  const currentDowIndex = getChinaDayOfWeekIndexMonday(nowSeconds)
  const nextResetUnix = status?.nextResetUnixSeconds ?? getNextChinaResetUnixSeconds(nowSeconds)
  const countdownText = formatCountdownClock(nextResetUnix - nowSeconds)

  return (
    <Modal
      className="checkin-modal"
      labelledBy={titleId}
      onClose={onClose}
      open={open}
      size="medium"
    >
      <div className="checkin-modal-inner">
        <header className="checkin-header">
          <div className="checkin-header-brand">
            <span aria-hidden="true" className="checkin-title-icon">
              <Icon name="checkin" size={16} />
            </span>
            <h2 className="checkin-title" id={titleId}>{t('app.checkin.title')}</h2>
            <span className="checkin-date-pill">{t('app.checkin.dateBadge', { date: todayDate })}</span>
          </div>
          <span className={`checkin-status-pill ${canClaimToday ? 'is-claimable' : 'is-claimed'}`}>
            {canClaimToday ? t('app.checkin.statusClaimable') : t('app.checkin.statusClaimed')}
          </span>
        </header>

        {loading && !status ? (
          <div className="checkin-loading-box">{t('app.checkin.loadingText')}</div>
        ) : error && !status ? (
          <div className="checkin-error-box">
            <p>{error}</p>
            <button className="checkin-retry-btn" onClick={() => void refreshStatus()} type="button">
              {t('app.checkin.retryButton')}
            </button>
          </div>
        ) : (
          <div className="checkin-body-grid">
            {/* 左栏：功德赏赐主卡、即时持仓、领取操作/今日收获回执 */}
            <section className="checkin-stage-col">
              <div className="checkin-hero-card">
                <div className="checkin-hero-top">
                  <ItemTooltipTarget
                    className="checkin-hero-anchor"
                    definition={rewardDef ?? undefined}
                    itemId={CHECKIN_REWARD_ITEM_ID}
                    metadata={rewardMeta}
                  >
                    <ItemTile
                      category={rewardMeta.category}
                      className="checkin-hero-tile"
                      enhancementLevel={0}
                      itemId={CHECKIN_REWARD_ITEM_ID}
                      level={rewardMeta.level}
                      name={rewardMeta.label}
                      rarity={rewardMeta.rarity}
                    />
                  </ItemTooltipTarget>
                  <div className="checkin-hero-meta">
                    <span className="checkin-hero-name">{t('app.checkin.rewardTitle')}</span>
                    <p className="checkin-hero-sub">{t('app.checkin.rewardSub')}</p>
                  </div>
                </div>

                <div className="checkin-wallet-strip">
                  <div className="checkin-wallet-cell">
                    <span className="checkin-wallet-label">{t('app.checkin.walletBoundLabel')}</span>
                    <span className="checkin-wallet-val">
                      <ItemIcon className="checkin-inline-icon" itemId={CHECKIN_REWARD_ITEM_ID} />
                      <span>{formatNumber(boundMeritOwned, { maxDecimals: 0 })}</span>
                    </span>
                  </div>
                  <div className="checkin-wallet-divider" aria-hidden="true" />
                  <div className="checkin-wallet-cell">
                    <span className="checkin-wallet-label">{t('app.checkin.walletTotalLabel')}</span>
                    <span className="checkin-wallet-val">
                      <ItemIcon className="checkin-inline-icon" itemId={NORMAL_MERIT_ITEM_ID} />
                      <span>{formatNumber(totalMeritOwned, { maxDecimals: 0 })}</span>
                    </span>
                  </div>
                </div>
              </div>

              {canClaimToday ? (
                <div className="checkin-action-card is-ready">
                  <p className="checkin-action-hint">{t('app.checkin.streakPrompt')}</p>
                  <button
                    className="checkin-claim-btn"
                    disabled={claiming}
                    onClick={() => void claim()}
                    type="button"
                  >
                    <Icon name="checkin" size={16} />
                    <span>{claiming ? t('app.checkin.claimingButton') : t('app.checkin.claimButton')}</span>
                  </button>
                </div>
              ) : (
                <div className={`checkin-receipt-card${isFreshClaim ? ' is-fresh' : ''}`}>
                  <div className="checkin-receipt-head">
                    <span className="checkin-receipt-title">{t('app.checkin.todayObtainedLabel')}</span>
                    {isFreshClaim && (
                      <span className="checkin-receipt-badge">{t('app.checkin.receiptFreshBadge')}</span>
                    )}
                  </div>
                  {todayRewardMerit !== null && (
                    <div className="checkin-receipt-amount">
                      <ItemIcon className="checkin-receipt-icon" itemId={CHECKIN_REWARD_ITEM_ID} />
                      <strong>{t('app.checkin.receiptAmount', { amount: todayRewardMerit })}</strong>
                      {todayFortuneTier && (
                        <span className={`checkin-fortune-pill tone-${todayFortuneTone}`}>
                          {t(`app.checkin.fortune.${todayFortuneTier}`)}
                        </span>
                      )}
                    </div>
                  )}
                  <div className="checkin-receipt-foot">
                    <span className="checkin-reset-row">
                      <Icon name="hourglass" size={13} />
                      <span>{t('app.checkin.nextResetLabel')}</span>
                      <strong className="checkin-countdown">{countdownText}</strong>
                    </span>
                    <button className="checkin-claim-btn is-done" disabled type="button">
                      {t('app.checkin.claimedButton')}
                    </button>
                  </div>
                </div>
              )}
            </section>

            {/* 右栏：气运/连签 2×2 宫格 + 本周7日条 + 本月统计 + 极简提示 */}
            <section className="checkin-stats-col">
              <div className="checkin-metrics-grid">
                <div className="checkin-metric-cell">
                  <span className="checkin-metric-label">{t('app.checkin.metricTodayFortuneLabel')}</span>
                  <strong className={`checkin-metric-value fortune-${todayFortuneTone}`}>
                    {todayFortuneTier
                      ? t(`app.checkin.fortune.${todayFortuneTier}`)
                      : t('app.checkin.fortunePending')}
                  </strong>
                </div>
                <div className="checkin-metric-cell">
                  <span className="checkin-metric-label">{t('app.checkin.metricWeekFortuneLabel')}</span>
                  <strong className={`checkin-metric-value fortune-${weekFortuneTone}`}>
                    {weekFortuneTier
                      ? t(`app.checkin.fortune.${weekFortuneTier}`)
                      : t('app.checkin.fortuneNone')}
                  </strong>
                </div>
                <div className="checkin-metric-cell">
                  <span className="checkin-metric-label">{t('app.checkin.metricStreakLabel')}</span>
                  <strong className="checkin-metric-value is-gold">
                    {t('app.checkin.metricDaysUnit', { days: streakDays })}
                  </strong>
                  <span className="checkin-metric-sub">
                    {t('app.checkin.metricTotalSub', { days: totalDays })}
                  </span>
                </div>
                <div className="checkin-metric-cell">
                  <span className="checkin-metric-label">
                    {canClaimToday ? t('app.checkin.metricLastLabel') : t('app.checkin.metricTodayGainLabel')}
                  </span>
                  <strong className="checkin-metric-value">
                    {lastRewardMerit !== null
                      ? t('app.checkin.metricMeritUnit', { amount: lastRewardMerit })
                      : t('app.checkin.metricLastEmpty')}
                  </strong>
                </div>
              </div>

              <div className="checkin-period-card">
                <div className="checkin-period-head">
                  <span className="checkin-period-title">{t('app.checkin.weekTitle')}</span>
                  <span className="checkin-period-summary">
                    {t('app.checkin.periodSummary', {
                      days: weekDays,
                      merit: formatNumber(weekMerit, { maxDecimals: 0 }),
                    })}
                  </span>
                </div>
                <div className="checkin-week-strip">
                  {WEEKDAY_KEYS.map((dayKey, index) => {
                    const signed = (weekDaysMask & (1 << index)) !== 0
                    const isToday = index === currentDowIndex
                    return (
                      <div
                        className={`checkin-week-day${signed ? ' is-signed' : ''}${isToday ? ' is-today' : ''}`}
                        key={dayKey}
                      >
                        <span className="checkin-week-label">{t(`app.checkin.weekdays.${dayKey}`)}</span>
                        <span className="checkin-week-mark" aria-hidden="true">
                          {signed ? '✓' : isToday ? '·' : ''}
                        </span>
                      </div>
                    )
                  })}
                </div>
              </div>

              <div className="checkin-month-card">
                <span className="checkin-period-title">{t('app.checkin.monthTitle')}</span>
                <span className="checkin-period-summary">
                  {t('app.checkin.periodSummary', {
                    days: monthDays,
                    merit: formatNumber(monthMerit, { maxDecimals: 0 }),
                  })}
                </span>
              </div>

              <p className="checkin-simple-hint">{t('app.checkin.simpleHint')}</p>
            </section>
          </div>
        )}
      </div>
    </Modal>
  )
}
