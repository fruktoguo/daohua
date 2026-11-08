import { TECHNIQUE_IDS } from './combatConstants'
import type { ActionDefinition, AutoUseBarType, ItemDefinition, QueueEntry } from './types'

export type SustainedDurationClock =
  | { mode: 'frozen' }
  | { mode: 'action'; progressAtSyncSeconds: number; elapsedSinceSyncSeconds: number; durationSeconds: number }
  | { mode: 'combat'; elapsedSinceSyncSeconds: number }

/** 判定持续消耗品是否会被当前行动实际消耗，保持与服务端生效守卫一致。 */
export function hasActiveSustainedEffect(
  item: ItemDefinition | undefined,
  action: ActionDefinition | null,
  bar: AutoUseBarType,
): boolean {
  if (!item || item.isContinuous !== true || (item.durationSeconds ?? 0) <= 0 || item.durable === true
    || !item.usableBars.includes(bar)) return false
  if (bar === 'combat') return true
  if (!action) return false

  const multiplier = action.type === 'cultivation' ? item.cultivationMultiplier
    : action.type === 'exploration' ? item.gatheringMultiplier
      : action.type === 'alchemy' ? item.alchemyMultiplier
        : undefined
  if (multiplier !== undefined && multiplier > 1) return true
  if (item.techniqueSpeedRatio && item.techniqueSpeedRatio > 0
    && action.technique != null && (TECHNIQUE_IDS as readonly string[]).includes(action.technique)) return true
  return item.enhancementSuccessBonus !== undefined && item.enhancementSuccessBonus > 0
    && action.type === 'enhancement'
}

/** 将非战斗行动进度、战斗存活时钟和队列暂停统一为槽位显示时钟。 */
export function resolveSustainedDurationClock(
  bar: AutoUseBarType,
  item: ItemDefinition | undefined,
  action: ActionDefinition | null,
  entry: QueueEntry | null,
  queuePaused: boolean,
  elapsedSinceSyncSeconds: number,
): SustainedDurationClock {
  if (queuePaused || !entry || entry.status !== 'running') return { mode: 'frozen' }

  if (bar === 'combat') {
    if (entry.expedition?.phase !== 'combat') return { mode: 'frozen' }
    return { mode: 'combat', elapsedSinceSyncSeconds: Math.max(0, elapsedSinceSyncSeconds) }
  }

  if (!hasActiveSustainedEffect(item, action, bar)) return { mode: 'frozen' }
  return {
    mode: 'action',
    progressAtSyncSeconds: Math.max(0, entry.progressSeconds),
    elapsedSinceSyncSeconds: Math.max(0, elapsedSinceSyncSeconds),
    durationSeconds: Math.max(0, entry.durationSeconds),
  }
}
