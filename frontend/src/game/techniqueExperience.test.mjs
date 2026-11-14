import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { rm } from 'node:fs/promises'
import { build } from 'esbuild'

const tempOut = path.join(tmpdir(), `daohua-technique-exp-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/combatConstants.ts')],
  bundle: true,
  format: 'esm',
  outfile: tempOut,
  logLevel: 'error',
  plugins: [
    {
      name: 'mock-i18n',
      setup(b) {
        b.onResolve({ filter: /i18n/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
        b.onLoad({ filter: /.*/, namespace: 'mock-i18n' }, () => ({
          contents: 'export const getT = () => (key) => key; export const useT = () => (key) => key;',
          loader: 'js',
        }))
      },
    },
  ],
})
const {
  calculateBaseExperienceForLevel,
  calculateEnhancementYields,
  calculateSecretArtYields,
  calculateTechniqueExperience,
  isEquipmentUpgradeForgingAction,
} = await import(pathToFileURL(tempOut).href)

test.after(async () => {
  await rm(tempOut, { force: true })
})

test('全局技艺经验 2× 与消耗上级装备锻造再 2× 与服务端公式 1:1 对齐', () => {
  const lv1Sword = { technique: 'forging', inputs: { 'item.refined-iron': 7, 'item.beast-bone': 1 } }
  const lv9Sword = { technique: 'forging', inputs: { 'item.refined-iron': 12, 'item.dark-essence': 4 } }
  const lv18Refine = { technique: 'forging', inputs: { 'item.copper-ore': 3 } }
  const lv18Upgrade = {
    technique: 'forging',
    inputs: { 'equipment.qingfeng-sword': 2, 'item.refined-copper': 3 },
  }

  assert.equal(isEquipmentUpgradeForgingAction(lv1Sword), false)
  assert.equal(isEquipmentUpgradeForgingAction(lv9Sword), false)
  assert.equal(isEquipmentUpgradeForgingAction(lv18Refine), false)
  assert.equal(isEquipmentUpgradeForgingAction(lv18Upgrade), true)
  assert.equal(isEquipmentUpgradeForgingAction({ technique: 'alchemy', inputs: { 'equipment.qingfeng-sword': 1 } }), false)

  // Lv.1 15s 普通锻造：10 * 1.5 * 2 = 30
  assert.equal(calculateTechniqueExperience(1, 15, lv1Sword), 30)
  // Lv.9 20s 无上级消耗散件锻造：只享全局 2×
  const lv9Base = calculateBaseExperienceForLevel(9)
  assert.equal(calculateTechniqueExperience(9, 20, lv9Sword), Math.round(lv9Base * 2 * 2))
  // Lv.18 20s 升阶锻造（消耗上级装备）：全局 2× × 升阶 2× = 4×
  const lv18Base = calculateBaseExperienceForLevel(18)
  assert.equal(calculateTechniqueExperience(18, 20, lv18Upgrade), Math.round(lv18Base * 2 * 4))
})

test('装备强化与秘法预览：技艺经验与等额底蕴翻倍，神识经验保持原倍率', () => {
  // 强化 Lv.1 装备 +0 -> +1（基础耗时 6s）：神识=6，秘法=12，底蕴=12
  const enhLv1 = calculateEnhancementYields(1, 1)
  assert.equal(enhLv1.consciousnessExp, 6)
  assert.equal(enhLv1.techniqueExp, 12)
  assert.equal(enhLv1.foundationGain, 12)

  // 秘法 Lv.1 物品化灵（5s）：神识=round(10*0.5)*2=10，秘法=round(10*0.5*2)=10，底蕴=10
  const artLv1 = calculateSecretArtYields(1, 5)
  assert.equal(artLv1.consciousnessExp, 10)
  assert.equal(artLv1.techniqueExp, 10)
  assert.equal(artLv1.foundationGain, 10)

  // 秘法 Lv.3 物品（5s）：神识=round(12.15*0.5)*2=12，秘法=round(12.15*0.5*2)=12，底蕴=12
  const artLv3 = calculateSecretArtYields(3, 5)
  assert.equal(artLv3.consciousnessExp, 12)
  assert.equal(artLv3.techniqueExp, 12)
  assert.equal(artLv3.foundationGain, 12)
})
