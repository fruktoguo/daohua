import { memo, useEffect, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { loadBattleReportHistory } from '../game/battleReportStore'
import { useT } from '../i18n'
import { formatReportTime, reportHasContent, ReportBlock, ReportExpSection, ReportInfoSection, ReportItemsSection } from './reportSections'
import type { BattleReport } from '../game/types'

/** 历史战报展示上限 */
const HISTORY_LIMIT = 10

/** 离线挂机与每日统计单例账本的固定 reportId：归档历史与本地缓存中需剔除，避免与专属分区重复展示 */
const SINGLETON_REPORT_IDS: ReadonlySet<string> = new Set(['offline', 'daily'])

/**
 * 行动记录页面 (RecordsPanel)：主区顶级页签（'records'），由左侧栏「记录」按钮切换进入。
 *
 * 展示内容：
 * 1. 离线挂机：duel.offlineReport 单例账本（reportId 固定 "offline"，全零账本不渲染）；
 * 2. 今日统计：duel.dailyReport 单例账本（reportId 固定 "daily"，自然日重置，全零账本不渲染）；
 * 3. 近期战报历史（服务端 reports 合并 localStorage 本地缓存，按 reportId 去重、倒序取 10 条）；
 * 4. 皆无内容时显示空态。
 */
export const RecordsPanel = memo(function RecordsPanel(): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const duel = useGameStore(state => state.duel)
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  // 离线/每日账本仅在存在内容时渲染分区（全零账本抑制）。
  const offline = duel.offlineReport && reportHasContent(duel.offlineReport) ? duel.offlineReport : null
  const daily = duel.dailyReport && reportHasContent(duel.dailyReport) ? duel.dailyReport : null

  // 每日账本进行中时长需要每秒刷新；切离记录页时组件随页签一起卸载，定时器自动清理。
  const needsTick = Boolean(daily)
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!needsTick) return undefined
    const timer = window.setInterval(() => setTick(value => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [needsTick])

  // 以「快照服务端时刻 + 距上次同步的本地流逝」估算当前服务端时间：倒计时以服务端时钟为准，避免本地时钟偏差。
  const estimatedServerNow = serverTime + Math.max(0, performance.now() - lastSyncPerformanceTime) / 1000

  // 历史 = 服务端 reports ∪ 本地缓存，按 reportId 去重（新数据覆盖）、开始时间倒序、
  // 剔除 offline/daily 单例账本（两者由专属分区展示）。
  const history = useMemo(() => {
    const merged = new Map<string, BattleReport>()
    for (const report of loadBattleReportHistory(playerId)) merged.set(report.reportId, report)
    for (const report of duel.reports) merged.set(report.reportId, report)
    return [...merged.values()]
      .filter(report => !SINGLETON_REPORT_IDS.has(report.reportId))
      .sort((left, right) => right.startedAtUnixSeconds - left.startedAtUnixSeconds)
      .slice(0, HISTORY_LIMIT)
  }, [playerId, duel.reports])

  const isEmpty = !offline && !daily && history.length === 0

  return (
    <section aria-label={t('app.nav.records')} className="panel records-page">
      <header className="panel-header">
        <h2>{t('app.nav.records')}</h2>
      </header>
      {isEmpty ? (
        <p className="battle-report-empty">{t('combat.report.empty')}</p>
      ) : (
        <div className="battle-report-body">
          {offline && (
            <ReportBlock
              nowSeconds={estimatedServerNow}
              report={offline}
              title={t('combat.report.offlineSection')}
            />
          )}
          {daily && (
            <ReportBlock
              nowSeconds={estimatedServerNow}
              report={daily}
              title={t('combat.report.dailySection')}
            />
          )}
          {history.length > 0 && (
            <div className="battle-report-history-block">
              <span className="battle-report-history-title">{t('combat.report.historyTitle')}</span>
              <ul className="battle-report-history">
                {history.map(report => (
                  <li className="battle-report-row" key={report.reportId}>
                    <strong>{report.actionName || report.actionId}</strong>
                    <span className="battle-report-row-time">{formatReportTime(report.startedAtUnixSeconds)}</span>
                    <div className="battle-report-row-sections">
                      <ReportExpSection report={report} />
                      <ReportItemsSection report={report} />
                      <ReportInfoSection nowSeconds={estimatedServerNow} report={report} />
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </section>
  )
})
