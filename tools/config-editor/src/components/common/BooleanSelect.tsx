// ==============================================================================
// 文件名称: BooleanSelect.tsx
// 主要职责: 现代扁平分段控制器 (Segmented Switch) 布尔值组件，支持三态平滑切换
// ==============================================================================

interface BooleanSelectProps {
  label?: string;
  description?: string;
  value?: boolean | null;
  onChange: (val: boolean | undefined) => void;
  trueLabel?: string;
  falseLabel?: string;
  clearLabel?: string;
  allowClear?: boolean;
  className?: string;
  disabled?: boolean;
}

/** 三态布尔分段开关（缺省/是/否）。value 为 undefined 或 null 时视为「缺省未配置」，onChange 回传 undefined 表示清除字段——用于可选布尔配表字段的显式三态编辑。 */
export function BooleanSelect({
  label,
  description,
  value,
  onChange,
  trueLabel = 'True (是)',
  falseLabel = 'False (否)',
  clearLabel = '默认缺省',
  allowClear = true,
  className = '',
  disabled = false
}: BooleanSelectProps) {
  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300 tracking-tight">{label}</label>
          {description && <span className="text-[11px] text-zinc-500 font-normal">{description}</span>}
        </div>
      )}

      {/* 现代极简扁平分段开关 */}
      <div className="flex items-center p-0.5 bg-zinc-900 border border-zinc-800 rounded-lg min-h-[34px]">
        {allowClear && (
          <button
            type="button"
            disabled={disabled}
            onClick={() => onChange(undefined)}
            className={`flex-1 py-1 px-2 text-xs font-medium rounded-md transition-all duration-150 text-center truncate ${
              value === undefined || value === null
                ? 'bg-zinc-800 text-zinc-200 shadow-sm'
                : 'text-zinc-500 hover:text-zinc-300'
            }`}
          >
            {clearLabel}
          </button>
        )}

        <button
          type="button"
          disabled={disabled}
          onClick={() => onChange(true)}
          className={`flex-1 py-1 px-2 text-xs font-medium rounded-md transition-all duration-150 text-center flex items-center justify-center gap-1 truncate ${
            value === true
              ? 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 shadow-sm'
              : 'text-zinc-500 hover:text-zinc-300'
          }`}
        >
          <span className={`w-1.5 h-1.5 rounded-full ${value === true ? 'bg-emerald-400' : 'bg-zinc-600'}`} />
          <span>{trueLabel}</span>
        </button>

        <button
          type="button"
          disabled={disabled}
          onClick={() => onChange(false)}
          className={`flex-1 py-1 px-2 text-xs font-medium rounded-md transition-all duration-150 text-center flex items-center justify-center gap-1 truncate ${
            value === false
              ? 'bg-rose-500/20 text-rose-300 border border-rose-500/30 shadow-sm'
              : 'text-zinc-500 hover:text-zinc-300'
          }`}
        >
          <span className={`w-1.5 h-1.5 rounded-full ${value === false ? 'bg-rose-400' : 'bg-zinc-600'}`} />
          <span>{falseLabel}</span>
        </button>
      </div>
    </div>
  );
}
