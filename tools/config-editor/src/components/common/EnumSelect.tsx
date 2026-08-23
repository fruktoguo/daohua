// ==============================================================================
// 文件名称: EnumSelect.tsx
// 主要职责: 通用枚举下拉单选/多选组件，提供中文语义、英文说明与彩色徽章
// ==============================================================================

import type { EnumOption } from '../../types/enums';

interface EnumSelectProps<T extends string | number = string> {
  label?: string;
  description?: string;
  value?: T | null;
  options: EnumOption<T>[];
  onChange: (val: T | undefined) => void;
  allowClear?: boolean;
  clearLabel?: string;
  className?: string;
  disabled?: boolean;
}

export function EnumSelect<T extends string | number = string>({
  label,
  description,
  value,
  options,
  onChange,
  allowClear = true,
  clearLabel = '— 保持默认 / 未指定 —',
  className = '',
  disabled = false
}: EnumSelectProps<T>) {
  const selectedOption = options.find(opt => opt.value === value);

  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300 flex items-center gap-1.5">
            {label}
            {selectedOption?.badgeColor && (
              <span className={`px-1.5 py-0.5 text-[10px] rounded border ${selectedOption.badgeColor}`}>
                {selectedOption.en}
              </span>
            )}
          </label>
          {description && <span className="text-[11px] text-zinc-500">{description}</span>}
        </div>
      )}
      <div className="relative">
        <select
          value={value === null || value === undefined ? '' : String(value)}
          onChange={e => {
            const raw = e.target.value;
            if (!raw) {
              onChange(undefined);
            } else {
              const found = options.find(opt => String(opt.value) === raw);
              onChange(found ? found.value : (raw as unknown as T));
            }
          }}
          disabled={disabled}
          className="w-full bg-[#121820] border border-zinc-700/80 hover:border-zinc-500 focus:border-cyan-500 focus:ring-1 focus:ring-cyan-500/30 text-sm text-zinc-100 rounded px-3 py-1.5 transition-colors appearance-none cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed pr-8"
        >
          {allowClear && <option value="">{clearLabel}</option>}
          {options.map(opt => (
            <option key={String(opt.value)} value={String(opt.value)} className="bg-[#121820] text-zinc-100 py-1">
              {opt.label} ({opt.en})
            </option>
          ))}
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

interface MultiEnumSelectProps<T extends string = string> {
  label?: string;
  description?: string;
  values?: T[];
  options: EnumOption<T>[];
  onChange: (vals: T[]) => void;
  className?: string;
}

export function MultiEnumSelect<T extends string = string>({
  label,
  description,
  values = [],
  options,
  onChange,
  className = ''
}: MultiEnumSelectProps<T>) {
  const currentSet = new Set(values || []);

  const toggle = (val: T) => {
    const next = new Set(currentSet);
    if (next.has(val)) {
      next.delete(val);
    } else {
      next.add(val);
    }
    onChange(Array.from(next));
  };

  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300">{label}</label>
          {description && <span className="text-[11px] text-zinc-500">{description}</span>}
        </div>
      )}
      <div className="flex flex-wrap gap-1.5 p-2 bg-[#121820] border border-zinc-700/80 rounded min-h-[42px] items-center">
        {options.map(opt => {
          const isSelected = currentSet.has(opt.value);
          return (
            <button
              key={opt.value}
              type="button"
              onClick={() => toggle(opt.value)}
              className={`px-2.5 py-1 text-xs rounded border transition-all flex items-center gap-1.5 ${
                isSelected
                  ? opt.badgeColor || 'border-cyan-600 bg-cyan-950/60 text-cyan-200 font-medium'
                  : 'border-zinc-800 bg-zinc-900/60 text-zinc-400 hover:border-zinc-600 hover:text-zinc-200'
              }`}
            >
              <span>{opt.label.split(' ')[0]}</span>
              {isSelected && <span className="text-[10px] opacity-75">✓</span>}
            </button>
          );
        })}
      </div>
    </div>
  );
}
