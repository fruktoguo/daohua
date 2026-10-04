// ==============================================================================
// 文件名称: RecipeEditor.tsx
// 主要职责: 现代扁平化配方消耗/产出配置组件
// ==============================================================================

import { useState } from 'react';
import { ReferenceSelect } from './ReferenceSelect';

interface RecipeEditorProps {
  label: string;
  description?: string;
  value?: Record<string, number> | null;
  onChange: (recipe: Record<string, number> | undefined) => void;
  className?: string;
}

/** 配方消耗编辑器：维护 itemId → 数量 字典（对应 ActionConfig.inputs / 建筑材料等消耗配方）。数量最低钳为 1；全部条目删除后回传 undefined 清空字段。 */
export function RecipeEditor({
  label,
  description,
  value = {},
  onChange,
  className = ''
}: RecipeEditorProps) {
  const currentEntries = Object.entries(value || {});
  const [newSelectedItemId, setNewSelectedItemId] = useState<string | undefined>();

  const handleAdd = () => {
    if (!newSelectedItemId) return;
    const next = { ...(value || {}), [newSelectedItemId]: 1 };
    onChange(next);
    setNewSelectedItemId(undefined);
  };

  const handleUpdateAmount = (itemId: string, amount: number) => {
    // parseInt 失败（空串/非数字）时回退为 1：配表语义上消耗数量必须为正，0 或负数无意义。
    const next = { ...(value || {}), [itemId]: amount };
    onChange(next);
  };

  const handleRemove = (itemId: string) => {
    const next = { ...(value || {}) };
    delete next[itemId];
    onChange(Object.keys(next).length > 0 ? next : undefined);
  };

  return (
    <div className={`p-4 bg-zinc-900/60 border border-zinc-800/80 rounded-xl flex flex-col gap-3 ${className}`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <label className="text-xs font-semibold text-zinc-200 tracking-tight">{label}</label>
          <span className="px-2 py-0.5 text-[10px] font-mono font-medium rounded-full bg-zinc-800 text-zinc-400">
            {currentEntries.length} 种道具
          </span>
        </div>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2">
        {currentEntries.map(([itemId, amount]) => (
          <div
            key={itemId}
            className="p-2.5 bg-zinc-900/90 border border-zinc-800 rounded-lg flex items-center justify-between gap-3 text-xs hover:border-zinc-700 transition-colors"
          >
            <div className="flex-1 min-w-[180px]">
              <ReferenceSelect
                kind="item"
                value={itemId}
                onChange={newId => {
                  if (newId && newId !== itemId) {
                    const next = { ...(value || {}) };
                    delete next[itemId];
                    next[newId] = amount;
                    onChange(next);
                  }
                }}
                allowClear={false}
              />
            </div>

            <div className="flex items-center gap-2">
              <span className="text-zinc-400 text-xs">数量:</span>
              <input
                type="number"
                min={1}
                value={amount}
                onChange={e => handleUpdateAmount(itemId, parseInt(e.target.value) || 1)}
                className="w-20 bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-1 text-xs text-right font-mono text-zinc-100 outline-none"
              />
              <button
                type="button"
                onClick={() => handleRemove(itemId)}
                className="text-zinc-500 hover:text-rose-400 p-1 hover:bg-zinc-800 rounded transition-colors text-sm leading-none"
                title="删除此项"
              >
                ✕
              </button>
            </div>
          </div>
        ))}
      </div>

      <div className="flex items-center gap-2 pt-2 border-t border-zinc-800/60">
        <div className="flex-1">
          <ReferenceSelect
            kind="item"
            value={newSelectedItemId}
            onChange={setNewSelectedItemId}
            placeholder="+ 选择要添加的配方道具..."
            allowClear={true}
          />
        </div>
        <button
          type="button"
          disabled={!newSelectedItemId}
          onClick={handleAdd}
          className="px-3.5 py-1.5 bg-indigo-600 hover:bg-indigo-500 disabled:opacity-40 text-white font-medium text-xs rounded-lg transition-colors shadow-sm"
        >
          添加
        </button>
      </div>
    </div>
  );
}
