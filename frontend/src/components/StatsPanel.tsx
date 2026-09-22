import { memo, useEffect, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { formatRemainingSeconds } from '../game/format'
import { useT } from '../i18n'
import { ItemReference } from './ItemDisplay'
import { ReportBlock } from './reportSections'

/** 斗法暂存区到期窗口（秒）：与服务端 StagingWindowSeconds=1200 对齐 */
const STAGED_WINDOW_SECONDS = 1200

/**
 * 行动统计面板 (StatsPanel)
 *
 * 仅展示本次行动相关内容：
 * 1. 当前行动：队首行动账本（duel.currentReport），头行为行动名 + 「进行中」徽标，
 *    含「获取经验 / 物品得失 / 行动信息」三小节；
 * 2. 斗法暂存区（队首行动勾选斗法且有暂存物品时展示，含逐项到期结算倒计时）；
 * 3. 皆无内容时显示「暂无统计」空态。
 * 离线挂机 / 今日统计 / 历史战报由左侧「记录」按钮的 RecordsPanel 承载。
 */
export const StatsPanel = memo(function StatsPanel(): React.JSX.Element {
  const t = useT()
  const duel = useGameStore(state => state.duel)
  const headEntry = useGameStore(state => state.queue.entries[0])
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  // 暂存区仅在「队首为勾选斗法的战斗行动」且存在暂存物品时展示。
  const pvpArmed = Boolean(headEntry && headEntry.executionKind === 'combat-encounter' && headEntry.pvpEnabled)
  const stagedItems = duel.stagedItems
  const showStaged = pvpArmed && stagedItems.length > 0

  const current = duel.currentReport

  // 进行中时长与暂存倒计时需要每秒刷新：仅存在动态内容时启用 tick，
  // 面板关闭时组件随 Modal 一起卸载，定时器自动清理。
  const needsTick = showStaged || Boolean(current)
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!needsTick) return undefined
    const timer = window.setInterval(() => setTick(value => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [needsTick])

  // 以「快照服务端时刻 + 距上次同步的本地流逝」估算当前服务端时间：倒计时以服务端时钟为准，避免本地时钟偏差。
  const estimatedServerNow = serverTime + Math.max(0, performance.now() - lastSyncPerformanceTime) / 1000

  const isEmpty = !current && !showStaged

  return (
    <section aria-label={t('combat.report.title')} className="battle-report-panel">
      <header className="battle-report-header">
        <span className="battle-report-title" id="battle-report-panel-title">{t('combat.report.title')}</span>
      </header>
      {isEmpty ? (
        <p className="battle-report-empty">{t('combat.report.empty')}</p>
      ) : (
        <div className="battle-report-body">
          {current && (
            <ReportBlock
              badge={t('combat.report.ongoing')}
              className="battle-report-block--current"
              nowSeconds={estimatedServerNow}
              report={current}
              title={current.actionName || current.actionId || t('combat.report.currentSection')}
            />
          )}
          {showStaged && (
            <div className="battle-report-staged">
              <span className="battle-report-staged-title">{t('combat.report.stagedTitle')}</span>
              <div className="battle-report-staged-list">
                {stagedItems.map(item => {
                  const remaining = Math.max(0, item.acquiredAtUnixSeconds + STAGED_WINDOW_SECONDS - estimatedServerNow)
                  return (
                    <span className="battle-report-staged-item" key={`${item.itemId}:${item.acquiredAtUnixSeconds}`}>
                      <ItemReference amount={item.quantity} itemId={item.itemId} />
                      <em>{t('combat.report.stagedRemaining', { time: formatRemainingSeconds(remaining) })}</em>
                    </span>
                  )
                })}
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  )
})
