import { useState } from 'react'
import { opsGet } from '../opsClient'
import type { OpsCompute, OpsLogEntry, OpsScheduler } from '../opsTypes'
import {
  OpsAsyncView,
  OpsCard,
  OpsPill,
  OpsStat,
  OpsStatGrid,
  OpsTable,
  formatDuration,
  formatNumber,
  formatTime,
  levelLabel,
  levelTone,
  presenceLabel,
  useOpsData,
} from '../opsUi'

/** 调度视图：推进边界、推进延迟分布、行动队列与最慢玩家。 */
export function OpsSchedulerView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsScheduler>(signal => opsGet('/scheduler', signal), [], intervalMs)
  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      {data && (
        <>
          <OpsStatGrid>
            <OpsStat label="已加载玩家" value={formatNumber(data.loadedPlayers)} />
            <OpsStat label="下次推进边界" value={formatTime(data.nextAdvanceBoundaryUtc)} />
            <OpsStat label="距下次推进" value={formatDuration(data.secondsToNextBoundary)} />
            <OpsStat label="推进延迟 p50" value={formatDuration(data.lag.p50Seconds)} />
            <OpsStat label="推进延迟 p95" value={formatDuration(data.lag.p95Seconds)} tone={data.lag.p95Seconds > 60 ? 'warn' : 'default'} />
            <OpsStat label="推进延迟峰值" value={formatDuration(data.lag.maxSeconds)} tone={data.lag.maxSeconds > 300 ? 'danger' : 'default'} />
            <OpsStat label="排队动作合计" value={formatNumber(data.queuedActions.total)} />
            <OpsStat label="单玩家队列峰值" value={formatNumber(data.queuedActions.max)} />
          </OpsStatGrid>
          <OpsCard title="在线状态分布">
            <OpsTable
              rows={data.byPresence}
              rowKey={item => item.presence}
              columns={[
                { header: '状态', cell: item => <OpsPill tone={item.presence === 'online' ? 'jade' : 'muted'}>{presenceLabel(item.presence)}</OpsPill> },
                { header: '人数', numeric: true, cell: item => formatNumber(item.count) },
              ]}
            />
          </OpsCard>
          <OpsCard title="推进欠债最深的玩家">
            <OpsTable
              rows={data.slowest}
              rowKey={item => item.playerId}
              columns={[
                { header: '玩家 ID', cell: item => item.playerId },
                { header: '道号', cell: item => item.displayName },
                { header: '上次模拟', cell: item => formatTime(item.lastSimulatedAt) },
                { header: '延迟', numeric: true, cell: item => formatDuration(item.lagSeconds) },
                { header: '排队动作', numeric: true, cell: item => formatNumber(item.queuedActions) },
              ]}
            />
          </OpsCard>
        </>
      )}
    </OpsAsyncView>
  )
}

/** 计算视图：节点组、链路计数与批次样本分布。 */
export function OpsComputeView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsCompute>(signal => opsGet('/compute', signal), [], intervalMs)
  const metrics = data?.metrics
  const sample = metrics?.recentBatches
  const batchFails = metrics
    ? metrics.batchesFailedWrite + metrics.batchesFailedTimeout + metrics.batchesFailedDisconnect + metrics.batchesFailedContract + metrics.batchesFailedError
    : 0
  const rejects = metrics
    ? metrics.tokenRejected + metrics.nodeLimitRejected + metrics.fingerprintRejected + metrics.handshakeRejected
    : 0

  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      {data && (
        <>
          {!data.enabled ? (
            <OpsCard title="派发链未启用">
              <p className="ops-empty">冷挂机派发链未启用（ComputeNode:Enabled 与令牌均未配置）。</p>
            </OpsCard>
          ) : (
            <>
              <OpsStatGrid>
                <OpsStat label="链路契约" value={`${data.contractVersion} / ${data.dispatchContractVersion}`} />
                <OpsStat label="在线节点" value={formatNumber(data.totals.nodeCount)} tone={data.totals.nodeCount > 0 ? 'jade' : 'muted'} />
                <OpsStat label="节点组容量" value={`${formatNumber(data.totals.capacity)} 批`} />
                <OpsStat label="在飞批次" value={formatNumber(data.totals.inFlight)} tone={data.totals.inFlight > 0 ? 'jade' : 'muted'} />
                <OpsStat label="等待结果帧" value={formatNumber(data.totals.pendingResults)} />
                <OpsStat label="出站积压帧" value={formatNumber(data.totals.outboundDepth)} tone={data.totals.outboundDepth > 32 ? 'warn' : 'default'} />
                {data.backlog && (
                  <>
                    <OpsStat label="任务积压" value={`${formatNumber(data.backlog.pending)}（到期 ${formatNumber(data.backlog.pendingDue)}）`} tone={data.backlog.pendingDue > 0 ? 'warn' : 'default'} />
                    <OpsStat label="最老积压" value={formatDuration(data.backlog.oldestPendingAgeSeconds)} />
                    <OpsStat label="在飞租约" value={formatNumber(data.backlog.leased)} />
                    <OpsStat label="dead 终态" value={formatNumber(data.backlog.dead)} tone={data.backlog.dead > 0 ? 'danger' : 'muted'} />
                  </>
                )}
                <OpsStat label="批次 成功/失败" value={`${formatNumber(metrics?.batchesCompleted ?? 0)} / ${formatNumber(batchFails)}`} tone={batchFails > 0 ? 'warn' : 'default'} />
                <OpsStat label="任务 落库/判死" value={`${formatNumber(metrics?.jobsApplied ?? 0)} / ${formatNumber(metrics?.jobsDead ?? 0)}`} tone={(metrics?.jobsDead ?? 0) > 0 ? 'danger' : 'default'} />
                <OpsStat label="拨入拒绝" value={formatNumber(rejects)} tone={rejects > 0 ? 'warn' : 'muted'} />
                <OpsStat label="迟到结果丢弃" value={formatNumber(metrics?.lateResultsDropped ?? 0)} />
              </OpsStatGrid>
              <OpsCard title="在线节点">
                <OpsTable
                  rows={data.nodes}
                  rowKey={node => node.nodeId}
                  empty="当前没有在线计算节点。"
                  columns={[
                    { header: '节点', cell: node => `${node.name}${node.draining ? '（排空中）' : ''}` },
                    { header: 'NodeId', cell: node => node.nodeId.slice(0, 8) },
                    { header: '容量', numeric: true, cell: node => formatNumber(node.capacity) },
                    { header: '在飞', numeric: true, cell: node => formatNumber(node.inFlight) },
                    { header: '出站', numeric: true, cell: node => formatNumber(node.outboundDepth) },
                    { header: '待回', numeric: true, cell: node => formatNumber(node.pendingResults) },
                    { header: '拨入时长', cell: node => formatDuration(node.uptimeSeconds) },
                    { header: '运行时指纹', cell: node => node.runtimeFingerprint ?? '-' },
                    { header: '批次 发/成/败', cell: node => `${formatNumber(node.batchesSent)}/${formatNumber(node.batchesCompleted)}/${formatNumber(node.batchesFailed)}` },
                    { header: '任务 成/败', cell: node => `${formatNumber(node.jobsApplied)}/${formatNumber(node.jobsFailed)}` },
                    { header: 'RTT 最近/均/峰(ms)', cell: node => `${formatNumber(node.lastRoundTripMs, 1)}/${formatNumber(node.avgRoundTripMs, 1)}/${formatNumber(node.maxRoundTripMs, 1)}` },
                  ]}
                />
              </OpsCard>
              <OpsCard title={`链路计数与最近批次样本（${formatNumber(sample?.sampleCount ?? 0)} 批）`}>
                <OpsTable
                  rows={[
                    { k1: '批次写入', v1: metrics?.batchesWritten, k2: '任务领取', v2: metrics?.jobsClaimed },
                    { k1: '批次失败·写', v1: metrics?.batchesFailedWrite, k2: '任务派发上链', v2: metrics?.jobsDispatched },
                    { k1: '批次失败·超时', v1: metrics?.batchesFailedTimeout, k2: '任务退回 pending', v2: metrics?.jobsReleased },
                    { k1: '批次失败·断线', v1: metrics?.batchesFailedDisconnect, k2: '任务漂移丢弃', v2: metrics?.jobsDriftDiscarded },
                    { k1: '批次失败·契约', v1: metrics?.batchesFailedContract, k2: '任务节点报错', v2: metrics?.jobsNodeFailed },
                    { k1: '批次失败·异常', v1: metrics?.batchesFailedError, k2: '任务落库异常', v2: metrics?.jobsPersistFailed },
                    { k1: '拒绝·令牌', v1: metrics?.tokenRejected, k2: '拒绝·节点上限', v2: metrics?.nodeLimitRejected },
                    { k1: '拒绝·指纹', v1: metrics?.fingerprintRejected, k2: '拒绝·握手违规', v2: metrics?.handshakeRejected },
                  ]}
                  rowKey={row => row.k1}
                  columns={[
                    { header: '口径', cell: row => row.k1 },
                    { header: '计数', numeric: true, cell: row => formatNumber(row.v1 ?? 0) },
                    { header: '口径', cell: row => row.k2 },
                    { header: '计数', numeric: true, cell: row => formatNumber(row.v2 ?? 0) },
                  ]}
                />
                <OpsTable
                  rows={[
                    { label: '批次往返 ms', p50: sample?.roundTripP50Ms, p95: sample?.roundTripP95Ms, max: sample?.roundTripMaxMs, avg: sample?.roundTripAvgMs },
                    { label: '积压等待 ms', p50: sample?.waitAgeP50Ms, p95: sample?.waitAgeP95Ms, max: sample?.waitAgeMaxMs, avg: undefined },
                    { label: '批内任务数', p50: sample?.jobsP50, p95: undefined, max: sample?.jobsMax, avg: undefined },
                    { label: '批次字节', p50: sample?.bytesP50, p95: undefined, max: sample?.bytesMax, avg: undefined },
                  ]}
                  rowKey={row => row.label}
                  columns={[
                    { header: '样本口径', cell: row => row.label },
                    { header: 'p50', numeric: true, cell: row => formatNumber(row.p50 ?? null, 1) },
                    { header: 'p95', numeric: true, cell: row => formatNumber(row.p95 ?? null, 1) },
                    { header: 'max', numeric: true, cell: row => formatNumber(row.max ?? null, 1) },
                    { header: '均值', numeric: true, cell: row => formatNumber(row.avg ?? null, 1) },
                  ]}
                />
                <p className="ops-note">
                  批内任务数分布：
                  {Object.entries(sample?.jobsHistogram ?? {}).map(([key, value]) => `${key}=${value}`).join('  ') || '-'}
                </p>
              </OpsCard>
            </>
          )}
        </>
      )}
    </OpsAsyncView>
  )
}

/** 日志视图：级别、类别与关键词过滤。 */
export function OpsLogsView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const [level, setLevel] = useState('Information')
  const [categoryDraft, setCategoryDraft] = useState('')
  const [queryDraft, setQueryDraft] = useState('')
  const [filter, setFilter] = useState({ category: '', query: '' })

  const { data, error, loading, reload } = useOpsData<OpsLogEntry[]>(
    signal => opsGet(
      `/logs?level=${level}&limit=300&category=${encodeURIComponent(filter.category)}&q=${encodeURIComponent(filter.query)}`,
      signal,
    ),
    [level, filter.category, filter.query],
    intervalMs,
  )

  return (
    <>
      <OpsCard title="过滤">
        <div className="ops-toolbar">
          <label>
            最低级别
            <select value={level} onChange={event => setLevel(event.target.value)}>
              {['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'].map(item => (
                <option key={item} value={item}>{levelLabel(item)}</option>
              ))}
            </select>
          </label>
          <label>
            类别
            <input value={categoryDraft} placeholder="如 MarketRuntime" onChange={event => setCategoryDraft(event.target.value)} />
          </label>
          <label>
            关键词
            <input
              value={queryDraft}
              placeholder="正文或异常关键词"
              onChange={event => setQueryDraft(event.target.value)}
              onKeyDown={event => {
                if (event.key === 'Enter') setFilter({ category: categoryDraft, query: queryDraft })
              }}
            />
          </label>
          <button type="button" onClick={() => setFilter({ category: categoryDraft, query: queryDraft })}>应用</button>
          <button
            type="button"
            onClick={() => {
              setCategoryDraft('')
              setQueryDraft('')
              setFilter({ category: '', query: '' })
            }}
          >
            清空
          </button>
          <button type="button" onClick={reload}>刷新</button>
        </div>
      </OpsCard>
      <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
        <OpsCard title={`最近日志（${formatNumber(data?.length ?? 0)} 条）`}>
          {data && data.length > 0 ? (
            <div className="ops-log-list">
              {data.map(entry => (
                <div key={entry.sequence} className="ops-log-row">
                  <span className="ops-log-time">{formatTime(entry.timestamp)}</span>
                  <OpsPill tone={levelTone(entry.level)}>{levelLabel(entry.level)}</OpsPill>
                  <span className="ops-log-category">{entry.category}</span>
                  <span className="ops-log-message">
                    {entry.message}
                    {entry.exception && <pre className="ops-log-exception">{entry.exception}</pre>}
                  </span>
                </div>
              ))}
            </div>
          ) : (
            <p className="ops-empty">暂无符合条件的日志。</p>
          )}
        </OpsCard>
      </OpsAsyncView>
    </>
  )
}
