import React, { memo, type CSSProperties, type ReactNode } from 'react'
import { FloatingPopover, FloatingTooltip, type FloatingTone } from './FloatingPopover'
import { UnitNumber } from './UnitNumber'
import { formatChineseSplitNumber } from '../game/format'

export type PillTone =
  | 'gold'
  | 'jade'
  | 'qi'
  | 'purple'
  | 'amber'
  | 'crimson'
  | 'emerald'
  | 'cyan'
  | 'violet'
  | 'indigo'
  | 'teal'
  | 'bronze'
  | 'silver'
  | 'slate'
  | 'sky'
  | 'muted'
  | 'default'

export interface PillProgress {
  current: number
  max: number
  text?: string
}

export interface PillStatLabelProps {
  icon?: ReactNode
  label: string
  value: string | number | ReactNode
  tone?: PillTone
  progress?: PillProgress
  tooltip?: string | ReactNode
  tooltipTone?: FloatingTone
  className?: string
  style?: CSSProperties
  onClick?: () => void
}

export interface PillBonusLabelProps {
  icon?: ReactNode
  label: string
  value: string | number | ReactNode
  tone?: PillTone
  tooltip?: string
  tooltipTone?: FloatingTone
  className?: string
  style?: CSSProperties
}

function renderPillValue(value: string | number | ReactNode, label: string, hasOuterTooltip: boolean): ReactNode {
  if (typeof value === 'number') {
    return <UnitNumber disableTooltip={hasOuterTooltip} tooltipLabel={label} value={value} />
  }
  return value
}

/**
 * 复合数值展示 Label：
 * 外层为修仙暗色卡片，图标裸显，标题采用胶囊包裹，等级/数值采用专属下划线高亮，经验值文本单行行内展示，底边展示经验条
 */
export const PillStatLabel = memo(function PillStatLabel({
  icon,
  label,
  value,
  tone = 'default',
  progress,
  tooltip,
  tooltipTone = 'gold',
  className = '',
  style,
  onClick,
}: PillStatLabelProps): React.JSX.Element {
  const ratio = progress ? Math.min(100, Math.max(0, (progress.current / Math.max(0.0001, progress.max)) * 100)) : 0
  const isNumeric = typeof value === 'number'

  const content = (
    <div
      className={`compound-stat-label pill-tone--${tone} ${progress ? 'has-progress' : ''} ${className}`}
      onClick={onClick}
      style={style}
    >
      <div className="compound-stat-main">
        {icon && <span className="compound-icon">{icon}</span>}
        <span className="capsule-label">{label}</span>
        <strong className="underlined-value">{renderPillValue(value, label, Boolean(tooltip) || isNumeric)}</strong>
        {progress?.text && <span className="compound-exp-text">{progress.text}</span>}
      </div>
      {progress && (
        <div className="compound-stat-progress-track">
          <div className="compound-stat-progress-fill" style={{ width: `${ratio}%` }} />
        </div>
      )}
    </div>
  )

  if (tooltip) {
    if (isNumeric) {
      return (
        <FloatingPopover
          content={tooltip}
          interactive={false}
          placement="top"
          quantity={value}
          quantityTitle={label || '数量'}
          trigger="hover"
        >
          {content}
        </FloatingPopover>
      )
    }
    return (
      <FloatingTooltip placement="top" title={tooltip} tone={tooltipTone}>
        {content}
      </FloatingTooltip>
    )
  }

  if (isNumeric) {
    const splitText = formatChineseSplitNumber(value)
    const singleRowTooltip = (
      <div className="unit-number-tooltip-inline">
        <span className="unit-number-tooltip-label">{label || '数量'}</span>
        <strong className="unit-number-tooltip-split">{splitText}</strong>
      </div>
    )
    return (
      <FloatingTooltip placement="top" title={singleRowTooltip} tone={tooltipTone}>
        {content}
      </FloatingTooltip>
    )
  }

  return content
})

/**
 * 复合属性加成 Label：
 * 无外边框，加成标题微胶囊包裹，加成数值采用专属色彩下划线高亮
 */
export const PillBonusLabel = memo(function PillBonusLabel({
  icon,
  label,
  value,
  tone = 'default',
  tooltip,
  tooltipTone = 'gold',
  className = '',
  style,
}: PillBonusLabelProps): React.JSX.Element {
  const isNumeric = typeof value === 'number'

  const content = (
    <div className={`compound-bonus-label pill-tone--${tone} ${className}`} style={style}>
      {icon && <span className="compound-icon">{icon}</span>}
      <span className="capsule-label">{label}</span>
      <span className="underlined-value">{renderPillValue(value, label, Boolean(tooltip) || isNumeric)}</span>
    </div>
  )

  if (tooltip) {
    if (isNumeric) {
      return (
        <FloatingPopover
          content={tooltip}
          interactive={false}
          placement="top"
          quantity={value}
          quantityTitle={label || '数量'}
          trigger="hover"
        >
          {content}
        </FloatingPopover>
      )
    }
    return (
      <FloatingTooltip placement="top" title={tooltip} tone={tooltipTone}>
        {content}
      </FloatingTooltip>
    )
  }

  if (isNumeric) {
    const splitText = formatChineseSplitNumber(value)
    const singleRowTooltip = (
      <div className="unit-number-tooltip-inline">
        <span className="unit-number-tooltip-label">{label || '数量'}</span>
        <strong className="unit-number-tooltip-split">{splitText}</strong>
      </div>
    )
    return (
      <FloatingTooltip placement="top" title={singleRowTooltip} tone={tooltipTone}>
        {content}
      </FloatingTooltip>
    )
  }

  return content
})
