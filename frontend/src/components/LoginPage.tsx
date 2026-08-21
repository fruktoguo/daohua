import { useState } from 'react'
import { useAccountStore } from '../network/account'

interface LoginPageProps {
  onLoggedIn?: () => void
}

type AuthMode = 'login' | 'register'

const ACCOUNT_REGEX = /^[\u4e00-\u9fa5a-zA-Z0-9_]+$/
const NAME_REGEX = /^[\u4e00-\u9fa5a-zA-Z0-9_]+$/

const RANDOM_DAO_NAMES = [
  '太玄真君', '凌霄散人', '紫府剑仙', '青云修士', '落霞真人',
  '忘忧居士', '丹阳道者', '清虚道君', '九天玄圣', '南华羽士',
  '灵源真人', '玉鼎道尊', '逍遥剑客', '天枢散人', '玄霄上人',
  '云水行者', '纯阳剑修', '妙法散人', '寒霄道人', '乾元真仙',
]

export function LoginPage({ onLoggedIn }: LoginPageProps): React.JSX.Element {
  const [mode, setMode] = useState<AuthMode>('login')
  const savedAccounts = useAccountStore(state => state.savedAccounts)
  const loginWithAuth = useAccountStore(state => state.loginWithAuth)
  const registerWithAuth = useAccountStore(state => state.registerWithAuth)
  const removeAccountAction = useAccountStore(state => state.removeSavedAccount)

  // Login form state
  const [loginAccount, setLoginAccount] = useState('')
  const [loginPassword, setLoginPassword] = useState('')

  // Register form state
  const [regAccount, setRegAccount] = useState('')
  const [regCharName, setRegCharName] = useState('')
  const [regPassword, setRegPassword] = useState('')
  const [regConfirmPassword, setRegConfirmPassword] = useState('')

  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleSelectSaved = (account: string) => {
    setLoginAccount(account)
    setError(null)
    const pwdInput = document.getElementById('login-pwd-input')
    pwdInput?.focus()
  }

  const handleRemoveSaved = (e: React.MouseEvent, account: string) => {
    e.stopPropagation()
    removeAccountAction(account)
  }

  const handleRandomName = () => {
    const picked = RANDOM_DAO_NAMES[Math.floor(Math.random() * RANDOM_DAO_NAMES.length)]
    const suffix = Math.floor(Math.random() * 90 + 10)
    setRegCharName(`${picked}${suffix}`)
    setError(null)
  }

  const handleLoginSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const cleanAcc = loginAccount.trim()
    if (!cleanAcc) {
      setError('请输入登录账号')
      return
    }
    if (!loginPassword) {
      setError('请输入登录密码')
      return
    }

    setLoading(true)
    setError(null)
    try {
      const res = await loginWithAuth(cleanAcc, loginPassword)
      if (!res.success) {
        setError(res.message || '登录失败，请检查账号和密码')
      } else {
        onLoggedIn?.()
      }
    } catch {
      setError('连接服务器失败，请检查网络连接')
    } finally {
      setLoading(false)
    }
  }

  const handleRegisterSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const cleanAcc = regAccount.trim()
    const cleanName = (regCharName || cleanAcc).trim()

    if (!cleanAcc) {
      setError('请输入登录账号')
      return
    }
    if (cleanAcc.length < 2 || cleanAcc.length > 20) {
      setError('账号长度须在 2 至 20 个字符之间')
      return
    }
    if (!ACCOUNT_REGEX.test(cleanAcc)) {
      setError('账号仅支持英文、数字、中文与下划线')
      return
    }
    if (cleanName.length < 2 || cleanName.length > 12) {
      setError('修士道号长度须在 2 至 12 个字符之间')
      return
    }
    if (!NAME_REGEX.test(cleanName)) {
      setError('修士道号仅支持中文、字母、数字与下划线')
      return
    }
    if (!regPassword || regPassword.length < 6 || regPassword.length > 32) {
      setError('密码长度须在 6 至 32 个字符之间')
      return
    }
    if (regPassword !== regConfirmPassword) {
      setError('两次输入的密码不一致，请核对后重新输入')
      return
    }

    setLoading(true)
    setError(null)
    try {
      const res = await registerWithAuth(cleanAcc, regPassword, cleanName)
      if (!res.success) {
        setError(res.message || '注册失败，请更换账号或道号')
      } else {
        onLoggedIn?.()
      }
    } catch {
      setError('连接服务器失败，请检查网络连接')
    } finally {
      setLoading(false)
    }
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

        <div className="auth-tab-bar" role="tablist" aria-label="登录注册切换">
          <button
            type="button"
            role="tab"
            aria-selected={mode === 'login'}
            className={`auth-tab-btn${mode === 'login' ? ' active' : ''}`}
            onClick={() => {
              setMode('login')
              setError(null)
            }}
          >
            登入仙途
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={mode === 'register'}
            className={`auth-tab-btn${mode === 'register' ? ' active' : ''}`}
            onClick={() => {
              setMode('register')
              setError(null)
            }}
          >
            注册道身
          </button>
        </div>

        {mode === 'login' && (
          <div className="auth-content">
            {savedAccounts.length > 0 && (
              <div className="saved-accounts-section">
                <div className="saved-accounts-label">选择已存道身：</div>
                <div className="saved-accounts-list">
                  {savedAccounts.map(item => (
                    <div
                      key={item.account}
                      className={`saved-account-chip${loginAccount === item.account ? ' selected' : ''}`}
                      onClick={() => handleSelectSaved(item.account)}
                      role="button"
                      tabIndex={0}
                    >
                      <div className="saved-account-info">
                        <strong className="saved-account-name">{item.name}</strong>
                        <span className="saved-account-id">账号: {item.account}</span>
                      </div>
                      <button
                        type="button"
                        className="saved-account-remove"
                        onClick={e => handleRemoveSaved(e, item.account)}
                        title="从本机移除此道身记忆"
                      >
                        ×
                      </button>
                    </div>
                  ))}
                </div>
                <div className="auth-divider"><span>输入账号密码登入</span></div>
              </div>
            )}

            <form onSubmit={handleLoginSubmit} className="login-form">
              <div className="login-field">
                <label htmlFor="login-acc-input" className="login-label">
                  登录账号
                </label>
                <input
                  id="login-acc-input"
                  className="login-input"
                  type="text"
                  placeholder="请输入您的登录账号"
                  value={loginAccount}
                  onChange={e => {
                    setLoginAccount(e.target.value)
                    setError(null)
                  }}
                  autoFocus
                />
              </div>

              <div className="login-field">
                <label htmlFor="login-pwd-input" className="login-label">
                  登录密码
                </label>
                <input
                  id="login-pwd-input"
                  className="login-input"
                  type="password"
                  placeholder="请输入您的密码"
                  value={loginPassword}
                  onChange={e => {
                    setLoginPassword(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              {error && <div className="login-error-msg">{error}</div>}

              <button type="submit" className="login-submit-btn" disabled={loading}>
                {loading ? '正在开辟神识...' : '踏入仙途'}
              </button>
            </form>
          </div>
        )}

        {mode === 'register' && (
          <div className="auth-content">
            <form onSubmit={handleRegisterSubmit} className="login-form">
              <div className="login-field">
                <label htmlFor="reg-acc-input" className="login-label">
                  登录账号
                </label>
                <input
                  id="reg-acc-input"
                  className="login-input"
                  type="text"
                  placeholder="2~20位字母、数字、中文或下划线"
                  value={regAccount}
                  onChange={e => {
                    setRegAccount(e.target.value)
                    setError(null)
                  }}
                  autoFocus
                />
              </div>

              <div className="login-field">
                <div className="login-label-row">
                  <label htmlFor="reg-char-input" className="login-label">
                    修士道号 (游戏中展示)
                  </label>
                  <button
                    type="button"
                    className="random-name-btn"
                    onClick={handleRandomName}
                    title="随机生成修仙道号"
                  >
                    🎲 随机道号
                  </button>
                </div>
                <input
                  id="reg-char-input"
                  className="login-input"
                  type="text"
                  maxLength={12}
                  placeholder="2~12位，默认同账号名"
                  value={regCharName}
                  onChange={e => {
                    setRegCharName(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              <div className="login-field">
                <label htmlFor="reg-pwd-input" className="login-label">
                  设置密码
                </label>
                <input
                  id="reg-pwd-input"
                  className="login-input"
                  type="password"
                  placeholder="6~32位字符"
                  value={regPassword}
                  onChange={e => {
                    setRegPassword(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              <div className="login-field">
                <label htmlFor="reg-confirm-pwd-input" className="login-label">
                  确认密码
                </label>
                <input
                  id="reg-confirm-pwd-input"
                  className="login-input"
                  type="password"
                  placeholder="请再次输入相同密码"
                  value={regConfirmPassword}
                  onChange={e => {
                    setRegConfirmPassword(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              {error && <div className="login-error-msg">{error}</div>}

              <button type="submit" className="login-submit-btn" disabled={loading}>
                {loading ? '正在凝聚元神...' : '凝聚元神 · 踏入仙途'}
              </button>
            </form>
          </div>
        )}

        <footer className="login-footer">
          <span>密码经哈希加密保障 · 服务端权威持久化</span>
        </footer>
      </div>
    </div>
  )
}
