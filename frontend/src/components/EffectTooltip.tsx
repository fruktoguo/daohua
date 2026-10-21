import { type ReactNode } from 'react'
import { calculateAbilityLevelAndProgress } from '../game/combatConstants'
import { getT } from '../i18n'
import {
  createDescriptionAsideCard,
  createProgressAsideCard,
  FloatingInfoCard,
  FloatingPopover,
  type FloatingAsideCard,
  type FloatingPlacement,
  type FloatingTone,
} from './FloatingPopover'
import { PillBonusLabel, type PillTone } from './PillLabel'
import { createBonusAsideCard, extractBuffBonusItems, type BonusAsideItem } from './ItemDisplay'
import { RootAspectRadarChart } from './RootAspectRadarChart'
import { ValueText } from './ValueText'
import { StatGlyph, resolveStatGlyph } from './StatGlyph'
import type { AbilityBonusStat } from '../game/format'
import type { AbilityBuffSpec, CombatAbilityDefinition } from '../game/types'
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
 * 构造内功心法六维资质倾向说明独立副卡片 (Description Aside Card)
 * 阐述战斗中获取的六维经验会按照内功的六维百分比去增幅。
 */
export function createInternalRootExpAsideCard(tone: FloatingTone = 'qi'): FloatingAsideCard {
  const t = getT()
  return createDescriptionAsideCard({
    id: 'description-internal-root-exp',
    mark: t('game.abilityAside.internalRootExpMark'),
    title: t('game.abilityAside.internalRootExpTitle'),
    tone,
    description: t('game.abilityAside.internalRootExpDesc'),
  })
}

/**
 * 提取内功心法的常驻面板被动加成与修为获取条目列表 (BonusAsideItem[])
 */
export function extractInternalAbilityBonusItems(
  ability: CombatAbilityDefinition,
  level = 1
): BonusAsideItem[] {
  if (ability.discipline !== 'internal') return []
  const items: BonusAsideItem[] = []
  const normalizedLevel = Math.max(1, level)
  const levelOffset = normalizedLevel - 1

  if (ability.cultivationExpBonusPoints) {
    const val = ability.cultivationExpBonusPoints
    items.push({
      glyph: 'experience',
      label: '修为获取',
      tone: val >= 0 ? 'gold' : 'amber',
      value: `${val > 0 ? '+' : ''}${val}%`,
    })
  }

  if (ability.passive) {
    const passiveEffPerLevel = ability.passiveEfficiencyPerLevel ?? 0.05
    const passiveScale = Math.max(0, 1.0 + passiveEffPerLevel * levelOffset)
    const statConfigs: Record<string, { label: string; tone: PillTone }> = {
      physicalDamageBonusPoints: { label: '物理增伤', tone: 'crimson' },
      spellDamageBonusPoints: { label: '术法增伤', tone: 'indigo' },
      physicalDamageReductionPoints: { label: '物理减伤', tone: 'emerald' },
      spellDamageReductionPoints: { label: '术法减伤', tone: 'violet' },
      actionSpeedPoints: { label: '行动加速', tone: 'teal' },
      attackSpeedPoints: { label: '攻击攻速', tone: 'teal' },
      criticalDamageBonusPoints: { label: '暴击伤害', tone: 'gold' },
      threatBonusPoints: { label: '威胁增幅', tone: 'crimson' },
      healingBonusPoints: { label: '治疗增幅', tone: 'violet' },
      damageAbsorptionPoints: { label: '伤害减免', tone: 'emerald' },
    }
    for (const [stat, baseVal] of Object.entries(ability.passive)) {
      if (typeof baseVal === 'number' && baseVal !== 0) {
        const scaledVal = Math.round(baseVal * passiveScale * 100) / 100
        const cfg = statConfigs[stat] ?? { label: stat, tone: 'default' }
        items.push({
          glyph: resolveStatGlyph(stat) ?? resolveStatGlyph(cfg.label) ?? undefined,
          label: cfg.label,
          tone: cfg.tone,
          value: `${scaledVal > 0 ? '+' : ''}${scaledVal}%`,
        })
      }
    }
  }

  return items
}

/**
 * 构造内功心法常驻面板被动加成与修为获取独立副卡片 (Bonus Aside Card)
 */
export function createInternalAbilityBonusAsideCard(
  ability: CombatAbilityDefinition,
  level = 1
): FloatingAsideCard | null {
  if (ability.discipline !== 'internal') return null
  const items = extractInternalAbilityBonusItems(ability, level)
  if (items.length === 0) return null
  const t = getT()
  return createBonusAsideCard({
    id: `aside-internal-bonus-${ability.abilityId}`,
    mark: t('game.abilityAside.internalBonusMark'),
    title: t('game.abilityAside.internalBonusTitle'),
    tone: 'qi',
    items,
  })
}

/**
 * 提取单个战内 Buff 效果规格的条目列表 (BonusAsideItem[])
 * 支持碎罡等双抗、攻击力、命中、暴击、叠层来源与机制说明条目。
 */
export function extractSingleBuffSpecBonusItems(
  buff: AbilityBuffSpec,
  level = 1
): BonusAsideItem[] {
  // Buff 效果量随功法等级成长（与服务端 CastSkill 同式：每级 +2% 线性成长）。
  const levelGrowth = 1 + (Math.max(1, level) - 1) * 0.02
  const isStacked = Boolean(
    (buff.resistancePerStack && buff.resistancePerStack > 0) ||
    (buff.missingHitpointsPerStack && buff.missingHitpointsPerStack > 0) ||
    (buff.spellAttackStackInterval && buff.spellAttackStackInterval > 0)
  )
  const scaled = {
    ...buff,
    hpRegen: buff.hpRegen !== undefined ? buff.hpRegen * levelGrowth : undefined,
    mpRegen: buff.mpRegen !== undefined ? buff.mpRegen * levelGrowth : undefined,
    hpRegenPostRatio: buff.hpRegenPostRatio !== undefined ? buff.hpRegenPostRatio * levelGrowth : undefined,
    mpRegenPostRatio: buff.mpRegenPostRatio !== undefined ? buff.mpRegenPostRatio * levelGrowth : undefined,
    damageAbsorptionPoints: buff.damageAbsorptionPoints !== undefined ? buff.damageAbsorptionPoints * levelGrowth : undefined,
    attackSpeedPoints: buff.attackSpeedPoints !== undefined ? buff.attackSpeedPoints * levelGrowth : undefined,
    threatPoints: buff.threatPoints !== undefined ? buff.threatPoints * levelGrowth : undefined,
    damageBonusPoints: buff.damageBonusPoints !== undefined ? buff.damageBonusPoints * levelGrowth : undefined,
    threatPerSecondMaxHpPoints: buff.threatPerSecondMaxHpPoints !== undefined ? buff.threatPerSecondMaxHpPoints * levelGrowth : undefined,
    attackPower: buff.attackPower !== undefined ? buff.attackPower * levelGrowth : undefined,
    accuracy: buff.accuracy !== undefined ? buff.accuracy * levelGrowth : undefined,
    critical: buff.critical !== undefined ? buff.critical * levelGrowth : undefined,
    resistancePoints: buff.resistancePoints,
    hpRegenPerSecond: buff.hpRegenPerSecond !== undefined ? buff.hpRegenPerSecond * levelGrowth : undefined,
    isPerStack: isStacked,
  }
  // 纯粹返回 Buff 本身带来的状态效果条目；持续时间与层数计算等属于添加事件控制的外部属性，由技能描述自身交代。
  return extractBuffBonusItems(scaled)
}

/**
 * 提取功法战内 Buff 的效果条目列表 (BonusAsideItem[])
 * 兼容单 Buff 或 buffs[] 取首项。
 */
export function extractAbilityBuffBonusItems(
  ability: CombatAbilityDefinition,
  level = 1
): BonusAsideItem[] {
  const buffSpecs = ability.buffs && ability.buffs.length > 0 ? ability.buffs : ability.buff ? [ability.buff] : []
  if (buffSpecs.length === 0) return []
  return extractSingleBuffSpecBonusItems(buffSpecs[0], level)
}

/**
 * 构造功法战内全部 Buff 独立副卡片列表 (FloatingAsideCard[])
 * 支持 buffs[] 多状态并列展开（如碎罡同时授予「碎罡」破防与「罡锋」锐利两张独立副卡）。
 */
export function createAbilityBuffAsideCards(
  ability: CombatAbilityDefinition,
  level = 1
): FloatingAsideCard[] {
  const buffSpecs = ability.buffs && ability.buffs.length > 0
    ? ability.buffs
    : ability.buff
    ? [ability.buff]
    : []
  if (buffSpecs.length === 0) return []
  const t = getT()
  return buffSpecs.map((spec, index) => {
    const items = extractSingleBuffSpecBonusItems(spec, level)
    const buffName = spec.name || (buffSpecs.length === 1 ? t('game.abilityAside.buffTitle') : `状态${index + 1}`)
    const isDebuff = (spec.resistancePoints ?? 0) < 0 || (spec.damageAbsorptionPoints ?? 0) < 0
    return createBonusAsideCard({
      id: `aside-ability-buff-${ability.abilityId}-${spec.buffId || index}`,
      mark: isDebuff ? '减益状态' : t('game.abilityAside.buffMark'),
      title: `${t('game.abilityAside.buffTitle')}·${buffName}`,
      tone: isDebuff ? 'crimson' : 'emerald',
      items,
    })
  })
}

/**
 * 构造功法战内首个 Buff 独立副卡片 (兼容旧单卡调用)
 */
export function createAbilityBuffAsideCard(
  ability: CombatAbilityDefinition,
  level = 1
): FloatingAsideCard | null {
  const cards = createAbilityBuffAsideCards(ability, level)
  return cards[0] ?? null
}

/**
 * 统一解析功法技能的附属卡片列表（升级进度 + 战内 Buff + 内功增益 + 内功六维倾向说明）
 */
export function resolveAbilityAsideCards(
  ability: CombatAbilityDefinition | null | undefined,
  totalExp?: number,
  tone: FloatingTone = 'qi'
): FloatingAsideCard[] {
  if (!ability) return []
  const list: FloatingAsideCard[] = []
  const level = totalExp !== undefined ? calculateAbilityLevelAndProgress(totalExp).level : 1
  if (totalExp !== undefined) {
    list.push(createAbilityProgressAsideCard(ability.abilityId, totalExp, tone))
  }
  const buffCards = createAbilityBuffAsideCards(ability, level)
  if (buffCards.length > 0) {
    list.push(...buffCards)
  }
  if (ability.discipline === 'internal') {
    const bonusCard = createInternalAbilityBonusAsideCard(ability, level)
    if (bonusCard) {
      list.push(bonusCard)
    }
    list.push(createInternalRootExpAsideCard(tone))
  }
  return list
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
  onOpenChange,
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
  /** 悬浮卡展开/收起状态变化回调 */
  onOpenChange?: (open: boolean) => void
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
            ...(!asideCards ? (content.bonuses?.map((bonus) => {
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
            }) ?? []) : []),
          ]}
          title={content.title}
        />
      }
      gap={8}
      interactive={false}
      onPlacementChange={onPlacementChange}
      onOpenChange={onOpenChange}
      openDelay={60}
      placement="bottom"
      tabIndex={tabIndex}
      triggerClassName={`effect-tooltip-target ${className}`.trim()}
    >
      {children}
    </FloatingPopover>
  )
}
