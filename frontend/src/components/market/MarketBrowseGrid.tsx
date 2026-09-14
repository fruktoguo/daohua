import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { formatNumber } from '../../game/format'
import { resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata } from '../../game/ids'
import { useT, type TFunction } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { useMarketStore, type MarketPrimaryCategory } from '../../game/marketStore'
import { ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import type { MarketInstrument } from './MarketOrderForm'

// 未注册物品与装备的坊市兜底元数据
function getMetadata(item: MarketInstrument, t: TFunction): ItemMetadata {
  if (item.equipmentDefinition || item.category === 'equipment' || item.itemId.startsWith('equipment.')) {
    const tier = Math.max(1, item.tier)
    const grade = resolveItemGrade(tier)
    return {
      label: item.name,
      glyph: '⚔️',
      tone: 'gold',
      category: resolveItemCategoryLabel(item.subCategory || item.category, t),
      rarity: grade.rarity,
      rarityLabel: grade.label,
      level: tier,
      description: item.equipmentDefinition ? '' : t('market.itemFallback.description'),
    }
  }

  const registered = resolveItemMetadata(item.itemId)
  if (!registered) {
    const tier = Math.max(1, item.tier)
    const grade = resolveItemGrade(tier)
    return {
      label: item.name,
      glyph: item.name.slice(0, 1),
      tone: 'gold',
      category: resolveItemCategoryLabel(item.category, t),
      rarity: grade.rarity,
      rarityLabel: grade.label,
      level: tier,
      description: t('market.itemFallback.description'),
    }
  }
  return withItemGrade({
    ...registered,
    category: resolveItemCategoryLabel(registered.category, t),
  }, item.tier > 0 ? item.tier : registered.level ?? 1)
}

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
  { id: 'pill', labelKey: 'market.browse.cat.pill', fallback: '丹药' },
  { id: 'manual', labelKey: 'market.browse.cat.manual', fallback: '功法' },
]

type MaterialSubCategory = 'all' | 'mineral' | 'herb' | 'other'
type EquipmentSubCategory = 'all' | 'weapon' | 'armor' | 'trinket'
type ManualSubCategory = 'all' | 'tome' | 'fragment'

const MATERIAL_SUB_TABS: ReadonlyArray<TabConfig<MaterialSubCategory>> = [
  { id: 'all', labelKey: 'market.browse.sub.allMaterial', fallback: '全部材料' },
  { id: 'mineral', labelKey: 'market.browse.sub.mineral', fallback: '矿石' },
  { id: 'herb', labelKey: 'market.browse.sub.herb', fallback: '药材' },
  { id: 'other', labelKey: 'market.browse.sub.otherMaterial', fallback: '通用材料' },
]

const EQUIPMENT_SUB_TABS: ReadonlyArray<TabConfig<EquipmentSubCategory>> = [
  { id: 'all', labelKey: 'market.browse.sub.allEquipment', fallback: '全部装备' },
  { id: 'weapon', labelKey: 'market.browse.sub.weapon', fallback: '武器' },
  { id: 'armor', labelKey: 'market.browse.sub.armor', fallback: '防具' },
  { id: 'trinket', labelKey: 'market.browse.sub.trinket', fallback: '饰品' },
]

const MANUAL_SUB_TABS: ReadonlyArray<TabConfig<ManualSubCategory>> = [
  { id: 'all', labelKey: 'market.browse.sub.allManual', fallback: '全部功法' },
  { id: 'tome', labelKey: 'market.browse.sub.tome', fallback: '秘籍全本' },
  { id: 'fragment', labelKey: 'market.browse.sub.fragment', fallback: '招式残卷' },
]

const ARMOR_SLOTS = new Set(['head', 'armor', 'legs', 'boots'])
const TRINKET_SLOTS = new Set(['trinket', 'ring', 'neck', 'talisman'])
const WEAPON_SLOTS = new Set(['weapon', 'two-hand', 'off-hand'])

function isMaterial(item: MarketInstrument): boolean {
  return (
    item.category === 'material' ||
    item.category === 'herb' ||
    item.category === 'mineral' ||
    item.definition?.kind === 'material'
  )
}

function isEquipment(item: MarketInstrument): boolean {
  return (
    item.category === 'equipment' ||
    !!item.equipmentDefinition ||
    item.itemId.startsWith('equipment.')
  )
}

function isPill(item: MarketInstrument): boolean {
  return (
    item.category === 'pill' ||
    item.definition?.category === 'pill'
  )
}

function isManual(item: MarketInstrument): boolean {
  return (
    item.category === 'manual' ||
    item.category === 'manualFragment' ||
    item.itemId.startsWith('item.tome.') ||
    item.itemId.startsWith('item.fragment.')
  )
}

function matchesCategory(
  item: MarketInstrument,
  primary: PrimaryCategory,
  sub: string
): boolean {
  switch (primary) {
    case 'all':
      return true

    case 'material': {
      if (!isMaterial(item)) return false
      if (sub === 'all') return true
      if (sub === 'mineral') return item.category === 'mineral'
      if (sub === 'herb') return item.category === 'herb'
      if (sub === 'other') return item.category !== 'mineral' && item.category !== 'herb'
      return true
    }

    case 'equipment': {
      if (!isEquipment(item)) return false
      if (sub === 'all') return true
      const slot = item.subCategory || item.equipmentDefinition?.slot || ''
      if (sub === 'weapon') return WEAPON_SLOTS.has(slot) || slot === '' || slot === 'weapon'
      if (sub === 'armor') return ARMOR_SLOTS.has(slot)
      if (sub === 'trinket') return TRINKET_SLOTS.has(slot)
      return true
    }

    case 'pill':
      return isPill(item)

    case 'manual': {
      if (!isManual(item)) return false
      if (sub === 'all') return true
      if (sub === 'tome') return item.itemId.startsWith('item.tome.')
      if (sub === 'fragment') return item.itemId.startsWith('item.fragment.') || item.category === 'manualFragment'
      return true
    }
  }
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
  const metadata = useMemo(() => getMetadata(item, t), [item, t])

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
          category={resolveItemCategoryLabel(item.subCategory || item.category, t)}
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
 * 坊市标的网格检索与多级分类浏览组件 (MarketBrowseGrid)
 */
export const MarketBrowseGrid = memo(function MarketBrowseGrid({
  instruments,
  value,
  onSelect,
}: MarketBrowseGridProps): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)

  // 1. 接入跨界面常驻的坊市状态中心
  const primaryTab = useMarketStore(state => state.primaryTab)
  const setPrimaryTab = useMarketStore(state => state.setPrimaryTab)
  const subCategory = useMarketStore(state => state.subCategory)
  const setSubCategory = useMarketStore(state => state.setSubCategory)
  const query = useMarketStore(state => state.query)
  const setQuery = useMarketStore(state => state.setQuery)
  const savedScrollTop = useMarketStore(state => state.scrollTop)
  const setScrollTop = useMarketStore(state => state.setScrollTop)

  // 2. 滚动容器 Ref 与位置无损恢复控制
  const gridRef = useRef<HTMLDivElement>(null)
  const isRestoringScrollRef = useRef(false)
  const [isScrolling, setIsScrolling] = useState(false)
  const scrollTimerRef = useRef<number | null>(null)

  const handleScroll = useCallback(() => {
    if (isRestoringScrollRef.current) return
    setIsScrolling(true)
    if (gridRef.current) {
      setScrollTop(gridRef.current.scrollTop)
    }
    if (scrollTimerRef.current !== null) {
      window.clearTimeout(scrollTimerRef.current)
    }
    scrollTimerRef.current = window.setTimeout(() => {
      setIsScrolling(false)
      scrollTimerRef.current = null
    }, 100)
  }, [setScrollTop])

  // 挂载时瞬时平滑恢复之前的滚动位置（0 抖动）
  useLayoutEffect(() => {
    if (gridRef.current && savedScrollTop > 0) {
      isRestoringScrollRef.current = true
      gridRef.current.scrollTop = savedScrollTop
      const raf = requestAnimationFrame(() => {
        isRestoringScrollRef.current = false
      })
      return () => cancelAnimationFrame(raf)
    }
    return undefined
  }, [savedScrollTop])

  useEffect(() => {
    return () => {
      if (scrollTimerRef.current !== null) {
        window.clearTimeout(scrollTimerRef.current)
      }
    }
  }, [])

  // 3. 背包字典 O(1) 映射，避免每次渲染 80 * N 线性扫描
  const inventoryMap = useMemo(
    () => new Map(inventory.map(entry => [entry.itemId, entry.amount])),
    [inventory]
  )

  const handlePrimaryTabChange = (newPrimary: PrimaryCategory) => {
    setPrimaryTab(newPrimary)
  }

  const subTabs = useMemo(() => {
    switch (primaryTab) {
      case 'material':
        return MATERIAL_SUB_TABS
      case 'equipment':
        return EQUIPMENT_SUB_TABS
      case 'manual':
        return MANUAL_SUB_TABS
      default:
        return []
    }
  }, [primaryTab])

  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')
  const visibleItems = useMemo(() => instruments.filter(item => {
    if (!matchesCategory(item, primaryTab, subCategory)) return false
    if (!normalizedQuery) return true
    const catLabel = resolveItemCategoryLabel(item.subCategory || item.category, t)
    const primaryCatLabel = resolveItemCategoryLabel(item.category, t)
    const searchTarget = `${item.name} ${item.itemId} ${item.category} ${item.subCategory ?? ''} ${catLabel} ${primaryCatLabel}`.toLocaleLowerCase('zh-CN')
    return searchTarget.includes(normalizedQuery)
  }), [instruments, primaryTab, subCategory, normalizedQuery, t])

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
          onChange={event => setQuery(event.target.value)}
          placeholder={t('market.browse.searchItems')}
          type="search"
          value={query}
        />
      </div>

      {subTabs.length > 0 && (
        <div className="insight-sub-tabs market-sub-tabs" role="tablist" aria-label={t('market.browse.filterSubCategoryAria')}>
          {subTabs.map(sub => (
            <button
              aria-selected={subCategory === sub.id}
              className={`insight-sub-tab-btn${subCategory === sub.id ? ' active' : ''}`}
              key={sub.id}
              onClick={() => setSubCategory(sub.id)}
              role="tab"
              type="button"
            >
              {t(sub.labelKey) || sub.fallback}
            </button>
          ))}
        </div>
      )}

      <div
        aria-label={t('market.browse.gridAria')}
        className={`market-browse-grid${isScrolling ? ' is-scrolling' : ''}`}
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
