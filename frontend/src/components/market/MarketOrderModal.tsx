import { memo, useCallback, useMemo, useState } from 'react'
import { QuantitySelector } from '../QuantitySelector'
import { formatNumber } from '../../game/format'
import { resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, resolveItemSubCategory, withItemGrade, type ItemMetadata } from '../../game/ids'
import { getT, useT, type TFunction } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import type { MarketBook } from '../../game/types'
import { gameClient } from '../../network/gameClient'
import { ItemTile } from '../ItemDisplay'
import { Modal } from '../Modal'
import type { MarketInstrument } from './MarketOrderForm'
import { getMarketPriceIncrement, normalizeMarketPrice } from './MarketOrderForm'

/** 挂单单价上限：防止 ×2 步进无限放大导致总价显示失真（服务端仅要求有限正数）。 */
const MARKET_ORDER_MAX_PRICE = 999_999_999

// 未注册物品的坊市兜底元数据：名称/品类保留服务端数据，说明文本走 market 域词典。
function getMetadata(item: MarketInstrument, t: TFunction): ItemMetadata {
  const subCategoryLabel = resolveItemCategoryLabel(
    resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.subCategory || item.category), t)
  if (item.equipmentDefinition || item.category === 'equipment' || item.itemId.startsWith('equipment.')) {
    const tier = Math.max(1, item.tier)
    const grade = resolveItemGrade(tier)
    return {
      label: item.name,
      glyph: '⚔️',
      tone: 'gold',
      category: subCategoryLabel,
      rarity: grade.rarity,
      rarityLabel: grade.label,
      level: tier,
      description: item.equipmentDefinition ? '' : t('market.itemFallback.description'),
    }
  }
  const registered = resolveItemMetadata(item.itemId)
  if (!registered) {
    const tier = Math.max(1, item.tier)
    const grade = resolveItemGrade(tier)
    return {
      label: item.name,
      glyph: item.name.slice(0, 1),
      tone: 'gold',
      category: subCategoryLabel,
      rarity: grade.rarity,
      rarityLabel: grade.label,
      level: tier,
      description: t('market.itemFallback.description'),
    }
  }
  return withItemGrade({
    ...registered,
    category: subCategoryLabel,
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
  /** 盘口快捷入口锁定的开单价格（买一卖出/卖一买入），优先于盘口默认初值 */
  initialPrice?: number
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
  initialPrice,
  onClose,
  onPlaced,
}: MarketOrderModalProps): React.JSX.Element | null {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const connection = useGameStore(state => state.connection)
  const setNotice = useGameStore(state => state.setNotice)
  // 已穿戴装备仍计钱包持存（selectedAmount 会包含它），卖出方向须整体拦截并提示先卸下。
  const isInstrumentEquipped = useGameStore(
    state => state.combat?.equipment?.some(entry => entry.itemId === instrument?.itemId) ?? false
  )

  const vendorPrice = Math.max(2, instrument?.sellPrice ?? 2)
  const minimumPrice = useMemo(() => normalizeMarketPrice(vendorPrice, vendorPrice, true), [vendorPrice])
  // 单价为步进按钮维护的整数，任何路径写入前都先落到 tick 网格并夹在 [minimumPrice, MARKET_ORDER_MAX_PRICE]。
  // 初值按打开瞬间的盘口最优价惰性初始化一次；之后行情轮询刷新不得重置用户输入（保持焦点与草稿稳定）。
  // 快捷入口锁定价（initialPrice）按方向归一化：买入向上取整、卖出向下取整，保证落价能立即撮合对手档。
  const [price, setPrice] = useState<number>(() => {
    const bookDefault = side === 'sell'
      ? (book?.asks[0]?.price ?? minimumPrice)
      : (book?.bids[0]?.price ?? minimumPrice)
    return Math.max(minimumPrice, normalizeMarketPrice(initialPrice ?? bookDefault, vendorPrice, side === 'buy'))
  })
  const [quantity, setQuantity] = useState(1)

  const bestAsk = book?.asks[0]?.price
  const bestBid = book?.bids[0]?.price
  // 坊市只交易基础变体（挂单冻结空 variant 资产），可卖量/灵石不能混入强化变体行
  const selectedAmount = instrument ? (inventory.find(item => item.itemId === instrument.itemId && !item.variant)?.amount ?? 0) : 0
  const spiritAmount = inventory.find(item => item.itemId === 'currency.spirit-stone' && !item.variant)?.amount ?? 0

  /** 步进调价：−/+ 按当前价位 tick 步长走一档，÷2/×2 折半翻倍后归一化到价格网格。 */
  const adjustPrice = useCallback((action: 'half' | 'decrease' | 'increase' | 'double') => {
    setPrice(current => {
      switch (action) {
        case 'double':
          return Math.min(MARKET_ORDER_MAX_PRICE, normalizeMarketPrice(current * 2, vendorPrice, true))
        case 'half':
          return Math.max(minimumPrice, normalizeMarketPrice(current / 2, vendorPrice, false))
        case 'increase':
          return Math.min(MARKET_ORDER_MAX_PRICE, normalizeMarketPrice(current + getMarketPriceIncrement(current), vendorPrice, true))
        case 'decrease': {
          const step = getMarketPriceIncrement(Math.max(minimumPrice, current - 1))
          return Math.max(minimumPrice, normalizeMarketPrice(current - step, vendorPrice, false))
        }
      }
    })
  }, [minimumPrice, vendorPrice])

  const applyPricePreset = useCallback((preset: number) => {
    setPrice(Math.min(MARKET_ORDER_MAX_PRICE, Math.max(minimumPrice, normalizeMarketPrice(preset, vendorPrice, true))))
  }, [minimumPrice, vendorPrice])

  const quantityLimit = useMemo(() => {
    if (side === 'sell') {
      return Math.max(1, selectedAmount)
    }
    const maxAffordable = Math.floor(spiritAmount / Math.max(1, price))
    return Math.max(1, maxAffordable)
  }, [price, selectedAmount, side, spiritAmount])

  const totalPrice = price * quantity
  const tax = Math.ceil(totalPrice * 0.02)
  const netEarnings = Math.max(0, totalPrice - tax)

  const submitOrder = useCallback((event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!instrument) return

    if (!Number.isSafeInteger(price) || price < vendorPrice || !Number.isSafeInteger(quantity) || quantity <= 0) {
      setNotice(getT()('market.error.invalidInput'))
      return
    }

    if (side === 'sell' && isInstrumentEquipped) {
      setNotice(getT()('app.inventoryPanel.equippedHint'))
      return
    }

    if (side === 'sell' && quantity > selectedAmount) {
      setNotice(getT()('market.error.sellExceedsBagStock'))
      return
    }

    if (side === 'buy' && price * quantity > spiritAmount) {
      setNotice(getT()('market.error.insufficientSpirit'))
      return
    }

    const orderId = crypto.randomUUID()
    const success = gameClient.sendCommand('market.place', {
      orderId,
      itemId: instrument.itemId,
      side,
      price,
      quantity,
      variant: null,
    })

    if (success) {
      setNotice(getT()(side === 'sell' ? 'market.notice.sellSubmitted' : 'market.notice.buySubmitted'))
      onClose()
      window.setTimeout(onPlaced, 120)
    }
  }, [instrument, isInstrumentEquipped, onClose, onPlaced, price, quantity, selectedAmount, setNotice, side, spiritAmount, vendorPrice])

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
          <span className="market-order-modal-category">{metadata.category} · {metadata.rarityLabel ?? t('market.fallbackGrade')}</span>
        </div>
      </header>

      <div className="market-order-modal-item-preview">
        <ItemTile
          amount={selectedAmount}
          category={metadata.category}
          className="inventory-cell"
          itemId={instrument.itemId}
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
            <span>{t('market.tickSize')}{formatNumber(getMarketPriceIncrement(price))}</span>
          </div>
        </div>
      </div>

      <form className="market-order-modal-form" onSubmit={submitOrder}>
        <div className="market-order-modal-field">
          <div className="market-order-modal-field-header">
            <label>{t('market.unitPriceSpirit')}</label>
            <div className="market-order-quick-prices" role="group" aria-label={t('market.quickFillAria')}>
              {bestBid !== undefined && (
                <button
                  className="market-quick-btn market-quick-btn--bid"
                  onClick={() => applyPricePreset(bestBid)}
                  type="button"
                >
                  {t('market.bidPrice', { price: formatNumber(bestBid) })}
                </button>
              )}
              {bestAsk !== undefined && (
                <button
                  className="market-quick-btn market-quick-btn--ask"
                  onClick={() => applyPricePreset(bestAsk)}
                  type="button"
                >
                  {t('market.askPrice', { price: formatNumber(bestAsk) })}
                </button>
              )}
              <button
                className="market-quick-btn"
                onClick={() => applyPricePreset(minimumPrice)}
                type="button"
              >
                {t('market.floorPrice', { price: formatNumber(minimumPrice) })}
              </button>
            </div>
          </div>
          {/* 价格步进控件：左侧 ÷2/−，中央只读价格显示，右侧 +/×2；−/+ 走 tick 档位，÷2/×2 折半翻倍后归一网格。 */}
          <div className="market-order-price-stepper" role="group" aria-label={t('market.unitPriceSpirit')}>
            <div className="market-order-price-stepper-side">
              <button
                aria-label={t('market.priceStepHalveAria')}
                className="market-order-price-step-btn"
                disabled={price <= minimumPrice}
                onClick={() => adjustPrice('half')}
                type="button"
              >÷2</button>
              <button
                aria-label={t('market.priceStepDecreaseAria')}
                className="market-order-price-step-btn"
                disabled={price <= minimumPrice}
                onClick={() => adjustPrice('decrease')}
                type="button"
              >−</button>
            </div>
            <div className="market-order-price-display" aria-live="polite">
              <strong>{formatNumber(price)}</strong>
              <span>{t('market.spiritUnit')}</span>
            </div>
            <div className="market-order-price-stepper-side">
              <button
                aria-label={t('market.priceStepIncreaseAria')}
                className="market-order-price-step-btn"
                disabled={price >= MARKET_ORDER_MAX_PRICE}
                onClick={() => adjustPrice('increase')}
                type="button"
              >+</button>
              <button
                aria-label={t('market.priceStepDoubleAria')}
                className="market-order-price-step-btn"
                disabled={price >= MARKET_ORDER_MAX_PRICE}
                onClick={() => adjustPrice('double')}
                type="button"
              >×2</button>
            </div>
          </div>
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
            leadingPresets={[1]}
            max={Math.max(1, quantityLimit)}
            maxShortcut
            onChange={setQuantity}
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
              (side === 'sell' && (isInstrumentEquipped || selectedAmount === 0 || quantity > selectedAmount)) ||
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
