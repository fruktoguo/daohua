import React, { memo, type CSSProperties, type ReactNode } from 'react'
import { FloatingPopover, FloatingTooltip, type FloatingTone } from './FloatingPopover'
import { UnitNumber } from './UnitNumber'
import { formatChineseSplitNumber } from '../game/format'
import { useT } from '../i18n'

/**
 * 复合标签修仙色系主题
 */
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

/**
 * 属性经验/熟练度进度定义
 */
export interface PillProgress {
  /** 当前数值 */
  current: number
  /** 上限数值 */
  max: number
  /** 自定义格式化文本（如 "5800/2.71万"） */
  text?: string
}

/**
 * 复合属性数值 Label Props
 */
export interface PillStatLabelProps {
  /** 可选左侧前置图标 */
  icon?: ReactNode
  /** 属性标题文本 */
  label: string
  /** 属性数值或节点 */
  value: string | number | ReactNode
  /** 数值后缀（如 "/5s", "%"） */
  suffix?: ReactNode
  /** 原始精确数量（用于自动生成四位分段气泡） */
  quantity?: number | bigint | string
  /** 色系主题 */
  tone?: PillTone
  /** 可选底部经验进度条数据 */
  progress?: PillProgress
  /** 悬浮气泡内容（提供时覆盖默认提示） */
  tooltip?: string | ReactNode
  /** 悬浮气泡主题色 */
  tooltipTone?: FloatingTone
  /** 外部样式类名 */
  className?: string
  /** 外部内联样式 */
  style?: CSSProperties
  /** 点击回调事件 */
  onClick?: () => void
}

/**
 * 复合属性加成 Label Props
 */
export interface PillBonusLabelProps {
  /** 可选左侧前置图标 */
  icon?: ReactNode
  /** 加成属性名称 */
  label: string
  /** 加成数值（如 "+10%"） */
  value: string | number | ReactNode
  /** 加成后缀 */
  suffix?: ReactNode
  /** 原始数量 */
  quantity?: number | bigint | string
  /** 色系主题 */
  tone?: PillTone
  /** 悬浮提示文本 */
  tooltip?: string
  /** 悬浮提示主题色 */
  tooltipTone?: FloatingTone
  /** 外部样式类名 */
  className?: string
  /** 外部内联样式 */
  style?: CSSProperties
}

/**
 * 渲染胶囊数值区：纯数字走 UnitNumber 获得紧凑格式化与四位分段气泡；
 * 外层已挂气泡（tooltip 或数量气泡）时关闭内层 tooltip，避免双重浮层叠加。
 */
function renderPillValue(
  value: string | number | ReactNode,
  label: string,
  hasOuterTooltip: boolean,
  suffix?: ReactNode
): ReactNode {
  if (typeof value === 'number') {
    return <UnitNumber disableTooltip={hasOuterTooltip} suffix={suffix} tooltipLabel={label} value={value} />
  }
  return (
    <>
      {value}
      {suffix}
    </>
  )
}

/**
 * 复合数值展示 Label：
 * 外层为修仙暗色卡片，图标裸显，标题采用胶囊包裹，等级/数值采用专属下划线高亮，经验值文本单行行内展示，底边展示经验条
 */
export const PillStatLabel = memo(function PillStatLabel({
  icon,
  label,
  value,
  suffix,
  quantity,
  tone = 'default',
  progress,
  tooltip,
  tooltipTone = 'gold',
  className = '',
  style,
  onClick,
}: PillStatLabelProps): React.JSX.Element {
  const t = useT()
  const ratio = progress ? Math.min(100, Math.max(0, (progress.current / Math.max(0.0001, progress.max)) * 100)) : 0
  const numericVal = quantity !== undefined ? quantity : (typeof value === 'number' ? value : undefined)
  const isNumeric = numericVal !== undefined

  const content = (
    <div
      className={`compound-stat-label pill-tone--${tone} ${progress ? 'has-progress' : ''} ${className}`}
      onClick={onClick}
      style={style}
    >
      <div className="compound-stat-main">
        {icon && <span className="compound-icon">{icon}</span>}
        <span className="capsule-label">{label}</span>
        <strong className="underlined-value">{renderPillValue(value, label, Boolean(tooltip) || isNumeric, suffix)}</strong>
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
      const tooltipNode =
        typeof tooltip === 'string' ? (
          <div className="daohua-floating-tooltip-text">{tooltip}</div>
        ) : (
          tooltip
        )
      return (
        <FloatingPopover
          className="daohua-floating-tooltip-simple"
          content={tooltipNode}
          interactive={false}
          placement="top"
          quantity={numericVal}
          quantityTitle={label || undefined}
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
    const splitText = formatChineseSplitNumber(numericVal)
    const singleRowTooltip = (
      <div className="unit-number-tooltip-inline">
        <span className="unit-number-tooltip-label">{label || t('common.quantity')}</span>
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
  suffix,
  quantity,
  tone = 'default',
  tooltip,
  tooltipTone = 'gold',
  className = '',
  style,
}: PillBonusLabelProps): React.JSX.Element {
  const t = useT()
  const numericVal = quantity !== undefined ? quantity : (typeof value === 'number' ? value : undefined)
  const isNumeric = numericVal !== undefined

  const content = (
    <div className={`compound-bonus-label pill-tone--${tone} ${className}`} style={style}>
      {icon && <span className="compound-icon">{icon}</span>}
      <span className="capsule-label">{label}</span>
      <span className="underlined-value">{renderPillValue(value, label, Boolean(tooltip) || isNumeric, suffix)}</span>
    </div>
  )

  if (tooltip) {
    if (isNumeric) {
      return (
        <FloatingPopover
          content={tooltip}
          interactive={false}
          placement="top"
          quantity={numericVal}
          quantityTitle={label || undefined}
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
    const splitText = formatChineseSplitNumber(numericVal)
    const singleRowTooltip = (
      <div className="unit-number-tooltip-inline">
        <span className="unit-number-tooltip-label">{label || t('common.quantity')}</span>
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
