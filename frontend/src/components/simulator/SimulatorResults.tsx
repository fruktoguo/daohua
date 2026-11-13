import { memo, useCallback, useMemo, useState } from 'react'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { marketStatsKey } from '../../network/apiClient'
import type {
  SimulatorEnvironmentDto, SimulatorMemberResultDto, SimulatorRunResultDto,
} from '../../game/simulator/simulatorTypes'
type SimulatorNodeEnvironmentDto = SimulatorEnvironmentDto['nodes'][number]
import {
  findCombatantInCatalog, resolveItemCategoryLabel, resolveItemMetadata, withItemGrade,
} from '../../game/ids'
import { ItemIcon, ItemReference, ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { LogTemplateText } from '../LogFeed'

interface Props {
  results: SimulatorRunResultDto[]
  nodeName: (id: string) => string
  nodeEnvMap?: ReadonlyMap<string, SimulatorNodeEnvironmentDto>
  runSeed: string
  startedAtUnixSeconds: number
}

type GroupView = 'comparison' | 'details'
type DetailTab = 'overview' | 'combat' | 'yields' | 'logs'

const LINGSHI = 'item.lingshi'
const ASSET_SEP = '\u001F'

function variantLevel(variant: string): number | null {
  if (!variant) return 0
  const raw = variant.startsWith('+') ? variant.slice(1) : variant
  if (!/^\d+$/.test(raw)) return null
  return Number(raw)
}

const ROOT_KEYS: readonly (keyof SimulatorMemberResultDto['rootExp'])[] =
  ['stamina', 'meridians', 'attack', 'bone', 'agility', 'consciousness']
const ROOT_LABEL_KEYS: Record<string, string> = {
  stamina: 'combat.simulator.core.stamina',
  meridians: 'combat.simulator.core.meridians',
  attack: 'combat.simulator.core.attack',
  bone: 'combat.simulator.core.bone',
  agility: 'combat.simulator.core.agility',
  consciousness: 'combat.simulator.core.consciousness',
}

function totalRootExp(member: SimulatorMemberResultDto): number {
  const r = member.rootExp
  return r.stamina + r.meridians + r.attack + r.bone + r.agility + r.consciousness
}

function computeMemberValuation(
  member: SimulatorMemberResultDto,
  marketStats: Record<string, { referencePrice?: number | null }> | undefined,
): { known: number; hasUnknown: boolean; unknownIds: string[] } {
  let known = 0
  let hasUnknown = false
  const unknownIds: string[] = []
  const totals = new Map<string, number>()
  for (const delta of member.assetDeltas) {
    const key = `${delta.itemId}${ASSET_SEP}${delta.variant}`
    totals.set(key, (totals.get(key) ?? 0) + delta.quantity)
  }
  for (const [key, quantity] of totals) {
    if (quantity === 0) continue
    const sepIndex = key.indexOf(ASSET_SEP)
    const itemId = sepIndex >= 0 ? key.slice(0, sepIndex) : key
    const variant = sepIndex >= 0 ? key.slice(sepIndex + 1) : ''
    if (itemId === LINGSHI) { known += quantity; continue }
    const level = variantLevel(variant)
    const ref = level === null ? undefined
      : marketStats?.[marketStatsKey(itemId, level)]?.referencePrice
    if (ref != null && Number.isFinite(ref) && ref > 0) { known += ref * quantity; continue }
    hasUnknown = true
    if (!unknownIds.includes(itemId)) unknownIds.push(itemId)
  }
  return { known, hasUnknown, unknownIds }
}

function summarizeMember(
  result: SimulatorRunResultDto,
  memberIndex: number,
  marketStats: Record<string, { referencePrice?: number | null }> | undefined,
) {
  const m = result.members[memberIndex] ?? result.members[0]
  const hours = result.simulatedSeconds > 0 ? result.simulatedSeconds / 3600 : 0
  const kills = m ? m.normalKills + m.eliteKills + m.bossKills : 0
  const exp = m?.lifetimeExperience ?? 0
  const sixDim = m ? totalRootExp(m) : 0
  const valuation = m ? computeMemberValuation(m, marketStats) : { known: 0, hasUnknown: false, unknownIds: [] }
  return {
    member: m,
    hours,
    killsPerHour: hours > 0 ? kills / hours : 0,
    expPerHour: hours > 0 ? exp / hours : 0,
    sixDimPerHour: hours > 0 ? sixDim / hours : 0,
    valuePerHour: hours > 0 ? valuation.known / hours : 0,
    deaths: m?.deaths ?? 0,
    encounters: m?.encounters ?? 0,
    valuation,
  }
}

export const SimulatorResults = memo(function SimulatorResults({
  results, nodeName, nodeEnvMap, runSeed, startedAtUnixSeconds,
}: Props) {
  const t = useT()
  const encounters = useGameStore(state => state.encounters)
  const maps = useGameStore(state => state.maps)
  const abilities = useGameStore(state => state.abilities)
  const items = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const marketStats = useGameStore(state => state.marketStats)

  const [groupView, setGroupView] = useState<GroupView>(results.length > 1 ? 'comparison' : 'details')
  const [selectedIndex, setSelectedIndex] = useState(0)
  const [selectedMemberIndex, setSelectedMemberIndex] = useState(0)
  const [detailTab, setDetailTab] = useState<DetailTab>('overview')

  const safeIndex = Math.min(selectedIndex, Math.max(0, results.length - 1))
  const current = results[safeIndex]
  const safeMemberIndex = current ? Math.min(selectedMemberIndex, Math.max(0, current.members.length - 1)) : 0
  const currentMember = current?.members[safeMemberIndex]

  const enemyName = useCallback((id: string) =>
    findCombatantInCatalog(encounters, maps, id)?.name ?? id, [encounters, maps])

  const abilityName = useCallback((id: string | null) =>
    id ? (abilities.find(a => a.abilityId === id)?.name ?? id) : '—', [abilities])

  const itemDisplayName = useCallback((id: string) =>
    items.find(i => i.itemId === id)?.name
    ?? equipmentCatalog.find(e => e.equipmentId === id)?.name
    ?? id, [equipmentCatalog, items])

  const diffLabel = useCallback((difficulty: number) =>
    difficulty === 0 ? t('combat.simulator.difficultyDefault') : `T${difficulty}`, [t])

  const buildingBadge = useCallback((nodeId: string) => {
    const env = nodeEnvMap?.get(nodeId)
    if (!env) return t('combat.simulator.noBuildingBonus')
    const parts: string[] = []
    if (env.effects.combatHitpointRegen) parts.push(t('combat.simulator.nodeHpRegen', { value: env.effects.combatHitpointRegen }))
    if (env.effects.combatManapointRegen) parts.push(t('combat.simulator.nodeMpRegen', { value: env.effects.combatManapointRegen }))
    if (env.effects.combatDamageReductionRatio) parts.push(`${t('combat.simulator.reduction')}${Math.round(env.effects.combatDamageReductionRatio * 100)}%`)
    if (env.effects.monsterWeakenRatio) parts.push(`${t('combat.simulator.weaken')}${Math.round(env.effects.monsterWeakenRatio * 100)}%`)
    for (const u of env.usages) {
      if (u.efficiency < 1) parts.push(t('combat.simulator.crowdedEff', { pct: Math.round(u.efficiency * 100) }))
    }
    return parts.length > 0 ? parts.join(' · ') : t('combat.simulator.noBuildingBonus')
  }, [nodeEnvMap, t])

  const rows = useMemo(() =>
    results.map((r, index) => ({
      index,
      result: r,
      summary: summarizeMember(r, 0, marketStats),
    })),
  [marketStats, results])

  const champions = useMemo(() => {
    if (rows.length === 0) return null
    let bestRev = rows[0]
    let bestExp = rows[0]
    let bestSix = rows[0]
    let bestSafe = rows[0]
    for (const row of rows) {
      if (row.summary.valuePerHour > bestRev.summary.valuePerHour) bestRev = row
      if (row.summary.expPerHour > bestExp.summary.expPerHour) bestExp = row
      if (row.summary.sixDimPerHour > bestSix.summary.sixDimPerHour) bestSix = row
      if (row.summary.deaths < bestSafe.summary.deaths
        || (row.summary.deaths === bestSafe.summary.deaths && row.summary.killsPerHour > bestSafe.summary.killsPerHour)) {
        bestSafe = row
      }
    }
    return { bestRev, bestExp, bestSix, bestSafe }
  }, [rows])

  if (results.length === 0 || !current) return null

  const currentHours = current.simulatedSeconds > 0 ? current.simulatedSeconds / 3600 : 0
  const maxDamage = currentMember
    ? Math.max(1, ...currentMember.combatStats.map(u => Math.max(
        u.damageDealtPhysical + u.damageDealtSpell + u.damageDealtTrue,
        u.damageTakenPhysical + u.damageTakenSpell + u.damageTakenTrue,
      )))
    : 1

  const currentValuation = currentMember ? computeMemberValuation(currentMember, marketStats) : null

  return (
    <div className="sim-results-view">
      {/* 顶部视图双组切换：简要对比 vs 详细剖析 */}
      <div className="sim-result-group-switcher">
        <button
          type="button"
          className={`sim-group-tab ${groupView === 'comparison' ? 'is-active' : ''}`}
          onClick={() => setGroupView('comparison')}
        >
          {t('combat.simulator.resultTabComparison')} ({results.length})
        </button>
        <button
          type="button"
          className={`sim-group-tab ${groupView === 'details' ? 'is-active' : ''}`}
          onClick={() => setGroupView('details')}
        >
          {t('combat.simulator.resultTabDetails')}
        </button>
      </div>

      {groupView === 'comparison' ? (
        <div className="sim-tab-content">
          {champions && (
            <div className="sim-champions-grid">
              <div className="sim-champ-card champ-revenue">
                <span className="sim-champ-badge">{t('combat.simulator.champRevenueTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestRev.result.nodeId)} · {diffLabel(champions.bestRev.result.difficulty)}
                </span>
                <span className="sim-champ-val text-gold">
                  {champions.bestRev.summary.valuePerHour.toFixed(1)}{t('combat.simulator.result.perHour')}
                </span>
                <span className="sim-champ-sub">{t('combat.simulator.currentMarket')}</span>
              </div>
              <div className="sim-champ-card champ-exp">
                <span className="sim-champ-badge">{t('combat.simulator.champExpTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestExp.result.nodeId)} · {diffLabel(champions.bestExp.result.difficulty)}
                </span>
                <span className="sim-champ-val">
                  {champions.bestExp.summary.expPerHour.toFixed(1)}{t('combat.simulator.result.perHour')}
                </span>
                <span className="sim-champ-sub">
                  {t('combat.simulator.result.killsPerHour')}: {champions.bestExp.summary.killsPerHour.toFixed(1)}
                </span>
              </div>
              <div className="sim-champ-card champ-sixdim">
                <span className="sim-champ-badge">{t('combat.simulator.champSixDimTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestSix.result.nodeId)} · {diffLabel(champions.bestSix.result.difficulty)}
                </span>
                <span className="sim-champ-val text-emerald">
                  {champions.bestSix.summary.sixDimPerHour.toFixed(1)}{t('combat.simulator.result.perHour')}
                </span>
                <span className="sim-champ-sub">{t('combat.simulator.result.sixDimTotal')}</span>
              </div>
              <div className="sim-champ-card champ-safe">
                <span className="sim-champ-badge">{t('combat.simulator.champSafeTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestSafe.result.nodeId)} · {diffLabel(champions.bestSafe.result.difficulty)}
                </span>
                <span className="sim-champ-val">
                  {t('combat.simulator.result.deaths')}: {champions.bestSafe.summary.deaths}
                </span>
                <span className="sim-champ-sub">
                  {t('combat.simulator.result.encounters')}: {champions.bestSafe.summary.encounters}
                </span>
              </div>
            </div>
          )}

          <div className="sim-card">
            <div className="sim-card-header">
              <span className="sim-card-title">{t('combat.simulator.resultTabComparison')}</span>
              <span className="sim-card-sub">
                {t('combat.simulator.seedTitle')}: {runSeed}
              </span>
            </div>
            <div className="sim-data-table-wrap">
              <table className="sim-data-table">
                <thead>
                  <tr>
                    <th>{t('combat.simulator.compare.node')}</th>
                    <th>{t('combat.simulator.difficultyLabel')}</th>
                    <th>{t('combat.simulator.compare.building')}</th>
                    <th>{t('combat.simulator.compare.duration')}</th>
                    <th>{t('combat.simulator.result.killsPerHour')}</th>
                    <th className="th-highlight-cyan">{t('combat.simulator.result.expPerHour')}</th>
                    <th className="th-highlight-emerald">{t('combat.simulator.compare.sixDimPerHour')}</th>
                    <th className="th-highlight-gold">{t('combat.simulator.compare.valuePerHour')}</th>
                    <th>{t('combat.simulator.result.deaths')}</th>
                    <th>{t('combat.simulator.compare.action')}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map(({ index, result, summary }) => {
                    const isRev = champions?.bestRev.index === index && rows.length > 1
                    const isExp = champions?.bestExp.index === index && rows.length > 1
                    return (
                      <tr key={index}>
                        <td>
                          <strong>{nodeName(result.nodeId)}</strong>
                          {isRev && <span className="sim-tag-champ champ-tag-rev">{t('combat.simulator.champTagRev')}</span>}
                          {isExp && <span className="sim-tag-champ champ-tag-exp">{t('combat.simulator.champTagExp')}</span>}
                        </td>
                        <td><span className="sim-difficulty-tag">{diffLabel(result.difficulty)}</span></td>
                        <td className="sim-node-building-cell">{buildingBadge(result.nodeId)}</td>
                        <td>{result.simulatedSeconds}{t('combat.simulator.secondsUnit')}</td>
                        <td>{summary.killsPerHour.toFixed(1)}</td>
                        <td className="td-highlight-cyan">{summary.expPerHour.toFixed(1)}</td>
                        <td className="td-highlight-emerald">{summary.sixDimPerHour.toFixed(1)}</td>
                        <td className="td-highlight-gold">{summary.valuePerHour.toFixed(1)}</td>
                        <td className={summary.deaths > 0 ? 'text-crimson' : 'text-emerald'}>{summary.deaths}</td>
                        <td>
                          <button
                            type="button"
                            className="sim-table-action-btn"
                            onClick={() => {
                              setSelectedIndex(index)
                              setGroupView('details')
                            }}
                          >
                            {t('combat.simulator.viewDetails')}
                          </button>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      ) : (
        <div className="sim-tab-content">
          {/* 节点与难度切换器 */}
          {results.length > 1 && (
            <div className="sim-node-detail-picker">
              <span className="sim-picker-label">{t('combat.simulator.targets')}:</span>
              <div className="sim-picker-pills">
                {results.map((r, ri) => {
                  const s = summarizeMember(r, 0, marketStats)
                  return (
                    <button
                      key={ri}
                      type="button"
                      className={`sim-picker-pill ${ri === safeIndex ? 'is-active' : ''}`}
                      onClick={() => setSelectedIndex(ri)}
                    >
                      <span>{nodeName(r.nodeId)} · {diffLabel(r.difficulty)}</span>
                      <span className="sim-pill-revenue">{s.valuePerHour.toFixed(0)}{t('combat.simulator.result.perHour')}</span>
                    </button>
                  )
                })}
              </div>
            </div>
          )}

          {/* 多成员切换条 */}
          {current.members.length > 1 && (
            <div className="sim-node-detail-picker">
              <span className="sim-picker-label">{t('combat.simulator.result.memberSwitch')}:</span>
              <div className="sim-picker-pills">
                {current.members.map((m, mi) => (
                  <button
                    key={mi}
                    type="button"
                    className={`sim-picker-pill ${mi === safeMemberIndex ? 'is-active' : ''}`}
                    onClick={() => setSelectedMemberIndex(mi)}
                  >
                    <span>{m.displayName}</span>
                  </button>
                ))}
              </div>
            </div>
          )}

          {/* 详细剖析 4 子 Tab */}
          <div className="sim-nav-pills">
            {(['overview', 'combat', 'yields', 'logs'] as const).map(tab => (
              <button
                key={tab}
                type="button"
                className={`sim-nav-pill ${detailTab === tab ? 'is-active' : ''}`}
                onClick={() => setDetailTab(tab)}
              >
                {t(`combat.simulator.resultTab${tab[0].toUpperCase()}${tab.slice(1)}`)}
              </button>
            ))}
          </div>

          {currentMember && detailTab === 'overview' && (
            <div className="sim-tab-content">
              <div className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">
                    {nodeName(current.nodeId)} · {diffLabel(current.difficulty)} · {currentMember.displayName}
                  </span>
                  <span className="sim-card-sub">
                    {t('combat.simulator.simulatedSeconds', { s: current.simulatedSeconds })} · {t('combat.simulator.seedTitle')} {current.seed ?? runSeed}
                  </span>
                </div>
                <div className="sim-kpi-grid">
                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.result.encounters')}</span>
                    <span className="sim-kpi-value">{currentMember.encounters}</span>
                    <span className="sim-kpi-sub">{t('combat.simulator.result.cycles')}: {currentMember.cycles}</span>
                  </div>
                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.result.kills')}</span>
                    <span className="sim-kpi-value">{currentMember.normalKills + currentMember.eliteKills + currentMember.bossKills}</span>
                    <span className="sim-kpi-sub">{currentMember.normalKills}+{currentMember.eliteKills}+{currentMember.bossKills}</span>
                  </div>
                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.result.deaths')}</span>
                    <span className={`sim-kpi-value ${currentMember.deaths > 0 ? 'text-crimson' : 'text-emerald'}`}>
                      {currentMember.deaths}
                    </span>
                    <span className="sim-kpi-sub">{t('combat.simulator.result.repeats')}: {currentMember.completedRepeats}</span>
                  </div>
                  <div className="sim-kpi-block" title={t('combat.simulator.realExpHint')}>
                    <span className="sim-kpi-label">{t('combat.simulator.realExpPerHourLabel')}</span>
                    <span className="sim-kpi-value text-gold">
                      {currentHours > 0 ? (currentMember.lifetimeExperience / currentHours).toFixed(1) : '—'}
                    </span>
                    <span className="sim-kpi-sub">
                      {t('combat.simulator.realExpTotalLabel')}: {currentMember.lifetimeExperience.toFixed(1)}
                    </span>
                  </div>
                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.result.combatExp')}</span>
                    <span className="sim-kpi-value">{currentMember.combatExperience.toFixed(1)}</span>
                    <span className="sim-kpi-sub">
                      {currentHours > 0 ? `${(currentMember.combatExperience / currentHours).toFixed(1)} / h` : '—'}
                    </span>
                  </div>
                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.hp')} / {t('combat.simulator.mp')}</span>
                    <span className="sim-kpi-value">{currentMember.hitpoints.toFixed(0)} / {currentMember.manapoints.toFixed(0)}</span>
                    <span className="sim-kpi-sub">
                      {currentMember.maxHitpoints.toFixed(0)} / {currentMember.maxManapoints.toFixed(0)}
                    </span>
                  </div>
                  <div className="sim-kpi-block">
                    <span className="sim-kpi-label">{t('combat.simulator.result.sixDimTotal')}</span>
                    <span className="sim-kpi-value text-emerald">{totalRootExp(currentMember).toFixed(1)}</span>
                  </div>
                </div>
              </div>

              <div className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">{t('combat.simulator.result.rootExp')}</span>
                </div>
                <div className="wasm-sim-root-values">
                  {ROOT_KEYS.map(k => (
                    <span key={k} className="wasm-sim-tag">
                      {t(ROOT_LABEL_KEYS[k])} {currentMember.rootExp[k].toFixed(1)}
                    </span>
                  ))}
                </div>
              </div>

              {(currentMember.combatBuffCarry.length > 0 || (currentMember.combatSkills?.cooldowns ?? []).length > 0 || currentMember.combatAutoUseSlots.length > 0) && (
                <div className="sim-card">
                  {currentMember.combatBuffCarry.length > 0 && (
                    <div className="wasm-sim-result-items">
                      <span className="wasm-sim-strip-label">{t('combat.simulator.result.buffs')}</span>
                      <div className="wasm-sim-item-chips">
                        {currentMember.combatBuffCarry.map((b, i) => (
                          <span key={i} className="wasm-sim-tag">
                            <ItemIcon itemId={b.buffId} />{b.name || b.buffId}×{b.stacks} {b.remainingSeconds.toFixed(1)}s
                          </span>
                        ))}
                      </div>
                    </div>
                  )}
                  {(currentMember.combatSkills?.cooldowns ?? []).length > 0 && (
                    <div className="wasm-sim-result-items">
                      <span className="wasm-sim-strip-label">{t('combat.simulator.result.cooldowns')}</span>
                      <div className="wasm-sim-item-chips">
                        {(currentMember.combatSkills?.cooldowns ?? []).map((c, i) => (
                          <span key={i} className="wasm-sim-tag">
                            <ItemIcon itemId={c.abilityId} />{abilityName(c.abilityId)} {c.remainingSeconds.toFixed(1)}s
                          </span>
                        ))}
                      </div>
                    </div>
                  )}
                  {currentMember.combatAutoUseSlots.length > 0 && (
                    <div className="wasm-sim-result-items">
                      <span className="wasm-sim-strip-label">{t('combat.simulator.result.slots')}</span>
                      <div className="wasm-sim-item-chips">
                        {currentMember.combatAutoUseSlots.map((slot, i) => (
                          <span key={i} className="wasm-sim-tag">
                            {slot.itemId
                              ? <ItemReference itemId={slot.itemId} amount={slot.quantity} className="wasm-sim-asset-inline" />
                              : `#${slot.slotIndex} —`}
                            {slot.enabled ? '' : `（${t('combat.simulator.slotDisabled')}）`}
                          </span>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          {currentMember && detailTab === 'combat' && (
            <div className="sim-tab-content">
              <div className="sim-two-column-tables">
                <div className="sim-column-table-box">
                  <h4 className="sim-table-box-title">{t('combat.simulator.damageTitle')}</h4>
                  <div className="sim-data-table-wrap">
                    <table className="sim-data-table">
                      <thead>
                        <tr>
                          <th>{t('combat.simulator.result.unit')}</th>
                          <th>{t('combat.simulator.result.dealt')}</th>
                          <th>{t('combat.simulator.result.heal')}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {currentMember.combatStats.map((u, ui) => {
                          const dealt = u.damageDealtPhysical + u.damageDealtSpell + u.damageDealtTrue
                          const heal = u.healingRegen + u.healingOther
                          return (
                            <tr key={ui}>
                              <td>{u.displayName || enemyName(u.unitKey)}</td>
                              <td>
                                <div>{dealt.toFixed(0)}</div>
                                <div className="sim-mini-bar-bg">
                                  <div className="sim-progress-bar bar-damage" style={{ width: `${Math.min(100, (dealt / maxDamage) * 100)}%` }} />
                                </div>
                              </td>
                              <td>{heal.toFixed(0)}</td>
                            </tr>
                          )
                        })}
                      </tbody>
                    </table>
                  </div>
                </div>

                <div className="sim-column-table-box">
                  <h4 className="sim-table-box-title">{t('combat.simulator.damageTakenTitle')}</h4>
                  <div className="sim-data-table-wrap">
                    <table className="sim-data-table">
                      <thead>
                        <tr>
                          <th>{t('combat.simulator.result.unit')}</th>
                          <th>{t('combat.simulator.result.taken')}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {currentMember.combatStats.map((u, ui) => {
                          const taken = u.damageTakenPhysical + u.damageTakenSpell + u.damageTakenTrue
                          return (
                            <tr key={ui}>
                              <td>{u.displayName || enemyName(u.unitKey)}</td>
                              <td>
                                <div>{taken.toFixed(0)}</div>
                                <div className="sim-mini-bar-bg">
                                  <div className="sim-progress-bar bar-damage-taken" style={{ width: `${Math.min(100, (taken / maxDamage) * 100)}%` }} />
                                </div>
                              </td>
                            </tr>
                          )
                        })}
                      </tbody>
                    </table>
                  </div>
                </div>
              </div>

              {currentMember.enemies.length > 0 && (
                <div className="sim-card">
                  <div className="sim-card-header">
                    <span className="sim-card-title">{t('combat.simulator.result.enemies')}</span>
                  </div>
                  <div className="wasm-sim-item-chips">
                    {currentMember.enemies.map((e, i) => (
                      <span key={i} className="wasm-sim-tag">
                        <LogTemplateText template={`{{enemy:${e.enemyId}|${enemyName(e.enemyId)}}}`} />
                        {' '}{t('combat.simulator.hp')}{e.hitpoints.toFixed(0)} {t('combat.simulator.mp')}{e.manapoints.toFixed(0)}
                      </span>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}

          {currentMember && detailTab === 'yields' && (
            <div className="sim-tab-content">
              {currentValuation && (
                <div className="wasm-sim-valuation">
                  <span>
                    {currentValuation.hasUnknown ? t('combat.simulator.partialValue') : t('combat.simulator.netValue')}:{' '}
                    <strong>{currentValuation.known.toFixed(1)}</strong>
                    <em className="wasm-sim-valuation-note">（{t('combat.simulator.currentMarket')}）</em>
                    {currentValuation.unknownIds.length > 0 && (
                      <em className="wasm-sim-valuation-note">
                        （{t('combat.simulator.unpriced')}：{currentValuation.unknownIds.map(id =>
                          <ItemReference key={id} itemId={id} className="wasm-sim-asset-inline" />)}）
                      </em>
                    )}
                  </span>
                </div>
              )}

              <div className="sim-card">
                <div className="sim-card-header">
                  <span className="sim-card-title">{t('combat.simulator.result.gained')}</span>
                  <span className="sim-card-sub">{currentMember.items.length}</span>
                </div>
                {currentMember.items.length === 0 ? (
                  <div className="sim-no-drops">{t('combat.simulator.noDropsHint')}</div>
                ) : (
                  <div className="sim-items-cards-grid">
                    {currentMember.items.map((item, i) => {
                      const enhLv = variantLevel(item.variant) ?? 0
                      const itemDef = items.find(x => x.itemId === item.itemId)
                      const eqDef = equipmentCatalog.find(x => x.equipmentId === item.itemId)
                      const rawMeta = resolveItemMetadata(item.itemId)
                      const tier = eqDef?.tier ?? itemDef?.tier ?? rawMeta?.tier ?? 1
                      const meta = withItemGrade(rawMeta ?? {
                        label: itemDisplayName(item.itemId),
                        glyph: '·',
                        tone: 'gold',
                        category: resolveItemCategoryLabel(itemDef?.category ?? eqDef?.slot ?? '', t),
                        rarity: 'mundane',
                        rarityLabel: '',
                        description: itemDef?.description ?? eqDef?.description ?? '',
                      }, tier)
                      const displayName = enhLv > 0 ? `${itemDisplayName(item.itemId)} +${enhLv}` : itemDisplayName(item.itemId)
                      const ratePerHour = currentHours > 0 ? item.quantity / currentHours : 0
                      return (
                        <div key={i} className="sim-item-row-card">
                          <ItemTooltipTarget
                            itemId={item.itemId}
                            amount={item.quantity}
                            enhancementLevel={enhLv}
                            definition={itemDef}
                            equipmentDefinition={eqDef}
                            metadata={meta}
                          >
                            <ItemTile
                              itemId={item.itemId}
                              name={displayName}
                              category={resolveItemCategoryLabel(itemDef?.category ?? eqDef?.slot ?? meta.category, t)}
                              className="inventory-cell"
                              enhancementLevel={enhLv}
                              level={meta.level}
                              rarity={meta.rarity}
                            />
                          </ItemTooltipTarget>
                          <div className="sim-item-card-details">
                            <span className="sim-item-full-name">{displayName}</span>
                            <div className="sim-item-stat-row">
                              <span className="sim-stat-sub">×{item.quantity}</span>
                              <span className="sim-stat-rate text-gold">
                                {ratePerHour.toFixed(1)}{t('combat.simulator.result.perHour')}
                              </span>
                            </div>
                          </div>
                        </div>
                      )
                    })}
                  </div>
                )}
              </div>

              {currentMember.itemsLost.length > 0 && (
                <div className="sim-card">
                  <div className="sim-card-header">
                    <span className="sim-card-title">{t('combat.simulator.result.lost')}</span>
                  </div>
                  <div className="wasm-sim-item-chips">
                    {currentMember.itemsLost.map((item, i) => (
                      <ItemReference
                        key={i}
                        itemId={item.itemId}
                        enhancementLevel={variantLevel(item.variant) ?? 0}
                        amount={item.quantity}
                      />
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}

          {currentMember && detailTab === 'logs' && (
            <div className="sim-logs-container">
              <div className="sim-logs-content">
                <div className="sim-log-stream-header">
                  <span className="sim-log-stream-title">
                    {t('combat.simulator.result.events')}（{currentMember.events.length}
                    {currentMember.droppedEventCount > 0 ? ` +${currentMember.droppedEventCount}` : ''}）
                  </span>
                </div>
                <div className="sim-log-stream-body">
                  {currentMember.events.slice(-80).map((ev, i) => (
                    <div key={i} className="sim-log-line">
                      <span className="sim-log-time">{(ev.atUnixSeconds - startedAtUnixSeconds).toFixed(1)}s</span>
                      <span className="sim-log-text"><LogTemplateText template={ev.message} /></span>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  )
})
