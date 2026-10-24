/**
 * 战场九宫格阵型自适应缩放推导算法
 * 根据阵型容器的实际可用宽高，推导 --arena-scale，保证在任意分辨率与缩放下
 * 双方 18 个角色（3x3 allies + 3x3 enemies）全部完整容纳且绝不发生垂直碰撞踩头。
 */

/** 角色基准卡片尺寸：宽 96px，高 206px（包含 overhead 42px + avatar 120px + action bar 12px + 底部 buff 列表 24px + 防撞边距 8px） */
export const ARENA_ACTOR_BASE_WIDTH = 96
export const ARENA_ACTOR_BASE_HEIGHT = 206

/**
 * 根据参战单位数量推导阵型所占有效行数：
 * - 1~3 人：槽位为 [1, 4, 7]，全部位于九宫格 Row 2（正中间行），实际仅占 1 行；
 * - 4~6 人：槽位为 [1, 4, 7, 2, 5, 8]，占用 Row 2 与 Row 1，实际占 2 行；
 * - 7~9 人：槽位包含 Row 3，占用 3 行。
 */
export function resolveOccupiedRows(unitCount?: number): number {
  if (typeof unitCount !== 'number' || !Number.isFinite(unitCount) || unitCount <= 0) return 3
  if (unitCount <= 3) return 1
  if (unitCount <= 6) return 2
  return 3
}

/**
 * 推导战场阵型缩放比例
 * @param width 3x3 阵型容器总宽度
 * @param height 3x3 阵型容器总高度
 * @param windowWidth 视口宽度（<= 760px 竖屏退化为不缩放）
 * @param unitCount 该侧参战单位数（缺省按 3 行保守估算，<=3 人时按单行自适应展开）
 */
export function computeArenaFormationScale(
  width: number,
  height: number,
  windowWidth = 1024,
  unitCount = 9
): number {
  if (width <= 0 || height <= 0) return 1
  if (windowWidth <= 760 || width <= 200) return 1

  // 九宫阵型 gap 遵循 CSS clamp(4px, 1vw, 12px)
  const gap = Math.max(4, Math.min(12, width * 0.015))
  const slotWidth = (width - 2 * gap) / 3

  const rows = resolveOccupiedRows(unitCount)
  // 当占用 1 行时，该行单位坐镇正中（Row 2），上下两侧（Row 1 与 Row 3）完全为空，
  // 垂直可用空间为阵型容器的整高；当占用 2 行时分摊 2 行高度；3 行时除以 3。
  const slotHeight = rows === 1
    ? (height - 2 * gap)
    : rows === 2
      ? (height - gap) / 2
      : (height - 2 * gap) / 3

  const scaleH = (slotHeight - 2) / ARENA_ACTOR_BASE_HEIGHT
  const scaleW = (slotWidth - 2) / ARENA_ACTOR_BASE_WIDTH

  // 取两轴较严格的缩放比，允许范围 [0.2, 1.05]，保留两位小数防抖
  const ideal = Math.min(1.05, Math.min(scaleH, scaleW))
  return Math.max(0.2, Math.min(1.05, Math.round(ideal * 100) / 100))
}
