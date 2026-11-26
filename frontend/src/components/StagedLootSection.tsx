import { memo, useEffect, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { formatRemainingSeconds } from '../game/format'
import { summarizeStagedLoot } from '../game/stagedLoot'
import { useT } from '../i18n'
import { FloatingTooltip } from './FloatingPopover'
import { ItemReference } from './ItemDisplay'

export type StagedLootSectionVariant = 'card' | 'inline'

/**
 * 斗法暂存区展示区块（StagedLootSection）：
 * 只要 duel.stagedItems 非空即持续渲染（不受当前队首是否仍勾选斗法限制），
 * 聚合展示全部待入袋物品、冻结状态与最早/全部自动入袋倒计时。
 * 复用于储物袋顶部、记录页顶部（variant="card"）及行动统计面板内（variant="inline"）。
 */
export const StagedLootSection = memo(function StagedLootSection({
  variant = 'card',
  className = '',
}: {
  variant?: StagedLootSectionVariant
  className?: string
}): React.JSX.Element | null {
  const t = useT()
  const stagedItems = useGameStore(state => state.duel.stagedItems)
  const activeDuelId = useGameStore(state => state.duel.activeDuelId)
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  const hasStaged = stagedItems.length > 0
  const isFrozen = Boolean(activeDuelId)

  // 仅当存在待入袋暂存物品且未处于斗法冻结态时启用每秒倒计时刷新，无暂存物时零开销。
  const needsTick = hasStaged && !isFrozen
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!needsTick) return undefined
    const timer = window.setInterval(() => setTick(value => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [needsTick])

  if (!hasStaged) return null

  const estimatedServerNow = serverTime + Math.max(0, performance.now() - lastSyncPerformanceTime) / 1000
  const summary = summarizeStagedLoot(stagedItems, estimatedServerNow)
  if (!summary) return null

  const badgeText = isFrozen
    ? t('combat.report.stagedBadgeFrozen')
    : t('combat.report.stagedBadgePending')

  let countdownText: string
  if (isFrozen) {
    countdownText = t('combat.report.stagedFrozenHint')
  } else if (summary.batchCount <= 1) {
    countdownText = summary.nextSettleRemainingSeconds > 0
      ? t('combat.report.stagedSettleSingle', {
          next: formatRemainingSeconds(summary.nextSettleRemainingSeconds),
        })
      : t('combat.report.stagedSettlingSoon')
  } else {
    const allText = formatRemainingSeconds(summary.allSettledRemainingSeconds)
    countdownText = summary.nextSettleRemainingSeconds > 0
      ? t('combat.report.stagedSettleMulti', {
          batches: summary.batchCount,
          next: formatRemainingSeconds(summary.nextSettleRemainingSeconds),
          all: allText,
        })
      : t('combat.report.stagedSettleMultiSoon', {
          batches: summary.batchCount,
          all: allText,
        })
  }

  const rootClassName = variant === 'card'
    ? `battle-report-staged staged-loot-card ${className}`.trim()
    : `battle-report-staged-group ${className}`.trim()

  return (
    <section aria-label={t('combat.report.stagedTitle')} className={rootClassName}>
      <div className="staged-loot-header">
        <div className="staged-loot-title-group">
          <FloatingTooltip placement="top" title={t('combat.report.stagedTooltip')} tone="purple">
            <span className="battle-report-staged-title staged-loot-title-hint">
              {t('combat.report.stagedTitle')}
            </span>
          </FloatingTooltip>
          <span className={`staged-loot-badge ${isFrozen ? 'is-frozen' : ''}`}>
            {badgeText}
          </span>
        </div>
        <span className="staged-loot-countdown">{countdownText}</span>
      </div>
      <div className="battle-report-staged-list">
        {summary.items.map(item => (
          <ItemReference amount={item.quantity} itemId={item.itemId} key={item.itemId} />
        ))}
      </div>
    </section>
  )
})
