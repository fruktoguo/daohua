import { useState } from 'react'
import { getOpsToken, opsGet, setOpsToken } from './opsClient'
import type { OpsOverview } from './opsTypes'
import { OpsPill, formatDuration, formatNumber, useOpsData } from './opsUi'
import { OpsActionsView } from './views/ActionsView'
import { OpsPlayersView, OpsSessionsView, OpsStorageView } from './views/DataViews'
import { OpsBookView, OpsMarketMetricsView, OpsOrdersView, OpsTradesView } from './views/MarketViews'
import { OpsConfigView, OpsHealthView, OpsOverviewView } from './views/OverviewViews'
import { OpsComputeView, OpsLogsView, OpsSchedulerView } from './views/RuntimeViews'
import './ops.css'

/** 面板视图标识。 */
type OpsViewId =
  | 'overview' | 'health'
  | 'scheduler' | 'compute' | 'logs'
  | 'players' | 'sessions' | 'storage'
  | 'book' | 'orders' | 'trades' | 'market'
  | 'config' | 'actions'

/** 左侧竖排导航分组。 */
const NAV_GROUPS: Array<{ group: string; items: Array<{ id: OpsViewId; label: string }> }> = [
  { group: '总览', items: [{ id: 'overview', label: '概览' }, { id: 'health', label: '健康' }] },
  { group: '运行', items: [{ id: 'scheduler', label: '调度' }, { id: 'compute', label: '计算' }, { id: 'logs', label: '日志' }] },
  { group: '数据', items: [{ id: 'players', label: '玩家' }, { id: 'sessions', label: '会话' }, { id: 'storage', label: '存储' }] },
  {
    group: '交易',
    items: [
      { id: 'book', label: '盘口' },
      { id: 'orders', label: '挂单' },
      { id: 'trades', label: '成交' },
      { id: 'market', label: '市场指标' },
    ],
  },
  { group: '系统', items: [{ id: 'config', label: '配置' }, { id: 'actions', label: '运维操作' }] },
]

/** 自动刷新间隔（毫秒）。 */
const AUTO_REFRESH_MS = 3000

/**
 * 本地运维面板外壳（Development-only）。
 *
 * 复用游戏全局样式与公共弹窗组件；文案直接使用中文（面板不面向玩家，不参与多语言发布）。
 */
export function OpsApp(): React.JSX.Element {
  const [view, setView] = useState<OpsViewId>('overview')
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [token, setToken] = useState(getOpsToken())
  const intervalMs = autoRefresh ? AUTO_REFRESH_MS : 0

  const { data: overview } = useOpsData<OpsOverview>(signal => opsGet('/overview', signal), [], intervalMs)

  return (
    <div className="ops-root">
      <header className="ops-topbar">
        <div className="ops-brand">
          <h1>道化太初 · 本地运维面板</h1>
          <span className="ops-brand-sub">仅本地开发使用：生产环境不装配面板，部署层已拦截 /ops</span>
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
          <label className="ops-token">
            令牌
            <input
              type="password"
              value={token}
              placeholder="写操作令牌"
              onChange={event => {
                setToken(event.target.value)
                setOpsToken(event.target.value)
              }}
            />
          </label>
          <label className="ops-auto">
            <input type="checkbox" checked={autoRefresh} onChange={event => setAutoRefresh(event.target.checked)} />
            自动刷新
          </label>
          {token ? <OpsPill tone="jade">令牌已设置</OpsPill> : <OpsPill tone="warn">未设置令牌</OpsPill>}
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
          {view === 'book' && <OpsBookView />}
          {view === 'orders' && <OpsOrdersView />}
          {view === 'trades' && <OpsTradesView intervalMs={intervalMs} />}
          {view === 'market' && <OpsMarketMetricsView intervalMs={intervalMs} />}
          {view === 'config' && <OpsConfigView />}
          {view === 'actions' && <OpsActionsView />}
        </main>
      </div>
    </div>
  )
}
