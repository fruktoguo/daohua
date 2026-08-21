import type { CombatTrigger } from './types'
import { getT, useI18nStore } from '../i18n'

const numberFormatters = new Map<string, Intl.NumberFormat>()

/**
 * 精确千分位数字格式化（按当前语言环境缓存）
 */
export function formatExactNumber(value: number): string {
  const locale = useI18nStore.getState().locale
  let formatter = numberFormatters.get(locale)
  if (!formatter) {
    formatter = new Intl.NumberFormat(locale)
    numberFormatters.set(locale, formatter)
  }
  return formatter.format(value)
}

/**
 * 通用数字格式化：
 * 默认使用基础单位大数换算（万、亿、兆...），数字最多6位，小数点后最多2位四舍五入
 */
export function formatNumber(
  value: number | bigint | string,
  options?: FormatUnitOptions
): string {
  return formatUnitNumber(value, options)
}

// 触发条件枚举 -> 展示文案统一走 i18n；组合语序由模板键承载（中英语序不同）。
export function describeCombatTrigger(trigger: CombatTrigger): string {
  const t = getT()
  const dependency = t(`game.trigger.dependency.${trigger.dependency}`)
  const condition = t(`game.trigger.condition.${trigger.condition}`)
  const comparator = t(`game.trigger.comparator.${trigger.comparator}`)
  if (trigger.comparator === 'is-active' || trigger.comparator === 'is-inactive') {
    return t('game.trigger.template.status', { dependency, condition, comparator })
  }
  const isPercentage = trigger.condition.includes('percentage')
  return t('game.trigger.template.value', {
    dependency,
    condition,
    comparator,
    value: formatNumber(trigger.value),
    unit: isPercentage ? '%' : '',
  })
}

export const CHINESE_SECTION_UNITS = [
  '',
  '万',
  '亿',
  '兆',
  '京',
  '垓',
  '秭',
  '穰',
  '沟',
  '涧',
  '正',
  '载',
  '极',
] as const

export interface FormatUnitOptions {
  /** 小数点后最多位数，默认 2 */
  maxDecimals?: number
  /** 数字最大字符数（包含整数和小数，不含符号与单位），默认 6 */
  maxDigits?: number
  /** 取整模式：'round'(四舍五入, 默认) | 'floor'(向下截断) | 'ceil'(向上取整) */
  roundMode?: 'round' | 'floor' | 'ceil'
  /** 是否强制保留小数位数（默认 false，即去除末尾多余 0） */
  padDecimals?: boolean
}

export interface ParsedNumericParts {
  sign: string
  intStr: string
  decStr: string
  isZero: boolean
}

/**
 * 将任意数值/大数解析为符号、整数部分、小数部分
 */
export function parseNumericParts(value: number | bigint | string): ParsedNumericParts {
  if (typeof value === 'bigint') {
    const isNeg = value < 0n
    const absBig = isNeg ? -value : value
    return {
      sign: isNeg ? '-' : '',
      intStr: absBig.toString(),
      decStr: '',
      isZero: value === 0n,
    }
  }

  const rawStr = String(value).trim()
  if (!rawStr || rawStr === 'NaN' || rawStr === 'undefined' || rawStr === 'null') {
    return { sign: '', intStr: '0', decStr: '', isZero: true }
  }

  let normalized = rawStr
  if (rawStr.includes('e') || rawStr.includes('E')) {
    const num = Number(rawStr)
    if (!Number.isFinite(num)) {
      return { sign: '', intStr: '0', decStr: '', isZero: true }
    }
    normalized = num.toLocaleString('fullwide', { useGrouping: false })
  }

  let sign = ''
  let rest = normalized
  if (rest.startsWith('-')) {
    sign = '-'
    rest = rest.slice(1)
  } else if (rest.startsWith('+')) {
    rest = rest.slice(1)
  }

  const [rawInt = '0', rawDec = ''] = rest.split('.')
  const intStr = rawInt.replace(/^0+(?=\d)/, '') || '0'
  const decStr = rawDec.replace(/0+$/, '')
  const isZero = intStr === '0' && (!decStr || decStr === '')

  return {
    sign: isZero ? '' : sign,
    intStr,
    decStr,
    isZero,
  }
}

/**
 * 完整数字中文四位分段格式化
 * 例如：
 * 123477775211 -> "1234亿 7777万 5211"
 * 2000000000 -> "20亿"
 * 200005211 -> "2亿 5211"
 * 5211 -> "5211"
 * 0 -> "0"
 * -123477775211 -> "-1234亿 7777万 5211"
 */
export function formatChineseSplitNumber(value: number | bigint | string): string {
  const { sign, intStr, decStr, isZero } = parseNumericParts(value)
  if (isZero) return '0'

  // 从低到高每 4 位切分
  const sections: { val: number; unit: string; chunk: string }[] = []
  let remaining = intStr
  let unitIdx = 0

  while (remaining.length > 0) {
    const start = Math.max(0, remaining.length - 4)
    const chunk = remaining.slice(start)
    remaining = remaining.slice(0, start)

    const numVal = parseInt(chunk, 10)
    const unit = CHINESE_SECTION_UNITS[unitIdx] ?? ''
    sections.push({ val: numVal, unit, chunk })
    unitIdx++
  }

  // 过滤出非零段，从高到低排列
  const nonZeroSections: string[] = []
  for (let i = sections.length - 1; i >= 0; i--) {
    const sec = sections[i]
    if (sec.val > 0) {
      nonZeroSections.push(`${sec.val}${sec.unit}`)
    }
  }

  let result = nonZeroSections.length === 0 ? '0' : nonZeroSections.join(' ')

  if (decStr) {
    result += `.${decStr}`
  }

  return `${sign}${result}`
}

function roundToMaxDecimals(
  val: number,
  maxDecimals: number,
  mode: 'round' | 'floor' | 'ceil',
  padDecimals: boolean
): string {
  const factor = Math.pow(10, maxDecimals)
  let rounded: number
  if (mode === 'floor') {
    rounded = Math.floor(val * factor + 1e-9) / factor
  } else if (mode === 'ceil') {
    rounded = Math.ceil(val * factor - 1e-9) / factor
  } else {
    rounded = Math.round(val * factor) / factor
  }

  if (padDecimals) {
    return rounded.toFixed(maxDecimals)
  }

  const str = rounded.toFixed(maxDecimals)
  return str.includes('.') ? str.replace(/\.?0+$/, '') : str
}

/**
 * 自动改后缀单位格式化：
 * 1. 小数点后最多两位 (maxDecimals=2)
 * 2. 数字最多六个字符 (maxDigits=6，整数1~4位 + 小数0~2位)
 * 3. 满万、亿、兆、京等阶梯自动进位改后缀，四舍五入
 * 例如：
 * 99999 -> "10万" (round) 或 "9.99万" (floor)
 * 99999999 -> "1亿" (round) 或 "9999.99万" (floor)
 * 2000000000 -> "20亿"
 * 200000000 -> "2亿"
 */
export function formatUnitNumber(
  value: number | bigint | string,
  options?: FormatUnitOptions
): string {
  const { maxDecimals = 2, roundMode = 'round', padDecimals = false } = options ?? {}

  const num = typeof value === 'number' ? value : Number(value)
  if (isNaN(num)) return '0'
  if (!isFinite(num)) return num > 0 ? '∞' : '-∞'
  if (num === 0) return '0'

  const isNeg = num < 0
  const absVal = Math.abs(num)

  // 小于 10000，直接展示数值
  if (absVal < 10000) {
    const formatted = roundToMaxDecimals(absVal, maxDecimals, roundMode, padDecimals)
    return `${isNeg ? '-' : ''}${formatted}`
  }

  // 匹配万进制阶梯
  let unitIndex = 0
  let scale = 1
  while (unitIndex < CHINESE_SECTION_UNITS.length - 1 && absVal >= scale * 10000) {
    scale *= 10000
    unitIndex++
  }

  let scaled = absVal / scale
  let formatted = roundToMaxDecimals(scaled, maxDecimals, roundMode, padDecimals)

  // 检查四舍五入是否进位为 10000（例如 9999.996 万 -> 10000 万，进位为 1 亿）
  if (Number(formatted) >= 10000 && unitIndex < CHINESE_SECTION_UNITS.length - 1) {
    unitIndex++
    scale *= 10000
    scaled = absVal / scale
    formatted = roundToMaxDecimals(scaled, maxDecimals, roundMode, padDecimals)
  }

  const unitSymbol = CHINESE_SECTION_UNITS[unitIndex]
  return `${isNeg ? '-' : ''}${formatted}${unitSymbol}`
}

export interface UnitNumberInfo {
  raw: number | bigint | string
  sign: string
  compactNumber: string
  unit: string
  compactText: string
  splitText: string
  standardText: string
}

/**
 * 获取数值的完整拆解信息
 */
export function getUnitNumberInfo(
  value: number | bigint | string,
  options?: FormatUnitOptions
): UnitNumberInfo {
  const compactText = formatUnitNumber(value, options)
  const splitText = formatChineseSplitNumber(value)
  const num = typeof value === 'number' ? value : Number(value)
  const standardText = !isNaN(num) ? formatExactNumber(num) : String(value)

  // 提取单位和紧凑数值
  let unit = ''
  let compactNumber = compactText
  for (let i = CHINESE_SECTION_UNITS.length - 1; i >= 1; i--) {
    const sym = CHINESE_SECTION_UNITS[i]
    if (sym && compactText.endsWith(sym)) {
      unit = sym
      compactNumber = compactText.slice(0, -sym.length)
      break
    }
  }

  const sign = compactText.startsWith('-') ? '-' : ''

  return {
    raw: value,
    sign,
    compactNumber,
    unit,
    compactText,
    splitText,
    standardText,
  }
}
