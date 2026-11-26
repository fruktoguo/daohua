import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { TRAIT_DEFINITIONS } from '../game/combatConstants'
import { formatDuelBattleRecord } from '../game/duelRecordFormatter'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { fetchPlayerDuelRecords } from '../network/apiClient'
import type { BattleReportCombatant, DuelBattleRecordDto } from '../game/types'
import { RichContentSegmentView } from './EntityReference'
import { UnitNumber } from './UnitNumber'

/**
 * 战斗统计面板 (CombatStatsPanel)
 *
 * 由战斗详情页头部「战斗统计」按钮打开的独立弹窗：
 * 1. 「数据统计」页签：展示当前行动账本的单位级战斗表现（duel.currentReport.combatants）；
 *    「我方/敌方」阵营页签 × 「输出/承伤/减免/治疗」指标页签。
 * 2. 「斗法记录」页签：按需从 `/api/v1/players/{id}/duel-records` 拉取当前行动内最近 20 场
 *    PVP 斗法的紧凑行动记录（单场上限 1000 次行动），并由前端结合本地配表动态拼装为带图元引用的行动流。
 */

type PanelTabKey = 'stats' | 'records'
type MetricKey = 'dealt' | 'taken' | 'mitigated' | 'healing'
type SideKey = 'ally' | 'enemy'

const METRIC_TABS: ReadonlyArray<{ id: MetricKey; labelKey: string }> = [
  { id: 'dealt', labelKey: 'combat.stats.dealt' },
  { id: 'taken', labelKey: 'combat.stats.taken' },
  { id: 'mitigated', labelKey: 'combat.stats.mitigated' },
  { id: 'healing', labelKey: 'combat.stats.healing' },
]

const SIDE_TABS: ReadonlyArray<{ id: SideKey; labelKey: string }> = [
  { id: 'ally', labelKey: 'combat.stats.sideAlly' },
  { id: 'enemy', labelKey: 'combat.stats.sideEnemy' },
]

/** 指标 → 单位的双段数值抽取：前三项为物理/术法，治疗为自动回复/其他。 */
function metricPair(unit: BattleReportCombatant, metric: MetricKey): [number, number] {
  switch (metric) {
    case 'dealt': return [unit.damageDealtPhysical, unit.damageDealtSpell]
    case 'taken': return [unit.damageTakenPhysical, unit.damageTakenSpell]
    case 'mitigated': return [unit.damageMitigatedPhysical, unit.damageMitigatedSpell]
    case 'healing': return [unit.healingRegen, unit.healingOther]
  }
}

function formatOutcomeLabel(record: DuelBattleRecordDto, t: ReturnType<typeof useT>): string {
  const base = (() => {
    switch (record.out) {
      case 0: return t('combat.stats.duelRecordOutcomeRunning')
      case 1: return t('combat.stats.duelRecordOutcomeWin')
      case 2: return t('combat.stats.duelRecordOutcomeLoss')
      default: return t('combat.stats.duelRecordOutcomeDraw')
    }
  })()
  return record.ff ? `${base}·${t('combat.stats.duelRecordOutcomeForfeit')}` : base
}

export const CombatStatsPanel = memo(function CombatStatsPanel(): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const current = useGameStore(state => state.duel.currentReport)
  const headPhase = useGameStore(state => state.queue.entries[0]?.expedition?.phase)
  const abilities = useGameStore(state => state.abilities)
  const items = useGameStore(state => state.items)
  const buffs = useGameStore(state => state.buffs)

  const [panelTab, setPanelTab] = useState<PanelTabKey>('stats')
  const [side, setSide] = useState<SideKey>('ally')
  const [metric, setMetric] = useState<MetricKey>('dealt')

  const [duelRecords, setDuelRecords] = useState<DuelBattleRecordDto[]>([])
  const [selectedDuelId, setSelectedDuelId] = useState<string>('')
  const [recordsLoading, setRecordsLoading] = useState(false)
  const [recordsError, setRecordsError] = useState(false)

  const currentReportId = current?.reportId ?? ''
  const duelRecordCount = current?.duelRecordCount ?? 0
  const isLiveDuel = headPhase === 'duel'
  const badgeCount = Math.max(duelRecordCount, duelRecords.length)

  useEffect(() => {
    setDuelRecords([])
    setSelectedDuelId('')
  }, [currentReportId])

  const loadDuelRecords = useCallback(async (signal?: AbortSignal) => {
    if (!playerId) return
    setRecordsLoading(true)
    setRecordsError(false)
    try {
      const res = await fetchPlayerDuelRecords(playerId, signal)
      if (signal?.aborted) return
      setDuelRecords(res.records)
      setSelectedDuelId(prev => (
        prev && res.records.some(r => r.id === prev) ? prev : (res.records[0]?.id ?? '')
      ))
    } catch {
      if (!signal?.aborted) setRecordsError(true)
    } finally {
      if (!signal?.aborted) setRecordsLoading(false)
    }
  }, [playerId])

  useEffect(() => {
    if (panelTab !== 'records' || !playerId) return undefined
    const controller = new AbortController()
    void loadDuelRecords(controller.signal)
    return () => controller.abort()
  }, [panelTab, playerId, currentReportId, duelRecordCount, loadDuelRecords])

  const selectedRecord = useMemo(() => {
    if (duelRecords.length === 0) return null
    return duelRecords.find(r => r.id === selectedDuelId) ?? duelRecords[0]
  }, [duelRecords, selectedDuelId])

  const formattedActions = useMemo(() => {
    if (!selectedRecord) return []
    return formatDuelBattleRecord(selectedRecord, { abilities, items, buffs, traits: TRAIT_DEFINITIONS }, t)
  }, [selectedRecord, abilities, items, buffs, t])

  // 只渲染确有结算的单位行（全零注册行不占位）；敌我分侧，不混排。
  const combatants = (current?.combatants ?? []).filter(unit =>
    unit.damageDealtPhysical > 0 || unit.damageDealtSpell > 0
    || unit.damageDealtTrue > 0 || unit.damageTakenTrue > 0
    || unit.damageTakenPhysical > 0 || unit.damageTakenSpell > 0
    || unit.damageMitigatedPhysical > 0 || unit.damageMitigatedSpell > 0
    || unit.healingRegen > 0 || unit.healingOther > 0)

  const sideUnits = combatants.filter(unit => side === 'enemy' ? unit.kind === 'monster' : unit.kind !== 'monster')
  const rows = sideUnits
    .map(unit => ({ unit, first: metricPair(unit, metric)[0], second: metricPair(unit, metric)[1], third: metric === 'dealt' ? unit.damageDealtTrue : metric === 'taken' ? unit.damageTakenTrue : 0 }))
    .filter(row => row.first + row.second + row.third > 0)
    .sort((left, right) => (right.first + right.second + right.third) - (left.first + left.second + left.third))
  const maxTotal = rows.reduce((acc, row) => Math.max(acc, row.first + row.second + row.third), 0)

  // 伤害指标先抽取物理/术法，输出与承伤另加真实伤害；治疗改为自动回复/其他。
  const isHealing = metric === 'healing'
  const firstLabel = isHealing ? t('combat.report.combatantRegenTag') : t('combat.encounter.damageCategory.physical')
  const secondLabel = isHealing ? t('combat.report.combatantOtherTag') : t('combat.encounter.damageCategory.spell')
  const firstSeg = isHealing ? 'combat-stats-seg--regen' : 'combat-stats-seg--physical'
  const secondSeg = isHealing ? 'combat-stats-seg--other' : 'combat-stats-seg--spell'

  return (
    <section aria-label={t('combat.stats.title')} className="battle-report-panel combat-stats-panel">
      <header className="battle-report-header">
        <div className="combat-stats-title-group">
          <span className="battle-report-title" id="combat-stats-panel-title">{t('combat.stats.title')}</span>
          <nav aria-label={t('combat.stats.tabAria')} className="expedition-tabs combat-stats-tabs" role="tablist">
            <button
              aria-selected={panelTab === 'stats'}
              className={panelTab === 'stats' ? 'active' : ''}
              onClick={() => setPanelTab('stats')}
              role="tab"
              type="button"
            >{t('combat.stats.tabStats')}</button>
            <button
              aria-selected={panelTab === 'records'}
              className={panelTab === 'records' ? 'active' : ''}
              onClick={() => setPanelTab('records')}
              role="tab"
              type="button"
            >
              {t('combat.stats.tabDuelRecords')}
              {(badgeCount > 0 || isLiveDuel) && (
                <em className="combat-stats-tab-badge">{isLiveDuel && badgeCount === 0 ? '•' : badgeCount}</em>
              )}
            </button>
          </nav>
        </div>

        {/* 敌我切换：仅数据统计页签显示 */}
        {panelTab === 'stats' && (
          <nav aria-label={t('combat.stats.sideAria')} className="expedition-tabs combat-stats-tabs" role="tablist">
            {SIDE_TABS.map(tab => (
              <button
                aria-selected={side === tab.id}
                className={side === tab.id ? 'active' : ''}
                key={tab.id}
                onClick={() => setSide(tab.id)}
                role="tab"
                type="button"
              >{t(tab.labelKey)}</button>
            ))}
          </nav>
        )}
      </header>

      {panelTab === 'stats' ? (
        <>
          <nav aria-label={t('combat.stats.metricAria')} className="expedition-tabs combat-stats-metric-tabs" role="tablist">
            {METRIC_TABS.map(tab => (
              <button
                aria-selected={metric === tab.id}
                className={metric === tab.id ? 'active' : ''}
                key={tab.id}
                onClick={() => setMetric(tab.id)}
                role="tab"
                type="button"
              >{t(tab.labelKey)}</button>
            ))}
          </nav>
          {rows.length === 0 ? (
            <p className="battle-report-empty">{t('combat.stats.empty')}</p>
          ) : (
            <div className="combat-stats-body">
              {rows.map((row, index) => (
                <div className="combat-stats-row" key={row.unit.unitKey}>
                  <span className="combat-stats-rank">{index + 1}</span>
                  <span className="combat-stats-name">
                    {row.unit.displayName || row.unit.unitKey}
                    {row.unit.kind === 'ally' && <em className="combat-stats-tag">{t('combat.report.combatantAllyTag')}</em>}
                    {row.unit.kind === 'monster' && <em className="combat-stats-tag">{t('combat.report.combatantMonsterTag')}</em>}
                  </span>
                  <span className="combat-stats-bar">
                    {row.first > 0 && <i className={`combat-stats-seg ${firstSeg}`} style={{ width: `${(row.first / maxTotal) * 100}%` }} />}
                    {row.second > 0 && <i className={`combat-stats-seg ${secondSeg}`} style={{ width: `${(row.second / maxTotal) * 100}%` }} />}
                    {row.third > 0 && <i className="combat-stats-seg combat-stats-seg--true" style={{ width: `${(row.third / maxTotal) * 100}%` }} />}
                  </span>
                  <span className="combat-stats-value"><UnitNumber disableTooltip value={row.first + row.second + row.third} /></span>
                  <span className="combat-stats-split">
                    <em>{firstLabel} <UnitNumber disableTooltip value={row.first} /></em>
                    <em>{secondLabel} <UnitNumber disableTooltip value={row.second} /></em>
                    {row.third > 0 && <em>{t('combat.encounter.damageCategory.true')} <UnitNumber disableTooltip value={row.third} /></em>}
                  </span>
                </div>
              ))}
              <div className="combat-stats-legend" aria-hidden="true">
                <span><i className={`combat-stats-seg ${firstSeg}`} />{firstLabel}</span>
                <span><i className={`combat-stats-seg ${secondSeg}`} />{secondLabel}</span>
                {(metric === 'dealt' || metric === 'taken') && <span><i className="combat-stats-seg combat-stats-seg--true" />{t('combat.encounter.damageCategory.true')}</span>}
              </div>
            </div>
          )}
        </>
      ) : (
        <div className="duel-records-view">
          <div className="duel-records-toolbar">
            {duelRecords.length > 0 ? (
              <select
                aria-label={t('combat.stats.duelRecordSelectAria')}
                className="filter-grade-select duel-records-select"
                onChange={event => setSelectedDuelId(event.target.value)}
                value={selectedRecord?.id ?? ''}
              >
                {duelRecords.map((rec, idx) => {
                  const opponentName = rec.u[1]?.n ?? rec.u[0]?.n ?? '-'
                  const outcomeLabel = formatOutcomeLabel(rec, t)
                  const countLabel = t('combat.stats.duelRecordActionCount', { count: rec.acts.length })
                  return (
                    <option key={rec.id} value={rec.id}>
                      {t('combat.stats.duelRecordMatchTitle', { index: idx + 1, opponent: opponentName })}
                      {` · ${outcomeLabel} · ${rec.dur.toFixed(1)}s · ${countLabel}`}
                    </option>
                  )
                })}
              </select>
            ) : (
              <span className="duel-records-placeholder" />
            )}
            <button
              className="ghost-button duel-records-refresh"
              disabled={recordsLoading}
              onClick={() => void loadDuelRecords()}
              type="button"
            >
              {t('combat.stats.duelRecordRefresh')}
            </button>
          </div>

          {recordsLoading && duelRecords.length === 0 ? (
            <p className="battle-report-empty">{t('combat.stats.duelRecordsLoading')}</p>
          ) : recordsError && duelRecords.length === 0 ? (
            <p className="battle-report-empty">
              {t('combat.stats.duelRecordsError')}{' '}
              <button className="ghost-button" onClick={() => void loadDuelRecords()} type="button">
                {t('app.mail.retry')}
              </button>
            </p>
          ) : !selectedRecord || formattedActions.length === 0 ? (
            <p className="battle-report-empty">{t('combat.stats.duelRecordsEmpty')}</p>
          ) : (
            <div className="duel-records-list-wrap">
              <ol className="duel-records-list">
                {formattedActions.map(item => (
                  <li className={`duel-record-row${item.isAllyActor ? ' is-ally' : ' is-enemy'}`} key={item.index}>
                    <span className="duel-record-time">[{item.atSeconds.toFixed(2)}s]</span>
                    <span className="duel-record-seq">#{item.index}</span>
                    <span className="duel-record-content">
                      {item.segments.map((seg, segIdx) => (
                        <RichContentSegmentView key={`${item.index}-${segIdx}`} segment={seg} />
                      ))}
                    </span>
                  </li>
                ))}
              </ol>
              {selectedRecord.tr && (
                <p className="duel-record-truncated">
                  {t('combat.stats.duelRecordTruncated', { limit: 1000 })}
                </p>
              )}
            </div>
          )}
        </div>
      )}
    </section>
  )
})
