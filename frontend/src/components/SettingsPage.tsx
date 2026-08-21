import { useEffect, useState } from 'react'
import { ConfirmDialog } from './ConfirmDialog'
import { RenameModal } from './RenameModal'
import { UnitNumber } from './UnitNumber'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'
import { getAccessToken, setAccessToken } from '../network/access_token'
import { getUiScale, setUiScale } from '../utils/uiScale'
import { useI18nStore } from '../i18n'

type SettingsTab = 'account' | 'display'

function formatDateTime(unixSeconds: number): string {
  if (!unixSeconds || unixSeconds <= 0) return '未知'
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

function formatCooldown(seconds: number): string {
  if (seconds <= 0) return ''
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  if (h > 0) {
    return `${h}小时${m}分`
  }
  if (m > 0) {
    return `${m}分${s}秒`
  }
  return `${s}秒`
}

export function SettingsPage(): React.JSX.Element {
  const [activeTab, setActiveTab] = useState<SettingsTab>('account')
  const [renameModalOpen, setRenameModalOpen] = useState(false)
  const [logoutConfirmOpen, setLogoutConfirmOpen] = useState(false)
  const [tokenDraft, setTokenDraft] = useState(getAccessToken())
  const [zoom, setZoom] = useState(getUiScale())

  const name = useGameStore(state => state.name)
  const account = useGameStore(state => state.account)
  const sequenceId = useGameStore(state => state.sequenceId)
  const playerId = useGameStore(state => state.playerId)
  const createdAtUnixSeconds = useGameStore(state => state.createdAtUnixSeconds)
  const renameCooldownSeconds = useGameStore(state => state.renameCooldownSeconds)
  const version = useGameStore(state => state.version)
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const presence = useGameStore(state => state.presenceMode)
  const connection = useGameStore(state => state.connection)

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

  const modes = [
    ['online', '在线'],
    ['idle-offline', '离线挂机'],
    ['fully-offline', '完全离线'],
  ] as const

  const zoomPresets = [50, 75, 100, 125, 150, 200]

  return (
    <section className="panel settings-page">
      <header className="panel-header settings-header">
        <h2>设置</h2>
        <nav className="settings-sub-tabs" role="tablist" aria-label="设置分类">
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'account'}
            className={`settings-sub-tab${activeTab === 'account' ? ' active' : ''}`}
            onClick={() => setActiveTab('account')}
          >
            账号
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'display'}
            className={`settings-sub-tab${activeTab === 'display' ? ' active' : ''}`}
            onClick={() => setActiveTab('display')}
          >
            显示
          </button>
        </nav>
      </header>

      {activeTab === 'account' && (
        <div className="settings-tab-content">
          <div className="settings-block">
            <h3>修士身份</h3>
            <div className="stat-row">
              <span>道号</span>
              <div className="stat-value-with-action">
                <strong className="player-highlight-name">{name}</strong>
                {remainingCooldown > 0 ? (
                  <span className="cooldown-tag" title={`改名冷却中，剩余 ${formatCooldown(remainingCooldown)}`}>
                    冷却中 ({formatCooldown(remainingCooldown)})
                  </span>
                ) : (
                  <button
                    type="button"
                    className="inline-modify-btn"
                    onClick={() => setRenameModalOpen(true)}
                  >
                    修改道号
                  </button>
                )}
              </div>
            </div>
            <div className="stat-row">
              <span>登录账号</span>
              <span>{account || name}</span>
            </div>
            <div className="stat-row">
              <span>序号 ID</span>
              <span className="sequence-id-tag">
                {sequenceId > 0 ? `#${sequenceId}` : playerId.slice(0, 8)}
              </span>
            </div>
            <div className="stat-row">
              <span>注册时间</span>
              <span>{formatDateTime(createdAtUnixSeconds)}</span>
            </div>
            <div className="stat-row">
              <span>状态版本</span>
              <span><UnitNumber value={version} /></span>
            </div>
            <div className="stat-row">
              <span>服务器时间</span>
              <span>{new Date(serverTime * 1000).toLocaleString('zh-CN')}</span>
            </div>
            <div className="stat-row">
              <span>连接状态</span>
              <span>{connection === 'online' ? '正常连通' : connection}</span>
            </div>
          </div>

          <div className="settings-block">
            <h3>挂机运行状态</h3>
            <div className="presence-options" role="group" aria-label="运行状态">
              {modes.map(([mode, label]) => (
                <button
                  aria-pressed={presence === mode}
                  className={presence === mode ? 'active' : ''}
                  key={mode}
                  onClick={() => gameClient.sendCommand('set_presence', { mode })}
                  type="button"
                >{label}</button>
              ))}
            </div>
          </div>

          <div className="settings-block">
            <h3>访问令牌 (可选)</h3>
            <div className="stat-row">
              <span>令牌</span>
              <input
                aria-label="访问令牌"
                onChange={event => setTokenDraft(event.target.value)}
                placeholder="留空表示不使用"
                style={{
                  border: '1px solid var(--gold-faint)',
                  background: 'rgba(255, 255, 255, 0.04)',
                  color: 'var(--text)',
                  fontSize: 11,
                  height: 28,
                  padding: '3px 9px',
                  width: 'min(220px, 100%)',
                }}
                type="password"
                value={tokenDraft}
              />
            </div>
            <button
              onClick={() => {
                setAccessToken(tokenDraft)
                gameClient.notifyAccessTokenChanged()
              }}
              type="button"
            >保存令牌</button>
          </div>

          <div className="settings-block settings-logout-block">
            <div className="settings-logout-row">
              <span className="settings-logout-hint">若要切换其他修士账号或重新登录：</span>
              <button
                type="button"
                className="danger logout-action-btn"
                onClick={() => setLogoutConfirmOpen(true)}
              >
                退出登录
              </button>
            </div>
          </div>
        </div>
      )}

      {activeTab === 'display' && (
        <div className="settings-tab-content">
          <div className="settings-block">
            <h3>整体界面缩放</h3>
            <p className="settings-desc">
              根据您的屏幕分辨率与视觉习惯调整全局界面大小（支持 50% 至 200%）。
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
                  aria-label="界面缩放"
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
                  重置 100%
                </button>
              </div>
            </div>
          </div>

          <div className="settings-block">
            <h3>界面语言</h3>
            <div className="presence-options" role="group" aria-label="界面语言">
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
        title="确认登出"
        description="确定要退出当前修士账号并返回登录界面吗？"
        confirmLabel="登出账号"
        danger
        onConfirm={() => {
          setLogoutConfirmOpen(false)
          gameClient.logout()
        }}
        onClose={() => setLogoutConfirmOpen(false)}
      />
    </section>
  )
}
