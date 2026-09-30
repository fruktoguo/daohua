import { memo, useState } from 'react'
import { PillStatLabel } from './PillLabel'
import { formatNumber } from '../game/format'
import { PercentValue } from '../game/combatConstants'
import type { Buff, CombatAttributes, MonsterDropDefinition } from '../game/types'
import { useT } from '../i18n'
import { ItemDropReference } from './ItemDisplay'

/** 斗法属性面板分组 Tab 标识：基础战斗属性 | 特殊战斗属性 | 增免伤与五行属性 | 掉落明细（仅敌怪详情） */
type CombatAttributeGroupTab = 'base' | 'special' | 'elements' | 'drops'

/**
 * 斗法战斗属性统一面板 (CombatAttributePanel)
 *
 * 内视「斗法」Tab 与战场单位详情弹窗（角色/敌怪/斗法镜像）共用：
 * 只渲染服务端下发的 CombatAttributes 终值，不做任何本地估算或配表反查；
 * 基础点数、特殊点数（战斗设计 §7.5 十项特殊属性）、增减伤十六项三组以 Tab 切换展示，
 * 以及可选的当前运转丹药与神通加持 Buff 分区。
 * 敌怪详情额外提供 drops（静态配表 CombatantDefinition.drops）时追加第四个「掉落」Tab。
 */
export const CombatAttributePanel = memo(function CombatAttributePanel({
  attributes,
  buffs = [],
  drops,
}: {
  /** 服务端权威战斗属性终值（玩家 combat.attributes / 敌怪 attributes / 斗法镜像 opponentAttributes 同一形状） */
  attributes: CombatAttributes
  buffs?: readonly Buff[]
  /** 敌怪掉落条目（仅战场敌怪详情提供；undefined = 不渲染掉落 Tab） */
  drops?: readonly MonsterDropDefinition[]
}): React.JSX.Element {
  const t = useT()
  const [activeGroup, setActiveGroup] = useState<CombatAttributeGroupTab>('base')

  // 点数制属性按双曲线渲染有效百分比：增伤类 Linear（+x 点 = +x%），减伤/速率类 NonLinear。
  const linearText = (points: number) => PercentValue.linear(points).format()
  const nonLinearText = (points: number) => PercentValue.nonLinear(points).format()
  const pointsText = (points: number) => formatNumber(Math.floor(points))
  // 原始乘区因子（暴击倍率/成长曲线）：倍率直存属性，按原值百分比展示（如 150%）。
  const factorText = (factor: number) => `${formatNumber(Math.round(factor * 100))}%`
  // 增减伤对：[增伤 Linear 点数, 减伤 NonLinear 点数] → "+x% / y%"。
  const boostReduceText = (bonus: number, reduction: number) =>
    `${linearText(bonus)} / ${nonLinearText(reduction)}`

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
        {drops !== undefined && (
          <button
            className={`insight-sub-tab-btn ${activeGroup === 'drops' ? 'active' : ''}`}
            onClick={() => setActiveGroup('drops')}
            type="button"
          >
            {t('insight.combat.groupDrops')}
          </button>
        )}
      </div>

      {activeGroup === 'base' && (
        <div className="insight-base-layout">
          {/* 第 1 排：气血、灵力与自然回复 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.hp')} value={Math.floor(attributes.maxHitpoints)} tone="emerald" tooltip={t('insight.combat.hpTooltip')} />
            <PillStatLabel label={t('insight.combat.mp')} value={Math.floor(attributes.maxManapoints)} tone="cyan" tooltip={t('insight.combat.mpTooltip')} />
            <PillStatLabel label={t('insight.combat.hpRegen')} value={Math.floor(attributes.hpRegenPerFiveSeconds)} tone="emerald" tooltip={t('insight.combat.hpRegenTooltip')} />
            <PillStatLabel label={t('insight.combat.mpRegen')} value={Math.floor(attributes.mpRegenPerFiveSeconds)} tone="cyan" tooltip={t('insight.combat.mpRegenTooltip')} />
          </div>

          {/* 第 2 排：攻防属性 (物理攻击、术法攻击、物理护甲、术法抗性) */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.physAtk')} value={Math.floor(attributes.physicalAttack)} tone="crimson" tooltip={t('insight.combat.physAtkTooltip')} />
            <PillStatLabel label={t('insight.combat.spellAtk')} value={Math.floor(attributes.spellAttack)} tone="indigo" tooltip={t('insight.combat.spellAtkTooltip')} />
            <PillStatLabel label={t('insight.combat.armor')} value={Math.floor(attributes.armor)} tone="bronze" tooltip={t('insight.combat.armorTooltip')} />
            <PillStatLabel label={t('insight.combat.magicRes')} value={Math.floor(attributes.magicResistance)} tone="violet" tooltip={t('insight.combat.magicResTooltip')} />
          </div>

          {/* 第 3 排：博弈对抗点数 (命中/闪避、暴击/免暴、破招/化解) */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.accuracy')} value={pointsText(attributes.accuracy)} tone="sky" tooltip={t('insight.combat.accuracyTooltip')} />
            <PillStatLabel label={t('insight.combat.evasion')} value={pointsText(attributes.evasion)} tone="teal" tooltip={t('insight.combat.evasionTooltip')} />
            <PillStatLabel label={t('insight.combat.crit')} value={pointsText(attributes.critical)} tone="gold" tooltip={t('insight.combat.critTooltip')} />
            <PillStatLabel label={t('insight.combat.critAvoid')} value={pointsText(attributes.criticalResistance)} tone="slate" tooltip={t('insight.combat.critAvoidTooltip')} />
            <PillStatLabel label={t('insight.combat.parryBreak')} value={pointsText(attributes.breakPower)} tone="amber" tooltip={t('insight.combat.parryBreakTooltip')} />
            <PillStatLabel label={t('insight.combat.parryResolve')} value={pointsText(attributes.parryResolve)} tone="silver" tooltip={t('insight.combat.parryResolveTooltip')} />
          </div>
        </div>
      )}

      {activeGroup === 'special' && (
        <div className="insight-base-layout">
          {/* 第 1 排：时序速率（NonLinear 点数；出手间隔为系统常量机制，不在面板展示） */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.attackSpeed')} value={nonLinearText(attributes.attackSpeedPoints)} tone="teal" tooltip={t('insight.combat.attackSpeedTooltip')} />
            <PillStatLabel label={t('insight.combat.actionSpeed')} value={nonLinearText(attributes.actionSpeedPoints)} tone="teal" tooltip={t('insight.combat.actionSpeedTooltip')} />
            <PillStatLabel label={t('insight.combat.cooldownReduction')} value={nonLinearText(attributes.cooldownReductionPoints)} tone="cyan" tooltip={t('insight.combat.cooldownReductionTooltip')} />
          </div>

          {/* 第 2 排：伤害增幅（暴击倍率 + Linear 点数） */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.critDamage')} value={factorText(attributes.criticalMultiplier)} tone="gold" tooltip={t('insight.combat.critDamageTooltip')} />
            <PillStatLabel label={t('insight.combat.basicAttackBonus')} value={linearText(attributes.basicAttackBonusPoints)} tone="crimson" tooltip={t('insight.combat.basicAttackBonusTooltip')} />
            <PillStatLabel label={t('insight.combat.skillBonus')} value={linearText(attributes.skillBonusPoints)} tone="indigo" tooltip={t('insight.combat.skillBonusTooltip')} />
            <PillStatLabel label={t('insight.combat.threatBonus')} value={linearText(attributes.threatBonusPoints)} tone="crimson" tooltip={t('insight.combat.threatBonusTooltip')} />
          </div>

          {/* 第 3 排：治疗增幅与汲取/反弹（Linear 点数） */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.healingBonus')} value={linearText(attributes.healingBonusPoints)} tone="violet" tooltip={t('insight.combat.healingBonusTooltip')} />
            <PillStatLabel label={t('insight.combat.hpDrain')} value={linearText(attributes.hpDrainPoints)} tone="emerald" tooltip={t('insight.combat.hpDrainTooltip')} />
            <PillStatLabel label={t('insight.combat.mpDrain')} value={linearText(attributes.mpDrainPoints)} tone="cyan" tooltip={t('insight.combat.mpDrainTooltip')} />
            <PillStatLabel label={t('insight.combat.damageReflect')} value={linearText(attributes.damageReflectPoints)} tone="bronze" tooltip={t('insight.combat.damageReflectTooltip')} />
          </div>

          {/* 第 4 排：成长曲线乘区（普攻倍率/技能强度/吟唱系数/冷却系数） */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.basicAttackRatio')} value={factorText(attributes.basicAttackRatio)} tone="crimson" tooltip={t('insight.combat.basicAttackRatioTooltip')} />
            <PillStatLabel label={t('insight.combat.skillIntensity')} value={factorText(attributes.skillIntensity)} tone="indigo" tooltip={t('insight.combat.skillIntensityTooltip')} />
            <PillStatLabel label={t('insight.combat.castTimeFactor')} value={factorText(attributes.castTimeFactor)} tone="cyan" tooltip={t('insight.combat.castTimeFactorTooltip')} />
            <PillStatLabel label={t('insight.combat.cooldownFactor')} value={factorText(attributes.cooldownFactor)} tone="cyan" tooltip={t('insight.combat.cooldownFactorTooltip')} />
          </div>
        </div>
      )}

      {activeGroup === 'elements' && (
        <div className="insight-base-layout">
          {/* 第 1 排：全部/物理/术法增减对（增伤 Linear / 减伤 NonLinear） */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.totalBoostReduce')} value={boostReduceText(attributes.totalDamageBonusPoints, attributes.damageAbsorptionPoints)} tone="emerald" tooltip={t('insight.combat.totalBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.physBoostReduce')} value={boostReduceText(attributes.physicalDamageBonusPoints, attributes.physicalDamageReductionPoints)} tone="crimson" tooltip={t('insight.combat.physBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.spellBoostReduce')} value={boostReduceText(attributes.spellDamageBonusPoints, attributes.spellDamageReductionPoints)} tone="indigo" tooltip={t('insight.combat.spellBoostReduceTooltip')} />
          </div>

          {/* 第 2 排：五行增减对——增伤按攻击携带元素匹配生效，减伤按承受攻击元素匹配生效 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.metalBoostReduce')} value={boostReduceText(attributes.metalDamageBonusPoints, attributes.metalDamageReductionPoints)} tone="gold" tooltip={t('insight.combat.metalBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.woodBoostReduce')} value={boostReduceText(attributes.woodDamageBonusPoints, attributes.woodDamageReductionPoints)} tone="emerald" tooltip={t('insight.combat.woodBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.waterBoostReduce')} value={boostReduceText(attributes.waterDamageBonusPoints, attributes.waterDamageReductionPoints)} tone="cyan" tooltip={t('insight.combat.waterBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.fireBoostReduce')} value={boostReduceText(attributes.fireDamageBonusPoints, attributes.fireDamageReductionPoints)} tone="amber" tooltip={t('insight.combat.fireBoostReduceTooltip')} />
            <PillStatLabel label={t('insight.combat.earthBoostReduce')} value={boostReduceText(attributes.earthDamageBonusPoints, attributes.earthDamageReductionPoints)} tone="bronze" tooltip={t('insight.combat.earthBoostReduceTooltip')} />
          </div>
        </div>
      )}

      {activeGroup === 'drops' && drops !== undefined && (
        drops.length > 0 ? (
          <div className="world-monster-drops-row">
            {drops.map(drop => (
              <ItemDropReference drop={drop} key={drop.itemId} />
            ))}
          </div>
        ) : (
          <p className="nearby-empty">{t('insight.combat.dropsEmpty')}</p>
        )
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
