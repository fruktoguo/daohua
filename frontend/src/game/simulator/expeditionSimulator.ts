import type { CombatAbilityDefinition, CombatAttributes, CombatEncounterDefinition, ItemDefinition } from '../types'
import { nextSeed, rollDropTable, rollMillionth } from './deterministicRng'
import { ManaDeficitTracker } from './manaDeficitTracker'
import { SimCombatEncounter, SimCombatant, type SimSkill } from './combatSimulatorCore'
import type { SimulatorConfig, SimulatorItemYield, SimulatorReport } from './simulatorTypes'
interface RawEnemySpec {
  enemyId: string
  name: string
  rank: string
  level: number
  tier: number
  maxHitpoints: number
  physicalAttack: number
  spellAttack?: number
  armor: number
  magicResistance?: number
  experience: number
  drops: Array<{
    itemId: string
    dropChance: number
    minQuantity?: number
    maxQuantity?: number
  }>
}


/**
 * 远征多波次与挂机模拟调度器
 */
export class ExpeditionSimulator {
  /**
   * 运行完整的离线战斗推演
   */
  public static runSimulation(
    config: SimulatorConfig,
    catalog: {
      encounters: readonly CombatEncounterDefinition[]
      abilities: readonly CombatAbilityDefinition[]
      items: readonly ItemDefinition[]
    }
  ): SimulatorReport {
    const totalTargetSeconds = Math.max(1, config.durationSeconds)
    let currentElapsedSeconds = 0

    let searchSeconds = 0
    let combatActiveSeconds = 0
    let respawnSeconds = 0

    let totalEncounters = 0
    let victories = 0
    let defeats = 0
    let maxWave = 1
    let normalKills = 0
    let eliteKills = 0
    let bossKills = 0

    const itemDrops = new Map<string, number>()
    let totalExperience = 0
    let totalCombatExperience = 0

    let seed = config.seed || 123456789n

    // 解析当前地图节点的遭遇定义
    const encounter = this.resolveEncounter(config, catalog.encounters)

    // 创建技能目录字典便于检索
    const abilityMap = new Map<string, CombatAbilityDefinition>()
    for (const a of catalog.abilities) abilityMap.set(a.abilityId, a)

    // 初始化玩家自身的灵力追踪器
    const selfConfig = config.allies.find(a => a.isSelf) || config.allies[0]
    const selfSlots = (selfConfig?.abilitySlots || [])
      .filter(slot => typeof slot.abilityId === 'string' && slot.abilityId.length > 0)
      .map(slot => {
        const abilityId = slot.abilityId as string
        const def = abilityMap.get(abilityId)
        return {
          abilityId,
          name: def?.name || abilityId,
          manaCost: def?.manaCost || 0,
        }
      })
    const manaTracker = new ManaDeficitTracker(selfSlots, selfConfig?.attributes.maxManapoints || 100)
    // 构建盟友战斗单位模板列表
    const buildAllies = (): SimCombatant[] => {
      return config.allies.map((ally, index) => {
        const skills: SimSkill[] = (ally.abilitySlots || [])
          .map((slot, slotIndex) => ({ slot, slotIndex }))
          .filter(({ slot, slotIndex }) => slotIndex > 0 && typeof slot.abilityId === 'string' && slot.abilityId.length > 0)
          .map(({ slot, slotIndex }) => {
            const abilityId = slot.abilityId as string
            const def = abilityMap.get(abilityId)
            const triggers = ally.abilityTriggers?.[abilityId] || []
            return {
              slotIndex,
              abilityId,
              name: def?.name || abilityId,
              level: slot.level || 1,
              category: (def?.category as 'physical' | 'spell') || 'physical',
              element: (def?.element ?? 'none') as 'none' | 'metal' | 'wood' | 'water' | 'fire' | 'earth',
              damageRatio: def?.damageRatio || 0,
              flatDamage: def?.flatDamage || 0,
              healingRatio: def?.healingRatio || 0,
              flatHealing: def?.flatHealing || 0,
              targetType: def?.targetType || 'targetEnemy',
              castMilliseconds: def?.castMilliseconds || 1000,
              cooldownMilliseconds: def?.cooldownMilliseconds || 0,
              manaCost: def?.manaCost || 0,
              triggers,
            }
          })
        return new SimCombatant(
          ally.id,
          ally.name,
          true,
          index,
          { ...ally.attributes },
          skills
        )
      })
    }

    let activeAllies = buildAllies()
    let waveNumber = 1

    // 循环推进直到达到指定总模拟时长
    let loopGuard = 0
    while (currentElapsedSeconds < totalTargetSeconds) {
      if (++loopGuard > 50_000) break

      // 1. 搜寻读条阶段 (Search Phase)
      const searchDuration = Math.min(
        totalTargetSeconds - currentElapsedSeconds,
        3.0
      )
      currentElapsedSeconds += searchDuration
      searchSeconds += searchDuration
      if (currentElapsedSeconds >= totalTargetSeconds) break

      // 2. 生成本波怪物阵容
      const waveResult = this.generateWave(encounter, seed, waveNumber)
      seed = waveResult.nextSeed
      const enemies = waveResult.enemies

      totalEncounters++
      if (waveNumber > maxWave) maxWave = waveNumber

      // 3. 战斗推进阶段 (Combat Phase)
      const combatMaxSeconds = totalTargetSeconds - currentElapsedSeconds
      const combat = new SimCombatEncounter(
        seed,
        activeAllies,
        enemies,
        (encounter.playerRespawnMilliseconds ?? 10000) / 1000,
        manaTracker
      )

      const outcome = combat.advanceTo(combatMaxSeconds)
      const battleDuration = combat.elapsedSeconds
      currentElapsedSeconds += battleDuration
      combatActiveSeconds += battleDuration

      if (outcome === 'victory') {
        victories++
        // 击杀与战利品结算
        for (const enemyDef of waveResult.rawEnemies) {
          if (enemyDef.rank === 'boss') bossKills++
          else if (enemyDef.rank === 'elite') eliteKills++
          else normalKills++

          // 修为与战斗经验
          const exp = (enemyDef.experience || 10) / Math.max(1, config.allies.length)
          totalExperience += exp
          totalCombatExperience += exp

          // 掉落抽取
          for (const drop of enemyDef.drops || []) {
            if (!drop.itemId || drop.dropChance <= 0) continue
            const dropRes = rollDropTable(seed, drop.minQuantity || 1, drop.maxQuantity || 1)
            seed = dropRes.nextSeed
            if (dropRes.chanceRoll <= drop.dropChance) {
              const current = itemDrops.get(drop.itemId) || 0
              itemDrops.set(drop.itemId, current + dropRes.quantity)
            }
          }
        }

        waveNumber++
        seed = nextSeed(seed)
      } else if (outcome === 'defeat') {
        defeats++
        // 团灭复活读条阶段
        const respawnDuration = Math.min(
          totalTargetSeconds - currentElapsedSeconds,
          (encounter.playerRespawnMilliseconds ?? 10000) / 1000
        )
        currentElapsedSeconds += respawnDuration
        respawnSeconds += respawnDuration

        // 复活重置盟友队伍
        activeAllies = buildAllies()
        waveNumber = 1
        seed = nextSeed(seed)
      } else {
        // 时间耗尽中断
        break
      }
    }

    manaTracker.finalize(combatActiveSeconds)

    // 构建最终输出报表
    const totalHours = Math.max(1e-6, currentElapsedSeconds / 3600)
    const itemMap = new Map<string, ItemDefinition>()
    for (const it of catalog.items) itemMap.set(it.itemId, it)

    const itemYields: SimulatorItemYield[] = []
    for (const [itemId, quantity] of itemDrops.entries()) {
      const def = itemMap.get(itemId)
      itemYields.push({
        itemId,
        name: def?.name || itemId,
        quantity,
        perHour: Number((quantity / totalHours).toFixed(2)),
      })
    }
    itemYields.sort((a, b) => b.quantity - a.quantity)

    const totalKills = normalKills + eliteKills + bossKills
    const winRate = totalEncounters > 0 ? victories / totalEncounters : 0

    return {
      totalSimulatedSeconds: currentElapsedSeconds,
      combatActiveSeconds,
      searchSeconds,
      respawnSeconds,
      totalEncounters,
      victories,
      defeats,
      winRate,
      maxWave,
      normalKills,
      eliteKills,
      bossKills,
      totalKills,
      itemYields,
      totalExperience: Math.floor(totalExperience),
      experiencePerHour: Math.floor(totalExperience / totalHours),
      totalCombatExperience: Math.floor(totalCombatExperience),
      combatExperiencePerHour: Math.floor(totalCombatExperience / totalHours),
      manaDeficit: manaTracker.generateReport(),
    }
  }

  private static resolveEncounter(
    config: SimulatorConfig,
    encounters: readonly CombatEncounterDefinition[]
  ): CombatEncounterDefinition {
    if (config.encounterId) {
      const found = encounters.find(e => e.encounterId === config.encounterId)
      if (found) return found
    }
    // 默认回落到第一个遭遇
    return encounters[0] || ({
      encounterId: 'default',
      name: config.nodeName || '默认野外遭遇',
      minBudget: 30,
      maxBudget: 60,
      battlesPerElite: 5,
      battlesPerBoss: 20,
      searchDurationSeconds: 3,
      playerRespawnSeconds: 10,
      randomSpawns: [],
      enemy: {
        enemyId: 'monster-default',
        name: '野外妖兽',
        rank: 'normal',
        level: 1,
        tier: 1,
        maxHitpoints: 100,
        physicalAttack: 10,
        armor: 5,
        budgetCost: 15,
        drops: [],
      },
    } as unknown as CombatEncounterDefinition)
  }

  /**
   * 按预算轮盘与波次规则抽取怪物阵容（与 EncounterWaveSelector 一致）
   */
  private static generateWave(
    encounter: CombatEncounterDefinition,
    seed: bigint,
    waveNumber: number
  ): {
    enemies: SimCombatant[]
    rawEnemies: RawEnemySpec[]
    nextSeed: bigint
  } {
    const bossInterval = encounter.battlesPerBoss && encounter.battlesPerBoss > 0 ? encounter.battlesPerBoss : 20
    const eliteInterval = encounter.battlesPerElite && encounter.battlesPerElite > 0 ? encounter.battlesPerElite : 5
    const isBoss = waveNumber % bossInterval === 0
    const isElite = !isBoss && waveNumber % eliteInterval === 0

    const rawEnemies: RawEnemySpec[] = []
    let currentSeed = seed

    // 如果是特殊波次优先加入特殊怪
    if (isBoss && encounter.bossEnemies && encounter.bossEnemies.length > 0) {
      const { roll, nextSeed: s1 } = rollMillionth(currentSeed)
      currentSeed = s1
      const boss = encounter.bossEnemies[Math.floor(roll * encounter.bossEnemies.length)]
      rawEnemies.push({
        enemyId: boss.enemyId,
        name: boss.name,
        rank: 'boss',
        level: boss.level || 5,
        tier: boss.tier || 2,
        maxHitpoints: boss.attributes?.maxHitpoints || 1500,
        physicalAttack: boss.attributes?.physicalAttack || 60,
        spellAttack: boss.attributes?.spellAttack || 30,
        armor: boss.attributes?.armor || 30,
        magicResistance: boss.attributes?.magicResistance || 15,
        experience: boss.experience || 150,
        drops: (boss.drops || []).map(d => ({
          itemId: d.itemId,
          dropChance: d.dropChance,
          minQuantity: d.minQuantity,
          maxQuantity: d.maxQuantity,
        })),
      })
    } else if (isElite && encounter.eliteEnemies && encounter.eliteEnemies.length > 0) {
      const { roll, nextSeed: s1 } = rollMillionth(currentSeed)
      currentSeed = s1
      const elite = encounter.eliteEnemies[Math.floor(roll * encounter.eliteEnemies.length)]
      rawEnemies.push({
        enemyId: elite.enemyId,
        name: elite.name,
        rank: 'elite',
        level: elite.level || 3,
        tier: elite.tier || 1,
        maxHitpoints: elite.attributes?.maxHitpoints || 400,
        physicalAttack: elite.attributes?.physicalAttack || 25,
        spellAttack: elite.attributes?.spellAttack || 15,
        armor: elite.attributes?.armor || 15,
        magicResistance: elite.attributes?.magicResistance || 10,
        experience: elite.experience || 50,
        drops: (elite.drops || []).map(d => ({
          itemId: d.itemId,
          dropChance: d.dropChance,
          minQuantity: d.minQuantity,
          maxQuantity: d.maxQuantity,
        })),
      })
    }

    // 补充普通小怪护从至最多 3 只
    const spawns = encounter.randomSpawns && encounter.randomSpawns.length > 0
      ? encounter.randomSpawns
      : [{ enemy: encounter.enemy, weight: 1, strength: 1 }]

    while (rawEnemies.length < 3) {
      const { roll, nextSeed: s2 } = rollMillionth(currentSeed)
      currentSeed = s2
      const spawn = spawns[Math.floor(roll * spawns.length)]
      const enemy = spawn?.enemy || encounter.enemy
      rawEnemies.push({
        enemyId: enemy?.enemyId || 'monster',
        name: enemy?.name || '妖兽',
        rank: enemy?.rank || 'normal',
        level: enemy?.level || 1,
        tier: enemy?.tier || 1,
        maxHitpoints: enemy?.attributes?.maxHitpoints || 120,
        physicalAttack: enemy?.attributes?.physicalAttack || 12,
        spellAttack: enemy?.attributes?.spellAttack || 5,
        armor: enemy?.attributes?.armor || 8,
        magicResistance: enemy?.attributes?.magicResistance || 4,
        experience: enemy?.experience || 15,
        drops: (enemy?.drops || []).map(d => ({
          itemId: d.itemId,
          dropChance: d.dropChance,
          minQuantity: d.minQuantity,
          maxQuantity: d.maxQuantity,
        })),
      })
    }

    // 转换为 SimCombatant 实体
    const enemies = rawEnemies.map((e, index) => {
      const stats: CombatAttributes = {
        maxHitpoints: e.maxHitpoints,
        maxManapoints: 50,
        physicalAttack: e.physicalAttack,
        spellAttack: e.spellAttack || 5,
        armor: e.armor,
        magicResistance: e.magicResistance || 0,
        accuracy: 10,
        evasion: 5,
        critical: 5,
        criticalResistance: 5,
        criticalDamagePoints: 50,
        breakPower: 5,
        parryResolve: 5,
        hpRegenPerFiveSeconds: 2,
        mpRegenPerFiveSeconds: 1,
        attackIntervalMilliseconds: 1200,
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
        hpDrainPoints: 0,
        mpDrainPoints: 0,
        damageReflectPoints: 0,
        healingBonusPoints: 0,
      }

      return new SimCombatant(
        `${e.enemyId}_${index}`,
        e.name,
        false,
        index,
        stats,
        []
      )
    })

    return {
      enemies,
      rawEnemies,
      nextSeed: currentSeed,
    }
  }
}
