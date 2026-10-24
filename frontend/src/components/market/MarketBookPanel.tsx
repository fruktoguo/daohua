import { memo } from 'react'
import { formatNumber } from '../../game/format'
import { UnitNumber } from '../UnitNumber'
import { useT } from '../../i18n'
import type { MarketBook, MarketLevel } from '../../game/types'

/** 盘口深度默认占位行数（确保盘口高度规整不跳动） */
const BOOK_PLACEHOLDER_ROWS = 8

/**
 * 盘口单向深度档位列表渲染组件 (MarketRows)
 *
 * 三列布局：单价、挂单总量、快捷交易按钮；快捷按钮只挂在最优档（买一/卖一）行上，
 * 买盘最优档对应「按买一价卖出」，卖盘最优档对应「按卖一价买入」。
 */
function MarketRows({ levels, side, onQuickTrade }: {
  levels: readonly MarketLevel[]
  side: 'ask' | 'bid'
  onQuickTrade?: (price: number) => void
}): React.JSX.Element {
  const t = useT()
  const placeholders = Math.max(0, BOOK_PLACEHOLDER_ROWS - levels.length)
  return (
    <div className="market-levels" role="table" aria-label={side === 'ask' ? t('market.book.askRowsAria') : t('market.book.bidRowsAria')}>
      <div className="market-level market-level--header" role="row">
        <span role="columnheader">{t('market.book.priceColumn')}</span>
        <span role="columnheader">{t('market.quantity')}</span>
        <span role="columnheader">{t('market.book.actionColumn')}</span>
      </div>
      {levels.map((level, index) => (
        <div className={`market-level market-level--${side}`} key={level.price} role="row">
          <strong role="cell"><UnitNumber tooltipLabel={t('market.unitPrice')} value={level.price} tone={side === 'bid' ? 'jade' : 'crimson'} /></strong>
          <span role="cell"><UnitNumber value={level.quantity} /></span>
          <span role="cell" className="market-level-action">
            {index === 0 && onQuickTrade && (
              <button
                aria-label={side === 'bid'
                  ? t('market.book.quickSellAria', { price: formatNumber(level.price) })
                  : t('market.book.quickBuyAria', { price: formatNumber(level.price) })}
                className={`market-level-trade-btn market-level-trade-btn--${side}`}
                onClick={() => onQuickTrade(level.price)}
                type="button"
              >
                {side === 'bid' ? t('market.book.quickSell') : t('market.book.quickBuy')}
              </button>
            )}
          </span>
        </div>
      ))}
      {levels.length === 0
        ? <div className="market-empty">{t('market.book.noOrders')}</div>
        : Array.from({ length: placeholders }, (_, index) => (
            <div className="market-level market-level--placeholder" key={`placeholder-${index}`} role="row" aria-hidden="true" />
          ))}
    </div>
  )
}

/** 盘口深度面板 Props */
interface MarketBookPanelProps {
  /** 当前标的订单簿数据（含 bids 买盘与 asks 卖盘） */
  book: MarketBook | null
  /** 显示的方向侧（默认同时显示买盘与卖盘） */
  sides?: readonly ('ask' | 'bid')[]
  /**
   * 最优档快捷交易回调：买盘买一触发 ('sell', 买一价)，卖盘卖一触发 ('buy', 卖一价)；
   * 传入后买一/卖一行尾渲染对应快捷交易按钮。
   */
  onQuickTrade?: (side: 'buy' | 'sell', price: number) => void
}

/**
 * 坊市订单簿盘口深度面板组件 (MarketBookPanel)
 *
 * 左右双列或单向展示指定标的的买一至买N、卖一至卖N档位价格、累计挂单总量与笔数；
 * 最优档行尾提供快捷交易入口（买一卖出/卖一买入），由宿主打开挂单弹窗并预填对手价。
 */
export const MarketBookPanel = memo(function MarketBookPanel({ book, sides = ['bid', 'ask'], onQuickTrade }: MarketBookPanelProps): React.JSX.Element {
  const t = useT()
  return (
    <div className="market-book-panel">
      <div className="market-book">
        {sides.includes('bid') && (
          <section className="market-book-side market-book-side--bid">
            <div className="market-book-side-title">
              <h3>{t('market.book.bidsTitle')}</h3>
              <span className="market-book-side-tag market-book-side-tag--bid">{t('market.book.bidTag')}</span>
            </div>
            <MarketRows
              levels={book?.bids ?? []}
              onQuickTrade={onQuickTrade ? price => onQuickTrade('sell', price) : undefined}
              side="bid"
            />
          </section>
        )}
        {sides.includes('ask') && (
          <section className="market-book-side market-book-side--ask">
            <div className="market-book-side-title">
              <h3>{t('market.book.asksTitle')}</h3>
              <span className="market-book-side-tag market-book-side-tag--ask">{t('market.book.askTag')}</span>
            </div>
            <MarketRows
              levels={book?.asks ?? []}
              onQuickTrade={onQuickTrade ? price => onQuickTrade('buy', price) : undefined}
              side="ask"
            />
          </section>
        )}
      </div>
    </div>
  )
})
