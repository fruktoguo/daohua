import type { CombatAttributes, CombatTrigger } from '../types'
import { deterministicRoll } from './deterministicRng'
import { SimSkillTriggerEvaluator, type SimUnitView } from './skillTriggerEvaluator'
import type { ManaDeficitTracker } from './manaDeficitTracker'

/** 百分比曲线倍率计算 */
export function percentMultiplier(points: number, mode: 'linear' | 'non-linear'): number {
  if (mode === 'linear') {
    return points >= 0 ? 1.0 + points / 100.0 : 100.0 / (100.0 + Math.abs(points))
  }
  return points >= 0 ? 100.0 / (100.0 + points) : 1.0 + Math.abs(points) / 100.0
}

/** 技能等级线性成长率 (每级 +2%) */
export function abilityLevelGrowth(level: number): number {
  return 1.0 + Math.max(0, level - 1) * 0.02
}

/**
 * 战内 Buff 条目
 */
export interface SimBuff {
  id: string
  name: string
  expiresAtSeconds: number
  stacks: number
  maxStacks: number
  hpRegen: number
  mpRegen: number
  hpRegenPostRatio: number
  mpRegenPostRatio: number
  attackSpeedPoints: number
  damageBonusPoints: number
  damageAbsorptionPoints: number
  threatPoints: number
  threatPerSecondMaxHpPoints: number
}

/**
 * 技能槽运行时抽象
 */
export interface SimSkill {
  slotIndex: number
  abilityId: string
  name: string
  level: number
  category: 'physical' | 'spell'
  element: 'none' | 'metal' | 'wood' | 'water' | 'fire' | 'earth'
  damageRatio: number
  flatDamage: number
  healingRatio: number
  flatHealing: number
  targetType: string
  castMilliseconds: number
  cooldownMilliseconds: number
  manaCost: number
  triggers?: readonly CombatTrigger[]
  buff?: SimBuff
}

/**
 * 模拟器战斗单位模型
 */
export class SimCombatant implements SimUnitView {
  public id: string
  public name: string
  public isAlly: boolean
  public memberIndex: number
  public stats: CombatAttributes
  public skills: SimSkill[] = []

  public hitpoints: number
  public manapoints: number
  public threat = 0
  public attackCount = 0
  public skillCastCount = 0
  public isAlive = true

  public nextAttackAtSeconds = 0
  public pendingSkillIndex = -1
  public skillCooldowns: number[] = []
  public respawnAtSeconds = Number.POSITIVE_INFINITY

  public buffs: SimBuff[] = []
  public combatExperience = 0

  constructor(
    id: string,
    name: string,
    isAlly: boolean,
    memberIndex: number,
    stats: CombatAttributes,
    skills: SimSkill[],
    combatExperience = 0
  ) {
    this.id = id
    this.name = name
    this.isAlly = isAlly
    this.memberIndex = memberIndex
    this.stats = stats
    this.skills = skills
    this.hitpoints = Math.max(1, stats.maxHitpoints)
    this.manapoints = Math.max(0, stats.maxManapoints)
    this.combatExperience = combatExperience
    this.skillCooldowns = new Array(skills.length).fill(0)
  }

  get maxHitpoints(): number {
    return this.stats.maxHitpoints
  }

  get maxManapoints(): number {
    return this.stats.maxManapoints
  }

  get effectiveThreat(): number {
    if (!this.isAlive) return 0
    return Math.max(0.1, this.threat + 1)
  }

  get buffAttackSpeedPoints(): number {
    let sum = 0
    for (const b of this.buffs) sum += b.attackSpeedPoints * b.stacks
    return sum
  }

  get buffDamageBonusPoints(): number {
    let sum = 0
    for (const b of this.buffs) sum += b.damageBonusPoints * b.stacks
    return sum
  }

  get buffDamageAbsorptionPoints(): number {
    let sum = 0
    for (const b of this.buffs) sum += b.damageAbsorptionPoints * b.stacks
    return sum
  }
}

/**
 * 战斗对局结果
 */
export type SimCombatOutcome = 'running' | 'victory' | 'defeat'

/**
 * 确定性战斗推进引擎
 */
export class SimCombatEncounter {
  public seed: bigint
  public elapsedSeconds = 0
  public allies: SimCombatant[]
  public enemies: SimCombatant[]
  public outcome: SimCombatOutcome = 'running'
  public playerRespawnSeconds: number
  public tracker: ManaDeficitTracker | null = null

  private static readonly TICK_SECONDS = 5.0

  constructor(
    seed: bigint,
    allies: SimCombatant[],
    enemies: SimCombatant[],
    playerRespawnSeconds = 10,
    tracker: ManaDeficitTracker | null = null
  ) {
    this.seed = seed
    this.allies = allies
    this.enemies = enemies
    this.playerRespawnSeconds = playerRespawnSeconds
    this.tracker = tracker

    this.decideInitialActions()
  }

  /**
   * 开战决策首次出手动作
   */
  public decideInitialActions(): void {
    // 敌方首次出手决策
    for (let i = 0; i < this.enemies.length; i++) {
      const enemy = this.enemies[i]
      if (enemy.isAlive && enemy.pendingSkillIndex < 0) {
        const target = this.selectEnemyTarget(enemy, this.allies, 19n)
        this.decideNextAction(enemy, this.enemies, this.allies, target)
      }
    }

    // 盟友首次出手决策
    for (let i = 0; i < this.allies.length; i++) {
      const ally = this.allies[i]
      if (ally.isAlive && ally.pendingSkillIndex < 0) {
        const target = this.selectAllyTarget(ally, this.enemies, 3n)
        this.decideNextAction(ally, this.allies, this.enemies, target)
      }
    }
  }

  /**
   * 目标轮盘抽取（与服务端 SelectTarget 免分配版逐位一致）
   */
  public selectTarget(
    opponents: readonly SimCombatant[],
    side: bigint,
    count: number,
    channel: bigint
  ): SimCombatant | null {
    let aliveCount = 0
    let totalThreat = 0
    let first: SimCombatant | null = null

    for (let i = 0; i < opponents.length; i++) {
      const unit = opponents[i]
      if (!unit.isAlive) continue
      if (!first) first = unit
      aliveCount++
      totalThreat += unit.effectiveThreat
    }

    if (aliveCount === 0) return null
    if (aliveCount === 1) return first

    const roll = deterministicRoll(this.seed, side, count, channel)
    if (totalThreat <= 1e-12) {
      const idx = Math.min(aliveCount - 1, Math.floor(roll * aliveCount))
      return this.pickAliveAt(opponents, idx)
    }

    let ticket = roll * totalThreat
    let last: SimCombatant | null = null
    for (let i = 0; i < opponents.length; i++) {
      const unit = opponents[i]
      if (!unit.isAlive) continue
      last = unit
      if (ticket < unit.effectiveThreat) return unit
      ticket -= unit.effectiveThreat
    }
    return last
  }

  private pickAliveAt(opponents: readonly SimCombatant[], wanted: number): SimCombatant | null {
    let seen = 0
    for (let i = 0; i < opponents.length; i++) {
      const unit = opponents[i]
      if (!unit.isAlive) continue
      if (seen++ === wanted) return unit
    }
    return null
  }

  private selectAllyTarget(unit: SimCombatant, opponents: readonly SimCombatant[], channel: bigint): SimCombatant | null {
    const side = unit.memberIndex === 0 ? 0n : 0x400n + BigInt(unit.memberIndex)
    return this.selectTarget(opponents, side, unit.attackCount, channel)
  }

  private selectEnemyTarget(unit: SimCombatant, opponents: readonly SimCombatant[], channel: bigint): SimCombatant | null {
    const side = BigInt(1 + unit.memberIndex)
    return this.selectTarget(opponents, side, unit.attackCount, channel)
  }

  /**
   * 出手动作决策：按槽位扫描可用技能，若不可用回落普攻
   */
  public decideNextAction(
    unit: SimCombatant,
    allies: readonly SimCombatant[],
    opponents: readonly SimCombatant[],
    target: SimCombatant | null
  ): void {
    const skills = unit.skills
    if (skills.length > 0) {
      for (let i = 0; i < skills.length; i++) {
        const slot = skills[i]
        if (unit.skillCooldowns[i] > this.elapsedSeconds + 1e-12) continue

        // 触发条件判断
        if (slot.triggers && slot.triggers.length > 0) {
          if (!SimSkillTriggerEvaluator.matchesAll(slot.triggers, unit, target, allies, opponents)) {
            if (unit.isAlly && unit.memberIndex === 0 && this.tracker) {
              this.tracker.cancelPendingDeficit(i, this.elapsedSeconds)
            }
            continue
          }
        }

        // 灵力判定与缺蓝追踪
        if (unit.manapoints < slot.manaCost) {
          if (unit.isAlly && unit.memberIndex === 0 && this.tracker) {
            this.tracker.recordDeficit(i, this.elapsedSeconds)
          }
          continue
        }

        // 灵力满足，扣减灵力进入施法
        unit.manapoints -= slot.manaCost
        if (unit.isAlly && unit.memberIndex === 0 && this.tracker) {
          this.tracker.recordCast(i, this.elapsedSeconds, slot.manaCost)
        }

        const castSeconds = Math.max(
          1e-9,
          (slot.castMilliseconds / 1000) * percentMultiplier(unit.stats.actionSpeedPoints, 'non-linear')
        )
        unit.pendingSkillIndex = i
        unit.nextAttackAtSeconds = this.elapsedSeconds + castSeconds

        // 冷却自吟唱结束时刻起算
        unit.skillCooldowns[i] = unit.nextAttackAtSeconds
          + (slot.cooldownMilliseconds / 1000) * percentMultiplier(unit.stats.cooldownReductionPoints, 'non-linear')
        return
      }
    }

    // 回落普通攻击
    unit.pendingSkillIndex = -1
    const attackInterval = (unit.stats.attackIntervalMilliseconds || 1000) / 1000
    unit.nextAttackAtSeconds = this.elapsedSeconds
      + attackInterval
      * percentMultiplier(unit.stats.attackSpeedPoints + unit.buffAttackSpeedPoints, 'non-linear')
      * percentMultiplier(unit.stats.actionSpeedPoints, 'non-linear')
  }

  /**
   * 查询下一个自然边界推进时刻
   */
  public nextBoundarySeconds(targetSeconds: number): number {
    if (this.outcome !== 'running' || this.elapsedSeconds >= targetSeconds) {
      return this.elapsedSeconds
    }

    let next = Number.POSITIVE_INFINITY

    // 存活盟友与敌人出手
    for (let i = 0; i < this.allies.length; i++) {
      const ally = this.allies[i]
      if (ally.isAlive && ally.nextAttackAtSeconds < next) next = ally.nextAttackAtSeconds
      else if (!ally.isAlive && ally.respawnAtSeconds < next) next = ally.respawnAtSeconds
    }

    for (let i = 0; i < this.enemies.length; i++) {
      const enemy = this.enemies[i]
      if (enemy.isAlive && enemy.nextAttackAtSeconds < next) next = enemy.nextAttackAtSeconds
    }

    // Buff 到期与 5s 回复网格
    const buffNext = this.nextBuffBoundary()
    if (buffNext < next) next = buffNext

    return Math.min(next, targetSeconds)
  }

  private nextBuffBoundary(): number {
    let next = Number.POSITIVE_INFINITY
    let regenPending = false

    const scan = (u: SimCombatant): void => {
      if (u.stats.hpRegenPerFiveSeconds !== 0 || u.stats.mpRegenPerFiveSeconds !== 0) regenPending = true
      for (const b of u.buffs) {
        if (b.expiresAtSeconds < next) next = b.expiresAtSeconds
        if (b.hpRegen !== 0 || b.mpRegen !== 0 || b.hpRegenPostRatio !== 0 || b.mpRegenPostRatio !== 0) {
          regenPending = true
        }
      }
    }

    this.allies.forEach(scan)
    this.enemies.forEach(scan)

    if (regenPending) {
      const nextTick = (Math.floor(this.elapsedSeconds / SimCombatEncounter.TICK_SECONDS) + 1) * SimCombatEncounter.TICK_SECONDS
      next = Math.min(next, nextTick)
    }

    return next
  }

  /**
   * 推进一个自然边界
   */
  public advanceOneBoundary(targetSeconds: number): void {
    const nextSeconds = this.nextBoundarySeconds(targetSeconds)
    const stepDuration = Math.max(0, nextSeconds - this.elapsedSeconds)
    this.elapsedSeconds = nextSeconds

    // 灵力追踪采样
    if (this.tracker && this.allies[0]) {
      this.tracker.sampleMana(this.allies[0].manapoints, stepDuration)
    }

    // 1. 战内效果与周期回复相位
    this.applyBuffTick(nextSeconds)

    // 2. 盟友复活相位
    for (const ally of this.allies) {
      if (!ally.isAlive && ally.respawnAtSeconds <= nextSeconds + 1e-12) {
        ally.isAlive = true
        ally.hitpoints = ally.stats.maxHitpoints
        ally.respawnAtSeconds = Number.POSITIVE_INFINITY
        this.decideNextAction(ally, this.allies, this.enemies, this.selectAllyTarget(ally, this.enemies, 3n))
      }
    }

    // 3. 盟友出手相位
    for (let i = 0; i < this.allies.length; i++) {
      const ally = this.allies[i]
      if (ally.isAlive && ally.nextAttackAtSeconds <= nextSeconds + 1e-12) {
        const side = ally.memberIndex === 0 ? 0n : 0x400n + BigInt(ally.memberIndex)
        this.resolvePending(ally, this.allies, this.enemies, side)
      }
    }

    // 4. 敌方出手相位
    for (let i = 0; i < this.enemies.length; i++) {
      const enemy = this.enemies[i]
      if (enemy.isAlive && enemy.nextAttackAtSeconds <= nextSeconds + 1e-12) {
        const side = BigInt(1 + i)
        this.resolvePending(enemy, this.enemies, this.allies, side)
      }
    }

    // 5. 检查阵亡单位与复活登记
    for (const ally of this.allies) {
      if (!ally.isAlive && !Number.isFinite(ally.respawnAtSeconds) && this.playerRespawnSeconds > 0) {
        ally.respawnAtSeconds = this.elapsedSeconds + this.playerRespawnSeconds
      }
    }

    // 6. 检查胜负终局
    const anyEnemyAlive = this.enemies.some(e => e.isAlive)
    const anyAllyAlive = this.allies.some(a => a.isAlive)

    if (!anyEnemyAlive) {
      this.outcome = 'victory'
      if (this.tracker) this.tracker.finalize(this.elapsedSeconds)
    } else if (!anyAllyAlive) {
      this.outcome = 'defeat'
      if (this.tracker) this.tracker.finalize(this.elapsedSeconds)
    }
  }

  /**
   * 批量推进至目标时间或战斗决出胜负
   */
  public advanceTo(targetSeconds: number): SimCombatOutcome {
    let loopGuard = 0
    while (this.outcome === 'running' && this.elapsedSeconds < targetSeconds - 1e-9) {
      if (++loopGuard > 100_000) break
      this.advanceOneBoundary(targetSeconds)
    }
    return this.outcome
  }

  /**
   * 结算单位当前出手（技能结算或普攻结算）
   */
  private resolvePending(
    unit: SimCombatant,
    allies: readonly SimCombatant[],
    opponents: readonly SimCombatant[],
    side: bigint
  ): void {
    const pending = unit.pendingSkillIndex
    unit.pendingSkillIndex = -1

    if (pending >= 0 && pending < unit.skills.length) {
      const skill = unit.skills[pending]
      this.castSkill(unit, allies, opponents, skill, side + 0x1000n)
      unit.skillCastCount++
    } else {
      const target = this.selectTarget(opponents, side, unit.attackCount, 0n)
      if (target) {
        this.resolveAttack(unit, target, side)
      }
    }

    // 出手完毕立即决策下一次行动
    const nextTarget = this.selectTarget(opponents, side, unit.attackCount, 3n)
    this.decideNextAction(unit, allies, opponents, nextTarget)
  }

  /**
   * 普攻命中结算
   */
  private resolveAttack(attacker: SimCombatant, target: SimCombatant, side: bigint): void {
    attacker.attackCount++
    const damage = this.calculateDamage(attacker, target, null, side)
    if (damage > 0) {
      target.hitpoints = Math.max(0, target.hitpoints - damage)
      if (target.hitpoints <= 0) target.isAlive = false

      // 汲取吸血/吸蓝
      if (attacker.stats.hpDrainPoints > 0) {
        const drainHp = damage * (attacker.stats.hpDrainPoints / 100)
        attacker.hitpoints = Math.min(attacker.stats.maxHitpoints, attacker.hitpoints + drainHp)
      }
      if (attacker.stats.mpDrainPoints > 0) {
        const drainMp = damage * (attacker.stats.mpDrainPoints / 100)
        attacker.manapoints = Math.min(attacker.stats.maxManapoints, attacker.manapoints + drainMp)
      }
    }
  }

  /**
   * 技能施放结算
   */
  private castSkill(
    caster: SimCombatant,
    allies: readonly SimCombatant[],
    opponents: readonly SimCombatant[],
    skill: SimSkill,
    side: bigint
  ): void {
    const isHealing = skill.healingRatio > 0 || skill.flatHealing > 0
    const levelMult = abilityLevelGrowth(skill.level)

    if (isHealing) {
      // 治疗技能
      const targets = this.resolveTargets(caster, allies, opponents, skill.targetType, side)
      const baseHeal = (caster.stats.spellAttack * skill.healingRatio + skill.flatHealing) * levelMult
      const healAmount = baseHeal * percentMultiplier(caster.stats.healingBonusPoints, 'linear')

      for (const target of targets) {
        if (!target.isAlive) continue
        target.hitpoints = Math.min(target.stats.maxHitpoints, target.hitpoints + healAmount)
      }
    } else {
      // 伤害技能
      const targets = this.resolveTargets(caster, allies, opponents, skill.targetType, side)
      for (const target of targets) {
        if (!target.isAlive) continue
        caster.attackCount++
        const damage = this.calculateDamage(caster, target, skill, side)
        if (damage > 0) {
          target.hitpoints = Math.max(0, target.hitpoints - damage)
          if (target.hitpoints <= 0) target.isAlive = false
        }
      }
    }

    // 附带 Buff 效果
    if (skill.buff) {
      const buffTargets = this.resolveTargets(caster, allies, opponents, skill.targetType, side)
      for (const target of buffTargets) {
        if (!target.isAlive) continue
        this.applyBuff(target, skill.buff)
      }
    }
  }

  private resolveTargets(
    caster: SimCombatant,
    allies: readonly SimCombatant[],
    opponents: readonly SimCombatant[],
    targetType: string,
    side: bigint
  ): SimCombatant[] {
    switch (targetType) {
      case 'self':
        return [caster]
      case 'allAllies':
        return allies.filter(a => a.isAlive)
      case 'lowestHitpointsAlly': {
        let best: SimCombatant | null = null
        for (const a of allies) {
          if (!a.isAlive) continue
          if (!best || a.hitpoints / a.stats.maxHitpoints < best.hitpoints / best.stats.maxHitpoints) {
            best = a
          }
        }
        return best ? [best] : [caster]
      }
      case 'allEnemies':
        return opponents.filter(o => o.isAlive)
      case 'targetEnemy':
      default: {
        const single = this.selectTarget(opponents, side, caster.attackCount, 0n)
        return single ? [single] : []
      }
    }
  }

  private applyBuff(target: SimCombatant, buffTemplate: SimBuff): void {
    const existing = target.buffs.find(b => b.id === buffTemplate.id)
    if (existing) {
      existing.stacks = Math.min(existing.maxStacks, existing.stacks + 1)
      existing.expiresAtSeconds = Math.max(existing.expiresAtSeconds, this.elapsedSeconds + (buffTemplate.expiresAtSeconds || 10))
    } else {
      target.buffs.push({
        ...buffTemplate,
        stacks: 1,
        expiresAtSeconds: this.elapsedSeconds + (buffTemplate.expiresAtSeconds || 10),
      })
    }
  }

  /**
   * 纯命中与伤害解算（与服务端 ResolveHit 完全一致）
   */
  private calculateDamage(
    attacker: SimCombatant,
    defender: SimCombatant,
    skill: SimSkill | null,
    side: bigint
  ): number {
    const count = attacker.attackCount
    const atkStats = attacker.stats
    const defStats = defender.stats

    // 对抗掷点
    const contestRoll = deterministicRoll(this.seed, side, count, 0n)
    const broken = atkStats.breakPower > defStats.parryResolve
      && contestRoll < atkStats.breakPower / (atkStats.breakPower + defStats.parryResolve + 100)
    const parried = defStats.parryResolve > atkStats.breakPower
      && contestRoll < defStats.parryResolve / (defStats.parryResolve + atkStats.breakPower + 100)

    let accuracy = atkStats.accuracy * (broken ? 2 : 1)
    let evasion = defStats.evasion * (parried ? 2 : 1)

    // 战斗经验对抗修正
    if (attacker.combatExperience > 0 || defender.combatExperience > 0) {
      const r = Math.min(10, Math.max(0.1, attacker.combatExperience / Math.max(defender.combatExperience, 1e-9)))
      if (r >= 1) {
        accuracy *= Math.min(2, 1 + (r - 1) / 8)
        evasion *= Math.max(0.5, 1 - (r - 1) / 16)
      } else {
        const s = 1 / r
        accuracy *= Math.max(0.5, 1 - (s - 1) / 16)
        evasion *= Math.min(2, 1 + (s - 1) / 8)
      }
    }

    const hit = deterministicRoll(this.seed, side, count, 1n) >= evasion / (evasion + accuracy + 100)
    if (!hit) return 0

    // 暴击判定
    const critPoints = atkStats.critical * (broken ? 2 : 1)
    const critical = deterministicRoll(this.seed, side, count, 2n)
      < critPoints / (critPoints + defStats.criticalResistance + 100)

    const isPhysical = skill ? skill.category === 'physical' : true
    let resistance = isPhysical ? defStats.armor : defStats.magicResistance
    if (parried) resistance *= 2

    // 原始威力 RawDamage
    let rawDamage = 0
    if (!skill) {
      // 普攻 = AttackPower * (1 + 普攻增幅点数 / 100)
      rawDamage = atkStats.physicalAttack * percentMultiplier(atkStats.basicAttackBonusPoints, 'linear')
    } else {
      const growth = abilityLevelGrowth(skill.level)
      const baseAtk = isPhysical ? atkStats.physicalAttack : atkStats.spellAttack
      rawDamage = (baseAtk * skill.damageRatio + skill.flatDamage) * growth
      rawDamage *= percentMultiplier(atkStats.skillBonusPoints, 'linear')
    }

    if (rawDamage <= 0) return 0

    // 守方等阶 K (默认按 1 级 T1 换算，怪物取自身等级换算)
    const tier = Math.max(1, Math.floor((defStats.maxHitpoints > 200 ? 2 : 1)))
    // 第一层：抗性平方穿甲 RawDamage² / (RawDamage + RES * K + 1)
    let damage = rawDamage * (rawDamage / (rawDamage + resistance * tier + 1))

    // 第二层：攻方大类增伤
    damage *= percentMultiplier(isPhysical ? atkStats.physicalDamageBonusPoints : atkStats.spellDamageBonusPoints, 'linear')
    damage *= percentMultiplier(atkStats.totalDamageBonusPoints + attacker.buffDamageBonusPoints, 'linear')

    // 第三层：守方大类减伤与总免伤收口
    damage *= percentMultiplier(isPhysical ? defStats.physicalDamageReductionPoints : defStats.spellDamageReductionPoints, 'non-linear')
    damage *= percentMultiplier(defStats.damageAbsorptionPoints + defender.buffDamageAbsorptionPoints, 'non-linear')

    // 暴击伤害倍率 (基础 1.5 + 额外暴伤)
    if (critical) {
      const critMult = 1.0 + (atkStats.criticalDamagePoints || 50) / 100.0
      damage *= Math.max(1.5, critMult)
    }

    // 第四层：固定增减免
    const fixedBonus = isPhysical ? (atkStats.fixedDamageBonus + atkStats.fixedPhysicalDamageBonus)
      : (atkStats.fixedDamageBonus + atkStats.fixedSpellDamageBonus)
    const fixedReduction = isPhysical ? (defStats.fixedDamageReduction + defStats.fixedPhysicalDamageReduction)
      : (defStats.fixedDamageReduction + defStats.fixedSpellDamageReduction)

    return Math.max(0, damage + fixedBonus - fixedReduction)
  }

  /**
   * 5秒周期回复与 Buff 到期更新
   */
  private applyBuffTick(atSeconds: number): void {
    const isRegenTick = Math.abs(atSeconds / SimCombatEncounter.TICK_SECONDS - Math.round(atSeconds / SimCombatEncounter.TICK_SECONDS)) <= 1e-9

    const tickUnit = (unit: SimCombatant): void => {
      // 移除过期 Buff
      unit.buffs = unit.buffs.filter(b => b.expiresAtSeconds > atSeconds + 1e-9)

      if (!isRegenTick) return

      let hp = unit.stats.hpRegenPerFiveSeconds || 0
      let mp = unit.stats.mpRegenPerFiveSeconds || 0
      let hpPost = 0
      let mpPost = 0

      for (const b of unit.buffs) {
        hp += b.hpRegen * b.stacks
        mp += b.mpRegen * b.stacks
        hpPost += b.hpRegenPostRatio * b.stacks
        mpPost += b.mpRegenPostRatio * b.stacks
      }

      hp *= 1 + hpPost
      mp *= 1 + mpPost

      if (unit.isAlive && hp !== 0) {
        unit.hitpoints = Math.min(unit.stats.maxHitpoints, Math.max(0, unit.hitpoints + hp))
      }
      if (mp !== 0) {
        unit.manapoints = Math.min(unit.stats.maxManapoints, Math.max(0, unit.manapoints + mp))
      }
    }

    this.allies.forEach(tickUnit)
    this.enemies.forEach(tickUnit)
  }
}
