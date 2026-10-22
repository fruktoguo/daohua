import type { AutoUseSlot, CombatAbilitySlot, CombatAttributes, CombatTrigger, EquippedItem } from '../types'

/** 截断并保留最多 1 位小数，杜绝多余小数点 */
export function roundStat(val: number): number {
  if (!Number.isFinite(val)) return 0
  return Math.round(val * 10) / 10
}

/** 规范化战斗属性，所有数值项最多保留 1 位小数 */
export function sanitizeAttributes(attrs: CombatAttributes): CombatAttributes {
  const result: Partial<CombatAttributes> = {}
  for (const [key, val] of Object.entries(attrs)) {
    if (typeof val === 'number') {
      result[key as keyof CombatAttributes] = roundStat(val)
    }
  }
  return result as CombatAttributes
}

/**
 * 参战角色配置（模拟器单体模型）
 */
export interface SimulatorCombatantConfig {
  id: string
  name: string
  isSelf: boolean
  attributes: CombatAttributes
  abilitySlots: CombatAbilitySlot[]
  /** 技能独立触发器映射表 (abilityId -> triggers) */
  abilityTriggers?: Record<string, CombatTrigger[]>
  /** 阵型站位 (0~8) */
  formationIndex: number
  /** 已穿戴装备配置 (6部位装备实例) */
  equipment?: EquippedItem[]
  /** 战斗自动消耗品配置 (3药品 + 3通用) */
  consumables?: AutoUseSlot[]
  /** 消耗品独立触发器映射表 (itemId -> triggers) */
  consumableTriggers?: Record<string, CombatTrigger[]>
}

/**
 * 模拟推演环境配置
 */
export interface SimulatorConfig {
  /** 选定的大地图地点 ID */
  nodeId: string
  /** 选定的大地图地点名称 */
  nodeName: string
  /** 所属大地图区域名称 */
  mapName?: string
  /** 锁定的战斗遭遇 ID（可选，若为空则按节点遭遇池与预算轮盘动态刷怪） */
  encounterId?: string | null
  /** 模拟推进总时长（秒） */
  durationSeconds: number
  /** 确定性随机数种子 */
  seed: bigint
  /** 参战阵容（1~5人） */
  allies: SimulatorCombatantConfig[]
}

/**
 * 单个技能槽位的灵力缺口追踪指标
 */
export interface SkillManaDeficitStat {
  slotIndex: number
  abilityId: string
  abilityName: string
  /** 施放成功总次数 */
  castCount: number
  /** 累计消耗的灵力总量 */
  totalManaSpent: number
  /** 因灵力不足被阻断/跳过的次数 */
  deficitCount: number
  /** 处于“就绪但缺蓝”状态的累计持续时长（秒） */
  totalDeficitSeconds: number
  /** 卡蓝时间占战斗总有效时长的百分比 (0~1) */
  deficitRatio: number
  /** 单次卡蓝平均等待秒数 */
  averageWaitSeconds: number
}

/**
 * 灵力池综合状态与缺口评估
 */
export interface ManaDeficitReport {
  /** 全场灵力缺口并集总时长（秒，处于任一技能因缺蓝等待的状态） */
  overallDeficitSeconds: number
  /** 全场灵力缺口时间占比 (0~1，即 0% ~ 100%) */
  overallDeficitRatio: number
  /** 灵力循环健康度诊断级别 */
  diagnosis: 'sufficient' | 'mild' | 'moderate' | 'severe'
  /** 诊断评语 */
  diagnosisText: string
  /** 针对性的调优建议列表 */
  suggestions: string[]
  /** 各技能槽位卡蓝明细 */
  skillStats: SkillManaDeficitStat[]
  /** 灵力池极值与平均值统计 */
  poolStats: {
    averageMana: number
    minMana: number
    maxMana: number
    /** 灵力归零（完全枯竭）的总时长（秒） */
    emptyDurationSeconds: number
  }
}

/**
 * 掉落物品明细条目
 */
export interface SimulatorItemYield {
  itemId: string
  name: string
  quantity: number
  perHour: number
  /** 平均每日掉落数量 (24小时) */
  perDay: number
  /** 市场单价 (参考价优先，回收价兜底) */
  unitPrice: number
  /** 平均每日收益总额 (灵石) */
  dailyRevenue: number
}
/**
 * 妖兽击杀明细
 */
export interface MonsterKillStat {
  enemyId: string
  name: string
  rank: 'normal' | 'elite' | 'boss'
  count: number
  perHour: number
}

/**
 * 造成伤害来源明细条目
 */
export interface DamageSourceStat {
  sourceKey: string
  sourceName: string
  totalHits: number
  successfulHits: number
  hitRate: number
  criticalHits: number
  criticalRate: number
  totalDamage: number
  dps: number
  percentage: number
}

/**
 * 对指定怪物的造成伤害细分
 */
export interface TargetDamageBreakdown {
  targetId: string
  targetName: string
  totalDamage: number
  percentage: number
  dps: number
  sources: DamageSourceStat[]
}

/**
 * 承受伤害来源明细条目
 */
export interface DamageTakenSourceStat {
  sourceKey: string
  sourceName: string
  attackerName: string
  totalHits: number
  evadedHits: number
  parriedHits: number
  totalDamage: number
  dps: number
  percentage: number
}

/**
 * 承受指定怪物的伤害细分
 */
export interface AttackerDamageTakenBreakdown {
  attackerId: string
  attackerName: string
  totalDamage: number
  percentage: number
  dps: number
  sources: DamageTakenSourceStat[]
}

/**
 * 回复来源条目（气血/灵力）
 */
export interface RegenSourceStat {
  sourceKey: string
  sourceName: string
  totalAmount: number
  ratePerSecond: number
  percentage: number
}

/**
 * 气血与灵力恢复总体指标
 */
export interface RecoveryStats {
  hpRegenTotal: number
  hpRegenPerSecond: number
  hpSources: RegenSourceStat[]
  mpRegenTotal: number
  mpRegenPerSecond: number
  mpSources: RegenSourceStat[]
  isManaExhausted: boolean
}

/**
 * 技能每小时法力消耗明细
 */
export interface SkillManaConsumptionStat {
  abilityId: string
  abilityName: string
  totalManaSpent: number
  manaPerHour: number
  averagePerCast: number
  castCount: number
}

/**
 * 单条战斗详细事件日志
 */
export interface SimCombatLogEvent {
  timestamp: number
  actorName: string
  isAlly: boolean
  targetName?: string
  actionType: 'attack' | 'skill' | 'heal' | 'buff' | 'natural_regen' | 'defeat' | 'victory' | 'respawn'
  abilityName?: string
  damage?: number
  healing?: number
  isCritical?: boolean
  isBreak?: boolean
  isParry?: boolean
  isMiss?: boolean
  message: string
}

/**
 * 单场战斗完整回放记录（采样留存）
 */
export interface SimBattleLogRecord {
  battleIndex: number
  waveNumber: number
  encounterName: string
  outcome: 'victory' | 'defeat'
  durationSeconds: number
  events: SimCombatLogEvent[]
}


/**
 * 波次战斗时长统计
 */
export interface WaveDurationStat {
  /** 场次数 */
  count: number
  /** 平均单场战斗时长（秒） */
  averageSeconds: number
  /** 最短耗时（秒） */
  minSeconds: number
  /** 最长耗时（秒） */
  maxSeconds: number
}

/**
 * 遭遇时长深度细分（普通回/精英回/BOSS回）
 */
export interface WaveDurationBreakdown {
  /** 全场平均单场战斗时长（秒） */
  overallAverageSeconds: number
  /** 普通回耗时统计 */
  normal: WaveDurationStat
  /** 精英回耗时统计（若未刷出精英则为 null） */
  elite?: WaveDurationStat | null
  /** BOSS回耗时统计（若未刷出妖王则为 null） */
  boss?: WaveDurationStat | null
}

/**
 * 模拟器最终输出的综合分析报表
 */
export interface SimulatorReport {
  /** 选定的大地图地点 ID */
  nodeId: string
  /** 选定的大地图地点名称 */
  nodeName: string
  /** 所属大地图区域名称 */
  mapName: string
  /** 模拟总耗时（秒） */
  totalSimulatedSeconds: number
  /** 战斗有效进行总时长（秒） */
  combatActiveSeconds: number
  /** 搜寻怪物读条总时长（秒） */
  searchSeconds: number
  /** 团灭倒地复活读条总时长（秒） */
  respawnSeconds: number
  /** 总遭遇/战斗场次 */
  totalEncounters: number
  /** 胜利场次 */
  victories: number
  /** 团灭/失败场次 */
  defeats: number
  /** 胜率 (0~1) */
  winRate: number
  /** 模拟期间达到的最高波次 */
  maxWave: number
  /** 普通妖兽击杀数 */
  normalKills: number
  /** 精英妖兽击杀数 */
  eliteKills: number
  /** 妖王/首领击杀数 */
  bossKills: number
  /** 总击杀数 */
  totalKills: number
  /** 掉落物品清单汇总 */
  itemYields: SimulatorItemYield[]
  /** 平均每天掉落灵石总收益（按市场参考价格计算） */
  dailyDropsRevenue: number
  /** 每小时战斗场次速率 */
  encountersPerHour: number
  /** 玩家死亡总次数 */
  playerDeaths: number
  /** 玩家每小时死亡率 */
  playerDeathsPerHour: number
  /** 各妖兽击杀明细清单 */
  monsterKills: MonsterKillStat[]
  /** 造成伤害深度剖析 */
  damageStats: {
    totalDamage: number
    dps: number
    overallHitRate: number
    overallCriticalRate: number
    sources: DamageSourceStat[]
    targetBreakdown: TargetDamageBreakdown[]
  }
  /** 承受伤害深度剖析 */
  damageTakenStats: {
    totalDamageTaken: number
    dps: number
    overallEvasionRate: number
    overallParryRate: number
    sources: DamageTakenSourceStat[]
    attackerBreakdown: AttackerDamageTakenBreakdown[]
  }
  /** 气血与灵力恢复指标（奶牛放置每秒恢复） */
  recoveryStats: RecoveryStats
  /** 技能每小时法力消耗统计 */
  manaConsumptionStats: SkillManaConsumptionStat[]
  /** 累计获得修为总值 */
  totalExperience: number
  /** 每小时修为产出速率 */
  experiencePerHour: number
  /** 平均每天获得的修为经验（24小时） */
  dailyCultivationExp: number
  /** 累计获得战斗经验总值 */
  totalCombatExperience: number
  /** 每小时战斗经验产出速率 */
  combatExperiencePerHour: number
  /** 累计获得六维根基经验总值 */
  totalSixDimensionExperience: number
  /** 每小时六维经验产出速率 */
  sixDimensionExperiencePerHour: number
  /** 平均每天获得的六维经验（24小时） */
  dailySixDimensionExp: number
  /** 六维经验分项累计明细 */
  sixDimensionBreakdown: Record<string, number>
  /** 灵力缺口深度分析报告 */
  manaDeficit: ManaDeficitReport
  /** 最近战斗详细事件日志（供玩家回溯复盘死因与战局走势） */
  recentBattleLogs: SimBattleLogRecord[]
  /** 单场战斗时长分析 (普通回/精英回/BOSS回) */
  durationBreakdown: WaveDurationBreakdown
}

/**
 * 多地图节点横向刷图对比摘要
 */
export interface SimulatorNodeComparisonItem {
  nodeId: string
  nodeName: string
  mapName: string
  /** 每小时遇敌频次 */
  encountersPerHour: number
  /** 胜率 (0~1) */
  winRate: number
  /** 平均每天收益（全部掉落按市场参考价折算灵石总值） */
  dailyRevenue: number
  /** 平均每天修为经验 */
  dailyCultivationExp: number
  /** 平均每天六维经验 */
  dailySixDimensionExp: number
  /** 每小时击杀妖兽数 */
  killsPerHour: number
  /** 玩家每小时死亡数 */
  deathsPerHour: number
  /** 最高通关波次 */
  maxWave: number
  /** 推荐标签（最高收益/极速升级/稳健挂机等） */
  recommendTag?: string
  /** 对应的完整单点报表 */
  /** 单场平均耗时（秒） */
  avgDurationSeconds: number
  report: SimulatorReport
}

/**
 * 多节点模拟复合报表
 */
export interface MultiNodeSimulatorReport {
  comparison: SimulatorNodeComparisonItem[]
  reports: Record<string, SimulatorReport>
  selectedNodeId: string
}
