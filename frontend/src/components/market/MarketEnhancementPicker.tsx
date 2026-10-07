import { memo, useMemo } from 'react'
import { formatNumber } from '../../game/format'
import { parseEnhancementVariant, type ItemMetadata } from '../../game/ids'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import type { MarketVariantSummary } from '../../game/types'
import { ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { UnitNumber } from '../UnitNumber'
import type { MarketInstrument } from './MarketOrderForm'

/** 强化等级行视图数据（占位行 bestAsk/bestBid 为 null 时价格列显示占位符） */
interface MarketEnhancementRowData {
  /** 强化等级（0 = 基础无强化变体） */
  level: number
  /** 当前最低卖价（卖一） */
  bestAsk: number | null
  /** 当前最高买价（买一） */
  bestBid: number | null
}

/** 单个强化等级行情行 Props */
interface MarketEnhancementRowProps extends MarketEnhancementRowData {
  /** 交易标的（装备） */
  instrument: MarketInstrument
  /** 标的展示元数据（品质/等阶/品类） */
  metadata: ItemMetadata
  /** 玩家该变体行持有量 */
  held: number
  /** 打开该等级挂单弹窗（购买=按卖一价买入，出售=按买一价卖出；无档位时 price 缺省） */
  onTrade: (side: 'buy' | 'sell', level: number, price?: number) => void
  /** 进入该等级盘口详情视图 */
  onViewBook: (level: number) => void
}

/**
 * 坊市装备强化等级单行组件 (MarketEnhancementRow)
 *
 * 行内四列：物品瓦片（悬浮详情按该等级缩放）→ 最佳出售价 + 购买按钮 →
 * 最佳收购价 + 出售按钮 → 查看全部按钮；按钮色调与盘口买卖侧对齐（ask=售/bid=购）。
 */
const MarketEnhancementRow = memo(function MarketEnhancementRow({
  instrument,
  metadata,
  level,
  bestAsk,
  bestBid,
  held,
  onTrade,
  onViewBook,
}: MarketEnhancementRowProps): React.JSX.Element {
  const t = useT()
  // 名称口径与背包变体行一致：基础变体用原名 + 「无强化」标注，强化变体直接拼 "+N"。
  const displayName = level > 0 ? `${instrument.name} +${level}` : instrument.name
  return (
    <div className="market-enhance-row" role="row">
      <span className="market-enhance-item" role="cell">
        <ItemTooltipTarget
          amount={held}
          className="market-enhance-tooltip"
          definition={instrument.definition}
          enhancementLevel={level}
          equipmentDefinition={instrument.equipmentDefinition}
          itemId={instrument.itemId}
          metadata={metadata}
        >
          <span className="market-enhance-item-cell">
            <ItemTile
              category={metadata.category}
              className="market-enhance-tile"
              enhancementLevel={level}
              itemId={instrument.itemId}
              level={metadata.level}
              name={displayName}
              rarity={metadata.rarity}
            />
            <span className="market-enhance-item-info">
              <span className="market-enhance-item-name">
                {displayName}
                {level === 0 && <em className="market-enhance-tag">{t('market.enhance.noEnhancement')}</em>}
              </span>
              {held > 0 && (
                <span className="market-enhance-held">{t('market.enhance.held', { count: formatNumber(held) })}</span>
              )}
            </span>
          </span>
        </ItemTooltipTarget>
      </span>
      <span className="market-enhance-price" role="cell">
        {bestAsk !== null
          ? <UnitNumber tone="crimson" tooltipLabel={t('market.enhance.bestAskColumn')} value={bestAsk} />
          : <span className="market-enhance-none">—</span>}
        <button
          aria-label={t('market.enhance.buyAria', { name: displayName })}
          className="market-level-trade-btn market-level-trade-btn--ask"
          onClick={() => onTrade('buy', level, bestAsk ?? undefined)}
          type="button"
        >
          {t('market.enhance.buy')}
        </button>
      </span>
      <span className="market-enhance-price" role="cell">
        {bestBid !== null
          ? <UnitNumber tone="jade" tooltipLabel={t('market.enhance.bestBidColumn')} value={bestBid} />
          : <span className="market-enhance-none">—</span>}
        <button
          aria-label={t('market.enhance.sellAria', { name: displayName })}
          className="market-level-trade-btn market-level-trade-btn--bid"
          onClick={() => onTrade('sell', level, bestBid ?? undefined)}
          type="button"
        >
          {t('market.enhance.sell')}
        </button>
      </span>
      <span className="market-enhance-action" role="cell">
        <button
          aria-label={t('market.enhance.viewAllAria', { name: displayName })}
          className="market-level-trade-btn market-enhance-view-btn"
          onClick={() => onViewBook(level)}
          type="button"
        >
          {t('market.enhance.viewAll')}
        </button>
      </span>
    </div>
  )
})

/** 强化等级选择视图 Props */
interface MarketEnhancementPickerProps {
  /** 交易标的（装备） */
  instrument: MarketInstrument
  /** 标的展示元数据（品质/等阶/品类） */
  metadata: ItemMetadata
  /** 服务端返回的存在挂单的变体摘要列表 */
  variants: readonly MarketVariantSummary[]
  /** 打开指定等级挂单弹窗 */
  onTrade: (side: 'buy' | 'sell', level: number, price?: number) => void
  /** 进入指定等级盘口详情视图 */
  onViewBook: (level: number) => void
}

/**
 * 坊市装备强化等级选择视图 (MarketEnhancementPicker)
 *
 * 参考 Milky Way Idle 布局：表头「物品 | 最佳出售价 | 最佳收购价 | 操作」，
 * 每行一个存在挂单的强化等级变体盘口；若服务端返回为空（无任何等级挂单），
 * 退化为单行 +0 无强化占位行，价格显示「—」但买卖/查看按钮仍可用。
 */
export const MarketEnhancementPicker = memo(function MarketEnhancementPicker({
  instrument,
  metadata,
  variants,
  onTrade,
  onViewBook,
}: MarketEnhancementPickerProps): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)

  // 该标的各强化等级钱包变体行持有量索引（"+N" → 等级 → 数量），行渲染 O(1) 查询。
  const heldByLevel = useMemo(() => {
    const map = new Map<number, number>()
    for (const entry of inventory) {
      if (entry.itemId !== instrument.itemId) continue
      const level = parseEnhancementVariant(entry.variant)
      map.set(level, (map.get(level) ?? 0) + entry.amount)
    }
    return map
  }, [inventory, instrument.itemId])

  // 只保留买或卖任一侧有挂单价格的等级并按等级升序；接口空集退化为 +0 占位行。
  const rows = useMemo<MarketEnhancementRowData[]>(() => {
    const active = variants
      .filter(variant => variant.bestAsk !== null || variant.bestBid !== null)
      .map(variant => ({ level: variant.enhancementLevel, bestAsk: variant.bestAsk, bestBid: variant.bestBid }))
      .sort((left, right) => left.level - right.level)
    return active.length > 0 ? active : [{ level: 0, bestAsk: null, bestBid: null }]
  }, [variants])

  return (
    <div
      aria-label={t('market.enhance.tableAria', { name: instrument.name })}
      className="market-enhance"
      role="table"
    >
      <div className="market-enhance-row market-enhance-row--header" role="row">
        <span role="columnheader">{t('market.enhance.itemColumn')}</span>
        <span role="columnheader">{t('market.enhance.bestAskColumn')}</span>
        <span role="columnheader">{t('market.enhance.bestBidColumn')}</span>
        <span role="columnheader">{t('market.enhance.actionColumn')}</span>
      </div>
      {rows.map(row => (
        <MarketEnhancementRow
          bestAsk={row.bestAsk}
          bestBid={row.bestBid}
          held={heldByLevel.get(row.level) ?? 0}
          instrument={instrument}
          key={row.level}
          level={row.level}
          metadata={metadata}
          onTrade={onTrade}
          onViewBook={onViewBook}
        />
      ))}
    </div>
  )
})
