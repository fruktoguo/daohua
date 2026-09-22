import type { CombatAbilityDefinition, CombatTrigger } from './types'
import { getT, useI18nStore } from '../i18n'

/**
 * 技能效果浮动提示内容结构
 */
export interface EffectTooltipContent {
  /** 提示标题（如技能名称） */
  title: string
  /** 徽章标签（如 流派 · 伤害类型） */
  badge?: string
  /** 简短效果概述 */
  description: string
  /** 详细属性条目列表（每行一个描述） */
  lines: readonly string[]
}

/**
 * 格式化技能系数为整数百分比字符串（如 "+15%"）
 */
function abilityPercent(ratio: number): string {
  const percent = Math.round(ratio * 100)
  return `${percent > 0 ? '+' : ''}${percent}%`
}

/**
 * 伤害构成百分比（不带正负号，如 "60%"、"61.8%"）
 */
function abilityRatioPercent(ratio: number): string {
  return `${+((ratio * 100).toFixed(1))}%`
}

/**
 * 根据功法技能定义与当前等级，生成用于 Tooltip 展示的结构化说明数据：
 * 主描述 = 配表招式描述 + 效果句（目标/五行/伤害类别）；
 * 词条行 = 等级、冷却、吟唱、灵力与伤害/治疗构成胶囊（标明所吃攻击属性）。
 * @param ability 技能定义
 * @param level 当前技能等级
 * @returns Tooltip 数据结构
 */
export function describeAbility(ability: CombatAbilityDefinition, level: number): EffectTooltipContent {
  const t = getT()
  const normalizedLevel = Math.max(1, level)
  const levelOffset = normalizedLevel - 1
  // 等级成长与服务端 CombatSkillEngine.Complete 同式：倍率项每级 +0.6%，固定值项每级 +1.0%。
  const ratioBonus = 1 + levelOffset * 0.006
  const flatBonus = 1 + levelOffset * 0.01
  const damageRatio = (ability.damageRatio ?? 0) * ratioBonus
  const flatDamage = (ability.flatDamage ?? 0) * flatBonus
  const healingRatio = (ability.healingRatio ?? 0) * ratioBonus
  const flatHealing = (ability.flatHealing ?? 0) * flatBonus
  // wire 上 targetType 已是 kebab 规范化词（enemy/all-enemies/self/all-allies/lowest-hp-ally）。
  const target = t(`game.abilityTarget.${ability.targetType ?? 'enemy'}`)
  const categoryLabel = t(`combat.encounter.damageCategory.${ability.category}`)
  const elementWord = ability.element && ability.element !== 'none'
    ? t(`game.abilityDesc.elementWord.${ability.element}`)
    : ''
  const damageKind = elementWord
    ? t('game.abilityDesc.damageKindElement', { element: elementWord, category: categoryLabel })
    : t('game.abilityDesc.damageKindPlain', { category: categoryLabel })

  // 伤害/治疗构成表达式：嵌入主描述句，[[ ]] 由 ValueText 渲染为整组胶囊，
  // 标明所吃攻击属性（术法吃术法攻击、物理吃物理攻击），与服务端结算一致。
  const attackStat = t(`game.abilityDesc.attackStat.${ability.category === 'spell' ? 'spell' : 'physical'}`)
  const composition = (ratio: number, flat: number) => {
    const parts: string[] = []
    if (ratio !== 0) parts.push(`[[${abilityRatioPercent(ratio)}${attackStat}]]`)
    if (flat !== 0) parts.push(`[[+${formatNumber(flat)}]]`)
    return parts.length > 0 ? `${parts.join(' ')} ` : ''
  }
  const damageExpr = composition(damageRatio, flatDamage)
  const healExpr = composition(healingRatio, flatHealing)

  // 主描述：配表招式描述（如「从掌心发出一道雷光」）+ 按效果类型生成的效果句。
  const healing = ability.effectType === 'heal'
  const clause = healing
    ? t('game.abilityDesc.effectHeal', { target, expr: healExpr })
    : damageExpr
      ? t('game.abilityDesc.effectDamage', { target, expr: damageExpr, damageKind })
      : (ability.threatGain ?? 0) > 0 || (ability.threatRatio ?? 0) > 0
        ? t('game.abilityDesc.effectThreat')
        : t('game.abilityDesc.effectBuff', { target })
  const description = ability.description ? `${ability.description}，${clause}。` : `${clause}。`

  const lines = [
    t('game.abilityDesc.level', { n: normalizedLevel }),
    // 配表字段可缺省，服务端按 0 处理，前端同口径。
    t('game.abilityDesc.cooldownTime', { n: ((ability.cooldownMilliseconds ?? 0) / 1000).toFixed(1) }),
    t('game.abilityDesc.castTime', { n: ((ability.castMilliseconds ?? 0) / 1000).toFixed(1) }),
    t('game.abilityDesc.manaCost', { n: formatNumber(ability.manaCost ?? 0) }),
  ]
  // 支援类效果数值（嘲讽威胁、破阵子减伤屏障等）。
  if ((ability.threatGain ?? 0) !== 0) {
    lines.push(t('game.abilityDesc.threatGain', { n: formatNumber(ability.threatGain ?? 0) }))
  }
  if ((ability.threatRatio ?? 0) !== 0) {
    lines.push(t('game.abilityDesc.threatRatio', { n: abilityPercent(ability.threatRatio ?? 0) }))
  }
  if ((ability.buff?.damageAbsorptionRatio ?? 0) !== 0) {
    lines.push(t('game.abilityDesc.buffAbsorb', { n: abilityPercent(ability.buff?.damageAbsorptionRatio ?? 0) }))
  }
  return {
    title: ability.name,
    badge: formatAbilityCategory(ability.category, ability.element),
    description,
    lines,
  }
}

/**
 * 伤害大类与五行标签格式化：统一走 i18n 键（combat.encounter.*），
 * 未知值回退原始 wire 值，避免维护本地中文字典。
 */
export function formatAbilityCategory(category?: string, element?: string): string {
  const t = getT()
  let cat = t('combat.encounter.damageCategory.physical')
  if (category) {
    const catKey = `combat.encounter.damageCategory.${category}`
    const translated = t(catKey)
    cat = translated !== catKey ? translated : category
  }
  let elem = ''
  if (element && element !== 'none') {
    const elemKey = `combat.encounter.element.${element}`
    const translated = t(elemKey)
    elem = ` · ${translated !== elemKey ? translated : element}`
  }
  return `${cat}${elem}`
}

/** Intl.NumberFormat 按语言环境缓存，避免高频渲染路径重复构造格式化器 */
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

/**
 * 战斗属性与收益预览的整数显示约定：统一下取整，保证展示层不出现小数。
 */
export function formatInteger(value: number | bigint | string): string {
  return formatUnitNumber(value, { maxDecimals: 0 })
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
    return options?.completedText ?? getT()('common.progressComplete')
  }
  const curStr = formatNumber(Math.max(0, current))
  const maxStr = formatNumber(max)
  const decimals = options?.percentDecimals ?? 0
  const pct = Math.min(100, Math.max(0, (current / max) * 100.0)).toFixed(decimals)
  return options?.showPercent === false ? `${curStr}/${maxStr}` : `${curStr}/${maxStr} (${pct}%)`
}

/**
 * 战斗触发条件转展示文案（如「自身生命值 <= 30%」）
 * @param trigger 触发条件对象
 */
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

/**
 * 中文万进制大数阶梯单位数组 (万、亿、兆、京、垓、秭、穰、沟、涧、正、载、极)
 */
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

/**
 * 单位数值格式化选项
 */
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

/**
 * 解析后的数值结构拆解
 */
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
  if (typeof value === 'number') {
    if (!Number.isFinite(value) || value === 0) {
      return { sign: '', intStr: '0', decStr: '', isZero: true }
    }
    const isNeg = value < 0
    const absVal = isNeg ? -value : value
    if (Number.isInteger(absVal) && absVal < 1e15) {
      return {
        sign: isNeg ? '-' : '',
        intStr: String(absVal),
        decStr: '',
        isZero: false,
      }
    }
  }

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

/**
 * 将整数字符串按万进制拆分并加上中文单位（如 12345678 -> "1234万 5678"）
 */
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

/**
 * 按照指定小数位与取整模式四舍五入/向下截断/向上取整
 */
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
  // 快路径：无格式化选项的非负小整数直接输出，跳过选项解析与阶梯计算（渲染热点优化）。
  if (options === undefined && typeof value === 'number' && Number.isInteger(value) && value >= 0 && value < 10000) {
    return String(value)
  }

  const { maxDecimals = 2, maxDigits = 6, roundMode = 'floor', padDecimals = false } = options ?? {}

  const num = typeof value === 'number' ? value : Number(value)
  if (isNaN(num)) return '0'
  if (!isFinite(num)) return num > 0 ? '∞' : '-∞'
  if (num === 0) return '0'

  const isNeg = num < 0
  const absVal = Math.abs(num)

  // 小于 10000，直接展示数值
  if (absVal < 10000) {
    const formatted = formatWithinDigitLimit(absVal, maxDecimals, maxDigits, roundMode, padDecimals)
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
  let formatted = formatWithinDigitLimit(scaled, maxDecimals, maxDigits, roundMode, padDecimals)

  // 检查进位是否达到 10000（例如 9999.996 万进位为 1 亿）
  if (Number(formatted) >= 10000 && unitIndex < CHINESE_SECTION_UNITS.length - 1) {
    unitIndex++
    scale *= 10000
    scaled = absVal / scale
    formatted = formatWithinDigitLimit(scaled, maxDecimals, maxDigits, roundMode, padDecimals)
  }

  // 降精度后整数位数仍超出上限：继续进位到更大的万进制单位；已到最大单位时尽力而为。
  while (countNumericDigits(formatted) > maxDigits && unitIndex < CHINESE_SECTION_UNITS.length - 1) {
    unitIndex++
    scale *= 10000
    scaled = absVal / scale
    formatted = formatWithinDigitLimit(scaled, 0, maxDigits, roundMode, padDecimals)
  }

  const unitSymbol = CHINESE_SECTION_UNITS[unitIndex]
  return `${isNeg ? '-' : ''}${formatted}${unitSymbol}`
}

/** 统计格式化文本中的数字字符数（不含符号、小数点与中文单位后缀） */
function countNumericDigits(text: string): number {
  let count = 0
  for (const char of text) {
    if (char >= '0' && char <= '9') count += 1
  }
  return count
}

/**
 * 在数字位数上限内格式化：先按 maxDecimals 取整，若数字位数超出 maxDigits 则逐位降低小数精度。
 * maxDigits 不小于默认输出位数时输出与直接取整完全一致，保证既有调用点行为不变。
 */
function formatWithinDigitLimit(
  val: number,
  maxDecimals: number,
  maxDigits: number,
  roundMode: 'round' | 'floor' | 'ceil',
  padDecimals: boolean,
): string {
  let decimals = maxDecimals
  let formatted = roundToMaxDecimals(val, decimals, roundMode, padDecimals)
  while (countNumericDigits(formatted) > maxDigits && decimals > 0) {
    decimals -= 1
    formatted = roundToMaxDecimals(val, decimals, roundMode, padDecimals)
  }
  return formatted
}


/**
 * 数值拆解信息对象（包含原始值、正负符号、紧凑数值、单位后缀、完整四位分段字符串等）
 */
export interface UnitNumberInfo {
  /** 原始传入数值 */
  raw: number | bigint | string
  /** 符号（"-" 或 ""） */
  sign: string
  /** 紧凑主数值（去除单位后缀） */
  compactNumber: string
  /** 单位后缀（如 "万", "亿"） */
  unit: string
  /** 紧凑展示文本（如 "-12.5万"） */
  compactText: string
  /** 中文万进制完整分段文本（如 "1234亿 5678万"） */
  splitText: string
  /** 标准千分位展示文本（如 "123,456"） */
  standardText: string
}

/**
 * 获取数值的完整拆解信息（用于 UnitNumber 组件的悬浮提示与阶梯渲染）
 * @param value 原始数值
 * @param options 格式化选项
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

/**
 * 将剩余秒数格式化为本地化的时/分/秒短文本（如「1小时5分」「3分20秒」「42秒」）。
 * 复用 app.settings.time* 词条；用于斗法冷却与暂存区到期倒计时等非渲染热点路径。
 */
export function formatRemainingSeconds(seconds: number): string {
  const t = getT()
  const total = Math.max(0, Math.floor(seconds))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  if (h > 0) return t('app.settings.timeHoursMinutes', { h, m })
  if (m > 0) return t('app.settings.timeMinutesSeconds', { m, s })
  return t('app.settings.timeSeconds', { s })
}
