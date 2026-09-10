import { type ReactNode, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { formatNumber } from '../game/format'
import { resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, withItemGrade, type ItemMetadata } from '../game/ids'
import { useGameStore } from '../game/gameStore'
import { useT, type TFunction } from '../i18n'
import type { ItemDefinition, QueueEntry } from '../game/types'
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

// 未注册物品的兜底元数据：名称/品类/描述保留服务端数据。
function fallbackMetadata(item: ItemPickerEntry, t: TFunction): ItemMetadata {
  const tier = Math.max(1, item.tier)
  const grade = resolveItemGrade(tier)
  return {
    label: item.name,
    glyph: item.name.slice(0, 1),
    tone: 'gold',
    category: resolveItemCategoryLabel(item.category, t),
    rarity: grade.rarity,
    rarityLabel: grade.label,
    level: tier,
    description: item.description,
  }
}

function getMetadata(item: ItemPickerEntry, t: TFunction): ItemMetadata {
  const registered = resolveItemMetadata(item.itemId)
  if (!registered) return fallbackMetadata(item, t)
  return withItemGrade({
    ...registered,
    label: item.name || registered.label,
    category: resolveItemCategoryLabel(item.category || registered.category, t),
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
          category={resolveItemCategoryLabel(item.category, t)}
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
    return items.filter(item => {
      const catLabel = resolveItemCategoryLabel(item.category, t)
      return `${item.name} ${item.category} ${catLabel} ${item.description}`
        .toLocaleLowerCase('zh-CN')
        .includes(normalizedQuery)
    })
  }, [items, normalizedQuery, t])

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
 * 持续消耗品动作时间倒计时遮罩。
 * 服务端只在整轮完成时扣减权威剩余时间；当前轮通过行动进度平滑推导显示值，暂停时不会继续流逝。
 */
function SustainedDurationOverlay({
  activeEntry,
  remainingDurationSeconds,
  totalDurationSeconds,
}: {
  activeEntry?: QueueEntry | null
  remainingDurationSeconds: number
  totalDurationSeconds: number
}): React.JSX.Element {
  const overlayRef = useRef<HTMLSpanElement>(null)
  const textRef = useRef<HTMLSpanElement>(null)
  const lastQueueSyncPerformanceTime = useGameStore(state => state.lastQueueSyncPerformanceTime ?? state.lastSyncPerformanceTime)

  useLayoutEffect(() => {
    const overlay = overlayRef.current
    if (!overlay) return
    const total = Math.max(1, totalDurationSeconds)
    const actionDuration = Math.max(0, activeEntry?.durationSeconds ?? 0)
    const baseProgress = Math.max(0, activeEntry?.progressSeconds ?? 0)
    const isRunning = activeEntry?.status === 'running' && actionDuration > 0
    let frameId: number | undefined

    const update = () => {
      const elapsed = isRunning
        ? Math.max(0, (performance.now() - lastQueueSyncPerformanceTime) / 1000)
        : 0
      const currentActionProgress = Math.min(actionDuration, baseProgress + elapsed)
      const remaining = Math.max(0, remainingDurationSeconds - currentActionProgress)
      overlay.style.setProperty('--cooldown-ratio', String(Math.min(1, remaining / total)))
      // 只写数字子元素的 textContent：遮罩自身还要承载外圈虚线弧的嵌套层，
      // 直接写遮罩的 textContent 会连子元素一并清空。
      if (textRef.current) textRef.current.textContent = `${Math.ceil(remaining)}s`
      if (isRunning && currentActionProgress < actionDuration) frameId = window.requestAnimationFrame(update)
    }

    update()
    return () => {
      if (frameId !== undefined) window.cancelAnimationFrame(frameId)
    }
  }, [
    activeEntry?.durationSeconds,
    activeEntry?.entryId,
    activeEntry?.progressSeconds,
    activeEntry?.remainingRepeats,
    activeEntry?.status,
    lastQueueSyncPerformanceTime,
    remainingDurationSeconds,
    totalDurationSeconds,
  ])

  // 不复用 combat-ability-cooldown：该类规则定义在 styles/expedition.css 中且在 main.tsx 里后于
  // styles/selection-grid.css 加载，二者对同一元素等权重同名，会静默覆盖本遮罩的颜色与扇形起点。
  // 7px 圆角与溢出裁剪已由 styles.css 的 `.combat-ability-cooldown, .item-slot-duration-overlay` 分组规则统一提供。
  // 数字与「外圈流动虚线弧」拆成两个子元素：外圈需要「扇形裁剪 × 虚线相位」两道遮罩叠加，
  // 用嵌套元素各自持有遮罩即可天然取交集，无需依赖 mask-composite，也保证虚线绝不越出扇形范围。
  return (
    <span aria-hidden="true" className="item-slot-duration-overlay" ref={overlayRef}>
      <span className="item-slot-duration-text" ref={textRef} />
      <span className="item-slot-duration-ring" />
    </span>
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
  activeEntry,
  cooldownOverlay,
  conditionCount = 0,
  item,
  remainingDurationSeconds = 0,
  totalDurationSeconds = 0,
  onClick,
  onCondition,
}: {
  /** 当前实际执行同类行动；用于平滑推导本轮动作时间消耗 */
  activeEntry?: QueueEntry | null
  /** 冷却动画或倒计时蒙层节点 */
  cooldownOverlay?: ReactNode
  /** 已配置的战斗触发条件数量 */
  conditionCount?: number
  /** 当前装配的物品信息 (null 为空槽) */
  item: ItemPickerEntry | null
  /** 已开启的持续消耗品剩余有效秒数（银河奶牛式） */
  remainingDurationSeconds?: number
  /** 持续消耗品单件总有效秒数 */
  totalDurationSeconds?: number
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
  const effectiveTotal = totalDurationSeconds > 0
    ? totalDurationSeconds
    : (item.definition?.durationSeconds ?? 0)
  const isContinuousActive = remainingDurationSeconds > 0

  return (
    <ItemTooltipTarget
      amount={item.amount}
      className="item-use-slot-tooltip"
      definition={item.definition}
      itemId={item.itemId}
      metadata={metadata}
      tabIndex={-1}
    >
      <span
        className={`item-use-slot-shell${depleted ? ' is-depleted' : ''}${isContinuousActive ? ' is-continuous-active' : ''}`}
        data-rarity={metadata.rarity}
      >
        <button
          aria-label={t('market.picker.currentItem', { name: depleted ? `${item.name}${t('market.picker.insufficientAmount')}` : item.name })}
          className={`item-use-slot${depleted ? ' item-use-slot--depleted' : ''}${isContinuousActive ? ' item-use-slot--continuous-active' : ''}`}
          onClick={onClick}
          type="button"
        >
          <ItemTile
            amount={item.amount}
            category={resolveItemCategoryLabel(item.category, t)}
            className="item-use-slot-cell"
            level={metadata.level}
            name={item.name}
            rarity={metadata.rarity}
          />
        </button>
        {isContinuousActive && !cooldownOverlay && (
          <SustainedDurationOverlay
            activeEntry={activeEntry}
            remainingDurationSeconds={remainingDurationSeconds}
            totalDurationSeconds={effectiveTotal}
          />
        )}
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
