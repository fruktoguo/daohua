import { type ReactNode, useEffect, useId, useMemo, useState } from 'react'
import { formatNumber } from '../game/format'
import { resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata } from '../game/ids'
import { useT, type TFunction } from '../i18n'
import type { ItemDefinition } from '../game/types'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { Modal } from './Modal'

/**
 * 物品网格选择条目定义
 */
export interface ItemPickerEntry {
  /** 物品唯一 ID */
  itemId: string
  /** 物品名称 */
  name: string
  /** 物品描述 */
  description: string
  /** 物品品类 */
  category: string
  /** 物品等阶/品阶 */
  tier: number
  /** 玩家背包持存数量 */
  amount: number
  /** 可选服务端原始定义 */
  definition?: ItemDefinition
}

/**
 * 物品网格选择模态窗 Props
 */
interface ItemGridPickerProps {
  /** 弹窗展开状态 */
  open: boolean
  /** 弹窗标题 */
  title: string
  /** 可供选择的物品列表 */
  items: readonly ItemPickerEntry[]
  /** 当前选中的物品 ID (null 表示空选/卸下) */
  value: string | null
  /** 关闭弹窗回调 */
  onClose: () => void
  /** 选中物品回调 */
  onSelect: (itemId: string | null) => void
  /** 是否提供空选/卸下槽位按钮（默认 true） */
  allowEmpty?: boolean
  /** 空选项文本标签 */
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

/** 物品网格单元格组件 */
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

/**
 * 物品网格选择器模态窗组件 (ItemGridPicker)
 *
 * 用于快捷药品装配、丹药吞服、战斗法宝选用等网格式物品检索与选择。
 * 支持搜索过滤、持存数量气泡查看、空选卸下功能。
 */
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

/**
 * 物品装配/快捷使用槽位组件 (ItemUseSlot)
 *
 * 渲染单个技能栏/丹药槽位的物品方块：
 * 1. 空槽状态展示虚线占位符与“选用物品”引导；
 * 2. 已装配状态展示物品卡片，支持冷却倒计时蒙层 (cooldownOverlay)；
 * 3. 支持触发条件编辑按钮入口 (onCondition)，展示当前配置的条件数量。
 */
export function ItemUseSlot({
  cooldownOverlay,
  conditionCount = 0,
  item,
  onClick,
  onCondition,
}: {
  /** 冷却动画或倒计时蒙层节点 */
  cooldownOverlay?: ReactNode
  /** 已配置的战斗触发条件数量 */
  conditionCount?: number
  /** 当前装配的物品信息 (null 为空槽) */
  item: ItemPickerEntry | null
  /** 点击槽位选择/更换物品回调 */
  onClick: () => void
  /** 点击编辑战斗触发条件回调 */
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
