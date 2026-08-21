import { getAction, tryGetAction } from './catalog'
import { ACTION_IDS, BUFF_IDS, ITEM_IDS } from './ids'
import type { ActionType, PresenceMode } from './ids'
import type {
  BuffState,
  CommandResult,
  DirtySection,
  PlayerState,
  QueuedAction,
  SimulationResult
} from './types'

// DaoxuanSimulation.cs 的忠实移植：行动队列的唯一结算入口。
// 结算按“行动完成边界”推进，不按离线时长逐秒循环，重开页面后可直接快进到当前时间。

const MAX_TRANSITIONS_PER_ADVANCE = 2_000_000
const EVENT_LOG_LIMIT = 40

type Dirty = Set<DirtySection>

function newEntryId(): string {
  return typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
}

function normalizeTime(unixSeconds: number): number {
  return Math.floor(unixSeconds)
}

function formatClock(unixSeconds: number): string {
  const date = new Date(unixSeconds * 1000)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

function addEvent(state: PlayerState, message: string): void {
  state.eventLog.push(`[${formatClock(state.lastSimulatedAt)}] ${message}`)
  if (state.eventLog.length > EVENT_LOG_LIMIT) {
    state.eventLog.splice(0, state.eventLog.length - EVENT_LOG_LIMIT)
  }
}

function touch(state: PlayerState, dirty: Dirty, ...sections: DirtySection[]): void {
  state.version += 1
  for (const section of sections) {
    dirty.add(section)
  }
}

function getItem(state: PlayerState, itemId: string): number {
  return state.inventory[itemId] ?? 0
}

function addItem(state: PlayerState, itemId: string, amount: number): void {
  if (amount === 0) return
  const next = getItem(state, itemId) + amount
  if (next < 0) {
    throw new Error(`物品 ${itemId} 数量不能为负数。`)
  }
  state.inventory[itemId] = next
}

function createQueuedAction(actionId: string, repeats: number): QueuedAction {
  const definition = getAction(actionId)
  return {
    entryId: newEntryId(),
    actionId,
    totalRepeats: repeats,
    remainingRepeats: repeats,
    progressSeconds: 0,
    durationSeconds: definition.durationSeconds,
    status: 'queued'
  }
}

function ok(message: string, dirty: Dirty): CommandResult {
  return { success: true, code: 'OK', message, dirty }
}

function fail(code: string, message: string): CommandResult {
  return { success: false, code, message, dirty: new Set<DirtySection>() }
}

export function createDefaultState(playerId: string, name?: string, now?: number): PlayerState {
  const timestamp = normalizeTime(now ?? Date.now() / 1000)
  const state: PlayerState = {
    playerId,
    name: name?.trim() || '无名修士',
    version: 0,
    lastSimulatedAt: timestamp,
    presenceMode: 'online',
    queue: {
      entries: [],
      loopEnabled: true,
      autoUseSlots: {
        cultivation: { itemId: ITEM_IDS.qiGatheringPill, quantity: 1, enabled: true },
        exploration: { itemId: null, quantity: 1, enabled: false },
        alchemy: { itemId: null, quantity: 1, enabled: false }
      }
    },
    inventory: {
      [ITEM_IDS.spiritHerb]: 0,
      [ITEM_IDS.qiGatheringPill]: 0,
      [ITEM_IDS.spirit]: 0,
      [ITEM_IDS.qi]: 0
    },
    buffs: [],
    stats: {
      qi: 0,
      spirit: 0,
      herbsCollected: 0,
      pillsCrafted: 0,
      cultivationCycles: 0,
      completedActions: 0,
      blockedActions: 0
    },
    eventLog: []
  }

  state.queue.entries.push(createQueuedAction(ACTION_IDS.gatherHerbs, 1))
  state.queue.entries.push(createQueuedAction(ACTION_IDS.refineQiPill, 1))
  state.queue.entries.push(createQueuedAction(ACTION_IDS.drawQi, 1))
  state.eventLog.push(`[${formatClock(timestamp)}] 行动队列已建立：采集 → 炼丹 → 修炼`)
  return state
}

export function enqueue(state: PlayerState, actionId: string, repeats = 1): CommandResult {
  if (!tryGetAction(actionId)) {
    return fail('ACTION_NOT_FOUND', '行动不存在。')
  }
  if (repeats !== -1 && (repeats < 1 || repeats > 1000)) {
    return fail('INVALID_REPEATS', '重复次数必须在 1 到 1000 之间。')
  }

  const definition = getAction(actionId)
  const action = createQueuedAction(actionId, repeats)
  const dirty: Dirty = new Set()
  state.queue.entries.push(action)
  touch(state, dirty, 'queue', 'events')
  addEvent(state, `已加入行动：${definition.name} × ${repeats === -1 ? '∞' : repeats}`)
  return ok('行动已加入队列。', dirty)
}

export function remove(state: PlayerState, entryId: string): CommandResult {
  const index = state.queue.entries.findIndex(action => action.entryId === entryId)
  if (index < 0) {
    return fail('ACTION_NOT_FOUND', '队列中没有找到该行动。')
  }

  const dirty: Dirty = new Set()
  state.queue.entries.splice(index, 1)
  touch(state, dirty, 'queue', 'events')
  addEvent(state, '已从队列移除行动')
  return ok('行动已移除。', dirty)
}

export function move(state: PlayerState, entryId: string, direction: -1 | 1): CommandResult {
  const index = state.queue.entries.findIndex(action => action.entryId === entryId)
  const targetIndex = index + direction
  if (index < 0 || targetIndex < 0 || targetIndex >= state.queue.entries.length) {
    return fail('QUEUE_EDGE', '行动已经在队列边界。')
  }

  const dirty: Dirty = new Set()
  const [entry] = state.queue.entries.splice(index, 1)
  state.queue.entries.splice(targetIndex, 0, entry)
  touch(state, dirty, 'queue')
  return ok('队列顺序已更新。', dirty)
}

export function setAutoUse(
  state: PlayerState,
  actionType: ActionType,
  itemId: string | null,
  quantity: number,
  enabled: boolean
): CommandResult {
  if (quantity < 1 || quantity > 99) {
    return fail('INVALID_QUANTITY', '使用数量必须在 1 到 99 之间。')
  }
  if (itemId && itemId !== ITEM_IDS.qiGatheringPill) {
    return fail('ITEM_NOT_SUPPORTED', '当前行动暂不支持该物品。')
  }

  const dirty: Dirty = new Set()
  state.queue.autoUseSlots[actionType] = {
    itemId,
    quantity,
    enabled: enabled && !!itemId
  }
  touch(state, dirty, 'queue')
  return ok('自动使用槽已更新。', dirty)
}

export function setLoop(state: PlayerState, enabled: boolean): CommandResult {
  const dirty: Dirty = new Set()
  state.queue.loopEnabled = enabled
  touch(state, dirty, 'queue')
  return ok(enabled ? '循环已开启。' : '循环已关闭。', dirty)
}

export function setPresence(state: PlayerState, mode: PresenceMode, now: number): CommandResult {
  const normalizedNow = normalizeTime(now)
  const dirty: Dirty = new Set()
  if (state.presenceMode !== 'fullyOffline') {
    const result = advanceTo(state, normalizedNow)
    for (const section of result.dirty) dirty.add(section)
  }
  state.presenceMode = mode
  touch(state, dirty, 'presence')
  return ok(`状态已切换为 ${mode}。`, dirty)
}

export function advanceTo(state: PlayerState, targetTime: number): SimulationResult {
  const target = normalizeTime(targetTime)
  let cursor = normalizeTime(state.lastSimulatedAt)
  const dirty: Dirty = new Set()
  const events: string[] = []
  let completedActions = 0
  let transitions = 0

  if (target <= cursor) {
    return { dirty, completedActions, transitions, events }
  }

  if (state.presenceMode === 'fullyOffline') {
    // 完全离线账号不参与行动结算；推进时间戳即可避免重新启用时追溯这段时间。
    state.lastSimulatedAt = target
    return { dirty, completedActions, transitions, events }
  }

  while (cursor < target && transitions < MAX_TRANSITIONS_PER_ADVANCE) {
    if (expireBuffs(state, cursor)) {
      dirty.add('buffs')
    }

    if (state.queue.entries.length === 0) {
      cursor = target
      break
    }

    const current = state.queue.entries[0]
    const definition = getAction(current.actionId)
    current.durationSeconds = definition.durationSeconds
    const wasBlocked = current.status === 'blocked'
    current.status = 'running'

    if (!hasInputs(state, definition.inputs)) {
      current.status = 'blocked'
      if (!wasBlocked) {
        state.stats.blockedActions += 1
        addEvent(state, `行动暂停：缺少${definition.name}所需材料`)
        dirty.add('queue').add('stats').add('events')
        events.push(`blocked:${definition.id}`)
      }
      cursor = target
      break
    }

    const remainingSeconds = Math.max(1, definition.durationSeconds - current.progressSeconds)
    const finishAt = cursor + remainingSeconds
    if (finishAt > target) {
      const progress = Math.max(0, Math.floor(target - cursor))
      if (progress > 0) {
        current.progressSeconds = Math.min(definition.durationSeconds, current.progressSeconds + progress)
        dirty.add('queue')
      }
      cursor = target
      break
    }

    cursor = finishAt
    state.lastSimulatedAt = cursor
    current.progressSeconds = definition.durationSeconds
    completeAction(state, current.entryId, definition, dirty, events)
    completedActions += 1
    transitions += 1
  }

  if (transitions >= MAX_TRANSITIONS_PER_ADVANCE) {
    addEvent(state, '本次快进达到保护上限，剩余行动将在下一次推进继续结算')
    dirty.add('events')
  }

  if (dirty.size > 0) {
    state.version += 1
  }
  state.lastSimulatedAt = target
  return { dirty, completedActions, transitions, events }
}

function completeAction(
  state: PlayerState,
  entryId: string,
  definition: ReturnType<typeof getAction>,
  dirty: Dirty,
  events: string[]
): void {
  const current = state.queue.entries[0]
  for (const [itemId, amount] of Object.entries(definition.inputs)) {
    addItem(state, itemId, -amount)
  }
  for (const [itemId, amount] of Object.entries(definition.outputs)) {
    addItem(state, itemId, amount)
  }

  switch (definition.type) {
    case 'exploration':
      state.stats.herbsCollected += definition.outputs[ITEM_IDS.spiritHerb] ?? 0
      break
    case 'alchemy':
      state.stats.pillsCrafted += 1
      break
    case 'cultivation': {
      const multiplier = getQiGainMultiplier(state, state.lastSimulatedAt)
      state.stats.cultivationCycles += 1
      state.stats.qi += Math.round(10 * multiplier)
      state.stats.spirit += 1
      if (tryConsumeAutoUseItem(state, 'cultivation', state.lastSimulatedAt)) {
        dirty.add('inventory').add('buffs')
        addEvent(state, '修炼自动服用引气丹，获得引气增益')
      }
      dirty.add('stats')
      break
    }
  }

  state.stats.completedActions += 1
  dirty.add('queue').add('inventory').add('stats').add('events')
  addEvent(state, `行动完成：${definition.name}`)
  events.push(`completed:${definition.id}`)

  const isInfinite = current.remainingRepeats < 0
  if (isInfinite || current.remainingRepeats > 1) {
    if (!isInfinite) {
      current.remainingRepeats -= 1
    }
    current.progressSeconds = 0
    current.status = 'queued'
    return
  }

  // 循环开启时，完成的行动按总次数重置后挪到队尾，而不是从队列消失。
  if (state.queue.loopEnabled) {
    current.remainingRepeats = current.totalRepeats
    current.progressSeconds = 0
    current.status = 'queued'
    state.queue.entries.push(state.queue.entries.shift()!)
    return
  }

  const removeIndex = state.queue.entries.findIndex(action => action.entryId === entryId)
  if (removeIndex >= 0) {
    state.queue.entries.splice(removeIndex, 1)
  }
}

function tryConsumeAutoUseItem(state: PlayerState, actionType: ActionType, now: number): boolean {
  const slot = state.queue.autoUseSlots[actionType]
  if (!slot || !slot.enabled || !slot.itemId || getItem(state, slot.itemId) < slot.quantity) {
    return false
  }

  addItem(state, slot.itemId, -slot.quantity)
  const existing = state.buffs.find(buff => buff.buffId === BUFF_IDS.qiGathering)
  if (!existing) {
    const buff: BuffState = {
      buffId: BUFF_IDS.qiGathering,
      name: '引气增益',
      stacks: 1,
      qiGainMultiplier: 1.25,
      expiresAt: now + 60
    }
    state.buffs.push(buff)
  } else {
    existing.stacks = Math.min(5, existing.stacks + 1)
    existing.expiresAt = now + 60
    existing.qiGainMultiplier = Math.min(2.5, 1 + existing.stacks * 0.25)
  }
  return true
}

function hasInputs(state: PlayerState, inputs: Record<string, number>): boolean {
  return Object.entries(inputs).every(([itemId, amount]) => getItem(state, itemId) >= amount)
}

function expireBuffs(state: PlayerState, now: number): boolean {
  const before = state.buffs.length
  state.buffs = state.buffs.filter(buff => buff.expiresAt > now)
  return state.buffs.length !== before
}

function getQiGainMultiplier(state: PlayerState, now: number): number {
  let multiplier = 1
  for (const buff of state.buffs) {
    if (buff.expiresAt > now && buff.qiGainMultiplier > multiplier) {
      multiplier = buff.qiGainMultiplier
    }
  }
  return multiplier
}
