import { clamp, equalsIgnoreCase } from './math.ts'

// ---------- 百分比曲线与伤害/元素枚举 ----------

export const PercentCurveMode = {
  Linear: 0,
  NonLinear: 1,
} as const
export type PercentCurveMode = (typeof PercentCurveMode)[keyof typeof PercentCurveMode]

/** 点数转乘区倍率（100 点 = 100%）。 */
export function percentMultiplier(points: number, mode: PercentCurveMode): number {
  if (!Number.isFinite(points)) {
    throw new Error('百分比点数必须有限。')
  }
  if (mode === PercentCurveMode.Linear) {
    return points >= 0 ? 1 + points / 100 : 100 / (100 - points)
  }
  return points >= 0 ? 100 / (100 + points) : 1 - points / 100
}

export const CombatDamageKind = {
  Physical: 0,
  Spell: 1,
  True: 2,
} as const
export type CombatDamageKind = (typeof CombatDamageKind)[keyof typeof CombatDamageKind]

export const CombatElement = {
  None: 0,
  Metal: 1,
  Wood: 2,
  Water: 3,
  Fire: 4,
  Earth: 5,
} as const
export type CombatElement = (typeof CombatElement)[keyof typeof CombatElement]

export function parseCombatElement(value: string | null | undefined): CombatElement {
  if (!value) return CombatElement.None
  switch (value.toLowerCase()) {
    case 'metal': return CombatElement.Metal
    case 'wood': return CombatElement.Wood
    case 'water': return CombatElement.Water
    case 'fire': return CombatElement.Fire
    case 'earth': return CombatElement.Earth
    default: return CombatElement.None
  }
}

export function parseDamageKind(value: string | null | undefined): CombatDamageKind {
  if (equalsIgnoreCase(value, 'spell')) return CombatDamageKind.Spell
  if (equalsIgnoreCase(value, 'true')) return CombatDamageKind.True
  return CombatDamageKind.Physical
}

export const CombatMonsterRank = {
  Minion: 0,
  Elite: 1,
  Boss: 2,
} as const
export type CombatMonsterRank = (typeof CombatMonsterRank)[keyof typeof CombatMonsterRank]

export const PartyWipeRule = {
  Retry: 0,
  Abort: 1,
} as const
export type PartyWipeRule = (typeof PartyWipeRule)[keyof typeof PartyWipeRule]

export const AutoUseBar = {
  Combat: 'combat',
  Formation: 'formation',
  Alchemy: 'alchemy',
  Enhancement: 'enhancement',
  Forging: 'forging',
  Talisman: 'talisman',
  Gathering: 'gathering',
} as const

export const SlotGroupGeneral = 'general'
export const SlotGroupPotion = 'potion'

export const ItemCategories = {
  Material: 'material',
  Equipment: 'equipment',
  Consumable: 'consumable',
  Pill: 'pill',
  Manual: 'manual',
  Currency: 'currency',
  StorageBag: 'storage-bag',
} as const

export const CombatRespawnPolicy = {
  WaitAll: 0,
  Immediate: 1,
  WaitTarget: 2,
} as const
export type CombatRespawnPolicy = (typeof CombatRespawnPolicy)[keyof typeof CombatRespawnPolicy]

// ---------- Buff 效果通道注册表 ----------

export const BuffEffectStackMode = {
  PerStack: 0,
  Instance: 1,
} as const
export type BuffEffectStackMode = (typeof BuffEffectStackMode)[keyof typeof BuffEffectStackMode]

export const BuffEffectTickGroup = {
  None: 0,
  Regen5s: 1,
  Tick1s: 2,
} as const
export type BuffEffectTickGroup = (typeof BuffEffectTickGroup)[keyof typeof BuffEffectTickGroup]

export const BuffOutOfCombatPanel = {
  None: 0,
  HpRegenFlat: 1,
  MpRegenFlat: 2,
  HpRegenPost: 3,
  MpRegenPost: 4,
  AttackSpeedPoints: 5,
  DamageAbsorptionPoints: 6,
} as const
export type BuffOutOfCombatPanel = (typeof BuffOutOfCombatPanel)[keyof typeof BuffOutOfCombatPanel]

export const BuffEffectChannel = {
  HpRegen: 0,
  MpRegen: 1,
  HpRegenPostRatio: 2,
  MpRegenPostRatio: 3,
  HpRegenPerSecond: 4,
  ThreatPerSecondMaxHpPoints: 5,
  AttackSpeedPoints: 6,
  AttackPower: 7,
  Accuracy: 8,
  Critical: 9,
  DamageBonusPoints: 10,
  DamageAbsorptionPoints: 11,
  ResistancePoints: 12,
  ThreatPoints: 13,
  Evasion: 14,
  EvasionPostRatio: 15,
  ActionSpeedPoints: 16,
  CriticalDamagePoints: 17,
  HealingBonusPoints: 18,
  FireSpellDamagePerSecond: 19,
} as const
export type BuffEffectChannel = (typeof BuffEffectChannel)[keyof typeof BuffEffectChannel]

export interface BuffEffect {
  channel: number
  value: number
}

export type BuffEffectEntry = BuffEffect

export interface BuffEffectChannelInfo {
  readonly channel: BuffEffectChannel
  readonly configKey: string
  readonly stackMode: BuffEffectStackMode
  readonly growsWithLevel: boolean
  readonly tickGroup: BuffEffectTickGroup
  readonly outOfCombatPanel: BuffOutOfCombatPanel
}

export const BUFF_CHANNELS: readonly BuffEffectChannelInfo[] = [
  { channel: BuffEffectChannel.HpRegen, configKey: 'hpRegen', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.Regen5s, outOfCombatPanel: BuffOutOfCombatPanel.HpRegenFlat },
  { channel: BuffEffectChannel.MpRegen, configKey: 'mpRegen', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.Regen5s, outOfCombatPanel: BuffOutOfCombatPanel.MpRegenFlat },
  { channel: BuffEffectChannel.HpRegenPostRatio, configKey: 'hpRegenPostRatio', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.Regen5s, outOfCombatPanel: BuffOutOfCombatPanel.HpRegenPost },
  { channel: BuffEffectChannel.MpRegenPostRatio, configKey: 'mpRegenPostRatio', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.Regen5s, outOfCombatPanel: BuffOutOfCombatPanel.MpRegenPost },
  { channel: BuffEffectChannel.HpRegenPerSecond, configKey: 'hpRegenPerSecond', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.Tick1s, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.ThreatPerSecondMaxHpPoints, configKey: 'threatPerSecondMaxHpPoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.Tick1s, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.AttackSpeedPoints, configKey: 'attackSpeedPoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.AttackSpeedPoints },
  { channel: BuffEffectChannel.AttackPower, configKey: 'attackPower', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.Accuracy, configKey: 'accuracy', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.Critical, configKey: 'critical', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.DamageBonusPoints, configKey: 'damageBonusPoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.DamageAbsorptionPoints, configKey: 'damageAbsorptionPoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.DamageAbsorptionPoints },
  { channel: BuffEffectChannel.ResistancePoints, configKey: 'resistancePoints', stackMode: BuffEffectStackMode.Instance, growsWithLevel: false, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.ThreatPoints, configKey: 'threatPoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.Evasion, configKey: 'evasion', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.EvasionPostRatio, configKey: 'evasionPostRatio', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.ActionSpeedPoints, configKey: 'actionSpeedPoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.CriticalDamagePoints, configKey: 'criticalDamagePoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.HealingBonusPoints, configKey: 'healingBonusPoints', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.None, outOfCombatPanel: BuffOutOfCombatPanel.None },
  { channel: BuffEffectChannel.FireSpellDamagePerSecond, configKey: 'fireSpellDamagePerSecond', stackMode: BuffEffectStackMode.PerStack, growsWithLevel: true, tickGroup: BuffEffectTickGroup.Tick1s, outOfCombatPanel: BuffOutOfCombatPanel.None },
]

export const BUFF_CHANNEL_BY_KEY = new Map<string, BuffEffectChannelInfo>(
  BUFF_CHANNELS.map((info) => [info.configKey, info]),
)

export function parseBuffEffects(buffObj: Record<string, unknown>, effectGrowth: number): BuffEffect[] | null {
  let effects: BuffEffect[] | null = null
  for (const [key, val] of Object.entries(buffObj)) {
    const info = BUFF_CHANNEL_BY_KEY.get(key)
    if (!info || typeof val !== 'number' || !Number.isFinite(val)) continue
    const scaled = info.growsWithLevel ? val * effectGrowth : val
    if (!effects) effects = []
    effects.push({ channel: info.channel, value: scaled })
  }
  return effects
}

// ---------- 成长曲线与难度公式 ----------

export const CRITICAL_DAMAGE_BASE_POINTS = 50
export const COMBAT_GROWTH_MAX_LEVEL = 127

export function growthProgress(level: number): number {
  return Math.pow((clamp(level, 1, COMBAT_GROWTH_MAX_LEVEL) - 1) / (COMBAT_GROWTH_MAX_LEVEL - 1), 2)
}

export function growthSpeedPoints(level: number): number {
  return 100 * (1 / (1 - 0.5 * growthProgress(level)) - 1)
}

export function growthCooldownReductionPoints(level: number): number {
  return 100 * (1 / (1 - 0.75 * growthProgress(level)) - 1)
}

export function growthSkillBonusPoints(tier: number): number {
  return 30 * (tier - 1)
}

export function growthCriticalDamagePoints(level: number): number {
  return CRITICAL_DAMAGE_BASE_POINTS + 350 * growthProgress(level)
}

export function growthBasicAttackBonusPoints(level: number): number {
  return 100 * growthProgress(level)
}

export function abilityLevelGrowth(level: number): number {
  return 1 + (level - 1) * 0.02
}

export function abilityLevelForExp(exp: number): number {
  if (!Number.isFinite(exp) || exp <= 0) return 1
  const maxK = 126
  let k = Math.max(0, Math.min(maxK, Math.floor(Math.cbrt(exp / 100))))
  while (k < maxK && 100 * (k + 1) * (k + 1) * (k + 1) <= exp) k += 1
  while (k > 0 && 100 * k * k * k > exp) k -= 1
  return k + 1
}

export function clampDifficulty(difficulty: number): number {
  return Math.max(0, Math.min(5, Math.trunc(difficulty)))
}

export function difficultyAttributeMultiplier(difficulty: number): number {
  return Math.pow(1.25, clampDifficulty(difficulty))
}

export function difficultyExperienceMultiplier(difficulty: number): number {
  return 1.0 + 0.6 * clampDifficulty(difficulty)
}

export function difficultyDropRateMultiplier(difficulty: number): number {
  return 1.0 + 0.25 * clampDifficulty(difficulty)
}

export function difficultyDropQuantityMultiplier(difficulty: number): number {
  return 1.0 + 0.25 * clampDifficulty(difficulty)
}

export interface CombatEffectProfile {
  hpDrainPoints: number
  mpDrainPoints: number
  damageReflectPoints: number
  healingBonusPoints: number
  threatOnTakenPoints: number
}

export const EMPTY_COMBAT_EFFECT_PROFILE: CombatEffectProfile = {
  hpDrainPoints: 0,
  mpDrainPoints: 0,
  damageReflectPoints: 0,
  healingBonusPoints: 0,
  threatOnTakenPoints: 0,
}

export interface CombatantStats {
  maxHitpoints: number
  attackPower: number
  defense: number
  attackIntervalSeconds: number
  accuracy: number
  evasion: number
  damageKind: CombatDamageKind
  spellAttack: number
  maxManapoints: number
  magicResistance: number
  resistanceScale: number
  critical: number
  criticalResistance: number
  criticalDamagePoints: number
  breakPower: number
  parryResolve: number
  element: CombatElement
  physicalDamageBonusPoints: number
  spellDamageBonusPoints: number
  metalDamageBonusPoints: number
  woodDamageBonusPoints: number
  waterDamageBonusPoints: number
  fireDamageBonusPoints: number
  earthDamageBonusPoints: number
  totalDamageBonusPoints: number
  basicAttackBonusPoints: number
  skillBonusPoints: number
  physicalDamageReductionPoints: number
  spellDamageReductionPoints: number
  metalDamageReductionPoints: number
  woodDamageReductionPoints: number
  waterDamageReductionPoints: number
  fireDamageReductionPoints: number
  earthDamageReductionPoints: number
  damageAbsorptionPoints: number
  fixedDamageBonus: number
  fixedDamageReduction: number
  fixedPhysicalDamageBonus: number
  fixedPhysicalDamageReduction: number
  fixedSpellDamageBonus: number
  fixedSpellDamageReduction: number
  attackSpeedPoints: number
  actionSpeedPoints: number
  cooldownReductionPoints: number
  threatBonusPoints: number
  hpRegen: number
  mpRegen: number
  effects: CombatEffectProfile
  traits: string[] | null
  traitLevels: Record<string, number> | null
  combatExperience: number
}

export function createCombatantStats(
  maxHitpoints: number,
  attackPower: number,
  defense: number,
  attackIntervalSeconds: number,
  accuracy: number,
  evasion: number,
  damageKind: CombatDamageKind = CombatDamageKind.Physical,
  overrides?: Partial<CombatantStats>,
): CombatantStats {
  return {
    maxHitpoints,
    attackPower,
    defense,
    attackIntervalSeconds,
    accuracy,
    evasion,
    damageKind,
    spellAttack: 0,
    maxManapoints: 0,
    magicResistance: 0,
    resistanceScale: 1,
    critical: 0,
    criticalResistance: 0,
    criticalDamagePoints: CRITICAL_DAMAGE_BASE_POINTS,
    breakPower: 0,
    parryResolve: 0,
    element: CombatElement.None,
    physicalDamageBonusPoints: 0,
    spellDamageBonusPoints: 0,
    metalDamageBonusPoints: 0,
    woodDamageBonusPoints: 0,
    waterDamageBonusPoints: 0,
    fireDamageBonusPoints: 0,
    earthDamageBonusPoints: 0,
    totalDamageBonusPoints: 0,
    basicAttackBonusPoints: 0,
    skillBonusPoints: 0,
    physicalDamageReductionPoints: 0,
    spellDamageReductionPoints: 0,
    metalDamageReductionPoints: 0,
    woodDamageReductionPoints: 0,
    waterDamageReductionPoints: 0,
    fireDamageReductionPoints: 0,
    earthDamageReductionPoints: 0,
    damageAbsorptionPoints: 0,
    fixedDamageBonus: 0,
    fixedDamageReduction: 0,
    fixedPhysicalDamageBonus: 0,
    fixedPhysicalDamageReduction: 0,
    fixedSpellDamageBonus: 0,
    fixedSpellDamageReduction: 0,
    attackSpeedPoints: 0,
    actionSpeedPoints: 0,
    cooldownReductionPoints: 0,
    threatBonusPoints: 0,
    hpRegen: 0,
    mpRegen: 0,
    effects: EMPTY_COMBAT_EFFECT_PROFILE,
    traits: null,
    traitLevels: null,
    combatExperience: 0,
    ...overrides,
  }
}

export function ceilCombatantStats(stats: CombatantStats): CombatantStats {
  return {
    ...stats,
    maxHitpoints: Math.ceil(stats.maxHitpoints),
    attackPower: Math.ceil(stats.attackPower),
    defense: Math.ceil(stats.defense),
    spellAttack: Math.ceil(stats.spellAttack),
    maxManapoints: Math.ceil(stats.maxManapoints),
    magicResistance: Math.ceil(stats.magicResistance),
    accuracy: Math.ceil(stats.accuracy),
    evasion: Math.ceil(stats.evasion),
    critical: Math.ceil(stats.critical),
    criticalResistance: Math.ceil(stats.criticalResistance),
    criticalDamagePoints: Math.ceil(stats.criticalDamagePoints),
    breakPower: Math.ceil(stats.breakPower),
    parryResolve: Math.ceil(stats.parryResolve),
    physicalDamageBonusPoints: Math.ceil(stats.physicalDamageBonusPoints),
    spellDamageBonusPoints: Math.ceil(stats.spellDamageBonusPoints),
    totalDamageBonusPoints: Math.ceil(stats.totalDamageBonusPoints),
    basicAttackBonusPoints: Math.ceil(stats.basicAttackBonusPoints),
    skillBonusPoints: Math.ceil(stats.skillBonusPoints),
    physicalDamageReductionPoints: Math.ceil(stats.physicalDamageReductionPoints),
    spellDamageReductionPoints: Math.ceil(stats.spellDamageReductionPoints),
    damageAbsorptionPoints: Math.ceil(stats.damageAbsorptionPoints),
    fixedDamageBonus: Math.ceil(stats.fixedDamageBonus),
    fixedDamageReduction: Math.ceil(stats.fixedDamageReduction),
    fixedPhysicalDamageBonus: Math.ceil(stats.fixedPhysicalDamageBonus),
    fixedPhysicalDamageReduction: Math.ceil(stats.fixedPhysicalDamageReduction),
    fixedSpellDamageBonus: Math.ceil(stats.fixedSpellDamageBonus),
    fixedSpellDamageReduction: Math.ceil(stats.fixedSpellDamageReduction),
    attackSpeedPoints: Math.ceil(stats.attackSpeedPoints),
    actionSpeedPoints: Math.ceil(stats.actionSpeedPoints),
    cooldownReductionPoints: Math.ceil(stats.cooldownReductionPoints),
    threatBonusPoints: Math.ceil(stats.threatBonusPoints),
    hpRegen: Math.ceil(stats.hpRegen),
    mpRegen: Math.ceil(stats.mpRegen),
    combatExperience: Math.ceil(stats.combatExperience),
  }
}

/** 节点建筑怪物削弱：`1 / (1 + max(0, weakenRatio))` 作用在 HP 与双攻双防（不含上限灵力）。 */
export function weakenMonsterStats(stats: CombatantStats, weakenRatio: number): CombatantStats {
  if (weakenRatio <= 0) return stats
  const factor = 1.0 / (1.0 + Math.max(0, weakenRatio))
  return {
    ...stats,
    maxHitpoints: stats.maxHitpoints * factor,
    attackPower: stats.attackPower * factor,
    spellAttack: stats.spellAttack * factor,
    defense: stats.defense * factor,
    magicResistance: stats.magicResistance * factor,
  }
}

// ---------- 特质规则（36 项注册表 + 机制函数） ----------

export const TraitIds = {
  Unseen: 'trait.bukandaowo',
  ThreatOnTaken: 'trait.liweifanfou',
  FirstSight: 'trait.chujian',
  Haste: 'trait.yusuzebuda',
  Outnumbered: 'trait.woyaodashige',
  DuelKing: 'trait.dantiaowang',
  Xuyi: 'trait.xuyi',
  Ranhun: 'trait.ranhun',
} as const

export const BuffIds = {
  Xuyi: 'buff.xuyi',
  Ranhun: 'buff.ranhun',
  Unseen: 'buff.yinshen',
  FirstSight: 'buff.chujian',
  Haste: 'buff.yusuzebuda',
  Outnumbered: 'buff.woyaodashige',
  DuelKing: 'buff.dantiaowang',
} as const

export const BATTLE_LIFETIME_SECONDS = Number.MAX_VALUE
export const UNSEEN_BUFF_DURATION_SECONDS = 10
export const THREAT_ON_TAKEN_TRAIT_POINTS = 100

export interface TraitDefinition {
  readonly id: string
  readonly name: string
  readonly quality: number
  readonly cost: number
  readonly statRatios?: Record<string, number>
  readonly points?: Record<string, number>
}

const TRAIT_LIST: TraitDefinition[] = [
  // Q1 凡品
  { id: 'trait.tianshengmanli', name: '天生蛮力', quality: 1, cost: 2, statRatios: { physicalAttack: 0.10 } },
  { id: 'trait.qiganchongying', name: '气感充盈', quality: 1, cost: 2, statRatios: { spellAttack: 0.10 } },
  { id: 'trait.jinqiangguijan', name: '筋强骨健', quality: 1, cost: 2, statRatios: { maxHitpoints: 0.12 } },
  { id: 'trait.jingluoweitong', name: '经络微通', quality: 1, cost: 2, statRatios: { maxManapoints: 0.15 } },
  { id: 'trait.tiepihoujian', name: '铁皮厚茧', quality: 1, cost: 2, statRatios: { physicalArmor: 0.12 } },
  { id: 'trait.bihankangre', name: '避寒抗热', quality: 1, cost: 2, statRatios: { spellResistance: 0.12 } },
  { id: 'trait.qinglingxiaobu', name: '轻灵小步', quality: 1, cost: 2, statRatios: { evasion: 0.08, accuracy: 0.08 } },
  // Q2 黄品
  { id: 'trait.tianshengjiangu', name: '天生剑骨', quality: 2, cost: 5, statRatios: { physicalAttack: 0.20, criticalChance: 0.10 } },
  { id: 'trait.xuanyinlingqiao', name: '玄阴灵窍', quality: 2, cost: 5, statRatios: { spellAttack: 0.20, maxManapoints: 0.15 } },
  { id: 'trait.tongjiaotiezhu', name: '铜浇铁铸', quality: 2, cost: 5, statRatios: { maxHitpoints: 0.20, physicalArmor: 0.20 } },
  { id: 'trait.daoxinanhe', name: '道心安和', quality: 2, cost: 5, statRatios: { spellResistance: 0.20, criticalAvoidance: 0.15 } },
  { id: 'trait.fengxingbu', name: '风行步', quality: 2, cost: 5, statRatios: { evasion: 0.18, parry: 0.15 } },
  { id: 'trait.ningshenfayan', name: '凝神法眼', quality: 2, cost: 5, statRatios: { accuracy: 0.18, break: 0.15 } },
  { id: 'trait.tunamianchang', name: '吐纳绵长', quality: 2, cost: 5, statRatios: { hitpointRegen: 0.30, manapointRegen: 0.30 } },
  // Q3 玄品
  { id: 'trait.chunyangzhenmai', name: '纯阳真脉', quality: 3, cost: 10, statRatios: { physicalAttack: 0.35, break: 0.20 } },
  { id: 'trait.taiyinfahun', name: '太阴法魂', quality: 3, cost: 10, statRatios: { spellAttack: 0.35, criticalChance: 0.15 } },
  { id: 'trait.panshijingang', name: '磐石金刚', quality: 3, cost: 10, statRatios: { maxHitpoints: 0.30, physicalArmor: 0.30, spellResistance: 0.25 } },
  { id: 'trait.taxuewuying', name: '踏雪无影', quality: 3, cost: 10, statRatios: { evasion: 0.30, parry: 0.20, accuracy: 0.15 } },
  { id: 'trait.shengjiyongdong', name: '生机涌动', quality: 3, cost: 10, statRatios: { maxHitpoints: 0.25, hitpointRegen: 0.50, manapointRegen: 0.50 } },
  // Q4 地品
  { id: 'trait.houtukunyushen', name: '厚土坤舆身', quality: 4, cost: 15, statRatios: { physicalArmor: 0.45, spellResistance: 0.45, maxHitpoints: 0.35 }, points: { totalDamageBonusPoints: -15 } },
  { id: 'trait.lieyangkuangzhangu', name: '烈阳狂战骨', quality: 4, cost: 15, statRatios: { physicalAttack: 0.45, criticalChance: 0.20, break: 0.20, maxHitpoints: -0.15 } },
  { id: 'trait.minghaishihunti', name: '冥海噬魂体', quality: 4, cost: 15, statRatios: { spellAttack: 0.45, maxManapoints: 0.35, criticalChance: 0.15, physicalArmor: -0.20 } },
  { id: 'trait.kurongchangqingjue', name: '枯荣长青诀', quality: 4, cost: 15, statRatios: { hitpointRegen: 0.60, manapointRegen: 0.60, maxHitpoints: 0.25 }, points: { totalDamageBonusPoints: -20 } },
  // Q5 天品
  { id: 'trait.yaodutongyuan', name: '药毒同源', quality: 5, cost: 20, statRatios: { hitpointRegen: 1.00, manapointRegen: 1.00, maxHitpoints: 0.40 }, points: { totalDamageBonusPoints: -35 } },
  { id: 'trait.wanjianguiyi', name: '万剑归一', quality: 5, cost: 20, statRatios: { physicalAttack: 0.65, criticalChance: 0.30, break: 0.25, maxHitpoints: -0.30 }, points: { physicalDamageReductionPoints: -25, spellDamageReductionPoints: -25 } },
  { id: 'trait.jiuyoushiling', name: '九幽噬灵', quality: 5, cost: 20, statRatios: { spellAttack: 0.65, maxManapoints: 0.50, criticalChance: 0.20, physicalArmor: -0.35, evasion: -0.30 } },
  { id: 'trait.budongmingwang', name: '不动明王', quality: 5, cost: 20, statRatios: { physicalArmor: 0.80, spellResistance: 0.80, maxHitpoints: 0.50 }, points: { totalDamageBonusPoints: -45 } },
  { id: 'trait.tianshekuanggu', name: '天煞狂骨', quality: 5, cost: 20, statRatios: { physicalAttack: 0.50, spellAttack: 0.50, criticalChance: 0.40, hitpointRegen: -0.60, criticalAvoidance: -0.35 } },
  // 机制特质
  { id: TraitIds.Unseen, name: '看不到我', quality: 3, cost: 8 },
  { id: TraitIds.ThreatOnTaken, name: '力微饭否', quality: 3, cost: 8, points: { threatOnTakenPoints: THREAT_ON_TAKEN_TRAIT_POINTS } },
  { id: TraitIds.FirstSight, name: '初见', quality: 3, cost: 8 },
  { id: TraitIds.Haste, name: '欲速则不达', quality: 4, cost: 10 },
  { id: TraitIds.Outnumbered, name: '我要打十个', quality: 4, cost: 12 },
  { id: TraitIds.DuelKing, name: '单挑王', quality: 5, cost: 16 },
  { id: TraitIds.Xuyi, name: '蓄意', quality: 6, cost: 25 },
  { id: TraitIds.Ranhun, name: '燃魂', quality: 5, cost: 16 },
]

export const ALL_TRAITS = new Map<string, TraitDefinition>(TRAIT_LIST.map((t) => [t.id, t]))

export function containsTrait(traits: readonly string[] | null | undefined, traitId: string): boolean {
  if (!traits) return false
  for (let i = 0; i < traits.length; i++) {
    if (traits[i] === traitId) return true
  }
  return false
}

export function traitLevelScale(traitLevels: Record<string, number> | null | undefined, traitId: string): number {
  const lv = traitLevels ? (traitLevels[traitId] ?? 1) : 1
  return 1.0 + 0.05 * (Math.max(1, lv) - 1)
}

export function accumulateTraitModifiers(
  traitIds: readonly string[] | null | undefined,
  traitLevels: Record<string, number> | null | undefined,
  statRatios: Map<string, number>,
  points: Map<string, number>,
): void {
  if (!traitIds || traitIds.length === 0) return
  for (let i = 0; i < traitIds.length; i++) {
    const id = traitIds[i]
    const def = ALL_TRAITS.get(id)
    if (!def) continue
    const level = traitLevels ? (traitLevels[id] ?? 1) : 1
    const scale = 1.0 + 0.05 * (Math.max(1, level) - 1)
    if (def.statRatios) {
      for (const [k, v] of Object.entries(def.statRatios)) {
        const finalV = v > 0 ? v * scale : v
        statRatios.set(k, (statRatios.get(k) ?? 0) + finalV)
      }
    }
    if (def.points) {
      for (const [k, v] of Object.entries(def.points)) {
        const finalV = k === 'threatOnTakenPoints' || v > 0 ? v * scale : v
        points.set(k, (points.get(k) ?? 0) + finalV)
      }
    }
  }
}

export function resolveThreatOnTakenPoints(
  traits: readonly string[] | null | undefined,
  traitLevels: Record<string, number> | null | undefined,
): number {
  if (!containsTrait(traits, TraitIds.ThreatOnTaken)) return 0
  const level = traitLevels ? (traitLevels[TraitIds.ThreatOnTaken] ?? 1) : 1
  return THREAT_ON_TAKEN_TRAIT_POINTS * (1.0 + 0.05 * (Math.max(1, level) - 1))
}

// ---------- 开天门与灵根规则 ----------

export interface LinggenValues {
  metal: number
  wood: number
  water: number
  fire: number
  earth: number
}

export const EMPTY_LINGGEN: LinggenValues = { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 }

export function linggenEffectiveCount(l: LinggenValues): number {
  let c = 0
  if (l.metal > 0) c++
  if (l.wood > 0) c++
  if (l.water > 0) c++
  if (l.fire > 0) c++
  if (l.earth > 0) c++
  return c
}

export function linggenIsEmpty(l: LinggenValues): boolean {
  return l.metal <= 0 && l.wood <= 0 && l.water <= 0 && l.fire <= 0 && l.earth <= 0
}

export function linggenOf(l: LinggenValues, element: CombatElement): number {
  switch (element) {
    case CombatElement.Metal:
      return l.metal
    case CombatElement.Wood:
      return l.wood
    case CombatElement.Water:
      return l.water
    case CombatElement.Fire:
      return l.fire
    case CombatElement.Earth:
      return l.earth
    default:
      return 0
  }
}

export function resolveLinggenCultivationMultiplier(values: LinggenValues, entered: boolean): number {
  if (!entered) return 1
  const effective = linggenEffectiveCount(values)
  return effective <= 0 ? 1 : 1 + 0.5 * (Math.min(effective, 5) - 1)
}

export function resolveLinggenDamagePoints(pts: number): number {
  return clamp(pts, 0, 100) * 0.25
}

export function resolveFireCriticalChanceRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveFireCriticalDamageBonusPoints(pts: number): number {
  return 25.0 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveWaterMaxManapointsRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveWaterParryRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveWaterRegenRatio(pts: number): number {
  return 1.00 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveWaterSearchSpeedPoints(pts: number): number {
  return 20.0 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveWoodMaxHitpointsRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveWoodHealingBonusPoints(pts: number): number {
  return 50.0 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveMetalAttackRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveMetalBreakRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveMetalAccuracyRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveEarthDefenseRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveEarthCriticalAvoidanceRatio(pts: number): number {
  return 0.50 * (clamp(pts, 0, 100) / 100.0)
}

export function resolveEarthFixedDamageReduction(pts: number): number {
  const safe = clamp(Math.trunc(pts), 0, 100)
  return safe <= 0 ? 0 : 1.0 + safe * 0.09
}

// ---------- 通用稀有掉落与宗门节点校验 ----------

export const BROKEN_STORAGE_BAG_ITEM_ID = 'item.storage-bag.broken'

export function calculateStorageBagDropChance(level: number, rank: CombatMonsterRank): number {
  const levelFactor = Math.pow(1.05, Math.max(0, level - 1))
  const rankMult = rank === CombatMonsterRank.Elite ? 5.0 : rank === CombatMonsterRank.Boss ? 20.0 : 1.0
  return 0.0003 * levelFactor * rankMult
}

const RESIDENCE_PREFIX = 'node.zongmen.'
const HEX32 = /^[0-9a-fA-F]{32}$/

export function isZongmenResidenceNodeId(nodeId: string | null | undefined): boolean {
  if (!nodeId || !nodeId.startsWith(RESIDENCE_PREFIX)) return false
  return HEX32.test(nodeId.slice(RESIDENCE_PREFIX.length))
}
