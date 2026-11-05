import { convertTierToLevel, resolveItemSubCategory } from './ids'
import type { MarketInstrument } from './marketStore'

/**
 * 装备标的判定：标的包含 equipmentDefinition、属于 equipment 品类或以 equipment. 开头
 */
export function isEquipmentInstrument(item: MarketInstrument): boolean {
  return Boolean(item.equipmentDefinition || item.category === 'equipment' || item.itemId.startsWith('equipment.'))
}

/**
 * 坊市标的一级品类排序权重（方案 A）：装备 -> 功法/秘籍 -> 消耗品/丹药 -> 材料 -> 其他
 */
export const PRIMARY_CATEGORY_ORDER: Readonly<Record<string, number>> = {
  equipment: 1,
  manual: 2,
  consumable: 3,
  material: 4,
}

/**
 * 解析标的的一级品类键名
 */
export function resolvePrimaryCategoryKey(item: MarketInstrument): string {
  if (isEquipmentInstrument(item)) return 'equipment'
  if (item.category === 'material' || item.definition?.kind === 'material') return 'material'
  if (item.category === 'consumable' || item.definition?.category === 'consumable') return 'consumable'
  if (item.category === 'manual' || item.itemId.startsWith('item.tome.') || item.itemId.startsWith('item.fragment.')) return 'manual'
  return item.category || 'other'
}

/**
 * 坊市标的二级子品类排序权重（同大类下部位/细分类别展开次序）
 */
export function getSubCategoryOrder(primaryKey: string, subKey: string): number {
  switch (primaryKey) {
    case 'equipment': {
      // 装备部位：主手/武器 -> 副手 -> 头部 -> 衣服 -> 裤子 -> 饰品 -> 生活工具
      if (subKey === 'weapon' || subKey === 'two-hand' || subKey === 'mainHandImplement' || subKey === 'twoHandImplement') return 1
      if (subKey === 'off-hand') return 2
      if (subKey === 'head') return 3
      if (subKey === 'armor') return 4
      if (subKey === 'legs') return 5
      if (subKey === 'trinket' || subKey === 'ring' || subKey === 'neck' || subKey === 'talisman') return 6
      if (subKey.startsWith('tool-') || subKey === 'tool') return 7
      return 8
    }
    case 'manual': {
      // 功法形态：整本典籍 -> 残卷
      if (subKey === 'manualTome' || subKey === 'tome') return 1
      if (subKey === 'manualFragment' || subKey === 'fragment') return 2
      return 3
    }
    case 'consumable':
      // 消耗品：丹药 -> 其他消耗品
      return subKey === 'pill' ? 1 : 2
    case 'material': {
      // 材料：灵材/草药 -> 矿石 -> 异材 -> 精材
      if (subKey === 'spiritMaterial' || subKey === 'herb') return 1
      if (subKey === 'mineral') return 2
      if (subKey === 'exoticMaterial') return 3
      if (subKey === 'refinedMaterial') return 4
      return 5
    }
    default:
      return 99
  }
}

/**
 * 解析标的的使用/需求境界等级
 * 优先取显式配置的 level，缺省时由 tier 换算为境界等级（convertTierToLevel）
 */
export function resolveMarketInstrumentLevel(item: MarketInstrument): number {
  const explicitLevel = item.equipmentDefinition?.level ?? item.definition?.level
  if (typeof explicitLevel === 'number' && explicitLevel > 0) {
    return explicitLevel
  }
  return convertTierToLevel(item.tier ?? 1)
}

/**
 * 坊市交易标的多级排序比较函数
 *
 * 排序流水线严格依次为：
 * 1. 品阶 (Tier 升序)：T1 在前，T8 在后，低阶入门到高阶大成
 * 2. 等级 (Level 升序)：同阶按境界需求/使用等级由低到高排列
 * 3. 类型 (Type / Category 升序)：同阶同级按 装备(1) -> 功法(2) -> 消耗品(3) -> 材料(4) 归集，并在同大类下按子品类次序排序
 * 4. 名称 (Name 拼音升序)：前三级完全相同时按中文拼音升序，最终以 itemId 兜底保障跨平台确定性
 */
export function compareMarketInstruments(left: MarketInstrument, right: MarketInstrument): number {
  // 1. 品阶梯队（Tier 升序）：由浅入深，保证 T1 在前、T2 在后
  const tierDiff = (left.tier ?? 1) - (right.tier ?? 1)
  if (tierDiff !== 0) {
    return tierDiff
  }

  // 2. 等级梯度（Level 升序）：同阶按境界需求/使用等级由低到高排列
  const levelDiff = resolveMarketInstrumentLevel(left) - resolveMarketInstrumentLevel(right)
  if (levelDiff !== 0) {
    return levelDiff
  }

  // 3. 类型梯队：同品阶同等级下，按 装备 -> 功法 -> 消耗品 -> 材料 大类优先级
  const leftPrimary = resolvePrimaryCategoryKey(left)
  const rightPrimary = resolvePrimaryCategoryKey(right)
  const leftPrimaryOrder = PRIMARY_CATEGORY_ORDER[leftPrimary] ?? 99
  const rightPrimaryOrder = PRIMARY_CATEGORY_ORDER[rightPrimary] ?? 99
  if (leftPrimaryOrder !== rightPrimaryOrder) {
    return leftPrimaryOrder - rightPrimaryOrder
  }

  // 同大类下的二级子品类权重（部位/类别细分）
  const leftSubKey = resolveItemSubCategory(left.itemId, left.definition, left.equipmentDefinition, left.subCategory || left.category) ?? ''
  const rightSubKey = resolveItemSubCategory(right.itemId, right.definition, right.equipmentDefinition, right.subCategory || right.category) ?? ''
  const leftSubOrder = getSubCategoryOrder(leftPrimary, leftSubKey)
  const rightSubOrder = getSubCategoryOrder(rightPrimary, rightSubKey)
  if (leftSubOrder !== rightSubOrder) {
    return leftSubOrder - rightSubOrder
  }

  // 4. 名称与 ID 稳定兜底（同阶、同级、同类型下按名称拼音升序，最终以 ID 保证跨平台确定性）
  return left.name.localeCompare(right.name, 'zh-CN') || left.itemId.localeCompare(right.itemId)
}
