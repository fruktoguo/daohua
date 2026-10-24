import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { QuantitySelector } from '../QuantitySelector'
import { formatNumber } from '../../game/format'
import { parseEnhancementVariant } from '../../game/ids'
import { getT, useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import type { MarketBook, MarketOrder } from '../../game/types'
import { fetchMarketBook } from '../../network/apiClient'
import { gameClient } from '../../network/gameClient'
import { ItemTile } from '../ItemDisplay'
import { Modal } from '../Modal'
import { getMarketPriceIncrement, isEquipmentInstrument, normalizeMarketPrice, resolveMarketInstrumentMetadata, type MarketInstrument } from './MarketOrderForm'

/** 挂单单价上限：防止 ×2 步进无限放大导致总价显示失真（服务端仅要求有限正数）。 */
const MARKET_ORDER_MAX_PRICE = 999_999_999


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
  /** 标的强化等级（0 = 基础变体；>0 对应 "+N" 变体盘口与钱包变体行） */
  enhancementLevel?: number
  /**
   * 该玩家当前全部活动挂单；弹窗内按「标的 + 当前强化等级」自行比对，任一命中即禁止再挂新单。
   * 服务端「同一玩家同一标的至多一笔活动挂单」的权威裁决不变，此处只做提交前置拦截。
   */
  existingOrders?: readonly MarketOrder[]
  /** 关闭弹窗回调 */
  onClose: () => void
  /** 挂单成功后的刷新回调 */
  onPlaced: () => void
  /** 跳转「我的订单」入口：已有活动挂单时引导玩家先撤销 */
  onShowOrders?: () => void
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
  enhancementLevel = 0,
  existingOrders,
  onClose,
  onPlaced,
  onShowOrders,
}: MarketOrderModalProps): React.JSX.Element | null {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const connection = useGameStore(state => state.connection)
  const setNotice = useGameStore(state => state.setNotice)
  const isEquipment = instrument !== null && isEquipmentInstrument(instrument)
  const [level, setLevel] = useState<number>(enhancementLevel)
  const initialLevelRef = useRef(enhancementLevel)

  // 当外部传入的 enhancementLevel 或弹窗重新展开时同步状态
  useEffect(() => {
    if (open) {
      setLevel(enhancementLevel)
      initialLevelRef.current = enhancementLevel
    }
  }, [enhancementLevel, open])

  // 标的当前盘口深度状态（支持在弹窗内切换强化等级后拉取新等级盘口）
  const [activeBook, setActiveBook] = useState<MarketBook | null>(() => {
    return book && book.enhancementLevel === enhancementLevel ? book : null
  })

  // 标的各强化等级背包持存列表（用于在表单头提供一键点选芯片）
  const heldLevels = useMemo(() => {
    if (!instrument || !isEquipment) return []
    const counts = new Map<number, number>()
    for (const item of inventory) {
      if (item.itemId !== instrument.itemId) continue
      const lvl = parseEnhancementVariant(item.variant)
      counts.set(lvl, (counts.get(lvl) ?? 0) + item.amount)
    }
    return Array.from(counts.entries())
      .map(([lvl, amount]) => ({ level: lvl, amount }))
      .sort((a, b) => a.level - b.level)
  }, [inventory, instrument, isEquipment])

  // 选定强化等级下的背包持存（数量随选中的强化等级动态读取）
  const selectedAmount = instrument
    ? (inventory.find(item => item.itemId === instrument.itemId && parseEnhancementVariant(item.variant) === level)?.amount ?? 0)
    : 0

  // 选定强化等级下的已穿戴件数（用于可卖量扣除）
  const equippedCount = useGameStore(
    state => state.combat?.equipment?.filter(
      entry => entry.itemId === instrument?.itemId && Math.floor(entry.enhancementLevel) === level
    ).length ?? 0
  )

  // 选定强化等级下真正可支配售卖的件数
  const availableAmount = Math.max(0, selectedAmount - equippedCount)
  const spiritAmount = inventory.find(item => item.itemId === 'currency.spirit-stone' && !item.variant)?.amount ?? 0

  // 该标的（含当前强化等级）已有的活动挂单：命中即禁止再挂新单（买/卖双向都拦）。
  // 与服务端「同一玩家同一标的至多一笔活动挂单」同口径：同 itemId 同 "+N" 变体视为同一标的。
  const blockingOrder = useMemo(
    () => (instrument === null
      ? null
      : (existingOrders ?? []).find(order => order.itemId === instrument.itemId && order.enhancementLevel === level) ?? null),
    [existingOrders, instrument, level],
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

  // 盘口对账：强化等级切换或宿主尚未下发盘口时，异步拉取当前等级深度簿。
  // 非装备标的也要自取一次——详情页轮询尚未落地时 activeBook 为空，买入侧「最大」
  // 会拿不到卖档深度而退化为余额上限，重新出现「一键全额排队求购」。
  useEffect(() => {
    if (!open || !instrument) return
    if (book && book.enhancementLevel === level) {
      setActiveBook(book)
      return
    }
    let cancelled = false
    fetchMarketBook(instrument.itemId, level).then(b => {
      if (cancelled || !b) return
      setActiveBook(b)
      if (level !== initialLevelRef.current) {
        const bookDefault = side === 'sell'
          ? (b.asks[0]?.price ?? minimumPrice)
          : (b.bids[0]?.price ?? minimumPrice)
        setPrice(Math.max(minimumPrice, normalizeMarketPrice(bookDefault, vendorPrice, side === 'buy')))
      }
    }).catch(() => {})
    return () => { cancelled = true }
  }, [book, instrument, level, minimumPrice, open, side, vendorPrice])

  // 选中的强化等级切换导致可用量变动时，卖出方向自动将挂售数量钳位在可用范围内
  useEffect(() => {
    if (side === 'sell') {
      setQuantity(prev => Math.max(1, availableAmount > 0 ? Math.min(prev, availableAmount) : 1))
    }
  }, [level, availableAmount, side])

  const bestAsk = activeBook?.asks[0]?.price
  const bestBid = activeBook?.bids[0]?.price
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
      return Math.max(1, availableAmount > 0 ? availableAmount : selectedAmount)
    }
    const maxAffordable = Math.floor(spiritAmount / Math.max(1, price))
    return Math.max(1, maxAffordable)
  }, [price, availableAmount, selectedAmount, side, spiritAmount])

  /**
   * 本价位可立即成交的卖盘总量：限价买单只吃报价不高于自身的卖档。
   * 盘口深度被截断时得到的只是下界，方向安全——宁可少算，绝不会多冻结灵石。
   */
  const fillableQuantity = useMemo(() => {
    if (side !== 'buy') return 0
    const asks = activeBook?.asks
    if (!asks || asks.length === 0) return 0
    let total = 0
    for (const level of asks) {
      if (level.price <= price) total += level.quantity
    }
    return Math.max(0, Math.floor(total))
  }, [activeBook, price, side])

  /**
   * 「最大」快捷钮取值：买入侧收敛到本价位可成交卖盘量，避免一键把余额全额冻结成排队求购单。
   * 该价位没有任何卖盘（报价低于卖一或盘口为空）时回退到余额可买量——此时整单排队本就是唯一结果。
   */
  const maxShortcutValue = side === 'buy'
    ? Math.max(1, Math.min(quantityLimit, fillableQuantity > 0 ? fillableQuantity : quantityLimit))
    : quantityLimit

  /** 买入侧成交拆分：立即撮合量 / 留在求购列表排队的剩余量。 */
  const immediateQuantity = side === 'buy' ? Math.min(quantity, fillableQuantity) : quantity
  const restingQuantity = side === 'buy' ? Math.max(0, quantity - fillableQuantity) : 0

  const totalPrice = price * quantity
  const tax = Math.ceil(totalPrice * 0.02)
  const netEarnings = Math.max(0, totalPrice - tax)

  const submitOrder = useCallback((event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!instrument) return

    // 同一标的至多一笔活动挂单：已有挂单时先引导去「我的订单」撤销，不再发送注定被拒的指令。
    if (blockingOrder) {
      setNotice(getT()('market.error.ownerOrderExists'))
      return
    }

    if (!Number.isSafeInteger(price) || price < vendorPrice || !Number.isSafeInteger(quantity) || quantity <= 0) {
      setNotice(getT()('market.error.invalidInput'))
      return
    }

    if (side === 'sell' && availableAmount <= 0) {
      if (equippedCount > 0) {
        setNotice(getT()('app.inventoryPanel.equippedHint'))
      } else {
        setNotice(getT()('market.error.sellExceedsBagStock'))
      }
      return
    }

    if (side === 'sell' && quantity > availableAmount) {
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
      // 与服务端变体口径一致："+N" 落强化等级簿，null 落基础簿（仅装备允许带强化变体）。
      variant: (isEquipment && level > 0) ? `+${level}` : null,
    })

    if (success) {
      setNotice(getT()(side === 'sell' ? 'market.notice.sellSubmitted' : 'market.notice.buySubmitted'))
      onClose()
      window.setTimeout(onPlaced, 120)
    }
  }, [availableAmount, blockingOrder, equippedCount, instrument, isEquipment, level, onClose, onPlaced, price, quantity, setNotice, side, spiritAmount, vendorPrice])

  if (!open || !instrument) return null

  const metadata = resolveMarketInstrumentMetadata(instrument, t)
  const titleId = `market-order-modal-title-${instrument.itemId}`
  // 标题与物品卡名称口径与背包变体行一致：强化变体展示 "名称 +N"。
  const displayName = (isEquipment && level > 0) ? `${instrument.name} +${level}` : instrument.name
  // 数量行右侧提示：买入侧同时交代「本价位能成交多少」与「余额最多能买多少」，
  // 盘口尚未同步时明确标注同步中，避免误报“暂无卖单”。
  const quantityHint = side === 'sell'
    ? t('market.maxSellable', { count: formatNumber(availableAmount) })
    : activeBook === null
      ? t('market.buyHintSyncing', { max: formatNumber(quantityLimit) })
      : fillableQuantity > 0
        ? t('market.buyHint', { fill: formatNumber(fillableQuantity), max: formatNumber(quantityLimit) })
        : t('market.buyHintNoFill', { max: formatNumber(quantityLimit) })

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
          <h2 id={titleId}>{displayName}</h2>
          <span className="market-order-modal-category">{metadata.category} · {metadata.rarityLabel ?? t('market.fallbackGrade')}</span>
        </div>
      </header>

      <div className="market-order-modal-item-preview">
        <ItemTile
          amount={selectedAmount}
          category={metadata.category}
          className="inventory-cell"
          enhancementLevel={isEquipment ? level : 0}
          itemId={instrument.itemId}
          level={metadata.level}
          name={displayName}
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
        {isEquipment && (
          <div className="market-order-modal-field">
            <div className="market-order-modal-field-header">
              <label>{t('market.enhancementLevel')}</label>
              {heldLevels.length > 0 && (
                <div className="market-order-quick-prices" role="group" aria-label={t('market.enhancementLevelAria')}>
                  {heldLevels.map(h => (
                    <button
                      className={`market-quick-btn ${level === h.level ? 'active' : ''}`}
                      key={h.level}
                      onClick={() => setLevel(h.level)}
                      type="button"
                    >
                      {h.level > 0 ? `+${h.level}` : t('market.enhance.noEnhancement')} ({h.amount})
                    </button>
                  ))}
                </div>
              )}
            </div>
            <QuantitySelector
              ariaLabel={t('market.enhancementLevelAria')}
              compact={false}
              disabled={connection !== 'online'}
              leadingPresets={[0]}
              max={20}
              min={0}
              onChange={setLevel}
              presets={[5, 7, 10, 12]}
              value={level}
            />
          </div>
        )}
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
            <span className="market-order-max-hint">{quantityHint}</span>
          </div>
          <QuantitySelector
            ariaLabel={t('market.quantityAria')}
            compact={false}
            leadingPresets={[1]}
            max={Math.max(1, quantityLimit)}
            maxShortcut
            maxShortcutValue={maxShortcutValue}
            onChange={setQuantity}
            value={Math.min(quantity, Math.max(1, quantityLimit))}
          />
        </div>

        <div className="market-order-summary-box">
          {side === 'buy' ? (
            <>
              <div className="market-order-summary-row">
                <span>{t('market.immediateFill')}</span>
                <span className="market-immediate-text">{formatNumber(immediateQuantity)}</span>
              </div>
              {restingQuantity > 0 && (
                <div className="market-order-summary-row">
                  <span>{t('market.restingBid')}</span>
                  <span className="market-resting-text">{formatNumber(restingQuantity)}</span>
                </div>
              )}
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

        {blockingOrder !== null && (
          <p className="market-order-rest-hint market-order-blocked-hint" role="alert">
            {t('market.ownerOrderHint', {
              side: blockingOrder.side === 'sell' ? t('market.side.sell') : t('market.side.buy'),
              count: formatNumber(blockingOrder.remainingQuantity),
            })}
            {onShowOrders !== undefined && (
              <button className="market-order-blocked-link" onClick={onShowOrders} type="button">
                {t('market.gotoMyOrders')}
              </button>
            )}
          </p>
        )}

        {side === 'buy' && restingQuantity > 0 && blockingOrder === null && (
          <p className="market-order-rest-hint" role="note">{t('market.restingBidHint')}</p>
        )}

        <div className="market-order-modal-actions">
          <button className="market-order-btn-cancel" onClick={onClose} type="button">
            {t('common.cancel')}
          </button>
          <button
            className={`market-order-btn-submit market-order-btn-submit--${side}`}
            disabled={
              connection !== 'online' ||
              blockingOrder !== null ||
              (side === 'sell' && (availableAmount <= 0 || quantity > availableAmount)) ||
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
