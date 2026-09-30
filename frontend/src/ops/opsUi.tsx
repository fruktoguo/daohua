import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'

/**
 * 运维面板共享展示原语与数据加载 Hook。
 *
 * 只提供结构性部件（卡片、表格、键值、状态胶囊）与格式化；
 * 颜色与控件外观全部取自游戏全局主题变量，不新造一套配色。
 */

/** 面板语义色调（映射到游戏主题变量）。 */
export type OpsTone = 'default' | 'gold' | 'jade' | 'qi' | 'danger' | 'warn' | 'muted'

/** 面板语义色调对应的类名。 */
export const TONE_CLASS: Record<OpsTone, string> = {
  default: '',
  gold: 'ops-tone--gold',
  jade: 'ops-tone--jade',
  qi: 'ops-tone--qi',
  danger: 'ops-tone--danger',
  warn: 'ops-tone--warn',
  muted: 'ops-tone--muted',
}

/** 字节数可读化。 */
export function formatBytes(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let scaled = value
  let index = 0
  while (scaled >= 1024 && index < units.length - 1) {
    scaled /= 1024
    index += 1
  }
  return `${scaled.toFixed(index === 0 ? 0 : 1)} ${units[index]}`
}

/** 秒数可读化（天/时/分/秒）。 */
export function formatDuration(seconds: number | null | undefined): string {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds)) return '-'
  const total = Math.max(0, Math.floor(seconds))
  const days = Math.floor(total / 86400)
  const hours = Math.floor(total / 3600) % 24
  const minutes = Math.floor(total / 60) % 60
  return `${days ? `${days}天 ` : ''}${hours}时 ${minutes}分 ${total % 60}秒`
}

/** 数值可读化（千分位）。 */
export function formatNumber(value: number | null | undefined, digits = 0): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-'
  return value.toLocaleString('zh-CN', { minimumFractionDigits: digits, maximumFractionDigits: digits })
}

/** 时间戳可读化。 */
export function formatTime(value: string | number | null | undefined): string {
  if (value === null || value === undefined) return '-'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString('zh-CN')
}

/** 毫秒时间戳可读化。 */
export function formatMillis(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-'
  return formatTime(value)
}

/** 日志级别中文化。 */
export function levelLabel(level: string): string {
  return ({ Trace: '追踪', Debug: '调试', Information: '信息', Warning: '警告', Error: '错误', Critical: '致命' } as Record<string, string>)[level] ?? level
}

/** 日志级别色调。 */
export function levelTone(level: string): OpsTone {
  if (level === 'Error' || level === 'Critical') return 'danger'
  if (level === 'Warning') return 'warn'
  if (level === 'Information') return 'jade'
  return 'muted'
}

/** 在线状态中文化。 */
export function presenceLabel(presence: string): string {
  return ({ online: '在线', offline: '离线', suspended: '挂起' } as Record<string, string>)[presence] ?? presence
}

/** 健康状态中文化。 */
export function healthLabel(status: string): string {
  return ({ healthy: '健康', degraded: '降级', unhealthy: '异常', ok: '正常' } as Record<string, string>)[status] ?? status
}

/** 健康状态色调。 */
export function healthTone(status: string): OpsTone {
  if (status === 'healthy' || status === 'ok') return 'jade'
  if (status === 'degraded') return 'warn'
  return 'danger'
}

/** 面板卡片容器。 */
export function OpsCard({ title, extra, children }: { title?: string; extra?: ReactNode; children: ReactNode }): React.JSX.Element {
  return (
    <section className="ops-card">
      {(title || extra) && (
        <header className="ops-card-head">
          {title && <h3>{title}</h3>}
          {extra && <div className="ops-card-extra">{extra}</div>}
        </header>
      )}
      <div className="ops-card-body">{children}</div>
    </section>
  )
}

/** 指标卡片。 */
export function OpsStat({ label, value, tone = 'default', hint }: { label: string; value: ReactNode; tone?: OpsTone; hint?: string }): React.JSX.Element {
  return (
    <div className={`ops-stat ${TONE_CLASS[tone]}`}>
      <span className="ops-stat-label">{label}</span>
      <span className="ops-stat-value">{value}</span>
      {hint && <span className="ops-stat-hint">{hint}</span>}
    </div>
  )
}

/** 指标卡片网格。 */
export function OpsStatGrid({ children }: { children: ReactNode }): React.JSX.Element {
  return <div className="ops-stat-grid">{children}</div>
}

/** 状态胶囊。 */
export function OpsPill({ children, tone = 'default' }: { children: ReactNode; tone?: OpsTone }): React.JSX.Element {
  return <span className={`ops-pill ${TONE_CLASS[tone]}`}>{children}</span>
}

/** 表格列定义。 */
export interface OpsColumn<T> {
  /** 列标题。 */
  header: string
  /** 单元格渲染。 */
  cell: (row: T) => ReactNode
  /** 是否右对齐（数值列）。 */
  numeric?: boolean
}

/** 数据表格：行键由 rowKey 提供，避免索引作为 React key。 */
export function OpsTable<T>({ columns, rows, rowKey, empty }: {
  columns: Array<OpsColumn<T>>
  rows: T[]
  rowKey: (row: T) => string
  empty?: string
}): React.JSX.Element {
  if (rows.length === 0) return <p className="ops-empty">{empty ?? '暂无数据。'}</p>
  return (
    <div className="ops-table-wrap">
      <table className="ops-table">
        <thead>
          <tr>
            {columns.map(column => (
              <th key={column.header} className={column.numeric ? 'ops-numeric' : undefined}>{column.header}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map(row => (
            <tr key={rowKey(row)}>
              {columns.map(column => (
                <td key={column.header} className={column.numeric ? 'ops-numeric' : undefined}>{column.cell(row)}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** 键值列表。 */
export function OpsKeyValues({ items }: { items: Array<[string, ReactNode]> }): React.JSX.Element {
  return (
    <dl className="ops-key-values">
      {items.map(([key, value]) => (
        <div key={key} className="ops-key-value">
          <dt>{key}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  )
}

/** 视图三态：加载中 / 失败 / 内容。 */
export function OpsAsyncView({ loading, error, hasData, children, onRetry }: {
  loading: boolean
  error: string | null
  hasData: boolean
  children: ReactNode
  onRetry: () => void
}): React.JSX.Element {
  if (error) {
    return (
      <div className="ops-error">
        <span>加载失败：{error}</span>
        <button type="button" onClick={onRetry}>重试</button>
      </div>
    )
  }
  if (loading && !hasData) return <p className="ops-empty">加载中…</p>
  return <>{children}</>
}

/**
 * 面板数据加载 Hook：首次加载 + 可选轮询 + 手动重试。
 * 每次加载都新建 AbortController，卸载或重载时中止上一次请求，避免旧响应覆盖新数据。
 */
export function useOpsData<T>(
  load: (signal: AbortSignal) => Promise<T>,
  deps: readonly unknown[],
  intervalMs = 0,
): { data: T | null; error: string | null; loading: boolean; reload: () => void } {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const loadRef = useRef(load)
  loadRef.current = load

  const reload = useCallback(() => {
    const controller = new AbortController()
    setLoading(true)
    loadRef.current(controller.signal)
      .then(value => {
        if (controller.signal.aborted) return
        setData(value)
        setError(null)
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return
        setError(cause instanceof Error ? cause.message : String(cause))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [])

  useEffect(() => reload(), [reload, ...deps])

  useEffect(() => {
    if (intervalMs <= 0) return
    const timer = window.setInterval(reload, intervalMs)
    return () => window.clearInterval(timer)
  }, [intervalMs, reload])

  return { data, error, loading, reload }
}
