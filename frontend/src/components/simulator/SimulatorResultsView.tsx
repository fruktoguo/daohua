import { memo } from 'react'
import { useT } from '../../i18n'
import type { SimulatorReport } from '../../game/simulator/simulatorTypes'
import { ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { resolveItemMetadata } from '../../game/ids'

interface SimulatorResultsViewProps {
  report: SimulatorReport | null
}

export const SimulatorResultsView = memo(function SimulatorResultsView({
  report,
}: SimulatorResultsViewProps): React.JSX.Element {
  const t = useT()

  if (!report) {
    return (
      <div className="sim-results-empty">
        <span className="sim-empty-icon">⚔️</span>
        <span className="sim-empty-text">{t('combat.simulator.noReportHint')}</span>
      </div>
    )
  }

  const { manaDeficit } = report

  // 诊断色彩
  const diagnosisToneClass =
    manaDeficit.diagnosis === 'sufficient'
      ? 'is-sufficient'
      : manaDeficit.diagnosis === 'mild'
        ? 'is-mild'
        : manaDeficit.diagnosis === 'moderate'
          ? 'is-moderate'
          : 'is-severe'

  return (
    <div className="sim-results-view">
      {/* 1. 战斗胜利波次与综合 KPI */}
      <section className="sim-card sim-card--overview">
        <div className="sim-card-header">
          <span className="sim-card-title">{t('combat.simulator.overviewHeading')}</span>
          <span className="sim-card-sub">{t('combat.simulator.simulatedSeconds', { s: report.totalSimulatedSeconds })}</span>
        </div>

        <div className="sim-kpi-grid">
          <div className="sim-kpi-block">
            <span className="sim-kpi-label">{t('combat.simulator.winRate')}</span>
            <span className={`sim-kpi-value ${report.winRate >= 0.8 ? 'text-emerald' : report.winRate >= 0.5 ? 'text-amber' : 'text-crimson'}`}>
              {(report.winRate * 100).toFixed(1)}%
            </span>
            <span className="sim-kpi-sub">
              {report.victories} {t('combat.simulator.wins')} / {report.defeats} {t('combat.simulator.losses')}
            </span>
          </div>

          <div className="sim-kpi-block">
            <span className="sim-kpi-label">{t('combat.simulator.maxWave')}</span>
            <span className="sim-kpi-value text-gold">Wave {report.maxWave}</span>
            <span className="sim-kpi-sub">
              {report.totalEncounters} {t('combat.simulator.encountersUnit')}
            </span>
          </div>

          <div className="sim-kpi-block">
            <span className="sim-kpi-label">{t('combat.simulator.totalKills')}</span>
            <span className="sim-kpi-value">{report.totalKills}</span>
            <span className="sim-kpi-sub">
              小妖 {report.normalKills} · 精英 {report.eliteKills} · 首领 {report.bossKills}
            </span>
          </div>

          <div className="sim-kpi-block">
            <span className="sim-kpi-label">{t('combat.simulator.activeRatio')}</span>
            <span className="sim-kpi-value">
              {report.totalSimulatedSeconds > 0
                ? ((report.combatActiveSeconds / report.totalSimulatedSeconds) * 100).toFixed(0)
                : 0}%
            </span>
            <span className="sim-kpi-sub">
              战斗 {report.combatActiveSeconds.toFixed(0)}s · 搜寻 {report.searchSeconds.toFixed(0)}s
            </span>
          </div>
        </div>
      </section>

      {/* 2. 灵力缺口深度分析（核心亮点） */}
      <section className="sim-card sim-card--mana">
        <div className="sim-card-header">
          <div className="sim-mana-header-left">
            <span className="sim-card-title">{t('combat.simulator.manaDeficitHeading')}</span>
            <span className={`sim-diagnosis-badge ${diagnosisToneClass}`}>
              {t(`combat.simulator.diag_${manaDeficit.diagnosis}`)}
            </span>
          </div>
          <div className="sim-mana-ratio-highlight">
            <span className="sim-mana-ratio-label">{t('combat.simulator.deficitRatio')}:</span>
            <span className={`sim-mana-ratio-num ${diagnosisToneClass}`}>
              {(manaDeficit.overallDeficitRatio * 100).toFixed(1)}%
            </span>
          </div>
        </div>


        {/* 灵力池极值 */}
        <div className="sim-mana-pool-summary">
          <span className="sim-pool-item">
            {t('combat.simulator.avgMana')}: <strong>{manaDeficit.poolStats.averageMana.toFixed(1)}</strong>
          </span>
          <span className="sim-pool-item">
            {t('combat.simulator.minMana')}: <strong>{manaDeficit.poolStats.minMana.toFixed(0)}</strong>
          </span>
          <span className="sim-pool-item">
            {t('combat.simulator.emptyDuration')}: <strong>{manaDeficit.poolStats.emptyDurationSeconds.toFixed(1)}s</strong>
          </span>
          <span className="sim-pool-item">
            {t('combat.simulator.totalDeficitTime')}: <strong>{manaDeficit.overallDeficitSeconds.toFixed(1)}s</strong>
          </span>
        </div>

        {/* 各技能卡蓝明细表格 */}
        <div className="sim-skill-deficit-table-wrap">
          <table className="sim-skill-deficit-table">
            <thead>
              <tr>
                <th>{t('combat.simulator.thSkill')}</th>
                <th>{t('combat.simulator.thCasts')}</th>
                <th>{t('combat.simulator.thManaSpent')}</th>
                <th>{t('combat.simulator.thDeficitCount')}</th>
                <th>{t('combat.simulator.thWaitSeconds')}</th>
                <th>{t('combat.simulator.thDeficitRatio')}</th>
              </tr>
            </thead>
            <tbody>
              {manaDeficit.skillStats.map(stat => (
                <tr key={stat.slotIndex}>
                  <td className="sim-skill-name-col">
                    <span className="sim-slot-badge">#{stat.slotIndex}</span>
                    <span>{stat.abilityName}</span>
                  </td>
                  <td>{stat.castCount}</td>
                  <td>{stat.totalManaSpent}</td>
                  <td className={stat.deficitCount > 0 ? 'text-amber font-bold' : ''}>
                    {stat.deficitCount}
                  </td>
                  <td>{stat.totalDeficitSeconds.toFixed(1)}s</td>
                  <td>
                    <div className="sim-table-progress-cell">
                      <span>{(stat.deficitRatio * 100).toFixed(1)}%</span>
                      <div className="sim-mini-bar-bg">
                        <div
                          className="sim-mini-bar-fill"
                          style={{ width: `${Math.min(100, stat.deficitRatio * 100)}%` }}
                        />
                      </div>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {/* 智能调优建议 */}
        {manaDeficit.suggestions.length > 0 && (
          <div className="sim-suggestions-box">
            <div className="sim-suggestions-title">{t('combat.simulator.suggestionsTitle')}</div>
            <ul className="sim-suggestions-list">
              {manaDeficit.suggestions.map((suggestion, index) => (
                <li key={index}>{suggestion}</li>
              ))}
            </ul>
          </div>
        )}
      </section>

      {/* 3. 战斗收益（经验与物品掉落） */}
      <section className="sim-card sim-card--yields">
        <div className="sim-card-header">
          <span className="sim-card-title">{t('combat.simulator.yieldsHeading')}</span>
          <div className="sim-exp-rate-tag">
            <span>{t('combat.simulator.expPerHour')}:</span>
            <strong className="text-gold">+{report.experiencePerHour.toLocaleString()} /hr</strong>
            <span className="sim-total-exp">({t('combat.simulator.totalExp')}: {report.totalExperience.toLocaleString()})</span>
          </div>
        </div>

        <div className="sim-items-yield-container">
          {report.itemYields.length === 0 ? (
            <div className="sim-no-drops">{t('combat.simulator.noDropsHint')}</div>
          ) : (
            <div className="sim-items-grid">
              {report.itemYields.map(item => {
                const meta = resolveItemMetadata(item.itemId)
                return (
                  <div key={item.itemId} className="sim-item-tile-wrapper">
                    <ItemTooltipTarget itemId={item.itemId}>
                      <ItemTile
                        itemId={item.itemId}
                        name={item.name}
                        amount={item.quantity}
                        category={meta?.category ?? ''}
                        rarity={meta?.rarity ?? 'mundane'}
                        className="sim-item-tile"
                      />
                    </ItemTooltipTarget>
                    <div className="sim-item-rate-badge">
                      +{item.perHour}/hr
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </div>
      </section>
    </div>
  )
})
