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
  const commonHerb = allMaterials.find(it => it.itemId === 'item.banling-flower')
  const commonMineral = allMaterials.find(it => it.itemId === 'item.crimson-copper')
  const commonExotic = allMaterials.find(it => it.itemId === 'item.beast-fang')
  const rareExotic = allMaterials.find(it => it.itemId === 'item.demon-dan')
  assert.ok(commonHerb && commonMineral && commonExotic && rareExotic, '测试用材料必须存在于配表中')
  assert.equal(commonHerb.tier, 1)
  assert.equal(rareExotic.tier, 2)

  // 凡品材料（采集直出/高爆率战利品）一律 1 级默认解锁，无论技艺与背包
  assert.equal(isMaterialUnlocked(commonHerb, 1, []), true)
  assert.equal(isMaterialUnlocked(commonMineral, 1, []), true)
  assert.equal(isMaterialUnlocked(commonExotic, 1, []), true)

  // 稀有材料（tier >= 2）默认锁定，境界达标才由境界路径解锁
  assert.equal(isMaterialUnlocked(rareExotic, 1, []), false)
  assert.equal(isMaterialUnlocked(rareExotic, 18, []), true)

  // 背包持有稀有材料：提前点亮解锁
  assert.equal(isMaterialUnlocked(rareExotic, 1, [{ itemId: 'item.demon-dan', amount: 1 }]), true)

  // 技艺路径按 subCategory 分派（灵材→采集、矿石→采矿、异材/精材无技艺）：
  // 当前配表凡品材料已无技艺门槛，用同构材料定义验证分派逻辑本身不退化
  const herbAt18 = { itemId: 'item.test-herb', tier: 2, subCategory: 'spiritMaterial' }
  const mineralAt18 = { itemId: 'item.test-ore', tier: 2, subCategory: 'mineral' }
  const exoticAt18 = { itemId: 'item.test-exotic', tier: 2, subCategory: 'exoticMaterial' }
  assert.equal(isMaterialUnlocked(herbAt18, 1, [], { gathering: 18 }), true)
  assert.equal(isMaterialUnlocked(mineralAt18, 1, [], { gathering: 18 }), false)
  assert.equal(isMaterialUnlocked(mineralAt18, 1, [], { mining: 18 }), true)
  assert.equal(isMaterialUnlocked(exoticAt18, 1, [], { gathering: 18, mining: 18 }), false)

  // 稀有阶梯：tier 3 门槛 30 级，18 级不足以解锁
  const exoticAt30 = { itemId: 'item.test-exotic-3', tier: 3, subCategory: 'exoticMaterial' }
  assert.equal(isMaterialUnlocked(exoticAt30, 18, []), false)
  assert.equal(isMaterialUnlocked(exoticAt30, 30, []), true)
})

test('codexCalculations: 双重解锁文案格式化验证', () => {
  assert.equal(formatRealmUnlockCondition(1), '默认解锁')
  assert.equal(formatRealmUnlockCondition(18), '境界达到 lv18[叩天门] 解锁')
  assert.equal(formatDualUnlockCondition(18, '采集'), '境界达到 lv18[叩天门] 或 采集达到 18 级解锁')
  assert.equal(formatDualUnlockCondition(18, '采矿'), '境界达到 lv18[叩天门] 或 采矿达到 18 级解锁')
  assert.equal(formatDualUnlockCondition(18, '炼丹'), '境界达到 lv18[叩天门] 或 炼丹达到 18 级解锁')
  assert.equal(formatDualUnlockCondition(18, null), '境界达到 lv18[叩天门] 解锁')
})
