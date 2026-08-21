import { memo } from 'react'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'

interface ConnectingPageProps {
  onCancel?: () => void
}

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

interface ConnectionErrorPageProps {
  onRetry?: () => void
  onLogout?: () => void
}

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
