/**
 * 灵根品质与修仙资质评价体系
 *
 * 对标修仙小说公认谱系（《凡人修仙传》天灵根/真灵根/伪（杂）灵根 + 传统上中下品语汇）：
 * - 单系论「天灵根 / 真灵根」，极弱为废灵根；
 * - 双/三系按两两纯度论「上品 / 中品 / 下品」；
 * - 四系及以上不成天/上品者定名「X 系杂灵根」；
 * - 五系皆弱皆全（总值不足）即通称的「废灵根」。
 * 纯展示函数：称号（name）、品阶印章（seal）与沉浸式评语（desc），零结算影响。
 */
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

const ELEMENT_NAMES: Record<LinggenElementKey, string> = {
  metal: '金',
  wood: '木',
  water: '水',
  fire: '火',
  earth: '土',
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

function joinElements(keys: LinggenElementKey[]): string {
  return keys.map(k => ELEMENT_NAMES[k]).join('')
}

/**
 * 根据五行灵根分布评估修仙资质
 * @param roots 五行灵根点数
 * @param hasRolled 是否已经开天门抽取
 */
export function evaluateSpiritualRoots(
  roots: LinggenPoints | null | undefined,
  hasRolled: boolean,
): LinggenEvaluation {
  if (!hasRolled || !roots) {
    return {
      name: '灵根未定',
      seal: '凡胎未化',
      desc: '未叩天门，五气未凝，道骨凡胎尚待造化分明。',
    }
  }

  const entries = LINGGEN_ELEMENT_KEYS
    .map(key => ({ key, value: Math.max(0, Math.min(100, Math.floor(roots[key] ?? 0))) }))
    .filter(entry => entry.value > 0)
    .sort((a, b) => b.value - a.value)

  const total = entries.reduce((sum, e) => sum + e.value, 0)
  const lowest = entries[entries.length - 1].value

  if (entries.length === 0) {
    return {
      name: '五系皆空',
      seal: '灵根尽绝',
      desc: '体内五行之气断绝，灵气无所依附，无法聚气修行。',
    }
  }

  // ── 单灵根：天灵根 / 真灵根 / 下品 / 废 ──────────────────────────────
  if (entries.length === 1) {
    const name = ELEMENT_NAMES[entries[0].key]
    const val = entries[0].value
    if (val >= HEAVEN_TIER) {
      return {
        name: `${name}天灵根`,
        seal: '单灵根 · 天品',
        desc: val >= 100
          ? '一系圆满百数，灵气归一不染纤尘，正是上天的宠儿，结丹无碍，仙途坦荡。'
          : '仅存一系而杂质极少，修炼一日可抵旁人数日，宗门争抢的天灵根。',
      }
    }
    if (val >= TRUE_TIER) {
      return {
        name: `${name}真灵根`,
        seal: '单灵根 · 真品',
        desc: '一系纯粹充裕，虽不及天灵根那般逆天，却已有结丹之望，立足修仙界足矣。',
      }
    }
    if (val >= SINGLE_LOWER_TIER) {
      return {
        name: `${name}下品灵根`,
        seal: '单灵根 · 下品',
        desc: '路线倒也纯粹，只是根基偏枯薄弱，进境迟缓，须以丹药资源苦苦支撑。',
      }
    }
    return {
      name: `${name}废灵根`,
      seal: '单灵根 · 近废',
      desc: '一系孤悬却微弱如烛，灵气入体即散，纵有仙缘也难以为继。',
    }
  }

  const names = joinElements(entries.map(e => e.key))

  // ── 双灵根：天品 / 上品 / 中品 / 下品 / 废 ────────────────────────────
  if (entries.length === 2) {
    const allUpper = entries.every(e => e.value >= HEAVEN_TIER)
    if (allUpper) {
      return {
        name: `${names}双天灵根`,
        seal: '双灵根 · 天品',
        desc: '两系皆臻九十余而不驳，兼顾变化与纯度，万中无一的双系天灵根。',
      }
    }
    if (entries.every(e => e.value >= UPPER_TIER)) {
      return {
        name: `${names}上品双灵根`,
        seal: '双灵根 · 上品',
        desc: '两系俱都充裕扎实，双修相济，他日结丹有望，宗门中已可列入重点栽培之册。',
      }
    }
    if (entries.every(e => e.value >= MIDDLE_TIER)) {
      return {
        name: `${names}中品双灵根`,
        seal: '双灵根 · 中品',
        desc: '两系皆可堪一用，只是纯度平平，进境不快，胜在路子互补。',
      }
    }
    if (total > 45 && entries[0].value >= MIDDLE_TIER) {
      return {
        name: `${names}下品双灵根`,
        seal: '双灵根 · 下品',
        desc: '一系尚可一观，另一系已然孱弱，修行时灵气总在取舍间耗散。',
      }
    }
    return {
      name: `${names}废双灵根`,
      seal: '双灵根 · 近废',
      desc: '两系皆沾皮毛，俱不成气候，寻常杂役弟子也强他三分。',
    }
  }

  // ── 三灵根：天品 / 上品 / 中品 / 下品 / 废 ────────────────────────────
  if (entries.length === 3) {
    if (entries.every(e => e.value >= HEAVEN_TIER)) {
      return {
        name: `${names}三天灵根`,
        seal: '三灵根 · 天品',
        desc: '三系齐强皆过九十，驳杂之弊一洗而空，放在三灵根里已近传说。',
      }
    }
    if (entries.every(e => e.value >= UPPER_TIER)) {
      return {
        name: `${names}上品三灵根`,
        seal: '三灵根 · 上品',
        desc: '三系根基俱都扎实，兼容并包，虽有分纯之弊，底子仍胜过世间大多数修士。',
      }
    }
    if (entries.every(e => e.value >= MIDDLE_TIER)) {
      return {
        name: `${names}中品三灵根`,
        seal: '三灵根 · 中品',
        desc: '三系平平各可堪用，无甚短板也无甚长处，修行全看功法与资源。',
      }
    }
    if (total > 60 && entries[0].value >= MIDDLE_TIER) {
      return {
        name: `${names}下品三灵根`,
        seal: '三灵根 · 下品',
        desc: '三系散乱唯首系尚可，灵气三分其力，进境迟缓已是定数。',
      }
    }
    return {
      name: `${names}废三灵根`,
      seal: '三灵根 · 近废',
      desc: '三系俱弱如风中残烛，看似选择繁多，实则寸步难行。',
    }
  }

  // ── 四灵根：天品 / 上品 / 杂灵根 / 废 ─────────────────────────────────
  if (entries.length === 4) {
    if (entries.every(e => e.value >= HEAVEN_TIER)) {
      return {
        name: `${names}四象天灵根`,
        seal: '四灵根 · 天品',
        desc: '四象俱全而条条极高，驳杂之数反成周天之势，此等资质百年难遇。',
      }
    }
    if (entries.every(e => e.value >= UPPER_TIER)) {
      return {
        name: `${names}上品四灵根`,
        seal: '四灵根 · 上品',
        desc: '四系俱都不弱，纯度虽被摊薄，胜在法术路数极广，教人不敢小觑。',
      }
    }
    if (total > 120 && lowest >= 40) {
      return {
        name: `${names}杂灵根`,
        seal: '四灵根 · 杂品',
        desc: '四气混杂、灵气混沌，吸纳十份能留三分已是不易，进境迟缓，俗称的伪灵根便是此流。',
      }
    }
    return {
      name: `${names}废四灵根`,
      seal: '四灵根 · 近废',
      desc: '四系皆弱，条条如游丝，灵气未入玉府便已溃散大半。',
    }
  }

  // ── 五灵根：天品 / 上品 / 杂灵根 / 废灵根 ────────────────────────────
  if (entries.every(e => e.value >= HEAVEN_TIER)) {
    return {
      name: '五行天灵根',
      seal: '五灵根 · 天品',
      desc: '五行俱强皆过九十，齐鸣共振自成周天，万古罕逢，一旦有成当镇压一世。',
    }
  }
  if (entries.every(e => e.value >= UPPER_TIER)) {
    return {
      name: '五行上品灵根',
      seal: '五灵根 · 上品',
      desc: '五行俱全且俱都充裕，虽承分纯之弊，但五系齐修之势已非常人可及。',
    }
  }
  if (total >= FIVE_MIXED_TOTAL && lowest >= FIVE_MIXED_MIN) {
    return {
      name: '五行杂灵根',
      seal: '五灵根 · 杂品',
      desc: '五行混杂、灵气驳杂难凝，修炼事倍功半，这便是修士口中摇头叹息的伪灵根。',
    }
  }
  return {
    name: '废灵根',
    seal: '五灵根 · 废品',
    desc: '五行俱全却皆孱弱不堪，灵气入体即散于四肢百骸——仙路于常人而言已然断绝，唯有逆天改命，或可搏出一线机缘。',
  }
}
