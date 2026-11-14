import { ENHANCE_ACTION_ID, TECHNIQUE_IDS } from './combatConstants'
import type { ActionDefinition, AutoUseBarType, AutoUseSlot, InventoryItem, ItemDefinition, QueueEntry } from './types'

export type SustainedDurationClock =
  | { mode: 'frozen' }
  | { mode: 'action'; progressAtSyncSeconds: number; elapsedSinceSyncSeconds: number; durationSeconds: number }
  | { mode: 'combat'; elapsedSinceSyncSeconds: number }

export interface EnhancementConsumableBonusItem {
  itemId: string
  name: string
  bonus: number
}

export interface EffectiveEnhancementConsumables {
  totalBonus: number
  items: EnhancementConsumableBonusItem[]
}

function getUnvariantInventoryAmount(
  inventory: readonly InventoryItem[] | Readonly<Record<string, number>>,
  itemId: string,
): number {
  if (Array.isArray(inventory)) {
    let total = 0
    for (const entry of inventory) {
      if (entry.itemId === itemId && !entry.variant) total += entry.amount
    }
    return total
  }
  return (inventory as Readonly<Record<string, number>>)[itemId] ?? 0
}

/**
 * 统计强化栏位当前可即时生效的成功率加成（含已充能残余与开轮可自动充能的库存）：
 * 与服务端 EffectiveEnhancementSuccessBonus + PrepareSustainedAutoUse 同口径，按 itemId 预约库存防止重复计数。
 */
export function resolveEffectiveEnhancementSuccessBonuses(
  slots: readonly AutoUseSlot[] | undefined,
  items: readonly ItemDefinition[],
  inventory: readonly InventoryItem[] | Readonly<Record<string, number>>,
): EffectiveEnhancementConsumables {
  if (!slots || slots.length === 0) return { totalBonus: 0, items: [] }
  const itemById = new Map(items.map(item => [item.itemId, item]))
  const reservedByItem = new Map<string, number>()
  const bonusItems: EnhancementConsumableBonusItem[] = []
  let totalBonus = 0

  for (const slot of slots) {
    if (!slot.enabled || !slot.itemId) continue
    const item = itemById.get(slot.itemId)
    const bonus = item?.enhancementSuccessBonus
    if (!item || bonus === undefined || !Number.isFinite(bonus) || bonus <= 0) continue

    const remaining = slot.remainingDurationSeconds ?? 0
    if (remaining <= 0) {
      const canActivate = item.isContinuous === true
        && (item.durationSeconds ?? 0) > 0
        && item.durable !== true
        && item.usableBars.includes('enhancement')
      if (!canActivate) continue
      const reserved = reservedByItem.get(item.itemId) ?? 0
      if (getUnvariantInventoryAmount(inventory, item.itemId) - reserved + 1e-12 < 1) continue
      reservedByItem.set(item.itemId, reserved + 1)
    }

    totalBonus += bonus
    bonusItems.push({ itemId: item.itemId, name: item.name, bonus })
  }

  return { totalBonus, items: bonusItems }
}

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
  // 强化成功率加成仅装备强化结算消费；秘法行动同属 enhancement 大类但无成功率判定。
  return item.enhancementSuccessBonus !== undefined && item.enhancementSuccessBonus > 0
    && action.id === ENHANCE_ACTION_ID
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
