import type { BuildingModifiers, BuildingDefinition } from './types'
import type { PillTone } from '../components/PillLabel'

/** 建筑修饰项元数据定义 */
export interface BuildingBonusDescriptor {
  key: keyof BuildingModifiers
  labelKey: string
  defaultLabel: string
  tone: PillTone
  isRatio: boolean
  unit?: string
}

/** 建筑修饰项配置表 */
export const BUILDING_BONUS_DESCRIPTORS: readonly BuildingBonusDescriptor[] = [
  {
    key: 'cultivationOutputRatio',
    labelKey: 'combat.nearby.attrCultivationOutput',
    defaultLabel: '吐纳修炼收益',
    tone: 'cyan',
    isRatio: true,
  },
  {
    key: 'gatheringSpeedRatio',
    labelKey: 'combat.nearby.attrGatheringSpeed',
    defaultLabel: '采集采伐速度',
    tone: 'emerald',
    isRatio: true,
  },
  {
    key: 'miningSpeedRatio',
    labelKey: 'combat.nearby.attrMiningSpeed',
    defaultLabel: '采矿挖掘速度',
    tone: 'bronze',
    isRatio: true,
  },
  {
    key: 'alchemySpeedRatio',
    labelKey: 'combat.nearby.attrAlchemySpeed',
    defaultLabel: '炼丹行动速度',
    tone: 'amber',
    isRatio: true,
  },
  {
    key: 'forgingSpeedRatio',
    labelKey: 'combat.nearby.attrForgingSpeed',
    defaultLabel: '炼器精炼速度',
    tone: 'gold',
    isRatio: true,
  },
  {
    key: 'talismanSpeedRatio',
    labelKey: 'combat.nearby.attrTalismanSpeed',
    defaultLabel: '符箓绘制速度',
    tone: 'purple',
    isRatio: true,
  },
  {
    key: 'formationSpeedRatio',
    labelKey: 'combat.nearby.attrFormationSpeed',
    defaultLabel: '阵盘研制速度',
    tone: 'sky',
    isRatio: true,
  },
  {
    key: 'enhancementSpeedRatio',
    labelKey: 'combat.nearby.attrEnhancementSpeed',
    defaultLabel: '装备强化速度',
    tone: 'crimson',
    isRatio: true,
  },
  {
    key: 'monsterWeakenRatio',
    labelKey: 'combat.nearby.attrMonsterWeaken',
    defaultLabel: '此地怪物削弱',
    tone: 'crimson',
    isRatio: true,
  },
  {
    key: 'combatHitpointRegen',
    labelKey: 'combat.nearby.attrCombatHpRegen',
    defaultLabel: '战斗气血回复',
    tone: 'emerald',
    isRatio: false,
    unit: '/5秒',
  },
  {
    key: 'combatManapointRegen',
    labelKey: 'combat.nearby.attrCombatMpRegen',
    defaultLabel: '战斗灵力回复',
    tone: 'sky',
    isRatio: false,
    unit: '/5秒',
  },
  {
    key: 'explorationSpeedRatio',
    labelKey: 'combat.nearby.attrExplorationSpeed',
    defaultLabel: '历练采集速度',
    tone: 'sky',
    isRatio: true,
  },
  {
    key: 'explorationOutputRatio',
    labelKey: 'combat.nearby.attrExplorationOutput',
    defaultLabel: '历练采集收益',
    tone: 'emerald',
    isRatio: true,
  },
  {
    key: 'alchemyOutputRatio',
    labelKey: 'combat.nearby.attrAlchemyOutput',
    defaultLabel: '炼丹炼器收益',
    tone: 'gold',
    isRatio: true,
  },
  {
    key: 'combatDamageReductionRatio',
    labelKey: 'combat.nearby.attrCombatDamageReduction',
    defaultLabel: '战斗伤害减免',
    tone: 'bronze',
    isRatio: true,
  },
]

/** 单项生效加成展示数据 */
export interface BuildingBonusItem {
  key: keyof BuildingModifiers
  label: string
  tone: PillTone
  valueText: string
  numericValue: number
}

/** 升级对比展示行数据（参考境界突破） */
export interface BuildingUpgradeComparisonRow {
  key: keyof BuildingModifiers
  label: string
  tone: PillTone
  before: string
  after: string
  delta: string
}

/**
 * 格式化数值为干净的文本（整数不带小数，小数保留最多 1 位）
 */
function formatCleanNumber(num: number): string {
  const rounded = Math.round(num * 10) / 10
  return Math.abs(rounded - Math.round(rounded)) < 0.001
    ? String(Math.round(rounded))
    : rounded.toFixed(1)
}

/**
 * 格式化单项加成数值
 */
export function formatBuildingModifierValue(
  descriptor: BuildingBonusDescriptor,
  rawValue: number,
  isDelta = false
): string {
  if (descriptor.isRatio) {
    const percent = rawValue * 100
    if (Math.abs(percent) < 0.0001) return '0%'
    const clean = formatCleanNumber(Math.abs(percent))
    if (descriptor.key === 'monsterWeakenRatio') {
      return `-${clean}%`
    }
    return `+${clean}%`
  }

  if (Math.abs(rawValue) < 0.0001) return isDelta ? '+0' : '0'
  const clean = formatCleanNumber(Math.abs(rawValue))
  const sign = rawValue >= 0 ? '+' : '-'
  return `${sign}${clean}${descriptor.unit ?? ''}`
}

/**
 * 解析建筑在指定等级下的单项修饰值（支持 baseModifiers 与 growthModifiers 分级）
 */
export function resolveModifierValueAtLevel(
  baseModifiers: BuildingModifiers | undefined,
  growthModifiers: BuildingModifiers | undefined,
  key: keyof BuildingModifiers,
  level: number
): number {
  if (level <= 0) return 0
  const base = baseModifiers?.[key] ?? 0
  if (level === 1) return base
  const growth = growthModifiers?.[key] ?? base
  return base + growth * (level - 1)
}

/**
 * 从 BuildingDefinition 或 BuildingModifiers 中提取 base 与 growth 修饰项
 */
function extractModifiers(
  definitionOrModifiers: BuildingDefinition | BuildingModifiers | undefined
): { baseModifiers?: BuildingModifiers; growthModifiers?: BuildingModifiers } {
  if (!definitionOrModifiers) return {}
  if ('buildingId' in definitionOrModifiers) {
    const baseModifiers = definitionOrModifiers.baseModifiers ?? definitionOrModifiers.modifiers
    const growthModifiers = definitionOrModifiers.growthModifiers ?? baseModifiers
    return { baseModifiers, growthModifiers }
  }
  return { baseModifiers: definitionOrModifiers, growthModifiers: definitionOrModifiers }
}

/**
 * 解析建筑在指定等级下的实际有效加成项列表
 */
export function getBuildingBonusItems(
  definitionOrModifiers: BuildingDefinition | BuildingModifiers | undefined,
  level: number,
  resolveLabel?: (key: string, defaultText: string) => string
): BuildingBonusItem[] {
  if (!definitionOrModifiers) return []
  const effectiveLevel = Math.max(0, level)
  const { baseModifiers, growthModifiers } = extractModifiers(definitionOrModifiers)

  const items: BuildingBonusItem[] = []
  for (const descriptor of BUILDING_BONUS_DESCRIPTORS) {
    const baseVal = baseModifiers?.[descriptor.key] ?? 0
    if (baseVal <= 0) continue

    const actualValue = resolveModifierValueAtLevel(baseModifiers, growthModifiers, descriptor.key, effectiveLevel)
    const label = resolveLabel ? resolveLabel(descriptor.labelKey, descriptor.defaultLabel) : descriptor.defaultLabel
    const valueText = effectiveLevel > 0
      ? formatBuildingModifierValue(descriptor, actualValue)
      : '0%'

    items.push({
      key: descriptor.key,
      label,
      tone: descriptor.tone,
      valueText,
      numericValue: actualValue,
    })
  }

  return items
}

/**
 * 计算建筑从当前等级跃迁到目标等级的加成对比列表（参考境界突破）
 */
export function getBuildingUpgradeComparison(
  definitionOrModifiers: BuildingDefinition | BuildingModifiers | undefined,
  currentLevel: number,
  targetLevel: number,
  resolveLabel?: (key: string, defaultText: string) => string
): BuildingUpgradeComparisonRow[] {
  if (!definitionOrModifiers) return []
  const fromLevel = Math.max(0, currentLevel)
  const toLevel = Math.max(fromLevel + 1, targetLevel)

  const { baseModifiers, growthModifiers } = extractModifiers(definitionOrModifiers)

  const rows: BuildingUpgradeComparisonRow[] = []
  for (const descriptor of BUILDING_BONUS_DESCRIPTORS) {
    const baseVal = baseModifiers?.[descriptor.key] ?? 0
    if (baseVal <= 0) continue

    const beforeValue = resolveModifierValueAtLevel(baseModifiers, growthModifiers, descriptor.key, fromLevel)
    const afterValue = resolveModifierValueAtLevel(baseModifiers, growthModifiers, descriptor.key, toLevel)
    const deltaValue = afterValue - beforeValue

    const label = resolveLabel ? resolveLabel(descriptor.labelKey, descriptor.defaultLabel) : descriptor.defaultLabel
    const before = fromLevel > 0 ? formatBuildingModifierValue(descriptor, beforeValue) : (descriptor.isRatio ? '0%' : '0')
    const after = formatBuildingModifierValue(descriptor, afterValue)
    const delta = formatBuildingModifierValue(descriptor, deltaValue, true)

    rows.push({
      key: descriptor.key,
      label,
      tone: descriptor.tone,
      before,
      after,
      delta,
    })
  }

  return rows
}

/**
 * 列表卡片直显的主加成摘要文案
 */
export function getBuildingSummaryBonusText(
  definitionOrModifiers: BuildingDefinition | BuildingModifiers | undefined,
  level: number,
  resolveLabel?: (key: string, defaultText: string) => string
): string {
  if (!definitionOrModifiers) return ''
  const effectiveLevel = Math.max(0, level)
  const targetLevel = effectiveLevel > 0 ? effectiveLevel : 1
  const { baseModifiers, growthModifiers } = extractModifiers(definitionOrModifiers)

  const bonuses: string[] = []
  for (const descriptor of BUILDING_BONUS_DESCRIPTORS) {
    const baseVal = baseModifiers?.[descriptor.key] ?? 0
    if (baseVal <= 0) continue

    const val = resolveModifierValueAtLevel(baseModifiers, growthModifiers, descriptor.key, targetLevel)
    const label = resolveLabel ? resolveLabel(descriptor.labelKey, descriptor.defaultLabel) : descriptor.defaultLabel
    const formatted = formatBuildingModifierValue(descriptor, val)
    bonuses.push(`${label} ${formatted}`)
  }

  if (bonuses.length === 0) return ''
  const joined = bonuses.join(' · ')
  if (effectiveLevel <= 0) {
    return `建成生效: ${joined}`
  }
  return joined
}
