import { useState } from 'react'
import { getStoredAccount, setStoredAccount } from '../network/account'
import { getAccessToken, setAccessToken } from '../network/access_token'
import { gameClient } from '../network/gameClient'

interface LoginPageProps {
  onLoggedIn?: () => void
}

const NAME_REGEX = /^[\u4e00-\u9fa5a-zA-Z0-9_]+$/

export function LoginPage({ onLoggedIn }: LoginPageProps): React.JSX.Element {
  const existing = getStoredAccount()
  const [account, setAccount] = useState(existing?.account ?? '')
  const [characterName, setCharacterName] = useState(existing?.name ?? '')
  const [token, setToken] = useState(getAccessToken())
  const [showTokenField, setShowTokenField] = useState(Boolean(getAccessToken()))
  const [error, setError] = useState<string | null>(null)

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    const cleanAccount = account.trim()
    const cleanName = (characterName || cleanAccount).trim()

    if (!cleanAccount) {
      setError('请输入登录账号')
      return
    }
    if (cleanAccount.length < 2 || cleanAccount.length > 20) {
      setError('账号长度须在 2 至 20 个字符之间')
      return
    }
    if (!NAME_REGEX.test(cleanAccount)) {
      setError('账号仅支持英文、数字、中文与下划线')
      return
    }
    if (cleanName.length < 2 || cleanName.length > 12) {
      setError('修士道号长度须在 2 至 12 个字符之间')
      return
    }
    if (!NAME_REGEX.test(cleanName)) {
      setError('修士道号仅支持英文、数字、中文与下划线')
      return
    }

    setAccessToken(token)
    setStoredAccount(cleanAccount, cleanName)
    gameClient.connect()
    onLoggedIn?.()
  }

  return (
    <div className="login-screen">
      <div className="motes" aria-hidden="true">
        <i /><i /><i /><i /><i /><i />
      </div>
      <div className="login-card panel">
        <div className="login-badge-aura" aria-hidden="true">
          <span className="login-yin-yang" />
        </div>
        <h1 className="login-title">道化太初</h1>
        <p className="login-subtitle">修真无岁月 · 仙道求索长</p>

        <form onSubmit={handleSubmit} className="login-form">
          <div className="login-field">
            <label htmlFor="login-account" className="login-label">
              登录账号
            </label>
            <input
              id="login-account"
              className="login-input"
              type="text"
              placeholder="请输入您的登录账号"
              value={account}
              onChange={e => {
                setAccount(e.target.value)
                setError(null)
              }}
              autoFocus
            />
          </div>

          <div className="login-field">
            <label htmlFor="login-char-name" className="login-label">
              修士道号
            </label>
            <input
              id="login-char-name"
              className="login-input"
              type="text"
              placeholder="请输入宗门道号 (默认同账号)"
              value={characterName}
              onChange={e => {
                setCharacterName(e.target.value)
                setError(null)
              }}
            />
          </div>

          {showTokenField ? (
            <div className="login-field">
              <label htmlFor="login-token" className="login-label">
                访问令牌 (私服专用)
              </label>
              <input
                id="login-token"
                className="login-input"
                type="password"
                placeholder="若服务器开启鉴权请输入"
                value={token}
                onChange={e => setToken(e.target.value)}
              />
            </div>
          ) : (
            <button
              type="button"
              className="login-toggle-token-btn"
              onClick={() => setShowTokenField(true)}
            >
              + 输入服务端访问令牌 (可选)
            </button>
          )}

          {error && <div className="login-error-msg">{error}</div>}

          <button type="submit" className="login-submit-btn">
            踏入仙途
          </button>
        </form>

        <footer className="login-footer">
          <span>数据由服务端权威持久化 · 随时可离线挂机</span>
        </footer>
      </div>
    </div>
  )
}
