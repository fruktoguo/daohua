import test from 'node:test'
import assert from 'node:assert/strict'
import { nextSeed, deterministicRoll, rollDropTable } from './deterministicRng.ts'
import { SimSkillTriggerEvaluator } from './skillTriggerEvaluator.ts'
import { ManaDeficitTracker } from './manaDeficitTracker.ts'
import { ExpeditionSimulator } from './expeditionSimulator.ts'

test('deterministicRng: 同种子同参数产出绝对一致', () => {
  const seed = 123456789n
  const s1 = nextSeed(seed)
  const s2 = nextSeed(seed)
  assert.equal(s1, s2, 'nextSeed 必须确定')

  const r1 = deterministicRoll(seed, 0n, 10, 0n)
  const r2 = deterministicRoll(seed, 0n, 10, 0n)
  assert.equal(r1, r2, 'deterministicRoll 必须确定')
  assert.ok(r1 >= 0 && r1 < 1, '随机数必须处于 [0, 1)')
})

test('skillTriggerEvaluator: 条件判定自洽', () => {
  const caster = {
    isAlive: true,
    hitpoints: 50,
    maxHitpoints: 100,
    manapoints: 20,
    maxManapoints: 100,
    buffs: [{ id: 'buff-test', stacks: 2 }],
  }

  // 空条件恒为 true
  assert.equal(SimSkillTriggerEvaluator.matchesAll([], caster, null, [caster], []), true)

  // 自身气血百分比 <= 50 (50/100 = 50% 满足)
  assert.equal(
    SimSkillTriggerEvaluator.matches(
      { dependency: 'self', condition: 'current-hp-percentage', comparator: 'less-than-equal', value: 50 },
      caster, null, [caster], []
    ),
    true
  )

  // 自身气血百分比 > 60 (不满足)
  assert.equal(
    SimSkillTriggerEvaluator.matches(
      { dependency: 'self', condition: 'current-hp-percentage', comparator: 'greater-than', value: 60 },
      caster, null, [caster], []
    ),
    false
  )

  // Buff 层数 >= 2 (满足)
  assert.equal(
    SimSkillTriggerEvaluator.matches(
      { dependency: 'self', condition: 'buff-stacks', comparator: 'greater-than-equal', value: 2, buffId: 'buff-test' },
      caster, null, [caster], []
    ),
    true
  )
})

test('manaDeficitTracker: 灵力缺口追踪与时间占比自洽', () => {
  const tracker = new ManaDeficitTracker(
    [{ abilityId: 'fireball', name: '火球', manaCost: 30 }],
    100
  )

  // 模拟推进 10 秒，此时灵力 20 < 30，发生卡蓝
  tracker.recordDeficit(0, 0)
  tracker.sampleMana(20, 5)

  // 5秒时回复灵力到 50，成功施放
  tracker.recordCast(0, 5, 30)
  tracker.sampleMana(50, 5)
  tracker.finalize(10)

  const report = tracker.generateReport()
  assert.ok(report.overallDeficitSeconds >= 4.9 && report.overallDeficitSeconds <= 5.1, '卡蓝等待时长应约为 5 秒')
  assert.ok(report.overallDeficitRatio >= 0.49 && report.overallDeficitRatio <= 0.51, '10秒中卡蓝5秒，占比应约为 50%')
  assert.equal(report.skillStats[0].castCount, 1, '施放次数应为 1')
  assert.equal(report.skillStats[0].deficitCount, 1, '阻断次数应为 1')
})

test('expeditionSimulator: 1小时完整推演指标完整产出', () => {
  const ally = {
    id: 'test-player',
    name: '测试修士',
    isSelf: true,
    formationIndex: 1,
    attributes: {
      maxHitpoints: 300,
      maxManapoints: 100,
      physicalAttack: 30,
      spellAttack: 20,
      armor: 15,
      magicResistance: 10,
      accuracy: 20,
      evasion: 10,
      critical: 10,
      criticalResistance: 10,
      criticalDamagePoints: 50,
      breakPower: 10,
      parryResolve: 10,
      hpRegenPerFiveSeconds: 5,
      mpRegenPerFiveSeconds: 3,
      attackIntervalMilliseconds: 1000,
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
    },
    abilitySlots: [
      { slotIndex: 0, abilityId: 'internal-1', level: 1 },
      { slotIndex: 1, abilityId: 'spell-fire', level: 1 },
    ],
  }

  const encounter = {
    encounterId: 'test-enc',
    name: '测试遭遇',
    minBudget: 30,
    maxBudget: 60,
    battlesPerElite: 5,
    battlesPerBoss: 20,
    playerRespawnMilliseconds: 10000,
    randomSpawns: [],
    enemy: {
      enemyId: 'test-monster',
      name: '小妖',
      rank: 'normal',
      level: 1,
      tier: 1,
      attributes: { ...ally.attributes, maxHitpoints: 100, physicalAttack: 10, armor: 5 },
      drops: [{ itemId: 'item-fur', dropChance: 0.5, minQuantity: 1, maxQuantity: 2 }],
      experience: 20,
      skills: [],
    },
  }

  const ability = {
    abilityId: 'spell-fire',
    name: '烈火法术',
    category: 'spell',
    element: 'fire',
    damageRatio: 2.0,
    flatDamage: 10,
    healingRatio: 0,
    flatHealing: 0,
    targetType: 'targetEnemy',
    castMilliseconds: 800,
    cooldownMilliseconds: 3000,
    manaCost: 20,
  }

  const item = {
    itemId: 'item-fur',
    name: '兽皮',
    subCategory: 'material',
    tier: 1,
  }

  const report = ExpeditionSimulator.runSimulation(
    {
      nodeId: 'test-node',
      nodeName: '测试地点',
      durationSeconds: 3600,
      seed: 555666777n,
      allies: [ally],
    },
    {
      encounters: [encounter],
      abilities: [ability],
      items: [item],
    }
  )

  assert.equal(report.totalSimulatedSeconds, 3600, '总模拟时长必须为 3600 秒')
  assert.ok(report.totalEncounters > 50, '1小时应推进数十场以上遭遇')
  assert.ok(report.winRate > 0.8, '属性占优时胜率应大于80%')
  assert.ok(report.totalKills > 50, '击杀数应与遭遇数自洽')
  assert.ok(report.monsterKills.length > 0, '怪物击杀明细应非空')
  assert.ok(report.damageStats.totalDamage > 0, '造成总伤害必须大于0')
  assert.ok(report.damageStats.dps > 0, 'DPS必须大于0')
  assert.ok(report.damageStats.sources.length > 0, '伤害来源明细必须非空')
  assert.ok(report.recoveryStats.hpRegenPerSecond >= 0, '生命回复速率必须有效')
  assert.ok(report.recoveryStats.mpRegenPerSecond >= 0, '法力回复速率必须有效')
  assert.ok(report.recentBattleLogs.length > 0, '最近战斗日志必须被留存')
  assert.equal(report.manaDeficit.skillStats[0].slotIndex, 1, '技能槽0内功必须被排除在主动卡蓝之外')
  assert.ok(report.manaDeficit.overallDeficitRatio >= 0 && report.manaDeficit.overallDeficitRatio <= 1, '缺蓝比例在 0~1 之间')
})
