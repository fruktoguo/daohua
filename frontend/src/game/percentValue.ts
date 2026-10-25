/**
 * 统一百分比数值类型 (PercentValue)：增伤/减伤/速率等百分比修饰的点数表达与投影换算。
 *
 * 领域口径（总纲 §7.7.4）：所有百分比修饰以 `double`「点数」存储（100 点 = 100%），
 * 加成方向走分子线性、惩罚方向走分母渐近收敛；百分比只是收益域的投影显示。
 * 独立成模块是为了让纯展示换算（如内视灵根与斗法面板）能在不引入 i18n 运行时的情况下复用。
 */

/**
 * 百分比曲线计算模式：
 * - 'linear': 线性缩放（如灵气收益、伤害加成等）
 * - 'non-linear': 速率倒数缩减（用于攻速、冷却缩减、减伤等避免无限递增的属性）
 */
export type PercentCurveMode = 'linear' | 'non-linear'

/**
 * 统一百分比数值类型。
 * 内部存储与计算均使用 double 类型的固定点数 (points，基准 100 点 = 100%)。
 * 基础战斗数值使用固定值类型 (double)，特殊战斗数值使用 PercentValue。
 */
export class PercentValue {
  readonly points: number
  readonly mode: PercentCurveMode

  constructor(points: number, mode: PercentCurveMode = 'linear') {
    this.points = points
    this.mode = mode
  }

  /**
   * 创建线性百分比数值
   * @param points 百分比点数（100 点 = 100%）
   */
  public static linear(points: number): PercentValue {
    return new PercentValue(points, 'linear')
  }

  /**
   * 创建非线性（倒数递减）百分比数值
   * @param points 百分比点数
   */
  public static nonLinear(points: number): PercentValue {
    return new PercentValue(points, 'non-linear')
  }

  /**
   * 参与战斗公式乘法计算的实际倍率 (Multiplier)
   */
  get multiplier(): number {
    if (this.mode === 'linear') {
      return this.points >= 0
        ? 1.0 + this.points / 100.0
        : 100.0 / (100.0 + Math.abs(this.points))
    }
    // 非线性档（速率倒数缩减）：正点数按 100/(100+点数) 收敛，攻速/冷却缩减等属性不随点数无限递增
    return this.points >= 0
      ? 100.0 / (100.0 + this.points)
      : 1.0 + Math.abs(this.points) / 100.0
  }

  /**
   * 面板或 UI 展示的实际百分比值 (DisplayPercentage)
   */
  get displayPercentage(): number {
    if (this.mode === 'linear') {
      return this.points >= 0
        ? this.points
        : -(Math.abs(this.points) / (100.0 + Math.abs(this.points))) * 100.0
    }
    // 非线性档（softcap）：正点数展示值按 点数/(100+点数) 收敛，与 multiplier 的倒数缩减口径一致
    return this.points >= 0
      ? (this.points / (100.0 + this.points)) * 100.0
      : -Math.abs(this.points)
  }

  /**
   * 格式化为百分比文本字符串，如 "+100%"、"-50%"、"50%"，向下取整，默认不出现小数
   */
  format(decimals = 0): string {
    const val = this.displayPercentage
    const factor = Math.pow(10, decimals)
    const floored = Math.floor(val * factor + 1e-9) / factor
    const prefix = this.mode === 'linear' && floored > 0 ? '+' : ''
    return `${prefix}${floored.toFixed(decimals)}%`
  }
}
