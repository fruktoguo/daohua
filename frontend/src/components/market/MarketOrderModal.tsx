import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { QuantitySelector } from '../QuantitySelector'
import { formatNumber } from '../../game/format'
import { resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata } from '../../game/ids'
import { getT, useT, type TFunction } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import type { MarketBook } from '../../game/types'
import { gameClient } from '../../network/gameClient'
import { ItemTile } from '../ItemDisplay'
import { Modal } from '../Modal'
import type { MarketInstrument } from './MarketOrderForm'
import { getMarketPriceIncrement, normalizeMarketPrice } from './MarketOrderForm'

// 未注册物品的坊市兜底元数据：名称/品类保留服务端数据，说明文本走 market 域词典。
function getMetadata(item: MarketInstrument, t: TFunction): ItemMetadata {
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

/** 挂单交易模态弹窗 Props */
interface MarketOrderModalProps {
  /** 弹窗展开状态 */
  open: boolean
  /** 挂单方向：卖出 sell / 买入 buy */
  side: 'sell' | 'buy'
  /** 交易标的 */
  instrument: MarketInstrument | null
  /** 当前盘口行情深度 */
  book: MarketBook | null
  /** 关闭弹窗回调 */
  onClose: () => void
  /** 挂单成功后的刷新回调 */
  onPlaced: () => void
}

/**
 * 坊市快速限价挂单模态弹窗组件 (MarketOrderModal)
 *
 * 核心功能与交易校验机制：
 * 1. 标的概览与预览：展示物品卡片、当前背包持存数量、持有灵石、系统回收底价与步长；
 * 2. 快捷填价按钮：支持一键填入盘口最高买价 (bestBid)、最低卖价 (bestAsk) 与系统底价；
 * 3. 数量调节器 (QuantitySelector) 与最大可买/可卖上限联动约束；
 * 4. 交易明细核算：
 *    - 买入：显示总价扣款与扣款后预计剩余灵石；
 *    - 卖出：显示总价、2% 卖家印花税与扣税后预计净收益灵石；
 * 5. 安全校验：阻断余额不足、持仓不足、价格低于底价等非法提交。
 */
export const MarketOrderModal = memo(function MarketOrderModal({
  open,
  side,
  instrument,
  book,
  onClose,
  onPlaced,
}: MarketOrderModalProps): React.JSX.Element | null {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const connection = useGameStore(state => state.connection)
  const setNotice = useGameStore(state => state.setNotice)

  const vendorPrice = Math.max(2, instrument?.sellPrice ?? 2)
  const minimumPrice = useMemo(() => normalizeMarketPrice(vendorPrice, vendorPrice, true), [vendorPrice])
  const [price, setPrice] = useState(String(minimumPrice))
  const [quantity, setQuantity] = useState(1)

  const bestAsk = book?.asks[0]?.price
  const bestBid = book?.bids[0]?.price
  const selectedAmount = instrument ? (inventory.find(item => item.itemId === instrument.itemId)?.amount ?? 0) : 0
  const spiritAmount = inventory.find(item => item.itemId === 'currency.spirit')?.amount ?? 0

  const parsedPrice = Number.parseInt(price, 10)
  const effectivePrice = Number.isSafeInteger(parsedPrice) && parsedPrice >= vendorPrice ? parsedPrice : minimumPrice

  const quantityLimit = useMemo(() => {
    if (side === 'sell') {
      return Math.max(1, selectedAmount)
    }
    const maxAffordable = Math.floor(spiritAmount / Math.max(1, effectivePrice))
    return Math.max(1, maxAffordable)
  }, [effectivePrice, selectedAmount, side, spiritAmount])

  useEffect(() => {
    if (!open || !instrument) return
    const initialPrice = side === 'sell'
      ? (bestAsk ?? minimumPrice)
      : (bestBid ?? minimumPrice)
    setPrice(String(Math.max(minimumPrice, initialPrice)))
    setQuantity(1)
  }, [bestAsk, bestBid, instrument, minimumPrice, open, side])

  const totalPrice = effectivePrice * quantity
  const tax = Math.ceil(totalPrice * 0.02)
  const netEarnings = Math.max(0, totalPrice - tax)

  const submitOrder = useCallback((event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!instrument) return

    const numPrice = Number.parseInt(price, 10)
    const numQuantity = quantity

    if (!Number.isSafeInteger(numPrice) || numPrice < vendorPrice || !Number.isSafeInteger(numQuantity) || numQuantity <= 0) {
      setNotice(getT()('market.error.invalidInput'))
      return
    }

    if (numPrice !== normalizeMarketPrice(numPrice, vendorPrice, false)) {
      setPrice(String(normalizeMarketPrice(numPrice, vendorPrice, true)))
      setNotice(getT()('market.error.priceAdjustedConfirm'))
      return
    }

    if (side === 'sell' && numQuantity > selectedAmount) {
      setNotice(getT()('market.error.sellExceedsBagStock'))
      return
    }

    if (side === 'buy' && numPrice * numQuantity > spiritAmount) {
      setNotice(getT()('market.error.insufficientSpirit'))
      return
    }

    const orderId = crypto.randomUUID()
    const success = gameClient.sendCommand('market.place', {
      orderId,
      itemId: instrument.itemId,
      side,
      price: numPrice,
      quantity: numQuantity,
      variant: null,
    })

    if (success) {
      setNotice(getT()(side === 'sell' ? 'market.notice.sellSubmitted' : 'market.notice.buySubmitted'))
      onClose()
      window.setTimeout(onPlaced, 120)
    }
  }, [instrument, onClose, onPlaced, price, quantity, selectedAmount, setNotice, side, spiritAmount, vendorPrice])

  if (!open || !instrument) return null

  const metadata = getMetadata(instrument, t)
  const titleId = `market-order-modal-title-${instrument.itemId}`

  return (
    <Modal
      className="market-order-modal-shell"
      closeLabel={t('common.close')}
      labelledBy={titleId}
      onClose={onClose}
      open={open}
      size="medium"
    >
      <header className="market-order-modal-header">
        <div className="market-order-modal-title-row">
          <span className={`market-order-badge market-order-badge--${side}`}>
            {side === 'sell' ? t('market.placeSell') : t('market.placeBuy')}
          </span>
          <h2 id={titleId}>{instrument.name}</h2>
          <span className="market-order-modal-category">{resolveItemCategoryLabel(instrument.category, t)} · {metadata.rarityLabel ?? t('market.fallbackGrade')}</span>
        </div>
      </header>

      <div className="market-order-modal-item-preview">
        <ItemTile
          amount={selectedAmount}
          category={resolveItemCategoryLabel(instrument.category, t)}
          className="inventory-cell"
          level={metadata.level}
          name={instrument.name}
          rarity={metadata.rarity}
        />
        <div className="market-order-modal-item-info">
          <div className="market-order-modal-meta-row">
            <span>{t('market.currentStock')}<strong>{formatNumber(selectedAmount)}</strong></span>
            <span>{t('market.spiritHeld')}<strong>{formatNumber(spiritAmount)}</strong></span>
          </div>
          <div className="market-order-modal-meta-row">
            <span>{t('market.vendorFloor')}{formatNumber(vendorPrice)}</span>
            <span>{t('market.tickSize')}{formatNumber(getMarketPriceIncrement(effectivePrice))}</span>
          </div>
        </div>
      </div>

      <form className="market-order-modal-form" onSubmit={submitOrder}>
        <div className="market-order-modal-field">
          <div className="market-order-modal-field-header">
            <label htmlFor="market-order-price-input">{t('market.unitPriceSpirit')}</label>
            <div className="market-order-quick-prices" role="group" aria-label={t('market.quickFillAria')}>
              {bestBid !== undefined && (
                <button
                  className="market-quick-btn market-quick-btn--bid"
                  onClick={() => setPrice(String(bestBid))}
                  type="button"
                >
                  {t('market.bidPrice', { price: formatNumber(bestBid) })}
                </button>
              )}
              {bestAsk !== undefined && (
                <button
                  className="market-quick-btn market-quick-btn--ask"
                  onClick={() => setPrice(String(bestAsk))}
                  type="button"
                >
                  {t('market.askPrice', { price: formatNumber(bestAsk) })}
                </button>
              )}
              <button
                className="market-quick-btn"
                onClick={() => setPrice(String(minimumPrice))}
                type="button"
              >
                {t('market.floorPrice', { price: formatNumber(minimumPrice) })}
              </button>
            </div>
          </div>
          <input
            id="market-order-price-input"
            min={minimumPrice}
            onBlur={() => {
              const parsed = Number.parseInt(price, 10)
              if (Number.isSafeInteger(parsed)) {
                setPrice(String(normalizeMarketPrice(parsed, vendorPrice, true)))
              }
            }}
            onChange={event => setPrice(event.target.value)}
            step={getMarketPriceIncrement(Number.parseInt(price, 10) || vendorPrice)}
            type="number"
            value={price}
          />
        </div>

        <div className="market-order-modal-field">
          <div className="market-order-modal-field-header">
            <label>{t('market.quantity')}</label>
            <span className="market-order-max-hint">
              {side === 'sell' ? t('market.maxSellable', { count: formatNumber(selectedAmount) }) : t('market.maxBuyable', { count: formatNumber(quantityLimit) })}
            </span>
          </div>
          <QuantitySelector
            ariaLabel={t('market.quantityAria')}
            compact={false}
            max={Math.max(1, quantityLimit)}
            onChange={setQuantity}
            presets={[1, 5, 10, 50, 100].filter(value => value <= quantityLimit)}
            value={Math.min(quantity, Math.max(1, quantityLimit))}
          />
        </div>

        <div className="market-order-summary-box">
          {side === 'buy' ? (
            <>
              <div className="market-order-summary-row">
                <span>{t('market.totalPrice')}</span>
                <strong>{formatNumber(totalPrice)} {t('market.spiritUnit')}</strong>
              </div>
              <div className="market-order-summary-row">
                <span>{t('market.debitLine')}</span>
                <span className="market-bid-text">-{formatNumber(totalPrice)} {t('market.spiritUnit')}</span>
              </div>
              <div className="market-order-summary-row">
                <span>{t('market.balanceAfter')}</span>
                <span>{formatNumber(Math.max(0, spiritAmount - totalPrice))} {t('market.spiritUnit')}</span>
              </div>
            </>
          ) : (
            <>
              <div className="market-order-summary-row">
                <span>{t('market.totalPrice')}</span>
                <strong>{formatNumber(totalPrice)} {t('market.spiritUnit')}</strong>
              </div>
              <div className="market-order-summary-row">
                <span>{t('market.taxLine')}</span>
                <span className="market-tax-text">-{formatNumber(tax)} {t('market.spiritUnit')}</span>
              </div>
              <div className="market-order-summary-row">
                <span>{t('market.netEarnings')}</span>
                <span className="market-ask-text">+{formatNumber(netEarnings)} {t('market.spiritUnit')}</span>
              </div>
            </>
          )}
        </div>

        <div className="market-order-modal-actions">
          <button className="market-order-btn-cancel" onClick={onClose} type="button">
            {t('common.cancel')}
          </button>
          <button
            className={`market-order-btn-submit market-order-btn-submit--${side}`}
            disabled={
              connection !== 'online' ||
              (side === 'sell' && (selectedAmount === 0 || quantity > selectedAmount)) ||
              (side === 'buy' && (spiritAmount < totalPrice || totalPrice <= 0))
            }
            type="submit"
          >
            {side === 'sell' ? t('market.confirmSell') : t('market.confirmBuy')}
          </button>
        </div>
      </form>
    </Modal>
  )
})
