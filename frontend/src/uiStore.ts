import { create } from 'zustand'

/**
 * 客户端主界面顶级页签标识：
 * - 'insight': 悟道 / 属性 / 境界突破详情
 * - 'cultivation': 修炼（吐纳）
 * - 'exploration': 历练（当前地点特殊行动与妖兽历练）
 * - 'crafting': 技艺（炼丹与炼器）
 * - 'alchemy': 炼丹（兼容别名，自动重定向至技艺/炼丹）
 * - 'map': 地图（包含地图与附近子选项卡）
 * - 'combat': 详情（战斗对决现场与战备装配详情）
 * - 'market': 坊市交易行
 * - 'inventory': 储物袋（背包）
 * - 'log': 传音道历（日志）
 * - 'settings': 个人与系统设置
 */
export type TabId = 'insight' | 'market' | 'cultivation' | 'exploration' | 'crafting' | 'alchemy' | 'map' | 'combat' | 'inventory' | 'log' | 'settings'

/** 技艺当前模式：炼丹 vs 炼器 */
export type CraftingMode = 'alchemy' | 'forging'

/** 炼丹顶部子选项卡：修炼丹药 vs 斗法药剂 vs 材料提炼 */
export type AlchemySubTab = 'cultivation' | 'combat' | 'extraction'
/** 炼器顶部子选项卡：六大装备部位 */
export type ForgingSubTab = 'weapon' | 'head' | 'armor' | 'legs' | 'boots' | 'trinket'

/** 右侧主栏顶层子选项卡：储物袋 vs 装备 vs 功法 */
export type RightColumnTab = 'inventory' | 'equipment' | 'abilities'
/**
 * UI 视图切换状态机接口
 */
interface UiStore {
  /** 当前激活的主页签 */
  activeTab: TabId
  /** 切换主页签 */
  setActiveTab: (tab: TabId) => void
  /** 技艺当前模式：炼丹 vs 炼器 */
  craftingMode: CraftingMode
  /** 切换技艺模式 */
  setCraftingMode: (mode: CraftingMode) => void
  /** 侧边栏技艺二级导航折叠/展开状态 */
  craftingNavExpanded: boolean
  /** 切换/设置技艺二级导航折叠状态 */
  setCraftingNavExpanded: (expanded: boolean | ((prev: boolean) => boolean)) => void
  /** 炼丹顶部当前子分类 */
  alchemySubTab: AlchemySubTab
  /** 切换炼丹子分类 */
  setAlchemySubTab: (tab: AlchemySubTab) => void
  /** 炼器顶部当前子分类（六大部位） */
  forgingSubTab: ForgingSubTab
  /** 切换炼器子分类 */
  setForgingSubTab: (tab: ForgingSubTab) => void
  /** 右侧主栏当前激活的子选项卡 */
  rightColumnTab: RightColumnTab
  /** 切换右侧主栏子选项卡 */
  setRightColumnTab: (tab: RightColumnTab) => void
}

/**
 * UI 视图全局 Zustand 状态管理
 */
export const useUiStore = create<UiStore>(set => ({
  activeTab: 'exploration',
  setActiveTab: activeTab => {
    if (activeTab === 'alchemy') {
      set({ activeTab: 'crafting', craftingMode: 'alchemy', craftingNavExpanded: true })
    } else {
      set({ activeTab })
    }
  },
  craftingMode: 'alchemy',
  setCraftingMode: craftingMode => set({ craftingMode }),
  craftingNavExpanded: false,
  setCraftingNavExpanded: expanded => set(state => ({
    craftingNavExpanded: typeof expanded === 'function' ? expanded(state.craftingNavExpanded) : expanded,
  })),
  alchemySubTab: 'cultivation',
  setAlchemySubTab: alchemySubTab => set({ alchemySubTab }),
  forgingSubTab: 'weapon',
  setForgingSubTab: forgingSubTab => set({ forgingSubTab }),
  rightColumnTab: 'inventory',
  setRightColumnTab: rightColumnTab => set({ rightColumnTab }),
}))
