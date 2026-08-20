import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { formatNumber } from '../game/format'
import { ITEM_META, resolveItemGrade, withItemGrade, type ItemMetadata } from '../game/ids'
import { useGameStore } from '../game/gameStore'
import type { MarketBook, MarketMetrics, MarketOrder } from '../game/types'
import { fetchMarketBook, fetchMarketMetrics, fetchMarketOrders } from '../network/apiClient'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { MarketBookPanel } from './market/MarketBookPanel'
import { MarketBrowseGrid } from './market/MarketBrowseGrid'
import type { MarketInstrument } from './market/MarketOrderForm'
import { MarketOrderModal } from './market/MarketOrderModal'
import { MarketOwnOrders } from './market/MarketOwnOrders'

const REFRESH_INTERVAL_MS = 3_000
const ORDERS_REFRESH_INTERVAL_MS = 5_000
const METRICS_REFRESH_INTERVAL_MS = 10_000
const BOOK_DEPTH = 20

type MarketTab = 'browse' | 'orders'

const MARKET_TABS: ReadonlyArray<{ id: MarketTab; label: string }> = [
  { id: 'browse', label: '浏览' },
  { id: 'orders', label: '我的订单' },
]

function getMetadata(item: MarketInstrument): ItemMetadata {
  const registered = ITEM_META[item.itemId]
  if (!registered) {
    const tier = Math.max(1, item.tier)
    const grade = resolveItemGrade(tier)
    return {
      label: item.name,
      glyph: item.name.slice(0, 1),
      tone: 'gold',
      category: item.category,
      rarity: grade.rarity,
      rarityLabel: grade.label,
      level: tier,
      description: '坊市可交易物品。',
      source: '坊市',
      usage: '可在坊市挂牌交易。',
    }
  }
  return withItemGrade(registered, item.tier > 0 ? item.tier : registered.level ?? 1)
}

export const MarketPage = memo(function MarketPage(): React.JSX.Element {
  const catalog = useGameStore(state => state.items)
  const playerId = useGameStore(state => state.playerId)
  const inventory = useGameStore(state => state.inventory)

  const instruments = useMemo<MarketInstrument[]>(() => {
    const tradable = catalog
      .filter(item => item.isTradable)
      .map(item => ({
        itemId: item.itemId,
        name: item.name,
        category: item.category,
        tier: item.tier,
        sellPrice: item.sellPrice,
        definition: item,
      }))
    return tradable
      .sort((left, right) => left.category.localeCompare(right.category, 'zh-CN') || left.name.localeCompare(right.name, 'zh-CN'))
  }, [catalog])

  const [activeTab, setActiveTab] = useState<MarketTab>('browse')
  const [selectedItemId, setSelectedItemId] = useState<string | null>(null)
  const [orderModalSide, setOrderModalSide] = useState<'sell' | 'buy' | null>(null)
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
    let disposed = false
    let inFlight = false
    let controller: AbortController | null = null
    let timer: number | null = null
    let lastMetricsAt = 0
    let lastOrdersAt = 0

    const isAbortError = (reason: unknown): boolean =>
      reason instanceof DOMException && reason.name === 'AbortError'

    const refresh = async (force = false) => {
      if (inFlight || disposed || (!force && document.visibilityState !== 'visible')) return
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

        setError(hasFailure ? '坊市数据暂时不可用' : null)
      } finally {
        if (!disposed) setLoading(false)
        inFlight = false
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
  const selected = instruments.find(item => item.itemId === selectedItemId) ?? null
  const selectedAmount = selected ? (inventory.find(item => item.itemId === selected.itemId)?.amount ?? 0) : 0
  const spiritAmount = inventory.find(item => item.itemId === 'currency.spirit')?.amount ?? 0

  return (
    <section className="panel market-page">
      <header className="panel-header market-header">
        <h2>坊市</h2>
        <nav aria-label="坊市功能" className="market-tabs" role="tablist">
          {MARKET_TABS.map(tab => (
            <button
              aria-selected={activeTab === tab.id}
              className={activeTab === tab.id ? 'active' : ''}
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              role="tab"
              type="button"
            >{tab.label}</button>
          ))}
        </nav>
        <button aria-label="刷新盘口" className="market-refresh" disabled={loading} onClick={refresh} title="刷新盘口" type="button">↻</button>
      </header>

      <div className="market-status-row">
        <span className="market-toolbar-label">
          {activeTab === 'orders'
            ? `活动挂单 ${formatNumber(orders.length)} 笔`
            : selected
              ? `${selected.name} · 零阶盘口`
              : `共 ${formatNumber(instruments.length)} 种可交易物品`}
        </span>
        <span className={`market-status${error ? ' is-error' : ''}`} role="status">
          {error ?? (loading ? '同步中' : (selected && book ? `序号 ${formatNumber(book.sequence)}` : `灵石 ${formatNumber(spiritAmount)}`))}
        </span>
      </div>

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
                    ← 返回列表
                  </button>
                </div>

                <div className="market-detail-center">
                  <ItemTooltipTarget
                    amount={selectedAmount}
                    className="market-detail-tooltip"
                    definition={selected.definition}
                    itemId={selected.itemId}
                    metadata={getMetadata(selected)}
                  >
                    <div className="market-detail-item-card">
                      <ItemTile
                        amount={selectedAmount}
                        category={selected.category}
                        className="inventory-cell"
                        level={getMetadata(selected).level}
                        name={selected.name}
                        rarity={getMetadata(selected).rarity}
                      />
                    </div>
                  </ItemTooltipTarget>
                </div>

                <div className="market-detail-actions" role="group" aria-label="发起买卖">
                  <button
                    className="market-action-btn market-action-btn--buy"
                    onClick={() => setOrderModalSide('buy')}
                    type="button"
                  >
                    发起求购
                  </button>
                  <button
                    className="market-action-btn market-action-btn--sell"
                    onClick={() => setOrderModalSide('sell')}
                    type="button"
                  >
                    发起挂售
                  </button>
                </div>
              </div>

              <MarketBookPanel book={book} sides={['bid', 'ask']} />
            </div>
          )
        )}

        {activeTab === 'orders' && (
          <MarketOwnOrders metrics={metrics} onChanged={refresh} orders={orders} />
        )}
      </div>

      {orderModalSide !== null && selected !== null && (
        <MarketOrderModal
          book={book}
          instrument={selected}
          onClose={() => setOrderModalSide(null)}
          onPlaced={refresh}
          open={orderModalSide !== null}
          side={orderModalSide}
        />
      )}
    </section>
  )
})
