import test from 'node:test'
import assert from 'node:assert/strict'
import {
  isStorageBag,
  getStorageBagLoot,
  formatStorageBagLootQuantity,
  formatStorageBagLootChance,
  STORAGE_BAG_LOOT_TABLE,
} from './storageBagLoot.ts'

test('storageBagLoot: 储物袋识别与基础配置完备性', () => {
  assert.equal(isStorageBag('item.storage-bag.broken'), true)
  assert.equal(isStorageBag('item.storage-bag.advanced'), true)
  assert.equal(isStorageBag('item.ningxue-grass'), false)
  assert.equal(isStorageBag('currency.spirit-stone'), false)

  const brokenLoot = getStorageBagLoot('item.storage-bag.broken')
  assert.equal(brokenLoot.length, 5)

  // 空配置或非储物袋安全退化
  assert.deepEqual(getStorageBagLoot('unknown-item'), [])
})

test('storageBagLoot: 破损储物袋产出条目与概率模型严格对齐', () => {
  const loot = STORAGE_BAG_LOOT_TABLE['item.storage-bag.broken']
  assert.ok(loot, '破损储物袋必须在掉落表中')

  // 1. 基础灵石：1~1,000，100% 必出
  const spiritBase = loot.find(item => item.id === 'spirit-stone-base')
  assert.ok(spiritBase)
  assert.equal(spiritBase.itemId, 'currency.spirit-stone')
  assert.equal(spiritBase.minQuantity, 1)
  assert.equal(spiritBase.maxQuantity, 1000)
  assert.equal(spiritBase.chance, 1.0)
  assert.equal(formatStorageBagLootQuantity(spiritBase), '1~1,000')
  assert.equal(formatStorageBagLootChance(spiritBase.chance), '100% 必得')

  // 2. 暴击灵石：1~10,000，10% 概率
  const spiritCrit = loot.find(item => item.id === 'spirit-stone-crit')
  assert.ok(spiritCrit)
  assert.equal(spiritCrit.minQuantity, 1)
  assert.equal(spiritCrit.maxQuantity, 10000)
  assert.equal(spiritCrit.chance, 0.1)
  assert.equal(spiritCrit.isExtra, true)
  assert.equal(formatStorageBagLootQuantity(spiritCrit), '1~10,000')
  assert.equal(formatStorageBagLootChance(spiritCrit.chance), '10% 概率')

  // 3. 基础功德：5~10，10% 概率
  const gongdeBase = loot.find(item => item.id === 'gongde-base')
  assert.ok(gongdeBase)
  assert.equal(gongdeBase.itemId, 'item.gongde')
  assert.equal(gongdeBase.minQuantity, 5)
  assert.equal(gongdeBase.maxQuantity, 10)
  assert.equal(gongdeBase.chance, 0.1)
  assert.equal(formatStorageBagLootQuantity(gongdeBase), '5~10')
  assert.equal(formatStorageBagLootChance(gongdeBase.chance), '10% 概率')

  // 4. 暴击功德：50~100，1% 概率
  const gongdeCrit = loot.find(item => item.id === 'gongde-crit')
  assert.ok(gongdeCrit)
  assert.equal(gongdeCrit.minQuantity, 50)
  assert.equal(gongdeCrit.maxQuantity, 100)
  assert.equal(gongdeCrit.chance, 0.01)
  assert.equal(gongdeCrit.isExtra, true)
  assert.equal(formatStorageBagLootQuantity(gongdeCrit), '50~100')
  assert.equal(formatStorageBagLootChance(gongdeCrit.chance), '1% 概率')

  // 5. 觉醒石：固定 1 个，0.1% 概率
  const awakeningStone = loot.find(item => item.id === 'awakening-stone')
  assert.ok(awakeningStone)
  assert.equal(awakeningStone.itemId, 'item.awakening-stone')
  assert.equal(awakeningStone.minQuantity, 1)
  assert.equal(awakeningStone.maxQuantity, 1)
  assert.equal(awakeningStone.chance, 0.001)
  assert.equal(formatStorageBagLootQuantity(awakeningStone), '× 1')
  assert.equal(formatStorageBagLootChance(awakeningStone.chance), '0.1% 概率')
})
