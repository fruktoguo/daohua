import { useCallback, useEffect, useRef, useState } from 'react'
import { useT } from '../i18n'

/**
 * 数量选择器组件 Props
 */
interface QuantitySelectorProps {
  /** 当前数值（-1 表示无限循环） */
  value: number
  /** 数值变更回调 */
  onChange: (value: number) => void
  /** 无障碍输入标签 */
  ariaLabel: string
  /** 允许的最小值（默认 1） */
  min?: number
  /** 允许的最大值（默认 1000） */
  max?: number
  /** 快速预设按钮数值列表（如 [1, 10, 100]） */
  presets?: readonly number[]
  /** 是否提供无限循环 (∞, 对应 -1) 按钮 */
  allowInfinity?: boolean
  /** 是否禁用输入与按钮 */
  disabled?: boolean
  /** 是否使用紧凑布局 */
  compact?: boolean
  /** 外部样式类名 */
  className?: string
  /** 是否在输入框即时输入且有效时立即提交变更（默认 true） */
  commitOnInput?: boolean
}

/**
 * 通用数量选择器组件 (QuantitySelector)
 *
 * 适用于物品使用、批量购买/出售挂单、生活技能轮次设定等场景。
 * 具备输入框手输、预设档位快捷点选与无限轮次 (∞) 支持。
 */
export function QuantitySelector({
  value,
  onChange,
  ariaLabel,
  min = 1,
  max = 1000,
  presets = [],
  allowInfinity = false,
  disabled = false,
  compact = false,
  className = '',
  commitOnInput = true,
}: QuantitySelectorProps): React.JSX.Element {
  const t = useT()
  const [draft, setDraft] = useState(String(value < min ? min : value))
  const focused = useRef(false)

  useEffect(() => {
    if (!focused.current && value >= min) setDraft(String(value))
  }, [min, value])

  const commit = useCallback(() => {
    focused.current = false
    const parsed = Number.parseInt(draft, 10)
    const fallback = value >= min ? value : min
    const normalized = Number.isFinite(parsed) ? Math.max(min, Math.min(max, parsed)) : fallback
    setDraft(String(normalized))
    if (normalized !== value) onChange(normalized)
  }, [draft, max, min, onChange, value])

  const choose = useCallback((nextValue: number) => {
    if (nextValue >= min) setDraft(String(nextValue))
    onChange(nextValue)
  }, [min, onChange])

  return (
    <div className={`quantity-selector${compact ? ' quantity-selector--compact' : ''} ${className}`.trim()}>
      <input
        aria-label={ariaLabel}
        disabled={disabled}
        inputMode="numeric"
        max={max}
        min={min}
        onBlur={commit}
        onChange={event => {
          setDraft(event.target.value)
          const parsed = Number.parseInt(event.target.value, 10)
          if (commitOnInput && Number.isFinite(parsed) && parsed >= min && parsed <= max) onChange(parsed)
        }}
        onFocus={() => {
          focused.current = true
          if (value < min) choose(min)
        }}
        onKeyDown={event => {
          if (event.key === 'Enter') event.currentTarget.blur()
        }}
        type="number"
        value={draft}
      />
      {presets.map(preset => (
        <button
          aria-pressed={value === preset}
          className={value === preset ? 'active' : ''}
          disabled={disabled}
          key={preset}
          onClick={() => choose(preset)}
          type="button"
        >{preset}</button>
      ))}
      {allowInfinity && (
        <button
          aria-label={t('market.selector.infinityAria')}
          aria-pressed={value === -1}
          className={value === -1 ? 'active' : ''}
          disabled={disabled}
          onClick={() => choose(-1)}
          title={t('market.selector.infinity')}
          type="button"
        >∞</button>
      )}
    </div>
  )
}
