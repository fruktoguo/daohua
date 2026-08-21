import type { CombatAbilityDefinition, CombatTrigger } from './types'
import { getT, useI18nStore } from '../i18n'

export interface EffectTooltipContent {
  title: string
  badge?: string
  description: string
  lines: readonly string[]
}

function abilityPercent(ratio: number): string {
  const percent = Math.round(ratio * 1000) / 10
  return `${percent > 0 ? '+' : ''}${percent}%`
}

export function describeAbility(ability: CombatAbilityDefinition, level: number): EffectTooltipContent {
  const normalizedLevel = Math.max(1, level)
  const levelOffset = normalizedLevel - 1
  const damageRatio = ability.damageRatio + (ability.damageRatioLevelBonus ?? 0) * levelOffset
  const flatDamage = ability.flatDamage + (ability.flatDamageLevelBonus ?? 0) * levelOffset
  const healingRatio = (ability.healingRatio ?? 0) + (ability.healingRatioLevelBonus ?? 0) * levelOffset
  const flatHealing = (ability.flatHealing ?? 0) + (ability.flatHealingLevelBonus ?? 0) * levelOffset
  const target = ability.targetType === 'all-enemies'
    ? '敌方全体'
    : ability.targetType === 'self'
      ? '自身'
      : ability.targetType === 'all-allies'
        ? '己方全体'
        : ability.targetType === 'lowest-hp-ally'
          ? '生命最低的友方'
          : '当前敌人'
  const lines = [
    `等级 ${normalizedLevel}`,
    `目标 ${target}`,
    `灵力消耗 ${formatNumber(ability.manaCost)}`,
    `施法 ${(ability.castMilliseconds / 1000).toFixed(1)} 秒`,
    `冷却 ${(ability.cooldownMilliseconds / 1000).toFixed(1)} 秒`,
  ]
  if (damageRatio !== 0 || flatDamage !== 0) lines.push(`伤害 ${abilityPercent(damageRatio)} + ${formatNumber(flatDamage)}`)
  if (healingRatio !== 0 || flatHealing !== 0) lines.push(`治疗 ${abilityPercent(healingRatio)} + ${formatNumber(flatHealing)}`)
  if ((ability.penetrationRatio ?? 0) !== 0) lines.push(`抗性穿透 ${abilityPercent(ability.penetrationRatio)}`)
  if ((ability.pierceChance ?? 0) !== 0) lines.push(`穿透概率 ${abilityPercent(ability.pierceChance ?? 0)}`)
  if ((ability.armorDamageRatio ?? 0) !== 0) lines.push(`护甲增伤 ${abilityPercent(ability.armorDamageRatio ?? 0)}`)
  if ((ability.hpDrainRatio ?? 0) !== 0) lines.push(`生命汲取 ${abilityPercent(ability.hpDrainRatio ?? 0)}`)
  if ((ability.damageOverTimeRatio ?? 0) !== 0) {
    lines.push(`持续伤害 ${abilityPercent(ability.damageOverTimeRatio ?? 0)}，持续 ${((ability.damageOverTimeMilliseconds ?? 0) / 1000).toFixed(1)} 秒`)
  }
  if ((ability.stunChance ?? 0) !== 0) lines.push(`定身 ${abilityPercent(ability.stunChance ?? 0)}，持续 ${((ability.stunMilliseconds ?? 0) / 1000).toFixed(1)} 秒`)
  if ((ability.blindChance ?? 0) !== 0) lines.push(`致盲 ${abilityPercent(ability.blindChance ?? 0)}，持续 ${((ability.blindMilliseconds ?? 0) / 1000).toFixed(1)} 秒`)
  if ((ability.silenceChance ?? 0) !== 0) lines.push(`沉默 ${abilityPercent(ability.silenceChance ?? 0)}，持续 ${((ability.silenceMilliseconds ?? 0) / 1000).toFixed(1)} 秒`)
  for (const trigger of ability.defaultTriggers ?? []) lines.push(`默认条件 ${describeCombatTrigger(trigger)}`)
  return {
    title: ability.name,
    badge: `${ability.style} · ${ability.damageType}`,
    description: ability.effectType === 'heal' ? `为${target}恢复生命。` : `对${target}结算战斗效果。`,
    lines,
  }
}

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

export interface FormatProgressOptions {
  /** 是否展示百分比后缀，默认 true */
  showPercent?: boolean
  /** 百分比小数位，默认 0 */
  percentDecimals?: number
  /** 圆满/完成时的替代文本（当 max <= 0 时默认展示） */
  completedText?: string
}

/**
 * 通用进度描述文本生成器
 * 示例：
 * - 5000, 10000 -> "5000/1万 (50%)"
 * - 660000, 1000000 -> "66万/100万 (66%)"
 * - 0, 0 -> "已圆满 (100%)"
 */
export function formatProgressText(
  current: number,
  max: number,
  options?: FormatProgressOptions
): string {
  if (max <= 0) {
    return options?.completedText ?? '已圆满 (100%)'
  }
  const curStr = formatNumber(Math.max(0, current))
  const maxStr = formatNumber(max)
  const decimals = options?.percentDecimals ?? 0
  const pct = Math.min(100, Math.max(0, (current / max) * 100.0)).toFixed(decimals)
  return options?.showPercent === false ? `${curStr}/${maxStr}` : `${curStr}/${maxStr} (${pct}%)`
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

export interface FormatChineseSplitOptions {
  /** 小数点后最多位数，默认 2 */
  maxDecimals?: number
  /** 取整模式：'round'(四舍五入, 默认) | 'floor'(向下截断) | 'ceil'(向上取整) */
  roundMode?: 'round' | 'floor' | 'ceil'
  /** 是否强制保留小数位数（默认 false，即去除末尾多余 0） */
  padDecimals?: boolean
}

function splitIntegerIntoChineseSections(intStr: string): string {
  const cleanInt = intStr.replace(/^0+(?=\d)/, '') || '0'
  if (cleanInt === '0') return '0'

  const sections: { val: number; unit: string }[] = []
  let remaining = cleanInt
  let unitIdx = 0

  while (remaining.length > 0) {
    const start = Math.max(0, remaining.length - 4)
    const chunk = remaining.slice(start)
    remaining = remaining.slice(0, start)

    const numVal = parseInt(chunk, 10)
    const unit = CHINESE_SECTION_UNITS[unitIdx] ?? ''
    sections.push({ val: numVal, unit })
    unitIdx++
  }

  const nonZeroSections: string[] = []
  for (let i = sections.length - 1; i >= 0; i--) {
    const sec = sections[i]
    if (sec.val > 0) {
      nonZeroSections.push(`${sec.val}${sec.unit}`)
    }
  }

  return nonZeroSections.length === 0 ? '0' : nonZeroSections.join(' ')
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
 * 完整数字中文四位分段格式化（通用的完整数值悬浮展示，最多展示小数点后两位）
 * 例如：
 * 123477775211 -> "1234亿 7777万 5211"
 * 2000000000 -> "20亿"
 * 200005211 -> "2亿 5211"
 * 5211 -> "5211"
 * 10.5 -> "10.5"
 * 12345.678 -> "1万 2345.68"
 * 0 -> "0"
 * -123477775211 -> "-1234亿 7777万 5211"
 */
export function formatChineseSplitNumber(
  value: number | bigint | string,
  options?: FormatChineseSplitOptions | number
): string {
  const opts: FormatChineseSplitOptions =
    typeof options === 'number' ? { maxDecimals: options } : options ?? {}
  const { maxDecimals = 2, roundMode = 'round', padDecimals = false } = opts

  if (typeof value === 'bigint') {
    const isNeg = value < 0n
    const absBig = isNeg ? -value : value
    if (absBig === 0n) return '0'
    return `${isNeg ? '-' : ''}${splitIntegerIntoChineseSections(absBig.toString())}`
  }

  const rawStr = String(value).trim()
  if (!rawStr || rawStr === 'NaN' || rawStr === 'undefined' || rawStr === 'null') {
    return '0'
  }

  const num = typeof value === 'number' ? value : Number(rawStr)
  if (isNaN(num)) return '0'
  if (!isFinite(num)) return num > 0 ? '∞' : '-∞'
  if (num === 0) return '0'

  const isNeg = num < 0
  const absVal = Math.abs(num)

  const roundedStr = roundToMaxDecimals(absVal, maxDecimals, roundMode, padDecimals)
  const [rawInt = '0', rawDec = ''] = roundedStr.split('.')
  const sectionStr = splitIntegerIntoChineseSections(rawInt)
  const decStr = padDecimals ? rawDec : rawDec.replace(/0+$/, '')

  const decPart = decStr ? `.${decStr}` : ''
  return `${isNeg ? '-' : ''}${sectionStr}${decPart}`
}

/**
 * 自动改后缀单位格式化：
 * 1. 小数点后最多两位 (maxDecimals=2)
 * 2. 数字最多六个字符 (maxDigits=6，整数1~4位 + 小数0~2位)
 * 3. 满万、亿、兆、京等阶梯自动进位改后缀，默认向下取整 (floor)
 * 例如：
 * 99999 -> "9.99万" (floor)
 * 99999999 -> "9999.99万" (floor)
 * 2000000000 -> "20亿"
 * 200000000 -> "2亿"
 */
export function formatUnitNumber(
  value: number | bigint | string,
  options?: FormatUnitOptions
): string {
  const { maxDecimals = 2, roundMode = 'floor', padDecimals = false } = options ?? {}

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

  // 检查进位是否达到 10000（例如 9999.996 万进位为 1 亿）
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
