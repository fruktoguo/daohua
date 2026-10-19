import type { ManaDeficitReport, SkillManaDeficitStat } from './simulatorTypes'

interface SlotDeficitState {
  slotIndex: number
  abilityId: string
  abilityName: string
  manaCost: number
  castCount: number
  totalManaSpent: number
  deficitCount: number
  totalDeficitSeconds: number
  isPendingDeficit: boolean
  deficitStart: number
}

/**
 * 灵力缺口分析与追踪器（按技能槽精确统计卡蓝等待时长与空转占比）
 */
export class ManaDeficitTracker {
  private readonly slots: SlotDeficitState[] = []
  private readonly slotMap = new Map<number, SlotDeficitState>()
  private totalCombatActiveSeconds = 0
  // 灵力池追踪
  private manaTimeIntegral = 0
  private minMana = Number.POSITIVE_INFINITY
  private maxMana = 0
  private emptyManaSeconds = 0

  // 全局卡蓝并集区间追踪
  private isAnySlotDeficit = false
  private currentDeficitBlockStart = 0
  private totalUnionDeficitSeconds = 0

  constructor(
    abilitySlots: ReadonlyArray<{
      slotIndex?: number
      abilityId: string
      name?: string
      manaCost: number
    }>,
    initialMana: number
  ) {
    this.minMana = initialMana
    this.maxMana = initialMana

    abilitySlots.forEach((slot, index) => {
      const slotIdx = typeof slot.slotIndex === 'number' ? slot.slotIndex : index
      const item: SlotDeficitState = {
        slotIndex: slotIdx,
        abilityId: slot.abilityId,
        abilityName: slot.name || slot.abilityId,
        manaCost: slot.manaCost || 0,
        castCount: 0,
        totalManaSpent: 0,
        deficitCount: 0,
        totalDeficitSeconds: 0,
        isPendingDeficit: false,
        deficitStart: 0,
      }
      this.slots.push(item)
      this.slotMap.set(slotIdx, item)
    })
  }

  /**
   * 记录时间片推进对灵力池的采样
   */
  public sampleMana(currentMana: number, stepDuration: number): void {
    if (stepDuration <= 0) return
    this.totalCombatActiveSeconds += stepDuration
    this.manaTimeIntegral += currentMana * stepDuration
    if (currentMana < this.minMana) this.minMana = currentMana
    if (currentMana > this.maxMana) this.maxMana = currentMana
    if (currentMana <= 1e-6) this.emptyManaSeconds += stepDuration

  }

  /**
   * 标记技能在当前时间 t 因灵力不足被跳过/无法起手
   */
  public recordDeficit(slotIndex: number, currentElapsedSeconds: number): void {
    const slot = this.slotMap.get(slotIndex)
    if (!slot || slot.manaCost <= 0) return
    slot.deficitCount++
    if (!slot.isPendingDeficit) {
      slot.isPendingDeficit = true
      slot.deficitStart = currentElapsedSeconds

      if (!this.isAnySlotDeficit) {
        this.isAnySlotDeficit = true
        this.currentDeficitBlockStart = currentElapsedSeconds
      }
    }
  }

  /**
   * 标记技能已成功扣除灵力进入施放或已不需要等待（结束该段卡蓝区间）
   */
  public recordCast(slotIndex: number, currentElapsedSeconds: number, manaCost: number): void {
    const slot = this.slotMap.get(slotIndex)
    if (!slot) return
    slot.castCount++
    slot.totalManaSpent += manaCost

    if (slot.isPendingDeficit) {
      const wait = Math.max(0, currentElapsedSeconds - slot.deficitStart)
      slot.totalDeficitSeconds += wait
      slot.isPendingDeficit = false

      if (this.isAnySlotDeficit && !this.slots.some(s => s.isPendingDeficit)) {
        this.isAnySlotDeficit = false
        this.totalUnionDeficitSeconds += Math.max(0, currentElapsedSeconds - this.currentDeficitBlockStart)
      }
    }
  }

  /**
   * 当目标丢失或条件不再满足时，取消卡蓝等待状态
   */
  public cancelPendingDeficit(slotIndex: number, currentElapsedSeconds: number): void {
    const slot = this.slotMap.get(slotIndex)
    if (!slot || !slot.isPendingDeficit) return
    const wait = Math.max(0, currentElapsedSeconds - slot.deficitStart)
    slot.totalDeficitSeconds += wait
    slot.isPendingDeficit = false

    if (this.isAnySlotDeficit && !this.slots.some(s => s.isPendingDeficit)) {
      this.isAnySlotDeficit = false
      this.totalUnionDeficitSeconds += Math.max(0, currentElapsedSeconds - this.currentDeficitBlockStart)
    }
  }

  /**
   * 战斗结束时收口正在等待的未完结区间
   */
  public finalize(combatElapsedSeconds: number): void {
    for (const slot of this.slots) {
      if (slot.isPendingDeficit) {
        const wait = Math.max(0, combatElapsedSeconds - slot.deficitStart)
        slot.totalDeficitSeconds += wait
        slot.isPendingDeficit = false
      }
    }
    if (this.isAnySlotDeficit) {
      this.isAnySlotDeficit = false
      this.totalUnionDeficitSeconds += Math.max(0, combatElapsedSeconds - this.currentDeficitBlockStart)
    }
  }

  /**
   * 生成灵力缺口深度评估报告
   */
  public generateReport(): ManaDeficitReport {
    const activeSec = Math.max(1e-6, this.totalCombatActiveSeconds)
    const overallDeficitRatio = Math.min(1, Math.max(0, this.totalUnionDeficitSeconds / activeSec))

    const skillStats: SkillManaDeficitStat[] = this.slots.map(s => {
      const deficitRatio = Math.min(1, Math.max(0, s.totalDeficitSeconds / activeSec))
      const averageWaitSeconds = s.deficitCount > 0 ? s.totalDeficitSeconds / s.deficitCount : 0
      return {
        slotIndex: s.slotIndex,
        abilityId: s.abilityId,
        abilityName: s.abilityName,
        castCount: s.castCount,
        totalManaSpent: s.totalManaSpent,
        deficitCount: s.deficitCount,
        totalDeficitSeconds: s.totalDeficitSeconds,
        deficitRatio,
        averageWaitSeconds,
      }
    })

    // 诊断等级
    let diagnosis: 'sufficient' | 'mild' | 'moderate' | 'severe'
    let diagnosisText = ''
    const suggestions: string[] = []

    if (overallDeficitRatio < 0.02) {
      diagnosis = 'sufficient'
    } else if (overallDeficitRatio < 0.15) {
      diagnosis = 'mild'
    } else if (overallDeficitRatio < 0.40) {
      diagnosis = 'moderate'
    } else {
      diagnosis = 'severe'
    }

    // 找出耗蓝最高和卡蓝最严重的技能
    const mostDeficitSkill = [...skillStats].sort((a, b) => b.totalDeficitSeconds - a.totalDeficitSeconds)[0]
    if (mostDeficitSkill && mostDeficitSkill.totalDeficitSeconds > 3) {
      suggestions.push(
        `【${mostDeficitSkill.abilityName}】卡蓝等待 ${mostDeficitSkill.totalDeficitSeconds.toFixed(1)}s，建议提高释放阈值`
      )
    }

    const totalSpent = skillStats.reduce((sum, s) => sum + s.totalManaSpent, 0)
    const mostCostSkill = [...skillStats].sort((a, b) => b.totalManaSpent - a.totalManaSpent)[0]
    if (mostCostSkill && totalSpent > 0 && mostCostSkill.totalManaSpent / totalSpent > 0.5) {
      suggestions.push(
        `【${mostCostSkill.abilityName}】耗蓝占比 ${(mostCostSkill.totalManaSpent / totalSpent * 100).toFixed(0)}%`
      )
    }

    return {
      overallDeficitSeconds: this.totalUnionDeficitSeconds,
      overallDeficitRatio,
      diagnosis,
      diagnosisText,
      suggestions,
      skillStats,
      poolStats: {
        averageMana: activeSec > 0 ? this.manaTimeIntegral / activeSec : 0,
        minMana: Number.isFinite(this.minMana) ? this.minMana : 0,
        maxMana: this.maxMana,
        emptyDurationSeconds: this.emptyManaSeconds,
      },
    }
  }
}
