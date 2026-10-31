import { create } from 'zustand'
import { resolveItemCategoryLabel, resolveItemMetadata, resolveItemSubCategory, withItemGrade, type ItemMetadata } from './ids'
import { getT, type TFunction } from '../i18n'
import type { CombatEquipmentDefinition, ItemDefinition } from './types'

/** 坊市一级主分类 */
export type MarketPrimaryCategory = 'all' | 'material' | 'equipment' | 'consumable' | 'manual'

/** 坊市顶层 Tab */
export type MarketTab = 'browse' | 'orders'

/** 功法门类过滤：任意 (any) | 术法 (spell) | 内功 (internal) */
export type ManualDisciplineFilter = 'any' | 'spell' | 'internal'

/** 功法伤害大类过滤：任意 (any) | 物理 (physical) | 法术 (spell) */
export type ManualCategoryFilter = 'any' | 'physical' | 'spell'

/** 功法载体形式过滤：任意 (any) | 全本 (tome) | 残卷 (fragment) */
export type ManualFormFilter = 'any' | 'tome' | 'fragment'

/** 装备战斗类型过滤：任意 (any) | 物理 (physical) | 法术 (spell) | 防御 (defense) */
export type EquipmentKindFilter = 'any' | 'physical' | 'spell' | 'defense'

/** 装备部位过滤：任意 (any) | 六部位 + 工具 (tool) */
export type EquipmentSlotFilter =
  | 'any' | 'weapon' | 'offHand' | 'head' | 'armor' | 'legs' | 'trinket' | 'tool'

/** 消耗品分类过滤：任意 (any) | 战斗药品 | 战斗通用 | 技艺 */
export type ConsumableKindFilter = 'any' | 'combatPotion' | 'combatGeneral' | 'technique'

/** 材料稀有度过滤：任意 (any) | 普通 (common) | 稀有 (rare) */
export type MaterialRarityFilter = 'any' | 'common' | 'rare'


/** 坊市标的网格卡片右下角数量显示模式：'owned' (自身拥有，默认) | 'onSale' (坊市在售) */
export type MarketAmountDisplayMode = 'owned' | 'onSale'
/**
 * 坊市交易标的元数据定义
 */
export interface MarketInstrument {
  /** 物品唯一 ID */
  itemId: string
  /** 物品名称 */
  name: string
  /** 物品品类 */
  category: string
  /** 子品类 (可选，如装备 slot 或材料细分类别) */
  subCategory?: string
  /** 物品等阶 */
  tier: number
  /** 系统商店回收底价 (Vendor Price) */
  sellPrice: number
  /** 服务端原始物品定义 (道具/材料) */
  definition?: ItemDefinition
  /** 服务端原始装备定义 (装备) */
  equipmentDefinition?: CombatEquipmentDefinition
}

/**
 * 解析坊市交易标的对应的物品元数据 (ItemMetadata)
 *
 * 统一收拢品类细分、等阶归一化 (withItemGrade) 与等级换算 (convertTierToLevel)，
 * 避免各处分散兜底导致等阶/等级口径不一致（如误把 tier 当成 level 显示）。
 */
export function resolveMarketInstrumentMetadata(item: MarketInstrument, t?: TFunction): ItemMetadata {
  const translator = t ?? getT()
  const subCategoryLabel = resolveItemCategoryLabel(
    resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.subCategory || item.category),
    translator,
  )
  const tier = Math.max(1, item.tier)

  if (item.equipmentDefinition || item.category === 'equipment' || item.itemId.startsWith('equipment.')) {
    return withItemGrade({
      label: item.name,
      glyph: '⚔️',
      tone: 'gold',
      category: subCategoryLabel,
      rarity: 'mundane',
      rarityLabel: '',
      description: item.equipmentDefinition?.description || (item.equipmentDefinition ? '' : translator('market.itemFallback.description')),
      source: '',
      usage: '',
    }, tier)
  }

  const registered = resolveItemMetadata(item.itemId)
  if (!registered) {
    return withItemGrade({
      label: item.name,
      glyph: item.name.slice(0, 1),
      tone: 'gold',
      category: subCategoryLabel,
      rarity: 'mundane',
      rarityLabel: '',
      description: item.definition?.description || translator('market.itemFallback.description'),
      source: '',
      usage: '',
    }, tier)
  }

  return withItemGrade({
    ...registered,
    category: subCategoryLabel,
  }, item.tier > 0 ? item.tier : registered.tier ?? 1)
}

/** 坊市 UI 状态接口 */
export interface MarketUiState {
  /** 当前选中的一级分类 */
  primaryTab: MarketPrimaryCategory
  /** 当前选中的二级子分类 */
  subCategory: string
  /** 当前输入的检索文本 */
  query: string
  /** 标的网格列表滚动高度 */
  scrollTop: number
  /** 当前选中的标的 ID (null 表示处于列表浏览模式) */
  selectedItemId: string | null
  /** 坊市顶层当前激活的 Tab ('browse' | 'orders') */
  activeTab: MarketTab
  /** 功法门类过滤（任意 | 术法 | 内功） */
  manualDiscipline: ManualDisciplineFilter
  /** 功法伤害大类过滤（任意 | 物理 | 法术） */
  manualCategory: ManualCategoryFilter
  /** 功法载体形式过滤（任意 | 全本 | 残卷） */
  manualForm: ManualFormFilter
  /** 装备战斗类型过滤（任意 | 物理 | 法术 | 防御） */
  equipmentKind: EquipmentKindFilter
  /** 装备部位过滤（任意 | 六部位 | 工具），选中工具时类型维度禁用并忽略 */
  equipmentSlot: EquipmentSlotFilter
  /** 消耗品分类过滤（任意 | 战斗药品 | 战斗通用 | 技艺） */
  consumableKind: ConsumableKindFilter
  /** 材料稀有度过滤（任意 | 普通 | 稀有） */
  materialRarity: MaterialRarityFilter
  /** 品阶过滤（0 表示任意，1~8 对应品质档） */
  gradeFilter: number
  /** 标的卡片右下角数量显示模式：自身拥有 (owned) | 坊市在售 (onSale) */
  amountDisplayMode: MarketAmountDisplayMode


  /** 设置一级主分类并重置子分类与滚动位置 */
  setPrimaryTab: (tab: MarketPrimaryCategory) => void
  /** 设置二级子分类并重置滚动位置 */
  setSubCategory: (sub: string) => void
  /** 设置搜索关键字并重置滚动位置 */
  setQuery: (query: string) => void
  /** 记录网格滚动位置 */
  setScrollTop: (scrollTop: number) => void
  /** 设置当前选中的标的 ID */
  setSelectedItemId: (id: string | null) => void
  /** 设置坊市顶层 Tab */
  setActiveTab: (tab: MarketTab) => void
  /** 设置功法门类并重置滚动位置 */
  setManualDiscipline: (discipline: ManualDisciplineFilter) => void
  /** 设置功法伤害大类并重置滚动位置 */
  setManualCategory: (category: ManualCategoryFilter) => void
  /** 设置功法载体形式并重置滚动位置 */
  setManualForm: (form: ManualFormFilter) => void
  /** 设置装备战斗类型并重置滚动位置 */
  setEquipmentKind: (kind: EquipmentKindFilter) => void
  /** 设置装备部位并重置滚动位置 */
  setEquipmentSlot: (slot: EquipmentSlotFilter) => void
  /** 设置消耗品分类并重置滚动位置 */
  setConsumableKind: (kind: ConsumableKindFilter) => void
  /** 设置材料稀有度并重置滚动位置 */
  setMaterialRarity: (rarity: MaterialRarityFilter) => void
  /** 设置品阶过滤（0 为任意）并重置滚动位置 */
  setGradeFilter: (grade: number) => void
  /** 设置标的卡片右下角数量显示模式 */
  setAmountDisplayMode: (mode: MarketAmountDisplayMode) => void
  /** 翻转标的卡片右下角数量显示模式（owned <-> onSale） */
  toggleAmountDisplayMode: () => void
  /** 重置全部过滤项至默认状态 */
  resetFilters: () => void
}

/**
 * 坊市交互状态持久化 Store (useMarketStore)
 *
 * 核心机制：
 * 1. 跨界面常驻：脱离单个组件生命周期，切换至储物袋、内视等面板再切回坊市时，Tab、搜索词与滚动条位置 100% 保持；
 * 2. 详情返回无损：进入物品挂单盘口详情后点击“返回标的列表”，原分类、子分类、搜索词与滚动条瞬间精准还原。
 */
export const useMarketStore = create<MarketUiState>((set) => ({
  primaryTab: 'all',
  subCategory: 'all',
  query: '',
  scrollTop: 0,
  selectedItemId: null,
  activeTab: 'browse',
  manualDiscipline: 'any',
  manualCategory: 'any',
  manualForm: 'any',
  equipmentKind: 'any',
  equipmentSlot: 'any',
  consumableKind: 'any',
  materialRarity: 'any',
  gradeFilter: 0,
  amountDisplayMode: 'owned',


  setPrimaryTab: (primaryTab) => set({ primaryTab, subCategory: 'all', scrollTop: 0 }),
  setSubCategory: (subCategory) => set({ subCategory, scrollTop: 0 }),
  setQuery: (query) => set({ query, scrollTop: 0 }),
  setScrollTop: (scrollTop) => set({ scrollTop }),
  setSelectedItemId: (selectedItemId) => set({ selectedItemId }),
  setActiveTab: (activeTab) => set({ activeTab }),
  setManualDiscipline: (manualDiscipline) => set({ manualDiscipline, scrollTop: 0 }),
  setManualCategory: (manualCategory) => set({ manualCategory, scrollTop: 0 }),
  setManualForm: (manualForm) => set({ manualForm, scrollTop: 0 }),
  setEquipmentKind: (equipmentKind) => set({ equipmentKind, scrollTop: 0 }),
  setEquipmentSlot: (equipmentSlot) => set({ equipmentSlot, scrollTop: 0 }),
  setConsumableKind: (consumableKind) => set({ consumableKind, scrollTop: 0 }),
  setMaterialRarity: (materialRarity) => set({ materialRarity, scrollTop: 0 }),
  setGradeFilter: (gradeFilter) => set({ gradeFilter, scrollTop: 0 }),
  setAmountDisplayMode: (amountDisplayMode) => set({ amountDisplayMode }),
  toggleAmountDisplayMode: () => set((state) => ({
    amountDisplayMode: state.amountDisplayMode === 'owned' ? 'onSale' : 'owned',
  })),
  resetFilters: () => set({
    primaryTab: 'all',
    subCategory: 'all',
    query: '',
    scrollTop: 0,
    manualDiscipline: 'any',
    manualCategory: 'any',
    manualForm: 'any',
    equipmentKind: 'any',
    equipmentSlot: 'any',
    consumableKind: 'any',
    materialRarity: 'any',
    gradeFilter: 0,
  }),
}))
