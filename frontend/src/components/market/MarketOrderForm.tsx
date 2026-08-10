import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { QuantitySelector } from '../QuantitySelector'
import { formatNumber } from '../../game/format'
import { useGameStore } from '../../game/gameStore'
import type { ItemDefinition, MarketBook } from '../../game/types'
import { gameClient } from '../../network/gameClient'

export interface MarketInstrument {
  itemId: string
  name: string
  category: string
  tier: number
  sellPrice: number
  definition: ItemDefinition
}

export function getMarketPriceIncrement(price: number): number {
  if (price <= 1) return 1
  let firstDigit = Math.floor(price)
  let magnitude = 1
  while (firstDigit >= 10) {
    firstDigit = Math.floor(firstDigit / 10)
    magnitude *= 10
  }

  if ((firstDigit === 1 || firstDigit === 2) && magnitude >= 100) return 5 * (magnitude / 100)
  if ((firstDigit === 3 || firstDigit === 4) && magnitude >= 10) return magnitude / 10
  if (firstDigit >= 5 && magnitude >= 10) return 2 * (magnitude / 10)
  return 1
}

export function normalizeMarketPrice(price: number, vendorPrice: number, roundUp: boolean): number {
  if (price <= 1) return 2
  const increment = getMarketPriceIncrement(price)
  const remainder = price % increment
  let normalized = price - remainder
  if (normalized < vendorPrice) roundUp = true
  if (roundUp && remainder > 0) normalized += increment
  return Math.max(2, Math.trunc(normalized))
}

interface MarketOrderFormProps {
  side: 'sell' | 'buy'
  itemId: string
  instruments: readonly MarketInstrument[]
  book: MarketBook | null
  onPlaced: () => void
}

export const MarketOrderForm = memo(function MarketOrderForm({
  side,
  itemId,
  instruments,
  book,
  onPlaced,
}: MarketOrderFormProps): React.JSX.Element {
  const inventory = useGameStore(state => state.inventory)
  const connection = useGameStore(state => state.connection)
  const setNotice = useGameStore(state => state.setNotice)
  const selected = instruments.find(item => item.itemId === itemId)
  const vendorPrice = Math.max(2, selected?.sellPrice ?? 2)
  const minimumPrice = useMemo(() => normalizeMarketPrice(vendorPrice, vendorPrice, true), [vendorPrice])
  const [price, setPrice] = useState(String(minimumPrice))
  const [quantity, setQuantity] = useState(1)
  const bestAsk = book?.asks[0]?.price
  const bestBid = book?.bids[0]?.price
  const selectedAmount = inventory.find(item => item.itemId === itemId)?.amount ?? 0
  const spiritAmount = inventory.find(item => item.itemId === 'currency.spirit')?.amount ?? 0
  const quantityLimit = side === 'sell'
    ? Math.max(1, selectedAmount)
    : Math.max(1, Math.floor(spiritAmount / Math.max(1, Number.parseInt(price, 10) || vendorPrice)))

  useEffect(() => {
    setPrice(current => {
      const parsed = Number.parseInt(current, 10)
      return !Number.isSafeInteger(parsed) || parsed < vendorPrice
        ? String(minimumPrice)
        : current
    })
  }, [itemId, minimumPrice, vendorPrice])

  const submitOrder = useCallback((event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const parsedPrice = Number.parseInt(price, 10)
    const parsedQuantity = quantity
    if (!Number.isSafeInteger(parsedPrice) || parsedPrice < vendorPrice || !Number.isSafeInteger(parsedQuantity) || parsedQuantity <= 0) {
      setNotice('请输入有效的价格和数量。')
      return
    }

    if (parsedPrice !== normalizeMarketPrice(parsedPrice, vendorPrice, false)) {
      setPrice(String(normalizeMarketPrice(parsedPrice, vendorPrice, true)))
      setNotice('价格已调整为当前市场档位，请再次提交。')
      return
    }

    if (parsedQuantity > quantityLimit) {
      setNotice('订单数量超过当前可用上限。')
      return
    }

    if (side === 'sell' && parsedQuantity > selectedAmount) {
      setNotice('挂牌数量超过当前库存。')
      return
    }

    const orderId = `market-${crypto.randomUUID()}`
    if (!gameClient.sendCommand('market_place', {
      orderId,
      itemId,
      side,
      price: parsedPrice,
      quantity: parsedQuantity,
      enhancementLevel: 0,
    })) return
    setNotice('订单已提交，等待服务端撮合。')
    window.setTimeout(onPlaced, 120)
  }, [itemId, onPlaced, price, quantity, quantityLimit, selectedAmount, setNotice, side, vendorPrice])

  return (
    <form className="market-order-form" onSubmit={submitOrder}>
      <span className={`market-order-side-label market-order-side-label--${side}`}>
        {side === 'sell' ? '出售' : '求购'}
      </span>
      <label>
        单价
        <input
          min={minimumPrice}
          onBlur={() => {
            const parsed = Number.parseInt(price, 10)
            if (Number.isSafeInteger(parsed)) setPrice(String(normalizeMarketPrice(parsed, vendorPrice, true)))
          }}
          onChange={event => setPrice(event.target.value)}
          step={getMarketPriceIncrement(Number.parseInt(price, 10) || vendorPrice)}
          type="number"
          value={price}
        />
        <small>最低 {formatNumber(minimumPrice)} · 回收价 {formatNumber(vendorPrice)}</small>
      </label>
      <label>
        数量
        <QuantitySelector
          ariaLabel="市场订单数量"
          compact
          max={quantityLimit}
          onChange={setQuantity}
          presets={[1, 10, 100].filter(value => value <= quantityLimit)}
          value={Math.min(quantity, quantityLimit)}
        />
      </label>
      <span className="market-order-hint">{side === 'sell' ? `库存 ${formatNumber(selectedAmount)}` : `灵石 ${formatNumber(spiritAmount)}`}</span>
      <div className="market-price-shortcuts" role="group" aria-label="盘口价格快捷设置">
        <button disabled={bestAsk === undefined} onClick={() => bestAsk !== undefined && setPrice(String(bestAsk))} type="button">卖一</button>
        <button disabled={bestBid === undefined} onClick={() => bestBid !== undefined && setPrice(String(bestBid))} type="button">买一</button>
      </div>
      <button className="market-submit" disabled={connection !== 'online'} type="submit">
        {side === 'sell' ? '挂牌出售' : '发布求购'}
      </button>
    </form>
  )
})
