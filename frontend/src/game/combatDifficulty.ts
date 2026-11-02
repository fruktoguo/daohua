import type { MonsterDropDefinition } from './types'

/** 讨伐历练难度等级枚举 (0: 默认, 1~5: T1~T5) */
export const COMBAT_DIFFICULTY_TIERS = [0, 1, 2, 3, 4, 5] as const
export type CombatDifficultyTier = (typeof COMBAT_DIFFICULTY_TIERS)[number]

export const MIN_DIFFICULTY_TIER = 0
export const MAX_DIFFICULTY_TIER = 5

export const DIFFICULTY_BRACKET_DEFAULT = 0
export const DIFFICULTY_BRACKET_T1_TO_T3 = 1
export const DIFFICULTY_BRACKET_T4_TO_T5 = 2

/** 钳位难度到合法范围 [0, 5] */
export function clampDifficultyTier(tier: number): CombatDifficultyTier {
  if (!Number.isFinite(tier) || tier <= 0) return 0
  if (tier >= 5) return 5
  return Math.floor(tier) as CombatDifficultyTier
}

/**
 * 获取难度对应的斗法匹配区间：
 * 0: 默认档独立匹配
 * 1: T1 ~ T3 互相匹配
 * 2: T4 ~ T5 互相匹配
 */
export function getDifficultyBracket(tier: number): 0 | 1 | 2 {
  const clamped = clampDifficultyTier(tier)
  if (clamped === 0) return DIFFICULTY_BRACKET_DEFAULT
  if (clamped <= 3) return DIFFICULTY_BRACKET_T1_TO_T3
  return DIFFICULTY_BRACKET_T4_TO_T5
}

/** 难度对应的怪物全属性指数缩放倍率 (1.25^T) */
export function getDifficultyAttributeMultiplier(tier: number): number {
  const clamped = clampDifficultyTier(tier)
  return Math.pow(1.25, clamped)
}

/** 难度对应的击杀经验线性加成倍率 (1 + 0.60 * T) */
export function getDifficultyExperienceMultiplier(tier: number): number {
  const clamped = clampDifficultyTier(tier)
  return 1.0 + 0.60 * clamped
}

/** 难度对应的掉落率线性加成倍率 (1 + 0.25 * T) */
export function getDifficultyDropRateMultiplier(tier: number): number {
  const clamped = clampDifficultyTier(tier)
  return 1.0 + 0.25 * clamped
}

/** 难度对应的 100% 保底掉落项数量线性加成倍率 (1 + 0.25 * T) */
export function getDifficultyDropQuantityMultiplier(tier: number): number {
  const clamped = clampDifficultyTier(tier)
  return 1.0 + 0.25 * clamped
}

/**
 * 对怪物属性根据所选难度进行缩放，且底层计算一律 Math.ceil 向上取整
 */
export function scaleMonsterAttributes<T extends {
  maxHitpoints?: number
  maxManapoints?: number
  physicalAttack?: number
  spellAttack?: number
  armor?: number
  magicResistance?: number
  attackIntervalMilliseconds?: number
}>(baseAttrs: T, tier: number): T {
  const mult = getDifficultyAttributeMultiplier(tier)
  return {
    ...baseAttrs,
    maxHitpoints: Math.ceil((baseAttrs.maxHitpoints ?? 1) * mult),
    maxManapoints: Math.ceil((baseAttrs.maxManapoints ?? 0) * mult),
    physicalAttack: Math.ceil((baseAttrs.physicalAttack ?? 0) * mult),
    spellAttack: Math.ceil((baseAttrs.spellAttack ?? 0) * mult),
    armor: Math.ceil((baseAttrs.armor ?? 0) * mult),
    magicResistance: Math.ceil((baseAttrs.magicResistance ?? 0) * mult),
    // 普攻间隔毫秒不随属性倍率变长，保持攻击节奏不变
    attackIntervalMilliseconds: baseAttrs.attackIntervalMilliseconds,
  }
}

/**
 * 对怪物掉落表根据所选难度进行概率提升或数量提升
 */
export function scaleMonsterDrops(
  drops: readonly MonsterDropDefinition[],
  tier: number
): MonsterDropDefinition[] {
  const rateMult = getDifficultyDropRateMultiplier(tier)
  const qtyMult = getDifficultyDropQuantityMultiplier(tier)

  return drops.map(drop => {
    if (drop.dropChance >= 1.0 - 1e-9) {
      // 概率已达 100% 的保底掉落，提升掉落量区间（向上取整）
      const min = Math.ceil(drop.minQuantity * qtyMult)
      const max = Math.max(min, Math.ceil(drop.maxQuantity * qtyMult))
      return {
        ...drop,
        minQuantity: min,
        maxQuantity: max,
      }
    }
    // 概率未达 100% 的条目，线性提升掉率，封顶 100%
    return {
      ...drop,
      dropChance: Math.min(1.0, drop.dropChance * rateMult),
    }
  })
}
