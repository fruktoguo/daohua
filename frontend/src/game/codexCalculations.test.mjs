import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { isMaterialSubTabItem, isMaterialUnlocked } from './codexCalculations.ts'

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

test('codexCalculations: materials.json 中材料总数刚好为 25 项', () => {
  assert.equal(allMaterials.length, 25)
})

test('codexCalculations: 药材子Tab (herb) 准确收录 9 项草木灵材与草木精粹', () => {
  const herbItems = allMaterials.filter(it => isMaterialSubTabItem(it, 'herb'))
  const herbIds = herbItems.map(it => it.itemId).sort()
  const expectedHerbIds = [
    'item.banling-essence',
    'item.banling-flower',
    'item.herb-essence',
    'item.morning-dew',
    'item.ningxue-grass',
    'item.peach-wood',
    'item.peach-wood-crystal',
    'item.spirit-pine-crystal',
    'item.yinlingsong-wood',
  ].sort()

  assert.equal(herbItems.length, 9)
  assert.deepEqual(herbIds, expectedHerbIds)
})

test('codexCalculations: 矿石子Tab (mineral) 准确收录 4 项原矿与金铁精材', () => {
  const mineralItems = allMaterials.filter(it => isMaterialSubTabItem(it, 'mineral'))
  const mineralIds = mineralItems.map(it => it.itemId).sort()
  const expectedMineralIds = [
    'item.crimson-copper',
    'item.dark-iron',
    'item.refined-copper',
    'item.refined-iron',
  ].sort()

  assert.equal(mineralItems.length, 4)
  assert.deepEqual(mineralIds, expectedMineralIds)
})

test('codexCalculations: 异材子Tab (exotic) 准确收录 12 项妖兽讨伐残蜕', () => {
  const exoticItems = allMaterials.filter(it => isMaterialSubTabItem(it, 'exotic'))
  const exoticIds = exoticItems.map(it => it.itemId).sort()
  const expectedExoticIds = [
    'item.beast-bone',
    'item.beast-fang',
    'item.beast-hide',
    'item.broken-blade',
    'item.broken-spirit-core',
    'item.demon-core',
    'item.demon-dan',
    'item.fire-feather',
    'item.meteor-fragment',
    'item.sha-bone',
    'item.sha-fang',
    'item.soul-talisman',
  ].sort()

  assert.equal(exoticItems.length, 12)
  assert.deepEqual(exoticIds, expectedExoticIds)
})

test('codexCalculations: 25项材料在三大子Tab中严格互斥且完备，无一重合且无一遗漏', () => {
  const herbIds = new Set(allMaterials.filter(it => isMaterialSubTabItem(it, 'herb')).map(it => it.itemId))
  const mineralIds = new Set(allMaterials.filter(it => isMaterialSubTabItem(it, 'mineral')).map(it => it.itemId))
  const exoticIds = new Set(allMaterials.filter(it => isMaterialSubTabItem(it, 'exotic')).map(it => it.itemId))

  // 互斥性验证
  for (const id of herbIds) {
    assert.equal(mineralIds.has(id), false, `ID ${id} 不应同时属于矿石`)
    assert.equal(exoticIds.has(id), false, `ID ${id} 不应同时属于异材`)
  }
  for (const id of mineralIds) {
    assert.equal(exoticIds.has(id), false, `ID ${id} 不应同时属于异材`)
  }

  // 完备性验证
  assert.equal(herbIds.size + mineralIds.size + exoticIds.size, 25)
  for (const it of allMaterials) {
    const inAny = herbIds.has(it.itemId) || mineralIds.has(it.itemId) || exoticIds.has(it.itemId)
    assert.equal(inAny, true, `材料 ${it.itemId} 必须落入三者之一`)
  }
})

test('codexCalculations: 解锁规则验证（境界与背包联动）', () => {
  const t1Item = { itemId: 'item.ningxue-grass', tier: 1 }
  const t2Item = { itemId: 'item.banling-flower', tier: 2 }

  // 1 级修士：T1 自动解锁，T2 默认锁定
  assert.equal(isMaterialUnlocked(t1Item, 1, []), true)
  assert.equal(isMaterialUnlocked(t2Item, 1, []), false)

  // 1 级修士但背包持有 T2 材料：提前点亮解锁
  assert.equal(isMaterialUnlocked(t2Item, 1, [{ itemId: 'item.banling-flower', amount: 1 }]), true)

  // 18 级修士（叩仙门）：T2 境界自动解锁，无需背包持有
  assert.equal(isMaterialUnlocked(t2Item, 18, []), true)
})
