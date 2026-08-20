import { type ReactNode } from 'react'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
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
  content?: EffectTooltipContent | null
  tabIndex?: number
}): React.JSX.Element {
  if (!content) {
    return <span className={className} tabIndex={tabIndex}>{children}</span>
  }

  return (
    <FloatingPopover
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={content.badge}
          badgeTone="qi"
          description={content.description}
          lines={content.lines.map((line) => (
            <ValueText className="effect-value-capsule" key={line} text={line} />
          ))}
          title={content.title}
        />
      }
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      tabIndex={tabIndex}
      trigger="hover"
      triggerClassName={`effect-tooltip-target ${className}`.trim()}
    >
      {children}
    </FloatingPopover>
  )
}
