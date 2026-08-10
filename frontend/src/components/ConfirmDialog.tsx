import { useId } from 'react'
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
  confirmLabel = '确认',
  cancelLabel = '取消',
  danger = false,
  onConfirm,
  onClose,
}: ConfirmDialogProps): React.JSX.Element | null {
  const titleId = useId()
  return (
    <Modal labelledBy={titleId} onClose={onClose} open={open} size="small">
      <div className="confirm-dialog">
        <h2 id={titleId}>{title}</h2>
        <p>{description}</p>
        <div className="confirm-dialog-actions">
          <button onClick={onClose} type="button">{cancelLabel}</button>
          <button
            className={danger ? 'danger confirm-primary' : 'confirm-primary'}
            onClick={() => {
              onConfirm()
              onClose()
            }}
            type="button"
          >{confirmLabel}</button>
        </div>
      </div>
    </Modal>
  )
}
