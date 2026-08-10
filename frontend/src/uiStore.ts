import { create } from 'zustand'
import type { ActionType } from './game/types'

export type TabId = ActionType | 'market' | 'inventory' | 'log' | 'settings'

interface UiStore {
  activeTab: TabId
  setActiveTab: (tab: TabId) => void
}

export const useUiStore = create<UiStore>(set => ({
  activeTab: 'exploration',
  setActiveTab: activeTab => set({ activeTab }),
}))
