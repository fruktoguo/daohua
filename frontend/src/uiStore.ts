import { create } from 'zustand'
import type { CodexFilterState } from './game/codexFilters'
import { DEFAULT_CODEX_FILTERS } from './game/codexFilters'
import type { AwakenedTraitResult } from './game/types'
import { MOBILE_NAV_GROUP_DEFS, MOBILE_NAV_GROUP_ORDER, mobileNavGroupIdOf, type MobileNavGroupId } from './mobileNav'

/**
 * 客户端主界面顶级页签标识：
 * - 'insight': 悟道 / 属性 / 境界突破详情
 * - 'exploration': 历练（常驻修炼行动与当前地点技艺、讨伐行动）
 * - 'crafting': 技艺（炼丹与炼器）
 * - 'alchemy': 炼丹入口（映射至技艺/炼丹）
 * - 'map': 地图（包含地图与附近子选项卡）
 * - 'combat': 详情（战斗对决现场与战备装配详情）
 * - 'market': 坊市交易行
 * - 'shop': 系统商店（官价直售与每日限购）
 * - 'social': 同道（队伍组队、匹配与组队大厅）
 * - 'inventory': 储物袋（背包）
 * - 'log': 传音道历（日志）
 * - 'records': 记录（离线挂机 / 今日统计 / 近期战报历史）
 * - 'settings': 个人与系统设置
 */
export type TabId = 'insight' | 'market' | 'shop' | 'exploration' | 'crafting' | 'alchemy' | 'map' | 'combat' | 'social' | 'codex' | 'inventory' | 'log' | 'records' | 'settings'

/** 图鉴顶级分类：材料 vs 制造 vs 建筑 vs 技能 vs 怪物 vs 特质 */
export type CodexCategory = 'material' | 'crafting' | 'building' | 'ability' | 'monster' | 'trait'

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
export type ForgingSubTab = 'refining' | 'weapon' | 'off-hand' | 'head' | 'armor' | 'legs' | 'trinket' | 'technique'

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
  /** 图鉴顶级当前分类 */
  codexCategory: CodexCategory
  /** 切换图鉴分类 */
  setCodexCategory: (category: CodexCategory) => void
  /** 图鉴各顶级分类自己的筛选状态（子分类/分组/等阶等按分类独立保存） */
  codexFilters: Readonly<Record<CodexCategory, CodexFilterState>>
  /** 合并更新指定图鉴分类的筛选状态 */
  setCodexFilters: (category: CodexCategory, patch: Partial<CodexFilterState>) => void
  /** 觉醒石抽卡结果列表（非 null 时唤起抽取弹窗） */
  awakenResults: AwakenedTraitResult[] | null
  /** 打开觉醒结果弹窗 */
  openAwakenResult: (results: AwakenedTraitResult[]) => void
  /** 关闭觉醒结果弹窗 */
  closeAwakenResult: () => void
  /** 新手引导当前要求地图聚焦的目标节点 ID（由 TutorialOverlay 下发，WorldMapPanel 响应） */
  tutorialMapFocusNodeId: string | null
  /** 设置新手引导地图聚焦的目标节点 ID */
  setTutorialMapFocusNodeId: (nodeId: string | null) => void
  /** 各分组记忆的落点页签：切回分组时直达上次所见的子项 */
  mobileNavMemory: Record<MobileNavGroupId, TabId>
  /** 按分组激活其记忆页签（首次为组内首项）并展开子行 */
  activateMobileNavGroup: (group: MobileNavGroupId) => void
  /** 战斗模拟器弹窗开启状态 */
  simulatorOpen: boolean
  /** 切换/设置模拟器弹窗状态 */
  setSimulatorOpen: (open: boolean) => void
}

/** 各分组的初始落点：组内首个页签（与 `MOBILE_NAV_GROUPS` 同源，避免两处硬编码顺序） */
const INITIAL_NAV_MEMORY = Object.fromEntries(
  MOBILE_NAV_GROUP_ORDER.map(id => [id, MOBILE_NAV_GROUP_DEFS[id].tabs[0] as TabId]),
) as Record<MobileNavGroupId, TabId>

/**
 * UI 视图全局 Zustand 状态管理
 */
export const useUiStore = create<UiStore>(set => ({
  activeTab: 'exploration',
  setActiveTab: activeTab => {
    // 'alchemy' 不是独立页面：作为直达技艺-炼丹的别名，同时展开二级导航并复位子选项卡
    if (activeTab === 'alchemy') {
      set(state => ({
        activeTab: 'crafting',
        craftingMode: 'alchemy',
        craftingNavExpanded: true,
        alchemySubTab: 'extraction',
        mobileNavMemory: { ...state.mobileNavMemory, [mobileNavGroupIdOf('crafting')]: 'crafting' },
      }))
      return
    }
    // 任何页签跳转（含代码直跳、引导驱动）都记录该分组的落点，切回分组时直达上次所见
    set(state => ({
      activeTab,
      mobileNavMemory: { ...state.mobileNavMemory, [mobileNavGroupIdOf(activeTab)]: activeTab },
    }))
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
  codexCategory: 'material',
  setCodexCategory: codexCategory => set({ codexCategory }),
  codexFilters: {
    material: DEFAULT_CODEX_FILTERS,
    crafting: DEFAULT_CODEX_FILTERS,
    building: DEFAULT_CODEX_FILTERS,
    ability: DEFAULT_CODEX_FILTERS,
    monster: DEFAULT_CODEX_FILTERS,
    trait: DEFAULT_CODEX_FILTERS,
  },
  // 逐分类浅合并：切分类只改自己那一份筛选，其余分类的子分类与等阶保持不动。
  setCodexFilters: (category, patch) => set(state => ({
    codexFilters: { ...state.codexFilters, [category]: { ...state.codexFilters[category], ...patch } },
  })),
  awakenResults: null,
  openAwakenResult: awakenResults => set({ awakenResults }),
  closeAwakenResult: () => set({ awakenResults: null }),
  tutorialMapFocusNodeId: null,
  setTutorialMapFocusNodeId: tutorialMapFocusNodeId => set({ tutorialMapFocusNodeId }),
  mobileNavMemory: INITIAL_NAV_MEMORY,
  activateMobileNavGroup: group => set(state => {
    // 已在该分组：子行本就展开，重复点击不再改签，避免误触把玩家从子页签踢回首项
    if (mobileNavGroupIdOf(state.activeTab) === group) return {}
    const target = state.mobileNavMemory[group] ?? MOBILE_NAV_GROUP_DEFS[group].tabs[0]
    return { activeTab: target, mobileNavMemory: { ...state.mobileNavMemory, [group]: target } }
  }),
  simulatorOpen: false,
  setSimulatorOpen: simulatorOpen => set({ simulatorOpen }),
}))
