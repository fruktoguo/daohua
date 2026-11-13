import { create } from 'zustand'
import type {
  SimulatorEnvironmentDto,
  SimulatorProfileDto,
  SimulatorResolveResultDto,
  SimulatorRunResultDto,
} from './simulatorTypes'

/** 模拟器顶层页签（配置 / 结果） */
export type SimulatorTopTab = 'config' | 'results'

/** 成员配置二级页签（基础与特质 / 详细属性 / 装备 / 功法 / 消耗品） */
export type SimulatorProfileSubTab =
  | 'profile'
  | 'attributes'
  | 'equipment'
  | 'abilities'
  | 'consumables'

export interface SimulatorRunStamp {
  dirty: number
  seed: string
  startedAtUnixSeconds: number
}

export function generateSimulatorSeed(): string {
  if (typeof globalThis.crypto?.getRandomValues === 'function') {
    const arr = new Uint32Array(2)
    globalThis.crypto.getRandomValues(arr)
    const val = (BigInt(arr[0]) << 32n) | BigInt(arr[1])
    return (val & 0x7FFFFFFFFFFFFFFFn).toString()
  }
  return String(Date.now())
}

type Updater<T> = T | ((prev: T) => T)

function resolveUpdater<T>(next: Updater<T>, prev: T): T {
  return typeof next === 'function' ? (next as (p: T) => T)(prev) : next
}

export interface SimulatorSessionState {
  initialized: boolean
  activeTab: SimulatorTopTab
  profileSubTab: SimulatorProfileSubTab
  selectedMemberIndex: number
  members: SimulatorProfileDto[]
  environment: SimulatorEnvironmentDto | null
  combatNodeIds: string[] | null
  selectedMapId: string
  selectedNodeIds: string[]
  difficulties: number[]
  durationSeconds: number
  seed: string
  results: SimulatorRunResultDto[] | null
  runStamp: SimulatorRunStamp | null
  preview: SimulatorResolveResultDto | null
  configDirty: number

  setInitialized: (initialized: boolean) => void
  setActiveTab: (activeTab: Updater<SimulatorTopTab>) => void
  setProfileSubTab: (subTab: Updater<SimulatorProfileSubTab>) => void
  setSelectedMemberIndex: (index: Updater<number>) => void
  setMembers: (members: Updater<SimulatorProfileDto[]>) => void
  setEnvironment: (env: Updater<SimulatorEnvironmentDto | null>) => void
  setCombatNodeIds: (nodeIds: Updater<string[] | null>) => void
  setSelectedMapId: (mapId: Updater<string>) => void
  setSelectedNodeIds: (nodeIds: Updater<string[]>) => void
  setDifficulties: (difficulties: Updater<number[]>) => void
  setDurationSeconds: (seconds: Updater<number>) => void
  setSeed: (seed: Updater<string>) => void
  setResults: (results: Updater<SimulatorRunResultDto[] | null>) => void
  setRunStamp: (stamp: Updater<SimulatorRunStamp | null>) => void
  setPreview: (preview: Updater<SimulatorResolveResultDto | null>) => void
  setConfigDirty: (dirty: Updater<number>) => void
  resetSession: () => void
}

/**
 * 战斗模拟器会话级内存状态 Store
 *
 * 弹窗关闭时只中断进行中的 Worker 批次，不销毁成员配置、目标组合、当前页签与上次已完成结果；
 * 仅在页面刷新或显式点击重置时清空重捕。
 */
export const useSimulatorStore = create<SimulatorSessionState>((set) => ({
  initialized: false,
  activeTab: 'config',
  profileSubTab: 'profile',
  selectedMemberIndex: 0,
  members: [],
  environment: null,
  combatNodeIds: null,
  selectedMapId: 'all',
  selectedNodeIds: [],
  difficulties: [0],
  durationSeconds: 3600,
  seed: generateSimulatorSeed(),
  results: null,
  runStamp: null,
  preview: null,
  configDirty: 0,

  setInitialized: (initialized) => set({ initialized }),
  setActiveTab: (next) => set((s) => ({ activeTab: resolveUpdater(next, s.activeTab) })),
  setProfileSubTab: (next) => set((s) => ({ profileSubTab: resolveUpdater(next, s.profileSubTab) })),
  setSelectedMemberIndex: (next) => set((s) => ({ selectedMemberIndex: resolveUpdater(next, s.selectedMemberIndex) })),
  setMembers: (next) => set((s) => ({ members: resolveUpdater(next, s.members) })),
  setEnvironment: (next) => set((s) => ({ environment: resolveUpdater(next, s.environment) })),
  setCombatNodeIds: (next) => set((s) => ({ combatNodeIds: resolveUpdater(next, s.combatNodeIds) })),
  setSelectedMapId: (next) => set((s) => ({ selectedMapId: resolveUpdater(next, s.selectedMapId) })),
  setSelectedNodeIds: (next) => set((s) => ({ selectedNodeIds: resolveUpdater(next, s.selectedNodeIds) })),
  setDifficulties: (next) => set((s) => ({ difficulties: resolveUpdater(next, s.difficulties) })),
  setDurationSeconds: (next) => set((s) => ({ durationSeconds: resolveUpdater(next, s.durationSeconds) })),
  setSeed: (next) => set((s) => ({ seed: resolveUpdater(next, s.seed) })),
  setResults: (next) => set((s) => ({ results: resolveUpdater(next, s.results) })),
  setRunStamp: (next) => set((s) => ({ runStamp: resolveUpdater(next, s.runStamp) })),
  setPreview: (next) => set((s) => ({ preview: resolveUpdater(next, s.preview) })),
  setConfigDirty: (next) => set((s) => ({ configDirty: resolveUpdater(next, s.configDirty) })),
  resetSession: () =>
    set({
      initialized: false,
      activeTab: 'config',
      profileSubTab: 'profile',
      selectedMemberIndex: 0,
      members: [],
      environment: null,
      combatNodeIds: null,
      selectedMapId: 'all',
      selectedNodeIds: [],
      difficulties: [0],
      durationSeconds: 3600,
      seed: generateSimulatorSeed(),
      results: null,
      runStamp: null,
      preview: null,
      configDirty: 0,
    }),
}))
