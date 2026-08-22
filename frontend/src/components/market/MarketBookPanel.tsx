import { memo } from 'react'
import { UnitNumber } from '../UnitNumber'
import { useT } from '../../i18n'
import type { MarketBook, MarketLevel } from '../../game/types'

/** 盘口深度默认占位行数（确保盘口高度规整不跳动） */
const BOOK_PLACEHOLDER_ROWS = 8

/**
 * 盘口单向深度档位列表渲染组件 (MarketRows)
 */
function MarketRows({ levels, side }: { levels: readonly MarketLevel[]; side: 'ask' | 'bid' }): React.JSX.Element {
  const t = useT()
  const placeholders = Math.max(0, BOOK_PLACEHOLDER_ROWS - levels.length)
  return (
    <div className="market-levels" role="table" aria-label={side === 'ask' ? t('market.book.askRowsAria') : t('market.book.bidRowsAria')}>
      <div className="market-level market-level--header" role="row">
        <span role="columnheader">{t('market.book.priceColumn')}</span>
        <span role="columnheader">{t('market.quantity')}</span>
        <span role="columnheader">{t('market.book.ordersColumn')}</span>
      </div>
      {levels.map(level => (
        <div className={`market-level market-level--${side}`} key={level.price} role="row">
          <strong role="cell"><UnitNumber value={level.price} tone={side === 'bid' ? 'jade' : 'crimson'} /></strong>
          <span role="cell"><UnitNumber value={level.quantity} /></span>
          <span role="cell"><UnitNumber value={level.orderCount} /></span>
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
}

/**
 * 坊市订单簿盘口深度面板组件 (MarketBookPanel)
 *
 * 左右双列或单向展示指定标的的买一至买N、卖一至卖N档位价格、累计挂单总量与笔数。
 */
export const MarketBookPanel = memo(function MarketBookPanel({ book, sides = ['bid', 'ask'] }: MarketBookPanelProps): React.JSX.Element {
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
            <MarketRows levels={book?.bids ?? []} side="bid" />
          </section>
        )}
        {sides.includes('ask') && (
          <section className="market-book-side market-book-side--ask">
            <div className="market-book-side-title">
              <h3>{t('market.book.asksTitle')}</h3>
              <span className="market-book-side-tag market-book-side-tag--ask">{t('market.book.askTag')}</span>
            </div>
            <MarketRows levels={book?.asks ?? []} side="ask" />
          </section>
        )}
      </div>
    </div>
  )
})
