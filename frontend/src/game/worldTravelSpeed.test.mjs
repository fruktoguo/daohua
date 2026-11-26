import test from 'node:test'
import assert from 'node:assert/strict'
import {
  applyWorldTravelSpeed,
  calculateWorldTravelSpeed,
} from './worldTravelSpeed.ts'

test('大地图境界移速：凡俗段与修真段各档位与服务端 WorldTravelRules 逐位对齐', () => {
  const cases = [
    // [level, midBreaks, majorBreaks, bonusPoints, speedPercent, multiplier]
    [0, 0, 0, 0, 100, 1.0],
    [1, 0, 0, 0, 100, 1.0],
    [6, 0, 0, 0, 100, 1.0],
    [7, 1, 0, 5, 105, 1.05],
    [12, 1, 0, 5, 105, 1.05],
    [13, 2, 0, 10, 110, 1.1],
    [18, 2, 0, 10, 110, 1.1],
    [19, 2, 1, 40, 140, 1.4],     // 练气·初（大境界入口不叠加中境界）
    [22, 3, 1, 45, 145, 1.45],    // 练气·中
    [25, 4, 1, 50, 150, 1.5],     // 练气·后
    [28, 5, 1, 55, 155, 1.55],    // 练气·极
    [30, 5, 1, 55, 155, 1.55],    // 半步筑基
    [31, 5, 2, 85, 185, 1.85],    // 筑基·初（不重复计中境界）
    [43, 8, 3, 130, 230, 2.3],    // 金丹·初
    [67, 14, 5, 220, 320, 3.2],   // 化神·初
    [126, 29, 9, 415, 515, 5.15], // 半步飞升
    [127, 29, 10, 445, 545, 5.45], // 飞升满级
    [999, 29, 10, 445, 545, 5.45], // 越界钳制
  ]

  for (const [level, midBreaks, majorBreaks, bonusPoints, speedPercent, multiplier] of cases) {
    const info = calculateWorldTravelSpeed(level)
    assert.deepEqual(info, {
      midBreaks,
      majorBreaks,
      bonusPoints,
      speedPercent,
      multiplier,
    }, `境界 Lv.${level} 移速结果不匹配`)
  }
})

test('大地图耗时折算：按移速倍率精确缩放并正确处理零值边界', () => {
  assert.equal(applyWorldTravelSpeed(14, calculateWorldTravelSpeed(19).multiplier), 10)
  assert.equal(applyWorldTravelSpeed(37, calculateWorldTravelSpeed(31).multiplier), 20)
  assert.equal(applyWorldTravelSpeed(0, 1.4), 0)
  assert.equal(applyWorldTravelSpeed(-5, 1.4), 0)
})
