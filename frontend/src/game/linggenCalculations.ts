/**
 * 开天门与五行灵根的纯展示计算。
 *
 * 与领域 `HeavenGateRules` 同口径，但只承担前端展示所需的换算，
 * 不重复实现业务准入判断（能否突破、改命是否成立一律由服务端裁决）。
 */

/** 五行灵根系别键（与领域 LinggenElement 一一对应）。 */
export type LinggenElementKey = 'metal' | 'wood' | 'water' | 'fire' | 'earth'

/** 五行固定顺序：金属性起顺时针排布。 */
export const LINGGEN_ELEMENT_KEYS: readonly LinggenElementKey[] = ['metal', 'wood', 'water', 'fire', 'earth']

/** 单系灵根点数上限（雷达图刻度）。 */
export const LINGGEN_AXIS_CAP = 100

/** 五行灵根点数形状（与服务端 progression.heavenGate* 透传一致）。 */
export interface LinggenPoints {
  metal: number
  wood: number
  water: number
  fire: number
  earth: number
}

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
