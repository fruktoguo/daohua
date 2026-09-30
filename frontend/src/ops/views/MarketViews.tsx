import { useState } from 'react'
import { opsGet } from '../opsClient'
import type { OpsBook, OpsMarketMetricsView, OpsOrder, OpsTrade } from '../opsTypes'
import {
  OpsAsyncView,
  OpsCard,
  OpsPill,
  OpsStat,
  OpsStatGrid,
  OpsTable,
  formatNumber,
  formatTime,
  useOpsData,
} from '../opsUi'

/** 市场指标视图：分片、租约就绪与账本累计。 */
export function OpsMarketMetricsView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsMarketMetricsView>(signal => opsGet('/market/metrics', signal), [], intervalMs)
  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      {data && (
        <>
          <OpsStatGrid>
            <OpsStat label="分片就绪" value={data.ready ? '就绪' : '未就绪'} tone={data.ready ? 'jade' : 'warn'} />
            <OpsStat label="分片" value={`${formatNumber(data.partitionId)} / ${formatNumber(data.partitionCount)}`} />
            <OpsStat label="分区算法" value={data.algorithm} />
            <OpsStat label="活动订单" value={formatNumber(data.metrics.activeOrderCount)} />
            <OpsStat label="累计挂单" value={formatNumber(data.metrics.placementCount)} />
            <OpsStat label="累计成交" value={formatNumber(data.metrics.matchCount)} />
            <OpsStat label="累计撤单" value={formatNumber(data.metrics.cancelCount)} />
            <OpsStat label="累计拒绝" value={formatNumber(data.metrics.rejectedCount)} tone={data.metrics.rejectedCount > 0 ? 'warn' : 'muted'} />
            <OpsStat label="成交数量" value={formatNumber(data.metrics.tradedQuantity, 2)} />
            <OpsStat label="成交金额" value={formatNumber(data.metrics.tradedGrossAmount, 2)} />
          </OpsStatGrid>
        </>
      )}
    </OpsAsyncView>
  )
}

/** 盘口视图：按品种查询买卖档位。 */
export function OpsBookView(): React.JSX.Element {
  const [assetId, setAssetId] = useState('')
  const [variant, setVariant] = useState('')
  const [depth, setDepth] = useState('20')
  const [query, setQuery] = useState<{ assetId: string; variant: string; depth: string } | null>(null)

  const { data, error, loading, reload } = useOpsData<OpsBook | null>(
    signal => (query
      ? opsGet(`/market/book?assetId=${encodeURIComponent(query.assetId)}&variant=${encodeURIComponent(query.variant)}&depth=${encodeURIComponent(query.depth)}`, signal)
      : Promise.resolve(null)),
    [query?.assetId, query?.variant, query?.depth],
  )

  const submit = (): void => {
    if (!assetId.trim()) return
    setQuery({ assetId: assetId.trim(), variant: variant.trim(), depth: depth || '20' })
  }

  return (
    <>
      <OpsCard title="品种查询">
        <div className="ops-toolbar">
          <label>
            物品 ID
            <input value={assetId} placeholder="如 item.spirit-herb" onChange={event => setAssetId(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') submit() }} />
          </label>
          <label>
            变体
            <input value={variant} placeholder="可选" onChange={event => setVariant(event.target.value)} />
          </label>
          <label>
            深度
            <input value={depth} inputMode="numeric" onChange={event => setDepth(event.target.value)} />
          </label>
          <button type="button" onClick={submit}>查询盘口</button>
        </div>
      </OpsCard>
      {query === null ? (
        <OpsCard title="盘口"><p className="ops-empty">输入品种后查询。</p></OpsCard>
      ) : (
        <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
          {data && (
            <OpsCard title={`${data.assetId}${data.variant ? ` / ${data.variant}` : ''}（版本 ${formatNumber(data.version)}）`}>
              <p className="ops-note">采集时刻：{formatTime(data.capturedAt)}</p>
              <OpsTable
                rows={[
                  ...data.asks.map((level, index) => ({ side: '卖', rank: index + 1, ...level })),
                  ...data.bids.map((level, index) => ({ side: '买', rank: index + 1, ...level })),
                ]}
                rowKey={row => `${row.side}-${row.rank}`}
                empty="该品种当前没有挂单。"
                columns={[
                  { header: '方向', cell: row => <OpsPill tone={row.side === '卖' ? 'danger' : 'jade'}>{row.side}</OpsPill> },
                  { header: '档位', numeric: true, cell: row => formatNumber(row.rank) },
                  { header: '价格', numeric: true, cell: row => formatNumber(row.price, 2) },
                  { header: '数量', numeric: true, cell: row => formatNumber(row.quantity, 2) },
                  { header: '订单数', numeric: true, cell: row => formatNumber(row.orders) },
                ]}
              />
            </OpsCard>
          )}
        </OpsAsyncView>
      )}
    </>
  )
}

/** 挂单视图：按玩家查询当前挂单。 */
export function OpsOrdersView(): React.JSX.Element {
  const [playerIdDraft, setPlayerIdDraft] = useState('')
  const [playerId, setPlayerId] = useState('')

  const { data, error, loading, reload } = useOpsData<OpsOrder[] | null>(
    signal => (playerId ? opsGet(`/market/orders?playerId=${encodeURIComponent(playerId)}&limit=200`, signal) : Promise.resolve(null)),
    [playerId],
  )

  return (
    <>
      <OpsCard title="按玩家查询挂单">
        <div className="ops-toolbar">
          <label>
            玩家 ID
            <input value={playerIdDraft} placeholder="玩家 UUID" onChange={event => setPlayerIdDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') setPlayerId(playerIdDraft.trim()) }} />
          </label>
          <button type="button" onClick={() => setPlayerId(playerIdDraft.trim())}>查询挂单</button>
        </div>
      </OpsCard>
      {playerId === '' ? (
        <OpsCard title="挂单"><p className="ops-empty">输入玩家 ID 后查询。</p></OpsCard>
      ) : (
        <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
          <OpsCard title={`挂单（${formatNumber(data?.length ?? 0)}）`}>
            <OpsTable
              rows={data ?? []}
              rowKey={order => order.orderId}
              empty="该玩家当前没有挂单。"
              columns={[
                { header: '订单 ID', cell: order => order.orderId },
                { header: '方向', cell: order => <OpsPill tone={order.side === 'buy' ? 'jade' : 'danger'}>{order.side === 'buy' ? '买' : '卖'}</OpsPill> },
                { header: '品种', cell: order => `${order.assetId}${order.variant ? ` / ${order.variant}` : ''}` },
                { header: '价格', numeric: true, cell: order => formatNumber(order.price, 2) },
                { header: '原始/剩余', numeric: true, cell: order => `${formatNumber(order.originalQuantity, 2)} / ${formatNumber(order.remainingQuantity, 2)}` },
                { header: '状态', cell: order => order.status },
                { header: '创建', cell: order => formatTime(order.createdAt) },
                { header: '过期', cell: order => formatTime(order.expiresAt) },
              ]}
            />
          </OpsCard>
        </OpsAsyncView>
      )}
    </>
  )
}

/** 账本视图：最近成交流水。 */
export function OpsTradesView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsTrade[]>(signal => opsGet('/market/trades?limit=100', signal), [], intervalMs)
  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      <OpsCard title={`最近成交（${formatNumber(data?.length ?? 0)}）`}>
        <OpsTable
          rows={data ?? []}
          rowKey={trade => trade.tradeId}
          empty="还没有成交记录。"
          columns={[
            { header: '成交时刻', cell: trade => formatTime(trade.tradedAt) },
            { header: '品种', cell: trade => `${trade.assetId}${trade.variant ? ` / ${trade.variant}` : ''}` },
            { header: '价格', numeric: true, cell: trade => formatNumber(trade.price, 2) },
            { header: '数量', numeric: true, cell: trade => formatNumber(trade.quantity, 2) },
            { header: '买方', cell: trade => trade.buyerId },
            { header: '卖方', cell: trade => trade.sellerId },
          ]}
        />
      </OpsCard>
    </OpsAsyncView>
  )
}
