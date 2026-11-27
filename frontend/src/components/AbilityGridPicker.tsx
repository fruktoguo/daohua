import { useMemo } from 'react'
import { describeAbility, formatAbilityCategory, formatNumber } from '../game/format'
import { resolveCatalogItemMetadata, resolveItemCategoryLabel, resolveItemSubCategory } from '../game/ids'
import { resolveItemIconUrl } from '../game/itemIcons'
import { useT } from '../i18n'
import type { CombatAbilityDefinition, ItemDefinition } from '../game/types'
import { CandidatePicker, type CandidateOption } from './CandidatePicker'
import { EffectTooltipTarget, resolveAbilityAsideCards } from './EffectTooltip'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'

/** AbilityGridPicker 功法待选列表 Props */
export interface AbilityGridPickerProps {
  /** 待选列表展开状态 */
  open: boolean
  /** 设置目标锚点元素（正在配置的功法槽位按钮，面板贴靠其定位） */
  anchor: HTMLElement | null
  /** 目标标题 */
  title: string
  /** 可供选择的已学功法列表 */
  abilities: readonly CombatAbilityDefinition[]
  /** 装备「符法·天命」时允许装入术法槽的符箓物品列表（仅 slotIndex > 0 生效） */
  talismanItems?: readonly ItemDefinition[]
  /** 符箓库存数量查询（玩家面板传入以展示库存并置灰 0 库存；模拟器省略表示不限数量） */
  itemAmountFor?: (itemId: string) => number | undefined
  /** 当前选中的功法或符箓 ID (null 表示空选/卸下) */
  value: string | null
  /** 关闭回调 */
  onClose: () => void
  /** 选中功法或符箓回调 */
  onSelect: (abilityId: string | null) => void
  /** 是否提供空选/卸下槽位按钮（默认 true） */
  allowEmpty?: boolean
  /** 正在选择的槽位索引（0 为内功槽位，1+ 为术法槽位） */
  slotIndex?: number
  /** 已参悟等级查询（用于瓦片角标与悬浮详情展示；缺省按 1 级显示） */
  levelFor?: (abilityId: string) => number
  /** 当前编辑/查看单位的功法参悟经验映射；仅玩家本人配置面板传入，模拟器预设/非己方场景传 null 隐藏个人参悟进度副卡 */
  abilityExperiences?: Readonly<Record<string, number>> | null
  /** 当前槽位功法/符箓触发条件编辑回调 */
  onEditCondition?: () => void
  /** 当前槽位功法/符箓已配置触发条件数量 */
  triggerCount?: number
}

/**
 * 功法网格待选列表组件 (AbilityGridPicker)
 *
 * 渲染为贴靠目标槽位的通用待选列表（CandidatePicker），
 * 支持按需名称/品类搜索、空选卸下、五行品类角标与悬浮气泡技能数值详情；
 * 保留槽位类别互斥——内功位（index 0）只列内功心法，术法位列神通术法（及「符法·天命」激活时的符箓）。
 */
export function AbilityGridPicker({
  open,
  anchor,
  title,
  abilities,
  talismanItems,
  itemAmountFor,
  value,
  onClose,
  onSelect,
  allowEmpty = true,
  slotIndex,
  levelFor,
  abilityExperiences,
  onEditCondition,
  triggerCount,
}: AbilityGridPickerProps): React.JSX.Element {
  const t = useT()

  const isInternalSlot = slotIndex === 0
  // 槽位类别互斥：内功位（index 0）只列内功心法，术法位只列神通术法；
  // 与服务端 loadout.replace 的槽位校验一致，缺省 discipline 视为术法；未指定槽位时不过滤。
  const slotAbilities = useMemo(
    () => slotIndex === undefined
      ? abilities
      : abilities.filter(ability => (ability.discipline ?? 'spell') === (isInternalSlot ? 'internal' : 'spell')),
    [abilities, isInternalSlot, slotIndex],
  )

  const ribbonLabel = isInternalSlot ? t('app.combatPanel.internalRibbon') : t('app.combatPanel.spellRibbon')
  // 品质色与功法面板槽位统一：内功槽 emperor 帝金、术法槽 mystic 玄紫。
  const rarity = isInternalSlot ? 'emperor' : 'mystic'

  const conditionAction = useMemo(() => {
    if (!onEditCondition) return null
    const count = triggerCount ?? 0
    return {
      label: t('app.combatPanel.triggerConditionBtn', { count: count > 0 ? count : '' }),
      onClick: onEditCondition,
      hasTriggers: count > 0,
      title: t('app.combatPanel.editTriggersTitle'),
      ariaLabel: t('app.combatPanel.editTriggersTitle'),
    }
  }, [onEditCondition, triggerCount, t])
  const options = useMemo<readonly CandidateOption[]>(() => {
    const abilityOpts: CandidateOption[] = slotAbilities.map(ability => {
      const learnedLevel = levelFor?.(ability.abilityId) ?? 1
      const abilityExp = abilityExperiences
        ? (abilityExperiences[ability.abilityId] ?? 0)
        : undefined
      const asideCards = resolveAbilityAsideCards(ability, abilityExp, 'qi', learnedLevel)
      const iconUrl = resolveItemIconUrl(ability.abilityId)
      return {
        key: ability.abilityId,
        tile: (
          <span className="item-tile combat-ability-cell" data-rarity={rarity}>
            <span aria-hidden="true" className="item-type-ribbon">
              <span>{ribbonLabel}</span>
            </span>
            <span className="item-corner item-corner--top-right">
              Lv. {learnedLevel}
            </span>
            {iconUrl
              ? <img alt={ability.name} className="item-tile-icon" decoding="async" draggable={false} src={iconUrl} />
              : <strong className="item-tile-name">{ability.name}</strong>}
            <span className="item-corner item-corner--bottom-right">
              {t('app.combatPanel.manaCost', { mana: Math.round(ability.manaCost ?? 0) })}
            </span>
            <span aria-hidden="true" className="item-tile-glow" />
          </span>
        ),
        ariaLabel: ability.name,
        searchText: `${ability.name} ${ability.category} ${ability.element ?? ''} ${formatAbilityCategory(ability.category, ability.element)}`,
        wrapCell: cell => (
          <EffectTooltipTarget
            asideCards={asideCards}
            className="item-grid-picker-tooltip"
            content={describeAbility(ability, learnedLevel)}
            tabIndex={-1}
          >
            {cell}
          </EffectTooltipTarget>
        ),
      }
    })

    if (!isInternalSlot && slotIndex !== undefined && talismanItems && talismanItems.length > 0) {
      for (const item of talismanItems) {
        const metadata = resolveCatalogItemMetadata(item.itemId, { itemDefinition: item, t })
        const categoryLabel = resolveItemCategoryLabel(resolveItemSubCategory(item.itemId, item), t)
        const amount = itemAmountFor?.(item.itemId)
        abilityOpts.push({
          key: item.itemId,
          tile: (
            <ItemTile
              amount={amount}
              category={categoryLabel}
              className="item-grid-picker-cell"
              itemId={item.itemId}
              level={metadata.level}
              name={item.name}
              rarity={metadata.rarity}
            />
          ),
          disabled: amount !== undefined ? amount <= 0 : false,
          ariaLabel: amount !== undefined
            ? t('market.browse.itemOwned', { name: item.name, count: formatNumber(amount) })
            : item.name,
          searchText: `${item.name} ${categoryLabel} ${item.description ?? ''}`,
          wrapCell: cell => (
            <ItemTooltipTarget
              amount={amount}
              className="item-grid-picker-tooltip"
              definition={item}
              itemId={item.itemId}
              metadata={metadata}
              tabIndex={-1}
            >
              {cell}
            </ItemTooltipTarget>
          ),
        })
      }
    }

    return abilityOpts
  }, [abilityExperiences, isInternalSlot, itemAmountFor, levelFor, rarity, ribbonLabel, slotAbilities, slotIndex, t, talismanItems])

  return (
    <CandidatePicker
      allowEmpty={allowEmpty}
      anchor={anchor}
      conditionAction={conditionAction}
      onClose={onClose}
      onSelect={onSelect}
      open={open}
      options={options}
      title={title}
      value={value}
    />
  )
}
