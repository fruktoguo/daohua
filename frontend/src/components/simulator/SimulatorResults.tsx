import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { marketStatsKey } from '../../network/apiClient'
import type {
  SimulatorCombatElementDamageDto,
  SimulatorCombatUnitStatDto,
  SimulatorEnvironmentDto,
  SimulatorMemberResultDto,
  SimulatorRunResultDto,
} from '../../game/simulator/simulatorTypes'
type SimulatorNodeEnvironmentDto = SimulatorEnvironmentDto['nodes'][number]
import {
  findCombatantInCatalog, resolveCatalogItemMetadata,
} from '../../game/ids'
import { FloatingInfoCard, FloatingPopover } from '../FloatingPopover'
import { ItemIcon, ItemReference, ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { LogTemplateText } from '../LogFeed'
import { UnitNumber } from '../UnitNumber'

interface Props {
  results: SimulatorRunResultDto[]
  nodeName: (id: string) => string
  nodeEnvMap?: ReadonlyMap<string, SimulatorNodeEnvironmentDto>
  runSeed: string
  startedAtUnixSeconds: number
}

type GroupView = 'comparison' | 'details'
type DetailTab = 'overview' | 'combat' | 'yields' | 'logs'
type SortKey = 'default' | 'kills' | 'exp' | 'sixDim' | 'value' | 'deaths'
type SortDir = 'asc' | 'desc'
type ElementKey = keyof SimulatorCombatElementDamageDto

const LINGSHI = 'item.lingshi'
const ASSET_SEP = '\u001F'

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

const ELEMENT_META: ReadonlyArray<{ key: ElementKey; labelKey: string; toneClass: string }> = [
  { key: 'metal', labelKey: 'combat.encounter.element.metal', toneClass: 'is-metal' },
  { key: 'wood', labelKey: 'combat.encounter.element.wood', toneClass: 'is-wood' },
  { key: 'water', labelKey: 'combat.encounter.element.water', toneClass: 'is-water' },
  { key: 'fire', labelKey: 'combat.encounter.element.fire', toneClass: 'is-fire' },
  { key: 'earth', labelKey: 'combat.encounter.element.earth', toneClass: 'is-earth' },
  { key: 'none', labelKey: 'combat.encounter.element.none', toneClass: 'is-none' },
]

function variantLevel(variant: string): number | null {
  if (!variant) return 0
  const raw = variant.startsWith('+') ? variant.slice(1) : variant
  if (!/^\d+$/.test(raw)) return null
  return Number(raw)
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

interface ResultPickerOption {
  index: number
  label: string
  mapName: string
  valuePerHour: number
  searchText: string
}

/** 详细页顶部支持搜索的地点×难度下拉框（复用 FloatingPopover Portal 防裁剪） */
const SearchableResultSelect = memo(function SearchableResultSelect({
  options,
  selectedIndex,
  onSelect,
}: {
  options: readonly ResultPickerOption[]
  selectedIndex: number
  onSelect: (index: number) => void
}) {
  const t = useT()
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [triggerWidth, setTriggerWidth] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const current = options.find(o => o.index === selectedIndex) ?? options[0]
  const normalizedQuery = query.trim().toLowerCase()
  const filtered = useMemo(() => {
    if (!normalizedQuery) return options
    return options.filter(o => o.searchText.includes(normalizedQuery))
  }, [normalizedQuery, options])

  useEffect(() => {
    if (!open) {
      setQuery('')
      return
    }
    const id = requestAnimationFrame(() => inputRef.current?.focus())
    return () => cancelAnimationFrame(id)
  }, [open])

  if (!current) return null

  return (
    <div className={`sim-result-select${open ? ' is-open' : ''}`} ref={rootRef}>
      <FloatingPopover
        className="sim-result-select-popover"
        open={open}
        onOpenChange={next => {
          setOpen(next)
          if (next && rootRef.current) setTriggerWidth(Math.ceil(rootRef.current.getBoundingClientRect().width))
        }}
        placement="bottom-start"
        gap={4}
        size="auto"
        content={({ close }) => (
          <div
            className="sim-result-select-menu"
            style={triggerWidth > 0 ? { minWidth: Math.max(triggerWidth, 260) } : undefined}
          >
            <div className="sim-result-search-wrap">
              <input
                ref={inputRef}
                type="text"
                className="sim-input sim-result-search-input"
                value={query}
                onChange={e => setQuery(e.target.value)}
                onKeyDown={e => {
                  if (e.key === 'Enter' && filtered.length > 0) {
                    e.preventDefault()
                    onSelect(filtered[0].index)
                    close()
                  }
                }}
                placeholder={t('combat.simulator.searchResultPlaceholder')}
              />
            </div>
            <div className="sim-result-select-list" role="listbox">
              {filtered.length === 0 ? (
                <div className="sim-result-select-empty">{t('combat.simulator.noMatchedResults')}</div>
              ) : filtered.map(option => (
                <button
                  key={option.index}
                  type="button"
                  role="option"
                  aria-selected={option.index === selectedIndex}
                  className={`sim-result-select-option${option.index === selectedIndex ? ' is-selected' : ''}`}
                  onClick={() => {
                    onSelect(option.index)
                    close()
                  }}
                >
                  <span className="sim-result-option-main">
                    <span className="sim-result-option-label">{option.label}</span>
                    {option.mapName && <span className="sim-result-option-map">{option.mapName}</span>}
                  </span>
                  <span className="sim-result-option-val">
                    <UnitNumber value={option.valuePerHour} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} disableTooltip />
                  </span>
                </button>
              ))}
            </div>
          </div>
        )}
      >
        <button
          type="button"
          className="sim-result-select-trigger"
          aria-expanded={open}
          aria-haspopup="listbox"
        >
          <span className="sim-result-trigger-label">{current.label}</span>
          <span className="sim-result-trigger-val">
            <UnitNumber value={current.valuePerHour} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} disableTooltip />
          </span>
          <span className="sim-result-trigger-count">{selectedIndex + 1}/{options.length}</span>
          <span className="sim-result-trigger-arrow" aria-hidden="true">▾</span>
        </button>
      </FloatingPopover>
    </div>
  )
})

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
  const [sortKey, setSortKey] = useState<SortKey>('default')
  const [sortDir, setSortDir] = useState<SortDir>('desc')

  const safeIndex = Math.min(selectedIndex, Math.max(0, results.length - 1))
  const current = results[safeIndex]
  const safeMemberIndex = current ? Math.min(selectedMemberIndex, Math.max(0, current.members.length - 1)) : 0
  const currentMember = current?.members[safeMemberIndex]

  const nodeMapNameMap = useMemo(() => {
    const map = new Map<string, string>()
    for (const m of maps ?? []) {
      for (const n of m.nodes ?? []) {
        map.set(n.nodeId, m.name ?? m.mapId)
      }
    }
    return map
  }, [maps])

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

  const nodeBonusLines = useCallback((nodeId: string): string[] => {
    const env = nodeEnvMap?.get(nodeId)
    if (!env) return []
    const parts: string[] = []
    if (env.effects.combatHitpointRegen) parts.push(t('combat.simulator.nodeHpRegen', { value: env.effects.combatHitpointRegen }))
    if (env.effects.combatManapointRegen) parts.push(t('combat.simulator.nodeMpRegen', { value: env.effects.combatManapointRegen }))
    if (env.effects.combatDamageReductionRatio) parts.push(`${t('combat.simulator.reduction')}${Math.round(env.effects.combatDamageReductionRatio * 100)}%`)
    if (env.effects.monsterWeakenRatio) parts.push(`${t('combat.simulator.weaken')}${Math.round(env.effects.monsterWeakenRatio * 100)}%`)
    for (const u of env.usages) {
      if (u.efficiency < 1) parts.push(t('combat.simulator.crowdedEff', { pct: Math.round(u.efficiency * 100) }))
    }
    return parts
  }, [nodeEnvMap, t])

  const renderNodeHoverCard = useCallback((nodeId: string) => {
    const lines = nodeBonusLines(nodeId)
    return (
      <FloatingInfoCard
        title={nodeName(nodeId)}
        subtitle={nodeMapNameMap.get(nodeId)}
        badge={lines.length > 0 ? t('combat.simulator.nodeBonusCount', { count: lines.length }) : undefined}
        badgeTone="gold"
        lines={lines.length > 0 ? lines : [t('combat.simulator.noBuildingBonus')]}
      />
    )
  }, [nodeBonusLines, nodeMapNameMap, nodeName, t])

  const rows = useMemo(() =>
    results.map((r, index) => ({
      index,
      result: r,
      summary: summarizeMember(r, 0, marketStats),
    })),
  [marketStats, results])

  const sortedRows = useMemo(() => {
    if (sortKey === 'default') return rows
    const list = [...rows]
    const sign = sortDir === 'asc' ? 1 : -1
    list.sort((a, b) => {
      let va = 0
      let vb = 0
      switch (sortKey) {
        case 'kills': va = a.summary.killsPerHour; vb = b.summary.killsPerHour; break
        case 'exp': va = a.summary.expPerHour; vb = b.summary.expPerHour; break
        case 'sixDim': va = a.summary.sixDimPerHour; vb = b.summary.sixDimPerHour; break
        case 'value': va = a.summary.valuePerHour; vb = b.summary.valuePerHour; break
        case 'deaths': va = a.summary.deaths; vb = b.summary.deaths; break
      }
      if (va !== vb) return (va - vb) * sign
      return a.index - b.index
    })
    return list
  }, [rows, sortDir, sortKey])

  const toggleSort = useCallback((key: Exclude<SortKey, 'default'>) => {
    if (sortKey === key) {
      setSortDir(prev => prev === 'desc' ? 'asc' : 'desc')
    } else {
      setSortKey(key)
      setSortDir(key === 'deaths' ? 'asc' : 'desc')
    }
  }, [sortKey])

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

  const pickerOptions = useMemo<ResultPickerOption[]>(() =>
    rows.map(({ index, result, summary }) => {
      const nName = nodeName(result.nodeId)
      const dLabel = diffLabel(result.difficulty)
      const mName = nodeMapNameMap.get(result.nodeId) ?? ''
      const label = `${nName} · ${dLabel}`
      return {
        index,
        label,
        mapName: mName,
        valuePerHour: summary.valuePerHour,
        searchText: `${nName} ${mName} ${dLabel}`.toLowerCase(),
      }
    }),
  [diffLabel, nodeMapNameMap, nodeName, rows])

  if (results.length === 0 || !current) return null

  const currentHours = current.simulatedSeconds > 0 ? current.simulatedSeconds / 3600 : 0
  const simSecs = current.simulatedSeconds > 0 ? current.simulatedSeconds : 0
  const perSec = (value: number) => simSecs > 0 ? value / simSecs : 0

  const maxDealtPerSec = currentMember
    ? Math.max(1e-9, ...currentMember.combatStats.map(u =>
        perSec(u.damageDealtPhysical + u.damageDealtSpell + u.damageDealtTrue)))
    : 1
  const maxTakenPerSec = currentMember
    ? Math.max(1e-9, ...currentMember.combatStats.map(u =>
        perSec(u.damageTakenPhysical + u.damageTakenSpell + u.damageTakenTrue)))
    : 1

  const currentValuation = currentMember ? computeMemberValuation(currentMember, marketStats) : null

  const renderSortArrow = (key: Exclude<SortKey, 'default'>) => {
    if (sortKey !== key) return <span className="sim-sort-indicator is-idle" aria-hidden="true">⇅</span>
    return <span className="sim-sort-indicator is-active" aria-hidden="true">{sortDir === 'desc' ? '▼' : '▲'}</span>
  }

  const renderElementPopoverContent = (
    unit: SimulatorCombatUnitStatDto,
    mode: 'dealt' | 'taken',
  ) => {
    const unitLabel = unit.displayName || enemyName(unit.unitKey)
    const breakdown = mode === 'dealt' ? unit.damageDealtByElement : unit.damageTakenByElement
    const phys = mode === 'dealt' ? unit.damageDealtPhysical : unit.damageTakenPhysical
    const spell = mode === 'dealt' ? unit.damageDealtSpell : unit.damageTakenSpell
    const trueDmg = mode === 'dealt' ? unit.damageDealtTrue : unit.damageTakenTrue
    const total = phys + spell + trueDmg
    const itemsList = ELEMENT_META
      .map(meta => ({ ...meta, value: breakdown[meta.key] }))
      .filter(entry => entry.value > 0)
      .sort((a, b) => b.value - a.value)

    return (
      <div className="sim-elem-popover">
        <div className="sim-elem-popover-title">
          {t(mode === 'dealt' ? 'combat.simulator.elementBreakdownDealtTitle' : 'combat.simulator.elementBreakdownTakenTitle', { name: unitLabel })}
        </div>
        <div className="sim-elem-popover-cats">
          <span>{t('combat.encounter.damageCategory.physical')}: <UnitNumber value={perSec(phys)} maxDecimals={1} suffix={t('combat.simulator.perSecond')} disableTooltip /></span>
          <span>{t('combat.encounter.damageCategory.spell')}: <UnitNumber value={perSec(spell)} maxDecimals={1} suffix={t('combat.simulator.perSecond')} disableTooltip /></span>
          <span>{t('combat.encounter.damageCategory.true')}: <UnitNumber value={perSec(trueDmg)} maxDecimals={1} suffix={t('combat.simulator.perSecond')} disableTooltip /></span>
        </div>
        {total <= 0 || itemsList.length === 0 ? (
          <div className="sim-elem-popover-empty">{t('combat.simulator.noDamageRecorded')}</div>
        ) : (
          <div className="sim-elem-popover-list">
            {itemsList.map(entry => {
              const pct = (entry.value / total) * 100
              return (
                <div key={entry.key} className="sim-elem-row">
                  <span className={`sim-elem-name ${entry.toneClass}`}>{t(entry.labelKey)}</span>
                  <div className="sim-elem-bar-bg">
                    <div className={`sim-elem-bar-fill ${entry.toneClass}`} style={{ width: `${Math.min(100, pct)}%` }} />
                  </div>
                  <span className="sim-elem-pct">{pct.toFixed(1)}%</span>
                  <span className="sim-elem-rate">
                    <UnitNumber value={perSec(entry.value)} maxDecimals={1} suffix={t('combat.simulator.perSecond')} disableTooltip />
                  </span>
                </div>
              )
            })}
          </div>
        )}
        <div className="sim-elem-popover-foot">
          {t('combat.simulator.totalDamageLabel')}: <UnitNumber value={total} maxDecimals={1} disableTooltip />
        </div>
      </div>
    )
  }

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
        <div className="sim-tab-content sim-tab-content--dense">
          {champions && (
            <div className="sim-champions-strip">
              <button
                type="button"
                className="sim-champ-row champ-revenue"
                onClick={() => { setSelectedIndex(champions.bestRev.index); setGroupView('details') }}
              >
                <span className="sim-champ-badge">{t('combat.simulator.champRevenueTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestRev.result.nodeId)} · {diffLabel(champions.bestRev.result.difficulty)}
                </span>
                <span className="sim-champ-val text-gold">
                  <UnitNumber value={champions.bestRev.summary.valuePerHour} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} tooltipLabel={t('combat.simulator.compare.valuePerHour')} />
                </span>
              </button>
              <button
                type="button"
                className="sim-champ-row champ-exp"
                onClick={() => { setSelectedIndex(champions.bestExp.index); setGroupView('details') }}
              >
                <span className="sim-champ-badge">{t('combat.simulator.champExpTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestExp.result.nodeId)} · {diffLabel(champions.bestExp.result.difficulty)}
                </span>
                <span className="sim-champ-val text-cyan">
                  <UnitNumber value={champions.bestExp.summary.expPerHour} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} tooltipLabel={t('combat.simulator.result.expPerHour')} />
                </span>
              </button>
              <button
                type="button"
                className="sim-champ-row champ-sixdim"
                onClick={() => { setSelectedIndex(champions.bestSix.index); setGroupView('details') }}
              >
                <span className="sim-champ-badge">{t('combat.simulator.champSixDimTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestSix.result.nodeId)} · {diffLabel(champions.bestSix.result.difficulty)}
                </span>
                <span className="sim-champ-val text-emerald">
                  <UnitNumber value={champions.bestSix.summary.sixDimPerHour} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} tooltipLabel={t('combat.simulator.compare.sixDimPerHour')} />
                </span>
              </button>
              <button
                type="button"
                className="sim-champ-row champ-safe"
                onClick={() => { setSelectedIndex(champions.bestSafe.index); setGroupView('details') }}
              >
                <span className="sim-champ-badge">{t('combat.simulator.champSafeTitle')}</span>
                <span className="sim-champ-title">
                  {nodeName(champions.bestSafe.result.nodeId)} · {diffLabel(champions.bestSafe.result.difficulty)}
                </span>
                <span className="sim-champ-val">
                  {t('combat.simulator.result.deaths')} <UnitNumber value={champions.bestSafe.summary.deaths} maxDecimals={0} disableTooltip />
                  <span className="sim-champ-inline-sep">·</span>
                  <UnitNumber value={champions.bestSafe.summary.encounters} maxDecimals={0} suffix={t('combat.simulator.encountersUnit')} disableTooltip />
                </span>
              </button>
            </div>
          )}

          <div className="sim-card sim-card--dense">
            <div className="sim-card-header">
              <span className="sim-card-title">{t('combat.simulator.resultTabComparison')}</span>
              <span className="sim-card-sub">
                {t('combat.simulator.seedTitle')}: {runSeed}
              </span>
            </div>
            <div className="sim-data-table-wrap">
              <table className="sim-data-table sim-data-table--dense">
                <thead>
                  <tr>
                    <th>{t('combat.simulator.compare.node')}</th>
                    <th>{t('combat.simulator.difficultyLabel')}</th>
                    <th className="sim-th-sortable" title={t('combat.simulator.sortHint')} onClick={() => toggleSort('kills')}>
                      <span>{t('combat.simulator.result.killsPerHour')}{renderSortArrow('kills')}</span>
                    </th>
                    <th className="th-highlight-cyan sim-th-sortable" title={t('combat.simulator.sortHint')} onClick={() => toggleSort('exp')}>
                      <span>{t('combat.simulator.result.expPerHour')}{renderSortArrow('exp')}</span>
                    </th>
                    <th className="th-highlight-emerald sim-th-sortable" title={t('combat.simulator.sortHint')} onClick={() => toggleSort('sixDim')}>
                      <span>{t('combat.simulator.compare.sixDimPerHour')}{renderSortArrow('sixDim')}</span>
                    </th>
                    <th className="th-highlight-gold sim-th-sortable" title={t('combat.simulator.sortHint')} onClick={() => toggleSort('value')}>
                      <span>{t('combat.simulator.compare.valuePerHour')}{renderSortArrow('value')}</span>
                    </th>
                    <th className="sim-th-sortable" title={t('combat.simulator.sortHint')} onClick={() => toggleSort('deaths')}>
                      <span>{t('combat.simulator.result.deaths')}{renderSortArrow('deaths')}</span>
                    </th>
                    <th>{t('combat.simulator.compare.action')}</th>
                  </tr>
                </thead>
                <tbody>
                  {sortedRows.map(({ index, result, summary }) => {
                    const isRev = champions?.bestRev.index === index && rows.length > 1
                    const isExp = champions?.bestExp.index === index && rows.length > 1
                    return (
                      <tr key={index}>
                        <td>
                          <FloatingPopover
                            trigger="hover"
                            placement="top"
                            size="small"
                            interactive={false}
                            content={() => renderNodeHoverCard(result.nodeId)}
                          >
                            <span className="sim-node-hover-target">
                              <strong>{nodeName(result.nodeId)}</strong>
                            </span>
                          </FloatingPopover>
                          {isRev && <span className="sim-tag-champ champ-tag-rev">{t('combat.simulator.champTagRev')}</span>}
                          {isExp && <span className="sim-tag-champ champ-tag-exp">{t('combat.simulator.champTagExp')}</span>}
                        </td>
                        <td><span className="sim-difficulty-tag">{diffLabel(result.difficulty)}</span></td>
                        <td className="sim-num-cell">
                          <UnitNumber value={summary.killsPerHour} maxDecimals={1} tooltipLabel={t('combat.simulator.result.killsPerHour')} />
                        </td>
                        <td className="td-highlight-cyan sim-num-cell">
                          <UnitNumber value={summary.expPerHour} maxDecimals={1} tooltipLabel={t('combat.simulator.result.expPerHour')} />
                        </td>
                        <td className="td-highlight-emerald sim-num-cell">
                          <UnitNumber value={summary.sixDimPerHour} maxDecimals={1} tooltipLabel={t('combat.simulator.compare.sixDimPerHour')} />
                        </td>
                        <td className="td-highlight-gold sim-num-cell">
                          <UnitNumber value={summary.valuePerHour} maxDecimals={1} tooltipLabel={t('combat.simulator.compare.valuePerHour')} />
                        </td>
                        <td className={`sim-num-cell ${summary.deaths > 0 ? 'text-crimson' : 'text-emerald'}`}>
                          <UnitNumber value={summary.deaths} maxDecimals={0} tooltipLabel={t('combat.simulator.result.deaths')} />
                        </td>
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
        <div className="sim-tab-content sim-tab-content--dense">
          {/* 顶部紧凑单行：支持搜索的地点下拉框 + 多成员切换 + 子 Tab */}
          <div className="sim-details-topbar">
            {results.length > 1 ? (
              <div className="sim-details-picker-group">
                <span className="sim-picker-label">{t('combat.simulator.targets')}</span>
                <SearchableResultSelect
                  options={pickerOptions}
                  selectedIndex={safeIndex}
                  onSelect={setSelectedIndex}
                />
                <FloatingPopover
                  trigger="hover"
                  placement="top"
                  size="small"
                  interactive={false}
                  content={() => renderNodeHoverCard(current.nodeId)}
                >
                  <span className="sim-node-bonus-hint">ⓘ</span>
                </FloatingPopover>
              </div>
            ) : (
              <div className="sim-details-picker-group">
                <FloatingPopover
                  trigger="hover"
                  placement="top"
                  size="small"
                  interactive={false}
                  content={() => renderNodeHoverCard(current.nodeId)}
                >
                  <span className="sim-node-hover-target">
                    <strong>{nodeName(current.nodeId)} · {diffLabel(current.difficulty)}</strong>
                  </span>
                </FloatingPopover>
              </div>
            )}

            {current.members.length > 1 && (
              <div className="sim-details-picker-group">
                <span className="sim-picker-label">{t('combat.simulator.result.memberSwitch')}</span>
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

            <div className="sim-nav-pills sim-nav-pills--compact">
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
          </div>

          {currentMember && detailTab === 'overview' && (
            <div className="sim-tab-content sim-tab-content--dense">
              <div className="sim-card sim-card--dense">
                <div className="sim-card-header">
                  <span className="sim-card-title">
                    <FloatingPopover
                      trigger="hover"
                      placement="top"
                      size="small"
                      interactive={false}
                      content={() => renderNodeHoverCard(current.nodeId)}
                    >
                      <span className="sim-node-hover-target">{nodeName(current.nodeId)}</span>
                    </FloatingPopover>
                    {' · '}{diffLabel(current.difficulty)}{' · '}{currentMember.displayName}
                  </span>
                  <span className="sim-card-sub">
                    {t('combat.simulator.seedTitle')} {current.seed ?? runSeed}
                  </span>
                </div>
                <div className="sim-kpi-grid sim-kpi-grid--dense">
                  <div className="sim-kpi-row-item">
                    <span className="sim-kpi-label">{t('combat.simulator.result.encounters')}</span>
                    <span className="sim-kpi-val-inline">
                      <UnitNumber value={currentMember.encounters} maxDecimals={0} tooltipLabel={t('combat.simulator.result.encounters')} />
                      <small>{t('combat.simulator.result.cycles')} <UnitNumber value={currentMember.cycles} maxDecimals={0} disableTooltip /></small>
                    </span>
                  </div>
                  <div className="sim-kpi-row-item">
                    <span className="sim-kpi-label">{t('combat.simulator.result.kills')}</span>
                    <span className="sim-kpi-val-inline">
                      <UnitNumber value={currentMember.normalKills + currentMember.eliteKills + currentMember.bossKills} maxDecimals={0} tooltipLabel={t('combat.simulator.result.kills')} />
                      <small>{currentMember.normalKills}+{currentMember.eliteKills}+{currentMember.bossKills}</small>
                    </span>
                  </div>
                  <div className="sim-kpi-row-item">
                    <span className="sim-kpi-label">{t('combat.simulator.result.deaths')}</span>
                    <span className={`sim-kpi-val-inline ${currentMember.deaths > 0 ? 'text-crimson' : 'text-emerald'}`}>
                      <UnitNumber value={currentMember.deaths} maxDecimals={0} disableTooltip />
                      <small>{t('combat.simulator.result.repeats')} <UnitNumber value={currentMember.completedRepeats} maxDecimals={0} disableTooltip /></small>
                    </span>
                  </div>
                  <div className="sim-kpi-row-item" title={t('combat.simulator.realExpHint')}>
                    <span className="sim-kpi-label">{t('combat.simulator.realExpPerHourLabel')}</span>
                    <span className="sim-kpi-val-inline text-gold">
                      {currentHours > 0
                        ? <UnitNumber value={currentMember.lifetimeExperience / currentHours} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} tooltipLabel={t('combat.simulator.realExpPerHourLabel')} />
                        : '—'}
                      <small>{t('combat.simulator.realExpTotalLabel')} <UnitNumber value={currentMember.lifetimeExperience} maxDecimals={1} tooltipLabel={t('combat.simulator.realExpTotalLabel')} /></small>
                    </span>
                  </div>
                  <div className="sim-kpi-row-item">
                    <span className="sim-kpi-label">{t('combat.simulator.result.combatExp')}</span>
                    <span className="sim-kpi-val-inline">
                      <UnitNumber value={currentMember.combatExperience} maxDecimals={1} tooltipLabel={t('combat.simulator.result.combatExp')} />
                      <small>
                        {currentHours > 0
                          ? <UnitNumber value={currentMember.combatExperience / currentHours} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} disableTooltip />
                          : '—'}
                      </small>
                    </span>
                  </div>
                  <div className="sim-kpi-row-item">
                    <span className="sim-kpi-label">{t('combat.simulator.hp')} / {t('combat.simulator.mp')}</span>
                    <span className="sim-kpi-val-inline">
                      <UnitNumber value={currentMember.hitpoints} maxDecimals={0} disableTooltip /> / <UnitNumber value={currentMember.manapoints} maxDecimals={0} disableTooltip />
                      <small>
                        <UnitNumber value={currentMember.maxHitpoints} maxDecimals={0} disableTooltip /> / <UnitNumber value={currentMember.maxManapoints} maxDecimals={0} disableTooltip />
                      </small>
                    </span>
                  </div>
                  <div className="sim-kpi-row-item">
                    <span className="sim-kpi-label">{t('combat.simulator.result.sixDimTotal')}</span>
                    <span className="sim-kpi-val-inline text-emerald">
                      <UnitNumber value={totalRootExp(currentMember)} maxDecimals={1} tooltipLabel={t('combat.simulator.result.sixDimTotal')} />
                      <small>
                        {currentHours > 0
                          ? <UnitNumber value={totalRootExp(currentMember) / currentHours} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} disableTooltip />
                          : '—'}
                      </small>
                    </span>
                  </div>
                </div>

                <div className="sim-inline-strip-section">
                  <span className="wasm-sim-strip-label">{t('combat.simulator.result.rootExp')}</span>
                  <div className="wasm-sim-root-values">
                    {ROOT_KEYS.map(k => (
                      <span key={k} className="wasm-sim-tag">
                        {t(ROOT_LABEL_KEYS[k])}{' '}
                        <UnitNumber value={currentMember.rootExp[k]} maxDecimals={1} tooltipLabel={t(ROOT_LABEL_KEYS[k])} />
                      </span>
                    ))}
                  </div>
                </div>
              </div>

              {(currentMember.combatBuffCarry.length > 0 || (currentMember.combatSkills?.cooldowns ?? []).length > 0 || currentMember.combatAutoUseSlots.length > 0) && (
                <div className="sim-card sim-card--dense">
                  {currentMember.combatBuffCarry.length > 0 && (
                    <div className="sim-inline-strip-section">
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
                    <div className="sim-inline-strip-section">
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
                    <div className="sim-inline-strip-section">
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
            <div className="sim-tab-content sim-tab-content--dense">
              <div className="sim-combat-legend">
                <span><i className="combat-stats-seg combat-stats-seg--physical" />{t('combat.encounter.damageCategory.physical')}</span>
                <span><i className="combat-stats-seg combat-stats-seg--spell" />{t('combat.encounter.damageCategory.spell')}</span>
                <span><i className="combat-stats-seg combat-stats-seg--true" />{t('combat.encounter.damageCategory.true')}</span>
              </div>

              <div className="sim-two-column-tables">
                <div className="sim-column-table-box">
                  <h4 className="sim-table-box-title">{t('combat.simulator.damageTitle')}</h4>
                  <div className="sim-data-table-wrap">
                    <table className="sim-data-table sim-data-table--dense">
                      <thead>
                        <tr>
                          <th>{t('combat.simulator.result.unit')}</th>
                          <th>{t('combat.simulator.dealtPerSecond')}</th>
                          <th>{t('combat.simulator.healPerSecond')}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {currentMember.combatStats.map((u, ui) => {
                          const physRate = perSec(u.damageDealtPhysical)
                          const spellRate = perSec(u.damageDealtSpell)
                          const trueRate = perSec(u.damageDealtTrue)
                          const dealtRate = physRate + spellRate + trueRate
                          const healRate = perSec(u.healingRegen + u.healingOther)
                          return (
                            <tr key={ui}>
                              <td className="sim-unit-name-cell">{u.displayName || enemyName(u.unitKey)}</td>
                              <td>
                                <FloatingPopover
                                  trigger="hover"
                                  placement="top"
                                  size="small"
                                  interactive={false}
                                  content={() => renderElementPopoverContent(u, 'dealt')}
                                >
                                  <div className="sim-dmg-cell sim-node-hover-target">
                                    <div className="sim-dmg-head">
                                      <strong className="sim-dmg-total">
                                        <UnitNumber value={dealtRate} maxDecimals={1} suffix={t('combat.simulator.perSecond')} disableTooltip />
                                      </strong>
                                      <span className="sim-dmg-split">
                                        {physRate > 0 && <em className="is-phys">{t('combat.simulator.physShort')} <UnitNumber value={physRate} maxDecimals={1} disableTooltip /></em>}
                                        {spellRate > 0 && <em className="is-spell">{t('combat.simulator.spellShort')} <UnitNumber value={spellRate} maxDecimals={1} disableTooltip /></em>}
                                        {trueRate > 0 && <em className="is-true">{t('combat.simulator.trueShort')} <UnitNumber value={trueRate} maxDecimals={1} disableTooltip /></em>}
                                      </span>
                                    </div>
                                    <div className="sim-dmg-stacked-bar">
                                      {physRate > 0 && <i className="combat-stats-seg combat-stats-seg--physical" style={{ width: `${Math.min(100, (physRate / maxDealtPerSec) * 100)}%` }} />}
                                      {spellRate > 0 && <i className="combat-stats-seg combat-stats-seg--spell" style={{ width: `${Math.min(100, (spellRate / maxDealtPerSec) * 100)}%` }} />}
                                      {trueRate > 0 && <i className="combat-stats-seg combat-stats-seg--true" style={{ width: `${Math.min(100, (trueRate / maxDealtPerSec) * 100)}%` }} />}
                                    </div>
                                  </div>
                                </FloatingPopover>
                              </td>
                              <td className="sim-num-cell">
                                <UnitNumber value={healRate} maxDecimals={1} suffix={t('combat.simulator.perSecond')} tooltipLabel={t('combat.simulator.healPerSecond')} />
                              </td>
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
                    <table className="sim-data-table sim-data-table--dense">
                      <thead>
                        <tr>
                          <th>{t('combat.simulator.result.unit')}</th>
                          <th>{t('combat.simulator.takenPerSecond')}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {currentMember.combatStats.map((u, ui) => {
                          const physRate = perSec(u.damageTakenPhysical)
                          const spellRate = perSec(u.damageTakenSpell)
                          const trueRate = perSec(u.damageTakenTrue)
                          const takenRate = physRate + spellRate + trueRate
                          return (
                            <tr key={ui}>
                              <td className="sim-unit-name-cell">{u.displayName || enemyName(u.unitKey)}</td>
                              <td>
                                <FloatingPopover
                                  trigger="hover"
                                  placement="top"
                                  size="small"
                                  interactive={false}
                                  content={() => renderElementPopoverContent(u, 'taken')}
                                >
                                  <div className="sim-dmg-cell sim-node-hover-target">
                                    <div className="sim-dmg-head">
                                      <strong className="sim-dmg-total">
                                        <UnitNumber value={takenRate} maxDecimals={1} suffix={t('combat.simulator.perSecond')} disableTooltip />
                                      </strong>
                                      <span className="sim-dmg-split">
                                        {physRate > 0 && <em className="is-phys">{t('combat.simulator.physShort')} <UnitNumber value={physRate} maxDecimals={1} disableTooltip /></em>}
                                        {spellRate > 0 && <em className="is-spell">{t('combat.simulator.spellShort')} <UnitNumber value={spellRate} maxDecimals={1} disableTooltip /></em>}
                                        {trueRate > 0 && <em className="is-true">{t('combat.simulator.trueShort')} <UnitNumber value={trueRate} maxDecimals={1} disableTooltip /></em>}
                                      </span>
                                    </div>
                                    <div className="sim-dmg-stacked-bar">
                                      {physRate > 0 && <i className="combat-stats-seg combat-stats-seg--physical" style={{ width: `${Math.min(100, (physRate / maxTakenPerSec) * 100)}%` }} />}
                                      {spellRate > 0 && <i className="combat-stats-seg combat-stats-seg--spell" style={{ width: `${Math.min(100, (spellRate / maxTakenPerSec) * 100)}%` }} />}
                                      {trueRate > 0 && <i className="combat-stats-seg combat-stats-seg--true" style={{ width: `${Math.min(100, (trueRate / maxTakenPerSec) * 100)}%` }} />}
                                    </div>
                                  </div>
                                </FloatingPopover>
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
                <div className="sim-card sim-card--dense">
                  <div className="sim-inline-strip-section">
                    <span className="wasm-sim-strip-label">{t('combat.simulator.result.enemies')}</span>
                    <div className="wasm-sim-item-chips">
                      {currentMember.enemies.map((e, i) => (
                        <span key={i} className="wasm-sim-tag">
                          <LogTemplateText template={`{{enemy:${e.enemyId}|${enemyName(e.enemyId)}}}`} />
                          {' '}{t('combat.simulator.hpShort')}<UnitNumber value={e.hitpoints} maxDecimals={0} disableTooltip />
                          {' '}{t('combat.simulator.mpShort')}<UnitNumber value={e.manapoints} maxDecimals={0} disableTooltip />
                        </span>
                      ))}
                    </div>
                  </div>
                </div>
              )}
            </div>
          )}

          {currentMember && detailTab === 'yields' && (
            <div className="sim-tab-content sim-tab-content--dense">
              {currentValuation && (
                <div className="wasm-sim-valuation sim-valuation-dense">
                  <span>
                    {currentValuation.hasUnknown ? t('combat.simulator.partialValue') : t('combat.simulator.netValue')}:{' '}
                    <strong><UnitNumber value={currentValuation.known} maxDecimals={1} tooltipLabel={t('combat.simulator.netValue')} /></strong>
                    {currentHours > 0 && (
                      <span className="text-gold sim-valuation-rate">
                        {' · '}<UnitNumber value={currentValuation.known / currentHours} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} tooltipLabel={t('combat.simulator.compare.valuePerHour')} />
                      </span>
                    )}
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

              <div className="sim-card sim-card--dense">
                <div className="sim-card-header">
                  <span className="sim-card-title">{t('combat.simulator.result.gained')}</span>
                  <span className="sim-card-sub">{currentMember.items.length}</span>
                </div>
                {currentMember.items.length === 0 ? (
                  <div className="sim-no-drops">{t('combat.simulator.noDropsHint')}</div>
                ) : (
                  <div className="sim-items-cards-grid sim-items-cards-grid--dense">
                    {currentMember.items.map((item, i) => {
                      const enhLv = variantLevel(item.variant) ?? 0
                      const itemDef = items.find(x => x.itemId === item.itemId)
                      const eqDef = equipmentCatalog.find(x => x.equipmentId === item.itemId)
                      const meta = resolveCatalogItemMetadata(item.itemId, {
                        itemDefinition: itemDef,
                        equipmentDefinition: eqDef,
                        name: itemDisplayName(item.itemId),
                        t,
                      })
                      const displayName = enhLv > 0 ? `${itemDisplayName(item.itemId)} +${enhLv}` : itemDisplayName(item.itemId)
                      const ratePerHour = currentHours > 0 ? item.quantity / currentHours : 0
                      return (
                        <div key={i} className="sim-item-row-card sim-item-row-card--dense">
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
                              category={meta.category}
                              className="inventory-cell"
                              enhancementLevel={enhLv}
                              level={meta.level}
                              rarity={meta.rarity}
                            />
                          </ItemTooltipTarget>
                          <div className="sim-item-card-details">
                            <span className="sim-item-full-name">{displayName}</span>
                            <div className="sim-item-stat-row">
                              <span className="sim-stat-sub">
                                <UnitNumber value={item.quantity} prefix="×" maxDecimals={0} />
                              </span>
                              <span className="sim-stat-rate text-gold">
                                <UnitNumber value={ratePerHour} maxDecimals={1} suffix={t('combat.simulator.result.perHour')} disableTooltip />
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
                <div className="sim-card sim-card--dense">
                  <div className="sim-inline-strip-section">
                    <span className="wasm-sim-strip-label">{t('combat.simulator.result.lost')}</span>
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
