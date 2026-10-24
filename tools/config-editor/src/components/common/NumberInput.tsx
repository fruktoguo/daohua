// ==============================================================================
// 文件名称: NumberInput.tsx
// 主要职责: 现代扁平化数值步进输入组件，提供平滑步进、内嵌单位 Badge 与极简交互
// ==============================================================================

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

/** 数值步进输入框：空输入回传 undefined 表示清除可选字段；步进经 toFixed(4) 消除浮点累加误差，越界时钳到 min/max。 */
export function NumberInput({
  label,
  description,
  value,
  onChange,
  min,
  max,
  step = 1,
  unit,
  placeholder = '0',
  className = '',
  disabled = false
}: NumberInputProps) {
  const currentVal = value ?? '';

  // 步进以当前值为基准；toFixed(4) 抵消 0.05/0.01 等小数步长在二进制浮点下的累加误差（如 0.30000000004）。
  const handleStep = (delta: number) => {
    if (disabled) return;
    const base = value ?? 0;
    const next = Number((base + delta * step).toFixed(4));
    if (min !== undefined && next < min) return onChange(min);
    if (max !== undefined && next > max) return onChange(max);
    onChange(next);
  };

  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300 tracking-tight">{label}</label>
          {description && <span className="text-[11px] text-zinc-500 font-normal">{description}</span>}
        </div>
      )}
      <div className="relative flex items-center group">
        <input
          type="number"
          min={min}
          max={max}
          step={step}
          value={currentVal}
          placeholder={placeholder}
          disabled={disabled}
          onChange={e => {
            const raw = e.target.value;
            if (raw === '') {
              onChange(undefined);
            } else {
              const num = parseFloat(raw);
              onChange(isNaN(num) ? undefined : num);
            }
          }}
          className={`w-full bg-zinc-900/90 border border-zinc-800 hover:border-zinc-700 focus:border-indigo-500/80 focus:ring-2 focus:ring-indigo-500/20 text-sm font-mono text-zinc-100 rounded-lg px-3 py-1.5 transition-all duration-150 outline-none disabled:opacity-50 disabled:cursor-not-allowed ${
            unit ? 'pr-16' : 'pr-8'
          }`}
        />

        {/* 右侧单位与微调 */}
        <div className="absolute right-1.5 flex items-center gap-1">
          {unit && (
            <span className="px-1.5 py-0.5 text-[10px] font-mono text-zinc-400 bg-zinc-800/80 rounded border border-zinc-700/50 select-none">
              {unit}
            </span>
          )}
          <div className="flex flex-col opacity-0 group-hover:opacity-100 focus-within:opacity-100 transition-opacity">
            <button
              type="button"
              tabIndex={-1}
              onClick={() => handleStep(1)}
              className="text-zinc-500 hover:text-zinc-200 text-[9px] leading-3 p-0.5 hover:bg-zinc-800 rounded"
            >
              ▲
            </button>
            <button
              type="button"
              tabIndex={-1}
              onClick={() => handleStep(-1)}
              className="text-zinc-500 hover:text-zinc-200 text-[9px] leading-3 p-0.5 hover:bg-zinc-800 rounded"
            >
              ▼
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
