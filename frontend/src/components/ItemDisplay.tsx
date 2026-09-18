import { type ReactNode, useCallback } from 'react'
import { describeAbility, describeCombatTrigger, formatAbilityCategory, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { convertTierToLevel, findEquipmentInCatalog, findItemInCatalog, resolveDefaultEquipmentDefinition, resolveDefaultItemDefinition, resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata, type ItemRarity } from '../game/ids'
import { resolveItemIconUrl } from '../game/itemIcons'
import { getT, useT } from '../i18n'
import type { Buff, CombatAbilityDefinition, CombatEquipmentDefinition, InventoryItem, ItemDefinition } from '../game/types'
import { type FloatingAsideCard, FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import { PillBonusLabel, type PillTone } from './PillLabel'
import { UnitNumber } from './UnitNumber'

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
  /** 外部样式类名 */
  className: string
  /** 物品唯一 ID：命中图标资源时整块瓦片改用图片渲染，未命中则回退为文本名称 */
  itemId?: string
  /** 物品等阶/品级（展示在右上角） */
  level?: number
  /** 物品名称（无图标时的兜底文本，同时作为图片替代文本） */
  name: string
  /** 稀有度品质（决定边框与背景光效） */
  rarity: ItemRarity
}

/** 未在注册表登记的物品的最小兜底元数据（凡品/金字兜底，品类走默认标签） */
function fallbackMetadata(itemId: string): ItemMetadata {
  const t = getT()
  return {
    label: itemId,
    glyph: '·',
    tone: 'gold',
    category: resolveItemCategoryLabel(undefined, t),
    rarity: 'mundane',
    rarityLabel: t('game.grade.mundane'),
    description: '',
    source: '',
    usage: '',
  }
}

function rarityToTone(rarity: ItemRarity): FloatingTone {
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
 * 悬浮主卡片内容组件：聚合物品/装备定义、品阶徽章、主干效果行与品类/等阶/持存键值对。
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

  const itemDef = itemDefOverride ?? findItemInCatalog(itemCatalog, itemId) ?? resolveDefaultItemDefinition(itemId)
  const eqDef = eqDefOverride ?? findEquipmentInCatalog(equipmentCatalog, itemId) ?? resolveDefaultEquipmentDefinition(itemId)

  const baseMetadata = metadataOverride ?? resolveItemMetadata(itemId) ?? fallbackMetadata(itemId)
  const rawMetadata = itemDef
    ? {
        ...baseMetadata,
        label: itemDef.name || baseMetadata.label,
        category: resolveItemCategoryLabel(itemDef.category, t),
        level: itemDef.tier ?? baseMetadata.level,
        description: itemDef.description || baseMetadata.description,
      }
    : eqDef
    ? {
        ...baseMetadata,
        label: eqDef.name || baseMetadata.label,
        category: resolveItemCategoryLabel(eqDef.slot, t),
        level: eqDef.tier ?? baseMetadata.level,
      }
    : baseMetadata

  const tier = rawMetadata.level ?? (eqDef?.tier || itemDef?.tier || 1)
  const grade = resolveItemGrade(tier)
  const levelNumber = convertTierToLevel(tier)

  const metadata: ItemMetadata = {
    ...rawMetadata,
    rarity: grade.rarity,
    rarityLabel: grade.label,
    level: levelNumber,
  }

  // 主卡片只展示直接基础效果与攻防数值，Buff 与特殊机制解耦至 Aside 附属卡片群
  const effectLines = [
    ...describeItemEffects(itemDef),
    ...describeEquipmentEffects(eqDef, enhancementLevel),
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
      title={metadata.label}
      titleIcon={resolveItemIconUrl(itemId)}
    />
  )
}

/**
 * 副卡片聚合解析（惰性求值）：经 FloatingPopover 的 asideCards 函数形式传入，仅在浮层实际展开时执行。
 * 合并丹药/增益 Buff 副卡、装备特殊机制副卡、功法秘籍/残卷参悟副卡与调用方附加卡片；
 * 若物品自身 Buff 链路已产出参悟副卡（aside-manual-*），不再重复追加秘籍解析结果。
 */
function resolveItemTooltipAsideCards(
  itemId: string,
  metadataOverride?: ItemMetadata,
  itemDefOverride?: ItemDefinition,
  eqDefOverride?: CombatEquipmentDefinition,
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
  const equipAsides = resolveEquipmentAsideCards(eqDef)
  list.push(...itemAsides, ...equipAsides)
  if (!itemAsides.some(c => c.id?.startsWith('aside-manual-'))) {
    const manualAside = resolveManualAsideCard(itemId, rawMetadata.category, abilitiesCatalog)
    if (manualAside) {
      list.push(manualAside)
    }
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
 * 1. 【主卡片 (300px)】：展示物品/装备品阶徽章、名称、背景故事描述、即时回血回蓝、冷却、装备攻防基础数值与品类/等阶；
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
        extraAsideCards
      ),
    [itemId, metadataOverride, itemDefOverride, eqDefOverride, extraAsideCards]
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
  const percent = Math.round(value * 1000) / 10
  return `${percent > 0 ? '+' : ''}${percent}%`
}

function multiplierPercent(value?: number | null): string | null {
  if (value === undefined || value === null) return null
  const delta = value - 1
  return Math.abs(delta) < 0.000001 ? null : signedPercent(delta)
}

/**
 * 提取丹药/消耗品的直接主干效果（留在主卡片）
 * 包含即时回血、即时回蓝、冷却时长、使用条件等
 */
function describeItemEffects(definition?: ItemDefinition): ReactNode[] {
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
  if (definition.recoverySeconds > 0) {
    lines.push(
      <PillBonusLabel
        key="item-duration"
        label={t('market.effect.recoveryDuration')}
        tone="teal"
        value={`${formatNumber(definition.recoverySeconds)}s`}
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
  if (definition.triggerAtCombatStart) {
    lines.push(
      <span className="daohua-floating-card-line-text" key="item-trigger-start">
        {t('market.effect.useAtCombatStart')}
      </span>
    )
  }
  for (const trigger of definition.defaultCombatTriggers ?? []) {
    lines.push(
      <span className="daohua-floating-card-line-text" key={`trigger-${trigger.condition}`}>
        {describeCombatTrigger(trigger)}
      </span>
    )
  }
  if (
    (definition.defaultCombatTriggers?.length ?? 0) === 0 &&
    (definition.hitpointRestore > 0 || definition.manapointRestore > 0) &&
    definition.triggerHpRatio > 0
  ) {
    lines.push(
      <span className="daohua-floating-card-line-text" key="item-auto-use">
        {t('market.effect.autoUseBelowHp', { percent: formatNumber(Math.round(definition.triggerHpRatio * 100)) })}
      </span>
    )
  }
  return lines
}

/**
 * 通用 Buff 数值效果行解析器
 * 能够将任意 Buff 定义、配置或运行时实例中的所有属性字段解析为格式化中文词条
 */
export function describeBuffEffectLines(buff: {
  buffId?: string | null
  name?: string
  description?: string
  hpRegen?: number
  mpRegen?: number
  damageAbsorptionRatio?: number
  damageRatio?: number
  attackSpeedRatio?: number
  attackLevelRatio?: number
  armor?: number
  magicResistance?: number
  criticalChance?: number
  criticalDamageRatio?: number
  tenacity?: number
  qiGainMultiplier?: number
  gatheringOutputMultiplier?: number
  alchemyOutputMultiplier?: number
  modifiers?: Record<string, number> | null
}): string[] {
  const t = getT()
  const lines: string[] = []

  // 1. 每秒回复
  if (buff.hpRegen && buff.hpRegen > 0) {
    lines.push(t('market.effect.hpRegenPerSecond', { n: formatNumber(buff.hpRegen) }))
  }
  if (buff.mpRegen && buff.mpRegen > 0) {
    lines.push(t('market.effect.mpRegenPerSecond', { n: formatNumber(buff.mpRegen) }))
  }

  // 2. 伤害与减伤
  if (buff.damageAbsorptionRatio && buff.damageAbsorptionRatio !== 0) {
    lines.push(t('market.effect.damageReductionPct', { n: Math.round(buff.damageAbsorptionRatio * 100) }))
  }
  if (buff.damageRatio && buff.damageRatio !== 0) {
    lines.push(`${t('market.stat.damage')} ${signedPercent(buff.damageRatio)}`)
  }

  // 3. 攻速与命中闪避
  if (buff.attackSpeedRatio && buff.attackSpeedRatio !== 0) {
    lines.push(`${t('market.stat.attackSpeed')} ${signedPercent(buff.attackSpeedRatio)}`)
  }
  if (buff.attackLevelRatio && buff.attackLevelRatio !== 0) {
    lines.push(`${t('market.stat.accuracy')} ${signedPercent(buff.attackLevelRatio)}`)
  }

  // 4. 防御与抗性
  if (buff.armor && buff.armor !== 0) {
    lines.push(t('market.effect.armorFlat', { n: formatNumber(buff.armor) }))
  }
  if (buff.magicResistance && buff.magicResistance !== 0) {
    lines.push(t('market.effect.magicResFlat', { n: formatNumber(buff.magicResistance) }))
  }

  // 5. 暴击与韧性
  if (buff.criticalChance && buff.criticalChance !== 0) {
    lines.push(t('market.effect.critChancePct', { n: Math.round(buff.criticalChance * 100) }))
  }
  if (buff.criticalDamageRatio && buff.criticalDamageRatio > 1.5) {
    lines.push(t('market.effect.critDamagePct', { n: Math.round((buff.criticalDamageRatio - 1.5) * 100) }))
  }
  if (buff.tenacity && buff.tenacity !== 0) {
    lines.push(t('market.effect.tenacityFlat', { n: formatNumber(buff.tenacity) }))
  }

  // 6. 修炼与技艺收益
  const qi = multiplierPercent(buff.qiGainMultiplier)
  if (qi) lines.push(t('market.effect.qiGainBonus', { value: qi }))

  const gathering = multiplierPercent(buff.gatheringOutputMultiplier)
  if (gathering) lines.push(t('market.effect.gatheringOutput', { value: gathering }))

  const alchemy = multiplierPercent(buff.alchemyOutputMultiplier)
  if (alchemy) lines.push(t('market.effect.alchemyOutput', { value: alchemy }))

  // 7. 自定义修饰器字典
  if (buff.modifiers) {
    for (const [modKey, modVal] of Object.entries(buff.modifiers)) {
      if (modVal !== 0) {
        lines.push(`${modKey}: ${signedPercent(modVal)}`)
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
    const lines: string[] = [
      t('market.aside.cultivationSpeed', {
        value: `+${Math.round((definition.cultivationMultiplier - 1) * 100)}%`,
      }),
    ]
    if (definition.durationSeconds && definition.durationSeconds > 0) {
      lines.push(t('market.aside.duration', { seconds: formatNumber(definition.durationSeconds) }))
    }
    cards.push({
      id: `aside-cultivation-${definition.itemId}`,
      mark: t('market.aside.buff'),
      title: definition.name || fallbackName || t('market.aside.buff'),
      tone: 'qi',
      lines,
    })
  }

  // 2. 采集产出加成 (如寻灵香)
  if (definition.gatheringMultiplier && definition.gatheringMultiplier > 1) {
    const lines: string[] = [
      t('market.aside.gatheringOutput', {
        value: `+${Math.round((definition.gatheringMultiplier - 1) * 100)}%`,
      }),
    ]
    if (definition.durationSeconds && definition.durationSeconds > 0) {
      lines.push(t('market.aside.duration', { seconds: formatNumber(definition.durationSeconds) }))
    }
    cards.push({
      id: `aside-gathering-${definition.itemId}`,
      mark: t('market.aside.buff'),
      title: definition.name || fallbackName || t('market.aside.buff'),
      tone: 'jade',
      lines,
    })
  }

  // 3. 炼丹产出加成 (如地火引)
  if (definition.alchemyMultiplier && definition.alchemyMultiplier > 1) {
    const lines: string[] = [
      t('market.aside.alchemyOutput', {
        value: `+${Math.round((definition.alchemyMultiplier - 1) * 100)}%`,
      }),
    ]
    if (definition.durationSeconds && definition.durationSeconds > 0) {
      lines.push(t('market.aside.duration', { seconds: formatNumber(definition.durationSeconds) }))
    }
    cards.push({
      id: `aside-alchemy-${definition.itemId}`,
      mark: t('market.aside.buff'),
      title: definition.name || fallbackName || t('market.aside.buff'),
      tone: 'gold',
      lines,
    })
  }

  // 4. 战斗丹药附带的 Buff 效果 (如止血散、顺气丸)
  for (const buff of definition.buffEffects ?? []) {
    const buffLines = describeBuffEffectLines(buff)

    const subParts: string[] = []
    if (buff.durationSeconds > 0) {
      subParts.push(t('market.aside.duration', { seconds: formatNumber(buff.durationSeconds) }))
    }
    if (buff.maxStacks > 1) {
      subParts.push(t('market.aside.maxStacks', { stacks: formatNumber(buff.maxStacks) }))
    }

    cards.push({
      id: `aside-buff-${buff.name}`,
      mark: t('market.aside.buff'),
      title: buff.name,
      subtitle: subParts.length > 0 ? subParts.join(' · ') : undefined,
      tone: 'gold',
      lines: buffLines.length > 0 ? buffLines : [t('market.effect.ongoing')],
    })
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

  const lines = [...abilityInfo.lines]
  // 结构性去重：默认触发条件里已包含「自身生命低于阈值」时不再重复追加自动使用提示
  const hasHpTrigger = (ability.defaultTriggers ?? []).some(
    (tr) =>
      tr.dependency === 'self' &&
      tr.condition === 'current-hp-percentage' &&
      (tr.comparator === 'less-than' || tr.comparator === 'less-than-equal')
  )
  if (ability.triggerHpRatio && ability.triggerHpRatio > 0 && !hasHpTrigger) {
    lines.push(t('market.effect.autoUseBelowHp', { percent: Math.round(ability.triggerHpRatio * 100) }))
  }

  return {
    id: `aside-manual-${ability.abilityId}`,
    mark,
    title: ability.name,
    subtitle,
    tone: 'qi',
    lines,
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

  // 术法攻击类 (靛紫)
  spellAttack: 'indigo',
  spellAtk: 'indigo',

  // 生命类 (翡翠绿)
  maxHitpoints: 'emerald',
  hp: 'emerald',
  hitpointRegen: 'emerald',
  hpRegen: 'emerald',
  healingBonusRatio: 'emerald',
  damageAbsorption: 'emerald',
  damageAbsorptionRatio: 'emerald',
  damageReduction: 'emerald',

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

  // 五行抗性
  metalResistance: 'gold',
  woodResistance: 'emerald',
  waterResistance: 'cyan',
  fireResistance: 'amber',
  earthResistance: 'bronze',
}

/**
 * 提取装备的主干攻防数值与强化加成（留在主卡片）
 */
function describeEquipmentEffects(
  equipment?: CombatEquipmentDefinition,
  enhancementLevel = 0
): ReactNode[] {
  if (!equipment) return []
  const t = getT()
  const lines: ReactNode[] = []

  // 属性 key -> i18n 词条键映射；未登记的新属性回退原始 wire key
  const ratioLabelKeys: Record<string, string> = {
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

  const flatLabelKeys: Record<string, string> = {
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
  }

  const hasStructured = Boolean(
    (equipment.baseStatRatios && Object.keys(equipment.baseStatRatios).length > 0) ||
    (equipment.flatStats && Object.keys(equipment.flatStats).length > 0)
  )

  // ⚠️ 彻底移除攻击间隔展示逻辑（修仙规则中装备不带攻击间隔属性）

  if (hasStructured) {
    if (equipment.baseStatRatios) {
      for (const [key, val] of Object.entries(equipment.baseStatRatios)) {
        if (val !== 0) {
          const label = ratioLabelKeys[key] ? t(ratioLabelKeys[key]) : key
          const tone = STAT_TONE_MAP[key] ?? 'default'
          lines.push(
            <PillBonusLabel
              key={`ratio-${key}`}
              label={label}
              tone={tone}
              value={signedPercent(val)}
            />
          )
        }
      }
    }
    if (equipment.flatStats) {
      for (const [key, val] of Object.entries(equipment.flatStats)) {
        if (val !== 0) {
          const label = flatLabelKeys[key] ? t(flatLabelKeys[key]) : key
          const tone = STAT_TONE_MAP[key] ?? 'default'
          if (key === 'criticalChance' && val <= 1.0) {
            lines.push(
              <PillBonusLabel
                key={`flat-${key}`}
                label={label}
                tone={tone}
                value={`+${formatNumber(val * 100)}%`}
              />
            )
          } else {
            // 计算强化带来的加成
            const bonusPerLevel = equipment.enhancementBonus
              ? (equipment.enhancementBonus as unknown as Record<string, number>)[key] ?? 0
              : 0
            const totalBonus = bonusPerLevel * enhancementLevel
            lines.push(
              <PillBonusLabel
                key={`flat-${key}`}
                label={label}
                suffix={totalBonus > 0 ? ` (+${formatNumber(totalBonus)})` : undefined}
                tone={tone}
                value={`+${formatNumber(val)}`}
              />
            )
          }
        }
      }
    }
    return lines
  }

  // 兜底回退：当没有结构化字段时读取 stats
  const stats = equipment.stats
  const addPercent = (key: string, label: string, value: number) => {
    if (value !== 0) {
      const tone = STAT_TONE_MAP[key] ?? 'default'
      lines.push(
        <PillBonusLabel
          key={`stat-${key}`}
          label={label}
          tone={tone}
          value={signedPercent(value)}
        />
      )
    }
  }
  const addNumber = (key: string, label: string, value: number) => {
    if (value !== 0) {
      const tone = STAT_TONE_MAP[key] ?? 'default'
      lines.push(
        <PillBonusLabel
          key={`stat-${key}`}
          label={label}
          tone={tone}
          value={`+${formatNumber(value)}`}
        />
      )
    }
  }
  addPercent('damageRatio', t('market.stat.damage'), stats.damageRatio)
  addPercent('accuracyRatio', t('market.stat.accuracy'), stats.accuracyRatio)
  addPercent('attackSpeedRatio', t('market.stat.attackSpeed'), stats.attackSpeedRatio)
  addPercent('evasionRatio', t('market.stat.evasion'), stats.evasionRatio)
  addPercent('armor', t('market.stat.armor'), stats.armor)
  addPercent('magicResistance', t('market.stat.magicRes'), stats.magicResistance)
  addNumber('tenacity', t('market.stat.tenacity'), stats.tenacity)
  addPercent('penetrationRatio', t('market.stat.penetration'), stats.penetrationRatio)
  addPercent('lifeStealRatio', t('market.stat.lifeSteal'), stats.lifeStealRatio)
  addPercent('armorDamageRatio', t('market.stat.armorDamage'), stats.armorDamageRatio)
  addPercent('hpDrainRatio', t('market.stat.hpDrain'), stats.hpDrainRatio)
  addPercent('criticalChance', t('market.stat.critChance'), stats.criticalChance)
  addPercent('criticalDamageRatio', t('market.stat.critDamage'), stats.criticalDamageRatio)
  addPercent('criticalAvoidance', t('market.stat.critAvoidance'), stats.criticalAvoidance)
  addPercent('damageAbsorptionRatio', t('market.stat.damageAbsorption'), stats.damageAbsorptionRatio)
  return lines
}

/**
 * 提取装备的特殊机制与词缀 Buff，构造为独立的附属气泡卡片 (Aside Cards)
 */
export function resolveEquipmentAsideCards(
  equipment?: CombatEquipmentDefinition
): FloatingAsideCard[] {
  if (!equipment) return []
  const t = getT()
  const cards: FloatingAsideCard[] = []

  const specialLabelKeys: Record<string, string> = {
    threatRatio: 'market.effect.threatBonus',
    healingBonusRatio: 'market.effect.healingBonus',
  }

  if (equipment.specialMechanics && Object.keys(equipment.specialMechanics).length > 0) {
    const lines: string[] = []
    for (const [key, val] of Object.entries(equipment.specialMechanics)) {
      if (val !== 0) {
        const labelKey = specialLabelKeys[key]
        lines.push(labelKey ? t(labelKey, { n: Math.round(val * 100) }) : `${key}: +${val}`)
      }
    }
    if (lines.length > 0) {
      cards.push({
        id: `aside-equip-special-${equipment.equipmentId}`,
        mark: t('market.aside.buff'),
        title: t('market.effect.specialMechanicTitle', { name: equipment.name }),
        tone: 'gold',
        lines,
      })
    }
  }

  return cards
}

/**
 * 通用 Buff / Debuff 附属卡片构造工具
 * 能够将任意实时 Buff 实例转化为标准统一的 FloatingAsideCard
 */
export function createBuffAsideCard(
  buff: Buff,
  mark?: string
): FloatingAsideCard {
  const t = getT()
  const lines = describeBuffEffectLines(buff)

  const isDebuff =
    (buff.damageRatio !== undefined && buff.damageRatio < 0) ||
    (buff.attackSpeedRatio !== undefined && buff.attackSpeedRatio < 0) ||
    (buff.qiGainMultiplier !== undefined && buff.qiGainMultiplier < 1)

  const tone: FloatingTone = isDebuff ? 'danger' : 'gold'
  const resolvedMark = mark ?? (isDebuff ? t('market.aside.debuff') : t('market.aside.buff'))

  return {
    id: `aside-active-buff-${buff.buffId}`,
    mark: resolvedMark,
    title: buff.name,
    subtitle: buff.stacks > 1 ? t('market.aside.maxStacks', { stacks: formatNumber(buff.stacks) }) : undefined,
    tone,
    lines: lines.length > 0 ? lines : [t('market.effect.ongoing')],
  }
}

/**
 * 物品标准卡片瓦片组件 (ItemTile)
 *
 * 紧凑的修仙网格方块：左上角品类丝带、右上角等阶 Lv、中间名称与右下角拥有数量。
 * 若该物品 ID 已配备图标资源，中间的文本名称会被图标整块替换；未配备时保持文本渲染。
 */
export function ItemTile({
  amount,
  bottomRightLabel,
  category,
  className,
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
      {bottomRight !== null && <span className="item-corner item-corner--bottom-right">{bottomRight}</span>}
    </span>
  )
}

/**
 * 背包物品卡片完整组件 (ItemCard)
 *
 * 结合了 ItemTooltipTarget 与 ItemTile，用于背包格子的标准渲染。
 */
export function ItemCard({ item, definition }: { item: InventoryItem; definition?: ItemDefinition }): React.JSX.Element {
  // 订阅语言状态：resolveItemMetadata 返回的文案随语言切换刷新。
  const t = useT()
  const meta = resolveItemMetadata(item.itemId)
  const metadata = withItemGrade(
    meta ?? fallbackMetadata(item.itemId),
    item.tier ?? meta?.level ?? 1,
  )
  return (
    <ItemTooltipTarget amount={item.amount} className="inventory-item-anchor" definition={definition} itemId={item.itemId} metadata={metadata}>
      <ItemTile
        amount={item.amount}
        category={resolveItemCategoryLabel(item.category, t)}
        className="inventory-cell"
        itemId={item.itemId}
        level={metadata.level}
        name={item.name}
        rarity={metadata.rarity}
      />
    </ItemTooltipTarget>
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
  className = '',
}: {
  /** 物品唯一 ID */
  itemId: string
  /** 涉及数量（提供时显示复合胶囊） */
  amount?: number
  /** 覆写显示文本（默认取物品名称） */
  label?: string
  /** 额外样式类名 */
  className?: string
}): React.JSX.Element {
  // 订阅语言状态：resolveItemMetadata 返回的文案随语言切换刷新。
  useT()
  const metadata = resolveItemMetadata(itemId) ?? fallbackMetadata(itemId)
  const definition = useGameStore((state) => findItemInCatalog(state.items, itemId))
  const displayLabel = label ?? metadata.label
  const tone = rarityToTone(metadata.rarity)

  if (amount !== undefined) {
    return (
      <ItemTooltipTarget
        amount={amount}
        className={`item-reference-compound pill-tone--${tone} ${className}`.trim()}
        definition={definition}
        itemId={itemId}
      >
        <ItemIcon itemId={itemId} />
        <span className="capsule-label">{displayLabel}</span>
        <span className="underlined-value">
          × <UnitNumber disableTooltip value={amount} />
        </span>
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
