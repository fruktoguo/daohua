import { type ReactNode, useEffect, useId, useMemo, useState } from 'react'
import { formatNumber } from '../game/format'
import { resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata } from '../game/ids'
import { useT, type TFunction } from '../i18n'
import type { ItemDefinition } from '../game/types'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { Modal } from './Modal'

export interface ItemPickerEntry {
  itemId: string
  name: string
  description: string
  category: string
  tier: number
  amount: number
  definition?: ItemDefinition
}

interface ItemGridPickerProps {
  open: boolean
  title: string
  items: readonly ItemPickerEntry[]
  value: string | null
  onClose: () => void
  onSelect: (itemId: string | null) => void
  allowEmpty?: boolean
  emptyLabel?: string
}

// 未注册物品的兜底元数据：名称/品类/描述保留服务端数据，来源与用途走 market 域词典。
function fallbackMetadata(item: ItemPickerEntry, t: TFunction): ItemMetadata {
  const tier = Math.max(1, item.tier)
  const grade = resolveItemGrade(tier)
  return {
    label: item.name,
    glyph: item.name.slice(0, 1),
    tone: 'gold',
    category: item.category,
    rarity: grade.rarity,
    rarityLabel: grade.label,
    level: tier,
    description: item.description,
    source: t('market.itemFallback.catalogSource'),
    usage: t('market.itemFallback.usableFor', { category: item.category }),
  }
}

function getMetadata(item: ItemPickerEntry, t: TFunction): ItemMetadata {
  const registered = resolveItemMetadata(item.itemId)
  if (!registered) return fallbackMetadata(item, t)
  return withItemGrade({
    ...registered,
    label: item.name || registered.label,
    category: item.category || registered.category,
    level: item.tier > 0 ? item.tier : registered.level,
    description: item.description || registered.description,
  }, item.tier > 0 ? item.tier : registered.level ?? 1)
}

function ItemGridCell({
  item,
  selected,
  onSelect,
}: {
  item: ItemPickerEntry
  selected: boolean
  onSelect: () => void
}): React.JSX.Element {
  const t = useT()
  const metadata = getMetadata(item, t)
  return (
    <ItemTooltipTarget
      amount={item.amount}
      className="item-grid-picker-tooltip"
      definition={item.definition}
      itemId={item.itemId}
      metadata={metadata}
      tabIndex={-1}
    >
      <button
        aria-label={t('market.browse.itemOwned', { name: item.name, count: formatNumber(item.amount) })}
        aria-selected={selected}
        className={`item-grid-picker-card${selected ? ' is-selected' : ''}${item.amount <= 0 ? ' is-empty' : ''}`}
        disabled={item.amount <= 0}
        onClick={onSelect}
        role="option"
        type="button"
      >
        <ItemTile
          amount={item.amount}
          category={item.category}
          className="item-grid-picker-cell"
          level={metadata.level}
          name={item.name}
          rarity={metadata.rarity}
        />
      </button>
    </ItemTooltipTarget>
  )
}

export function ItemGridPicker({
  open,
  title,
  items,
  value,
  onClose,
  onSelect,
  allowEmpty = true,
  emptyLabel,
}: ItemGridPickerProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const [query, setQuery] = useState('')
  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')
  const visibleItems = useMemo(() => {
    if (!normalizedQuery) return items
    return items.filter(item => `${item.name} ${item.category} ${item.description}`
      .toLocaleLowerCase('zh-CN')
      .includes(normalizedQuery))
  }, [items, normalizedQuery])

  useEffect(() => {
    if (open) setQuery('')
  }, [open])

  if (!open) return null

  // 默认参数不能在解构处调用 hook，收拢到组件体内解析。
  const emptyText = emptyLabel ?? t('common.pickerEmpty')
  const select = (itemId: string | null) => {
    onSelect(itemId)
    onClose()
  }

  return (
    <Modal labelledBy={titleId} onClose={onClose} open size="large" className="item-grid-picker-modal">
      <header className="item-grid-picker-header">
        <div>
          <h2 id={titleId}>{title}</h2>
        </div>
        <input
          aria-label={t('market.picker.searchTitleTemplate', { title })}
          autoComplete="off"
          onChange={event => setQuery(event.target.value)}
          placeholder={t('market.browse.searchItems')}
          type="search"
          value={query}
        />
      </header>
      <div className="item-grid-picker-grid" role="listbox" aria-label={title}>
        {allowEmpty && (
          <button
            aria-selected={value === null}
            className={`item-grid-picker-card item-grid-picker-card--empty${value === null ? ' is-selected' : ''}`}
            onClick={() => select(null)}
            role="option"
            type="button"
          >
            <span className="item-grid-picker-empty-cell">
              <strong>{emptyText}</strong>
            </span>
          </button>
        )}
        {visibleItems.map(item => (
          <ItemGridCell
            item={item}
            key={item.itemId}
            onSelect={() => select(item.itemId)}
            selected={value === item.itemId}
          />
        ))}
      </div>
      {visibleItems.length === 0 && <p className="item-grid-picker-empty">{t('market.picker.noAvailableMatch')}</p>}
    </Modal>
  )
}

export function ItemUseSlot({
  cooldownOverlay,
  conditionCount = 0,
  item,
  onClick,
  onCondition,
}: {
  cooldownOverlay?: ReactNode
  conditionCount?: number
  item: ItemPickerEntry | null
  onClick: () => void
  onCondition?: () => void
}): React.JSX.Element {
  const t = useT()
  if (!item) {
    return (
      <button aria-label={t('common.selectItem')} className="item-use-slot item-use-slot--empty" onClick={onClick} type="button">
        <span>{t('market.picker.selectItemShort')}</span>
      </button>
    )
  }

  const metadata = getMetadata(item, t)
  const depleted = item.amount <= 0
  return (
    <ItemTooltipTarget
      amount={item.amount}
      className="item-use-slot-tooltip"
      definition={item.definition}
      itemId={item.itemId}
      metadata={metadata}
      tabIndex={-1}
    >
      <span className={`item-use-slot-shell${depleted ? ' is-depleted' : ''}`}>
        <button
          aria-label={t('market.picker.currentItem', { name: depleted ? `${item.name}${t('market.picker.insufficientAmount')}` : item.name })}
          className={`item-use-slot${depleted ? ' item-use-slot--depleted' : ''}`}
          onClick={onClick}
          type="button"
        >
          <ItemTile
            amount={item.amount}
            category={item.category}
            className="item-use-slot-cell"
            level={metadata.level}
            name={item.name}
            rarity={metadata.rarity}
          />
        </button>
        {cooldownOverlay}
        {onCondition && (
          <button
            aria-label={t('market.picker.editConditionsWithCount', { name: item.name, count: formatNumber(conditionCount) })}
            className="item-slot-inline-action item-slot-inline-action--condition"
            onClick={onCondition}
            title={t('market.picker.editConditions')}
            type="button"
          >{t('market.picker.conditionLabel', { count: conditionCount > 0 ? formatNumber(conditionCount) : '' })}</button>
        )}
      </span>
    </ItemTooltipTarget>
  )
}
