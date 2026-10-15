import { memo, useMemo, type ReactNode } from 'react'
import { calculateRealmInfo } from '../game/combatConstants'
import { formatInteger } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findAbilityInCatalog, findCombatantInCatalog } from '../game/ids'
import type { CombatantDefinition, MonsterDropDefinition, NearbyMonsterDefinition } from '../game/types'
import { useT, type TFunction } from '../i18n'
import { FloatingInfoCard, FloatingPopover, type FloatingPlacement, type FloatingTone } from './FloatingPopover'
import { ItemDropReference } from './ItemDisplay'
import { UnitNumber } from './UnitNumber'

export type MonsterRank = 'normal' | 'elite' | 'boss'

/**
 * 拼接敌怪类型标签：修仙伤害流派 + 可选五行属性（例如「体魄型 · 纯阳」）
 */
export function getEnemyTypeLabel(
  category?: string | null,
  element?: string | null,
  t?: TFunction,
): string {
  if (!t || !category) return ''
  const catKey = `combat.encounter.damageCategory.${category}`
  const elemKey = `combat.encounter.element.${element}`
  const cat = t(catKey) || category
  const elem = element && element !== 'none' ? ` · ${t(elemKey) || element}` : ''
  return `${cat}${elem}`
}

/**
 * 解析并归一化怪物的生命层次与徽记色彩
 */
export function resolveMonsterRank(
  rawRank?: string | null,
  isElite?: boolean,
  isBoss?: boolean,
): { rank: MonsterRank; badgeTone: FloatingTone } {
  if (isBoss || rawRank === 'boss') {
    return { rank: 'boss', badgeTone: 'danger' }
  }
  if (isElite || rawRank === 'elite') {
    return { rank: 'elite', badgeTone: 'purple' }
  }
  return { rank: 'normal', badgeTone: 'gold' }
}

export interface MonsterDisplayTargetProps {
  /** 怪物数据源：配表定义、附近快照定义或 enemyId 标识 */
  monster: CombatantDefinition | NearbyMonsterDefinition | string
  /** 刷出节奏提示（如「逢 20 场出没」） */
  intervalHint?: string
  /** 显式声明为精英 */
  isElite?: boolean
  /** 显式声明为领主/Boss */
  isBoss?: boolean
  /** 自定义卡片描述文本（缺省自动根据流派与属性生成） */
  description?: ReactNode
  /** 自定义卡片底部额外插槽（如万象图鉴的收录/击败状态胶囊） */
  footer?: ReactNode
}

/**
 * 统一生灵悬浮详情卡 (MonsterInfoCard)
 *
 * 全局收拢大地图、附近生灵、讨伐生态、万象图鉴与事件日志中的妖兽预览；
 * 具备生命层次品质徽记、境界等阶副标、修仙流派属性标签、出没周期、击杀修为收益、
 * 施展功法名录、复合掉落物胶囊（ItemDropReference）以及标准战斗属性网格。
 */
export const MonsterInfoCard = memo(function MonsterInfoCard({
  monster,
  intervalHint,
  isElite,
  isBoss,
  description,
  footer,
}: MonsterDisplayTargetProps): React.JSX.Element {
  const t = useT()
  const encounters = useGameStore(state => state.encounters)
  const maps = useGameStore(state => state.maps)
  const abilities = useGameStore(state => state.abilities)

  // 1. 规整化数据源：支持直接传 CombatantDefinition、NearbyMonsterDefinition 或 enemyId 字符串
  const resolved = useMemo<{
    enemyId: string
    name: string
    level: number
    tier: number
    rawRank?: string | null
    category?: string | null
    element?: string | null
    experience: number
    skills: readonly { abilityId?: string | null }[]
    drops: readonly MonsterDropDefinition[]
    attributes: {
      maxHitpoints: number
      maxManapoints: number
      physicalAttack: number
      spellAttack: number
      armor: number
      magicResistance: number
      attackIntervalMilliseconds?: number
    }
  }>(() => {
    let enemyId = ''
    let name = ''
    let level = 1
    let tier = 1
    let rawRank: string | null = null
    let category: string | null = null
    let element: string | null = null
    let experience = 0
    let skills: readonly { abilityId?: string | null }[] = []
    let drops: readonly MonsterDropDefinition[] = []
    let staticDef: CombatantDefinition | undefined

    if (typeof monster === 'string') {
      enemyId = monster
      staticDef = findCombatantInCatalog(encounters, maps, enemyId)
    } else if ('enemyId' in monster) {
      enemyId = monster.enemyId
      staticDef = monster
    } else {
      enemyId = monster.monsterId
      name = monster.name
      level = monster.level
      tier = monster.tier
      rawRank = monster.rank
      drops = monster.drops ?? []
      staticDef = findCombatantInCatalog(encounters, maps, enemyId)
    }

    if (staticDef) {
      name = staticDef.name || name || enemyId
      level = staticDef.level ?? level
      tier = staticDef.tier ?? tier
      rawRank = staticDef.rank ?? rawRank
      category = staticDef.category
      element = staticDef.element
      experience = staticDef.experience ?? experience
      skills = staticDef.skills ?? skills
      drops = staticDef.drops && staticDef.drops.length > 0 ? staticDef.drops : drops
      const attrs = staticDef.attributes
      return {
        enemyId,
        name,
        level,
        tier,
        rawRank,
        category,
        element,
        experience,
        skills,
        drops,
        attributes: {
          maxHitpoints: attrs.maxHitpoints ?? 1,
          maxManapoints: attrs.maxManapoints ?? 0,
          physicalAttack: attrs.physicalAttack ?? 0,
          spellAttack: attrs.spellAttack ?? 0,
          armor: attrs.armor ?? 0,
          magicResistance: attrs.magicResistance ?? 0,
          attackIntervalMilliseconds: attrs.attackIntervalMilliseconds,
        },
      }
    }

    // 兜底退化：仅持有 NearbyMonsterDefinition 扁平数值时的保底
    const nearby = typeof monster === 'object' && !('enemyId' in monster) ? monster : null
    return {
      enemyId,
      name: name || enemyId,
      level,
      tier,
      rawRank,
      category,
      element,
      experience,
      skills,
      drops,
      attributes: {
        maxHitpoints: nearby?.maxHitpoints ?? 1,
        maxManapoints: 0,
        physicalAttack: nearby?.physicalAttack ?? 0,
        spellAttack: 0,
        armor: nearby?.armor ?? 0,
        magicResistance: 0,
      },
    }
  }, [monster, encounters, maps])

  // 2. 生命层次徽记与色调
  const { rank, badgeTone } = resolveMonsterRank(resolved.rawRank, isElite, isBoss)
  const rankLabel = rank === 'boss'
    ? t('combat.nearby.rankBoss')
    : rank === 'elite'
      ? t('combat.nearby.rankElite')
      : t('combat.nearby.rankMonster')

  // 3. 境界等阶副标
  const realm = calculateRealmInfo(0, resolved.level)
  const subtitle = t('combat.map.monsterSubtitle', {
    realm: realm.minorName,
    level: resolved.level,
    tier: resolved.tier,
  })

  // 4. 描述文本：优先展示修仙流派与五行（如「体魄型 · 纯阳」）
  const descContent = description !== undefined
    ? description
    : (resolved.category ? getEnemyTypeLabel(resolved.category, resolved.element, t) : null)

  // 5. 明细行构建：出没节奏、击杀修为收益、施展功法名录、掉落物图元胶囊行
  const rewardParts = [
    resolved.experience > 0 ? `${t('combat.report.experience')} +${formatInteger(resolved.experience)}` : null,
  ].filter((part): part is string => part !== null)

  const abilityNames = resolved.skills
    .map(slot => slot.abilityId)
    .filter((id): id is string => Boolean(id))
    .map(id => findAbilityInCatalog(abilities, id)?.name ?? id)
    .filter(name => name.length > 0)

  const lines = [
    ...(intervalHint ? [`${t('combat.nearby.statInterval')}：${intervalHint}`] : []),
    ...(rewardParts.length > 0 ? [`${t('combat.map.monsterReward')}：${rewardParts.join(' · ')}`] : []),
    ...(abilityNames.length > 0 ? [`${t('combat.ecology.abilities')}：${abilityNames.join('、')}`] : []),
    ...(resolved.drops.length > 0
      ? [
          <span key="drops">
            {t('combat.map.monsterDrops')}：
            <span className="world-monster-drops-row">
              {resolved.drops.map(drop => (
                <ItemDropReference drop={drop} key={drop.itemId} />
              ))}
            </span>
          </span>,
        ]
      : []),
  ]

  // 6. 战斗属性网格：标准六维战斗属性（气血/灵力/物攻/术攻/护甲/抗性）
  const attrs = resolved.attributes
  const stats = [
    { label: t('insight.combat.hp'), value: <UnitNumber disableTooltip value={attrs.maxHitpoints} /> },
    { label: t('insight.combat.mp'), value: <UnitNumber disableTooltip value={attrs.maxManapoints} /> },
    { label: t('insight.combat.physAtk'), value: <UnitNumber disableTooltip value={attrs.physicalAttack} /> },
    { label: t('insight.combat.spellAtk'), value: <UnitNumber disableTooltip value={attrs.spellAttack} /> },
    { label: t('insight.combat.armor'), value: <UnitNumber disableTooltip value={attrs.armor} /> },
    { label: t('insight.combat.magicRes'), value: <UnitNumber disableTooltip value={attrs.magicResistance} /> },
  ]

  return (
    <FloatingInfoCard
      badge={rankLabel}
      badgeTone={badgeTone}
      description={descContent}
      footer={footer}
      lines={lines}
      stats={stats}
      subtitle={subtitle}
      title={resolved.name}
    />
  )
})

export interface MonsterTooltipTargetProps extends MonsterDisplayTargetProps {
  /** 触发子元素 */
  children: ReactNode
  /** 浮层弹出方向，默认 top */
  placement?: FloatingPlacement
  /** 是否允许鼠标移入浮层（默认 false，纯 tooltip 体验） */
  interactive?: boolean
  /** 外部样式名 */
  className?: string
  /** 是否使用 CSS display: contents 消除额外外层容器布局影响 */
  displayContents?: boolean
}

/**
 * 通用生灵悬浮提示触发器组件 (MonsterTooltipTarget)
 *
 * 将子元素包裹在 Hover 触发器中，鼠标悬停即弹出全规格统一的生灵详情信息卡。
 */
export const MonsterTooltipTarget = memo(function MonsterTooltipTarget({
  children,
  placement = 'top',
  interactive = false,
  className,
  displayContents = false,
  ...cardProps
}: MonsterTooltipTargetProps): React.JSX.Element {
  return (
    <FloatingPopover
      className={className}
      content={<MonsterInfoCard {...cardProps} />}
      displayContents={displayContents}
      interactive={interactive}
      placement={placement}
      trigger="hover"
    >
      {children}
    </FloatingPopover>
  )
})
