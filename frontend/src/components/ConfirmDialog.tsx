import { useId } from 'react'
import { useT } from '../i18n'
import { Modal } from './Modal'

/**
 * 二次确认通用对话框 Props
 */
interface ConfirmDialogProps {
  /** 弹窗是否开启 */
  open: boolean
  /** 确认框标题 */
  title: string
  /** 确认框内容描述文字 */
  description: string
  /** 确认按钮文案（默认 common.confirm） */
  confirmLabel?: string
  /** 取消按钮文案（默认 common.cancel） */
  cancelLabel?: string
  /** 是否为危险操作（如登出、销毁资产，启用红色警示样式） */
  danger?: boolean
  /** 点击确认后的回调（回调返回后组件自动触发 onClose 关闭弹窗） */
  onConfirm: () => void
  /** 关闭弹窗回调 */
  onClose: () => void
}

/**
 * 通用二次确认模态弹窗组件 (ConfirmDialog)
 *
 * 用于敏感操作（退出登录、放弃行动、批量售出等）的防误触确认。
 * 遵循前端公共弹窗规范，支持 danger 红色警示主题与无障碍焦点捕获。
 */
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
