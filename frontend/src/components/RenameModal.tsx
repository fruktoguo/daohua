import { memo, useState } from 'react'
import { Modal } from './Modal'
import { gameClient } from '../network/gameClient'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'

/** 修改道号模态窗 Props */
interface RenameModalProps {
  /** 弹窗是否展开 */
  open: boolean
  /** 关闭弹窗回调 */
  onClose: () => void
}

/** 道号字符格式校验规则（2-12字符，支持汉字、英文字母、数字与下划线） */
const NAME_REGEX = /^[\u4e00-\u9fa5a-zA-Z0-9_]+$/

/**
 * 修改修士道号模态窗组件 (RenameModal)
 *
 * 具备实时格式与长度校验，向服务端发送 player.rename 指令并在成功时关闭弹窗。
 */
export const RenameModal = memo(function RenameModal({ open, onClose }: RenameModalProps): React.JSX.Element | null {
  const t = useT()
  const currentName = useGameStore(state => state.name)
  const [draft, setDraft] = useState(currentName)
  const [validationError, setValidationError] = useState<string | null>(null)

  const validate = (value: string): string | null => {
    const trimmed = value.trim()
    if (!trimmed) return t('app.rename.errorEmpty')
    if (trimmed.length < 1 || trimmed.length > 64) return t('app.rename.errorLength')
    if (!NAME_REGEX.test(trimmed)) return t('app.rename.errorFormat')
    if (trimmed === currentName) return t('app.rename.errorSame')
    return null
  }

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const value = e.target.value
    setDraft(value)
    setValidationError(validate(value))
  }

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    const error = validate(draft)
    if (error) {
      setValidationError(error)
      return
    }
    const trimmed = draft.trim()
    const sent = gameClient.sendCommand('player.rename', { displayName: trimmed })
    if (sent) {
      onClose()
    }
  }

  return (
    <Modal
      className="rename-modal"
      labelledBy="rename-modal-title"
      onClose={onClose}
      open={open}
      size="small"
    >
      <header className="panel-header rename-modal-header">
        <h2 id="rename-modal-title">{t('app.rename.title')}</h2>
      </header>
      <form onSubmit={handleSubmit} className="rename-form">
        <div className="rename-field">
          <label htmlFor="rename-input" className="rename-label">
            {t('app.rename.inputLabel')}
          </label>
          <input
            id="rename-input"
            className="rename-input"
            maxLength={12}
            onChange={handleChange}
            placeholder={t('app.rename.inputPlaceholder')}
            type="text"
            value={draft}
            autoFocus
          />
          {validationError && <span className="rename-error">{validationError}</span>}
          <p className="rename-hint">{t('app.rename.hint')}</p>
        </div>
        <div className="rename-actions">
          <button type="button" className="rename-cancel" onClick={onClose}>
            {t('app.rename.cancelBtn')}
          </button>
          <button
            type="submit"
            className="rename-submit active"
            disabled={Boolean(validationError) || !draft.trim() || draft.trim() === currentName}
          >
            {t('app.rename.submitBtn')}
          </button>
        </div>
      </form>
    </Modal>
  )
})
