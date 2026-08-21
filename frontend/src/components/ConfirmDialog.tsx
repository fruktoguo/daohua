import { useId } from 'react'
import { useT } from '../i18n'
import { Modal } from './Modal'

interface ConfirmDialogProps {
  open: boolean
  title: string
  description: string
  confirmLabel?: string
  cancelLabel?: string
  danger?: boolean
  onConfirm: () => void
  onClose: () => void
}

export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel,
  cancelLabel,
  danger = false,
  onConfirm,
  onClose,
}: ConfirmDialogProps): React.JSX.Element | null {
  const t = useT()
  // 默认按钮文案取 common.confirm/common.cancel；默认参数无法调用 hook，改在组件体内解析。
  const confirmText = confirmLabel ?? t('common.confirm')
  const cancelText = cancelLabel ?? t('common.cancel')
  const titleId = useId()
  return (
    <Modal labelledBy={titleId} onClose={onClose} open={open} size="small">
      <div className="confirm-dialog">
        <h2 id={titleId}>{title}</h2>
        <p>{description}</p>
        <div className="confirm-dialog-actions">
          <button onClick={onClose} type="button">{cancelText}</button>
          <button
            className={danger ? 'danger confirm-primary' : 'confirm-primary'}
            onClick={() => {
              onConfirm()
              onClose()
            }}
            type="button"
          >{confirmText}</button>
        </div>
      </div>
    </Modal>
  )
}
