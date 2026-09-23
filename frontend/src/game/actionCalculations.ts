/**
 * 行动产出、持续消耗品加成与动态加成解算器
 */

import type { ActionDefinition, ActionType, ActionYieldPeriod, AutoUseSlot, InventoryItem, ItemDefinition, Stats } from './types'
import { calculateRealmProgress } from './combatConstants'
import { getT } from '../i18n'

/** 非物品、非增益的资源类收益类别（修为/灵力/气血/历练经验） */
export type ResourceYieldKind = 'qi' | 'mana' | 'hitpoints' | 'experience'

/** 单条生效加成的展示明细 */
export interface ActiveBonusDetail {
  /** 加成源名称（如 '引气丹' / '青岚山谷' / '吐纳增益'） */
  sourceName: string
  /** 加成标签（如 '+30%' / '+20%' / '3倍加速'） */
  tag: string
  /** 加成类别 */
  type: 'pill' | 'world' | 'buff' | 'foundation' | 'speed'
  /** 数值乘率（如 1.3） */
  multiplier?: number
  /** 说明描述 */
  description?: string
}

/** 修炼行动专属详情（境界圆满待突破判定） */
export interface CultivationYieldDetail {
  /** 当前境界是否已圆满待突破（修为转为底蕴） */
  isPendingBreakthrough: boolean
}

/**
 * 行动收益预览解算结果：资源收益按综合倍率放大、物品收益聚合后向上取整（保底 1）、
 * Buff 收益原样透传；仅用于 UI 预估展示，权威结算仍在服务端。
 */
export interface ActionYieldPreview {
  /** 是否为修炼动作 */
  isCultivation: boolean
  /** 综合加成倍率 */
  totalMultiplier: number
  /** 修炼专属详情 */
  cultivation?: CultivationYieldDetail
  /** 物品产出预览（按 yields 中 kind === 'item' 聚合，包含加成） */
  itemOutputs: Array<{
    itemId: string
    baseAmount: number
    finalAmount: number
    multiplier: number
  }>
  /** 资源类收益预览（修为/灵力/气血/历练经验，包含加成） */
  resourceYields: Array<{
    kind: ResourceYieldKind
    per: ActionYieldPeriod
    baseAmount: number
    finalAmount: number
    multiplier: number
  }>
  /** 增益状态收益预览（透传服务端配置，不参与加成倍率） */
  buffYields: Array<{
    buffId: string
    name: string
    durationSeconds: number
    stacks: number
  }>
  /** 生效加成明细列表 */
  bonuses: ActiveBonusDetail[]
  /** 基础耗时（秒） */
  baseDurationSeconds: number
  /** 实际耗时（秒） */
  actualDurationSeconds: number
}

interface CalculateActionParams {
  action: ActionDefinition
  inventory: readonly InventoryItem[] | InventoryItem[] | Record<string, number>
  items: readonly ItemDefinition[] | ItemDefinition[]
  autoUseSlots?: Record<ActionType, AutoUseSlot[]>
  world?: {
    locationName?: string
    cultivationMultiplier?: number
  }
  stats?: Stats
}

/** 兼容背包数组与 {itemId: amount} 映射两种入参形态的持有量查询；数组形态只计基础变体行 */
function getItemAmount(inventory: readonly InventoryItem[] | InventoryItem[] | Record<string, number>, itemId: string): number {
  if (Array.isArray(inventory)) {
    return inventory.find(i => i.itemId === itemId && !i.variant)?.amount ?? 0
  }
  return (inventory as Record<string, number>)[itemId] ?? 0
}

/**
 * 实时解算指定行动在当前状态下的实际产出与全部生效加成
 */
export function calculateActionYieldPreview({
  action,
  inventory,
  items,
  autoUseSlots,
  world,
  stats,
}: CalculateActionParams): ActionYieldPreview {
  const t = getT()
  const isCultivation = action.type === 'cultivation'
  const actionType = action.type
  const baseDurationSeconds = action.durationSeconds || 10

  const bonuses: ActiveBonusDetail[] = []

  // 1. 持续消耗品 / 丹药加成
  let autoUseMultiplier = 1.0
  const slotsForType = autoUseSlots?.[actionType] ?? []
  for (const slot of slotsForType) {
    if (!slot || !slot.enabled || !slot.itemId) continue
    const itemDef = items.find(item => item.itemId === slot.itemId)
    if (!itemDef) continue
    const isApplicable = itemDef.usableActionTypes?.includes(actionType)
    if (!isApplicable) continue

    // 检查是否有持续时间或背包有货可自动补充
    const hasRemaining = (slot.remainingDurationSeconds ?? 0) > 0
    const hasStock = getItemAmount(inventory, slot.itemId) >= 1
    if (!hasRemaining && !hasStock) continue

    let itemMult = 1.0
    if (actionType === 'cultivation' && itemDef.cultivationMultiplier && itemDef.cultivationMultiplier > 0) {
      itemMult = itemDef.cultivationMultiplier
    } else if (actionType === 'exploration' && itemDef.gatheringMultiplier && itemDef.gatheringMultiplier > 0) {
      itemMult = itemDef.gatheringMultiplier
    } else if (actionType === 'alchemy' && itemDef.alchemyMultiplier && itemDef.alchemyMultiplier > 0) {
      itemMult = itemDef.alchemyMultiplier
    }
    if (itemMult > 1.0) {
      autoUseMultiplier *= itemMult
      const percent = Math.round((itemMult - 1.0) * 100)
      bonuses.push({
        sourceName: itemDef.name,
        tag: `+${percent}%`,
        type: 'pill',
        multiplier: itemMult,
        description: t('app.action.bonusPillDesc', { name: itemDef.name, percent }),
      })
    }
  }

  // 2. 世界/地图灵脉倍率（修炼专属）
  let worldMultiplier = 1.0
  if (isCultivation && world && typeof world.cultivationMultiplier === 'number') {
    worldMultiplier = world.cultivationMultiplier
    if (Math.abs(worldMultiplier - 1.0) > 0.001) {
      const diffPercent = Math.round((worldMultiplier - 1.0) * 100)
      const sign = diffPercent > 0 ? '+' : ''
      bonuses.push({
        sourceName: world.locationName || t('app.action.bonusWorldSourceFallback'),
        tag: `${sign}${diffPercent}%`,
        type: 'world',
        multiplier: worldMultiplier,
        description: t('app.action.bonusWorldDesc', { percent: `${sign}${diffPercent}%` }),
      })
    }
  }

  // 活跃 Buff 只携带战斗属性修饰器（hpRegen/mpRegen/attackSpeedPoints/damageAbsorptionPoints/statRatios），
  // 不再承载行动收益倍率——收益加成唯一通道为持续消耗品（autoUseSlots）与灵脉/建筑倍率。
  const totalMultiplier = autoUseMultiplier * worldMultiplier

  // 4. 修炼专属境界判定（产出数值统一由 yields 驱动）
  let cultivationDetail: CultivationYieldDetail | undefined
  if (isCultivation) {
    const spirit = stats?.spirit ?? 0
    const realmProgress = calculateRealmProgress(spirit, stats?.realmLevel)
    const expToCap = realmProgress.expToNext - realmProgress.currentLayerExp
    const isPendingBreakthrough = realmProgress.expToNext > 0 && expToCap <= 0.0001 && spirit > 0

    if (isPendingBreakthrough) {
      // 境界圆满待突破
      bonuses.push({
        sourceName: t('app.action.bonusFoundationSource'),
        tag: t('app.action.bonusFoundationTag'),
        type: 'foundation',
        description: t('app.action.bonusFoundationDesc'),
      })
    }

    cultivationDetail = { isPendingBreakthrough }
  }

  // 5. 收益解算：物品按 id 聚合，资源类收益同步应用综合倍率，增益收益原样透传
  const itemAmountMap = new Map<string, number>()
  const resourceYields: ActionYieldPreview['resourceYields'] = []
  const buffYields: ActionYieldPreview['buffYields'] = []
  for (const yieldEntry of action.yields ?? []) {
    switch (yieldEntry.kind) {
      case 'item': {
        if (!yieldEntry.id) break
        itemAmountMap.set(yieldEntry.id, (itemAmountMap.get(yieldEntry.id) ?? 0) + yieldEntry.amount)
        break
      }
      case 'qi':
      case 'mana':
      case 'hitpoints':
      case 'experience': {
        const baseAmount = yieldEntry.amount
        resourceYields.push({
          kind: yieldEntry.kind,
          per: yieldEntry.per,
          baseAmount,
          finalAmount: baseAmount * totalMultiplier,
          multiplier: totalMultiplier,
        })
        break
      }
      case 'buff': {
        if (!yieldEntry.id) break
        buffYields.push({
          buffId: yieldEntry.id,
          name: yieldEntry.name ?? yieldEntry.id,
          durationSeconds: yieldEntry.durationSeconds ?? 0,
          stacks: yieldEntry.amount,
        })
        break
      }
    }
  }

  const itemOutputs = Array.from(itemAmountMap.entries()).map(([itemId, baseAmount]) => {
    const multiplier = totalMultiplier
    // 物品件数必须向上取整且保底 1：收益展示不允许出现 0.x 件或被倍率抹零。
    const finalAmount = multiplier === 1.0
      ? baseAmount
      : Math.max(1, Math.ceil(baseAmount * multiplier))
    return {
      itemId,
      baseAmount,
      finalAmount,
      multiplier,
    }
  })

  return {
    isCultivation,
    totalMultiplier,
    cultivation: cultivationDetail,
    itemOutputs,
    resourceYields,
    buffYields,
    bonuses,
    baseDurationSeconds,
    actualDurationSeconds: baseDurationSeconds,
  }
}
