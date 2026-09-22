import { memo, useState } from 'react'
import { PillStatLabel } from './PillLabel'
import { formatNumber } from '../game/format'
import { PercentValue } from '../game/combatConstants'
import type { Buff, CombatAttributes } from '../game/types'
import { useT } from '../i18n'

/** 斗法属性面板分组 Tab 标识：基础战斗属性 | 特殊战斗属性 | 五行抗性 */
type CombatAttributeGroupTab = 'base' | 'special' | 'elements'

/**
 * 斗法战斗属性统一面板 (CombatAttributePanel)
 *
 * 内视「斗法」Tab 与战场单位详情弹窗（角色/敌怪/斗法镜像）共用：
 * 只渲染服务端白名单下发的 CombatAttributes 终值，不做任何本地估算或配表反查；
 * 基础战斗属性、特殊战斗属性、五行抗性三组以 Tab 切换展示，
 * 以及可选的当前运转丹药与神通加持 Buff 分区。
 */
export const CombatAttributePanel = memo(function CombatAttributePanel({
  attributes,
  buffs = [],
}: {
  /** 服务端权威战斗属性终值（玩家 combat.attributes / 敌怪 attributes / 斗法镜像 opponentAttributes 同一形状） */
  attributes: CombatAttributes
  buffs?: readonly Buff[]
}): React.JSX.Element {
  const t = useT()
  const [activeGroup, setActiveGroup] = useState<CombatAttributeGroupTab>('base')

  // 比率型字段统一以 PercentValue 线性百分比渲染（内部均为 double 比率原始值）
  const ratioText = (ratio: number) => PercentValue.linear(ratio * 100.0).format()
  const intervalText = `${formatNumber(attributes.attackIntervalMilliseconds / 1000.0)}s`

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

          {/* 第 3 排：博弈对抗 (命中/闪避、暴击率/免暴率、破招/化解) */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.accuracy')} value={Math.floor(attributes.accuracy)} tone="sky" tooltip={t('insight.combat.accuracyTooltip')} />
            <PillStatLabel label={t('insight.combat.evasion')} value={Math.floor(attributes.evasion)} tone="teal" tooltip={t('insight.combat.evasionTooltip')} />
            <PillStatLabel label={t('insight.combat.crit')} value={ratioText(attributes.criticalChance)} tone="gold" tooltip={t('insight.combat.critTooltip')} />
            <PillStatLabel label={t('insight.combat.critAvoid')} value={ratioText(attributes.criticalAvoidance)} tone="slate" tooltip={t('insight.combat.critAvoidTooltip')} />
            <PillStatLabel label={t('insight.combat.parryBreak')} value={Math.floor(attributes.breakPower)} tone="amber" tooltip={t('insight.combat.parryBreakTooltip')} />
            <PillStatLabel label={t('insight.combat.parryResolve')} value={Math.floor(attributes.parryResolve)} tone="silver" tooltip={t('insight.combat.parryResolveTooltip')} />
          </div>
        </div>
      )}

      {activeGroup === 'special' && (
        <div className="insight-base-layout">
          {/* 第 1 排：出手时序与功法乘区 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.critDamage')} value={ratioText(attributes.criticalDamageRatio)} tone="gold" tooltip={t('insight.combat.critDamageTooltip')} />
            <PillStatLabel label={t('insight.combat.attackInterval')} value={intervalText} tone="teal" tooltip={t('insight.combat.attackIntervalTooltip')} />
            <PillStatLabel label={t('insight.combat.cooldownFactor')} value={ratioText(attributes.cooldownFactor)} tone="cyan" tooltip={t('insight.combat.cooldownFactorTooltip')} />
            <PillStatLabel label={t('insight.combat.castTimeFactor')} value={ratioText(attributes.castTimeFactor)} tone="cyan" tooltip={t('insight.combat.castTimeFactorTooltip')} />
          </div>

          {/* 第 2 排：伤害强度、减伤与仇恨 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.basicAttackRatio')} value={ratioText(attributes.basicAttackRatio)} tone="crimson" tooltip={t('insight.combat.basicAttackRatioTooltip')} />
            <PillStatLabel label={t('insight.combat.skillIntensity')} value={ratioText(attributes.skillIntensity)} tone="indigo" tooltip={t('insight.combat.skillIntensityTooltip')} />
            <PillStatLabel label={t('insight.combat.damageReduction')} value={ratioText(attributes.damageAbsorptionRatio)} tone="emerald" tooltip={t('insight.combat.damageReductionTooltip')} />
            <PillStatLabel label={t('insight.combat.threatBonus')} value={ratioText(attributes.threatRatio)} tone="crimson" tooltip={t('insight.combat.threatBonusTooltip')} />
          </div>

          {/* 第 3 排：汲取、反弹与治疗增益 */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.hpDrain')} value={ratioText(attributes.hpDrainRatio)} tone="emerald" tooltip={t('insight.combat.hpDrainTooltip')} />
            <PillStatLabel label={t('insight.combat.mpDrain')} value={ratioText(attributes.mpDrainRatio)} tone="cyan" tooltip={t('insight.combat.mpDrainTooltip')} />
            <PillStatLabel label={t('insight.combat.damageReflect')} value={ratioText(attributes.damageReflectRatio)} tone="bronze" tooltip={t('insight.combat.damageReflectTooltip')} />
            <PillStatLabel label={t('insight.combat.healingBonus')} value={ratioText(attributes.healingBonusRatio)} tone="violet" tooltip={t('insight.combat.healingBonusTooltip')} />
          </div>
        </div>
      )}

      {activeGroup === 'elements' && (
        <div className="insight-base-layout">
          {/* 五行抗性（服务端下发点数终值） */}
          <div className="insight-base-row">
            <PillStatLabel label={t('insight.combat.metalRes')} value={Math.floor(attributes.metalResistance)} tone="gold" tooltip={t('insight.combat.metalResTooltip')} />
            <PillStatLabel label={t('insight.combat.woodRes')} value={Math.floor(attributes.woodResistance)} tone="emerald" tooltip={t('insight.combat.woodResTooltip')} />
            <PillStatLabel label={t('insight.combat.waterRes')} value={Math.floor(attributes.waterResistance)} tone="cyan" tooltip={t('insight.combat.waterResTooltip')} />
            <PillStatLabel label={t('insight.combat.fireRes')} value={Math.floor(attributes.fireResistance)} tone="amber" tooltip={t('insight.combat.fireResTooltip')} />
            <PillStatLabel label={t('insight.combat.earthRes')} value={Math.floor(attributes.earthResistance)} tone="bronze" tooltip={t('insight.combat.earthResTooltip')} />
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
