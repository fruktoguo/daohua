import { opsGet } from '../opsClient'
import type { OpsConfig, OpsHealth, OpsOverview } from '../opsTypes'
import {
  OpsAsyncView,
  OpsCard,
  OpsKeyValues,
  OpsPill,
  OpsStat,
  OpsStatGrid,
  OpsTable,
  formatBytes,
  formatDuration,
  formatNumber,
  formatTime,
  healthLabel,
  healthTone,
  useOpsData,
} from '../opsUi'

/** 概览视图：进程资源、版本口径与市场累计指标。 */
export function OpsOverviewView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsOverview>(signal => opsGet('/overview', signal), [], intervalMs)
  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      {data && (
        <>
          <OpsStatGrid>
            <OpsStat label="服务" value={data.service} tone="gold" />
            <OpsStat label="环境" value={data.environment} tone="qi" />
            <OpsStat label="版本" value={data.version} />
            <OpsStat label="运行时长" value={formatDuration(data.uptimeSeconds)} />
            <OpsStat label="在线会话" value={formatNumber(data.sessions)} tone={data.sessions > 0 ? 'jade' : 'muted'} />
            <OpsStat label="已加载玩家" value={formatNumber(data.loadedPlayers)} tone={data.loadedPlayers > 0 ? 'jade' : 'muted'} />
            <OpsStat label="工作集内存" value={formatBytes(data.workingSetBytes)} />
            <OpsStat label="GC 托管内存" value={formatBytes(data.gcTotalMemoryBytes)} />
            <OpsStat label="线程数" value={formatNumber(data.threadCount)} />
          </OpsStatGrid>
          <OpsCard title="版本与配表口径">
            <OpsKeyValues
              items={[
                ['协议版本', formatNumber(data.protocolVersion)],
                ['存档 Schema 版本', formatNumber(data.schemaVersion)],
                ['配表版本', data.gameDataVersion],
                ['配表指纹', data.gameDataFingerprint],
                ['主机', data.machineName],
                ['进程 ID', formatNumber(data.processId)],
                ['CPU 核数', formatNumber(data.processorCount)],
                ['启动时刻', formatTime(data.startedAt)],
              ]}
            />
          </OpsCard>
          <OpsCard title="市场累计指标">
            <OpsStatGrid>
              <OpsStat label="市场分片" value={formatNumber(data.market.partitionCount)} />
              <OpsStat label="活动订单" value={formatNumber(data.market.activeOrderCount)} />
              <OpsStat label="累计挂单" value={formatNumber(data.market.placementCount)} />
              <OpsStat label="累计成交" value={formatNumber(data.market.matchCount)} />
              <OpsStat label="累计撤单" value={formatNumber(data.market.cancelCount)} />
              <OpsStat label="累计拒绝" value={formatNumber(data.market.rejectedCount)} tone={data.market.rejectedCount > 0 ? 'warn' : 'muted'} />
              <OpsStat label="成交数量" value={formatNumber(data.market.tradedQuantity, 2)} />
              <OpsStat label="成交金额" value={formatNumber(data.market.tradedGrossAmount, 2)} />
            </OpsStatGrid>
          </OpsCard>
        </>
      )}
    </OpsAsyncView>
  )
}

/** 健康视图：逐项健康检查状态、耗时与异常。 */
export function OpsHealthView({ intervalMs }: { intervalMs: number }): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsHealth>(signal => opsGet('/health', signal), [], intervalMs)
  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      {data && (
        <>
          <OpsStatGrid>
            <OpsStat label="总体状态" value={healthLabel(data.status)} tone={healthTone(data.status)} />
            <OpsStat label="总耗时" value={`${formatNumber(data.totalDurationMs, 1)} ms`} />
            <OpsStat label="检查项" value={formatNumber(data.checks.length)} />
          </OpsStatGrid>
          <OpsCard title="检查明细">
            <OpsTable
              rows={data.checks}
              rowKey={check => check.name}
              columns={[
                { header: '检查项', cell: check => check.name },
                { header: '状态', cell: check => <OpsPill tone={healthTone(check.status)}>{healthLabel(check.status)}</OpsPill> },
                { header: '耗时(ms)', numeric: true, cell: check => formatNumber(check.durationMs, 1) },
                { header: '标签', cell: check => check.tags.join('、') || '-' },
                { header: '说明', cell: check => check.description ?? '-' },
                { header: '异常', cell: check => (check.exception ? <span className="ops-tone--danger">{check.exception}</span> : '-') },
              ]}
            />
          </OpsCard>
        </>
      )}
    </OpsAsyncView>
  )
}

/** 配置视图：脱敏后的运行时配置口径。 */
export function OpsConfigView(): React.JSX.Element {
  const { data, error, loading, reload } = useOpsData<OpsConfig>(signal => opsGet('/config', signal), [])
  const flatten = (record: Record<string, string | number | boolean> | undefined): Array<[string, React.ReactNode]> =>
    Object.entries(record ?? {}).map(([key, value]) => [key, typeof value === 'boolean' ? (value ? '开启' : '关闭') : String(value)])

  return (
    <OpsAsyncView loading={loading} error={error} hasData={data !== null} onRetry={reload}>
      {data && (
        <>
          <OpsCard title="版本口径">
            <OpsKeyValues
              items={[
                ['服务', data.service],
                ['环境', data.environment],
                ['进程角色', data.processRole],
                ['协议版本', formatNumber(data.protocolVersion)],
                ['存档 Schema 版本', formatNumber(data.schemaVersion)],
                ['配表版本', data.gameDataVersion],
                ['配表指纹', data.gameDataFingerprint],
              ]}
            />
          </OpsCard>
          <OpsCard title="存储（连接串已脱敏）">
            <OpsKeyValues items={flatten(data.storage)} />
          </OpsCard>
          <OpsCard title="服务端与鉴权">
            <OpsKeyValues
              items={[
                ...flatten(data.server),
                ['会话有效期(天)', formatNumber(data.auth.sessionLifetimeDays)],
              ]}
            />
          </OpsCard>
          <OpsCard title="市场分片">
            <OpsKeyValues items={flatten(data.market)} />
          </OpsCard>
          <OpsCard title="冷挂机派发链">
            <OpsKeyValues items={flatten(data.compute)} />
          </OpsCard>
          <OpsCard title="防多开">
            <OpsKeyValues items={flatten(data.antiMultiOpen)} />
          </OpsCard>
        </>
      )}
    </OpsAsyncView>
  )
}
