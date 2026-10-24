/**
 * 战场九宫格阵型自适应缩放推导算法
 * 根据阵型容器的实际可用宽高，推导 --arena-scale，保证在任意分辨率与缩放下
 * 双方 18 个角色（3x3 allies + 3x3 enemies）全部完整容纳且绝不发生垂直碰撞踩头。
 */

/** 角色基准卡片尺寸：宽 96px，高 206px（包含 overhead 42px + avatar 120px + action bar 12px + 底部 buff 列表 24px + 防撞边距 8px） */
export const ARENA_ACTOR_BASE_WIDTH = 96
export const ARENA_ACTOR_BASE_HEIGHT = 206

/**
 * 推导战场阵型缩放比例
 * @param width 3x3 阵型容器总宽度
 * @param height 3x3 阵型容器总高度
 * @param windowWidth 视口宽度（<= 760px 竖屏退化为不缩放）
 */
export function computeArenaFormationScale(width: number, height: number, windowWidth = 1024): number {
  if (width <= 0 || height <= 0) return 1
  if (windowWidth <= 760 || width <= 200) return 1

  // 九宫阵型 gap 遵循 CSS clamp(4px, 1vw, 12px)
  const gap = Math.max(4, Math.min(12, width * 0.015))
  const slotWidth = (width - 2 * gap) / 3
  const slotHeight = (height - 2 * gap) / 3

  const scaleH = (slotHeight - 2) / ARENA_ACTOR_BASE_HEIGHT
  const scaleW = (slotWidth - 2) / ARENA_ACTOR_BASE_WIDTH

  // 取两轴较严格的缩放比，允许范围 [0.2, 1.05]，保留两位小数防抖
  const ideal = Math.min(1.05, Math.min(scaleH, scaleW))
  return Math.max(0.2, Math.min(1.05, Math.round(ideal * 100) / 100))
}
