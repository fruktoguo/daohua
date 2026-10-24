import { memo, useMemo } from 'react'
import { useGameStore } from '../game/gameStore'
import { formatNumber, formatRemainingSeconds } from '../game/format'
import { calculateBriefStats } from '../game/statsCalculations'
import { useT } from '../i18n'
import { ItemReference } from './ItemDisplay'
import type { BattleReport } from '../game/types'

export interface ReportBriefSectionProps {
  report: BattleReport
  nowSeconds: number
  title?: string
  badge?: string
  className?: string
}

/**
 * 行动简要统计小节 (ReportBriefSection)
 *
 * 根据当前行动账本自开始以来的真实流逝时间，进行速率与每日预期收益换算：
 * 1. 每日灵石收益估值：掉落物品按参考价折算为灵石，换算为「+xxx万 灵石/天」；
 * 2. 战斗行动：展示每小时战斗场次（xx.x 场/小时）与击杀速率；生活行动展示轮次速率；
 * 3. 经验与修为速率：分别展示每小时修为与每小时经验（及每日预计入账量）；
 * 4. 高价值产物估算：列出主要产出物品的预计日产量与日价值。
 */
export const ReportBriefSection = memo(function ReportBriefSection({
  report,
  nowSeconds,
  title,
  badge,
  className = '',
}: ReportBriefSectionProps): React.JSX.Element {
  const t = useT()
  const itemsCatalog = useGameStore(state => state.items)
  const marketStats = useGameStore(state => state.marketStats)
  const equipmentsCatalog = useGameStore(state => state.equipment)

  const {
    durationSeconds,
    isCombat,
    encountersPerHour,
    killsPerHour,
    deathsPerHour,
    cyclesPerHour,
    dailyRevenue,
    revenuePerHour,
    cultivationPerHour,
    dailyCultivation,
    combatExpPerHour,
    dailyCombatExp,
    rootExpPerHour,
    topYields,
  } = useMemo(() => {
    return calculateBriefStats(report, nowSeconds, {
      items: itemsCatalog,
      marketStats,
      equipments: equipmentsCatalog,
    })
  }, [report, nowSeconds, itemsCatalog, marketStats, equipmentsCatalog])

  return (
    <div className={`battle-report-block battle-report-brief ${className}`.trim()}>
      {(title || badge) && (
        <div className="battle-report-block-head">
          <div className="battle-report-brief-head-main">
            {title && <strong>{title}</strong>}
            {badge && <span className="badge">{badge}</span>}
          </div>
          <span className="battle-report-brief-duration">
            {t('combat.report.durationLabel', { time: formatRemainingSeconds(durationSeconds) })}
          </span>
        </div>
      )}

      {/* 四大核心速率指标瓦片 */}
      <div className="battle-report-kpi-grid">
        {/* KPI 1: 平均每日收益 */}
        <div className="battle-report-kpi-card battle-report-kpi-card--revenue">
          <span className="battle-report-kpi-label">{t('combat.report.kpiDailyRevenue')}</span>
          <span className="battle-report-kpi-value battle-report-kpi-value--gold">
            +{formatNumber(dailyRevenue)} {t('combat.report.spiritStone')}/{t('combat.report.perDayUnit')}
          </span>
          <span className="battle-report-kpi-sub">
            {t('combat.report.kpiHourlyRevenue', { amount: formatNumber(revenuePerHour) })}
          </span>
        </div>

        {/* KPI 2: 战斗频次或轮次效率 */}
        <div className="battle-report-kpi-card battle-report-kpi-card--combat">
          <span className="battle-report-kpi-label">
            {isCombat ? t('combat.report.kpiCombatFrequency') : t('combat.report.kpiCycleEfficiency')}
          </span>
          <span className="battle-report-kpi-value battle-report-kpi-value--crimson">
            {isCombat
              ? `${encountersPerHour.toFixed(1)} ${t('combat.report.encountersPerHour')}`
              : `${cyclesPerHour.toFixed(1)} ${t('combat.report.cyclesPerHour')}`}
          </span>
          <span className="battle-report-kpi-sub">
            {isCombat
              ? `${killsPerHour.toFixed(1)} ${t('combat.report.killsPerHour')}${deathsPerHour > 0 ? ` · ${deathsPerHour.toFixed(1)} ${t('combat.report.deathsPerHour')}` : ''}`
              : t('combat.report.totalCyclesDone', { n: report.cycles })}
          </span>
        </div>

        {/* KPI 3: 修为速率 */}
        <div className="battle-report-kpi-card battle-report-kpi-card--cultivation">
          <span className="battle-report-kpi-label">{t('combat.report.kpiCultivationRate')}</span>
          <span className="battle-report-kpi-value battle-report-kpi-value--cyan">
            +{formatNumber(cultivationPerHour)} /{t('combat.report.perHourUnit')}
          </span>
          <span className="battle-report-kpi-sub">
            {t('combat.report.kpiDailyExpEstimate', { amount: formatNumber(dailyCultivation) })}
          </span>
        </div>

        {/* KPI 4: 经验速率 */}
        <div className="battle-report-kpi-card battle-report-kpi-card--exp">
          <span className="battle-report-kpi-label">{t('combat.report.kpiCombatExpRate')}</span>
          <span className="battle-report-kpi-value battle-report-kpi-value--emerald">
            +{formatNumber(combatExpPerHour)} /{t('combat.report.perHourUnit')}
          </span>
          <span className="battle-report-kpi-sub">
            {t('combat.report.kpiDailyExpEstimate', { amount: formatNumber(dailyCombatExp) })}
            {rootExpPerHour > 0 && ` · ${t('combat.report.rootExpPerHour', { amount: formatNumber(rootExpPerHour) })}`}
          </span>
        </div>
      </div>

      {/* 主要产出日产量估算 */}
      <div className="battle-report-brief-drops">
        <div className="battle-report-brief-drops-title">
          <span>{t('combat.report.topDropsEstimatedTitle')}</span>
          {topYields.length > 0 && (
            <span className="battle-report-brief-drops-hint">{t('combat.report.topDropsEstimatedHint')}</span>
          )}
        </div>
        {topYields.length === 0 ? (
          <p className="battle-report-brief-drops-empty">{t('combat.report.noDropsYet')}</p>
        ) : (
          <div className="battle-report-brief-drops-list">
            {topYields.map(item => (
              <div className="battle-report-brief-drop-row" key={item.itemId}>
                <ItemReference amount={item.quantity} itemId={item.itemId} />
                <div className="battle-report-brief-drop-rates">
                  <span className="battle-report-brief-drop-daily-qty font-mono">
                    +{formatNumber(item.dailyQty)} /{t('combat.report.perDayUnit')}
                  </span>
                  {item.dailyVal > 0 && (
                    <span className="battle-report-brief-drop-daily-val font-mono">
                      ≈ +{formatNumber(item.dailyVal)} {t('combat.report.spiritStone')}/{t('combat.report.perDayUnit')}
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
})
