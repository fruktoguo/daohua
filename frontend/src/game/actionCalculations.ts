/**
 * 行动产出、持续消耗品加成与动态加成解算器
 */

import type { ActionDefinition, AutoUseBarType, AutoUseSlot, InventoryItem, ItemDefinition, NodeBuildingEffectSet, Stats } from './types'
import { calculateRealmProgress, calculateTechniqueProgress, serverExpToNextCap, TECHNIQUE_IDS, TECHNIQUE_SPEED_RATIO_PER_LEVEL } from './combatConstants'
import { autoUseBarFor, findEquipmentInCatalog } from './ids'
import { getT } from '../i18n'

/** 非物品、非增益的资源类收益类别（修为/灵力/气血） */
export type ResourceYieldKind = 'mana' | 'hitpoints' | 'experience'

/** 六维归属键 → 展示词条（复用 combat.attr.*，键为服务端 CoreAspectKind 小写名） */
export const ROOT_ASPECT_LABEL_KEYS: Readonly<Record<string, string>> = {
  stamina: 'combat.attr.stamina',
  meridians: 'combat.attr.meridian',
  attack: 'combat.attr.attack',
  bone: 'combat.attr.defense',
  agility: 'combat.attr.movement',
  consciousness: 'combat.attr.divineSense',
}

/** 单条生效加成的展示明细 */
export interface ActiveBonusDetail {
  /** 加成源名称（如 '引气丹' / '青岚山谷' / '吐纳增益'） */
  sourceName: string
  /** 加成标签（如 '+30%' / '+20%' / '3倍加速'） */
  tag: string
  /** 加成类别 */
  type: 'pill' | 'world' | 'building' | 'buff' | 'foundation' | 'speed' | 'spiritStone'
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
  /** 资源类收益预览（修为/灵力/气血）：同类收益按单轮口径合并，包含加成 */
  resourceYields: Array<{
    kind: ResourceYieldKind
    /** 单轮基础产出（按秒速率已按整轮耗时折算并入） */
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
  /** 六维经验收益预览（配表定额，不吃产出倍率；按秒速率折算为整轮量） */
  rootExpYields: Array<{
    aspect: string
    amount: number
  }>
  /** 生效加成明细列表 */
  bonuses: ActiveBonusDetail[]
  /** 基础耗时（秒） */
  baseDurationSeconds: number
  /** 实际耗时（秒） */
  actualDurationSeconds: number
}

/** calculateActionYieldPreview 的入参集合：行动定义 + 评估现场（背包/世界节点/玩家属性/装配快照） */
interface CalculateActionParams {
  action: ActionDefinition
  inventory: readonly InventoryItem[] | InventoryItem[] | Record<string, number>
  items: readonly ItemDefinition[] | ItemDefinition[]
  autoUseSlots?: Record<AutoUseBarType, AutoUseSlot[]>
  world?: {
    locationName?: string
    cultivationMultiplier?: number
    buildingEffects?: NodeBuildingEffectSet
  }
  stats?: Stats
  /** 动态行动参数（如灵石注灵档位 spiritStone: 1/10/100 等） */
  parameters?: Record<string, number>
  /** 装配快照：技艺经验表与已装备列表（技艺等级/技艺工具耗时加成来源） */
  combat?: {
    equipment?: readonly { itemId: string; enhancementLevel?: number }[]
    techniqueExperiences?: Record<string, number>
  } | null
  /** 装备目录（techniqueBonus 加成块的查询来源；缺省按无工具加成计） */
  equipmentCatalog?: readonly {
    equipmentId: string
    name?: string
    slot?: string
    techniqueBonus?: { technique: string; speedRatio: number } | null
  }[]
}

/** 兼容背包数组与 {itemId: amount} 映射两种入参形态的持有量查询；数组形态只计基础变体行 */
export function getItemAmount(inventory: readonly InventoryItem[] | InventoryItem[] | Record<string, number>, itemId: string): number {
  if (Array.isArray(inventory)) {
    return inventory.find(i => i.itemId === itemId && !i.variant)?.amount ?? 0
  }
  return (inventory as Record<string, number>)[itemId] ?? 0
}

/**
 * 行动在当前地点的可执行性（与服务端开轮门禁同口径）：
 * 白名单 availableNodeIds 非空 → 须在列；黑名单 excludedNodeIds → 驻足该节点时禁用
 * （移动途中 nodeId 为空不算驻足，不触发排除）。
 */
export function isActionAvailableAtNode(
  action: { availableNodeIds?: string[]; excludedNodeIds?: string[] },
  nodeId: string | null,
): boolean {
  if (action.availableNodeIds?.length)
    return nodeId !== null && action.availableNodeIds.includes(nodeId)
  return !(nodeId !== null && (action.excludedNodeIds?.includes(nodeId) ?? false))
}

/**
 * 按行动类别与技艺获取对应建筑加速比率（与服务端 NodeBuildings.SpeedRatioFor 同口径）
 */
export function resolveBuildingSpeedRatio(
  effects: NodeBuildingEffectSet | undefined | null,
  actionType: string,
  technique?: string | null,
): number {
  if (!effects) return 0
  if (technique) {
    let techRatio = 0
    switch (technique) {
      case 'gathering':
        techRatio = (effects.gatheringSpeedRatio ?? 0) > 0 ? (effects.gatheringSpeedRatio ?? 0) : (effects.explorationSpeedRatio ?? 0)
        break
      case 'mining':
        techRatio = effects.miningSpeedRatio ?? 0
        break
      case 'alchemy':
        techRatio = effects.alchemySpeedRatio ?? 0
        break
      case 'forging':
        techRatio = effects.forgingSpeedRatio ?? 0
        break
      case 'talisman':
        techRatio = effects.talismanSpeedRatio ?? 0
        break
      case 'formation':
        techRatio = effects.formationSpeedRatio ?? 0
        break
      case 'enhancement':
        techRatio = effects.enhancementSpeedRatio ?? 0
        break
      default:
        techRatio = 0
        break
    }
    if (techRatio > 0) return techRatio
  }
  switch (actionType) {
    case 'exploration':
      return (effects.gatheringSpeedRatio ?? 0) > 0 ? (effects.gatheringSpeedRatio ?? 0) : (effects.explorationSpeedRatio ?? 0)
    case 'alchemy':
      return effects.alchemySpeedRatio ?? 0
    case 'enhancement':
      return effects.enhancementSpeedRatio ?? 0
    default:
      return 0
  }
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
  parameters,
  combat,
  equipmentCatalog,
}: CalculateActionParams): ActionYieldPreview {
  const t = getT()
  const isCultivation = action.type === 'cultivation'
  const actionType = action.type
  const baseDurationSeconds = action.durationSeconds || 10

  const bonuses: ActiveBonusDetail[] = []

  // 1. 持续消耗品 / 丹药加成（与服务端 TryGetSustainedConsumable 同口径：
  //    槽位启用、物品存在、持续型且时长为正、行动归属栏域匹配且物品声明该栏域）
  let autoUseMultiplier = 1.0
  const actionBar = autoUseBarFor(action)
  const slotsForType = actionBar ? (autoUseSlots?.[actionBar] ?? []) : []
  for (const slot of slotsForType) {
    if (!slot || !slot.enabled || !slot.itemId) continue
    const itemDef = items.find(item => item.itemId === slot.itemId)
    if (!itemDef) continue
    const isContinuous = itemDef.isContinuous === true && (itemDef.durationSeconds ?? 0) > 0
    const isApplicable = isContinuous && actionBar !== null && (itemDef.usableBars?.includes(actionBar) ?? false)
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
    if (itemMult !== 1.0) {
      autoUseMultiplier *= itemMult
      const percent = Math.round((itemMult - 1.0) * 100)
      bonuses.push({
        sourceName: itemDef.name,
        tag: `${percent >= 0 ? '+' : ''}${percent}%`,
        type: 'pill',
        multiplier: itemMult,
        description: t('app.action.bonusPillDesc', {
          name: itemDef.name,
          percent: `${percent >= 0 ? '+' : ''}${percent}`,
        }),
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

  // 3. 当前节点公共建筑加成（与服务端 OutputRatioFor/SpeedRatioFor 同口径）：
  //    产出类乘进总倍率；耗时加速只缩短每轮时长、不乘产出，且按轮扣除持续用品时长。
  const effects = world?.buildingEffects
  const buildingOutputRatio =
    actionType === 'cultivation' ? (effects?.cultivationOutputRatio ?? 0)
    : actionType === 'exploration' ? (effects?.explorationOutputRatio ?? 0)
    : actionType === 'alchemy' ? (effects?.alchemyOutputRatio ?? 0)
    : 0
  const buildingSpeedRatio = resolveBuildingSpeedRatio(effects, actionType, typeof action.technique === 'string' ? action.technique : null)

  // 技艺耗时加成（与服务端 TimedActionDuration / TechniqueCatalog 同口径）：
  // 行动 technique 命中七种已知技艺时，技艺等级加成（每级 +5%）与已装备工具
  // techniqueBonus.speedRatio 并入同一除数；建筑速度乘区保持独立除法。
  let techniqueSpeedRatio = 0
  const techniqueId = typeof action.technique === 'string' ? action.technique : null
  if (techniqueId && (TECHNIQUE_IDS as readonly string[]).includes(techniqueId)) {
    const techniqueLevel = calculateTechniqueProgress(combat?.techniqueExperiences?.[techniqueId] ?? 0).level
    const levelRatio = TECHNIQUE_SPEED_RATIO_PER_LEVEL * techniqueLevel
    techniqueSpeedRatio += levelRatio
    if (techniqueLevel > 0) {
      // 行动速度提升比率
      const speedPercent = Math.round(levelRatio * 100)
      const techniqueName = t(`insight.technique.${techniqueId}`)
      bonuses.push({
        sourceName: techniqueName,
        tag: t('app.action.bonusBuildingSpeedTag', { percent: speedPercent }),
        type: 'speed',
        description: t('app.action.bonusTechniqueSpeedDesc', {
          name: techniqueName,
          level: techniqueLevel,
          percent: speedPercent,
        }),
      })
    }
    // 已装备技艺工具：techniqueBonus.technique 命中本行动技艺时累加 speedRatio（含强化缩放）
    for (const equipped of combat?.equipment ?? []) {
      const eqDef = findEquipmentInCatalog(equipmentCatalog ?? [], equipped.itemId)
      const bonus = eqDef?.techniqueBonus
      if (!bonus || bonus.technique !== techniqueId) continue
      if (!Number.isFinite(bonus.speedRatio) || bonus.speedRatio <= 0) continue
      const enhanceLevel = equipped.enhancementLevel ?? 0
      const scale = Math.pow(1.05, enhanceLevel)
      const effectiveRatio = bonus.speedRatio * scale
      techniqueSpeedRatio += effectiveRatio
      const baseName = eqDef.name ?? equipped.itemId
      const toolName = enhanceLevel > 0 ? `${baseName} +${enhanceLevel}` : baseName
      const speedPercent = +(effectiveRatio * 100).toFixed(1)
      bonuses.push({
        sourceName: toolName,
        tag: t('app.action.bonusBuildingSpeedTag', { percent: Math.round(speedPercent) }),
        type: 'speed',
        description: t('app.action.bonusToolSpeedDesc', {
          name: toolName,
          percent: speedPercent,
        }),
      })
    }
  }

  const actualDurationSeconds = (buildingSpeedRatio > 0
    ? baseDurationSeconds / (1 + buildingSpeedRatio)
    : baseDurationSeconds) / (1 + techniqueSpeedRatio)

  if (buildingOutputRatio !== 0) {
    const percent = Math.round(buildingOutputRatio * 100)
    bonuses.push({
      sourceName: t('app.action.bonusBuildingSource'),
      tag: `${percent >= 0 ? '+' : ''}${percent}%`,
      type: 'building',
      multiplier: 1 + buildingOutputRatio,
      description: t('app.action.bonusBuildingDesc', { percent: `${percent >= 0 ? '+' : ''}${percent}%` }),
    })
  }
  if (buildingSpeedRatio > 0) {
    const speedPercent = Math.round(buildingSpeedRatio * 100)
    bonuses.push({
      sourceName: t('app.action.bonusBuildingSource'),
      tag: t('app.action.bonusBuildingSpeedTag', { percent: speedPercent }),
      type: 'speed',
      description: t('app.action.bonusBuildingSpeedDesc', { percent: speedPercent }),
    })
  }

  // 活跃 Buff 只携带战斗属性修饰器（hpRegen/mpRegen/attackSpeedPoints/damageAbsorptionPoints/statRatios），
  // 不再承载行动收益倍率——收益加成唯一通道为持续消耗品（autoUseSlots）与灵脉/建筑倍率。
  const totalMultiplier = autoUseMultiplier * worldMultiplier * (1 + buildingOutputRatio)

  // 4. 修炼专属境界判定（产出数值统一由 yields 驱动）
  let cultivationDetail: CultivationYieldDetail | undefined
  if (isCultivation) {
    const experience = stats?.experience ?? 0
    // 圆满判定必须用服务端权威瓶颈（含灵根乘区），否则入天门玩家在裸瓶颈处就被告知「修为圆满待突破」。
    const realmProgress = calculateRealmProgress(experience, stats?.realmLevel,
      serverExpToNextCap(stats?.expToNextLinggen, stats?.realmLevel))
    const expToCap = realmProgress.expToNext - realmProgress.currentLayerExp
    const isPendingBreakthrough = realmProgress.expToNext > 0 && expToCap <= 0.0001 && experience > 0

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
  // 引气入体灵石注灵加速（总纲 §6.4.3）：仅放大修为与灵力基础值，重算后续乘区；六维经验定额不受影响
  const spiritStoneCost = (action.id === 'cultivation.draw-qi' && parameters?.spiritStone)
    ? parameters.spiritStone
    : 0
  const spiritStoneMultiplier = spiritStoneCost >= 100
    ? 4.0
    : (spiritStoneCost >= 10 ? 2.0 : (spiritStoneCost >= 1 ? 1.2 : 1.0))
  if (spiritStoneMultiplier > 1.0) {
    const stonePercent = Math.round((spiritStoneMultiplier - 1.0) * 100)
    bonuses.push({
      sourceName: t('app.action.bonusSpiritStoneSource'),
      tag: `+${stonePercent}%`,
      type: 'spiritStone',
      multiplier: spiritStoneMultiplier,
      description: t('app.action.bonusSpiritStoneDesc', { cost: spiritStoneCost, percent: `+${stonePercent}%` }),
    })
  }


  // 5. 收益解算：物品按 id 聚合，资源类收益按 kind 折算合并为单轮总量（产出只展示每轮口径），增益收益原样透传。
  //    按秒收益整轮总量 = 速率 × 实际耗时整秒数（服务端进度按整秒量化推进，时长浮点贴整取上界）。
  const effectiveRoundSeconds = Math.max(0, Math.ceil(actualDurationSeconds - 1e-9))
  const itemAmountMap = new Map<string, number>()
  const resourceBaseMap = new Map<ResourceYieldKind, number>()
  const rootExpMap = new Map<string, number>()
  const buffYields: ActionYieldPreview['buffYields'] = []
  for (const yieldEntry of action.yields ?? []) {
    switch (yieldEntry.kind) {
      case 'item': {
        if (!yieldEntry.id) break
        itemAmountMap.set(yieldEntry.id, (itemAmountMap.get(yieldEntry.id) ?? 0) + yieldEntry.amount)
        break
      }
      case 'mana':
      case 'experience': {
        const rawPerRound = yieldEntry.per === 'second' ? yieldEntry.amount * effectiveRoundSeconds : yieldEntry.amount
        const basePerRound = rawPerRound * spiritStoneMultiplier
        resourceBaseMap.set(yieldEntry.kind, (resourceBaseMap.get(yieldEntry.kind) ?? 0) + basePerRound)
        break
      }
      case 'hitpoints': {
        const perRound = yieldEntry.per === 'second' ? yieldEntry.amount * effectiveRoundSeconds : yieldEntry.amount
        resourceBaseMap.set(yieldEntry.kind, (resourceBaseMap.get(yieldEntry.kind) ?? 0) + perRound)
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
      case 'rootexp': {
        // 六维经验定额展示：不吃 totalMultiplier（总纲 §20.1.4），按秒速率按整轮耗时折算并入。
        if (!yieldEntry.aspect) break
        const perRound = yieldEntry.per === 'second' ? yieldEntry.amount * effectiveRoundSeconds : yieldEntry.amount
        rootExpMap.set(yieldEntry.aspect, (rootExpMap.get(yieldEntry.aspect) ?? 0) + perRound)
        break
      }
    }
  }

  const resourceYields: ActionYieldPreview['resourceYields'] = Array.from(resourceBaseMap.entries()).map(([kind, baseAmount]) => ({
    kind,
    baseAmount,
    finalAmount: baseAmount * totalMultiplier,
    multiplier: totalMultiplier,
  }))

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
    rootExpYields: Array.from(rootExpMap.entries()).map(([aspect, amount]) => ({ aspect, amount })),
    bonuses,
    baseDurationSeconds,
    actualDurationSeconds,
  }
}
