/**
 * 行动产出、持续消耗品加成与动态加成解算器
 */

import type { ActionDefinition, ActionType, AutoUseSlot, Buff, InventoryItem, ItemDefinition, Stats } from './types'
import { calculateRealmProgress } from './combatConstants'

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

export interface CultivationYieldDetail {
  /** 基础修为 */
  baseExp: number
  /** 加成后修为（不含底蕴消耗） */
  boostedExp: number
  /** 基础灵力 */
  baseQi: number
  /** 加成后灵力 */
  boostedQi: number
  /** 综合加成乘率 */
  totalMultiplier: number
  /** 是否拥有道基底蕴并可消耗加速 */
  hasFoundationAcceleration: boolean
  /** 预计消耗底蕴点数 */
  foundationConsumed: number
  /** 底蕴加速后实际总修为获得 */
  actualExpGain: number
  /** 当前境界是否已圆满待突破（修为转为底蕴） */
  isPendingBreakthrough: boolean
  /** 生效加成明细列表 */
  bonuses: ActiveBonusDetail[]
}

export interface ActionYieldPreview {
  /** 是否为修炼动作 */
  isCultivation: boolean
  /** 修炼专属详情 */
  cultivation?: CultivationYieldDetail
  /** 普通物品产出预览（包含加成） */
  itemOutputs: Array<{
    itemId: string
    baseAmount: number
    finalAmount: number
    multiplier: number
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
  buffs?: readonly Buff[] | Buff[]
  world?: {
    locationName?: string
    cultivationMultiplier?: number
  }
  stats?: Stats
}

function getItemAmount(inventory: readonly InventoryItem[] | InventoryItem[] | Record<string, number>, itemId: string): number {
  if (Array.isArray(inventory)) {
    return inventory.find(i => i.itemId === itemId)?.amount ?? 0
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
  buffs = [],
  world,
  stats,
}: CalculateActionParams): ActionYieldPreview {
  const isCultivation = action.type === 'cultivation'
  const actionType = action.type
  const baseDurationSeconds = action.durationSeconds || 10
  const nowUnixSeconds = Math.floor(Date.now() / 1000)

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
        description: `${itemDef.name}持续生效中，提供 +${percent}% 产出提升`,
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
        sourceName: world.locationName || '灵脉地势',
        tag: `${sign}${diffPercent}%`,
        type: 'world',
        multiplier: worldMultiplier,
        description: `当前地点灵气浓郁度提供 ${sign}${diffPercent}% 修炼加成`,
      })
    }
  }

  // 3. 状态增益 (Buffs)
  let buffMultiplier = 1.0
  const activeBuffs = buffs.filter(b => b.expiresAtUnixSeconds > nowUnixSeconds)
  for (const buff of activeBuffs) {
    let bMult = 1.0
    if (isCultivation && buff.qiGainMultiplier && buff.qiGainMultiplier > 0) {
      bMult = buff.qiGainMultiplier
    } else if (actionType === 'exploration' && buff.gatheringOutputMultiplier && buff.gatheringOutputMultiplier > 0) {
      bMult = buff.gatheringOutputMultiplier
    } else if (actionType === 'alchemy' && buff.alchemyOutputMultiplier && buff.alchemyOutputMultiplier > 0) {
      bMult = buff.alchemyOutputMultiplier
    }

    if (bMult > 1.0) {
      buffMultiplier = Math.max(buffMultiplier, bMult)
      const percent = Math.round((bMult - 1.0) * 100)
      bonuses.push({
        sourceName: buff.name,
        tag: `+${percent}%`,
        type: 'buff',
        multiplier: bMult,
        description: `状态增益【${buff.name}】提供 +${percent}% 效果`,
      })
    }
  }

  // 综合倍率
  const totalMultiplier = autoUseMultiplier * worldMultiplier * buffMultiplier

  // 4. 修炼专属产出解算
  let cultivationDetail: CultivationYieldDetail | undefined
  if (isCultivation) {
    const baseExp = 10.0
    const baseQi = Math.max(1, action.outputs?.['resource.qi'] ?? 1)

    const boostedExp = baseExp * totalMultiplier
    const boostedQi = Math.max(1, Math.round(baseQi * totalMultiplier))

    // 境界与底蕴判定
    const spirit = stats?.spirit ?? 0
    const foundationExp = stats?.foundationExp ?? 0
    const realmProgress = calculateRealmProgress(spirit)
    const expToCap = realmProgress.expToNext - realmProgress.currentLayerExp
    const isPendingBreakthrough = realmProgress.expToNext > 0 && expToCap <= 0.0001 && spirit > 0

    let hasFoundationAcceleration = false
    let foundationConsumed = 0
    let actualExpGain = boostedExp

    if (isPendingBreakthrough) {
      // 境界圆满待突破
      bonuses.push({
        sourceName: '境界圆满',
        tag: '底蕴转化',
        type: 'foundation',
        description: '当前境界待突破，修炼所得修为将 100% 转化为道基底蕴',
      })
    } else if (foundationExp > 0) {
      // 底蕴化生：消耗等额底蕴，享受 3 倍修为加速（最多消耗 2 * boostedExp）
      const maxBonus = boostedExp * 2.0
      foundationConsumed = Math.min(foundationExp, maxBonus)
      actualExpGain = boostedExp + foundationConsumed
      hasFoundationAcceleration = true

      bonuses.push({
        sourceName: '底蕴化生',
        tag: '3倍加速',
        type: 'foundation',
        description: `消耗 ${foundationConsumed.toFixed(0)} 点道基底蕴，本次实际获得 ${actualExpGain.toFixed(0)} 点修为`,
      })
    }

    cultivationDetail = {
      baseExp,
      boostedExp,
      baseQi,
      boostedQi,
      totalMultiplier,
      hasFoundationAcceleration,
      foundationConsumed,
      actualExpGain,
      isPendingBreakthrough,
      bonuses,
    }
  }

  // 5. 普通物品产出解算
  const itemOutputs = Object.entries(action.outputs ?? {})
    .filter(([itemId]) => itemId !== 'resource.qi')
    .map(([itemId, baseAmount]) => {
      const multiplier = totalMultiplier
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
    cultivation: cultivationDetail,
    itemOutputs,
    bonuses,
    baseDurationSeconds,
    actualDurationSeconds: baseDurationSeconds,
  }
}
