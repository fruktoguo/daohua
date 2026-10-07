import { create } from 'zustand'

/**
 * 客户端主界面顶级页签标识：
 * - 'insight': 悟道 / 属性 / 境界突破详情
 * - 'exploration': 历练（常驻修炼行动与当前地点技艺、讨伐行动）
 * - 'crafting': 技艺（炼丹与炼器）
 * - 'alchemy': 炼丹入口（映射至技艺/炼丹）
 * - 'map': 地图（包含地图与附近子选项卡）
 * - 'combat': 详情（战斗对决现场与战备装配详情）
 * - 'market': 坊市交易行
 * - 'social': 同道（队伍组队、匹配与组队大厅）
 * - 'inventory': 储物袋（背包）
 * - 'log': 传音道历（日志）
 * - 'settings': 个人与系统设置
 */
export type TabId = 'insight' | 'market' | 'exploration' | 'crafting' | 'alchemy' | 'map' | 'combat' | 'social' | 'inventory' | 'log' | 'settings'

/** 同道页子选项卡：队伍 / 邀请（后续同道玩法沿用同一结构扩展） */
export type SocialSubTab = 'party' | 'invite'

/**
 * 队伍页内部页签：
 * - 无队伍态为三入口自选：lobby 组队大厅 / match 队伍匹配 / create 创建队伍；
 * - 有队伍态为 overview 队伍信息 / lobby 组队大厅（招募散人，邀请按钮仅队长可见）。
 */
export type PartySubTab = 'lobby' | 'match' | 'create' | 'overview'

/** 技艺当前模式：炼丹 | 炼器 | 符箓 | 阵法 | 强化 */
export type CraftingMode = 'alchemy' | 'forging' | 'talisman' | 'formation' | 'enhancement'

/** 炼丹顶部子选项卡：提炼 vs 修炼丹药 vs 斗法药剂 */
export type AlchemySubTab = 'extraction' | 'cultivation' | 'combat'
/** 炼器顶部子选项卡：精炼 vs 六大装备部位 vs 技艺工具分组 */
export type ForgingSubTab = 'refining' | 'weapon' | 'head' | 'armor' | 'legs' | 'boots' | 'trinket' | 'technique'

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
  /** 炼器顶部当前子分类（精炼、六大装备部位与技艺工具分组） */
  forgingSubTab: ForgingSubTab
  /** 切换炼器子分类 */
  setForgingSubTab: (tab: ForgingSubTab) => void
  /** 右侧主栏当前激活的子选项卡 */
  rightColumnTab: RightColumnTab
  /** 切换右侧主栏子选项卡 */
  setRightColumnTab: (tab: RightColumnTab) => void
  /** 同道页当前子选项卡 */
  socialSubTab: SocialSubTab
  /** 切换同道页子选项卡 */
  setSocialSubTab: (tab: SocialSubTab) => void
  /** 队伍页内部当前页签 */
  partySubTab: PartySubTab
  /** 切换队伍页内部页签 */
  setPartySubTab: (tab: PartySubTab) => void
}

/**
 * UI 视图全局 Zustand 状态管理
 */
export const useUiStore = create<UiStore>(set => ({
  activeTab: 'exploration',
  setActiveTab: activeTab => {
    // 'alchemy' 不是独立页面：作为直达技艺-炼丹的别名，同时展开二级导航并复位子选项卡
    if (activeTab === 'alchemy') {
      set({ activeTab: 'crafting', craftingMode: 'alchemy', craftingNavExpanded: true, alchemySubTab: 'extraction' })
    } else {
      set({ activeTab })
    }
  },
  craftingMode: 'alchemy',
  // 切换技艺模式时同时复位对应子选项卡，避免残留上一个模式的深层选中态
  // （符箓/阵法/强化无二级子选项卡，仅切模式即可）
  setCraftingMode: craftingMode => set({
    craftingMode,
    ...(craftingMode === 'alchemy'
      ? { alchemySubTab: 'extraction' as AlchemySubTab }
      : craftingMode === 'forging'
        ? { forgingSubTab: 'refining' as ForgingSubTab }
        : {}),
  }),
  craftingNavExpanded: false,
  setCraftingNavExpanded: expanded => set(state => ({
    craftingNavExpanded: typeof expanded === 'function' ? expanded(state.craftingNavExpanded) : expanded,
  })),
  alchemySubTab: 'extraction',
  setAlchemySubTab: alchemySubTab => set({ alchemySubTab }),
  forgingSubTab: 'refining',
  setForgingSubTab: forgingSubTab => set({ forgingSubTab }),
  rightColumnTab: 'inventory',
  setRightColumnTab: rightColumnTab => set({ rightColumnTab }),
  socialSubTab: 'party',
  setSocialSubTab: socialSubTab => set({ socialSubTab }),
  partySubTab: 'lobby',
  setPartySubTab: partySubTab => set({ partySubTab }),
}))
