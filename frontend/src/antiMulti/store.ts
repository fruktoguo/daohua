import { create } from 'zustand'

/** 标签互斥开关的本地缓存键：服务端经 HelloAck 下发的最新值持久化于此，供连接建立前的守卫读取 */
const TAB_GUARD_STORAGE_KEY = 'daohua.antiMulti.tabGuardEnabled'

function readTabGuardEnabled(): boolean {
  try {
    const stored = window.localStorage.getItem(TAB_GUARD_STORAGE_KEY)
    return stored === null ? true : stored === '1'
  } catch {
    return true
  }
}

interface AntiMultiOpenState {
  /** 当前标签页是否被互斥守卫阻断（被顶替或检测到已有运行页） */
  tabBlocked: boolean
  /** 多标签页互斥守卫是否启用；默认启用，服务端 HelloAck 下发后持久化 */
  tabGuardEnabled: boolean
  /** 标记当前标签页进入/退出阻断态 */
  setTabBlocked: (tabBlocked: boolean) => void
  /** 更新守卫开关并写入本地缓存 */
  setTabGuardEnabled: (tabGuardEnabled: boolean) => void
}

/** 防多开模块独立状态存储，与游戏主状态解耦 */
export const useAntiMultiOpenStore = create<AntiMultiOpenState>(set => ({
  tabBlocked: false,
  tabGuardEnabled: readTabGuardEnabled(),
  setTabBlocked: tabBlocked => set({ tabBlocked }),
  setTabGuardEnabled: tabGuardEnabled => {
    try {
      window.localStorage.setItem(TAB_GUARD_STORAGE_KEY, tabGuardEnabled ? '1' : '0')
    } catch {
      // localStorage 不可用时仅保留内存态。
    }
    set({ tabGuardEnabled })
  },
}))
