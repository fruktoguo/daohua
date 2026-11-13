import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { build } from 'esbuild'
import { buildGameDataCatalog } from './catalog.ts'
import { CombatantState } from './combatModels.ts'
import { evaluateTriggers } from './triggerEvaluator.ts'
import { UnitChannel, resolveTriggerListWakeMask } from './triggerWake.ts'
import { listCombatNodeIds, runSimulation } from './simulatorRunner.ts'
import { abilityLevelForExp } from './rules.ts'
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
