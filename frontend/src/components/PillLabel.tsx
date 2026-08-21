import React, { memo, type CSSProperties, type ReactNode } from 'react'
import { FloatingTooltip, type FloatingTone } from './FloatingPopover'

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
  value: string | number
  tone?: PillTone
  tooltip?: string
  tooltipTone?: FloatingTone
  className?: string
  style?: CSSProperties
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

  const content = (
    <div
      className={`compound-stat-label pill-tone--${tone} ${progress ? 'has-progress' : ''} ${className}`}
      onClick={onClick}
      style={style}
    >
      <div className="compound-stat-main">
        {icon && <span className="compound-icon">{icon}</span>}
        <span className="capsule-label">{label}</span>
        <strong className="underlined-value">{value}</strong>
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
    return (
      <FloatingTooltip placement="top" title={tooltip} tone={tooltipTone}>
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
  const content = (
    <div className={`compound-bonus-label pill-tone--${tone} ${className}`} style={style}>
      {icon && <span className="compound-icon">{icon}</span>}
      <span className="capsule-label">{label}</span>
      <span className="underlined-value">{value}</span>
    </div>
  )

  if (tooltip) {
    return (
      <FloatingTooltip placement="top" title={tooltip} tone={tooltipTone}>
        {content}
      </FloatingTooltip>
    )
  }

  return content
})
