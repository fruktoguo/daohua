import { type MouseEvent, type ReactNode, useLayoutEffect, useMemo, useRef } from 'react'
import { formatNumber } from '../game/format'
import { resolveItemCategoryLabel, resolveItemGrade, resolveItemMetadata, resolveItemSubCategory, withItemGrade, type ItemMetadata } from '../game/ids'
import { useGameStore } from '../game/gameStore'
import { useT, type TFunction } from '../i18n'
import type { CombatEquipmentDefinition, ItemDefinition, QueueEntry } from '../game/types'
import { CandidatePicker, type CandidateOption } from './CandidatePicker'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'

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
  /** 物品等阶 */
  tier: number
  /** 玩家背包持存数量 */
  amount: number
  /** 可选服务端原始定义 */
  definition?: ItemDefinition
  /** 可选装备目录定义（装备类条目悬浮详情使用） */
  equipmentDefinition?: CombatEquipmentDefinition
  /** 资产变体键（强化等级行形如 "+3"）；同 itemId 多变体行以复合键区分 */
  variant?: string | null
  /** 强化等级（装备变体行：悬浮详情按该等级缩放属性预览） */
  enhancementLevel?: number
}

/**
 * 待选条目复合键：itemId + 变体分隔符（与钱包 AssetKey 合入键同构），
 * 保证同物品不同强化等级行在选择器内不互相覆盖。
 */
export function itemPickerEntryKey(itemId: string, variant?: string | null): string {
  return variant ? `${itemId}\u001F${variant}` : itemId
}

/**
 * 物品网格待选列表 Props
 */
interface ItemGridPickerProps {
  /** 待选列表展开状态 */
  open: boolean
  /** 设置目标锚点元素（正在配置的槽位按钮，面板贴靠其定位） */
  anchor: HTMLElement | null
  /** 目标标题 */
  title: string
  /** 可供选择的物品列表 */
  items: readonly ItemPickerEntry[]
  /** 当前选中的条目键（itemId 或 itemId+变体复合键；null 表示空选/卸下） */
  value: string | null
  /** 关闭回调 */
  onClose: () => void
  /** 选中物品回调：回传物品 ID 与完整条目（含变体/强化等级），空选为 null */
  onSelect: (itemId: string | null, entry?: ItemPickerEntry | null) => void
  /** 是否提供空选/卸下槽位按钮（默认 true） */
  allowEmpty?: boolean
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

/**
 * 物品网格待选列表组件 (ItemGridPicker)
 *
 * 用于快捷药品装配、丹药吞服、战斗法宝选用等网格式物品检索与选择。
 * 渲染为贴靠目标槽位的通用待选列表（CandidatePicker），
 * 支持按需搜索过滤、持存数量气泡查看、空选卸下功能。
 */
export function ItemGridPicker({
  open,
  anchor,
  title,
  items,
  value,
  onClose,
  onSelect,
  allowEmpty = true,
}: ItemGridPickerProps): React.JSX.Element {
  const t = useT()
  const options = useMemo<readonly CandidateOption[]>(() => items.map(item => {
    const metadata = getMetadata(item, t)
    const categoryLabel = resolveItemCategoryLabel(
      resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.category), t)
    return {
      key: itemPickerEntryKey(item.itemId, item.variant),
      tile: (
        <ItemTile
          amount={item.amount}
          category={categoryLabel}
          className="item-grid-picker-cell"
          enhancementLevel={item.enhancementLevel}
          itemId={item.itemId}
          level={metadata.level}
          name={item.name}
          rarity={metadata.rarity}
        />
      ),
      disabled: item.amount <= 0,
      ariaLabel: t('market.browse.itemOwned', { name: item.name, count: formatNumber(item.amount) }),
      searchText: `${item.name} ${item.category} ${categoryLabel} ${item.description}`,
      wrapCell: cell => (
        <ItemTooltipTarget
          amount={item.amount}
          className="item-grid-picker-tooltip"
          definition={item.definition}
          enhancementLevel={item.enhancementLevel}
          equipmentDefinition={item.equipmentDefinition}
          itemId={item.itemId}
          metadata={metadata}
          tabIndex={-1}
        >
          {cell}
        </ItemTooltipTarget>
      ),
    }
  }), [items, t])

  return (
    <CandidatePicker
      allowEmpty={allowEmpty}
      anchor={anchor}
      onClose={onClose}
      onSelect={key => {
        const entry = key === null ? null : items.find(item => itemPickerEntryKey(item.itemId, item.variant) === key) ?? null
        onSelect(entry?.itemId ?? null, entry)
      }}
      open={open}
      options={options}
      title={title}
      value={value}
    />
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
    const now = performance.now()
    const elapsed = isRunning ? Math.max(0, (now - lastQueueSyncPerformanceTime) / 1000) : 0
    const progressAtStart = Math.min(actionDuration, baseProgress + elapsed)

    const renderText = (progressSec: number) => {
      const remaining = Math.max(0, remainingDurationSeconds - progressSec)
      // 只写数字子元素的 textContent：遮罩自身还要承载外圈虚线弧的嵌套层，
      // 直接写遮罩的 textContent 会连子元素一并清空。
      const text = `${Math.ceil(remaining)}s`
      if (textRef.current && textRef.current.textContent !== text) {
        textRef.current.textContent = text
      }
    }

    // 扇形遮罩经 @property 注册的 --cooldown-ratio 走 WAAPI 动画时间轴：
    // 当前轮内剩余时长随行动进度线性流逝，一次动画直达轮末，不再逐帧 setProperty。
    const ratioFrom = Math.min(1, Math.max(0, remainingDurationSeconds - progressAtStart) / total)
    overlay.style.setProperty('--cooldown-ratio', String(ratioFrom))
    let anim: Animation | undefined
    let interval: number | undefined
    if (isRunning && progressAtStart < actionDuration) {
      const ratioEnd = Math.min(1, Math.max(0, remainingDurationSeconds - actionDuration) / total)
      anim = overlay.animate(
        [{ '--cooldown-ratio': String(ratioFrom) }, { '--cooldown-ratio': String(ratioEnd) }],
        { duration: (actionDuration - progressAtStart) * 1000, easing: 'linear', fill: 'forwards' },
      )
      renderText(progressAtStart)
      interval = window.setInterval(() => {
        const progress = Math.min(actionDuration, progressAtStart + (performance.now() - now) / 1000)
        renderText(progress)
        if (progress >= actionDuration && interval !== undefined) window.clearInterval(interval)
      }, 250)
    } else {
      renderText(progressAtStart)
    }

    return () => {
      if (interval !== undefined) window.clearInterval(interval)
      anim?.cancel()
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
  /** 点击槽位选择/更换物品回调（回传事件供调用方取锚点元素） */
  onClick: (event: MouseEvent<HTMLButtonElement>) => void
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
      enhancementLevel={item.enhancementLevel}
      equipmentDefinition={item.equipmentDefinition}
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
            category={resolveItemCategoryLabel(
              resolveItemSubCategory(item.itemId, item.definition, item.equipmentDefinition, item.category), t)}
            className="item-use-slot-cell"
            enhancementLevel={item.enhancementLevel}
            itemId={item.itemId}
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
