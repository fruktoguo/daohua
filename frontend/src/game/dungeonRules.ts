import type {
  CombatantDefinition,
  DungeonDefinition,
  DungeonLevelAnchor,
  DungeonLootEntry,
  DungeonMonsterDefinition,
  MonsterDropDefinition,
} from './types'

/** 追忆副本周清体力上限（240/日 × 7 日 = 1680，与服务端 DungeonEnergyRules.MaxEnergy 一致） */
export const TILI_MAX = 1680
/** 新角色与旧存档首登初始化体力 */
export const TILI_INITIAL = 240
/** 每点体力恢复间隔（秒）：10 点/小时 = 360 秒/点 */
export const TILI_RECOVERY_INTERVAL_SECONDS = 360

/** 追忆副本难度下限与上限（虚境 3 档 + 现世 8 阶 = 11 档：0..10） */
export const MIN_DUNGEON_DIFFICULTY = 0
export const MAX_DUNGEON_DIFFICULTY = 10
/** 现世模式的起始难度档位（d >= 3 为现世八阶） */
export const REALITY_MIN_DIFFICULTY = 3
/** 动态怪物等级上限 */
export const MAX_DUNGEON_MONSTER_LEVEL = 127

/** 追忆副本大类模式：虚境（固定原等级） / 现世（动态等级 + 阶位叠乘） */
export type DungeonMode = 'virtual' | 'reality'

/** 11 档追忆副本单次体力消耗（对应 0..10，与服务端 DungeonDifficultyRules.EnergyCost 严格对齐） */
export const DUNGEON_ENERGY_COSTS: readonly number[] = [
  30, 40, 50, 60, 80, 100, 120, 150, 180, 210, 240,
]

/** 11 档追忆副本元数据：虚境 3 档常规色 + 现世 8 阶直用游戏内八品品质色（Q1~Q8） */
export interface DungeonDifficultyMeta {
  readonly level: number
  readonly mode: DungeonMode
  readonly key: string
  readonly energyCost: number
  readonly styleTone:
    | 'normal'
    | 'hard'
    | 'hell'
    | 'mundane'
    | 'yellow'
    | 'mystic'
    | 'earth'
    | 'heaven'
    | 'immortal'
    | 'divine'
    | 'chaos'
  readonly isQualityTier: boolean
}

export const DUNGEON_DIFFICULTIES: readonly DungeonDifficultyMeta[] = [
  { level: 0, mode: 'virtual', key: 'normal', energyCost: 30, styleTone: 'normal', isQualityTier: false },
  { level: 1, mode: 'virtual', key: 'hard', energyCost: 40, styleTone: 'hard', isQualityTier: false },
  { level: 2, mode: 'virtual', key: 'hell', energyCost: 50, styleTone: 'hell', isQualityTier: false },
  { level: 3, mode: 'reality', key: 'mundane', energyCost: 60, styleTone: 'mundane', isQualityTier: true },
  { level: 4, mode: 'reality', key: 'yellow', energyCost: 80, styleTone: 'yellow', isQualityTier: true },
  { level: 5, mode: 'reality', key: 'mystic', energyCost: 100, styleTone: 'mystic', isQualityTier: true },
  { level: 6, mode: 'reality', key: 'earth', energyCost: 120, styleTone: 'earth', isQualityTier: true },
  { level: 7, mode: 'reality', key: 'heaven', energyCost: 150, styleTone: 'heaven', isQualityTier: true },
  { level: 8, mode: 'reality', key: 'immortal', energyCost: 180, styleTone: 'immortal', isQualityTier: true },
  { level: 9, mode: 'reality', key: 'divine', energyCost: 210, styleTone: 'divine', isQualityTier: true },
  { level: 10, mode: 'reality', key: 'chaos', energyCost: 240, styleTone: 'chaos', isQualityTier: true },
]

/** 钳位追忆副本难度到合法区间 [0, 10] */
export function clampDungeonDifficulty(difficulty: number): number {
  if (!Number.isFinite(difficulty) || difficulty <= MIN_DUNGEON_DIFFICULTY) return MIN_DUNGEON_DIFFICULTY
  if (difficulty >= MAX_DUNGEON_DIFFICULTY) return MAX_DUNGEON_DIFFICULTY
  return Math.floor(difficulty)
}

/** 判定是否为现世模式（d >= 3） */
export function isRealityDifficulty(difficulty: number): boolean {
  return clampDungeonDifficulty(difficulty) >= REALITY_MIN_DIFFICULTY
}

/** 获取指定难度所属模式（virtual / reality） */
export function getDungeonMode(difficulty: number): DungeonMode {
  return isRealityDifficulty(difficulty) ? 'reality' : 'virtual'
}

/** 获取指定模式下的全部可选档位列表 */
export function getDungeonDifficultiesByMode(mode: DungeonMode): readonly DungeonDifficultyMeta[] {
  return DUNGEON_DIFFICULTIES.filter(item => item.mode === mode)
}

/** 获取指定模式切换后的默认难度（虚境默认 0 普通，现世默认 3 凡阶） */
export function getDefaultDifficultyForMode(mode: DungeonMode): number {
  return mode === 'reality' ? REALITY_MIN_DIFFICULTY : 0
}

/** 获取指定追忆副本难度元信息 */
export function getDungeonDifficultyMeta(difficulty: number): DungeonDifficultyMeta {
  return DUNGEON_DIFFICULTIES[clampDungeonDifficulty(difficulty)]
}

/** 获取指定追忆副本难度单次体力消耗 */
export function getDungeonEnergyCost(difficulty: number): number {
  return DUNGEON_ENERGY_COSTS[clampDungeonDifficulty(difficulty)]
}

/** 追忆副本敌怪气血轴缩放倍率（1.30^d） */
export function getDungeonHpMultiplier(difficulty: number): number {
  return Math.pow(1.30, clampDungeonDifficulty(difficulty))
}

/** 追忆副本敌怪常规属性轴缩放倍率（1.15^d） */
export function getDungeonStatMultiplier(difficulty: number): number {
  return Math.pow(1.15, clampDungeonDifficulty(difficulty))
}

/** 追忆副本单位体力效率倍率（1 + 0.15d） */
export function getDungeonEfficiencyMultiplier(difficulty: number): number {
  return 1.0 + 0.15 * clampDungeonDifficulty(difficulty)
}

/** 追忆副本单次总收益倍率（(Cost / 30) × (1 + 0.15d)） */
export function getDungeonTotalRewardMultiplier(difficulty: number): number {
  const d = clampDungeonDifficulty(difficulty)
  return (DUNGEON_ENERGY_COSTS[d] / 30.0) * (1.0 + 0.15 * d)
}

/** 追忆副本单次稀有掉落率效率倍率（1 + 0.25d） */
export function getDungeonRareDropRateMultiplier(difficulty: number): number {
  return 1.0 + 0.25 * clampDungeonDifficulty(difficulty)
}

/** 追忆副本高耗体档位对应的稀有掉落期望抽次展示值 */
export function getDungeonRareRollCount(difficulty: number): number {
  return Math.max(1, Math.round((getDungeonEnergyCost(difficulty) / 30.0) * 10) / 10)
}

/** 计算副本最低原始怪物等级 */
export function resolveDungeonMinMonsterLevel(dungeon: DungeonDefinition): number {
  if (typeof dungeon.minMonsterLevel === 'number' && dungeon.minMonsterLevel >= 1) {
    return Math.floor(dungeon.minMonsterLevel)
  }
  let minLvl = Infinity
  for (const wave of dungeon.waves) {
    for (const enemy of wave.enemies) {
      if (typeof enemy.level === 'number' && enemy.level < minLvl) minLvl = enemy.level
    }
  }
  return Number.isFinite(minLvl) ? Math.max(1, Math.floor(minLvl)) : Math.max(1, dungeon.baseLevel)
}

/** 解析现世模式目标基准等级（含下限保护：max(队伍最高等级, 副本最低怪等级)） */
export function resolveDungeonTargetBaseLevel(
  minDungeonMonsterLevel: number,
  maxPartyRealmLevel: number,
  difficulty: number,
): number {
  const floor = Math.max(1, Math.min(MAX_DUNGEON_MONSTER_LEVEL, Math.floor(minDungeonMonsterLevel || 1)))
  if (!isRealityDifficulty(difficulty)) return floor
  const partyLvl = Math.max(1, Math.floor(maxPartyRealmLevel || 1))
  return Math.max(1, Math.min(MAX_DUNGEON_MONSTER_LEVEL, Math.max(partyLvl, floor)))
}

/** 解析单只怪物在当前难度下的实际等级（现世保差值升阶） */
export function resolveDungeonDynamicMonsterLevel(
  originalMonsterLevel: number,
  minDungeonMonsterLevel: number,
  maxPartyRealmLevel: number,
  difficulty: number,
): number {
  const orig = Math.max(1, Math.min(MAX_DUNGEON_MONSTER_LEVEL, Math.floor(originalMonsterLevel || 1)))
  if (!isRealityDifficulty(difficulty)) return orig
  const floor = Math.max(1, Math.min(MAX_DUNGEON_MONSTER_LEVEL, Math.floor(minDungeonMonsterLevel || 1)))
  const targetBase = resolveDungeonTargetBaseLevel(floor, maxPartyRealmLevel, difficulty)
  const delta = Math.max(0, orig - floor)
  return Math.max(1, Math.min(MAX_DUNGEON_MONSTER_LEVEL, targetBase + delta))
}

/** 从静态目录列表中提取共享的 1..127 级期望锚点表 */
export function extractDungeonLevelAnchors(
  dungeons: readonly DungeonDefinition[],
): readonly DungeonLevelAnchor[] | null {
  for (const d of dungeons) {
    if (d.levelAnchors && d.levelAnchors.length > 0) return d.levelAnchors
  }
  return null
}

/**
 * 追忆副本怪物双轴属性缩放（前端详情与预览同源）：
 * 气血 × 1.30^d，常规攻防命中闪避等 × 1.15^d（向上取整），严禁缩放攻速与五行增减伤。
 */
export function scaleDungeonMonsterAttributes<T extends {
  maxHitpoints?: number
  maxManapoints?: number
  physicalAttack?: number
  spellAttack?: number
  armor?: number
  magicResistance?: number
  accuracy?: number
  evasion?: number
  critical?: number
  criticalResistance?: number
  breakPower?: number
  parryResolve?: number
  hpRegenPerFiveSeconds?: number
  mpRegenPerFiveSeconds?: number
  attackIntervalMilliseconds?: number
}>(baseAttrs: T, difficulty: number): T {
  const d = clampDungeonDifficulty(difficulty)
  if (d <= 0) return baseAttrs
  const hpMult = getDungeonHpMultiplier(d)
  const statMult = getDungeonStatMultiplier(d)
  const scaleCeil = (v: number | undefined, mult: number): number | undefined =>
    v === undefined ? undefined : Math.ceil(v * mult)
  return {
    ...baseAttrs,
    maxHitpoints: baseAttrs.maxHitpoints === undefined ? undefined : Math.max(1, Math.ceil(baseAttrs.maxHitpoints * hpMult)),
    maxManapoints: scaleCeil(baseAttrs.maxManapoints, statMult),
    physicalAttack: scaleCeil(baseAttrs.physicalAttack, statMult),
    spellAttack: scaleCeil(baseAttrs.spellAttack, statMult),
    armor: scaleCeil(baseAttrs.armor, statMult),
    magicResistance: scaleCeil(baseAttrs.magicResistance, statMult),
    accuracy: scaleCeil(baseAttrs.accuracy, statMult),
    evasion: scaleCeil(baseAttrs.evasion, statMult),
    critical: scaleCeil(baseAttrs.critical, statMult),
    criticalResistance: scaleCeil(baseAttrs.criticalResistance, statMult),
    breakPower: scaleCeil(baseAttrs.breakPower, statMult),
    parryResolve: scaleCeil(baseAttrs.parryResolve, statMult),
    hpRegenPerFiveSeconds: scaleCeil(baseAttrs.hpRegenPerFiveSeconds, statMult),
    mpRegenPerFiveSeconds: scaleCeil(baseAttrs.mpRegenPerFiveSeconds, statMult),
    attackIntervalMilliseconds: baseAttrs.attackIntervalMilliseconds,
  }
}

/** 按追忆副本难度缩放单波怪物掉落（严格仅随难度等阶变化，不因现世动态等级额外膨胀） */
export function scaleDungeonMonsterDrops(
  drops: readonly MonsterDropDefinition[],
  difficulty: number,
): MonsterDropDefinition[] {
  const d = clampDungeonDifficulty(difficulty)
  if (d <= 0) return [...drops]
  const totalMult = getDungeonTotalRewardMultiplier(d)
  const rareMult = getDungeonRareDropRateMultiplier(d)
  return drops.map(drop => {
    if (drop.itemId.startsWith('item.storage-bag')) return drop
    if (drop.dropChance >= 1 - 1e-9) {
      const minQty = Math.max(1, Math.floor(drop.minQuantity * totalMult))
      const maxQty = Math.max(minQty, Math.ceil(drop.maxQuantity * totalMult))
      return { ...drop, minQuantity: minQty, maxQuantity: maxQty }
    }
    return {
      ...drop,
      dropChance: Math.min(1, drop.dropChance * rareMult),
    }
  })
}

/**
 * 计算单只怪物在指定追忆副本、难度及队伍最大等级下的最终实战展示定义（含现世动态等级重算 + 阶位双轴增幅），
 * 可直接传给 `<MonsterTooltipTarget preScaled monster={...}>` 在 Hover 卡片内展示最终具体数值。
 */
export function resolveDungeonMonsterForDisplay(
  monster: DungeonMonsterDefinition | CombatantDefinition,
  dungeon: DungeonDefinition,
  difficulty: number,
  maxPartyRealmLevel: number,
  levelAnchors?: readonly DungeonLevelAnchor[] | null,
): CombatantDefinition {
  const d = clampDungeonDifficulty(difficulty)
  const minLvl = resolveDungeonMinMonsterLevel(dungeon)
  const origLvl = Math.max(1, Math.floor(monster.level ?? 1))
  const dynLvl = resolveDungeonDynamicMonsterLevel(origLvl, minLvl, maxPartyRealmLevel, d)

  let baseAttrs = monster.attributes
  let baseExp = monster.experience ?? 0
  let resolvedTier = monster.tier ?? 1

  const anchors = levelAnchors ?? dungeon.levelAnchors ?? null
  if (dynLvl !== origLvl && anchors && anchors.length >= dynLvl) {
    const anchor = anchors[dynLvl - 1]
    const origAnchor = anchors[Math.min(anchors.length, origLvl) - 1]
    if (anchor) {
      resolvedTier = anchor.tier
      const rawRank = (monster.rank ?? 'normal').toLowerCase()
      const attrMult = rawRank === 'boss' ? 1.5 : rawRank === 'elite' ? 1.2 : 0.8
      const hpMult = rawRank === 'boss' ? 20.0 : rawRank === 'elite' ? 5.0 : 3.0
      const post18Step = rawRank === 'boss' ? 0.03 : 0.02
      const post18Mult = 1.0 + post18Step * Math.max(0, dynLvl - 18)
      const attackMult = dynLvl <= 1 ? 0.6 : dynLvl >= 24 ? 0.8 : 0.6 + (0.2 * (dynLvl - 1.0)) / 23.0
      const weights = ('weights' in monster ? monster.weights : null) ?? null
      const w = (key: string): number =>
        weights && typeof weights[key] === 'number' && Number.isFinite(weights[key]) ? weights[key] : 1

      baseAttrs = {
        ...monster.attributes,
        maxHitpoints: Math.ceil(anchor.hp * hpMult * w('hp') * post18Mult),
        maxManapoints: Math.ceil(anchor.mp * attrMult * w('mp') * post18Mult),
        physicalAttack: Math.ceil(anchor.ad * attrMult * w('ad') * attackMult * post18Mult),
        spellAttack: Math.ceil(anchor.ap * attrMult * w('ap') * attackMult * post18Mult),
        armor: Math.ceil(anchor.armor * attrMult * w('armor') * post18Mult),
        magicResistance: Math.ceil(anchor.mr * attrMult * w('mres') * post18Mult),
        accuracy: Math.ceil(anchor.acc * attrMult * w('hit') * post18Mult),
        evasion: Math.ceil(anchor.eva * attrMult * w('eva') * 0.5 * post18Mult),
        critical: Math.ceil(anchor.crit * attrMult * w('crit') * post18Mult),
        criticalResistance: Math.ceil(anchor.cavoid * attrMult * w('cavoid') * 0.5 * post18Mult),
        breakPower: Math.ceil(anchor.breakPt * attrMult * w('break') * post18Mult),
        parryResolve: Math.ceil(anchor.parry * attrMult * w('parry') * post18Mult),
      }

      const pickKillExp = (row: DungeonLevelAnchor): number =>
        rawRank === 'boss' ? row.killExpBoss : rawRank === 'elite' ? row.killExpElite : row.killExpNormal
      const dynKillExp = pickKillExp(anchor)
      const explicitExp = 'explicitExperience' in monster && monster.explicitExperience === true
      if (explicitExp && origAnchor) {
        const origKillExp = pickKillExp(origAnchor)
        baseExp = origKillExp > 0 ? baseExp * (dynKillExp / origKillExp) : dynKillExp
      } else {
        baseExp = dynKillExp
      }
    }
  }

  const finalAttrs = scaleDungeonMonsterAttributes(baseAttrs, d)
  const finalExp = Math.round(baseExp * getDungeonTotalRewardMultiplier(d))
  const finalDrops = scaleDungeonMonsterDrops(monster.drops ?? [], d)

  return {
    ...monster,
    level: dynLvl,
    tier: resolvedTier,
    attributes: finalAttrs,
    experience: finalExp,
    drops: finalDrops,
  }
}

/** 计算第 10 波通关修为与灵石最终展示值（含现世动态等级放大 + 难度总收益倍率） */
export function resolveDungeonClearRewardsPreview(
  dungeon: DungeonDefinition,
  difficulty: number,
  maxPartyRealmLevel: number,
  levelAnchors?: readonly DungeonLevelAnchor[] | null,
): { experience: number; spiritStones: number } {
  const d = clampDungeonDifficulty(difficulty)
  const minLvl = resolveDungeonMinMonsterLevel(dungeon)
  const targetLvl = resolveDungeonTargetBaseLevel(minLvl, maxPartyRealmLevel, d)
  let baseExp = dungeon.baseRewards.experience
  let baseStones = dungeon.baseRewards.spiritStones

  const anchors = levelAnchors ?? dungeon.levelAnchors ?? null
  if (isRealityDifficulty(d) && targetLvl > minLvl && anchors && anchors.length >= targetLvl) {
    const minAnchor = anchors[minLvl - 1]
    const targetAnchor = anchors[targetLvl - 1]
    if (minAnchor && targetAnchor) {
      if (minAnchor.killExpBoss > 0) {
        baseExp = Math.round(baseExp * (targetAnchor.killExpBoss / minAnchor.killExpBoss))
      }
      if (minAnchor.hp > 0) {
        baseStones = Math.round(baseStones * (targetAnchor.hp / minAnchor.hp))
      }
    }
  }

  const rewardMult = getDungeonTotalRewardMultiplier(d)
  return {
    experience: Math.round(baseExp * rewardMult),
    spiritStones: Math.round(baseStones * rewardMult),
  }
}

/** 实时体力快照（前端平滑倒计时推导，与服务端 DungeonEnergyRules.Compute 数学等价） */
export interface DungeonEnergyView {
  current: number
  max: number
  nextRecoverySeconds: number
  fullRecoverySeconds: number
}

export function computeDungeonEnergy(
  anchorValue: number | undefined,
  anchorUpdatedAtUnixSeconds: number | undefined,
  nowUnixSeconds: number,
  fallbackCurrent?: number,
): DungeonEnergyView {
  const safeNow = Number.isFinite(nowUnixSeconds) && nowUnixSeconds > 0 ? nowUnixSeconds : 0
  if (
    anchorUpdatedAtUnixSeconds === undefined ||
    !Number.isFinite(anchorUpdatedAtUnixSeconds) ||
    anchorUpdatedAtUnixSeconds <= 0
  ) {
    const initial = typeof fallbackCurrent === 'number' && Number.isFinite(fallbackCurrent)
      ? Math.max(0, Math.min(TILI_MAX, Math.floor(fallbackCurrent)))
      : TILI_INITIAL
    if (initial >= TILI_MAX) {
      return { current: TILI_MAX, max: TILI_MAX, nextRecoverySeconds: 0, fullRecoverySeconds: 0 }
    }
    return {
      current: initial,
      max: TILI_MAX,
      nextRecoverySeconds: TILI_RECOVERY_INTERVAL_SECONDS,
      fullRecoverySeconds: (TILI_MAX - initial) * TILI_RECOVERY_INTERVAL_SECONDS,
    }
  }

  const clampedAnchor = Math.max(0, Math.min(TILI_MAX, Math.floor(anchorValue ?? TILI_INITIAL)))
  if (clampedAnchor >= TILI_MAX) {
    return { current: TILI_MAX, max: TILI_MAX, nextRecoverySeconds: 0, fullRecoverySeconds: 0 }
  }

  const elapsed = Math.max(0, safeNow - anchorUpdatedAtUnixSeconds)
  const recoveredTicks = Math.floor(elapsed / TILI_RECOVERY_INTERVAL_SECONDS)
  const current = Math.min(TILI_MAX, clampedAnchor + recoveredTicks)
  if (current >= TILI_MAX) {
    return { current: TILI_MAX, max: TILI_MAX, nextRecoverySeconds: 0, fullRecoverySeconds: 0 }
  }

  const advancedAnchor = anchorUpdatedAtUnixSeconds + recoveredTicks * TILI_RECOVERY_INTERVAL_SECONDS
  const remainder = Math.max(0, safeNow - advancedAnchor)
  const nextRecoverySeconds = Math.max(0, TILI_RECOVERY_INTERVAL_SECONDS - remainder)
  const missing = TILI_MAX - current
  const fullRecoverySeconds = nextRecoverySeconds + Math.max(0, missing - 1) * TILI_RECOVERY_INTERVAL_SECONDS
  return {
    current,
    max: TILI_MAX,
    nextRecoverySeconds,
    fullRecoverySeconds,
  }
}

/** 通关掉落项在指定难度下的预期展示结果 */
export interface DungeonLootPreview {
  unlocked: boolean
  minQuantity: number
  maxQuantity: number
  /** 单次通关至少掉落 1 次的概率（0~1） */
  effectiveChance: number
  rolls: number
}

export function computeDungeonLootPreview(entry: DungeonLootEntry, difficulty: number): DungeonLootPreview {
  const d = clampDungeonDifficulty(difficulty)
  if (d < entry.minDifficulty) {
    return {
      unlocked: false,
      minQuantity: entry.minQuantity,
      maxQuantity: entry.maxQuantity,
      effectiveChance: 0,
      rolls: 0,
    }
  }
  if (entry.kind === 'guaranteed') {
    const mult = getDungeonTotalRewardMultiplier(d)
    const minQty = Math.max(1, Math.floor(entry.minQuantity * mult))
    const maxQty = Math.max(minQty, Math.ceil(entry.maxQuantity * mult))
    return {
      unlocked: true,
      minQuantity: minQty,
      maxQuantity: maxQty,
      effectiveChance: 1.0,
      rolls: 1,
    }
  }
  const costScale = getDungeonEnergyCost(d) / 30.0
  const fullRolls = Math.floor(costScale)
  const frac = costScale - fullRolls
  const singleChance = Math.min(1.0, entry.dropChance * getDungeonRareDropRateMultiplier(d))
  const missProb = Math.pow(1 - singleChance, fullRolls) * (1 - frac * singleChance)
  const effectiveChance = Math.max(0, Math.min(1, 1 - missProb))
  return {
    unlocked: true,
    minQuantity: entry.minQuantity,
    maxQuantity: entry.maxQuantity,
    effectiveChance,
    rolls: getDungeonRareRollCount(d),
  }
}
