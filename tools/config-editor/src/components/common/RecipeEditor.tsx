// ==============================================================================
// 文件名称: RecipeEditor.tsx
// 主要职责: 行动配方配置器（输入消耗 / 输出产出），道具通过下拉搜索选择
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
    const next = { ...(value || {}), [itemId]: amount };
    onChange(next);
  };

  const handleRemove = (itemId: string) => {
    const next = { ...(value || {}) };
    delete next[itemId];
    onChange(Object.keys(next).length > 0 ? next : undefined);
  };

  return (
    <div className={`p-3 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-2.5 ${className}`}>
      <div className="flex items-center justify-between">
        <label className="text-xs font-semibold text-zinc-200 flex items-center gap-2">
          <span>{label}</span>
          <span className="px-1.5 py-0.2 text-[10px] bg-zinc-800 text-zinc-400 rounded">
            {currentEntries.length} 种道具
          </span>
        </label>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2">
        {currentEntries.map(([itemId, amount]) => (
          <div
            key={itemId}
            className="p-2 bg-[#141b24] border border-zinc-700/60 rounded flex items-center justify-between gap-3 text-xs"
          >
            <div className="flex-1 min-w-[200px]">
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
                className="w-20 bg-[#0e131a] border border-zinc-700 focus:border-cyan-500 rounded px-2 py-1 text-xs text-right font-mono text-zinc-100 outline-none"
              />
              <button
                type="button"
                onClick={() => handleRemove(itemId)}
                className="text-zinc-400 hover:text-red-400 p-1 hover:bg-zinc-800 rounded"
                title="删除此项"
              >
                ×
              </button>
            </div>
          </div>
        ))}
      </div>

      <div className="flex items-center gap-2 pt-2 border-t border-zinc-800/80">
        <div className="flex-1">
          <ReferenceSelect
            kind="item"
            value={newSelectedItemId}
            onChange={setNewSelectedItemId}
            placeholder="+ 选择要添加的道具..."
            allowClear={true}
          />
        </div>
        <button
          type="button"
          disabled={!newSelectedItemId}
          onClick={handleAdd}
          className="px-3 py-1.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 disabled:opacity-40 text-cyan-200 text-xs rounded transition-colors"
        >
          添加
        </button>
      </div>
    </div>
  );
}
