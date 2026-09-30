import { memo, useEffect, useState } from 'react'
import { Modal } from './Modal'
import { requestChangePassword } from '../network/account'
import { useT } from '../i18n'

/** 修改密码模态窗 Props */
interface ChangePasswordModalProps {
  /** 弹窗是否展开 */
  open: boolean
  /** 关闭弹窗回调 */
  onClose: () => void
}

/** 密码长度约束，与服务端 PostgresAccountRepository 口径一致 */
const PASSWORD_MIN = 6
const PASSWORD_MAX = 24

/**
 * 修改登录密码模态窗组件 (ChangePasswordModal)
 *
 * 依次收集当前密码 / 新密码 / 确认新密码，本地先做长度与一致性校验，
 * 再调用 /api/v1/auth/change-password；服务端校验失败时回显其错误文案。
 */
export const ChangePasswordModal = memo(function ChangePasswordModal({ open, onClose }: ChangePasswordModalProps): React.JSX.Element | null {
  const t = useT()
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [succeeded, setSucceeded] = useState(false)

  // 每次打开时重置表单，避免残留上次输入的密码明文与旧错误态。
  useEffect(() => {
    if (!open) return
    setCurrentPassword('')
    setNewPassword('')
    setConfirmPassword('')
    setError(null)
    setSubmitting(false)
    setSucceeded(false)
  }, [open])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (newPassword.length < PASSWORD_MIN || newPassword.length > PASSWORD_MAX) {
      setError(t('app.login.errorPasswordLength'))
      return
    }
    if (newPassword !== confirmPassword) {
      setError(t('app.password.errorMismatch'))
      return
    }
    if (newPassword === currentPassword) {
      setError(t('app.password.errorSame'))
      return
    }
    setSubmitting(true)
    setError(null)
    const result = await requestChangePassword(currentPassword, newPassword)
    setSubmitting(false)
    if (!result.success) {
      setError(result.message || t('app.password.errorFailed'))
      return
    }
    setSucceeded(true)
  }

  return (
    <Modal
      className="modal-form-shell"
      labelledBy="change-password-title"
      onClose={onClose}
      open={open}
      size="small"
    >
      <header className="panel-header modal-form-header">
        <h2 id="change-password-title">{t('app.password.title')}</h2>
      </header>
      {succeeded ? (
        <div className="modal-form">
          <p className="modal-form-hint">{t('app.password.success')}</p>
          <div className="modal-form-actions">
            <button type="button" className="modal-form-submit active" onClick={onClose}>
              {t('common.confirm')}
            </button>
          </div>
        </div>
      ) : (
        <form onSubmit={handleSubmit} className="modal-form">
          <div className="modal-form-field">
            <label htmlFor="change-password-current" className="modal-form-label">
              {t('app.password.currentLabel')}
            </label>
            <input
              id="change-password-current"
              className="modal-form-input"
              type="password"
              autoComplete="current-password"
              maxLength={PASSWORD_MAX}
              value={currentPassword}
              onChange={e => setCurrentPassword(e.target.value)}
              autoFocus
            />
          </div>
          <div className="modal-form-field">
            <label htmlFor="change-password-new" className="modal-form-label">
              {t('app.password.newLabel')}
            </label>
            <input
              id="change-password-new"
              className="modal-form-input"
              type="password"
              autoComplete="new-password"
              maxLength={PASSWORD_MAX}
              placeholder={t('app.password.newPlaceholder')}
              value={newPassword}
              onChange={e => setNewPassword(e.target.value)}
            />
          </div>
          <div className="modal-form-field">
            <label htmlFor="change-password-confirm" className="modal-form-label">
              {t('app.password.confirmLabel')}
            </label>
            <input
              id="change-password-confirm"
              className="modal-form-input"
              type="password"
              autoComplete="new-password"
              maxLength={PASSWORD_MAX}
              value={confirmPassword}
              onChange={e => setConfirmPassword(e.target.value)}
            />
            {error && <span className="modal-form-error" role="alert">{error}</span>}
            <p className="modal-form-hint">{t('app.password.hint')}</p>
          </div>
          <div className="modal-form-actions">
            <button type="button" className="modal-form-cancel" onClick={onClose} disabled={submitting}>
              {t('common.cancel')}
            </button>
            <button
              type="submit"
              className="modal-form-submit active"
              disabled={submitting || !currentPassword || !newPassword || !confirmPassword}
            >
              {submitting ? t('app.password.submitting') : t('app.password.submitBtn')}
            </button>
          </div>
        </form>
      )}
    </Modal>
  )
})
