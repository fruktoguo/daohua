import { memo, useEffect, useState } from 'react'
import { ConfirmDialog } from './ConfirmDialog'
import { RenameModal } from './RenameModal'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'
import { getUiScale, setUiScale } from '../utils/uiScale'
import { useI18nStore, useT } from '../i18n'

/** 设置页面 Tab 分类（账号信息与显示偏好） */
type SettingsTab = 'account' | 'display'

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
 * 功能与交互模块：
 * 1. 账号信息 (account Tab)：展示道号、账号、序列 ID、注册时间，支持道号重命名（含冷却倒计时）与退出登录确认；
 * 2. 显示设置 (display Tab)：提供界面缩放比例 (UI Scale: 50%~200% 滑块与预设档位) 与多语言切换 (zh-CN / en-US)。
 */
export const SettingsPage = memo(function SettingsPage(): React.JSX.Element {
  const t = useT()
  const [activeTab, setActiveTab] = useState<SettingsTab>('account')
  const [renameModalOpen, setRenameModalOpen] = useState(false)
  const [logoutConfirmOpen, setLogoutConfirmOpen] = useState(false)
  const [zoom, setZoom] = useState(getUiScale())

  const name = useGameStore(state => state.name)
  const account = useGameStore(state => state.account)
  const sequenceId = useGameStore(state => state.sequenceId)
  const createdAtUnixSeconds = useGameStore(state => state.createdAtUnixSeconds)
  const renameCooldownSeconds = useGameStore(state => state.renameCooldownSeconds)

  const locale = useI18nStore(state => state.locale)
  const setLocale = useI18nStore(state => state.setLocale)

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

  const handleZoomChange = (newZoom: number) => {
    const clamped = setUiScale(newZoom)
    setZoom(clamped)
  }

  const zoomPresets = [50, 75, 100, 125, 150, 200]
  const cooldownStr = formatCooldown(remainingCooldown, t)

  return (
    <section className="panel settings-page">
      <header className="panel-header settings-header">
        <h2>{t('app.settings.title')}</h2>
        <nav className="settings-sub-tabs" role="tablist" aria-label={t('app.settings.categoryAria')}>
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'account'}
            className={`settings-sub-tab${activeTab === 'account' ? ' active' : ''}`}
            onClick={() => setActiveTab('account')}
          >
            {t('app.settings.tabAccount')}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'display'}
            className={`settings-sub-tab${activeTab === 'display' ? ' active' : ''}`}
            onClick={() => setActiveTab('display')}
          >
            {t('app.settings.tabDisplay')}
          </button>
        </nav>
      </header>

      {activeTab === 'account' && (
        <div className="settings-tab-content">
          <div className="settings-block">
            <h3>{t('app.settings.accountHeading')}</h3>
            <div className="stat-row">
              <span>{t('app.settings.nameLabel')}</span>
              <div className="stat-value-with-action">
                <strong className="player-highlight-name">{name}</strong>
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
            </div>
            <div className="stat-row">
              <span>{t('app.settings.accountLabel')}</span>
              <span>{account || name}</span>
            </div>
            <div className="stat-row">
              <span>{t('app.settings.sequenceIdLabel')}</span>
              <span className="sequence-id-tag">
                #{sequenceId > 0 ? sequenceId : 1}
              </span>
            </div>
            <div className="stat-row">
              <span>{t('app.settings.registeredTimeLabel')}</span>
              <span>{formatDateTime(createdAtUnixSeconds, t('app.settings.unknownTime'))}</span>
            </div>
          </div>

          <div className="settings-block settings-logout-block">
            <div className="settings-logout-row">
              <span className="settings-logout-hint">{t('app.settings.logoutHint')}</span>
              <button
                type="button"
                className="danger logout-action-btn"
                onClick={() => setLogoutConfirmOpen(true)}
              >
                {t('app.settings.logoutBtn')}
              </button>
            </div>
          </div>
        </div>
      )}

      {activeTab === 'display' && (
        <div className="settings-tab-content">
          <div className="settings-block">
            <h3>{t('app.settings.zoomHeading')}</h3>
            <p className="settings-desc">
              {t('app.settings.zoomDesc')}
            </p>
            <div className="zoom-control-group">
              <div className="zoom-slider-row">
                <span className="zoom-min">50%</span>
                <input
                  type="range"
                  min={50}
                  max={200}
                  step={5}
                  value={zoom}
                  onChange={e => handleZoomChange(Number(e.target.value))}
                  className="zoom-slider"
                  aria-label={t('app.settings.zoomAria')}
                />
                <span className="zoom-max">200%</span>
                <strong className="zoom-current-badge">{zoom}%</strong>
              </div>
              <div className="zoom-presets">
                {zoomPresets.map(preset => (
                  <button
                    key={preset}
                    type="button"
                    className={`zoom-preset-btn${zoom === preset ? ' active' : ''}`}
                    onClick={() => handleZoomChange(preset)}
                  >
                    {preset}%
                  </button>
                ))}
                <button
                  type="button"
                  className="zoom-reset-btn"
                  onClick={() => handleZoomChange(100)}
                >
                  {t('app.settings.zoomReset')}
                </button>
              </div>
            </div>
          </div>

          <div className="settings-block">
            <h3>{t('app.settings.languageHeading')}</h3>
            <div className="presence-options" role="group" aria-label={t('app.settings.languageAria')}>
              {[
                { code: 'zh-CN' as const, label: '简体中文' },
                { code: 'en-US' as const, label: 'English' },
              ].map(loc => (
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
          </div>
        </div>
      )}

      <RenameModal
        open={renameModalOpen}
        onClose={() => setRenameModalOpen(false)}
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
    </section>
  )
})
