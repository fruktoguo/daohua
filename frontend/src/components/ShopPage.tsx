import { memo, useCallback, useEffect, useMemo, useRef, useState, useId } from 'react'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { resolveItemCategoryLabel, resolveItemMetadata, resolveItemSubCategory } from '../game/ids'
import type { ItemDefinition, ShopOffer, ShopState } from '../game/types'
import { useT } from '../i18n'
import { fetchShopState } from '../network/apiClient'
import { gameClient } from '../network/gameClient'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { Modal } from './Modal'
import { QuantitySelector } from './QuantitySelector'

/** 绑定功德与流通功德资产 ID */
const BOUND_MERIT_ITEM_ID = 'item.gongde-bound'
const NORMAL_MERIT_ITEM_ID = 'item.gongde'

/** 单次购买件数硬上限：与服务端 ShopPurchaseRules.MaxQuantityPerPurchase 对齐 */
const MAX_PURCHASE_QUANTITY = 999
/** 购买成功后等待钱包增量回流的兜底重拉间隔（毫秒）：余额未变化时仍能在该窗口后对账限购进度 */
const PURCHASE_FALLBACK_REFRESH_MS = 2_500

interface ShopItemSlotProps {
  /** 商品定义与当日限购进度 */
  offer: ShopOffer
  /** 服务端物品定义（用于悬浮详情与品类丝带） */
  definition: ItemDefinition | undefined
  /** 点击条目呼出购买弹窗 */
  onSelect: (offer: ShopOffer) => void
}

/**
 * 紧凑商品格子条目：
 * 上部为物品 Item 格子（支持 Hover 触发标准物品效果详情卡片与限购角标），
 * 底部为等宽金色功德价格。
 */
const ShopItemSlot = memo(function ShopItemSlot({
  offer,
  definition,
  onSelect,
}: ShopItemSlotProps): React.JSX.Element {
  const t = useT()
  const metadata = resolveItemMetadata(offer.itemId)
  const name = metadata?.label ?? definition?.name ?? offer.itemId
  const limited = offer.dailyLimit !== null
  const remaining = limited ? Math.max(0, (offer.dailyLimit ?? 0) - offer.purchasedToday) : Number.POSITIVE_INFINITY
  const soldOut = limited && remaining <= 0

  return (
    <div
      aria-label={`${name} ${formatNumber(offer.unitPrice)} ${t('shop.meritUnit')}`}
      className={`shop-item-slot${soldOut ? ' is-sold-out' : ''}`}
      onClick={() => onSelect(offer)}
      onKeyDown={e => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onSelect(offer)
        }
      }}
      role="button"
      tabIndex={0}
    >
      <div className="shop-slot-visual">
        <ItemTooltipTarget
          className="shop-item-target"
          definition={definition}
          itemId={offer.itemId}
          metadata={metadata}
        >
          <div className="shop-item-tile-wrap">
            <ItemTile
              category={resolveItemCategoryLabel(
                resolveItemSubCategory(offer.itemId, definition, undefined, metadata?.category), t)}
              className="inventory-cell"
              itemId={offer.itemId}
              level={metadata?.level ?? definition?.tier}
              name={name}
              rarity={metadata?.rarity ?? 'yellow'}
            />
            {limited && (
              <span className={`shop-slot-badge${soldOut ? ' is-sold-out' : ''}`}>
                {soldOut ? t('shop.soldOut') : t('shop.dailyLimitTag', { limit: offer.dailyLimit ?? 0 })}
              </span>
            )}
          </div>
        </ItemTooltipTarget>
      </div>

      <div className="shop-slot-price">
        <span className="shop-price-glyph" aria-hidden="true">功</span>
        <span className="shop-price-val">{formatNumber(offer.unitPrice)}</span>
      </div>
    </div>
  )
})

interface ShopBuyModalProps {
  /** 弹窗是否开启 */
  open: boolean
  /** 当前选中的商品 */
  offer: ShopOffer | null
  /** 服务端物品定义 */
  definition: ItemDefinition | undefined
  /** 当前已拥有件数 */
  ownedAmount: number
  /** 玩家功德总余额 */
  totalMerit: number
  /** 绑定功德余额 */
  boundMerit: number
  /** 流通功德余额 */
  normalMerit: number
  /** 是否正在发送购买请求 */
  pending: boolean
  /** 关闭弹窗 */
  onClose: () => void
  /** 确认购买 */
  onConfirm: (offer: ShopOffer, quantity: number) => void
}

/**
 * 独立二次确认购买弹窗 (ShopBuyModal)
 *
 * 点击物品格子后弹出：展示物品完整信息与悬浮详情、限购进度、单价、数量选择器、
 * 功德余额对比与确认购买操作。
 */
const ShopBuyModal = memo(function ShopBuyModal({
  open,
  offer,
  definition,
  ownedAmount,
  totalMerit,
  boundMerit,
  normalMerit,
  pending,
  onClose,
  onConfirm,
}: ShopBuyModalProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const [quantity, setQuantity] = useState(1)

  // 当弹窗打开或切换商品时，重置购买数量为 1
  useEffect(() => {
    if (open && offer) {
      setQuantity(1)
    }
  }, [open, offer?.offerId])

  if (!open || !offer) return null

  const metadata = resolveItemMetadata(offer.itemId)
  const name = metadata?.label ?? definition?.name ?? offer.itemId
  const limited = offer.dailyLimit !== null
  const remaining = limited ? Math.max(0, (offer.dailyLimit ?? 0) - offer.purchasedToday) : Number.POSITIVE_INFINITY
  const soldOut = limited && remaining <= 0

  const maxAffordable = Math.floor(totalMerit / Math.max(1, offer.unitPrice))
  const buyableQuantity = limited ? 1 : Math.max(1, Math.min(MAX_PURCHASE_QUANTITY, maxAffordable))
  const effectiveQuantity = limited ? 1 : Math.max(1, Math.min(quantity, buyableQuantity))
  const totalPrice = offer.unitPrice * effectiveQuantity
  const insufficient = totalPrice > totalMerit
  const disabled = pending || soldOut || insufficient || maxAffordable < 1

  return (
    <Modal labelledBy={titleId} onClose={onClose} open={open} size="small">
      <div className="shop-buy-modal">
        <header className="shop-modal-header">
          <h2 id={titleId} className="shop-modal-title">{t('shop.confirmModalTitle')}</h2>
        </header>

        <div className="shop-modal-item-showcase">
          <ItemTooltipTarget
            className="shop-item-target"
            definition={definition}
            itemId={offer.itemId}
            metadata={metadata}
          >
            <div className="shop-item-tile-wrap">
              <ItemTile
                category={resolveItemCategoryLabel(
                  resolveItemSubCategory(offer.itemId, definition, undefined, metadata?.category), t)}
                className="inventory-cell"
                itemId={offer.itemId}
                level={metadata?.level ?? definition?.tier}
                name={name}
                rarity={metadata?.rarity ?? 'yellow'}
              />
            </div>
          </ItemTooltipTarget>

          <span className="shop-modal-item-name">{name}</span>
          <div className="shop-modal-item-sub">
            <span>{t('shop.owned', { amount: formatNumber(ownedAmount) })}</span>
            {limited && (
              <span className={`shop-modal-limit-note${soldOut ? ' is-exhausted' : ''}`}>
                {soldOut ? t('shop.soldOut') : t('shop.dailyProgress', { purchased: offer.purchasedToday, limit: offer.dailyLimit ?? 0 })}
              </span>
            )}
          </div>
        </div>

        <div className="shop-modal-details">
          <div className="shop-modal-row">
            <span className="shop-modal-row-label">{t('shop.unitPrice')}</span>
            <span className="shop-modal-row-val">{formatNumber(offer.unitPrice)} {t('shop.meritUnit')}</span>
          </div>

          {limited ? null : (
            <div className="shop-modal-row">
              <span className="shop-modal-row-label">{t('shop.quantity')}</span>
              <QuantitySelector
                ariaLabel={t('shop.quantityAria')}
                compact
                disabled={pending || soldOut}
                max={buyableQuantity}
                maxShortcut
                onChange={setQuantity}
                presets={[1, 5, 10]}
                value={effectiveQuantity}
              />
            </div>
          )}

          <div className="shop-modal-row is-total">
            <span className="shop-modal-row-label">{t('shop.payable')}</span>
            <span className={`shop-modal-row-val${insufficient ? ' is-insufficient' : ''}`}>
              {formatNumber(totalPrice)} {t('shop.meritUnit')}
            </span>
          </div>

          <div className="shop-modal-row">
            <span className="shop-modal-row-label">{t('shop.meritBalance')}</span>
            <span
              className={`shop-modal-row-val${insufficient ? ' is-insufficient' : ''}`}
              title={t('shop.meritDetails', { bound: formatNumber(boundMerit), normal: formatNumber(normalMerit) })}
            >
              {formatNumber(totalMerit)} {t('shop.meritUnit')}
            </span>
          </div>
        </div>

        <div className="shop-modal-actions">
          <button onClick={onClose} type="button">
            {t('common.cancel')}
          </button>
          <button
            className="shop-modal-confirm-btn"
            disabled={disabled}
            onClick={() => onConfirm(offer, effectiveQuantity)}
            type="button"
          >
            {pending ? t('shop.buying') : soldOut ? t('shop.soldOut') : insufficient ? t('shop.insufficient') : t('shop.confirmBuy')}
          </button>
        </div>
      </div>
    </Modal>
  )
})

/**
 * 系统商店页：官价直售货架与每日限购进度。
 *
 * 价格、限购额度与发货全部由服务端裁决：本页只拉取货架状态、展示可购量与提交 `shop.buy`，
 * 购买成功后以服务端返回的当日已购件数为准重拉，不在本地自增限购计数。
 */
export function ShopPage(): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const inventory = useGameStore(state => state.inventory)
  const catalogItems = useGameStore(state => state.items)
  const [state, setState] = useState<ShopState | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [activeOffer, setActiveOffer] = useState<ShopOffer | null>(null)
  const [pendingOfferId, setPendingOfferId] = useState<string | null>(null)
  const pendingRef = useRef<string | null>(null)
  const controllerRef = useRef<AbortController | null>(null)
  const fallbackTimerRef = useRef<number | null>(null)

  const boundMerit = useMemo(
    () => inventory.find(item => item.itemId === BOUND_MERIT_ITEM_ID && !item.variant)?.amount ?? 0,
    [inventory],
  )
  const normalMerit = useMemo(
    () => inventory.find(item => item.itemId === NORMAL_MERIT_ITEM_ID && !item.variant)?.amount ?? 0,
    [inventory],
  )
  const totalMerit = boundMerit + normalMerit

  const definitions = useMemo(() => {
    const map = new Map<string, ItemDefinition>()
    for (const definition of catalogItems) map.set(definition.itemId, definition)
    return map
  }, [catalogItems])

  const ownedOf = useCallback(
    (itemId: string) => inventory
      .filter(item => item.itemId === itemId && !item.variant)
      .reduce((sum, item) => sum + item.amount, 0),
    [inventory],
  )

  const load = useCallback(async () => {
    if (!playerId) return
    controllerRef.current?.abort()
    const controller = new AbortController()
    controllerRef.current = controller
    setLoading(true)
    try {
      const next = await fetchShopState(playerId, controller.signal)
      if (controller.signal.aborted) return
      setState(next)
      setError(null)
    } catch (loadError) {
      if (controller.signal.aborted) return
      setError(loadError instanceof Error ? loadError.message : String(loadError))
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [playerId])

  useEffect(() => {
    void load()
    return () => {
      controllerRef.current?.abort()
      if (fallbackTimerRef.current !== null) window.clearTimeout(fallbackTimerRef.current)
    }
  }, [load])

  // 购买已发出后，功德余额变动落地即视为服务端已提交，立即重拉限购进度并关闭弹窗；
  // 失败（余额不足等）不会改变余额，由兜底定时器清理等待态并再对账一次。
  useEffect(() => {
    if (pendingRef.current === null) return
    pendingRef.current = null
    setPendingOfferId(null)
    setActiveOffer(null)
    void load()
  }, [totalMerit, load])

  const handleBuy = useCallback((offer: ShopOffer, quantity: number) => {
    if (pendingRef.current !== null) return
    if (!gameClient.sendCommand('shop.buy', { offerId: offer.offerId, quantity })) return
    pendingRef.current = offer.offerId
    setPendingOfferId(offer.offerId)
    if (fallbackTimerRef.current !== null) window.clearTimeout(fallbackTimerRef.current)
    fallbackTimerRef.current = window.setTimeout(() => {
      fallbackTimerRef.current = null
      if (pendingRef.current === null) return
      pendingRef.current = null
      setPendingOfferId(null)
      void load()
    }, PURCHASE_FALLBACK_REFRESH_MS)
  }, [load])

  const offers = state?.offers ?? []

  // 同步更新 activeOffer 引用以反映最新的已购数量
  const currentSelectedOffer = useMemo(() => {
    if (!activeOffer) return null
    return offers.find(o => o.offerId === activeOffer.offerId) ?? activeOffer
  }, [activeOffer, offers])

  return (
    <section className="panel shop-page">
      <header className="panel-header shop-header">
        <h2>{t('shop.title')}</h2>
        <span className="shop-subtitle">{t('shop.subtitle')}</span>
        <div className="shop-header-actions">
          {error ? (
            <span className="shop-header-status is-error" role="status" title={error}>{t('shop.loadFailed')}</span>
          ) : loading ? (
            <span className="shop-header-status is-loading" role="status">{t('shop.syncing')}</span>
          ) : null}
          <span
            aria-label={t('shop.walletAria')}
            className="shop-wallet"
            title={t('shop.meritDetails', { bound: formatNumber(boundMerit), normal: formatNumber(normalMerit) })}
          >
            <span className="shop-wallet-glyph" aria-hidden="true">功</span>
            <span className="shop-wallet-value">{formatNumber(totalMerit)}</span>
            <span className="shop-wallet-unit">{t('shop.meritUnit')}</span>
          </span>
          <button
            aria-label={t('shop.refresh')}
            className="shop-refresh"
            disabled={loading}
            onClick={() => void load()}
            title={t('shop.refresh')}
            type="button"
          >↻</button>
        </div>
      </header>

      <div className="shop-board">
        {offers.length === 0 ? (
          <p className="shop-empty">{loading ? t('shop.syncing') : t('shop.empty')}</p>
        ) : (
          <div className="shop-shelf-grid">
            {offers.map(offer => (
              <ShopItemSlot
                definition={definitions.get(offer.itemId)}
                key={offer.offerId}
                offer={offer}
                onSelect={setActiveOffer}
              />
            ))}
          </div>
        )}
      </div>

      <ShopBuyModal
        boundMerit={boundMerit}
        definition={currentSelectedOffer ? definitions.get(currentSelectedOffer.itemId) : undefined}
        normalMerit={normalMerit}
        offer={currentSelectedOffer}
        onClose={() => setActiveOffer(null)}
        onConfirm={handleBuy}
        open={currentSelectedOffer !== null}
        ownedAmount={currentSelectedOffer ? ownedOf(currentSelectedOffer.itemId) : 0}
        pending={pendingOfferId !== null}
        totalMerit={totalMerit}
      />
    </section>
  )
}
