import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { build } from 'esbuild'
import { buildGameDataCatalog } from './catalog.ts'
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
