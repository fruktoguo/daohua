import { memo, useMemo, useState } from 'react'
import { useT } from '../../i18n'
import type { MultiNodeSimulatorReport, SimulatorNodeComparisonItem, SimulatorReport } from '../../game/simulator/simulatorTypes'
import { ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { resolveItemMetadata } from '../../game/ids'

interface SimulatorResultsViewProps {
  multiReport?: MultiNodeSimulatorReport | null
  report?: SimulatorReport | null
  activeResultGroup?: 'comparison' | 'details'
  onChangeResultGroup?: (group: 'comparison' | 'details') => void
  activeNodeId?: string
  onChangeActiveNodeId?: (nodeId: string) => void
}

export const SimulatorResultsView = memo(function SimulatorResultsView({
  multiReport,
  report: singleReport,
  activeResultGroup: controlledGroup,
  onChangeResultGroup: setControlledGroup,
  activeNodeId: controlledNodeId,
  onChangeActiveNodeId: setControlledNodeId,
}: SimulatorResultsViewProps): React.JSX.Element {
  const t = useT()

  // 内部或外部受控的双组状态（简要对比 vs 详细剖析）
  const [internalGroup, setInternalGroup] = useState<'comparison' | 'details'>('comparison')
  const activeGroup = controlledGroup ?? internalGroup
  const setActiveGroup = setControlledGroup ?? setInternalGroup

  // 内部或外部受控的激活节点
  const [internalNodeId, setInternalNodeId] = useState<string>('')
  const activeNode = controlledNodeId || internalNodeId || multiReport?.selectedNodeId || multiReport?.comparison[0]?.nodeId || singleReport?.nodeId || ''
  const setActiveNode = setControlledNodeId ?? setInternalNodeId

  // 详细分析中的二级 Tab (战况 / 伤害 / 灵力 / 战利品 / 日志)
  const [detailSubTab, setDetailSubTab] = useState<'overview' | 'combat' | 'mana' | 'yields' | 'logs'>('overview')
  const [selectedLogIndex, setSelectedLogIndex] = useState<number>(0)


  // 规整当前需要展示的对比列表与当前选中节点的详细报告
  const comparisonList: SimulatorNodeComparisonItem[] = useMemo(() => {
    if (multiReport?.comparison && multiReport.comparison.length > 0) {
      return multiReport.comparison
    }
    if (singleReport) {
      return [{
        nodeId: singleReport.nodeId,
        nodeName: singleReport.nodeName,
        mapName: singleReport.mapName,
        encountersPerHour: singleReport.encountersPerHour,
        winRate: singleReport.winRate,
        dailyRevenue: singleReport.dailyDropsRevenue,
        dailyCultivationExp: singleReport.dailyCultivationExp,
        dailySixDimensionExp: singleReport.dailySixDimensionExp,
        killsPerHour: Number((singleReport.totalKills / (singleReport.totalSimulatedSeconds / 3600)).toFixed(1)),
        deathsPerHour: singleReport.playerDeathsPerHour,
        maxWave: singleReport.maxWave,
        report: singleReport,
      }]
    }
    return []
  }, [multiReport, singleReport])

  const currentReport: SimulatorReport | null = useMemo(() => {
    if (multiReport?.reports && activeNode && multiReport.reports[activeNode]) {
      return multiReport.reports[activeNode]
    }
    if (comparisonList.length > 0) {
      const found = comparisonList.find(c => c.nodeId === activeNode)
      if (found) return found.report
      return comparisonList[0].report
    }
    return singleReport ?? null
  }, [activeNode, comparisonList, multiReport, singleReport])

  // 计算多节点对比中的各项第一名
  const champions = useMemo(() => {
    if (comparisonList.length <= 1) return null
    let maxRevenueItem = comparisonList[0]
    let maxExpItem = comparisonList[0]
    let maxWinRateItem = comparisonList[0]
    let maxSixDimItem = comparisonList[0]

    for (const item of comparisonList) {
      if (item.dailyRevenue > maxRevenueItem.dailyRevenue) maxRevenueItem = item
      if (item.dailyCultivationExp > maxExpItem.dailyCultivationExp) maxExpItem = item
      if (item.winRate > maxWinRateItem.winRate) maxWinRateItem = item
      if (item.dailySixDimensionExp > maxSixDimItem.dailySixDimensionExp) maxSixDimItem = item
    }

    return {
      revenue: maxRevenueItem,
      exp: maxExpItem,
      winRate: maxWinRateItem,
      sixDim: maxSixDimItem,
    }
  }, [comparisonList])

  if (!currentReport && comparisonList.length === 0) {
    return (
      <div className="sim-results-empty">
        <span className="sim-empty-icon">⚔️</span>
        <span className="sim-empty-text">{t('combat.simulator.noReportHint')}</span>
      </div>
    )
  }

  const { manaDeficit, recoveryStats, damageStats, damageTakenStats } = currentReport!

  const activeDamageSources = damageStats.sources
  const activeDamageTakenSources = damageTakenStats.sources

  return (
    <div className="sim-results-view">
      {/* 顶级界面组切换：【简要信息对比】 vs 【节点详细剖析】 */}
      <div className="sim-result-group-switcher">
        <button
          type="button"
          className={`sim-group-tab ${activeGroup === 'comparison' ? 'is-active' : ''}`}
          onClick={() => setActiveGroup('comparison')}
        >
          🏆 简要信息对比 ({comparisonList.length} 个地点)
        </button>
        <button
          type="button"
          className={`sim-group-tab ${activeGroup === 'details' ? 'is-active' : ''}`}
          onClick={() => setActiveGroup('details')}
        >
          🔍 节点详细剖析 [{currentReport?.nodeName || '当前节点'}]
        </button>
      </div>

      {/* ======================= 第一组：简要信息对比 ======================= */}
      {activeGroup === 'comparison' && (
        <div className="sim-tab-content sim-comparison-group">
          {/* 榜首冠军高光卡片 */}
          {champions && (
            <div className="sim-champions-grid">
              <div className="sim-champ-card champ-revenue">
                <div className="sim-champ-badge">💰 收益之最</div>
                <div className="sim-champ-title">{champions.revenue.nodeName}</div>
                <div className="sim-champ-val text-gold">+{champions.revenue.dailyRevenue.toLocaleString()} 灵石/天</div>
                <div className="sim-champ-sub">[{champions.revenue.mapName}] · 胜率 {(champions.revenue.winRate * 100).toFixed(1)}%</div>
              </div>

              <div className="sim-champ-card champ-exp">
                <div className="sim-champ-badge">⚡ 修为最速</div>
                <div className="sim-champ-title">{champions.exp.nodeName}</div>
                <div className="sim-champ-val text-cyan">+{champions.exp.dailyCultivationExp.toLocaleString()} 修为/天</div>
                <div className="sim-champ-sub">[{champions.exp.mapName}] · 遇敌 {champions.exp.encountersPerHour}/hr</div>
              </div>

              <div className="sim-champ-card champ-sixdim">
                <div className="sim-champ-badge">🗡️ 六维之冠</div>
                <div className="sim-champ-title">{champions.sixDim.nodeName}</div>
                <div className="sim-champ-val text-emerald">+{champions.sixDim.dailySixDimensionExp.toLocaleString()} 六维/天</div>
                <div className="sim-champ-sub">[{champions.sixDim.mapName}]</div>
              </div>

              <div className="sim-champ-card champ-safe">
                <div className="sim-champ-badge">🛡️ 稳健挂机</div>
                <div className="sim-champ-title">{champions.winRate.nodeName}</div>
                <div className="sim-champ-val text-emerald">{(champions.winRate.winRate * 100).toFixed(1)}% 胜率</div>
                <div className="sim-champ-sub">阵亡: {champions.winRate.deathsPerHour}/hr · 收益: {champions.winRate.dailyRevenue.toLocaleString()}/天</div>
              </div>
            </div>
          )}

          {/* 简要横向对比大表格 */}
          <section className="sim-card sim-card--comparison">
            <div className="sim-card-header">
              <div className="sim-card-title-wrap">
                <span className="sim-card-title">挂机刷图收益与效率对比总览</span>
                <span className="sim-card-sub">掉落物统一按坊市市场参考价（未成交按系统回收价）折算每日灵石估值期望</span>
              </div>
            </div>

            <div className="sim-data-table-wrap">
              <table className="sim-data-table sim-comparison-table">
                <thead>
                  <tr>
                    <th>地点节点</th>
                    <th>所属区域</th>
                    <th>每小时遇敌</th>
                    <th>胜率</th>
                    <th className="th-highlight-gold">平均每日收益 (灵石/天)</th>
                    <th className="th-highlight-cyan">每日修为经验</th>
                    <th className="th-highlight-emerald">每日六维经验</th>
                    <th>击杀速率</th>
                    <th>阵亡频次</th>
                    <th>最高波次</th>
                    <th>操作</th>
                  </tr>
                </thead>
                <tbody>
                  {comparisonList.map(item => {
                    const isCurrent = item.nodeId === activeNode
                    const isTopRevenue = champions?.revenue.nodeId === item.nodeId
                    const isTopExp = champions?.exp.nodeId === item.nodeId

                    return (
                      <tr key={item.nodeId} className={isCurrent ? 'row-selected-node' : ''}>
                        <td className="font-semibold text-main">
                          <span className="sim-node-table-name">{item.nodeName}</span>
                          {isTopRevenue && <span className="sim-tag-champ champ-tag-rev">高收益</span>}
                          {isTopExp && <span className="sim-tag-champ champ-tag-exp">升级宝地</span>}
                        </td>
                        <td className="text-muted">[{item.mapName}]</td>
                        <td>{item.encountersPerHour} 次/hr</td>
                        <td>
                          <span className={item.winRate >= 0.8 ? 'text-emerald' : item.winRate >= 0.5 ? 'text-amber' : 'text-crimson'}>
                            {(item.winRate * 100).toFixed(1)}%
                          </span>
                        </td>
                        <td className="td-highlight-gold">
                          <strong className="text-gold font-mono">+{item.dailyRevenue.toLocaleString()}</strong> 灵石/天
                        </td>
                        <td className="td-highlight-cyan font-mono">
                          +{item.dailyCultivationExp.toLocaleString()} /天
                        </td>
                        <td className="td-highlight-emerald font-mono">
                          +{item.dailySixDimensionExp.toLocaleString()} /天
                        </td>
                        <td>{item.killsPerHour} 只/hr</td>
                        <td>
                          <span className={item.deathsPerHour > 0 ? 'text-crimson' : 'text-muted'}>
                            {item.deathsPerHour} 次/hr
                          </span>
                        </td>
                        <td>Wave {item.maxWave}</td>
                        <td>
                          <button
                            type="button"
                            className="sim-table-action-btn"
                            onClick={() => {
                              setActiveNode(item.nodeId)
                              setActiveGroup('details')
                            }}
                          >
                            查看详情 →
                          </button>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </section>
        </div>
      )}

      {/* ======================= 第二组：节点详细剖析 ======================= */}
      {activeGroup === 'details' && (
        <div className="sim-tab-content sim-details-group">
          {/* 节点选择胶囊 (在多节点下快速切换) */}
          {comparisonList.length > 1 && (
            <div className="sim-node-detail-picker">
              <span className="sim-picker-label">查看地点:</span>
              <div className="sim-picker-pills">
                {comparisonList.map(item => (
                  <button
                    key={item.nodeId}
                    type="button"
                    className={`sim-picker-pill ${item.nodeId === activeNode ? 'is-active' : ''}`}
                    onClick={() => setActiveNode(item.nodeId)}
                  >
                    <span>{item.nodeName}</span>
                    <span className="sim-pill-revenue">+{item.dailyRevenue.toLocaleString()} 灵石/天</span>
                  </button>
                ))}
              </div>
            </div>
          )}

          {/* 结果二级选项卡 */}
          <nav className="sim-nav-pills" aria-label="单节点详细分析导航">
            <button
              type="button"
              className={`sim-nav-pill ${detailSubTab === 'overview' ? 'is-active' : ''}`}
              onClick={() => setDetailSubTab('overview')}
            >
              📊 战况总览
            </button>
            <button
              type="button"
              className={`sim-nav-pill ${detailSubTab === 'combat' ? 'is-active' : ''}`}
              onClick={() => setDetailSubTab('combat')}
            >
              ⚔️ 伤害与承伤
            </button>
            <button
              type="button"
              className={`sim-nav-pill ${detailSubTab === 'mana' ? 'is-active' : ''}`}
              onClick={() => setDetailSubTab('mana')}
            >
              💧 灵力与恢复
            </button>
            <button
              type="button"
              className={`sim-nav-pill ${detailSubTab === 'yields' ? 'is-active' : ''}`}
              onClick={() => setDetailSubTab('yields')}
            >
              🎁 战利品与收益
            </button>
            <button
              type="button"
              className={`sim-nav-pill ${detailSubTab === 'logs' ? 'is-active' : ''}`}
              onClick={() => setDetailSubTab('logs')}
            >
              📜 战斗日志 ({currentReport?.recentBattleLogs.length || 0})
            </button>
          </nav>

          {/* 1. 战况总览 */}
          {detailSubTab === 'overview' && (
            <div className="sim-tab-content">
              <section className="sim-card sim-card--overview">
                <div className="sim-card-header">
                  <span className="sim-card-title">{currentReport?.nodeName} · 战况指标</span>
                  <span className="sim-card-sub">{t('combat.simulator.simulatedSeconds', { s: currentReport?.totalSimulatedSeconds || 0 })}</span>
                </div>

                <div className="sim-kpi-grid">
                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.winRate')}</span>
                    <span className={`sim-kpi-value ${currentReport!.winRate >= 0.8 ? 'text-emerald' : currentReport!.winRate >= 0.5 ? 'text-amber' : 'text-crimson'}`}>
                      {(currentReport!.winRate * 100).toFixed(1)}%
                    </span>
                    <span className="sim-kpi-sub">
                      {currentReport!.victories} {t('combat.simulator.wins')} / {currentReport!.defeats} {t('combat.simulator.losses')}
                    </span>
                  </div>

                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">每日掉落收益估值</span>
                    <span className="sim-kpi-value text-gold">+{currentReport!.dailyDropsRevenue.toLocaleString()}</span>
                    <span className="sim-kpi-sub">灵石/天 (按市场参考价)</span>
                  </div>

                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">每日修为产出</span>
                    <span className="sim-kpi-value text-cyan">+{currentReport!.dailyCultivationExp.toLocaleString()}</span>
                    <span className="sim-kpi-sub">+{currentReport!.experiencePerHour.toLocaleString()} /hr</span>
                  </div>

                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">每日六维根基</span>
                    <span className="sim-kpi-value text-emerald">+{currentReport!.dailySixDimensionExp.toLocaleString()}</span>
                    <span className="sim-kpi-sub">+{currentReport!.sixDimensionExperiencePerHour.toLocaleString()} /hr</span>
                  </div>

                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.totalKills')}</span>
                    <span className="sim-kpi-value">{currentReport!.totalKills}</span>
                    <span className="sim-kpi-sub">
                      普通 {currentReport!.normalKills} · 精英 {currentReport!.eliteKills} · 妖王 {currentReport!.bossKills}
                    </span>
                  </div>

                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.maxWave')}</span>
                    <span className="sim-kpi-value text-gold">Wave {currentReport!.maxWave}</span>
                    <span className="sim-kpi-sub">
                      {currentReport!.totalEncounters} 场 · {currentReport!.encountersPerHour}/hr
                    </span>
                  </div>
                </div>
              </section>

              {/* 妖兽击杀明细 */}
              <section className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">{t('combat.simulator.monsterKillsTitle')}</span>
                  <span className="sim-card-sub">本地点妖兽击杀频率与总数</span>
                </div>
                <div className="sim-data-table-wrap">
                  <table className="sim-data-table">
                    <thead>
                      <tr>
                        <th>妖兽名称</th>
                        <th>位阶层次</th>
                        <th>击杀总数</th>
                        <th>每小时击杀速率</th>
                      </tr>
                    </thead>
                    <tbody>
                      {currentReport!.monsterKills.length === 0 ? (
                        <tr><td colSpan={4} className="text-center text-muted">暂无妖兽击杀记录</td></tr>
                      ) : (
                        currentReport!.monsterKills.map(m => (
                          <tr key={m.enemyId}>
                            <td className="font-medium text-main">{m.name}</td>
                            <td>
                              <span className={`sim-rank-badge rank-${m.rank}`}>
                                {m.rank === 'boss' ? '妖王' : m.rank === 'elite' ? '精英' : '普通小妖'}
                              </span>
                            </td>
                            <td>{m.count} 只</td>
                            <td className="text-emerald">+{m.perHour} /hr</td>
                          </tr>
                        ))
                      )}
                    </tbody>
                  </table>
                </div>
              </section>
            </div>
          )}

          {/* 2. 伤害与承伤 */}
          {detailSubTab === 'combat' && (
            <div className="sim-tab-content">
              <section className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">{t('combat.simulator.damageTitle')}</span>
                  <div className="sim-header-status-group">
                    <span className="sim-metric-pill">DPS: {damageStats.dps}</span>
                    <span className="sim-metric-pill">命中率: {(damageStats.overallHitRate * 100).toFixed(1)}%</span>
                    <span className="sim-metric-pill">暴击率: {(damageStats.overallCriticalRate * 100).toFixed(1)}%</span>
                  </div>
                </div>
                <div className="sim-data-table-wrap">
                  <table className="sim-data-table">
                    <thead>
                      <tr>
                        <th>招式技能</th>
                        <th>总伤害</th>
                        <th>DPS</th>
                        <th>命中率</th>
                        <th>暴击率</th>
                        <th>伤害占比</th>
                      </tr>
                    </thead>
                    <tbody>
                      {activeDamageSources.length === 0 ? (
                        <tr><td colSpan={6} className="text-center text-muted">暂无造成伤害记录</td></tr>
                      ) : (
                        activeDamageSources.map(s => (
                          <tr key={s.sourceKey}>
                            <td className="font-medium text-main">{s.sourceName}</td>
                            <td>{s.totalDamage.toLocaleString()}</td>
                            <td className="text-emerald font-semibold">{s.dps}</td>
                            <td>{(s.hitRate * 100).toFixed(1)}%</td>
                            <td>{(s.criticalRate * 100).toFixed(1)}%</td>
                            <td>{s.percentage}%</td>
                          </tr>
                        ))
                      )}
                    </tbody>
                  </table>
                </div>
              </section>

              <section className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">{t('combat.simulator.damageTakenTitle')}</span>
                  <div className="sim-header-status-group">
                    <span className="sim-metric-pill">受创 DPS: {damageTakenStats.dps}</span>
                    <span className="sim-metric-pill">闪避率: {(damageTakenStats.overallEvasionRate * 100).toFixed(1)}%</span>
                    <span className="sim-metric-pill">化解率: {(damageTakenStats.overallParryRate * 100).toFixed(1)}%</span>
                  </div>
                </div>
                <div className="sim-data-table-wrap">
                  <table className="sim-data-table">
                    <thead>
                      <tr>
                        <th>受创来源</th>
                        <th>施暴敌怪</th>
                        <th>承受总伤害</th>
                        <th>受创 DPS</th>
                        <th>闪避率</th>
                        <th>化解率</th>
                        <th>受创占比</th>
                      </tr>
                    </thead>
                    <tbody>
                      {activeDamageTakenSources.length === 0 ? (
                        <tr><td colSpan={7} className="text-center text-muted">暂无承受伤害记录</td></tr>
                      ) : (
                        activeDamageTakenSources.map(s => (
                          <tr key={s.sourceKey}>
                            <td className="font-medium text-main">{s.sourceName}</td>
                            <td className="text-muted">{s.attackerName}</td>
                            <td>{s.totalDamage.toLocaleString()}</td>
                            <td className="text-crimson font-semibold">{s.dps}</td>
                            <td>{s.totalHits > 0 ? ((s.evadedHits / s.totalHits) * 100).toFixed(1) + '%' : '0%'}</td>
                            <td>{s.totalHits > 0 ? ((s.parriedHits / s.totalHits) * 100).toFixed(1) + '%' : '0%'}</td>
                            <td>{s.percentage}%</td>
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
          {detailSubTab === 'mana' && (
            <div className="sim-tab-content">
              {/* 灵力缺口核心诊断 */}
              <section className={`sim-card sim-card--mana ${manaDeficit.diagnosis === 'sufficient' ? 'is-sufficient' : 'is-deficit'}`}>
                <div className="sim-card-header">
                  <div className="sim-header-title-box">
                    <span className="sim-card-title">{t('combat.simulator.manaDeficitTitle')}</span>
                    <span className="sim-badge-diagnosis">
                      {manaDeficit.diagnosisText} (卡蓝时间比: {(manaDeficit.overallDeficitRatio * 100).toFixed(1)}%)
                    </span>
                  </div>
                </div>

                <div className="sim-mana-summary-box">
                  <div className="sim-kpi-grid">
                    <div className="sim-kpi-block">
                      <span className="sim-kpi-label">卡蓝总等待时长</span>
                      <span className="sim-kpi-value text-crimson">{manaDeficit.overallDeficitSeconds.toFixed(1)}s</span>
                      <span className="sim-kpi-sub">技能CD好但缺蓝无法施法</span>
                    </div>
                    <div className="sim-kpi-block">
                      <span className="sim-kpi-label">全场平均法力存量</span>
                      <span className="sim-kpi-value text-cyan">{manaDeficit.poolStats.averageMana.toFixed(1)} MP</span>
                      <span className="sim-kpi-sub">最低谷值: {manaDeficit.poolStats.minMana.toFixed(0)} MP</span>
                    </div>
                    <div className="sim-kpi-block">
                      <span className="sim-kpi-label">法力完全枯竭时长</span>
                      <span className="sim-kpi-value">{manaDeficit.poolStats.emptyDurationSeconds.toFixed(1)}s</span>
                      <span className="sim-kpi-sub">法力归零完全断蓝时长</span>
                    </div>
                  </div>

                  {manaDeficit.suggestions.length > 0 && (
                    <div className="sim-mana-suggestions-box">
                      <div className="sim-suggestion-header">💡 战斗调优建议:</div>
                      <ul className="sim-suggestion-list">
                        {manaDeficit.suggestions.map((sug, idx) => (
                          <li key={idx} className="sim-suggestion-item">{sug}</li>
                        ))}
                      </ul>
                    </div>
                  )}
                </div>
              </section>

              {/* 生命与灵力周期恢复 */}
              <section className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">生命与灵力周期恢复</span>
                  <div className="sim-header-status-group">
                    <span className="sim-metric-pill">气血回复: +{recoveryStats.hpRegenPerSecond} HP/s</span>
                    <span className="sim-metric-pill">灵力回复: +{recoveryStats.mpRegenPerSecond} MP/s</span>
                  </div>
                </div>

                <div className="sim-two-column-tables">
                  <div className="sim-column-table-box">
                    <h4 className="sim-table-box-title">气血恢复来源 (+{recoveryStats.hpRegenPerSecond} HP/s)</h4>
                    <table className="sim-data-table">
                      <thead>
                        <tr><th>恢复途径</th><th>累计恢复</th><th>速率 (HP/s)</th><th>占比</th></tr>
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

                  <div className="sim-column-table-box">
                    <h4 className="sim-table-box-title">灵力恢复来源 (+{recoveryStats.mpRegenPerSecond} MP/s)</h4>
                    <table className="sim-data-table">
                      <thead>
                        <tr><th>恢复途径</th><th>累计恢复</th><th>速率 (MP/s)</th><th>占比</th></tr>
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
            </div>
          )}

          {/* 4. 战利品与修为收益 */}
          {detailSubTab === 'yields' && (
            <div className="sim-tab-content">
              <section className="sim-card sim-card--yields">
                <div className="sim-card-header">
                  <span className="sim-card-title">{t('combat.simulator.yieldsHeading')}</span>
                  <div className="sim-exp-rate-tag">
                    <span className="text-gold font-semibold">平均每日掉落收益: +{currentReport!.dailyDropsRevenue.toLocaleString()} 灵石/天</span>
                    <span className="sim-total-exp">每日修为: +{currentReport!.dailyCultivationExp.toLocaleString()} /天</span>
                  </div>
                </div>

                <div className="sim-items-yield-container">
                  {currentReport!.itemYields.length === 0 ? (
                    <div className="sim-no-drops">{t('combat.simulator.noDropsHint')}</div>
                  ) : (
                    <div className="sim-data-table-wrap">
                      <table className="sim-data-table sim-yields-table">
                        <thead>
                          <tr>
                            <th>物品名称</th>
                            <th>总掉落数量</th>
                            <th>每小时速率</th>
                            <th>平均每天产出</th>
                            <th>市场参考单价</th>
                            <th>平均每日收益估值</th>
                          </tr>
                        </thead>
                        <tbody>
                          {currentReport!.itemYields.map(item => {
                            const meta = resolveItemMetadata(item.itemId)
                            return (
                              <tr key={item.itemId}>
                                <td className="font-medium text-main flex items-center gap-2">
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
                                  <span>{item.name}</span>
                                </td>
                                <td>{item.quantity.toLocaleString()}</td>
                                <td className="text-emerald">+{item.perHour} /hr</td>
                                <td className="font-semibold text-main">+{item.perDay.toLocaleString()} 件/天</td>
                                <td>
                                  {item.unitPrice > 0 ? (
                                    <span className="text-gold font-mono">{item.unitPrice.toLocaleString()} 灵石</span>
                                  ) : (
                                    <span className="text-muted">不可售</span>
                                  )}
                                </td>
                                <td>
                                  <strong className="text-gold font-mono">+{item.dailyRevenue.toLocaleString()}</strong> 灵石/天
                                </td>
                              </tr>
                            )
                          })}
                        </tbody>
                      </table>
                    </div>
                  )}
                </div>
              </section>
            </div>
          )}

          {/* 5. 战斗日志复盘 */}
          {detailSubTab === 'logs' && (
            <div className="sim-tab-content">
              <section className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">战斗详细日志复盘</span>
                  <span className="sim-card-sub">留存最近与败北场次招式伤害流水</span>
                </div>

                {currentReport!.recentBattleLogs.length === 0 ? (
                  <div className="sim-no-drops">本次模拟时长极短，未采样到完整交锋日志</div>
                ) : (
                  <div className="sim-logs-container">
                    <div className="sim-logs-sidebar">
                      <div className="sim-logs-sidebar-title">对局索引 ({currentReport!.recentBattleLogs.length})</div>
                      <div className="sim-logs-list">
                        {currentReport!.recentBattleLogs.map((log, idx) => (
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

                    <div className="sim-logs-content">
                      {currentReport!.recentBattleLogs[selectedLogIndex] ? (
                        <div className="sim-log-stream-wrap">
                          <div className="sim-log-stream-header">
                            <span className="sim-log-stream-title">
                              第 {currentReport!.recentBattleLogs[selectedLogIndex].battleIndex} 场实况 ({currentReport!.recentBattleLogs[selectedLogIndex].encounterName})
                            </span>
                            <span className={`sim-log-badge ${currentReport!.recentBattleLogs[selectedLogIndex].outcome === 'defeat' ? 'badge-defeat' : 'badge-victory'}`}>
                              {currentReport!.recentBattleLogs[selectedLogIndex].outcome === 'defeat' ? '讨伐败北' : '妖兽全灭'}
                            </span>
                          </div>
                          <div className="sim-log-stream-body">
                            {currentReport!.recentBattleLogs[selectedLogIndex].events.length === 0 ? (
                              <div className="sim-empty-log-line">无事件流水记录</div>
                            ) : (
                              currentReport!.recentBattleLogs[selectedLogIndex].events.map((ev, evIdx) => (
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
      )}
    </div>
  )
})
