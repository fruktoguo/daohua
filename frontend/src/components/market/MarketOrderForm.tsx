import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { QuantitySelector } from '../QuantitySelector'
import { formatNumber } from '../../game/format'
import { getT, useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import type { CombatEquipmentDefinition, ItemDefinition, MarketBook } from '../../game/types'
import { gameClient } from '../../network/gameClient'

/**
 * 坊市交易标的元数据定义
 */
export interface MarketInstrument {
  /** 物品唯一 ID */
  itemId: string
  /** 物品名称 */
  name: string
  /** 物品品类 */
  category: string
  /** 子品类 (可选，如装备 slot 或材料细分类别) */
  subCategory?: string
  /** 物品等阶/品阶 */
  tier: number
  /** 系统商店回收底价 (Vendor Price) */
  sellPrice: number
  /** 服务端原始物品定义 (道具/材料) */
  definition?: ItemDefinition
  /** 服务端原始装备定义 (装备) */
  equipmentDefinition?: CombatEquipmentDefinition
}

/**
 * 根据价格区间动态计算坊市最小变动价格步长 (Tick Size)
 *
 * 步长规则：
 * - 低价位以 1 为基准；
 * - 随着价格数量级放大，按 1/2/5 阶梯逐步放宽报价精度，避免盘口档位过度碎片化。
 */
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

/**
 * 归一化挂单价格至合法的步长网格，并强制不得低于系统回收底价
 */
export function normalizeMarketPrice(price: number, vendorPrice: number, roundUp: boolean): number {
  if (price <= 1) return 2
  const increment = getMarketPriceIncrement(price)
  const remainder = price % increment
  let normalized = price - remainder
  if (normalized < vendorPrice) roundUp = true
  if (roundUp && remainder > 0) normalized += increment
  return Math.max(2, Math.trunc(normalized))
}

/** 挂单表单组件 Props */
interface MarketOrderFormProps {
  /** 挂单方向：买入 buy 或 卖出 sell */
  side: 'sell' | 'buy'
  /** 标的物品 ID */
  itemId: string
  /** 可交易标的列表 */
  instruments: readonly MarketInstrument[]
  /** 当前盘口行情数据 */
  book: MarketBook | null
  /** 挂单成功后的刷新回调 */
  onPlaced: () => void
}

/**
 * 坊市限价挂单表单嵌入组件 (MarketOrderForm)
 *
 * 处理单笔挂单的价格归一化校验、灵石/背包上限计算与向服务端发送 market.place 交易指令。
 */
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
  const selectedAmount = inventory.find(item => item.itemId === itemId && !item.variant)?.amount ?? 0
  const spiritAmount = inventory.find(item => item.itemId === 'currency.spirit-stone' && !item.variant)?.amount ?? 0
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

    // 订单 ID 由前端预生成：作为服务端撮合的幂等键，重发/重连不会产生重复挂单。
    const orderId = crypto.randomUUID()
    if (!gameClient.sendCommand('market.place', {
      orderId,
      itemId,
      side,
      price: parsedPrice,
      quantity: parsedQuantity,
      variant: null,
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
