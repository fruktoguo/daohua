import { useState } from 'react'
import { Modal } from './Modal'
import { gameClient } from '../network/gameClient'
import { useGameStore } from '../game/gameStore'

interface RenameModalProps {
  open: boolean
  onClose: () => void
}

const NAME_REGEX = /^[\u4e00-\u9fa5a-zA-Z0-9_]+$/

export function RenameModal({ open, onClose }: RenameModalProps): React.JSX.Element | null {
  const currentName = useGameStore(state => state.name)
  const [draft, setDraft] = useState(currentName)
  const [validationError, setValidationError] = useState<string | null>(null)

  const validate = (value: string): string | null => {
    const trimmed = value.trim()
    if (!trimmed) return '请输入道号'
    if (trimmed.length < 2 || trimmed.length > 12) return '道号长度须在 2 至 12 个字符之间'
    if (!NAME_REGEX.test(trimmed)) return '道号仅支持中文、字母、数字和下划线，不支持特殊字符'
    if (trimmed === currentName) return '新道号不能与当前道号相同'
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
    const sent = gameClient.sendCommand('rename', { name: trimmed })
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
        <h2 id="rename-modal-title">修改修士道号</h2>
      </header>
      <form onSubmit={handleSubmit} className="rename-form">
        <div className="rename-field">
          <label htmlFor="rename-input" className="rename-label">
            新道号
          </label>
          <input
            id="rename-input"
            className="rename-input"
            maxLength={12}
            onChange={handleChange}
            placeholder="2~12字符，支持中文、英文、数字与下划线"
            type="text"
            value={draft}
            autoFocus
          />
          {validationError && <span className="rename-error">{validationError}</span>}
          <p className="rename-hint">道号在全服不可重复，修改后将进入 24 小时改名冷却。</p>
        </div>
        <div className="rename-actions">
          <button type="button" className="rename-cancel" onClick={onClose}>
            取消
          </button>
          <button
            type="submit"
            className="rename-submit active"
            disabled={Boolean(validationError) || !draft.trim() || draft.trim() === currentName}
          >
            确认修改
          </button>
        </div>
      </form>
    </Modal>
  )
}
