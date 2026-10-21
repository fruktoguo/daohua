import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { formatDualUnlockCondition, formatRealmUnlockCondition, isMaterialSubTabItem, isMaterialUnlocked, resolveMaterialTechnique } from './codexCalculations.ts'

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

// 判据只锁「分类规则」，不锁配表条目数：材料随玩法扩张会持续新增，
// 把总数与 ID 清单钉死会让测试在每次加材料时误红，从而被习惯性跳过。
const subtabOf = it =>
  isMaterialSubTabItem(it, 'herb') ? 'herb'
    : isMaterialSubTabItem(it, 'mineral') ? 'mineral'
      : isMaterialSubTabItem(it, 'exotic') ? 'exotic'
        : null

test('codexCalculations: 每项材料必须且只能落入一个子 Tab（互斥且完备）', () => {
  const unclassified = allMaterials.filter(it => subtabOf(it) === null).map(it => it.itemId)
  assert.deepEqual(unclassified, [], '所有材料都必须被某一子 Tab 收录，否则图鉴里会整条消失')

  // 三个子 Tab 的判定是三个独立谓词，必须两两互斥，否则同一材料会在多个 Tab 重复出现。
  for (const it of allMaterials) {
    const hits = ['herb', 'mineral', 'exotic'].filter(t => isMaterialSubTabItem(it, t))
    assert.equal(hits.length, 1, `材料 ${it.itemId} 命中子 Tab ${hits.join('/') || '（无）'}，必须恰好命中一个`)
  }
})

test('codexCalculations: 子 Tab 归属与配表 subCategory 语义一致', () => {
  // 妖兽残蜕必须进「异材」；原矿必须进「矿石」；草木灵材必须进「药材」。
  const bySub = sub => allMaterials.filter(it => it.subCategory === sub)
  const expectSubtab = (sub, tab) => {
    const items = bySub(sub)
    assert.ok(items.length > 0, `配表必须仍存在 subCategory=${sub} 的材料，否则该断言空跑`)
    for (const it of items) assert.equal(subtabOf(it), tab, `${it.itemId}（${sub}）必须归入 ${tab}`)
  }
  expectSubtab('exoticMaterial', 'exotic')
  expectSubtab('mineral', 'mineral')
  expectSubtab('spiritMaterial', 'herb')

  // refinedMaterial 是「精粹」混合类，按物品 ID 显式分流到药材或矿石，
  // 这是分类器里唯一的特例分支，必须逐项锁死（新增精粹时必须同步改分类器）。
  const refinedSplit = {
    'item.herb-essence': 'herb',
    'item.banling-essence': 'herb',
    'item.peach-wood-crystal': 'herb',
    'item.spirit-pine-crystal': 'herb',
    'item.refined-iron': 'mineral',
    'item.refined-copper': 'mineral',
  }
  for (const [itemId, tab] of Object.entries(refinedSplit)) {
    const item = allMaterials.find(it => it.itemId === itemId)
    assert.ok(item, `精粹分流表列出的 ${itemId} 必须仍存在于配表`)
    assert.equal(subtabOf(item), tab, `${itemId} 必须归入 ${tab}`)
  }
  const unlistedRefined = bySub('refinedMaterial')
    .map(it => it.itemId)
    .filter(id => !(id in refinedSplit))
  assert.deepEqual(unlistedRefined, [], '新增的 refinedMaterial 材料必须显式登记分流去向，否则图鉴归属未定义')
})

test('codexCalculations: 药材子Tab 收录全部草木灵材与精粹且无遗漏', () => {
  const herbItems = allMaterials.filter(it => isMaterialSubTabItem(it, 'herb'))
  const expected = allMaterials
    .filter(it => it.subCategory === 'spiritMaterial'
      || ['item.herb-essence', 'item.banling-essence', 'item.peach-wood-crystal', 'item.spirit-pine-crystal'].includes(it.itemId))
    .map(it => it.itemId)
    .sort()
  assert.deepEqual(herbItems.map(it => it.itemId).sort(), expected)
})

test('codexCalculations: 矿石子Tab 收录全部原矿与金铁精材且无遗漏', () => {
  const mineralItems = allMaterials.filter(it => isMaterialSubTabItem(it, 'mineral'))
  const expected = allMaterials
    .filter(it => it.subCategory === 'mineral' || ['item.refined-iron', 'item.refined-copper'].includes(it.itemId))
    .map(it => it.itemId)
    .sort()
  assert.deepEqual(mineralItems.map(it => it.itemId).sort(), expected)
})

test('codexCalculations: 异材子Tab 收录全部妖兽残蜕且无遗漏', () => {
  const exoticItems = allMaterials.filter(it => isMaterialSubTabItem(it, 'exotic'))
  const expected = allMaterials.filter(it => it.subCategory === 'exoticMaterial').map(it => it.itemId).sort()
  assert.deepEqual(exoticItems.map(it => it.itemId).sort(), expected)
})

test('codexCalculations: 解锁规则验证（境界、技艺与背包三路联动）', () => {
  const t1Item = allMaterials.find(it => it.itemId === 'item.ningxue-grass')
  const t2Herb = allMaterials.find(it => it.itemId === 'item.banling-flower')
  const t2Mineral = allMaterials.find(it => it.itemId === 'item.crimson-copper')
  const t2Exotic = allMaterials.find(it => it.itemId === 'item.beast-fang')
  assert.ok(t1Item && t2Herb && t2Mineral && t2Exotic, '测试用材料必须存在于配表中')
  // 技艺关联验证
  assert.equal(resolveMaterialTechnique(t2Herb), 'gathering')
  assert.equal(resolveMaterialTechnique(t2Mineral), 'mining')
  assert.equal(resolveMaterialTechnique(t2Exotic), null)

  // 1 级修士：T1 自动解锁，T2 默认锁定
  assert.equal(isMaterialUnlocked(t1Item, 1, []), true)
  assert.equal(isMaterialUnlocked(t2Herb, 1, []), false)
  assert.equal(isMaterialUnlocked(t2Mineral, 1, []), false)
  assert.equal(isMaterialUnlocked(t2Exotic, 1, []), false)

  // 1 级修士但采集 18 级：伴灵花（药材）解锁，赤铜矿（矿石）与异材仍然锁定
  assert.equal(isMaterialUnlocked(t2Herb, 1, [], { gathering: 18 }), true)
  assert.equal(isMaterialUnlocked(t2Mineral, 1, [], { gathering: 18 }), false)
  assert.equal(isMaterialUnlocked(t2Exotic, 1, [], { gathering: 18 }), false)

  // 1 级修士但采矿 18 级：赤铜矿（矿石）解锁，伴灵花（药材）与异材仍然锁定
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
