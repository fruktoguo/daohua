import { memo, useState } from 'react'
import { PillStatLabel, type PillTone } from './PillLabel'
import { formatNumber } from '../game/format'
import { PercentValue } from '../game/combatConstants'
import type { Buff, CombatAttributes, MonsterDropDefinition } from '../game/types'
import { useT } from '../i18n'
import { describeBuffEffectLines, ItemDropReference } from './ItemDisplay'

/** 斗法属性面板分组 Tab 标识：基础战斗属性 | 特殊与增减伤属性 | 掉落明细（仅敌怪详情） */
type CombatAttributeGroupTab = 'base' | 'special' | 'drops'

/**
 * 面板属性条目：文案按 `insight.combat.{key}` / `insight.combat.{key}Tooltip` 词条约定取。
 * `atDefault` 标记等于默认值的条目——点数类修饰默认 0 点、基础数值默认 0；
 * 默认条目不渲染，唯暴击伤害豁免恒显（基础自带 50 点 = 150%，属固有战斗特征）。
 */
interface AttrPill {
  key: string
  value: string | number
  tone: PillTone
  atDefault: boolean
}

/** 默认值判定容差：吸收点数求和的双精度残差。 */
const DEFAULT_EPSILON = 1e-9

/**
 * 斗法战斗属性统一面板 (CombatAttributePanel)
 *
 * 内视「斗法」Tab 与战场单位详情弹窗（角色/敌怪/斗法镜像）共用：
 * 只渲染服务端下发的 CombatAttributes 终值，不做任何本地估算或配表反查；
 * 「基础」「特殊」两组以 Tab 切换，增减伤十六项整组收在「特殊」最下面，
 * 全部条目按默认值隐藏（点数 0 / 数值 0 不显示，暴击伤害恒显）。
 * 以及可选的增益（Buff）分区：条目平铺「名称×层数」，悬浮气泡展示具体效果行。
 * 敌怪详情额外提供 drops（静态配表 CombatantDefinition.drops）时追加第三个「掉落」Tab。
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
  // 暴击伤害点数折算倍率展示：倍率 = 1+点数/100（50 点即 150%）。
  const critDamageText = (points: number) => `${formatNumber(Math.round(points + 100))}%`
  // 增减伤对：[增伤 Linear 点数, 减伤 NonLinear 点数] → "+x% / y%"。
  const boostReduceText = (bonus: number, reduction: number) =>
    `${linearText(bonus)} / ${nonLinearText(reduction)}`

  // 默认值隐藏判定：点数类修饰与基础数值默认 0；增减伤对两侧皆默认才隐藏。
  const atDefault = (points: number) => Math.abs(points) < DEFAULT_EPSILON
  const pill = (key: string, value: string | number, tone: PillTone, visible = true): AttrPill =>
    ({ key, value, tone, atDefault: !visible })
  const rawPill = (key: string, raw: number, tone: PillTone) =>
    pill(key, Math.floor(raw), tone, !atDefault(raw))
  const pointsPill = (key: string, points: number, tone: PillTone) =>
    pill(key, pointsText(points), tone, !atDefault(points))
  const linearPill = (key: string, points: number, tone: PillTone) =>
    pill(key, linearText(points), tone, !atDefault(points))
  const nonLinearPill = (key: string, points: number, tone: PillTone) =>
    pill(key, nonLinearText(points), tone, !atDefault(points))
  const boostReducePill = (key: string, bonus: number, reduction: number, tone: PillTone) =>
    pill(key, boostReduceText(bonus, reduction), tone,
      !atDefault(bonus) || !atDefault(reduction))

  // 基础组：气血/灵力/回复 → 攻防 → 博弈对抗点数
  const baseRows: AttrPill[][] = [
    [rawPill('hp', attributes.maxHitpoints, 'emerald'),
      rawPill('mp', attributes.maxManapoints, 'cyan'),
      rawPill('hpRegen', attributes.hpRegenPerFiveSeconds, 'emerald'),
      rawPill('mpRegen', attributes.mpRegenPerFiveSeconds, 'cyan')],
    [rawPill('physAtk', attributes.physicalAttack, 'crimson'),
      rawPill('spellAtk', attributes.spellAttack, 'indigo'),
      rawPill('armor', attributes.armor, 'bronze'),
      rawPill('magicRes', attributes.magicResistance, 'violet')],
    [pointsPill('accuracy', attributes.accuracy, 'sky'),
      pointsPill('evasion', attributes.evasion, 'teal'),
      pointsPill('crit', attributes.critical, 'gold'),
      pointsPill('critAvoid', attributes.criticalResistance, 'slate'),
      pointsPill('parryBreak', attributes.breakPower, 'amber'),
      pointsPill('parryResolve', attributes.parryResolve, 'silver')],
  ]

  // 特殊组：时序速率 → 伤害增幅（暴击伤害恒显）→ 治疗/汲取/反弹
  const specialRows: AttrPill[][] = [
    [nonLinearPill('attackSpeed', attributes.attackSpeedPoints, 'teal'),
      nonLinearPill('actionSpeed', attributes.actionSpeedPoints, 'teal'),
      nonLinearPill('cooldownReduction', attributes.cooldownReductionPoints, 'cyan')],
    [pill('critDamage', critDamageText(attributes.criticalDamagePoints), 'gold'),
      linearPill('basicAttackBonus', attributes.basicAttackBonusPoints, 'crimson'),
      linearPill('skillBonus', attributes.skillBonusPoints, 'indigo'),
      linearPill('threatBonus', attributes.threatBonusPoints, 'crimson')],
    [linearPill('healingBonus', attributes.healingBonusPoints, 'violet'),
      linearPill('hpDrain', attributes.hpDrainPoints, 'emerald'),
      linearPill('mpDrain', attributes.mpDrainPoints, 'cyan'),
      linearPill('damageReflect', attributes.damageReflectPoints, 'bronze')],
  ]

  // 伤害增减整组收在「特殊」最下面：大类增减对 + 五行增减对（增伤 Linear / 减伤 NonLinear）
  const boostReduceRows: AttrPill[][] = [
    [boostReducePill('totalBoostReduce', attributes.totalDamageBonusPoints, attributes.damageAbsorptionPoints, 'emerald'),
      boostReducePill('physBoostReduce', attributes.physicalDamageBonusPoints, attributes.physicalDamageReductionPoints, 'crimson'),
      boostReducePill('spellBoostReduce', attributes.spellDamageBonusPoints, attributes.spellDamageReductionPoints, 'indigo')],
    [boostReducePill('metalBoostReduce', attributes.metalDamageBonusPoints, attributes.metalDamageReductionPoints, 'gold'),
      boostReducePill('woodBoostReduce', attributes.woodDamageBonusPoints, attributes.woodDamageReductionPoints, 'emerald'),
      boostReducePill('waterBoostReduce', attributes.waterDamageBonusPoints, attributes.waterDamageReductionPoints, 'cyan'),
      boostReducePill('fireBoostReduce', attributes.fireDamageBonusPoints, attributes.fireDamageReductionPoints, 'amber'),
      boostReducePill('earthBoostReduce', attributes.earthDamageBonusPoints, attributes.earthDamageReductionPoints, 'bronze')],
  ]

  const renderPill = (p: AttrPill) => (
    <PillStatLabel
      key={p.key}
      label={t(`insight.combat.${p.key}`)}
      value={p.value}
      tone={p.tone}
      tooltip={t(`insight.combat.${p.key}Tooltip`)}
    />
  )
  const renderRows = (rows: AttrPill[][], rowClass: string) =>
    rows
      .map(row => row.filter(p => !p.atDefault))
      .filter(row => row.length > 0)
      .map((row, index) => (
        <div className={rowClass} key={index}>
          {row.map(renderPill)}
        </div>
      ))

  return (
    <div className="combat-attribute-panel">
      {/* 属性分组 Tab 切换（复用内视根基/坊市同款二级导航样式） */}
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
          {renderRows(baseRows, 'insight-base-row')}
        </div>
      )}

      {activeGroup === 'special' && (
        <div className="insight-base-layout">
          {renderRows(specialRows, 'insight-base-row')}
          {/* 伤害增减整组：收在「特殊」最下面，全部条目默认时整组隐藏 */}
          {boostReduceRows.some(row => row.some(p => !p.atDefault)) && (
            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.combat.groupElements')}</h4>
              {renderRows(boostReduceRows, 'insight-pill-group-items')}
            </div>
          )}
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

      {/* 生效中的增益 Buff：条目平铺「名称×层数」，悬浮气泡展示具体效果行（与坊市/战斗场景同一解析口径） */}
      {buffs.length > 0 && (
        <div className="insight-pill-group">
          <h4 className="insight-pill-group-title">{t('insight.combat.groupBuffs')}</h4>
          <div className="insight-pill-group-items">
            {buffs.map(buff => {
              const effectLines = describeBuffEffectLines(buff)
              return (
                <PillStatLabel
                  key={buff.buffId}
                  label={buff.name}
                  value={`×${formatNumber(buff.stacks)}`}
                  tone="gold"
                  tooltip={effectLines.length > 0 ? effectLines.join('；') : undefined}
                />
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
})
