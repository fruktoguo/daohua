import type { CombatAbilityDefinition, CombatTrigger } from './types'
import { getT, useI18nStore } from '../i18n'

/**
 * 功法加成属性条目结构（对齐装备复合加成展示风格）
 */
export interface AbilityBonusStat {
  key: string
  label: string
  tone: string
  value: string
}

/**
 * 技能效果浮动提示内容结构
 */
export interface EffectTooltipContent {
  /** 提示标题（如技能名称） */
  title: string
  badge?: string
  /** 技能详细描述（包含招式意境与效果叙述主句） */
  description: string
  /** 详细属性条目列表（每行一个描述） */
  lines: readonly string[]
  /** 复合加成属性条目（走装备 PillBonusLabel 同款展示风格） */
  bonuses?: readonly AbilityBonusStat[]
  /** 六维雷达图乘率字典（内功心法专用） */
  radarMultipliers?: Partial<Record<string, number>>
  /** 六维雷达图当前等级效率乘率（内功心法专用） */
  radarEfficiency?: number
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
 * 主描述 = 配表韵文 + 完整效果叙述句（参照英雄联盟技能说明：全部机制融在一段
 * 连贯文本里，数值以 [[ ]] 胶囊内嵌高亮，而非拆散成配置项式的标签行）；
 * 词条行 = 等级、冷却、吟唱、灵力消耗等资源元数据。
 * @param ability 技能定义
 * @param level 当前技能等级
 * @returns Tooltip 数据结构
 */
export function describeAbility(ability: CombatAbilityDefinition, level: number): EffectTooltipContent {
  const t = getT()
  const normalizedLevel = Math.max(1, level)
  const levelOffset = normalizedLevel - 1
  if (ability.discipline === 'internal') {
    const lines = [
      t('game.abilityDesc.level', { n: normalizedLevel }),
    ]

    const bonuses: AbilityBonusStat[] = []

    if (ability.cultivationExpBonusPoints) {
      const val = ability.cultivationExpBonusPoints
      bonuses.push({
        key: 'cultivationExpBonusPoints',
        label: '修为获取',
        tone: val >= 0 ? 'gold' : 'amber',
        value: `${val > 0 ? '+' : ''}${val}%`,
      })
    }

    if (ability.passive) {
      const statConfigs: Record<string, { label: string; tone: string }> = {
        physicalDamageBonusPoints: { label: '物理增伤', tone: 'crimson' },
        spellDamageBonusPoints: { label: '术法增伤', tone: 'indigo' },
        physicalDamageReductionPoints: { label: '物理减伤', tone: 'emerald' },
        spellDamageReductionPoints: { label: '术法减伤', tone: 'violet' },
        actionSpeedPoints: { label: '行动加速', tone: 'teal' },
        attackSpeedPoints: { label: '攻击攻速', tone: 'teal' },
        criticalDamageBonusPoints: { label: '暴击伤害', tone: 'gold' },
        threatBonusPoints: { label: '威胁增幅', tone: 'crimson' },
        healingBonusPoints: { label: '治疗增幅', tone: 'violet' },
        damageAbsorptionPoints: { label: '伤害减免', tone: 'emerald' },
      }
      for (const [stat, val] of Object.entries(ability.passive)) {
        if (typeof val === 'number' && val !== 0) {
          const cfg = statConfigs[stat] ?? { label: stat, tone: 'default' }
          bonuses.push({
            key: stat,
            label: cfg.label,
            tone: cfg.tone,
            value: `${val > 0 ? '+' : ''}${val}%`,
          })
        }
      }
    }

    // 六维属性随等级线性放大（eff = 1.0 + effPerLevel * levelOffset），数据载入 radarMultipliers 供雷达图组件渲染
    const effPerLevel = ability.rootExpEfficiencyPerLevel ?? 0.02
    const eff = Math.max(0, 1.0 + effPerLevel * levelOffset)

    return {
      title: ability.name,
      badge: '内功心法',
      description: ability.description ?? '',
      lines,
      bonuses,
      radarMultipliers: ability.rootExpMultipliers,
      radarEfficiency: eff,
    }
  }

  // 等级成长与服务端 CastSkill 同式：全部数值项每级 +2.0% 线性成长。
  const levelGrowth = 1 + levelOffset * 0.02
  const damageRatio = (ability.damageRatio ?? 0) * levelGrowth
  const flatDamage = (ability.flatDamage ?? 0) * levelGrowth
  const healingRatio = (ability.healingRatio ?? 0) * levelGrowth
  const flatHealing = (ability.flatHealing ?? 0) * levelGrowth
  // wire 上 targetType 已是 kebab 规范化词（enemy/all-enemies/self/all-allies/lowest-hp-ally）。
  const target = t(`game.abilityTarget.${ability.targetType ?? 'enemy'}`)
  const categoryLabel = t(`combat.encounter.damageCategory.${ability.category}`)
  const elementWord = ability.element && ability.element !== 'none'
    ? t(`game.abilityDesc.elementWord.${ability.element}`)
    : ''
  const damageKind = elementWord
    ? t('game.abilityDesc.damageKindElement', { element: elementWord, category: categoryLabel })
    : t('game.abilityDesc.damageKindPlain', { category: categoryLabel })

  // 伤害/治疗构成表达式：嵌入叙述句，[[ ]] 由 ValueText 渲染为整组胶囊，
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

  // 效果叙述主句：按效果类型一次性写清目标、数值与伤害类别。
  const hitCount = ability.hitCount ?? 0
  const healing = ability.effectType === 'heal'
  // 支援类数值同样吃 levelGrowth：固定威胁与 Buff 点数项均按每级 +2% 放大。
  const threatGain = (ability.threatGain ?? 0) * levelGrowth
  const threatPoints = (ability.threatPoints ?? 0) * levelGrowth
  const buff = ability.buff
  // 战内 Buff 效果子句：增伤/免伤/威胁增幅/每秒威胁逐条并列描述，全零回退通用增益文案。
  const buffClause = () => {
    const duration = buff?.durationSeconds ?? 0
    const bonusPoints = (buff?.damageBonusPoints ?? 0) * levelGrowth
    const absorbPoints = (buff?.damageAbsorptionPoints ?? 0) * levelGrowth
    const buffThreatPoints = (buff?.threatPoints ?? 0) * levelGrowth
    const threatPerSecond = (buff?.threatPerSecondMaxHpPoints ?? 0) * levelGrowth
    const effects: string[] = []
    if (bonusPoints !== 0) effects.push(t('game.abilityDesc.effectBuffDamage', { target, n: abilityPercent(bonusPoints / 100) }))
    if (absorbPoints !== 0) effects.push(t('game.abilityDesc.effectBuffAbsorb', { target, n: abilityPercent(absorbPoints / (100 + absorbPoints)) }))
    if (buffThreatPoints !== 0) effects.push(t('game.abilityDesc.threatRatioClause', { n: abilityPercent(buffThreatPoints / 100) }))
    if (threatPerSecond !== 0) effects.push(t('game.abilityDesc.effectBuffThreatPerSecond', { n: abilityPercent(threatPerSecond / 100) }))
    let effect = effects.length > 0 ? effects.join('，') : t('game.abilityDesc.effectBuff', { target })
    if (duration > 0) {
      effect = `${effect}，${t('game.abilityDesc.buffDuration', { n: formatNumber(duration) })}`
    }
    return effect
  }
  const clauses: string[] = []
  if (healing) {
    clauses.push(t('game.abilityDesc.effectHeal', { target, expr: healExpr }))
  } else if (damageExpr) {
    clauses.push(hitCount > 1
      ? t('game.abilityDesc.effectMultiHit', { target, count: hitCount, expr: damageExpr, damageKind })
      : t('game.abilityDesc.effectDamage', { target, expr: damageExpr, damageKind }))
  } else if (threatGain > 0 || threatPoints > 0) {
    let clause = threatGain > 0
      ? t('game.abilityDesc.effectThreatGain', { n: formatNumber(threatGain) })
      : ''
    if (threatPoints > 0) {
      const ratio = t('game.abilityDesc.threatRatioClause', { n: abilityPercent(threatPoints / 100) })
      clause = clause ? `${clause}，${ratio}` : ratio
    }
    // 嘲讽类技能即时威胁与限时 Buff 并存：Buff 子句并连，不被威胁主句吞掉。
    if (buff) clause = clause ? `${clause}，${buffClause()}` : buffClause()
    clauses.push(clause || t('game.abilityDesc.effectThreat'))
  } else if (buff) {
    clauses.push(buffClause())
  } else {
    clauses.push(t('game.abilityDesc.effectBuff', { target }))
  }
  // 机制附加子句：连击中止条件与击杀连锁与主句连成同一段，不另起配置行。
  if (hitCount > 1) {
    clauses.push(t('game.abilityDesc.multiHitEnd'))
  }
  const chainCount = ability.chainOnKillCount ?? 0
  if (chainCount > 0) {
    clauses.push(t('game.abilityDesc.chainOnKill', { n: formatNumber(chainCount) }))
  }
  const effectSentence = `${clauses.join('；')}。`
  const description = ability.description ? `${ability.description}\n${effectSentence}` : effectSentence

  const lines = [
    t('game.abilityDesc.level', { n: normalizedLevel }),
    // 配表字段可缺省，服务端按 0 处理，前端同口径。
    t('game.abilityDesc.cooldownTime', { n: ((ability.cooldownMilliseconds ?? 0) / 1000).toFixed(1) }),
    t('game.abilityDesc.castTime', { n: ((ability.castMilliseconds ?? 0) / 1000).toFixed(1) }),
    t('game.abilityDesc.manaCost', { n: formatNumber(ability.manaCost ?? 0) }),
  ]
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
 * 战斗属性与收益预览的整数显示约定：原始值先向零取整，再按默认精度走大数缩写。
 * 注意不能直接传 maxDecimals=0——那会把取整应用在万进制换算后的值上，
 * 导致 1.8万 被截成 1万（万以下尾数全丢）。
 */
export function formatInteger(value: number | bigint | string): string {
  const integral = typeof value === 'bigint' ? value : Math.trunc(Number(value))
  return formatUnitNumber(integral)
}

/** formatProgressText 的展示选项 */
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

// 触发条件枚举 -> 展示文案统一走 i18n；组合语序由模板键承载（中英语序不同）。
/**
 * 战斗触发条件转展示文案（如「自身生命值 <= 30%」）
 * @param trigger 触发条件对象
 */
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
  /** 小数点后最多位数（默认 2）；整数部分达到 3 位时小数自动不再显示 */
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

/** 中文万进制分段格式化的精度与取整选项 */
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
 * 1. 智能精度——整数部分达到 3 位后不再显示小数（如 1447.89 → 1447、106万 → 106万），
 *    不足 3 位时最多保留 maxDecimals 位（默认 2，如 1.06万、9.99万）；
 * 2. 数字最多六个字符 (maxDigits=6) 兜底；
 * 3. 满万、亿、兆、京等阶梯自动进位改后缀，默认向下取整 (floor)
 * 例如：
 * 99999 -> "9.99万" (floor)
 * 99999999 -> "9999.99万" -> 整数位超 3 位收敛为 "9999万" (floor)
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
 * 在数字位数上限内格式化：智能精度——整数部分达到 3 位后不再显示小数（如 1447.89 → 1447），
 * 不足 3 位时保留最多 maxDecimals 位小数（如 1.06 → 1.06、9.999 → 9.99）；
 * 若数字位数仍超出 maxDigits 则逐位降低小数精度兜底。
 */
function formatWithinDigitLimit(
  val: number,
  maxDecimals: number,
  maxDigits: number,
  roundMode: 'round' | 'floor' | 'ceil',
  padDecimals: boolean,
): string {
  let decimals = val >= 100 ? 0 : maxDecimals
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
