import test from 'node:test'
import assert from 'node:assert/strict'
import {
  TILI_MAX,
  TILI_INITIAL,
  TILI_RECOVERY_INTERVAL_SECONDS,
  DUNGEON_ENERGY_COSTS,
  DUNGEON_DIFFICULTIES,
  clampDungeonDifficulty,
  getDungeonMode,
  getDungeonDifficultiesByMode,
  getDungeonEnergyCost,
  getDungeonHpMultiplier,
  getDungeonStatMultiplier,
  getDungeonEfficiencyMultiplier,
  getDungeonTotalRewardMultiplier,
  getDungeonRareDropRateMultiplier,
  getDungeonRareRollCount,
  resolveDungeonDynamicMonsterLevel,
  resolveDungeonMonsterForDisplay,
  scaleDungeonMonsterAttributes,
  computeDungeonEnergy,
  computeDungeonLootPreview,
} from './dungeonRules.ts'

test('dungeonRules: 虚境3档+现世8阶共11档耗体、效率与总倍率严格对齐规约', () => {
  assert.equal(DUNGEON_DIFFICULTIES.length, 11)
  assert.equal(getDungeonDifficultiesByMode('virtual').length, 3)
  assert.equal(getDungeonDifficultiesByMode('reality').length, 8)
  assert.deepEqual([...DUNGEON_ENERGY_COSTS], [30, 40, 50, 60, 80, 100, 120, 150, 180, 210, 240])
  assert.equal(clampDungeonDifficulty(-5), 0)
  assert.equal(clampDungeonDifficulty(99), 10)
  assert.equal(getDungeonMode(2), 'virtual')
  assert.equal(getDungeonMode(3), 'reality')

  // D0 虚境·普通：30 体力、1.0x 效率、1.0x 总倍率
  assert.equal(getDungeonEnergyCost(0), 30)
  assert.equal(getDungeonEfficiencyMultiplier(0), 1.0)
  assert.equal(getDungeonTotalRewardMultiplier(0), 1.0)
  assert.equal(getDungeonRareRollCount(0), 1)

  // D3 现世·凡阶：60 体力、1.45x 效率、2.90x 总倍率（继续从 d=3 叠乘，高于虚境·地狱 d=2）
  assert.equal(getDungeonEnergyCost(3), 60)
  assert.ok(Math.abs(getDungeonEfficiencyMultiplier(3) - 1.45) < 1e-9)
  assert.ok(Math.abs(getDungeonTotalRewardMultiplier(3) - 2.90) < 1e-9)
  assert.ok(getDungeonHpMultiplier(3) > getDungeonHpMultiplier(2))
  assert.ok(getDungeonStatMultiplier(3) > getDungeonStatMultiplier(2))

  // D10 现世·帝阶：240 体力、2.50x 效率、20.0x 总倍率、3.50x 稀有倍率、8 次独立掷骰
  assert.equal(getDungeonEnergyCost(10), 240)
  assert.ok(Math.abs(getDungeonEfficiencyMultiplier(10) - 2.50) < 1e-9)
  assert.ok(Math.abs(getDungeonTotalRewardMultiplier(10) - 20.0) < 1e-9)
  assert.ok(Math.abs(getDungeonRareDropRateMultiplier(10) - 3.50) < 1e-9)
  assert.equal(getDungeonRareRollCount(10), 8)
})

test('dungeonRules: 现世动态怪物等级具备下限保护并保持与最低级怪的差值，且不额外膨胀材料掉率', () => {
  // 副本最低怪 6 级，Boss 12 级（差值 +6）
  assert.equal(resolveDungeonDynamicMonsterLevel(12, 6, 40, 2), 12) // 虚境不升阶
  assert.equal(resolveDungeonDynamicMonsterLevel(12, 6, 2, 3), 12) // 现世下限保护：max(2, 6) = 6 -> 12
  assert.equal(resolveDungeonDynamicMonsterLevel(6, 6, 30, 3), 30) // 最低级怪 -> 30
  assert.equal(resolveDungeonDynamicMonsterLevel(12, 6, 30, 3), 36) // Boss -> 30 + (12 - 6) = 36
  assert.equal(resolveDungeonDynamicMonsterLevel(12, 6, 125, 10), 127) // 127 封顶

  const anchors = Array.from({ length: 40 }, (_, idx) => {
    const lvl = idx + 1
    return {
      level: lvl,
      tier: lvl >= 18 ? 2 : 1,
      hp: 100 * lvl,
      mp: 50 * lvl,
      ad: 20 * lvl,
      ap: 20 * lvl,
      armor: 10 * lvl,
      mr: 10 * lvl,
      acc: 10 * lvl,
      eva: 10 * lvl,
      crit: 5 * lvl,
      cavoid: 5 * lvl,
      breakPt: 5 * lvl,
      parry: 5 * lvl,
      killExpNormal: 10 * lvl,
      killExpElite: 30 * lvl,
      killExpBoss: 100 * lvl,
    }
  })

  const mockDungeon = {
    dungeonId: 'dungeon.test',
    name: '追忆·测试',
    description: '',
    nodeId: 'node.cloud-peak',
    tier: 1,
    baseLevel: 12,
    minMonsterLevel: 6,
    recommendedValuation: [],
    waveRestSeconds: 5,
    totalWaves: 10,
    waves: [],
    baseRewards: { experience: 1000, spiritStones: 100 },
    clearLootTable: [],
    levelAnchors: anchors,
  }
  const boss = {
    enemyId: 'enemy.boss',
    name: '妖王',
    style: 'body',
    category: 'physical',
    element: 'none',
    rank: 'boss',
    level: 12,
    tier: 1,
    attributes: { maxHitpoints: 2000, physicalAttack: 100, armor: 50 },
    experience: 1200,
    drops: [{ itemId: 'item.sha-fang', dropChance: 0.2, minQuantity: 1, maxQuantity: 1 }],
    skills: [],
  }

  // 同为 d=3（现世·凡阶），玩家 6 级 vs 玩家 30 级：面板和经验显著放大，但材料掉率严格只按 d=3 (1.75x) 计算
  const atLv6 = resolveDungeonMonsterForDisplay(boss, mockDungeon, 3, 6, anchors)
  const atLv30 = resolveDungeonMonsterForDisplay(boss, mockDungeon, 3, 30, anchors)
  assert.equal(atLv6.level, 12)
  assert.equal(atLv30.level, 36)
  assert.ok((atLv30.attributes.maxHitpoints ?? 0) > (atLv6.attributes.maxHitpoints ?? 0) * 2)
  assert.ok((atLv30.experience ?? 0) > (atLv6.experience ?? 0) * 2)
  assert.equal(atLv6.drops[0].dropChance, atLv30.drops[0].dropChance)
  assert.ok(Math.abs(atLv30.drops[0].dropChance - 0.2 * 1.75) < 1e-9)
})

test('dungeonRules: 双轴缩放气血 1.30^d 与常规属性 1.15^d 且保留攻速', () => {
  const base = {
    maxHitpoints: 100,
    physicalAttack: 20,
    spellAttack: 15,
    armor: 10,
    attackIntervalMilliseconds: 1800,
  }
  const d3 = scaleDungeonMonsterAttributes(base, 3)
  assert.equal(d3.maxHitpoints, Math.ceil(100 * Math.pow(1.30, 3)))
  assert.equal(d3.physicalAttack, Math.ceil(20 * Math.pow(1.15, 3)))
  assert.equal(d3.spellAttack, Math.ceil(15 * Math.pow(1.15, 3)))
  assert.equal(d3.armor, Math.ceil(10 * Math.pow(1.15, 3)))
  assert.equal(d3.attackIntervalMilliseconds, 1800)
  assert.ok(Math.abs(getDungeonHpMultiplier(10) - Math.pow(1.30, 10)) < 1e-9)
  assert.ok(Math.abs(getDungeonStatMultiplier(10) - Math.pow(1.15, 10)) < 1e-9)
})

test('dungeonRules: 体力懒恢复整点推进与上限封顶守恒', () => {
  const uninit = computeDungeonEnergy(undefined, 0, 10000)
  assert.equal(uninit.current, TILI_INITIAL)
  assert.equal(uninit.max, TILI_MAX)
  assert.equal(uninit.nextRecoverySeconds, TILI_RECOVERY_INTERVAL_SECONDS)

  const partial = computeDungeonEnergy(100, 1000, 1750)
  assert.equal(partial.current, 102)
  assert.equal(partial.nextRecoverySeconds, 330)
  assert.equal(partial.fullRecoverySeconds, 330 + (TILI_MAX - 103) * TILI_RECOVERY_INTERVAL_SECONDS)

  const full = computeDungeonEnergy(0, 1000, 1000 + 8 * 86400)
  assert.equal(full.current, TILI_MAX)
  assert.equal(full.nextRecoverySeconds, 0)
  assert.equal(full.fullRecoverySeconds, 0)
})

test('dungeonRules: 通关独占掉落预览区分保底倍率与稀有解锁门槛', () => {
  const guaranteed = {
    itemId: 'item.qingyun-secret-crystal',
    kind: 'guaranteed',
    minQuantity: 2,
    maxQuantity: 3,
    dropChance: 1.0,
    minDifficulty: 0,
  }
  const rare = {
    itemId: 'equipment.changhu-pendant',
    kind: 'rare',
    minQuantity: 1,
    maxQuantity: 1,
    dropChance: 0.02,
    minDifficulty: 3,
  }

  // D2 虚境·地狱下稀有未解锁
  const rareLocked = computeDungeonLootPreview(rare, 2)
  assert.equal(rareLocked.unlocked, false)
  assert.equal(rareLocked.effectiveChance, 0)

  // D3 现世·凡阶解锁：单次 0.02 * 1.75 = 0.035，2 份耗体抽次 = 1 - 0.965^2
  const rareD3 = computeDungeonLootPreview(rare, 3)
  assert.equal(rareD3.unlocked, true)
  assert.equal(rareD3.rolls, 2)
  assert.ok(Math.abs(rareD3.effectiveChance - (1 - Math.pow(1 - 0.035, 2))) < 1e-9)

  // D3 保底材料随总倍率 2.90x 放大：floor(2*2.9)=5, ceil(3*2.9)=9
  const guarD3 = computeDungeonLootPreview(guaranteed, 3)
  assert.equal(guarD3.unlocked, true)
  assert.equal(guarD3.minQuantity, 5)
  assert.equal(guarD3.maxQuantity, 9)
})
