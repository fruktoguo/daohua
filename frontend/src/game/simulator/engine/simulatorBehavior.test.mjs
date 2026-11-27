import '../../../../test-loader.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildGameDataCatalog } from './catalog.ts'
import { CombatEncounterState, CombatantState } from './combatModels.ts'
import { advanceCombatTo, decideInitialActions } from './combatEngine.ts'
import { evaluateTriggers } from './triggerEvaluator.ts'
import { CombatFactTracker, UnitChannel, resolveTriggerListWakeMask } from './triggerWake.ts'
import { createDefaultProfile, listCombatNodeIds, listDungeonEntries, runSimulation } from './simulatorRunner.ts'
import {
  BATTLE_LIFETIME_SECONDS,
  BuffEffectChannel,
  CombatDamageKind,
  CombatRespawnPolicy,
  abilityLevelForExp,
  createCombatantStats,
} from './rules.ts'
import { pendingActionDurationOf } from './resourceBoundaries.ts'
import { validateMember } from './validator.ts'
import { useSimulatorStore } from '../simulatorStore.ts'

const {
  ROOT_LEVEL_CAP,
  getRootCumulativeExpForLevel,
  getRootPointsForExp,
  calculateAbilityLevelAndProgress,
} = await import('../../combatConstants.ts')

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const catalogJson = JSON.parse(
  fs.readFileSync(path.resolve(__dirname, '../__golden__/simulatorCatalog.json'), 'utf8'),
)
const parityCases = JSON.parse(
  fs.readFileSync(path.resolve(__dirname, '../__golden__/parityCases.json'), 'utf8'),
)

const catalog = buildGameDataCatalog(catalogJson)

test('玩家双抗分来源缩放：境界和六维只翻固定值，装备目录已翻倍一次', async () => {
  const { resolveAttributesCore } = await import('./attributeResolver.ts')
  const { buildSimulatorCatalogPayload } = await import('../../../../../scripts/gen-simulator-catalog.mjs')
  const currentCatalog = buildGameDataCatalog(buildSimulatorCatalogPayload())
  const armorId = 'equipment.yulin-armor'
  const roots = {
    staminaExp: getRootCumulativeExpForLevel(10),
    meridiansExp: getRootCumulativeExpForLevel(20),
    attackExp: 0, boneExp: 0, agilityExp: 0, consciousnessExp: 0,
  }
  const linggen = { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 }
  const stats = resolveAttributesCore(30, [armorId], [{ itemId: armorId, enhancementLevel: 0 }],
    [], null, roots, [], currentCatalog, null, null, null, linggen)

  assert.equal(currentCatalog.baseAttributes.at(30).armor, 68)
  assert.equal(currentCatalog.baseAttributes.at(30).mr, 56)
  assert.ok(Math.abs(stats.armor - ((136 + 20) * 1.10 + 24) * 1.52) < 1e-9)
  assert.ok(Math.abs(stats.magicResistance - ((112 + 40) * 1.20 + 8) * 1.12) < 1e-9)
  assert.equal(stats.physicalDamageReductionPoints, 2)
})

test('装备固定回血随穿戴者中境界升档，装备目录保留基础值', async () => {
  const { resolveAttributesCore } = await import('./attributeResolver.ts')
  const { buildSimulatorCatalogPayload } = await import('../../../../../scripts/gen-simulator-catalog.mjs')
  const live = buildGameDataCatalog(buildSimulatorCatalogPayload())
  const id = 'equipment.shouya-pendant'
  const roots = { staminaExp: 0, meridiansExp: 0, attackExp: 0, boneExp: 0, agilityExp: 0, consciousnessExp: 0 }
  const linggen = { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 }
  assert.equal(live.rawEquipments.get(id).flatStats.hitpointRegen, 3)
  for (const [level, expected] of [[1, 3], [6, 3], [7, 6], [9, 6], [18, 9], [19, 9],
    [22, 12], [25, 15], [28, 18], [30, 18], [31, 18], [34, 21], [127, 90]]) {
    const bare = resolveAttributesCore(level, [], [], [], null, roots, [], live, null, null, null, linggen)
    const geared = resolveAttributesCore(level, [id], [{ itemId: id, enhancementLevel: 0 }],
      [], null, roots, [], live, null, null, null, linggen)
    assert.ok(Math.abs(geared.hpRegen - bare.hpRegen - expected) < 1e-9, `Lv${level} 装备回血应为 ${expected}`)
    assert.equal(geared.mpRegen, bare.mpRegen)
  }
})

test('listCombatNodeIds 严格按可用讨伐行动过滤非战斗节点与无战斗节点地图', () => {
  const validNodes = listCombatNodeIds(catalog)
  assert.ok(validNodes.includes('node.yunxi-village'), '有效战斗节点应保留在清单内')
  assert.equal(
    validNodes.includes('node.tea-house'),
    false,
    '即使配有 encounterIds 的非讨伐节点（如望云茶肆）也不应出现在可用战斗节点中',
  )

  // 人造仅有遭遇池但未配置讨伐行动的孤立节点，确保不会被纳入
  const cloned = buildGameDataCatalog({
    ...catalogJson,
    maps: [
      ...(catalogJson.maps ?? []),
      {
        mapId: 'map.non_combat_only',
        name: '纯商贸地图',
        nodes: [
          {
            nodeId: 'node.pure-market',
            name: '纯坊市节点',
            encounterIds: ['encounter.twilight-pass'],
          },
        ],
      },
    ],
  })
  const clonedValid = listCombatNodeIds(cloned)
  assert.equal(clonedValid.includes('node.pure-market'), false, '无可用讨伐行动的地图节点应被彻底过滤')
})

test('runSimulation 返回纯净真实修为 lifetimeExperience，不受底蕴 3 倍燃烧增幅污染', () => {
  const baseRequest = structuredClone(parityCases.runCases[0].request)
  const noFoundationReq = structuredClone(baseRequest)
  noFoundationReq.members[0].foundation = 0
  const highFoundationReq = structuredClone(baseRequest)
  highFoundationReq.members[0].foundation = 1_000_000

  const noFoundationRes = runSimulation(noFoundationReq, catalog)
  const highFoundationRes = runSimulation(highFoundationReq, catalog)

  const m0 = noFoundationRes.members[0]
  const m1 = highFoundationRes.members[0]
  assert.ok(m0.lifetimeExperience > 0, '基线真实修为产出应大于 0')
  assert.ok(m1.experience > m0.experience, '带底蕴时面板 experience 含燃烧增幅，应高于无底蕴')
  assert.equal(
    m1.lifetimeExperience,
    m0.lifetimeExperience,
    '真实修为 lifetimeExperience 不得包含底蕴燃烧增幅',
  )
})

test('useSimulatorStore 支持配置与推演结果会话缓存，resetSession 可恢复初始态', () => {
  useSimulatorStore.getState().resetSession()
  const initState = useSimulatorStore.getState()
  assert.equal(initState.initialized, false)
  assert.equal(initState.activeTab, 'config')
  assert.equal(initState.profileSubTab, 'profile')
  assert.equal(initState.results, null)

  initState.setInitialized(true)
  initState.setActiveTab('results')
  initState.setProfileSubTab('attributes')
  initState.setSelectedNodeIds(['node.yunxi-village'])

  const cached = useSimulatorStore.getState()
  assert.equal(cached.initialized, true)
  assert.equal(cached.activeTab, 'results')
  assert.equal(cached.profileSubTab, 'attributes')
  assert.deepEqual(cached.selectedNodeIds, ['node.yunxi-village'])

  cached.resetSession()
  const reset = useSimulatorStore.getState()
  assert.equal(reset.initialized, false)
  assert.equal(reset.activeTab, 'config')
  assert.equal(reset.profileSubTab, 'profile')
  assert.deepEqual(reset.selectedNodeIds, [])
  assert.equal(reset.results, null)
})

test('getRootPointsForExp 与引擎 RealmProgressionCatalog.rootLevelFor 在 0..381 全点数范围严格一致', () => {
  assert.equal(ROOT_LEVEL_CAP, 381)
  assert.equal(getRootPointsForExp(-5), 0)
  assert.equal(getRootPointsForExp(Number.NaN), 0)
  for (let p = 0; p <= ROOT_LEVEL_CAP; p += 1) {
    const exp = getRootCumulativeExpForLevel(p)
    assert.equal(
      getRootPointsForExp(exp),
      p,
      `点数 ${p} 查表经验 ${exp} 反算点数应为 ${p}`,
    )
    assert.equal(
      catalog.realmProgression.rootLevelFor(exp),
      p,
      `点数 ${p} 经验 ${exp} 与引擎 rootLevelFor 必须一致`,
    )
    if (p < ROOT_LEVEL_CAP) {
      const nextExp = getRootCumulativeExpForLevel(p + 1)
      const midExp = exp + (nextExp - exp) * 0.5
      assert.equal(
        getRootPointsForExp(midExp),
        p,
        `段间小数经验 ${midExp} 反算点数应停留在当前点数 ${p}`,
      )
      assert.equal(catalog.realmProgression.rootLevelFor(midExp), p)
    }
  }
})

test('calculateAbilityLevelAndProgress 与 abilityLevelForExp 在 1..127 全等级立方边界精确一致', () => {
  assert.equal(calculateAbilityLevelAndProgress(-5).level, 1)
  assert.equal(calculateAbilityLevelAndProgress(Number.NaN).level, 1)
  assert.equal(abilityLevelForExp(-5), 1)
  assert.equal(abilityLevelForExp(Number.NaN), 1)

  for (let level = 1; level <= 127; level += 1) {
    const k = level - 1
    const threshold = 100 * k * k * k
    const info = calculateAbilityLevelAndProgress(threshold)
    assert.equal(info.level, level, `阈值 ${threshold} 处 calculateAbilityLevelAndProgress 等级应为 ${level}`)
    assert.equal(info.currentProgress, 0, `阈值 ${threshold} 处当前进度应严格为 0`)
    assert.equal(abilityLevelForExp(threshold), level, `阈值 ${threshold} 处 abilityLevelForExp 等级应为 ${level}`)

    if (level > 1) {
      const below = threshold - 1
      assert.equal(calculateAbilityLevelAndProgress(below).level, level - 1)
      assert.equal(abilityLevelForExp(below), level - 1)
    }
  }
  assert.equal(calculateAbilityLevelAndProgress(1e12).level, 127)
  assert.equal(abilityLevelForExp(1e12), 127)
})

test('combatStats 按五行维度完整守恒累计造成与承受伤害', () => {
  const spellCase = parityCases.runCases.find(c => c.name === 'solo_spell_dot_shield_2h')
  assert.ok(spellCase, '应存在 solo_spell_dot_shield_2h 黄金样本')
  const res = runSimulation(spellCase.request, catalog)
  const stats = res.members[0].combatStats
  assert.ok(stats.length > 0)

  let playerElementalSum = 0
  for (const row of stats) {
    const dealtTotal = row.damageDealtPhysical + row.damageDealtSpell + row.damageDealtTrue
    const dealtElem = row.damageDealtByElement
    const dealtElemSum = dealtElem.none + dealtElem.metal + dealtElem.wood + dealtElem.water + dealtElem.fire + dealtElem.earth
    assert.ok(
      Math.abs(dealtTotal - dealtElemSum) <= 1e-6,
      `${row.unitKey} 造成伤害五行合计(${dealtElemSum})应等于分类合计(${dealtTotal})`,
    )

    const takenTotal = row.damageTakenPhysical + row.damageTakenSpell + row.damageTakenTrue
    const takenElem = row.damageTakenByElement
    const takenElemSum = takenElem.none + takenElem.metal + takenElem.wood + takenElem.water + takenElem.fire + takenElem.earth
    assert.ok(
      Math.abs(takenTotal - takenElemSum) <= 1e-6,
      `${row.unitKey} 承受伤害五行合计(${takenElemSum})应等于分类合计(${takenTotal})`,
    )

    if (row.unitKey === 'player') {
      playerElementalSum = dealtElem.metal + dealtElem.wood + dealtElem.water + dealtElem.fire + dealtElem.earth
    }
  }
  assert.ok(playerElementalSum > 0, '火法燃魂样本应记录到非零的五行属性伤害')
})

test('消耗品条件求值（妖王/精英阶层、难度缩放百分比、战内 buff-stacks 不双计）与空集合唤醒严格对齐', () => {
  const emptyCaster = new CombatantState('player', {
    maxHitpoints: 100,
    maxManapoints: 100,
    attackPower: 10,
    spellAttack: 10,
    damageKind: 0,
    element: 0,
    physicalArmor: 0,
    spellResistance: 0,
    fixedDamageReduction: 0,
    accuracy: 1,
    evasion: 0,
    criticalChance: 0,
    criticalDamageBonusPoints: 50,
    criticalAvoidance: 0,
    breakValue: 0,
    parryValue: 0,
    attackIntervalSeconds: 2,
    hitpointRegen: 0,
    manapointRegen: 0,
    skillBonusPoints: 0,
    healingBonusPoints: 0,
    takenHealingBonusPoints: 0,
    attackSpeedPoints: 0,
    cooldownReductionPoints: 0,
    damageAbsorptionPoints: 0,
    actionSpeedPoints: 0,
    totalDamageBonusPoints: 0,
    physicalDamageBonusPoints: 0,
    spellDamageBonusPoints: 0,
    metalDamageBonusPoints: 0,
    woodDamageBonusPoints: 0,
    waterDamageBonusPoints: 0,
    fireDamageBonusPoints: 0,
    earthDamageBonusPoints: 0,
    metalDamageReductionPoints: 0,
    woodDamageReductionPoints: 0,
    waterDamageReductionPoints: 0,
    fireDamageReductionPoints: 0,
    earthDamageReductionPoints: 0,
    lifestealPoints: 0,
    reflectPoints: 0,
    threatOnAttackPoints: 0,
    threatOnTakenPoints: 0,
    traits: null,
    traitLevels: null,
  })
  assert.equal(
    evaluateTriggers(
      [{ dependency: 'enemies', condition: 'total-hp-percentage', comparator: 'less-than-equal', value: 50 }],
      emptyCaster,
      null,
      [emptyCaster],
      [],
      0,
    ),
    false,
    '空敌方集合时 total-hp-percentage 不得误判为 0 <= 50',
  )
  const wakeMask = resolveTriggerListWakeMask(
    [{ dependency: 'enemies', condition: 'total-hp-percentage', comparator: 'less-than-equal', value: 50 }],
    false,
  )
  assert.equal(
    (wakeMask.enemySide & UnitChannel.Alive) !== 0,
    true,
    'total-hp-percentage 唤醒掩码必须包含 UnitChannel.Alive',
  )

  const baseReq = parityCases.runCases.find(c => c.name === 'solo_spell_dot_shield_2h')?.request
  assert.ok(baseReq, '应存在基准模拟请求模板')

  const makeBossCatalog = (rank) => {
    const cloned = structuredClone(catalogJson)
    const templateKey = Object.keys(cloned.monsters)[0]
    assert.ok(templateKey)
    cloned.monsters['enemy.test-rank'] = {
      ...structuredClone(cloned.monsters[templateKey]),
      id: 'enemy.test-rank',
      name: '测试阶层妖兽',
      rank,
      budgetCost: 1,
      weights: { hp: 200, mp: 1, ad: 0.01, ap: 0.01, armor: 0, mr: 0 },
      attackIntervalMilliseconds: 5000,
      skills: [],
      dropTable: [],
    }
    const combatAction = cloned.combatActions.find(
      a => Array.isArray(a.availableNodeIds) && a.availableNodeIds.includes(baseReq.nodeId),
    )
    assert.ok(combatAction)
    const enc = cloned.encounters.find(e => e.id === combatAction.combatEncounterId)
    assert.ok(enc)
    enc.minBudget = 1
    enc.maxBudget = 1
    enc.maxTotalStrength = 1
    enc.searchMilliseconds = 1000
    enc.randomSpawns = [{ enemyId: 'enemy.test-rank', weight: 1, strength: 1 }]
    enc.eliteEnemyIds = []
    enc.bossEnemyIds = []
    enc.battlesPerBoss = 0
    return buildGameDataCatalog(cloned)
  }

  const runWithTriggers = (simCatalog, consumableTriggers, difficulty = 0) => {
    const baseMember = structuredClone(baseReq.members[0])
    baseMember.buffs = []
    baseMember.combatBuffCarry = []
    baseMember.hitpoints = 99999
    baseMember.manapoints = 99999
    baseMember.combatAutoUseSlots = [
      {
        actionType: 'combat',
        slotIndex: 0,
        itemId: 'item.zhixue-powder',
        quantity: 1,
        enabled: true,
        remainingDurationSeconds: 0,
        cooldownUntilUnixSeconds: 0,
        group: 'potion',
      },
      {
        actionType: 'combat',
        slotIndex: 0,
        itemId: 'item.leiji-talisman',
        quantity: 1,
        enabled: true,
        remainingDurationSeconds: 0,
        cooldownUntilUnixSeconds: 0,
        group: 'general',
      },
    ]
    baseMember.consumableTriggers = consumableTriggers
    baseMember.assets = [
      { itemId: 'item.zhixue-powder', variant: '', quantity: 5 },
      { itemId: 'item.leiji-talisman', variant: '', quantity: 5 },
    ]
    const req = {
      ...structuredClone(baseReq),
      difficulty,
      durationSeconds: 3,
      members: [baseMember],
    }
    const res = runSimulation(req, simCatalog)
    const leijiDelta = res.members[0].assetDeltas.find(d => d.itemId === 'item.leiji-talisman')?.quantity ?? 0
    return Math.abs(leijiDelta)
  }

  const bossCatalog = makeBossCatalog('boss')
  const minionCatalog = makeBossCatalog('minion')

  assert.equal(
    runWithTriggers(bossCatalog, {
      'item.zhixue-powder': [{ dependency: 'self', condition: 'buff-stacks', buffId: 'buff.zhixue', comparator: 'equal', value: 0 }],
      'item.leiji-talisman': [
        { dependency: 'target-enemy', condition: 'is-boss', comparator: 'is-active', value: 1 },
        { dependency: 'enemies', condition: 'has-boss', comparator: 'is-active', value: 1 },
        { dependency: 'target-enemy', condition: 'current-hp-percentage', comparator: 'less-than-equal', value: 100 },
        { dependency: 'self', condition: 'buff-stacks', buffId: 'buff.zhixue', comparator: 'equal', value: 1 },
      ],
    }, 2),
    1,
    'Boss + 难度2 + 止血散1层buff 组合条件应全部精确命中并激发雷击符',
  )

  assert.equal(
    runWithTriggers(minionCatalog, {
      'item.leiji-talisman': [{ dependency: 'target-enemy', condition: 'is-boss', comparator: 'is-active', value: 1 }],
    }, 0),
    0,
    '普通怪遭遇下 target-enemy is-boss 不得触发雷击符',
  )
})

test('切片2&3同源回归：复活策略枚举、highest-hp按百分比筛选、零层蓄意校验、普攻攻速Buff与常驻Buff唤醒', () => {
  assert.equal(CombatRespawnPolicy.Immediate, 0, 'Immediate 必须与服务端 0 对齐')
  assert.equal(CombatRespawnPolicy.WaitAll, 1, 'WaitAll 必须与服务端 1 对齐')

  const makeStats = (maxHitpoints) =>
    createCombatantStats(maxHitpoints, 10, 0, 2, 1, 0, 0, {
      maxManapoints: 100,
      spellAttack: 10,
    })

  // 1. highest-hp 按百分比而非绝对生命筛选（200/1000 = 20% < 90/100 = 90%）
  const allyLowRatio = new CombatantState('player', makeStats(1000), [])
  allyLowRatio.hitpoints = 200
  const allyHighRatio = new CombatantState('ally:1', makeStats(100), [])
  allyHighRatio.hitpoints = 90
  const enemyLowRatio = new CombatantState('enemy:0', makeStats(1000), [])
  enemyLowRatio.hitpoints = 200
  const enemyHighRatio = new CombatantState('enemy:1', makeStats(100), [])
  enemyHighRatio.hitpoints = 90

  assert.equal(
    evaluateTriggers(
      [{ dependency: 'ally-highest-hp', condition: 'current-hp-percentage', comparator: 'greater-than', value: 80 }],
      allyLowRatio,
      enemyLowRatio,
      [allyLowRatio, allyHighRatio],
      [enemyLowRatio, enemyHighRatio],
      0,
    ),
    true,
    'ally-highest-hp 应选出血量比例最高的盟友（90%），而非绝对血量更高的 20% 单位',
  )
  assert.equal(
    evaluateTriggers(
      [{ dependency: 'enemy-highest-hp', condition: 'current-hp-percentage', comparator: 'greater-than', value: 80 }],
      allyLowRatio,
      enemyLowRatio,
      [allyLowRatio, allyHighRatio],
      [enemyLowRatio, enemyHighRatio],
      0,
    ),
    true,
    'enemy-highest-hp 应选出血量比例最高的敌方（90%），而非绝对血量更高的 20% 单位',
  )

  // 2. pendingActionDurationOf 计入战内攻击速度加成
  assert.equal(pendingActionDurationOf(allyLowRatio), 2)
  allyLowRatio.addBuff({
    buffId: 'buff.attack-speed-test',
    expiresAtSeconds: 10,
    stacks: 1,
    name: '疾风测试',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [{ channel: BuffEffectChannel.AttackSpeedPoints, value: 100 }],
    statRatios: null,
    sourceUnitId: null,
  })
  assert.equal(pendingActionDurationOf(allyLowRatio), 1, '+100 战内攻速加成应将 2s 普攻间隔压半为 1s')

  // 3. 常驻 Buff（BATTLE_LIFETIME_SECONDS）不得吞没有限时长 Buff 的刷新唤醒
  const permBuff = {
    buffId: 'buff.perm-test',
    expiresAtSeconds: BATTLE_LIFETIME_SECONDS,
    stacks: 1,
    name: '常驻测试',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [],
    statRatios: null,
    sourceUnitId: null,
  }
  const finiteBuff = {
    buffId: 'buff.finite-test',
    expiresAtSeconds: 10,
    stacks: 1,
    name: '有限测试',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [],
    statRatios: null,
    sourceUnitId: null,
  }
  const encounter = CombatEncounterState.create('encounter.test', 1n, { stats: makeStats(100), skills: [] }, [])
  encounter.player.addBuff(permBuff)
  encounter.player.addBuff(finiteBuff)
  const tracker = new CombatFactTracker(encounter)
  tracker.advance(encounter)
  encounter.player.replaceBuffs([permBuff, { ...finiteBuff, expiresAtSeconds: 15 }])
  const delta = tracker.advance(encounter)
  assert.equal((delta.allyUnion & UnitChannel.Buff) !== 0, true, '常驻 Buff 存在时有限 Buff 续期仍必须触发 UnitChannel.Buff')

  // 4. combatBuffCarry 允许 0 层携带（如满血蓄意导出预演）
  const memberWithZeroStackCarry = structuredClone(parityCases.runCases[0].request.members[0])
  memberWithZeroStackCarry.combatBuffCarry = [
    {
      buffId: 'buff.test-xuyi',
      remainingSeconds: 6,
      stacks: 0,
      name: '蓄意',
      missingHitpointsPerStack: 0,
    },
  ]
  assert.doesNotThrow(
    () => validateMember(memberWithZeroStackCarry, 0, catalog),
    'validateMember 应允许 stacks === 0 的合法战内增益携带项',
  )
})

test('移形位移体系、剑与心特质与三新技能模拟行为正确', () => {
  const liveCatalogJson = JSON.parse(
    fs.readFileSync(path.resolve(__dirname, '../data/simulatorCatalog.json'), 'utf8'),
  )
  const liveCatalog = buildGameDataCatalog(liveCatalogJson)
  const yueqian = liveCatalog.abilities.get('ability.yueqian')
  const zhanyu = liveCatalog.abilities.get('ability.zhanyu')
  const huoyan = liveCatalog.abilities.get('ability.huoyan-fengbao')
  assert.ok(yueqian && zhanyu && huoyan, 'simulatorCatalog.json 应包含跃迁、斩玉与火焰风暴')

  const makeUnitStats = (overrides = {}) =>
    createCombatantStats(
      overrides.maxHitpoints ?? 5000,
      overrides.attackPower ?? 100,
      overrides.defense ?? 0,
      overrides.attackIntervalSeconds ?? 100,
      overrides.accuracy ?? 1000,
      overrides.evasion ?? 10,
      CombatDamageKind.Physical,
      {
        maxManapoints: 500,
        spellAttack: 100,
        ...overrides,
      },
    )

  // 1. 跃迁付与全体友方 5s「移形」（2× 闪避），斩玉切态为「踏云」打全体敌方并消耗 1 层「移形」
  const teamEncounter = CombatEncounterState.createAllied(
    'sim-yixing',
    7n,
    [
      {
        stats: makeUnitStats({ evasion: 10, attackIntervalSeconds: 1.6 }),
        skills: [
          {
            ability: zhanyu,
            abilityId: 'ability.zhanyu',
            level: 1,
            triggers: [{ dependency: 'self', condition: 'buff-stacks', comparator: 'greater-than-equal', value: 1, buffId: 'buff.yixing' }],
          },
        ],
      },
      {
        stats: makeUnitStats({ evasion: 15 }),
        skills: [{ ability: yueqian, abilityId: 'ability.yueqian', level: 1, triggers: [] }],
      },
    ],
    0,
    [
      { stats: makeUnitStats({ maxHitpoints: 20000 }), skills: [] },
      { stats: makeUnitStats({ maxHitpoints: 20000 }), skills: [] },
    ],
  )
  decideInitialActions(teamEncounter)
  advanceCombatTo(teamEncounter, 3.0)
  assert.equal(teamEncounter.player.buffStacks('buff.yixing'), 1)
  assert.equal(teamEncounter.player.effectiveStats.evasion, 20)
  assert.equal(teamEncounter.playerAllies[1].effectiveStats.evasion, 30)

  const morphRes = advanceCombatTo(teamEncounter, 3.8)
  const tayunEvents = morphRes.events.filter(e => e.abilityId === 'ability.zhanyu')
  assert.equal(tayunEvents.length, 2, '拥有移形时斩玉应切换为踏云命中 2 名敌人')
  assert.ok(tayunEvents.every(e => e.abilityNameOverride === '踏云'))
  assert.equal(teamEncounter.player.buffStacks('buff.yixing'), 0, '踏云只应消耗 1 层移形')

  // 2. 普攻不消耗移形
  const basicOnly = CombatEncounterState.create(
    'sim-basic-no-consume',
    1n,
    { stats: makeUnitStats({ attackIntervalSeconds: 1.0 }), skills: [] },
    [{ stats: makeUnitStats({ maxHitpoints: 20000 }), skills: [] }],
  )
  basicOnly.player.setBuff({
    buffId: 'buff.yixing',
    expiresAtSeconds: 10,
    stacks: 2,
    name: '移形',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [{ channel: BuffEffectChannel.EvasionPostRatio, value: 1.0 }],
    statRatios: null,
    sourceUnitId: 'player',
  })
  decideInitialActions(basicOnly)
  advanceCombatTo(basicOnly, 1.5)
  assert.equal(basicOnly.player.buffStacks('buff.yixing'), 2, '普通攻击不应消耗移形层数')

  // 3. 剑与心 + 移形：无视高仇恨随机选主目标（含群体 + 蓄意真伤）
  const jianyuxinTargets = new Set()
  for (let s = 1n; s <= 25n; s++) {
    const enc = CombatEncounterState.create(
      'sim-jianyuxin',
      s,
      {
        stats: makeUnitStats({ maxHitpoints: 100, traits: ['trait.jianyuxin', 'trait.xuyi'] }),
        skills: [{ ability: huoyan, abilityId: 'ability.huoyan-fengbao', level: 1, triggers: [] }],
      },
      [
        { stats: makeUnitStats({ maxHitpoints: 10000 }), skills: [] },
        { stats: makeUnitStats({ maxHitpoints: 10000 }), skills: [] },
        { stats: makeUnitStats({ maxHitpoints: 10000 }), skills: [] },
      ],
    )
    enc.enemies[0].threat = 0
    enc.enemies[1].threat = 1_000_000
    enc.enemies[2].threat = 0
    enc.player.setBuff({
      buffId: 'buff.yixing',
      expiresAtSeconds: 10,
      stacks: 1,
      name: '移形',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: [{ channel: BuffEffectChannel.EvasionPostRatio, value: 1.0 }],
      statRatios: null,
      sourceUnitId: 'player',
    })
    enc.player.setBuff({
      buffId: 'buff.xuyi',
      expiresAtSeconds: BATTLE_LIFETIME_SECONDS,
      stacks: 200,
      name: '蓄意',
      resetOnWave: true,
      missingHitpointsPerStack: 0,
      effects: [],
      statRatios: null,
      sourceUnitId: null,
    })
    decideInitialActions(enc)
    const r = advanceCombatTo(enc, 3.1)
    const trueBurst = r.events.find(e => e.abilityId === 'trait.xuyi' && e.damageKind === 2)
    assert.ok(trueBurst)
    jianyuxinTargets.add(trueBurst.targetId)
  }
  assert.ok(jianyuxinTargets.has('enemy:0') && jianyuxinTargets.has('enemy:2'), '剑与心在移形下应无视高仇恨随机选取主目标')

  // 4. 火焰风暴按公式叠加燃魂并与特质燃魂同池累加
  const fireEnc = CombatEncounterState.create(
    'sim-huoyan',
    5n,
    {
      stats: makeUnitStats({ skillBonusPoints: 50, traits: ['trait.ranhun'] }),
      skills: [{ ability: huoyan, abilityId: 'ability.huoyan-fengbao', level: 2, triggers: [] }],
    },
    [{ stats: makeUnitStats({ maxHitpoints: 20000 }), skills: [] }],
  )
  decideInitialActions(fireEnc)
  advanceCombatTo(fireEnc, 3.1)
  assert.equal(fireEnc.enemies[0].buffStacks('buff.ranhun'), 39)
  const tickRes = advanceCombatTo(fireEnc, 4.1)
  const dot = tickRes.events.find(e => e.abilityId === 'trait.ranhun')
  assert.ok(dot && Math.abs(dot.damage - (39 * 39) / 40) <= 1e-6)
})

test('火神特质：全伤害转火（不改物理/术法/真伤类别）、等级缩放、衍生阵盘与符箓均按火属性结算', async () => {
  const rules = await import('./rules.ts')
  const { CombatUnitStatsBook } = await import('./combatModels.ts')

  assert.equal(rules.TraitIds.Huoshen, 'trait.huoshen')
  assert.equal(rules.ALL_TRAITS.size, 47)
  const huoshenDef = rules.ALL_TRAITS.get('trait.huoshen')
  assert.ok(huoshenDef && huoshenDef.quality === 5 && huoshenDef.cost === 12)

  // 1. 等级成长：10 点火属性增伤按 1 + 0.05 * (Lv - 1) 缩放
  for (const [level, expected] of [[1, 10], [2, 10.5], [3, 11]]) {
    const ratios = new Map()
    const points = new Map()
    rules.accumulateTraitModifiers(['trait.huoshen'], { 'trait.huoshen': level }, ratios, points)
    assert.equal(ratios.size, 0)
    assert.ok(Math.abs((points.get('fireDamageBonusPoints') ?? 0) - expected) <= 1e-12)
  }

  const liveCatalogJson = JSON.parse(
    fs.readFileSync(path.resolve(__dirname, '../data/simulatorCatalog.json'), 'utf8'),
  )
  const liveCatalog = buildGameDataCatalog(liveCatalogJson)
  const palmLightning = liveCatalog.abilities.get('ability.palm-lightning')
  assert.ok(palmLightning, 'simulatorCatalog.json 应包含 ability.palm-lightning')

  // 2. 普攻(物理/无属性) -> Physical + Fire；木法掌心雷(术法/木) -> Spell + Fire，均按火统计并触发燃魂
  const basicStats = createCombatantStats(5000, 100, 0, 1.0, 1_000_000, 0, CombatDamageKind.Physical, {
    fireDamageBonusPoints: 10,
    traits: ['trait.huoshen', 'trait.ranhun'],
  })
  const enemyArmoredFire = createCombatantStats(10_000, 10, 100, 100, 0, 0, CombatDamageKind.Physical, {
    magicResistance: 0,
    fireDamageReductionPoints: 100,
  })
  const basicEnc = CombatEncounterState.create(
    'sim-huoshen-basic',
    1n,
    { stats: basicStats, skills: [] },
    [{ stats: enemyArmoredFire, skills: [] }],
  )
  basicEnc.statsBook = new CombatUnitStatsBook()
  decideInitialActions(basicEnc)
  const basicHit = advanceCombatTo(basicEnc, 1.1).events[0]
  const expectedBasic = ((100 * 100) / 201) * 1.1 / 2
  assert.equal(basicHit.damageKind, CombatDamageKind.Physical)
  assert.ok(Math.abs(basicHit.damage - expectedBasic) <= 1e-8)
  assert.equal(basicEnc.enemies[0].buffStacks('buff.ranhun'), 1)
  const basicBookPlayer = basicEnc.statsBook.entries.find(e => e.unitKey === 'player')
  assert.ok(basicBookPlayer)
  assert.ok(Math.abs(basicBookPlayer.damageDealtByElement.fire - expectedBasic) <= 1e-8)
  assert.equal(basicBookPlayer.damageDealtByElement.none, 0)

  const spellStats = createCombatantStats(5000, 10, 0, 100, 1_000_000, 0, CombatDamageKind.Physical, {
    spellAttack: 100,
    maxManapoints: 500,
    fireDamageBonusPoints: 10,
    traits: ['trait.huoshen', 'trait.ranhun'],
  })
  const spellEnemy = createCombatantStats(10_000, 10, 0, 100, 0, 0, CombatDamageKind.Physical, {
    magicResistance: 100,
    fireDamageReductionPoints: 0,
  })
  const spellEnc = CombatEncounterState.create(
    'sim-huoshen-spell',
    2n,
    { stats: spellStats, skills: [{ ability: palmLightning, abilityId: 'ability.palm-lightning', level: 1, triggers: [] }] },
    [{ stats: spellEnemy, skills: [] }],
  )
  spellEnc.statsBook = new CombatUnitStatsBook()
  decideInitialActions(spellEnc)
  const spellHit = advanceCombatTo(spellEnc, 2.1).events[0]
  const expectedSpell = ((110 * 110) / 211) * 1.1
  assert.equal(spellHit.damageKind, CombatDamageKind.Spell)
  assert.ok(Math.abs(spellHit.damage - expectedSpell) <= 1e-8)
  assert.equal(spellEnc.enemies[0].buffStacks('buff.ranhun'), 1)
  const spellBookPlayer = spellEnc.statsBook.entries.find(e => e.unitKey === 'player')
  assert.ok(spellBookPlayer)
  assert.ok(Math.abs(spellBookPlayer.damageDealtByElement.fire - expectedSpell) <= 1e-8)
  assert.equal(spellBookPlayer.damageDealtByElement.wood, 0)

  // 3. 阵盘受击反击（天雷原木法、血煞原无属性物理）均转火、吃火增伤、计入火统计并联动燃魂
  const diskEnc = CombatEncounterState.create(
    'sim-huoshen-disk',
    1n,
    {
      stats: createCombatantStats(2000, 50, 0, 0.5, 1_000_000, 0, CombatDamageKind.Physical, {
        spellAttack: 100,
        maxManapoints: 200,
        fireDamageBonusPoints: 10,
        traits: ['trait.huoshen', 'trait.ranhun'],
      }),
      skills: [],
    },
    [{
      stats: createCombatantStats(1_000_000, 10, 0, 100, 0, 0, CombatDamageKind.Physical, { magicResistance: 0 }),
      skills: [],
    }],
  )
  diskEnc.statsBook = new CombatUnitStatsBook()
  diskEnc.player.manapoints = 200
  const permBuff = (buffId) => ({
    buffId,
    expiresAtSeconds: 300,
    stacks: 1,
    name: buffId,
    resetOnWave: false,
    missingHitpointsPerStack: 0,
    effects: [],
    statRatios: null,
    sourceUnitId: null,
  })
  diskEnc.player.addBuff(permBuff('formation.tianlei'))
  diskEnc.player.addBuff(permBuff('formation.xuesha'))
  decideInitialActions(diskEnc)
  const diskRes = advanceCombatTo(diskEnc, 30)
  const tianleiHit = diskRes.events.find(e => e.abilityId === 'item.tianlei-formation-disk')
  const xueshaHit = diskRes.events.find(e => e.abilityId === 'item.xuesha-formation-disk')
  const diskRanhun = diskEnc.enemies[0].buffStacks('buff.ranhun')
  const diskBookRow = diskEnc.statsBook.entries.find(e => e.unitKey === 'player')
  assert.ok(tianleiHit && xueshaHit, '30 秒高频受击应触发天雷与血煞阵盘')
  assert.equal(tianleiHit.damageKind, CombatDamageKind.Spell)
  assert.ok(Math.abs(tianleiHit.damage - ((60 * 60) / 61) * 1.1) <= 1e-8)
  assert.equal(xueshaHit.damageKind, CombatDamageKind.Physical)
  assert.ok(Math.abs(xueshaHit.damage - ((200 * 200) / 201) * 1.1) <= 1e-8)
  assert.ok(diskBookRow)
  assert.equal(diskBookRow.damageDealtByElement.wood, 0)
  assert.equal(diskBookRow.damageDealtByElement.none, 0)
  const allDiskPlayerDmg = diskRes.events
    .filter(e => e.attackerId === 'player')
    .reduce((sum, e) => sum + e.damage, 0)
  assert.ok(Math.abs(diskBookRow.damageDealtByElement.fire - allDiskPlayerDmg) <= 1e-8)
  assert.ok(diskRanhun > 0)

  // 4. 蓄意爆发：主目标真伤（200，忽略火增减伤但计入火统计），副目标物理溅射转为火系物理并受火增减伤
  const xuyiEnemyStats = createCombatantStats(10_000, 10, 0, 100, 0, 0, CombatDamageKind.Physical, {
    magicResistance: 0,
    fireDamageReductionPoints: 100,
  })
  const xuyiEnc = CombatEncounterState.create(
    'sim-huoshen-xuyi',
    3n,
    {
      stats: createCombatantStats(100, 10, 0, 0.5, 1_000_000, 0, CombatDamageKind.Physical, {
        fireDamageBonusPoints: 10,
        traits: ['trait.huoshen', 'trait.xuyi'],
      }),
      skills: [],
    },
    [{ stats: xuyiEnemyStats, skills: [] }, { stats: xuyiEnemyStats, skills: [] }],
  )
  xuyiEnc.statsBook = new CombatUnitStatsBook()
  xuyiEnc.player.setBuff({
    buffId: 'buff.xuyi',
    expiresAtSeconds: Number.MAX_VALUE,
    stacks: 200,
    name: '蓄意',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [],
    statRatios: null,
    sourceUnitId: null,
  })
  decideInitialActions(xuyiEnc)
  const xuyiEvents = advanceCombatTo(xuyiEnc, 0.6).events.filter(e => e.abilityId === 'trait.xuyi')
  const primaryBurst = xuyiEvents.find(e => e.damageKind === CombatDamageKind.True)
  const splashBurst = xuyiEvents.find(e => e.damageKind === CombatDamageKind.Physical)
  assert.ok(primaryBurst && splashBurst)
  assert.equal(primaryBurst.targetId, 'enemy:0')
  assert.equal(primaryBurst.damage, 200)
  assert.equal(splashBurst.targetId, 'enemy:1')
  assert.ok(Math.abs(splashBurst.damage - (((200 * 200) / 201) * 1.1) / 2) <= 1e-8)
  const xuyiBook = xuyiEnc.statsBook.entries.find(e => e.unitKey === 'player')
  assert.ok(xuyiBook)
  assert.equal(xuyiBook.damageDealtByElement.none, 0)
  assert.ok(xuyiBook.damageDealtByElement.fire >= primaryBurst.damage + splashBurst.damage)

  // 5. 端到端单人与组队消耗品（雷击符原 wood -> fire，不留 wood/none 伤害）
  const soloTemplate = parityCases.runCases.find(c => c.mode === 'solo' || c.request.members.length === 1)
  assert.ok(soloTemplate)
  const huoshenSoloReq = structuredClone(soloTemplate.request)
  const m0 = huoshenSoloReq.members[0]
  m0.traitIds = ['trait.huoshen', 'trait.ranhun']
  m0.traitLevels = { 'trait.huoshen': 2, 'trait.ranhun': 1 }
  m0.traitUnlockedIds = ['trait.huoshen', 'trait.ranhun']
  m0.activeSkills = []
  m0.consumableSlots = [{ itemId: 'item.leiji-talisman', actionType: 'combat', slotGroup: 'general', slotIndex: 0 }]
  m0.assets = [...(m0.assets ?? []), { itemId: 'item.leiji-talisman', variant: '', quantity: 5 }]
  huoshenSoloReq.environment.gameDataFingerprint = liveCatalog.fingerprint
  m0.gameDataFingerprint = liveCatalog.fingerprint
  huoshenSoloReq.durationSeconds = 90
  const soloRes = runSimulation(huoshenSoloReq, liveCatalog)
  const soloPlayerStat = soloRes.members[0].combatStats.find(r => r.unitKey === 'player')
  assert.ok(soloPlayerStat)
  assert.equal(soloPlayerStat.damageDealtByElement.wood, 0, '持有火神后雷击符不应留下木系伤害统计')
  assert.equal(soloPlayerStat.damageDealtByElement.none, 0, '持有火神后普攻不应留下无属性伤害统计')
  assert.ok(soloPlayerStat.damageDealtByElement.fire > 0)
  assert.ok(soloPlayerStat.damageDealtSpell > 0, '雷击符转火后仍应计入术法输出')
})

test('雾梦泽 20 级五技能在 TS 模拟器中与服务端语义 1:1 一致（天雷破/灼心/瞬闪/山鬼谣/破军）', () => {
  const liveCatalogJson = JSON.parse(
    fs.readFileSync(path.resolve(__dirname, '../data/simulatorCatalog.json'), 'utf8'),
  )
  const liveCatalog = buildGameDataCatalog(liveCatalogJson)

  const tianleiPo = liveCatalog.abilities.get('ability.tianlei-po')
  const zhuoxin = liveCatalog.abilities.get('ability.zhuoxin')
  const shunshan = liveCatalog.abilities.get('ability.shunshan')
  const shanguiYao = liveCatalog.abilities.get('ability.shangui-yao')
  const pojun = liveCatalog.abilities.get('ability.pojun')
  assert.ok(tianleiPo && zhuoxin && shunshan && shanguiYao && pojun)

  // 1. 天雷破：至多 2 名目标受 5s 麻痹（闪避/化解 ×0.8，行动速度 -10，不吃成长）
  const spellBase = createCombatantStats(2000, 10, 100, 100, 1_000_000, 0, CombatDamageKind.Spell, {
    maxManapoints: 500,
    accuracy: 1_000_000,
    spellAttack: 100,
  })
  const mabiTarget = createCombatantStats(10_000, 10, 0, 100, 0, 50, CombatDamageKind.Physical, {
    parryResolve: 50,
  })
  const tianleiEnc = CombatEncounterState.create(
    'sim-tianlei-po',
    11n,
    { stats: spellBase, skills: [{ ability: tianleiPo, abilityId: 'ability.tianlei-po', level: 5, triggers: [] }] },
    [{ stats: mabiTarget, skills: [] }, { stats: mabiTarget, skills: [] }, { stats: mabiTarget, skills: [] }],
  )
  decideInitialActions(tianleiEnc)
  const tianleiRes = advanceCombatTo(tianleiEnc, 2.1)
  assert.equal(tianleiRes.events.length, 2)
  const paralysed = tianleiEnc.enemies.filter(e => e.buffStacks('buff.mabi') > 0)
  assert.equal(paralysed.length, 2)
  for (const foe of paralysed) {
    assert.ok(Math.abs(foe.effectiveStats.evasion - 50 / 1.2) <= 1e-9)
    assert.ok(Math.abs(foe.effectiveStats.parryResolve - 50 / 1.2) <= 1e-9)
    assert.ok(Math.abs(foe.effectiveStats.actionSpeedPoints - (-10)) <= 1e-9)
  }

  // 2. 灼心：零直伤，叠加 50% AP + 10 层燃烧
  const zhuoxinCaster = { ...spellBase, fixedSpellDamageBonus: 999, fixedDamageBonus: 999 }
  const zhuoxinEnc = CombatEncounterState.create(
    'sim-zhuoxin',
    12n,
    { stats: zhuoxinCaster, skills: [{ ability: zhuoxin, abilityId: 'ability.zhuoxin', level: 1, triggers: [] }] },
    [{ stats: createCombatantStats(10_000, 10, 0, 100, 0, 0, CombatDamageKind.Physical), skills: [] }],
  )
  decideInitialActions(zhuoxinEnc)
  const zhuoxinHit = advanceCombatTo(zhuoxinEnc, 2.1).events[0]
  assert.equal(zhuoxinHit.hit, true)
  assert.equal(zhuoxinHit.damage, 0)
  assert.equal(zhuoxinEnc.enemies[0].hitpoints, 10_000)
  assert.equal(zhuoxinEnc.enemies[0].buffStacks('buff.ranhun'), 60)

  // 3. 瞬闪：先导移形击杀刷新 CD 且不重施，二连杀后正常入 CD 并重获移形
  const swordBase = createCombatantStats(2000, 100, 0, 100, 1_000_000, 0, CombatDamageKind.Physical, {
    maxManapoints: 500,
    accuracy: 1_000_000,
  })
  const fragile = createCombatantStats(150, 10, 0, 100, 0, 0, CombatDamageKind.Physical)
  const tank = createCombatantStats(10_000, 10, 0, 100, 0, 0, CombatDamageKind.Physical)
  const shunEnc = CombatEncounterState.create(
    'sim-shunshan-reset',
    14n,
    { stats: swordBase, skills: [{ ability: shunshan, abilityId: 'ability.shunshan', level: 1, triggers: [] }] },
    [{ stats: fragile, skills: [] }, { stats: fragile, skills: [] }, { stats: tank, skills: [] }],
  )
  shunEnc.enemies[0].threat = 1000
  shunEnc.enemies[1].threat = 1000
  shunEnc.enemies[2].threat = 0
  shunEnc.player.addBuff({
    buffId: 'buff.yixing',
    expiresAtSeconds: 5.0,
    stacks: 1,
    name: '移形',
    resetOnWave: true,
    missingHitpointsPerStack: 0,
    effects: [{ channel: 15, value: 1 }],
    statRatios: null,
    sourceUnitId: 'player',
  })
  decideInitialActions(shunEnc)
  advanceCombatTo(shunEnc, 0.6)
  assert.equal(shunEnc.enemies.filter(e => !e.isAlive).length, 1)
  assert.equal(shunEnc.player.buffStacks('buff.yixing'), 0)
  assert.equal(shunEnc.player.pendingSkillIndex, 0)

  advanceCombatTo(shunEnc, 1.1)
  assert.equal(shunEnc.enemies.filter(e => !e.isAlive).length, 2)
  assert.equal(shunEnc.player.buffStacks('buff.yixing'), 1)
  assert.ok((shunEnc.player.skillCooldownUntil?.[0] ?? 0) > 20)

  // 4. 山鬼谣：全体友方获得 5 + 12% AP = 17 层甘露
  const shanguiEnc = CombatEncounterState.createAllied(
    'sim-shangui-yao',
    15n,
    [
      { stats: spellBase, skills: [{ ability: shanguiYao, abilityId: 'ability.shangui-yao', level: 1, triggers: [] }] },
      { stats: spellBase, skills: [] },
    ],
    0,
    [{ stats: tank, skills: [] }],
  )
  shanguiEnc.playerAllies[1].hitpoints = 1000
  decideInitialActions(shanguiEnc)
  advanceCombatTo(shanguiEnc, 3.1)
  for (const ally of shanguiEnc.playerAllies) {
    assert.equal(ally.buffStacks('buff.ganlu'), 17)
  }

  // 5. 破军：授予移形与 5 层势不可挡（100 免伤），2/3/5/6s 整秒衰减至移除
  const pojunEnc = CombatEncounterState.create(
    'sim-pojun',
    16n,
    { stats: swordBase, skills: [{ ability: pojun, abilityId: 'ability.pojun', level: 10, triggers: [] }] },
    [{ stats: tank, skills: [] }, { stats: tank, skills: [] }],
  )
  decideInitialActions(pojunEnc)
  advanceCombatTo(pojunEnc, 1.6)
  assert.equal(pojunEnc.player.buffStacks('buff.yixing'), 1)
  assert.equal(pojunEnc.player.buffStacks('buff.shibukedang'), 5)
  assert.equal(pojunEnc.player.buffTotal(11), 100)
  advanceCombatTo(pojunEnc, 2.1)
  assert.equal(pojunEnc.player.buffStacks('buff.shibukedang'), 4)
  advanceCombatTo(pojunEnc, 6.1)
  assert.equal(pojunEnc.player.buffStacks('buff.shibukedang'), 0)
})

test('追忆副本模拟：目录解析、单人/组队十波通关、现世动态等级重算与封魔阵免疫', () => {
  const liveCatalogJson = JSON.parse(
    fs.readFileSync(path.resolve(__dirname, '../data/simulatorCatalog.json'), 'utf8'),
  )
  const liveCatalog = buildGameDataCatalog(liveCatalogJson)
  const dungeons = listDungeonEntries(liveCatalog)
  assert.equal(dungeons.length, 4)
  assert.deepEqual(
    dungeons.map(d => d.id).sort(),
    [
      'dungeon.qingyun-secret-gorge',
      'dungeon.starfall-ancient-ruin',
      'dungeon.trail-boundary-rift',
      'dungeon.wumeng-sword-tomb',
    ],
  )
  for (const d of dungeons) {
    assert.equal(d.totalWaves, 10)
    assert.ok(d.baseLevel >= 1 && d.minMonsterLevel >= 1)
    assert.ok(liveCatalog.worldNodes.has(d.nodeId), `副本 ${d.id} 绑定的世界节点 ${d.nodeId} 应存在于世界目录`)
  }
  const qingyunEntry = dungeons.find(d => d.id === 'dungeon.qingyun-secret-gorge')
  const qingyunDef = liveCatalog.dungeons.get('dungeon.qingyun-secret-gorge')
  assert.ok(qingyunEntry && qingyunDef)
  const guaranteedLoot = qingyunDef.clearLootTable.find(e => e.kind === 'guaranteed')
  assert.ok(guaranteedLoot, '青云秘峡应配置至少 1 项通关保底材料')

  // 构造一名配装齐全的修士（Lv.25 + 强化 +15 成套武修装备与技能），稳定速通青云秘峡虚境与现世凡阶
  const makeStrongMember = (name, realmLevel = 25) => {
    const p = createDefaultProfile(liveCatalog)
    p.displayName = name
    p.realmLevel = realmLevel
    const fullExp = getRootCumulativeExpForLevel(ROOT_LEVEL_CAP)
    p.coreAttributes = {
      staminaExp: fullExp,
      meridiansExp: fullExp,
      attackExp: fullExp,
      boneExp: fullExp,
      agilityExp: fullExp,
      consciousnessExp: fullExp,
    }
    p.equipment = [
      { itemId: 'equipment.pogang-sword', enhancementLevel: 15 },
      { itemId: 'equipment.xuanjia-guard', enhancementLevel: 15 },
      { itemId: 'equipment.chijin-helmet', enhancementLevel: 15 },
      { itemId: 'equipment.liegang-armor', enhancementLevel: 15 },
      { itemId: 'equipment.tafeng-pants', enhancementLevel: 15 },
      { itemId: 'equipment.shouya-pendant', enhancementLevel: 15 },
    ]
    p.abilitySlots = [
      { abilityId: null, level: 1 },
      { abilityId: 'ability.pojun', level: 30 },
      { abilityId: 'ability.shunshan', level: 30 },
      { abilityId: null, level: 1 },
      { abilityId: null, level: 1 },
    ]
    p.maxHitpoints = 50_000
    p.hitpoints = 50_000
    p.maxManapoints = 10_000
    p.manapoints = 10_000
    return p
  }

  const baseEnv = {
    formatVersion: 1,
    gameDataFingerprint: liveCatalog.fingerprint,
    capturedAtUnixSeconds: 1_700_000_000,
    nodes: [
      {
        nodeId: qingyunEntry.nodeId,
        buildings: [],
        effects: {
          combatHitpointRegen: 5,
          combatManapointRegen: 5,
          combatDamageReductionRatio: 0,
          monsterWeakenRatio: 0,
        },
        usages: [],
      },
    ],
  }

  // 1. 单人虚境·普通（d=0）连续挑战 2 次：精确完工停表、发放 2 次通关保底材料与通关灵石
  const soloClearRes = runSimulation({
    formatVersion: 1,
    nodeId: qingyunEntry.nodeId,
    dungeonId: 'dungeon.qingyun-secret-gorge',
    difficulty: 0,
    repeatCount: 2,
    durationSeconds: 3600,
    seed: '20261126',
    members: [makeStrongMember('青云剑修', 25)],
    environment: baseEnv,
  }, liveCatalog)

  assert.ok(soloClearRes.dungeonSummary)
  assert.equal(soloClearRes.dungeonSummary.dungeonId, 'dungeon.qingyun-secret-gorge')
  assert.equal(soloClearRes.dungeonSummary.requestedRuns, 2)
  assert.equal(soloClearRes.dungeonSummary.completedRuns, 2)
  assert.equal(soloClearRes.dungeonSummary.clearedRuns, 2)
  assert.equal(soloClearRes.dungeonSummary.failedRuns, 0)
  assert.deepEqual(soloClearRes.dungeonSummary.failedWaves, {})
  assert.equal(soloClearRes.dungeonSummary.energyPerRun, 30)
  assert.equal(soloClearRes.dungeonSummary.totalEnergySpent, 60)
  assert.ok(soloClearRes.simulatedSeconds > 80 && soloClearRes.simulatedSeconds < 600, '完成全部挑战次数后应立即停表')
  assert.equal(soloClearRes.members[0].encounters, 20, '2 次十波通关应累计 20 波遭遇')
  const soloStones = soloClearRes.members[0].items.find(i => i.itemId === 'currency.spirit-stone')?.quantity ?? 0
  const soloGuaranteedQty = soloClearRes.members[0].items.find(i => i.itemId === guaranteedLoot.itemId)?.quantity ?? 0
  assert.ok(soloStones >= qingyunDef.baseRewards.spiritStones * 2, '2 次虚境普通通关应发放至少 2 倍基准通关灵石')
  assert.ok(soloGuaranteedQty >= guaranteedLoot.minQuantity * 2, '2 次虚境普通通关应发放至少 2 倍保底材料')

  // 2. 追忆副本完全免疫节点封魔阵怪物削弱（monsterWeakenRatio = 0.5 与 0 结果逐字段严格一致）
  const weakenEnv = structuredClone(baseEnv)
  weakenEnv.nodes[0].effects.monsterWeakenRatio = 0.5
  const soloWithWeakenRes = runSimulation({
    formatVersion: 1,
    nodeId: qingyunEntry.nodeId,
    dungeonId: 'dungeon.qingyun-secret-gorge',
    difficulty: 0,
    repeatCount: 2,
    durationSeconds: 3600,
    seed: '20261126',
    members: [makeStrongMember('青云剑修', 25)],
    environment: weakenEnv,
  }, liveCatalog)
  assert.equal(soloWithWeakenRes.simulatedSeconds, soloClearRes.simulatedSeconds)
  assert.equal(soloWithWeakenRes.members[0].lifetimeExperience, soloClearRes.members[0].lifetimeExperience)
  assert.deepEqual(soloWithWeakenRes.members[0].combatStats, soloClearRes.members[0].combatStats)

  // 3. 现世·凡阶（d=3）动态等级重算 + 2 人组队独立结算：Lv.25 队伍在现世模式修为远高于虚境地狱（d=2）
  const virtualHellRes = runSimulation({
    formatVersion: 1,
    nodeId: qingyunEntry.nodeId,
    dungeonId: 'dungeon.qingyun-secret-gorge',
    difficulty: 2,
    repeatCount: 1,
    durationSeconds: 3600,
    seed: '99',
    members: [makeStrongMember('队长', 25), makeStrongMember('队员', 22)],
    environment: baseEnv,
  }, liveCatalog)
  const realityMundaneRes = runSimulation({
    formatVersion: 1,
    nodeId: qingyunEntry.nodeId,
    dungeonId: 'dungeon.qingyun-secret-gorge',
    difficulty: 3,
    repeatCount: 1,
    durationSeconds: 3600,
    seed: '99',
    members: [makeStrongMember('队长', 25), makeStrongMember('队员', 22)],
    environment: baseEnv,
  }, liveCatalog)
  assert.equal(realityMundaneRes.dungeonSummary?.clearedRuns, 1)
  assert.equal(realityMundaneRes.dungeonSummary?.energyPerRun, 60)
  assert.ok(
    realityMundaneRes.members[0].lifetimeExperience > virtualHellRes.members[0].lifetimeExperience * 2,
    'Lv.25 队伍在现世凡阶（动态对齐 Lv.25）获得的真实修为应显著高于固定低等级的虚境地狱',
  )
  assert.ok(realityMundaneRes.members[1].items.some(i => i.itemId === guaranteedLoot.itemId))
})

test('追忆副本模拟：团灭满状态重开统计通关率、全灭立判失败与单波 600s 超时保护', async () => {
  const { CombatOutcome } = await import('./combatModels.ts')
  const liveCatalogJson = JSON.parse(
    fs.readFileSync(path.resolve(__dirname, '../data/simulatorCatalog.json'), 'utf8'),
  )
  const liveCatalog = buildGameDataCatalog(liveCatalogJson)
  const wumengDef = liveCatalog.dungeons.get('dungeon.wumeng-sword-tomb')
  assert.ok(wumengDef)
  const wumengGuaranteed = wumengDef.clearLootTable.find(e => e.kind === 'guaranteed')
  assert.ok(wumengGuaranteed)

  // 1. 战力不足角色连续挑战 3 次：每次团灭后自动满状态重开跑满 3 次，准确统计失败波次且不发第 10 波通关保底
  const weakMember = createDefaultProfile(liveCatalog)
  weakMember.realmLevel = 1
  weakMember.maxHitpoints = 40
  weakMember.hitpoints = 40
  const wipeRes = runSimulation({
    formatVersion: 1,
    nodeId: wumengDef.nodeId,
    dungeonId: 'dungeon.wumeng-sword-tomb',
    difficulty: 0,
    repeatCount: 3,
    durationSeconds: 3600,
    seed: '777',
    members: [weakMember],
    environment: {
      formatVersion: 1,
      gameDataFingerprint: liveCatalog.fingerprint,
      capturedAtUnixSeconds: 1_700_000_000,
      nodes: [{ nodeId: wumengDef.nodeId, buildings: [], effects: {}, usages: [] }],
    },
  }, liveCatalog)

  assert.ok(wipeRes.dungeonSummary)
  assert.equal(wipeRes.dungeonSummary.requestedRuns, 3)
  assert.equal(wipeRes.dungeonSummary.completedRuns, 3)
  assert.equal(wipeRes.dungeonSummary.clearedRuns, 0)
  assert.equal(wipeRes.dungeonSummary.failedRuns, 3)
  assert.deepEqual(wipeRes.dungeonSummary.failedWaves, { 1: 3 })
  assert.equal(wipeRes.dungeonSummary.totalEnergySpent, 90)
  assert.equal(wipeRes.members[0].deaths, 3)
  assert.equal(wipeRes.members[0].hitpoints, 1, '末次挑战团灭后按服务端 TerminateAtOneHp 保留 1 点气血')
  const playerStat = wipeRes.members[0].combatStats.find(s => s.unitKey === 'player')
  assert.ok(playerStat)
  const totalTaken = playerStat.damageTakenPhysical + playerStat.damageTakenSpell + playerStat.damageTakenTrue
  assert.ok(
    totalTaken >= wipeRes.members[0].maxHitpoints * 3,
    '第 1、2 次团灭重开时必须将角色恢复至满气血，累计承伤至少达到 3 倍生命上限',
  )
  assert.equal(
    wipeRes.members[0].items.some(i => i.itemId === wumengGuaranteed.itemId),
    false,
    '未通关第 10 波不得发放通关保底特殊材料',
  )

  // 2. 组队副本全员同时无存活单位时立即判负（不等候战内 15s 复活倒计时）
  const fastKillEnemy = createCombatantStats(10_000, 500, 0, 1.0, 1_000_000, 0, CombatDamageKind.Physical, {
    accuracy: 1_000_000,
  })
  const fragileAlly = createCombatantStats(50, 5, 0, 10.0, 1_000_000, 0, CombatDamageKind.Physical)
  const wipeEnc = CombatEncounterState.createAllied(
    'dungeon-wipe-immediate',
    1n,
    [{ stats: fragileAlly, skills: [] }, { stats: fragileAlly, skills: [] }],
    15.0,
    [{ stats: fastKillEnemy, skills: [] }],
  )
  wipeEnc.wipeEndsBattle = true
  decideInitialActions(wipeEnc)
  const wipeStep = advanceCombatTo(wipeEnc, 20.0)
  assert.equal(wipeStep.outcome, CombatOutcome.EnemyWon)
  assert.ok(wipeEnc.elapsedSeconds <= 2.5, '最后一名存活队友倒下瞬间应立即结束战斗，不得空等 15s 复活计时')

  // 3. 单波 600s 超时熔断保护：纯肉盾死锁对局在 600s 精确判负退出
  const immortalTank = createCombatantStats(1_000_000, 1, 500, 2.0, 1_000_000, 0, CombatDamageKind.Physical)
  const timeoutEnc = CombatEncounterState.create(
    'dungeon-wave-timeout',
    2n,
    { stats: immortalTank, skills: [] },
    [{ stats: immortalTank, skills: [] }],
  )
  timeoutEnc.maxBattleSeconds = 600
  decideInitialActions(timeoutEnc)
  const timeoutStep = advanceCombatTo(timeoutEnc, 1200)
  assert.equal(timeoutStep.outcome, CombatOutcome.EnemyWon)
  assert.equal(timeoutEnc.timedOut, true)
  assert.ok(Math.abs(timeoutEnc.elapsedSeconds - 600) <= 1e-6)
})

test('符箓与阵法技艺每级独立 +1% 伤害增幅在 TS 模拟器与服务端 1:1 一致（抗性穿甲前生效且不放大维持代价）', async () => {
  const { resolveAttributesCore, playerAttributesToCombatantStats } = await import('./attributeResolver.ts')
  const { resolveConsumableStrikePower } = await import('./combatEngine.ts')
  const { buildSimulatorCatalogPayload } = await import('../../../../../scripts/gen-simulator-catalog.mjs')
  const liveCatalog = buildGameDataCatalog(buildSimulatorCatalogPayload())

  const cumulativeTechExp = (targetLevel) =>
    liveCatalog.realmProgression.discountedExpToNext.slice(0, targetLevel).reduce((acc, v) => acc + v, 0)

  const zeroCore = {
    staminaExp: 0,
    meridiansExp: 0,
    attackExp: 0,
    boneExp: 0,
    agilityExp: 0,
    consciousnessExp: 0,
  }
  const attrs = resolveAttributesCore(
    1,
    [],
    [],
    [],
    null,
    zeroCore,
    [],
    liveCatalog,
    null,
    null,
    null,
    { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 },
    0,
    {
      talisman: cumulativeTechExp(25),
      formation: cumulativeTechExp(40),
      alchemy: cumulativeTechExp(10),
    },
  )
  assert.equal(attrs.talismanDamageBonusPoints, 25)
  assert.equal(attrs.formationDamageBonusPoints, 40)
  const combatStats = playerAttributesToCombatantStats(attrs, 0)
  assert.equal(combatStats.talismanDamageBonusPoints, 25)
  assert.equal(combatStats.formationDamageBonusPoints, 40)

  // 雷击符原始威力按 talismanDamageBonusPoints 放大，不受 formationDamageBonusPoints 影响
  const leijiItem = liveCatalog.items.get('item.leiji-talisman')
  assert.ok(leijiItem && leijiItem.consumable?.strike)
  const baseCaster = createCombatantStats(1_000, 10, 0, 2, 100, 0, CombatDamageKind.Physical, {
    maxManapoints: 100,
    spellAttack: 50,
  })
  const power0 = resolveConsumableStrikePower(leijiItem, leijiItem.consumable.strike, baseCaster)
  const powerTalisman50 = resolveConsumableStrikePower(leijiItem, leijiItem.consumable.strike, {
    ...baseCaster,
    talismanDamageBonusPoints: 50,
  })
  const powerFormation50 = resolveConsumableStrikePower(leijiItem, leijiItem.consumable.strike, {
    ...baseCaster,
    formationDamageBonusPoints: 50,
  })
  assert.equal(power0, 120)
  assert.equal(powerTalisman50, 180)
  assert.equal(powerFormation50, 120)

  // 天雷阵与血煞阵反击伤害在抗性穿甲前按 formationDamageBonusPoints 放大，不放大耗蓝与自损代价
  const runDiskBattle = (buffId, formationBonusPoints, talismanBonusPoints) => {
    const pStats = createCombatantStats(10_000, 10, 0, 0.5, 1_000_000, 0, CombatDamageKind.Physical, {
      spellAttack: 100,
      maxManapoints: 10_000,
      hpRegen: 0,
      mpRegen: 0,
      formationDamageBonusPoints: formationBonusPoints,
      talismanDamageBonusPoints: talismanBonusPoints,
    })
    const eStats = createCombatantStats(500_000, 0, 100, 100, 1, 0, CombatDamageKind.Physical, {
      magicResistance: 100,
      resistanceScale: 1,
    })
    const enc = CombatEncounterState.create('ts-formation-tech', 12345n, { stats: pStats, skills: [] }, [{ stats: eStats, skills: [] }])
    enc.player.hitpoints = 5_000
    enc.player.addBuff({
      buffId,
      expiresAtSeconds: 999,
      stacks: 1,
      name: buffId,
      resetOnWave: false,
      missingHitpointsPerStack: 0,
      effects: null,
      statRatios: null,
      sourceUnitId: null,
    })
    decideInitialActions(enc)
    const res = advanceCombatTo(enc, 25)
    return { enc, res }
  }

  const t0 = runDiskBattle('formation.tianlei', 0, 0)
  const tForm50 = runDiskBattle('formation.tianlei', 50, 0)
  const tTalisman50 = runDiskBattle('formation.tianlei', 0, 50)
  const tHit0 = t0.res.events.find(e => e.abilityId === 'item.tianlei-formation-disk')
  const tHit50 = tForm50.res.events.find(e => e.abilityId === 'item.tianlei-formation-disk')
  const tHitTalisman = tTalisman50.res.events.find(e => e.abilityId === 'item.tianlei-formation-disk')
  assert.ok(tHit0 && tHit50 && tHitTalisman)
  assert.ok(Math.abs(tHit0.damage - (60 * 60) / 161) < 1e-6)
  assert.ok(Math.abs(tHit50.damage - (90 * 90) / 191) < 1e-6)
  assert.ok(Math.abs(tHit0.damage - tHitTalisman.damage) < 1e-9)
  assert.ok(Math.abs(t0.enc.player.manapoints - tForm50.enc.player.manapoints) < 1e-9, '天雷阵耗蓝不得被阵法增伤放大')

  const x0 = runDiskBattle('formation.xuesha', 0, 0)
  const xForm50 = runDiskBattle('formation.xuesha', 50, 0)
  const xTalisman50 = runDiskBattle('formation.xuesha', 0, 50)
  const xHit0 = x0.res.events.find(e => e.abilityId === 'item.xuesha-formation-disk')
  const xHit50 = xForm50.res.events.find(e => e.abilityId === 'item.xuesha-formation-disk')
  const xHitTalisman = xTalisman50.res.events.find(e => e.abilityId === 'item.xuesha-formation-disk')
  assert.ok(xHit0 && xHit50 && xHitTalisman)
  assert.ok(Math.abs(xHit0.damage - (1000 * 1000) / 1101) < 1e-6)
  assert.ok(Math.abs(xHit50.damage - (1500 * 1500) / 1601) < 1e-6)
  assert.ok(Math.abs(xHit50.healing - xHit50.damage) < 1e-6)
  assert.ok(Math.abs(xHit0.damage - xHitTalisman.damage) < 1e-9)
})

test('前端模拟器对称遭遇：双侧均识别为玩家、右侧阵盘反击生效、同刻致命互杀判平且满血吸血对称', () => {
  const stats = createCombatantStats(1_000, 200, 0, 1.0, 1_000_000, 0, CombatDamageKind.Physical, {
    spellAttack: 100,
    maxManapoints: 10_000,
    critical: 0,
    hpRegen: 0,
    mpRegen: 0,
    effects: { hpDrainPoints: 25, mpDrainPoints: 0, damageReflectPoints: 0, healingBonusPoints: 0, threatOnTakenPoints: 0 },
  })

  // 1. 双侧均识别为 Player，is-player / has-player 触发器对称成立
  const sym = CombatEncounterState.createSymmetric('ts-sym-1', 42n, [{ stats, skills: [] }], [{ stats, skills: [] }])
  assert.equal(sym.isSymmetric, true)
  assert.equal(sym.sideA[0].isPlayer, true)
  assert.equal(sym.sideB[0].isPlayer, true)
  const isPlayerTrigger = [{ dependency: 'target-enemy', condition: 'is-player', comparator: 'is-active', value: 1 }]
  assert.equal(evaluateTriggers(isPlayerTrigger, sym.sideA[0], sym.sideB[0], sym.sideA, sym.sideB, 0), true)
  assert.equal(evaluateTriggers(isPlayerTrigger, sym.sideB[0], sym.sideA[0], sym.sideB, sym.sideA, 0), true)

  // 2. 同刻满血互砍 + 25% 吸血：双方剩余气血严格相等，最终同刻互杀判平
  decideInitialActions(sym)
  const r1 = advanceCombatTo(sym, 1.0)
  assert.ok(Math.abs(sym.sideA[0].hitpoints - sym.sideB[0].hitpoints) < 1e-9, '同刻互砍加吸血后双方剩余气血必须完全相等')
  assert.ok(r1.events.some(e => e.attackerId === sym.sideA[0].id && e.healing > 0))
  assert.ok(r1.events.some(e => e.attackerId === sym.sideB[0].id && e.healing > 0))
  const rEnd = advanceCombatTo(sym, 20)
  assert.equal(rEnd.outcome, 3, '完全镜像同刻互杀应判平 (CombatOutcome.Draw=3)')

  // 3. 右侧单位持有天雷阵盘，受攻击后反击伤害正常触发
  const tankStats = createCombatantStats(100_000, 50, 0, 0.5, 1_000_000, 0, CombatDamageKind.Physical, {
    spellAttack: 120,
    maxManapoints: 10_000,
    critical: 0,
    hpRegen: 0,
    mpRegen: 0,
  })
  const diskEnc = CombatEncounterState.createSymmetric('ts-sym-disk', 77n, [{ stats: tankStats, skills: [] }], [{ stats: tankStats, skills: [] }])
  diskEnc.sideB[0].addBuff({
    buffId: 'formation.tianlei',
    expiresAtSeconds: 999,
    stacks: 1,
    name: 'formation.tianlei',
    resetOnWave: false,
    missingHitpointsPerStack: 0,
    effects: null,
    statRatios: null,
    sourceUnitId: null,
  })
  decideInitialActions(diskEnc)
  const diskRes = advanceCombatTo(diskEnc, 25)
  assert.ok(
    diskRes.events.some(e => e.attackerId === diskEnc.sideB[0].id && e.targetId === diskEnc.sideA[0].id && e.abilityId === 'item.tianlei-formation-disk' && e.damage > 0),
    '右侧单位携带天雷阵盘时必须能正常触发反击',
  )
})

test('新流派机制模拟：护盾卖血与迸裂、天罡断岳独立受击叠层、养剑藏锋闪身步、五符箓与符法天命组队结算', () => {
  const liveCatalogJson = JSON.parse(
    fs.readFileSync(path.resolve(__dirname, '../data/simulatorCatalog.json'), 'utf8'),
  )
  const liveCatalog = buildGameDataCatalog(liveCatalogJson)
  const gangqi = liveCatalog.abilities.get('ability.gangqi-huti')
  const benglie = liveCatalog.abilities.get('ability.benglie')
  const tiangang = liveCatalog.abilities.get('ability.tiangang-duanyue')
  assert.ok(gangqi && benglie && tiangang, '三项新增武学应存在于模拟器目录')

  // 1. 血气化盾 + 蓄意 + 战后盾转血（受治疗增幅加成）
  const xueqiStats = createCombatantStats(1_000, 500, 0, 1.0, 1_000_000, 0, CombatDamageKind.Physical, {
    traits: ['trait.xueqihuadun', 'trait.xuyi'],
    effects: { hpDrainPoints: 0, mpDrainPoints: 0, damageReflectPoints: 0, healingBonusPoints: 50, threatOnTakenPoints: 0 },
  })
  const weakDummy = createCombatantStats(300, 0, 0, 100, 0, 0, CombatDamageKind.Physical)
  const xueqiEnc = CombatEncounterState.create('ts-xueqi', 101n, { stats: xueqiStats, skills: [] }, [{ stats: weakDummy, skills: [] }])
  decideInitialActions(xueqiEnc)
  assert.ok(Math.abs(xueqiEnc.player.hitpoints - 200) < 1e-9)
  assert.ok(Math.abs(xueqiEnc.player.shield - 800) < 1e-9)
  assert.ok(Math.abs(xueqiEnc.player.buffStacks('buff.xuyi') - 800) < 1e-9)
  const xueqiRes = advanceCombatTo(xueqiEnc, 5.0)
  assert.equal(xueqiRes.outcome, 1)
  assert.ok(Math.abs(xueqiEnc.player.hitpoints - 1_000) < 1e-9)
  assert.equal(xueqiEnc.player.shield, 0)
  assert.ok(xueqiRes.events.some(e => e.abilityId === 'trait.xueqihuadun' && e.healing > 0))

  // 2. 罡气护体 + 迸裂（5% 最大生命消耗、清空护盾并按 100%AD + 10%MaxHP + 100%消耗护盾 + 50 爆发）
  const shieldCasterStats = createCombatantStats(2_000, 100, 0, 100, 1_000_000, 0, CombatDamageKind.Physical, {
    maxManapoints: 500,
  })
  const zeroArmorEnemy = createCombatantStats(20_000, 0, 0, 100, 0, 0, CombatDamageKind.Physical)
  const benglieEnc = CombatEncounterState.create(
    'ts-benglie',
    102n,
    {
      stats: shieldCasterStats,
      skills: [
        { ability: gangqi, abilityId: 'ability.gangqi-huti', level: 1, triggers: [] },
        { ability: benglie, abilityId: 'ability.benglie', level: 1, triggers: [] },
      ],
    },
    [{ stats: zeroArmorEnemy, skills: [] }],
  )
  decideInitialActions(benglieEnc)
  advanceCombatTo(benglieEnc, 1.05)
  assert.ok(Math.abs(benglieEnc.player.shield - 400) < 1e-9)
  assert.ok(Math.abs(benglieEnc.player.hitpoints - 1_900) < 1e-9)
  const benglieRes = advanceCombatTo(benglieEnc, 2.6)
  assert.equal(benglieEnc.player.shield, 0)
  const benglieHit = benglieRes.events.find(e => e.abilityId === 'ability.benglie')
  assert.ok(benglieHit && Math.abs(benglieHit.damage - (750 * 750) / 751) < 1e-6)

  // 3. 天罡断岳：5s 吟唱期间多段连击只算 1 次受击叠 1 层天罡，释放时先吃天罡 +10%/层再乘 (1 + 0.2*层) 后清空天罡
  const multiHitSkill = {
    id: 'test.multihit',
    name: '十连击',
    category: 'physical',
    element: 'none',
    targetType: 'targetEnemy',
    cooldownMilliseconds: 10000,
    castMilliseconds: 1000,
    manaCost: 0,
    damageRatio: 0.1,
    flatDamage: 1,
    hitCount: 5,
  }
  const tgCaster = createCombatantStats(10_000, 100, 0, 100, 1_000_000, 0, CombatDamageKind.Physical, { maxManapoints: 500 })
  const multiAttacker = createCombatantStats(50_000, 10, 0, 2.0, 1_000_000, 0, CombatDamageKind.Physical)
  const tgEnc = CombatEncounterState.create(
    'ts-tiangang',
    103n,
    { stats: tgCaster, skills: [{ ability: tiangang, abilityId: 'ability.tiangang-duanyue', level: 1, triggers: [] }] },
    [{ stats: multiAttacker, skills: [{ ability: multiHitSkill, abilityId: 'test.multihit', level: 1, triggers: [] }] }],
  )
  decideInitialActions(tgEnc)
  advanceCombatTo(tgEnc, 1.1)
  assert.equal(tgEnc.player.buffStacks('buff.tiangang'), 1)
  advanceCombatTo(tgEnc, 3.1)
  assert.equal(tgEnc.player.buffStacks('buff.tiangang'), 2)
  const tgRes = advanceCombatTo(tgEnc, 5.1)
  const tgHit = tgRes.events.find(e => e.abilityId === 'ability.tiangang-duanyue')
  assert.ok(tgHit)
  const expectedTgDmg = ((400 * 400) / 401) * 1.2 * 1.4
  assert.ok(Math.abs(tgHit.damage - expectedTgDmg) < 1e-6, `天罡断岳 2 层伤害应为 ${expectedTgDmg}，实际 ${tgHit.damage}`)
  assert.equal(tgEnc.player.buffStacks('buff.tiangang'), 0)

  // 4. 养剑术 + 藏锋：不普攻，每整秒叠 1 层剑意，技能造成直伤后清空剑意
  const delayedSlash = {
    id: 'test.slash',
    name: '拔剑',
    category: 'physical',
    element: 'metal',
    targetType: 'targetEnemy',
    cooldownMilliseconds: 10000,
    castMilliseconds: 500,
    manaCost: 0,
    damageRatio: 1.0,
    flatDamage: 0,
  }
  const cangfengCaster = createCombatantStats(5_000, 100, 0, 0.5, 1_000_000, 0, CombatDamageKind.Physical, {
    traits: ['trait.yangjianshu', 'trait.cangfeng'],
    skillBonusPoints: 20,
  })
  const cfEnc = CombatEncounterState.create(
    'ts-yangjian-cangfeng',
    104n,
    {
      stats: cangfengCaster,
      skills: [
        {
          ability: delayedSlash,
          abilityId: 'test.slash',
          level: 1,
          triggers: [{ dependency: 'self', condition: 'buff-stacks', buffId: 'buff.jianyi', comparator: 'greater-than-equal', value: 2 }],
        },
      ],
    },
    [{ stats: zeroArmorEnemy, skills: [] }],
  )
  decideInitialActions(cfEnc)
  const cfWait = advanceCombatTo(cfEnc, 2.05)
  assert.equal(cfWait.events.filter(e => e.attackerId === 'player').length, 0, '藏锋期间不得打出普通攻击')
  assert.equal(cfEnc.player.buffStacks('buff.jianyi'), 2, '2 秒未造成直伤应叠 2 层剑意')
  assert.ok(Math.abs(cfEnc.player.effectiveStats.attackPower - 160) < 1e-9, '2 层剑意应提升 60% 物理攻击')
  const cfSlash = advanceCombatTo(cfEnc, 2.6)
  assert.equal(cfSlash.events.length, 1)
  assert.equal(cfEnc.player.buffStacks('buff.jianyi'), 0, '造成直接伤害后剑意应全部清空')

  // 5. 闪身步：闪避获移形，Lv.1 冷却 3s，Lv.20 反比例缩至 1.5s
  const dodgeMaster = createCombatantStats(5_000, 10, 0, 100, 100, 1_000_000, CombatDamageKind.Physical, {
    traits: ['trait.shanshenbu'],
    traitLevels: { 'trait.shanshenbu': 20 },
  })
  const fastPoker = createCombatantStats(5_000, 10, 0, 1.0, 1, 0, CombatDamageKind.Physical)
  const ssbEnc = CombatEncounterState.create('ts-shanshenbu', 105n, { stats: dodgeMaster, skills: [] }, [{ stats: fastPoker, skills: [] }])
  decideInitialActions(ssbEnc)
  advanceCombatTo(ssbEnc, 1.05)
  assert.equal(ssbEnc.player.buffStacks('buff.yixing'), 1)
  advanceCombatTo(ssbEnc, 2.05)
  assert.equal(ssbEnc.player.buffStacks('buff.yixing'), 1)
  advanceCombatTo(ssbEnc, 3.05)
  assert.equal(ssbEnc.player.buffStacks('buff.yixing'), 2)

  // 6. 符法·天命 + 符法·疾 + 符法·破阵 + 符法·天罡 + 五种新符箓（单人与组队 runSimulation）
  const nodes = listCombatNodeIds(liveCatalog)
  assert.ok(nodes.length > 0)
  const nodeId = nodes[0]
  const makeTalismanMage = (name) => {
    const p = createDefaultProfile(liveCatalog)
    p.displayName = name
    p.realmLevel = 25
    p.maxHitpoints = 10_000
    p.hitpoints = 10_000
    p.maxManapoints = 10_000
    p.manapoints = 10_000
    p.traitIds = ['trait.fufatianming', 'trait.fufaji', 'trait.fufapozhen', 'trait.fufatiangang', 'trait.bukandaowo']
    p.unlockedTraits = [
      { id: 'trait.fufatianming', level: 1, exp: 0 },
      { id: 'trait.fufaji', level: 1, exp: 0 },
      { id: 'trait.fufapozhen', level: 10, exp: 0 },
      { id: 'trait.fufatiangang', level: 1, exp: 0 },
      { id: 'trait.bukandaowo', level: 5, exp: 0 },
    ]
    p.abilitySlots = [
      { abilityId: null, level: 1 },
      { abilityId: 'item.jian-talisman', level: 1 },
      { abilityId: 'item.nuyan-talisman', level: 1 },
      { abilityId: 'item.dingshen-talisman', level: 1 },
      { abilityId: null, level: 1 },
    ]
    p.combatAutoUseSlots = [
      { actionType: 'combat', slotIndex: 0, itemId: 'item.shenxing-talisman', quantity: 50, enabled: true, remainingDurationSeconds: 0, cooldownUntilUnixSeconds: 0, group: 'general' },
      { actionType: 'combat', slotIndex: 1, itemId: 'item.yinshen-talisman', quantity: 50, enabled: true, remainingDurationSeconds: 0, cooldownUntilUnixSeconds: 0, group: 'general' },
    ]
    p.assets = [
      { itemId: 'item.jian-talisman', variant: '', quantity: 50 },
      { itemId: 'item.nuyan-talisman', variant: '', quantity: 50 },
      { itemId: 'item.dingshen-talisman', variant: '', quantity: 50 },
      { itemId: 'item.shenxing-talisman', variant: '', quantity: 50 },
      { itemId: 'item.yinshen-talisman', variant: '', quantity: 50 },
    ]
    return p
  }
  const env = {
    formatVersion: 1,
    gameDataFingerprint: liveCatalog.fingerprint,
    capturedAtUnixSeconds: 1_700_000_000,
    nodes: [{ nodeId, buildings: [], effects: { combatHitpointRegen: 0, combatManapointRegen: 500, combatDamageReductionRatio: 0, monsterWeakenRatio: 0 }, usages: [] }],
  }
  const soloTalismanRes = runSimulation({
    formatVersion: 1,
    nodeId,
    difficulty: 0,
    durationSeconds: 60,
    seed: '20261127',
    members: [makeTalismanMage('天命符师')],
    environment: env,
  }, liveCatalog)
  const m0 = soloTalismanRes.members[0]
  assert.ok(m0.itemsLost.some(x => x.itemId === 'item.jian-talisman' && x.quantity >= 2), '符法·疾缩短 80% 冷却至 12s，60 秒内术法槽剑符应多次瞬发')
  assert.ok(m0.itemsLost.some(x => x.itemId === 'item.shenxing-talisman'), '神行符应正常消耗触发')
  assert.ok(m0.itemsLost.some(x => x.itemId === 'item.yinshen-talisman'), '隐身符应正常消耗触发')
  assert.ok(m0.combatAutoUseSlots.some(s => s.group === 'ability' && s.itemId === 'item.jian-talisman' && s.cooldownUntilUnixSeconds > 0), '结算快照应导出主动术法槽符箓冷却')

  const partyTalismanRes = runSimulation({
    formatVersion: 1,
    nodeId,
    difficulty: 0,
    durationSeconds: 45,
    seed: '20261127',
    members: [makeTalismanMage('符修甲'), makeTalismanMage('符修乙')],
    environment: env,
  }, liveCatalog)
  assert.ok(partyTalismanRes.members[0].itemsLost.some(x => x.itemId === 'item.jian-talisman'))
  assert.ok(partyTalismanRes.members[1].itemsLost.some(x => x.itemId === 'item.jian-talisman'))
  assert.ok(partyTalismanRes.members[1].combatAutoUseSlots.some(s => s.group === 'ability'))
})
