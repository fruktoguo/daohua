import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

// 1. 读取 materials.json 配置
const materialsJsonPath = path.resolve(process.cwd(), 'config/game-data/items/materials.json')
const rawMaterials = JSON.parse(fs.readFileSync(materialsJsonPath, 'utf8'))

// 2. 编译 ids.ts 获得纯净 ESM 模块（mock 掉对 React/Zustand 的 i18n 目录依赖）
const tempOut = path.join(tmpdir(), `daohua-ids-test-${Date.now()}.mjs`)
await build({
  entryPoints: [path.resolve(process.cwd(), 'frontend/src/game/ids.ts')],
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
          contents: 'export const getT = () => (key) => key === "game.category.special" ? "特殊" : key; export const useT = () => (key) => key; export const hasTranslation = () => false;',
          loader: 'js',
        }))
      },
    },
  ],
})

const { resolveInventoryGroup, resolveItemCategoryLabel } = await import(pathToFileURL(tempOut).href)

// 测试完毕后清理临时文件
test.after(() => {
  try {
    fs.unlinkSync(tempOut)
  } catch {}
})

test('inventoryGroup: materials.json 中觉醒石配置为 category=special, subCategory=special, tier=5, isTradable=true', () => {
  const awakeningStone = rawMaterials.find(it => it.id === 'item.awakening-stone')
  assert.ok(awakeningStone, '觉醒石必须存在于 materials.json')
  assert.equal(awakeningStone.category, 'special', '觉醒石 category 必须为 special')
  assert.equal(awakeningStone.subCategory, 'special', '觉醒石 subCategory 必须为 special')
  assert.equal(awakeningStone.tier, 5, '觉醒石 tier 必须为 5 (天品)')
  assert.equal(awakeningStone.isTradable, true, '觉醒石 isTradable 必须为 true (坊市可交易)')
})

test('inventoryGroup: 觉醒石必须归入 important (重要) 分组', () => {
  const items = [
    { itemId: 'item.awakening-stone', kind: 'material', category: 'special' },
  ]
  const equipment = []

  // 配表加载后，category 为 special 归入 important
  const groupWithCatalog = resolveInventoryGroup('item.awakening-stone', items, equipment)
  assert.equal(groupWithCatalog, 'important', '配表加载后觉醒石应归入 important 分组')

  // 目录未加载或为空时，通过 itemId 兜底仍归入 important
  const groupFallback = resolveInventoryGroup('item.awakening-stone', [], equipment)
  assert.equal(groupFallback, 'important', '未加载配表时觉醒石应通过前置兜底归入 important 分组')
})

test('inventoryGroup: 各大类物品分组判定正确', () => {
  const items = [
    { itemId: 'currency.spirit-stone', kind: 'currency', category: 'currency' },
    { itemId: 'item.gongde', kind: 'currency', category: 'currency' },
    { itemId: 'item.special-box', kind: 'resource', category: 'special' },
    { itemId: 'item.qi-gathering-pill', kind: 'consumable', category: 'consumable' },
    { itemId: 'item.ningxue-grass', kind: 'material', category: 'material' },
    { itemId: 'item.tome.heavy-slash', kind: 'consumable', category: 'manual' },
  ]
  const equipment = [
    { equipmentId: 'equipment.qingfeng-sword' },
  ]

  assert.equal(resolveInventoryGroup('currency.spirit-stone', items, equipment), 'important')
  assert.equal(resolveInventoryGroup('item.gongde', items, equipment), 'important')
  assert.equal(resolveInventoryGroup('item.special-box', items, equipment), 'important')
  assert.equal(resolveInventoryGroup('item.qi-gathering-pill', items, equipment), 'consumable')
  assert.equal(resolveInventoryGroup('equipment.qingfeng-sword', items, equipment), 'equipment')
  assert.equal(resolveInventoryGroup('item.tome.heavy-slash', items, equipment), 'manual')
  assert.equal(resolveInventoryGroup('item.fragment.heavy-slash', items, equipment), 'manual')
  assert.equal(resolveInventoryGroup('item.ningxue-grass', items, equipment), 'material')
})

test('inventoryGroup: resolveItemCategoryLabel 正确解析 special 品类标签', () => {
  const customT = (k) => k === 'game.category.special' ? '特殊' : k
  assert.equal(resolveItemCategoryLabel('special', customT), '特殊')
  assert.equal(resolveItemCategoryLabel('special'), '特殊')
})
