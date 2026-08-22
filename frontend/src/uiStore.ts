import { create } from 'zustand'
import type { ActionType } from './game/types'

/**
 * 客户端主界面顶级页签标识：
 * - 'insight': 悟道 / 属性 / 境界突破详情
 * - 'cultivation' | 'exploration' | 'alchemy' | 'combat': 四大行动与地图/战斗面板
 * - 'market': 坊市交易行
 * - 'inventory': 储物袋（背包）
 * - 'log': 传音道历（日志）
 * - 'settings': 个人与系统设置
 */
export type TabId = 'insight' | ActionType | 'market' | 'inventory' | 'log' | 'settings'

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
