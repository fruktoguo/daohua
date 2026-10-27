import test from 'node:test'
import assert from 'node:assert/strict'

import { computeSafeFloatingPosition } from './floatingPosition.ts'

test('悬浮窗定位算法 - 空间充裕时保持 1.0x 原始比例', () => {
  const anchorRect = { left: 500, top: 400, right: 600, bottom: 450, width: 100, height: 50 }
  const floatingSize = { width: 300, height: 400 }

  const result = computeSafeFloatingPosition(anchorRect, floatingSize, 'bottom', {
    viewport: { width: 1920, height: 1080 },
    padding: 10,
    gap: 8,
  })

  assert.equal(result.scale, 1.0, '空间充裕时不应缩放')
  assert.equal(result.actualPlacement, 'bottom')
  assert.equal(result.top, 450 + 8) // bottom + gap
  assert.equal(result.left, 500 + (100 - 300) / 2) // 水平居中
})

test('悬浮窗定位算法 - 竖屏多卡片纵向超高时自适应等比缩小', () => {
  // 模拟手机竖屏环境：宽 375，高 667
  const anchorRect = { left: 100, top: 200, right: 160, bottom: 250, width: 60, height: 50 }
  // 多卡片纵向堆叠：宽度 300，自然高度 1000px
  const floatingSize = { width: 300, height: 1000 }

  const result = computeSafeFloatingPosition(anchorRect, floatingSize, 'bottom', {
    viewport: { width: 375, height: 667 },
    padding: 10,
    gap: 8,
  })

  // 可用高度 = 667 - 10 * 2 = 647
  // 理论缩放比 = 647 / 1000 = 0.647
  assert.equal(result.scale, 0.647, '应根据可用高度按比例缩小')
  assert.ok(result.scale >= 0.5 && result.scale < 1.0, '缩放比例应在 50% 到 100% 之间')

  // 验证缩放后的物理尺寸是否完全落在视口内
  const scaledH = floatingSize.height * result.scale
  const scaledW = floatingSize.width * result.scale

  assert.ok(result.top >= 10, '顶部坐标不得小于安全边距 padding')
  assert.ok(result.top + scaledH <= 667 - 10 + 0.5, '底部不得溢出视口可用区域')
  assert.ok(result.left >= 10, '左侧坐标不得小于安全边距 padding')
  assert.ok(result.left + scaledW <= 375 - 10 + 0.5, '右侧不得溢出视口可用区域')
})

test('悬浮窗定位算法 - 极端超高时最多缩小到 50%（硬性下限）', () => {
  const anchorRect = { left: 100, top: 200, right: 160, bottom: 250, width: 60, height: 50 }
  // 极端超高：自然高度 1600px，在 667px 视口下需要缩小到 0.404
  const floatingSize = { width: 300, height: 1600 }

  const result = computeSafeFloatingPosition(anchorRect, floatingSize, 'bottom', {
    viewport: { width: 375, height: 667 },
    padding: 10,
    gap: 8,
    minScale: 0.5,
  })

  assert.equal(result.scale, 0.5, '即使超高极端严重，缩放比例也必须被钳位在硬下限 50%')
  assert.equal(result.top, 10, '超高钳位后顶部应锁定在安全边距')
})

test('悬浮窗定位算法 - 极窄宽度视口横向超出时等比缩小', () => {
  // 极窄视口：宽 320，高 600
  const anchorRect = { left: 50, top: 100, right: 100, bottom: 140, width: 50, height: 40 }
  // 宽卡片：宽 380，高 300
  const floatingSize = { width: 380, height: 300 }

  const result = computeSafeFloatingPosition(anchorRect, floatingSize, 'bottom', {
    viewport: { width: 320, height: 600 },
    padding: 10,
    gap: 8,
  })

  // 可用宽度 = 320 - 20 = 300
  // 理论缩放比 = 300 / 380 ≈ 0.789
  assert.equal(result.scale, 0.789)
  const scaledW = floatingSize.width * result.scale
  assert.ok(result.left + scaledW <= 320 - 10 + 0.5, '横向缩小后不得溢出右侧')
})

test('悬浮窗定位算法 - 上下翻转判定联动缩放后尺寸', () => {
  // 视口高 600，锚点在靠近底部 y = 500
  const anchorRect = { left: 100, top: 500, right: 200, bottom: 550, width: 100, height: 50 }
  // 下方剩余空间：600 - 550 = 50px（根本放不下）
  // 浮层高度 400px，上方空间 500px 充裕
  const floatingSize = { width: 300, height: 400 }

  const result = computeSafeFloatingPosition(anchorRect, floatingSize, 'bottom', {
    viewport: { width: 800, height: 600 },
    padding: 10,
    gap: 8,
  })

  assert.equal(result.actualPlacement, 'top', '下方空间不足且上方空间充足时应翻转到 top')
  assert.equal(result.scale, 1.0, '上方空间充裕无需缩放')
  assert.equal(result.top, 500 - 400 - 8) // anchor.top - height - gap
})
