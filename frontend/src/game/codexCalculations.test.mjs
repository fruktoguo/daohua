import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import {
  formatDualUnlockCondition,
  formatRealmUnlockCondition,
  isMaterialUnlocked,
  resolveMaterialTechnique,
} from './codexCalculations.ts'
import { resolveCodexMaterialSubTab } from './codexFilters.ts'

const materialsJsonPath = path.resolve(process.cwd(), 'config/game-data/items/materials.json')
const rawMaterials = JSON.parse(fs.readFileSync(materialsJsonPath, 'utf8'))
// 转换成前端 ItemDefinition 结构（字段映射 id -> itemId）
const allMaterials = rawMaterials
  .filter(it => it.category === 'material')
  .map(it => ({
    itemId: it.id,
    name: it.name,
    description: it.description,
    kind: it.kind,
    category: it.category,
    subCategory: it.subCategory,
    tier: it.tier ?? 1,
    sellPrice: it.sellPrice ?? 0,
  }))

test('codexCalculations: 每项材料都必须能归入一个图鉴子分类（配表 subCategory 完备）', () => {
  // 图鉴材料页按配表 subCategory 直接分流，缺 field 或出现新取值的材料会整条从图鉴消失。
  const unclassified = allMaterials
    .filter(it => resolveCodexMaterialSubTab(it) === null)
    .map(it => `${it.itemId}(${it.subCategory ?? 'undefined'})`)
  assert.deepEqual(unclassified, [], '配表材料必须声明 subCategory ∈ {spiritMaterial, mineral, exoticMaterial, refinedMaterial}')
})

test('codexCalculations: 子分类归一与配表 subCategory 严格一一对应', () => {
  for (const it of allMaterials) {
    assert.equal(resolveCodexMaterialSubTab(it), it.subCategory, `${it.itemId} 的图鉴归属必须等于配表 subCategory`)
  }
})

test('codexCalculations: 技艺归属与材料子分类一致（灵材→采集、矿石→采矿、其余无生活技艺）', () => {
  const bySub = sub => allMaterials.filter(it => it.subCategory === sub)
  const spirit = bySub('spiritMaterial')
  const mineral = bySub('mineral')
  assert.ok(spirit.length > 0 && mineral.length > 0, '配表必须仍存在灵材与矿石，否则该断言空跑')
  for (const it of spirit) assert.equal(resolveMaterialTechnique(it), 'gathering', `${it.itemId} 必须关联采集`)
  for (const it of mineral) assert.equal(resolveMaterialTechnique(it), 'mining', `${it.itemId} 必须关联采矿`)
  for (const it of [...bySub('exoticMaterial'), ...bySub('refinedMaterial')]) {
    assert.equal(resolveMaterialTechnique(it), null, `${it.itemId} 无专属生活技艺，只能由境界裁决`)
  }
})

test('codexCalculations: 解锁规则验证（境界、技艺与背包三路联动）', () => {
  const t1Item = allMaterials.find(it => it.itemId === 'item.ningxue-grass')
  const t2Herb = allMaterials.find(it => it.itemId === 'item.banling-flower')
  const t2Mineral = allMaterials.find(it => it.itemId === 'item.crimson-copper')
  const t2Exotic = allMaterials.find(it => it.itemId === 'item.beast-fang')
  assert.ok(t1Item && t2Herb && t2Mineral && t2Exotic, '测试用材料必须存在于配表中')

  // 1 级修士：T1 自动解锁，T2 默认锁定
  assert.equal(isMaterialUnlocked(t1Item, 1, []), true)
  assert.equal(isMaterialUnlocked(t2Herb, 1, []), false)
  assert.equal(isMaterialUnlocked(t2Mineral, 1, []), false)
  assert.equal(isMaterialUnlocked(t2Exotic, 1, []), false)

  // 1 级修士但采集 18 级：灵材解锁，矿石与异材仍然锁定
  assert.equal(isMaterialUnlocked(t2Herb, 1, [], { gathering: 18 }), true)
  assert.equal(isMaterialUnlocked(t2Mineral, 1, [], { gathering: 18 }), false)
  assert.equal(isMaterialUnlocked(t2Exotic, 1, [], { gathering: 18 }), false)

  // 1 级修士但采矿 18 级：矿石解锁，灵材与异材仍然锁定
  assert.equal(isMaterialUnlocked(t2Herb, 1, [], { mining: 18 }), false)
  assert.equal(isMaterialUnlocked(t2Mineral, 1, [], { mining: 18 }), true)
  assert.equal(isMaterialUnlocked(t2Exotic, 1, [], { mining: 18 }), false)

  // 1 级修士但背包持有 T2 材料：提前点亮解锁
  assert.equal(isMaterialUnlocked(t2Herb, 1, [{ itemId: 'item.banling-flower', amount: 1 }]), true)
  assert.equal(isMaterialUnlocked(t2Exotic, 1, [{ itemId: 'item.beast-fang', amount: 1 }]), true)

  // 18 级修士（叩天门）：T2 全体系境界自动解锁，无需技艺或背包持有
  assert.equal(isMaterialUnlocked(t2Herb, 18, []), true)
  assert.equal(isMaterialUnlocked(t2Mineral, 18, []), true)
  assert.equal(isMaterialUnlocked(t2Exotic, 18, []), true)
})

test('codexCalculations: 双重解锁文案格式化验证', () => {
  assert.equal(formatRealmUnlockCondition(1), '默认解锁')
  assert.equal(formatRealmUnlockCondition(18), '境界达到 lv18[叩天门] 解锁')
  assert.equal(formatDualUnlockCondition(18, '采集'), '境界达到 lv18[叩天门] 或 采集达到 18 级解锁')
  assert.equal(formatDualUnlockCondition(18, '采矿'), '境界达到 lv18[叩天门] 或 采矿达到 18 级解锁')
  assert.equal(formatDualUnlockCondition(18, '炼丹'), '境界达到 lv18[叩天门] 或 炼丹达到 18 级解锁')
  assert.equal(formatDualUnlockCondition(18, null), '境界达到 lv18[叩天门] 解锁')
})
