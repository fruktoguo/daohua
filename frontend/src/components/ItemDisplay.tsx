import { memo, type ReactNode, useCallback } from 'react'
import { describeAbility, formatAbilityCategory, formatInteger, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { convertTierToLevel, findAbilityInCatalog, findEquipmentInCatalog, findItemInCatalog, parseEnhancementVariant, resolveDefaultEquipmentDefinition, resolveDefaultItemDefinition, resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, resolveItemSubCategory, withItemGrade, type ItemMetadata, type ItemRarity } from '../game/ids'
import { resolveItemIconUrl } from '../game/itemIcons'
import { getT, useT } from '../i18n'
import { marketStatsKey } from '../network/apiClient'
import type { Buff, CombatAbilityDefinition, CombatEquipmentDefinition, InventoryItem, ItemDefinition, MonsterDropDefinition } from '../game/types'
import { type FloatingAsideCard, FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import { PillBonusLabel, type PillTone } from './PillLabel'
import { UnitNumber } from './UnitNumber'
import { ValueText } from './ValueText'
import { RootAspectRadarChart } from './RootAspectRadarChart'
import { StatGlyph, resolveStatGlyph } from './StatGlyph'
import { createInternalRootExpAsideCard } from './EffectTooltip'

/** 物品悬浮提示气泡触发器 Props */
export interface ItemTooltipTargetProps {
  /** 物品唯一 ID */
  itemId: string
  /** 拥有/涉及数量 */
  amount?: number
  /** 被包裹触发子元素 */
  children: ReactNode
  /** 触发器样式类名 */
  className?: string
  /** 自定义覆盖元数据 */
  metadata?: ItemMetadata
  /** 服务端原始物品定义 */
  definition?: ItemDefinition
  /** 服务端装备定义 */
  equipmentDefinition?: CombatEquipmentDefinition
  /** 装备强化等级（如 +3） */
  enhancementLevel?: number
  /** 键盘焦点 tab 索引 */
  tabIndex?: number
  /** 是否使用 CSS display: contents 消除外层包裹容器布局影响 */
  displayContents?: boolean
  /** 额外附属卡片群 */
  extraAsideCards?: readonly FloatingAsideCard[]
  /** 鼠标悬停展开延时 (ms)，默认 60ms */
  openDelay?: number
}

/** 物品网格方块 Props */
export interface ItemTileProps {
  /** 显示数量（位于右下角） */
  amount?: number
  /** 右下角自定义标签（覆盖默认数量） */
  bottomRightLabel?: ReactNode
  /** 物品品类名称（展示在左上角丝带 Ribbon） */
  category: string
  /** 装备强化等级：>0 时左下角显示 "+N" 徽标，+0 不显示 */
  enhancementLevel?: number
  /** 外部样式类名 */
  className: string
  /** 物品唯一 ID：命中图标资源时整块瓦片改用图片渲染，未命中则回退为文本名称 */
  itemId?: string
  /** 物品等阶（展示在右上角） */
  level?: number
  /** 物品名称（无图标时的兜底文本，同时作为图片替代文本） */
  name: string
  /** 稀有度品质（决定边框与背景光效） */
  rarity: ItemRarity
}

/** 未在注册表登记的物品的最小兜底元数据（凡品/金字兜底，品类走默认标签） */
function fallbackMetadata(itemId: string): ItemMetadata {
  const t = getT()
  const def = useGameStore.getState().items.find(i => i.itemId === itemId)
  const isTome = itemId.startsWith('item.tome.')
  const isFragment = itemId.startsWith('item.fragment.')
  const tier = def?.tier ?? 1
  const grade = resolveItemGrade(tier)
  return {
    label: def?.name || itemId,
    glyph: isTome ? '📘' : isFragment ? '📜' : '·',
    tone: tier >= 2 ? 'green' : 'gold',
    category: resolveItemCategoryLabel(def?.subCategory ?? (isTome ? 'manualTome' : isFragment ? 'manualFragment' : undefined), t),
    rarity: grade.rarity,
    rarityLabel: grade.label,
    tier,
    level: convertTierToLevel(tier),
    description: def?.description ?? '',
    source: '',
    usage: '',
  }
}

export function rarityToTone(rarity: ItemRarity): FloatingTone {
  switch (rarity) {
    case 'emperor':
    case 'saint':
      return 'gold'
    case 'spirit':
    case 'heaven':
      return 'purple'
    case 'earth':
      return 'qi'
    case 'mystic':
      return 'jade'
    case 'yellow':
      return 'gold'
    default:
      return 'muted'
  }
}

/** 悬浮主卡片内容渲染组件的内部 Props（override 字段优先于全局配表查询结果） */
interface ItemTooltipCardContentProps {
  itemId: string
  amount?: number
  metadataOverride?: ItemMetadata
  itemDefOverride?: ItemDefinition
  eqDefOverride?: CombatEquipmentDefinition
  enhancementLevel?: number
}

/**
 * 悬浮主卡片内容组件：聚合物品/装备定义、品质徽章、主干效果行与品类/等阶/持存键值对。
 * Buff 与特殊机制不混入主卡，统一走 Aside 副卡片（见 resolveItemTooltipAsideCards）。
 */
function ItemTooltipCardContent({
  itemId,
  amount,
  metadataOverride,
  itemDefOverride,
  eqDefOverride,
  enhancementLevel = 0,
}: ItemTooltipCardContentProps): React.JSX.Element {
  const t = useT()
  const itemCatalog = useGameStore((state) => state.items)
  const equipmentCatalog = useGameStore((state) => state.equipment)
  const abilityCatalog = useGameStore((state) => state.abilities)

  const itemDef = itemDefOverride ?? findItemInCatalog(itemCatalog, itemId) ?? resolveDefaultItemDefinition(itemId)
  const eqDef = eqDefOverride ?? findEquipmentInCatalog(equipmentCatalog, itemId) ?? resolveDefaultEquipmentDefinition(itemId)

  const baseMetadata = metadataOverride ?? resolveItemMetadata(itemId) ?? fallbackMetadata(itemId)
  // 品类展示统一走细分解析：装备=槽位、功法书=秘籍/残卷、其余=配表细分 category
  const subCategoryLabel = resolveItemCategoryLabel(
    resolveItemSubCategory(itemId, itemDef, eqDef, baseMetadata.category), t)
  const rawMetadata = itemDef
    ? {
        ...baseMetadata,
        label: itemDef.name || baseMetadata.label,
        category: subCategoryLabel,
        tier: itemDef.tier ?? baseMetadata.tier,
        level: itemDef.tier ? convertTierToLevel(itemDef.tier) : baseMetadata.level,
        description: itemDef.description || baseMetadata.description,
      }
    : eqDef
    ? {
        ...baseMetadata,
        label: eqDef.name || baseMetadata.label,
        category: subCategoryLabel,
        tier: eqDef.tier ?? baseMetadata.tier,
        level: eqDef.tier ? convertTierToLevel(eqDef.tier) : baseMetadata.level,
      }
    : baseMetadata

  const tier = rawMetadata.tier ?? (eqDef?.tier || itemDef?.tier || (rawMetadata.level && rawMetadata.level <= 8 ? rawMetadata.level : 1))
  const normalizedTier = tier > 8 ? 1 : Math.max(1, Math.floor(tier))
  const grade = resolveItemGrade(normalizedTier)
  const levelNumber = convertTierToLevel(normalizedTier)

  const metadata: ItemMetadata = {
    ...rawMetadata,
    rarity: grade.rarity,
    rarityLabel: grade.label,
    tier: normalizedTier,
    level: levelNumber,
  }

  // 主卡片只展示直接基础效果与攻防数值，Buff 与特殊机制解耦至 Aside 附属卡片群
  const effectLines = [
    ...describeItemEffects(itemDef),
    ...describeEquipmentEffects(eqDef, enhancementLevel),
    ...describeManualEffects(itemId, abilityCatalog),
  ]

  const stats: { label: string; value: ReactNode; tone?: FloatingTone }[] = [
    { label: t('market.tooltip.category'), value: resolveItemCategoryLabel(metadata.category, t), tone: 'muted' },
  ]
  if (metadata.level !== undefined) {
    stats.push({ label: t('market.tooltip.level'), value: `Lv. ${formatNumber(metadata.level)}`, tone: 'gold' })
  }
  if (amount !== undefined) {
    stats.push({
      label: t('market.tooltip.held'),
      value: <UnitNumber disableTooltip value={amount} />,
      tone: 'gold',
    })
  }

  const tone = rarityToTone(metadata.rarity)

  return (
    <FloatingInfoCard
      badge={metadata.rarityLabel}
      badgeTone={tone}
      description={metadata.description}
      lines={effectLines}
      stats={stats}
      title={enhancementLevel > 0 ? `${metadata.label} +${enhancementLevel}` : metadata.label}
      titleIcon={resolveItemIconUrl(itemId)}
    />
  )
}

/**
 * 副卡片聚合解析（惰性求值）：经 FloatingPopover 的 asideCards 函数形式传入，仅在浮层实际展开时执行。
 * 合并丹药/增益 Buff 副卡、装备特殊机制副卡、功法秘籍/残卷参悟副卡与调用方附加卡片；
 * 若物品自身 Buff 链路已产出参悟副卡（aside-manual-*），不再重复追加秘籍解析结果。
 * 可交易标的额外追加「市场价格」行情副卡：按强化等级命中对应 "+N" 变体簿统计。
 */
export function resolveItemTooltipAsideCards(
  itemId: string,
  metadataOverride?: ItemMetadata,
  itemDefOverride?: ItemDefinition,
  eqDefOverride?: CombatEquipmentDefinition,
  enhancementLevel = 0,
  extraAsideCards?: readonly FloatingAsideCard[]
): FloatingAsideCard[] {
  const store = useGameStore.getState()
  const itemCatalog = store.items
  const equipmentCatalog = store.equipment
  const abilitiesCatalog = store.abilities

  const itemDef = itemDefOverride ?? findItemInCatalog(itemCatalog, itemId) ?? resolveDefaultItemDefinition(itemId)
  const eqDef = eqDefOverride ?? findEquipmentInCatalog(equipmentCatalog, itemId) ?? resolveDefaultEquipmentDefinition(itemId)

  const baseMetadata = metadataOverride ?? resolveItemMetadata(itemId) ?? fallbackMetadata(itemId)
  const rawMetadata = itemDef
    ? {
        ...baseMetadata,
        label: itemDef.name || baseMetadata.label,
        category: itemDef.category ?? baseMetadata.category,
      }
    : eqDef
    ? {
        ...baseMetadata,
        label: eqDef.name || baseMetadata.label,
        category: eqDef.slot ?? baseMetadata.category,
      }
    : baseMetadata

  const list: FloatingAsideCard[] = []
  const itemAsides = resolveItemAsideCards(itemDef, rawMetadata.label)
  list.push(...itemAsides)
  if (!itemAsides.some(c => c.id?.startsWith('aside-manual-'))) {
    const manualAside = resolveManualAsideCard(itemId, rawMetadata.category, abilitiesCatalog)
    if (manualAside) {
      list.push(manualAside)
    }
  }
  const manualCard = list.find(c => c.id?.startsWith('aside-manual-'))
  if (manualCard?.id) {
    const abilityId = manualCard.id.replace('aside-manual-', '')
    const matchedAbility = abilitiesCatalog.find(a => a.abilityId === abilityId)
    if (matchedAbility?.discipline === 'internal') {
      list.push(createInternalRootExpAsideCard())
    }
  }
  const marketAside = resolveMarketStatsAsideCard(itemId, enhancementLevel, itemDef, eqDef, rawMetadata.label)
  if (marketAside) {
    list.push(marketAside)
  }
  if (extraAsideCards && extraAsideCards.length > 0) {
    list.push(...extraAsideCards)
  }
  return list
}

/**
 * 物品悬浮气泡包裹组件 (ItemTooltipTarget)
 *
 * 鼠标悬停展示修仙风物品卡片气泡体系：
 * 1. 【主卡片 (300px)】：展示物品/装备品质徽章、名称、背景故事描述、即时回血回蓝、冷却、装备攻防基础数值与品类/等阶；
 * 2. 【专属 Buff 附属卡片窗口 (220px)】：若为丹药附带持续 Buff，或修炼/采集/炼丹持续加成，在右侧独立展开附属面板；
 * 3. 【特殊机制附属卡片窗口 (220px)】：若装备带有特殊机制/特技，在右侧独立展开特殊机制副面板；
 * 4. 【数量副气泡】：若传入 amount，自动在右侧并排展示独立的数量副气泡。
 */
export function ItemTooltipTarget({
  itemId,
  amount,
  children,
  className = '',
  metadata: metadataOverride,
  definition: itemDefOverride,
  equipmentDefinition: eqDefOverride,
  enhancementLevel = 0,
  tabIndex = 0,
  displayContents,
  extraAsideCards,
  openDelay = 60,
}: ItemTooltipTargetProps): React.JSX.Element {
  const renderContent = useCallback(
    () => (
      <ItemTooltipCardContent
        amount={amount}
        enhancementLevel={enhancementLevel}
        eqDefOverride={eqDefOverride}
        itemDefOverride={itemDefOverride}
        itemId={itemId}
        metadataOverride={metadataOverride}
      />
    ),
    [amount, enhancementLevel, eqDefOverride, itemDefOverride, itemId, metadataOverride]
  )

  const renderAsideCards = useCallback(
    () =>
      resolveItemTooltipAsideCards(
        itemId,
        metadataOverride,
        itemDefOverride,
        eqDefOverride,
        enhancementLevel,
        extraAsideCards
      ),
    [itemId, metadataOverride, itemDefOverride, eqDefOverride, enhancementLevel, extraAsideCards]
  )

  return (
    <FloatingPopover
      asideCards={renderAsideCards}
      closeDelay={120}
      content={renderContent}
      displayContents={displayContents}
      gap={8}
      interactive={false}
      openDelay={openDelay}
      placement="bottom"
      quantity={amount}
      tabIndex={tabIndex}
      trigger="hover"
      triggerClassName={`item-tooltip-target ${className}`.trim()}
    >
      {children}
    </FloatingPopover>
  )
}

function signedPercent(value: number): string {
  const percent = Math.round(value * 100)
  return `${percent > 0 ? '+' : ''}${percent}%`
}

/**
 * 提取丹药/消耗品的直接主干效果（留在主卡片）
 * 包含即时回血、即时回蓝、冷却时长、使用条件等
 */
export function describeItemEffects(definition?: ItemDefinition): ReactNode[] {
  if (!definition) return []
  const t = getT()
  const lines: ReactNode[] = []
  if (definition.hitpointRestore > 0) {
    lines.push(
      <PillBonusLabel
        key="item-hp"
        label={t('market.effect.restoreHp')}
        tone="emerald"
        value={`+${formatNumber(definition.hitpointRestore)}`}
      />
    )
  }
  if (definition.manapointRestore > 0) {
    lines.push(
      <PillBonusLabel
        key="item-mp"
        label={t('market.effect.restoreMp')}
        tone="cyan"
        value={`+${formatNumber(definition.manapointRestore)}`}
      />
    )
  }
  // 即时打击规格（符箓）：祭出耗灵一击，伤害 = 最大灵力×manaDamageRatio + 术法攻击×spellAttackRatio
  if (definition.strike) {
    const strike = definition.strike
    lines.push(
      <PillBonusLabel
        key="item-strike"
        label={t('market.effect.strikeDamage', { element: t(`combat.encounter.element.${strike.element}`) })}
        tone="crimson"
        value={t('market.effect.strikeDamageFormula', {
          ap: formatNumber(strike.spellAttackRatio * 100, { maxDecimals: 0 }),
          mana: formatNumber(strike.manaDamageRatio * 100, { maxDecimals: 0 }),
        })}
      />,
      <PillBonusLabel
        key="item-strike-cost"
        label={t('market.effect.strikeCost')}
        tone="cyan"
        value={`${formatNumber(strike.manaCostRatio * 100, { maxDecimals: 1 })}%`}
      />,
    )
  }
  // 耐久型持续消耗品（阵盘）：部署后槽位持有即生效，不消耗不耗竭
  if (definition.durable) {
    lines.push(
      <PillBonusLabel
        key="item-durable"
        label={t('market.effect.durableTag')}
        tone="gold"
        value="∞"
      />
    )
  }
  if (definition.isContinuous && definition.durationSeconds && definition.durationSeconds > 0) {
    lines.push(
      <PillBonusLabel
        key="item-duration"
        label={t('market.effect.recoveryDuration')}
        tone="teal"
        value={`${formatNumber(definition.durationSeconds)}s`}
      />
    )
  }
  if (definition.cooldownSeconds > 0) {
    lines.push(
      <PillBonusLabel
        key="item-cd"
        label={t('market.effect.cooldown')}
        tone="slate"
        value={`${formatNumber(definition.cooldownSeconds)}s`}
      />
    )
  }
  return lines
}

/**
 * 功法秘籍/残卷的研读提示行：完本可修习对应功法，残卷须先习得后研读，两者均产出功法经验。
 * 功法名由物品 ID 后缀（item.tome|fragment.<slug> → ability.<slug>）在功法目录中解析。
 */
function describeManualEffects(
  itemId: string,
  abilities: readonly CombatAbilityDefinition[],
): ReactNode[] {
  const isTome = itemId.startsWith('item.tome.')
  const isFragment = itemId.startsWith('item.fragment.')
  if (!isTome && !isFragment) return []
  const t = getT()
  const slug = itemId.slice(isTome ? 'item.tome.'.length : 'item.fragment.'.length)
  const allAbilities = abilities.length > 0 ? abilities : useGameStore.getState().abilities
  const itemDef = useGameStore.getState().items.find(i => i.itemId === itemId)
  const fallbackName = itemDef?.name ? itemDef.name.replace(/^《|秘籍》|·残卷》|》$/g, '') : slug
  const abilityName = findAbilityInCatalog(allAbilities, `ability.${slug}`)?.name ?? fallbackName
  const bookTitle = `《${abilityName}》`
  if (isTome) {
    return [
      <PillBonusLabel
        key="manual-learn"
        label={t('market.effect.manualLearn')}
        tone="gold"
        value={bookTitle}
      />,
      <PillBonusLabel
        key="manual-exp"
        label={t('market.effect.manualTomeExp')}
        tone="emerald"
        value={t('market.effect.manualExpValue', { n: formatNumber(10_000) })}
      />,
    ]
  }
  return [
    <PillBonusLabel
      key="manual-require"
      label={t('market.effect.manualRequire')}
      tone="crimson"
      value={t('market.effect.manualRequireValue', { name: bookTitle })}
    />,
    <PillBonusLabel
      key="manual-exp"
      label={t('market.effect.manualFragmentExp')}
      tone="emerald"
      value={t('market.effect.manualExpValue', { n: formatNumber(500) })}
    />,
  ]
}

/** statRatios 通用属性比率字典 key -> i18n 词条键；未登记的新属性回退原始 wire key */
const STAT_RATIO_LABEL_KEYS: Record<string, string> = {
  maxHitpoints: 'market.stat.maxHitpoints',
  maxManapoints: 'market.stat.maxManapoints',
  physicalAttack: 'market.stat.physicalAttack',
  spellAttack: 'market.stat.spellAttack',
  physicalArmor: 'market.stat.physicalArmor',
  spellResistance: 'market.stat.spellResistance',
  accuracy: 'market.stat.accuracy',
  evasion: 'market.stat.evasion',
  criticalChance: 'market.stat.critChance',
  criticalAvoidance: 'market.stat.critAvoidance',
  break: 'market.stat.breakPower',
  parry: 'market.stat.parryResolve',
  hitpointRegen: 'market.stat.hitpointRegen',
  manapointRegen: 'market.stat.manapointRegen',
}

/**
 * 通用 Buff 数值效果行解析器：唯一 Buff 形状（hpRegen/mpRegen/attackSpeedPoints/
 * damageAbsorptionPoints/statRatios）解析为格式化词条；点数键按 NonLinear 曲线换算有效值。
 */
export function describeBuffEffectLines(buff: {
  buffId?: string | null
  name?: string
  description?: string
  hpRegen?: number
  mpRegen?: number
  hpRegenPostRatio?: number
  mpRegenPostRatio?: number
  damageAbsorptionPoints?: number
  attackSpeedPoints?: number
  statRatios?: Record<string, number> | null
  threatPoints?: number
  damageBonusPoints?: number
  threatPerSecondMaxHpPoints?: number
}): string[] {
  const t = getT()
  const lines: string[] = []

  // 1. 每 5 秒结算点回复
  if (buff.hpRegen && buff.hpRegen !== 0) {
    lines.push(t('market.effect.hpRegenPerFiveSeconds', { n: formatNumber(buff.hpRegen) }))
  }
  if (buff.mpRegen && buff.mpRegen !== 0) {
    lines.push(t('market.effect.mpRegenPerFiveSeconds', { n: formatNumber(buff.mpRegen) }))
  }

  // 1b. 回复后段乘区（生效值 = 基础回复 ×(1+Σ比率)，阵盘等 durable 增益走此区）
  if (buff.hpRegenPostRatio && buff.hpRegenPostRatio !== 0) {
    lines.push(t('market.effect.hpRegenPostRatio', { n: signedPercent(buff.hpRegenPostRatio) }))
  }
  if (buff.mpRegenPostRatio && buff.mpRegenPostRatio !== 0) {
    lines.push(t('market.effect.mpRegenPostRatio', { n: signedPercent(buff.mpRegenPostRatio) }))
  }

  // 2. 免伤与攻速点数：NonLinear 口径换算有效百分比（减伤 x/(100+x)，速率 +x%）。
  if (buff.damageAbsorptionPoints && buff.damageAbsorptionPoints !== 0) {
    const points = buff.damageAbsorptionPoints
    lines.push(t('market.effect.damageReductionPct', { n: +(points / (100 + points) * 100).toFixed(1) }))
  }
  if (buff.attackSpeedPoints && buff.attackSpeedPoints !== 0) {
    lines.push(`${t('market.stat.attackSpeed')} ${signedPercent(buff.attackSpeedPoints / 100)}`)
  }

  // 2b. 战内点数修饰（技能 Buff 投影：增伤/威胁增幅/每秒威胁成长，Linear 点数直读）。
  if (buff.damageBonusPoints && buff.damageBonusPoints !== 0) {
    lines.push(t('market.effect.damageBonusPoints', { n: signedPercent(buff.damageBonusPoints / 100) }))
  }
  if (buff.threatPoints && buff.threatPoints !== 0) {
    lines.push(t('market.effect.buffThreatPoints', { n: signedPercent(buff.threatPoints / 100) }))
  }
  if (buff.threatPerSecondMaxHpPoints && buff.threatPerSecondMaxHpPoints !== 0) {
    lines.push(t('market.effect.threatPerSecondMaxHp', { n: signedPercent(buff.threatPerSecondMaxHpPoints / 100) }))
  }

  // 3. 通用属性比率字典（领域 BuffModifiers.statRatios 透传键）
  if (buff.statRatios) {
    for (const [modKey, modVal] of Object.entries(buff.statRatios)) {
      if (modVal !== 0) {
        const label = STAT_RATIO_LABEL_KEYS[modKey] ? t(STAT_RATIO_LABEL_KEYS[modKey]) : modKey
        lines.push(`${label} ${signedPercent(modVal)}`)
      }
    }
  }

  // 8. 若尚未解析出任何行，但有明确的 buffId，尝试从词典内建已知效果表中查询具体效果
  if (lines.length === 0 && buff.buffId) {
    const KNOWN_BUFF_EFFECT_KEYS: Record<string, string> = {
      'buff.zhixue': 'market.effect.knownZhixue',
      'buff.shunqi': 'market.effect.knownShunqi',
      'buff.qiGathering': 'market.effect.knownQiGathering',
      'buff.tongmai': 'market.effect.knownTongmai',
    }
    const knownKey = KNOWN_BUFF_EFFECT_KEYS[buff.buffId]
    if (knownKey) {
      lines.push(...t(knownKey).split('\n'))
    }
  }

  // 9. 若有描述文本
  if (lines.length === 0 && buff.description) {
    lines.push(buff.description)
  }

  return lines
}

/**
 * 通用属性数值加成条目模型（对齐装备属性展示：[小图标] + [属性名胶囊] + [下划线加成数值]）
 */
export interface BonusAsideItem {
  /** 属性小图标标识（如 'hp', 'phys-atk', 'crit', 'alchemy', 'bio-cultivation' 等），缺省时由 label 智能反查 */
  glyph?: string
  /** 自定义图标 ReactNode（优先于 glyph） */
  icon?: ReactNode
  /** 属性名称（如「物理攻击」、「气血回复」、「修炼效率」、「持续时间」等） */
  label: string
  /** 加成数值（如「+120」、「+20%」、「300秒」等，固定数值或百分比） */
  value: string | number
  /** 修仙色彩色调 */
  tone?: PillTone
}

/**
 * 通用属性数值加成独立悬浮窗参数
 */
export interface BonusAsideCardOptions {
  id?: string
  mark?: string
  title: string
  subtitle?: string
  tone?: FloatingTone
  items: readonly BonusAsideItem[]
}

/**
 * 通用属性数值加成列表渲染组件
 * 前面是图标（StatGlyph），后面是属性名（capsule-label），后面是加成值（underlined-value，固定数值或百分比）
 */
export const BonusAsideList = memo(function BonusAsideList({
  items,
  className = '',
}: {
  items: readonly BonusAsideItem[]
  className?: string
}): React.JSX.Element {
  return (
    <div className={`daohua-bonus-aside-list ${className}`.trim()}>
      {items.map((item, idx) => {
        const glyphName = item.glyph ?? resolveStatGlyph(item.label)
        const iconNode = item.icon ?? (glyphName ? <StatGlyph glyph={glyphName} size={14.5} /> : undefined)
        return (
          <PillBonusLabel
            key={idx}
            icon={iconNode}
            label={item.label}
            tone={item.tone ?? 'default'}
            value={item.value}
          />
        )
      })}
    </div>
  )
})

/**
 * 构造标准统一的属性数值加成独立悬浮窗 (Bonus Aside Card)
 * 装备属性、药品效果、实时Buff 均共用此悬浮窗！
 */
export function createBonusAsideCard(options: BonusAsideCardOptions): FloatingAsideCard {
  return {
    id: options.id,
    mark: options.mark,
    title: options.title,
    subtitle: options.subtitle,
    tone: options.tone ?? 'gold',
    content: <BonusAsideList items={options.items} />,
  }
}

/**
 * 将 Buff 实例或丹药 Buff 定义解析为标准属性数值加成条目列表 (BonusAsideItem[])
 */
export function extractBuffBonusItems(buff: {
  buffId?: string | null
  name?: string
  description?: string
  hpRegen?: number
  mpRegen?: number
  hpRegenPostRatio?: number
  mpRegenPostRatio?: number
  damageAbsorptionPoints?: number
  attackSpeedPoints?: number
  statRatios?: Record<string, number> | null
  threatPoints?: number
  damageBonusPoints?: number
  threatPerSecondMaxHpPoints?: number
}): BonusAsideItem[] {
  const t = getT()
  const items: BonusAsideItem[] = []

  // 1. 每 5 秒结算点回复
  if (buff.hpRegen && buff.hpRegen !== 0) {
    const isNeg = buff.hpRegen < 0
    items.push({
      glyph: 'hp-regen',
      label: t('market.effect.badgeHpRegen'),
      tone: isNeg ? 'crimson' : 'emerald',
      value: `${buff.hpRegen > 0 ? '+' : ''}${formatNumber(buff.hpRegen)}/5s`,
    })
  }
  if (buff.mpRegen && buff.mpRegen !== 0) {
    const isNeg = buff.mpRegen < 0
    items.push({
      glyph: 'mp-regen',
      label: t('market.effect.badgeMpRegen'),
      tone: isNeg ? 'crimson' : 'cyan',
      value: `${buff.mpRegen > 0 ? '+' : ''}${formatNumber(buff.mpRegen)}/5s`,
    })
  }

  // 1b. 回复后段乘区
  if (buff.hpRegenPostRatio && buff.hpRegenPostRatio !== 0) {
    const isNeg = buff.hpRegenPostRatio < 0
    items.push({
      glyph: 'hp-regen',
      label: t('market.effect.badgeHpPostRatio'),
      tone: isNeg ? 'crimson' : 'emerald',
      value: signedPercent(buff.hpRegenPostRatio),
    })
  }
  if (buff.mpRegenPostRatio && buff.mpRegenPostRatio !== 0) {
    const isNeg = buff.mpRegenPostRatio < 0
    items.push({
      glyph: 'mp-regen',
      label: t('market.effect.badgeMpPostRatio'),
      tone: isNeg ? 'crimson' : 'cyan',
      value: signedPercent(buff.mpRegenPostRatio),
    })
  }

  // 2. 免伤与攻速点数
  if (buff.damageAbsorptionPoints && buff.damageAbsorptionPoints !== 0) {
    const points = buff.damageAbsorptionPoints
    const isNeg = points < 0
    const absPercent = Math.abs(+(points / (100 + Math.abs(points)) * 100).toFixed(1))
    items.push({
      glyph: 'total-damage',
      label: t('market.effect.badgeDamageAbsorption'),
      tone: isNeg ? 'crimson' : 'jade',
      value: `${points > 0 ? '+' : '-'}${absPercent}%`,
    })
  }
  if (buff.attackSpeedPoints && buff.attackSpeedPoints !== 0) {
    const isNeg = buff.attackSpeedPoints < 0
    items.push({
      glyph: 'attack-speed',
      label: t('market.effect.badgeAttackSpeed'),
      tone: isNeg ? 'crimson' : 'purple',
      value: signedPercent(buff.attackSpeedPoints / 100),
    })
  }

  // 2b. 战内点数修饰（增伤/威胁）
  if (buff.damageBonusPoints && buff.damageBonusPoints !== 0) {
    const isNeg = buff.damageBonusPoints < 0
    items.push({
      glyph: 'phys-damage',
      label: t('market.effect.badgeDamageBonus'),
      tone: isNeg ? 'crimson' : 'gold',
      value: signedPercent(buff.damageBonusPoints / 100),
    })
  }
  if (buff.threatPoints && buff.threatPoints !== 0) {
    const isNeg = buff.threatPoints < 0
    items.push({
      glyph: 'threat-bonus',
      label: t('market.effect.badgeThreat'),
      tone: isNeg ? 'crimson' : 'amber',
      value: signedPercent(buff.threatPoints / 100),
    })
  }
  if (buff.threatPerSecondMaxHpPoints && buff.threatPerSecondMaxHpPoints !== 0) {
    items.push({
      glyph: 'threat-bonus',
      label: t('market.effect.badgeThreatSec'),
      tone: 'amber',
      value: `+${(buff.threatPerSecondMaxHpPoints / 100).toFixed(1)}%/s`,
    })
  }

  // 3. 通用属性比率字典
  if (buff.statRatios) {
    for (const [modKey, modVal] of Object.entries(buff.statRatios)) {
      if (modVal !== 0) {
        const statKey = modKey.replace(/Ratio$/, '')
        const labelKey = STAT_RATIO_LABEL_KEYS[statKey] ?? STAT_RATIO_LABEL_KEYS[modKey]
        const label = labelKey ? t(labelKey) : modKey
        const isNeg = modVal < 0
        const glyph = resolveStatGlyph(statKey) ?? resolveStatGlyph(modKey) ?? 'foundation'
        const defaultTone = (STAT_TONE_MAP[statKey] ?? STAT_TONE_MAP[modKey] ?? 'default') as PillTone
        items.push({
          glyph,
          label,
          tone: isNeg ? 'crimson' : defaultTone,
          value: signedPercent(modVal),
        })
      }
    }
  }

  // 4. 若无点数解析，回退到原有文本解析
  if (items.length === 0) {
    const rawLines = describeBuffEffectLines(buff)
    for (const line of rawLines) {
      items.push({
        glyph: 'foundation',
        label: t('market.effect.badgeGeneral'),
        tone: 'gold',
        value: line,
      })
    }
  }

  return items
}

/** 向下兼容别名与转发 */
export type StructuredEffectItem = BonusAsideItem
export const resolveStructuredBuffEffects = extractBuffBonusItems
export const EffectAsideBody = BonusAsideList

/**
 * 坊市「市场价格」副卡（惰性求值）：按 (itemId, 强化等级) 命中 gameStore.marketStats
 * 中服务端持续维护的品种行情，展示卖一价/在售量、买一价/收购量与当日/当周成交量；
 * 仅可交易标的产出卡片（item.isTradable 或装备变体簿），无盘口一侧价格显示 "-"。
 */
function resolveMarketStatsAsideCard(
  itemId: string,
  enhancementLevel: number,
  itemDef: ItemDefinition | undefined,
  eqDef: CombatEquipmentDefinition | undefined,
  itemName: string,
): FloatingAsideCard | null {
  // 可交易口径与坊市浏览网格一致：配表 isTradable 物品或装备类标的（全部装备可挂单）。
  const tradable = itemDef?.isTradable === true
    || eqDef !== undefined
    || itemId.startsWith('equipment.')
  if (!tradable) return null
  const t = getT()
  const stats = useGameStore.getState().marketStats[marketStatsKey(itemId, enhancementLevel)]
  const vendorPrice = itemDef?.sellPrice && itemDef.sellPrice > 0 ? itemDef.sellPrice : null
  const spiritUnit = t('market.spiritUnit')

  const askPrice = stats?.bestAskPrice ?? null
  const bidPrice = stats?.bestBidPrice ?? null
  const askQty = stats?.askQuantity ?? 0
  const bidQty = stats?.bidQuantity ?? 0
  const dayVol = stats?.dayVolume ?? 0
  const weekVol = stats?.weekVolume ?? 0

  return {
    id: `aside-market-${itemId}-${enhancementLevel}`,
    mark: t('market.price.mark'),
    title: t('market.price.title'),
    subtitle: enhancementLevel > 0 ? `${itemName} +${enhancementLevel}` : itemName,
    tone: 'gold',
    content: (
      <div className="daohua-market-aside-body">
        {/* 最低在售 */}
        <div className="daohua-market-aside-row">
          <span className="daohua-market-aside-badge daohua-market-aside-badge--ask">
            {t('market.price.askBadge')}
          </span>
          {askPrice !== null ? (
            <span className="daohua-market-aside-price-wrap">
              <span className="daohua-market-aside-price daohua-market-aside-price--ask">
                {formatNumber(askPrice)}
              </span>
              <span className="daohua-market-aside-unit">{spiritUnit}</span>
            </span>
          ) : (
            <span className="daohua-market-aside-empty">{t('market.price.none')}</span>
          )}
          <span className="daohua-market-aside-stock">
            <span className="daohua-market-aside-stock-label">{t('market.price.askStockLabel')}</span>
            <span className={`daohua-market-aside-stock-val daohua-market-aside-stock-val--ask ${askQty > 0 ? 'is-active' : 'is-zero'}`}>
              {formatNumber(askQty)}
            </span>
          </span>
        </div>

        {/* 最高求购 */}
        <div className="daohua-market-aside-row">
          <span className="daohua-market-aside-badge daohua-market-aside-badge--bid">
            {t('market.price.bidBadge')}
          </span>
          {bidPrice !== null ? (
            <span className="daohua-market-aside-price-wrap">
              <span className="daohua-market-aside-price daohua-market-aside-price--bid">
                {formatNumber(bidPrice)}
              </span>
              <span className="daohua-market-aside-unit">{spiritUnit}</span>
            </span>
          ) : (
            <span className="daohua-market-aside-empty">{t('market.price.none')}</span>
          )}
          <span className="daohua-market-aside-stock">
            <span className="daohua-market-aside-stock-label">{t('market.price.bidDemandLabel')}</span>
            <span className={`daohua-market-aside-stock-val daohua-market-aside-stock-val--bid ${bidQty > 0 ? 'is-active' : 'is-zero'}`}>
              {formatNumber(bidQty)}
            </span>
          </span>
        </div>

        {/* 系统回收价格 */}
        <div className="daohua-market-aside-row">
          <span className="daohua-market-aside-badge daohua-market-aside-badge--recycle">
            {t('market.price.recycleBadge')}
          </span>
          {vendorPrice !== null ? (
            <span className="daohua-market-aside-price-wrap">
              <span className="daohua-market-aside-price daohua-market-aside-price--recycle">
                {formatNumber(vendorPrice)}
              </span>
              <span className="daohua-market-aside-unit">{spiritUnit}</span>
            </span>
          ) : (
            <span className="daohua-market-aside-empty daohua-market-aside-empty--muted">
              {t('market.price.notRecyclable')}
            </span>
          )}
        </div>

        {/* 分隔线与成交统计 */}
        <div className="daohua-market-aside-divider" />
        <div className="daohua-market-aside-row daohua-market-aside-row--volume">
          <span className="daohua-market-aside-badge daohua-market-aside-badge--volume">
            {t('market.price.volumeBadge')}
          </span>
          <div className="daohua-market-aside-volume-items">
            <span className="daohua-market-aside-volume-chip">
              <span className="daohua-market-aside-volume-label">{t('market.price.dayVolume')}</span>
              <span className="daohua-market-aside-volume-val">{formatNumber(dayVol)}</span>
            </span>
            <span className="daohua-market-aside-volume-chip">
              <span className="daohua-market-aside-volume-label">{t('market.price.weekVolume')}</span>
              <span className="daohua-market-aside-volume-val">{formatNumber(weekVol)}</span>
            </span>
          </div>
        </div>
      </div>
    ),
  }
}

/**
 * 提取丹药与消耗品的持续增益 Buff 效果，构造为独立的附属气泡卡片 (Aside Cards)
 */
export function resolveItemAsideCards(
  definition?: ItemDefinition,
  fallbackName?: string
): FloatingAsideCard[] {
  if (!definition) return []
  const t = getT()
  const cards: FloatingAsideCard[] = []

  // 1. 修炼丹药/持续消耗品效果 (如引气丹、通脉丹)
  if (definition.cultivationMultiplier && definition.cultivationMultiplier > 1) {
    const items: BonusAsideItem[] = [
      {
        glyph: 'bio-cultivation',
        label: t('market.aside.badgeCultivation'),
        tone: 'qi',
        value: `+${Math.round((definition.cultivationMultiplier - 1) * 100)}%`,
      },
    ]
    if (definition.durationSeconds && definition.durationSeconds > 0) {
      items.push({
        glyph: 'cooldown-reduction',
        label: t('market.aside.badgeDuration'),
        tone: 'slate',
        value: `${formatNumber(definition.durationSeconds)}秒`,
      })
    }
    cards.push(
      createBonusAsideCard({
        id: `aside-cultivation-${definition.itemId}`,
        mark: t('market.aside.buff'),
        title: definition.name || fallbackName || t('market.aside.buff'),
        tone: 'qi',
        items,
      })
    )
  }

  // 2. 采集产出加成 (如寻灵香)
  if (definition.gatheringMultiplier && definition.gatheringMultiplier > 1) {
    const items: BonusAsideItem[] = [
      {
        glyph: 'alchemy',
        label: t('market.aside.badgeGathering'),
        tone: 'jade',
        value: `+${Math.round((definition.gatheringMultiplier - 1) * 100)}%`,
      },
    ]
    if (definition.durationSeconds && definition.durationSeconds > 0) {
      items.push({
        glyph: 'cooldown-reduction',
        label: t('market.aside.badgeDuration'),
        tone: 'slate',
        value: `${formatNumber(definition.durationSeconds)}秒`,
      })
    }
    cards.push(
      createBonusAsideCard({
        id: `aside-gathering-${definition.itemId}`,
        mark: t('market.aside.buff'),
        title: definition.name || fallbackName || t('market.aside.buff'),
        tone: 'jade',
        items,
      })
    )
  }

  // 3. 炼丹产出加成 (如地火引)
  if (definition.alchemyMultiplier && definition.alchemyMultiplier > 1) {
    const items: BonusAsideItem[] = [
      {
        glyph: 'alchemy',
        label: t('market.aside.badgeAlchemy'),
        tone: 'gold',
        value: `+${Math.round((definition.alchemyMultiplier - 1) * 100)}%`,
      },
    ]
    if (definition.durationSeconds && definition.durationSeconds > 0) {
      items.push({
        glyph: 'cooldown-reduction',
        label: t('market.aside.badgeDuration'),
        tone: 'slate',
        value: `${formatNumber(definition.durationSeconds)}秒`,
      })
    }
    cards.push(
      createBonusAsideCard({
        id: `aside-alchemy-${definition.itemId}`,
        mark: t('market.aside.buff'),
        title: definition.name || fallbackName || t('market.aside.buff'),
        tone: 'gold',
        items,
      })
    )
  }

  // 4. 战斗丹药附带的 Buff 效果 (如止血散、顺气丸)
  for (const buff of definition.buffEffects ?? []) {
    const items = extractBuffBonusItems(buff)
    if (buff.durationSeconds > 0) {
      items.push({
        glyph: 'cooldown-reduction',
        label: t('market.aside.badgeDuration'),
        tone: 'slate',
        value: `${formatNumber(buff.durationSeconds)}秒`,
      })
    }
    cards.push(
      createBonusAsideCard({
        id: `aside-buff-${buff.name}`,
        mark: t('market.aside.buff'),
        title: buff.name,
        tone: 'gold',
        items,
      })
    )
  }

  // 5. 功法秘籍/残卷战技参悟
  const manualAside = resolveManualAsideCard(definition.itemId, definition.category)
  if (manualAside) {
    cards.push(manualAside)
  }

  return cards
}

/**
 * 功法秘籍/残卷副卡片解析：将秘籍全本或残卷关联的战技技能数据提取为联动展示的副卡片
 */
export function resolveManualAsideCard(
  itemId: string,
  category?: string,
  abilities?: readonly CombatAbilityDefinition[]
): FloatingAsideCard | null {
  const isTome = itemId.startsWith('item.tome.')
  const isFragment = itemId.startsWith('item.fragment.')
  const isManual = isTome || isFragment || category === 'manual' || category === 'manualFragment'
  if (!isManual) return null

  let abilityId: string
  if (isTome) {
    abilityId = `ability.${itemId.slice('item.tome.'.length)}`
  } else if (isFragment) {
    abilityId = `ability.${itemId.slice('item.fragment.'.length)}`
  } else if (itemId.startsWith('item.')) {
    abilityId = `ability.${itemId.slice('item.'.length)}`
  } else {
    abilityId = `ability.${itemId}`
  }

  const abilityList = abilities ?? useGameStore.getState().abilities
  const ability = abilityList.find((a) => a.abilityId === abilityId)
  if (!ability) return null

  const t = getT()
  const abilityInfo = describeAbility(ability, 1)
  const isFrag = isFragment || category === 'manualFragment'
  const mark = isFrag
    ? t('market.aside.fragmentComprehension')
    : t('market.aside.tomeComprehension')

  const styleLabel = ability.style ? t(`game.abilityStyle.${ability.style}`) : ''
  const categoryLabel = formatAbilityCategory(ability.category, ability.element)
  const subtitle = styleLabel ? `${styleLabel} · ${categoryLabel}` : categoryLabel

  const bonusElements = abilityInfo.bonuses?.map((bonus) => (
    <PillBonusLabel
      key={bonus.key}
      label={bonus.label}
      tone={bonus.tone as PillTone}
      value={bonus.value}
    />
  )) ?? []

  const lines = [
    ...abilityInfo.lines,
    ...bonusElements,
  ]

  return {
    id: `aside-manual-${ability.abilityId}`,
    mark,
    title: ability.name,
    subtitle,
    tone: 'qi',
    lines,
    content: (
      <div>
        {abilityInfo.description && (
          <ValueText className="effect-value-capsule" text={abilityInfo.description} />
        )}
        {abilityInfo.radarMultipliers && (
          <RootAspectRadarChart
            efficiency={abilityInfo.radarEfficiency}
            multipliers={abilityInfo.radarMultipliers}
          />
        )}
      </div>
    ),
  }
}

/**
 * 装备与战斗属性到修仙色彩主题（PillTone）的标准对应字典
 * 严格与内视属性面板（InsightPage）保持完全一致：
 * - 物理攻击/威能/吸血/威胁 -> crimson（赤红）
 * - 术法攻击 -> indigo（靛紫）
 * - 生命上限/生命回复/伤害减免 -> emerald（翡翠绿）
 * - 灵力上限/灵力回复/冷却缩减 -> cyan（灵气青）
 * - 物理护甲/护甲穿透 -> bronze（青铜）
 * - 术法抗性/法术穿透 -> violet（紫罗兰）
 * - 命中 -> sky（天蓝）
 * - 闪避/施法攻速 -> teal（水绿）
 * - 暴击率/暴击伤害 -> gold（道金）
 * - 免暴/韧性 -> slate（玄岩灰）
 * - 破招 -> amber（琥珀橙）
 * - 化解 -> silver（银白）
 * - 五行克制 -> 金 gold / 木 emerald / 水 cyan / 火 amber / 土 bronze
 */
export const STAT_TONE_MAP: Record<string, PillTone> = {
  // 物理攻击类 (赤红)
  physicalAttack: 'crimson',
  physAtk: 'crimson',
  damage: 'crimson',
  damageRatio: 'crimson',
  lifeStealRatio: 'crimson',
  hpDrainRatio: 'crimson',
  threatRatio: 'crimson',
  threatBonus: 'crimson',
  threatBonusPoints: 'crimson',
  basicAttackBonus: 'crimson',
  basicAttackBonusPoints: 'crimson',

  // 术法攻击类 (靛紫)
  spellAttack: 'indigo',
  spellAtk: 'indigo',

  skillBonus: 'indigo',
  skillBonusPoints: 'indigo',
  // 生命类 (翡翠绿)
  maxHitpoints: 'emerald',
  hp: 'emerald',
  hitpointRegen: 'emerald',
  hpRegen: 'emerald',
  healingBonusRatio: 'emerald',
  damageAbsorption: 'emerald',
  damageAbsorptionRatio: 'emerald',
  damageReduction: 'emerald',
  fixedDamageBonus: 'emerald',
  fixedDamageReduction: 'emerald',
  fixedPhysicalDamageBonus: 'crimson',
  fixedPhysicalDamageReduction: 'crimson',
  fixedSpellDamageBonus: 'indigo',
  fixedSpellDamageReduction: 'indigo',

  // 灵力类 (灵气青)
  maxManapoints: 'cyan',
  mp: 'cyan',
  manapointRegen: 'cyan',
  mpRegen: 'cyan',
  cooldownReduction: 'cyan',
  cooldownSpeed: 'cyan',
  manaCostReduction: 'cyan',

  // 物理护甲与穿透 (青铜)
  physicalArmor: 'bronze',
  armor: 'bronze',
  penetrationRatio: 'bronze',
  armorDamageRatio: 'bronze',
  armorPen: 'bronze',

  // 术法抗性与法穿 (紫罗兰)
  spellResistance: 'violet',
  mr: 'violet',
  magicResistance: 'violet',
  magicRes: 'violet',
  magicPen: 'violet',

  healingBonus: 'violet',
  healingBonusPoints: 'violet',
  // 命中 (天蓝)
  accuracy: 'sky',
  hit: 'sky',
  accuracyRatio: 'sky',

  // 闪避与攻速 (水绿)
  evasion: 'teal',
  eva: 'teal',
  evasionRatio: 'teal',
  actionSpeed: 'teal',
  attackSpeedRatio: 'teal',

  // 暴击体系 (道金)
  criticalChance: 'gold',
  crit: 'gold',
  criticalDamage: 'gold',
  criticalDamageRatio: 'gold',

  // 免暴与韧性 (玄岩灰)
  criticalAvoidance: 'slate',
  critAvoid: 'slate',
  tenacity: 'slate',

  // 破招 (琥珀橙)
  parryBreak: 'amber',

  // 化解 (银白)
  parryResolve: 'silver',
}

/** 点数键 → 词条标签与曲线口径（战斗设计 §7.5：增伤类 Linear / 减伤速率类 NonLinear / 速率类 rate） */
const POINT_STAT_LABELS: Record<string, { label: string; curve: 'linear' | 'nonlinear' | 'rate' }> = {
  physicalDamageBonusPoints: { label: 'market.stat.physicalDamageBonus', curve: 'linear' },
  spellDamageBonusPoints: { label: 'market.stat.spellDamageBonus', curve: 'linear' },
  metalDamageBonusPoints: { label: 'market.stat.metalDamageBonus', curve: 'linear' },
  woodDamageBonusPoints: { label: 'market.stat.woodDamageBonus', curve: 'linear' },
  waterDamageBonusPoints: { label: 'market.stat.waterDamageBonus', curve: 'linear' },
  fireDamageBonusPoints: { label: 'market.stat.fireDamageBonus', curve: 'linear' },
  earthDamageBonusPoints: { label: 'market.stat.earthDamageBonus', curve: 'linear' },
  totalDamageBonusPoints: { label: 'market.stat.totalDamageBonus', curve: 'linear' },
  basicAttackBonusPoints: { label: 'market.stat.basicAttackBonus', curve: 'linear' },
  skillBonusPoints: { label: 'market.stat.skillBonus', curve: 'linear' },
  physicalDamageReductionPoints: { label: 'market.stat.physicalDamageReduction', curve: 'nonlinear' },
  spellDamageReductionPoints: { label: 'market.stat.spellDamageReduction', curve: 'nonlinear' },
  metalDamageReductionPoints: { label: 'market.stat.metalDamageReduction', curve: 'nonlinear' },
  woodDamageReductionPoints: { label: 'market.stat.woodDamageReduction', curve: 'nonlinear' },
  waterDamageReductionPoints: { label: 'market.stat.waterDamageReduction', curve: 'nonlinear' },
  fireDamageReductionPoints: { label: 'market.stat.fireDamageReduction', curve: 'nonlinear' },
  earthDamageReductionPoints: { label: 'market.stat.earthDamageReduction', curve: 'nonlinear' },
  damageAbsorptionPoints: { label: 'market.stat.damageReduction', curve: 'nonlinear' },
  attackSpeedPoints: { label: 'market.stat.attackSpeed', curve: 'rate' },
  actionSpeedPoints: { label: 'market.stat.actionSpeed', curve: 'rate' },
  cooldownReductionPoints: { label: 'market.stat.cooldownReduction', curve: 'rate' },
  criticalDamageBonusPoints: { label: 'market.stat.criticalDamage', curve: 'linear' },
  threatBonusPoints: { label: 'market.stat.threatBonus', curve: 'linear' },
  hpDrainPoints: { label: 'market.stat.hpDrain', curve: 'linear' },
  mpDrainPoints: { label: 'market.stat.mpDrain', curve: 'linear' },
  damageReflectPoints: { label: 'market.stat.damageReflect', curve: 'linear' },
  healingBonusPoints: { label: 'market.stat.healingBonus', curve: 'linear' },
}

/** 技艺工具技艺标识 → 速度加成词条键 */
const TOOL_SPEED_LABEL_KEYS: Record<string, string> = {
  gathering: 'combat.nearby.attrGatheringSpeed',
  mining: 'combat.nearby.attrMiningSpeed',
  alchemy: 'combat.nearby.attrAlchemySpeed',
  forging: 'combat.nearby.attrForgingSpeed',
  talisman: 'combat.nearby.attrTalismanSpeed',
  formation: 'combat.nearby.attrFormationSpeed',
  enhancement: 'combat.nearby.attrEnhancementSpeed',
}

/** 装备属性比率 key -> i18n 词条键映射 */
const EQUIPMENT_RATIO_LABEL_KEYS: Record<string, string> = {
  physicalAttack: 'market.stat.physicalAttack',
  spellAttack: 'market.stat.spellAttack',
  damage: 'market.stat.attackPower',
  physicalArmor: 'market.stat.physicalArmor',
  armor: 'market.stat.physicalArmor',
  spellResistance: 'market.stat.spellResistance',
  mr: 'market.stat.spellResistance',
  accuracy: 'market.stat.accuracy',
  hit: 'market.stat.accuracy',
  evasion: 'market.stat.evasion',
  eva: 'market.stat.evasion',
  criticalChance: 'market.stat.critChance',
  criticalDamage: 'market.stat.criticalDamage',
  damageAbsorption: 'market.stat.damageReduction',
  actionSpeed: 'market.stat.actionSpeed',
  cooldownReduction: 'market.stat.cooldownReduction',
}

/** 装备固定数值属性 key -> i18n 词条键映射 */
const EQUIPMENT_FLAT_LABEL_KEYS: Record<string, string> = {
  physicalAttack: 'market.stat.physicalAttack',
  spellAttack: 'market.stat.spellAttack',
  accuracy: 'market.stat.accuracy',
  evasion: 'market.stat.evasion',
  maxHitpoints: 'market.stat.maxHitpoints',
  hp: 'market.stat.maxHitpoints',
  maxManapoints: 'market.stat.maxManapoints',
  mp: 'market.stat.maxManapoints',
  physicalArmor: 'market.stat.physicalArmor',
  armor: 'market.stat.physicalArmor',
  spellResistance: 'market.stat.spellResistance',
  mr: 'market.stat.spellResistance',
  hitpointRegen: 'market.stat.hitpointRegen',
  hpRegen: 'market.stat.hitpointRegen',
  manapointRegen: 'market.stat.manapointRegen',
  mpRegen: 'market.stat.manapointRegen',
  criticalChance: 'market.stat.critChance',
  tenacity: 'market.stat.resilience',
  fixedDamageBonus: 'market.stat.fixedDamageBonus',
  fixedDamageReduction: 'market.stat.fixedDamageReduction',
  fixedPhysicalDamageBonus: 'market.stat.fixedPhysicalDamageBonus',
  fixedPhysicalDamageReduction: 'market.stat.fixedPhysicalDamageReduction',
  fixedSpellDamageBonus: 'market.stat.fixedSpellDamageBonus',
  fixedSpellDamageReduction: 'market.stat.fixedSpellDamageReduction',
}

/**
 * 提取装备的主干攻防数值、百分比点数与强化加成（供主卡片内联渲染与独立悬浮副窗共用）
 */
export function extractEquipmentBonusItems(
  equipment?: CombatEquipmentDefinition,
  enhancementLevel = 0
): BonusAsideItem[] {
  if (!equipment) return []
  const t = getT()
  const items: BonusAsideItem[] = []
  const enhanceScale = Math.pow(1.05, enhancementLevel)

  if (equipment.baseStatRatios) {
    for (const [key, val] of Object.entries(equipment.baseStatRatios)) {
      if (val !== 0) {
        const label = EQUIPMENT_RATIO_LABEL_KEYS[key] ? t(EQUIPMENT_RATIO_LABEL_KEYS[key]) : key
        const tone = (STAT_TONE_MAP[key] ?? 'default') as PillTone
        const glyph = resolveStatGlyph(key) ?? undefined
        items.push({
          glyph,
          label,
          tone,
          value: signedPercent(val * enhanceScale),
        })
      }
    }
  }

  if (equipment.flatStats) {
    for (const [key, val] of Object.entries(equipment.flatStats)) {
      if (val === 0) continue
      const point = POINT_STAT_LABELS[key]
      if (point) {
        // 点数键（*Points）为百分比点数通道，与固定值同乘强化倍率（口径同服务端 PlayerAttributeResolver）。
        const scaled = val * enhanceScale
        const effective = +(point.curve === 'nonlinear' ? scaled / (100 + Math.abs(scaled)) * 100 : scaled).toFixed(1)
        const isTemplate = point.label.startsWith('market.effect.')
        const glyph = resolveStatGlyph(key) ?? undefined
        const tone = (STAT_TONE_MAP[key] ?? STAT_TONE_MAP[key.replace(/Points$/, '')] ?? 'default') as PillTone
        items.push({
          glyph,
          label: isTemplate ? t(point.label, { n: effective }) : t(point.label),
          tone,
          value: isTemplate ? '' : `${effective >= 0 ? '+' : ''}${effective}%`,
        })
        continue
      }
      const label = EQUIPMENT_FLAT_LABEL_KEYS[key] ? t(EQUIPMENT_FLAT_LABEL_KEYS[key]) : key
      const tone = (STAT_TONE_MAP[key] ?? 'default') as PillTone
      const glyph = resolveStatGlyph(key) ?? undefined
      items.push({
        glyph,
        label,
        tone,
        value: `+${formatInteger(val * enhanceScale)}`,
      })
    }
  }

  if (equipment.techniqueBonus) {
    const { technique, speedRatio } = equipment.techniqueBonus
    if (speedRatio > 0) {
      const scaled = speedRatio * enhanceScale
      const percent = +(scaled * 100).toFixed(1)
      const labelKey = TOOL_SPEED_LABEL_KEYS[technique]
      const label = labelKey ? t(labelKey) : t('insight.panel.actionSpeed')
      const glyph = resolveStatGlyph(technique) ?? resolveStatGlyph('actionSpeed') ?? undefined
      items.push({
        glyph,
        label,
        tone: 'emerald',
        value: `+${percent}%`,
      })
    }
  }

  return items
}

/**
 * 提取装备的主干攻防数值、百分比点数与强化加成（同属主卡片，统一按强化倍率结算）
 */
export function describeEquipmentEffects(
  equipment?: CombatEquipmentDefinition,
  enhancementLevel = 0
): ReactNode[] {
  const items = extractEquipmentBonusItems(equipment, enhancementLevel)
  return items.map((item, idx) => {
    const glyphName = item.glyph ?? resolveStatGlyph(item.label)
    const iconNode = item.icon ?? (glyphName ? <StatGlyph glyph={glyphName} size={14.5} /> : undefined)
    return (
      <PillBonusLabel
        key={idx}
        icon={iconNode}
        label={item.label}
        tone={item.tone}
        value={item.value}
      />
    )
  })
}

/**
 * 构造装备属性数值加成独立悬浮窗 (Equipment Bonus Aside Card)
 */
export function createEquipmentBonusAsideCard(
  equipment: CombatEquipmentDefinition,
  enhancementLevel = 0,
  title?: string
): FloatingAsideCard {
  const t = getT()
  const items = extractEquipmentBonusItems(equipment, enhancementLevel)
  return createBonusAsideCard({
    id: `aside-eq-bonus-${equipment.slot}`,
    mark: t('market.aside.equipmentBonus'),
    title: title ?? t('market.aside.equipmentStats'),
    tone: 'gold',
    items,
  })
}

/**
 * 通用 Buff / Debuff 附属卡片构造工具
 * 将任意实时 Buff 实例转化为统一标准的属性数值加成独立悬浮窗 (createBonusAsideCard)
 */
export function createBuffAsideCard(
  buff: Buff,
  mark?: string
): FloatingAsideCard {
  const t = getT()
  const items = extractBuffBonusItems(buff)

  const isDebuff =
    buff.attackSpeedPoints < 0 ||
    buff.damageAbsorptionPoints < 0 ||
    buff.hpRegen < 0 ||
    buff.mpRegen < 0 ||
    Object.values(buff.statRatios ?? {}).some(value => value < 0)

  if (buff.stacks > 1) {
    items.unshift({
      glyph: 'realm',
      label: t('market.aside.badgeStacks'),
      tone: 'gold',
      value: `×${formatNumber(buff.stacks)}`,
    })
  }

  const tone: FloatingTone = isDebuff ? 'danger' : 'gold'
  const resolvedMark = mark ?? (isDebuff ? t('market.aside.debuff') : t('market.aside.buff'))

  return createBonusAsideCard({
    id: `aside-active-buff-${buff.buffId}`,
    mark: resolvedMark,
    title: buff.name,
    tone,
    items,
  })
}

/**
 * 物品标准卡片瓦片组件 (ItemTile)
 *
 * 紧凑的修仙网格方块：左上角品类丝带、右上角等阶 Lv、左下角强化 +N（仅强化装备）、中间名称与右下角拥有数量。
 * 若该物品 ID 已配备图标资源，中间的文本名称会被图标整块替换；未配备时保持文本渲染。
 */
export function ItemTile({
  amount,
  bottomRightLabel,
  category,
  className,
  enhancementLevel = 0,
  itemId,
  level,
  name,
  rarity,
}: ItemTileProps): React.JSX.Element {
  const categoryLabel = resolveItemCategoryLabel(category)
  const iconUrl = resolveItemIconUrl(itemId)
  const bottomRight =
    bottomRightLabel ??
    (amount === undefined ? null : <UnitNumber disableTooltip prefix="×" value={amount} />)
  return (
    <span className={`item-tile ${className}`.trim()} data-rarity={rarity}>
      <span className="item-type-ribbon" aria-hidden="true"><span>{categoryLabel}</span></span>
      {level !== undefined && (
        <span className="item-corner item-corner--top-right">
          Lv. <UnitNumber disableTooltip value={level} />
        </span>
      )}
      {iconUrl
        ? <img alt={name} className="item-tile-icon" decoding="async" draggable={false} src={iconUrl} />
        : <strong className="item-tile-name">{name}</strong>}
      {enhancementLevel > 0 && (
        <span className="item-corner item-corner--bottom-left">+{Math.floor(enhancementLevel)}</span>
      )}
      {bottomRight !== null && <span className="item-corner item-corner--bottom-right">{bottomRight}</span>}
      {/* hover 辉光叠加层：常驻 opacity:0，hover 时只动画 opacity（合成器线程），
          替代原先逐帧重绘瓦片的 box-shadow 过渡（见 selection-grid.css） */}
      <span className="item-tile-glow" aria-hidden="true" />
    </span>
  )
}

/**
 * 背包物品卡片完整组件 (ItemCard)
 *
 * 结合了 ItemTooltipTarget 与 ItemTile，用于背包格子的标准渲染。
 */
export function ItemCard({ item, definition, equipmentDefinition }: { item: InventoryItem; definition?: ItemDefinition; equipmentDefinition?: CombatEquipmentDefinition }): React.JSX.Element {
  // 订阅语言状态：resolveItemMetadata 返回的文案随语言切换刷新。
  const t = useT()
  const meta = resolveItemMetadata(item.itemId)
  const metadata = withItemGrade(
    meta ?? fallbackMetadata(item.itemId),
    item.tier ?? meta?.tier ?? 1,
  )
  // 强化变体行（AssetKey "+N"）：名称附强化等级、悬浮卡片按该等级缩放属性预览。
  const enhancementLevel = parseEnhancementVariant(item.variant)
  const rawName = (meta?.label ?? definition?.name) || item.name
  const displayName = enhancementLevel > 0 ? `${rawName} +${enhancementLevel}` : rawName
  return (
    <ItemTooltipTarget
      amount={item.amount}
      className="inventory-item-anchor"
      definition={definition}
      enhancementLevel={enhancementLevel}
      equipmentDefinition={equipmentDefinition}
      itemId={item.itemId}
      metadata={metadata}
    >
      <ItemTile
        amount={item.amount}
        category={resolveItemCategoryLabel(resolveItemSubCategory(item.itemId, definition, equipmentDefinition, item.subCategory), t)}
        className="inventory-cell"
        enhancementLevel={enhancementLevel}
        itemId={item.itemId}
        level={metadata.level}
        name={displayName}
        rarity={metadata.rarity}
      />
    </ItemTooltipTarget>
  )
}

/**
 * 怪物掉落条目胶囊 (ItemDropReference)
 *
 * 与暂存区/账本同款的复合物品胶囊：行内小图标 + 名称 + `×数量区间` 下划线值
 * （min=max 显示单值，否则 `min-max`）+ 胶囊尾部括号内掉落概率小字。
 * 对应服务端「先判定后数量」结算语义：先按 dropChance 判定是否掉落，
 * 命中后再在 [minQuantity, maxQuantity] 整数区间内均匀抽取数量。
 */
export function ItemDropReference({
  drop,
  className = '',
}: {
  /** 怪物掉落条目（物品 ID + 掉落概率 + 数量区间） */
  drop: MonsterDropDefinition
  /** 外部样式类名 */
  className?: string
}): React.JSX.Element {
  const quantityText = drop.minQuantity === drop.maxQuantity
    ? formatInteger(drop.minQuantity)
    : `${formatInteger(drop.minQuantity)}-${formatInteger(drop.maxQuantity)}`
  // 概率小数比率转百分数并去尾零：1 -> 100%、0.02 -> 2%、0.001 -> 0.1%
  const chanceLabel = `${+(drop.dropChance * 100).toFixed(2)}%`
  return (
    <ItemReference
      className={className}
      itemId={drop.itemId}
      valueContent={
        <>
          <span className="underlined-value">× {quantityText}</span>
          <span className="world-monster-drop-chance">（{chanceLabel}）</span>
        </>
      }
    />
  )
}

/**
 * 物品小图标 (ItemIcon)
 *
 * 物品 ID 配有图标资源时渲染一枚内联小图标；没有图标时**不渲染任何节点**（由调用方回退文本）。
 * 用于行内文本、列表行等不适合放整块瓦片的位置。
 */
export function ItemIcon({
  itemId,
  className = 'item-reference-icon',
}: {
  /** 物品唯一 ID */
  itemId: string
  /** 外部样式类名（默认与行内物品引用共用一套尺寸） */
  className?: string
}): React.JSX.Element | null {
  const url = resolveItemIconUrl(itemId)
  if (!url) return null
  return <img alt="" aria-hidden="true" className={className} draggable={false} src={url} />
}

/**
 * 文本行内物品引用胶囊组件 (ItemReference)
 *
 * 用于日志、任务描述、制造需求等行内文本中引用特定物品，
 * 点击/悬停弹出完整物品气泡，并显示品质对应的主题色下划线与数量。
 * 物品配有图标时在名称前额外渲染一枚行内小图标。
 */
export function ItemReference({
  itemId,
  amount,
  label,
  valueContent,
  className = '',
}: {
  /** 物品唯一 ID */
  itemId: string
  /** 涉及数量（提供时显示复合胶囊） */
  amount?: number
  /** 覆写显示文本（默认取物品名称） */
  label?: string
  /** 覆写胶囊值区内容（默认 `×数量` 下划线值；提供时即便无 amount 也渲染复合胶囊） */
  valueContent?: ReactNode
  /** 额外样式类名 */
  className?: string
}): React.JSX.Element {
  // 订阅语言状态：resolveItemMetadata 返回的文案随语言切换刷新。
  useT()
  const metadata = resolveItemMetadata(itemId) ?? fallbackMetadata(itemId)
  const definition = useGameStore((state) => findItemInCatalog(state.items, itemId))
  const displayLabel = label ?? definition?.name ?? metadata.label
  const tone = rarityToTone(metadata.rarity)

  if (amount !== undefined || valueContent !== undefined) {
    return (
      <ItemTooltipTarget
        amount={amount}
        className={`item-reference-compound pill-tone--${tone} ${className}`.trim()}
        definition={definition}
        itemId={itemId}
      >
        <ItemIcon itemId={itemId} />
        <span className="capsule-label">{displayLabel}</span>
        {valueContent ?? (amount !== undefined && (
          <span className="underlined-value">
            × <UnitNumber disableTooltip value={amount} />
          </span>
        ))}
      </ItemTooltipTarget>
    )
  }

  return (
    <ItemTooltipTarget
      amount={amount}
      className={`item-reference pill-tone--${tone} ${className}`.trim()}
      definition={definition}
      itemId={itemId}
    >
      <ItemIcon itemId={itemId} />
      {displayLabel}
    </ItemTooltipTarget>
  )
}

/**
 * 消耗/产出条目胶囊组件 (ActionDetailItem)
 *
 * 行动详情与强化预览共用的「物品 ×需求 / 持有·需求」行内条目：
 * current 提供时显示 `持有 / 需求` 并在不足时以 `is-insufficient` 警示色标记，
 * 缺省时仅显示 `×需求`。
 */
export const ActionDetailItem = memo(function ActionDetailItem({
  itemId,
  amount,
  current,
}: {
  /** 物品唯一 ID */
  itemId: string
  /** 需求数量 */
  amount: number
  /** 当前持有数量（提供时显示 持有/需求 并做不足标红） */
  current?: number
}): React.JSX.Element {
  const metadata = resolveItemMetadata(itemId)
  const insufficient = current !== undefined && current < amount
  return (
    <span className="action-detail-item">
      <ItemReference amount={current} itemId={itemId} label={metadata?.label ?? itemId} />
      <strong className={insufficient ? 'is-insufficient' : ''}>
        {current === undefined ? <UnitNumber prefix="×" value={amount} /> : <><UnitNumber value={current} /> / <UnitNumber value={amount} /></>}
      </strong>
    </span>
  )
})
