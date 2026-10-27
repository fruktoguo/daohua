import test from 'node:test'
import assert from 'node:assert/strict'
import {
  applyAxisDrag,
  axisPointContribution,
  formatLinggenBoostReduce,
  linggenCultivationMultiplier,
  linggenEffectiveCount,
  linggenTotal,
  linggenTechniqueSpeedRatio,
  getLinggenDerivedBonuses,
  maxReachableValue,
  remainingPoints,
  shouldShowOpenHeavenGate,
  getAllLinggenBonuses,
  getHeavenGateExpectedPoints,
} from './linggenCalculations.ts'

test('linggenCalculations：增减伤合并文本与斗法面板同口径（增伤 Linear、减伤 NonLinear）', () => {
  // 100 点 = 25 点增减伤：增伤直加 +25%，减伤按 25/(100+25) 收敛为 20%。
  assert.equal(formatLinggenBoostReduce(100), '+25% / 20%')
  // 50 点 = 12.5 点：增伤 +12%，减伤 11.11% 向下取整为 11%。
  assert.equal(formatLinggenBoostReduce(50), '+12% / 11%')
})

test('linggenCalculations：零与越界点数被夹取到合法区间', () => {
  assert.equal(formatLinggenBoostReduce(0), '0% / 0%')
  assert.equal(formatLinggenBoostReduce(180), '+25% / 20%')
  assert.equal(formatLinggenBoostReduce(-20), '0% / 0%')
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

test('linggenCalculations：衍生战斗与百艺加成及技艺速度', () => {
  const roots = { metal: 100, wood: 100, water: 100, fire: 100, earth: 100 }
  assert.equal(linggenTechniqueSpeedRatio(roots, 'alchemy'), 0.25)
  assert.equal(linggenTechniqueSpeedRatio(roots, 'forging'), 0.25)
  assert.equal(linggenTechniqueSpeedRatio(roots, 'talisman'), 0.25)
  assert.equal(linggenTechniqueSpeedRatio(roots, 'gathering'), 0.25)
  assert.equal(linggenTechniqueSpeedRatio(roots, 'enhancement'), 0.25)
  assert.equal(linggenTechniqueSpeedRatio(roots, 'formation'), 0.25)
  assert.equal(linggenTechniqueSpeedRatio(roots, 'mining'), 0)
  assert.equal(linggenTechniqueSpeedRatio(undefined, 'alchemy'), 0)

  // 火系：暴击率 + 暴击伤害 + 炼丹速度 + 炼器速度（丹器分列，避免含糊合并项）。
  const fireBonuses = getLinggenDerivedBonuses('fire', 100)
  assert.deepEqual(
    fireBonuses.map(item => [item.key, item.valueText]),
    [['critChance', '+50%'], ['critDamage', '+25%'], ['alchemySpeed', '+25%'], ['forgingSpeed', '+25%']],
  )

  // 每条衍生加成必须自带上线图标与专属色系，禁止组件层回落成纯文本单色。
  for (const item of [...fireBonuses, ...getLinggenDerivedBonuses('earth', 100)]) {
    assert.ok(item.glyph.length > 0, `${item.key} 缺少图标`)
    assert.ok(item.tone.length > 0, `${item.key} 缺少色系`)
    assert.equal(item.labelKey, `insight.heavenGate.derived.${item.key}`)
  }
  assert.equal(new Set(fireBonuses.map(item => item.tone)).size > 1, true)

  // 土系固定减伤走专属公式 1.0 + 点数 × 0.09（100 点整 10 点）。
  const earthBonuses = getLinggenDerivedBonuses('earth', 100)
  assert.equal(earthBonuses.length, 5)
  assert.equal(earthBonuses.find(item => item.key === 'fixedDamageReduction').valueText, '+10')

  // 点数归零的系别不产出任何衍生条目。
  assert.deepEqual(getLinggenDerivedBonuses('metal', 0), [])
})
test('linggenCalculations：getAllLinggenBonuses 完整聚合五系攻防与衍生加成', () => {
  const roots = { metal: 100, wood: 50, water: 0, fire: 80, earth: 20 }
  const all = getAllLinggenBonuses(roots)
  assert.equal(all.length, 5)

  const metal = all.find(item => item.element === 'metal')
  assert.equal(metal.points, 100)
  assert.equal(metal.boostReduceText, '+25% / 20%')
  assert.equal(metal.derived.length, 5) // 物理攻击/法术攻击/破招/命中/强化速度

  const water = all.find(item => item.element === 'water')
  assert.equal(water.points, 0)
  assert.equal(water.boostReduceText, '0% / 0%')
  assert.equal(water.derived.length, 0)
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

test('linggenCalculations：多轴极端拖拽绝不发生透支，未分配点数严格恒 >= 0', () => {
  const innate = { metal: 0, wood: 67, water: 99, fire: 69, earth: 0 }
  // 1. 水火全部散功到 0
  let current = { ...innate, water: 0, fire: 0 }
  // 水返还 49.5，火返还 34.5，总池 84 点
  assert.equal(remainingPoints(innate, current), 84)

  // 2. 尝试把金拉到 100：金最多只能到 0 + 84 = 84 点
  const dragMetal = applyAxisDrag(innate, current, 'metal', 100)
  assert.equal(dragMetal.roots.metal, 84)
  assert.equal(dragMetal.remainingPoints, 0)
  assert.equal(dragMetal.hitInsufficientBalance, true)
  current = dragMetal.roots

  // 3. 此时池中点数已耗尽（0 点），尝试把土也拉到 100：土必须停在 0 点，绝不能被拉高！
  const dragEarth = applyAxisDrag(innate, current, 'earth', 100)
  assert.equal(dragEarth.roots.earth, 0)
  assert.ok(dragEarth.remainingPoints >= 0, '未分配点数绝不能为负')
  assert.equal(dragEarth.hitInsufficientBalance, true)
  current = dragEarth.roots

  // 4. 尝试把木拉到 100：木必须停在先天 67 点，绝不能被拉高！
  const dragWood = applyAxisDrag(innate, current, 'wood', 100)
  assert.equal(dragWood.roots.wood, 67)
  assert.ok(dragWood.remainingPoints >= 0, '未分配点数绝不能为负')
  assert.equal(dragWood.hitInsufficientBalance, true)
})

test('linggenCalculations：getHeavenGateExpectedPoints 期望总点数计算与 500 点封顶', () => {
  // 0 次改命：初始基准期望 180 点
  assert.equal(getHeavenGateExpectedPoints(0), 180)
  // 负数或非法输入：自动防抖归 0，保持基准 180 点
  assert.equal(getHeavenGateExpectedPoints(-1), 180)
  assert.equal(getHeavenGateExpectedPoints(Number.NaN), 180)
  // 每次改命 +8 点期望
  assert.equal(getHeavenGateExpectedPoints(1), 188)
  assert.equal(getHeavenGateExpectedPoints(5), 220)
  assert.equal(getHeavenGateExpectedPoints(10), 260)
  // 40 次改命：180 + 40 * 8 = 500 点达到峰值
  assert.equal(getHeavenGateExpectedPoints(40), 500)
  // 超过 40 次：硬上限 500 点不溢出
  assert.equal(getHeavenGateExpectedPoints(50), 500)
  assert.equal(getHeavenGateExpectedPoints(100), 500)
})
