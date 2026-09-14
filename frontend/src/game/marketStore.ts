import { create } from 'zustand'

/** 坊市一级主分类 */
export type MarketPrimaryCategory = 'all' | 'material' | 'equipment' | 'pill' | 'manual'

/** 坊市顶层 Tab */
export type MarketTab = 'browse' | 'orders'

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

  setPrimaryTab: (primaryTab) => set({ primaryTab, subCategory: 'all', scrollTop: 0 }),
  setSubCategory: (subCategory) => set({ subCategory, scrollTop: 0 }),
  setQuery: (query) => set({ query, scrollTop: 0 }),
  setScrollTop: (scrollTop) => set({ scrollTop }),
  setSelectedItemId: (selectedItemId) => set({ selectedItemId }),
  setActiveTab: (activeTab) => set({ activeTab }),
  resetFilters: () => set({ primaryTab: 'all', subCategory: 'all', query: '', scrollTop: 0 }),
}))
