/**
 * 道化太初 · 战斗核心常量与中文公式说明脚本
 * 集中管理抗性减伤系数、技能消耗倍率、对抗概率系数、六维根基增益等全局配置。
 */

// =========================================================================
// 1. 伤害与抗性减伤公式相关常量
// =========================================================================

import { getT } from '../i18n'

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
 * @param baseCost 技能基础灵力消耗点数
 * @param multiplier 消耗换算倍率（默认为 MANA_COST_MULTIPLIER 即 10）
 * @returns 实际消耗的灵力数值（保底为 0）
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
 * 对抗公式基础平滑常数 (默认 100.0)
 * 
 * 【中文公式说明】：
 * 1. 通用对抗成功概率 = 自身属性 / (自身属性 + 对方对抗属性 * 对抗放大系数 + 基础平滑常数 100.0)
 * 2. 闪避概率 = 守方闪避 / (守方闪避 + 攻方命中 * 对抗放大系数 + 100.0)
 * 3. 命中概率 = 1.0 - 闪避概率 = (攻方命中 * 对抗放大系数 + 100.0) / (守方闪避 + 攻方命中 * 对抗放大系数 + 100.0)
 * 4. 暴击概率 = 攻方暴击 / (攻方暴击 + 守方免暴 * 对抗放大系数 + 100.0)
 */
export const CONTEST_BASE_CONST = 100.0

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

/**
 * 闪避概率计算 (闪避 vs 命中)
 * 闪避率 = 守方闪避 / (守方闪避 + 攻方命中 * 系数 + 100.0)
 * 0闪避0命中时闪避率为0% (即100%命中)
 */
export function calculateDodgeContestRate(
  evasion: number,
  hit: number,
  factor = CONTEST_OPPONENT_FACTOR,
  baseConst = CONTEST_BASE_CONST,
): number {
  return calculateContestRate(evasion, hit, factor, baseConst)
}

/**
 * 命中概率计算 (命中 vs 闪避)
 * 命中率 = 1.0 - 闪避率
 */
export function calculateHitContestRate(
  hit: number,
  evasion: number,
  factor = CONTEST_OPPONENT_FACTOR,
  baseConst = CONTEST_BASE_CONST,
): number {
  return 1.0 - calculateDodgeContestRate(evasion, hit, factor, baseConst)
}

/**
 * 暴击概率计算 (暴击 vs 免爆)
 * 暴击率 = 攻方暴击 / (攻方暴击 + 守方免暴 * 系数 + 100.0)
 */
export function calculateCritContestRate(
  crit: number,
  critAvoid: number,
  factor = CONTEST_OPPONENT_FACTOR,
  baseConst = CONTEST_BASE_CONST,
): number {
  return calculateContestRate(crit, critAvoid, factor, baseConst)
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

// 六维的名称、拼音、描述与增益文案属展示文本，统一走 i18n（insight.root.*），此处仅保留数值系数。
export const ROOT_ATTRIBUTES_CONFIG = {
  stamina: {
    hpRatioPerLevel: 0.01,
    armorRatioPerLevel: 0.01,
    hpRegenRatioPerLevel: 0.01,
  },
  meridians: {
    mpRatioPerLevel: 0.01,
    magicResRatioPerLevel: 0.01,
    mpRegenRatioPerLevel: 0.01,
  },
  bone: {
    hpRatioPerLevel: 0.01,
    mpRatioPerLevel: 0.01,
    parryRatioPerLevel: 0.01,
  },
  strength: {
    physAtkRatioPerLevel: 0.01,
    critRatioPerLevel: 0.01,
    breakRatioPerLevel: 0.01,
  },
  consciousness: {
    spellAtkRatioPerLevel: 0.02,
    hitRatioPerLevel: 0.01,
  },
  agility: {
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

/**
 * 角色 1 级时的基础基准战斗属性（全量 double 浮点数设计）
 */
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
 * @param totalCumulativeExp 累计获得的总经验值
 * @returns 包含当前等级、本级当前进度经验、升至下一级所需经验的对象
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

/**
 * 获取升到指定六维等级所需的累计经验总和
 * @param level 目标等级
 */
export function getCumulativeExpForLevel(level: number): number {
  if (level <= 1.0) return 0.0
  const l = level - 1.0
  return 100.0 * Math.pow(l, 3.0)
}

/**
 * 非玩家单位品阶层级量化定义（普通、精英、首领对应的全属性与生命倍率）
 */
// 单位品阶显示名走 i18n（game.unitTier.*），此处仅保留数值倍率。
export const NON_PLAYER_UNIT_TIERS = {
  normal: {
    allStatsMultiplier: 1.0,
    hpMultiplier: 1.0,
  },
  elite: {
    allStatsMultiplier: 1.2,
    hpMultiplier: 3.0,
  },
  boss: {
    allStatsMultiplier: 1.5,
    hpMultiplier: 10.0,
  },
} as const

// =========================================================================
// 8. 统一百分比数值类型 (PercentValue)
// =========================================================================

/**
 * 百分比曲线计算模式：
 * - 'linear': 线性缩放（如灵气收益、伤害加成等）
 * - 'non-linear': 速率倒数缩减（用于攻速、冷却缩减等避免无限递增的属性）
 */
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

  /**
   * 创建线性百分比数值
   * @param points 百分比点数（100 点 = 100%）
   */
  public static linear(points: number): PercentValue {
    return new PercentValue(points, 'linear')
  }

  /**
   * 创建非线性（倒数递减）百分比数值
   * @param points 百分比点数
   */
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
   * 格式化为百分比文本字符串，如 "+100.0%"、"-50.0%"、"50.0%"，向下取整
   */
  format(decimals = 1): string {
    const val = this.displayPercentage
    const factor = Math.pow(10, decimals)
    const floored = Math.floor(val * factor + 1e-9) / factor
    const prefix = this.mode === 'linear' && floored > 0 ? '+' : ''
    return `${prefix}${floored.toFixed(decimals)}%`
  }
}

// =========================================================================
// 8. 境界计算与位阶信息结构 (大境界 · 位阶 · 小境界)
// =========================================================================

export const MAX_REALM_LEVEL = 127

export const REALM_LEVEL_EXP_TO_NEXT: readonly number[] = [
  10000, // Lv.1 凡胎
  12000, // Lv.2 炼皮
  15000, // Lv.3 锻骨
  20000, // Lv.4 易筋
  25000, // Lv.5 洗髓
  30000, // Lv.6 养气
  50000, // Lv.7 通脉
  60000, // Lv.8 瑶光
  70000, // Lv.9 开阳
  90000, // Lv.10 玉衡
  110000, // Lv.11 天权
  130000, // Lv.12 天玑
  160000, // Lv.13 天璇
  250000, // Lv.14 天枢
  300000, // Lv.15 宗师
  400000, // Lv.16 大宗师
  500000, // Lv.17 临渊
  800000, // Lv.18 叩仙门
  1263000, // Lv.19 练气一层
  1516000, // Lv.20 练气二层
  1895000, // Lv.21 练气三层
  2274000, // Lv.22 练气四层
  2779000, // Lv.23 练气五层
  3789000, // Lv.24 练气六层
  4547000, // Lv.25 练气七层
  5432000, // Lv.26 练气八层
  6568000, // Lv.27 练气九层
  7832000, // Lv.28 练气巅峰
  9474000, // Lv.29 练气大圆满
  12631000, // Lv.30 半步筑基
  15158000, // Lv.31 筑基一层
  18316000, // Lv.32 筑基二层
  21979000, // Lv.33 筑基三层
  26526000, // Lv.34 筑基四层
  31579000, // Lv.35 筑基五层
  44211000, // Lv.36 筑基六层
  53053000, // Lv.37 筑基七层
  63158000, // Lv.38 筑基八层
  75789000, // Lv.39 筑基九层
  90947000, // Lv.40 筑基巅峰
  108632000, // Lv.41 筑基大圆满
  151578000, // Lv.42 半步金丹
  176842000, // Lv.43 金丹一层
  214737000, // Lv.44 金丹二层
  252632000, // Lv.45 金丹三层
  303158000, // Lv.46 金丹四层
  366316000, // Lv.47 金丹五层
  505263000, // Lv.48 金丹六层
  606316000, // Lv.49 金丹七层
  732632000, // Lv.50 金丹八层
  884211000, // Lv.51 金丹九层
  1061053000, // Lv.52 金丹巅峰
  1263158000, // Lv.53 金丹大圆满
  1768419000, // Lv.54 半步元婴
  2147368000, // Lv.55 元婴一层
  2526316000, // Lv.56 元婴二层
  3031579000, // Lv.57 元婴三层
  3663158000, // Lv.58 元婴四层
  4421053000, // Lv.59 元婴五层
  6315789000, // Lv.60 元婴六层
  7578947000, // Lv.61 元婴七层
  9094737000, // Lv.62 元婴八层
  10863158000, // Lv.63 元婴九层
  13010526000, // Lv.64 元婴巅峰
  15663158000, // Lv.65 元婴大圆满
  22736843000, // Lv.66 半步化神
  27789474000, // Lv.67 化神一层
  32842105000, // Lv.68 化神二层
  39157895000, // Lv.69 化神三层
  46736842000, // Lv.70 化神四层
  56842105000, // Lv.71 化神五层
  82105263000, // Lv.72 化神六层
  101052632000, // Lv.73 化神七层
  109136842000, // Lv.74 化神八层
  117852632000, // Lv.75 化神九层
  127326316000, // Lv.76 化神巅峰
  137557895000, // Lv.77 化神大圆满
  148547367000, // Lv.78 半步炼虚
  160421053000, // Lv.79 炼虚一层
  173305263000, // Lv.80 炼虚二层
  187200000000, // Lv.81 炼虚三层
  202231579000, // Lv.82 炼虚四层
  218400000000, // Lv.83 炼虚五层
  235831579000, // Lv.84 炼虚六层
  254652632000, // Lv.85 炼虚七层
  274989474000, // Lv.86 炼虚八层
  296968421000, // Lv.87 炼虚九层
  320715789000, // Lv.88 炼虚巅峰
  346357895000, // Lv.89 炼虚大圆满
  374021052000, // Lv.90 半步合体
  403957895000, // Lv.91 合体一层
  436294737000, // Lv.92 合体二层
  471157895000, // Lv.93 合体三层
  508800000000, // Lv.94 合体四层
  549473684000, // Lv.95 合体五层
  593431579000, // Lv.96 合体六层
  640926316000, // Lv.97 合体七层
  692210526000, // Lv.98 合体八层
  747536842000, // Lv.99 合体九层
  807284211000, // Lv.100 合体巅峰
  871831579000, // Lv.101 合体大圆满
  941557894000, // Lv.102 半步大乘
  1016842105000, // Lv.103 大乘一层
  1098189474000, // Lv.104 大乘二层
  1186105263000, // Lv.105 大乘三层
  1280968421000, // Lv.106 大乘四层
  1383410526000, // Lv.107 大乘五层
  1494063158000, // Lv.108 大乘六层
  1613557895000, // Lv.109 大乘七层
  1742652632000, // Lv.110 大乘八层
  1882105263000, // Lv.111 大乘九层
  2032673684000, // Lv.112 大乘巅峰
  2195242105000, // Lv.113 大乘大圆满
  2370821053000, // Lv.114 半步渡劫
  2560547368000, // Lv.115 渡劫一层
  2765431579000, // Lv.116 渡劫二层
  2986610526000, // Lv.117 渡劫三层
  3225600000000, // Lv.118 渡劫四层
  3483663158000, // Lv.119 渡劫五层
  3762315789000, // Lv.120 渡劫六层
  4063326316000, // Lv.121 渡劫七层
  4388336842000, // Lv.122 渡劫八层
  4739368421000, // Lv.123 渡劫九层
  5118568421000, // Lv.124 渡劫巅峰
  5528084211000, // Lv.125 渡劫大圆满
  5970315790000, // Lv.126 半步飞升
  0, // Lv.127 飞升
]

/**
 * 境界等级累计所需修为查表缓存数组（索引即境界等级）
 */
export const REALM_LEVEL_CUMULATIVE_EXP: readonly number[] = (() => {
  const cum = new Array<number>(MAX_REALM_LEVEL + 1)
  cum[0] = 0
  cum[1] = 0
  for (let i = 1; i < MAX_REALM_LEVEL; i++) {
    cum[i + 1] = cum[i] + REALM_LEVEL_EXP_TO_NEXT[i - 1]
  }
  return cum
})()

/**
 * 根据总修为或当前层修为+显式境界等级计算境界进度与当前等级
 * @param totalSpirit 角色累计获得的总修为（或当前层获得的修为）
 * @param explicitRealmLevel 可选的服务端权威境界等级 (1..127)
 * @returns 包含当前等级、当前重进度、下一重所需修为、百分比与带小数精度的总等级
 */
export function calculateRealmProgress(totalSpirit = 0, explicitRealmLevel?: number): {
  level: number
  currentLayerExp: number
  expToNext: number
  progressPercentage: number
  totalLevel: number
} {
  const spirit = Math.max(0, totalSpirit)
  let level = 1

  if (typeof explicitRealmLevel === 'number' && Number.isFinite(explicitRealmLevel) && explicitRealmLevel >= 1) {
    level = Math.min(MAX_REALM_LEVEL, Math.floor(explicitRealmLevel))
    const expToNext = level < MAX_REALM_LEVEL ? REALM_LEVEL_EXP_TO_NEXT[level - 1] : 0
    const currentLayerExp = expToNext > 0 ? Math.min(expToNext, spirit) : 0

    if (expToNext <= 0 || level >= MAX_REALM_LEVEL) {
      return {
        level: MAX_REALM_LEVEL,
        currentLayerExp: 0,
        expToNext: 0,
        progressPercentage: 100,
        totalLevel: MAX_REALM_LEVEL,
      }
    }

    const progressPercentage = Math.max(0, Math.min(100, (currentLayerExp / expToNext) * 100.0))
    const totalLevel = level + Math.max(0, Math.min(1, currentLayerExp / expToNext))

    return {
      level,
      currentLayerExp,
      expToNext,
      progressPercentage,
      totalLevel,
    }
  }

  while (level < MAX_REALM_LEVEL && spirit >= REALM_LEVEL_CUMULATIVE_EXP[level + 1]) {
    level++
  }

  const baseExp = REALM_LEVEL_CUMULATIVE_EXP[level]
  const expToNext = level < MAX_REALM_LEVEL ? REALM_LEVEL_EXP_TO_NEXT[level - 1] : 0
  const currentLayerExp = spirit - baseExp

  if (expToNext <= 0 || level >= MAX_REALM_LEVEL) {
    return {
      level: MAX_REALM_LEVEL,
      currentLayerExp: 0,
      expToNext: 0,
      progressPercentage: 100,
      totalLevel: MAX_REALM_LEVEL,
    }
  }

  const progressPercentage = Math.max(0, Math.min(100, (currentLayerExp / expToNext) * 100.0))
  const totalLevel = level + Math.max(0, Math.min(1, currentLayerExp / expToNext))

  return {
    level,
    currentLayerExp,
    expToNext,
    progressPercentage,
    totalLevel,
  }
}

/**
 * 境界展示信息结构体（用于 UI 渲染境界文本、颜色与徽章字形）
 */
export interface RealmInfo {
  /** 境界精确总等级 (带小数位，如 19.3) */
  totalLevel: number
  /** 当前层进度百分比 (0 - 100) */
  layerProgress: number
  /** 大境界名称 (如 "练气期", "筑基期") */
  stageName: string
  /** 位阶名称 (如 "前期", "中期", "后期", "巅峰") */
  phaseName: string
  /** 小境界/层数名称 (如 "练气一层", "筑基圆满") */
  minorName: string
  /** 子阶段展示文案 (如 "练气期 · 练气一层") */
  subStage: string
  /** 徽章展示字形单字 (如 "初", "中", "后", "极", "武", "玄", "天") */
  rankChar: string
  /** 大境界专属主题色 HEX/CSS 变量 */
  stageColor: string
  /** 完整境界展示名称 */
  fullDisplayName: string
}

/**
 * 根据总修为值与显式境界等级计算当前的 大境界、位阶、小境界 及专属棱形徽章信息
 * @param spirit 角色累计修为（或当前层修为）
 * @param explicitRealmLevel 可选的服务端权威境界等级
 * @returns 境界展示数据
 */
export function calculateRealmInfo(spirit = 0, explicitRealmLevel?: number): RealmInfo {
  const progress = calculateRealmProgress(spirit, explicitRealmLevel)
  const totalLevel = progress.totalLevel
  const layerProgress = progress.progressPercentage
  // 境界名属展示文本，统一走 i18n；rankChar 为徽章单字字形，双语共用。
  const t = getT()

  let stageName = t('game.realm.stageFallback')
  let phaseName = t('game.realm.phaseMortal1')
  let minorName = t('game.realm.minorFallback')
  let rankChar = '武'
  let stageColor = '#9ca3af'

  if (totalLevel <= 18.0) {
    stageName = t('game.realm.stageFallback')
    const mortalIndex = Math.max(1, Math.min(18, Math.floor(totalLevel)))
    minorName = t(`game.realm.mortal.${mortalIndex}`)

    if (totalLevel <= 6.0) {
      phaseName = t('game.realm.phaseMortal1')
      rankChar = '武'
      stageColor = '#9ca3af'
    } else if (totalLevel <= 12.0) {
      phaseName = t('game.realm.phaseMortal2')
      rankChar = '玄'
      stageColor = '#4ade80'
    } else {
      phaseName = t('game.realm.phaseMortal3')
      rankChar = '天'
      stageColor = '#38bdf8'
    }
  } else {
    const cultivationLevel = totalLevel - 18.0
    const majorIndex = Math.floor((cultivationLevel - 1.0) / 12.0) + 1.0
    const layerInMajor = Math.floor(((cultivationLevel - 1.0) % 12.0) + 1.0)

    const majorCount = 9
    const majorColors = ['var(--gold)', '#38bdf8', '#f59e0b', '#c084fc', '#e879f9', '#f43f5e', '#fb7185', '#fb923c', '#facc15']

    const rawMajorName = majorIndex >= 1 && majorIndex <= majorCount
      ? t(`game.realm.major.${majorIndex}`)
      : t('game.realm.ascension')
    stageName = rawMajorName
    stageColor = majorIndex >= 1 && majorIndex <= majorCount ? majorColors[majorIndex - 1] : '#facc15'

    const nextMajorName = majorIndex + 1 >= 1 && majorIndex + 1 <= majorCount
      ? t(`game.realm.major.${majorIndex + 1}`)
      : t('game.realm.ascension')
    if (layerInMajor <= 9) {
      minorName = t('game.realm.layer', {
        major: rawMajorName,
        digit: t(`game.realm.digit.${layerInMajor}`),
        n: layerInMajor,
      })
    } else if (layerInMajor === 10) {
      minorName = t('game.realm.peak', { major: rawMajorName })
    } else if (layerInMajor === 11) {
      minorName = t('game.realm.greatPerfection', { major: rawMajorName })
    } else {
      minorName = t('game.realm.halfStep', { next: nextMajorName })
    }

    if (layerInMajor <= 3) {
      phaseName = t('game.realm.phaseEarly')
      rankChar = '初'
    } else if (layerInMajor <= 6) {
      phaseName = t('game.realm.phaseMiddle')
      rankChar = '中'
    } else if (layerInMajor <= 9) {
      phaseName = t('game.realm.phaseLate')
      rankChar = '后'
    } else {
      phaseName = t('game.realm.phasePeak')
      rankChar = '极'
    }
  }

  const subStage = t('game.realm.subStage', { phase: phaseName, minor: minorName })
  const fullDisplayName = t('game.realm.fullDisplay', { stage: stageName, rank: rankChar, minor: minorName })

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

/**
 * 境界基础属性池结构（包含通过境界等级线性插值得到的所有攻防恢复基础值）
 */
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
  baseHpRegen: number
  baseMpRegen: number
}

/**
 * 境界各关键等级锚点基础数值表（用于不同境界等级区间的线性插值计算）
 */
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

/**
 * 根据角色累计修为与显式境界等级计算基础战斗属性池（通过分段线性插值）
 * @param spirit 累计修为（或当前层修为）
 * @param explicitRealmLevel 可选的服务端权威境界等级
 * @returns 角色当前境界对应的基础属性池
 */
export function calculateBaseAttributes(spirit = 0, explicitRealmLevel?: number): BaseAttributePool {
  const progress = calculateRealmProgress(spirit, explicitRealmLevel)
  const level = Math.max(1.0, progress.totalLevel)
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

  const baseHp = Math.floor(low.hp + (high.hp - low.hp) * t)
  const baseMp = Math.floor(low.mp + (high.mp - low.mp) * t)
  const baseAd = Math.floor(low.ad + (high.ad - low.ad) * t)
  const baseAp = Math.floor(low.ap + (high.ap - low.ap) * t)
  const baseArmor = Math.floor(low.armor + (high.armor - low.armor) * t)
  const baseMagicRes = Math.floor(low.mr + (high.mr - low.mr) * t)
  const baseAccuracy = Math.floor(low.acc + (high.acc - low.acc) * t)
  const baseEvasion = Math.floor(low.eva + (high.eva - low.eva) * t)
  const baseCrit = Math.floor(low.crit + (high.crit - low.crit) * t)
  const baseCritAvoid = Math.floor(low.cavoid + (high.cavoid - low.cavoid) * t)
  const baseBreak = Math.floor(low.breakPt + (high.breakPt - low.breakPt) * t)
  const baseParry = Math.floor(low.parry + (high.parry - low.parry) * t)

  // 基础生命回复与灵力回复固定为每 5 秒 2% 基础上限，向下取整（保底 1.0）
  const baseHpRegen = Math.max(1, Math.floor(baseHp * 0.02))
  const baseMpRegen = Math.max(1, Math.floor(baseMp * 0.02))

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
    baseHpRegen,
    baseMpRegen,
  }
}


