import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { SPIRIT_STONE_ITEM_ID } from '../game/combatConstants'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { resolveItemCategoryLabel, resolveItemMetadata, resolveItemSubCategory } from '../game/ids'
import type { ItemDefinition, ShopOffer, ShopState } from '../game/types'
import { useT } from '../i18n'
import { fetchShopState } from '../network/apiClient'
import { gameClient } from '../network/gameClient'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { QuantitySelector } from './QuantitySelector'

/** 单次购买件数硬上限：与服务端 ShopPurchaseRules.MaxQuantityPerPurchase 对齐 */
const MAX_PURCHASE_QUANTITY = 999
/** 购买成功后等待钱包增量回流的兜底重拉间隔（毫秒）：余额未变化时仍能在该窗口后对账限购进度 */
const PURCHASE_FALLBACK_REFRESH_MS = 2_500

interface ShopCardProps {
  /** 商品定义与当日限购进度 */
  offer: ShopOffer
  /** 玩家当前灵石余额 */
  spiritAmount: number
  /** 玩家当前持有该物品数量（基础变体） */
  ownedAmount: number
  /** 服务端物品定义（用于悬浮详情与品类丝带） */
  definition: ItemDefinition | undefined
  /** 不限购商品的待购数量 */
  quantity: number
  /** 购买请求已发出、等待服务端回执 */
  pending: boolean
  /** 修改待购数量 */
  onQuantityChange: (offerId: string, quantity: number) => void
  /** 提交购买 */
  onBuy: (offer: ShopOffer, quantity: number) => void
}

/** 单个货架商品卡：物品瓦片 + 官价 + 限购徽记 + 购买操作。 */
const ShopCard = memo(function ShopCard({
  offer,
  spiritAmount,
  ownedAmount,
  definition,
  quantity,
  pending,
  onQuantityChange,
  onBuy,
}: ShopCardProps): React.JSX.Element {
  const t = useT()
  const metadata = resolveItemMetadata(offer.itemId)
  const name = metadata?.label ?? definition?.name ?? offer.itemId
  const limited = offer.dailyLimit !== null
  const remaining = limited ? Math.max(0, (offer.dailyLimit ?? 0) - offer.purchasedToday) : Number.POSITIVE_INFINITY
  const soldOut = limited && remaining <= 0
  // 不限购商品按余额收敛可买量；限购商品固定单件（配额按件累计，多件必然超限）。
  const maxAffordable = Math.floor(spiritAmount / Math.max(1, offer.unitPrice))
  const buyableQuantity = limited ? 1 : Math.max(1, Math.min(MAX_PURCHASE_QUANTITY, maxAffordable))
  const effectiveQuantity = limited ? 1 : Math.max(1, Math.min(quantity, buyableQuantity))
  const totalPrice = offer.unitPrice * effectiveQuantity
  const insufficient = totalPrice > spiritAmount
  const disabled = pending || soldOut || insufficient || maxAffordable < 1

  return (
    <article className={`shop-card${soldOut ? ' is-sold-out' : ''}`}>
      <header className="shop-card-head">
        <ItemTooltipTarget
          className="shop-item-target"
          definition={definition}
          itemId={offer.itemId}
          metadata={metadata}
        >
          <div className="shop-item-tile">
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
        <div className="shop-card-title">
          <span className="shop-item-name">{name}</span>
          <span className={`shop-limit-badge${soldOut ? ' is-empty' : limited ? '' : ' is-open'}`}>
            {limited ? t('shop.dailyLimit', { limit: offer.dailyLimit ?? 0 }) : t('shop.unlimited')}
          </span>
          {limited && (
            <span className={`shop-limit-progress${soldOut ? ' is-exhausted' : ''}`}>
              {soldOut ? t('shop.soldOut') : t('shop.dailyProgress', { purchased: offer.purchasedToday, limit: offer.dailyLimit ?? 0 })}
            </span>
          )}
          <span className="shop-card-owned">{t('shop.owned', { amount: formatNumber(ownedAmount) })}</span>
        </div>
      </header>

      <dl className="shop-card-meta">
        <div>
          <dt>{t('shop.unitPrice')}</dt>
          <dd>
            <span className="shop-price-value">{formatNumber(offer.unitPrice)}</span>
            <span className="shop-price-unit">{t('shop.spiritUnit')}</span>
          </dd>
        </div>
        <div>
          <dt>{t('shop.payable')}</dt>
          <dd className={insufficient ? 'is-exhausted' : ''}>
            <span className="shop-price-value">{formatNumber(totalPrice)}</span>
            <span className="shop-price-unit">{t('shop.spiritUnit')}</span>
          </dd>
        </div>
      </dl>

      <div className="shop-card-actions">
        {limited ? null : (
          <div className="shop-quantity">
            <span className="shop-quantity-label">{t('shop.quantity')}</span>
            <QuantitySelector
              ariaLabel={t('shop.quantityAria')}
              compact
              disabled={pending || soldOut}
              max={buyableQuantity}
              maxShortcut
              onChange={value => onQuantityChange(offer.offerId, value)}
              presets={[1, 5, 10]}
              value={effectiveQuantity}
            />
          </div>
        )}
        <button
          className="shop-buy-btn"
          disabled={disabled}
          onClick={() => onBuy(offer, effectiveQuantity)}
          type="button"
        >
          {pending ? t('shop.buying') : soldOut ? t('shop.soldOut') : insufficient ? t('shop.insufficient') : t('shop.buy')}
        </button>
      </div>
    </article>
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
  const [quantities, setQuantities] = useState<Record<string, number>>({})
  const [pendingOfferId, setPendingOfferId] = useState<string | null>(null)
  const pendingRef = useRef<string | null>(null)
  const controllerRef = useRef<AbortController | null>(null)
  const fallbackTimerRef = useRef<number | null>(null)

  const spiritAmount = useMemo(
    () => inventory.find(item => item.itemId === SPIRIT_STONE_ITEM_ID && !item.variant)?.amount ?? 0,
    [inventory],
  )
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

  // 购买已发出后，钱包增量（灵石扣减）落地即视为服务端已提交，立即重拉限购进度；
  // 失败（余额不足等）不会改变余额，由兜底定时器清理等待态并再对账一次。
  useEffect(() => {
    if (pendingRef.current === null) return
    pendingRef.current = null
    setPendingOfferId(null)
    void load()
  }, [spiritAmount, load])

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

  const handleQuantityChange = useCallback((offerId: string, quantity: number) => {
    setQuantities(prev => (prev[offerId] === quantity ? prev : { ...prev, [offerId]: quantity }))
  }, [])

  const offers = state?.offers ?? []

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
          <span aria-label={t('shop.walletAria')} className="shop-wallet">
            <span className="shop-wallet-glyph" aria-hidden="true">◈</span>
            <span className="shop-wallet-value">{formatNumber(spiritAmount)}</span>
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
          <div className="shop-grid">
            {offers.map(offer => (
              <ShopCard
                definition={definitions.get(offer.itemId)}
                key={offer.offerId}
                offer={offer}
                onBuy={handleBuy}
                onQuantityChange={handleQuantityChange}
                ownedAmount={ownedOf(offer.itemId)}
                pending={pendingOfferId === offer.offerId}
                quantity={quantities[offer.offerId] ?? 1}
                spiritAmount={spiritAmount}
              />
            ))}
          </div>
        )}
      </div>
    </section>
  )
}
