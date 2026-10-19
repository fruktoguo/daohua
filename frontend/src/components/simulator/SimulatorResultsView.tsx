import { memo, useState } from 'react'
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
  const [activeTab, setActiveTab] = useState<'overview' | 'combat' | 'mana' | 'yields' | 'logs'>('overview')
  const [selectedLogIndex, setSelectedLogIndex] = useState<number>(0)
  const [damageTargetFilter, setDamageTargetFilter] = useState<string>('all')
  const [damageTakenAttackerFilter, setDamageTakenAttackerFilter] = useState<string>('all')

  if (!report) {
    return (
      <div className="sim-results-empty">
        <span className="sim-empty-icon">⚔️</span>
        <span className="sim-empty-text">{t('combat.simulator.noReportHint')}</span>
      </div>
    )
  }

  const { manaDeficit, recoveryStats, damageStats, damageTakenStats } = report

  // 诊断色彩
  const diagnosisToneClass =
    manaDeficit.diagnosis === 'sufficient'
      ? 'is-sufficient'
      : manaDeficit.diagnosis === 'mild'
        ? 'is-mild'
        : manaDeficit.diagnosis === 'moderate'
          ? 'is-moderate'
          : 'is-severe'

  const activeDamageSources = damageTargetFilter === 'all'
    ? damageStats.sources
    : damageStats.targetBreakdown.find(t => t.targetId === damageTargetFilter)?.sources || []

  const activeDamageTakenSources = damageTakenAttackerFilter === 'all'
    ? damageTakenStats.sources
    : damageTakenStats.attackerBreakdown.find(a => a.attackerId === damageTakenAttackerFilter)?.sources || []

  return (
    <div className="sim-results-view">
      {/* 结果主二级选项卡 */}
      <nav className="sim-nav-pills" aria-label="模拟器结果分类导航">
        <button
          type="button"
          className={`sim-nav-pill ${activeTab === 'overview' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('overview')}
        >
          📊 战况总览
        </button>
        <button
          type="button"
          className={`sim-nav-pill ${activeTab === 'combat' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('combat')}
        >
          ⚔️ 伤害与承伤
        </button>
        <button
          type="button"
          className={`sim-nav-pill ${activeTab === 'mana' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('mana')}
        >
          💧 灵力与恢复
        </button>
        <button
          type="button"
          className={`sim-nav-pill ${activeTab === 'yields' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('yields')}
        >
          🎁 战利品与修为
        </button>
        <button
          type="button"
          className={`sim-nav-pill ${activeTab === 'logs' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('logs')}
        >
          📜 战斗日志 ({report.recentBattleLogs.length})
        </button>
      </nav>

      {/* 1. 战况总览与妖兽击杀明细 */}
      {activeTab === 'overview' && (
        <div className="sim-tab-content">
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
                  {report.totalEncounters} 场 · {report.encountersPerHour}/hr
                </span>
              </div>

              <div className="sim-kpi-block">
                <span className="sim-kpi-label">{t('combat.simulator.totalKills')}</span>
                <span className="sim-kpi-value">{report.totalKills}</span>
                <span className="sim-kpi-sub">
                  小妖 {report.normalKills} · 精英 {report.eliteKills} · 妖王 {report.bossKills}
                </span>
              </div>

              <div className="sim-kpi-block">
                <span className="sim-kpi-label">战损频次</span>
                <span className={`sim-kpi-value ${report.playerDeaths === 0 ? 'text-emerald' : 'text-crimson'}`}>
                  {report.playerDeaths} 次
                </span>
                <span className="sim-kpi-sub">
                  每小时阵亡 {report.playerDeathsPerHour}/hr
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
                  交锋 {report.combatActiveSeconds.toFixed(0)}s · 搜寻 {report.searchSeconds.toFixed(0)}s
                </span>
              </div>
            </div>
          </section>

          {/* 妖兽击杀明细表 */}
          <section className="sim-card">
            <div className="sim-card-header">
              <span className="sim-card-title">出没妖兽击杀统计</span>
              <span className="sim-card-sub">共出没 {report.monsterKills.length} 种妖兽生灵</span>
            </div>
            <div className="sim-data-table-wrap">
              <table className="sim-data-table">
                <thead>
                  <tr>
                    <th>妖兽种类</th>
                    <th>位阶</th>
                    <th>击杀总数</th>
                    <th>每小时击杀</th>
                    <th>击杀占比</th>
                  </tr>
                </thead>
                <tbody>
                  {report.monsterKills.length === 0 ? (
                    <tr>
                      <td colSpan={5} className="text-center text-muted">暂无击杀记录</td>
                    </tr>
                  ) : (
                    report.monsterKills.map(m => {
                      const killRatio = report.totalKills > 0 ? ((m.count / report.totalKills) * 100).toFixed(1) : '0.0'
                      return (
                        <tr key={m.enemyId}>
                          <td className="font-medium text-main">{m.name}</td>
                          <td>
                            <span className={`sim-rank-tag rank-${m.rank}`}>
                              {m.rank === 'boss' ? '妖王' : m.rank === 'elite' ? '精英' : '妖兽'}
                            </span>
                          </td>
                          <td className="font-semibold">{m.count.toLocaleString()}</td>
                          <td className="text-emerald font-semibold">+{m.perHour}/hr</td>
                          <td>
                            <div className="sim-table-progress-cell">
                              <span className="sim-progress-text">{killRatio}%</span>
                              <div className="sim-progress-track">
                                <div className="sim-progress-bar" style={{ width: `${killRatio}%` }} />
                              </div>
                            </div>
                          </td>
                        </tr>
                      )
                    })
                  )}
                </tbody>
              </table>
            </div>
          </section>
        </div>
      )}

      {/* 2. 伤害与承伤深度剖析 */}
      {activeTab === 'combat' && (
        <div className="sim-tab-content">
          {/* 造成伤害部分 */}
          <section className="sim-card">
            <div className="sim-card-header">
              <div className="sim-header-with-badge">
                <span className="sim-card-title">造成伤害明细</span>
                <span className="sim-metric-pill">总伤: {damageStats.totalDamage.toLocaleString()}</span>
                <span className="sim-metric-pill text-gold">DPS: {damageStats.dps}</span>
                <span className="sim-metric-pill">命中率: {damageStats.overallHitRate}%</span>
                <span className="sim-metric-pill">暴击率: {damageStats.overallCriticalRate}%</span>
              </div>
              {damageStats.targetBreakdown.length > 1 && (
                <div className="sim-filter-group">
                  <span className="sim-filter-label">受击目标:</span>
                  <select
                    className="sim-filter-select"
                    value={damageTargetFilter}
                    onChange={e => setDamageTargetFilter(e.target.value)}
                  >
                    <option value="all">全量目标</option>
                    {damageStats.targetBreakdown.map(t => (
                      <option key={t.targetId} value={t.targetId}>
                        {t.targetName} ({t.percentage}%)
                      </option>
                    ))}
                  </select>
                </div>
              )}
            </div>

            <div className="sim-data-table-wrap">
              <table className="sim-data-table">
                <thead>
                  <tr>
                    <th>伤害来源</th>
                    <th>出手次数</th>
                    <th>命中率</th>
                    <th>暴击率</th>
                    <th>总伤害</th>
                    <th>DPS</th>
                    <th>伤害占比</th>
                  </tr>
                </thead>
                <tbody>
                  {activeDamageSources.length === 0 ? (
                    <tr>
                      <td colSpan={7} className="text-center text-muted">暂无造成伤害记录</td>
                    </tr>
                  ) : (
                    activeDamageSources.map(s => (
                      <tr key={s.sourceKey}>
                        <td className="font-medium text-main">{s.sourceName}</td>
                        <td>{s.totalHits.toLocaleString()}</td>
                        <td>{s.hitRate}%</td>
                        <td>{s.criticalRate}%</td>
                        <td className="font-semibold text-main">{s.totalDamage.toLocaleString()}</td>
                        <td className="font-semibold text-gold">{s.dps}</td>
                        <td>
                          <div className="sim-table-progress-cell">
                            <span className="sim-progress-text">{s.percentage}%</span>
                            <div className="sim-progress-track">
                              <div className="sim-progress-bar bar-damage" style={{ width: `${s.percentage}%` }} />
                            </div>
                          </div>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </section>

          {/* 承受伤害部分 */}
          <section className="sim-card">
            <div className="sim-card-header">
              <div className="sim-header-with-badge">
                <span className="sim-card-title">承受伤害明细</span>
                <span className="sim-metric-pill text-crimson">总承伤: {damageTakenStats.totalDamageTaken.toLocaleString()}</span>
                <span className="sim-metric-pill">承伤DPS: {damageTakenStats.dps}</span>
                <span className="sim-metric-pill">闪避率: {damageTakenStats.overallEvasionRate}%</span>
                <span className="sim-metric-pill">化解率: {damageTakenStats.overallParryRate}%</span>
              </div>
              {damageTakenStats.attackerBreakdown.length > 1 && (
                <div className="sim-filter-group">
                  <span className="sim-filter-label">攻击来源:</span>
                  <select
                    className="sim-filter-select"
                    value={damageTakenAttackerFilter}
                    onChange={e => setDamageTakenAttackerFilter(e.target.value)}
                  >
                    <option value="all">全量敌方</option>
                    {damageTakenStats.attackerBreakdown.map(a => (
                      <option key={a.attackerId} value={a.attackerId}>
                        {a.attackerName} ({a.percentage}%)
                      </option>
                    ))}
                  </select>
                </div>
              )}
            </div>

            <div className="sim-data-table-wrap">
              <table className="sim-data-table">
                <thead>
                  <tr>
                    <th>承伤来源</th>
                    <th>施放敌怪</th>
                    <th>受击次数</th>
                    <th>闪避次数</th>
                    <th>化解次数</th>
                    <th>总承伤</th>
                    <th>承伤DPS</th>
                    <th>承伤占比</th>
                  </tr>
                </thead>
                <tbody>
                  {activeDamageTakenSources.length === 0 ? (
                    <tr>
                      <td colSpan={8} className="text-center text-muted">暂无承受伤害记录</td>
                    </tr>
                  ) : (
                    activeDamageTakenSources.map(s => (
                      <tr key={`${s.sourceKey}_${s.attackerName}`}>
                        <td className="font-medium text-main">{s.sourceName}</td>
                        <td className="text-dim">{s.attackerName}</td>
                        <td>{s.totalHits.toLocaleString()}</td>
                        <td className="text-emerald">{s.evadedHits}</td>
                        <td className="text-indigo">{s.parriedHits}</td>
                        <td className="font-semibold text-crimson">{s.totalDamage.toLocaleString()}</td>
                        <td className="font-semibold">{s.dps}</td>
                        <td>
                          <div className="sim-table-progress-cell">
                            <span className="sim-progress-text">{s.percentage}%</span>
                            <div className="sim-progress-track">
                              <div className="sim-progress-bar bar-damage-taken" style={{ width: `${s.percentage}%` }} />
                            </div>
                          </div>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </section>
        </div>
      )}

      {/* 3. 灵力与恢复 */}
      {activeTab === 'mana' && (
        <div className="sim-tab-content">
          {/* 气血与灵力每秒恢复指标 */}
          <section className="sim-card">
            <div className="sim-card-header">
              <span className="sim-card-title">生命与灵力周期恢复</span>
              <div className="sim-header-status-group">
                <span className={`sim-metric-pill ${recoveryStats.isManaExhausted ? 'text-crimson' : 'text-emerald'}`}>
                  法力断竭: {recoveryStats.isManaExhausted ? '发生断蓝' : '未曾枯竭'}
                </span>
                <span className="sim-metric-pill">气血回复: +{recoveryStats.hpRegenPerSecond} HP/s</span>
                <span className="sim-metric-pill">灵力回复: +{recoveryStats.mpRegenPerSecond} MP/s</span>
              </div>
            </div>

            <div className="sim-two-column-tables">
              {/* 生命恢复明细 */}
              <div className="sim-column-table-box">
                <h4 className="sim-table-box-title">气血恢复来源 (+{recoveryStats.hpRegenPerSecond} HP/s)</h4>
                <table className="sim-data-table">
                  <thead>
                    <tr>
                      <th>恢复途径</th>
                      <th>累计恢复</th>
                      <th>速率 (HP/s)</th>
                      <th>恢复占比</th>
                    </tr>
                  </thead>
                  <tbody>
                    {recoveryStats.hpSources.length === 0 ? (
                      <tr><td colSpan={4} className="text-center text-muted">暂无气血恢复</td></tr>
                    ) : (
                      recoveryStats.hpSources.map(s => (
                        <tr key={s.sourceKey}>
                          <td className="font-medium text-main">{s.sourceName}</td>
                          <td>+{s.totalAmount.toLocaleString()}</td>
                          <td className="text-emerald font-semibold">+{s.ratePerSecond}</td>
                          <td>{s.percentage}%</td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>

              {/* 灵力恢复明细 */}
              <div className="sim-column-table-box">
                <h4 className="sim-table-box-title">灵力恢复来源 (+{recoveryStats.mpRegenPerSecond} MP/s)</h4>
                <table className="sim-data-table">
                  <thead>
                    <tr>
                      <th>恢复途径</th>
                      <th>累计恢复</th>
                      <th>速率 (MP/s)</th>
                      <th>恢复占比</th>
                    </tr>
                  </thead>
                  <tbody>
                    {recoveryStats.mpSources.length === 0 ? (
                      <tr><td colSpan={4} className="text-center text-muted">暂无灵力恢复</td></tr>
                    ) : (
                      recoveryStats.mpSources.map(s => (
                        <tr key={s.sourceKey}>
                          <td className="font-medium text-main">{s.sourceName}</td>
                          <td>+{s.totalAmount.toLocaleString()}</td>
                          <td className="text-indigo font-semibold">+{s.ratePerSecond}</td>
                          <td>{s.percentage}%</td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            </div>
          </section>

          {/* 技能法力消耗与每小时消耗速率 */}
          <section className="sim-card">
            <div className="sim-card-header">
              <span className="sim-card-title">技能法力消耗速率</span>
              <span className="sim-card-sub">每门神通的单次与持续法力开销</span>
            </div>
            <div className="sim-data-table-wrap">
              <table className="sim-data-table">
                <thead>
                  <tr>
                    <th>神通技能</th>
                    <th>施放次数</th>
                    <th>单次平均耗蓝</th>
                    <th>累计消耗法力</th>
                    <th>每小时法力消耗</th>
                  </tr>
                </thead>
                <tbody>
                  {report.manaConsumptionStats.length === 0 ? (
                    <tr><td colSpan={5} className="text-center text-muted">暂无技能法力消耗</td></tr>
                  ) : (
                    report.manaConsumptionStats.map(s => (
                      <tr key={s.abilityId}>
                        <td className="font-medium text-main">{s.abilityName}</td>
                        <td>{s.castCount.toLocaleString()}</td>
                        <td className="text-indigo">{s.averagePerCast} MP</td>
                        <td>{s.totalManaSpent.toLocaleString()}</td>
                        <td className="text-gold font-semibold">-{s.manaPerHour} /hr</td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </section>

          {/* 灵力缺口深度评估 */}
          <section className="sim-card sim-card--mana">
            <div className="sim-card-header">
              <div className="sim-mana-header-left">
                <span className="sim-card-title">灵力缺口与卡蓝等待</span>
                <span className={`sim-diagnosis-badge ${diagnosisToneClass}`}>
                  {t(`combat.simulator.diag_${manaDeficit.diagnosis}`)}
                </span>
              </div>
              <div className="sim-mana-ratio-highlight">
                <span className="sim-mana-ratio-label">缺蓝总占比:</span>
                <span className={`sim-mana-ratio-num ${diagnosisToneClass}`}>
                  {(manaDeficit.overallDeficitRatio * 100).toFixed(1)}%
                </span>
              </div>
            </div>

            <div className="sim-mana-pool-summary">
              <span className="sim-pool-item">
                平均灵力: <strong>{manaDeficit.poolStats.averageMana.toFixed(1)}</strong>
              </span>
              <span className="sim-pool-item">
                最低灵力: <strong>{manaDeficit.poolStats.minMana.toFixed(0)}</strong>
              </span>
              <span className="sim-pool-item">
                空蓝时长: <strong>{manaDeficit.poolStats.emptyDurationSeconds.toFixed(1)}s</strong>
              </span>
              <span className="sim-pool-item">
                卡蓝总等待: <strong>{manaDeficit.overallDeficitSeconds.toFixed(1)}s</strong>
              </span>
            </div>

            <div className="sim-skill-deficit-table-wrap">
              <table className="sim-skill-deficit-table">
                <thead>
                  <tr>
                    <th>槽位与神通</th>
                    <th>施放次数</th>
                    <th>消耗灵力</th>
                    <th>卡蓝次数</th>
                    <th>等蓝耗时</th>
                    <th>卡蓝占比</th>
                  </tr>
                </thead>
                <tbody>
                  {manaDeficit.skillStats.map(stat => (
                    <tr key={stat.slotIndex}>
                      <td className="sim-skill-name-col">
                        <span className="sim-slot-badge">#{stat.slotIndex}</span>
                        <span className="sim-skill-name-text">{stat.abilityName}</span>
                      </td>
                      <td>{stat.castCount}</td>
                      <td>{stat.totalManaSpent}</td>
                      <td className={stat.deficitCount > 0 ? 'text-amber' : 'text-dim'}>
                        {stat.deficitCount}
                      </td>
                      <td>{stat.totalDeficitSeconds.toFixed(1)}s</td>
                      <td>
                        <div className="sim-table-progress-cell">
                          <span className={`sim-progress-text ${stat.deficitRatio > 0.1 ? 'text-amber' : ''}`}>
                            {(stat.deficitRatio * 100).toFixed(1)}%
                          </span>
                          <div className="sim-progress-track">
                            <div
                              className="sim-progress-bar"
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
          </section>
        </div>
      )}

      {/* 4. 战利品与修为收益 */}
      {activeTab === 'yields' && (
        <div className="sim-tab-content">
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
                <div className="sim-items-cards-grid">
                  {report.itemYields.map(item => {
                    const meta = resolveItemMetadata(item.itemId)
                    return (
                      <div key={item.itemId} className="sim-item-row-card">
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
                        <div className="sim-item-card-details">
                          <div className="sim-item-full-name">{item.name}</div>
                          <div className="sim-item-stat-row">
                            <span className="sim-stat-sub">总量: <strong>{item.quantity.toLocaleString()}</strong></span>
                            <span className="sim-stat-rate text-emerald">+{item.perHour}/hr</span>
                          </div>
                        </div>
                      </div>
                    )
                  })}
                </div>
              )}
            </div>
          </section>
        </div>
      )}

      {/* 5. 战斗日志复盘 */}
      {activeTab === 'logs' && (
        <div className="sim-tab-content">
          <section className="sim-card">
            <div className="sim-card-header">
              <span className="sim-card-title">战斗详细日志复盘</span>
              <span className="sim-card-sub">留存最近与败北场次招式伤害流水</span>
            </div>

            {report.recentBattleLogs.length === 0 ? (
              <div className="sim-no-drops">本次模拟时长极短，未采样到完整交锋日志</div>
            ) : (
              <div className="sim-logs-container">
                {/* 场次列表 */}
                <div className="sim-logs-sidebar">
                  <div className="sim-logs-sidebar-title">对局索引 ({report.recentBattleLogs.length})</div>
                  <div className="sim-logs-list">
                    {report.recentBattleLogs.map((log, idx) => (
                      <button
                        key={idx}
                        type="button"
                        className={`sim-log-nav-item ${selectedLogIndex === idx ? 'is-active' : ''} ${log.outcome === 'defeat' ? 'is-defeat' : 'is-victory'}`}
                        onClick={() => setSelectedLogIndex(idx)}
                      >
                        <span className={`sim-log-outcome-indicator ${log.outcome === 'defeat' ? 'indicator-defeat' : 'indicator-victory'}`}>
                          {log.outcome === 'defeat' ? '败' : '胜'}
                        </span>
                        <div className="sim-log-nav-meta">
                          <span className="sim-log-nav-title">第 {log.battleIndex} 场 · W{log.waveNumber}</span>
                          <span className="sim-log-nav-time">{log.durationSeconds.toFixed(1)}s</span>
                        </div>
                      </button>
                    ))}
                  </div>
                </div>

                {/* 当前场次详细流水 */}
                <div className="sim-logs-content">
                  {report.recentBattleLogs[selectedLogIndex] ? (
                    <div className="sim-log-stream-wrap">
                      <div className="sim-log-stream-header">
                        <span className="sim-log-stream-title">
                          第 {report.recentBattleLogs[selectedLogIndex].battleIndex} 场实况 ({report.recentBattleLogs[selectedLogIndex].encounterName})
                        </span>
                        <span className={`sim-log-badge ${report.recentBattleLogs[selectedLogIndex].outcome === 'defeat' ? 'badge-defeat' : 'badge-victory'}`}>
                          {report.recentBattleLogs[selectedLogIndex].outcome === 'defeat' ? '讨伐败北' : '妖兽全灭'}
                        </span>
                      </div>
                      <div className="sim-log-stream-body">
                        {report.recentBattleLogs[selectedLogIndex].events.length === 0 ? (
                          <div className="sim-empty-log-line">无事件流水记录</div>
                        ) : (
                          report.recentBattleLogs[selectedLogIndex].events.map((ev, evIdx) => (
                            <div key={evIdx} className={`sim-log-line log-type-${ev.actionType}`}>
                              <span className="sim-log-time">{ev.timestamp.toFixed(1)}s</span>
                              <span className="sim-log-text">{ev.message}</span>
                            </div>
                          ))
                        )}
                      </div>
                    </div>
                  ) : (
                    <div className="sim-empty-log-line">请选择左侧对局查看详细日志</div>
                  )}
                </div>
              </div>
            )}
          </section>
        </div>
      )}
    </div>
  )
})
