import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

// 编译 marketSort.ts 获得纯净 ESM 模块（mock 掉对 React/Zustand 的 i18n 目录依赖）
const tempOut = path.join(tmpdir(), `daohua-market-sort-test-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/marketSort.ts')],
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
          contents: 'export const getT = () => (key) => key; export const useT = () => (key) => key; export const hasTranslation = () => false;',
          loader: 'js',
        }))
      },
    },
  ],
})

const {
  compareMarketInstruments,
  resolveMarketInstrumentLevel,
  resolvePrimaryCategoryKey,
  PRIMARY_CATEGORY_ORDER,
  getSubCategoryOrder,
} = await import(pathToFileURL(tempOut).href)

test.after(() => {
  try {
    fs.unlinkSync(tempOut)
  } catch {
    // 忽略临时文件清理失败
  }
})

test('marketSort: 一级品类与二级子品类权重判定自洽', () => {
  // 1. 方案 A 一级品类顺序：装备 (1) -> 功法 (2) -> 消耗品 (3) -> 材料 (4)
  assert.equal(PRIMARY_CATEGORY_ORDER.equipment, 1)
  assert.equal(PRIMARY_CATEGORY_ORDER.manual, 2)
  assert.equal(PRIMARY_CATEGORY_ORDER.consumable, 3)
  assert.equal(PRIMARY_CATEGORY_ORDER.material, 4)

  // 2. 标的大类类型识别
  assert.equal(resolvePrimaryCategoryKey({ itemId: 'equipment.iron-sword', name: '铁剑', category: 'equipment', tier: 1, sellPrice: 0 }), 'equipment')
  assert.equal(resolvePrimaryCategoryKey({ itemId: 'item.tome.tuna-gong', name: '吐纳功', category: 'manual', tier: 1, sellPrice: 0 }), 'manual')
  assert.equal(resolvePrimaryCategoryKey({ itemId: 'item.zhixue-powder', name: '止血粉', category: 'consumable', tier: 1, sellPrice: 0 }), 'consumable')
  assert.equal(resolvePrimaryCategoryKey({ itemId: 'item.banling-flower', name: '半灵花', category: 'material', tier: 1, sellPrice: 0 }), 'material')

  // 3. 装备部位排序权重
  assert.equal(getSubCategoryOrder('equipment', 'weapon'), 1)
  assert.equal(getSubCategoryOrder('equipment', 'off-hand'), 2)
  assert.equal(getSubCategoryOrder('equipment', 'head'), 3)
  assert.equal(getSubCategoryOrder('equipment', 'armor'), 4)
  assert.equal(getSubCategoryOrder('equipment', 'legs'), 5)
  assert.equal(getSubCategoryOrder('equipment', 'ring'), 6)
  assert.equal(getSubCategoryOrder('equipment', 'tool'), 7)

  // 4. 材料细分排序权重
  assert.equal(getSubCategoryOrder('material', 'herb'), 1)
  assert.equal(getSubCategoryOrder('material', 'mineral'), 2)
  assert.equal(getSubCategoryOrder('material', 'exoticMaterial'), 3)
  assert.equal(getSubCategoryOrder('material', 'refinedMaterial'), 4)
})

test('marketSort: 四级严格排序流水线（品阶 -> 等级 -> 类型 -> 名称）', () => {
  // 模拟标的定义
  const t1Weapon = {
    itemId: 'equipment.iron-sword',
    name: '铁剑',
    category: 'equipment',
    subCategory: 'weapon',
    tier: 1,
    sellPrice: 0,
    equipmentDefinition: { equipmentId: 'equipment.iron-sword', name: '铁剑', slot: 'weapon', tier: 1, level: 1 },
  }
  const t1Armor = {
    itemId: 'equipment.cloth-robe',
    name: '布袍',
    category: 'equipment',
    subCategory: 'armor',
    tier: 1,
    sellPrice: 0,
    equipmentDefinition: { equipmentId: 'equipment.cloth-robe', name: '布袍', slot: 'armor', tier: 1, level: 1 },
  }
  const t1Tome = {
    itemId: 'item.tome.tuna-gong',
    name: '吐纳功',
    category: 'manual',
    subCategory: 'manualTome',
    tier: 1,
    sellPrice: 10,
    definition: { itemId: 'item.tome.tuna-gong', name: '吐纳功', kind: 'manual', category: 'manual', tier: 1, level: 1 },
  }
  const t1Pill = {
    itemId: 'item.zhixue-powder',
    name: '止血粉',
    category: 'consumable',
    subCategory: 'pill',
    tier: 1,
    sellPrice: 5,
    definition: { itemId: 'item.zhixue-powder', name: '止血粉', kind: 'consumable', category: 'consumable', tier: 1, level: 1 },
  }
  const t1HerbBanling = {
    itemId: 'item.banling-flower',
    name: '半灵花',
    category: 'material',
    subCategory: 'herb',
    tier: 1,
    sellPrice: 2,
    definition: { itemId: 'item.banling-flower', name: '半灵花', kind: 'material', category: 'material', tier: 1, level: 1 },
  }
  const t1HerbNingxue = {
    itemId: 'item.ningxue-grass',
    name: '凝血草',
    category: 'material',
    subCategory: 'herb',
    tier: 1,
    sellPrice: 2,
    definition: { itemId: 'item.ningxue-grass', name: '凝血草', kind: 'material', category: 'material', tier: 1, level: 1 },
  }
  const t1MineralIron = {
    itemId: 'item.iron-ore',
    name: '铁矿石',
    category: 'material',
    subCategory: 'mineral',
    tier: 1,
    sellPrice: 2,
    definition: { itemId: 'item.iron-ore', name: '铁矿石', kind: 'material', category: 'material', tier: 1, level: 1 },
  }
  const t2Weapon = {
    itemId: 'equipment.steel-blade',
    name: '精钢刀',
    category: 'equipment',
    subCategory: 'weapon',
    tier: 2,
    sellPrice: 0,
    equipmentDefinition: { equipmentId: 'equipment.steel-blade', name: '精钢刀', slot: 'weapon', tier: 2, level: 18 },
  }
  const t1HighLevelWeapon = {
    itemId: 'equipment.heavy-sword',
    name: '重剑',
    category: 'equipment',
    subCategory: 'weapon',
    tier: 1,
    sellPrice: 0,
    equipmentDefinition: { equipmentId: 'equipment.heavy-sword', name: '重剑', slot: 'weapon', tier: 1, level: 10 },
  }

  // --- 第 1 级：品阶优先（Tier）---
  // T1 材料必然排在 T2 装备前面
  assert.ok(compareMarketInstruments(t1HerbBanling, t2Weapon) < 0, 'T1 半灵花 应先于 T2 精钢刀')
  assert.ok(compareMarketInstruments(t2Weapon, t1HerbBanling) > 0, 'T2 精钢刀 应后于 T1 半灵花')

  // --- 第 2 级：等级优先（Level）---
  // 同为 T1 装备，Level 1 的铁剑先于 Level 10 的重剑
  assert.ok(compareMarketInstruments(t1Weapon, t1HighLevelWeapon) < 0, 'Level 1 铁剑 应先于 Level 10 重剑')

  // --- 第 3 级：类型优先（Type 方案 A：装备 -> 功法 -> 消耗品 -> 材料）---
  // 同为 T1 Level 1，装备先于功法，功法先于消耗品，消耗品先于材料
  assert.ok(compareMarketInstruments(t1Weapon, t1Tome) < 0, 'T1 装备 应先于 T1 功法')
  assert.ok(compareMarketInstruments(t1Tome, t1Pill) < 0, 'T1 功法 应先于 T1 消耗品')
  assert.ok(compareMarketInstruments(t1Pill, t1HerbBanling) < 0, 'T1 消耗品 应先于 T1 材料')

  // 【核心业务验证】：同为 T1 Level 1，铁剑必然先于半灵花，彻底解决半灵花霸占第一位的问题！
  assert.ok(compareMarketInstruments(t1Weapon, t1HerbBanling) < 0, 'T1 铁剑 应先于 T1 半灵花')

  // 同大类内部按部位/子类排序：武器先于防具，药材先于矿石
  assert.ok(compareMarketInstruments(t1Weapon, t1Armor) < 0, '武器 应先于 衣服防具')
  assert.ok(compareMarketInstruments(t1HerbBanling, t1MineralIron) < 0, '药材 应先于 矿石')

  // --- 第 4 级：名称拼音优先（Name）---
  // 同为 T1 Level 1 材料/草药：半灵花 (b) 先于 凝血草 (n)
  assert.ok(compareMarketInstruments(t1HerbBanling, t1HerbNingxue) < 0, '同品类下拼音 b 半灵花 应先于 n 凝血草')
  assert.ok(compareMarketInstruments(t1HerbNingxue, t1HerbBanling) > 0, '同品类下拼音 n 凝血草 应后于 b 半灵花')

  // 综合排序验证：多类型混合列表
  const rawList = [
    t2Weapon,
    t1HerbNingxue,
    t1HerbBanling,
    t1MineralIron,
    t1Pill,
    t1HighLevelWeapon,
    t1Armor,
    t1Tome,
    t1Weapon,
  ]

  const sortedList = [...rawList].sort(compareMarketInstruments)

  const expectedOrder = [
    t1Weapon,          // T1, Level 1, 装备(武器)
    t1Armor,           // T1, Level 1, 装备(衣服)
    t1Tome,            // T1, Level 1, 功法
    t1Pill,            // T1, Level 1, 消耗品
    t1HerbBanling,     // T1, Level 1, 材料(药材, 拼音b)
    t1HerbNingxue,     // T1, Level 1, 材料(药材, 拼音n)
    t1MineralIron,     // T1, Level 1, 材料(矿石)
    t1HighLevelWeapon, // T1, Level 10, 装备(武器)
    t2Weapon,          // T2, Level 18, 装备
  ]

  assert.deepEqual(
    sortedList.map(item => item.itemId),
    expectedOrder.map(item => item.itemId),
    '多标的混合排序结果应严格遵循 品阶 -> 等级 -> 类型 -> 名称 四级流水线',
  )
})
