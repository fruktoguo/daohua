// ==============================================================================
// 文件名称: StatDictEditor.tsx
// 主要职责: 现代扁平化属性字典编辑器，支持 14 维基准与机制属性字典配置
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

/** 属性字典编辑器：键集限定为 STAT_METADATA_LIST 授权属性（新增下拉只列未配置项），防止写入引擎不认识的属性键。空字典回传 undefined 清空字段。 */
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
    // 新增条目给一个非零初值：百分比属性 0.1（=10%）、点数属性 10 点，避免 0 值条目被误认为无效配置。
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
    <div className={`p-4 bg-zinc-900/60 border border-zinc-800/80 rounded-xl flex flex-col gap-3 ${className}`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <label className="text-xs font-semibold text-zinc-200 tracking-tight">{label}</label>
          <span className="px-2 py-0.5 text-[10px] font-mono font-medium rounded-full bg-zinc-800 text-zinc-400">
            {currentEntries.length} 项
          </span>
        </div>
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
                className="flex items-center justify-between gap-2 p-2 bg-zinc-900/90 border border-zinc-800 rounded-lg text-xs hover:border-zinc-700 transition-colors"
              >
                <div className="flex flex-col flex-1 min-w-0">
                  <div className="flex items-center gap-1.5 truncate">
                    <span className="font-medium text-zinc-200">
                      {meta ? meta.label : key}
                    </span>
                    <span className="font-mono text-[10px] text-zinc-500">({key})</span>
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
                      className="w-24 bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-1 text-xs text-right font-mono text-zinc-100 outline-none pr-6"
                    />
                    {unit && (
                      <span className="absolute right-2 text-[10px] text-zinc-500 font-mono pointer-events-none">
                        {unit}
                      </span>
                    )}
                  </div>

                  <button
                    type="button"
                    onClick={() => handleRemove(key)}
                    className="text-zinc-500 hover:text-rose-400 p-1 rounded hover:bg-zinc-800 transition-colors text-sm leading-none"
                    title="删除此属性"
                  >
                    ✕
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      ) : (
        <div className="text-center py-3 text-xs text-zinc-500 italic">
          尚未配置任何属性条目
        </div>
      )}

      {/* 添加新属性下拉框 */}
      {availableStats.length > 0 && (
        <div className="flex items-center gap-2 pt-2 border-t border-zinc-800/60">
          <select
            value={selectedNewStat}
            onChange={e => setSelectedNewStat(e.target.value)}
            className="flex-1 bg-zinc-900 border border-zinc-800 text-xs text-zinc-300 rounded-lg px-3 py-1.5 outline-none hover:border-zinc-700 focus:border-indigo-500"
          >
            <option value="">+ 选择要添加的属性项...</option>
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
            className="px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 disabled:opacity-40 text-white font-medium text-xs rounded-lg transition-colors shadow-sm"
          >
            添加
          </button>
        </div>
      )}
    </div>
  );
}
