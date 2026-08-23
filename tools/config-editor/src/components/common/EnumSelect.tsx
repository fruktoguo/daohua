// ==============================================================================
// 文件名称: EnumSelect.tsx
// 主要职责: 现代扁平化枚举选择器，支持彩色 Pill 徽章与胶囊多选模式
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
  clearLabel = '— 默认未指定 —',
  className = '',
  disabled = false
}: EnumSelectProps<T>) {
  const selectedOption = options.find(opt => opt.value === value);

  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300 flex items-center gap-1.5 tracking-tight">
            {label}
            {selectedOption?.badgeColor && (
              <span className={`px-2 py-0.5 text-[10px] font-medium rounded-full border ${selectedOption.badgeColor}`}>
                {selectedOption.en}
              </span>
            )}
          </label>
          {description && <span className="text-[11px] text-zinc-500 font-normal">{description}</span>}
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
          className="w-full bg-zinc-900/90 border border-zinc-800 hover:border-zinc-700 focus:border-indigo-500/80 focus:ring-2 focus:ring-indigo-500/20 text-sm text-zinc-100 rounded-lg px-3 py-1.5 transition-all duration-150 appearance-none cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed pr-8 outline-none"
        >
          {allowClear && <option value="" className="bg-zinc-900 text-zinc-500">{clearLabel}</option>}
          {options.map(opt => (
            <option key={String(opt.value)} value={String(opt.value)} className="bg-zinc-900 text-zinc-100 py-1.5">
              {opt.label} ({opt.en})
            </option>
          ))}
        </select>
        <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center px-2.5 text-zinc-500">
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.5" d="M19 9l-7 7-7-7" />
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
          <label className="text-xs font-medium text-zinc-300 tracking-tight">{label}</label>
          {description && <span className="text-[11px] text-zinc-500 font-normal">{description}</span>}
        </div>
      )}
      <div className="flex flex-wrap gap-1.5 p-2 bg-zinc-900/60 border border-zinc-800/80 rounded-lg min-h-[42px] items-center">
        {options.map(opt => {
          const isSelected = currentSet.has(opt.value);
          return (
            <button
              key={opt.value}
              type="button"
              onClick={() => toggle(opt.value)}
              className={`px-2.5 py-1 text-xs font-medium rounded-md border transition-all duration-150 flex items-center gap-1.5 ${
                isSelected
                  ? opt.badgeColor || 'border-indigo-500/30 bg-indigo-500/20 text-indigo-300'
                  : 'border-zinc-800 bg-zinc-900 text-zinc-400 hover:border-zinc-700 hover:text-zinc-200'
              }`}
            >
              <span>{opt.label.split(' ')[0]}</span>
              {isSelected && <span className="text-[10px] font-bold">✓</span>}
            </button>
          );
        })}
      </div>
    </div>
  );
}
