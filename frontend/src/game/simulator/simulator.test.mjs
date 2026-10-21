import test from 'node:test'
import assert from 'node:assert/strict'
import { nextSeed, deterministicRoll, rollDropTable } from './deterministicRng.ts'
import { SimSkillTriggerEvaluator } from './skillTriggerEvaluator.ts'
import { ManaDeficitTracker } from './manaDeficitTracker.ts'
import { ExpeditionSimulator, resolveMonsterSkillTriggers } from './expeditionSimulator.ts'

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
  assert.ok(report.dailyCultivationExp > 0, '每日修为产出必须大于0')
  assert.ok(report.dailySixDimensionExp > 0, '每日六维经验产出必须大于0')
  assert.ok(report.dailyDropsRevenue >= 0, '每日掉落收益估值必须大于等于0')
  assert.ok(typeof report.sixDimensionBreakdown.stamina === 'number', '六维分项统计必须存在')
})

test('expeditionSimulator: 装备强化属性加成与战斗消耗品生效验证', () => {
  const allyWithGear = {
    id: 'test-player-gear',
    name: '全装修士',
    isSelf: true,
    formationIndex: 1,
    attributes: {
      maxHitpoints: 100,
      maxManapoints: 50,
      physicalAttack: 10,
      spellAttack: 10,
      armor: 5,
      magicResistance: 5,
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
    equipment: [
      { itemId: 'equipment.sword-t1', enhancementLevel: 5 },
    ],
    consumables: [
      { itemId: 'item.ningxue-pill', quantity: 10, enabled: true },
    ],
  }

  const equipmentDef = {
    equipmentId: 'equipment.sword-t1',
    name: '测试玄剑',
    slot: 'weapon',
    tier: 1,
    flatStats: { physicalAttack: 20 },
  }

  const pillItem = {
    itemId: 'item.ningxue-pill',
    name: '凝血丹',
    sellPrice: 15,
  }

  const report = ExpeditionSimulator.runSimulation(
    {
      nodeId: 'test-node-gear',
      nodeName: '测试地点',
      durationSeconds: 600,
      seed: 999888n,
      allies: [allyWithGear],
    },
    {
      encounters: [{
        encounterId: 'test-enc',
        name: '单兔遭遇',
        minThreat: 10,
        maxThreat: 20,
        enemy: {
          enemyId: 'rabbit',
          name: '魔兔',
          rank: 'normal',
          experience: 15,
          attributes: {
            maxHitpoints: 50,
            maxManapoints: 0,
            physicalAttack: 5,
            spellAttack: 0,
            armor: 2,
            magicResistance: 2,
            accuracy: 10,
            evasion: 5,
            attackIntervalMilliseconds: 1500,
          },
          drops: [{ itemId: 'item.ningxue-pill', dropChance: 1.0, minQuantity: 1, maxQuantity: 2 }],
        },
      }],
      abilities: [{
        abilityId: 'spell-fire',
        name: '火球术',
        damageRatio: 2.0,
        castMilliseconds: 1000,
        cooldownMilliseconds: 4000,
        manaCost: 10,
      }],
      items: [pillItem],
      equipment: [equipmentDef],
      marketStats: {
        'item.ningxue-pill|0': {
          itemId: 'item.ningxue-pill',
          enhancementLevel: 0,
          referencePrice: 20,
          bestAskPrice: 22,
          askQuantity: 50,
          bestBidPrice: 18,
          bidQuantity: 30,
          dayVolume: 100,
          weekVolume: 500,
        },
      },
    }
  )

  assert.ok(report.totalEncounters > 0, '推演必须有遭遇产出')
  assert.ok(report.dailyDropsRevenue > 0, '掉落收益必须按照参考价估值计算')
  const pillYield = report.itemYields.find(i => i.itemId === 'item.ningxue-pill')
  assert.ok(pillYield, '必须统计到凝血丹掉落')
  assert.equal(pillYield?.unitPrice, 20, '市场参考价必须为 20')
  assert.ok(pillYield?.dailyRevenue > 0, '每日收益必须大于0')
})

test('resolveMonsterSkillTriggers: 怪物槽位触发条件优先于功法默认条件', () => {
  // 水灵槽位：服务端把 triggerHpRatio=1.0 折叠为 self 气血百分比 ≤ 100（恒真）。
  const slotTriggers = [
    { dependency: 'self', condition: 'current-hp-percentage', comparator: 'less-than-equal', value: 100 },
  ]
  // 甘露配表默认条件：友方最低血量缺失 ≥ 1——怪物视角下恒假，会导致水灵永不施放。
  const ability = {
    abilityId: 'ability.ganlu',
    defaultTriggers: [
      { dependency: 'ally-lowest-hp', condition: 'missing-hp', comparator: 'greater-than-equal', value: 1 },
    ],
  }

  // 服务端已下发槽位条件时必须原样采用，不得退回功法默认条件。
  const resolved = resolveMonsterSkillTriggers(
    { abilityId: 'ability.ganlu', level: 1, triggers: slotTriggers },
    ability,
  )
  assert.equal(resolved.length, 1)
  assert.equal(resolved[0].dependency, 'self')
  assert.equal(resolved[0].condition, 'current-hp-percentage')

  // 服务端下发空数组表示「恒可用」，必须原样保留（不能被默认条件覆盖）。
  const always = resolveMonsterSkillTriggers({ abilityId: 'ability.ganlu', level: 1, triggers: [] }, ability)
  assert.equal(always.length, 0, '下发的空条件表表示恒可用，不得回退功法默认条件')

  // 未下发槽位条件时才回退功法默认条件（兼容旧快照）。
  const fallback = resolveMonsterSkillTriggers({ abilityId: 'ability.ganlu', level: 1 }, ability)
  assert.equal(fallback.length, 1)
  assert.equal(fallback[0].dependency, 'ally-lowest-hp')

  // 回归断言：按服务端口径（水灵气血百分比 ≤ 100）时水灵可施放；按功法默认条件时恒不可施放。
  const waterSpirit = {
    isAlive: true, hitpoints: 866, maxHitpoints: 866, manapoints: 44, maxManapoints: 44, buffs: [],
  }
  const player = {
    isAlive: true, hitpoints: 500, maxHitpoints: 500, manapoints: 100, maxManapoints: 100, buffs: [],
  }
  assert.equal(
    SimSkillTriggerEvaluator.matchesAll(resolved, waterSpirit, player, [waterSpirit], [player]),
    true,
    '水灵必须能按服务端槽位条件施放甘露',
  )
  assert.equal(
    SimSkillTriggerEvaluator.matchesAll(fallback, waterSpirit, player, [waterSpirit], [player]),
    false,
    '功法默认条件在怪物视角下恒假，正是水灵不爆技能的根因',
  )
})

test('expeditionSimulator: 副手与六大部位装备属性注入与强化缩放生效', () => {
  // 验证副手（off-hand）及其他部位装备均能正常被推演引擎读取、属性合并与强化缩放
  const allyWithSixGear = {
    id: 'ally-full-gear',
    name: '六神装修士',
    isSelf: true,
    formationIndex: 1,
    attributes: {
      maxHitpoints: 500,
      maxManapoints: 200,
      physicalAttack: 50,
      spellAttack: 50,
      armor: 20,
      magicResistance: 20,
      accuracy: 30,
      evasion: 20,
      critical: 20,
      criticalResistance: 20,
      criticalDamagePoints: 50,
      breakPower: 10,
      parryResolve: 10,
      hpRegenPerFiveSeconds: 10,
      mpRegenPerFiveSeconds: 5,
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
    equipment: [
      { itemId: 'equipment.stick-t2', enhancementLevel: 20 }, // 武器 (+20 -> scale 3.0)
      { itemId: 'equipment.shield-t2', enhancementLevel: 10 }, // 副手盾牌 (+10 -> scale 2.0)
      { itemId: 'equipment.robe-t2', enhancementLevel: 0 },   // 衣服 (+0 -> scale 1.0)
    ],
  }

  const equipmentDefs = [
    {
      equipmentId: 'equipment.stick-t2',
      name: '朝元杖',
      slot: 'weapon',
      tier: 2,
      flatStats: { spellAttack: 42 },
    },
    {
      equipmentId: 'equipment.shield-t2',
      name: '玄铁灵盾',
      slot: 'off-hand',
      tier: 2,
      flatStats: { armor: 30, maxHitpoints: 100 },
    },
    {
      equipmentId: 'equipment.robe-t2',
      name: '流云法袍',
      slot: 'armor',
      tier: 2,
      flatStats: { magicResistance: 25 },
    },
  ]

  const report = ExpeditionSimulator.runSimulation(
    {
      nodeId: 'test-node-offhand',
      nodeName: '副手验证地点',
      durationSeconds: 60,
      seed: 123456n,
      allies: [allyWithSixGear],
    },
    {
      encounters: [{
        encounterId: 'test-enc',
        name: '单怪遭遇',
        minThreat: 10,
        maxThreat: 20,
        enemy: {
          enemyId: 'dummy',
          name: '木桩怪',
          rank: 'normal',
          experience: 10,
          attributes: {
            maxHitpoints: 2000,
            maxManapoints: 0,
            physicalAttack: 10,
            spellAttack: 0,
            armor: 5,
            magicResistance: 5,
            accuracy: 10,
            evasion: 0,
            attackIntervalMilliseconds: 2000,
          },
        },
      }],
      abilities: [{
        abilityId: 'spell-fire',
        name: '火球术',
        damageRatio: 1.0,
        castMilliseconds: 0,
        cooldownMilliseconds: 1000,
      }],
      equipment: equipmentDefs,
    }
  )

  assert.ok(report.totalEncounters > 0, '必须完成遭遇战推演')
  assert.ok(report.damageStats.totalDamage > 0, '必须产生伤害')
})
