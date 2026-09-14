import { useEffect, useId, useMemo, useState } from 'react'
import { describeAbility, formatAbilityCategory } from '../game/format'
import { resolveItemIconUrl } from '../game/itemIcons'
import { useT } from '../i18n'
import type { CombatAbilityDefinition } from '../game/types'
import { EffectTooltipTarget } from './EffectTooltip'
import { Modal } from './Modal'

export interface AbilityGridPickerProps {
  /** 弹窗展开状态 */
  open: boolean
  /** 弹窗标题 */
  title: string
  /** 可供选择的已学功法列表 */
  abilities: readonly CombatAbilityDefinition[]
  /** 当前选中的功法 ID (null 表示空选/卸下) */
  value: string | null
  /** 关闭弹窗回调 */
  onClose: () => void
  /** 选中功法回调 */
  onSelect: (abilityId: string | null) => void
  /** 是否提供空选/卸下槽位按钮（默认 true） */
  allowEmpty?: boolean
  /** 空选项文本标签 */
  emptyLabel?: string
  /** 正在选择的槽位索引（0 为内功槽位，1+ 为术法槽位） */
  slotIndex?: number
}

/**
 * 功法网格选择器模态窗组件 (AbilityGridPicker)
 *
 * 与物品选择弹窗 (ItemGridPicker) 统一采用紧凑网格模态窗模板与正方形技能瓦片，
 * 支持即时名称/品类搜索、空选卸下、五行品类角标与悬浮气泡技能数值详情。
 */
export function AbilityGridPicker({
  open,
  title,
  abilities,
  value,
  onClose,
  onSelect,
  allowEmpty = true,
  emptyLabel,
  slotIndex,
}: AbilityGridPickerProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const [query, setQuery] = useState('')
  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')

  const isInternalSlot = slotIndex === 0

  const visibleAbilities = useMemo(() => {
    if (!normalizedQuery) return abilities
    return abilities.filter(ability => {
      const catLabel = formatAbilityCategory(ability.category, ability.element)
      return `${ability.name} ${ability.category} ${ability.element ?? ''} ${catLabel}`
        .toLocaleLowerCase('zh-CN')
        .includes(normalizedQuery)
    })
  }, [abilities, normalizedQuery])

  useEffect(() => {
    if (open) setQuery('')
  }, [open])

  if (!open) return null

  const emptyText = emptyLabel ?? t('common.pickerEmpty')
  const select = (abilityId: string | null) => {
    onSelect(abilityId)
    onClose()
  }

  const ribbonLabel = isInternalSlot ? t('app.combatPanel.internalRibbon') : t('app.combatPanel.spellRibbon')
  const rarity = isInternalSlot ? 'emperor' : 'mystic'

  return (
    <Modal className="item-grid-picker-modal" labelledBy={titleId} onClose={onClose} open size="medium">
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
      <div aria-label={title} className="item-grid-picker-grid" role="listbox">
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
        {visibleAbilities.map(ability => {
          const isSelected = value === ability.abilityId
          const iconUrl = resolveItemIconUrl(ability.abilityId)
          return (
            <EffectTooltipTarget
              className="item-grid-picker-tooltip"
              content={describeAbility(ability, 1)}
              key={ability.abilityId}
              tabIndex={-1}
            >
              <button
                aria-selected={isSelected}
                className={`item-grid-picker-card${isSelected ? ' is-selected' : ''}`}
                onClick={() => select(ability.abilityId)}
                role="option"
                type="button"
              >
                <span
                  className="item-tile combat-ability-cell"
                  data-rarity={rarity}
                >
                  <span aria-hidden="true" className="item-type-ribbon">
                    <span>{ribbonLabel}</span>
                  </span>
                  {iconUrl
                    ? <img alt={ability.name} className="item-tile-icon" decoding="async" draggable={false} src={iconUrl} />
                    : <strong className="item-tile-name">{ability.name}</strong>}
                  <span className="item-corner item-corner--bottom-right">
                    {t('app.combatPanel.manaCost', { mana: Math.round(ability.manaCost) })}
                  </span>
                </span>
              </button>
            </EffectTooltipTarget>
          )
        })}
      </div>
      {visibleAbilities.length === 0 && (
        <p className="item-grid-picker-empty">{t('market.picker.noAvailableMatch')}</p>
      )}
    </Modal>
  )
}
