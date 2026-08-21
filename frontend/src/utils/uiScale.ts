const STORAGE_KEY = 'daoxuan_ui_zoom'
const MIN_SCALE = 50
const MAX_SCALE = 200
const DEFAULT_SCALE = 100

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

export function initUiScale(): void {
  const current = getUiScale()
  setUiScale(current)
}
