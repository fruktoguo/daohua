import React, { memo, type CSSProperties, type ReactNode } from 'react'
import { FloatingTooltip, type FloatingPlacement, type FloatingTone } from './FloatingPopover'
import {
  formatChineseSplitNumber,
  formatUnitNumber,
} from '../game/format'
import { useT } from '../i18n'
import type { PillTone } from './PillLabel'

/** UnitNumber 单位数值组件 Props */
export interface UnitNumberProps {
  /** 原始数值（支持 number, bigint, 纯数字 string） */
  value: number | bigint | string
  /** 预设修仙视觉色调 */
  tone?: PillTone | FloatingTone
  /** 前缀 (如 "+", "-", "¥", "×") */
  prefix?: ReactNode
  /** 后缀 (如 "/s", "%", " 点") */
  suffix?: ReactNode
  /** 精度：最大小数位数 (默认 2) */
  maxDecimals?: number
  /** 最大数字字符总数 (默认 6) */
  maxDigits?: number
  /** 取整模式 (默认 'floor'，可选 'round' 或 'ceil') */
  roundMode?: 'round' | 'floor' | 'ceil'
  /** 是否强制保留两位小数 (默认 false) */
  padDecimals?: boolean
  /** 是否禁用 Hover 气泡 (默认 false) */
  disableTooltip?: boolean
  /** 气泡提示放置位置 (默认 'top') */
  tooltipPlacement?: FloatingPlacement
  /** 气泡提示主题色 (默认 'gold') */
  tooltipTone?: FloatingTone
  /** 气泡标题前缀说明 (如 "当前灵石"、"角色真元") */
  tooltipLabel?: ReactNode
  /** 自定义气泡完整内容（若提供则覆盖默认分段提示） */
  customTooltip?: ReactNode
  /** 额外自定义类名 */
  className?: string
  /** 额外内联样式 */
  style?: CSSProperties
  /** 点击事件 */
  onClick?: (event: React.MouseEvent<HTMLSpanElement>) => void
}

/**
 * 基础修仙单位数值组件 (UnitNumber)
 * - 自动改后缀（万、亿、兆、京等阶梯），数字最多六位，小数点后最多两位，默认向下取整 (floor)
 * - 鼠标悬停 (Hover) 时弹出轻量气泡展示完整数字，并按中文四位分段，最多显示小数点后两位（如 "1234亿 7777万 5211"）
 */
export const UnitNumber = memo(function UnitNumber({
  value,
  tone,
  prefix,
  suffix,
  maxDecimals = 2,
  maxDigits = 6,
  roundMode = 'floor',
  padDecimals = false,
  disableTooltip = false,
  tooltipPlacement = 'top',
  tooltipTone = 'gold',
  tooltipLabel,
  customTooltip,
  className = '',
  style,
  onClick,
}: UnitNumberProps): React.JSX.Element {
  const t = useT()
  // 1. 紧凑展示文本 (例如 "9999.99万", "20亿", "5211")
  const compactText = formatUnitNumber(value, {
    maxDecimals,
    maxDigits,
    roundMode,
    padDecimals,
  })

  // 2. 完整四位分段文本 (例如 "1234亿 7777万 5211")
  const splitText = formatChineseSplitNumber(value)

  // 构建 Hover 提示内容 (严格保证单行展示：标题对应数量，然后显示具体数值，就只要这俩文本)
  const titleText = tooltipLabel ?? t('common.quantity')
  const tooltipContent = customTooltip ?? (
    <div className="unit-number-tooltip-inline">
      <span className="unit-number-tooltip-label">{titleText}</span>
      <strong className="unit-number-tooltip-split">{splitText}</strong>
    </div>
  )

  const toneClass = tone ? `unit-number--tone-${tone}` : ''
  const noTooltipClass = disableTooltip ? 'unit-number--no-tooltip' : ''
  const element = (
    <span
      className={`unit-number ${toneClass} ${noTooltipClass} ${className}`.trim()}
      onClick={onClick}
      style={style}
    >
      {prefix && <span className="unit-number-prefix">{prefix}</span>}
      <span className="unit-number-val">{compactText}</span>
      {suffix && <span className="unit-number-suffix">{suffix}</span>}
    </span>
  )

  if (disableTooltip) {
    return element
  }

  return (
    <FloatingTooltip placement={tooltipPlacement} title={tooltipContent} tone={tooltipTone}>
      {element}
    </FloatingTooltip>
  )
})
