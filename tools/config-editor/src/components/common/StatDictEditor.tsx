// ==============================================================================
// 文件名称: StatDictEditor.tsx
// 主要职责: 属性字典配置器，提供属性名下拉选择框与数值输入，支持 BaseStatRatios/FlatStats
// ==============================================================================

import { useState } from 'react';
import { STAT_METADATA_LIST, STAT_METADATA_MAP } from '../../types/enums';

interface StatDictEditorProps {
  label: string;
  description?: string;
  value?: Record<string, number> | null;
  onChange: (dict: Record<string, number> | undefined) => void;
  unit?: string;
  step?: number;
  className?: string;
}

export function StatDictEditor({
  label,
  description,
  value = {},
  onChange,
  unit = '%',
  step = 0.01,
  className = ''
}: StatDictEditorProps) {
  const currentEntries = Object.entries(value || {});
  const [selectedNewStat, setSelectedNewStat] = useState<string>('');

  const availableStats = STAT_METADATA_LIST.filter(
    s => !value || !(s.key in value)
  );

  const handleAdd = () => {
    if (!selectedNewStat) return;
    const next = { ...(value || {}), [selectedNewStat]: unit === '%' ? 0.1 : 10 };
    onChange(next);
    setSelectedNewStat('');
  };

  const handleUpdateValue = (key: string, num: number) => {
    const next = { ...(value || {}), [key]: num };
    onChange(next);
  };

  const handleRemove = (key: string) => {
    const next = { ...(value || {}) };
    delete next[key];
    onChange(Object.keys(next).length > 0 ? next : undefined);
  };

  return (
    <div className={`p-3 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-2.5 ${className}`}>
      <div className="flex items-center justify-between">
        <label className="text-xs font-semibold text-zinc-200 flex items-center gap-2">
          <span>{label}</span>
          <span className="px-1.5 py-0.2 text-[10px] bg-zinc-800 text-zinc-400 rounded">
            已配置 {currentEntries.length} 项
          </span>
        </label>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      {/* 现有属性列表 */}
      {currentEntries.length > 0 ? (
        <div className="flex flex-col gap-1.5">
          {currentEntries.map(([key, val]) => {
            const meta = STAT_METADATA_MAP.get(key);
            return (
              <div
                key={key}
                className="flex items-center justify-between gap-2 p-1.5 bg-[#141b24] border border-zinc-700/60 rounded text-xs"
              >
                <div className="flex flex-col flex-1 min-w-0">
                  <div className="flex items-center gap-1.5 truncate">
                    <span className="font-medium text-zinc-200">
                      {meta ? meta.label : key}
                    </span>
                    <span className="font-mono text-[10px] text-zinc-400">({key})</span>
                  </div>
                  {meta?.description && (
                    <span className="text-[10px] text-zinc-500 truncate">{meta.description}</span>
                  )}
                </div>

                <div className="flex items-center gap-1.5 shrink-0">
                  <div className="relative flex items-center">
                    <input
                      type="number"
                      step={step}
                      value={val}
                      onChange={e => handleUpdateValue(key, parseFloat(e.target.value) || 0)}
                      className="w-24 bg-[#0e131a] border border-zinc-700 focus:border-cyan-500 rounded px-2 py-1 text-xs text-right font-mono text-zinc-100 outline-none pr-6"
                    />
                    {unit && (
                      <span className="absolute right-2 text-[10px] text-zinc-500 pointer-events-none">
                        {unit}
                      </span>
                    )}
                  </div>

                  <button
                    type="button"
                    onClick={() => handleRemove(key)}
                    className="text-zinc-500 hover:text-red-400 px-1.5 py-1 rounded hover:bg-zinc-800"
                    title="删除此属性"
                  >
                    ×
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      ) : (
        <div className="text-center py-2 text-xs text-zinc-600 italic">
          尚未配置任何属性条目
        </div>
      )}

      {/* 添加新属性下拉选择器 */}
      {availableStats.length > 0 && (
        <div className="flex items-center gap-2 pt-2 border-t border-zinc-800/80">
          <select
            value={selectedNewStat}
            onChange={e => setSelectedNewStat(e.target.value)}
            className="flex-1 bg-[#141b24] border border-zinc-700/80 text-xs text-zinc-300 rounded px-2.5 py-1 outline-none"
          >
            <option value="">— 选择要添加的属性项 —</option>
            {availableStats.map(s => (
              <option key={s.key} value={s.key}>
                {s.label} ({s.key})
              </option>
            ))}
          </select>
          <button
            type="button"
            disabled={!selectedNewStat}
            onClick={handleAdd}
            className="px-3 py-1 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 disabled:opacity-40 text-cyan-200 text-xs rounded transition-colors"
          >
            + 添加
          </button>
        </div>
      )}
    </div>
  );
}
