import test from 'node:test'
import assert from 'node:assert/strict'
import {
  getBuildingBonusItems,
  getBuildingUpgradeComparison,
  getBuildingSummaryBonusText,
} from './buildingCalculations.ts'

test('buildingCalculations：未建成建筑（Lv.0）加成值为 0%', () => {
  const modifiers = {
    explorationOutputRatio: 0.05,
  }

  const items = getBuildingBonusItems(modifiers, 0)
  assert.equal(items.length, 1)
  assert.equal(items[0].key, 'explorationOutputRatio')
  assert.equal(items[0].valueText, '0%')
  assert.equal(items[0].numericValue, 0)
})

test('buildingCalculations：有效等级（Lv.2）计算线性加成（兼容单 modifiers）', () => {
  const modifiers = {
    explorationOutputRatio: 0.05,
  }

  const items = getBuildingBonusItems(modifiers, 2)
  assert.equal(items.length, 1)
  assert.equal(items[0].key, 'explorationOutputRatio')
  assert.equal(items[0].valueText, '+10%')
  assert.equal(items[0].numericValue, 0.1)
})

test('buildingCalculations：支持 baseModifiers 与 growthModifiers 分级加成（如初始 12%，每级 2%）', () => {
  const definition = {
    buildingId: 'building.gathering-tent',
    name: '灵材坊',
    description: '',
    effect: '',
    requiredRealmLevel: 1,
    group: 'production',
    baseModifiers: {
      gatheringSpeedRatio: 0.12,
    },
    growthModifiers: {
      gatheringSpeedRatio: 0.02,
    },
  }

  // Lv.1 为 12%
  const lv1Items = getBuildingBonusItems(definition, 1)
  assert.equal(lv1Items.length, 1)
  assert.equal(lv1Items[0].key, 'gatheringSpeedRatio')
  assert.equal(lv1Items[0].valueText, '+12%')

  // Lv.2 为 14%
  const lv2Items = getBuildingBonusItems(definition, 2)
  assert.equal(lv2Items[0].valueText, '+14%')

  // Lv.10 为 12% + 2% * 9 = 30%
  const lv10Items = getBuildingBonusItems(definition, 10)
  assert.equal(lv10Items[0].valueText, '+30%')

  // 升级对比 Lv.1 -> Lv.2 (+2%)
  const upgradeRows = getBuildingUpgradeComparison(definition, 1, 2)
  assert.equal(upgradeRows[0].before, '+12%')
  assert.equal(upgradeRows[0].after, '+14%')
  assert.equal(upgradeRows[0].delta, '+2%')
})

test('buildingCalculations：战斗回复带时间单位、怪物削弱带负号且无硬上限', () => {
  const definition = {
    buildingId: 'building.test',
    name: '战阵',
    description: '',
    effect: '',
    requiredRealmLevel: 19,
    group: 'combat',
    baseModifiers: {
      combatHitpointRegen: 2.4,
      combatManapointRegen: 2.4,
      monsterWeakenRatio: 0.06,
    },
    growthModifiers: {
      combatHitpointRegen: 0.4,
      combatManapointRegen: 0.4,
      monsterWeakenRatio: 0.01,
    },
  }

  const items = getBuildingBonusItems(definition, 95)
  const hpRegen = items.find(i => i.key === 'combatHitpointRegen')
  const mpRegen = items.find(i => i.key === 'combatManapointRegen')
  const weaken = items.find(i => i.key === 'monsterWeakenRatio')

  assert.ok(hpRegen)
  // 2.4 + 0.4 * 94 = 40
  assert.equal(hpRegen.valueText, '+40/5秒')
  assert.ok(mpRegen)
  assert.equal(mpRegen.valueText, '+40/5秒')
  assert.ok(weaken)
  // 0.06 + 0.01 * 94 = 1.0 (100%)
  assert.equal(weaken.valueText, '-100%')
})

test('buildingCalculations：列表摘要卡片直显文本', () => {
  const definition = {
    buildingId: 'building.gathering-tent',
    name: '灵材坊',
    description: '',
    effect: '',
    requiredRealmLevel: 1,
    group: 'production',
    baseModifiers: {
      gatheringSpeedRatio: 0.12,
    },
    growthModifiers: {
      gatheringSpeedRatio: 0.02,
    },
  }

  assert.equal(getBuildingSummaryBonusText(definition, 2), '采集采伐速度 +14%')
  assert.equal(getBuildingSummaryBonusText(definition, 0), '建成生效: 采集采伐速度 +12%')
})
