import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { QuantitySelector } from '../QuantitySelector'
import { formatNumber } from '../../game/format'
import { ITEM_META, resolveItemGrade, withItemGrade, type ItemMetadata } from '../../game/ids'
import { useGameStore } from '../../game/gameStore'
import type { MarketBook } from '../../game/types'
import { gameClient } from '../../network/gameClient'
import { ItemTile } from '../ItemDisplay'
import { Modal } from '../Modal'
import type { MarketInstrument } from './MarketOrderForm'
import { getMarketPriceIncrement, normalizeMarketPrice } from './MarketOrderForm'

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

interface MarketOrderModalProps {
  open: boolean
  side: 'sell' | 'buy'
  instrument: MarketInstrument | null
  book: MarketBook | null
  onClose: () => void
  onPlaced: () => void
}

export const MarketOrderModal = memo(function MarketOrderModal({
  open,
  side,
  instrument,
  book,
  onClose,
  onPlaced,
}: MarketOrderModalProps): React.JSX.Element | null {
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
      setNotice('请输入有效的价格和数量。')
      return
    }

    if (numPrice !== normalizeMarketPrice(numPrice, vendorPrice, false)) {
      setPrice(String(normalizeMarketPrice(numPrice, vendorPrice, true)))
      setNotice('价格已调整为当前市场档位，请再次确认提交。')
      return
    }

    if (side === 'sell' && numQuantity > selectedAmount) {
      setNotice('挂牌数量超过当前背包库存。')
      return
    }

    if (side === 'buy' && numPrice * numQuantity > spiritAmount) {
      setNotice('所需灵石超过当前持有量。')
      return
    }

    const orderId = `market-${crypto.randomUUID()}`
    const success = gameClient.sendCommand('market_place', {
      orderId,
      itemId: instrument.itemId,
      side,
      price: numPrice,
      quantity: numQuantity,
      enhancementLevel: 0,
    })

    if (success) {
      setNotice(side === 'sell' ? '挂售订单已提交，等待撮合。' : '求购订单已提交，等待撮合。')
      onClose()
      window.setTimeout(onPlaced, 120)
    }
  }, [instrument, onClose, onPlaced, price, quantity, selectedAmount, setNotice, side, spiritAmount, vendorPrice])

  if (!open || !instrument) return null

  const metadata = getMetadata(instrument)
  const titleId = `market-order-modal-title-${instrument.itemId}`

  return (
    <Modal
      className="market-order-modal-shell"
      closeLabel="关闭"
      labelledBy={titleId}
      onClose={onClose}
      open={open}
      size="medium"
    >
      <header className="market-order-modal-header">
        <div className="market-order-modal-title-row">
          <span className={`market-order-badge market-order-badge--${side}`}>
            {side === 'sell' ? '发起挂售' : '发起求购'}
          </span>
          <h2 id={titleId}>{instrument.name}</h2>
          <span className="market-order-modal-category">{instrument.category} · {metadata.rarityLabel ?? '一阶'}</span>
        </div>
      </header>

      <div className="market-order-modal-item-preview">
        <ItemTile
          amount={selectedAmount}
          category={instrument.category}
          className="inventory-cell"
          level={metadata.level}
          name={instrument.name}
          rarity={metadata.rarity}
        />
        <div className="market-order-modal-item-info">
          <div className="market-order-modal-meta-row">
            <span>当前库存：<strong>{formatNumber(selectedAmount)}</strong></span>
            <span>持有灵石：<strong>{formatNumber(spiritAmount)}</strong></span>
          </div>
          <div className="market-order-modal-meta-row">
            <span>回收保底：{formatNumber(vendorPrice)}</span>
            <span>档位步长：{formatNumber(getMarketPriceIncrement(effectivePrice))}</span>
          </div>
        </div>
      </div>

      <form className="market-order-modal-form" onSubmit={submitOrder}>
        <div className="market-order-modal-field">
          <div className="market-order-modal-field-header">
            <label htmlFor="market-order-price-input">单价（灵石）</label>
            <div className="market-order-quick-prices" role="group" aria-label="快捷填价">
              {bestBid !== undefined && (
                <button
                  className="market-quick-btn market-quick-btn--bid"
                  onClick={() => setPrice(String(bestBid))}
                  type="button"
                >
                  买一 {formatNumber(bestBid)}
                </button>
              )}
              {bestAsk !== undefined && (
                <button
                  className="market-quick-btn market-quick-btn--ask"
                  onClick={() => setPrice(String(bestAsk))}
                  type="button"
                >
                  卖一 {formatNumber(bestAsk)}
                </button>
              )}
              <button
                className="market-quick-btn"
                onClick={() => setPrice(String(minimumPrice))}
                type="button"
              >
                底价 {formatNumber(minimumPrice)}
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
            <label>数量</label>
            <span className="market-order-max-hint">
              {side === 'sell' ? `最大可挂售 ${formatNumber(selectedAmount)}` : `最大可求购 ${formatNumber(quantityLimit)}`}
            </span>
          </div>
          <QuantitySelector
            ariaLabel="订单数量"
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
                <span>订单总价</span>
                <strong>{formatNumber(totalPrice)} 灵石</strong>
              </div>
              <div className="market-order-summary-row">
                <span>交易扣款</span>
                <span className="market-bid-text">-{formatNumber(totalPrice)} 灵石</span>
              </div>
              <div className="market-order-summary-row">
                <span>灵石结余预估</span>
                <span>{formatNumber(Math.max(0, spiritAmount - totalPrice))} 灵石</span>
              </div>
            </>
          ) : (
            <>
              <div className="market-order-summary-row">
                <span>订单总价</span>
                <strong>{formatNumber(totalPrice)} 灵石</strong>
              </div>
              <div className="market-order-summary-row">
                <span>交易税 (2%)</span>
                <span className="market-tax-text">-{formatNumber(tax)} 灵石</span>
              </div>
              <div className="market-order-summary-row">
                <span>预计净收益</span>
                <span className="market-ask-text">+{formatNumber(netEarnings)} 灵石</span>
              </div>
            </>
          )}
        </div>

        <div className="market-order-modal-actions">
          <button className="market-order-btn-cancel" onClick={onClose} type="button">
            取消
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
            {side === 'sell' ? '确认挂售' : '确认求购'}
          </button>
        </div>
      </form>
    </Modal>
  )
})
