/**
 * 灵根品质与修仙资质评价体系
 *
 * 对标修仙小说公认谱系（《凡人修仙传》天灵根/真灵根/伪（杂）灵根 + 传统上中下品语汇）：
 * - 单系论「天灵根 / 真灵根」，极弱为废灵根；
 * - 双/三系按两两纯度论「上品 / 中品 / 下品」；
 * - 四系及以上不成天/上品者定名「X 系杂灵根」；
 * - 五系皆弱皆全（总值不足）即通称的「废灵根」。
 * 纯展示函数：称号（name）、品阶印章（seal）与沉浸式评语（desc），零结算影响。
 * 文案一律经 t 走 `insight.heavenGate.evaluation.*` 词条族；五行串接复用 `element.*` 词条，
 * 名称模板以 {elements} 插值承接按点数降序拼接的五行字符。
 */
import type { TFunction } from '../i18n'
import {
  type LinggenElementKey,
  type LinggenPoints,
  LINGGEN_ELEMENT_KEYS,
} from './linggenBoard.ts'

export interface LinggenEvaluation {
  /** 资质名称，例如「火真灵根」「金木上品双灵根」「废灵根」 */
  name: string
  /** 资质印章铭牌，例如「单灵根 · 天品」「五灵根 · 废品」 */
  seal: string
  /** 详细修仙风味评语 */
  desc: string
}

/** 天灵根单系门槛：灵气归一，杂质极少。 */
const HEAVEN_TIER = 90
/** 真灵根单系门槛：结丹有望的资格线。 */
const TRUE_TIER = 60
/** 单系真灵根之下的下品下限。 */
const SINGLE_LOWER_TIER = 35
/** 上品门槛：双/三系两两纯度。 */
const UPPER_TIER = 75
/** 中品门槛：双/三系两两纯度。 */
const MIDDLE_TIER = 55
/** 五系杂灵根总值下限（低于即废灵根）。 */
const FIVE_MIXED_TOTAL = 200
/** 五系杂灵根单系下限。 */
const FIVE_MIXED_MIN = 40

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
      name: t('insight.heavenGate.evaluation.name.unrolled'),
      seal: t('insight.heavenGate.evaluation.seal.unrolled'),
      desc: t('insight.heavenGate.evaluation.desc.unrolled'),
    }
  }

  const entries = LINGGEN_ELEMENT_KEYS
    .map(key => ({ key, value: Math.max(0, Math.min(100, Math.floor(roots[key] ?? 0))) }))
    .filter(entry => entry.value > 0)
    .sort((a, b) => b.value - a.value)

  // 五系全部归零时 entries 为空：必须先于 lowest 计算短路，否则 entries[-1] 未定义直接崩溃。
  if (entries.length === 0) {
    return {
      name: t('insight.heavenGate.evaluation.name.empty'),
      seal: t('insight.heavenGate.evaluation.seal.empty'),
      desc: t('insight.heavenGate.evaluation.desc.empty'),
    }
  }

  const total = entries.reduce((sum, e) => sum + e.value, 0)
  const lowest = entries[entries.length - 1].value

  // ── 单灵根：天灵根 / 真灵根 / 下品 / 废 ──────────────────────────────
  if (entries.length === 1) {
    const elements = joinElements(t, [entries[0].key])
    const val = entries[0].value
    if (val >= HEAVEN_TIER) {
      return {
        name: t('insight.heavenGate.evaluation.name.singleHeaven', { elements }),
        seal: t('insight.heavenGate.evaluation.seal.singleHeaven'),
        desc: val >= 100
          ? t('insight.heavenGate.evaluation.desc.singleHeavenFull')
          : t('insight.heavenGate.evaluation.desc.singleHeaven'),
      }
    }
    if (val >= TRUE_TIER) {
      return {
        name: t('insight.heavenGate.evaluation.name.singleTrue', { elements }),
        seal: t('insight.heavenGate.evaluation.seal.singleTrue'),
        desc: t('insight.heavenGate.evaluation.desc.singleTrue'),
      }
    }
    if (val >= SINGLE_LOWER_TIER) {
      return {
        name: t('insight.heavenGate.evaluation.name.singleLower', { elements }),
        seal: t('insight.heavenGate.evaluation.seal.singleLower'),
        desc: t('insight.heavenGate.evaluation.desc.singleLower'),
      }
    }
    return {
      name: t('insight.heavenGate.evaluation.name.singleWaste', { elements }),
      seal: t('insight.heavenGate.evaluation.seal.singleWaste'),
      desc: t('insight.heavenGate.evaluation.desc.singleWaste'),
    }
  }

  const names = joinElements(t, entries.map(e => e.key))

  // ── 双灵根：天品 / 上品 / 中品 / 下品 / 废 ────────────────────────────
  if (entries.length === 2) {
    const allUpper = entries.every(e => e.value >= HEAVEN_TIER)
    if (allUpper) {
      return {
        name: t('insight.heavenGate.evaluation.name.doubleHeaven', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.doubleHeaven'),
        desc: t('insight.heavenGate.evaluation.desc.doubleHeaven'),
      }
    }
    if (entries.every(e => e.value >= UPPER_TIER)) {
      return {
        name: t('insight.heavenGate.evaluation.name.doubleUpper', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.doubleUpper'),
        desc: t('insight.heavenGate.evaluation.desc.doubleUpper'),
      }
    }
    if (entries.every(e => e.value >= MIDDLE_TIER)) {
      return {
        name: t('insight.heavenGate.evaluation.name.doubleMiddle', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.doubleMiddle'),
        desc: t('insight.heavenGate.evaluation.desc.doubleMiddle'),
      }
    }
    if (total > 45 && entries[0].value >= MIDDLE_TIER) {
      return {
        name: t('insight.heavenGate.evaluation.name.doubleLower', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.doubleLower'),
        desc: t('insight.heavenGate.evaluation.desc.doubleLower'),
      }
    }
    return {
      name: t('insight.heavenGate.evaluation.name.doubleWaste', { elements: names }),
      seal: t('insight.heavenGate.evaluation.seal.doubleWaste'),
      desc: t('insight.heavenGate.evaluation.desc.doubleWaste'),
    }
  }

  // ── 三灵根：天品 / 上品 / 中品 / 下品 / 废 ────────────────────────────
  if (entries.length === 3) {
    if (entries.every(e => e.value >= HEAVEN_TIER)) {
      return {
        name: t('insight.heavenGate.evaluation.name.tripleHeaven', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.tripleHeaven'),
        desc: t('insight.heavenGate.evaluation.desc.tripleHeaven'),
      }
    }
    if (entries.every(e => e.value >= UPPER_TIER)) {
      return {
        name: t('insight.heavenGate.evaluation.name.tripleUpper', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.tripleUpper'),
        desc: t('insight.heavenGate.evaluation.desc.tripleUpper'),
      }
    }
    if (entries.every(e => e.value >= MIDDLE_TIER)) {
      return {
        name: t('insight.heavenGate.evaluation.name.tripleMiddle', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.tripleMiddle'),
        desc: t('insight.heavenGate.evaluation.desc.tripleMiddle'),
      }
    }
    if (total > 60 && entries[0].value >= MIDDLE_TIER) {
      return {
        name: t('insight.heavenGate.evaluation.name.tripleLower', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.tripleLower'),
        desc: t('insight.heavenGate.evaluation.desc.tripleLower'),
      }
    }
    return {
      name: t('insight.heavenGate.evaluation.name.tripleWaste', { elements: names }),
      seal: t('insight.heavenGate.evaluation.seal.tripleWaste'),
      desc: t('insight.heavenGate.evaluation.desc.tripleWaste'),
    }
  }

  // ── 四灵根：天品 / 上品 / 杂灵根 / 废 ─────────────────────────────────
  if (entries.length === 4) {
    if (entries.every(e => e.value >= HEAVEN_TIER)) {
      return {
        name: t('insight.heavenGate.evaluation.name.quadHeaven', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.quadHeaven'),
        desc: t('insight.heavenGate.evaluation.desc.quadHeaven'),
      }
    }
    if (entries.every(e => e.value >= UPPER_TIER)) {
      return {
        name: t('insight.heavenGate.evaluation.name.quadUpper', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.quadUpper'),
        desc: t('insight.heavenGate.evaluation.desc.quadUpper'),
      }
    }
    if (total > 120 && lowest >= 40) {
      return {
        name: t('insight.heavenGate.evaluation.name.quadMixed', { elements: names }),
        seal: t('insight.heavenGate.evaluation.seal.quadMixed'),
        desc: t('insight.heavenGate.evaluation.desc.quadMixed'),
      }
    }
    return {
      name: t('insight.heavenGate.evaluation.name.quadWaste', { elements: names }),
      seal: t('insight.heavenGate.evaluation.seal.quadWaste'),
      desc: t('insight.heavenGate.evaluation.desc.quadWaste'),
    }
  }

  // ── 五灵根：天品 / 上品 / 杂灵根 / 废灵根 ────────────────────────────
  if (entries.every(e => e.value >= HEAVEN_TIER)) {
    return {
      name: t('insight.heavenGate.evaluation.name.fiveHeaven'),
      seal: t('insight.heavenGate.evaluation.seal.fiveHeaven'),
      desc: t('insight.heavenGate.evaluation.desc.fiveHeaven'),
    }
  }
  if (entries.every(e => e.value >= UPPER_TIER)) {
    return {
      name: t('insight.heavenGate.evaluation.name.fiveUpper'),
      seal: t('insight.heavenGate.evaluation.seal.fiveUpper'),
      desc: t('insight.heavenGate.evaluation.desc.fiveUpper'),
    }
  }
  if (total >= FIVE_MIXED_TOTAL && lowest >= FIVE_MIXED_MIN) {
    return {
      name: t('insight.heavenGate.evaluation.name.fiveMixed'),
      seal: t('insight.heavenGate.evaluation.seal.fiveMixed'),
      desc: t('insight.heavenGate.evaluation.desc.fiveMixed'),
    }
  }
  return {
    name: t('insight.heavenGate.evaluation.name.fiveWaste'),
    seal: t('insight.heavenGate.evaluation.seal.fiveWaste'),
    desc: t('insight.heavenGate.evaluation.desc.fiveWaste'),
  }
}
