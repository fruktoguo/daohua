import { memo, useEffect, useState } from 'react'
import { ConfirmDialog } from './ConfirmDialog'
import { RenameModal } from './RenameModal'
import { ChangePasswordModal } from './ChangePasswordModal'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'
import { LOCALES, useI18nStore, useT } from '../i18n'
import { AVATAR_OPTIONS, getPlayerAvatarSrc, useAvatarStore } from '../game/avatarStore'
import { useThemeStore } from '../game/themeStore'
import { TUTORIAL_DONE } from '../tutorial/steps'

/** 设置页面分类（账号、显示偏好、形象与引导） */
type SettingsTab = 'account' | 'display' | 'avatar' | 'tutorial'

/** 设置分类导航配置：顺序即左栏展示顺序 */
const SETTINGS_TABS: ReadonlyArray<{ id: SettingsTab; labelKey: 'tabAccount' | 'tabDisplay' | 'tabAvatar' | 'tabTutorial' }> = [
  { id: 'account', labelKey: 'tabAccount' },
  { id: 'display', labelKey: 'tabDisplay' },
  { id: 'avatar', labelKey: 'tabAvatar' },
  { id: 'tutorial', labelKey: 'tabTutorial' },
]

/** 左栏分类小图标（22px 格内 13px 描边矢量，与全局 Icon 同风格） */
const SETTINGS_NAV_ICONS: Record<SettingsTab, React.JSX.Element> = {
  account: (
    <svg fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.7" viewBox="0 0 24 24">
      <circle cx="12" cy="8" r="3.4" />
      <path d="M5.5 19.5c1.3-3.6 3.9-5.3 6.5-5.3s5.2 1.7 6.5 5.3" />
    </svg>
  ),
  display: (
    <svg fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.7" viewBox="0 0 24 24">
      <rect x="3.5" y="5" width="17" height="11.5" rx="1.5" />
      <path d="M9.5 20h5M12 16.5V20" />
    </svg>
  ),
  avatar: (
    <svg fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.7" viewBox="0 0 24 24">
      <rect x="5" y="3.5" width="14" height="17" rx="1.5" />
      <circle cx="12" cy="9.5" r="2.4" />
      <path d="M7.8 17.2c.9-2.5 2.5-3.7 4.2-3.7s3.3 1.2 4.2 3.7" />
    </svg>
  ),
  tutorial: (
    <svg fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.7" viewBox="0 0 24 24">
      <path d="M4.5 5.5A2 2 0 0 1 6.5 3.5h11a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2z" />
      <path d="M9 8.5h6M9 12h6M9 15.5h3.5" />
    </svg>
  ),
}

/** 格式化绝对时间戳为 YYYY-MM-DD HH:mm:ss 字符串 */
function formatDateTime(unixSeconds: number, fallbackText: string): string {
  if (!unixSeconds || unixSeconds <= 0) return fallbackText
  const date = new Date(unixSeconds * 1000)
  const pad = (n: number) => String(n).padStart(2, '0')
  const y = date.getFullYear()
  const m = pad(date.getMonth() + 1)
  const d = pad(date.getDate())
  const hh = pad(date.getHours())
  const mm = pad(date.getMinutes())
  const ss = pad(date.getSeconds())
  return `${y}-${m}-${d} ${hh}:${mm}:${ss}`
}

/** 格式化改名冷却时间文案（时/分/秒） */
function formatCooldown(seconds: number, t: (key: string, params?: Record<string, string | number>) => string): string {
  if (seconds <= 0) return ''
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  if (h > 0) {
    return t('app.settings.timeHoursMinutes', { h, m })
  }
  if (m > 0) {
    return t('app.settings.timeMinutesSeconds', { m, s })
  }
  return t('app.settings.timeSeconds', { s })
}

/**
 * 游戏设置中心主页面组件 (SettingsPage)
 *
 * 布局结构（经典游戏设置范式）：
 * - 左侧竖排分类栏（账号 / 显示 / 形象，含图标格与金色高亮态），移动端降级为顶部横向分类条；
 * - 右侧为限宽（640px）的设置内容列，由若干 `settings-group` 分组卡片组成；
 * - 分组内统一使用 `setting-row`「标签 + 说明 | 控件」设置行。
 *
 * 功能模块：
 * 1. 账号：修士资料卡（立绘 + 道号 + 序号 ID + 改名入口/冷却）、登录账号（含序号）、注册时间与密码修改；会话分组承载退出登录；
 * 2. 显示：界面主题与语言（分段按钮）；
 * 3. 形象：本命形象选择瓦片（图标 + 名称），供后续扩展更多形象。
 */
export const SettingsPage = memo(function SettingsPage(): React.JSX.Element {
  const t = useT()
  const [activeTab, setActiveTab] = useState<SettingsTab>('account')
  const [renameModalOpen, setRenameModalOpen] = useState(false)
  const [passwordModalOpen, setPasswordModalOpen] = useState(false)
  const [logoutConfirmOpen, setLogoutConfirmOpen] = useState(false)
  const [restartConfirmOpen, setRestartConfirmOpen] = useState(false)

  const tutorialStep = useGameStore(state => state.tutorialStep)

  const name = useGameStore(state => state.name)
  const account = useGameStore(state => state.account)
  const sequenceId = useGameStore(state => state.sequenceId)
  const createdAtUnixSeconds = useGameStore(state => state.createdAtUnixSeconds)
  const renameCooldownSeconds = useGameStore(state => state.renameCooldownSeconds)

  const locale = useI18nStore(state => state.locale)
  const setLocale = useI18nStore(state => state.setLocale)
  const localeError = useI18nStore(state => state.error)

  const theme = useThemeStore(state => state.theme)
  const setTheme = useThemeStore(state => state.setTheme)

  const avatarId = useAvatarStore(state => state.avatarId)
  const setAvatarId = useAvatarStore(state => state.setAvatarId)
  const avatarSrc = getPlayerAvatarSrc(avatarId)

  const [remainingCooldown, setRemainingCooldown] = useState(renameCooldownSeconds)

  useEffect(() => {
    setRemainingCooldown(renameCooldownSeconds)
  }, [renameCooldownSeconds])

  useEffect(() => {
    if (remainingCooldown <= 0) return undefined
    const timer = window.setInterval(() => {
      setRemainingCooldown(prev => Math.max(0, prev - 1))
    }, 1000)
    return () => window.clearInterval(timer)
  }, [remainingCooldown])

  const cooldownStr = formatCooldown(remainingCooldown, t)
  const accountValue = `${account || name}${sequenceId > 0 ? `#${sequenceId}` : ''}`

  return (
    <section className="panel settings-page">
      <header className="panel-header">
        <h2>{t('app.settings.title')}</h2>
      </header>

      <div className="settings-layout">
        <nav className="settings-nav" role="tablist" aria-label={t('app.settings.categoryAria')}>
          {SETTINGS_TABS.map(tab => (
            <button
              key={tab.id}
              type="button"
              role="tab"
              aria-selected={activeTab === tab.id}
              className={`settings-nav-item${activeTab === tab.id ? ' active' : ''}`}
              onClick={() => setActiveTab(tab.id)}
            >
              <span className="settings-nav-glyph" aria-hidden="true">{SETTINGS_NAV_ICONS[tab.id]}</span>
              <span className="settings-nav-label">{t(`app.settings.${tab.labelKey}`)}</span>
            </button>
          ))}
        </nav>

        <div className="settings-content" role="tabpanel">
          {activeTab === 'account' && (
            <>
              <section className="settings-group" aria-label={t('app.settings.accountHeading')}>
                <h3 className="settings-group-title">{t('app.settings.accountHeading')}</h3>
                <div className="settings-profile">
                  <span className="settings-profile-avatar">
                    <img alt="" draggable={false} src={avatarSrc} />
                  </span>
                  <div className="settings-profile-main">
                    <span className="settings-profile-name">
                      {name}
                      <span className="sequence-id-tag">{sequenceId > 0 ? `#${sequenceId}` : '-'}</span>
                    </span>
                    <span className="settings-profile-meta">{accountValue}</span>
                  </div>
                  {remainingCooldown > 0 ? (
                    <span className="cooldown-tag" title={t('app.settings.cooldownTitle', { time: cooldownStr })}>
                      {t('app.settings.cooldownTag', { time: cooldownStr })}
                    </span>
                  ) : (
                    <button
                      type="button"
                      className="inline-modify-btn"
                      onClick={() => setRenameModalOpen(true)}
                    >
                      {t('app.settings.modifyNameBtn')}
                    </button>
                  )}
                </div>
                <div className="setting-row">
                  <span className="setting-label">{t('app.settings.accountLabel')}</span>
                  <span className="setting-value">{accountValue}</span>
                </div>
                <div className="setting-row">
                  <span className="setting-label">{t('app.settings.registeredTimeLabel')}</span>
                  <span className="setting-value">{formatDateTime(createdAtUnixSeconds, t('app.settings.unknownTime'))}</span>
                </div>
              </section>

              <section className="settings-group" aria-label={t('app.settings.securityHeading')}>
                <h3 className="settings-group-title">{t('app.settings.securityHeading')}</h3>
                <div className="setting-row">
                  <span className="setting-label">{t('app.settings.passwordLabel')}</span>
                  <button
                    type="button"
                    className="inline-modify-btn"
                    onClick={() => setPasswordModalOpen(true)}
                  >
                    {t('app.settings.changePasswordBtn')}
                  </button>
                </div>
              </section>

              <section className="settings-group settings-group--danger" aria-label={t('app.settings.sessionHeading')}>
                <h3 className="settings-group-title">{t('app.settings.sessionHeading')}</h3>
                <div className="setting-row">
                  <span className="setting-label">{t('app.settings.logoutBtn')}</span>
                  <button
                    type="button"
                    className="danger logout-action-btn"
                    onClick={() => setLogoutConfirmOpen(true)}
                  >
                    {t('app.settings.logoutBtn')}
                  </button>
                </div>
              </section>
            </>
          )}

          {activeTab === 'display' && (
            <>
              <section className="settings-group" aria-label={t('app.settings.themeHeading')}>
                <h3 className="settings-group-title">{t('app.settings.themeHeading')}</h3>
                <div className="presence-options" role="group" aria-label={t('app.settings.themeHeading')}>
                  {(['light', 'dark'] as const).map(option => (
                    <button
                      key={option}
                      aria-pressed={theme === option}
                      className={theme === option ? 'active' : ''}
                      onClick={event => setTheme(option, { x: event.clientX, y: event.clientY })}
                      type="button"
                    >
                      {t(option === 'light' ? 'app.settings.themeLight' : 'app.settings.themeDark')}
                    </button>
                  ))}
                </div>
              </section>
              <section className="settings-group" aria-label={t('app.settings.languageHeading')}>
                <h3 className="settings-group-title">{t('app.settings.languageHeading')}</h3>
                {localeError && <p role="alert">{localeError}</p>}
                <div className="presence-options" role="group" aria-label={t('app.settings.languageAria')}>
                  {LOCALES.map(code => ({ code, label: code === 'zh-CN' ? t('app.settings.localeBuiltin') : code })).map(loc => (
                    <button
                      key={loc.code}
                      aria-pressed={locale === loc.code}
                      className={locale === loc.code ? 'active' : ''}
                      onClick={() => setLocale(loc.code)}
                      type="button"
                    >
                      {loc.label}
                    </button>
                  ))}
                </div>
              </section>
            </>
          )}

          {activeTab === 'tutorial' && (
            <section className="settings-group" aria-label={t('app.settings.tutorialHeading')}>
              <h3 className="settings-group-title">{t('app.settings.tutorialHeading')}</h3>
              <div className="setting-row">
                <span className="setting-label">{t('app.settings.tutorialHeading')}</span>
                <span className="setting-value">
                  {tutorialStep >= TUTORIAL_DONE
                    ? t('app.settings.tutorialStatusDone')
                    : t('app.settings.tutorialStatusInProgress', { step: tutorialStep + 1 })}
                </span>
              </div>
              <div className="setting-row">
                <span className="setting-label">{t('app.settings.tutorialRestartLabel')}</span>
                <button
                  type="button"
                  className="inline-modify-btn"
                  onClick={() => setRestartConfirmOpen(true)}
                >
                  {t('app.settings.tutorialRestartBtn')}
                </button>
              </div>
              <p className="setting-desc">{t('app.settings.tutorialRestartDesc')}</p>
            </section>
          )}

          {activeTab === 'avatar' && (
            <section className="settings-group" aria-label={t('app.settings.avatarHeading')}>
              <h3 className="settings-group-title">{t('app.settings.avatarHeading')}</h3>
              <div className="avatar-selection-grid" role="radiogroup" aria-label={t('app.settings.avatarHeading')}>
                {AVATAR_OPTIONS.map(opt => {
                  const isSelected = avatarId === opt.id
                  return (
                    <button
                      key={opt.id}
                      type="button"
                      role="radio"
                      aria-checked={isSelected}
                      className={`avatar-tile${isSelected ? ' active' : ''}`}
                      onClick={() => {
                        setAvatarId(opt.id)
                        gameClient.sendCommand('player.set_avatar', { avatar: opt.id === 'male' ? 'player-male' : 'player' })
                      }}
                    >
                      <img className="avatar-tile-icon" alt="" draggable={false} src={opt.src} />
                      <span className="avatar-tile-name">{t(opt.nameKey)}</span>
                    </button>
                  )
                })}
              </div>
            </section>
          )}
        </div>
      </div>

      <RenameModal
        open={renameModalOpen}
        onClose={() => setRenameModalOpen(false)}
      />

      <ChangePasswordModal
        open={passwordModalOpen}
        onClose={() => setPasswordModalOpen(false)}
      />

      <ConfirmDialog
        open={logoutConfirmOpen}
        title={t('app.settings.logoutConfirmTitle')}
        description={t('app.settings.logoutConfirmDesc')}
        confirmLabel={t('app.settings.logoutConfirmBtn')}
        danger
        onConfirm={() => {
          setLogoutConfirmOpen(false)
          gameClient.logout()
        }}
        onClose={() => setLogoutConfirmOpen(false)}
      />

      <ConfirmDialog
        open={restartConfirmOpen}
        title={t('app.settings.tutorialRestartConfirmTitle')}
        description={t('app.settings.tutorialRestartConfirmDesc')}
        confirmLabel={t('app.settings.tutorialRestartConfirmBtn')}
        onConfirm={() => {
          setRestartConfirmOpen(false)
          gameClient.sendCommand('tutorial.restart', {})
        }}
        onClose={() => setRestartConfirmOpen(false)}
      />
    </section>
  )
})
