import { memo, useState } from 'react'
import { useAccountStore } from '../network/account'
import { useT } from '../i18n'

/** 登录注册页 Props */
interface LoginPageProps {
  /** 登录/注册成功后的回调 */
  onLoggedIn?: () => void
}

/** 认证模式：登录或注册 */
type AuthMode = 'login' | 'register'

/** 账号字符格式校验规则（服务端登录键只允许 ASCII 字母、数字与下划线） */
const ACCOUNT_REGEX = /^[a-zA-Z0-9_]+$/
/** 道号字符格式校验规则 */
const NAME_REGEX = /^[\u4e00-\u9fa5a-zA-Z0-9_]+$/

/** 随机修仙道号词库 */
const RANDOM_DAO_NAMES = [
  '太玄真君', '凌霄散人', '紫府剑仙', '青云修士', '落霞真人',
  '忘忧居士', '丹阳道者', '清虚道君', '九天玄圣', '南华羽士',
  '灵源真人', '玉鼎道尊', '逍遥剑客', '天枢散人', '玄霄上人',
  '云水行者', '纯阳剑修', '妙法散人', '寒霄道人', '乾元真仙',
]

/**
 * 账号登录与注册认证主页面组件 (LoginPage)
 *
 * 功能与交互特性：
 * 1. 登录模式：展示已记住的本机本地账号列表（支持点击快捷选择与删除），并提供账号密码表单提交；
 * 2. 注册模式：支持账号、修仙道号（支持一键随机起名）、密码及确认密码校验；
 * 3. 严格的前端格式校验与错误提示，并连接底层 account 鉴权仓库 (useAccountStore)。
 */
export const LoginPage = memo(function LoginPage({ onLoggedIn }: LoginPageProps): React.JSX.Element {
  const t = useT()
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
      setError(t('app.login.errorEmptyAccount'))
      return
    }
    if (!loginPassword) {
      setError(t('app.login.errorEmptyPassword'))
      return
    }

    setLoading(true)
    setError(null)
    try {
      const res = await loginWithAuth(cleanAcc, loginPassword)
      if (!res.success) {
        setError(res.message || t('app.login.errorLoginFailed'))
      } else {
        onLoggedIn?.()
      }
    } catch {
      setError(t('app.login.errorNetworkFailed'))
    } finally {
      setLoading(false)
    }
  }

  const handleRegisterSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const cleanAcc = regAccount.trim()
    const cleanName = (regCharName || cleanAcc).trim()

    if (!cleanAcc) {
      setError(t('app.login.errorEmptyAccount'))
      return
    }
    if (cleanAcc.length < 3 || cleanAcc.length > 32) {
      setError(t('app.login.errorAccountLength'))
      return
    }
    if (!ACCOUNT_REGEX.test(cleanAcc)) {
      setError(t('app.login.errorAccountFormat'))
      return
    }
    if (cleanName.length < 1 || cleanName.length > 64) {
      setError(t('app.login.errorNameLength'))
      return
    }
    if (!NAME_REGEX.test(cleanName)) {
      setError(t('app.login.errorNameFormat'))
      return
    }
    if (!regPassword || regPassword.length < 12 || regPassword.length > 256) {
      setError(t('app.login.errorPasswordLength'))
      return
    }
    if (regPassword !== regConfirmPassword) {
      setError(t('app.login.errorPasswordMismatch'))
      return
    }

    setLoading(true)
    setError(null)
    try {
      const res = await registerWithAuth(cleanAcc, regPassword, cleanName)
      if (!res.success) {
        setError(res.message || t('app.login.errorRegisterFailed'))
      } else {
        onLoggedIn?.()
      }
    } catch {
      setError(t('app.login.errorNetworkFailed'))
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
        <h1 className="login-title">{t('app.login.gameTitle')}</h1>
        <p className="login-subtitle">{t('app.login.subtitle')}</p>

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
            {t('app.login.tabLogin')}
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
            {t('app.login.tabRegister')}
          </button>
        </div>

        {mode === 'login' && (
          <div className="auth-content">
            {savedAccounts.length > 0 && (
              <div className="saved-accounts-section">
                <div className="saved-accounts-label">{t('app.login.savedAccountsLabel')}</div>
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
                        <span className="saved-account-id">{t('app.login.accountPrefix', { account: item.account })}</span>
                      </div>
                      <button
                        type="button"
                        className="saved-account-remove"
                        onClick={e => handleRemoveSaved(e, item.account)}
                        title={t('app.login.removeSavedTitle')}
                      >
                        ×
                      </button>
                    </div>
                  ))}
                </div>
                <div className="auth-divider"><span>{t('app.login.manualInputDivider')}</span></div>
              </div>
            )}

            <form onSubmit={handleLoginSubmit} className="login-form">
              <div className="login-field">
                <label htmlFor="login-acc-input" className="login-label">
                  {t('app.login.loginAccountLabel')}
                </label>
                <input
                  id="login-acc-input"
                  className="login-input"
                  type="text"
                  placeholder={t('app.login.loginAccountPlaceholder')}
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
                  {t('app.login.loginPasswordLabel')}
                </label>
                <input
                  id="login-pwd-input"
                  className="login-input"
                  type="password"
                  placeholder={t('app.login.loginPasswordPlaceholder')}
                  value={loginPassword}
                  onChange={e => {
                    setLoginPassword(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              {error && <div className="login-error-msg">{error}</div>}

              <button type="submit" className="login-submit-btn" disabled={loading}>
                {loading ? t('app.login.loginLoading') : t('app.login.loginBtn')}
              </button>
            </form>
          </div>
        )}

        {mode === 'register' && (
          <div className="auth-content">
            <form onSubmit={handleRegisterSubmit} className="login-form">
              <div className="login-field">
                <label htmlFor="reg-acc-input" className="login-label">
                  {t('app.login.regAccountLabel')}
                </label>
                <input
                  id="reg-acc-input"
                  className="login-input"
                  type="text"
                  placeholder={t('app.login.regAccountPlaceholder')}
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
                    {t('app.login.regCharNameLabel')}
                  </label>
                  <button
                    type="button"
                    className="random-name-btn"
                    onClick={handleRandomName}
                    title="随机生成修仙道号"
                  >
                    {t('app.login.randomNameBtn')}
                  </button>
                </div>
                <input
                  id="reg-char-input"
                  className="login-input"
                  type="text"
                  maxLength={12}
                  placeholder={t('app.login.regCharNamePlaceholder')}
                  value={regCharName}
                  onChange={e => {
                    setRegCharName(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              <div className="login-field">
                <label htmlFor="reg-pwd-input" className="login-label">
                  {t('app.login.regPasswordLabel')}
                </label>
                <input
                  id="reg-pwd-input"
                  className="login-input"
                  type="password"
                  placeholder={t('app.login.regPasswordPlaceholder')}
                  value={regPassword}
                  onChange={e => {
                    setRegPassword(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              <div className="login-field">
                <label htmlFor="reg-confirm-pwd-input" className="login-label">
                  {t('app.login.regConfirmPasswordLabel')}
                </label>
                <input
                  id="reg-confirm-pwd-input"
                  className="login-input"
                  type="password"
                  placeholder={t('app.login.regConfirmPasswordPlaceholder')}
                  value={regConfirmPassword}
                  onChange={e => {
                    setRegConfirmPassword(e.target.value)
                    setError(null)
                  }}
                />
              </div>

              {error && <div className="login-error-msg">{error}</div>}

              <button type="submit" className="login-submit-btn" disabled={loading}>
                {loading ? t('app.login.registerLoading') : t('app.login.registerBtn')}
              </button>
            </form>
          </div>
        )}

        <footer className="login-footer">
          <span>{t('app.login.securityFooter')}</span>
        </footer>
      </div>
    </div>
  )
})
