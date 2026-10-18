import type { ReactNode } from 'react'
import { EffectTooltipTarget, type EffectTooltipContent } from './EffectTooltip'
import type { FloatingAsideCard } from './FloatingPopover'

/**
 * 「瓦片 + 悬浮卡 + 容器附加层」组合容器 (ConditionedTile)
 *
 * 统一功法/物品瓦片与悬浮卡的层叠结构，
 * 将角标、等级徽标等附加元素（extras）置于悬浮卡触发域之外，
 * 避免误触或遮挡。
 * （条件配置按钮已统一收拢至展开选择面板及已学功法瓦片直达，彻底消灭 Hover 浮动遮挡）
 */
export function ConditionedTile({
  as: Tag = 'span',
  children,
  className = '',
  content,
  extras,
  triggerClassName = '',
  triggerTabIndex,
  asideCards,
}: {
  /** 容器元素类型，默认 span（块级卡片容器传 'div'） */
  as?: 'span' | 'div'
  /** 瓦片本体（进入悬浮卡触发域） */
  children: ReactNode
  /** 容器样式类名（如 abilities-slot-card / combat-slot-card） */
  className?: string
  /** 悬浮卡内容（null 时 tooltip 关闭，仅渲染瓦片） */
  content?: EffectTooltipContent | null
  /** 初始条件按钮方位兼容字段（已弃用框外悬浮按钮） */
  defaultSide?: 'top' | 'bottom'
  /** 历史兼容条件入口配置（已迁至展开选择面板，此处安全忽略） */
  condition?: unknown
  /** 容器级附加元素（Lv 徽标等，置于悬浮卡触发域之外） */
  extras?: ReactNode
  /** 悬浮卡触发层样式类名（如 combat-ability-slot-shell / item-grid-picker-tooltip） */
  triggerClassName?: string
  /** 悬浮卡触发层的 tabIndex（瓦片本体已是可聚焦 button 时传 -1 避免多占一站） */
  triggerTabIndex?: number
  /** 附属气泡列表（如升级进度） */
  asideCards?: readonly FloatingAsideCard[] | (() => readonly FloatingAsideCard[])
}): React.JSX.Element {
  return (
    <Tag className={`conditioned-tile ${className}`.trim()}>
      <EffectTooltipTarget
        asideCards={asideCards}
        className={triggerClassName}
        content={content}
        tabIndex={triggerTabIndex}
      >
        {children}
      </EffectTooltipTarget>
      {extras}
    </Tag>
  )
}
