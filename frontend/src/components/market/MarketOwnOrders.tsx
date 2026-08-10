import { memo, useCallback } from 'react'
import { formatNumber } from '../../game/format'
import { useGameStore } from '../../game/gameStore'
import type { MarketMetrics, MarketOrder } from '../../game/types'
import { gameClient } from '../../network/gameClient'

interface MarketOwnOrdersProps {
  orders: readonly MarketOrder[]
  metrics: MarketMetrics | null
  onChanged: () => void
}

export const MarketOwnOrders = memo(function MarketOwnOrders({
  orders,
  metrics,
  onChanged,
}: MarketOwnOrdersProps): React.JSX.Element {
  const catalog = useGameStore(state => state.items)
  const setNotice = useGameStore(state => state.setNotice)
  const cancelOrder = useCallback((orderId: string) => {
    if (!gameClient.sendCommand('market_cancel', { orderId })) return
    setNotice('撤单请求已提交。')
    window.setTimeout(onChanged, 120)
  }, [onChanged, setNotice])

  return (
    <div className="market-own-panel">
      <div className="market-own-toolbar">
        <span className="market-toolbar-label">共 {formatNumber(orders.length)} 笔活动挂单</span>
        <button className="market-claim" onClick={() => gameClient.sendCommand('market_claim', {})} type="button">领取结算</button>
      </div>
      <section className="market-own-orders">
        {orders.length === 0
          ? <div className="market-own-empty">暂无活动挂单</div>
          : orders.map(order => (
              <div className="market-own-order" key={order.orderId}>
                <span className={`market-own-side market-own-side--${order.side}`}>{order.side === 'buy' ? '求购' : '出售'}</span>
                <strong>{catalog.find(item => item.itemId === order.itemId)?.name ?? order.itemId}</strong>
                <span>{formatNumber(order.price)} × {formatNumber(order.remainingQuantity)}</span>
                <span>{order.status === 'partially-filled' ? '部分成交' : '挂单中'}</span>
                <time dateTime={order.expiresAt} title={`创建于 ${new Date(order.createdAt).toLocaleString('zh-CN')}`}>
                  {new Date(order.expiresAt).toLocaleDateString('zh-CN')} 到期
                </time>
                <button aria-label="撤销订单" onClick={() => cancelOrder(order.orderId)} title="撤销订单" type="button">×</button>
              </div>
            ))}
      </section>
      <footer className="market-footer">
        <span>累计成交 {formatNumber(metrics?.tradedQuantity ?? 0)}</span>
        <span>撮合 {formatNumber(metrics?.matchCount ?? 0)}</span>
        <span>2% 卖方税</span>
      </footer>
    </div>
  )
})
