import { create } from 'zustand'

/**
 * 客户端主界面顶级页签标识：
 * - 'insight': 悟道 / 属性 / 境界突破详情
 * - 'cultivation': 修炼（吐纳）
 * - 'exploration': 历练（当前地点特殊行动与妖兽历练）
 * - 'alchemy': 炼丹
 * - 'map': 地图（包含地图与附近子选项卡）
 * - 'combat': 详情（战斗对决现场与战备装配详情）
 * - 'market': 坊市交易行
 * - 'inventory': 储物袋（背包）
 * - 'log': 传音道历（日志）
 * - 'settings': 个人与系统设置
 */
export type TabId = 'insight' | 'market' | 'cultivation' | 'exploration' | 'alchemy' | 'map' | 'combat' | 'inventory' | 'log' | 'settings'

/**
 * UI 视图切换状态机接口
 */
interface UiStore {
  /** 当前激活的主页签 */
  activeTab: TabId
  /** 切换主页签 */
  setActiveTab: (tab: TabId) => void
}

/**
 * UI 视图全局 Zustand 状态管理
 */
export const useUiStore = create<UiStore>(set => ({
  activeTab: 'exploration',
  setActiveTab: activeTab => set({ activeTab }),
}))
