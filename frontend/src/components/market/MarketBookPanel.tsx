import { memo } from 'react'
import { formatNumber } from '../../game/format'
import type { MarketBook, MarketLevel } from '../../game/types'

const BOOK_PLACEHOLDER_ROWS = 8

function MarketRows({ levels, side }: { levels: readonly MarketLevel[]; side: 'ask' | 'bid' }): React.JSX.Element {
  const placeholders = Math.max(0, BOOK_PLACEHOLDER_ROWS - levels.length)
  return (
    <div className="market-levels" role="table" aria-label={side === 'ask' ? '卖单' : '买单'}>
      <div className="market-level market-level--header" role="row">
        <span role="columnheader">单价</span>
        <span role="columnheader">数量</span>
        <span role="columnheader">订单</span>
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
  sides: readonly ('ask' | 'bid')[]
}

export const MarketBookPanel = memo(function MarketBookPanel({ book, sides }: MarketBookPanelProps): React.JSX.Element {
  const bestAsk = book?.asks[0]?.price
  const bestBid = book?.bids[0]?.price
  return (
    <div className="market-book-panel">
      <div className="market-summary" aria-label="盘口摘要">
        <div><span>买一</span><strong className="market-bid-text">{bestBid === undefined ? '—' : formatNumber(bestBid)}</strong></div>
        <div><span>卖一</span><strong className="market-ask-text">{bestAsk === undefined ? '—' : formatNumber(bestAsk)}</strong></div>
        <div><span>活动订单</span><strong>{formatNumber(book?.activeOrderCount ?? 0)}</strong></div>
      </div>
      <div className="market-book">
        {sides.includes('bid') && (
          <section>
            <h3>求购</h3>
            <MarketRows levels={book?.bids ?? []} side="bid" />
          </section>
        )}
        {sides.includes('ask') && (
          <section>
            <h3>出售</h3>
            <MarketRows levels={book?.asks ?? []} side="ask" />
          </section>
        )}
      </div>
    </div>
  )
})
