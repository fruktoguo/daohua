import { memo } from 'react'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'

/** 连接中页面 Props */
interface ConnectingPageProps {
  /** 点击返回登录页回调 */
  onCancel?: () => void
}

/**
 * 网络连接建立中过渡页面 (ConnectingPage)
 *
 * 展示全屏灵气粒子动效、太极聚灵旋转指示器与连接进度条，支持取消连接并登出。
 */
export const ConnectingPage = memo(function ConnectingPage({ onCancel }: ConnectingPageProps): React.JSX.Element {
  const t = useT()
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
        <h2 className="status-title">{t('app.connect.connectingTitle')}</h2>
        <p className="status-subtitle">{t('app.connect.connectingSubtitle')}</p>
        <div className="status-progress-bar">
          <div className="status-progress-glow" />
        </div>
        <div className="status-actions">
          <button type="button" className="status-btn secondary" onClick={handleCancel}>
            {t('app.connect.returnLogin')}
          </button>
        </div>
      </div>
    </div>
  )
})

/** 连接异常重试页面 Props */
interface ConnectionErrorPageProps {
  /** 点击重试连接回调 */
  onRetry?: () => void
  /** 点击切换道号/登出回调 */
  onLogout?: () => void
}

/**
 * 网络连接失败/断开页面 (ConnectionErrorPage)
 *
 * 展示断连警示图标与说明文案，提供一键重新连接 (Retry) 与切换账号 (Switch Account) 选项。
 */
export const ConnectionErrorPage = memo(function ConnectionErrorPage({ onRetry, onLogout }: ConnectionErrorPageProps): React.JSX.Element {
  const t = useT()
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
        <h2 className="status-title">{t('app.connect.errorTitle')}</h2>
        <p className="status-subtitle">
          {t('app.connect.errorSubtitle')}
        </p>
        <div className="status-actions">
          <button type="button" className="status-btn primary" onClick={handleRetry}>
            {t('app.connect.retryBtn')}
          </button>
          <button type="button" className="status-btn secondary" onClick={handleLogout}>
            {t('app.connect.switchAccountBtn')}
          </button>
        </div>
      </div>
    </div>
  )
})
