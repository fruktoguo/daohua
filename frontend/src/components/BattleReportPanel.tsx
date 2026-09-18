import { memo, useEffect, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { loadBattleReportHistory } from '../game/battleReportStore'
import { formatRemainingSeconds } from '../game/format'
import { useT } from '../i18n'
import { ItemReference } from './ItemDisplay'
import { UnitNumber } from './UnitNumber'
import type { BattleReport, BattleReportRootExp } from '../game/types'

/** 斗法暂存区到期窗口（秒）：与服务端 StagingWindowSeconds=1200 对齐 */
const STAGED_WINDOW_SECONDS = 1200
/** 历史战报展示上限 */
const HISTORY_LIMIT = 10

/** 六维经验展示顺序与行标签（复用 combat.attr.* 既有词条） */
const ROOT_EXP_ROWS: ReadonlyArray<{ key: keyof BattleReportRootExp; labelKey: string }> = [
  { key: 'stamina', labelKey: 'combat.attr.stamina' },
  { key: 'intelligence', labelKey: 'combat.attr.meridian' },
  { key: 'attack', labelKey: 'combat.attr.attack' },
  { key: 'defense', labelKey: 'combat.attr.defense' },
  { key: 'swordsmanship', labelKey: 'combat.attr.divineSense' },
  { key: 'talisman', labelKey: 'combat.attr.movement' },
]

/** 战报开始时刻的 HH:MM 短格式（本地时区） */
function formatReportTime(unixSeconds: number): string {
  const date = new Date(unixSeconds * 1000)
  const hh = String(date.getHours()).padStart(2, '0')
  const mm = String(date.getMinutes()).padStart(2, '0')
  return `${hh}:${mm}`
}

/**
 * 单场战报的收益指标行（修为 / 底蕴 / 六维经验 / 物品 / 斗法战绩）
 */
const ReportMetrics = memo(function ReportMetrics({ report }: { report: BattleReport }): React.JSX.Element {
  const t = useT()
  const rootExpEntries = ROOT_EXP_ROWS
    .map(row => ({ key: row.key, label: t(row.labelKey), value: report.rootExp[row.key] }))
    .filter(entry => entry.value > 0)
  return (
    <div className="battle-report-metrics">
      <span>{t('combat.report.qi')} <strong><UnitNumber disableTooltip prefix="+" value={report.qi} /></strong></span>
      <span>{t('combat.report.experience')} <strong><UnitNumber disableTooltip prefix="+" value={report.experience} /></strong></span>
      {rootExpEntries.map(entry => (
        <span key={entry.key}>{entry.label} <strong><UnitNumber disableTooltip prefix="+" value={entry.value} /></strong></span>
      ))}
      {(report.duelWins > 0 || report.duelLosses > 0) && (
        <span className="battle-report-duel-record">{t('combat.report.duelRecord', { wins: report.duelWins, losses: report.duelLosses })}</span>
      )}
      {report.items.length > 0 && (
        <span className="battle-report-items">
          {report.items.map(item => (
            <ItemReference amount={item.quantity} itemId={item.itemId} key={item.itemId} />
          ))}
        </span>
      )}
    </div>
  )
})

/**
 * 战斗收益战报面板 (BattleReportPanel)
 *
 * 展示内容：
 * 1. 当前进行中战斗行动的收益累计（修为、底蕴、六维经验、物品、斗法胜负）；
 * 2. 斗法暂存区（队首行动勾选斗法且有暂存物品时展示，含逐项到期结算倒计时）；
 * 3. 近期战报历史（服务端 reports 合并 localStorage 本地缓存，按 reportId 去重、倒序取 10 条）；
 * 4. 三者皆空时显示「暂无战报」空态。
 */
export const BattleReportPanel = memo(function BattleReportPanel(): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const duel = useGameStore(state => state.duel)
  const headEntry = useGameStore(state => state.queue.entries[0])
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  // 暂存区仅在「队首为勾选斗法的战斗行动」且存在暂存物品时展示。
  const pvpArmed = Boolean(headEntry && headEntry.executionKind === 'combat-encounter' && headEntry.pvpEnabled)
  const stagedItems = duel.stagedItems
  const showStaged = pvpArmed && stagedItems.length > 0

  // 暂存区到期倒计时：每秒重渲染一次（仅展示期间启用）。
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!showStaged) return undefined
    const timer = window.setInterval(() => setTick(value => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [showStaged])

  // 以「快照服务端时刻 + 距上次同步的本地流逝」估算当前服务端时间：倒计时以服务端时钟为准，避免本地时钟偏差。
  const estimatedServerNow = serverTime + Math.max(0, performance.now() - lastSyncPerformanceTime) / 1000
  const current = duel.currentReport

  // 历史 = 服务端 reports ∪ 本地缓存，按 reportId 去重（新数据覆盖）、开始时间倒序、剔除当前进行中条目。
  const history = useMemo(() => {
    const merged = new Map<string, BattleReport>()
    for (const report of loadBattleReportHistory(playerId)) merged.set(report.reportId, report)
    for (const report of duel.reports) merged.set(report.reportId, report)
    return [...merged.values()]
      .filter(report => report.reportId !== current?.reportId)
      .sort((left, right) => right.startedAtUnixSeconds - left.startedAtUnixSeconds)
      .slice(0, HISTORY_LIMIT)
  }, [playerId, duel.reports, current?.reportId])

  const isEmpty = !current && !showStaged && history.length === 0

  return (
    <section aria-label={t('combat.report.title')} className="battle-report-panel">
      <header className="battle-report-header">
        <span className="battle-report-title">{t('combat.report.title')}</span>
      </header>
      {isEmpty ? (
        <p className="battle-report-empty">{t('combat.report.empty')}</p>
      ) : (
        <div className="battle-report-body">
          {current && (
            <div className="battle-report-current">
              <div className="battle-report-current-head">
                <strong>{current.actionName || current.actionId}</strong>
                <span className="badge">{t('combat.report.ongoing')}</span>
              </div>
              <ReportMetrics report={current} />
            </div>
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
          {history.length > 0 && (
            <div className="battle-report-history-block">
              <span className="battle-report-history-title">{t('combat.report.historyTitle')}</span>
              <ul className="battle-report-history">
                {history.map(report => (
                  <li className="battle-report-row" key={report.reportId}>
                    <strong>{report.actionName || report.actionId}</strong>
                    <span className="battle-report-row-time">{formatReportTime(report.startedAtUnixSeconds)}</span>
                    <ReportMetrics report={report} />
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
