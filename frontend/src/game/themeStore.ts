import { create } from 'zustand'

export type Theme = 'light' | 'dark'

export interface ThemeOrigin {
  x: number
  y: number
}

interface ThemeStore {
  theme: Theme
  setTheme: (theme: Theme, origin?: ThemeOrigin) => void
  toggleTheme: (origin?: ThemeOrigin) => void
}

/** 首屏选择由 index.html 恢复，切换只更新根节点配色，不重新挂载界面。 */
function applyTheme(theme: Theme): void {
  document.documentElement.dataset.theme = theme
  document.documentElement.style.colorScheme = theme
  document.querySelector<HTMLMetaElement>('meta[name="color-scheme"]')!.content = theme
  document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')!.content = theme === 'dark' ? '#070604' : '#f1ecdc'
}

/** 以触发点为圆心做圆形扩散换色；不支持 View Transitions 或减少动效偏好时直接切换。 */
function applyThemeAnimated(theme: Theme, origin?: ThemeOrigin): void {
  if (!document.startViewTransition || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    applyTheme(theme)
    return
  }
  const transition = document.startViewTransition(() => applyTheme(theme))
  // finished 在过渡被打断/跳过时同样拒绝（连点场景），消费掉避免 unhandled rejection。
  void transition.finished.catch(() => {})
  void transition.ready.then(() => {
    const x = origin?.x ?? window.innerWidth - 40
    const y = origin?.y ?? 40
    const radius = Math.hypot(Math.max(x, window.innerWidth - x), Math.max(y, window.innerHeight - y))
    document.documentElement.animate(
      { clipPath: [`circle(0px at ${x}px ${y}px)`, `circle(${radius}px at ${x}px ${y}px)`] },
      { duration: 680, easing: 'cubic-bezier(0.22, 1, 0.36, 1)', pseudoElement: '::view-transition-new(root)' },
    )
  }, () => {
    // 切换被跳过时 ready 拒绝（如连点打断上一次过渡），无需处理。
  })
}

function persistTheme(theme: Theme): void {
  try {
    window.localStorage.setItem('daohua.theme', theme)
  } catch {
    // 本地存储不可用时仍保留当前会话的选择。
  }
}

export const useThemeStore = create<ThemeStore>(set => ({
  theme: document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light',
  setTheme: (theme, origin) => {
    applyThemeAnimated(theme, origin)
    set({ theme })
    persistTheme(theme)
  },
  toggleTheme: origin => set(state => {
    const theme = state.theme === 'dark' ? 'light' : 'dark'
    applyThemeAnimated(theme, origin)
    persistTheme(theme)
    return { theme }
  }),
}))
