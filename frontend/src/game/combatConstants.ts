/**
 * 太虚仙途 · 战斗核心常量与中文公式说明脚本
 * 集中管理抗性减伤系数、技能消耗倍率、对抗概率系数、六维根基增益等全局配置。
 */

// =========================================================================
// 1. 伤害与抗性减伤公式相关常量
// =========================================================================

/**
 * 抗性减伤系数 (常量配置，暂定为 5.0)
 * 
 * 【中文公式】：
 * 实际伤害 = 原始伤害 * (原始伤害 / (原始伤害 + 有效抗性 * 抗性减伤系数 + 基础常数))
 * 
 * 【设计说明】：
 * - 当有效抗性为 0 时，减伤比例为 0，造成 100% 原始伤害；
 * - 随着抗性提升，伤害呈双曲线平滑衰减，抗性越高边际减伤收益越平缓；
 * - 有效抗性 = 基础抗性 / (1 + 穿透比例)。
 */
export const RESISTANCE_FACTOR = 5.0

/**
 * 减伤公式底数常数 (默认 1.0)，防止分母为零。
 */
export const DAMAGE_BASE_FACTOR = 1.0

// =========================================================================
// 2. 技能灵力消耗计算常量
// =========================================================================

/**
 * 功法技能灵力消耗换算倍率 (暂定为 10)
 * 
 * 【中文公式】：
 * 实际灵力消耗 = 基础消耗 * 消耗换算倍率
 * 
 * 示例：
 * - 基础消耗 2 -> 实际消耗 20 灵力
 * - 基础消耗 5 -> 实际消耗 50 灵力
 */
export const MANA_COST_MULTIPLIER = 10

/**
 * 计算技能实际灵力消耗的方法
 */
export function calculateManaCost(baseCost: number, multiplier = MANA_COST_MULTIPLIER): number {
  return Math.max(0, Math.round(baseCost * multiplier))
}

// =========================================================================
// 3. 对抗概率计算公式常量 (命中vs闪避, 暴击vs免爆, 破招vs拆招)
// =========================================================================

/**
 * 对抗公式对方属性放大系数 (默认 1.0)
 */
export const CONTEST_OPPONENT_FACTOR = 1.0

/**
 * 对抗公式基础平滑常数 (默认 1.0)
 * 
 * 【中文公式】：
 * 概率 = 自身属性 / (自身属性 + 对方对抗属性 * 对抗放大系数 + 基础平滑常数)
 */
export const CONTEST_BASE_CONST = 1.0

/**
 * 通用对抗胜率计算方法
 */
export function calculateContestRate(
  selfAttr: number,
  opponentAttr: number,
  factor = CONTEST_OPPONENT_FACTOR,
  baseConst = CONTEST_BASE_CONST,
): number {
  const selfVal = Math.max(0, selfAttr)
  const opponentVal = Math.max(0, opponentAttr)
  const denom = selfVal + opponentVal * factor + baseConst
  if (denom <= 0) return 0
  return Math.max(0, Math.min(1, selfVal / denom))
}

// =========================================================================
// 4. 破招与拆招 (化解) 翻倍判定规则常量
// =========================================================================

/**
 * 破招成功后的属性放大倍率 (翻倍 = 2.0)
 * 
 * 【中文规则】：
 * 攻击者破招 > 被攻击者拆招 时计算破招概率。
 * 若破招成功：本次攻击的【命中值】与【暴击值】实际计算效果翻倍 (× 2.0)。
 */
export const BREAK_SUCCESS_EFFECT_MULTIPLIER = 2.0

/**
 * 拆招成功后的属性放大倍率 (翻倍 = 2.0)
 * 
 * 【中文规则】：
 * 被攻击者拆招 > 攻击者破招 时计算拆招概率。
 * 若拆招成功：本次承伤的【双抗(护甲/法抗)】与【闪避值】实际计算效果翻倍 (× 2.0)。
 */
export const PARRY_SUCCESS_EFFECT_MULTIPLIER = 2.0

// =========================================================================
// 5. 暴击伤害基础倍率
// =========================================================================

/**
 * 基础暴击伤害倍率 (150% = 1.5)
 * 
 * 【中文公式】：
 * 最终暴击伤害 = 未暴击伤害 * (基础暴击倍率 + 额外暴击伤害加成比例)
 */
export const BASE_CRITICAL_DAMAGE_MULTIPLIER = 1.5

// =========================================================================
// 6. 六维根基换算系数
// =========================================================================

export const ROOT_ATTRIBUTES_CONFIG = {
  stamina: {
    name: '体魄',
    pinyin: 'Tǐ Pò',
    desc: '强筋壮骨，筑就修仙宝体之基石。',
    benefitsText: ['生命值 +1%', '护甲 +1%', '生命值回复 +1%'],
    hpRatioPerLevel: 0.01,
    armorRatioPerLevel: 0.01,
    hpRegenRatioPerLevel: 0.01,
  },
  meridians: {
    name: '经脉',
    pinyin: 'Jīng Mài',
    desc: '气海通达周天，温养灵力纳涌与法御屏障。',
    benefitsText: ['灵力值 +1%', '法抗 +1%', '灵力值回复 +1%'],
    mpRatioPerLevel: 0.01,
    magicResRatioPerLevel: 0.01,
    mpRegenRatioPerLevel: 0.01,
  },
  bone: {
    name: '根骨',
    pinyin: 'Gēn Gǔ',
    desc: '天生仙骨资质，交泰阴阳化解天地诸力。',
    benefitsText: ['生命值 +1%', '灵力值 +1%', '化解 +1%'],
    hpRatioPerLevel: 0.01,
    mpRatioPerLevel: 0.01,
    parryRatioPerLevel: 0.01,
  },
  strength: {
    name: '力道',
    pinyin: 'Lì Dào',
    desc: '力拔山河破岳，以纯粹杀力洞穿敌手破绽。',
    benefitsText: ['物理攻击 +1%', '暴击 +1%', '破招 +1%'],
    physAtkRatioPerLevel: 0.01,
    critRatioPerLevel: 0.01,
    breakRatioPerLevel: 0.01,
  },
  consciousness: {
    name: '神识',
    pinyin: 'Shén Shí',
    desc: '灵台清明通幽，神念锁定敌手并极度催发术法杀威。',
    benefitsText: ['术法攻击 +2% (×2)', '命中 +1%'],
    spellAtkRatioPerLevel: 0.02,
    hitRatioPerLevel: 0.01,
  },
  agility: {
    name: '身法',
    pinyin: 'Shēn Fǎ',
    desc: '步踏罡斗凌波，规避致命杀机并从容化解攻势。',
    benefitsText: ['闪避 +1%', '免爆 +1%', '化解 +1%'],
    evasionRatioPerLevel: 0.01,
    critAvoidRatioPerLevel: 0.01,
    parryRatioPerLevel: 0.01,
  },
} as const

/**
 * 默认初始六维点数 (全 10 点/重)
 */
export const DEFAULT_ROOT_ATTRIBUTE_POINTS = 10.0

/**
 * 普通攻击 (Basic Attack) 规则常数
 * 行动速度默认为 0，不使用技能时普通攻击造成 100% 物理伤害，CD 为 0，引导为 1
 */
export const BASIC_ATTACK_CONFIG = {
  damageRatio: 1.0, // 100% 物理伤害
  cooldownMilliseconds: 0.0, // CD 0s
  castMilliseconds: 1000.0, // 引导 1.0s
  manaCost: 0.0, // 消耗 0
  defaultActionSpeedPoints: 0.0, // 行动速度 0 点 (0.0%)
} as const

// =========================================================================
// 7. 统一基准数值规范 (Baseline Attributes, 全量 double)
// =========================================================================

export const BASELINE_COMBAT_ATTRIBUTES = {
  maxHp: 100.0,
  maxMp: 50.0,
  physAtk: 10.0,
  spellAtk: 5.0,
  armor: 10.0,
  magicRes: 0.0,
  accuracy: 1.0,
  evasion: 1.0,
  criticalRating: 1.0,
  critAvoidance: 1.0,
  breakPower: 1.0,
  parryResolve: 1.0,
  hpRegen: 2.0, // 每五秒恢复 2.0 生命值
  mpRegen: 1.0, // 每五秒恢复 1.0 灵力值
  percentPoints: 0.0,
} as const

/**
 * 六维累计经验到等级换算方法（经验累加不清空）。
 * 每级累计所需经验 = 100 * (level ^ 3)。
 */
export function calculateRootLevelAndProgress(totalCumulativeExp: number): {
  level: number
  currentProgress: number
  levelRequirement: number
} {
  const exp = Math.max(0.0, totalCumulativeExp)
  let level = 1.0
  while (getCumulativeExpForLevel(level + 1.0) <= exp) {
    level += 1.0
  }

  const currentLevelBaseExp = getCumulativeExpForLevel(level)
  const nextLevelBaseExp = getCumulativeExpForLevel(level + 1.0)
  const currentProgress = exp - currentLevelBaseExp
  const levelRequirement = nextLevelBaseExp - currentLevelBaseExp

  return {
    level,
    currentProgress,
    levelRequirement,
  }
}

export function getCumulativeExpForLevel(level: number): number {
  if (level <= 1.0) return 0.0
  const l = level - 1.0
  return 100.0 * Math.pow(l, 3.0)
}

/**
 * 非玩家单位品阶层级量化定义
 */
export const NON_PLAYER_UNIT_TIERS = {
  normal: {
    name: '普通',
    allStatsMultiplier: 1.0,
    hpMultiplier: 1.0,
  },
  elite: {
    name: '精英',
    allStatsMultiplier: 1.2,
    hpMultiplier: 3.0,
  },
  boss: {
    name: '妖王',
    allStatsMultiplier: 1.5,
    hpMultiplier: 10.0,
  },
} as const

// =========================================================================
// 8. 统一百分比数值类型 (PercentValue)
// =========================================================================

export type PercentCurveMode = 'linear' | 'non-linear'

/**
 * 统一百分比数值类型。
 * 内部存储与计算均使用 double 类型的固定点数 (points，基准 100 点 = 100%)。
 * 基础战斗数值使用固定值类型 (double)，特殊战斗数值使用 PercentValue。
 */
export class PercentValue {
  readonly points: number
  readonly mode: PercentCurveMode

  constructor(points: number, mode: PercentCurveMode = 'linear') {
    this.points = points
    this.mode = mode
  }

  public static linear(points: number): PercentValue {
    return new PercentValue(points, 'linear')
  }

  public static nonLinear(points: number): PercentValue {
    return new PercentValue(points, 'non-linear')
  }

  /**
   * 参与战斗公式乘法计算的实际倍率 (Multiplier)
   */
  get multiplier(): number {
    if (this.mode === 'linear') {
      return this.points >= 0
        ? 1.0 + this.points / 100.0
        : 100.0 / (100.0 + Math.abs(this.points))
    }
    // non-linear (速率倒数缩减)
    return this.points >= 0
      ? 100.0 / (100.0 + this.points)
      : 1.0 + Math.abs(this.points) / 100.0
  }

  /**
   * 面板或 UI 展示的实际百分比值 (DisplayPercentage)
   */
  get displayPercentage(): number {
    if (this.mode === 'linear') {
      return this.points >= 0
        ? this.points
        : -(Math.abs(this.points) / (100.0 + Math.abs(this.points))) * 100.0
    }
    // non-linear
    return this.points >= 0
      ? (this.points / (100.0 + this.points)) * 100.0
      : -Math.abs(this.points)
  }

  /**
   * 格式化为百分比文本字符串，如 "+100.0%"、"-50.0%"、"50.0%"
   */
  format(decimals = 1): string {
    const val = this.displayPercentage
    const prefix = this.mode === 'linear' && val > 0 ? '+' : ''
    return `${prefix}${val.toFixed(decimals)}%`
  }
}

// =========================================================================
// 8. 境界计算与位阶信息结构 (大境界 · 位阶 · 小境界)
// =========================================================================

export interface RealmInfo {
  totalLevel: number
  layerProgress: number
  stageName: string
  phaseName: string
  minorName: string
  subStage: string
  rankChar: string
  stageColor: string
  fullDisplayName: string
}

/**
 * 根据总修为值计算当前的 大境界、位阶、小境界 及专属棱形徽章信息
 */
export function calculateRealmInfo(spirit = 0): RealmInfo {
  const totalLevel = 1.0 + Math.floor(spirit / 100.0)
  const layerProgress = spirit % 100.0

  const mortalStageNames = [
    '凡胎', '炼皮', '锻骨', '易筋', '洗髓', '养气',
    '通脉', '瑶光', '开阳', '玉衡', '天权', '天玑',
    '天璇', '天枢', '宗师', '大宗师', '临渊', '叩仙门'
  ]

  let stageName = '凡俗'
  let phaseName = '凡武'
  let minorName = '凡胎'
  let rankChar = '武'
  let stageColor = '#9ca3af'

  if (totalLevel <= 18.0) {
    stageName = '凡俗'
    const mortalIndex = Math.max(0, Math.min(17, Math.floor(totalLevel) - 1))
    minorName = mortalStageNames[mortalIndex]

    if (totalLevel <= 6.0) {
      phaseName = '凡武'
      rankChar = '武'
      stageColor = '#9ca3af'
    } else if (totalLevel <= 12.0) {
      phaseName = '通玄'
      rankChar = '玄'
      stageColor = '#4ade80'
    } else {
      phaseName = '天人'
      rankChar = '天'
      stageColor = '#38bdf8'
    }
  } else {
    const cultivationLevel = totalLevel - 18.0
    const majorIndex = Math.floor((cultivationLevel - 1.0) / 12.0)
    const layerInMajor = Math.floor(((cultivationLevel - 1.0) % 12.0) + 1.0)

    const majorNames = ['练气', '筑基', '金丹', '元婴', '化神', '炼虚', '合体', '大乘', '渡劫']
    const majorColors = ['var(--gold)', '#38bdf8', '#f59e0b', '#c084fc', '#e879f9', '#f43f5e', '#fb7185', '#fb923c', '#facc15']

    const rawMajorName = majorNames[majorIndex] || '飞升'
    stageName = rawMajorName
    stageColor = majorColors[majorIndex] || '#facc15'

    const nextMajorName = majorNames[majorIndex + 1] || '飞升'
    if (layerInMajor <= 9) {
      const chineseDigits = ['', '一', '二', '三', '四', '五', '六', '七', '八', '九']
      minorName = `${rawMajorName}${chineseDigits[layerInMajor]}层`
    } else if (layerInMajor === 10) {
      minorName = `${rawMajorName}巅峰`
    } else if (layerInMajor === 11) {
      minorName = `${rawMajorName}大圆满`
    } else {
      minorName = `半步${nextMajorName}`
    }

    if (layerInMajor <= 3) {
      phaseName = '初'
      rankChar = '初'
    } else if (layerInMajor <= 6) {
      phaseName = '中'
      rankChar = '中'
    } else if (layerInMajor <= 9) {
      phaseName = '后'
      rankChar = '后'
    } else {
      phaseName = '极'
      rankChar = '极'
    }
  }

  const subStage = `${phaseName} ${minorName}`
  const fullDisplayName = `${stageName} [${rankChar}] ${minorName}`

  return {
    totalLevel,
    layerProgress,
    stageName,
    phaseName,
    minorName,
    subStage,
    rankChar,
    stageColor,
    fullDisplayName,
  }
}

export interface BaseAttributePool {
  level: number
  tier: number
  baseHp: number
  baseMp: number
  baseAd: number
  baseAp: number
  baseArmor: number
  baseMagicRes: number
  baseAccuracy: number
  baseEvasion: number
  baseCrit: number
  baseCritAvoid: number
  baseBreak: number
  baseParry: number
  cdr: number
  atr: number
  critDmg: number
  skillIntensity: number
  k: number
}

const BASE_FLAT_ANCHORS = [
  { level: 1,   hp: 100,   mp: 50,    ad: 10,   ap: 5,    armor: 10,  mr: 0,    acc: 1,   eva: 1,   crit: 1,   cavoid: 1,  breakPt: 1,   parry: 1 },
  { level: 18,  hp: 232,   mp: 90,    ad: 15,   ap: 7,    armor: 13,  mr: 0,    acc: 3,   eva: 1,   crit: 1,   cavoid: 1,  breakPt: 1,   parry: 1 },
  { level: 30,  hp: 472,   mp: 570,   ad: 30,   ap: 27,   armor: 23,  mr: 14,   acc: 7,   eva: 4,   crit: 3,   cavoid: 2,  breakPt: 2,   parry: 2 },
  { level: 42,  hp: 1108,  mp: 1402,  ad: 62,   ap: 67,   armor: 46,  mr: 41,   acc: 12,  eva: 8,   crit: 6,   cavoid: 4,  breakPt: 4,   parry: 4 },
  { level: 54,  hp: 2164,  mp: 2770,  ad: 116,  ap: 133,  armor: 83,  mr: 85,   acc: 19,  eva: 15,  crit: 13,  cavoid: 8,  breakPt: 7,   parry: 7 },
  { level: 66,  hp: 4624,  mp: 4234,  ad: 196,  ap: 232,  armor: 138, mr: 152,  acc: 28,  eva: 24,  crit: 22,  cavoid: 14, breakPt: 13,  parry: 12 },
  { level: 78,  hp: 8056,  mp: 6282,  ad: 308,  ap: 369,  armor: 214, mr: 246,  acc: 39,  eva: 35,  crit: 33,  cavoid: 25, breakPt: 23,  parry: 20 },
  { level: 90,  hp: 11644, mp: 8586,  ad: 430,  ap: 517,  armor: 297, mr: 347,  acc: 50,  eva: 46,  crit: 44,  cavoid: 36, breakPt: 34,  parry: 30 },
  { level: 102, hp: 16360, mp: 11618, ad: 590,  ap: 712,  armor: 406, mr: 480,  acc: 65,  eva: 61,  crit: 59,  cavoid: 50, breakPt: 48,  parry: 44 },
  { level: 114, hp: 22372, mp: 15474, ad: 794,  ap: 961,  armor: 545, mr: 650,  acc: 84,  eva: 79,  crit: 77,  cavoid: 68, breakPt: 66,  parry: 62 },
  { level: 126, hp: 29824, mp: 20258, ad: 1047, ap: 1270, armor: 717, mr: 860,  acc: 107, eva: 102, crit: 100, cavoid: 91, breakPt: 89,  parry: 84 },
  { level: 127, hp: 38872, mp: 26066, ad: 1354, ap: 1645, armor: 927, mr: 1116, acc: 135, eva: 130, crit: 128, cavoid: 119,breakPt: 116, parry: 111 },
]

export function calculateBaseAttributes(spirit = 0): BaseAttributePool {
  const level = Math.max(1.0, 1.0 + Math.floor(spirit / 100.0))
  let tier = 1
  if (level <= 18.0) tier = level <= 1.0 ? 1 : 2
  else if (level <= 30.0) tier = 3
  else if (level <= 42.0) tier = 4
  else if (level <= 54.0) tier = 5
  else if (level <= 66.0) tier = 6
  else if (level <= 78.0) tier = 7
  else if (level <= 90.0) tier = 8
  else if (level <= 102.0) tier = 9
  else if (level <= 114.0) tier = 10
  else if (level <= 126.0) tier = 11
  else tier = 12

  let lowIndex = 0
  for (let i = 0; i < BASE_FLAT_ANCHORS.length - 1; i++) {
    if (level >= BASE_FLAT_ANCHORS[i].level && level <= BASE_FLAT_ANCHORS[i + 1].level) {
      lowIndex = i
      break
    }
    if (level > BASE_FLAT_ANCHORS[i + 1].level) {
      lowIndex = i + 1
    }
  }

  const low = BASE_FLAT_ANCHORS[lowIndex]
  const high = lowIndex < BASE_FLAT_ANCHORS.length - 1 ? BASE_FLAT_ANCHORS[lowIndex + 1] : low
  const span = Math.max(1e-6, high.level - low.level)
  const t = lowIndex < BASE_FLAT_ANCHORS.length - 1 ? Math.min(1.0, Math.max(0.0, (level - low.level) / span)) : 0.0

  const baseHp = low.hp + (high.hp - low.hp) * t
  const baseMp = low.mp + (high.mp - low.mp) * t
  const baseAd = low.ad + (high.ad - low.ad) * t
  const baseAp = low.ap + (high.ap - low.ap) * t
  const baseArmor = low.armor + (high.armor - low.armor) * t
  const baseMagicRes = low.mr + (high.mr - low.mr) * t
  const baseAccuracy = low.acc + (high.acc - low.acc) * t
  const baseEvasion = low.eva + (high.eva - low.eva) * t
  const baseCrit = low.crit + (high.crit - low.crit) * t
  const baseCritAvoid = low.cavoid + (high.cavoid - low.cavoid) * t
  const baseBreak = low.breakPt + (high.breakPt - low.breakPt) * t
  const baseParry = low.parry + (high.parry - low.parry) * t

  const x = Math.min(1.0, Math.max(0.0, (level - 1.0) / 126.0))
  const progress = x * x
  const cdr = progress * 0.75
  const atr = progress * 0.50
  const critDmg = 1.50 + progress * 3.50
  const skillIntensity = 1.0 + (tier - 1) * 0.30
  const k = tier

  return {
    level,
    tier,
    baseHp,
    baseMp,
    baseAd,
    baseAp,
    baseArmor,
    baseMagicRes,
    baseAccuracy,
    baseEvasion,
    baseCrit,
    baseCritAvoid,
    baseBreak,
    baseParry,
    cdr,
    atr,
    critDmg,
    skillIntensity,
    k,
  }
}


