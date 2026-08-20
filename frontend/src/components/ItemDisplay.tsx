import { type ReactNode } from 'react'
import { describeCombatTrigger, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { ITEM_META, withItemGrade, type ItemMetadata, type ItemRarity } from '../game/ids'
import type { CombatEquipmentDefinition, InventoryItem, ItemDefinition } from '../game/types'
import { type FloatingAsideCard, FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import { ValueText } from './ValueText'

interface ItemTooltipTargetProps {
  itemId: string
  amount?: number
  children: ReactNode
  className?: string
  metadata?: ItemMetadata
  definition?: ItemDefinition
  tabIndex?: number
}

interface ItemTileProps {
  amount?: number
  bottomRightLabel?: string
  category: string
  className: string
  level?: number
  name: string
  rarity: ItemRarity
}

function fallbackMetadata(itemId: string): ItemMetadata {
  return {
    label: itemId,
    glyph: '·',
    tone: 'gold',
    category: '物品',
    rarity: 'mundane',
    rarityLabel: '未知',
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

export function ItemTooltipTarget({
  itemId,
  amount,
  children,
  className = '',
  metadata: metadataOverride,
  definition,
  tabIndex = 0,
}: ItemTooltipTargetProps): React.JSX.Element {
  const baseMetadata = metadataOverride ?? ITEM_META[itemId] ?? fallbackMetadata(itemId)
  const rawMetadata = definition
    ? {
        ...baseMetadata,
        label: definition.name,
        category: definition.category,
        level: definition.tier,
        description: definition.description,
      }
    : baseMetadata
  const metadata =
    rawMetadata.level === undefined ? rawMetadata : withItemGrade(rawMetadata, rawMetadata.level)
  const equipment = useGameStore((state) =>
    state.equipment.find((entry) => entry.equipmentId === itemId)
  )
  const effectLines = [...describeItemEffects(definition), ...describeEquipmentEffects(equipment)]

  const stats: { label: string; value: ReactNode; tone?: FloatingTone }[] = [
    { label: '品类', value: metadata.category, tone: 'muted' },
  ]
  if (metadata.level !== undefined) {
    stats.push({ label: '等阶', value: `Lv. ${metadata.level}`, tone: 'gold' })
  }
  if (amount !== undefined) {
    stats.push({ label: '持有', value: formatNumber(amount), tone: 'gold' })
  }
  if (metadata.source) {
    stats.push({ label: '产出', value: metadata.source, tone: 'jade' })
  }

  // 附属卡片：如果存在额外用途说明或特殊词条，可以作为附属气泡组合展示
  const asideCards: FloatingAsideCard[] = []
  if (metadata.usage) {
    asideCards.push({
      mark: '用途',
      title: '使用法门',
      tone: 'qi',
      lines: [metadata.usage],
    })
  }

  const tone = rarityToTone(metadata.rarity)

  return (
    <FloatingPopover
      asideCards={asideCards.length > 0 ? asideCards : undefined}
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={metadata.rarityLabel}
          badgeTone={tone}
          description={metadata.description}
          lines={effectLines.map((line) => (
            <ValueText className="effect-value-capsule" key={line} text={line} />
          ))}
          stats={stats}
          subtitle={metadata.source ? `来源：${metadata.source}` : undefined}
          title={metadata.label}
        />
      }
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
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

function describeItemEffects(definition?: ItemDefinition): string[] {
  if (!definition) return []
  const lines: string[] = []
  if (definition.hitpointRestore > 0) lines.push(`恢复生命 ${formatNumber(definition.hitpointRestore)}`)
  if (definition.manapointRestore > 0) lines.push(`恢复灵力 ${formatNumber(definition.manapointRestore)}`)
  if (definition.recoverySeconds > 0) lines.push(`恢复持续 ${definition.recoverySeconds} 秒`)
  if (definition.cooldownSeconds > 0) lines.push(`冷却 ${definition.cooldownSeconds} 秒`)
  if (definition.triggerAtCombatStart) lines.push('进入战斗时使用')
  for (const trigger of definition.defaultCombatTriggers ?? []) lines.push(`默认条件 ${describeCombatTrigger(trigger)}`)
  if (
    (definition.defaultCombatTriggers?.length ?? 0) === 0 &&
    (definition.hitpointRestore > 0 || definition.manapointRestore > 0) &&
    definition.triggerHpRatio > 0
  ) {
    lines.push(`默认在生命低于 ${Math.round(definition.triggerHpRatio * 100)}% 时使用`)
  }
  for (const buff of definition.buffEffects ?? []) {
    const heading = [
      buff.name,
      buff.durationSeconds > 0 ? `${buff.durationSeconds} 秒` : '',
      buff.maxStacks > 1 ? `最多 ${buff.maxStacks} 层` : '',
    ]
      .filter(Boolean)
      .join(' · ')
    if (heading) lines.push(heading)
    const qi = multiplierPercent(buff.qiGainMultiplier)
    const gathering = multiplierPercent(buff.gatheringOutputMultiplier)
    const alchemy = multiplierPercent(buff.alchemyOutputMultiplier)
    if (qi) lines.push(`修炼收益 ${qi}`)
    if (buff.attackLevelRatio !== 0) lines.push(`命中 ${signedPercent(buff.attackLevelRatio)}`)
    if (buff.damageRatio !== 0) lines.push(`伤害 ${signedPercent(buff.damageRatio)}`)
    if (buff.attackSpeedRatio !== 0) lines.push(`攻击速度 ${signedPercent(buff.attackSpeedRatio)}`)
    if (gathering) lines.push(`采集产出 ${gathering}`)
    if (alchemy) lines.push(`炼丹产出 ${alchemy}`)
  }
  return lines
}

function describeEquipmentEffects(equipment?: CombatEquipmentDefinition): string[] {
  if (!equipment) return []
  const stats = equipment.stats
  const lines: string[] = []
  const addPercent = (label: string, value: number) => {
    if (value !== 0) lines.push(`${label} ${signedPercent(value)}`)
  }
  const addNumber = (label: string, value: number) => {
    if (value !== 0) lines.push(`${label} ${formatNumber(value)}`)
  }
  addPercent('命中', stats.accuracyRatio)
  addPercent('伤害', stats.damageRatio)
  addPercent('攻击速度', stats.attackSpeedRatio)
  addPercent('闪避', stats.evasionRatio)
  addNumber('护甲', stats.armor)
  addNumber('水抗', stats.waterResistance)
  addNumber('木抗', stats.natureResistance)
  addNumber('火抗', stats.fireResistance)
  addNumber('控制抗性', stats.tenacity)
  addPercent('抗性穿透', stats.penetrationRatio)
  addPercent('穿透概率', stats.pierceChance)
  addPercent('蚀心', stats.curseRatio)
  addPercent('沸血', stats.furyRatio)
  addPercent('衰弱', stats.weakenRatio)
  addPercent('生命偷取', stats.lifeStealRatio)
  addPercent('护甲增伤', stats.armorDamageRatio)
  addPercent('生命汲取', stats.hpDrainRatio)
  addPercent('暴击率', stats.criticalChance)
  addPercent('暴击增伤', stats.criticalDamageRatio)
  addPercent('免暴率', stats.criticalAvoidance)
  addPercent('伤害吸收', stats.damageAbsorptionRatio)
  if (stats.attackIntervalMilliseconds > 0) lines.push(`攻击间隔 ${(stats.attackIntervalMilliseconds / 1000).toFixed(2)} 秒`)
  return lines
}

export function ItemTile({
  amount,
  bottomRightLabel,
  category,
  className,
  level,
  name,
  rarity,
}: ItemTileProps): React.JSX.Element {
  const bottomRight = bottomRightLabel ?? (amount === undefined ? null : `×${formatNumber(amount)}`)
  return (
    <span className={`item-tile ${className}`.trim()} data-rarity={rarity}>
      <span className="item-type-ribbon" aria-hidden="true"><span>{category}</span></span>
      {level !== undefined && <span className="item-corner item-corner--top-right">Lv. {level}</span>}
      <strong className="item-tile-name">{name}</strong>
      {bottomRight !== null && <span className="item-corner item-corner--bottom-right">{bottomRight}</span>}
    </span>
  )
}

export function ItemCard({ item, definition }: { item: InventoryItem; definition?: ItemDefinition }): React.JSX.Element {
  const metadata = withItemGrade(
    ITEM_META[item.itemId] ?? fallbackMetadata(item.itemId),
    item.tier ?? ITEM_META[item.itemId]?.level ?? 1,
  )
  return (
    <ItemTooltipTarget amount={item.amount} className="inventory-item-anchor" definition={definition} itemId={item.itemId} metadata={metadata}>
      <ItemTile
        amount={item.amount}
        category={item.category}
        className="inventory-cell"
        level={metadata.level}
        name={item.name}
        rarity={metadata.rarity}
      />
    </ItemTooltipTarget>
  )
}

export function ItemReference({
  itemId,
  amount,
  label,
  className = '',
}: {
  itemId: string
  amount?: number
  label?: string
  className?: string
}): React.JSX.Element {
  const metadata = ITEM_META[itemId] ?? fallbackMetadata(itemId)
  const definition = useGameStore((state) => state.items.find((item) => item.itemId === itemId))
  return (
    <ItemTooltipTarget amount={amount} className={`item-reference ${className}`.trim()} definition={definition} itemId={itemId}>
      {label ?? metadata.label}
    </ItemTooltipTarget>
  )
}
