import test from 'node:test'
import assert from 'node:assert/strict'
import {
  applyAxisDrag,
  axisPointContribution,
  linggenBonusPercent,
  linggenReducePercent,
  linggenCultivationMultiplier,
  linggenEffectiveCount,
  linggenTotal,
  maxReachableValue,
  remainingPoints,
  shouldShowOpenHeavenGate,
} from './linggenCalculations.ts'

test('linggenCalculations：100 点灵根 = 100% 增伤，减伤走收益递减为 50%（伤害减半）', () => {
  assert.equal(linggenBonusPercent(100), 100)
  assert.equal(linggenReducePercent(100), 50)
  // 半量投入即为半量增伤，减伤按曲线提前收敛。
  assert.equal(linggenBonusPercent(50), 50)
  assert.equal(linggenReducePercent(50), 33)
})

test('linggenCalculations：零与越界点数被夹取到合法区间', () => {
  assert.equal(linggenBonusPercent(0), 0)
  assert.equal(linggenReducePercent(0), 0)
  assert.equal(linggenBonusPercent(180), 100)
  assert.equal(linggenBonusPercent(-20), 0)
})

test('linggenCalculations：升级需求乘区只在入天门后生效，五系齐全为三倍', () => {
  const oneRoot = { metal: 100, wood: 0, water: 0, fire: 0, earth: 0 }
  const threeRoots = { metal: 60, wood: 60, water: 60, fire: 0, earth: 0 }
  const fiveRoots = { metal: 60, wood: 60, water: 60, fire: 60, earth: 60 }

  assert.equal(linggenEffectiveCount(oneRoot), 1)
  // 未入天门一律按 1.0 计算（调配阶段不放大）。
  assert.equal(linggenCultivationMultiplier(fiveRoots, false), 1)
  // 首条灵根不额外放大，此后每多一条 +50%，五系齐全封顶 3.0。
  assert.equal(linggenCultivationMultiplier(oneRoot, true), 1)
  assert.equal(linggenCultivationMultiplier(threeRoots, true), 2)
  assert.equal(linggenCultivationMultiplier(fiveRoots, true), 3)
})

test('linggenCalculations：总值统计与空灵根', () => {
  assert.equal(linggenTotal(undefined), 0)
  assert.equal(linggenTotal({ metal: 40, wood: 50, water: 60, fire: 70, earth: 80 }), 300)
  assert.equal(linggenEffectiveCount(undefined), 0)
})

test('linggenCalculations：叩天门满修且未入天门时才展示「开天门」', () => {
  assert.equal(shouldShowOpenHeavenGate(18, false, true), true)
  // 未满修为仍然显示普通修为进度，不提前引导开天门。
  assert.equal(shouldShowOpenHeavenGate(18, false, false), false)
  // 已入天门后恢复普通突破语义。
  assert.equal(shouldShowOpenHeavenGate(18, true, true), false)
  // 未到叩天门境界不展示。
  assert.equal(shouldShowOpenHeavenGate(17, false, true), false)
})

test('linggenCalculations：点数池按「散功 2 返 1、灌注 1:1」结算', () => {
  const innate = { metal: 60, wood: 60, water: 60, fire: 60, earth: 60 }
  // 金系散到 0 返还 30 点；木系拉到 90 消耗 30 点，额度刚好用尽。
  assert.equal(axisPointContribution(60, 0), 30)
  assert.equal(axisPointContribution(60, 90), -30)
  const drained = { ...innate, metal: 0 }
  assert.equal(remainingPoints(innate, drained), 30)
  assert.equal(maxReachableValue(innate, drained, 'wood'), 90)
  const result = applyAxisDrag(innate, drained, 'wood', 90)
  assert.equal(result.roots.wood, 90)
  assert.equal(result.remainingPoints, 0)
  assert.equal(result.hitInsufficientBalance, false)
})

test('linggenCalculations：超出可分配额度时停在可达上限并标记不足', () => {
  const innate = { metal: 60, wood: 60, water: 60, fire: 60, earth: 60 }
  const drained = { ...innate, metal: 0 }
  const result = applyAxisDrag(innate, drained, 'wood', 100)
  // 只有 30 点额度，木系最多到 90；不得凭空拉到 100。
  assert.equal(result.roots.wood, 90)
  assert.equal(result.hitInsufficientBalance, true)
})

test('linggenCalculations：拖到 0 触发下限边界', () => {
  const innate = { metal: 60, wood: 60, water: 60, fire: 60, earth: 60 }
  const result = applyAxisDrag(innate, innate, 'metal', 0)
  assert.equal(result.roots.metal, 0)
  assert.equal(result.hitBound, 'min')
  assert.equal(result.remainingPoints, 30)
})
