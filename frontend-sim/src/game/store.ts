import { create } from 'zustand'
import {
  advanceTo,
  createDefaultState,
  enqueue,
  move,
  remove,
  setAutoUse,
  setLoop
} from './simulation'
import { ACTION_TYPES } from './ids'
import type { ActionType } from './ids'
import type { CommandResult, DirtySection, PlayerState } from './types'

// 模拟状态放在 React 之外的可变单例里，zustand 只保存各脏区段的版本号。
// 组件订阅自己关心的区段版本号，tick 高频推进时不会整棵树重渲染。

const SAVE_KEY = 'daoxuan-save-v1'

export const sim: { state: PlayerState } = {
  state: loadOrCreate()
}

function loadOrCreate(): PlayerState {
  try {
    const raw = localStorage.getItem(SAVE_KEY)
    if (raw) {
      const parsed = JSON.parse(raw) as PlayerState
      if (parsed && parsed.queue && parsed.inventory && parsed.stats) {
        const state = migrate(parsed)
        // 启动即离线快进：把离开期间的时间一次性补全。
        const result = advanceTo(state, nowSeconds())
        if (result.transitions > 0) {
          console.info(`[daoxuan] 离线快进完成：${result.transitions} 次行动结算`)
        }
        return state
      }
    }
  } catch (error) {
    console.warn('[daoxuan] 存档读取失败，已新建存档', error)
  }
  return createDefaultState(crypto.randomUUID?.() ?? `player-${Date.now()}`)
}

// 旧存档补默认字段，避免结构升级后炸档。
function migrate(state: PlayerState): PlayerState {
  state.buffs ??= []
  state.eventLog ??= []
  state.queue.autoUseSlots ??= {
    cultivation: { itemId: null, quantity: 1, enabled: false },
    exploration: { itemId: null, quantity: 1, enabled: false },
    alchemy: { itemId: null, quantity: 1, enabled: false }
  }
  for (const type of ACTION_TYPES) {
    state.queue.autoUseSlots[type] ??= { itemId: null, quantity: 1, enabled: false }
  }
  // 循环改为“完成挪队尾”后引入的字段。
  for (const entry of state.queue.entries) {
    entry.totalRepeats ??= entry.remainingRepeats
  }
  return state
}

export function nowSeconds(): number {
  return Date.now() / 1000
}

let saveBlocked = false

export function saveNow(): void {
  if (saveBlocked) return
  try {
    localStorage.setItem(SAVE_KEY, JSON.stringify(sim.state))
  } catch (error) {
    console.warn('[daoxuan] 存档写入失败', error)
  }
}

export interface SectionVersions {
  queue: number
  inventory: number
  buffs: number
  stats: number
  events: number
  presence: number
}

interface SimStore extends SectionVersions {
  /** 低频时钟（约 2Hz），供倒计时、进度条这类持续变化的展示订阅。 */
  clock: number
  lastMessage: string
}

const initialVersions: SectionVersions = {
  queue: 0,
  inventory: 0,
  buffs: 0,
  stats: 0,
  events: 0,
  presence: 0
}

export const useSimStore = create<SimStore>(() => ({
  ...initialVersions,
  clock: 0,
  lastMessage: ''
}))

function bump(dirty: ReadonlySet<DirtySection>, message?: string): void {
  if (dirty.size === 0 && !message) return
  useSimStore.setState(state => {
    const patch: Partial<SimStore> = {}
    for (const section of dirty) {
      patch[section] = state[section] + 1
    }
    if (message) {
      patch.lastMessage = message
    }
    return patch
  })
}

function dispatch(result: CommandResult): CommandResult {
  bump(result.dirty, result.message)
  scheduleSave()
  return result
}

// ---- 引擎：250ms 结算一次，2Hz 时钟刷新展示，2s 自动存档 ----

let engineStarted = false
let saveTimer: ReturnType<typeof setTimeout> | undefined

function scheduleSave(): void {
  if (saveTimer) return
  saveTimer = setTimeout(() => {
    saveTimer = undefined
    saveNow()
  }, 500)
}

export function startEngine(): void {
  if (engineStarted) return
  engineStarted = true

  const tick = () => {
    const result = advanceTo(sim.state, nowSeconds())
    bump(result.dirty)
  }

  setInterval(tick, 250)
  setInterval(() => {
    useSimStore.setState({ clock: Math.floor(nowSeconds()) })
  }, 500)
  setInterval(saveNow, 5_000)

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      saveNow()
    } else {
      tick()
    }
  })
  window.addEventListener('beforeunload', saveNow)
}

// ---- UI 指令入口：与 DaoxuanContracts.cs 的请求一一对应 ----

export const gameCommands = {
  enqueue: (actionId: string, repeats: number) => dispatch(enqueue(sim.state, actionId, repeats)),
  remove: (entryId: string) => dispatch(remove(sim.state, entryId)),
  move: (entryId: string, direction: -1 | 1) => dispatch(move(sim.state, entryId, direction)),
  setAutoUse: (actionType: ActionType, itemId: string | null, quantity: number, enabled: boolean) =>
    dispatch(setAutoUse(sim.state, actionType, itemId, quantity, enabled)),
  setLoop: (enabled: boolean) => dispatch(setLoop(sim.state, enabled))
}

// ---- 调试：演示离线快进 ----

export function debugFastForward(seconds: number): void {
  const result = advanceTo(sim.state, nowSeconds() + seconds)
  bump(result.dirty, `已快进 ${Math.round(seconds / 60)} 分钟，结算 ${result.transitions} 次行动`)
  scheduleSave()
}

export function debugSimulateOfflineGap(seconds: number): void {
  saveNow()
  sim.state.lastSimulatedAt -= seconds
  const result = advanceTo(sim.state, nowSeconds())
  bump(result.dirty, `模拟服务器停机 ${Math.round(seconds / 60)} 分钟后补全：结算 ${result.transitions} 次行动`)
  scheduleSave()
}

export function debugResetSave(): void {
  // 先封锁写入：reload 触发的 beforeunload 会把旧状态重新写回去。
  saveBlocked = true
  localStorage.removeItem(SAVE_KEY)
  location.reload()
}
