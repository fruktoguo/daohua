import { useSyncExternalStore } from 'react'
import { create } from 'zustand'

/** 本地存储键名：战斗详情特效开关偏好 */
export const COMBAT_VFX_STORAGE_KEY = 'daohua.combatVfxEnabled'

interface CombatVfxStore {
  /** 是否开启战斗动画与视觉特效（关闭后仅保留正常伤害数字） */
  vfxEnabled: boolean
  /** 显式设置开关状态并持久化 */
  setVfxEnabled: (enabled: boolean) => void
  /** 切换开关状态并持久化 */
  toggleVfxEnabled: () => void
}

/** 从指定 Storage 读取特效开关偏好；存储不可用或未记录时默认开启。 */
export function readStoredCombatVfxEnabled(storage?: Pick<Storage, 'getItem'> | null): boolean {
  try {
    const target = storage ?? (typeof window !== 'undefined' ? window.localStorage : null)
    const raw = target?.getItem(COMBAT_VFX_STORAGE_KEY)
    if (raw === 'false' || raw === '0') return false
    return true
  } catch {
    return true
  }
}

/** 将特效开关偏好写入 Storage；隐私模式等不可用场景静默跳过。 */
export function persistCombatVfxEnabled(enabled: boolean, storage?: Pick<Storage, 'setItem'> | null): void {
  try {
    const target = storage ?? (typeof window !== 'undefined' ? window.localStorage : null)
    target?.setItem(COMBAT_VFX_STORAGE_KEY, enabled ? 'true' : 'false')
  } catch {
    // 隐私模式等本地存储不可用时仍保留当前会话选择。
  }
}

/** 战斗特效开关全局 Store（持久化到 `localStorage`）。 */
export const useCombatVfxStore = create<CombatVfxStore>(set => ({
  vfxEnabled: readStoredCombatVfxEnabled(),
  setVfxEnabled: enabled => {
    persistCombatVfxEnabled(enabled)
    set({ vfxEnabled: enabled })
  },
  toggleVfxEnabled: () => set(state => {
    const next = !state.vfxEnabled
    persistCombatVfxEnabled(next)
    return { vfxEnabled: next }
  }),
}))

/** 判断当前浏览器文档标签页是否处于可见状态。 */
export function isDocumentCurrentlyVisible(): boolean {
  if (typeof document === 'undefined') return true
  return document.visibilityState !== 'hidden'
}

function subscribeDocumentVisibility(listener: () => void): () => void {
  if (typeof document === 'undefined') return () => {}
  document.addEventListener('visibilitychange', listener)
  return () => document.removeEventListener('visibilitychange', listener)
}

/**
 * 订阅浏览器页签可见性（`document.visibilityState !== 'hidden'`）。
 * 切换浏览器标签页或最小化窗口时自动通知组件停用特效并在恢复时重建基线。
 */
export function useDocumentVisible(): boolean {
  return useSyncExternalStore(
    subscribeDocumentVisibility,
    isDocumentCurrentlyVisible,
    () => true,
  )
}
