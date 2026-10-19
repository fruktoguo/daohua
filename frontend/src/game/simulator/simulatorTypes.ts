import type { CombatAbilitySlot, CombatAttributes, CombatTrigger } from '../types'

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
}

/**
 * 模拟推演环境配置
 */
export interface SimulatorConfig {
  /** 选定的大地图地点 ID */
  nodeId: string
  /** 选定的大地图地点名称 */
  nodeName: string
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
}

/**
 * 模拟器最终输出的综合分析报表
 */
export interface SimulatorReport {
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
  /** 累计获得修为总值 */
  totalExperience: number
  /** 每小时修为产出速率 */
  experiencePerHour: number
  /** 累计获得战斗经验总值 */
  totalCombatExperience: number
  /** 每小时战斗经验产出速率 */
  combatExperiencePerHour: number
  /** 灵力缺口深度分析报告 */
  manaDeficit: ManaDeficitReport
}
