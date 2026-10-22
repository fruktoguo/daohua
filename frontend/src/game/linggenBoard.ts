/**
 * 五行灵根阵盘几何常量。
 *
 * 阵盘采用 400×400 SVG 坐标系，丹田居于 (200,200)，单轴满值半径 150。
 * 五轴自 -90° 起顺时针均分 72°，与金→木→水→火→土的传统顺序一致。
 */

/** 五行灵根系别键。 */
export type LinggenElementKey = 'metal' | 'wood' | 'water' | 'fire' | 'earth'

/** 丹田中心坐标。 */
export const BOARD_CENTER = { x: 200, y: 200 } as const

/** 单轴满值（100 点）对应的半径。 */
export const BOARD_MAX_RADIUS = 150

/** 灵根 0 点对应的内环半径：位于外环半径的大概 1/8 处（约 20px），丹田按钮位于其内。 */
export const BOARD_MIN_RADIUS = 20

/** 单系灵根点数上限。 */
export const LINGGEN_AXIS_CAP = 100

/** 地灵门槛：达到即为地灵根。 */
export const LINGGEN_EARTH_TIER = 60

/** 天灵门槛：达到即为天灵根。 */
export const LINGGEN_HEAVEN_TIER = 90

/** 五行轴元数据：顺序、角度与单位向量。 */
export interface LinggenAxisMeta {
  key: LinggenElementKey
  /** 五行汉字。 */
  label: string
  angleDeg: number
  unitCos: number
  unitSin: number
  /** 手柄内部纹样的旋转角（使灵株始终朝外生长）。 */
  handleRotation: number
}

/** 五轴定义：金属性自正上方起顺时针排布。 */
export const LINGGEN_AXES: readonly LinggenAxisMeta[] = [
  { key: 'metal', label: '金', angleDeg: -90, unitCos: 0, unitSin: -1, handleRotation: 0 },
  { key: 'wood', label: '木', angleDeg: -18, unitCos: 0.9510565, unitSin: -0.309017, handleRotation: 72 },
  { key: 'water', label: '水', angleDeg: 54, unitCos: 0.5877853, unitSin: 0.809017, handleRotation: 144 },
  { key: 'fire', label: '火', angleDeg: 126, unitCos: -0.5877853, unitSin: 0.809017, handleRotation: 216 },
  { key: 'earth', label: '土', angleDeg: 198, unitCos: -0.9510565, unitSin: -0.309017, handleRotation: 288 },
]

/** 五行键顺序（与轴顺序一致）。 */
export const LINGGEN_ELEMENT_KEYS: readonly LinggenElementKey[] = LINGGEN_AXES.map(axis => axis.key)

/** 五行灵根点数形状。 */
export interface LinggenPoints {
  metal: number
  wood: number
  water: number
  fire: number
  earth: number
}

/** 空灵根。 */
export const EMPTY_LINGGEN: LinggenPoints = { metal: 0, wood: 0, water: 0, fire: 0, earth: 0 }

/** 把点数夹取到 [0, 100] 的整数。 */
export function clampPoints(value: number): number {
  if (!Number.isFinite(value)) return 0
  return Math.max(0, Math.min(LINGGEN_AXIS_CAP, Math.round(value)))
}

/** 顶点坐标：按当前点数额定半径换算。 */
export function vertexPoint(axis: LinggenAxisMeta, value: number): { x: number; y: number } {
  const ratio = clampPoints(value) / LINGGEN_AXIS_CAP
  const radius = BOARD_MIN_RADIUS + (BOARD_MAX_RADIUS - BOARD_MIN_RADIUS) * ratio
  return {
    x: Number((BOARD_CENTER.x + radius * axis.unitCos).toFixed(1)),
    y: Number((BOARD_CENTER.y + radius * axis.unitSin).toFixed(1)),
  }
}

/** 五轴顶点围成的多边形 points 串。 */
export function polygonPoints(values: LinggenPoints, insetPx = 0): string {
  return LINGGEN_AXES.map(axis => {
    const point = vertexPoint(axis, values[axis.key])
    if (insetPx === 0) return `${point.x},${point.y}`
    const dx = point.x - BOARD_CENTER.x
    const dy = point.y - BOARD_CENTER.y
    const length = Math.hypot(dx, dy)
    if (length <= insetPx) return `${BOARD_CENTER.x},${BOARD_CENTER.y}`
    const scale = (length - insetPx) / length
    return `${(BOARD_CENTER.x + dx * scale).toFixed(1)},${(BOARD_CENTER.y + dy * scale).toFixed(1)}`
  }).join(' ')
}

/** 单系品阶判定：0 起凡品，≥60 地灵，≥90 天灵。 */
export function axisTier(value: number): 'none' | 'earth' | 'heaven' {
  if (value >= LINGGEN_HEAVEN_TIER) return 'heaven'
  if (value >= LINGGEN_EARTH_TIER) return 'earth'
  return 'none'
}
