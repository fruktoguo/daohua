import { create } from 'zustand'

export type Theme = 'light' | 'dark'

interface ThemeStore {
  theme: Theme
  setTheme: (theme: Theme) => void
}

/** 首屏选择由 index.html 恢复，切换只更新根节点配色，不重新挂载界面。 */
function applyTheme(theme: Theme): void {
  document.documentElement.dataset.theme = theme
  document.documentElement.style.colorScheme = theme
  document.querySelector<HTMLMetaElement>('meta[name="color-scheme"]')!.content = theme
  document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')!.content = theme === 'dark' ? '#070604' : '#e7dfcf'
}

export const useThemeStore = create<ThemeStore>(set => ({
  theme: document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light',
  setTheme: theme => {
    applyTheme(theme)
    set({ theme })
    try {
      window.localStorage.setItem('daohua.theme', theme)
    } catch {
      // 本地存储不可用时仍保留当前会话的选择。
    }
  },
}))
