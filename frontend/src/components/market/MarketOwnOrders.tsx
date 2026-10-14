import { memo, useCallback } from 'react'
import { formatNumber } from '../../game/format'
import { findEquipmentInCatalog } from '../../game/ids'
import { ItemIcon } from '../ItemDisplay'
import { UnitNumber } from '../UnitNumber'
import { getT, useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import type { MarketOrder } from '../../game/types'
import { gameClient } from '../../network/gameClient'

/** 个人订单管理面板 Props */
interface MarketOwnOrdersProps {
  /** 个人当前活跃挂单列表 */
  orders: readonly MarketOrder[]
  /** 订单状态变动刷新回调 */
  onChanged: () => void
}

/**
 * 坊市个人订单管理与结算收益提取面板 (MarketOwnOrders)
 *
 * 核心功能与交互流程：
 * 1. 活跃挂单列表：展示每笔订单的买卖方向、标的名称、挂单单价、未成交余量、订单状态（挂单中 / 部分成交）与过期时间；
 * 2. 撤单交互：点击 × 按钮向服务端发送 market.cancel 撤回指定订单并解冻资产；
 * 3. 结算提取 (Claim)：点击“提取结算”向服务端发送 market.claim 指令提取成交收入与退还押金；
 * 4. 底部展示卖家 2% 印花税率说明。
 */
export const MarketOwnOrders = memo(function MarketOwnOrders({
  orders,
  onChanged,
}: MarketOwnOrdersProps): React.JSX.Element {
  const t = useT()
  const catalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const setNotice = useGameStore(state => state.setNotice)
  const cancelOrder = useCallback((orderId: string) => {
    if (!gameClient.sendCommand('market.cancel', { orderId })) return
    setNotice(getT()('market.notice.cancelSubmitted'))
    // 延迟 120ms 再拉取：给服务端一个结算窗口消化撤单指令，避免立刻刷新读到旧快照。
    window.setTimeout(onChanged, 120)
  }, [onChanged, setNotice])

  return (
    <div className="market-own-panel">
      <div className="market-own-toolbar">
        <span className="market-toolbar-label">{t('market.activeOrdersTotal', { count: formatNumber(orders.length) })}</span>
      <button className="market-claim" onClick={() => gameClient.sendCommand('market.claim', {})} type="button">{t('market.claimSettlement')}</button>
      </div>
      <section className="market-own-orders">
        {orders.length === 0
          ? <div className="market-own-empty">{t('market.noActiveOrders')}</div>
          : orders.map(order => {
              // 强化变体订单展示 "名称 +N"；装备名走装备目录（物品目录查不到 equipment.*）。
              const baseName = catalog.find(item => item.itemId === order.itemId)?.name
                ?? findEquipmentInCatalog(equipmentCatalog ?? [], order.itemId)?.name
                ?? order.itemId
              const displayName = order.enhancementLevel > 0 ? `${baseName} +${order.enhancementLevel}` : baseName
              return (
              <div className="market-own-order" key={order.orderId}>
                <span className={`market-own-side market-own-side--${order.side}`}>{order.side === 'buy' ? t('market.side.buy') : t('market.side.sell')}</span>
                <strong><ItemIcon itemId={order.itemId} />{displayName}</strong>
                <span><UnitNumber value={order.price} /> × <UnitNumber value={order.remainingQuantity} /></span>
                <span>{order.status === 'partially-filled' ? t('market.status.partiallyFilled') : t('market.status.open')}</span>
                <time dateTime={order.expiresAt ?? order.createdAt} title={t('market.createdAt', { time: new Date(order.createdAt).toLocaleString('zh-CN') })}>
                  {order.expiresAt ? t('market.expiresOn', { date: new Date(order.expiresAt).toLocaleDateString('zh-CN') }) : t('market.noExpiry')}
                </time>
                <button aria-label={t('market.cancelOrder')} onClick={() => cancelOrder(order.orderId)} title={t('market.cancelOrder')} type="button">×</button>
              </div>
              )
            })}
      </section>
      <footer className="market-footer">
        <span>{t('market.sellerTaxNote')}</span>
      </footer>
    </div>
  )
})
