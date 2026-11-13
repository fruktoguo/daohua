// 遭遇波次生成器（严格对应 Domain.Combat.EncounterWaveSelector）

import type { CombatEncounterDefinition, CombatMonsterDefinition } from './catalog.ts'
import { toUint64 } from './math.ts'

const MASK64 = 0xffffffffffffffffn
const MAX_WAVE_SIZE = 3
const MAX_ALLIED_WAVE_SIZE = 9
const THREAT_RATIO_PER_EXTRA_PLAYER = 0.15
const MAX_THREAT_MULTIPLIER = 1.6

export function getPartyThreatMultiplier(partySize: number): number {
  const extra = Math.max(0, partySize - 1)
  return Math.min(MAX_THREAT_MULTIPLIER, 1 + extra * THREAT_RATIO_PER_EXTRA_PLAYER)
}

function nextXorshift(stateRef: { s: bigint }): bigint {
  let x = stateRef.s
  x = (x ^ (x << 13n)) & MASK64
  x = (x ^ (x >> 7n)) & MASK64
  x = (x ^ (x << 17n)) & MASK64
  stateRef.s = x
  return x
}

export function selectEncounterWave(
  encounter: CombatEncounterDefinition,
  monsters: ReadonlyMap<string, CombatMonsterDefinition>,
  seed: bigint,
  waveNumber: number,
  partySize = 1,
): CombatMonsterDefinition[] {
  if (
    waveNumber < 1 ||
    !Number.isFinite(encounter.minBudget) ||
    !Number.isFinite(encounter.maxBudget) ||
    encounter.minBudget <= 0 ||
    encounter.maxBudget < encounter.minBudget
  ) {
    throw new Error('遭遇波次或预算无效。')
  }

  const bossInterval = encounter.battlesPerBoss > 0 ? encounter.battlesPerBoss : 20
  const eliteInterval = encounter.battlesPerElite > 0 ? encounter.battlesPerElite : 5
  const specialPool =
    waveNumber % bossInterval === 0
      ? encounter.bossEnemyIds
      : waveNumber % eliteInterval === 0
        ? encounter.eliteEnemyIds
        : null

  const threatMultiplier = getPartyThreatMultiplier(partySize)
  const minBudget = encounter.minBudget * threatMultiplier
  const maxBudget = encounter.maxBudget * threatMultiplier
  let strengthPool =
    encounter.maxTotalStrength > 0
      ? encounter.maxTotalStrength * threatMultiplier
      : Number.POSITIVE_INFINITY
  const maxWaveSize = partySize > 1 ? MAX_ALLIED_WAVE_SIZE : MAX_WAVE_SIZE

  const stateRef = { s: toUint64(seed) }
  const result: CombatMonsterDefinition[] = []
  let remain =
    minBudget + (Number(nextXorshift(stateRef) % 1000000n) / 1000000) * (maxBudget - minBudget)

  const spawns = encounter.randomSpawns
  const spawnCount = spawns.length

  if (specialPool !== null && specialPool.length > 0) {
    const id = specialPool[Number(nextXorshift(stateRef) % BigInt(specialPool.length))]
    const monster = monsters.get(id)
    if (!monster) throw new Error('特殊怪物未定义。')
    if (!Number.isFinite(monster.budgetCost) || monster.budgetCost <= 0) {
      throw new Error('特殊怪物预算无效。')
    }
    result.push(monster)
    remain = maxBudget - monster.budgetCost
    let specialStrength = 0
    for (let i = spawnCount - 1; i >= 0; i--) {
      if (spawns[i].enemyId === id) {
        specialStrength = spawns[i].strength
        break
      }
    }
    strengthPool -= specialStrength
  }

  const resolved: CombatMonsterDefinition[] = new Array(spawnCount)
  const budgetCosts: number[] = new Array(spawnCount)
  for (let i = 0; i < spawnCount; i++) {
    const sp = spawns[i]
    const def = monsters.get(sp.enemyId)
    if (
      !Number.isFinite(sp.weight) ||
      sp.weight <= 0 ||
      !def ||
      !Number.isFinite(def.budgetCost) ||
      def.budgetCost <= 0 ||
      !Number.isFinite(sp.strength) ||
      sp.strength <= 0
    ) {
      throw new Error('遭遇怪物引用、权重、预算或强度无效。')
    }
    resolved[i] = def
    budgetCosts[i] = def.budgetCost
  }

  if (spawnCount === 0 && result.length === 0) {
    throw new Error('遭遇没有有效怪物池。')
  }

  while (result.length < maxWaveSize && remain > 0) {
    let total = 0
    let affordableCount = 0
    for (let i = 0; i < spawnCount; i++) {
      if (budgetCosts[i] <= remain && spawns[i].strength <= strengthPool) {
        affordableCount++
        total += spawns[i].weight
      }
    }
    if (affordableCount === 0) break
    if (!Number.isFinite(total)) throw new Error('遭遇总权重无效。')
    let ticket = (Number(nextXorshift(stateRef) % 1000000n) / 1000000) * total
    let chosen = -1
    for (let i = 0; i < spawnCount; i++) {
      if (!(budgetCosts[i] <= remain && spawns[i].strength <= strengthPool)) continue
      chosen = i
      if (ticket < spawns[i].weight) break
      ticket -= spawns[i].weight
    }
    const monster = resolved[chosen]
    result.push(monster)
    remain -= monster.budgetCost
    strengthPool -= spawns[chosen].strength
  }

  if (result.length === 0) {
    let fallback = -1
    let minCost = Number.POSITIVE_INFINITY
    for (let i = 0; i < spawnCount; i++) {
      if (budgetCosts[i] < minCost) {
        minCost = budgetCosts[i]
        fallback = i
      }
    }
    if (fallback < 0) throw new Error('遭遇预算不足以生成任何怪物。')
    result.push(resolved[fallback])
  }

  return result
}
