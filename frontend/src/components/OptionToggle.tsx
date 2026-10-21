/**
 * 通用多选一 Toggle 组（坊市多维筛选、万象图鉴分类与编码共用）
 *
 * 首项约定为「全部/任意」（该维度不过滤），其余选项互斥；维度之间由调用方按 AND 联合。
 * 支持整体置灰（aria-disabled + 视觉降噪，置灰时点击不触发 onChange）；
 * 可选 count 角标用于收集度面板（如万象图鉴的「已收录/总数」）。
 * 样式：`.option-toggle-group` / `.option-toggle-btn` / `.option-toggle-count`。
 */
export interface OptionToggleOption<T extends string> {
  value: T
  label: string
  /** 可选角标文本（收集度），不参与过滤语义 */
  count?: string
}

interface OptionToggleProps<T extends string> {
  ariaLabel: string
  disabled?: boolean
  onChange: (value: T) => void
  options: ReadonlyArray<OptionToggleOption<T>>
  value: T
}

export function OptionToggle<T extends string>({
  ariaLabel,
  disabled,
  onChange,
  options,
  value,
}: OptionToggleProps<T>): React.JSX.Element {
  return (
    <div
      aria-disabled={disabled}
      aria-label={ariaLabel}
      className={`option-toggle-group${disabled ? ' is-disabled' : ''}`}
      role="group"
    >
      {options.map(option => (
        <button
          aria-pressed={value === option.value}
          className={`option-toggle-btn${value === option.value ? ' active' : ''}`}
          key={option.value}
          onClick={() => {
            if (disabled) return
            onChange(option.value)
          }}
          tabIndex={disabled ? -1 : undefined}
          type="button"
        >
          {option.label}
          {option.count !== undefined && <span className="option-toggle-count">{option.count}</span>}
        </button>
      ))}
    </div>
  )
}
