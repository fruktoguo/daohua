import test from 'node:test'
import assert from 'node:assert/strict'

import { computeArenaFormationScale } from './combatScale.ts'

test('combatFormationScale: 竖屏视口 (<= 760px) 保持 1 不缩放（由移动端专用纵列样式接管）', () => {
  assert.equal(computeArenaFormationScale(320, 480, 760), 1)
  assert.equal(computeArenaFormationScale(360, 600, 375), 1)
})

test('combatFormationScale: 宽/高无效时返回 1 兜底', () => {
  assert.equal(computeArenaFormationScale(0, 0, 1024), 1)
  assert.equal(computeArenaFormationScale(-100, 300, 1024), 1)
})

test('combatFormationScale: 大屏幕/高分辨率自适应比例在 [0.9, 1.05] 正常满格展示', () => {
  // 1920x1080 100% 缩放下，阵型容器通常有 450x550 充足空间
  const scale = computeArenaFormationScale(450, 550, 1920)
  assert.ok(scale >= 0.85 && scale <= 1.05, `scale 应在 0.85~1.05 之间，实际: ${scale}`)
})

test('combatFormationScale: 1080p 125% 缩放 (阵型约 360x380) 自适应微缩约 0.58~0.65', () => {
  const scale = computeArenaFormationScale(360, 380, 1536)
  assert.ok(scale >= 0.55 && scale <= 0.65, `scale 应在 0.55~0.65 之间，实际: ${scale}`)
})

test('combatFormationScale: 1080p 150% 缩放 (阵型约 300x270) 自适应微缩约 0.40~0.46', () => {
  const scale = computeArenaFormationScale(300, 270, 1280)
  assert.ok(scale >= 0.38 && scale <= 0.46, `scale 应在 0.38~0.46 之间，实际: ${scale}`)
})

test('combatFormationScale: 极低高度/高缩放 175%~200% (阵型约 260x210) 触发自适应收缩至约 [0.30, 0.35]', () => {
  const scale = computeArenaFormationScale(260, 210, 1097)
  assert.ok(scale >= 0.30 && scale <= 0.35, `scale 应在 0.30~0.35 之间，实际: ${scale}`)
})

test('combatFormationScale: 数学防踩头定理——全分辨率网格下角色高度恒小于槽高，垂直间距绝对大于零', () => {
  // 遍历各种可能的宽高组合（宽度 220~600，高度 180~600）
  for (let w = 220; w <= 600; w += 40) {
    for (let h = 180; h <= 600; h += 30) {
      const scale = computeArenaFormationScale(w, h, 1024)
      const gap = Math.max(4, Math.min(12, w * 0.015))
      const slotHeight = (h - 2 * gap) / 3
      const actorVisualHeight = 184 * scale

      // 核心不变量 1：角色高度必须完全装进自身槽位高度内
      assert.ok(
        actorVisualHeight <= slotHeight + 0.5,
        `尺寸 ${w}x${h} 下角色高度 ${actorVisualHeight} 超出槽高 ${slotHeight}（踩头风险）`
      )

      // 核心不变量 2：上一排底端与下一排顶端必须存在正净空（绝不踩头）
      // Row 1 中心在 slotHeight / 2，Row 2 中心在 slotHeight + gap + slotHeight / 2
      const row1Bottom = slotHeight / 2 + actorVisualHeight / 2
      const row2Top = (slotHeight + gap + slotHeight / 2) - actorVisualHeight / 2
      const clearance = row2Top - row1Bottom

      assert.ok(
        clearance >= gap - 0.5,
        `尺寸 ${w}x${h} 下相邻排净空 ${clearance} 小于安全间距 ${gap}`
      )
    }
  }
})

test('combatFormationScale: 5角色与18角色阵型——上下排槽位垂直投影严格隔离', () => {
  // 5 个角色时：Slot 2 (Col 3, Row 1) 与 Slot 1 (Col 3, Row 2) 处于同列相邻排
  // 18 个角色时：Row 1/2/3 全部铺满
  const formationSizes = [
    { w: 280, h: 240, desc: '低分辨率/150%高缩放' },
    { w: 340, h: 320, desc: '中等分辨率/125%缩放' },
    { w: 450, h: 480, desc: '高分辨率/100%全屏' },
  ]

  for (const { w, h, desc } of formationSizes) {
    const scale = computeArenaFormationScale(w, h, 1024)
    const gap = Math.max(4, Math.min(12, w * 0.015))
    const slotH = (h - 2 * gap) / 3
    const actorH = 184 * scale

    // Slot 2: Row 1 (y: 0 ~ slotH)
    const slot2ActorTop = (slotH - actorH) / 2
    const slot2ActorBottom = slot2ActorTop + actorH

    // Slot 1: Row 2 (y: slotH + gap ~ 2*slotH + gap)
    const slot1Top = slotH + gap
    const slot1ActorTop = slot1Top + (slotH - actorH) / 2

    const verticalGap = slot1ActorTop - slot2ActorBottom
    assert.ok(
      verticalGap > 0,
      `在「${desc}」(${w}x${h}) 下，Slot 2 (前排上) 踩到 Slot 1 (前排中) 头上，重叠量: ${-verticalGap}px`
    )
  }
})
