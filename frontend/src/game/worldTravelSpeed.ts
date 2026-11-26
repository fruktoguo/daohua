/**
 * 境界驱动的大地图移动速度纯计算模块：与服务端 `Domain.World.WorldTravelRules` 逐位同源（总纲 §3.1 / §8.3.1b）。
 *
 * 规则：
 * - 每次中境界阶段晋升 +5%、每次大境界突破 +30%，采用线性加算；
 * - 大境界入口仅计大境界突破，不重复叠加中境界（与根基上限及装备固定回血同口径）：
 *   凡俗段 武(1~6) 0 中 / 玄(7~12) 1 中 / 天(13~18) 2 中；
 *   修真段 Lv.19~127 每 12 层一大境界，入口第 1 层（及 Lv.127 飞升）计 1 次大境界，
 *   档内第 4/7/10 层（中/后/极）各计 1 次中境界。
 */

/** 每次中境界晋升的大地图移速增幅点数（5%）。 */
export const MID_REALM_TRAVEL_SPEED_BONUS_POINTS = 5

/** 每次大境界突破的大地图移速增幅点数（30%）。 */
export const MAJOR_REALM_TRAVEL_SPEED_BONUS_POINTS = 30

/** 大地图移速成长封顶境界等级（Lv.127 飞升）。 */
export const MAX_TRAVEL_SPEED_REALM_LEVEL = 127

/** 角色当前境界对应的大地图移动速度汇总。 */
export interface WorldTravelSpeedInfo {
  /** 累计中境界阶段晋升次数（0 ~ 29）。 */
  midBreaks: number
  /** 累计大境界突破次数（0 ~ 10）。 */
  majorBreaks: number
  /** 总移速增幅点数（整数百分比，0 ~ 445，例如 40 表示 +40%）。 */
  bonusPoints: number
  /** 总移速百分比（整数，100 ~ 545，例如 140 表示 140%）。 */
  speedPercent: number
  /** 精确移速倍率（1.00 ~ 5.45）。 */
  multiplier: number
}

/**
 * 计算指定境界等级的大地图移动速度信息（与 `WorldTravelRules` 逐位对齐）。
 */
export function calculateWorldTravelSpeed(realmLevel = 1): WorldTravelSpeedInfo {
  const normalized = Number.isFinite(realmLevel) ? Math.trunc(realmLevel) : 1
  const level = Math.min(MAX_TRAVEL_SPEED_REALM_LEVEL, Math.max(1, normalized))

  let midBreaks = 0
  let majorBreaks = 0
  if (level <= 18) {
    midBreaks = Math.floor((level - 1) / 6)
  } else {
    const layerIndex = level - 19
    midBreaks = 2 + Math.floor(layerIndex / 12) * 3 + Math.floor((layerIndex % 12) / 3)
    majorBreaks = Math.floor(layerIndex / 12) + 1
  }

  const bonusPoints =
    midBreaks * MID_REALM_TRAVEL_SPEED_BONUS_POINTS +
    majorBreaks * MAJOR_REALM_TRAVEL_SPEED_BONUS_POINTS
  const speedPercent = 100 + bonusPoints
  const multiplier = speedPercent / 100

  return {
    midBreaks,
    majorBreaks,
    bonusPoints,
    speedPercent,
    multiplier,
  }
}

/**
 * 按大地图移速倍率折算路径耗时（秒）。
 */
export function applyWorldTravelSpeed(baseDurationSeconds: number, multiplier: number): number {
  if (!Number.isFinite(baseDurationSeconds) || baseDurationSeconds <= 0) return 0
  if (!Number.isFinite(multiplier) || multiplier <= 0) return baseDurationSeconds
  return baseDurationSeconds / multiplier
}
