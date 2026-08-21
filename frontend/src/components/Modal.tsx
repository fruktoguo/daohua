import { useEffect, useRef, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { useT } from '../i18n'

const FOCUSABLE_SELECTOR = [
  'button:not(:disabled)',
  'input:not(:disabled)',
  'select:not(:disabled)',
  'textarea:not(:disabled)',
  'a[href]',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

export type ModalSize = 'small' | 'medium' | 'large'

interface ModalProps {
  open: boolean
  labelledBy: string
  onClose: () => void
  children: ReactNode
  size?: ModalSize
  className?: string
  closeLabel?: string
  initialFocusRef?: RefObject<HTMLElement | null>
  dismissible?: boolean
}

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
