/**
 * 开天门与五行灵根的纯计算：展示换算 + 阵盘点数池分配。
 *
 * 与领域 `HeavenGateRules` 同口径，但只承担前端所需的换算与即时反馈，
 * 不重复实现业务准入判断（能否突破、改命是否成立一律由服务端裁决）。
 */
import {
  BOARD_CENTER,
  BOARD_MAX_RADIUS,
  LINGGEN_AXES,
  LINGGEN_AXIS_CAP,
  LINGGEN_ELEMENT_KEYS,
  clampPoints,
  type LinggenElementKey,
  type LinggenPoints,
} from './linggenBoard.ts'

export type { LinggenElementKey, LinggenPoints }
export { LINGGEN_AXIS_CAP, LINGGEN_ELEMENT_KEYS }

/** 叩天门境界等级：该级满修为后必须先开天门取得灵根。 */
export const HEAVEN_GATE_REALM_LEVEL = 18

/**
 * 灵根提供的五行增伤百分比（Linear）：100 点即 100%。
 * @param points 该系灵根点数。
 */
export function linggenBonusPercent(points: number): number {
  const safe = Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.trunc(points) || 0))
  return Math.round((safe / LINGGEN_AXIS_CAP) * 100)
}

/**
 * 灵根提供的五行实际减伤百分比（NonLinear 收益递减）：100 点对应 50%，即伤害减半。
 * @param points 该系灵根点数。
 */
export function linggenReducePercent(points: number): number {
  const safe = Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.trunc(points) || 0))
  return Math.round((safe / (safe + LINGGEN_AXIS_CAP)) * 100)
}

/** 五系点数总和。 */
export function linggenTotal(values: LinggenPoints | undefined): number {
  if (!values) return 0
  return values.metal + values.wood + values.water + values.fire + values.earth
}

/** 有效灵根条数（点数 > 0 的系别数量）。 */
export function linggenEffectiveCount(values: LinggenPoints | undefined): number {
  if (!values) return 0
  return LINGGEN_ELEMENT_KEYS.reduce((count, key) => (values[key] > 0 ? count + 1 : count), 0)
}

/**
 * 灵根带来的升级所需修为乘区：每多一条有效灵根 +50%，五系齐全为 3.0 倍。
 * 未入天门时恒为 1.0（调配阶段不放大）。
 * @param values 生效灵根。
 * @param entered 是否已入天门。
 */
export function linggenCultivationMultiplier(values: LinggenPoints | undefined, entered: boolean): number {
  if (!entered) return 1
  // 五系齐全即 3.0 倍封顶：基础 1.0 已含首条灵根，此后每多一条再 +50%。
  const effective = linggenEffectiveCount(values)
  if (effective <= 0) return 1
  return 1 + 0.5 * (Math.min(effective, 5) - 1)
}

/**
 * 叩天门满修后突破按钮的语义：返回 true 表示当前应展示「开天门」而非「破境突破」。
 * @param realmLevel 服务端权威境界等级。
 * @param entered 是否已入天门。
 * @param expFull 本层修为是否圆满。
 */
export function shouldShowOpenHeavenGate(realmLevel: number, entered: boolean, expFull: boolean): boolean {
  return realmLevel >= HEAVEN_GATE_REALM_LEVEL && !entered && expFull
}

// ─────────────────────────────────────────────────────────────────────────────
// 点数池分配模型（阵盘拖拽）
//
// 每个灵根有抽取时的先天值 B：低于 B 时按「散功 2 返 1」折半返还点数，
// 高于 B 时按 1:1 消耗点数。玩家只能在可用额度内调整五行分布。
// ─────────────────────────────────────────────────────────────────────────────

/** 单轴净贡献：散功返还记正，灌注消耗记负。 */
export function axisPointContribution(innateValue: number, currentValue: number): number {
  return currentValue <= innateValue
    ? (innateValue - currentValue) * 0.5
    : -(currentValue - innateValue)
}

/** 剩余可分配点数（为负表示已透支）。 */
export function remainingPoints(innate: LinggenPoints, current: LinggenPoints): number {
  const total = LINGGEN_ELEMENT_KEYS.reduce(
    (sum, key) => sum + axisPointContribution(innate[key], current[key]),
    0,
  )
  return Math.round(total * 2) / 2
}

/** 其余四轴让点支持下，该轴可达的最高值。 */
export function maxReachableValue(
  innate: LinggenPoints,
  current: LinggenPoints,
  axisKey: LinggenElementKey,
): number {
  const pool = LINGGEN_ELEMENT_KEYS.reduce(
    (sum, key) => (key === axisKey ? sum : sum + axisPointContribution(innate[key], current[key])),
    0,
  )
  return Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.floor(current[axisKey] + pool)))
}

/** 拖拽结果：新分布、剩余点数与三种边界反馈。 */
export interface AxisDragResult {
  roots: LinggenPoints
  remainingPoints: number
  appliedDelta: number
  /** 因点数不足未能拉到目标值。 */
  hitInsufficientBalance: boolean
  /** 触达 0 或 100 的边界。 */
  hitBound: 'min' | 'max' | null
}

/** 把某轴拖到目标值，返回受限后的结果。 */
export function applyAxisDrag(
  innate: LinggenPoints,
  current: LinggenPoints,
  axisKey: LinggenElementKey,
  targetValue: number,
): AxisDragResult {
  const clampedTarget = clampPoints(targetValue)
  const before = current[axisKey]
  const delta = clampedTarget - before
  const next: LinggenPoints = { ...current }
  let hitInsufficientBalance = false
  let hitBound: 'min' | 'max' | null = null

  if (delta > 0) {
    const reachable = maxReachableValue(innate, current, axisKey)
    const applied = Math.min(clampedTarget, reachable)
    next[axisKey] = applied
    if (applied < clampedTarget) hitInsufficientBalance = true
    if (applied >= LINGGEN_AXIS_CAP && clampedTarget >= LINGGEN_AXIS_CAP) hitBound = 'max'
  } else if (delta < 0) {
    next[axisKey] = clampedTarget
    if (clampedTarget === 0 && targetValue <= 0) hitBound = 'min'
  }

  return {
    roots: next,
    remainingPoints: remainingPoints(innate, next),
    appliedDelta: next[axisKey] - before,
    hitInsufficientBalance,
    hitBound,
  }
}

/** 指针在轴向上的投影值（0~100）。 */
export function projectPointerToAxis(
  pointer: { x: number; y: number },
  axisIndex: number,
): number {
  const axis = LINGGEN_AXES[axisIndex]
  if (!axis) return 0
  const dx = pointer.x - BOARD_CENTER.x
  const dy = pointer.y - BOARD_CENTER.y
  const projected = Math.max(0, dx * axis.unitCos + dy * axis.unitSin)
  return Math.round((projected / BOARD_MAX_RADIUS) * LINGGEN_AXIS_CAP)
}
