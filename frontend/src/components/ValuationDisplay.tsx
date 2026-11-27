import React, { memo } from 'react'
import { FloatingTooltip } from './FloatingPopover'
import type { ValuationBreakdownDto } from '../network/protocol'
import { formatValuation } from '../game/format'
import { useT } from '../i18n'

export interface ValuationHoverTargetProps {
  /** 总道行 */
  valuation: number
  /** 道行构成细分；为空时展示总道行与各项 0 */
  breakdown?: ValuationBreakdownDto | null
  /** 弹出方位，默认 top */
  placement?: 'top' | 'bottom' | 'left' | 'right'
  /** 是否 display: contents，使内部子元素参与父级 flex 布局 */
  displayContents?: boolean
  /** 目标子节点 */
  children: React.ReactElement
}

/**
 * 道行悬浮目标组件：鼠标 hover 时展示总道行及「境界根基」、「战斗装备」、「携带功法」、「携带特质」四项构成（兼容旧三分项回退）。
 */
export const ValuationHoverTarget = memo(function ValuationHoverTarget({
  valuation,
  breakdown,
  placement = 'top',
  displayContents = false,
  children,
}: ValuationHoverTargetProps): React.JSX.Element {
  const t = useT()
  const total = Math.max(0, valuation)
  const useLegacyBreakdown = Boolean(
    breakdown &&
      breakdown.realmAndGenji === undefined &&
      breakdown.traits === undefined &&
      breakdown.tools > 0,
  )
  const realmGenji = Math.max(0, breakdown?.realmAndGenji ?? 0)
  const eq = Math.max(0, breakdown?.equipment ?? 0)
  const ab = Math.max(0, breakdown?.abilities ?? 0)
  const traits = Math.max(0, breakdown?.traits ?? 0)
  const tool = Math.max(0, breakdown?.tools ?? 0)

  const tooltipContent = (
    <div className="valuation-breakdown-card">
      <div className="valuation-breakdown-header">
        <span className="valuation-breakdown-title">{t('social.valuationBreakdown.title')}</span>
        <strong className="valuation-breakdown-total tone-gold">{formatValuation(total)}</strong>
      </div>
      <div className="valuation-breakdown-divider" />
      <div className="valuation-breakdown-rows">
        {!useLegacyBreakdown && (
          <div className="valuation-breakdown-row">
            <span className="valuation-breakdown-label">{t('social.valuationBreakdown.realmAndGenji')}</span>
            <span className="valuation-breakdown-val">{formatValuation(realmGenji)}</span>
          </div>
        )}
        <div className="valuation-breakdown-row">
          <span className="valuation-breakdown-label">{t('social.valuationBreakdown.equipment')}</span>
          <span className="valuation-breakdown-val">{formatValuation(eq)}</span>
        </div>
        <div className="valuation-breakdown-row">
          <span className="valuation-breakdown-label">{t('social.valuationBreakdown.abilities')}</span>
          <span className="valuation-breakdown-val">{formatValuation(ab)}</span>
        </div>
        {useLegacyBreakdown ? (
          <div className="valuation-breakdown-row">
            <span className="valuation-breakdown-label">{t('social.valuationBreakdown.tools')}</span>
            <span className="valuation-breakdown-val">{formatValuation(tool)}</span>
          </div>
        ) : (
          <div className="valuation-breakdown-row">
            <span className="valuation-breakdown-label">{t('social.valuationBreakdown.traits')}</span>
            <span className="valuation-breakdown-val">{formatValuation(traits)}</span>
          </div>
        )}
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
  /** 总道行 */
  valuation: number
  /** 道行构成细分 */
  breakdown?: ValuationBreakdownDto | null
  /** 前缀标签（如「道行：」或「道行」），缺省时使用词典预设 */
  prefix?: string
  /** 自定义最外层类名 */
  className?: string
  /** 弹出方位，默认 top */
  placement?: 'top' | 'bottom' | 'left' | 'right'
}

/**
 * 完整道行行内展示组件：集成「道行：599.9」与 hover 构成悬浮卡。
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
        <span className="tone-gold">{formatValuation(valuation)}</span>
      </span>
    </ValuationHoverTarget>
  )
})
