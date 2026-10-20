import { findAbilityInCatalog, resolveItemSubCategory } from '../../game/ids'
import type { CombatAbilityDefinition } from '../../game/types'
import type {
  ConsumableKindFilter,
  EquipmentKindFilter,
  EquipmentSlotFilter,
  ManualCategoryFilter,
  ManualDisciplineFilter,
  ManualFormFilter,
  MarketInstrument,
  MaterialRarityFilter,
} from '../../game/marketStore'

/**
 * 坊市多维筛选判定口径（纯函数，不依赖 React 与 store 运行时）。
 *
 * 所有维度遵循同一约定：过滤值为 'any' 时不做过滤，其余取值互斥；
 * 多维之间统一 AND 联合，品阶为跨品类全局维度。
 */

/** 武器槽位（含双手与主手法器变体） */
const WEAPON_SLOTS: Record<string, true> = { weapon: true, 'two-hand': true, mainHandImplement: true, twoHandImplement: true }
/** 饰品槽位（指环/项链/护符统一归饰品） */
const TRINKET_SLOTS: Record<string, true> = { trinket: true, ring: true, neck: true, talisman: true }

export function isMaterial(item: MarketInstrument): boolean {
  // 品类以主分类为准：觉醒石等 `kind: material` 但主分类为 consumable 的功能道具不得跨入材料页
  return item.category === 'material'
}

export function isEquipment(item: MarketInstrument): boolean {
  return (
    item.category === 'equipment' ||
    !!item.equipmentDefinition ||
    item.itemId.startsWith('equipment.')
  )
}

export function isConsumable(item: MarketInstrument): boolean {
  return item.category === 'consumable' || item.definition?.category === 'consumable'
}

export function isManual(item: MarketInstrument): boolean {
  return (
    item.category === 'manual' ||
    item.itemId.startsWith('item.tome.') ||
    item.itemId.startsWith('item.fragment.')
  )
}

/** 标的品阶归一：缺省/非法值兜底一阶，与 `withItemGrade` 的 tier 归一一致 */
export function resolveInstrumentTier(item: MarketInstrument): number {
  const tier = Math.floor(item.tier ?? 0)
  return Number.isFinite(tier) && tier > 0 ? Math.min(8, tier) : 1
}

/** 品阶过滤：`grade` 为 0 表示任意 */
export function matchesGrade(item: MarketInstrument, grade: number): boolean {
  return grade <= 0 || resolveInstrumentTier(item) === grade
}

/** 功法过滤：门类 / 伤害大类 / 载体形式三维 AND */
export function matchesManual(
  item: MarketInstrument,
  abilities: readonly CombatAbilityDefinition[],
  discipline: ManualDisciplineFilter,
  category: ManualCategoryFilter,
  form: ManualFormFilter,
): boolean {
  if (!isManual(item)) return false

  if (form !== 'any') {
    const isTome = item.itemId.startsWith('item.tome.')
    const isFragment = item.itemId.startsWith('item.fragment.')
    const itemForm = isTome
      ? 'tome'
      : isFragment
        ? 'fragment'
        : (item.subCategory === 'manualTome' ? 'tome' : 'fragment')
    if (itemForm !== form) return false
  }

  if (discipline === 'any' && category === 'any') return true

  const slug = item.itemId
    .replace(/^item\.tome\./, '')
    .replace(/^item\.fragment\./, '')
  const ability = findAbilityInCatalog(abilities, `ability.${slug}`)

  if (discipline !== 'any') {
    const itemDiscipline: ManualDisciplineFilter = ability?.discipline === 'internal' ? 'internal' : 'spell'
    if (itemDiscipline !== discipline) return false
  }

  if (category !== 'any') {
    const itemCategory: ManualCategoryFilter = ability?.category === 'physical' ? 'physical' : 'spell'
    if (itemCategory !== category) return false
  }

  return true
}

/**
 * 装备战斗类型解析：比例段攻击键为主、固定值段兜底，均无攻击键归防御
 * （护甲、减伤、回复与辅助型装备）。
 */
export function resolveEquipmentKind(item: MarketInstrument): Exclude<EquipmentKindFilter, 'any'> {
  const ratios = item.equipmentDefinition?.baseStatRatios ?? {}
  if ((ratios.spellAttack ?? 0) > 0) return 'spell'
  if ((ratios.physicalAttack ?? 0) > 0) return 'physical'

  const flat = item.equipmentDefinition?.flatStats ?? {}
  if ((flat.spellAttack ?? 0) > 0) return 'spell'
  if ((flat.physicalAttack ?? 0) > 0) return 'physical'

  return 'defense'
}

/** 装备部位归一：槽位原始键映射到筛选部位 */
export function resolveEquipmentSlot(item: MarketInstrument): Exclude<EquipmentSlotFilter, 'any'> | null {
  const subKey = resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.subCategory)
  if (!subKey) return null
  if (subKey.startsWith('tool-')) return 'tool'
  if (WEAPON_SLOTS[subKey]) return 'weapon'
  if (subKey === 'off-hand') return 'offHand'
  if (subKey === 'head') return 'head'
  if (subKey === 'armor') return 'armor'
  if (subKey === 'legs') return 'legs'
  if (TRINKET_SLOTS[subKey]) return 'trinket'
  return null
}

/** 装备过滤：部位与战斗类型 AND；工具无战斗类型，仅按部位过滤且不参与类型筛选 */
export function matchesEquipment(
  item: MarketInstrument,
  kind: EquipmentKindFilter,
  slot: EquipmentSlotFilter,
): boolean {
  if (!isEquipment(item)) return false
  const itemSlot = resolveEquipmentSlot(item)
  if (slot !== 'any' && itemSlot !== slot) return false
  if (slot === 'tool') return true
  if (kind === 'any') return true
  // 工具对战斗属性零贡献，选中任一战斗类型时一律排除，避免归入防御的误导
  if (itemSlot === 'tool') return false
  return resolveEquipmentKind(item) === kind
}

/**
 * 消耗品三分：丹药类（pill，含修炼辅助丹）为战斗药品；其余声明战斗栏域的为战斗通用；
 * 探索/炼丹/炼器等生活技艺栏域与无栏域声明的功能道具归技艺。
 */
export function resolveConsumableKind(item: MarketInstrument): Exclude<ConsumableKindFilter, 'any'> {
  const subKey = item.definition?.subCategory ?? item.subCategory
  if (subKey === 'pill') return 'combatPotion'
  const usableBars = item.definition?.usableBars ?? []
  if (usableBars.includes('combat')) return 'combatGeneral'
  return 'technique'
}

export function matchesConsumable(item: MarketInstrument, kind: ConsumableKindFilter): boolean {
  if (!isConsumable(item)) return false
  if (kind === 'any') return true
  return resolveConsumableKind(item) === kind
}

/** 材料稀有度：二阶及以上为稀有，一阶与未配置 tier 的材料为普通 */
export function matchesMaterialRarity(item: MarketInstrument, rarity: MaterialRarityFilter): boolean {
  if (!isMaterial(item)) return false
  if (rarity === 'any') return true
  const isRare = resolveInstrumentTier(item) >= 2
  return rarity === 'rare' ? isRare : !isRare
}
