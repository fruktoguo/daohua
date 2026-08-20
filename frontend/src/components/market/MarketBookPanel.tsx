import { memo } from 'react'
import { formatNumber } from '../../game/format'
import type { MarketBook, MarketLevel } from '../../game/types'

const BOOK_PLACEHOLDER_ROWS = 8

function MarketRows({ levels, side }: { levels: readonly MarketLevel[]; side: 'ask' | 'bid' }): React.JSX.Element {
  const placeholders = Math.max(0, BOOK_PLACEHOLDER_ROWS - levels.length)
  return (
    <div className="market-levels" role="table" aria-label={side === 'ask' ? '正在售卖' : '求购'}>
      <div className="market-level market-level--header" role="row">
        <span role="columnheader">单价 (灵石)</span>
        <span role="columnheader">数量</span>
        <span role="columnheader">笔数</span>
      </div>
      {levels.map(level => (
        <div className={`market-level market-level--${side}`} key={level.price} role="row">
          <strong role="cell">{formatNumber(level.price)}</strong>
          <span role="cell">{formatNumber(level.quantity)}</span>
          <span role="cell">{formatNumber(level.orderCount)}</span>
        </div>
      ))}
      {levels.length === 0
        ? <div className="market-empty">暂无挂单</div>
        : Array.from({ length: placeholders }, (_, index) => (
            <div className="market-level market-level--placeholder" key={`placeholder-${index}`} role="row" aria-hidden="true" />
          ))}
    </div>
  )
}

interface MarketBookPanelProps {
  book: MarketBook | null
  sides?: readonly ('ask' | 'bid')[]
}

export const MarketBookPanel = memo(function MarketBookPanel({ book, sides = ['bid', 'ask'] }: MarketBookPanelProps): React.JSX.Element {
  return (
    <div className="market-book-panel">
      <div className="market-book">
        {sides.includes('bid') && (
          <section className="market-book-side market-book-side--bid">
            <div className="market-book-side-title">
              <h3>求购订单</h3>
              <span className="market-book-side-tag market-book-side-tag--bid">买单</span>
            </div>
            <MarketRows levels={book?.bids ?? []} side="bid" />
          </section>
        )}
        {sides.includes('ask') && (
          <section className="market-book-side market-book-side--ask">
            <div className="market-book-side-title">
              <h3>正在售卖</h3>
              <span className="market-book-side-tag market-book-side-tag--ask">卖单</span>
            </div>
            <MarketRows levels={book?.asks ?? []} side="ask" />
          </section>
        )}
      </div>
    </div>
  )
})
