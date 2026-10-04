import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, resolveItemSubCategory, withItemGrade, type ItemMetadata } from '../game/ids'
import { getT, useT, type TFunction } from '../i18n'
import { useGameStore } from '../game/gameStore'
import type { MarketBook, MarketMetrics, MarketOrder } from '../game/types'
import { useMarketStore, type MarketTab } from '../game/marketStore'
import { fetchMarketBook, fetchMarketMetrics, fetchMarketOrders } from '../network/apiClient'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { MarketBookPanel } from './market/MarketBookPanel'
import { MarketBrowseGrid } from './market/MarketBrowseGrid'
import type { MarketInstrument } from './market/MarketOrderForm'
import { MarketOrderModal } from './market/MarketOrderModal'
import { MarketOwnOrders } from './market/MarketOwnOrders'

/** 坊市行情深度轮询间隔（3秒） */
const REFRESH_INTERVAL_MS = 3_000
/** 个人订单刷新间隔（5秒） */
const ORDERS_REFRESH_INTERVAL_MS = 5_000
/** 市场宏观指标刷新间隔（10秒） */
const METRICS_REFRESH_INTERVAL_MS = 10_000
/** 盘口深度展示档位数 */
const BOOK_DEPTH = 20

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
      level: tier,
      description: t('market.itemFallback.description'),
    }
  }
  return withItemGrade({
    ...registered,
    category: subCategoryLabel,
  }, item.tier > 0 ? item.tier : registered.level ?? 1)
}

/**
 * 修仙界坊市主页面组件 (MarketPage)
 *
 * 核心架构与交互数据流：
 * 1. 浏览与选品 (browse Tab)：展示全服可交易物品网格列表 (MarketBrowseGrid)；
 * 2. 标的行情详情：选中物品后进入标的盘口深度界面，展示多档买盘/卖盘挂单 (MarketBookPanel)；
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
  // 挂单弹窗请求：盘口快捷按钮除了方向还锁定对手档价格（买一卖出/卖一买入），顶部按钮只给方向。
  const [orderModal, setOrderModal] = useState<{ side: 'sell' | 'buy'; price?: number } | null>(null)
  const [book, setBook] = useState<MarketBook | null>(null)
  const [metrics, setMetrics] = useState<MarketMetrics | null>(null)
  const [orders, setOrders] = useState<MarketOrder[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const refreshRef = useRef<(() => void) | null>(null)

  useEffect(() => {
    if (selectedItemId && !instruments.some(item => item.itemId === selectedItemId)) {
      setSelectedItemId(null)
    }
  }, [instruments, selectedItemId])

  useEffect(() => {
    // disposed 阻止卸载后回写；inFlight 阻止上一轮未完成的轮询重入；controller 随卸载中止在途请求。
    let disposed = false
    let inFlight = false
    // 挂单/撤单/领取后的强制刷新撞上在途轮询时不能吞掉：记账，在途请求收尾后补一轮全量刷新，
    // 否则我的订单/持仓最坏滞后一个轮询周期才可见。
    let pendingForceRefresh = false
    let controller: AbortController | null = null
    let timer: number | null = null
    let lastMetricsAt = 0
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
      const shouldRefreshMetrics = force
        || lastMetricsAt === 0
        || now - lastMetricsAt >= METRICS_REFRESH_INTERVAL_MS
      const shouldRefreshOrders = force
        || lastOrdersAt === 0
        || now - lastOrdersAt >= ORDERS_REFRESH_INTERVAL_MS
      const shouldRefreshBook = selectedItemId !== null

      try {
        const [bookResult, metricsResult, ordersResult] = await Promise.allSettled([
          shouldRefreshBook && selectedItemId
            ? fetchMarketBook(selectedItemId, 0, BOOK_DEPTH, controller.signal)
            : Promise.resolve<MarketBook | null>(null),
          shouldRefreshMetrics
            ? fetchMarketMetrics(controller.signal)
            : Promise.resolve<MarketMetrics | null>(null),
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
        if (metricsResult.status === 'fulfilled' && metricsResult.value !== null) {
          setMetrics(metricsResult.value)
          lastMetricsAt = now
        } else if (metricsResult.status === 'rejected' && !isAbortError(metricsResult.reason)) {
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
  }, [selectedItemId, playerId])

  const refresh = useCallback(() => refreshRef.current?.(), [])
  const closeOrderModal = useCallback(() => setOrderModal(null), [])
  // 盘口最优档快捷入口：买一行=按买一价卖出，卖一行=按卖一价买入；
  // 仍走挂单弹窗确认数量与明细，避免一键无确认成交。
  const openQuickTrade = useCallback((side: 'sell' | 'buy', price: number) => {
    setOrderModal({ side, price })
  }, [])
  const selected = instruments.find(item => item.itemId === selectedItemId) ?? null
  // 坊市只交易基础变体（挂单冻结空 variant 资产），持有量不能混入强化变体行
  const selectedAmount = selected ? (inventory.find(item => item.itemId === selected.itemId && !item.variant)?.amount ?? 0) : 0
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
          selected === null ? (
            <MarketBrowseGrid
              instruments={instruments}
              onSelect={setSelectedItemId}
              value={selectedItemId}
            />
          ) : (
            <div className="market-instrument-detail-view">
              <div className="market-detail-navbar">
                <div className="market-detail-left">
                  <button
                    className="market-back-btn"
                    onClick={() => setSelectedItemId(null)}
                    type="button"
                  >
                    {t('market.backToList')}
                  </button>
                </div>

                <div className="market-detail-center">
                  <ItemTooltipTarget
                    amount={selectedAmount}
                    className="market-detail-tooltip"
                    definition={selected.definition}
                    equipmentDefinition={selected.equipmentDefinition}
                    itemId={selected.itemId}
                    metadata={getMetadata(selected, t)}
                  >
                    <div className="market-detail-item-card">
                      <ItemTile
                        amount={selectedAmount}
                        category={resolveItemCategoryLabel(
                          resolveItemSubCategory(selected.itemId, selected.definition, selected.equipmentDefinition, selected.subCategory || selected.category), t)}
                        className="inventory-cell"
                        itemId={selected.itemId}
                        level={getMetadata(selected, t).level}
                        name={selected.name}
                        rarity={getMetadata(selected, t).rarity}
                      />
                    </div>
                  </ItemTooltipTarget>
                </div>

                <div className="market-detail-actions" role="group" aria-label={t('market.actionsGroupAria')}>
                  <button
                    className="market-action-btn market-action-btn--buy"
                    onClick={() => setOrderModal({ side: 'buy' })}
                    type="button"
                  >
                    {t('market.placeBuy')}
                  </button>
                  <button
                    className="market-action-btn market-action-btn--sell"
                    onClick={() => setOrderModal({ side: 'sell' })}
                    type="button"
                  >
                    {t('market.placeSell')}
                  </button>
                </div>
              </div>

              <MarketBookPanel book={book} onQuickTrade={openQuickTrade} sides={['bid', 'ask']} />
            </div>
          )
        )}

        {activeTab === 'orders' && (
          <MarketOwnOrders metrics={metrics} onChanged={refresh} orders={orders} />
        )}
      </div>

      {orderModal !== null && selected !== null && (
        <MarketOrderModal
          book={book}
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
