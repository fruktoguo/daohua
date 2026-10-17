import { type ReactNode } from 'react'
import { calculateAbilityLevelAndProgress } from '../game/combatConstants'
import { getT } from '../i18n'
import {
  createProgressAsideCard,
  FloatingInfoCard,
  FloatingPopover,
  type FloatingAsideCard,
  type FloatingPlacement,
  type FloatingTone,
} from './FloatingPopover'
import { PillBonusLabel, type PillTone } from './PillLabel'
import { RootAspectRadarChart } from './RootAspectRadarChart'
import { ValueText } from './ValueText'
import { StatGlyph, resolveStatGlyph } from './StatGlyph'
import type { AbilityBonusStat } from '../game/format'
/**
 * 功法/词条/效果悬浮气泡内容结构
 */
export interface EffectTooltipContent {
  /** 气泡主标题（如技能或词条名称） */
  title: string
  /** 可选顶部徽章文本（如品质/流派） */
  badge?: string
  /** 效果简要说明描述 */
  description: string
  /** 效果详细词条行列表 */
  lines: readonly string[]
  /** 复合加成属性条目（走装备 PillBonusLabel 同款展示风格） */
  bonuses?: readonly AbilityBonusStat[]
  /** 六维雷达图乘率字典（内功心法专用） */
  radarMultipliers?: Partial<Record<string, number>>
  /** 六维雷达图当前等级效率乘率（内功心法专用） */
  radarEfficiency?: number
}

/**
 * 根据技能 ID 和累计经验构造技能升级进度副卡片 (FloatingAsideCard)
 */
export function createAbilityProgressAsideCard(
  abilityId: string,
  totalExp: number,
  tone: FloatingTone = 'qi'
): FloatingAsideCard {
  const t = getT()
  const progress = calculateAbilityLevelAndProgress(totalExp)
  const displayLevel = Math.max(1, progress.level)
  return createProgressAsideCard({
    id: `ability-progress-${abilityId}`,
    mark: t('common.progressMark.ability'),
    title: t('common.upgradeProgress'),
    level: displayLevel,
    currentExp: progress.currentProgress,
    maxExp: progress.levelRequirement,
    tone,
  })
}

/**
 * 效果悬浮气泡触发包裹组件 (EffectTooltipTarget)
 *
 * 将子元素包裹在 Hover 触发器中，鼠标悬停展示修仙风 FloatingInfoCard 提示卡片。
 * 若未提供 content 则直接降级渲染原生包裹 span，不产生额外 Popover 容器开销。
 */
export function EffectTooltipTarget({
  children,
  className = '',
  content,
  tabIndex = 0,
  onPlacementChange,
  asideCards,
}: {
  /** 被包裹的触发子节点 */
  children: ReactNode
  /** 触发器样式类名 */
  className?: string
  /** 气泡展示内容定义 */
  content?: EffectTooltipContent | null
  /** 无障碍键盘聚焦顺序 */
  tabIndex?: number
  /** 悬浮卡实际落位变化回调（含视口翻转结果），供宿主避让附件元素 */
  onPlacementChange?: (placement: FloatingPlacement) => void
  /** 附属气泡列表（如技能升级进度） */
  asideCards?: readonly FloatingAsideCard[] | (() => readonly FloatingAsideCard[])
}): React.JSX.Element {
  if (!content) {
    return <span className={className} tabIndex={tabIndex}>{children}</span>
  }

  return (
    <FloatingPopover
      asideCards={asideCards}
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={content.badge}
          badgeTone="qi"
          description={
            <div>
              {content.description && (
                <ValueText className="effect-value-capsule" text={content.description} />
              )}
              {content.radarMultipliers && (
                <RootAspectRadarChart
                  efficiency={content.radarEfficiency}
                  multipliers={content.radarMultipliers}
                />
              )}
            </div>
          }
          lines={[
            ...content.lines.map((line) => {
              let glyphName: string | null = null
              if (line.includes('冷却')) glyphName = 'cooldown-reduction'
              else if (line.includes('吟唱') || line.includes('施法')) glyphName = 'action-speed'
              else if (line.includes('消耗') || line.includes('灵力')) glyphName = 'mp'
              else if (line.includes('等级')) glyphName = 'realm'
              return (
                <span key={line} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
                  {glyphName && <StatGlyph glyph={glyphName} size={14} />}
                  <ValueText className="effect-value-capsule" text={line} />
                </span>
              )
            }),
            ...(content.bonuses?.map((bonus) => {
              const glyph = resolveStatGlyph(bonus.key) || resolveStatGlyph(bonus.label)
              return (
                <PillBonusLabel
                  key={bonus.key}
                  icon={glyph ? <StatGlyph glyph={glyph} size={14.5} /> : undefined}
                  label={bonus.label}
                  tone={bonus.tone as PillTone}
                  value={bonus.value}
                />
              )
            }) ?? []),
          ]}
          title={content.title}
        />
      }
      gap={8}
      interactive={false}
      onPlacementChange={onPlacementChange}
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
