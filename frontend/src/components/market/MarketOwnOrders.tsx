import { memo, useCallback } from 'react'
import { formatNumber } from '../../game/format'
import { getT, useT } from '../../i18n'
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
  const t = useT()
  const catalog = useGameStore(state => state.items)
  const setNotice = useGameStore(state => state.setNotice)
  const cancelOrder = useCallback((orderId: string) => {
    if (!gameClient.sendCommand('market_cancel', { orderId })) return
    setNotice(getT()('market.notice.cancelSubmitted'))
    window.setTimeout(onChanged, 120)
  }, [onChanged, setNotice])

  return (
    <div className="market-own-panel">
      <div className="market-own-toolbar">
        <span className="market-toolbar-label">{t('market.activeOrdersTotal', { count: formatNumber(orders.length) })}</span>
        <button className="market-claim" onClick={() => gameClient.sendCommand('market_claim', {})} type="button">{t('market.claimSettlement')}</button>
      </div>
      <section className="market-own-orders">
        {orders.length === 0
          ? <div className="market-own-empty">{t('market.noActiveOrders')}</div>
          : orders.map(order => (
              <div className="market-own-order" key={order.orderId}>
                <span className={`market-own-side market-own-side--${order.side}`}>{order.side === 'buy' ? t('market.side.buy') : t('market.side.sell')}</span>
                <strong>{catalog.find(item => item.itemId === order.itemId)?.name ?? order.itemId}</strong>
                <span>{formatNumber(order.price)} × {formatNumber(order.remainingQuantity)}</span>
                <span>{order.status === 'partially-filled' ? t('market.status.partiallyFilled') : t('market.status.open')}</span>
                <time dateTime={order.expiresAt} title={t('market.createdAt', { time: new Date(order.createdAt).toLocaleString('zh-CN') })}>
                  {t('market.expiresOn', { date: new Date(order.expiresAt).toLocaleDateString('zh-CN') })}
                </time>
                <button aria-label={t('market.cancelOrder')} onClick={() => cancelOrder(order.orderId)} title={t('market.cancelOrder')} type="button">×</button>
              </div>
            ))}
      </section>
      <footer className="market-footer">
        <span>{t('market.metricsTraded', { count: formatNumber(metrics?.tradedQuantity ?? 0) })}</span>
        <span>{t('market.metricsMatches', { count: formatNumber(metrics?.matchCount ?? 0) })}</span>
        <span>{t('market.sellerTaxNote')}</span>
      </footer>
    </div>
  )
})
