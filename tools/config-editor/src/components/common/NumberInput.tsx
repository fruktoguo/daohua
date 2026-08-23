// ==============================================================================
// 文件名称: NumberInput.tsx
// 主要职责: 专有数值输入框组件，支持步进、清空为 null/默认、浮点数与单位后缀
// ==============================================================================

import React from 'react';

interface NumberInputProps {
  label?: string;
  description?: string;
  value?: number | null;
  onChange: (val: number | undefined) => void;
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
}

export function NumberInput({
  label,
  description,
  value,
  onChange,
  min,
  max,
  step = 1,
  unit,
  placeholder = '默认 / 未设置',
  className = '',
  disabled = false
}: NumberInputProps) {
  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const raw = e.target.value;
    if (raw === '' || raw === null || raw === undefined) {
      onChange(undefined);
      return;
    }
    const parsed = parseFloat(raw);
    if (!isNaN(parsed)) {
      onChange(parsed);
    }
  };

  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300">{label}</label>
          {description && <span className="text-[11px] text-zinc-500">{description}</span>}
        </div>
      )}
      <div className="relative flex items-center">
        <input
          type="number"
          value={value === null || value === undefined ? '' : value}
          onChange={handleChange}
          min={min}
          max={max}
          step={step}
          placeholder={placeholder}
          disabled={disabled}
          className="w-full bg-[#121820] border border-zinc-700/80 hover:border-zinc-500 focus:border-cyan-500 focus:ring-1 focus:ring-cyan-500/30 text-sm text-zinc-100 rounded px-3 py-1.5 font-mono transition-colors outline-none disabled:opacity-50"
        />
        {unit && (
          <span className="absolute right-3 text-xs text-zinc-400 font-medium pointer-events-none">
            {unit}
          </span>
        )}
      </div>
    </div>
  );
}
