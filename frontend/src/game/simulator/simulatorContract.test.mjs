import test from 'node:test'
import assert from 'node:assert/strict'
import { existsSync } from 'node:fs'
import { readFile, readdir } from 'node:fs/promises'
import path from 'node:path'

const UI_FILES = [
  'frontend/src/components/simulator/CombatSimulatorModal.tsx',
  'frontend/src/components/simulator/SimulatorProfileEditor.tsx',
  'frontend/src/components/simulator/SimulatorResults.tsx',
]

async function collectTsx(dir) {
  const out = []
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) out.push(...await collectTsx(full))
    else if (/\.(ts|tsx)$/.test(entry.name)) out.push(full)
  }
  return out
}

test('simulator: 实体展示组件全部显式 itemId/abilityId', async () => {
  for (const file of UI_FILES) {
    const content = await readFile(file, 'utf8')
    for (const tag of ['ItemTile', 'ItemIcon', 'ItemReference', 'ItemTooltipTarget']) {
      const pattern = new RegExp(`<${tag}[\\s\\S]*?/>`, 'g')
      for (const match of content.matchAll(pattern)) {
        assert.match(match[0], /itemId=\{/, `${file} 的 <${tag}> 必须显式传 itemId`)
      }
    }
  }
  const editor = await readFile(UI_FILES[1], 'utf8')
  assert.match(editor, /<AbilityGridPicker[\s\S]*?abilities=\{abilities\}/, '功法选择必须使用真实功法目录')
})

test('simulator: 生产入口切换到纯 TS Worker 内核且不含 WASM 与旧版引擎残留', async () => {
  const app = await readFile('frontend/src/App.tsx', 'utf8')
  assert.match(app, /import\('\.\/components\/simulator\/CombatSimulatorModal'\)/,
    'App.tsx 必须导入 CombatSimulatorModal')
  assert.doesNotMatch(app, /WasmCombatSimulatorModal/,
    'App.tsx 不得再引用 WasmCombatSimulatorModal')

  for (const removed of [
    'frontend/public/simulator',
    'frontend/src/components/simulator/WasmCombatSimulatorModal.tsx',
    'frontend/src/components/simulator/WasmSimulatorProfileEditor.tsx',
    'frontend/src/components/simulator/WasmSimulatorResults.tsx',
    'frontend/src/game/simulator/wasmSimulatorBridge.ts',
    'frontend/src/game/simulator/wasmSimulatorTypes.ts',
    'frontend/src/game/simulator/expeditionSimulator.ts',
    'frontend/src/game/simulator/combatSimulatorCore.ts',
  ]) {
    assert.equal(existsSync(removed), false, `已废弃文件/目录不应存在：${removed}`)
  }

  const files = [...await collectTsx('frontend/src'), ...await collectTsx('frontend/src/network')]
  const offenders = []
  for (const file of files) {
    const content = await readFile(file, 'utf8')
    const normalized = file.replaceAll('\\', '/')
    if (normalized.includes('game/simulator/')) continue
    if (/(expeditionSimulator|manaDeficitTracker|combatSimulatorCore|skillTriggerEvaluator|wasmSimulatorBridge|wasmSimulatorTypes|WasmCombatSimulatorModal|WasmSimulatorProfileEditor|WasmSimulatorResults|SimulatorPartyEditor|SimulatorConfigPanel|SimulatorResultsView)/
      .test(content)) offenders.push(normalized)
  }
  assert.deepEqual(offenders, [], `生产代码不得引用已移除的 WASM 或旧 TS 模拟组件：${offenders.join(', ')}`)
})

test('simulator: bridge 与 worker 种子为字符串且不用 Math.random', async () => {
  const modal = await readFile(UI_FILES[0], 'utf8')
  assert.doesNotMatch(modal, /Math\.random/, '种子必须为字符串/crypto，禁止 Math.random')
  const bridge = await readFile('frontend/src/game/simulator/simulatorBridge.ts', 'utf8')
  assert.doesNotMatch(bridge, /Math\.random/, '桥接层禁止 Math.random')
  const worker = await readFile('frontend/src/game/simulator/simulatorWorker.ts', 'utf8')
  assert.doesNotMatch(worker, /Math\.random/, 'Worker 内核禁止 Math.random')
})
