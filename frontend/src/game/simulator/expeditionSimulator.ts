import type { CombatAbilityDefinition, CombatAbilitySlot, CombatAttributes, CombatEncounterDefinition, CombatEquipmentDefinition, ItemDefinition, MarketItemStats, WorldMapDefinition } from '../types.ts'
function resolveAssetLabel(id: string, items?: readonly ItemDefinition[] | null): string {
  if (Array.isArray(items)) {
    const found = items.find(i => i.itemId === id)
    if (found?.name) return found.name
  }
  return id
}

function resolveItemUnitPrice(
  itemId: string,
  enhancementLevel: number = 0,
  items?: readonly ItemDefinition[] | null,
  marketStats?: Record<string, MarketItemStats> | null
): number {
  if (marketStats) {
    const key = `${itemId}|${enhancementLevel}`
    const stats = marketStats[key]
    if (stats?.referencePrice && stats.referencePrice > 0) return stats.referencePrice
    if (enhancementLevel > 0) {
      const baseStats = marketStats[`${itemId}|0`]
      if (baseStats?.referencePrice && baseStats.referencePrice > 0) return baseStats.referencePrice
    }
  }
  if (Array.isArray(items)) {
    const item = items.find(i => i.itemId === itemId)
    if (item?.sellPrice && item.sellPrice > 0) return item.sellPrice
  }
  return 0
}
import { nextSeed, rollDropTable, rollMillionth } from './deterministicRng.ts'
import { ManaDeficitTracker } from './manaDeficitTracker.ts'
import {
  SimCombatEncounter,
  SimCombatant,
  type SimSkill,
  CombatStatsCollector,
  ensureCombatAttributes,
} from './combatSimulatorCore.ts'
import type {
  SimulatorConfig,
  SimulatorItemYield,
  SimulatorReport,
  MonsterKillStat,
  DamageSourceStat,
  TargetDamageBreakdown,
  DamageTakenSourceStat,
  AttackerDamageTakenBreakdown,
  RecoveryStats,
  RegenSourceStat,
  SkillManaConsumptionStat,
  SimBattleLogRecord,
  SimCombatLogEvent,
} from './simulatorTypes.ts'

interface RawEnemySpec {
  enemyId: string
  name: string
  rank: 'normal' | 'elite' | 'boss'
  level: number
  tier: number
  budgetCost: number
  strength: number
  attributes: CombatAttributes
  experience: number
  drops: Array<{
    itemId: string
    dropChance: number
    minQuantity?: number
    maxQuantity?: number
  }>
  skills?: CombatAbilitySlot[]
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
      maps?: readonly WorldMapDefinition[]
      equipment?: readonly CombatEquipmentDefinition[]
      marketStats?: Record<string, MarketItemStats>
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
    const monsterKillMap = new Map<string, MonsterKillStat>()
    const statsCollector = new CombatStatsCollector()
    const recentBattleLogs: SimBattleLogRecord[] = []

    let totalExperience = 0
    let totalCombatExperience = 0
    let totalCultivationExperience = 0
    let totalSixDimensionExperience = 0
    const sixDimensionBreakdown: Record<string, number> = {
      stamina: 0,
      meridians: 0,
      attack: 0,
      bone: 0,
      agility: 0,
      consciousness: 0,
    }

    let seed = config.seed || 123456789n

    // 解析当前地图节点的遭遇定义
    const encounter = this.resolveEncounter(config, catalog.encounters, catalog.maps)

    // 创建技能目录字典便于检索
    const abilityMap = new Map<string, CombatAbilityDefinition>()
    for (const a of catalog.abilities) abilityMap.set(a.abilityId, a)

    // 初始化玩家自身的主动技能灵力追踪器（严格排除 0 号槽位内功心法）
    const selfConfig = config.allies.find(a => a.isSelf) || config.allies[0]
    const selfSlots = (selfConfig?.abilitySlots || [])
      .map((slot, slotIndex) => ({ slot, slotIndex }))
      .filter(({ slot, slotIndex }) => slotIndex > 0 && typeof slot.abilityId === 'string' && slot.abilityId.length > 0)
      .map(({ slot, slotIndex }) => {
        const abilityId = slot.abilityId as string
        const def = abilityMap.get(abilityId)
        return {
          slotIndex,
          abilityId,
          name: def?.name || abilityId,
          manaCost: def?.manaCost || 0,
        }
      })

    const manaTracker = new ManaDeficitTracker(selfSlots, selfConfig?.attributes.maxManapoints || 100)

    // 构建盟友战斗单位模板列表
    const buildAllies = (): SimCombatant[] => {
      return config.allies.map((ally, index) => {
        const baseAttrs: CombatAttributes = { ...ally.attributes }
        if (ally.equipment && ally.equipment.length > 0 && catalog.equipment) {
          for (const eq of ally.equipment) {
            const eqDef = catalog.equipment.find(e => e.equipmentId === eq.itemId)
            if (!eqDef) continue
            const level = Math.max(0, Math.floor(eq.enhancementLevel || 0))
            const scale = 1 + level * 0.1
            if (eqDef.flatStats) {
              for (const [k, v] of Object.entries(eqDef.flatStats)) {
                if (typeof v === 'number' && Number.isFinite(v)) {
                  baseAttrs[k as keyof CombatAttributes] = (baseAttrs[k as keyof CombatAttributes] || 0) + v * scale
                }
              }
            }
            if (eqDef.baseStatRatios) {
              for (const [k, v] of Object.entries(eqDef.baseStatRatios)) {
                if (typeof v === 'number' && Number.isFinite(v)) {
                  baseAttrs[k as keyof CombatAttributes] = (baseAttrs[k as keyof CombatAttributes] || 0) + v * scale
                }
              }
            }
          }
        }

        const skills: SimSkill[] = (ally.abilitySlots || [])
          .map((slot, slotIndex) => ({ slot, slotIndex }))
          .filter(({ slot, slotIndex }) => slotIndex > 0 && typeof slot.abilityId === 'string' && slot.abilityId.length > 0)
          .map(({ slot, slotIndex }) => {
            const abilityId = slot.abilityId as string
            const def = abilityMap.get(abilityId)
            const triggers = ally.abilityTriggers?.[abilityId] || def?.defaultTriggers || []
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
          baseAttrs,
          skills,
          0,
          ally.id,
          ally.consumables
        )
      })
    }

    let activeAllies = buildAllies()
    let waveNumber = 1
    let loopGuard = 0

    while (currentElapsedSeconds < totalTargetSeconds) {
      if (++loopGuard > 50_000) break

      // 1. 搜寻读条阶段 (Search Phase)
      const searchDuration = Math.min(
        totalTargetSeconds - currentElapsedSeconds,
        (encounter.searchMilliseconds ? encounter.searchMilliseconds / 1000 : 3.0)
      )
      currentElapsedSeconds += searchDuration
      searchSeconds += searchDuration
      if (currentElapsedSeconds >= totalTargetSeconds) break

      // 战外自然回复与技能冷却推进 (对齐服务端 CompleteResourceRegeneration 与 AdvanceIdleSkillResources)
      const regenFactor = searchDuration / 5.0
      for (const ally of activeAllies) {
        if (ally.isAlive) {
          const hpGain = Math.max(0, ally.stats.hpRegenPerFiveSeconds * regenFactor)
          const mpGain = Math.max(0, ally.stats.mpRegenPerFiveSeconds * regenFactor)
          const actualHp = Math.min(ally.maxHitpoints - ally.hitpoints, hpGain)
          const actualMp = Math.min(ally.maxManapoints - ally.manapoints, mpGain)
          ally.hitpoints = Math.min(ally.maxHitpoints, ally.hitpoints + hpGain)
          ally.manapoints = Math.min(ally.maxManapoints, ally.manapoints + mpGain)
          statsCollector.recordNaturalRegen(ally, actualHp, actualMp)
        }
        for (let i = 0; i < ally.skillCooldowns.length; i++) {
          ally.skillCooldowns[i] = Math.max(0, ally.skillCooldowns[i] - searchDuration)
        }
      }

      // 2. 生成本波怪物阵容
      const waveResult = this.generateWave(encounter, seed, waveNumber, abilityMap)
      seed = waveResult.nextSeed
      const enemies = waveResult.enemies

      totalEncounters++
      if (waveNumber > maxWave) maxWave = waveNumber

      // 3. 战斗推进阶段
      const combatMaxSeconds = totalTargetSeconds - currentElapsedSeconds

      // 采样战斗日志：记录首场战斗、或接近尾声场次、或失败场次
      const shouldRecordLog = (
        totalEncounters === 1 ||
        currentElapsedSeconds + 300 >= totalTargetSeconds ||
        recentBattleLogs.length < 5
      )
      const currentBattleEvents: SimCombatLogEvent[] | null = shouldRecordLog ? [] : null

      const combat = new SimCombatEncounter(
        seed,
        activeAllies,
        enemies,
        (encounter.playerRespawnMilliseconds ?? 10000) / 1000,
        manaTracker,
        statsCollector,
        currentBattleEvents
      )

      const outcome = combat.advanceTo(combatMaxSeconds)
      const battleDuration = combat.elapsedSeconds
      currentElapsedSeconds += battleDuration
      combatActiveSeconds += battleDuration

      // 留存战斗日志
      if (currentBattleEvents && (outcome === 'defeat' || recentBattleLogs.length < 5 || currentElapsedSeconds >= totalTargetSeconds)) {
        recentBattleLogs.push({
          battleIndex: totalEncounters,
          waveNumber,
          encounterName: encounter.name || '妖兽遭遇',
          outcome: outcome === 'running' ? 'defeat' : outcome,
          durationSeconds: battleDuration,
          events: currentBattleEvents,
        })
        if (recentBattleLogs.length > 8) {
          recentBattleLogs.shift()
        }
      }

      if (outcome === 'victory') {
        victories++
        // 击杀与战利品结算
        for (const enemyDef of waveResult.rawEnemies) {
          if (enemyDef.rank === 'boss') bossKills++
          else if (enemyDef.rank === 'elite') eliteKills++
          else normalKills++

          // 记录每种怪物的击杀统计
          const enemyId = enemyDef.enemyId
          const existingKill = monsterKillMap.get(enemyId)
          if (existingKill) {
            existingKill.count++
          } else {
            monsterKillMap.set(enemyId, {
              enemyId,
              name: enemyDef.name,
              rank: enemyDef.rank,
              count: 1,
              perHour: 0,
            })
          }

          // 修为与战斗经验
          // 修为与六维经验结算（对齐 CombatActionRules.cs:1185, 1270）
          const rawExp = (enemyDef.experience || 10) / Math.max(1, config.allies.length)
          totalCombatExperience += rawExp

          const internalSlot = selfConfig?.abilitySlots?.[0]
          const internalDef = internalSlot ? abilityMap.get(internalSlot.abilityId as string) : null
          const isInternal = internalDef?.discipline === 'internal'
          const attrs = selfConfig?.attributes
          const candidateBonus = attrs && typeof attrs === 'object' && 'cultivationExpBonusPoints' in attrs
            ? attrs.cultivationExpBonusPoints
            : undefined
          const cultivationBonus = typeof candidateBonus === 'number' ? candidateBonus : 0
          const creditedCultivationExp = cultivationBonus !== 0 ? Math.max(0, rawExp * (1 + cultivationBonus / 100)) : rawExp
          totalCultivationExperience += creditedCultivationExp
          totalExperience += creditedCultivationExp

          const ROOT_ASPECTS = ['stamina', 'meridians', 'attack', 'bone', 'agility', 'consciousness'] as const
          let roundSixExp = 0
          if (!isInternal || !internalDef?.rootExpMultipliers) {
            roundSixExp = rawExp * 6
            for (const aspect of ROOT_ASPECTS) {
              sixDimensionBreakdown[aspect] = (sixDimensionBreakdown[aspect] || 0) + rawExp
            }
          } else {
            const mults = internalDef.rootExpMultipliers as Record<string, number>
            for (const aspect of ROOT_ASPECTS) {
              const mult = typeof mults[aspect] === 'number' ? mults[aspect] : 1.0
              const share = rawExp * mult
              sixDimensionBreakdown[aspect] = (sixDimensionBreakdown[aspect] || 0) + share
              roundSixExp += share
            }
          }
          totalSixDimensionExperience += roundSixExp

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

        // 胜战：将技能绝对冷却时间折算回跨波剩余秒数
        for (const ally of activeAllies) {
          for (let i = 0; i < ally.skillCooldowns.length; i++) {
            ally.skillCooldowns[i] = Math.max(0, ally.skillCooldowns[i] - combat.elapsedSeconds)
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

        // 复活重置盟友队伍满状态
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
    const activeSec = Math.max(1e-6, combatActiveSeconds)

    const itemYields: SimulatorItemYield[] = []
    let dailyDropsRevenue = 0
    for (const [itemId, quantity] of itemDrops.entries()) {
      const name = resolveAssetLabel(itemId, catalog.items)
      const perHour = Number((quantity / totalHours).toFixed(1))
      const perDay = Math.floor(perHour * 24)
      const unitPrice = resolveItemUnitPrice(itemId, 0, catalog.items, catalog.marketStats)
      const dailyRevenue = Math.floor(perDay * unitPrice)
      dailyDropsRevenue += dailyRevenue
      itemYields.push({
        itemId,
        name: name || itemId,
        quantity,
        perHour,
        perDay,
        unitPrice,
        dailyRevenue,
      })
    }
    itemYields.sort((a, b) => b.dailyRevenue - a.dailyRevenue || b.quantity - a.quantity)

    const monsterKills: MonsterKillStat[] = Array.from(monsterKillMap.values()).map(m => ({
      ...m,
      perHour: Number((m.count / totalHours).toFixed(1)),
    }))
    monsterKills.sort((a, b) => b.count - a.count)

    // 伤害剖析
    const totalDamage = Array.from(statsCollector.damageSources.values()).reduce((sum, s) => sum + s.totalDamage, 0)
    const damageSources: DamageSourceStat[] = Array.from(statsCollector.damageSources.values()).map(s => ({
      sourceKey: s.sourceKey,
      sourceName: s.sourceName,
      totalHits: s.totalHits,
      successfulHits: s.successfulHits,
      hitRate: s.totalHits > 0 ? Number(((s.successfulHits / s.totalHits) * 100).toFixed(1)) : 0,
      criticalHits: s.criticalHits,
      criticalRate: s.successfulHits > 0 ? Number(((s.criticalHits / s.successfulHits) * 100).toFixed(1)) : 0,
      totalDamage: Number(s.totalDamage.toFixed(0)),
      dps: Number((s.totalDamage / activeSec).toFixed(1)),
      percentage: totalDamage > 0 ? Number(((s.totalDamage / totalDamage) * 100).toFixed(1)) : 0,
    }))
    damageSources.sort((a, b) => b.totalDamage - a.totalDamage)

    const totalHitsAll = damageSources.reduce((sum, s) => sum + s.totalHits, 0)
    const totalSuccAll = damageSources.reduce((sum, s) => sum + s.successfulHits, 0)
    const totalCritAll = damageSources.reduce((sum, s) => sum + s.criticalHits, 0)
    const overallHitRate = totalHitsAll > 0 ? Number(((totalSuccAll / totalHitsAll) * 100).toFixed(1)) : 0
    const overallCriticalRate = totalSuccAll > 0 ? Number(((totalCritAll / totalSuccAll) * 100).toFixed(1)) : 0

    const targetBreakdown: TargetDamageBreakdown[] = Array.from(statsCollector.targetDamage.values()).map(t => {
      const sources: DamageSourceStat[] = Array.from(t.sources.values()).map(s => ({
        sourceKey: s.sourceKey,
        sourceName: s.sourceName,
        totalHits: s.totalHits,
        successfulHits: s.successfulHits,
        hitRate: s.totalHits > 0 ? Number(((s.successfulHits / s.totalHits) * 100).toFixed(1)) : 0,
        criticalHits: s.criticalHits,
        criticalRate: s.successfulHits > 0 ? Number(((s.criticalHits / s.successfulHits) * 100).toFixed(1)) : 0,
        totalDamage: Number(s.totalDamage.toFixed(0)),
        dps: Number((s.totalDamage / activeSec).toFixed(1)),
        percentage: t.totalDamage > 0 ? Number(((s.totalDamage / t.totalDamage) * 100).toFixed(1)) : 0,
      }))
      sources.sort((a, b) => b.totalDamage - a.totalDamage)
      return {
        targetId: t.targetId,
        targetName: t.targetName,
        totalDamage: Number(t.totalDamage.toFixed(0)),
        percentage: totalDamage > 0 ? Number(((t.totalDamage / totalDamage) * 100).toFixed(1)) : 0,
        dps: Number((t.totalDamage / activeSec).toFixed(1)),
        sources,
      }
    })
    targetBreakdown.sort((a, b) => b.totalDamage - a.totalDamage)

    // 承伤剖析
    const totalDamageTaken = Array.from(statsCollector.damageTakenSources.values()).reduce((sum, s) => sum + s.totalDamage, 0)
    const damageTakenSources: DamageTakenSourceStat[] = Array.from(statsCollector.damageTakenSources.values()).map(s => ({
      sourceKey: s.sourceKey,
      sourceName: s.sourceName,
      attackerName: s.attackerName,
      totalHits: s.totalHits,
      evadedHits: s.evadedHits,
      parriedHits: s.parriedHits,
      totalDamage: Number(s.totalDamage.toFixed(0)),
      dps: Number((s.totalDamage / activeSec).toFixed(1)),
      percentage: totalDamageTaken > 0 ? Number(((s.totalDamage / totalDamageTaken) * 100).toFixed(1)) : 0,
    }))
    damageTakenSources.sort((a, b) => b.totalDamage - a.totalDamage)

    const totalTakenHits = damageTakenSources.reduce((sum, s) => sum + s.totalHits, 0)
    const totalEvadedHits = damageTakenSources.reduce((sum, s) => sum + s.evadedHits, 0)
    const totalParriedHits = damageTakenSources.reduce((sum, s) => sum + s.parriedHits, 0)
    const overallEvasionRate = totalTakenHits > 0 ? Number(((totalEvadedHits / totalTakenHits) * 100).toFixed(1)) : 0
    const overallParryRate = totalTakenHits > 0 ? Number(((totalParriedHits / totalTakenHits) * 100).toFixed(1)) : 0

    const attackerBreakdown: AttackerDamageTakenBreakdown[] = Array.from(statsCollector.attackerDamageTaken.values()).map(a => {
      const sources: DamageTakenSourceStat[] = Array.from(a.sources.values()).map(s => ({
        sourceKey: s.sourceKey,
        sourceName: s.sourceName,
        attackerName: s.attackerName,
        totalHits: s.totalHits,
        evadedHits: s.evadedHits,
        parriedHits: s.parriedHits,
        totalDamage: Number(s.totalDamage.toFixed(0)),
        dps: Number((s.totalDamage / activeSec).toFixed(1)),
        percentage: a.totalDamage > 0 ? Number(((s.totalDamage / a.totalDamage) * 100).toFixed(1)) : 0,
      }))
      sources.sort((a, b) => b.totalDamage - a.totalDamage)
      return {
        attackerId: a.attackerId,
        attackerName: a.attackerName,
        totalDamage: Number(a.totalDamage.toFixed(0)),
        percentage: totalDamageTaken > 0 ? Number(((a.totalDamage / totalDamageTaken) * 100).toFixed(1)) : 0,
        dps: Number((a.totalDamage / activeSec).toFixed(1)),
        sources,
      }
    })
    attackerBreakdown.sort((a, b) => b.totalDamage - a.totalDamage)

    // 恢复统计
    const totalHpRecovered = statsCollector.hpRegenNatural + statsCollector.hpRegenDrain + Array.from(statsCollector.hpRegenSkill.values()).reduce((sum, s) => sum + s.amount, 0)
    const hpSources: RegenSourceStat[] = []
    if (statsCollector.hpRegenNatural > 0) {
      hpSources.push({
        sourceKey: 'natural',
        sourceName: '自然回复',
        totalAmount: Number(statsCollector.hpRegenNatural.toFixed(0)),
        ratePerSecond: Number((statsCollector.hpRegenNatural / activeSec).toFixed(1)),
        percentage: totalHpRecovered > 0 ? Number(((statsCollector.hpRegenNatural / totalHpRecovered) * 100).toFixed(1)) : 0,
      })
    }
    if (statsCollector.hpRegenDrain > 0) {
      hpSources.push({
        sourceKey: 'drain',
        sourceName: '汲取吸血',
        totalAmount: Number(statsCollector.hpRegenDrain.toFixed(0)),
        ratePerSecond: Number((statsCollector.hpRegenDrain / activeSec).toFixed(1)),
        percentage: totalHpRecovered > 0 ? Number(((statsCollector.hpRegenDrain / totalHpRecovered) * 100).toFixed(1)) : 0,
      })
    }
    for (const sk of statsCollector.hpRegenSkill.values()) {
      if (sk.amount > 0) {
        hpSources.push({
          sourceKey: `skill:${sk.abilityId}`,
          sourceName: sk.name,
          totalAmount: Number(sk.amount.toFixed(0)),
          ratePerSecond: Number((sk.amount / activeSec).toFixed(1)),
          percentage: totalHpRecovered > 0 ? Number(((sk.amount / totalHpRecovered) * 100).toFixed(1)) : 0,
        })
      }
    }
    hpSources.sort((a, b) => b.totalAmount - a.totalAmount)

    const totalMpRecovered = statsCollector.mpRegenNatural + statsCollector.mpRegenDrain
    const mpSources: RegenSourceStat[] = []
    if (statsCollector.mpRegenNatural > 0) {
      mpSources.push({
        sourceKey: 'natural',
        sourceName: '自然回复',
        totalAmount: Number(statsCollector.mpRegenNatural.toFixed(0)),
        ratePerSecond: Number((statsCollector.mpRegenNatural / activeSec).toFixed(1)),
        percentage: totalMpRecovered > 0 ? Number(((statsCollector.mpRegenNatural / totalMpRecovered) * 100).toFixed(1)) : 0,
      })
    }
    if (statsCollector.mpRegenDrain > 0) {
      mpSources.push({
        sourceKey: 'drain',
        sourceName: '汲取吸蓝',
        totalAmount: Number(statsCollector.mpRegenDrain.toFixed(0)),
        ratePerSecond: Number((statsCollector.mpRegenDrain / activeSec).toFixed(1)),
        percentage: totalMpRecovered > 0 ? Number(((statsCollector.mpRegenDrain / totalMpRecovered) * 100).toFixed(1)) : 0,
      })
    }

    const recoveryStats: RecoveryStats = {
      hpRegenTotal: Number(totalHpRecovered.toFixed(0)),
      hpRegenPerSecond: Number((totalHpRecovered / activeSec).toFixed(1)),
      hpSources,
      mpRegenTotal: Number(totalMpRecovered.toFixed(0)),
      mpRegenPerSecond: Number((totalMpRecovered / activeSec).toFixed(1)),
      mpSources,
      isManaExhausted: statsCollector.isManaExhausted,
    }

    // 技能每小时法力消耗
    const manaConsumptionStats: SkillManaConsumptionStat[] = Array.from(statsCollector.skillManaSpent.values()).map(s => ({
      abilityId: s.abilityId,
      abilityName: s.name,
      totalManaSpent: s.totalSpent,
      manaPerHour: Number((s.totalSpent / totalHours).toFixed(1)),
      averagePerCast: s.casts > 0 ? Number((s.totalSpent / s.casts).toFixed(1)) : 0,
      castCount: s.casts,
    }))
    manaConsumptionStats.sort((a, b) => b.totalManaSpent - a.totalManaSpent)

    const totalKills = normalKills + eliteKills + bossKills
    const winRate = totalEncounters > 0 ? victories / totalEncounters : 0
    const encountersPerHour = Number((totalEncounters / totalHours).toFixed(1))
    const playerDeaths = defeats
    const playerDeathsPerHour = Number((playerDeaths / totalHours).toFixed(2))

    return {
      nodeId: config.nodeId,
      nodeName: config.nodeName,
      mapName: config.mapName || '',
      totalSimulatedSeconds: currentElapsedSeconds,
      combatActiveSeconds,
      searchSeconds,
      respawnSeconds,
      totalEncounters,
      encountersPerHour,
      victories,
      defeats,
      playerDeaths,
      playerDeathsPerHour,
      winRate,
      maxWave,
      normalKills,
      eliteKills,
      bossKills,
      totalKills,
      monsterKills,
      damageStats: {
        totalDamage: Number(totalDamage.toFixed(0)),
        dps: Number((totalDamage / activeSec).toFixed(1)),
        overallHitRate,
        overallCriticalRate,
        sources: damageSources,
        targetBreakdown,
      },
      damageTakenStats: {
        totalDamageTaken: Number(totalDamageTaken.toFixed(0)),
        dps: Number((totalDamageTaken / activeSec).toFixed(1)),
        overallEvasionRate,
        overallParryRate,
        sources: damageTakenSources,
        attackerBreakdown,
      },
      recoveryStats,
      manaConsumptionStats,
      itemYields,
      dailyDropsRevenue,
      totalExperience: Math.floor(totalExperience),
      experiencePerHour: Math.floor(totalExperience / totalHours),
      dailyCultivationExp: Math.floor((totalCultivationExperience / totalHours) * 24),
      totalCombatExperience: Math.floor(totalCombatExperience),
      combatExperiencePerHour: Math.floor(totalCombatExperience / totalHours),
      totalSixDimensionExperience: Math.floor(totalSixDimensionExperience),
      sixDimensionExperiencePerHour: Math.floor(totalSixDimensionExperience / totalHours),
      dailySixDimensionExp: Math.floor((totalSixDimensionExperience / totalHours) * 24),
      sixDimensionBreakdown,
      manaDeficit: manaTracker.generateReport(),
      recentBattleLogs,
    }
  }

  private static resolveEncounter(
    config: SimulatorConfig,
    encounters: readonly CombatEncounterDefinition[],
    maps?: readonly WorldMapDefinition[]
  ): CombatEncounterDefinition {
    if (config.encounterId) {
      const found = encounters.find(e => e.encounterId === config.encounterId)
      if (found) return found
    }

    if (config.nodeId && maps) {
      for (const map of maps) {
        const node = map.nodes?.find(n => n.nodeId === config.nodeId)
        if (node && node.encounterIds && node.encounterIds.length > 0) {
          const enc = encounters.find(e => e.encounterId === node.encounterIds[0])
          if (enc) return enc
        }
      }
    }

    if (config.nodeName) {
      const found = encounters.find(e => e.name === config.nodeName || e.name.includes(config.nodeName))
      if (found) return found
    }

    return encounters[0] || ({
      encounterId: 'default',
      name: config.nodeName || '默认野外遭遇',
      minBudget: 30,
      maxBudget: 60,
      battlesPerElite: 5,
      battlesPerBoss: 20,
      searchMilliseconds: 3000,
      respawnMilliseconds: 5000,
      playerRespawnMilliseconds: 10000,
      randomSpawns: [],
      enemy: {
        enemyId: 'monster-default',
        name: '野外妖兽',
        rank: 'normal',
        level: 1,
        tier: 1,
        attributes: ensureCombatAttributes({ maxHitpoints: 100, physicalAttack: 10, armor: 5 }),
        budgetCost: 15,
        drops: [],
      },
    } as unknown as CombatEncounterDefinition)
  }

  /**
   * 按预算轮盘与波次规则抽取怪物阵容（与 EncounterWaveSelector.cs 100% 对齐）
   */
  private static generateWave(
    encounter: CombatEncounterDefinition,
    seed: bigint,
    waveNumber: number,
    abilityMap: Map<string, CombatAbilityDefinition>
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
    const minBudget = 'minBudget' in encounter && typeof encounter.minBudget === 'number' ? encounter.minBudget : 30
    const maxBudget = 'maxBudget' in encounter && typeof encounter.maxBudget === 'number' ? encounter.maxBudget : 60
    const { roll: budgetRoll, nextSeed: s0 } = rollMillionth(currentSeed)
    currentSeed = s0

    let remain = minBudget + budgetRoll * (maxBudget - minBudget)
    let strengthPool = encounter.maxTotalStrength > 0 ? encounter.maxTotalStrength : Number.POSITIVE_INFINITY

    // 特殊波次优先加入特殊怪
    if (isBoss && encounter.bossEnemies && encounter.bossEnemies.length > 0) {
      const { roll, nextSeed: s1 } = rollMillionth(currentSeed)
      currentSeed = s1
      const boss = encounter.bossEnemies[Math.floor(roll * encounter.bossEnemies.length)]
      const cost = boss.budgetCost || 30
      rawEnemies.push({
        enemyId: boss.enemyId,
        name: boss.name,
        rank: 'boss',
        level: boss.level || 5,
        tier: boss.tier || 2,
        budgetCost: cost,
        strength: 30,
        attributes: ensureCombatAttributes(boss.attributes),
        experience: boss.experience || 150,
        drops: (boss.drops || []).map(d => ({
          itemId: d.itemId,
          dropChance: d.dropChance,
          minQuantity: d.minQuantity,
          maxQuantity: d.maxQuantity,
        })),
        skills: boss.skills,
      })
      remain = maxBudget - cost
      strengthPool -= 30
    } else if (isElite && encounter.eliteEnemies && encounter.eliteEnemies.length > 0) {
      const { roll, nextSeed: s1 } = rollMillionth(currentSeed)
      currentSeed = s1
      const elite = encounter.eliteEnemies[Math.floor(roll * encounter.eliteEnemies.length)]
      const cost = elite.budgetCost || 20
      rawEnemies.push({
        enemyId: elite.enemyId,
        name: elite.name,
        rank: 'elite',
        level: elite.level || 3,
        tier: elite.tier || 1,
        budgetCost: cost,
        strength: 20,
        attributes: ensureCombatAttributes(elite.attributes),
        experience: elite.experience || 50,
        drops: (elite.drops || []).map(d => ({
          itemId: d.itemId,
          dropChance: d.dropChance,
          minQuantity: d.minQuantity,
          maxQuantity: d.maxQuantity,
        })),
        skills: elite.skills,
      })
      remain = maxBudget - cost
      strengthPool -= 20
    }

    // 预算轮盘抽取普通小怪 (最多 3 只)
    const spawns = encounter.randomSpawns && encounter.randomSpawns.length > 0
      ? encounter.randomSpawns
      : [{ enemy: encounter.enemy, weight: 1, strength: 15 }]

    while (rawEnemies.length < 3 && remain > 0) {
      const affordable = spawns.filter(s => {
        const cost = s.enemy?.budgetCost || s.strength || 15
        const str = s.strength || 15
        return cost <= remain && str <= strengthPool
      })
      if (affordable.length === 0) break

      const totalWeight = affordable.reduce((sum, s) => sum + (s.weight || 1), 0)
      const { roll, nextSeed: s2 } = rollMillionth(currentSeed)
      currentSeed = s2

      let ticket = roll * totalWeight
      let chosen = affordable[0]
      for (const candidate of affordable) {
        chosen = candidate
        if (ticket < (candidate.weight || 1)) break
        ticket -= (candidate.weight || 1)
      }

      const enemy = chosen.enemy || encounter.enemy
      const cost = enemy?.budgetCost || chosen.strength || 15
      rawEnemies.push({
        enemyId: enemy?.enemyId || 'monster',
        name: enemy?.name || '妖兽',
        rank: (enemy?.rank as 'normal' | 'elite' | 'boss') || 'normal',
        level: enemy?.level || 1,
        tier: enemy?.tier || 1,
        budgetCost: cost,
        strength: chosen.strength || 15,
        attributes: ensureCombatAttributes(enemy?.attributes),
        experience: enemy?.experience || 15,
        drops: (enemy?.drops || []).map(d => ({
          itemId: d.itemId,
          dropChance: d.dropChance,
          minQuantity: d.minQuantity,
          maxQuantity: d.maxQuantity,
        })),
        skills: enemy?.skills,
      })

      remain -= cost
      strengthPool -= (chosen.strength || 15)
    }

    // 保底：若阵容为空，加入 1 只保底小怪
    if (rawEnemies.length === 0) {
      const fallbackEnemy = encounter.enemy || spawns[0]?.enemy
      rawEnemies.push({
        enemyId: fallbackEnemy?.enemyId || 'monster',
        name: fallbackEnemy?.name || '妖兽',
        rank: (fallbackEnemy?.rank as 'normal' | 'elite' | 'boss') || 'normal',
        level: fallbackEnemy?.level || 1,
        tier: fallbackEnemy?.tier || 1,
        budgetCost: 15,
        strength: 15,
        attributes: ensureCombatAttributes(fallbackEnemy?.attributes),
        experience: fallbackEnemy?.experience || 15,
        drops: (fallbackEnemy?.drops || []).map(d => ({
          itemId: d.itemId,
          dropChance: d.dropChance,
          minQuantity: d.minQuantity,
          maxQuantity: d.maxQuantity,
        })),
        skills: fallbackEnemy?.skills,
      })
    }

    // 转换为 SimCombatant 实体
    const enemies = rawEnemies.map((e, index) => {
      const skills: SimSkill[] = (e.skills || [])
        .filter(sk => typeof sk.abilityId === 'string' && sk.abilityId.length > 0)
        .map((sk, skIdx) => {
          const abilityId = sk.abilityId as string
          const def = abilityMap.get(abilityId)
          return {
            slotIndex: skIdx + 1,
            abilityId,
            name: def?.name || abilityId,
            level: sk.level || 1,
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
            triggers: def?.defaultTriggers || [],
          }
        })

      return new SimCombatant(
        `${e.enemyId}_${index}`,
        e.name,
        false,
        index,
        e.attributes,
        skills,
        0,
        e.enemyId
      )
    })

    return {
      enemies,
      rawEnemies,
      nextSeed: currentSeed,
    }
  }
}
