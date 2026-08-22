/**
 * 界面缩放比例 (UI Zoom / Scale) 存储与应用工具
 * 集中管理用户自定义界面缩放比例，并通过 CSS 变量 `--ui-scale` 及 `document.documentElement.style.zoom` 实现全站即时无损缩放。
 */

const STORAGE_KEY = 'daoxuan_ui_zoom'
const MIN_SCALE = 50
const MAX_SCALE = 200
const DEFAULT_SCALE = 100

/**
 * 从 localStorage 读取当前配置的 UI 缩放百分比 (50% ~ 200%)
 * @returns 缩放百分比数值（默认 100）
 */
export function getUiScale(): number {
  if (typeof window === 'undefined') return DEFAULT_SCALE
  const stored = window.localStorage.getItem(STORAGE_KEY)
  if (!stored) return DEFAULT_SCALE
  const parsed = Number(stored)
  if (Number.isNaN(parsed) || parsed < MIN_SCALE || parsed > MAX_SCALE) {
    return DEFAULT_SCALE
  }
  return parsed
}

/**
 * 设置并立即应用新的 UI 缩放百分比
 * @param percent 缩放百分比（如 90, 100, 110 等）
 * @returns 限制在 [MIN_SCALE, MAX_SCALE] 内的实际生效缩放数值
 */
export function setUiScale(percent: number): number {
  const clamped = Math.max(MIN_SCALE, Math.min(MAX_SCALE, Math.round(percent)))
  if (typeof document !== 'undefined') {
    document.documentElement.style.setProperty('--ui-scale', String(clamped / 100))
    ;(document.documentElement.style as unknown as { zoom: string }).zoom = `${clamped}%`
  }
  if (typeof window !== 'undefined') {
    window.localStorage.setItem(STORAGE_KEY, String(clamped))
  }
  return clamped
}

/**
 * 应用启动时初始化 UI 缩放比例
 */
export function initUiScale(): void {
  const current = getUiScale()
  setUiScale(current)
}
