import { memo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import { UnitNumber } from './UnitNumber'
import type { BattleReportCombatant } from '../game/types'

/**
 * 战斗统计面板 (CombatStatsPanel)
 *
 * 由战斗详情页头部「战斗统计」按钮打开的独立弹窗：
 * 展示当前行动账本的单位级战斗表现（duel.currentReport.combatants）。
 * 交互结构：「我方/敌方」阵营页签 × 「输出/承伤/减免/治疗」指标页签；
 * 条形图按所选阵营+指标把全部单位降序排列做横向对比——
 *   输出/承伤/减免 按物理（红）·术法（蓝）分段，治疗按自动回复（翠）·其他治疗（紫）分段；
 * 条宽按该组合全场最大值归一化；同类怪物已由服务端折叠为单个 monster 单位。
 * 进行中行动才有统计，行动归档后数据即弃（空列表 → 空态）。
 */

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

export const CombatStatsPanel = memo(function CombatStatsPanel(): React.JSX.Element {
  const t = useT()
  const current = useGameStore(state => state.duel.currentReport)
  const [side, setSide] = useState<SideKey>('ally')
  const [metric, setMetric] = useState<MetricKey>('dealt')

  // 只渲染确有结算的单位行（全零注册行不占位）；敌我分侧，不混排。
  const combatants = (current?.combatants ?? []).filter(unit =>
    unit.damageDealtPhysical > 0 || unit.damageDealtSpell > 0
    || unit.damageTakenPhysical > 0 || unit.damageTakenSpell > 0
    || unit.damageMitigatedPhysical > 0 || unit.damageMitigatedSpell > 0
    || unit.healingRegen > 0 || unit.healingOther > 0)

  const sideUnits = combatants.filter(unit => side === 'enemy' ? unit.kind === 'monster' : unit.kind !== 'monster')
  const rows = sideUnits
    .map(unit => ({ unit, first: metricPair(unit, metric)[0], second: metricPair(unit, metric)[1] }))
    .filter(row => row.first + row.second > 0)
    .sort((left, right) => (right.first + right.second) - (left.first + left.second))
  const maxTotal = rows.reduce((acc, row) => Math.max(acc, row.first + row.second), 0)

  // 图例与分段语义随指标切换：伤害系三指标为物理/术法，治疗为自动回复/其他。
  const isHealing = metric === 'healing'
  const firstLabel = isHealing ? t('combat.report.combatantRegenTag') : t('combat.encounter.damageCategory.physical')
  const secondLabel = isHealing ? t('combat.report.combatantOtherTag') : t('combat.encounter.damageCategory.spell')
  const firstSeg = isHealing ? 'combat-stats-seg--regen' : 'combat-stats-seg--physical'
  const secondSeg = isHealing ? 'combat-stats-seg--other' : 'combat-stats-seg--spell'

  return (
    <section aria-label={t('combat.stats.title')} className="battle-report-panel combat-stats-panel">
      <header className="battle-report-header">
        <span className="battle-report-title" id="combat-stats-panel-title">{t('combat.stats.title')}</span>
        {/* 敌我切换：本方（玩家+盟友）与敌方怪物分屏对比，不混排 */}
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
      </header>
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
              </span>
              <span className="combat-stats-value"><UnitNumber disableTooltip value={row.first + row.second} /></span>
              <span className="combat-stats-split">
                <em>{firstLabel} <UnitNumber disableTooltip value={row.first} /></em>
                <em>{secondLabel} <UnitNumber disableTooltip value={row.second} /></em>
              </span>
            </div>
          ))}
          <div className="combat-stats-legend" aria-hidden="true">
            <span><i className={`combat-stats-seg ${firstSeg}`} />{firstLabel}</span>
            <span><i className={`combat-stats-seg ${secondSeg}`} />{secondLabel}</span>
          </div>
        </div>
      )}
    </section>
  )
})
