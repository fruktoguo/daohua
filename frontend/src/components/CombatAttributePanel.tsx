import { memo, useState } from 'react'
import { PillStatLabel, type PillTone } from './PillLabel'
import { describeAbility, formatAbilityCategory, formatNumber } from '../game/format'
import { PercentValue } from '../game/combatConstants'
import { useGameStore } from '../game/gameStore'
import {
  findAbilityInCatalog,
  findEquipmentInCatalog,
  findItemInCatalog,
  resolveItemCategoryLabel,
  resolveItemMetadata,
  resolveItemSubCategory,
  withItemGrade,
} from '../game/ids'
import { resolveItemIconUrl } from '../game/itemIcons'
import type {
  Buff,
  CombatAbilitySlot,
  CombatAttributes,
  EquippedItem,
  MonsterDropDefinition,
} from '../game/types'
import { useT } from '../i18n'
import { ConditionedTile } from './ConditionedTile'
import { resolveAbilityAsideCards } from './EffectTooltip'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { BonusAsideList, ItemDropReference, ItemTile, ItemTooltipTarget, extractBuffBonusItems } from './ItemDisplay'
import { StatGlyph } from './StatGlyph'

/** 斗法属性面板分组 Tab 标识：基础战斗属性 | 特殊与增减伤属性 | 功法与装备装配 | 掉落明细（仅敌怪详情） */
type CombatAttributeGroupTab = 'base' | 'special' | 'loadout' | 'drops'

/**
 * 单位装配视图（详情弹窗「装配」Tab 的数据载荷）：
 * 四类目标同构——玩家取本地 combat 装配、敌怪取静态目录功法槽、
 * 盟友/斗法镜像取开打定格的 UnitInspect 一次性投影（与属性面板同一交付窗口）。
 */
export interface UnitLoadoutView {
  /** 功法槽位（CombatAbilitySlot 形状：怪物为配表施放等级、玩家/镜像为参悟等级；null abilityId 为空槽不渲染） */
  skills?: readonly CombatAbilitySlot[] | null
  /** 已穿戴装备清单；未提供时装备分组不渲染（怪物等无装备概念的单位） */
  equipment?: readonly EquippedItem[] | null
}

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
 * 敌怪详情额外提供 drops（静态配表 CombatantDefinition.drops）时追加第四个「掉落」Tab；
 * 提供 loadout 时追加「装配」Tab——功法瓦片悬浮详情与功法面板同构、装备瓦片与背包同构，
 * 怪物等无装备概念的单位只展示功法分组。
 */
export const CombatAttributePanel = memo(function CombatAttributePanel({
  attributes,
  buffs = [],
  drops,
  loadout,
  activeGroup: controlledActiveGroup,
  onGroupChange,
  hideTabs = false,
}: {
  /** 服务端权威战斗属性终值（玩家 combat.attributes / 敌怪 attributes / 斗法镜像 opponentAttributes 同一形状） */
  attributes: CombatAttributes
  buffs?: readonly Buff[]
  /** 敌怪掉落条目（仅战场敌怪详情提供；undefined = 不渲染掉落 Tab） */
  drops?: readonly MonsterDropDefinition[]
  /** 单位装配（功法槽 + 已穿戴装备）；null/undefined = 不渲染装配 Tab */
  loadout?: UnitLoadoutView | null
  activeGroup?: CombatAttributeGroupTab
  onGroupChange?: (group: CombatAttributeGroupTab) => void
  hideTabs?: boolean
}): React.JSX.Element {
   const t = useT()
   const [internalGroup, setInternalGroup] = useState<CombatAttributeGroupTab>('base')
  const activeGroup = controlledActiveGroup ?? internalGroup
  const setActiveGroup = (group: CombatAttributeGroupTab) => {
    setInternalGroup(group)
    onGroupChange?.(group)
  }
  // 点数制属性按双曲线渲染有效百分比：增伤类 Linear（+x 点 = +x%），减伤/速率类 NonLinear。
  const linearText = (points: number) => PercentValue.linear(points).format()
  const nonLinearText = (points: number) => PercentValue.nonLinear(points).format()
  const pointsText = (points: number) => formatNumber(Math.floor(points))
  // 暴击伤害点数折算倍率展示：倍率 = 1+点数/100（50 点即 150%）。
  const critDamageText = (points: number) => `${formatNumber(Math.round(points + 100))}%`
  // 增减伤对：[增伤 Linear 点数, 减伤 NonLinear 点数] → "+x% / y%"。
  const boostReduceText = (bonus: number, reduction: number) =>
    `${linearText(bonus)} / ${nonLinearText(reduction)}`
  // 固定伤害增减对：[固定增伤, 固定减伤] → "+x / y"。
  const fixedBoostReduceText = (bonus: number, reduction: number) => {
    const b = Math.round(bonus)
    const r = Math.round(reduction)
    const bonusPrefix = b >= 0 ? '+' : ''
    return `${bonusPrefix}${formatNumber(b)} / ${formatNumber(r)}`
  }
  const fixedBoostReducePill = (key: string, bonus: number, reduction: number, tone: PillTone) =>
    pill(key, fixedBoostReduceText(bonus, reduction), tone,
      !atDefault(bonus) || !atDefault(reduction))

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
    [fixedBoostReducePill('fixedTotalBoostReduce', attributes.fixedDamageBonus, attributes.fixedDamageReduction, 'emerald'),
      fixedBoostReducePill('fixedPhysBoostReduce', attributes.fixedPhysicalDamageBonus, attributes.fixedPhysicalDamageReduction, 'crimson'),
      fixedBoostReducePill('fixedSpellBoostReduce', attributes.fixedSpellDamageBonus, attributes.fixedSpellDamageReduction, 'indigo')],
    [boostReducePill('metalBoostReduce', attributes.metalDamageBonusPoints, attributes.metalDamageReductionPoints, 'gold'),
      boostReducePill('woodBoostReduce', attributes.woodDamageBonusPoints, attributes.woodDamageReductionPoints, 'emerald'),
      boostReducePill('waterBoostReduce', attributes.waterDamageBonusPoints, attributes.waterDamageReductionPoints, 'cyan'),
      boostReducePill('fireBoostReduce', attributes.fireDamageBonusPoints, attributes.fireDamageReductionPoints, 'amber'),
      boostReducePill('earthBoostReduce', attributes.earthDamageBonusPoints, attributes.earthDamageReductionPoints, 'bronze')],
  ]
  const ATTR_GLYPH_MAP: Record<string, string> = {
    hp: 'hp',
    mp: 'mp',
    hpRegen: 'hp-regen',
    mpRegen: 'mp-regen',
    physAtk: 'phys-atk',
    spellAtk: 'spell-atk',
    armor: 'armor',
    magicRes: 'magic-res',
    accuracy: 'accuracy',
    evasion: 'evasion',
    crit: 'crit',
    critAvoid: 'crit-avoid',
    parryBreak: 'parry-break',
    parryResolve: 'parry-resolve',
    attackSpeed: 'attack-speed',
    actionSpeed: 'action-speed',
    cooldownReduction: 'cooldown-reduction',
    critDamage: 'crit-damage',
    basicAttackBonus: 'basic-attack-bonus',
    skillBonus: 'skill-bonus',
    threatBonus: 'threat-bonus',
    healingBonus: 'healing-bonus',
    hpDrain: 'hp-drain',
    mpDrain: 'mp-drain',
    damageReflect: 'damage-reflect',
    totalBoostReduce: 'total-damage',
    physBoostReduce: 'phys-damage',
    spellBoostReduce: 'spell-damage',
    fixedTotalBoostReduce: 'total-damage',
    fixedPhysBoostReduce: 'phys-damage',
    fixedSpellBoostReduce: 'spell-damage',
    metalBoostReduce: 'element-metal',
    woodBoostReduce: 'element-wood',
    waterBoostReduce: 'element-water',
    fireBoostReduce: 'element-fire',
    earthBoostReduce: 'element-earth',
  }

  const renderPill = (p: AttrPill) => (
    <PillStatLabel
      key={p.key}
      icon={ATTR_GLYPH_MAP[p.key] ? <StatGlyph glyph={ATTR_GLYPH_MAP[p.key]} size={16.5} /> : undefined}
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
      {!hideTabs && (
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
        {loadout != null && (
          <button
            className={`insight-sub-tab-btn ${activeGroup === 'loadout' ? 'active' : ''}`}
            onClick={() => setActiveGroup('loadout')}
            type="button"
          >
            {t('insight.combat.groupLoadout')}
          </button>
        )}
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
      )}
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

      {activeGroup === 'loadout' && loadout != null && (
        <UnitLoadoutSection loadout={loadout} />
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
              const bonusItems = extractBuffBonusItems(buff)
              return (
                <FloatingPopover
                  content={
                    <FloatingInfoCard
                      badge={buff.stacks > 1 ? `×${formatNumber(buff.stacks)}` : undefined}
                      badgeTone="gold"
                      description={bonusItems.length > 0 ? <BonusAsideList items={bonusItems} /> : undefined}
                      title={buff.name}
                    />
                  }
                  interactive={false}
                  key={buff.buffId}
                  openDelay={60}
                  placement="top"
                  size="small"
                >
                  <PillStatLabel
                    label={buff.name}
                    tone="gold"
                    value={`×${formatNumber(buff.stacks)}`}
                  />
                </FloatingPopover>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
})

/**
 * 单位装配只读视图（详情弹窗「装配」Tab 内容区）：
 * 功法瓦片与已学功法目录同构（元素丝带 + Lv 角标 + 图标/名 + 悬浮详情卡）；
 * 装备瓦片与装备槽/背包同构（ItemTile + ItemTooltipTarget 详情卡）；
 * 纯展示不带装配交互——槽位编辑只在功法面板、穿脱只在装备面板发生。
 * 两组均为空时回退空态文案；equipment 未提供的单位（怪物）整组不渲染。
 */
const UnitLoadoutSection = memo(function UnitLoadoutSection({
  loadout,
}: {
  loadout: UnitLoadoutView
}): React.JSX.Element {
  const t = useT()
  const abilities = useGameStore(state => state.abilities)
  const equipmentCatalog = useGameStore(state => state.equipment)
   const itemCatalog = useGameStore(state => state.items)
  const abilityExperiences = useGameStore(state => state.combat?.abilityExperiences)
  const skills = (loadout.skills ?? []).filter(
    (slot): slot is CombatAbilitySlot & { abilityId: string } => slot.abilityId !== null && slot.abilityId.length > 0,
  )
  const equipment = loadout.equipment ?? []

  if (skills.length === 0 && equipment.length === 0) {
    return <p className="nearby-empty">{t('insight.combat.loadoutEmpty')}</p>
  }

  return (
    <div className="insight-base-layout">
      {skills.length > 0 && (
        <div className="insight-pill-group">
          <h4 className="insight-pill-group-title">{t('insight.combat.loadoutAbilities')}</h4>
          <div className="abilities-learned-grid">
            {skills.map(slot => {
              const ability = findAbilityInCatalog(abilities, slot.abilityId)
              const iconUrl = resolveItemIconUrl(slot.abilityId)
              const level = Math.max(1, Math.floor(slot.level))
              // 丝带文案与已学功法目录同口径：非无属性优先元素名，否则按功法大类。
              const ribbonLabel = ability?.element && ability.element !== 'none'
                ? t(`combat.encounter.element.${ability.element}`)
                : formatAbilityCategory(ability?.category)
              const abilityExp = slot.abilityId ? abilityExperiences?.[slot.abilityId] : undefined
              const asideCards = ability && abilityExp !== undefined
                ? resolveAbilityAsideCards(ability, abilityExp)
                : undefined
              return (
                <ConditionedTile
                  asideCards={asideCards}
                  className="abilities-learned-cell"
                  content={ability ? describeAbility(ability, level) : null}
                  key={slot.abilityId}
                  triggerClassName="item-grid-picker-tooltip"
                >
                  <span
                    className="item-tile combat-ability-cell"
                    data-rarity={ability?.discipline === 'internal' ? 'emperor' : 'mystic'}
                  >
                    <span aria-hidden="true" className="item-type-ribbon"><span>{ribbonLabel}</span></span>
                    <span className="item-corner item-corner--top-right">Lv. {level}</span>
                    {iconUrl
                      ? <img alt={ability?.name ?? slot.abilityId} className="item-tile-icon" decoding="async" draggable={false} src={iconUrl} />
                      : <strong className="item-tile-name">{ability?.name ?? slot.abilityId}</strong>}
                    <span aria-hidden="true" className="item-tile-glow" />
                  </span>
                </ConditionedTile>
              )
            })}
          </div>
        </div>
      )}
      {equipment.length > 0 && (
        <div className="insight-pill-group">
          <h4 className="insight-pill-group-title">{t('insight.combat.loadoutEquipment')}</h4>
          <div className="abilities-learned-grid">
            {equipment.map(item => {
              const equipmentDef = findEquipmentInCatalog(equipmentCatalog, item.itemId)
              const itemDef = findItemInCatalog(itemCatalog, item.itemId)
              const meta = resolveItemMetadata(item.itemId)
              // 元数据/等阶解析与装备槽位卡同口径：品质档按等阶覆盖，缺配表时回退物品兜底。
              const metadata = withItemGrade(
                meta ?? {
                  label: equipmentDef?.name ?? item.itemId,
                  glyph: '⚔️',
                  tone: 'gray',
                  category: resolveItemCategoryLabel(equipmentDef?.slot ?? 'equipment', t),
                  rarity: 'mundane',
                  rarityLabel: '',
                  description: equipmentDef?.description ?? itemDef?.description ?? '',
                  source: '',
                  usage: '',
                },
                equipmentDef?.tier ?? meta?.tier ?? 1,
              )
              const enhancementLevel = Math.max(0, Math.floor(item.enhancementLevel))
              const baseName = equipmentDef?.name ?? itemDef?.name ?? item.itemId
              const displayName = enhancementLevel > 0 ? `${baseName} +${enhancementLevel}` : baseName
              return (
                <ItemTooltipTarget
                  definition={itemDef}
                  enhancementLevel={enhancementLevel}
                  equipmentDefinition={equipmentDef}
                  itemId={item.itemId}
                  key={item.itemId}
                  metadata={metadata}
                >
                  <ItemTile
                    category={resolveItemCategoryLabel(
                      resolveItemSubCategory(item.itemId, itemDef, equipmentDef), t)}
                    className="inventory-cell"
                    enhancementLevel={enhancementLevel}
                    itemId={item.itemId}
                    level={metadata.level}
                    name={displayName}
                    rarity={metadata.rarity}
                  />
                </ItemTooltipTarget>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
})
