import { useEffect, useState, type FormEvent } from 'react'
import {
  OPS_UNAUTHORIZED_EVENT,
  OpsApiError,
  getOpsBasePath,
  getOpsToken,
  opsGet,
  setOpsToken,
} from './opsClient'
import type { OpsOverview } from './opsTypes'
import { OpsPill, formatDuration, formatNumber, useOpsData } from './opsUi'
import { OpsActionsView } from './views/ActionsView'
import { OpsPlayersView, OpsSessionsView, OpsStorageView } from './views/DataViews'
import { OpsBookView, OpsMarketMetricsView, OpsOrdersView, OpsSystemOrderView, OpsTradesView } from './views/MarketViews'
import { OpsConfigView, OpsHealthView, OpsOverviewView } from './views/OverviewViews'
import { OpsComputeView, OpsLogsView, OpsSchedulerView } from './views/RuntimeViews'
import { OpsAccountAbuseView } from './views/AccountAbuseView'
import './ops.css'

/** 面板视图标识。 */
type OpsViewId =
  | 'overview' | 'health'
  | 'scheduler' | 'compute' | 'logs'
  | 'players' | 'sessions' | 'storage'
  | 'accountAbuse'
  | 'book' | 'orders' | 'trades' | 'market' | 'systemOrders'
  | 'config' | 'actions'

/** 左侧竖排导航分组。 */
const NAV_GROUPS: Array<{ group: string; items: Array<{ id: OpsViewId; label: string }> }> = [
  { group: '总览', items: [{ id: 'overview', label: '概览' }, { id: 'health', label: '健康' }] },
  { group: '运行', items: [{ id: 'scheduler', label: '调度' }, { id: 'compute', label: '计算' }, { id: 'logs', label: '日志' }] },
  { group: '数据', items: [{ id: 'players', label: '玩家' }, { id: 'sessions', label: '会话' }, { id: 'storage', label: '存储' }, { id: 'accountAbuse', label: '账号检测' }] },
  {
    group: '交易',
    items: [
      { id: 'book', label: '盘口' },
      { id: 'orders', label: '挂单' },
      { id: 'trades', label: '成交' },
      { id: 'market', label: '市场指标' },
      { id: 'systemOrders', label: '系统下单' },
    ],
  },
  { group: '系统', items: [{ id: 'config', label: '配置' }, { id: 'actions', label: '运维操作' }] },
]

/** 自动刷新间隔（毫秒）。 */
const AUTO_REFRESH_MS = 3000

/**
 * 运维监控面板外壳。
 *
 * 未通过密码验证前仅渲染独立密码登录门禁页，不发起后台轮询；
 * 验证通过后挂载完整监控与运维视图。
 */
export function OpsApp(): React.JSX.Element {
  const basePath = getOpsBasePath()
  const [authenticated, setAuthenticated] = useState(false)
  const [passwordInput, setPasswordInput] = useState('')
  const [verifying, setVerifying] = useState(() => getOpsToken().length > 0)
  const [authError, setAuthError] = useState<string | null>(null)

  useEffect(() => {
    const handleUnauthorized = (): void => {
      setOpsToken('')
      setAuthenticated(false)
      setAuthError('密码已失效或未授权，请重新输入。')
    }
    window.addEventListener(OPS_UNAUTHORIZED_EVENT, handleUnauthorized)
    return () => window.removeEventListener(OPS_UNAUTHORIZED_EVENT, handleUnauthorized)
  }, [])

  useEffect(() => {
    const saved = getOpsToken()
    if (!saved) return
    const controller = new AbortController()
    setVerifying(true)
    opsGet<OpsOverview>('/overview', controller.signal)
      .then(() => {
        if (!controller.signal.aborted) {
          setAuthenticated(true)
          setAuthError(null)
        }
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setOpsToken('')
        setAuthenticated(false)
        if (error instanceof OpsApiError && error.status === 404) {
          setAuthError('当前后台随机路径无效或未启用。')
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setVerifying(false)
      })
    return () => controller.abort()
  }, [])

  const handleLogin = async (event: FormEvent<HTMLFormElement>): Promise<void> => {
    event.preventDefault()
    const trimmed = passwordInput.trim()
    if (!trimmed) {
      setAuthError('请输入后台访问密码。')
      return
    }
    setVerifying(true)
    setAuthError(null)
    setOpsToken(trimmed)
    try {
      await opsGet<OpsOverview>('/overview')
      setPasswordInput('')
      setAuthenticated(true)
    } catch (error) {
      setOpsToken('')
      setAuthenticated(false)
      if (error instanceof OpsApiError && error.status === 401) {
        setAuthError('密码错误，请重新输入。')
      } else if (error instanceof OpsApiError && error.status === 404) {
        setAuthError('当前后台随机路径无效或未启用。')
      } else {
        setAuthError(error instanceof Error ? error.message : '验证失败，请稍后重试。')
      }
    } finally {
      setVerifying(false)
    }
  }

  const handleLock = (): void => {
    setOpsToken('')
    setAuthenticated(false)
    setPasswordInput('')
    setAuthError(null)
  }

  if (!authenticated) {
    return (
      <div className="ops-root">
        <div className="ops-login-wrap">
          <form className="ops-login-card" onSubmit={event => void handleLogin(event)}>
            <div className="ops-login-head">
              <h1>道化太初 · 运维监控后台</h1>
              <OpsPill tone="gold">{basePath}</OpsPill>
            </div>
            <p className="ops-login-desc">请输入后台访问密码以查看实时监控与执行运维操作。</p>
            <label className="ops-login-field">
              <span>访问密码</span>
              <input
                type="password"
                value={passwordInput}
                placeholder="请输入运维密码"
                autoFocus
                disabled={verifying}
                onChange={event => setPasswordInput(event.target.value)}
              />
            </label>
            {authError && <div className="ops-error-text">{authError}</div>}
            <div className="ops-login-actions">
              <button type="submit" disabled={verifying}>
                {verifying ? '验证中…' : '进入后台'}
              </button>
            </div>
          </form>
        </div>
      </div>
    )
  }

  return <OpsDashboard basePath={basePath} onLock={handleLock} />
}

/** 已通过密码验证后的监控主界面。 */
function OpsDashboard({ basePath, onLock }: { basePath: string; onLock: () => void }): React.JSX.Element {
  const [view, setView] = useState<OpsViewId>('overview')
  const [autoRefresh, setAutoRefresh] = useState(true)
  const intervalMs = autoRefresh ? AUTO_REFRESH_MS : 0

  const { data: overview } = useOpsData<OpsOverview>(signal => opsGet('/overview', signal), [], intervalMs)

  return (
    <div className="ops-root">
      <header className="ops-topbar">
        <div className="ops-brand">
          <h1>道化太初 · 运维监控后台</h1>
          <span className="ops-brand-sub">入口 {basePath} · 读写全量密码保护</span>
        </div>
        <div className="ops-status">
          {overview ? (
            <>
              <OpsPill tone="gold">{overview.service}</OpsPill>
              <OpsPill tone="qi">{overview.environment}</OpsPill>
              <span className="ops-status-item">版本 {overview.version}</span>
              <span className="ops-status-item">运行 {formatDuration(overview.uptimeSeconds)}</span>
              <span className="ops-status-item">会话 {formatNumber(overview.sessions)}</span>
              <span className="ops-status-item">玩家 {formatNumber(overview.loadedPlayers)}</span>
            </>
          ) : (
            <span className="ops-status-item">状态加载中…</span>
          )}
        </div>
        <div className="ops-controls">
          <label className="ops-auto">
            <input type="checkbox" checked={autoRefresh} onChange={event => setAutoRefresh(event.target.checked)} />
            自动刷新
          </label>
          <OpsPill tone="jade">已验证</OpsPill>
          <button type="button" onClick={onLock}>退出锁定</button>
        </div>
      </header>

      <div className="ops-body">
        <nav className="ops-nav">
          {NAV_GROUPS.map(group => (
            <div key={group.group} className="ops-nav-group">
              <span className="ops-nav-title">{group.group}</span>
              {group.items.map(item => (
                <button
                  key={item.id}
                  type="button"
                  className={item.id === view ? 'active' : undefined}
                  onClick={() => setView(item.id)}
                >
                  {item.label}
                </button>
              ))}
            </div>
          ))}
        </nav>

        <main className="ops-main">
          {view === 'overview' && <OpsOverviewView intervalMs={intervalMs} />}
          {view === 'health' && <OpsHealthView intervalMs={intervalMs} />}
          {view === 'scheduler' && <OpsSchedulerView intervalMs={intervalMs} />}
          {view === 'compute' && <OpsComputeView intervalMs={intervalMs} />}
          {view === 'logs' && <OpsLogsView intervalMs={intervalMs} />}
          {view === 'players' && <OpsPlayersView intervalMs={intervalMs} />}
          {view === 'sessions' && <OpsSessionsView intervalMs={intervalMs} />}
          {view === 'storage' && <OpsStorageView intervalMs={intervalMs} />}
          {view === 'accountAbuse' && <OpsAccountAbuseView intervalMs={intervalMs} />}
          {view === 'book' && <OpsBookView />}
          {view === 'orders' && <OpsOrdersView />}
          {view === 'trades' && <OpsTradesView intervalMs={intervalMs} />}
          {view === 'market' && <OpsMarketMetricsView intervalMs={intervalMs} />}
          {view === 'systemOrders' && <OpsSystemOrderView intervalMs={intervalMs} />}
          {view === 'config' && <OpsConfigView />}
          {view === 'actions' && <OpsActionsView />}
        </main>
      </div>
    </div>
  )
}
