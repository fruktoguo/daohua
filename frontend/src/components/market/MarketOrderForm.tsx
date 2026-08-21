import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { QuantitySelector } from '../QuantitySelector'
import { formatNumber } from '../../game/format'
import { getT, useT } from '../../i18n'
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
  const t = useT()
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
      setNotice(getT()('market.error.invalidInput'))
      return
    }

    if (parsedPrice !== normalizeMarketPrice(parsedPrice, vendorPrice, false)) {
      setPrice(String(normalizeMarketPrice(parsedPrice, vendorPrice, true)))
      setNotice(getT()('market.error.priceAdjusted'))
      return
    }

    if (parsedQuantity > quantityLimit) {
      setNotice(getT()('market.error.quantityExceedsLimit'))
      return
    }

    if (side === 'sell' && parsedQuantity > selectedAmount) {
      setNotice(getT()('market.error.sellExceedsStock'))
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
    setNotice(getT()('market.notice.orderSubmitted'))
    window.setTimeout(onPlaced, 120)
  }, [itemId, onPlaced, price, quantity, quantityLimit, selectedAmount, setNotice, side, vendorPrice])

  return (
    <form className="market-order-form" onSubmit={submitOrder}>
      <span className={`market-order-side-label market-order-side-label--${side}`}>
        {side === 'sell' ? t('market.side.sell') : t('market.side.buy')}
      </span>
      <label>
        {t('market.unitPrice')}
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
        <small>{t('market.priceHint', { min: formatNumber(minimumPrice), vendor: formatNumber(vendorPrice) })}</small>
      </label>
      <label>
        {t('market.quantity')}
        <QuantitySelector
          ariaLabel={t('market.orderQuantityAria')}
          compact
          max={quantityLimit}
          onChange={setQuantity}
          presets={[1, 10, 100].filter(value => value <= quantityLimit)}
          value={Math.min(quantity, quantityLimit)}
        />
      </label>
      <span className="market-order-hint">{side === 'sell' ? t('market.stockHint', { count: formatNumber(selectedAmount) }) : t('market.spiritHint', { amount: formatNumber(spiritAmount) })}</span>
      <div className="market-price-shortcuts" role="group" aria-label={t('market.quickPricesAria')}>
        <button disabled={bestAsk === undefined} onClick={() => bestAsk !== undefined && setPrice(String(bestAsk))} type="button">{t('market.bestAsk')}</button>
        <button disabled={bestBid === undefined} onClick={() => bestBid !== undefined && setPrice(String(bestBid))} type="button">{t('market.bestBid')}</button>
      </div>
      <button className="market-submit" disabled={connection !== 'online'} type="submit">
        {side === 'sell' ? t('market.submitSell') : t('market.submitBuy')}
      </button>
    </form>
  )
})
