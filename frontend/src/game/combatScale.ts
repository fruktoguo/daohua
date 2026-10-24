/**
 * 战场九宫格阵型自适应缩放推导算法
 * 根据阵型容器的实际可用宽高，推导 --arena-scale，保证在任意分辨率与缩放下
 * 双方 18 个角色（3x3 allies + 3x3 enemies）全部完整容纳且绝不发生垂直碰撞踩头。
 */

/** 角色基准卡片尺寸：宽 96px，高 184px（对齐 CSS --actor-base-height: 184px） */
export const ARENA_ACTOR_BASE_WIDTH = 96
export const ARENA_ACTOR_BASE_HEIGHT = 184

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
  unitCount = 9,
  explicitRows?: number
): number {
  if (width <= 0 || height <= 0) return 1
  if (windowWidth <= 760 || width <= 200) return 1

  // 九宫阵型 gap 遵循 CSS clamp(4px, 1vw, 12px)
  const gap = Math.max(4, Math.min(12, width * 0.015))
  const slotWidth = (width - 2 * gap) / 3

  const rows = explicitRows ?? resolveOccupiedRows(unitCount)
  // 严格根据参战行数推导每个槽位的实际可用高度：
  // 1 行时：占满整个垂直空间（上下无同行挤压），满额 1.0~1.05 舒展；
  // 2 行时：等分两行高度 (height - gap) / 2，严格缩放到槽位内，绝不溢出到顶层与底层；
  // 3 行时：等分三行高度 (height - 2*gap) / 3，严格限制卡片总高度 <= 槽高，
  //         彻底消除第 1 排名字被顶部裁切、第 3 排行动栏被底部裁切的几何缺陷。
  const slotHeight = rows === 1
    ? (height - 2 * gap)
    : rows === 2
      ? (height - gap) / 2
      : (height - 2 * gap) / 3

  const scaleH = Math.max(0.2, (slotHeight - 4) / ARENA_ACTOR_BASE_HEIGHT)
  const scaleW = Math.max(0.2, (slotWidth - 2) / ARENA_ACTOR_BASE_WIDTH)

  const ideal = Math.min(1.05, Math.min(scaleH, scaleW))
  return Math.max(0.2, Math.min(1.05, Math.round(ideal * 100) / 100))
}
