import { useEffect, useRef, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { useT } from '../i18n'

/** 可聚焦元素选择器列表，用于弹窗内焦点循环锁定 (Focus Trap) */
const FOCUSABLE_SELECTOR = [
  'button:not(:disabled)',
  'input:not(:disabled)',
  'select:not(:disabled)',
  'textarea:not(:disabled)',
  'a[href]',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

/** 模态窗预设尺寸规格 */
export type ModalSize = 'small' | 'medium' | 'large'

/**
 * 通用模态弹窗组件 Props
 */
interface ModalProps {
  /** 弹窗是否开启显示 */
  open: boolean
  /** 关联标题无障碍 ID (aria-labelledby) */
  labelledBy: string
  /** 关闭弹窗回调 */
  onClose: () => void
  /** 弹窗主体子内容 */
  children: ReactNode
  /** 弹窗尺寸规格（默认 'medium'） */
  size?: ModalSize
  /** 外部样式类名 */
  className?: string
  /** 关闭按钮提示文本（默认取 common.close） */
  closeLabel?: string
  /** 初始聚焦元素 Ref */
  initialFocusRef?: RefObject<HTMLElement | null>
  /** 是否允许通过 ESC 键或点击遮罩层关闭（默认 true） */
  dismissible?: boolean
}

/**
 * 通用修仙风模态弹窗基础组件 (Modal)
 *
 * 核心特性与无障碍支持：
 * 1. 使用 createPortal 挂载至 document.body，彻底隔离层叠上下文与父级 overflow 裁剪；
 * 2. 焦点陷阱 (Focus Trap)：按下 Tab/Shift+Tab 时自动在弹窗内首尾可聚焦元素之间循环，防止焦点逃逸；
 * 3. 自动还原焦点：弹窗销毁时自动还原至唤起前的活跃 DOM 元素；
 * 4. 滚动穿透锁定：打开弹窗时锁定 document.body 页面滚动；
 * 5. 键盘与遮罩交互：支持 Escape 键关闭与点击背景蒙层关闭 (dismissible=true 时)。
 */
export function Modal({
  open,
  labelledBy,
  onClose,
  children,
  size = 'medium',
  className = '',
  closeLabel,
  initialFocusRef,
  dismissible = true,
}: ModalProps): React.JSX.Element | null {
  const t = useT()
  // 关闭按钮文案默认取 common.close；默认参数无法调用 hook，改在组件体内解析。
  const resolvedCloseLabel = closeLabel ?? t('common.close')
  const dialogRef = useRef<HTMLDivElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!open) return undefined
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const focusFrame = window.requestAnimationFrame(() => {
      if (initialFocusRef?.current) initialFocusRef.current.focus()
      else if (closeRef.current) closeRef.current.focus()
      else dialogRef.current?.focus()
    })

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && dismissible) {
        event.preventDefault()
        onClose()
        return
      }
      if (event.key !== 'Tab' || !dialogRef.current) return
      const focusable = Array.from(dialogRef.current.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR))
      if (focusable.length === 0) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    return () => {
      window.cancelAnimationFrame(focusFrame)
      document.removeEventListener('keydown', handleKeyDown)
      document.body.style.overflow = previousOverflow
      previouslyFocused?.focus()
    }
  }, [dismissible, initialFocusRef, onClose, open])

  if (!open) return null

  return createPortal(
    <div
      className="modal-backdrop"
      onMouseDown={event => {
        if (dismissible && event.target === event.currentTarget) onClose()
      }}
    >
      <div
        aria-labelledby={labelledBy}
        aria-modal="true"
        className={`modal-shell modal-shell--${size} panel ${className}`.trim()}
        ref={dialogRef}
        role="dialog"
        tabIndex={-1}
      >
        {dismissible && (
          <button
            aria-label={resolvedCloseLabel}
            className="modal-close"
            onClick={onClose}
            ref={closeRef}
            title={resolvedCloseLabel}
            type="button"
          >×</button>
        )}
        {children}
      </div>
    </div>,
    document.body,
  )
}
