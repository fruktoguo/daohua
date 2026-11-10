import type { BuildingModifiers, BuildingDefinition, MapNodeBuilding, NodeBuildingEffectSet } from './types'
import type { PillTone } from '../components/PillLabel'

/** 建筑修饰项元数据定义 */
export interface BuildingBonusDescriptor {
  key: keyof BuildingModifiers
  labelKey: string
  defaultLabel: string
  tone: PillTone
  /** 属性极简二值小图标 ID（`frontend/src/assets/glyphs/` 下的资源名） */
  glyph: string
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
    glyph: 'bio-cultivation',
    isRatio: true,
  },
  {
    key: 'gatheringSpeedRatio',
    labelKey: 'combat.nearby.attrGatheringSpeed',
    defaultLabel: '采集采伐速度',
    tone: 'emerald',
    glyph: 'gathering',
    isRatio: true,
  },
  {
    key: 'miningSpeedRatio',
    labelKey: 'combat.nearby.attrMiningSpeed',
    defaultLabel: '采矿挖掘速度',
    tone: 'bronze',
    glyph: 'mining',
    isRatio: true,
  },
  {
    key: 'alchemySpeedRatio',
    labelKey: 'combat.nearby.attrAlchemySpeed',
    defaultLabel: '炼丹行动速度',
    tone: 'amber',
    glyph: 'alchemy',
    isRatio: true,
  },
  {
    key: 'forgingSpeedRatio',
    labelKey: 'combat.nearby.attrForgingSpeed',
    defaultLabel: '炼器精炼速度',
    tone: 'gold',
    glyph: 'forging',
    isRatio: true,
  },
  {
    key: 'talismanSpeedRatio',
    labelKey: 'combat.nearby.attrTalismanSpeed',
    defaultLabel: '符箓绘制速度',
    tone: 'purple',
    glyph: 'talisman',
    isRatio: true,
  },
  {
    key: 'formationSpeedRatio',
    labelKey: 'combat.nearby.attrFormationSpeed',
    defaultLabel: '阵盘研制速度',
    tone: 'sky',
    glyph: 'formation',
    isRatio: true,
  },
  {
    key: 'enhancementSpeedRatio',
    labelKey: 'combat.nearby.attrEnhancementSpeed',
    defaultLabel: '装备强化速度',
    tone: 'crimson',
    glyph: 'enhancement',
    isRatio: true,
  },
  {
    key: 'monsterWeakenRatio',
    labelKey: 'combat.nearby.attrMonsterWeaken',
    defaultLabel: '此地怪物削弱',
    tone: 'crimson',
    glyph: 'bio-monsters',
    isRatio: true,
  },
  {
    key: 'combatHitpointRegen',
    labelKey: 'combat.nearby.attrCombatHpRegen',
    defaultLabel: '战斗气血回复',
    tone: 'emerald',
    glyph: 'hp-regen',
    isRatio: false,
    unit: '/5秒',
  },
  {
    key: 'combatManapointRegen',
    labelKey: 'combat.nearby.attrCombatMpRegen',
    defaultLabel: '战斗灵力回复',
    tone: 'sky',
    glyph: 'mp-regen',
    isRatio: false,
    unit: '/5秒',
  },
  {
    key: 'explorationSpeedRatio',
    labelKey: 'combat.nearby.attrExplorationSpeed',
    defaultLabel: '历练采集速度',
    tone: 'sky',
    glyph: 'action-speed',
    isRatio: true,
  },
  {
    key: 'explorationOutputRatio',
    labelKey: 'combat.nearby.attrExplorationOutput',
    defaultLabel: '历练采集收益',
    tone: 'emerald',
    glyph: 'combat-exp',
    isRatio: true,
  },
  {
    key: 'alchemyOutputRatio',
    labelKey: 'combat.nearby.attrAlchemyOutput',
    defaultLabel: '炼丹炼器收益',
    tone: 'gold',
    glyph: 'alchemy',
    isRatio: true,
  },
  {
    key: 'combatDamageReductionRatio',
    labelKey: 'combat.nearby.attrCombatDamageReduction',
    defaultLabel: '战斗伤害减免',
    tone: 'bronze',
    glyph: 'total-damage',
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

/**
 * 单项加成公式展示数据（1 级基准值 + 每级线性增幅）。
 *
 * 配表模型为 `Lv N 生效 = Base + Growth × (N - 1)`（总纲 §9.4），
 * 因此图鉴等无等级上下文的位置必须同时给出基准值与每级增幅，缺一即误导。
 */
export interface BuildingBonusFormula {
  key: keyof BuildingModifiers
  label: string
  tone: PillTone
  /** 属性极简二值小图标 ID */
  glyph: string
  /** 1 级基准值文本（如 `+12%`） */
  baseText: string
  /** 每级增幅文本（如 `+2%`）；无增幅时为空串 */
  growthText: string
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
 * 解析建筑的加成公式项列表（1 级基准 + 每级增幅）。
 *
 * 图鉴没有当前等级上下文，只展示 `modifiers` 的静态基准会让玩家误以为加成恒定不变；
 * 此函数按配表真实成长模型同时给出基准值与每级增量，供图鉴与列表直接渲染。
 */
export function getBuildingBonusFormulas(
  definitionOrModifiers: BuildingDefinition | BuildingModifiers | undefined,
  resolveLabel?: (key: string, defaultText: string) => string
): BuildingBonusFormula[] {
  if (!definitionOrModifiers) return []
  const { baseModifiers, growthModifiers } = extractModifiers(definitionOrModifiers)

  const formulas: BuildingBonusFormula[] = []
  for (const descriptor of BUILDING_BONUS_DESCRIPTORS) {
    const baseVal = baseModifiers?.[descriptor.key] ?? 0
    if (baseVal <= 0) continue

    const growthVal = growthModifiers?.[descriptor.key] ?? baseVal
    const label = resolveLabel ? resolveLabel(descriptor.labelKey, descriptor.defaultLabel) : descriptor.defaultLabel
    formulas.push({
      key: descriptor.key,
      label,
      tone: descriptor.tone,
      glyph: descriptor.glyph,
      baseText: formatBuildingModifierValue(descriptor, baseVal),
      growthText: Math.abs(growthVal) < 0.0001 ? '' : formatBuildingModifierValue(descriptor, growthVal, true),
    })
  }

  return formulas
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

/** 与建筑效果相关的战斗类键（其余键只影响非战斗行动，模拟器无需携带） */
const COMBAT_EFFECT_KEYS: readonly (keyof BuildingModifiers)[] = [
  'combatHitpointRegen',
  'combatManapointRegen',
  'combatDamageReductionRatio',
  'monsterWeakenRatio',
]

/**
 * 汇总节点当前建筑效果（对齐服务端 `NodeBuildingRules.AggregateEffects` 的单座效果口径）。
 *
 * 效果 = `Lv N 生效 = Base + Growth × (N - 1)`，多座建筑逐项求和；只计入已建成等级（level > 0）。
 * 服务端另按「各行动类别正在使用人数」施加拥挤效率衰减，而全图节点建筑索引
 * （`/api/v1/world/node-buildings`）不含使用人数，故此处为无拥挤上限口径：
 * 对当前节点应优先直接使用快照下发的权威 `world.buildingEffects`，本函数只用于推演其它地点。
 */
export function aggregateNodeBuildingEffects(
  buildings: readonly MapNodeBuilding[] | null | undefined,
  definitions: readonly BuildingDefinition[]
): NodeBuildingEffectSet {
  const total: NodeBuildingEffectSet = {
    explorationSpeedRatio: 0,
    explorationOutputRatio: 0,
    alchemySpeedRatio: 0,
    alchemyOutputRatio: 0,
    cultivationOutputRatio: 0,
    combatHitpointRegen: 0,
    combatManapointRegen: 0,
    combatDamageReductionRatio: 0,
    monsterWeakenRatio: 0,
  }
  if (!buildings || buildings.length === 0) return total

  const byId = new Map<string, BuildingDefinition>()
  for (const definition of definitions) byId.set(definition.buildingId, definition)

  for (const building of buildings) {
    const level = Math.floor(building.level)
    if (!Number.isFinite(level) || level <= 0) continue
    const definition = byId.get(building.buildingId)
    if (!definition) continue
    const modifiers: BuildingModifiers | undefined = definition.baseModifiers ?? definition.modifiers
    const growth: BuildingModifiers | undefined = definition.growthModifiers ?? modifiers
    for (const key of COMBAT_EFFECT_KEYS) {
      if ((modifiers?.[key] ?? 0) <= 0) continue
      const value = resolveModifierValueAtLevel(modifiers, growth, key, level)
      if (!Number.isFinite(value) || value === 0) continue
      total[key] = (total[key] ?? 0) + value
    }
  }
  return total
}

/**
 * 按节点建筑削弱比率折减怪物面板（对齐服务端 `NodeBuildingRules.WeakenMonsterStats`）：
 * 双重曲阻尼 `1 / (1 + ratio)` 衰减气血上限与双攻双防，灵力上限不受影响。
 */
export function weakenMonsterAttributes<T extends { maxHitpoints?: number; physicalAttack?: number; spellAttack?: number; armor?: number; magicResistance?: number }>(
  attributes: T,
  weakenRatio: number
): T {
  if (!Number.isFinite(weakenRatio) || weakenRatio <= 0) return attributes
  const factor = 1 / (1 + Math.max(0, weakenRatio))
  return {
    ...attributes,
    maxHitpoints: Math.ceil((attributes.maxHitpoints ?? 1) * factor),
    physicalAttack: Math.ceil((attributes.physicalAttack ?? 0) * factor),
    spellAttack: Math.ceil((attributes.spellAttack ?? 0) * factor),
    armor: Math.ceil((attributes.armor ?? 0) * factor),
    magicResistance: Math.ceil((attributes.magicResistance ?? 0) * factor),
  }
}

/** 装备配表比率/固定值键到前端面板字段的映射（服务端 PlayerAttributeResolver 同名键口径） */
const EQUIP_RATIO_KEY_MAP: Readonly<Record<string, keyof import('./types').CombatAttributes>> = {
  maxHitpoints: 'maxHitpoints',
  maxManapoints: 'maxManapoints',
  physicalAttack: 'physicalAttack',
  spellAttack: 'spellAttack',
  physicalArmor: 'armor',
  spellResistance: 'magicResistance',
  accuracy: 'accuracy',
  evasion: 'evasion',
  criticalChance: 'critical',
  criticalAvoidance: 'criticalResistance',
  break: 'breakPower',
  parry: 'parryResolve',
  hitpointRegen: 'hpRegenPerFiveSeconds',
  manapointRegen: 'mpRegenPerFiveSeconds',
}

/**
 * 计算一套装备在给定强化等级下的面板增量（供模拟器「换装推演」使用）。
 *
 * 口径对齐服务端：强化总倍率 = `1.05^等级`（总纲 §4.2）；`*Points` 结尾键为百分比点数通道，
 * 其余键为固定值；比率段（baseStatRatios）按同名键并入，未映射的键忽略。
 * 注意：本函数只服务于「在已解析终值面板之上做换装增量」的场景，
 * 真实战斗终值仍以服务端 `PlayerAttributeResolver` 下发为准。
 */
export function resolveEquipmentAttributeDelta(
  equipment: readonly { itemId: string; enhancementLevel?: number }[] | null | undefined,
  definitions: readonly { equipmentId: string; flatStats?: Record<string, number>; baseStatRatios?: Record<string, number> }[]
): Partial<Record<keyof import('./types').CombatAttributes, number>> {
  const delta: Partial<Record<keyof import('./types').CombatAttributes, number>> = {}
  if (!equipment || equipment.length === 0) return delta
  const byId = new Map(definitions.map(definition => [definition.equipmentId, definition]))
  const add = (key: keyof import('./types').CombatAttributes, value: number) => {
    if (!Number.isFinite(value) || value === 0) return
    delta[key] = (delta[key] ?? 0) + value
  }

  for (const item of equipment) {
    const definition = byId.get(item.itemId)
    if (!definition) continue
    const level = Math.max(0, Math.floor(item.enhancementLevel ?? 0))
    const scale = Math.pow(1.05, level)
    for (const [key, raw] of Object.entries(definition.flatStats ?? {})) {
      if (typeof raw !== 'number' || !Number.isFinite(raw)) continue
      const target = EQUIP_RATIO_KEY_MAP[key] ?? (key.endsWith('Points') ? (key as keyof import('./types').CombatAttributes) : null)
      if (target === null) continue
      add(target, raw * scale)
    }
    for (const [key, raw] of Object.entries(definition.baseStatRatios ?? {})) {
      if (typeof raw !== 'number' || !Number.isFinite(raw)) continue
      const target = EQUIP_RATIO_KEY_MAP[key]
      if (target === undefined) continue
      // 比率段无基础值可乘，模拟器以「按当前面板值放大」近似：以 1 点等价计入，
      // 避免静默丢弃（真实乘区由服务端结算）。
      add(target, raw * scale)
    }
  }
  return delta
}
