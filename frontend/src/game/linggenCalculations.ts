/**
 * 开天门与五行灵根的纯计算：展示换算 + 阵盘点数池分配。
 *
 * 与领域 `HeavenGateRules` 同口径，但只承担前端所需的换算与即时反馈，
 * 不重复实现业务准入判断（能否突破、改命是否成立一律由服务端裁决）。
 */
import {
  BOARD_CENTER,
  BOARD_MAX_RADIUS,
  BOARD_MIN_RADIUS,
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
 * 灵根提供的五行增伤百分比（Linear）：100 点即 25%（总纲 §7.7.5.1）。
 * @param points 该系灵根点数。
 */
export function linggenBonusPercent(points: number): number {
  const safe = Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.trunc(points) || 0))
  return Math.round((safe / LINGGEN_AXIS_CAP) * 25)
}

/**
 * 灵根提供的五行减伤百分比（NonLinear 收敛）：100 点对应 25 点减伤点数（总纲 §7.7.5.1）。
 * @param points 该系灵根点数。
 */
export function linggenReducePercent(points: number): number {
  const safe = Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.trunc(points) || 0))
  return Math.round((safe / LINGGEN_AXIS_CAP) * 25)
}

/** 灵根百艺速度加成：100 点对应 25% 技艺速度。 */
export function linggenTechniqueSpeedRatio(linggen: LinggenPoints | undefined, techniqueId: string | null): number {
  if (!linggen || !techniqueId) return 0
  switch (techniqueId) {
    case 'alchemy':
    case 'forging':
      return 0.25 * (Math.max(0, Math.min(LINGGEN_AXIS_CAP, linggen.fire)) / LINGGEN_AXIS_CAP)
    case 'talisman':
      return 0.25 * (Math.max(0, Math.min(LINGGEN_AXIS_CAP, linggen.water)) / LINGGEN_AXIS_CAP)
    case 'gathering':
      return 0.25 * (Math.max(0, Math.min(LINGGEN_AXIS_CAP, linggen.wood)) / LINGGEN_AXIS_CAP)
    case 'enhancement':
      return 0.25 * (Math.max(0, Math.min(LINGGEN_AXIS_CAP, linggen.metal)) / LINGGEN_AXIS_CAP)
    case 'formation':
      return 0.25 * (Math.max(0, Math.min(LINGGEN_AXIS_CAP, linggen.earth)) / LINGGEN_AXIS_CAP)
    default:
      return 0
  }
}

export interface LinggenDerivedBonus {
  labelKey: string
  valueText: string
}

/** 获取单系灵根的所有衍生战斗与百艺加成条目。 */
export function getLinggenDerivedBonuses(element: LinggenElementKey, points: number): LinggenDerivedBonus[] {
  const safe = Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.trunc(points) || 0))
  if (safe <= 0) return []
  const factor = safe / LINGGEN_AXIS_CAP
  switch (element) {
    case 'fire':
      return [
        { labelKey: 'insight.heavenGate.derived.critChance', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.critDamage', valueText: `+${+(25 * factor).toFixed(1)}%` },
        { labelKey: 'insight.heavenGate.derived.alchemyForgingSpeed', valueText: `+${Math.round(25 * factor)}%` },
      ]
    case 'water':
      return [
        { labelKey: 'insight.heavenGate.derived.maxMana', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.parry', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.regen', valueText: `+${Math.round(100 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.talismanSpeed', valueText: `+${Math.round(25 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.searchSpeed', valueText: `+${Math.round(20 * factor)}%` },
      ]
    case 'wood':
      return [
        { labelKey: 'insight.heavenGate.derived.maxHp', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.healingBonus', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.gatheringSpeed', valueText: `+${Math.round(25 * factor)}%` },
      ]
    case 'metal':
      return [
        { labelKey: 'insight.heavenGate.derived.attack', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.break', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.accuracy', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.enhancementSpeed', valueText: `+${Math.round(25 * factor)}%` },
      ]
    case 'earth': {
      const flatDr = +(1.0 + safe * 0.09).toFixed(2)
      return [
        { labelKey: 'insight.heavenGate.derived.defense', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.criticalAvoidance', valueText: `+${Math.round(50 * factor)}%` },
        { labelKey: 'insight.heavenGate.derived.fixedDamageReduction', valueText: `+${flatDr}` },
        { labelKey: 'insight.heavenGate.derived.formationSpeed', valueText: `+${Math.round(25 * factor)}%` },
      ]
    }
  }
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

/** 其余四轴让点支持下，该轴可达的最高理论上限（保证 remainingPoints 恒 >= 0，严禁透支）。 */
export function maxReachableValue(
  innate: LinggenPoints,
  current: LinggenPoints,
  axisKey: LinggenElementKey,
): number {
  const innateVal = innate[axisKey]
  const otherPool = LINGGEN_ELEMENT_KEYS.reduce(
    (sum, key) => (key === axisKey ? sum : sum + axisPointContribution(innate[key], current[key])),
    0,
  )
  const maxAllowed = otherPool >= 0
    ? innateVal + otherPool
    : innateVal + 2 * otherPool

  return Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.floor(maxAllowed)))
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

/** 把某轴拖到目标值，返回受限后的结果（点数池绝不允许透支，remainingPoints 恒 >= 0）。 */
export function applyAxisDrag(
  innate: LinggenPoints,
  current: LinggenPoints,
  axisKey: LinggenElementKey,
  targetValue: number,
): AxisDragResult {
  const clampedTarget = clampPoints(targetValue)
  const before = current[axisKey]
  const reachable = maxReachableValue(innate, current, axisKey)
  // 目标值绝对不能超过在点数池约束下的理论上限！
  const applied = Math.min(clampedTarget, reachable)
  const next: LinggenPoints = { ...current, [axisKey]: applied }

  let finalRemaining = remainingPoints(innate, next)
  // 坚固守卫：若存在浮点微小误差导致 remainingPoints < 0，坚决回退保证恒 >= 0
  while (finalRemaining < 0 && next[axisKey] > 0) {
    next[axisKey] -= 1
    finalRemaining = remainingPoints(innate, next)
  }

  const hitInsufficientBalance = applied < clampedTarget
  const hitBound =
    applied === 0 && targetValue <= 0
      ? 'min'
      : applied === LINGGEN_AXIS_CAP && clampedTarget >= LINGGEN_AXIS_CAP
        ? 'max'
        : null

  return {
    roots: next,
    remainingPoints: Math.max(0, finalRemaining),
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
  const projected = dx * axis.unitCos + dy * axis.unitSin
  // 投影在内环以内即视为归零（0 点），拉至外圈或以外记为满值（100 点）。
  if (projected <= BOARD_MIN_RADIUS) return 0
  if (projected >= BOARD_MAX_RADIUS) return LINGGEN_AXIS_CAP
  const ratio = (projected - BOARD_MIN_RADIUS) / (BOARD_MAX_RADIUS - BOARD_MIN_RADIUS)
  return Math.round(ratio * LINGGEN_AXIS_CAP)
}
