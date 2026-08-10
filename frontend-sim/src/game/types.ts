import type { ActionStatus, ActionType, PresenceMode } from './ids'

// 与 XiuxianModels.cs / XiuxianContracts.cs 对应的类型。
// 时间一律用 unix 秒（number），对应 C# 侧 NormalizeTime 截断到秒。

export interface ActionDefinition {
  id: string
  name: string
  description: string
  type: ActionType
  durationSeconds: number
  inputs: Record<string, number>
  outputs: Record<string, number>
}

export interface QueuedAction {
  entryId: string
  actionId: string
  /** 入队时的总次数；-1 表示无限。循环模式下用完后按此值重置并挪到队尾。 */
  totalRepeats: number
  remainingRepeats: number
  progressSeconds: number
  durationSeconds: number
  status: ActionStatus
}

export interface AutoUseSlot {
  itemId: string | null
  quantity: number
  enabled: boolean
}

export interface ActionQueueState {
  entries: QueuedAction[]
  loopEnabled: boolean
  autoUseSlots: Record<ActionType, AutoUseSlot>
}

export interface BuffState {
  buffId: string
  name: string
  stacks: number
  qiGainMultiplier: number
  expiresAt: number
}

export interface PlayerStats {
  qi: number
  spirit: number
  herbsCollected: number
  pillsCrafted: number
  cultivationCycles: number
  completedActions: number
  blockedActions: number
}

export interface PlayerState {
  playerId: string
  name: string
  version: number
  lastSimulatedAt: number
  presenceMode: PresenceMode
  queue: ActionQueueState
  inventory: Record<string, number>
  buffs: BuffState[]
  stats: PlayerStats
  eventLog: string[]
}

// 脏区段：对应 XiuxianDirtySections，store 用它做细粒度刷新。
export type DirtySection = 'queue' | 'inventory' | 'buffs' | 'stats' | 'events' | 'presence'

export interface CommandResult {
  success: boolean
  code: string
  message: string
  dirty: ReadonlySet<DirtySection>
}

export interface SimulationResult {
  dirty: ReadonlySet<DirtySection>
  completedActions: number
  transitions: number
  events: string[]
}
