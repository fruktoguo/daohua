import type {
  ActionDefinition,
  CombatEquipmentDefinition,
  EquippedItem,
  InventoryItem,
  QueueEntry,
} from './types'
import {
  CONVERT_ACTION_ID,
  DISASSEMBLE_ACTION_ID,
  ENHANCE_ACTION_ID,
  ENHANCE_MIN_PROTECT_LEVEL,
  LIQUEFY_ACTION_ID,
  SPIRIT_STONE_ITEM_ID,
  calculateRealmProgress,
  enhanceSpiritStoneCost,
  serverExpToNextCap,
} from './combatConstants'
import {
  getEquipmentMap,
  parseEnhancementVariant,
} from './ids'

export type QueueEstimateTranslateFn = (
  key: string,
  params?: Record<string, string | number>,
) => string

/** 行动估算所需的最小角色数值字段 */
export interface QueueEstimateStats {
  foundation?: number
  experience?: number
  realmLevel?: number
  expToNextLinggen?: number
}

/** 顶部当前行动栏的次数与耗时估算输入 */
export interface CalculateQueueActionEstimateInput {
  entry: QueueEntry
  action?: ActionDefinition
  inventory: readonly InventoryItem[]
  combatEquipment?: readonly EquippedItem[]
  queueEntries?: readonly QueueEntry[]
  equipmentCatalog?: readonly CombatEquipmentDefinition[]
  stats?: QueueEstimateStats | null
}

/** 顶部当前行动栏的次数与耗时估算结果 */
export interface QueueActionEstimate {
  /** 是否需要在次数旁附带估算信息（战斗/赶路/无限采集修炼为 false） */
  hasEstimate: boolean
  /** 队列条目本身是否为无限轮次 */
  isInfinite: boolean
  /** 服务端下发的剩余轮次（-1 表示无限） */
  remainingRepeats: number
  /** 当前资源下实际可执行的总轮次 */
  effectiveCount: number
  /** 是否需要显示 xN 次数（无限消耗型行动，或有限行动受材料截断时为 true） */
  showEffectiveCount: boolean
  /** 服务端已含全部加速的单轮真实耗时（秒） */
  roundDurationSeconds: number
  /** 基于快照时刻的剩余总耗时（秒）；无耗时估算时为 0 */
  baseRemainingSeconds: number
}

const SECRET_ART_ACTION_IDS: ReadonlySet<string> = new Set([
  LIQUEFY_ACTION_ID,
  DISASSEMBLE_ACTION_ID,
  CONVERT_ACTION_ID,
])

type ResourceBoundResult =
  | { kind: 'excluded' }
  | { kind: 'unbounded' }
  | { kind: 'bounded'; maxSupported: number }

/**
 * 判定当前队列行动对背包/占用的依赖及对底蕴的依赖，
 * 供 UI 订阅裁剪使用，避免无关状态变化触发不必要重算。
 */
export function getQueueEstimateDependencies(
  entry: QueueEntry,
  action?: ActionDefinition,
): { needsInventory: boolean; needsFoundation: boolean; isExcluded: boolean } {
  if (
    entry.type === 'combat' ||
    entry.executionKind === 'combat-encounter' ||
    entry.executionKind === 'travel'
  ) {
    return { needsInventory: false, needsFoundation: false, isExcluded: true }
  }
  const isEnhance = entry.actionId === ENHANCE_ACTION_ID
  const isSecretArt = SECRET_ART_ACTION_IDS.has(entry.actionId) && Boolean(entry.targetItemId)
  const hasInputs = Boolean(
    action?.inputs &&
    Object.values(action.inputs).some(amount => typeof amount === 'number' && Number.isFinite(amount) && amount > 0),
  )
  const needsFoundation = Boolean(
    action?.foundationCostRatio &&
    typeof action.foundationCostRatio === 'number' &&
    Number.isFinite(action.foundationCostRatio) &&
    action.foundationCostRatio > 0,
  )
  return {
    needsInventory: isEnhance || isSecretArt || hasInputs,
    needsFoundation,
    isExcluded: false,
  }
}

/**
 * 统计背包中指定物品及强化等级的可支配数量（扣除已穿戴与强化队列占用）。
 * 普通物品与货币仅按基础行计数；装备按 `itemId#enhanceLevel` 扣减占用。
 */
function getAvailableInventoryAmount(
  inventory: readonly InventoryItem[],
  itemId: string,
  targetEnhanceLevel: number,
  equipmentMap: ReadonlyMap<string, CombatEquipmentDefinition>,
  occupiedCounts: ReadonlyMap<string, number>,
): number {
  const isEquipment = equipmentMap.has(itemId) || itemId.startsWith('equipment.')
  let owned = 0
  for (const inv of inventory) {
    if (inv.itemId !== itemId || inv.amount <= 0) continue
    if (inv.variant && !inv.variant.startsWith('+')) continue
    if (parseEnhancementVariant(inv.variant) === targetEnhanceLevel) {
      owned += inv.amount
    }
  }
  const occupied = isEquipment ? (occupiedCounts.get(`${itemId}#${targetEnhanceLevel}`) ?? 0) : 0
  return Math.max(0, Math.floor(owned - occupied))
}

/**
 * 按服务端 `EnhancementRules.CountOccupied` 同源口径统计已穿戴与强化队列锁定件数：
 * 仅计入已穿戴装备与 `enhance.equipment` 队列占用，不把秘法队列自身计为自锁占用。
 */
function collectServerAlignedOccupiedCounts(
  combatEquipment?: readonly EquippedItem[],
  queueEntries?: readonly QueueEntry[],
): Map<string, number> {
  const counts = new Map<string, number>()
  for (const eq of combatEquipment ?? []) {
    const key = `${eq.itemId}#${Math.floor(eq.enhancementLevel)}`
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  for (const qEntry of queueEntries ?? []) {
    if (qEntry.actionId === ENHANCE_ACTION_ID && qEntry.targetItemId) {
      const level = Math.max(0, Math.floor(
        qEntry.enhancementCurrentLevel
          ?? (typeof qEntry.parameters?.initialLevel === 'number' ? qEntry.parameters.initialLevel : 0),
      ))
      const key = `${qEntry.targetItemId}#${level}`
      counts.set(key, (counts.get(key) ?? 0) + 1)
    }
  }
  return counts
}

/** 判定当前行动是否适用材料次数上限，并返回对应上限 */
function resolveResourceBound({
  entry,
  action,
  inventory,
  combatEquipment,
  queueEntries,
  equipmentCatalog,
  stats,
}: CalculateQueueActionEstimateInput): ResourceBoundResult {
  const deps = getQueueEstimateDependencies(entry, action)
  if (deps.isExcluded) {
    return { kind: 'excluded' }
  }
  if (!deps.needsInventory && !deps.needsFoundation) {
    return { kind: 'unbounded' }
  }

  const eqCatalog = equipmentCatalog ?? []
  const equipmentMap = getEquipmentMap(eqCatalog)
  const resolvedOccupied = collectServerAlignedOccupiedCounts(combatEquipment, queueEntries)

  // 装备强化：按当前目标等级对应的单轮灵石（及触发保护时的保护材料）估算最大可执行次数
  if (entry.actionId === ENHANCE_ACTION_ID) {
    const currentLevel = Math.max(0, Math.floor(entry.enhancementCurrentLevel ?? entry.parameters?.initialLevel ?? 0))
    const spiritCost = enhanceSpiritStoneCost(currentLevel + 1)
    const spiritOwned = getAvailableInventoryAmount(inventory, SPIRIT_STONE_ITEM_ID, 0, equipmentMap, resolvedOccupied)
    let maxSupported = spiritCost > 0 ? Math.floor(spiritOwned / spiritCost) : Number.POSITIVE_INFINITY

    const rawProtectLevel = entry.parameters?.protectLevel
    const protectLevel = typeof rawProtectLevel === 'number' && Number.isFinite(rawProtectLevel)
      ? Math.max(ENHANCE_MIN_PROTECT_LEVEL, Math.floor(rawProtectLevel))
      : 0
    if (entry.auxItemId && currentLevel >= protectLevel) {
      const protectOwned = getAvailableInventoryAmount(inventory, entry.auxItemId, 0, equipmentMap, resolvedOccupied)
      maxSupported = Math.min(maxSupported, protectOwned)
    }
    return { kind: 'bounded', maxSupported: Math.max(0, Math.floor(maxSupported)) }
  }

  // 秘法（化灵 / 分解 / 转化）：每次固定消耗 1 件目标物品/对应强化变体
  if (SECRET_ART_ACTION_IDS.has(entry.actionId)) {
    const targetLevel = Math.max(0, Math.floor(entry.enhancementCurrentLevel ?? entry.parameters?.initialLevel ?? 0))
    const available = getAvailableInventoryAmount(inventory, entry.targetItemId!, targetLevel, equipmentMap, resolvedOccupied)
    return { kind: 'bounded', maxSupported: available }
  }

  const inputEntries = action?.inputs
    ? Object.entries(action.inputs).filter(([, amount]) => typeof amount === 'number' && Number.isFinite(amount) && amount > 0)
    : []
  const foundationRatio = deps.needsFoundation ? action!.foundationCostRatio! : 0

  let maxSupported = Number.POSITIVE_INFINITY
  for (const [itemId, costPerRound] of inputEntries) {
    const available = getAvailableInventoryAmount(inventory, itemId, 0, equipmentMap, resolvedOccupied)
    maxSupported = Math.min(maxSupported, Math.floor(available / costPerRound))
  }

  if (foundationRatio > 0) {
    const expToNext = calculateRealmProgress(
      stats?.experience ?? 0,
      stats?.realmLevel,
      serverExpToNextCap(stats?.expToNextLinggen, stats?.realmLevel),
    ).expToNext ?? 0
    const foundationCostPerRound = Math.max(1, Math.round(expToNext * foundationRatio))
    const availableFoundation = Math.max(0, stats?.foundation ?? 0)
    maxSupported = Math.min(maxSupported, Math.floor(availableFoundation / foundationCostPerRound))
  }

  return { kind: 'bounded', maxSupported: Math.max(0, Math.floor(maxSupported)) }
}

/**
 * 计算顶部当前行动栏的可用次数与快照基准总剩余耗时。
 * 当前轮材料在完成结算时才扣减，因此背包可支持总次数包含正在进行的当前轮。
 */
export function calculateQueueActionEstimate(input: CalculateQueueActionEstimateInput): QueueActionEstimate {
  const { entry } = input
  const remainingRepeats = Number.isFinite(entry.remainingRepeats) ? Math.floor(entry.remainingRepeats) : 0
  const isInfinite = remainingRepeats < 0
  const roundDurationSeconds = Number.isFinite(entry.durationSeconds) && entry.durationSeconds > 0
    ? entry.durationSeconds
    : 0

  const bound = resolveResourceBound(input)
  if (bound.kind === 'excluded') {
    return {
      hasEstimate: false,
      isInfinite,
      remainingRepeats,
      effectiveCount: 0,
      showEffectiveCount: false,
      roundDurationSeconds,
      baseRemainingSeconds: 0,
    }
  }

  // 采集、修炼等可无限进行且无材料消耗的行动：无限时不展示估算，有限次按剩余次数推算总时间
  if (bound.kind === 'unbounded') {
    if (isInfinite || remainingRepeats <= 0) {
      return {
        hasEstimate: false,
        isInfinite,
        remainingRepeats,
        effectiveCount: 0,
        showEffectiveCount: false,
        roundDurationSeconds,
        baseRemainingSeconds: 0,
      }
    }
    const baseRemainingSeconds = computeBaseTotalRemainingSeconds(roundDurationSeconds, entry.progressSeconds, remainingRepeats)
    return {
      hasEstimate: roundDurationSeconds > 0,
      isInfinite: false,
      remainingRepeats,
      effectiveCount: remainingRepeats,
      showEffectiveCount: false,
      roundDurationSeconds,
      baseRemainingSeconds,
    }
  }

  // 受材料/底蕴/灵石约束的行动（制造、秘法、强化等）
  if (!isInfinite && remainingRepeats <= 0) {
    return {
      hasEstimate: false,
      isInfinite: false,
      remainingRepeats: 0,
      effectiveCount: 0,
      showEffectiveCount: false,
      roundDurationSeconds,
      baseRemainingSeconds: 0,
    }
  }

  const effectiveCount = isInfinite
    ? bound.maxSupported
    : Math.min(remainingRepeats, bound.maxSupported)
  const showEffectiveCount = isInfinite || effectiveCount < remainingRepeats
  const baseRemainingSeconds = computeBaseTotalRemainingSeconds(roundDurationSeconds, entry.progressSeconds, effectiveCount)

  return {
    hasEstimate: true,
    isInfinite,
    remainingRepeats,
    effectiveCount,
    showEffectiveCount,
    roundDurationSeconds,
    baseRemainingSeconds,
  }
}

function computeBaseTotalRemainingSeconds(
  roundDurationSeconds: number,
  progressSeconds: number,
  effectiveCount: number,
): number {
  if (effectiveCount <= 0 || roundDurationSeconds <= 0) return 0
  const safeProgress = Number.isFinite(progressSeconds) ? Math.max(0, Math.min(roundDurationSeconds, progressSeconds)) : 0
  const currentRoundRemaining = Math.max(0, roundDurationSeconds - safeProgress)
  return currentRoundRemaining + (effectiveCount - 1) * roundDurationSeconds
}

/** 根据距离上次队列同步的流逝秒数，计算当前实时剩余总时间 */
export function resolveLiveRemainingSeconds(
  estimate: QueueActionEstimate,
  elapsedSinceSyncSeconds: number,
): number {
  if (!estimate.hasEstimate || estimate.baseRemainingSeconds <= 0) return 0
  const safeElapsed = Number.isFinite(elapsedSinceSyncSeconds) ? Math.max(0, elapsedSinceSyncSeconds) : 0
  return Math.max(0, estimate.baseRemainingSeconds - safeElapsed)
}

/** 紧凑格式化 0.1 精度数值：整数不保留多余 `.0` */
function formatTenthsValue(tenths: number): string {
  return tenths % 10 === 0 ? String(tenths / 10) : (tenths / 10).toFixed(1)
}

/**
 * 紧凑剩余时长格式化：
 * - `< 60s`：`{s}秒`
 * - `< 1h`：`{m}分` 或 `{m}分{s}秒`
 * - `< 24h`：`{v}小时`（1 位小数，整时去 `.0`，如 `5.8小时`）
 * - `>= 24h`：`{v}天`（1 位小数，整天去 `.0`）
 */
export function formatCompactDuration(seconds: number, t: QueueEstimateTranslateFn): string {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return t('app.queue.timeSeconds', { s: 0 })
  }
  const totalSeconds = Math.ceil(seconds)
  if (totalSeconds < 60) {
    return t('app.queue.timeSeconds', { s: totalSeconds })
  }
  if (totalSeconds < 3600) {
    const minutes = Math.floor(totalSeconds / 60)
    const remainderSeconds = totalSeconds % 60
    return remainderSeconds === 0
      ? t('app.queue.timeMinutes', { m: minutes })
      : t('app.queue.timeMinutesSeconds', { m: minutes, s: remainderSeconds })
  }
  const hoursTenths = Math.round((totalSeconds / 3600) * 10)
  if (hoursTenths < 240) {
    return t('app.queue.timeHours', { v: formatTenthsValue(hoursTenths) })
  }
  const daysTenths = Math.round((totalSeconds / 86400) * 10)
  return t('app.queue.timeDays', { v: formatTenthsValue(daysTenths) })
}

/** 将行动估算与实时剩余秒数格式化为顶部行动栏 `.qc-repeats` 展示文案 */
export function formatQueueRepeatsText(
  estimate: QueueActionEstimate,
  remainingSeconds: number,
  t: QueueEstimateTranslateFn,
): string {
  if (!estimate.hasEstimate) {
    return estimate.isInfinite
      ? t('app.queue.repeatsInfinite')
      : t('common.repeatsCount', { n: estimate.remainingRepeats })
  }

  if (estimate.effectiveCount <= 0) {
    if (estimate.isInfinite) return t('app.queue.repeatsInfiniteZero')
    if (estimate.showEffectiveCount) return t('app.queue.repeatsFiniteZero', { n: estimate.remainingRepeats })
    return t('common.repeatsCount', { n: estimate.remainingRepeats })
  }

  if (estimate.roundDurationSeconds <= 0) {
    if (estimate.isInfinite) {
      return t('app.queue.repeatsInfiniteCountOnly', { count: estimate.effectiveCount })
    }
    if (estimate.showEffectiveCount) {
      return t('app.queue.repeatsFiniteCountOnly', {
        n: estimate.remainingRepeats,
        count: estimate.effectiveCount,
      })
    }
    return t('common.repeatsCount', { n: estimate.remainingRepeats })
  }

  const duration = formatCompactDuration(remainingSeconds, t)
  if (estimate.isInfinite) {
    return t('app.queue.repeatsInfiniteEstimate', {
      count: estimate.effectiveCount,
      duration,
    })
  }
  if (estimate.showEffectiveCount) {
    return t('app.queue.repeatsFiniteCappedEstimate', {
      n: estimate.remainingRepeats,
      count: estimate.effectiveCount,
      duration,
    })
  }
  return t('app.queue.repeatsFiniteEstimate', {
    n: estimate.remainingRepeats,
    duration,
  })
}
