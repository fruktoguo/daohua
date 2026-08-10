import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { ValueText } from './ValueText'

export interface EffectTooltipContent {
  title: string
  badge?: string
  description: string
  lines: readonly string[]
}

export function EffectTooltipTarget({
  children,
  className = '',
  content,
  tabIndex = 0,
}: {
  children: ReactNode
  className?: string
  content: EffectTooltipContent
  tabIndex?: number
}): React.JSX.Element {
  const anchorRef = useRef<HTMLSpanElement>(null)
  const tooltipRef = useRef<HTMLDivElement>(null)
  const tooltipId = useId()
  const [visible, setVisible] = useState(false)
  const [position, setPosition] = useState({ left: 8, top: 8 })

  const positionTooltip = useCallback(() => {
    const anchor = anchorRef.current
    if (!anchor) return
    const anchorRect = anchor.getBoundingClientRect()
    const tooltipWidth = Math.min(320, window.innerWidth - 16)
    const tooltipHeight = tooltipRef.current?.offsetHeight ?? 220
    const left = Math.max(8, Math.min(window.innerWidth - tooltipWidth - 8, anchorRect.left + anchorRect.width / 2 - tooltipWidth / 2))
    const below = anchorRect.bottom + 8
    const top = below + tooltipHeight <= window.innerHeight - 8
      ? below
      : Math.max(8, anchorRect.top - tooltipHeight - 8)
    setPosition(current => current.left === left && current.top === top ? current : { left, top })
  }, [])

  useLayoutEffect(() => {
    if (visible) positionTooltip()
  }, [positionTooltip, visible])

  useEffect(() => {
    if (!visible) return undefined
    window.addEventListener('resize', positionTooltip)
    window.addEventListener('scroll', positionTooltip, true)
    return () => {
      window.removeEventListener('resize', positionTooltip)
      window.removeEventListener('scroll', positionTooltip, true)
    }
  }, [positionTooltip, visible])

  return (
    <>
      <span
        aria-describedby={visible ? tooltipId : undefined}
        className={`effect-tooltip-target ${className}`.trim()}
        onBlur={() => setVisible(false)}
        onFocus={() => setVisible(true)}
        onMouseEnter={() => setVisible(true)}
        onMouseLeave={() => setVisible(false)}
        ref={anchorRef}
        tabIndex={tabIndex}
      >{children}</span>
      {visible && createPortal(
        <div
          className="effect-tooltip"
          id={tooltipId}
          ref={tooltipRef}
          role="tooltip"
          style={{ left: position.left, top: position.top }}
        >
          <header>
            <strong>{content.title}</strong>
            {content.badge && <span>{content.badge}</span>}
          </header>
          <p>{content.description}</p>
          <ul>{content.lines.map(line => <li key={line}><ValueText className="effect-value-capsule" text={line} /></li>)}</ul>
        </div>,
        document.body,
      )}
    </>
  )
}
