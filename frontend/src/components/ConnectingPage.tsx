import { gameClient } from '../network/gameClient'

interface ConnectingPageProps {
  onCancel?: () => void
}

export function ConnectingPage({ onCancel }: ConnectingPageProps): React.JSX.Element {
  const handleCancel = () => {
    gameClient.logout()
    onCancel?.()
  }

  return (
    <div className="status-screen connecting-screen">
      <div className="motes" aria-hidden="true">
        <i /><i /><i /><i /><i /><i />
      </div>
      <div className="status-card panel">
        <div className="status-spinner-aura" aria-hidden="true">
          <div className="status-pulsing-circle" />
          <div className="status-orbit-dot" />
        </div>
        <h2 className="status-title">正在连通仙界</h2>
        <p className="status-subtitle">感应天地法则，正在同步宗门仙境与修士本相…</p>
        <div className="status-progress-bar">
          <div className="status-progress-glow" />
        </div>
        <div className="status-actions">
          <button type="button" className="status-btn secondary" onClick={handleCancel}>
            返回登录
          </button>
        </div>
      </div>
    </div>
  )
}

interface ConnectionErrorPageProps {
  onRetry?: () => void
  onLogout?: () => void
}

export function ConnectionErrorPage({ onRetry, onLogout }: ConnectionErrorPageProps): React.JSX.Element {
  const handleRetry = () => {
    gameClient.connect()
    onRetry?.()
  }

  const handleLogout = () => {
    gameClient.logout()
    onLogout?.()
  }

  return (
    <div className="status-screen error-screen">
      <div className="motes" aria-hidden="true">
        <i /><i /><i /><i /><i /><i />
      </div>
      <div className="status-card panel">
        <div className="status-error-icon" aria-hidden="true">
          <span>⚡</span>
        </div>
        <h2 className="status-title">仙途阻隔 · 未能连通</h2>
        <p className="status-subtitle">
          未能连接到修仙世界服务端。请检查您的网络连接或确认后端服务已正常启动。
        </p>
        <div className="status-actions">
          <button type="button" className="status-btn primary" onClick={handleRetry}>
            重新连接
          </button>
          <button type="button" className="status-btn secondary" onClick={handleLogout}>
            返回登录 / 切换账号
          </button>
        </div>
      </div>
    </div>
  )
}
