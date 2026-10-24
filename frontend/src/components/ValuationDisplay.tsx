import React, { memo } from 'react'
import { FloatingTooltip } from './FloatingPopover'
import type { ValuationBreakdownDto } from '../network/protocol'
import { formatNumber } from '../game/format'
import { useT } from '../i18n'

export interface ValuationHoverTargetProps {
  /** 总身家估值 */
  valuation: number
  /** 身家构成细分；为空时展示总身家与保底说明 */
  breakdown?: ValuationBreakdownDto | null
  /** 弹出方位，默认 top */
  placement?: 'top' | 'bottom' | 'left' | 'right'
  /** 是否 display: contents，使内部子元素参与父级 flex 布局 */
  displayContents?: boolean
  /** 目标子节点 */
  children: React.ReactElement
}

/**
 * 身家悬浮目标组件：鼠标 hover 时展示总身家及「战斗装备」、「参悟功法」、「技艺工具」三项细分估值。
 */
export const ValuationHoverTarget = memo(function ValuationHoverTarget({
  valuation,
  breakdown,
  placement = 'top',
  displayContents = false,
  children,
}: ValuationHoverTargetProps): React.JSX.Element {
  const t = useT()
  const total = Math.max(0, Math.floor(valuation))
  const eq = Math.max(0, Math.floor(breakdown?.equipment ?? 0))
  const ab = Math.max(0, Math.floor(breakdown?.abilities ?? 0))
  const tool = Math.max(0, Math.floor(breakdown?.tools ?? 0))

  const tooltipContent = (
    <div className="valuation-breakdown-card">
      <div className="valuation-breakdown-header">
        <span className="valuation-breakdown-title">{t('social.valuationBreakdown.title')}</span>
        <strong className="valuation-breakdown-total tone-gold">{formatNumber(total)}</strong>
      </div>
      <div className="valuation-breakdown-divider" />
      <div className="valuation-breakdown-rows">
        <div className="valuation-breakdown-row">
          <span className="valuation-breakdown-label">{t('social.valuationBreakdown.equipment')}</span>
          <span className="valuation-breakdown-val">{formatNumber(eq)}</span>
        </div>
        <div className="valuation-breakdown-row">
          <span className="valuation-breakdown-label">{t('social.valuationBreakdown.abilities')}</span>
          <span className="valuation-breakdown-val">{formatNumber(ab)}</span>
        </div>
        <div className="valuation-breakdown-row">
          <span className="valuation-breakdown-label">{t('social.valuationBreakdown.tools')}</span>
          <span className="valuation-breakdown-val">{formatNumber(tool)}</span>
        </div>
      </div>
    </div>
  )

  return (
    <FloatingTooltip
      displayContents={displayContents}
      placement={placement}
      title={tooltipContent}
      tone="gold"
    >
      {children}
    </FloatingTooltip>
  )
})

export interface ValuationDisplayProps {
  /** 总身家估值 */
  valuation: number
  /** 身家构成细分 */
  breakdown?: ValuationBreakdownDto | null
  /** 前缀标签（如「身家：」或「身家」），缺省时使用词典预设 */
  prefix?: string
  /** 自定义最外层类名 */
  className?: string
  /** 弹出方位，默认 top */
  placement?: 'top' | 'bottom' | 'left' | 'right'
}

/**
 * 完整身家行内展示组件：集成「身家：53.3万」与 hover 细分悬浮卡。
 */
export const ValuationDisplay = memo(function ValuationDisplay({
  valuation,
  breakdown,
  prefix,
  className,
  placement = 'top',
}: ValuationDisplayProps): React.JSX.Element {
  const t = useT()
  const label = prefix ?? `${t('social.card.valuation')}：`

  return (
    <ValuationHoverTarget breakdown={breakdown} placement={placement} valuation={valuation}>
      <span className={className} style={{ cursor: 'help' }}>
        <span>{label}</span>
        <span className="tone-gold">{formatNumber(Math.max(0, Math.floor(valuation)))}</span>
      </span>
    </ValuationHoverTarget>
  )
})
