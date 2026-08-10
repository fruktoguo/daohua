import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import type { MarketBook, MarketMetrics, MarketOrder } from '../game/types'
import { fetchMarketBook, fetchMarketMetrics, fetchMarketOrders } from '../network/apiClient'
import { MarketBookPanel } from './market/MarketBookPanel'
import { MarketBrowseGrid } from './market/MarketBrowseGrid'
import { MarketOrderForm, type MarketInstrument } from './market/MarketOrderForm'
import { MarketOwnOrders } from './market/MarketOwnOrders'

const REFRESH_INTERVAL_MS = 3_000
const ORDERS_REFRESH_INTERVAL_MS = 5_000
const METRICS_REFRESH_INTERVAL_MS = 10_000
const BOOK_DEPTH = 20

type MarketTab = 'browse' | 'sell' | 'buy' | 'orders'

const MARKET_TABS: ReadonlyArray<{ id: MarketTab; label: string }> = [
  { id: 'browse', label: '浏览' },
  { id: 'sell', label: '出售' },
  { id: 'buy', label: '求购' },
  { id: 'orders', label: '我的挂单' },
]

export const MarketPage = memo(function MarketPage(): React.JSX.Element {
  const catalog = useGameStore(state => state.items)
  const playerId = useGameStore(state => state.playerId)
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
  const [itemId, setItemId] = useState('item.spirit-herb')
  const [book, setBook] = useState<MarketBook | null>(null)
  const [metrics, setMetrics] = useState<MarketMetrics | null>(null)
  const [orders, setOrders] = useState<MarketOrder[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const refreshRef = useRef<(() => void) | null>(null)

  useEffect(() => {
    if (instruments.some(item => item.itemId === itemId)) return
    if (instruments[0]) setItemId(instruments[0].itemId)
  }, [instruments, itemId])

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
      try {
        const [bookResult, metricsResult, ordersResult] = await Promise.allSettled([
          fetchMarketBook(itemId, 0, BOOK_DEPTH, controller.signal),
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
          setBook(bookResult.value)
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

        setError(hasFailure ? '市场数据暂时不可用' : null)
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
  }, [itemId, playerId])

  const refresh = useCallback(() => refreshRef.current?.(), [])
  const selected = instruments.find(item => item.itemId === itemId)
  const openOrderTab = useCallback((tab: 'sell' | 'buy') => setActiveTab(tab), [])

  return (
    <section className="panel market-page">
      <header className="panel-header market-header">
        <h2>灵市</h2>
        <nav aria-label="灵市功能" className="market-tabs" role="tablist">
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
          {activeTab === 'browse' ? `共 ${formatNumber(instruments.length)} 种可交易物品` : `${selected?.name ?? '市场'} · 零阶盘口`}
        </span>
        <span className={`market-status${error ? ' is-error' : ''}`} role="status">
          {error ?? (loading ? '同步中' : `序号 ${formatNumber(book?.sequence ?? 0)}`)}
        </span>
      </div>

      <div className="market-tab-content">
        {activeTab === 'browse' && (
          <>
            <MarketBrowseGrid instruments={instruments} onSelect={setItemId} value={itemId} />
            {selected && (
              <div className="market-browse-detail">
                <div className="market-browse-detail-header">
                  <strong>{selected.name}</strong>
                  <span className="market-toolbar-label">{selected.category} · 零阶盘口</span>
                  <div className="market-browse-detail-actions" role="group" aria-label="下单">
                    <button onClick={() => openOrderTab('sell')} type="button">去出售</button>
                    <button onClick={() => openOrderTab('buy')} type="button">去求购</button>
                  </div>
                </div>
                <MarketBookPanel book={book} sides={['bid', 'ask']} />
              </div>
            )}
          </>
        )}
        {activeTab === 'sell' && (
          <div className="market-order-tab">
            <div className="market-order-tab-item">
              <span>当前品种</span>
              <strong>{selected?.name ?? '—'}</strong>
              <button onClick={() => setActiveTab('browse')} type="button">去浏览更换</button>
            </div>
            <MarketOrderForm book={book} instruments={instruments} itemId={itemId} onPlaced={refresh} side="sell" />
            <MarketBookPanel book={book} sides={['ask']} />
          </div>
        )}
        {activeTab === 'buy' && (
          <div className="market-order-tab">
            <div className="market-order-tab-item">
              <span>当前品种</span>
              <strong>{selected?.name ?? '—'}</strong>
              <button onClick={() => setActiveTab('browse')} type="button">去浏览更换</button>
            </div>
            <MarketOrderForm book={book} instruments={instruments} itemId={itemId} onPlaced={refresh} side="buy" />
            <MarketBookPanel book={book} sides={['bid']} />
          </div>
        )}
        {activeTab === 'orders' && (
          <MarketOwnOrders metrics={metrics} onChanged={refresh} orders={orders} />
        )}
      </div>
    </section>
  )
})
