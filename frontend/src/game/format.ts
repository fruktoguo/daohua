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
 * 伤害构成百分比（不带正负号，如 "60%"、"61.8%"）
 */
function abilityRatioPercent(ratio: number): string {
  return `${+((ratio * 100).toFixed(1))}%`
}

const TARGET_KEY_ALIASES: Record<string, string> = {
  allEnemies: 'all-enemies',
  allAllies: 'all-allies',
  lowestHitpointsAlly: 'lowest-hp-ally',
}

/**
 * 功法施法目标枚举（支持 camelCase 与 wire kebab-case）解析为本地化显示名称，
 * 供 describeAbility 主描述与 EffectTooltip 变招副卡共享。
 */
export function resolveAbilityTargetLabel(rawTargetType = 'enemy', maxTargets?: number): string {
  const t = getT()
  const normalizedTargetKey = TARGET_KEY_ALIASES[rawTargetType] ?? rawTargetType
  if (normalizedTargetKey === 'all-enemies' && maxTargets && maxTargets > 0) {
    return t('game.abilityTarget.upToEnemies', { n: formatNumber(maxTargets) })
  }
  return t(`game.abilityTarget.${normalizedTargetKey}`)
}

/**
 * 功法被动属性展示标签单一来源：describeAbility 与 EffectTooltip 的被动词条行共用，
 * 新增属性时只需在此补一个 case 与对应词条，避免两处文案漂移。
 */
export function abilityPassiveStatLabel(stat: string): string {
  const t = getT()
  switch (stat) {
    case 'physicalDamageBonusPoints': return t('game.abilityStat.physicalDamageBonusPoints')
    case 'spellDamageBonusPoints': return t('game.abilityStat.spellDamageBonusPoints')
    case 'physicalDamageReductionPoints': return t('game.abilityStat.physicalDamageReductionPoints')
    case 'spellDamageReductionPoints': return t('game.abilityStat.spellDamageReductionPoints')
    case 'actionSpeedPoints': return t('game.abilityStat.actionSpeedPoints')
    case 'attackSpeedPoints': return t('game.abilityStat.attackSpeedPoints')
    case 'criticalDamageBonusPoints': return t('game.abilityStat.criticalDamageBonusPoints')
    case 'threatBonusPoints': return t('game.abilityStat.threatBonusPoints')
    case 'healingBonusPoints': return t('game.abilityStat.healingBonusPoints')
    case 'damageAbsorptionPoints': return t('game.abilityStat.damageAbsorptionPoints')
    case 'cultivationExpBonusPoints': return t('game.abilityStat.cultivationExpBonusPoints')
    case 'combatExpBonusPoints': return t('game.abilityStat.combatExpBonusPoints')
    default: return stat
  }
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
        label: abilityPassiveStatLabel('cultivationExpBonusPoints'),
        tone: val >= 0 ? 'gold' : 'amber',
        value: `${val > 0 ? '+' : ''}${val}%`,
      })
    }

    if (ability.passive) {
      const passiveEffPerLevel = ability.passiveEfficiencyPerLevel ?? 0.05
      const passiveScale = Math.max(0, 1.0 + passiveEffPerLevel * levelOffset)
      const statTones: Record<string, string> = {
        physicalDamageBonusPoints: 'crimson',
        spellDamageBonusPoints: 'indigo',
        physicalDamageReductionPoints: 'emerald',
        spellDamageReductionPoints: 'violet',
        actionSpeedPoints: 'teal',
        attackSpeedPoints: 'teal',
        criticalDamageBonusPoints: 'gold',
        threatBonusPoints: 'crimson',
        healingBonusPoints: 'violet',
        damageAbsorptionPoints: 'emerald',
        cultivationExpBonusPoints: 'gold',
        combatExpBonusPoints: 'amber',
      }
      for (const [stat, baseVal] of Object.entries(ability.passive)) {
        if (typeof baseVal === 'number' && baseVal !== 0) {
          const scaledVal = Math.round(baseVal * passiveScale * 100) / 100
          bonuses.push({
            key: stat,
            label: abilityPassiveStatLabel(stat),
            tone: statTones[stat] ?? 'default',
            value: `${scaledVal > 0 ? '+' : ''}${scaledVal}%`,
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
  const maxHpDamageRatio = (ability.maxHpDamageRatio ?? 0) * levelGrowth
  const shieldDamageRatio = (ability.shieldDamageRatio ?? 0) * levelGrowth
  const flatDamage = (ability.flatDamage ?? 0) * levelGrowth
  const healingRatio = (ability.healingRatio ?? 0) * levelGrowth
  const flatHealing = (ability.flatHealing ?? 0) * levelGrowth
  const shieldMaxHpRatio = (ability.shieldMaxHpRatio ?? 0) * levelGrowth
  // wire 上 targetType 已是 kebab 规范化词（enemy/all-enemies/self/all-allies/lowest-hp-ally），同时兼容原始配表 camelCase。
  const target = resolveAbilityTargetLabel(ability.targetType ?? 'enemy', ability.maxTargets)
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
  const composition = (ratio: number, flat: number, extraMaxHpRatio = 0, extraShieldRatio = 0) => {
    const parts: string[] = []
    if (ratio !== 0) parts.push(`[[${abilityRatioPercent(ratio)}${attackStat}]]`)
    if (extraMaxHpRatio !== 0) {
      const prefix = parts.length > 0 ? '+' : ''
      parts.push(`[[${prefix}${abilityRatioPercent(extraMaxHpRatio)}${t('game.abilityDesc.maxHpStat')}]]`)
    }
    if (extraShieldRatio !== 0) {
      const prefix = parts.length > 0 ? '+' : ''
      parts.push(`[[${prefix}${abilityRatioPercent(extraShieldRatio)}${t('game.abilityDesc.consumedShieldStat')}]]`)
    }
    if (flat !== 0) parts.push(`[[+${formatNumber(flat)}]]`)
    return parts.length > 0 ? `${parts.join(' ')} ` : ''
  }
  const damageExpr = composition(damageRatio, flatDamage, maxHpDamageRatio, shieldDamageRatio)
  const healExpr = composition(healingRatio, flatHealing)

  // 效果叙述主句：按效果类型一次性写清目标、数值与伤害类别。
  const hitCount = ability.hitCount ?? 0
  const healing = ability.effectType === 'heal'
  // 支援类数值同样吃 levelGrowth：固定威胁与 Buff 点数项均按每级 +2% 放大；
  // 有层数派生的 Buff 例外——等级成长折入层数，每层效果量保持配置基础值。
  const threatGain = (ability.threatGain ?? 0) * levelGrowth
  const threatPoints = (ability.threatPoints ?? 0) * levelGrowth
  const buff = ability.buff
  const buffSpecs = ability.buffs && ability.buffs.length > 0 ? ability.buffs : buff ? [buff] : []
  const onHitSpecs = ability.onHitBuffs && ability.onHitBuffs.length > 0
    ? ability.onHitBuffs
    : ability.onHitBuff
    ? [ability.onHitBuff]
    : []
  const selfSpecs = ability.selfBuffs && ability.selfBuffs.length > 0
    ? ability.selfBuffs
    : ability.selfBuff
    ? [ability.selfBuff]
    : []

  /**
   * 层数派生构成：与服务端优先级一致（已损气血 > 法攻百分比 > 双抗百分比 > 固定初始层数）。
   * 未标 noLevelGrowth 时等级成长放大百分比/层数，不改变每层效果量；展示值随技能等级即时变化。
   */
  const stackComposition = (spec: NonNullable<typeof ability.buff>): string | null => {
    const effGrowth = spec.noLevelGrowth ? 1 : levelGrowth
    const base = spec.baseStacks ?? 1
    const missingPerStack = spec.missingHitpointsPerStack ?? 0
    if (missingPerStack > 0) {
      const effective = +(missingPerStack / effGrowth).toFixed(1)
      return t('game.abilityDesc.buffStackMissingComposition', { n: formatNumber(effective) })
    }
    const spellStackPercent = spec.spellAttackStackPercent ?? 0
    if (spellStackPercent > 0) {
      const scaledRatio = abilityRatioPercent(spellStackPercent * effGrowth)
      if (base !== 1) {
        return t('game.abilityDesc.buffStackFormulaPair', {
          ratio: scaledRatio,
          stat: t('game.abilityDesc.attackStat.spell'),
          base: formatNumber(base),
        })
      }
      return t('game.abilityDesc.buffStackSpellFormula', {
        ratio: scaledRatio,
      })
    }
    const resistancePerStack = spec.resistancePerStack ?? 0
    if (resistancePerStack > 0) {
      return t('game.abilityDesc.buffStackResistanceComposition', {
        ratio: abilityRatioPercent((spec.resistanceStackRatio ?? 1) * effGrowth),
      })
    }
    if (base > 1) {
      return t('game.abilityDesc.buffStackFixed', { n: formatNumber(base) })
    }
    return null
  }

  const buffLabel = (spec: NonNullable<typeof ability.buff>, index: number, totalCount: number): string =>
    `[[${spec.name || (totalCount === 1 ? t('game.abilityAside.buffTitle') : t('game.abilityAside.buffFallback', { index: index + 1 }))}]]`

  /** 统一施加句：支持指定施加对象、单/多 Buff 展开、层数构成与最大持续时间。 */
  const formatBuffSpecsClause = (
    specs: NonNullable<typeof ability.buff>[],
    targetLabel: string,
    templateKeys: { single: string; simple: string; multi: string }
  ): string => {
    let body: string
    if (specs.length === 1) {
      const spec = specs[0]
      const composition = stackComposition(spec)
      body = composition
        ? t(templateKeys.single, {
          target: targetLabel,
          composition,
          buff: buffLabel(spec, 0, 1),
        })
        : t(templateKeys.simple, { target: targetLabel, buff: buffLabel(spec, 0, 1) })
    } else {
      const tokens = specs.map((spec, index) => {
        const composition = stackComposition(spec)
        const label = buffLabel(spec, index, specs.length)
        return composition ? `${composition}的 ${label}` : label
      })
      body = t(templateKeys.multi, {
        target: targetLabel,
        buffs: tokens.join('、'),
      })
    }
    const maxDuration = Math.max(0, ...specs.map(spec => spec.durationSeconds ?? 0))
    return maxDuration > 0
      ? `${body}，${t('game.abilityDesc.buffDuration', { n: formatNumber(maxDuration) })}`
      : body
  }

  const buffApplicationClause = (): string =>
    formatBuffSpecsClause(buffSpecs, target, {
      single: 'game.abilityDesc.buffApplySingle',
      simple: 'game.abilityDesc.buffApplySimple',
      multi: 'game.abilityDesc.buffApplyMulti',
    })

  const hasRanhunAttach = (ability.ranhunBaseStacks ?? 0) > 0 || (ability.ranhunSpellAttackRatio ?? 0) > 0
  const buildRanhunComp = () =>
    t('game.abilityDesc.buffStackFormulaPair', {
      ratio: abilityRatioPercent((ability.ranhunSpellAttackRatio ?? 0) * levelGrowth),
      stat: t('game.abilityDesc.attackStat.spell'),
      base: formatNumber((ability.ranhunBaseStacks ?? 0) * levelGrowth),
    })

  const clauses: string[] = []
  let ranhunHandledAsPrimary = false
  if (healing) {
    clauses.push(t('game.abilityDesc.effectHeal', { target, expr: healExpr }))
  } else if (damageExpr) {
    clauses.push(hitCount > 1
      ? t('game.abilityDesc.effectMultiHit', { target, count: hitCount, expr: damageExpr, damageKind })
      : ability.consumeAllShield
      ? t('game.abilityDesc.effectConsumeShieldDamage', { target, expr: damageExpr, damageKind })
      : t('game.abilityDesc.effectDamage', { target, expr: damageExpr, damageKind }))
  } else if (shieldMaxHpRatio > 0) {
    clauses.push(t('game.abilityDesc.effectShieldMaxHp', { ratio: abilityRatioPercent(shieldMaxHpRatio) }))
    if (buffSpecs.length > 0) {
      clauses.push(buffApplicationClause())
    }
  } else if (hasRanhunAttach) {
    clauses.push(
      `${t('game.abilityDesc.buffApplySingle', {
        target,
        composition: buildRanhunComp(),
        buff: `[[${t('combat.trigger.buff.ranhun')}]]`,
      })}，${t('game.abilityDesc.buffDuration', { n: '5' })}`
    )
    ranhunHandledAsPrimary = true
  } else if (threatGain > 0 || threatPoints > 0) {
    let clause = threatGain > 0
      ? t('game.abilityDesc.effectThreatGain', { n: formatNumber(threatGain) })
      : ''
    if (threatPoints > 0) {
      const ratio = t('game.abilityDesc.threatRatioClause', { n: abilityRatioPercent(threatPoints / 100) })
      clause = clause ? `${clause}，${ratio}` : ratio
    }
    if (buffSpecs.length > 0) {
      const apply = buffApplicationClause()
      clause = clause ? `${clause}，${apply}` : apply
    }
    clauses.push(clause || t('game.abilityDesc.effectThreat'))
  } else if (buffSpecs.length > 0) {
    clauses.push(buffApplicationClause())
  } else {
    clauses.push(t('game.abilityDesc.effectBuff', { target }))
  }
  // 机制附加子句：引导受击叠层、消耗层数增伤、连击中止条件与击杀连锁与主句连成同一段，不另起配置行。
  if (ability.channelingAttackedBuff) {
    const chanBuff = ability.channelingAttackedBuff
    const chanLabel = buffLabel(chanBuff, 0, 1)
    clauses.push(t('game.abilityDesc.channelingAttackedBuffClause', {
      buff: chanLabel,
      n: formatNumber(chanBuff.durationSeconds ?? 5),
    }))
  }
  if ((ability.consumeBuffExtraDamagePerStack ?? 0) > 0 && ability.consumeBuffId) {
    const cName = ability.channelingAttackedBuff?.buffId === ability.consumeBuffId && ability.channelingAttackedBuff.name
      ? ability.channelingAttackedBuff.name
      : resolveTriggerBuffName(ability.consumeBuffId)
    clauses.push(t('game.abilityDesc.consumeBuffExtraDamageClause', {
      buff: `[[${cName}]]`,
      ratio: abilityRatioPercent(ability.consumeBuffExtraDamagePerStack!),
    }))
  }
  if (hitCount > 1) {
    clauses.push(t('game.abilityDesc.multiHitEnd'))
  }
  const chainCount = ability.chainOnKillCount ?? 0
  if (chainCount > 0) {
    clauses.push(t('game.abilityDesc.chainOnKill', { n: formatNumber(chainCount) }))
  }
  if (ability.yixingMorphName) {
    clauses.push(t('game.abilityDesc.yixingMorphClause', {
      name: `[[${ability.yixingMorphName}]]`,
    }))
  }
  if (hasRanhunAttach && !ranhunHandledAsPrimary) {
    const ranhunApply = t('game.abilityDesc.ranhunApplyClause', {
      composition: buildRanhunComp(),
      buff: `[[${t('combat.trigger.buff.ranhun')}]]`,
    })
    clauses.push(`${ranhunApply}，${t('game.abilityDesc.buffDuration', { n: '5' })}`)
  }
  if (onHitSpecs.length > 0) {
    clauses.push(formatBuffSpecsClause(onHitSpecs, t('game.abilityDesc.hitTargetRef'), {
      single: 'game.abilityDesc.onHitBuffSingle',
      simple: 'game.abilityDesc.onHitBuffSimple',
      multi: 'game.abilityDesc.onHitBuffMulti',
    }))
  }
  if (selfSpecs.length > 0) {
    const conditionalSpecs = selfSpecs.filter(s => s.skipIfHasYixing)
    const unconditionalSpecs = selfSpecs.filter(s => !s.skipIfHasYixing)
    if (unconditionalSpecs.length > 0) {
      clauses.push(formatBuffSpecsClause(unconditionalSpecs, t('game.abilityTarget.self'), {
        single: 'game.abilityDesc.selfBuffSingle',
        simple: 'game.abilityDesc.selfBuffSimple',
        multi: 'game.abilityDesc.selfBuffMulti',
      }))
    }
    for (const condSpec of conditionalSpecs) {
      const condDuration = condSpec.durationSeconds ?? 0
      const baseCond = t('game.abilityDesc.selfBuffIfNoYixing', {
        buff: buffLabel(condSpec, 0, 1),
      })
      clauses.push(condDuration > 0
        ? `${baseCond}，${t('game.abilityDesc.buffDuration', { n: formatNumber(condDuration) })}`
        : baseCond)
    }
  }
  if (ability.resetCooldownOnYixingKill) {
    clauses.push(t('game.abilityDesc.resetCooldownOnYixingKillClause'))
  }
  const effectSentence = `${clauses.join('；')}。`
  const description = ability.description ? `${ability.description}\n${effectSentence}` : effectSentence

  // 主卡片只保留四项基础战斗消耗与冷却行；涉及 Buff 的详细数值加成与机制统一由独立 Buff 副窗展示。
  const costLine = (ability.hpCostMaxHpRatio ?? 0) > 0 && (ability.manaCost ?? 0) <= 0
    ? t('game.abilityDesc.hpCostMaxHp', { n: abilityRatioPercent(ability.hpCostMaxHpRatio!) })
    : t('game.abilityDesc.manaCost', { n: formatNumber(ability.manaCost ?? 0) })
  const lines = [
    t('game.abilityDesc.level', { n: normalizedLevel }),
    // 配表字段可缺省，服务端按 0 处理，前端同口径。
    t('game.abilityDesc.cooldownTime', { n: ((ability.cooldownMilliseconds ?? 0) / 1000).toFixed(1) }),
    t('game.abilityDesc.castTime', { n: ((ability.castMilliseconds ?? 0) / 1000).toFixed(1) }),
    costLine,
  ]
  return {
    title: ability.name,
    badge: formatAbilityCategory(ability.category, ability.element),
    description,
    lines,
  }
}

/**
 * 双形态「变招」独立技能描述构造器（如斩玉拥有移形时切换为踏云）
 * 生成与主技能窗完全同构的效果说明与基础属性行。
 */
export function describeMorphAbility(
  ability: CombatAbilityDefinition,
  level: number
): EffectTooltipContent | null {
  if (!ability.yixingMorphName) return null
  const t = getT()
  const normalizedLevel = Math.max(1, level)
  const levelGrowth = 1 + (normalizedLevel - 1) * 0.02
  const damageRatio = (ability.damageRatio ?? 0) * levelGrowth
  const flatDamage = (ability.flatDamage ?? 0) * levelGrowth
  const morphTarget = resolveAbilityTargetLabel(ability.yixingMorphTargetType ?? 'allEnemies')
  const categoryLabel = t(`combat.encounter.damageCategory.${ability.category}`)
  const elementWord = ability.element && ability.element !== 'none'
    ? t(`game.abilityDesc.elementWord.${ability.element}`)
    : ''
  const damageKind = elementWord
    ? t('game.abilityDesc.damageKindElement', { element: elementWord, category: categoryLabel })
    : t('game.abilityDesc.damageKindPlain', { category: categoryLabel })
  const attackStat = t(`game.abilityDesc.attackStat.${ability.category === 'spell' ? 'spell' : 'physical'}`)
  const parts: string[] = []
  if (damageRatio !== 0) parts.push(`[[${abilityRatioPercent(damageRatio)}${attackStat}]]`)
  if (flatDamage !== 0) parts.push(`[[+${formatNumber(flatDamage)}]]`)
  const damageExpr = parts.length > 0 ? `${parts.join(' ')} ` : ''
  const hitCount = ability.hitCount ?? 0
  const mainClause = hitCount > 1
    ? t('game.abilityDesc.effectMultiHit', { target: morphTarget, count: hitCount, expr: damageExpr, damageKind })
    : t('game.abilityDesc.effectDamage', { target: morphTarget, expr: damageExpr, damageKind })
  const description = `${mainClause}；${t('game.abilityDesc.yixingMorphSkillConsumeClause')}。`
  const lines = [
    t('game.abilityDesc.level', { n: normalizedLevel }),
    t('game.abilityDesc.cooldownTime', { n: ((ability.cooldownMilliseconds ?? 0) / 1000).toFixed(1) }),
    t('game.abilityDesc.castTime', { n: ((ability.castMilliseconds ?? 0) / 1000).toFixed(1) }),
    t('game.abilityDesc.manaCost', { n: formatNumber(ability.manaCost ?? 0) }),
  ]
  return {
    title: ability.yixingMorphName,
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
 * 道行（原身家）数值格式化：小于 1 万时保留最多 2 位有效小数（如 606.55、599.9、113），
 * 达到 1 万及以上自动走中文万进制缩写。
 */
export function formatValuation(value: number | null | undefined): string {
  const num = typeof value === 'number' && Number.isFinite(value) ? Math.max(0, value) : 0
  const rounded = Math.round(num * 100) / 100
  if (rounded < 10000) return String(rounded)
  return formatUnitNumber(rounded)
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
 * 解析触发条件关联的 Buff / 状态名称
 */
export function resolveTriggerBuffName(buffId: string | null | undefined): string {
  const t = getT()
  if (!buffId) return t('combat.trigger.unspecifiedBuff') || '指定状态'
  const stripped = buffId.startsWith('buff.') ? buffId.slice(5) : buffId
  const direct = t(`combat.trigger.buff.${stripped}`)
  if (direct && !direct.startsWith('combat.trigger.buff.')) return direct
  const full = t(`combat.trigger.buff.${buffId}`)
  if (full && !full.startsWith('combat.trigger.buff.')) return full
  return buffId
}

/**
 * 战斗触发条件转展示文案（如「当自身生命百分比不高于 50% 时」、「当目标凝神茶状态层数大于 0 时」）
 * @param trigger 触发条件对象
 */
export function describeCombatTrigger(trigger: CombatTrigger): string {
  const t = getT()
  const dependency = t(`game.trigger.dependency.${trigger.dependency}`)
  const condition = t(`game.trigger.condition.${trigger.condition}`)
  const comparator = t(`game.trigger.comparator.${trigger.comparator}`)

  // 状态层数（Buff Stacks）条件：显式注入具体状态名称与层数判断
  if (trigger.condition === 'buff-stacks') {
    const buff = resolveTriggerBuffName(trigger.buffId)
    return t('game.trigger.template.buffStack', {
      dependency,
      buff,
      comparator,
      value: formatNumber(trigger.value),
      unit: '',
    })
  }

  if (trigger.comparator === 'is-active' || trigger.comparator === 'is-inactive') {
    const active = trigger.comparator === 'is-active'
    if (trigger.condition.startsWith('is-')) {
      const role = condition.replace(/^是/, '')
      return active
        ? t('game.trigger.template.roleIs', { dependency, role })
        : t('game.trigger.template.roleIsNot', { dependency, role })
    }
    if (trigger.condition.startsWith('has-')) {
      const role = condition.replace(/^有/, '')
      return active
        ? t('game.trigger.template.roleHas', { dependency, role })
        : t('game.trigger.template.roleHasNot', { dependency, role })
    }
    return t('game.trigger.template.status', { dependency, condition, comparator })
  }
  const isPercentage = trigger.condition.includes('percentage')
  const unit = isPercentage ? '%' : trigger.condition === 'elapsed-seconds' ? t('game.trigger.unit.seconds') : ''
  return t('game.trigger.template.value', {
    dependency,
    condition,
    comparator,
    value: formatNumber(trigger.value),
    unit,
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
