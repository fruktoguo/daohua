import { memo, useState } from 'react'
import { PillStatLabel } from './PillLabel'
import { formatNumber } from '../game/format'
import {
  PercentValue,
  DEFAULT_ROOT_ATTRIBUTE_POINTS,
  BASELINE_COMBAT_ATTRIBUTES,
  type BaseAttributePool,
} from '../game/combatConstants'
import type { Buff, CombatAttributes, CombatantDefinition, CombatLoadout } from '../game/types'
import { useT } from '../i18n'

/**
 * 物法与五行增免伤比例对：[增伤比例, 免伤比例]，内部均为 double 点数比例
 */
export interface CombatBoostReduceSet {
  physical: readonly [number, number]
  spell: readonly [number, number]
  metal: readonly [number, number]
  wood: readonly [number, number]
  water: readonly [number, number]
  fire: readonly [number, number]
  earth: readonly [number, number]
}

/**
 * 斗法战斗属性统一展示模型 (CombatAttributePanelModel)
 *
 * 内视「斗法」Tab 与战场单位详情弹窗（角色/敌怪）共用的归一化数值口径：
 * 只承载展示所需的最终数值（点数与 double 比例原始值），
 * 百分比包装与格式化统一由 CombatAttributePanel 渲染层完成。
 */
export interface CombatAttributePanelModel {
  maxHitpoints: number
  maxManapoints: number
  hpRegenPerFiveSeconds: number
  mpRegenPerFiveSeconds: number
  physicalAttack: number
  spellAttack: number
  armor: number
  magicResistance: number
  accuracyRating: number
  evasionRating: number
  criticalRating: number
  criticalAvoidanceRating: number
  parryBreakRating: number
  parryResolveRating: number
  criticalDamageRatio: number
  actionSpeedPoints: number
  cooldownSpeedPoints: number
  manaCostReductionPoints: number
  damageReductionRatio: number
  armorPenetrationRatio: number
  magicPenetrationRatio: number
  threatBonusRatio: number
  boostReduce: CombatBoostReduceSet
}

const EMPTY_BOOST_REDUCE: CombatBoostReduceSet = {
  physical: [0, 0],
  spell: [0, 0],
  metal: [0, 0],
  wood: [0, 0],
  water: [0, 0],
  fire: [0, 0],
  earth: [0, 0],
}

/**
 * 玩家斗法属性模型解析：服务端实时属性 (CombatAttributes) 优先，
 * 未推送时以当前境界基础属性底座 (basePool) 应用六维加成估算，全部向下取整。
 */
export function resolvePlayerCombatAttributeModel(
  attributes: CombatAttributes | null | undefined,
  loadout: CombatLoadout | null | undefined,
  basePool: BaseAttributePool,
): CombatAttributePanelModel {
  const staminaBonus = (loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const intelligenceBonus = (loadout?.intelligenceLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const boneBonus = (loadout?.defenseLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const attackBonus = (loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const swordsmanshipBonus = (loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const talismanBonus = (loadout?.talismanLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01

  const physicalAttack = Math.floor(attributes
    ? (attributes.style === 'five-elements' ? (basePool.baseAd * (1.0 + attackBonus)) : attributes.maxDamage)
    : (basePool.baseAd * (1.0 + attackBonus)))
  const spellAttack = Math.floor(attributes
    ? (attributes.style === 'five-elements' ? attributes.maxDamage : (basePool.baseAp * (1.0 + swordsmanshipBonus * 2.0)))
    : (basePool.baseAp * (1.0 + swordsmanshipBonus * 2.0)))

  return {
    maxHitpoints: Math.floor(attributes?.maxHitpoints ?? (basePool.baseHp * (1.0 + staminaBonus) * (1.0 + boneBonus))),
    maxManapoints: Math.floor(attributes?.maxManapoints ?? (basePool.baseMp * (1.0 + intelligenceBonus) * (1.0 + boneBonus))),
    hpRegenPerFiveSeconds: Math.floor(attributes?.hpRegenPerFiveSeconds ?? basePool.baseHpRegen),
    mpRegenPerFiveSeconds: Math.floor(attributes?.mpRegenPerFiveSeconds ?? basePool.baseMpRegen),
    physicalAttack,
    spellAttack,
    armor: Math.floor(Math.max(0.0, attributes?.armor ?? (basePool.baseArmor * (1.0 + staminaBonus)))),
    magicResistance: Math.floor(Math.max(0.0, attributes?.magicResistance ?? (basePool.baseMagicRes * (1.0 + intelligenceBonus)))),
    accuracyRating: Math.floor(attributes?.accuracyRating ?? (basePool.baseAccuracy * (1.0 + swordsmanshipBonus))),
    evasionRating: Math.floor(attributes?.evasionRating ?? (basePool.baseEvasion * (1.0 + talismanBonus))),
    criticalRating: Math.floor(attributes ? (attributes.criticalRating ?? attributes.criticalChance * 100.0) : (basePool.baseCrit * (1.0 + attackBonus))),
    criticalAvoidanceRating: Math.floor(attributes ? (attributes.criticalAvoidanceRating ?? attributes.criticalAvoidance * 100.0) : (basePool.baseCritAvoid * (1.0 + talismanBonus))),
    parryBreakRating: Math.floor(basePool.baseBreak * (1.0 + attackBonus)),
    parryResolveRating: Math.floor(basePool.baseParry * (1.0 + boneBonus + talismanBonus)),
    criticalDamageRatio: attributes?.criticalDamageRatio ?? 1.5,
    actionSpeedPoints: 0.0,
    cooldownSpeedPoints: BASELINE_COMBAT_ATTRIBUTES.percentPoints,
    manaCostReductionPoints: BASELINE_COMBAT_ATTRIBUTES.percentPoints,
    damageReductionRatio: attributes?.damageAbsorptionRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints,
    armorPenetrationRatio: attributes?.penetrationRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints,
    magicPenetrationRatio: attributes?.penetrationRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints,
    threatBonusRatio: BASELINE_COMBAT_ATTRIBUTES.percentPoints,
    boostReduce: EMPTY_BOOST_REDUCE,
  }
}

/**
 * 敌怪斗法属性模型解析：由遭遇配表 CombatantDefinition 映射到同一展示口径。
 * 术法系（category=spell 或 five-elements 流派）攻强记入术法攻击，否则记入物理攻击；
 * 暴击/免暴仅有比例字段，按点数 = 比例 × 100 折算（与玩家 criticalChance 兜底同口径）；
 * 破招/化解与穿透类字段配表未下发，按 0 展示。
 * 斗法镜像对手无配表定义时，气血/灵力上限回退到 wire 实时值（fallbackResources）。
 */
export function resolveEnemyCombatAttributeModel(
  definition: CombatantDefinition | null | undefined,
  fallbackResources?: { maxHitpoints: number; maxManapoints: number },
): CombatAttributePanelModel {
  const isSpellAttacker = definition?.category === 'spell' || definition?.style === 'five-elements'
  const attack = definition?.maxDamage ?? 0
  return {
    maxHitpoints: Math.floor(definition?.maxHitpoints ?? fallbackResources?.maxHitpoints ?? 0),
    maxManapoints: Math.floor(definition?.maxManapoints ?? fallbackResources?.maxManapoints ?? 0),
    hpRegenPerFiveSeconds: Math.floor(definition?.hpRegenPerFiveSeconds ?? 0),
    mpRegenPerFiveSeconds: Math.floor(definition?.mpRegenPerFiveSeconds ?? 0),
    physicalAttack: Math.floor(isSpellAttacker ? 0 : attack),
    spellAttack: Math.floor(isSpellAttacker ? attack : 0),
    armor: Math.floor(Math.max(0, definition?.armor ?? 0)),
    magicResistance: Math.floor(Math.max(0, definition?.magicResistance ?? 0)),
    accuracyRating: Math.floor(definition?.accuracyRating ?? 0),
    evasionRating: Math.floor(definition?.evasionRating ?? 0),
    criticalRating: Math.floor((definition?.criticalChance ?? 0) * 100.0),
    criticalAvoidanceRating: Math.floor((definition?.criticalAvoidance ?? 0) * 100.0),
    parryBreakRating: 0,
    parryResolveRating: 0,
    criticalDamageRatio: definition?.criticalDamageRatio ?? 1.5,
    actionSpeedPoints: 0,
    cooldownSpeedPoints: 0,
    manaCostReductionPoints: 0,
    damageReductionRatio: definition?.damageAbsorptionRatio ?? 0,
    armorPenetrationRatio: 0,
    magicPenetrationRatio: 0,
    threatBonusRatio: 0,
    boostReduce: EMPTY_BOOST_REDUCE,
  }
}

/** 斗法属性面板分组 Tab 标识：基础战斗属性 | 特殊战斗属性 | 增免伤与五行属性 */
type CombatAttributeGroupTab = 'base' | 'special' | 'elements'

/**
 * 斗法战斗属性统一面板 (CombatAttributePanel)
 *
 * 内视「斗法」Tab 与战场单位详情弹窗（角色/敌怪）共用：
 * 基础战斗属性、特殊战斗属性、增免伤与五行属性三组以 Tab 切换展示，
 * 以及可选的当前运转丹药与神通加持 Buff 分区。
 */
export const CombatAttributePanel = memo(function CombatAttributePanel({
  model,
  buffs = [],
}: {
  model: CombatAttributePanelModel
  buffs?: readonly Buff[]
}): React.JSX.Element {
  const t = useT()
  const [activeGroup, setActiveGroup] = useState<CombatAttributeGroupTab>('base')

  // 特殊战斗属性 (统一百分比类型 PercentValue，内部均为 double 点数)
  const critDamagePercent = PercentValue.linear(model.criticalDamageRatio * 100.0)
  const actionSpeedPercent = PercentValue.nonLinear(model.actionSpeedPoints)
  const cooldownSpeedPercent = PercentValue.nonLinear(model.cooldownSpeedPoints)
  const manaCostReductionPercent = PercentValue.nonLinear(model.manaCostReductionPoints)
  const damageReductionPercent = PercentValue.linear(model.damageReductionRatio * 100.0)
  const armorPenPercent = PercentValue.linear(model.armorPenetrationRatio * 100.0)
  const magicPenPercent = PercentValue.linear(model.magicPenetrationRatio * 100.0)
  const threatBonusPercent = PercentValue.linear(model.threatBonusRatio * 100.0)

  // 增免伤与五行 (全部为 PercentValue double)
  const boostReduceText = (pair: readonly [number, number]) =>
    `${PercentValue.linear(pair[0] * 100.0).format()} / ${PercentValue.linear(pair[1] * 100.0).format()}`

  return (
    <div className="combat-attribute-panel">
      {/* 三属性分组 Tab 切换（复用内视根基/坊市同款二级导航样式） */}
      <div className="insight-sub-tabs" role="tablist">
        <button
          className={`insight-sub-tab-btn ${activeGroup === 'base' ? 'active' : ''}`}
          onClick={() => setActiveGroup('base')}
          type="button"
        >
          {t('insight.combat.groupBase')}
        </button>
        <button
          className={`insight-sub-tab-btn ${activeGroup === 'special' ? 'active' : ''}`}
          onClick={() => setActiveGroup('special')}
          type="button"
        >
          {t('insight.combat.groupSpecial')}
        </button>
        <button
          className={`insight-sub-tab-btn ${activeGroup === 'elements' ? 'active' : ''}`}
          onClick={() => setActiveGroup('elements')}
          type="button"
        >
          {t('insight.combat.groupElements')}
        </button>
      </div>

      {activeGroup === 'base' && (
        <div className="insight-base-layout">
          {/* 第 1 排：气血、灵力与自然回复 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.hp')} value={model.maxHitpoints} tone="emerald" tooltip={t('insight.combat.hpTooltip')} />
            <PillStatLabel label={t('insight.combat.mp')} value={model.maxManapoints} tone="cyan" tooltip={t('insight.combat.mpTooltip')} />
            <PillStatLabel label={t('insight.combat.hpRegen')} value={model.hpRegenPerFiveSeconds} tone="emerald" tooltip={t('insight.combat.hpRegenTooltip')} />
            <PillStatLabel label={t('insight.combat.mpRegen')} value={model.mpRegenPerFiveSeconds} tone="cyan" tooltip={t('insight.combat.mpRegenTooltip')} />
          </div>

          {/* 第 2 排：攻防属性 (物理攻击、术法攻击、物理护甲、术法抗性) */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.physAtk')} value={model.physicalAttack} tone="crimson" tooltip={t('insight.combat.physAtkTooltip')} />
            <PillStatLabel label={t('insight.combat.spellAtk')} value={model.spellAttack} tone="indigo" tooltip={t('insight.combat.spellAtkTooltip')} />
            <PillStatLabel label={t('insight.combat.armor')} value={model.armor} tone="bronze" tooltip={t('insight.combat.armorTooltip')} />
            <PillStatLabel label={t('insight.combat.magicRes')} value={model.magicResistance} tone="violet" tooltip={t('insight.combat.magicResTooltip')} />
          </div>

          {/* 第 3 排：博弈对抗 (命中/闪避、暴击/免暴、破招/化解) */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.accuracy')} value={model.accuracyRating} tone="sky" tooltip={t('insight.combat.accuracyTooltip')} />
            <PillStatLabel label={t('insight.combat.evasion')} value={model.evasionRating} tone="teal" tooltip={t('insight.combat.evasionTooltip')} />
            <PillStatLabel label={t('insight.combat.crit')} value={model.criticalRating} tone="gold" tooltip={t('insight.combat.critTooltip')} />
            <PillStatLabel label={t('insight.combat.critAvoid')} value={model.criticalAvoidanceRating} tone="slate" tooltip={t('insight.combat.critAvoidTooltip')} />
            <PillStatLabel label={t('insight.combat.parryBreak')} value={model.parryBreakRating} tone="amber" tooltip={t('insight.combat.parryBreakTooltip')} />
            <PillStatLabel label={t('insight.combat.parryResolve')} value={model.parryResolveRating} tone="silver" tooltip={t('insight.combat.parryResolveTooltip')} />
          </div>
        </div>
      )}

      {activeGroup === 'special' && (
        <div className="insight-base-layout">
          {/* 第 1 排：时序与施法效能 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.critDamage')} value={critDamagePercent.format()} tone="gold" tooltip={t('insight.combat.critDamageTooltip')} />
            <PillStatLabel label={t('insight.combat.actionSpeed')} value={actionSpeedPercent.format()} tone="teal" tooltip={t('insight.combat.actionSpeedTooltip')} />
            <PillStatLabel label={t('insight.combat.cooldownSpeed')} value={cooldownSpeedPercent.format()} tone="cyan" tooltip={t('insight.combat.cooldownSpeedTooltip')} />
            <PillStatLabel label={t('insight.combat.manaCostReduction')} value={manaCostReductionPercent.format()} tone="cyan" tooltip={t('insight.combat.manaCostReductionTooltip')} />
          </div>

          {/* 第 2 排：增伤减伤与穿透 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.damageReduction')} value={damageReductionPercent.format()} tone="emerald" tooltip={t('insight.combat.damageReductionTooltip')} />
            <PillStatLabel label={t('insight.combat.armorPen')} value={armorPenPercent.format()} tone="bronze" tooltip={t('insight.combat.armorPenTooltip')} />
            <PillStatLabel label={t('insight.combat.magicPen')} value={magicPenPercent.format()} tone="violet" tooltip={t('insight.combat.magicPenTooltip')} />
            <PillStatLabel label={t('insight.combat.threatBonus')} value={threatBonusPercent.format()} tone="crimson" tooltip={t('insight.combat.threatBonusTooltip')} />
          </div>
        </div>
      )}

      {activeGroup === 'elements' && (
        <div className="insight-base-layout">
          {/* 第 1 排：物法基础增减伤 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.physBoostReduce')} value={boostReduceText(model.boostReduce.physical)} tone="crimson" tooltip={t('insight.combat.physBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.spellBoostReduce')} value={boostReduceText(model.boostReduce.spell)} tone="indigo" tooltip={t('insight.combat.spellBoostReduceTooltip')} />
          </div>

          {/* 第 2 排：五行克制增免 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.metalBoostReduce')} value={boostReduceText(model.boostReduce.metal)} tone="gold" tooltip={t('insight.combat.metalBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.woodBoostReduce')} value={boostReduceText(model.boostReduce.wood)} tone="emerald" tooltip={t('insight.combat.woodBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.waterBoostReduce')} value={boostReduceText(model.boostReduce.water)} tone="cyan" tooltip={t('insight.combat.waterBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.fireBoostReduce')} value={boostReduceText(model.boostReduce.fire)} tone="amber" tooltip={t('insight.combat.fireBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.earthBoostReduce')} value={boostReduceText(model.boostReduce.earth)} tone="bronze" tooltip={t('insight.combat.earthBoostReduceTooltip')} />
          </div>
        </div>
      )}

      {/* 运转中的丹药与神通 Buff */}
      {buffs.length > 0 && (
        <div className="insight-pill-group">
          <h4 className="insight-pill-group-title">{t('insight.combat.groupBuffs')}</h4>
          <div className="insight-pill-group-items">
            {buffs.map(buff => (
              <PillStatLabel
                key={buff.buffId}
                label={buff.name}
                value={buff.stacks > 1 ? `×${formatNumber(buff.stacks)}` : t('insight.combat.buffActive')}
                tone="gold"
                tooltip={t('insight.combat.buffTooltip', { name: buff.name, stacks: formatNumber(buff.stacks) })}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  )
})
