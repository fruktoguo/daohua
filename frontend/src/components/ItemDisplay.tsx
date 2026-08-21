import { type ReactNode } from 'react'
import {
  describeCombatTrigger,
  formatChineseSplitNumber,
  formatExactNumber,
  formatNumber,
} from '../game/format'
import { useGameStore } from '../game/gameStore'
import { resolveItemMetadata, withItemGrade, type ItemMetadata, type ItemRarity } from '../game/ids'
import { getT, useT } from '../i18n'
import type { CombatEquipmentDefinition, InventoryItem, ItemDefinition } from '../game/types'
import { type FloatingAsideCard, FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import { ValueText } from './ValueText'
import { UnitNumber } from './UnitNumber'

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
  bottomRightLabel?: ReactNode
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
  const t = useT()
  const baseMetadata = metadataOverride ?? resolveItemMetadata(itemId) ?? fallbackMetadata(itemId)
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
    { label: t('market.tooltip.category'), value: metadata.category, tone: 'muted' },
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
  if (metadata.source) {
    stats.push({ label: t('market.tooltip.source'), value: metadata.source, tone: 'jade' })
  }

  // 附属卡片：单独数量详情气泡 + 额外用途说明或特殊词条
  const asideCards: FloatingAsideCard[] = []
  if (amount !== undefined) {
    asideCards.push({
      mark: t('market.tooltip.quantityMark'),
      title: t('market.tooltip.quantityTitle'),
      tone: 'gold',
      content: (
        <div className="xiuxian-quantity-aside-body">
          <div className="xiuxian-quantity-aside-split">{formatChineseSplitNumber(amount)}</div>
          <div className="xiuxian-quantity-aside-compact">
            约 <UnitNumber disableTooltip value={amount} />
          </div>
          <div className="xiuxian-quantity-aside-exact">
            {t('market.tooltip.exactQuantity')}：{formatExactNumber(amount)}
          </div>
        </div>
      ),
    })
  }
  if (metadata.usage) {
    asideCards.push({
      mark: t('market.tooltip.usageMark'),
      title: t('market.tooltip.usageTitle'),
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
          subtitle={metadata.source ? t('market.tooltip.sourcePrefix', { source: metadata.source }) : undefined}
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
  // 非渲染辅助路径：词条行由调用方组件的重渲染携带语言切换。
  const t = getT()
  const lines: string[] = []
  if (definition.hitpointRestore > 0) lines.push(t('market.effect.restoreHp', { value: formatNumber(definition.hitpointRestore) }))
  if (definition.manapointRestore > 0) lines.push(t('market.effect.restoreMp', { value: formatNumber(definition.manapointRestore) }))
  if (definition.recoverySeconds > 0) lines.push(t('market.effect.recoveryDuration', { seconds: formatNumber(definition.recoverySeconds) }))
  if (definition.cooldownSeconds > 0) lines.push(t('market.effect.cooldown', { seconds: formatNumber(definition.cooldownSeconds) }))
  if (definition.triggerAtCombatStart) lines.push(t('market.effect.useAtCombatStart'))
  for (const trigger of definition.defaultCombatTriggers ?? []) lines.push(t('market.effect.defaultCondition', { trigger: describeCombatTrigger(trigger) }))
  if (
    (definition.defaultCombatTriggers?.length ?? 0) === 0 &&
    (definition.hitpointRestore > 0 || definition.manapointRestore > 0) &&
    definition.triggerHpRatio > 0
  ) {
    lines.push(t('market.effect.autoUseBelowHp', { percent: formatNumber(Math.round(definition.triggerHpRatio * 100)) }))
  }
  for (const buff of definition.buffEffects ?? []) {
    const heading = [
      buff.name,
      buff.durationSeconds > 0 ? t('market.effect.secondsShort', { seconds: formatNumber(buff.durationSeconds) }) : '',
      buff.maxStacks > 1 ? t('market.effect.maxStacks', { stacks: formatNumber(buff.maxStacks) }) : '',
    ]
      .filter(Boolean)
      .join(' · ')
    if (heading) lines.push(heading)
    const qi = multiplierPercent(buff.qiGainMultiplier)
    const gathering = multiplierPercent(buff.gatheringOutputMultiplier)
    const alchemy = multiplierPercent(buff.alchemyOutputMultiplier)
    if (qi) lines.push(t('market.effect.qiGainBonus', { value: qi }))
    if (buff.attackLevelRatio !== 0) lines.push(`${t('market.stat.accuracy')} ${signedPercent(buff.attackLevelRatio)}`)
    if (buff.damageRatio !== 0) lines.push(`${t('market.stat.damage')} ${signedPercent(buff.damageRatio)}`)
    if (buff.attackSpeedRatio !== 0) lines.push(`${t('market.stat.attackSpeed')} ${signedPercent(buff.attackSpeedRatio)}`)
    if (gathering) lines.push(t('market.effect.gatheringOutput', { value: gathering }))
    if (alchemy) lines.push(t('market.effect.alchemyOutput', { value: alchemy }))
  }
  return lines
}

function describeEquipmentEffects(equipment?: CombatEquipmentDefinition): string[] {
  if (!equipment) return []
  // 非渲染辅助路径：词条行由调用方组件的重渲染携带语言切换。
  const t = getT()
  const stats = equipment.stats
  const lines: string[] = []
  const addPercent = (label: string, value: number) => {
    if (value !== 0) lines.push(`${label} ${signedPercent(value)}`)
  }
  const addNumber = (label: string, value: number) => {
    if (value !== 0) lines.push(`${label} ${formatNumber(value)}`)
  }
  addPercent(t('market.stat.accuracy'), stats.accuracyRatio)
  addPercent(t('market.stat.damage'), stats.damageRatio)
  addPercent(t('market.stat.attackSpeed'), stats.attackSpeedRatio)
  addPercent(t('market.stat.evasion'), stats.evasionRatio)
  addNumber(t('market.stat.armor'), stats.armor)
  addNumber(t('market.stat.waterRes'), stats.waterResistance)
  addNumber(t('market.stat.natureRes'), stats.natureResistance)
  addNumber(t('market.stat.fireRes'), stats.fireResistance)
  addNumber(t('market.stat.tenacity'), stats.tenacity)
  addPercent(t('market.stat.penetration'), stats.penetrationRatio)
  addPercent(t('market.stat.pierceChance'), stats.pierceChance)
  addPercent(t('market.stat.curse'), stats.curseRatio)
  addPercent(t('market.stat.fury'), stats.furyRatio)
  addPercent(t('market.stat.weaken'), stats.weakenRatio)
  addPercent(t('market.stat.lifeSteal'), stats.lifeStealRatio)
  addPercent(t('market.stat.armorDamage'), stats.armorDamageRatio)
  addPercent(t('market.stat.hpDrain'), stats.hpDrainRatio)
  addPercent(t('market.stat.critChance'), stats.criticalChance)
  addPercent(t('market.stat.critDamage'), stats.criticalDamageRatio)
  addPercent(t('market.stat.critAvoidance'), stats.criticalAvoidance)
  addPercent(t('market.stat.damageAbsorption'), stats.damageAbsorptionRatio)
  if (stats.attackIntervalMilliseconds > 0) lines.push(t('market.stat.attackInterval', { seconds: (stats.attackIntervalMilliseconds / 1000).toFixed(2) }))
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
  const bottomRight =
    bottomRightLabel ??
    (amount === undefined ? null : <UnitNumber disableTooltip prefix="×" value={amount} />)
  return (
    <span className={`item-tile ${className}`.trim()} data-rarity={rarity}>
      <span className="item-type-ribbon" aria-hidden="true"><span>{category}</span></span>
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

export function ItemCard({ item, definition }: { item: InventoryItem; definition?: ItemDefinition }): React.JSX.Element {
  // 订阅语言状态：resolveItemMetadata 返回的文案随语言切换刷新。
  useT()
  const meta = resolveItemMetadata(item.itemId)
  const metadata = withItemGrade(
    meta ?? fallbackMetadata(item.itemId),
    item.tier ?? meta?.level ?? 1,
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
