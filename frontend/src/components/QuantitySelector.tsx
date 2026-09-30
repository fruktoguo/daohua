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
  /** 输入框左侧快捷档位数值列表（如 [1]） */
  leadingPresets?: readonly number[]
  /** 快速预设按钮数值列表（如 [1, 10, 100]，渲染在输入框右侧） */
  presets?: readonly number[]
  /** 是否在输入框右侧提供「最大」快捷钮（点击填入 max） */
  maxShortcut?: boolean
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
  leadingPresets = [],
  presets = [],
  maxShortcut = false,
  allowInfinity = false,
  disabled = false,
  compact = false,
  className = '',
  commitOnInput = true,
}: QuantitySelectorProps): React.JSX.Element {
  const t = useT()
  const [draft, setDraft] = useState(String(value < min ? min : value))
  // 聚焦标记：输入过程中冻结外部 value 对 draft 的回写，否则高频快照推送会冲掉正在键入的文本。
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
      {leadingPresets.map(preset => (
        <button
          aria-pressed={value === preset}
          className={value === preset ? 'active' : ''}
          disabled={disabled}
          key={preset}
          onClick={() => choose(preset)}
          type="button"
        >{preset}</button>
      ))}
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
      {maxShortcut && (
        <button
          aria-label={t('market.selector.maxAria')}
          aria-pressed={value === max}
          className={value === max ? 'active' : ''}
          disabled={disabled}
          onClick={() => choose(max)}
          title={t('market.selector.maxAria')}
          type="button"
        >{t('market.selector.max')}</button>
      )}
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
