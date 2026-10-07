import { memo, useEffect, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { localizeActionDisplayText, resolveActionDefinition } from '../game/ids'
import { useT } from '../i18n'
import { ItemReference } from './ItemDisplay'
import { ReportBlock } from './reportSections'

/**
 * 行动统计面板 (StatsPanel)
 *
 * 仅展示本次行动相关内容：
 * 1. 当前行动：队首行动账本（duel.currentReport），头行为行动名 + 「进行中」徽标，
 *    含「获取经验 / 物品得失 / 行动信息」三小节；
 * 2. 斗法暂存区（队首行动勾选斗法且有暂存物品时展示；按物品聚合总量胶囊行）；
 * 3. 皆无内容时显示「暂无统计」空态。
 * 离线挂机 / 今日统计 / 历史战报由左侧「记录」按钮的 RecordsPanel 承载。
 */
export const StatsPanel = memo(function StatsPanel(): React.JSX.Element {
  const t = useT()
  const duel = useGameStore(state => state.duel)
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const headEntry = useGameStore(state => state.queue.entries[0])
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  // 暂存区仅在「队首为勾选斗法的战斗行动」且存在暂存物品时展示；
  // 服务端按物品+时间桶分多批下发，这里按 itemId 聚合总量成一行胶囊。
  const pvpArmed = Boolean(headEntry && headEntry.executionKind === 'combat-encounter' && headEntry.pvpEnabled)
  const stagedTotals = new Map<string, number>()
  for (const item of duel.stagedItems) {
    stagedTotals.set(item.itemId, (stagedTotals.get(item.itemId) ?? 0) + item.quantity)
  }
  const showStaged = pvpArmed && stagedTotals.size > 0

  const current = duel.currentReport

  // 「进行中」时长需要每秒刷新：仅当前账本存在时启用 tick，
  // 面板关闭时组件随 Modal 一起卸载，定时器自动清理。
  const needsTick = Boolean(current)
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
              title={localizeActionDisplayText(
                // 标题以 actionId 回查的目录定义名为准：存档 actionName 可能滞留旧地点的本地化文本，
                // 已不含地图名时无法二次替换；定义名回退存档名再回退通用标题。
                resolveActionDefinition(current.actionId)?.name
                  || current.actionName || current.actionId || t('combat.report.currentSection'), world, maps)}
            />
          )}
          {showStaged && (
            <div className="battle-report-staged">
              <span className="battle-report-staged-title">{t('combat.report.stagedTitle')}</span>
              <div className="battle-report-staged-list">
                {[...stagedTotals].map(([itemId, quantity]) => (
                  <ItemReference amount={quantity} itemId={itemId} key={itemId} />
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  )
})
