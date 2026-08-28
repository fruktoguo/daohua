import { type ReactNode, useMemo } from 'react'
import { describeCombatTrigger, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { convertTierToLevel, resolveDefaultEquipmentDefinition, resolveDefaultItemDefinition, resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata, type ItemRarity } from '../game/ids'
import { getT, useT } from '../i18n'
import type { Buff, CombatEquipmentDefinition, InventoryItem, ItemDefinition } from '../game/types'
import { type FloatingAsideCard, FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import { ValueText } from './ValueText'
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
  /** 物品等阶/品级（展示在右上角） */
  level?: number
  /** 物品名称 */
  name: string
  /** 稀有度品质（决定边框与背景光效） */
  rarity: ItemRarity
}

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
}: ItemTooltipTargetProps): React.JSX.Element {
  const t = useT()
  const itemCatalog = useGameStore((state) => state.items)
  const equipmentCatalog = useGameStore((state) => state.equipment)

  const itemDef = itemDefOverride ?? itemCatalog.find((i) => i.itemId === itemId) ?? resolveDefaultItemDefinition(itemId)
  const eqDef = eqDefOverride ?? equipmentCatalog.find((e) => e.equipmentId === itemId) ?? resolveDefaultEquipmentDefinition(itemId)

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

  // 附属卡片群：解析丹药 Buff、装备特殊机制与传入的副卡片
  const asideCards = useMemo<FloatingAsideCard[]>(() => {
    const list: FloatingAsideCard[] = []
    const itemAsides = resolveItemAsideCards(itemDef, metadata.label)
    const equipAsides = resolveEquipmentAsideCards(eqDef)
    list.push(...itemAsides, ...equipAsides)
    if (extraAsideCards && extraAsideCards.length > 0) {
      list.push(...extraAsideCards)
    }
    return list
  }, [itemDef, eqDef, metadata.label, extraAsideCards])

  return (
    <FloatingPopover
      asideCards={asideCards.length > 0 ? asideCards : undefined}
      closeDelay={120}
      displayContents={displayContents}
      content={
        <FloatingInfoCard
          badge={metadata.rarityLabel}
          badgeTone={tone}
          description={metadata.description}
          lines={effectLines.map((line) => (
            <ValueText className="effect-value-capsule" key={line} text={line} />
          ))}
          stats={stats}
          title={metadata.label}
        />
      }
      gap={8}
      interactive={false}
      openDelay={60}
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

function multiplierPercent(value: number): string | null {
  const delta = value - 1
  return Math.abs(delta) < 0.000001 ? null : signedPercent(delta)
}

/**
 * 提取丹药/消耗品的直接主干效果（留在主卡片）
 * 包含即时回血、即时回蓝、冷却时长、使用条件等
 */
function describeItemEffects(definition?: ItemDefinition): string[] {
  if (!definition) return []
  const t = getT()
  const lines: string[] = []
  if (definition.hitpointRestore > 0) {
    lines.push(t('market.effect.restoreHp', { value: formatNumber(definition.hitpointRestore) }))
  }
  if (definition.manapointRestore > 0) {
    lines.push(t('market.effect.restoreMp', { value: formatNumber(definition.manapointRestore) }))
  }
  if (definition.recoverySeconds > 0) {
    lines.push(t('market.effect.recoveryDuration', { seconds: formatNumber(definition.recoverySeconds) }))
  }
  if (definition.cooldownSeconds > 0) {
    lines.push(t('market.effect.cooldown', { seconds: formatNumber(definition.cooldownSeconds) }))
  }
  if (definition.triggerAtCombatStart) {
    lines.push(t('market.effect.useAtCombatStart'))
  }
  for (const trigger of definition.defaultCombatTriggers ?? []) {
    lines.push(t('market.effect.defaultCondition', { trigger: describeCombatTrigger(trigger) }))
  }
  if (
    (definition.defaultCombatTriggers?.length ?? 0) === 0 &&
    (definition.hitpointRestore > 0 || definition.manapointRestore > 0) &&
    definition.triggerHpRatio > 0
  ) {
    lines.push(t('market.effect.autoUseBelowHp', { percent: formatNumber(Math.round(definition.triggerHpRatio * 100)) }))
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
      mark: t('market.aside.cultivationBuff'),
      title: definition.name || fallbackName || t('market.aside.cultivationBuff'),
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
      mark: t('market.aside.gatheringBuff'),
      title: definition.name || fallbackName || t('market.aside.gatheringBuff'),
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
      mark: t('market.aside.alchemyBuff'),
      title: definition.name || fallbackName || t('market.aside.alchemyBuff'),
      tone: 'gold',
      lines,
    })
  }

  // 4. 战斗丹药附带的 Buff 效果 (如止血散、顺气丸)
  for (const buff of definition.buffEffects ?? []) {
    const buffLines: string[] = []
    const qi = multiplierPercent(buff.qiGainMultiplier)
    const gathering = multiplierPercent(buff.gatheringOutputMultiplier)
    const alchemy = multiplierPercent(buff.alchemyOutputMultiplier)
    if (qi) buffLines.push(t('market.effect.qiGainBonus', { value: qi }))
    if (buff.attackLevelRatio !== 0) buffLines.push(`${t('market.stat.accuracy')} ${signedPercent(buff.attackLevelRatio)}`)
    if (buff.damageRatio !== 0) buffLines.push(`${t('market.stat.damage')} ${signedPercent(buff.damageRatio)}`)
    if (buff.attackSpeedRatio !== 0) buffLines.push(`${t('market.stat.attackSpeed')} ${signedPercent(buff.attackSpeedRatio)}`)
    if (gathering) buffLines.push(t('market.effect.gatheringOutput', { value: gathering }))
    if (alchemy) buffLines.push(t('market.effect.alchemyOutput', { value: alchemy }))

    const subParts: string[] = []
    if (buff.durationSeconds > 0) {
      subParts.push(t('market.aside.duration', { seconds: formatNumber(buff.durationSeconds) }))
    }
    if (buff.maxStacks > 1) {
      subParts.push(t('market.aside.maxStacks', { stacks: formatNumber(buff.maxStacks) }))
    }

    cards.push({
      id: `aside-buff-${buff.name}`,
      mark: t('market.aside.pillBuff'),
      title: buff.name,
      subtitle: subParts.length > 0 ? subParts.join(' · ') : undefined,
      tone: 'gold',
      lines: buffLines.length > 0 ? buffLines : [t('app.buff.combatBuff')],
    })
  }

  return cards
}

/**
 * 提取装备的主干攻防数值与强化加成（留在主卡片）
 */
function describeEquipmentEffects(
  equipment?: CombatEquipmentDefinition,
  enhancementLevel = 0
): string[] {
  if (!equipment) return []
  const t = getT()
  const lines: string[] = []

  const ratioLabels: Record<string, string> = {
    physicalAttack: '物理攻击',
    spellAttack: '术法攻击',
    damage: '攻击威能',
    physicalArmor: '物理护甲',
    armor: '物理护甲',
    spellResistance: '法术抗性',
    mr: '法术抗性',
    accuracy: '命中',
    hit: '命中',
    evasion: '闪避',
    eva: '闪避',
    criticalChance: '暴击率',
    criticalDamage: '暴击伤害',
    damageAbsorption: '伤害减免',
    actionSpeed: '施法攻速',
    cooldownReduction: '冷却缩减',
  }

  const flatLabels: Record<string, string> = {
    physicalAttack: '物理攻击',
    spellAttack: '术法攻击',
    accuracy: '命中',
    evasion: '闪避',
    maxHitpoints: '生命上限',
    hp: '生命上限',
    maxManapoints: '灵力上限',
    mp: '灵力上限',
    physicalArmor: '物理护甲',
    armor: '物理护甲',
    spellResistance: '法术抗性',
    mr: '法术抗性',
    hitpointRegen: '生命回复',
    hpRegen: '生命回复',
    manapointRegen: '灵力回复',
    mpRegen: '灵力回复',
    criticalChance: '暴击率',
    tenacity: '韧性',
  }

  const hasStructured = Boolean(
    (equipment.baseStatRatios && Object.keys(equipment.baseStatRatios).length > 0) ||
    (equipment.flatStats && Object.keys(equipment.flatStats).length > 0)
  )

  // 攻击间隔（若存在）
  const attackIntervalMs = (equipment as unknown as { attackIntervalMilliseconds?: number }).attackIntervalMilliseconds
  if (attackIntervalMs && attackIntervalMs > 0) {
    lines.push(t('market.stat.attackInterval', { seconds: (attackIntervalMs / 1000).toFixed(2) }))
  }

  if (hasStructured) {
    if (equipment.baseStatRatios) {
      for (const [key, val] of Object.entries(equipment.baseStatRatios)) {
        if (val !== 0) {
          const label = ratioLabels[key] ?? key
          lines.push(`${label} ${signedPercent(val)}`)
        }
      }
    }
    if (equipment.flatStats) {
      for (const [key, val] of Object.entries(equipment.flatStats)) {
        if (val !== 0) {
          const label = flatLabels[key] ?? key
          if (key === 'criticalChance' && val <= 1.0) {
            lines.push(`${label} +${formatNumber(val * 100)}%`)
          } else {
            // 计算强化带来的加成
            const bonusPerLevel = equipment.enhancementBonus
              ? (equipment.enhancementBonus as unknown as Record<string, number>)[key] ?? 0
              : 0
            const totalBonus = bonusPerLevel * enhancementLevel
            if (totalBonus > 0) {
              lines.push(`${label} +${formatNumber(val)} (+${formatNumber(totalBonus)})`)
            } else {
              lines.push(`${label} +${formatNumber(val)}`)
            }
          }
        }
      }
    }
    return lines
  }

  // 兜底回退：当没有结构化字段时读取 stats
  const stats = equipment.stats
  const addPercent = (label: string, value: number) => {
    if (value !== 0) lines.push(`${label} ${signedPercent(value)}`)
  }
  const addNumber = (label: string, value: number) => {
    if (value !== 0) lines.push(`${label} ${formatNumber(value)}`)
  }
  addPercent(t('market.stat.damage'), stats.damageRatio)
  addPercent(t('market.stat.accuracy'), stats.accuracyRatio)
  addPercent(t('market.stat.attackSpeed'), stats.attackSpeedRatio)
  addPercent(t('market.stat.evasion'), stats.evasionRatio)
  addPercent(t('market.stat.armor'), stats.armor)
  addPercent(t('market.stat.magicRes'), stats.magicResistance)
  addNumber(t('market.stat.tenacity'), stats.tenacity)
  addPercent(t('market.stat.penetration'), stats.penetrationRatio)
  addPercent(t('market.stat.lifeSteal'), stats.lifeStealRatio)
  addPercent(t('market.stat.armorDamage'), stats.armorDamageRatio)
  addPercent(t('market.stat.hpDrain'), stats.hpDrainRatio)
  addPercent(t('market.stat.critChance'), stats.criticalChance)
  addPercent(t('market.stat.critDamage'), stats.criticalDamageRatio)
  addPercent(t('market.stat.critAvoidance'), stats.criticalAvoidance)
  addPercent(t('market.stat.damageAbsorption'), stats.damageAbsorptionRatio)
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

  const specialLabels: Record<string, (val: number) => string> = {
    threatRatio: (val) => `额外威胁值 +${Math.round(val * 100)}%`,
    healingBonusRatio: (val) => `额外治疗提升 +${Math.round(val * 100)}%`,
  }

  if (equipment.specialMechanics && Object.keys(equipment.specialMechanics).length > 0) {
    const lines: string[] = []
    for (const [key, val] of Object.entries(equipment.specialMechanics)) {
      if (val !== 0) {
        const describer = specialLabels[key]
        lines.push(describer ? describer(val) : `${key}: +${val}`)
      }
    }
    if (lines.length > 0) {
      cards.push({
        id: `aside-equip-special-${equipment.equipmentId}`,
        mark: t('market.aside.specialMechanic'),
        title: `${equipment.name}·特技`,
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
  const lines: string[] = []
  const qi = multiplierPercent(buff.qiGainMultiplier)
  const gathering = multiplierPercent(buff.gatheringOutputMultiplier ?? 1)
  const alchemy = multiplierPercent(buff.alchemyOutputMultiplier ?? 1)

  if (qi) lines.push(t('market.effect.qiGainBonus', { value: qi }))
  if (buff.damageRatio && buff.damageRatio !== 0) {
    lines.push(`${t('market.stat.damage')} ${signedPercent(buff.damageRatio)}`)
  }
  if (buff.attackLevelRatio && buff.attackLevelRatio !== 0) {
    lines.push(`${t('market.stat.accuracy')} ${signedPercent(buff.attackLevelRatio)}`)
  }
  if (buff.attackSpeedRatio && buff.attackSpeedRatio !== 0) {
    lines.push(`${t('market.stat.attackSpeed')} ${signedPercent(buff.attackSpeedRatio)}`)
  }
  if (gathering) lines.push(t('market.effect.gatheringOutput', { value: gathering }))
  if (alchemy) lines.push(t('market.effect.alchemyOutput', { value: alchemy }))

  if (buff.modifiers) {
    for (const [modKey, modVal] of Object.entries(buff.modifiers)) {
      if (modVal !== 0) {
        lines.push(`${modKey}: ${signedPercent(modVal)}`)
      }
    }
  }

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
    lines: lines.length > 0 ? lines : [t('app.buff.combatBuff')],
  }
}

/**
 * 物品标准卡片瓦片组件 (ItemTile)
 *
 * 紧凑的修仙网格方块：左上角品类丝带、右上角等阶 Lv、中间名称与右下角拥有数量。
 */
export function ItemTile({
  amount,
  bottomRightLabel,
  category,
  className,
  level,
  name,
  rarity,
}: ItemTileProps): React.JSX.Element {
  const categoryLabel = resolveItemCategoryLabel(category)
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
      <strong className="item-tile-name">{name}</strong>
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
        level={metadata.level}
        name={item.name}
        rarity={metadata.rarity}
      />
    </ItemTooltipTarget>
  )
}

/**
 * 文本行内物品引用胶囊组件 (ItemReference)
 *
 * 用于日志、任务描述、制造需求等行内文本中引用特定物品，
 * 点击/悬停弹出完整物品气泡，并显示品质对应的主题色下划线与数量。
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
  const definition = useGameStore((state) => state.items.find((item) => item.itemId === itemId))
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
      {displayLabel}
    </ItemTooltipTarget>
  )
}
