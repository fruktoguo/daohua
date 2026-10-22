import type { AutoUseSlot, CombatAttributes, CombatTrigger } from '../types.ts'
import type { SimCombatLogEvent } from './simulatorTypes.ts'
import { deterministicRoll } from './deterministicRng.ts'
import { SimSkillTriggerEvaluator, type SimUnitView } from './skillTriggerEvaluator.ts'
import type { ManaDeficitTracker } from './manaDeficitTracker.ts'

/** 保证战斗属性无任何 undefined 或 NaN */
export function ensureCombatAttributes(stats?: Partial<CombatAttributes> | null): CombatAttributes {
  return {
    maxHitpoints: Math.max(1, stats?.maxHitpoints ?? 100),
    maxManapoints: Math.max(0, stats?.maxManapoints ?? 50),
    physicalAttack: stats?.physicalAttack ?? 10,
    spellAttack: stats?.spellAttack ?? 5,
    armor: stats?.armor ?? 5,
    magicResistance: stats?.magicResistance ?? 5,
    accuracy: stats?.accuracy ?? 10,
    evasion: stats?.evasion ?? 5,
    critical: stats?.critical ?? 5,
    criticalResistance: stats?.criticalResistance ?? 5,
    criticalDamagePoints: stats?.criticalDamagePoints ?? 50,
    breakPower: stats?.breakPower ?? 5,
    parryResolve: stats?.parryResolve ?? 5,
    hpRegenPerFiveSeconds: stats?.hpRegenPerFiveSeconds ?? 0,
    mpRegenPerFiveSeconds: stats?.mpRegenPerFiveSeconds ?? 0,
    attackIntervalMilliseconds: Math.max(100, stats?.attackIntervalMilliseconds || 1000),
    physicalDamageBonusPoints: stats?.physicalDamageBonusPoints ?? 0,
    spellDamageBonusPoints: stats?.spellDamageBonusPoints ?? 0,
    metalDamageBonusPoints: stats?.metalDamageBonusPoints ?? 0,
    woodDamageBonusPoints: stats?.woodDamageBonusPoints ?? 0,
    waterDamageBonusPoints: stats?.waterDamageBonusPoints ?? 0,
    fireDamageBonusPoints: stats?.fireDamageBonusPoints ?? 0,
    earthDamageBonusPoints: stats?.earthDamageBonusPoints ?? 0,
    totalDamageBonusPoints: stats?.totalDamageBonusPoints ?? 0,
    basicAttackBonusPoints: stats?.basicAttackBonusPoints ?? 0,
    skillBonusPoints: stats?.skillBonusPoints ?? 0,
    physicalDamageReductionPoints: stats?.physicalDamageReductionPoints ?? 0,
    spellDamageReductionPoints: stats?.spellDamageReductionPoints ?? 0,
    metalDamageReductionPoints: stats?.metalDamageReductionPoints ?? 0,
    woodDamageReductionPoints: stats?.woodDamageReductionPoints ?? 0,
    waterDamageReductionPoints: stats?.waterDamageReductionPoints ?? 0,
    fireDamageReductionPoints: stats?.fireDamageReductionPoints ?? 0,
    earthDamageReductionPoints: stats?.earthDamageReductionPoints ?? 0,
    damageAbsorptionPoints: stats?.damageAbsorptionPoints ?? 0,
    fixedDamageBonus: stats?.fixedDamageBonus ?? 0,
    fixedDamageReduction: stats?.fixedDamageReduction ?? 0,
    fixedPhysicalDamageBonus: stats?.fixedPhysicalDamageBonus ?? 0,
    fixedPhysicalDamageReduction: stats?.fixedPhysicalDamageReduction ?? 0,
    fixedSpellDamageBonus: stats?.fixedSpellDamageBonus ?? 0,
    fixedSpellDamageReduction: stats?.fixedSpellDamageReduction ?? 0,
    attackSpeedPoints: stats?.attackSpeedPoints ?? 0,
    actionSpeedPoints: stats?.actionSpeedPoints ?? 0,
    cooldownReductionPoints: stats?.cooldownReductionPoints ?? 0,
    threatBonusPoints: stats?.threatBonusPoints ?? 0,
    hpDrainPoints: stats?.hpDrainPoints ?? 0,
    mpDrainPoints: stats?.mpDrainPoints ?? 0,
    damageReflectPoints: stats?.damageReflectPoints ?? 0,
    healingBonusPoints: stats?.healingBonusPoints ?? 0,
  }
}

/** 伤害结算单次命中结果详情 */
export interface SimHitResult {
  hit: boolean
  damage: number
  critical: boolean
  broken: boolean
  parried: boolean
}

/** 造成伤害统计条目 */
export interface RawHitSourceStat {
  sourceKey: string
  sourceName: string
  totalHits: number
  successfulHits: number
  criticalHits: number
  totalDamage: number
}

/** 承受伤害统计条目 */
export interface RawDamageTakenStat {
  sourceKey: string
  sourceName: string
  attackerName: string
  totalHits: number
  evadedHits: number
  parriedHits: number
  totalDamage: number
}

/**
 * 综合战斗数据采集器（收集总伤害、承伤、DPS、回复与击杀）
 */
export class CombatStatsCollector {
  public damageSources = new Map<string, RawHitSourceStat>()
  public targetDamage = new Map<string, {
    targetId: string
    targetName: string
    totalDamage: number
    sources: Map<string, RawHitSourceStat>
  }>()

  public damageTakenSources = new Map<string, RawDamageTakenStat>()
  public attackerDamageTaken = new Map<string, {
    attackerId: string
    attackerName: string
    totalDamage: number
    sources: Map<string, RawDamageTakenStat>
  }>()

  public hpRegenNatural = 0
  public hpRegenDrain = 0
  public hpRegenSkill = new Map<string, { abilityId: string; name: string; amount: number }>()
  public mpRegenNatural = 0
  public mpRegenDrain = 0
  public isManaExhausted = false

  public skillManaSpent = new Map<string, { abilityId: string; name: string; totalSpent: number; casts: number }>()

  public recordHit(attacker: SimCombatant, target: SimCombatant, skill: SimSkill | null, res: SimHitResult): void {
    if (attacker.isAlly) {
      const sourceKey = skill ? `skill:${skill.abilityId}` : 'basic-attack'
      const sourceName = skill ? skill.name : '普通攻击'

      let src = this.damageSources.get(sourceKey)
      if (!src) {
        src = { sourceKey, sourceName, totalHits: 0, successfulHits: 0, criticalHits: 0, totalDamage: 0 }
        this.damageSources.set(sourceKey, src)
      }
      src.totalHits++
      if (res.hit) {
        src.successfulHits++
        if (res.critical) src.criticalHits++
        src.totalDamage += res.damage
      }

      const targetId = target.definitionId || target.id
      let tgt = this.targetDamage.get(targetId)
      if (!tgt) {
        tgt = { targetId, targetName: target.name, totalDamage: 0, sources: new Map() }
        this.targetDamage.set(targetId, tgt)
      }
      if (res.hit) {
        tgt.totalDamage += res.damage
      }
      let tgtSrc = tgt.sources.get(sourceKey)
      if (!tgtSrc) {
        tgtSrc = { sourceKey, sourceName, totalHits: 0, successfulHits: 0, criticalHits: 0, totalDamage: 0 }
        tgt.sources.set(sourceKey, tgtSrc)
      }
      tgtSrc.totalHits++
      if (res.hit) {
        tgtSrc.successfulHits++
        if (res.critical) tgtSrc.criticalHits++
        tgtSrc.totalDamage += res.damage
      }
    }

    if (target.isAlly) {
      const sourceKey = skill ? `skill:${skill.abilityId}` : 'basic-attack'
      const sourceName = skill ? skill.name : '普通攻击'

      let taken = this.damageTakenSources.get(sourceKey)
      if (!taken) {
        taken = { sourceKey, sourceName, attackerName: attacker.name, totalHits: 0, evadedHits: 0, parriedHits: 0, totalDamage: 0 }
        this.damageTakenSources.set(sourceKey, taken)
      }
      taken.totalHits++
      if (!res.hit) taken.evadedHits++
      if (res.parried) taken.parriedHits++
      if (res.hit) taken.totalDamage += res.damage

      const attackerId = attacker.definitionId || attacker.id
      let atk = this.attackerDamageTaken.get(attackerId)
      if (!atk) {
        atk = { attackerId, attackerName: attacker.name, totalDamage: 0, sources: new Map() }
        this.attackerDamageTaken.set(attackerId, atk)
      }
      if (res.hit) atk.totalDamage += res.damage
      let atkSrc = atk.sources.get(sourceKey)
      if (!atkSrc) {
        atkSrc = { sourceKey, sourceName, attackerName: attacker.name, totalHits: 0, evadedHits: 0, parriedHits: 0, totalDamage: 0 }
        atk.sources.set(sourceKey, atkSrc)
      }
      atkSrc.totalHits++
      if (!res.hit) atkSrc.evadedHits++
      if (res.parried) atkSrc.parriedHits++
      if (res.hit) atkSrc.totalDamage += res.damage
    }
  }

  public recordDrain(unit: SimCombatant, hp: number, mp: number): void {
    if (unit.isAlly) {
      if (hp > 0) this.hpRegenDrain += hp
      if (mp > 0) this.mpRegenDrain += mp
    }
  }

  public recordReflect(target: SimCombatant, attacker: SimCombatant, reflectDamage: number): void {
    if (target.isAlly) {
      let src = this.damageSources.get('reflect')
      if (!src) {
        src = { sourceKey: 'reflect', sourceName: '伤害反弹', totalHits: 0, successfulHits: 0, criticalHits: 0, totalDamage: 0 }
        this.damageSources.set('reflect', src)
      }
      src.totalHits++
      src.successfulHits++
      src.totalDamage += reflectDamage
    }
    if (attacker.isAlly) {
      let taken = this.damageTakenSources.get('reflect')
      if (!taken) {
        taken = { sourceKey: 'reflect', sourceName: '反伤波及', attackerName: target.name, totalHits: 0, evadedHits: 0, parriedHits: 0, totalDamage: 0 }
        this.damageTakenSources.set('reflect', taken)
      }
      taken.totalHits++
      taken.totalDamage += reflectDamage
    }
  }

  public recordHealing(_caster: SimCombatant, target: SimCombatant, skill: SimSkill, amount: number): void {
    if (target.isAlly && amount > 0) {
      let rec = this.hpRegenSkill.get(skill.abilityId)
      if (!rec) {
        rec = { abilityId: skill.abilityId, name: skill.name, amount: 0 }
        this.hpRegenSkill.set(skill.abilityId, rec)
      }
      rec.amount += amount
    }
  }

  public recordNaturalRegen(unit: SimCombatant, hp: number, mp: number): void {
    if (unit.isAlly) {
      if (hp > 0) this.hpRegenNatural += hp
      if (mp > 0) this.mpRegenNatural += mp
    }
  }

  public recordManaCast(unit: SimCombatant, skill: SimSkill, manaCost: number): void {
    if (unit.isAlly && manaCost > 0) {
      let item = this.skillManaSpent.get(skill.abilityId)
      if (!item) {
        item = { abilityId: skill.abilityId, name: skill.name, totalSpent: 0, casts: 0 }
        this.skillManaSpent.set(skill.abilityId, item)
      }
      item.totalSpent += manaCost
      item.casts++
    }
  }

  public markManaExhausted(): void {
    this.isManaExhausted = true
  }
}

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
  public definitionId: string

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
  public consumables: AutoUseSlot[] = []
  public consumableTriggers?: Record<string, CombatTrigger[]>
  public consumableCooldowns: number[] = []

  constructor(
    id: string,
    name: string,
    isAlly: boolean,
    memberIndex: number,
    stats: CombatAttributes,
    skills: SimSkill[],
    combatExperience = 0,
    definitionId?: string,
    consumables?: AutoUseSlot[],
    consumableTriggers?: Record<string, CombatTrigger[]>
  ) {
    this.id = id
    this.name = name
    this.isAlly = isAlly
    this.memberIndex = memberIndex
    this.stats = ensureCombatAttributes(stats)
    this.skills = skills
    this.hitpoints = Math.max(1, this.stats.maxHitpoints)
    this.manapoints = Math.max(0, this.stats.maxManapoints)
    this.combatExperience = combatExperience
    this.skillCooldowns = new Array(skills.length).fill(0)
    this.definitionId = definitionId || id
    this.consumables = consumables || []
    this.consumableTriggers = consumableTriggers
    this.consumableCooldowns = new Array(this.consumables.length).fill(0)
  }

  /**
   * 新波次准备：复活或继承状态，重新初始化普攻读条
   */
  public prepareForWave(isRespawn: boolean): void {
    if (isRespawn) {
      this.hitpoints = this.maxHitpoints
      this.manapoints = this.maxManapoints
      this.isAlive = true
      this.buffs = []
      this.skillCooldowns = new Array(this.skills.length).fill(0)
      this.consumableCooldowns = new Array(this.consumables.length).fill(0)
    } else {
    }
    this.pendingSkillIndex = -1
    const baseInterval = (this.stats.attackIntervalMilliseconds || 1000) / 1000
    this.nextAttackAtSeconds = Math.max(
      0.05,
      baseInterval *
        percentMultiplier(this.stats.attackSpeedPoints + this.buffAttackSpeedPoints, 'non-linear') *
        percentMultiplier(this.stats.actionSpeedPoints, 'non-linear')
    )
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
  public collector: CombatStatsCollector | null = null
  public logEvents: SimCombatLogEvent[] | null = null

  private static readonly TICK_SECONDS = 5.0

  constructor(
    seed: bigint,
    allies: SimCombatant[],
    enemies: SimCombatant[],
    playerRespawnSeconds = 10,
    tracker: ManaDeficitTracker | null = null,
    collector: CombatStatsCollector | null = null,
    logEvents: SimCombatLogEvent[] | null = null
  ) {
    this.seed = seed
    this.allies = allies
    this.enemies = enemies
    this.playerRespawnSeconds = playerRespawnSeconds
    this.tracker = tracker
    this.collector = collector
    this.logEvents = logEvents

    this.decideInitialActions()
  }

  private appendLog(event: SimCombatLogEvent): void {
    if (this.logEvents) {
      this.logEvents.push(event)
    }
  }
  /**
   * 开战决策首次出手动作
   */
  public decideInitialActions(): void {
    // 敌方首次出手决策
    for (let i = 0; i < this.enemies.length; i++) {
      const enemy = this.enemies[i]
      if (enemy.isAlive) {
        enemy.prepareForWave(false)
        const target = this.selectEnemyTarget(enemy, this.allies, 19n)
        this.decideNextAction(enemy, this.enemies, this.allies, target)
      }
    }

    // 盟友首次出手决策
    for (let i = 0; i < this.allies.length; i++) {
      const ally = this.allies[i]
      if (ally.isAlive) {
        ally.prepareForWave(false)
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
              this.tracker.cancelPendingDeficit(slot.slotIndex, this.elapsedSeconds)
            }
            continue
          }
        }

        // 灵力判定与缺蓝追踪
        if (unit.manapoints < slot.manaCost) {
          if (unit.isAlly && unit.memberIndex === 0 && this.tracker) {
            this.tracker.recordDeficit(slot.slotIndex, this.elapsedSeconds)
          }
          continue
        }

        // 灵力满足，扣减灵力进入施法
        unit.manapoints -= slot.manaCost
        if (unit.isAlly && unit.memberIndex === 0 && this.tracker) {
          this.tracker.recordCast(slot.slotIndex, this.elapsedSeconds, slot.manaCost)
        }
        if (this.collector) {
          this.collector.recordManaCast(unit, slot, slot.manaCost)
          if (unit.manapoints <= 1e-6 && unit.isAlly && unit.memberIndex === 0) {
            this.collector.markManaExhausted()
          }
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


    // 1.1 自动消耗品相位（回血、回蓝）
    this.applyConsumables(nextSeconds)
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
      this.appendLog({
        timestamp: this.elapsedSeconds,
        actorName: '战报',
        isAlly: true,
        actionType: 'victory',
        message: `[${this.elapsedSeconds.toFixed(1)}s] 战斗胜利！妖兽全灭。`,
      })
    } else if (!anyAllyAlive) {
      this.outcome = 'defeat'
      if (this.tracker) this.tracker.finalize(this.elapsedSeconds)
      this.appendLog({
        timestamp: this.elapsedSeconds,
        actorName: '战报',
        isAlly: false,
        actionType: 'defeat',
        message: `[${this.elapsedSeconds.toFixed(1)}s] 全员力竭倒地，战斗失败！`,
      })
    }
  }

  /**
   * 战斗中自动消耗品判定与生效（药品栏与战斗消耗）
   */
  private applyConsumables(nowSeconds: number): void {
    for (const ally of this.allies) {
      if (!ally.isAlive || !ally.consumables || ally.consumables.length === 0) continue
      for (let sIdx = 0; sIdx < ally.consumables.length; sIdx++) {
        const slot = ally.consumables[sIdx]
        if (!slot || !slot.enabled || !slot.itemId) continue
        if ((ally.consumableCooldowns[sIdx] || 0) > nowSeconds) continue

        const itemId = slot.itemId.toLowerCase()
        const isHealing = itemId.includes('ningxue') || itemId.includes('blood') || itemId.includes('heal') || itemId.includes('wound')
        const isMana = itemId.includes('qi') || itemId.includes('mana') || itemId.includes('spirit')

        // 触发条件求值：若已配置独立触发器（含空数组表示无条件就绪即用），按条件严格求值；未配置则保持默认健康度阈值
        const triggers = ally.consumableTriggers?.[slot.itemId]
        if (triggers !== undefined && triggers !== null) {
          if (triggers.length > 0) {
            const primaryEnemy = this.enemies.find(o => o.isAlive) ?? null
            if (!SimSkillTriggerEvaluator.matchesAll(triggers, ally, primaryEnemy, this.allies, this.enemies)) {
              continue
            }
          }
        } else {
          if (isHealing && ally.hitpoints >= ally.maxHitpoints * 0.6) continue
          if (isMana && ally.manapoints >= ally.maxManapoints * 0.4) continue
        }

        if (isHealing) {
          const healAmount = Math.min(ally.maxHitpoints - ally.hitpoints, Math.max(50, ally.maxHitpoints * 0.3))
          ally.hitpoints += healAmount
          ally.consumableCooldowns[sIdx] = nowSeconds + 10
          if (this.collector) {
            this.collector.recordNaturalRegen(ally, healAmount, 0)
          }
          this.appendLog({
            timestamp: nowSeconds,
            actorName: ally.name,
            isAlly: true,
            actionType: 'heal',
            abilityName: '战斗丹药',
            healing: healAmount,
            message: `[${nowSeconds.toFixed(1)}s] 自动消耗品：${ally.name} 使用药品恢复 ${healAmount.toFixed(0)} 点气血 [当前 ${ally.hitpoints.toFixed(0)}/${ally.maxHitpoints.toFixed(0)}]`,
          })
        } else if (isMana && ally.manapoints < ally.maxManapoints * 0.4) {
          const manaAmount = Math.min(ally.maxManapoints - ally.manapoints, Math.max(30, ally.maxManapoints * 0.35))
          ally.manapoints += manaAmount
          ally.consumableCooldowns[sIdx] = nowSeconds + 10
          if (this.collector) {
            this.collector.recordNaturalRegen(ally, 0, manaAmount)
          }
          this.appendLog({
            timestamp: nowSeconds,
            actorName: ally.name,
            isAlly: true,
            actionType: 'heal',
            abilityName: '战斗灵散',
            message: `[${nowSeconds.toFixed(1)}s] 自动消耗品：${ally.name} 使用灵散补充 ${manaAmount.toFixed(0)} 点法力 [当前 ${ally.manapoints.toFixed(0)}/${ally.maxManapoints.toFixed(0)}]`,
          })
        }
      }
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
    const res = this.calculateDamage(attacker, target, null, side)
    if (this.collector) {
      this.collector.recordHit(attacker, target, null, res)
    }

    if (!res.hit) {
      this.appendLog({
        timestamp: this.elapsedSeconds,
        actorName: attacker.name,
        isAlly: attacker.isAlly,
        targetName: target.name,
        actionType: 'attack',
        isMiss: true,
        message: `[${this.elapsedSeconds.toFixed(1)}s] ${attacker.name} 对 ${target.name} 发动普攻，被闪避！`,
      })
      return
    }

    const damage = res.damage
    const critTag = res.critical ? ' (暴击)' : ''
    const parryTag = res.parried ? ' (化解)' : ''
    const breakTag = res.broken ? ' (破招)' : ''

    target.hitpoints = Math.max(0, target.hitpoints - damage)
    this.appendLog({
      timestamp: this.elapsedSeconds,
      actorName: attacker.name,
      isAlly: attacker.isAlly,
      targetName: target.name,
      actionType: 'attack',
      damage,
      isCritical: res.critical,
      isBreak: res.broken,
      isParry: res.parried,
      message: `[${this.elapsedSeconds.toFixed(1)}s] ${attacker.name} 对 ${target.name} 发动普攻，造成 ${damage.toFixed(0)} 点伤害${critTag}${parryTag}${breakTag} [${target.name}剩余气血 ${target.hitpoints.toFixed(0)}/${target.maxHitpoints.toFixed(0)}]`,
    })

    if (target.hitpoints <= 0) {
      target.isAlive = false
      this.appendLog({
        timestamp: this.elapsedSeconds,
        actorName: target.name,
        isAlly: target.isAlly,
        actionType: 'defeat',
        message: `[${this.elapsedSeconds.toFixed(1)}s] ${target.name} 气血耗尽倒地！`,
      })
    }

    // 汲取吸血/吸蓝
    if (attacker.stats.hpDrainPoints > 0) {
      const drainHp = damage * (attacker.stats.hpDrainPoints / 100)
      attacker.hitpoints = Math.min(attacker.stats.maxHitpoints, attacker.hitpoints + drainHp)
      if (this.collector) this.collector.recordDrain(attacker, drainHp, 0)
    }
    if (attacker.stats.mpDrainPoints > 0) {
      const drainMp = damage * (attacker.stats.mpDrainPoints / 100)
      attacker.manapoints = Math.min(attacker.stats.maxManapoints, attacker.manapoints + drainMp)
      if (this.collector) this.collector.recordDrain(attacker, 0, drainMp)
    }

    // 伤害反弹
    if (target.stats.damageReflectPoints > 0 && attacker.isAlive) {
      const reflectDamage = damage * (target.stats.damageReflectPoints / 100)
      attacker.hitpoints = Math.max(0, attacker.hitpoints - reflectDamage)
      if (this.collector) this.collector.recordReflect(target, attacker, reflectDamage)
      if (attacker.hitpoints <= 0) {
        attacker.isAlive = false
        this.appendLog({
          timestamp: this.elapsedSeconds,
          actorName: attacker.name,
          isAlly: attacker.isAlly,
          actionType: 'defeat',
          message: `[${this.elapsedSeconds.toFixed(1)}s] ${attacker.name} 受到反伤致死倒地！`,
        })
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
        const actualHeal = Math.min(target.stats.maxHitpoints - target.hitpoints, healAmount)
        target.hitpoints = Math.min(target.stats.maxHitpoints, target.hitpoints + healAmount)
        if (this.collector) this.collector.recordHealing(caster, target, skill, actualHeal)
        this.appendLog({
          timestamp: this.elapsedSeconds,
          actorName: caster.name,
          isAlly: caster.isAlly,
          targetName: target.name,
          actionType: 'heal',
          abilityName: skill.name,
          healing: actualHeal,
          message: `[${this.elapsedSeconds.toFixed(1)}s] ${caster.name} 施展【${skill.name}】为 ${target.name} 恢复 ${actualHeal.toFixed(0)} 点气血 [气血 ${target.hitpoints.toFixed(0)}/${target.maxHitpoints.toFixed(0)}]`,
        })
      }
    } else {
      // 伤害技能
      const targets = this.resolveTargets(caster, allies, opponents, skill.targetType, side)
      for (const target of targets) {
        if (!target.isAlive) continue
        caster.attackCount++
        const res = this.calculateDamage(caster, target, skill, side)
        if (this.collector) this.collector.recordHit(caster, target, skill, res)

        if (!res.hit) {
          this.appendLog({
            timestamp: this.elapsedSeconds,
            actorName: caster.name,
            isAlly: caster.isAlly,
            targetName: target.name,
            actionType: 'skill',
            abilityName: skill.name,
            isMiss: true,
            message: `[${this.elapsedSeconds.toFixed(1)}s] ${caster.name} 施展【${skill.name}】对 ${target.name}，被闪避！`,
          })
          continue
        }

        const damage = res.damage
        const critTag = res.critical ? ' (暴击)' : ''
        const parryTag = res.parried ? ' (化解)' : ''
        const breakTag = res.broken ? ' (破招)' : ''

        target.hitpoints = Math.max(0, target.hitpoints - damage)
        this.appendLog({
          timestamp: this.elapsedSeconds,
          actorName: caster.name,
          isAlly: caster.isAlly,
          targetName: target.name,
          actionType: 'skill',
          abilityName: skill.name,
          damage,
          isCritical: res.critical,
          isBreak: res.broken,
          isParry: res.parried,
          message: `[${this.elapsedSeconds.toFixed(1)}s] ${caster.name} 施展【${skill.name}】对 ${target.name}，造成 ${damage.toFixed(0)} 点${skill.element !== 'none' ? skill.element + '系' : ''}术法伤害${critTag}${parryTag}${breakTag} [${target.name}剩余气血 ${target.hitpoints.toFixed(0)}/${target.maxHitpoints.toFixed(0)}]`,
        })

        if (target.hitpoints <= 0) {
          target.isAlive = false
          this.appendLog({
            timestamp: this.elapsedSeconds,
            actorName: target.name,
            isAlly: target.isAlly,
            actionType: 'defeat',
            message: `[${this.elapsedSeconds.toFixed(1)}s] ${target.name} 气血耗尽倒地！`,
          })
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
  ): SimHitResult {
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
    if (!hit) {
      return { hit: false, damage: 0, critical: false, broken, parried }
    }

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

    if (rawDamage <= 0) {
      return { hit: true, damage: 0, critical, broken, parried }
    }

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

    const finalDamage = Math.max(0, damage + fixedBonus - fixedReduction)
    return { hit: true, damage: finalDamage, critical, broken, parried }
  }

  /**
   * 5秒周期回复与 Buff 到期更新
   */
  private applyBuffTick(atSeconds: number): void {
    const isRegenTick = Math.abs(atSeconds / SimCombatEncounter.TICK_SECONDS - Math.round(atSeconds / SimCombatEncounter.TICK_SECONDS)) <= 1e-9

    const tickUnit = (unit: SimCombatant): void => {
      // 先按 5 秒网格结算周期效果，再剔除到期 Buff——与服务端 CombatPhaseSystems.Apply 同序。
      // 到期时刻本身属于 Buff 生效区间，先剔除会让到期点恰好落在网格上时整跳丢失。
      if (!isRegenTick) {
        unit.buffs = unit.buffs.filter(b => b.expiresAtSeconds > atSeconds + 1e-9)
        return
      }

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
        const actualHp = Math.min(unit.stats.maxHitpoints - unit.hitpoints, hp)
        unit.hitpoints = Math.min(unit.stats.maxHitpoints, Math.max(0, unit.hitpoints + hp))
        if (this.collector) this.collector.recordNaturalRegen(unit, actualHp, 0)
        if (actualHp > 0) {
          this.appendLog({
            timestamp: atSeconds,
            actorName: unit.name,
            isAlly: unit.isAlly,
            actionType: 'natural_regen',
            healing: actualHp,
            message: `[${atSeconds.toFixed(1)}s] 5秒自然回复：${unit.name} 恢复 ${actualHp.toFixed(0)} 点气血 [当前 ${unit.hitpoints.toFixed(0)}/${unit.maxHitpoints.toFixed(0)}]`,
          })
        }
      }
      if (mp !== 0) {
        const actualMp = Math.min(unit.stats.maxManapoints - unit.manapoints, mp)
        unit.manapoints = Math.min(unit.stats.maxManapoints, Math.max(0, unit.manapoints + mp))
        if (this.collector) this.collector.recordNaturalRegen(unit, 0, actualMp)
      }
      if (unit.manapoints <= 1e-6 && this.collector && unit.isAlly && unit.memberIndex === 0) {
        this.collector.markManaExhausted()
      }
      // 网格边界已按完整效果结算完毕，再剔除本边界到期 Buff。
      unit.buffs = unit.buffs.filter(b => b.expiresAtSeconds > atSeconds + 1e-9)
    }

    this.allies.forEach(tickUnit)
    this.enemies.forEach(tickUnit)
  }
}
