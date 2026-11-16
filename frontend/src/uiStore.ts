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
 * - 'social': 同道（队伍、宗门与邀请）
 * - 'codex': 图鉴
 * - 'leaderboard': 排行榜
 * - 'changelog': 更新日志
 * - 'checkin': 每日签到（独立居中弹窗，不替换底层主视区）
 * - 'help': 帮助教程
 * - 'inventory': 储物袋（背包）
 * - 'log': 传音道历（日志）
 * - 'records': 记录（离线挂机 / 今日统计 / 近期战报历史）
 * - 'settings': 个人与系统设置
 */
export type TabId = 'insight' | 'market' | 'shop' | 'exploration' | 'crafting' | 'alchemy' | 'map' | 'combat' | 'social' | 'codex' | 'leaderboard' | 'changelog' | 'checkin' | 'help' | 'inventory' | 'log' | 'records' | 'settings'

/** 帮助中心快捷跳转目标（支持直达一级页签与嵌套子选项卡） */
export type HelpJumpTarget =
  | { tab: 'insight' | 'exploration' | 'shop' | 'market' | 'combat' | 'codex' | 'inventory' }
  | { tab: 'crafting'; craftingMode: CraftingMode; enhancementSubTab?: EnhancementSubTab }
  | { tab: 'map'; mapSubTab?: MapSubTab }
  | { tab: 'social'; socialSubTab?: SocialSubTab }

/** 图鉴顶级分类：材料 vs 制造 vs 建筑 vs 技能 vs 怪物 vs 特质 */
export type CodexCategory = 'material' | 'crafting' | 'building' | 'ability' | 'monster' | 'trait'

/** 同道页子选项卡：队伍、宗门与邀请。 */
export type SocialSubTab = 'party' | 'zongmen' | 'invite'

/** 队伍面板顶部页签：队伍管理与斗地主对弈。 */
export type PartyGameSubTab = 'party' | 'doudizhu'

/** 地图页子选项卡：地图本体 vs 附近同域（妖兽、同道修士、灵地建筑） */
export type MapSubTab = 'map' | 'monsters' | 'people' | 'buildings'

/**
 * 队伍页内部页签：
 * - 无队伍态为三入口自选：lobby 组队大厅 / match 队伍匹配 / create 创建队伍；
 * - 有队伍态为 overview 队伍信息 / lobby 组队大厅（招募散人，邀请按钮仅队长可见）。
 */
export type PartySubTab = 'lobby' | 'match' | 'create' | 'overview'

/** 技艺当前模式：炼丹 | 炼器 | 符箓 | 阵法 | 秘法 */
export type CraftingMode = 'alchemy' | 'forging' | 'talisman' | 'formation' | 'enhancement'

/** 炼丹顶部子选项卡：提炼 vs 修炼丹药 vs 斗法药剂 */
export type AlchemySubTab = 'extraction' | 'cultivation' | 'combat'
/** 炼器顶部子选项卡：精炼 vs 六大装备部位 vs 技艺工具分组 */
export type ForgingSubTab = 'refining' | 'weapon' | 'off-hand' | 'head' | 'armor' | 'legs' | 'trinket' | 'technique'
/** 秘法顶部子选项卡：强化 vs 化灵 vs 分解 vs 转化 */
export type EnhancementSubTab = 'enhance' | 'liquefy' | 'disassemble' | 'convert'

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
  /** 侧边栏坊市二级导航折叠/展开状态 */
  marketNavExpanded: boolean
  setMarketNavExpanded: (expanded: boolean | ((prev: boolean) => boolean)) => void
  /** 侧边栏同道二级导航折叠/展开状态 */
  socialNavExpanded: boolean
  setSocialNavExpanded: (expanded: boolean | ((prev: boolean) => boolean)) => void
  /** 侧边栏技艺二级导航折叠/展开状态 */
  craftingNavExpanded: boolean
  /** 切换/设置技艺二级导航折叠状态 */
  setCraftingNavExpanded: (expanded: boolean | ((prev: boolean) => boolean)) => void
  /** 侧边栏其他二级导航折叠/展开状态 */
  otherNavExpanded: boolean
  setOtherNavExpanded: (expanded: boolean | ((prev: boolean) => boolean)) => void
  /** 炼丹顶部当前子分类 */
  alchemySubTab: AlchemySubTab
  /** 切换炼丹子分类 */
  setAlchemySubTab: (tab: AlchemySubTab) => void
  /** 炼器顶部当前子分类（精炼、六大装备部位与技艺工具分组） */
  forgingSubTab: ForgingSubTab
  /** 切换炼器子分类 */
  setForgingSubTab: (tab: ForgingSubTab) => void
  /** 秘法顶部当前子分类（强化、化灵、分解、转化） */
  enhancementSubTab: EnhancementSubTab
  /** 切换秘法子分类 */
  setEnhancementSubTab: (tab: EnhancementSubTab) => void
  /** 右侧主栏当前激活的子选项卡 */
  rightColumnTab: RightColumnTab
  /** 切换右侧主栏子选项卡 */
  setRightColumnTab: (tab: RightColumnTab) => void
  /** 同道页当前子选项卡 */
  socialSubTab: SocialSubTab
  /** 切换同道页子选项卡 */
  setSocialSubTab: (tab: SocialSubTab) => void
  /** 队伍面板顶部页签 */
  partyGameSubTab: PartyGameSubTab
  /** 切换队伍面板顶部页签 */
  setPartyGameSubTab: (tab: PartyGameSubTab) => void
  /** 队伍页内部当前页签 */
  partySubTab: PartySubTab
  /** 切换队伍页内部页签 */
  /** 地图页当前子选项卡 */
  mapSubTab: MapSubTab
  /** 切换地图页子选项卡 */
  setMapSubTab: (tab: MapSubTab) => void
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
  /** 外部（如聊天地点超链接）请求大地图聚焦的目标节点 ID */
  mapFocusNodeId: string | null
  /** 设置大地图聚焦的目标节点 ID */
  setMapFocusNodeId: (nodeId: string | null) => void
  /** 快捷直达大地图并聚焦指定地点节点 */
  navigateToMapNode: (nodeId: string) => void
  /** 各分组记忆的落点页签：切回分组时直达上次所见的子项 */
  mobileNavMemory: Record<MobileNavGroupId, TabId>
  /** 按分组激活其记忆页签（首次为组内首项）并展开子行 */
  activateMobileNavGroup: (group: MobileNavGroupId) => void
  /** 战斗模拟器弹窗开启状态 */
  simulatorOpen: boolean
  /** 切换/设置模拟器弹窗状态 */
  setSimulatorOpen: (open: boolean) => void
  /** 仙门传书独立弹窗开启状态（挂载于顶层弹窗层，不替换底层主视区） */
  mailOpen: boolean
  /** 设置仙门传书弹窗开启状态 */
  setMailOpen: (open: boolean) => void
  /** 切换仙门传书弹窗开合状态 */
  toggleMailOpen: () => void
  /** 新手引导完成或跳过后的「帮助」引导提示弹窗状态 */
  helpPromptOpen: boolean
  /** 设置「帮助」引导提示弹窗状态 */
  setHelpPromptOpen: (open: boolean) => void
  /** 「帮助」修行指南独立弹窗开启状态 */
  helpModalOpen: boolean
  /** 设置「帮助」修行指南独立弹窗开启状态 */
  setHelpModalOpen: (open: boolean) => void
  /** 「每日签到」独立弹窗开启状态 */
  checkinModalOpen: boolean
  /** 设置「每日签到」独立弹窗开启状态 */
  setCheckinModalOpen: (open: boolean) => void
  /** 新手引导浮层当前是否处于可见状态（用于与更新日志等开局弹窗互斥避让） */
  tutorialOverlayActive: boolean
  /** 同步新手引导浮层当前可见状态 */
  setTutorialOverlayActive: (active: boolean) => void
  /** 外部（设置页）确认跳过引导递增序号，供 TutorialOverlay 同步拉起帮助提示弹窗 */
  tutorialExternalSkipSeq: number
  /** 通知前端触发了外部引导跳过 */
  notifyTutorialExternalSkip: () => void
  /** 关闭引导结束提示弹窗并唤起「帮助」独立教程弹窗 */
  navigateToHelp: () => void
  /** 从帮助条目一键跳转到对应游戏页面与子页签 */
  executeHelpJump: (target: HelpJumpTarget) => void
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
    // 'help' 与 'checkin' 以独立弹窗展示，不替换底层正在查看的主视图与导航记忆
    if (activeTab === 'help') {
      set({ helpModalOpen: true })
      return
    }
    if (activeTab === 'checkin') {
      set({ checkinModalOpen: true })
      return
    }
    // 'alchemy' 不是独立页面：作为直达技艺-炼丹的别名，同时展开二级导航并复位子选项卡
    if (activeTab === 'alchemy') {
      set(state => ({
        activeTab: 'crafting',
        craftingMode: 'alchemy',
        craftingNavExpanded: true,
        alchemySubTab: 'extraction',
        mailOpen: false,
        helpModalOpen: false,
        checkinModalOpen: false,
        mobileNavMemory: { ...state.mobileNavMemory, [mobileNavGroupIdOf('crafting')]: 'crafting' },
      }))
      return
    }
    // 任何页签跳转（含代码直跳、引导驱动）都记录该分组的落点，切回分组时直达上次所见，并收起顶栏传书、帮助与签到弹窗
    set(state => ({
      activeTab,
      mailOpen: false,
      helpModalOpen: false,
      checkinModalOpen: false,
      mobileNavMemory: { ...state.mobileNavMemory, [mobileNavGroupIdOf(activeTab)]: activeTab },
    }))
  },
  craftingMode: 'alchemy',
  // 切换技艺模式时同时复位对应子选项卡，避免残留上一个模式的深层选中态
  // （符箓/阵法无二级子选项卡，仅切模式即可）
  setCraftingMode: craftingMode => set({
    craftingMode,
    mailOpen: false,
    ...(craftingMode === 'alchemy'
      ? { alchemySubTab: 'extraction' as AlchemySubTab }
      : craftingMode === 'forging'
        ? { forgingSubTab: 'refining' as ForgingSubTab }
        : craftingMode === 'enhancement'
          ? { enhancementSubTab: 'enhance' as EnhancementSubTab }
          : {}),
  }),
  marketNavExpanded: false,
  setMarketNavExpanded: expanded => set(state => ({
    marketNavExpanded: typeof expanded === 'function' ? expanded(state.marketNavExpanded) : expanded,
  })),
  socialNavExpanded: false,
  setSocialNavExpanded: expanded => set(state => ({
    socialNavExpanded: typeof expanded === 'function' ? expanded(state.socialNavExpanded) : expanded,
  })),
  craftingNavExpanded: false,
  setCraftingNavExpanded: expanded => set(state => ({
    craftingNavExpanded: typeof expanded === 'function' ? expanded(state.craftingNavExpanded) : expanded,
  })),
  otherNavExpanded: false,
  setOtherNavExpanded: expanded => set(state => ({
    otherNavExpanded: typeof expanded === 'function' ? expanded(state.otherNavExpanded) : expanded,
  })),
  alchemySubTab: 'extraction',
  setAlchemySubTab: alchemySubTab => set({ alchemySubTab }),
  forgingSubTab: 'refining',
  setForgingSubTab: forgingSubTab => set({ forgingSubTab }),
  enhancementSubTab: 'enhance',
  setEnhancementSubTab: enhancementSubTab => set({ enhancementSubTab }),
  rightColumnTab: 'inventory',
  setRightColumnTab: rightColumnTab => set({ rightColumnTab }),
  socialSubTab: 'party',
  setSocialSubTab: socialSubTab => set({ socialSubTab }),
  partyGameSubTab: 'party',
  setPartyGameSubTab: partyGameSubTab => set({ partyGameSubTab }),
  partySubTab: 'lobby',
  setPartySubTab: partySubTab => set({ partySubTab }),
  mapSubTab: 'map',
  setMapSubTab: mapSubTab => set({ mapSubTab }),
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
  mapFocusNodeId: null,
  setMapFocusNodeId: mapFocusNodeId => set({ mapFocusNodeId }),
  navigateToMapNode: (nodeId: string) => {
    const targetTab: TabId = 'map'
    set(state => ({
      activeTab: targetTab,
      mapSubTab: 'map',
      mapFocusNodeId: nodeId,
      mailOpen: false,
      helpModalOpen: false,
      checkinModalOpen: false,
      mobileNavMemory: { ...state.mobileNavMemory, [mobileNavGroupIdOf(targetTab)]: targetTab },
    }))
  },
  mobileNavMemory: INITIAL_NAV_MEMORY,
  activateMobileNavGroup: group => set(state => {
    // 已在该分组且传书弹窗未打开：子行本就展开，重复点击不再改签，避免误触把玩家从子页签踢回首项；
    // 若传书弹窗处于打开态，则先收起弹窗回到该分组当前页签
    if (mobileNavGroupIdOf(state.activeTab) === group) {
      return state.mailOpen ? { mailOpen: false } : {}
    }
    const target = state.mobileNavMemory[group] ?? MOBILE_NAV_GROUP_DEFS[group].tabs[0]
    return { activeTab: target, mailOpen: false, mobileNavMemory: { ...state.mobileNavMemory, [group]: target } }
  }),
  simulatorOpen: false,
  setSimulatorOpen: simulatorOpen => set({ simulatorOpen }),
  mailOpen: false,
  setMailOpen: mailOpen => set({ mailOpen }),
  toggleMailOpen: () => set(state => ({ mailOpen: !state.mailOpen })),
  helpPromptOpen: false,
  setHelpPromptOpen: helpPromptOpen => set({ helpPromptOpen }),
  helpModalOpen: false,
  setHelpModalOpen: helpModalOpen => set({ helpModalOpen }),
  checkinModalOpen: false,
  setCheckinModalOpen: checkinModalOpen => set({ checkinModalOpen }),
  tutorialOverlayActive: false,
  setTutorialOverlayActive: tutorialOverlayActive => set({ tutorialOverlayActive }),
  tutorialExternalSkipSeq: 0,
  notifyTutorialExternalSkip: () => set(state => ({ tutorialExternalSkipSeq: state.tutorialExternalSkipSeq + 1 })),
  navigateToHelp: () => set({
    helpPromptOpen: false,
    helpModalOpen: true,
  }),
  executeHelpJump: target => set(state => {
    const baseMemory = { ...state.mobileNavMemory, [mobileNavGroupIdOf(target.tab)]: target.tab }
    if (target.tab === 'crafting') {
      return {
        activeTab: 'crafting',
        craftingMode: target.craftingMode,
        craftingNavExpanded: true,
        ...(target.craftingMode === 'alchemy' ? { alchemySubTab: 'extraction' as AlchemySubTab } : {}),
        ...(target.craftingMode === 'forging' ? { forgingSubTab: 'refining' as ForgingSubTab } : {}),
        ...(target.craftingMode === 'enhancement'
          ? { enhancementSubTab: (target.enhancementSubTab ?? 'enhance') as EnhancementSubTab }
          : {}),
        mailOpen: false,
        helpModalOpen: false,
        mobileNavMemory: baseMemory,
      }
    }
    if (target.tab === 'map') {
      return {
        activeTab: 'map',
        mapSubTab: target.mapSubTab ?? 'map',
        mailOpen: false,
        helpModalOpen: false,
        mobileNavMemory: baseMemory,
      }
    }
    if (target.tab === 'social') {
      return {
        activeTab: 'social',
        socialNavExpanded: true,
        socialSubTab: target.socialSubTab ?? 'party',
        mailOpen: false,
        helpModalOpen: false,
        mobileNavMemory: baseMemory,
      }
    }
    return {
      activeTab: target.tab,
      ...(target.tab === 'shop' || target.tab === 'market' ? { marketNavExpanded: true } : {}),
      ...(target.tab === 'codex' ? { otherNavExpanded: true } : {}),
      mailOpen: false,
      helpModalOpen: false,
      mobileNavMemory: baseMemory,
    }
  }),
}))
