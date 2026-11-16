import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { build } from 'esbuild'
import { buildGameDataCatalog } from './catalog.ts'
import { CombatEncounterState, CombatantState } from './combatModels.ts'
import { advanceCombatTo, decideInitialActions } from './combatEngine.ts'
import { evaluateTriggers } from './triggerEvaluator.ts'
import { CombatFactTracker, UnitChannel, resolveTriggerListWakeMask } from './triggerWake.ts'
import { listCombatNodeIds, runSimulation } from './simulatorRunner.ts'
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

// combatConstants 含 `../i18n` 目录导入，Node 原生 ESM 不支持，沿用 realmCalculations.test.mjs 的 esbuild 单文件打包范式
const tempCombatOut = path.join(tmpdir(), `daohua-sim-combat-constants-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/combatConstants.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempCombatOut,
  logLevel: 'error',
})
const {
  ROOT_LEVEL_CAP,
  getRootCumulativeExpForLevel,
  getRootPointsForExp,
  calculateAbilityLevelAndProgress,
} = await import(pathToFileURL(tempCombatOut).href)
test.after(() => {
  try { fs.unlinkSync(tempCombatOut) } catch { /* 临时文件可能已被清理 */ }
})

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const catalogJson = JSON.parse(
  fs.readFileSync(path.resolve(__dirname, '../__golden__/simulatorCatalog.json'), 'utf8'),
)
const parityCases = JSON.parse(
  fs.readFileSync(path.resolve(__dirname, '../__golden__/parityCases.json'), 'utf8'),
)

const catalog = buildGameDataCatalog(catalogJson)

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
  assert.equal(rules.ALL_TRAITS.size, 38)
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
    assert.ok(Math.abs(foe.effectiveStats.evasion - 40) <= 1e-9)
    assert.ok(Math.abs(foe.effectiveStats.parryResolve - 40) <= 1e-9)
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
