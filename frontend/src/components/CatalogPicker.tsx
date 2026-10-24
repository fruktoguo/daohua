import { useEffect, useId, useMemo, useState, type ReactNode } from 'react'
import { resolveItemCategoryLabel } from '../game/ids'
import { useT } from '../i18n'
import { EffectTooltipTarget, type EffectTooltipContent } from './EffectTooltip'
import { Modal } from './Modal'

/**
 * 目录选择器通用选项定义
 */
export interface CatalogPickerOption {
  /** 选项唯一标识符 */
  id: string
  /** 选项主标签标题 */
  label: string
  /** 可选副标题说明 */
  subtitle?: string
  /** 可选分类角标（展示在卡片左上角） */
  category?: string
  /** 可选色调风格 */
  tone?: string
  /** 是否禁用该选项 */
  disabled?: boolean
  /** 可选自定义附属节点 */
  detail?: ReactNode
  /** 可选悬浮提示气泡内容 */
  tooltip?: EffectTooltipContent
}

/**
 * 目录选择器模态窗 Props
 */
interface CatalogPickerProps {
  /** 弹窗是否开启 */
  open: boolean
  /** 弹窗标题 */
  title: string
  /** 可供选择的选项列表 */
  options: readonly CatalogPickerOption[]
  /** 当前选中的选项 ID（null 表示空选） */
  value: string | null
  /** 关闭弹窗回调 */
  onClose: () => void
  /** 选中选项回调（参数为选中 ID 或 null） */
  onSelect: (id: string | null) => void
  /** 是否允许选择空选项（默认 true） */
  allowEmpty?: boolean
}

/**
 * 目录网格选择器模态窗组件 (CatalogPicker)
 *
 * 用于装备、物品、功法或配置项的弹窗式分类检索与选择。
 * 内部维护即时模糊搜索过滤，支持分类徽章、状态提示与气泡详情。
 */
export function CatalogPicker({
  open,
  title,
  options,
  value,
  onClose,
  onSelect,
  allowEmpty = true,
}: CatalogPickerProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  // 搜索关键字状态与中文小写归一化
  const [query, setQuery] = useState('')
  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')

  // 根据关键字对标题、副标题与分类进行即时过滤
  const visibleOptions = useMemo(() => {
    if (!normalizedQuery) return options
    return options.filter(option => {
      const catLabel = resolveItemCategoryLabel(option.category, t)
      return `${option.label} ${option.subtitle ?? ''} ${option.category ?? ''} ${catLabel}`
        .toLocaleLowerCase('zh-CN')
        .includes(normalizedQuery)
    })
  }, [normalizedQuery, options, t])

  // 打开弹窗时清空上一次的搜索关键字
  useEffect(() => {
    if (open) setQuery('')
  }, [open])

  if (!open) return null

  // 空选项文案统一收拢为通用词条「无」（t 来自 hook，需在组件体内解析）。
  const emptyText = t('common.pickerEmpty')
  const select = (id: string | null) => {
    onSelect(id)
    onClose()
  }

  return (
    <Modal labelledBy={titleId} onClose={onClose} open size="large" className="catalog-picker-modal">
      <header className="catalog-picker-header">
        <div>
          <h2 id={titleId}>{title}</h2>
        </div>
        <input
          aria-label={t('market.picker.searchTitleTemplate', { title })}
          autoComplete="off"
          onChange={event => setQuery(event.target.value)}
          placeholder={t('market.picker.searchNameCategory')}
          type="search"
          value={query}
        />
      </header>
      <div className="catalog-picker-grid" role="listbox" aria-label={title}>
        {allowEmpty && (
          <button
            aria-selected={value === null}
            className={`catalog-picker-card catalog-picker-card--empty${value === null ? ' is-selected' : ''}`}
            onClick={() => select(null)}
            role="option"
            type="button"
          >
            <strong>{emptyText}</strong>
          </button>
        )}
        {visibleOptions.map(option => {
          const card = <button
            aria-selected={value === option.id}
            className={`catalog-picker-card${value === option.id ? ' is-selected' : ''}${option.disabled ? ' is-disabled' : ''}`}
            disabled={option.disabled}
            key={option.id}
            onClick={() => select(option.id)}
            role="option"
            type="button"
          >
            {option.category && <span className="catalog-picker-corner catalog-picker-corner--top-left">{resolveItemCategoryLabel(option.category, t)}</span>}
            {value === option.id && <span className="catalog-picker-corner catalog-picker-corner--top-right" aria-label={t('market.picker.selectedAria')}>{t('market.picker.selectedTag')}</span>}
            <span className="catalog-picker-copy">
              <strong>{option.label}</strong>
              {option.subtitle && <span>{option.subtitle}</span>}
              {option.detail}
            </span>
          </button>
          return option.tooltip ? (
            <EffectTooltipTarget className="catalog-picker-tooltip" content={option.tooltip} key={option.id} tabIndex={-1}>
              {card}
            </EffectTooltipTarget>
          ) : <span className="catalog-picker-tooltip" key={option.id}>{card}</span>
        })}
      </div>
      {visibleOptions.length === 0 && <p className="catalog-picker-empty">{t('market.picker.noCatalogMatch')}</p>}
    </Modal>
  )
}

/**
 * 选择器触发按钮组件 (PickerTrigger)
 *
 * 紧凑的字段触发器，展示左侧标签名与右侧当前取值，点击打开选择器弹窗。
 */
export function PickerTrigger({
  label,
  value,
  hint,
  onClick,
  className = '',
}: {
  /** 字段标题标签 */
  label: string
  /** 当前选中的值文本展示 */
  value: string
  /** 可选微型提示文字 */
  hint?: string
  /** 点击唤起选择器弹窗 */
  onClick: () => void
  /** 外部样式类名 */
  className?: string
}): React.JSX.Element {
  const t = useT()
  return (
    <button aria-label={t('market.picker.openPickerTemplate', { name: label })} className={`picker-trigger ${className}`.trim()} onClick={onClick} type="button">
      <span className="picker-trigger-label">{label}</span>
      <span className="picker-trigger-copy">
        <strong>{value}</strong>
        {hint && <small>{hint}</small>}
      </span>
    </button>
  )
}

/**
 * 下拉单选菜单选项定义
 */
export interface ChoiceMenuOption {
  /** 选项值 ID */
  id: string
  /** 显示主标签 */
  label: string
  /** 可选说明副标 */
  subtitle?: string
  /** 是否禁用 */
  disabled?: boolean
}

/**
 * 弹出式单选小菜单组件 (ChoiceMenu)
 *
 * 点击触发按钮后弹出轻量模态单选列表，适用于战斗触发条件、比较符等紧凑配置项。
 */
export function ChoiceMenu({
  label,
  value,
  options,
  onSelect,
  className = '',
}: {
  /** 菜单字段名称 */
  label: string
  /** 当前选中的选项 ID */
  value: string
  /** 可选选项列表 */
  options: readonly ChoiceMenuOption[]
  /** 选中选项时的回调函数 */
  onSelect: (id: string) => void
  /** 容器额外样式类名 */
  className?: string
}): React.JSX.Element {
  // 菜单弹窗展开状态
  const [open, setOpen] = useState(false)
  const titleId = useId()
  const selected = options.find(option => option.id === value)
  const t = useT()

  return (
    <div className={`choice-menu${open ? ' is-open' : ''} ${className}`.trim()}>
      <button
        aria-expanded={open}
        aria-haspopup="dialog"
        aria-label={label}
        className="choice-menu-trigger"
        onClick={() => setOpen(current => !current)}
        type="button"
      >
        <small>{label}</small>
        <strong>{selected?.label ?? value}</strong>
      </button>
      {open && (
        <Modal className="choice-menu-modal" labelledBy={titleId} onClose={() => setOpen(false)} open size="small">
          <header className="choice-menu-modal-header">
            <span>{t('market.picker.choiceMenuConfig')}</span>
            <h2 id={titleId}>{label}</h2>
          </header>
          <div aria-label={t('market.picker.optionsTemplate', { name: label })} className="choice-menu-options" role="listbox">
            {options.map(option => (
              <button
                aria-selected={option.id === value}
                className={`choice-menu-option${option.id === value ? ' is-selected' : ''}`}
                disabled={option.disabled}
                key={option.id}
                onClick={() => {
                  onSelect(option.id)
                  setOpen(false)
                }}
                role="option"
                type="button"
              >
                <strong>{option.label}</strong>
                {option.subtitle && <small>{option.subtitle}</small>}
              </button>
            ))}
          </div>
        </Modal>
      )}
    </div>
  )
}
