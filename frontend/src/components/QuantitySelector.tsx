import { useCallback, useEffect, useRef, useState } from 'react'
import { useT } from '../i18n'

interface QuantitySelectorProps {
  value: number
  onChange: (value: number) => void
  ariaLabel: string
  min?: number
  max?: number
  presets?: readonly number[]
  allowInfinity?: boolean
  disabled?: boolean
  compact?: boolean
  className?: string
  commitOnInput?: boolean
}

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
