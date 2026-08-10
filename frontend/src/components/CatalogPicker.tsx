import { useEffect, useId, useMemo, useState, type ReactNode } from 'react'
import { EffectTooltipTarget, type EffectTooltipContent } from './EffectTooltip'
import { Modal } from './Modal'

export interface CatalogPickerOption {
  id: string
  label: string
  subtitle?: string
  category?: string
  tone?: string
  disabled?: boolean
  detail?: ReactNode
  tooltip?: EffectTooltipContent
}

interface CatalogPickerProps {
  open: boolean
  title: string
  options: readonly CatalogPickerOption[]
  value: string | null
  onClose: () => void
  onSelect: (id: string | null) => void
  allowEmpty?: boolean
  emptyLabel?: string
}

export function CatalogPicker({
  open,
  title,
  options,
  value,
  onClose,
  onSelect,
  allowEmpty = true,
  emptyLabel = '卸下 / 不使用',
}: CatalogPickerProps): React.JSX.Element | null {
  const titleId = useId()
  const [query, setQuery] = useState('')
  const normalizedQuery = query.trim().toLocaleLowerCase('zh-CN')
  const visibleOptions = useMemo(() => {
    if (!normalizedQuery) return options
    return options.filter(option => `${option.label} ${option.subtitle ?? ''} ${option.category ?? ''}`
      .toLocaleLowerCase('zh-CN')
      .includes(normalizedQuery))
  }, [normalizedQuery, options])

  useEffect(() => {
    if (open) setQuery('')
  }, [open])

  if (!open) return null

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
          aria-label={`${title}搜索`}
          autoComplete="off"
          onChange={event => setQuery(event.target.value)}
          placeholder="搜索名称、分类"
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
            <strong>{emptyLabel}</strong>
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
            {option.category && <span className="catalog-picker-corner catalog-picker-corner--top-left">{option.category}</span>}
            {value === option.id && <span className="catalog-picker-corner catalog-picker-corner--top-right" aria-label="已选择">已选</span>}
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
      {visibleOptions.length === 0 && <p className="catalog-picker-empty">没有匹配的目录项</p>}
    </Modal>
  )
}

export function PickerTrigger({
  label,
  value,
  hint,
  onClick,
  className = '',
}: {
  label: string
  value: string
  hint?: string
  onClick: () => void
  className?: string
}): React.JSX.Element {
  return (
    <button aria-label={`打开${label}选择器`} className={`picker-trigger ${className}`.trim()} onClick={onClick} type="button">
      <span className="picker-trigger-label">{label}</span>
      <span className="picker-trigger-copy">
        <strong>{value}</strong>
        {hint && <small>{hint}</small>}
      </span>
    </button>
  )
}

export interface ChoiceMenuOption {
  id: string
  label: string
  subtitle?: string
  disabled?: boolean
}

export function ChoiceMenu({
  label,
  value,
  options,
  onSelect,
  className = '',
}: {
  label: string
  value: string
  options: readonly ChoiceMenuOption[]
  onSelect: (id: string) => void
  className?: string
}): React.JSX.Element {
  const [open, setOpen] = useState(false)
  const titleId = useId()
  const selected = options.find(option => option.id === value)

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
            <span>条件配置</span>
            <h2 id={titleId}>{label}</h2>
          </header>
          <div aria-label={`${label}选项`} className="choice-menu-options" role="listbox">
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
