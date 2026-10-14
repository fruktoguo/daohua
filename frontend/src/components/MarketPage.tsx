import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { convertTierToLevel, parseEnhancementVariant, resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, resolveItemSubCategory, withItemGrade, type ItemMetadata } from '../game/ids'
import { getT, useT, type TFunction } from '../i18n'
import { useGameStore } from '../game/gameStore'
import type { MarketBook, MarketOrder, MarketVariantSummary } from '../game/types'
import { useMarketStore, type MarketTab } from '../game/marketStore'
import { fetchMarketBook, fetchMarketOrders, fetchMarketVariants } from '../network/apiClient'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { MarketBookPanel } from './market/MarketBookPanel'
import { MarketBrowseGrid } from './market/MarketBrowseGrid'
import { MarketEnhancementPicker } from './market/MarketEnhancementPicker'
import type { MarketInstrument } from './market/MarketOrderForm'
import { MarketOrderModal } from './market/MarketOrderModal'
import { MarketOwnOrders } from './market/MarketOwnOrders'

/** 坊市行情深度轮询间隔（3秒） */
const REFRESH_INTERVAL_MS = 3_000
/** 个人订单刷新间隔（5秒） */
const ORDERS_REFRESH_INTERVAL_MS = 5_000
/** 盘口深度展示档位数 */
const BOOK_DEPTH = 20
/** 变体摘要空集共享引用：未选中/未对账时保持 props 引用稳定，避免 picker 行无谓重渲染 */
const EMPTY_VARIANTS: MarketVariantSummary[] = []

const MARKET_TABS: ReadonlyArray<{ id: MarketTab }> = [
  { id: 'browse' },
  { id: 'orders' },
]

// 未注册物品与装备的坊市兜底元数据
function getMetadata(item: MarketInstrument, t: TFunction): ItemMetadata {
  const subCategoryLabel = resolveItemCategoryLabel(
    resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.subCategory || item.category), t)
  if (item.equipmentDefinition || item.category === 'equipment' || item.itemId.startsWith('equipment.')) {
    const tier = Math.max(1, item.tier)
    const grade = resolveItemGrade(tier)
    return {
      label: item.name,
      glyph: '⚔️',
      tone: 'gold',
      category: subCategoryLabel,
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
      category: subCategoryLabel,
      rarity: grade.rarity,
      rarityLabel: grade.label,
      tier,
      level: convertTierToLevel(tier),
      description: t('market.itemFallback.description'),
    }
  }
  return withItemGrade({
    ...registered,
    category: subCategoryLabel,
  }, item.tier > 0 ? item.tier : registered.tier ?? 1)
}

/** 装备标的判定：与 MarketBrowseGrid.isEquipment / getMetadata 装备分支同一口径 */
function isEquipmentInstrument(item: MarketInstrument): boolean {
  return !!item.equipmentDefinition || item.category === 'equipment' || item.itemId.startsWith('equipment.')
}

/** 标的详情/等级选择视图共用的顶部导航条 Props */
interface MarketDetailNavbarProps {
  /** 物品卡数量角标与悬浮数量副气泡 */
  amount: number
  /** 返回按钮文案 */
  backLabel: string
  /** 悬浮卡片与名称后缀对应的强化等级（0 = 基础变体） */
  enhancementLevel?: number
  /** 交易标的 */
  instrument: MarketInstrument
  /** 标的展示元数据 */
  metadata: ItemMetadata
  /** 返回按钮回调 */
  onBack: () => void
  /** 右侧操作区（等级选择视图缺省为空占位） */
  actions?: React.ReactNode
}

/**
 * 坊市标的详情顶部导航条 (MarketDetailNavbar)
 * 三栏布局：返回按钮 + 居中物品卡（悬浮详情按强化等级缩放）+ 右侧操作按钮组。
 */
function MarketDetailNavbar({
  amount,
  backLabel,
  enhancementLevel = 0,
  instrument,
  metadata,
  onBack,
  actions,
}: MarketDetailNavbarProps): React.JSX.Element {
  const t = useT()
  const displayName = enhancementLevel > 0 ? `${instrument.name} +${enhancementLevel}` : instrument.name
  return (
    <div className="market-detail-navbar">
      <div className="market-detail-left">
        <button
          className="market-back-btn"
          onClick={onBack}
          type="button"
        >
          {backLabel}
        </button>
      </div>

      <div className="market-detail-center">
        <ItemTooltipTarget
          amount={amount}
          className="market-detail-tooltip"
          definition={instrument.definition}
          enhancementLevel={enhancementLevel}
          equipmentDefinition={instrument.equipmentDefinition}
          itemId={instrument.itemId}
          metadata={metadata}
        >
          <div className="market-detail-item-card">
            <ItemTile
              amount={amount}
              category={resolveItemCategoryLabel(
                resolveItemSubCategory(instrument.itemId, instrument.definition, instrument.equipmentDefinition, instrument.subCategory || instrument.category), t)}
              className="inventory-cell"
              itemId={instrument.itemId}
              level={metadata.level}
              name={displayName}
              rarity={metadata.rarity}
            />
          </div>
        </ItemTooltipTarget>
      </div>

      {actions
        ? (
            <div className="market-detail-actions" role="group" aria-label={t('market.actionsGroupAria')}>
              {actions}
            </div>
          )
        : <div className="market-detail-actions" />}
    </div>
  )
}

/**
 * 修仙界坊市主页面组件 (MarketPage)
 *
 * 核心架构与交互数据流：
 * 1. 浏览与选品 (browse Tab)：展示全服可交易物品网格列表 (MarketBrowseGrid)；
 * 2. 标的行情详情：普通物品选中后直接进入标的盘口深度界面 (MarketBookPanel)；装备标的第一级为
 *    强化等级选择视图 (MarketEnhancementPicker)，按 "+N" 变体盘口分列买一/卖一与开单入口，
 *    「查看全部」进入该等级独立盘口详情（detailLevel 状态机：null=等级选择，N=对应等级盘口）；
 * 3. 挂单交易弹窗：提供买入/卖出限价挂单模态窗 (MarketOrderModal)，支持快捷填价、税率计算与滑点校验；
 * 4. 个人委托管理 (orders Tab)：展示玩家当前活跃挂单列表 (MarketOwnOrders)，支持单笔撤单与结算收益提取；
 * 5. 智能轮询机制：基于 visibilityState 在页面前台时高频轮询订单簿/个人订单/宏观撮合指标，后台时自动休眠节能。
 */
export const MarketPage = memo(function MarketPage(): React.JSX.Element {
  const t = useT()
  const catalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const playerId = useGameStore(state => state.playerId)
  const inventory = useGameStore(state => state.inventory)

  const instruments = useMemo<MarketInstrument[]>(() => {
    const tradableItems: MarketInstrument[] = catalog
      .filter(item => item.isTradable)
      .map(item => ({
        itemId: item.itemId,
        name: item.name,
        category: item.category,
        subCategory: item.subCategory ?? undefined,
        tier: item.tier,
        sellPrice: item.sellPrice,
        definition: item,
      }))

    const tradableEquipments: MarketInstrument[] = (equipmentCatalog ?? []).map(eq => ({
      itemId: eq.equipmentId,
      name: eq.name,
      category: 'equipment',
      subCategory: eq.slot,
      tier: eq.tier,
      sellPrice: 0,
      equipmentDefinition: eq,
    }))

    return [...tradableItems, ...tradableEquipments].sort((left, right) => {
      const leftCat = resolveItemCategoryLabel(left.category, t)
      const rightCat = resolveItemCategoryLabel(right.category, t)
      return leftCat.localeCompare(rightCat, 'zh-CN')
        || left.name.localeCompare(right.name, 'zh-CN')
        || (left.itemId < right.itemId ? -1 : left.itemId > right.itemId ? 1 : 0)
    })
  }, [catalog, equipmentCatalog, t])

  const activeTab = useMarketStore(state => state.activeTab)
  const setActiveTab = useMarketStore(state => state.setActiveTab)
  const selectedItemId = useMarketStore(state => state.selectedItemId)
  const setSelectedItemId = useMarketStore(state => state.setSelectedItemId)
  // 挂单弹窗请求：盘口/等级行按钮除方向外还锁定对手档价格（买一卖出/卖一买入）与强化等级。
  const [orderModal, setOrderModal] = useState<{ side: 'sell' | 'buy'; price?: number; level: number } | null>(null)
  const [book, setBook] = useState<MarketBook | null>(null)
  // 变体摘要轮询结果连同所属 itemId 一起缓存：渲染期按选中标的对账，杜绝切换标的后旧行闪现。
  const [variantBook, setVariantBook] = useState<{ itemId: string; rows: MarketVariantSummary[] } | null>(null)
  const [orders, setOrders] = useState<MarketOrder[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // 装备标的详情层级：null = 强化等级选择视图，N = 该等级盘口详情视图（非装备恒按 0 级盘口处理）。
  const [detailLevel, setDetailLevel] = useState<number | null>(null)
  const refreshRef = useRef<(() => void) | null>(null)

  useEffect(() => {
    if (selectedItemId && !instruments.some(item => item.itemId === selectedItemId)) {
      setSelectedItemId(null)
    }
  }, [instruments, selectedItemId])

  // 切换选中标的后回到顶层视图：装备回到等级选择、非装备回到基础盘口。
  useEffect(() => {
    setDetailLevel(null)
  }, [selectedItemId])

  const selected = instruments.find(item => item.itemId === selectedItemId) ?? null
  const isEquipment = selected !== null && isEquipmentInstrument(selected)
  // 装备在等级选择视图时 detailLevel=null（拉变体摘要），进入详情后为具体等级拉对应盘口；
  // 非装备没有变体簿概念，恒按 0 级基础盘口处理。
  const effectiveLevel = isEquipment ? (detailLevel ?? 0) : 0
  const inPickerView = isEquipment && detailLevel === null

  useEffect(() => {
    // disposed 阻止卸载后回写；inFlight 阻止上一轮未完成的轮询重入；controller 随卸载中止在途请求。
    let disposed = false
    let inFlight = false
    // 挂单/撤单/领取后的强制刷新撞上在途轮询时不能吞掉：记账，在途请求收尾后补一轮全量刷新，
    // 否则我的订单/持仓最坏滞后一个轮询周期才可见。
    let pendingForceRefresh = false
    let controller: AbortController | null = null
    let timer: number | null = null
    let lastOrdersAt = 0

    const isAbortError = (reason: unknown): boolean =>
      reason instanceof DOMException && reason.name === 'AbortError'

    const refresh = async (force = false) => {
      if (disposed || (!force && document.visibilityState !== 'visible')) return
      if (inFlight) {
        if (force) pendingForceRefresh = true
        return
      }
      inFlight = true
      controller = new AbortController()
      const now = Date.now()
      const shouldRefreshOrders = force
        || lastOrdersAt === 0
        || now - lastOrdersAt >= ORDERS_REFRESH_INTERVAL_MS
      // 等级选择视图轮询变体摘要（各 "+N" 盘口买一/卖一），盘口详情视图轮询当前等级深度簿。
      const shouldRefreshBook = selectedItemId !== null && !inPickerView
      const shouldRefreshVariants = selectedItemId !== null && inPickerView

      try {
        const [bookResult, variantsResult, ordersResult] = await Promise.allSettled([
          shouldRefreshBook && selectedItemId
            ? fetchMarketBook(selectedItemId, effectiveLevel, BOOK_DEPTH, controller.signal)
            : Promise.resolve<MarketBook | null>(null),
          shouldRefreshVariants && selectedItemId
            ? fetchMarketVariants(selectedItemId, controller.signal)
            : Promise.resolve<MarketVariantSummary[] | null>(null),
          shouldRefreshOrders
            ? fetchMarketOrders(playerId, controller.signal)
            : Promise.resolve<MarketOrder[] | null>(null),
        ] as const)
        if (disposed) return

        let hasFailure = false
        if (bookResult.status === 'fulfilled') {
          if (shouldRefreshBook) setBook(bookResult.value)
        } else if (!isAbortError(bookResult.reason)) {
          hasFailure = true
        }
        if (variantsResult.status === 'fulfilled') {
          if (shouldRefreshVariants && variantsResult.value !== null && selectedItemId) {
            setVariantBook({ itemId: selectedItemId, rows: variantsResult.value })
          }
        } else if (!isAbortError(variantsResult.reason)) {
          hasFailure = true
        }
        if (ordersResult.status === 'fulfilled' && ordersResult.value !== null) {
          setOrders(ordersResult.value)
          lastOrdersAt = now
        } else if (ordersResult.status === 'rejected' && !isAbortError(ordersResult.reason)) {
          hasFailure = true
        }

        setError(hasFailure ? getT()('market.error.unavailable') : null)
      } finally {
        if (!disposed) setLoading(false)
        inFlight = false
        if (pendingForceRefresh && !disposed) {
          pendingForceRefresh = false
          void refresh(true)
        }
      }
    }

    const scheduleNextRefresh = () => {
      if (disposed || document.visibilityState !== 'visible') return
      timer = window.setTimeout(async () => {
        timer = null
        await refresh()
        scheduleNextRefresh()
      }, REFRESH_INTERVAL_MS)
    }

    const handleVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        void refresh(true)
        scheduleNextRefresh()
      } else if (timer !== null) {
        window.clearTimeout(timer)
        timer = null
      }
    }

    setLoading(true)
    refreshRef.current = () => { void refresh(true) }
    document.addEventListener('visibilitychange', handleVisibilityChange)
    void refresh(true)
    scheduleNextRefresh()
    return () => {
      disposed = true
      refreshRef.current = null
      controller?.abort()
      document.removeEventListener('visibilitychange', handleVisibilityChange)
      if (timer !== null) window.clearTimeout(timer)
    }
  }, [selectedItemId, playerId, detailLevel, isEquipment, inPickerView, effectiveLevel])

  const refresh = useCallback(() => refreshRef.current?.(), [])
  const closeOrderModal = useCallback(() => setOrderModal(null), [])
  // 挂单弹窗统一入口：picker 行买卖按钮、详情页顶部按钮与盘口最优档快捷按钮共用，
  // level 决定落在哪本变体盘口（0 = 基础簿）；price 仅快捷入口携带（买一卖出/卖一买入）。
  const openOrderModal = useCallback((side: 'sell' | 'buy', level: number, price?: number) => {
    setOrderModal({ side, price, level })
  }, [])
  const openQuickTrade = useCallback((side: 'sell' | 'buy', price: number) => {
    openOrderModal(side, effectiveLevel, price)
  }, [effectiveLevel, openOrderModal])
  // 盘口详情返回：装备回到强化等级选择视图，非装备直接回标的列表。
  const handleBackFromDetail = useCallback(() => {
    if (isEquipment) setDetailLevel(null)
    else setSelectedItemId(null)
  }, [isEquipment, setSelectedItemId])
  const handleBackToList = useCallback(() => setSelectedItemId(null), [setSelectedItemId])
  // 当前盘口视图的持有量按 (itemId, "+N") 变体行计数，不同强化等级余额互不相混。
  const selectedAmount = selected
    ? (inventory.find(item => item.itemId === selected.itemId && parseEnhancementVariant(item.variant) === effectiveLevel)?.amount ?? 0)
    : 0
  // 等级选择视图物品卡显示全部变体合计持有量（聚合口径与浏览网格角标一致）。
  const totalSelectedAmount = useMemo(() => {
    if (!selected) return 0
    let total = 0
    for (const entry of inventory) if (entry.itemId === selected.itemId) total += entry.amount
    return total
  }, [inventory, selected])
  const selectedMetadata = useMemo(() => (selected ? getMetadata(selected, t) : null), [selected, t])
  // 轮询返回的盘口只对「当前选中标的 + 当前等级」生效，切换等级时旧簿不落屏（先显占位档）。
  const activeBook = book !== null && book.itemId === selectedItemId && book.enhancementLevel === effectiveLevel ? book : null
  const activeVariants = variantBook !== null && variantBook.itemId === selectedItemId ? variantBook.rows : EMPTY_VARIANTS
  return (
    <section className="panel market-page">
      <header className="panel-header market-header">
        <h2>{t('market.title')}</h2>
        <nav aria-label={t('market.tabsAria')} className="market-tabs" role="tablist">
          {MARKET_TABS.map(tab => (
            <button
              aria-selected={activeTab === tab.id}
              className={activeTab === tab.id ? 'active' : ''}
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              role="tab"
              type="button"
            >{t(`market.tab.${tab.id}`)}</button>
          ))}
        </nav>
        <div className="market-header-actions">
          {error ? (
            <span className="market-header-status is-error" role="status" title={error}>
              {error}
            </span>
          ) : loading ? (
            <span className="market-header-status is-loading" role="status">
              {t('market.syncing')}
            </span>
          ) : null}
          <button aria-label={t('market.refreshBook')} className="market-refresh" disabled={loading} onClick={refresh} title={t('market.refreshBook')} type="button">↻</button>
        </div>
      </header>

      <div className="market-tab-content">
        {activeTab === 'browse' && (
          selected === null || selectedMetadata === null ? (
            <MarketBrowseGrid
              instruments={instruments}
              onSelect={setSelectedItemId}
              value={selectedItemId}
            />
          ) : inPickerView ? (
            <div className="market-instrument-detail-view">
              <MarketDetailNavbar
                amount={totalSelectedAmount}
                backLabel={t('market.backToList')}
                instrument={selected}
                metadata={selectedMetadata}
                onBack={handleBackToList}
              />
              <MarketEnhancementPicker
                instrument={selected}
                metadata={selectedMetadata}
                onTrade={openOrderModal}
                onViewBook={setDetailLevel}
                variants={activeVariants}
              />
            </div>
          ) : (
            <div className="market-instrument-detail-view">
              <MarketDetailNavbar
                actions={(
                  <>
                    <button
                      className="market-action-btn market-action-btn--buy"
                      onClick={() => openOrderModal('buy', effectiveLevel)}
                      type="button"
                    >
                      {t('market.placeBuy')}
                    </button>
                    <button
                      className="market-action-btn market-action-btn--sell"
                      onClick={() => openOrderModal('sell', effectiveLevel)}
                      type="button"
                    >
                      {t('market.placeSell')}
                    </button>
                  </>
                )}
                amount={selectedAmount}
                backLabel={isEquipment ? t('market.enhance.backToLevels') : t('market.backToList')}
                enhancementLevel={effectiveLevel}
                instrument={selected}
                metadata={selectedMetadata}
                onBack={handleBackFromDetail}
              />
              <MarketBookPanel book={activeBook} onQuickTrade={openQuickTrade} sides={['bid', 'ask']} />
            </div>
          )
        )}

        {activeTab === 'orders' && (
          <MarketOwnOrders onChanged={refresh} orders={orders} />
        )}
      </div>

      {orderModal !== null && selected !== null && (
        <MarketOrderModal
          // 快捷填价档位必须与弹窗锁定等级同一本簿：picker 行直接开单时视图级 activeBook 可能对应别的等级。
          book={book !== null && book.itemId === selectedItemId && book.enhancementLevel === orderModal.level ? book : null}
          enhancementLevel={orderModal.level}
          initialPrice={orderModal.price}
          instrument={selected}
          onClose={closeOrderModal}
          onPlaced={refresh}
          open
          side={orderModal.side}
        />
      )}
    </section>
  )
})
