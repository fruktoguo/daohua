import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef } from 'react'
import { formatNumber } from '../../game/format'
import { resolveItemCategoryLabel, resolveItemSubCategory } from '../../game/ids'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import {
  useMarketStore,
  type ConsumableKindFilter,
  type EquipmentKindFilter,
  type EquipmentSlotFilter,
  type ManualCategoryFilter,
  type ManualDisciplineFilter,
  type ManualFormFilter,
  type MarketPrimaryCategory,
  type MaterialRarityFilter,
} from '../../game/marketStore'
import { ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import {
  isMaterial,
  matchesConsumable,
  matchesEquipment,
  matchesGrade,
  matchesManual,
  matchesMaterialRarity,
} from './marketFilters'
import { resolveMarketInstrumentMetadata, type MarketInstrument } from './MarketOrderForm'


/** 一级主 Tab 分类类型 */
type PrimaryCategory = MarketPrimaryCategory

interface TabConfig<T extends string> {
  id: T
  labelKey: string
  fallback: string
}

const PRIMARY_TABS: ReadonlyArray<TabConfig<PrimaryCategory>> = [
  { id: 'all', labelKey: 'market.browse.cat.all', fallback: '全部' },
  { id: 'material', labelKey: 'market.browse.cat.material', fallback: '材料' },
  { id: 'equipment', labelKey: 'market.browse.cat.equipment', fallback: '装备' },
  { id: 'consumable', labelKey: 'market.browse.cat.consumable', fallback: '消耗品' },
  { id: 'manual', labelKey: 'market.browse.cat.manual', fallback: '功法' },
]

type MaterialSubCategory = 'all' | 'spiritMaterial' | 'mineral' | 'exoticMaterial' | 'refinedMaterial'

const MATERIAL_SUB_TABS: ReadonlyArray<TabConfig<MaterialSubCategory>> = [
  { id: 'all', labelKey: 'market.browse.sub.allMaterial', fallback: '全部材料' },
  { id: 'spiritMaterial', labelKey: 'market.browse.sub.spiritMaterial', fallback: '灵材' },
  { id: 'mineral', labelKey: 'market.browse.sub.mineral', fallback: '矿石' },
  { id: 'exoticMaterial', labelKey: 'market.browse.sub.exoticMaterial', fallback: '异材' },
  { id: 'refinedMaterial', labelKey: 'market.browse.sub.refinedMaterial', fallback: '精材' },
]

/** 品阶下拉档位：与品质档 `ITEM_GRADE_ORDER`（1~8 阶）同域 */
const GRADE_TIERS: ReadonlyArray<number> = [1, 2, 3, 4, 5, 6, 7, 8]

/** 坊市筛选项（Toggle 组通用） */
interface MarketFilterOption<T extends string> {
  value: T
  label: string
}

/** 材料子分类判定：与品类丝带同一 `resolveItemSubCategory` 口径 */
function matchesMaterialSubCategory(item: MarketInstrument, sub: string): boolean {
  if (sub === 'all') return true
  const subKey = resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.subCategory) ?? ''
  if (sub === 'spiritMaterial') return subKey === 'spiritMaterial' || subKey === 'herb'
  if (sub === 'mineral') return subKey === 'mineral'
  if (sub === 'exoticMaterial') return subKey === 'exoticMaterial'
  if (sub === 'refinedMaterial') return subKey === 'refinedMaterial'
  return true
}

/** 通用多选一 Toggle 组 Props（首项约定为「任意」） */
interface MarketOptionToggleProps<T extends string> {
  ariaLabel: string
  disabled?: boolean
  onChange: (value: T) => void
  options: ReadonlyArray<MarketFilterOption<T>>
  value: T
}

/** 通用多选一 Toggle 组：任意 + 互斥选项，支持整体置灰 */
function MarketOptionToggle<T extends string>({
  ariaLabel,
  disabled,
  onChange,
  options,
  value,
}: MarketOptionToggleProps<T>): React.JSX.Element {
  return (
    <div
      aria-disabled={disabled}
      aria-label={ariaLabel}
      className={`market-toggle-group${disabled ? ' is-disabled' : ''}`}
      role="group"
    >
      {options.map(option => (
        <button
          aria-pressed={value === option.value}
          className={`market-toggle-btn${value === option.value ? ' active' : ''}`}
          key={option.value}
          onClick={() => {
            if (disabled) return
            onChange(option.value)
          }}
          tabIndex={disabled ? -1 : undefined}
          type="button"
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}

/** 坊市可交易标的单个卡片 Props */
interface MarketBrowseCardItemProps {
  item: MarketInstrument
  amount: number
  selected: boolean
  onSelect: (itemId: string) => void
  openDelay?: number
}

/** 隔离后的单个坊市标的卡片（memo 避免 80+ 个格子联级重绘） */
const MarketBrowseCardItem = memo(function MarketBrowseCardItem({
  item,
  amount,
  selected,
  onSelect,
  openDelay = 150,
}: MarketBrowseCardItemProps): React.JSX.Element {
  const t = useT()
  const metadata = useMemo(() => resolveMarketInstrumentMetadata(item, t), [item, t])

  const handleClick = useCallback(() => {
    onSelect(item.itemId)
  }, [item.itemId, onSelect])

  return (
    <ItemTooltipTarget
      amount={amount}
      className="market-browse-tooltip"
      definition={item.definition}
      equipmentDefinition={item.equipmentDefinition}
      itemId={item.itemId}
      metadata={metadata}
      openDelay={openDelay}
      tabIndex={-1}
    >
      <button
        aria-label={t('market.browse.itemOwned', { name: item.name, count: formatNumber(amount) })}
        aria-selected={selected}
        className={`market-browse-card${selected ? ' is-selected' : ''}`}
        onClick={handleClick}
        role="option"
        type="button"
      >
        <ItemTile
          amount={amount}
          category={resolveItemCategoryLabel(
            resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.subCategory || item.category), t)}
          className="inventory-cell"
          itemId={item.itemId}
          level={metadata.level}
          name={item.name}
          rarity={metadata.rarity}
        />
      </button>
    </ItemTooltipTarget>
  )
})

/** 坊市可交易物品网格浏览 Props */
interface MarketBrowseGridProps {
  /** 全部可交易标的列表 */
  instruments: readonly MarketInstrument[]
  /** 当前选中的物品 ID */
  value?: string | null
  /** 选中物品进入详情盘口回调 */
  onSelect: (itemId: string) => void
}

/**
 * 坊市标的网格检索与多维分类浏览组件 (MarketBrowseGrid)
 *
 * 筛选体系：每个维度统一为「任意 + 互斥选项」Toggle 组，任意即该维不过滤，多维 AND 联合；
 * 品阶为跨品类全局维度（0 表示任意）。装备选中工具时类型维度置灰并忽略。
 */
export const MarketBrowseGrid = memo(function MarketBrowseGrid({
  instruments,
  value,
  onSelect,
}: MarketBrowseGridProps): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)

  // 1. 接入跨界面常驻的坊市状态中心（仅订阅必要属性，高频 scrollTop 不订阅以实现滚动零重绘）
  const primaryTab = useMarketStore(state => state.primaryTab)
  const setPrimaryTab = useMarketStore(state => state.setPrimaryTab)
  const subCategory = useMarketStore(state => state.subCategory)
  const setSubCategory = useMarketStore(state => state.setSubCategory)
  const query = useMarketStore(state => state.query)
  const setQuery = useMarketStore(state => state.setQuery)
  const abilities = useGameStore(state => state.abilities)
  const manualDiscipline = useMarketStore(state => state.manualDiscipline)
  const manualCategory = useMarketStore(state => state.manualCategory)
  const manualForm = useMarketStore(state => state.manualForm)
  const equipmentKind = useMarketStore(state => state.equipmentKind)
  const equipmentSlot = useMarketStore(state => state.equipmentSlot)
  const consumableKind = useMarketStore(state => state.consumableKind)
  const materialRarity = useMarketStore(state => state.materialRarity)
  const gradeFilter = useMarketStore(state => state.gradeFilter)
  const setManualDiscipline = useMarketStore(state => state.setManualDiscipline)
  const setManualCategory = useMarketStore(state => state.setManualCategory)
  const setManualForm = useMarketStore(state => state.setManualForm)
  const setEquipmentKind = useMarketStore(state => state.setEquipmentKind)
  const setEquipmentSlot = useMarketStore(state => state.setEquipmentSlot)
  const setConsumableKind = useMarketStore(state => state.setConsumableKind)
  const setMaterialRarity = useMarketStore(state => state.setMaterialRarity)
  const setGradeFilter = useMarketStore(state => state.setGradeFilter)

  // 2. 滚动容器 Ref 与位置无损恢复控制。
  //    is-scrolling 用命令式 classList 切换、scrollTop 先落 ref 再防抖写回 store：
  //    整个滚动路径不经过任何 React 渲染与 store 通知，保证滚动零重绘。
  const gridRef = useRef<HTMLDivElement>(null)
  const scrollTimerRef = useRef<number | null>(null)
  const pendingScrollTopRef = useRef<number | null>(null)

  const flushScrollTop = useCallback(() => {
    if (pendingScrollTopRef.current !== null) {
      useMarketStore.getState().setScrollTop(pendingScrollTopRef.current)
      pendingScrollTopRef.current = null
    }
  }, [])

  const handleScroll = useCallback(() => {
    const grid = gridRef.current
    if (!grid) return
    pendingScrollTopRef.current = grid.scrollTop
    if (!grid.classList.contains('is-scrolling')) {
      grid.classList.add('is-scrolling')
    }
    if (scrollTimerRef.current !== null) {
      window.clearTimeout(scrollTimerRef.current)
    }
    scrollTimerRef.current = window.setTimeout(() => {
      scrollTimerRef.current = null
      grid.classList.remove('is-scrolling')
      flushScrollTop()
    }, 100)
  }, [flushScrollTop])

  // 挂载时瞬时平滑恢复之前的滚动位置（0 抖动、零多余重绘）
  useLayoutEffect(() => {
    const savedTop = useMarketStore.getState().scrollTop
    if (gridRef.current && savedTop > 0) {
      gridRef.current.scrollTop = savedTop
    }
  }, [])

  useEffect(() => {
    return () => {
      if (scrollTimerRef.current !== null) {
        window.clearTimeout(scrollTimerRef.current)
      }
      flushScrollTop()
    }
  }, [flushScrollTop])

  // 3. 背包字典 O(1) 映射，避免每次渲染 80 * N 线性扫描。
  //    同一 itemId 可能存在多条变体行（如不同强化等级），角标显示全部变体合计持有量。
  const inventoryMap = useMemo(() => {
    const amounts = new Map<string, number>()
    for (const entry of inventory) amounts.set(entry.itemId, (amounts.get(entry.itemId) ?? 0) + entry.amount)
    return amounts
  }, [inventory])

  const resetScroll = useCallback(() => {
    if (gridRef.current) {
      gridRef.current.scrollTop = 0
    }
  }, [])

  const handlePrimaryTabChange = (newPrimary: PrimaryCategory) => {
    setPrimaryTab(newPrimary)
    resetScroll()
  }

  const handleSubCategoryChange = (newSub: string) => {
    setSubCategory(newSub)
    resetScroll()
  }

  const handleQueryChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    setQuery(event.target.value)
    resetScroll()
  }

  const handleGradeChange = (event: React.ChangeEvent<HTMLSelectElement>) => {
    setGradeFilter(Number(event.target.value))
    resetScroll()
  }

  // 4. 筛选选项常量：首项恒为「任意」，其余互斥
  const materialRarityOptions = useMemo<ReadonlyArray<MarketFilterOption<MaterialRarityFilter>>>(() => [
    { value: 'any', label: t('market.browse.filter.any') },
    { value: 'common', label: t('market.browse.filter.rarity.common') },
    { value: 'rare', label: t('market.browse.filter.rarity.rare') },
  ], [t])

  const equipmentKindOptions = useMemo<ReadonlyArray<MarketFilterOption<EquipmentKindFilter>>>(() => [
    { value: 'any', label: t('market.browse.filter.any') },
    { value: 'physical', label: t('market.browse.filter.kind.physical') },
    { value: 'spell', label: t('market.browse.filter.kind.spell') },
    { value: 'defense', label: t('market.browse.filter.kind.defense') },
  ], [t])

  const equipmentSlotOptions = useMemo<ReadonlyArray<MarketFilterOption<EquipmentSlotFilter>>>(() => [
    { value: 'any', label: t('market.browse.filter.any') },
    { value: 'weapon', label: t('market.browse.sub.weapon') },
    { value: 'offHand', label: t('market.browse.sub.offHand') },
    { value: 'head', label: t('market.browse.sub.head') },
    { value: 'armor', label: t('market.browse.sub.armor') },
    { value: 'legs', label: t('market.browse.sub.legs') },
    { value: 'trinket', label: t('market.browse.sub.trinket') },
    { value: 'tool', label: t('market.browse.sub.tool') },
  ], [t])

  const consumableKindOptions = useMemo<ReadonlyArray<MarketFilterOption<ConsumableKindFilter>>>(() => [
    { value: 'any', label: t('market.browse.filter.any') },
    { value: 'combatPotion', label: t('market.browse.filter.consumable.combatPotion') },
    { value: 'combatGeneral', label: t('market.browse.filter.consumable.combatGeneral') },
    { value: 'technique', label: t('market.browse.filter.consumable.technique') },
  ], [t])

  const disciplineOptions = useMemo<ReadonlyArray<MarketFilterOption<ManualDisciplineFilter>>>(() => [
    { value: 'any', label: t('market.browse.filter.any') },
    { value: 'spell', label: t('market.browse.manual.spell') },
    { value: 'internal', label: t('market.browse.manual.internal') },
  ], [t])

  const manualCategoryOptions = useMemo<ReadonlyArray<MarketFilterOption<ManualCategoryFilter>>>(() => [
    { value: 'any', label: t('market.browse.filter.any') },
    { value: 'physical', label: t('market.browse.manual.physical') },
    { value: 'spell', label: t('market.browse.manual.magic') },
  ], [t])

  const manualFormOptions = useMemo<ReadonlyArray<MarketFilterOption<ManualFormFilter>>>(() => [
    { value: 'any', label: t('market.browse.filter.any') },
    { value: 'tome', label: t('market.browse.manual.tome') },
    { value: 'fragment', label: t('market.browse.manual.fragment') },
  ], [t])

  const gradeOptions = useMemo<ReadonlyArray<{ value: number; label: string }>>(() => [
    { value: 0, label: t('market.browse.filter.any') },
    ...GRADE_TIERS.map(tier => ({
      value: tier,
      label: t('market.browse.filter.gradeOption', { tier: formatNumber(tier) }),
    })),
  ], [t])

  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')
  const visibleItems = useMemo(() => instruments.filter(item => {
    if (!matchesGrade(item, gradeFilter)) return false

    switch (primaryTab) {
      case 'material':
        if (!isMaterial(item)) return false
        if (!matchesMaterialSubCategory(item, subCategory)) return false
        if (!matchesMaterialRarity(item, materialRarity)) return false
        break
      case 'equipment':
        if (!matchesEquipment(item, equipmentKind, equipmentSlot)) return false
        break
      case 'consumable':
        if (!matchesConsumable(item, consumableKind)) return false
        break
      case 'manual':
        if (!matchesManual(item, abilities, manualDiscipline, manualCategory, manualForm)) return false
        break
      default:
        break
    }

    if (!normalizedQuery) return true
    const catLabel = resolveItemCategoryLabel(
      resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.subCategory || item.category), t)
    const primaryCatLabel = resolveItemCategoryLabel(item.category, t)
    const searchTarget = `${item.name} ${item.itemId} ${item.category} ${item.subCategory ?? ''} ${catLabel} ${primaryCatLabel}`.toLocaleLowerCase('zh-CN')
    return searchTarget.includes(normalizedQuery)
  }), [
    instruments,
    primaryTab,
    subCategory,
    normalizedQuery,
    t,
    abilities,
    gradeFilter,
    materialRarity,
    equipmentKind,
    equipmentSlot,
    consumableKind,
    manualDiscipline,
    manualCategory,
    manualForm,
  ])

  return (
    <div className="market-browse">
      <div className="market-browse-toolbar">
        <div className="market-category-chips" role="tablist" aria-label={t('market.browse.filterCategoryAria')}>
          {PRIMARY_TABS.map(tab => (
            <button
              aria-selected={primaryTab === tab.id}
              className={primaryTab === tab.id ? 'active' : ''}
              key={tab.id}
              onClick={() => handlePrimaryTabChange(tab.id)}
              role="tab"
              type="button"
            >
              {t(tab.labelKey) || tab.fallback}
            </button>
          ))}
        </div>
        <input
          aria-label={t('market.browse.searchItems')}
          autoComplete="off"
          onChange={handleQueryChange}
          placeholder={t('market.browse.searchItems')}
          type="search"
          value={query}
        />
      </div>

      {primaryTab === 'material' && (
        <div className="insight-sub-tabs market-sub-tabs" role="tablist" aria-label={t('market.browse.filterSubCategoryAria')}>
          {MATERIAL_SUB_TABS.map(sub => (
            <button
              aria-selected={subCategory === sub.id}
              className={`insight-sub-tab-btn${subCategory === sub.id ? ' active' : ''}`}
              key={sub.id}
              onClick={() => handleSubCategoryChange(sub.id)}
              role="tab"
              type="button"
            >
              {t(sub.labelKey) || sub.fallback}
            </button>
          ))}
        </div>
      )}

      <div className="market-manual-filters" role="toolbar" aria-label={t('market.browse.filterSubCategoryAria')}>
        {primaryTab === 'material' && (
          <>
            <span className="market-filter-label">{t('market.browse.filter.rarityLabel')}</span>
            <MarketOptionToggle
              ariaLabel={t('market.browse.filter.rarityAria')}
              onChange={next => {
                setMaterialRarity(next)
                resetScroll()
              }}
              options={materialRarityOptions}
              value={materialRarity}
            />
          </>
        )}

        {primaryTab === 'equipment' && (
          <>
            <span className="market-filter-label">{t('market.browse.filter.kindLabel')}</span>
            <MarketOptionToggle
              ariaLabel={t('market.browse.filter.kindAria')}
              disabled={equipmentSlot === 'tool'}
              onChange={next => {
                setEquipmentKind(next)
                resetScroll()
              }}
              options={equipmentKindOptions}
              value={equipmentKind}
            />
            <span className="market-filter-label">{t('market.browse.filter.slotLabel')}</span>
            <MarketOptionToggle
              ariaLabel={t('market.browse.filter.slotAria')}
              onChange={next => {
                setEquipmentSlot(next)
                resetScroll()
              }}
              options={equipmentSlotOptions}
              value={equipmentSlot}
            />
          </>
        )}

        {primaryTab === 'consumable' && (
          <>
            <span className="market-filter-label">{t('market.browse.filter.consumableLabel')}</span>
            <MarketOptionToggle
              ariaLabel={t('market.browse.filter.consumableAria')}
              onChange={next => {
                setConsumableKind(next)
                resetScroll()
              }}
              options={consumableKindOptions}
              value={consumableKind}
            />
          </>
        )}

        {primaryTab === 'manual' && (
          <>
            <span className="market-filter-label">{t('market.browse.filter.disciplineLabel')}</span>
            <MarketOptionToggle
              ariaLabel={t('market.browse.manual.disciplineAria')}
              onChange={next => {
                setManualDiscipline(next)
                resetScroll()
              }}
              options={disciplineOptions}
              value={manualDiscipline}
            />
            <span className="market-filter-label">{t('market.browse.filter.damageLabel')}</span>
            <MarketOptionToggle
              ariaLabel={t('market.browse.manual.categoryAria')}
              onChange={next => {
                setManualCategory(next)
                resetScroll()
              }}
              options={manualCategoryOptions}
              value={manualCategory}
            />
            <span className="market-filter-label">{t('market.browse.filter.formLabel')}</span>
            <MarketOptionToggle
              ariaLabel={t('market.browse.manual.formAria')}
              onChange={next => {
                setManualForm(next)
                resetScroll()
              }}
              options={manualFormOptions}
              value={manualForm}
            />
          </>
        )}

        <label className="market-grade-field">
          <span className="market-filter-label">{t('market.browse.filter.gradeLabel')}</span>
          <select
            aria-label={t('market.browse.filter.gradeAria')}
            className="market-grade-select"
            onChange={handleGradeChange}
            value={gradeFilter}
          >
            {gradeOptions.map(option => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
      </div>

      <div
        aria-label={t('market.browse.gridAria')}
        className="market-browse-grid"
        onScroll={handleScroll}
        ref={gridRef}
        role="listbox"
      >
        {visibleItems.map(item => (
          <MarketBrowseCardItem
            amount={inventoryMap.get(item.itemId) ?? 0}
            item={item}
            key={item.itemId}
            onSelect={onSelect}
            openDelay={150}
            selected={value === item.itemId}
          />
        ))}
      </div>
      {visibleItems.length === 0 && <p className="market-browse-empty">{t('market.browse.noMatch')}</p>}
    </div>
  )
})
