import { useState } from 'react'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { opsGet, opsPost } from '../opsClient'
import type {
  OpsBook,
  OpsCatalog,
  OpsMarketMetricsView,
  OpsOrder,
  OpsSystemCancelResponse,
  OpsTrade,
} from '../opsTypes'
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

/** 系统身份玩家 UUID（与 scripts/seed-market-system-listings.sh 的固定身份对齐）。 */
const SYSTEM_PLAYER_ID = 'f0000000-0000-4000-8000-000000000002'

/** 默认委托数量：1 亿份（对齐种子任务口径）。 */
const DEFAULT_QUANTITY = 100000000

/** 系统下单范围。 */
type SystemOrderScope = 'item' | 'category' | 'all'

/** 待确认的系统写操作。 */
interface PendingSystemAction {
  title: string
  description: string
  danger: boolean
  run: () => Promise<unknown>
}

/**
 * 系统下单视图：以系统身份批量挂售/收购（单个品种 / 按品类 / 全部品种：可回收物品 + 装备），
 * 下单即时上架无需重启；下方列出系统当前挂单，支持逐条与一键撤销。
 * 全部写操作经二次确认，结果与失败原因都留在页面上。
 */
export function OpsSystemOrderView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const [side, setSide] = useState<'sell' | 'buy'>('sell')
  const [scope, setScope] = useState<SystemOrderScope>('all')
  const [itemId, setItemId] = useState('')
  const [category, setCategory] = useState('')
  const [priceDraft, setPriceDraft] = useState('')
  const [quantityDraft, setQuantityDraft] = useState('')
  const [pending, setPending] = useState<PendingSystemAction | null>(null)
  const [result, setResult] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)

  const { data: catalog, error: catalogError, loading: catalogLoading, reload: reloadCatalog } = useOpsData<OpsCatalog>(
    signal => opsGet('/market/catalog', signal), [])
  const { data: systemOrders, error: ordersError, loading: ordersLoading, reload: reloadOrders } = useOpsData<OpsOrder[]>(
    signal => opsGet(`/market/orders?playerId=${encodeURIComponent(SYSTEM_PLAYER_ID)}&limit=200`, signal),
    [reloadKey], intervalMs)

  const items = catalog?.items ?? []
  const categories = catalog?.categories ?? []
  const parsedPrice = priceDraft.trim() === '' ? null : Number(priceDraft)
  const parsedQuantity = quantityDraft.trim() === '' ? null : Number(quantityDraft)
  const targets = scope === 'item'
    ? items.filter(item => item.id === itemId)
    : scope === 'category'
      ? items.filter(item => item.category === category)
      : items
  const fundsNeeded = side === 'buy'
    ? targets.reduce((sum, item) => sum + (parsedPrice ?? item.sellPrice) * (parsedQuantity ?? DEFAULT_QUANTITY), 0)
    : 0
  const nameById = new Map(items.map(item => [item.id, item.name]))

  const execute = async (action: () => Promise<unknown>): Promise<void> => {
    setBusy(true)
    setError(null)
    setResult(null)
    try {
      setResult(JSON.stringify(await action(), null, 2))
      setReloadKey(key => key + 1)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }

  const submitOrder = (): void => {
    if (targets.length === 0) {
      setError('请先选择有效范围。')
      return
    }
    if (parsedPrice !== null && (!Number.isFinite(parsedPrice) || parsedPrice <= 0)) {
      setError('价格必须是正数。')
      return
    }
    if (parsedQuantity !== null && (!Number.isFinite(parsedQuantity) || parsedQuantity <= 0)) {
      setError('数量必须是正数。')
      return
    }
    const scopeLabel = scope === 'item'
      ? `品种「${targets[0].name}」`
      : scope === 'category'
        ? `品类「${category}」（${targets.length} 个品种）`
        : `全部品种（${targets.length} 个）`
    const priceLabel = parsedPrice === null ? '回收价（装备为材料成本价）' : formatNumber(parsedPrice, 2)
    const quantityLabel = `${formatNumber(parsedQuantity ?? DEFAULT_QUANTITY)} 份`
    const fundsLabel = side === 'buy' ? `，系统需注入灵石约 ${formatNumber(fundsNeeded, 2)}` : ''
    setPending({
      title: side === 'sell' ? '系统挂售下单' : '系统收购下单',
      description: `将以系统身份挂${side === 'sell' ? '售' : '购'}${scopeLabel}：价格 ${priceLabel} × ${quantityLabel}/品种，下单即时上架${fundsLabel}。`,
      danger: false,
      run: () => opsPost('/actions/market-system-orders', {
        side,
        scope,
        itemId: scope === 'item' ? itemId : null,
        category: scope === 'category' ? category : null,
        price: parsedPrice,
        quantity: parsedQuantity,
      }),
    })
  }

  const cancelOrders = (assetId: string | null): void => {
    setPending({
      title: assetId ? `撤销系统挂单（${nameById.get(assetId) ?? assetId}）` : '撤销全部系统挂单',
      description: assetId
        ? `将撤销该品种的全部活动系统挂单，冻结资金退回系统余额。`
        : `将撤销系统身份的全部活动挂单（${formatNumber(systemOrders?.length ?? 0)} 笔），冻结资金退回系统余额。`,
      danger: true,
      run: () => opsPost<OpsSystemCancelResponse>('/actions/market-system-cancel', { assetId }),
    })
  }

  return (
    <>
      <OpsAsyncView loading={catalogLoading} error={catalogError} hasData={catalog !== null} onRetry={reloadCatalog}>
        <OpsCard title="系统下单（挂售 / 收购）">
          <p className="ops-note">
            以系统身份（系统坊市）挂售/收购，下单即时上架；价格缺省为回收价（装备为锻造材料成本价），数量缺省 1 亿份。
          </p>
          <div className="ops-toolbar">
            <label>
              方向
              <select value={side} onChange={event => setSide(event.target.value === 'buy' ? 'buy' : 'sell')}>
                <option value="sell">挂售单</option>
                <option value="buy">收购单</option>
              </select>
            </label>
            <label>
              范围
              <select value={scope} onChange={event => setScope(event.target.value as SystemOrderScope)}>
                <option value="item">单个品种</option>
                <option value="category">按品类</option>
                <option value="all">全部品种</option>
              </select>
            </label>
            {scope === 'item' && (
              <label>
                品种
                <select value={itemId} onChange={event => setItemId(event.target.value)}>
                  <option value="">请选择品种…</option>
                  {items.map(item => (
                    <option key={item.id} value={item.id}>{item.name}（{item.id}）</option>
                  ))}
                </select>
              </label>
            )}
            {scope === 'category' && (
              <label>
                品类
                <select value={category} onChange={event => setCategory(event.target.value)}>
                  <option value="">请选择品类…</option>
                  {categories.map(value => (
                    <option key={value} value={value}>{value}</option>
                  ))}
                </select>
              </label>
            )}
            <label>
              价格
              <input
                value={priceDraft}
                inputMode="decimal"
                placeholder={scope === 'item' && targets.length === 1 ? `缺省定价 ${targets[0].sellPrice}` : '缺省定价（回收价；装备为材料成本价）'}
                onChange={event => setPriceDraft(event.target.value)}
              />
            </label>
            <label>
              数量
              <input
                value={quantityDraft}
                inputMode="decimal"
                placeholder={`默认 ${formatNumber(DEFAULT_QUANTITY)} 份`}
                onChange={event => setQuantityDraft(event.target.value)}
              />
            </label>
            <button type="button" disabled={busy} onClick={submitOrder}>
              {side === 'sell' ? '挂售下单' : '收购下单'}（{targets.length}）
            </button>
          </div>
        </OpsCard>
      </OpsAsyncView>

      <OpsCard title="最近一次操作结果">
        {error && <p className="ops-error-text">失败：{error}</p>}
        {result && <pre className="ops-result">{result}</pre>}
        {!error && !result && <p className="ops-empty">尚未执行任何写操作。</p>}
        {busy && <OpsPill tone="warn">执行中…</OpsPill>}
      </OpsCard>

      <OpsAsyncView loading={ordersLoading} error={ordersError} hasData={systemOrders !== null} onRetry={reloadOrders}>
        <OpsCard
          title={`系统当前挂单（${formatNumber(systemOrders?.length ?? 0)}）`}
          extra={((systemOrders?.length ?? 0) > 0) && (
            <button type="button" disabled={busy} onClick={() => cancelOrders(null)}>撤销全部</button>
          )}
        >
          <OpsTable
            rows={systemOrders ?? []}
            rowKey={order => order.orderId}
            empty="系统身份当前没有挂单。"
            columns={[
              { header: '品种', cell: order => `${nameById.get(order.assetId) ?? order.assetId}${order.variant ? ` ${order.variant}` : ''}（${order.assetId}）` },
              { header: '方向', cell: order => <OpsPill tone={order.side === 'buy' ? 'jade' : 'danger'}>{order.side === 'buy' ? '买' : '卖'}</OpsPill> },
              { header: '价格', numeric: true, cell: order => formatNumber(order.price, 2) },
              { header: '原始/剩余', numeric: true, cell: order => `${formatNumber(order.originalQuantity, 2)} / ${formatNumber(order.remainingQuantity, 2)}` },
              { header: '状态', cell: order => order.status },
              { header: '创建', cell: order => formatTime(order.createdAt) },
              {
                header: '操作',
                cell: order => (
                  <button type="button" disabled={busy} onClick={() => cancelOrders(order.assetId)}>撤销</button>
                ),
              },
            ]}
          />
        </OpsCard>
      </OpsAsyncView>

      <ConfirmDialog
        open={pending !== null}
        title={pending?.title ?? ''}
        description={pending?.description ?? ''}
        danger={pending?.danger ?? false}
        confirmLabel="确认执行"
        cancelLabel="取消"
        onConfirm={() => {
          const action = pending
          if (action) void execute(action.run)
        }}
        onClose={() => setPending(null)}
      />
    </>
  )
}
