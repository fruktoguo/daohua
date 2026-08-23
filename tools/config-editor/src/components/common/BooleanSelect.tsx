// ==============================================================================
// 文件名称: BooleanSelect.tsx
// 主要职责: 布尔值专用下拉框组件（是/否/默认未指定）
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

export function BooleanSelect({
  label,
  description,
  value,
  onChange,
  trueLabel = '是 (true)',
  falseLabel = '否 (false)',
  clearLabel = '— 默认未指定 (null) —',
  allowClear = true,
  className = '',
  disabled = false
}: BooleanSelectProps) {
  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300 flex items-center gap-1.5">
            {label}
            {value === true && (
              <span className="px-1.5 py-0.2 text-[10px] rounded bg-emerald-950 text-emerald-300 border border-emerald-800">
                TRUE
              </span>
            )}
            {value === false && (
              <span className="px-1.5 py-0.2 text-[10px] rounded bg-red-950 text-red-300 border border-red-800">
                FALSE
              </span>
            )}
          </label>
          {description && <span className="text-[11px] text-zinc-500">{description}</span>}
        </div>
      )}
      <div className="relative">
        <select
          value={value === true ? 'true' : value === false ? 'false' : ''}
          onChange={e => {
            const val = e.target.value;
            if (val === 'true') onChange(true);
            else if (val === 'false') onChange(false);
            else onChange(undefined);
          }}
          disabled={disabled}
          className="w-full bg-[#121820] border border-zinc-700/80 hover:border-zinc-500 focus:border-cyan-500 focus:ring-1 focus:ring-cyan-500/30 text-sm text-zinc-100 rounded px-3 py-1.5 transition-colors appearance-none cursor-pointer disabled:opacity-50 pr-8"
        >
          {allowClear && <option value="">{clearLabel}</option>}
          <option value="true" className="bg-[#121820] text-emerald-300">{trueLabel}</option>
          <option value="false" className="bg-[#121820] text-red-300">{falseLabel}</option>
        </select>
        <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center px-2.5 text-zinc-400">
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
          </svg>
        </div>
      </div>
    </div>
  );
}
