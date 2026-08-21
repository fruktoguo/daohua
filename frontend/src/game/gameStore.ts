import { create } from 'zustand'
import { resolveActionDefinitions } from './ids'
import { createDemoSnapshot } from './demo'
import type { ActionDefinition, ActionType, AutoUseSlot, CombatAbilityDefinition, CombatEncounterDefinition, CombatEquipmentDefinition, CombatLoadout, ConsumableSlot, ItemDefinition, PlayerPatch, PlayerSnapshot, QueueState, WorldMapDefinition } from './types'

export type ConnectionState = 'connecting' | 'online' | 'reconnecting' | 'offline'

interface GameStore {
  playerId: string
  name: string
  account: string
  sequenceId: number
  createdAtUnixSeconds: number
  renameCooldownSeconds: number
  visual: PlayerSnapshot['visual']
  version: number
  serverTimeUnixSeconds: number
  serverTimeUnixMilliseconds: number
  lastSyncPerformanceTime: number
  presenceMode: PlayerSnapshot['presenceMode']
  isPendingBreakthrough: boolean
  hasReceivedSnapshot: boolean
  queue: PlayerSnapshot['queue']
  inventory: PlayerSnapshot['inventory']
  buffs: PlayerSnapshot['buffs']
  stats: PlayerSnapshot['stats']
  combat: PlayerSnapshot['combat']
  combatResources: PlayerSnapshot['combatResources']
  combatAttributes: PlayerSnapshot['combatAttributes']
  world: NonNullable<PlayerSnapshot['world']>
  actions: readonly ActionDefinition[]
  items: readonly ItemDefinition[]
  abilities: readonly CombatAbilityDefinition[]
  encounters: readonly CombatEncounterDefinition[]
  equipment: readonly CombatEquipmentDefinition[]
  maps: readonly WorldMapDefinition[]
  connection: ConnectionState
  notice: string | null
  setConnection: (connection: ConnectionState) => void
  setNotice: (notice: string | null) => void
  setActions: (actions: readonly ActionDefinition[]) => void
  setItems: (items: readonly ItemDefinition[]) => void
  setAbilities: (abilities: readonly CombatAbilityDefinition[]) => void
  setEncounters: (encounters: readonly CombatEncounterDefinition[]) => void
  setEquipment: (equipment: readonly CombatEquipmentDefinition[]) => void
  setMaps: (maps: readonly WorldMapDefinition[]) => void
  applySnapshot: (snapshot: PlayerSnapshot) => void
  applyPatch: (patch: PlayerPatch) => void
  resetGameStore: () => void
}

const initial = createDemoSnapshot('local-preview')

const AUTO_USE_SLOT_COUNT = 3
const COMBAT_ABILITY_SLOT_COUNT = 4
const COMBAT_CONSUMABLE_SLOT_COUNT = 3
const ACTION_TYPE_KEYS: readonly ActionType[] = ['cultivation', 'exploration', 'alchemy', 'combat']

type AutoUseSlotWire = AutoUseSlot | AutoUseSlot[] | null | undefined

function normalizeAutoUseSlot(slot: AutoUseSlot | null | undefined): AutoUseSlot {
  return {
    itemId: slot?.itemId ?? null,
    quantity: slot?.quantity ?? 1,
    enabled: slot?.enabled ?? false,
    triggers: slot?.triggers ?? [],
  }
}

function normalizeAutoUseSlots(
  slots: Partial<Record<ActionType, AutoUseSlotWire>> | null | undefined,
): QueueState['autoUseSlots'] {
  const normalized = {} as QueueState['autoUseSlots']
  for (const type of ACTION_TYPE_KEYS) {
    const wire = slots?.[type]
    const list = Array.isArray(wire) ? wire : wire ? [wire] : []
    normalized[type] = Array.from({ length: AUTO_USE_SLOT_COUNT }, (_, index) => normalizeAutoUseSlot(list[index]))
  }
  return normalized
}

function normalizeQueue(queue: PlayerSnapshot['queue']): PlayerSnapshot['queue'] {
  return { ...queue, autoUseSlots: normalizeAutoUseSlots(queue.autoUseSlots as Partial<Record<ActionType, AutoUseSlotWire>>) }
}

type LegacyCombatLoadout = CombatLoadout & {
  foodSlots?: ConsumableSlot[]
  drinkSlots?: ConsumableSlot[]
}

function normalizeCombat(combat: PlayerSnapshot['combat']): PlayerSnapshot['combat'] {
  if (!combat) return combat
  const wire = combat as LegacyCombatLoadout
  const abilitySlots = Array.isArray(wire.abilitySlots) ? wire.abilitySlots : []
  const normalizedAbilitySlots = Array.from({ length: COMBAT_ABILITY_SLOT_COUNT }, (_, index) => ({
    abilityId: abilitySlots[index]?.abilityId ?? null,
    level: Math.max(1, abilitySlots[index]?.level ?? 1),
    triggers: abilitySlots[index]?.triggers ?? [],
  }))
  const learnedAbilityIds = Array.isArray(wire.learnedAbilityIds)
    ? wire.learnedAbilityIds
    : normalizedAbilitySlots.map(slot => slot.abilityId).filter((abilityId): abilityId is string => Boolean(abilityId))
  const consumableSlots = Array.isArray(wire.consumableSlots)
    ? wire.consumableSlots
    : [...(wire.foodSlots ?? []), ...(wire.drinkSlots ?? [])]
  const normalizedConsumableSlots = Array.from({ length: COMBAT_CONSUMABLE_SLOT_COUNT }, (_, index) => ({
    itemId: consumableSlots[index]?.itemId ?? null,
    quantity: Math.max(1, consumableSlots[index]?.quantity ?? 1),
    enabled: consumableSlots[index]?.enabled ?? false,
    triggers: consumableSlots[index]?.triggers ?? [],
  }))
  return {
    ...combat,
    abilitySlots: normalizedAbilitySlots,
    consumableSlots: normalizedConsumableSlots,
    learnedAbilityIds,
  }
}

export const useGameStore = create<GameStore>((set) => ({
  playerId: initial.playerId,
  name: initial.name,
  account: initial.name,
  sequenceId: 0,
  createdAtUnixSeconds: initial.serverTimeUnixSeconds,
  renameCooldownSeconds: 0,
  visual: initial.visual,
  version: initial.version,
  serverTimeUnixSeconds: initial.serverTimeUnixSeconds,
  serverTimeUnixMilliseconds: initial.serverTimeUnixMilliseconds ?? initial.serverTimeUnixSeconds * 1000,
  lastSyncPerformanceTime: performance.now(),
  presenceMode: initial.presenceMode,
  isPendingBreakthrough: false,
  hasReceivedSnapshot: false,
  queue: initial.queue,
  inventory: initial.inventory,
  buffs: initial.buffs,
  stats: initial.stats,
  combat: initial.combat,
  combatResources: initial.combatResources,
  combatAttributes: initial.combatAttributes,
  world: initial.world!,
  actions: resolveActionDefinitions(),
  items: [],
  abilities: [],
  encounters: [],
  equipment: [],
  maps: [],
  connection: 'connecting',
  notice: null,
  setConnection: (connection) => set({ connection }),
  setNotice: (notice) => set({ notice }),
  setActions: (actions) => set({ actions }),
  setItems: (items) => set({ items }),
  setAbilities: (abilities) => set({ abilities }),
  setEncounters: (encounters) => set({ encounters }),
  setEquipment: (equipment) => set({ equipment }),
  setMaps: (maps) => set({ maps }),
  applySnapshot: (snapshot) => set({
    playerId: snapshot.playerId,
    name: snapshot.name,
    account: snapshot.account ?? snapshot.name,
    sequenceId: snapshot.sequenceId ?? 0,
    createdAtUnixSeconds: snapshot.createdAtUnixSeconds ?? snapshot.serverTimeUnixSeconds,
    renameCooldownSeconds: snapshot.renameCooldownSeconds ?? 0,
    hasReceivedSnapshot: true,
    visual: snapshot.visual ?? stateVisualFallback(snapshot.name),
    version: snapshot.version,
    serverTimeUnixSeconds: snapshot.serverTimeUnixSeconds,
    serverTimeUnixMilliseconds: snapshot.serverTimeUnixMilliseconds ?? snapshot.serverTimeUnixSeconds * 1000,
    lastSyncPerformanceTime: performance.now(),
    presenceMode: snapshot.presenceMode,
    isPendingBreakthrough: snapshot.isPendingBreakthrough ?? false,
    queue: normalizeQueue(snapshot.queue),
    inventory: snapshot.inventory,
    buffs: snapshot.buffs,
    stats: snapshot.stats,
    combat: normalizeCombat(snapshot.combat),
    combatResources: snapshot.combatResources,
    combatAttributes: snapshot.combatAttributes,
    world: snapshot.world ?? initial.world!,
  }),
  applyPatch: (patch) => set((state) => {
    if (patch.version < state.version) return state
    const queue = patch.queue ? normalizeQueue(patch.queue) : (patch.queueProgress
      ? {
          ...state.queue,
          entries: state.queue.entries.map(entry => entry.entryId === patch.queueProgress?.entryId
            ? {
                ...entry,
                status: patch.queueProgress.status,
                progressSeconds: patch.queueProgress.progressSeconds,
                durationSeconds: patch.queueProgress.durationSeconds,
                expedition: patch.queueProgress.expedition ?? entry.expedition,
              }
            : entry),
        }
      : state.queue)
    return {
      playerId: patch.playerId || state.playerId,
      name: patch.name ?? state.name,
      account: patch.account ?? state.account,
      sequenceId: patch.sequenceId ?? state.sequenceId,
      createdAtUnixSeconds: patch.createdAtUnixSeconds ?? state.createdAtUnixSeconds,
      renameCooldownSeconds: patch.renameCooldownSeconds ?? state.renameCooldownSeconds,
      visual: patch.visual ?? state.visual,
      version: patch.version,
      serverTimeUnixSeconds: patch.serverTimeUnixSeconds,
      serverTimeUnixMilliseconds: patch.serverTimeUnixMilliseconds ?? patch.serverTimeUnixSeconds * 1000,
      lastSyncPerformanceTime: performance.now(),
      presenceMode: patch.presenceMode ?? state.presenceMode,
      isPendingBreakthrough: patch.isPendingBreakthrough ?? state.isPendingBreakthrough,
      queue,
      inventory: patch.inventory ?? state.inventory,
      buffs: patch.buffs ?? state.buffs,
      stats: patch.stats ?? state.stats,
      combat: normalizeCombat(patch.combat) ?? state.combat,
      combatResources: patch.combatResources ?? state.combatResources,
      combatAttributes: patch.combatAttributes ?? state.combatAttributes,
      world: patch.world ?? state.world,
    }
  }),
  resetGameStore: () => set({
    hasReceivedSnapshot: false,
    connection: 'offline',
    notice: null,
    queue: initial.queue,
    inventory: initial.inventory,
    buffs: [],
    stats: initial.stats,
  }),
}))

function stateVisualFallback(name: string): NonNullable<PlayerSnapshot['visual']> {
  return { renderer: 'text', source: name.slice(0, 1) || '修' }
}
