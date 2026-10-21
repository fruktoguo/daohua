import test from 'node:test'
import assert from 'node:assert/strict'
import {
  getBuildingBonusFormulas,
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

test('buildingCalculations：加成公式同时给出 1 级基准与每级增幅', () => {
  const definition = {
    buildingId: 'building.alchemy-hearth',
    name: '丹房',
    description: '',
    effect: '提升此地炼丹行动的速度',
    requiredRealmLevel: 1,
    group: 'production',
    baseModifiers: { alchemySpeedRatio: 0.12 },
    growthModifiers: { alchemySpeedRatio: 0.02 },
  }

  const formulas = getBuildingBonusFormulas(definition)
  assert.equal(formulas.length, 1)
  assert.equal(formulas[0].key, 'alchemySpeedRatio')
  assert.equal(formulas[0].label, '炼丹行动速度')
  assert.equal(formulas[0].baseText, '+12%')
  assert.equal(formulas[0].growthText, '+2%')
})

test('buildingCalculations：固定值加成（战斗气血回复）公式带时间单位', () => {
  const definition = {
    buildingId: 'building.rejuvenation-array',
    name: '回春阵',
    description: '',
    effect: '',
    requiredRealmLevel: 19,
    group: 'combat',
    baseModifiers: { combatHitpointRegen: 2.4, monsterWeakenRatio: 0.06 },
    growthModifiers: { combatHitpointRegen: 0.4, monsterWeakenRatio: 0.01 },
  }

  const formulas = getBuildingBonusFormulas(definition)
  const hpRegen = formulas.find(item => item.key === 'combatHitpointRegen')
  const weaken = formulas.find(item => item.key === 'monsterWeakenRatio')

  assert.equal(hpRegen.baseText, '+2.4/5秒')
  assert.equal(hpRegen.growthText, '+0.4/5秒')
  // 怪物削弱为折减语义，基准与增幅都带负号
  assert.equal(weaken.baseText, '-6%')
  assert.equal(weaken.growthText, '-1%')
})

test('buildingCalculations：缺 growthModifiers 时每级增幅回落基准值（与服务端同口径）', () => {
  const flat = { explorationOutputRatio: 0.05 }

  const formulas = getBuildingBonusFormulas(flat)
  assert.equal(formulas[0].baseText, '+5%')
  assert.equal(formulas[0].growthText, '+5%')
})

test('buildingCalculations：显式零增幅时不输出每级文案（图鉴不渲染空成长行）', () => {
  const definition = {
    buildingId: 'building.test',
    name: '测试',
    description: '',
    effect: '',
    requiredRealmLevel: 1,
    group: 'other',
    baseModifiers: { explorationOutputRatio: 0.05 },
    growthModifiers: { explorationOutputRatio: 0 },
  }

  const formulas = getBuildingBonusFormulas(definition)
  assert.equal(formulas[0].baseText, '+5%')
  assert.equal(formulas[0].growthText, '')
})
