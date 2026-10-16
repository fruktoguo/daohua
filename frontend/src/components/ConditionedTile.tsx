import { useCallback, useState, type ReactNode } from 'react'
import { EffectTooltipTarget, type EffectTooltipContent } from './EffectTooltip'
import type { FloatingPlacement } from './FloatingPopover'

/**
 * 「瓦片 + 悬浮卡 + 框外条件按钮」组合容器 (ConditionedTile)
 *
 * 统一物品/功法瓦片的条件入口交互：
 * 1. 条件按钮默认隐藏，容器 hover/focus-within 时从瓦片画框之外浮现（不占用瓦片内部角落）；
 * 2. 按钮方位与悬浮卡落位联动避让：悬浮卡默认落瓦片下方，按钮悬于上缘外侧；
 *    悬浮卡经视口翻转到上方时，按钮自动切到下缘外侧（is-below）；
 * 3. 按钮位于悬浮卡触发域之外——悬停按钮不再弹卡，点开条件编辑器也不会出现
 *    tooltip 叠加在 modal 之上的问题（FloatingPopover z-index 10000 > Modal 70）。
 */
export function ConditionedTile({
  as: Tag = 'span',
  children,
  className = '',
  condition,
  content,
  defaultSide = 'top',
  extras,
  triggerClassName = '',
  triggerTabIndex,
}: {
  /** 容器元素类型，默认 span（块级卡片容器传 'div'） */
  as?: 'span' | 'div'
  /** 瓦片本体（进入悬浮卡触发域） */
  children: ReactNode
  /** 容器样式类名（如 abilities-slot-card / combat-slot-card） */
  className?: string
  /** 条件入口配置；null 时不渲染按钮 */
  condition?: {
    /** 按钮文案（如「条件」「条件×2」） */
    label: ReactNode
    /** 无障碍标签 */
    ariaLabel: string
    /** 悬停提示 */
    title: string
    /** 点击回调 */
    onClick: () => void
  } | null
  /** 悬浮卡内容（null 时 tooltip 关闭，仅渲染瓦片与按钮） */
  content?: EffectTooltipContent | null
  /** 初始条件按钮方位，默认 'top'；已知悬浮卡恒落在上方时可传 'bottom' 避免翻转跳动 */
  defaultSide?: 'top' | 'bottom'
  /** 容器级附加元素（Lv 徽标等，置于悬浮卡触发域之外） */
  extras?: ReactNode
  /** 悬浮卡触发层样式类名（如 combat-ability-slot-shell / item-grid-picker-tooltip） */
  triggerClassName?: string
  /** 悬浮卡触发层的 tabIndex（瓦片本体已是可聚焦 button 时传 -1 避免多占一站） */
  triggerTabIndex?: number
}): React.JSX.Element {
  // 条件按钮方位：悬浮卡落在上方（翻转结果）时按钮走下方外侧，其余方位恒走上方外侧。
  const [side, setSide] = useState<'top' | 'bottom'>(defaultSide)
  const handlePlacementChange = useCallback((placement: FloatingPlacement) => {
    const next = placement.startsWith('top') ? 'bottom' : 'top'
    setSide(previous => (previous === next ? previous : next))
  }, [])

  return (
    <Tag className={`conditioned-tile ${className}`.trim()}>
      <EffectTooltipTarget
        className={triggerClassName}
        content={content}
        onPlacementChange={handlePlacementChange}
        tabIndex={triggerTabIndex}
      >
        {children}
      </EffectTooltipTarget>
      {extras}
      {condition && (
        <button
          aria-label={condition.ariaLabel}
          className={`item-slot-inline-action item-slot-inline-action--condition${side === 'bottom' ? ' is-below' : ''}`}
          onClick={condition.onClick}
          title={condition.title}
          type="button"
        >
          {condition.label}
        </button>
      )}
    </Tag>
  )
}
