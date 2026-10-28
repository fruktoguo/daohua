/**
 * 灵根品质与修仙资质评价体系
 *
 * 分级口径与 `daojie-yusheng` 的 `describeSpiritualRoots` 完全一致：
 * 按「有效灵根条数」分支，同一分支内**先判纯度档（天品/上品）、再按离散度与总值梯度分档**，
 * 不设单系绝对门槛——一条微末残系不会把高总值灵根压成废灵根。
 * 纯展示函数：称号（name）、品阶印章（seal）与沉浸式评语（desc），零结算影响。
 * 文案一律经 t 走 `insight.heavenGate.evaluation.*` 词条族（键全部字面量书写，供 i18n 门禁静态校验）；
 * 五行串接复用 `insight.heavenGate.element.*` 词条，名称模板以 {elements} 插值承接按点数降序拼接的五行字符。
 */
import type { TFunction } from '../i18n'
import {
  type LinggenElementKey,
  type LinggenPoints,
  LINGGEN_ELEMENT_KEYS,
} from './linggenBoard.ts'

export interface LinggenEvaluation {
  /** 资质名称，例如「火真灵根」「金木上品双灵根」「极品五行灵根」 */
  name: string
  /** 资质印章铭牌，例如「单灵根 · 天品」「五灵根 · 极品」 */
  seal: string
  /** 详细修仙风味评语 */
  desc: string
}

/** 单系满值：先天道胎档门槛。 */
const INNATE_DAO_TIER = 100
/** 单系极品档门槛。 */
const SUPREME_TIER = 95
/** 天品档门槛（单/双/三/四/五系通用）。 */
const HEAVEN_TIER = 90
/** 单系真品与双系上品门槛。 */
const TRUE_TIER = 80
/** 单系偏枯门槛。 */
const SINGLE_WEAK_TIER = 35
/** 三系上品门槛。 */
const TRIPLE_UPPER_TIER = 75
/** 四系上品门槛。 */
const QUAD_UPPER_TIER = 70
/** 双系均衡：两极离散上限与次强系下限。 */
const DUAL_BALANCED_SPREAD = 8
const DUAL_BALANCED_FLOOR = 50
/** 三系均衡：两极离散上限与末位系下限。 */
const TRIPLE_BALANCED_SPREAD = 10
const TRIPLE_BALANCED_FLOOR = 45
/** 五系均衡：两极离散上限与末位系下限。 */
const FIVE_BALANCED_SPREAD = 10
const FIVE_BALANCED_FLOOR = 55
/** 双系总值分档：≤ 该值近废，≤ 下一档杂驳，之上常规。 */
const DUAL_WASTE_TOTAL = 40
const DUAL_MIXED_TOTAL = 80
/** 三系总值分档。 */
const TRIPLE_WASTE_TOTAL = 60
const TRIPLE_MIXED_TOTAL = 120
/** 四系总值分档。 */
const QUAD_WASTE_TOTAL = 90
const QUAD_MIXED_TOTAL = 160
/** 五系总值梯度分档。 */
const FIVE_WASTE_TOTAL = 45
const FIVE_MIXED_TOTAL = 120
const FIVE_LOW_TOTAL = 220
const FIVE_HIGH_TOTAL = 320

/** 档位判定上下文：已按点数降序排列的有效灵根与派生统计量。 */
interface TierContext {
  /** 按点数降序排列的有效灵根（点数 > 0）。 */
  entries: { key: LinggenElementKey; value: number }[]
  /** 各系点数总和。 */
  total: number
  /** 最高系与最低系的离散度。 */
  spread: number
  /** 五系字符拼接串（名称 {elements} 插值）。 */
  names: string
}

/** 单条档位定义：判定条件与称号/印章/评语词条键。 */
interface TierRule {
  /** 判定条件；按数组顺序首个命中者生效，末位为恒真兜底。 */
  match: (ctx: TierContext) => boolean
  nameKey: string
  sealKey: string
  descKey: string
}

/** 各条数族的档位判定表：顺序即优先级，纯度高者先判，其后按总值梯度。 */
const RULES: Record<number, TierRule[]> = {
  // ── 单灵根：先天道胎 / 极品 / 天品 / 真品 / 偏枯 / 近废 ─────────────────
  1: [
    { match: c => c.entries[0].value >= INNATE_DAO_TIER,
      nameKey: 'insight.heavenGate.evaluation.single.innateDao.name',
      sealKey: 'insight.heavenGate.evaluation.single.innateDao.seal',
      descKey: 'insight.heavenGate.evaluation.single.innateDao.desc' },
    { match: c => c.entries[0].value >= SUPREME_TIER,
      nameKey: 'insight.heavenGate.evaluation.single.supreme.name',
      sealKey: 'insight.heavenGate.evaluation.single.supreme.seal',
      descKey: 'insight.heavenGate.evaluation.single.supreme.desc' },
    { match: c => c.entries[0].value >= HEAVEN_TIER,
      nameKey: 'insight.heavenGate.evaluation.single.heaven.name',
      sealKey: 'insight.heavenGate.evaluation.single.heaven.seal',
      descKey: 'insight.heavenGate.evaluation.single.heaven.desc' },
    { match: c => c.entries[0].value >= TRUE_TIER,
      nameKey: 'insight.heavenGate.evaluation.single.true.name',
      sealKey: 'insight.heavenGate.evaluation.single.true.seal',
      descKey: 'insight.heavenGate.evaluation.single.true.desc' },
    { match: c => c.entries[0].value >= SINGLE_WEAK_TIER,
      nameKey: 'insight.heavenGate.evaluation.single.weak.name',
      sealKey: 'insight.heavenGate.evaluation.single.weak.seal',
      descKey: 'insight.heavenGate.evaluation.single.weak.desc' },
    { match: () => true,
      nameKey: 'insight.heavenGate.evaluation.single.waste.name',
      sealKey: 'insight.heavenGate.evaluation.single.waste.seal',
      descKey: 'insight.heavenGate.evaluation.single.waste.desc' },
  ],
  // ── 双灵根：天品 / 上品 / 均衡 / 近废 / 杂驳 / 常规 ─────────────────────
  2: [
    { match: c => c.entries.every(e => e.value >= HEAVEN_TIER),
      nameKey: 'insight.heavenGate.evaluation.dual.heaven.name',
      sealKey: 'insight.heavenGate.evaluation.dual.heaven.seal',
      descKey: 'insight.heavenGate.evaluation.dual.heaven.desc' },
    { match: c => c.entries.every(e => e.value >= TRUE_TIER),
      nameKey: 'insight.heavenGate.evaluation.dual.true.name',
      sealKey: 'insight.heavenGate.evaluation.dual.true.seal',
      descKey: 'insight.heavenGate.evaluation.dual.true.desc' },
    { match: c => c.spread <= DUAL_BALANCED_SPREAD && c.entries[1].value >= DUAL_BALANCED_FLOOR,
      nameKey: 'insight.heavenGate.evaluation.dual.balanced.name',
      sealKey: 'insight.heavenGate.evaluation.dual.balanced.seal',
      descKey: 'insight.heavenGate.evaluation.dual.balanced.desc' },
    { match: c => c.total <= DUAL_WASTE_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.dual.waste.name',
      sealKey: 'insight.heavenGate.evaluation.dual.waste.seal',
      descKey: 'insight.heavenGate.evaluation.dual.waste.desc' },
    { match: c => c.total <= DUAL_MIXED_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.dual.mixed.name',
      sealKey: 'insight.heavenGate.evaluation.dual.mixed.seal',
      descKey: 'insight.heavenGate.evaluation.dual.mixed.desc' },
    { match: () => true,
      nameKey: 'insight.heavenGate.evaluation.dual.normal.name',
      sealKey: 'insight.heavenGate.evaluation.dual.normal.seal',
      descKey: 'insight.heavenGate.evaluation.dual.normal.desc' },
  ],
  // ── 三灵根：天品 / 上品 / 均衡 / 近废 / 杂驳 / 常规 ─────────────────────
  3: [
    { match: c => c.entries.every(e => e.value >= HEAVEN_TIER),
      nameKey: 'insight.heavenGate.evaluation.triple.heaven.name',
      sealKey: 'insight.heavenGate.evaluation.triple.heaven.seal',
      descKey: 'insight.heavenGate.evaluation.triple.heaven.desc' },
    { match: c => c.entries.every(e => e.value >= TRIPLE_UPPER_TIER),
      nameKey: 'insight.heavenGate.evaluation.triple.true.name',
      sealKey: 'insight.heavenGate.evaluation.triple.true.seal',
      descKey: 'insight.heavenGate.evaluation.triple.true.desc' },
    { match: c => c.spread <= TRIPLE_BALANCED_SPREAD && c.entries[2].value >= TRIPLE_BALANCED_FLOOR,
      nameKey: 'insight.heavenGate.evaluation.triple.balanced.name',
      sealKey: 'insight.heavenGate.evaluation.triple.balanced.seal',
      descKey: 'insight.heavenGate.evaluation.triple.balanced.desc' },
    { match: c => c.total <= TRIPLE_WASTE_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.triple.waste.name',
      sealKey: 'insight.heavenGate.evaluation.triple.waste.seal',
      descKey: 'insight.heavenGate.evaluation.triple.waste.desc' },
    { match: c => c.total <= TRIPLE_MIXED_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.triple.mixed.name',
      sealKey: 'insight.heavenGate.evaluation.triple.mixed.seal',
      descKey: 'insight.heavenGate.evaluation.triple.mixed.desc' },
    { match: () => true,
      nameKey: 'insight.heavenGate.evaluation.triple.normal.name',
      sealKey: 'insight.heavenGate.evaluation.triple.normal.seal',
      descKey: 'insight.heavenGate.evaluation.triple.normal.desc' },
  ],
  // ── 四灵根：天品 / 上品 / 近废 / 杂驳 / 常规 ───────────────────────────
  4: [
    { match: c => c.entries.every(e => e.value >= HEAVEN_TIER),
      nameKey: 'insight.heavenGate.evaluation.quad.heaven.name',
      sealKey: 'insight.heavenGate.evaluation.quad.heaven.seal',
      descKey: 'insight.heavenGate.evaluation.quad.heaven.desc' },
    { match: c => c.entries.every(e => e.value >= QUAD_UPPER_TIER),
      nameKey: 'insight.heavenGate.evaluation.quad.true.name',
      sealKey: 'insight.heavenGate.evaluation.quad.true.seal',
      descKey: 'insight.heavenGate.evaluation.quad.true.desc' },
    { match: c => c.total <= QUAD_WASTE_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.quad.waste.name',
      sealKey: 'insight.heavenGate.evaluation.quad.waste.seal',
      descKey: 'insight.heavenGate.evaluation.quad.waste.desc' },
    { match: c => c.total <= QUAD_MIXED_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.quad.mixed.name',
      sealKey: 'insight.heavenGate.evaluation.quad.mixed.seal',
      descKey: 'insight.heavenGate.evaluation.quad.mixed.desc' },
    { match: () => true,
      nameKey: 'insight.heavenGate.evaluation.quad.normal.name',
      sealKey: 'insight.heavenGate.evaluation.quad.normal.seal',
      descKey: 'insight.heavenGate.evaluation.quad.normal.desc' },
  ],
  // ── 五灵根：神品 / 天品 / 极上 / 均衡 / 近废 / 杂驳 / 普通 / 上品 / 极品 ──
  5: [
    { match: c => c.entries.every(e => e.value >= INNATE_DAO_TIER),
      nameKey: 'insight.heavenGate.evaluation.five.god.name',
      sealKey: 'insight.heavenGate.evaluation.five.god.seal',
      descKey: 'insight.heavenGate.evaluation.five.god.desc' },
    { match: c => c.entries.every(e => e.value >= HEAVEN_TIER),
      nameKey: 'insight.heavenGate.evaluation.five.heaven.name',
      sealKey: 'insight.heavenGate.evaluation.five.heaven.seal',
      descKey: 'insight.heavenGate.evaluation.five.heaven.desc' },
    { match: c => c.entries.every(e => e.value >= TRUE_TIER),
      nameKey: 'insight.heavenGate.evaluation.five.true.name',
      sealKey: 'insight.heavenGate.evaluation.five.true.seal',
      descKey: 'insight.heavenGate.evaluation.five.true.desc' },
    { match: c => c.spread <= FIVE_BALANCED_SPREAD && c.entries[4].value >= FIVE_BALANCED_FLOOR,
      nameKey: 'insight.heavenGate.evaluation.five.balanced.name',
      sealKey: 'insight.heavenGate.evaluation.five.balanced.seal',
      descKey: 'insight.heavenGate.evaluation.five.balanced.desc' },
    { match: c => c.total <= FIVE_WASTE_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.five.waste.name',
      sealKey: 'insight.heavenGate.evaluation.five.waste.seal',
      descKey: 'insight.heavenGate.evaluation.five.waste.desc' },
    { match: c => c.total <= FIVE_MIXED_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.five.mixed.name',
      sealKey: 'insight.heavenGate.evaluation.five.mixed.seal',
      descKey: 'insight.heavenGate.evaluation.five.mixed.desc' },
    { match: c => c.total <= FIVE_LOW_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.five.low.name',
      sealKey: 'insight.heavenGate.evaluation.five.low.seal',
      descKey: 'insight.heavenGate.evaluation.five.low.desc' },
    { match: c => c.total <= FIVE_HIGH_TOTAL,
      nameKey: 'insight.heavenGate.evaluation.five.high.name',
      sealKey: 'insight.heavenGate.evaluation.five.high.seal',
      descKey: 'insight.heavenGate.evaluation.five.high.desc' },
    { match: () => true,
      nameKey: 'insight.heavenGate.evaluation.five.supreme.name',
      sealKey: 'insight.heavenGate.evaluation.five.supreme.seal',
      descKey: 'insight.heavenGate.evaluation.five.supreme.desc' },
  ],
}

/** 按传入顺序串接五行字面（经 element.* 词条翻译，用于名称 {elements} 插值）。 */
function joinElements(t: TFunction, keys: LinggenElementKey[]): string {
  return keys.map(k => t(`insight.heavenGate.element.${k}`)).join('')
}

/**
 * 根据五行灵根分布评估修仙资质
 * @param roots 五行灵根点数
 * @param hasRolled 是否已经开天门抽取
 * @param t 翻译函数（评定文案全部词条化，由调用方注入）
 */
export function evaluateSpiritualRoots(
  roots: LinggenPoints | null | undefined,
  hasRolled: boolean,
  t: TFunction,
): LinggenEvaluation {
  if (!hasRolled || !roots) {
    return {
      name: t('insight.heavenGate.evaluation.unrolled.name'),
      seal: t('insight.heavenGate.evaluation.unrolled.seal'),
      desc: t('insight.heavenGate.evaluation.unrolled.desc'),
    }
  }

  const entries = LINGGEN_ELEMENT_KEYS
    .map(key => ({ key, value: Math.max(0, Math.min(100, Math.floor(roots[key] ?? 0))) }))
    .filter(entry => entry.value > 0)
    .sort((a, b) => b.value - a.value)

  // 五系全部归零时 entries 为空：必须先于任何取值计算短路，否则 entries[0] 未定义直接崩溃。
  if (entries.length === 0) {
    return {
      name: t('insight.heavenGate.evaluation.empty.name'),
      seal: t('insight.heavenGate.evaluation.empty.seal'),
      desc: t('insight.heavenGate.evaluation.empty.desc'),
    }
  }

  const context: TierContext = {
    entries,
    total: entries.reduce((sum, e) => sum + e.value, 0),
    spread: entries[0].value - entries[entries.length - 1].value,
    names: joinElements(t, entries.map(e => e.key)),
  }

  const rule = RULES[entries.length].find(candidate => candidate.match(context))
  // 判定表每条数族末尾都有恒真兜底档，此处不成立即表本身被改坏，直接抛出保留证据。
  if (!rule) throw new Error(`灵根评价判定表缺少 ${entries.length} 系兜底档位`)

  return {
    name: t(rule.nameKey, { elements: context.names }),
    seal: t(rule.sealKey),
    desc: t(rule.descKey),
  }
}
