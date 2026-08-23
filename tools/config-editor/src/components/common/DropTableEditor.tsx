// ==============================================================================
// 文件名称: DropTableEditor.tsx
// 主要职责: 掉落表专用配置器，支持道具下拉选择、掉落概率与数量区间设置
// ==============================================================================

import type { CombatDropDefinition } from '../../types/config';
import { ReferenceSelect } from './ReferenceSelect';
import { NumberInput } from './NumberInput';

interface DropTableEditorProps {
  label: string;
  description?: string;
  drops?: CombatDropDefinition[] | null;
  onChange: (drops: CombatDropDefinition[] | undefined) => void;
  className?: string;
}

export function DropTableEditor({
  label,
  description,
  drops = [],
  onChange,
  className = ''
}: DropTableEditorProps) {
  const currentDrops = drops || [];

  const handleAdd = () => {
    const next = [...currentDrops, { itemId: '', chance: 0.1, minAmount: 1, maxAmount: 1 }];
    onChange(next);
  };

  const handleUpdate = (index: number, patch: Partial<CombatDropDefinition>) => {
    const next = [...currentDrops];
    next[index] = { ...next[index], ...patch };
    onChange(next);
  };

  const handleRemove = (index: number) => {
    const next = currentDrops.filter((_, i) => i !== index);
    onChange(next.length > 0 ? next : undefined);
  };

  return (
    <div className={`p-3 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-2.5 ${className}`}>
      <div className="flex items-center justify-between">
        <label className="text-xs font-semibold text-zinc-200 flex items-center gap-2">
          <span>{label}</span>
          <span className="px-1.5 py-0.2 text-[10px] bg-zinc-800 text-zinc-400 rounded">
            {currentDrops.length} 项掉落
          </span>
        </label>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2">
        {currentDrops.map((drop, idx) => (
          <div
            key={idx}
            className="p-2.5 bg-[#141b24] border border-zinc-700/60 rounded-md flex flex-col md:flex-row items-start md:items-center gap-3 text-xs"
          >
            <div className="flex-1 w-full min-w-[200px]">
              <ReferenceSelect
                kind="item"
                value={drop.itemId}
                onChange={id => handleUpdate(idx, { itemId: id || '' })}
                placeholder="请选择掉落道具..."
                allowClear={false}
              />
            </div>

            <div className="flex items-center gap-2 w-full md:w-auto">
              <div className="w-24">
                <NumberInput
                  label="概率"
                  value={drop.chance}
                  onChange={v => handleUpdate(idx, { chance: v ?? 0 })}
                  step={0.01}
                  min={0}
                  max={1}
                />
              </div>

              <div className="w-20">
                <NumberInput
                  label="最小"
                  value={drop.minAmount ?? 1}
                  onChange={v => handleUpdate(idx, { minAmount: v ?? 1 })}
                  min={1}
                />
              </div>

              <div className="w-20">
                <NumberInput
                  label="最大"
                  value={drop.maxAmount ?? 1}
                  onChange={v => handleUpdate(idx, { maxAmount: v ?? 1 })}
                  min={1}
                />
              </div>

              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="mt-4 p-1.5 text-zinc-400 hover:text-red-400 hover:bg-zinc-800 rounded text-sm"
                title="删除此掉落项"
              >
                ×
              </button>
            </div>
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={handleAdd}
        className="w-full py-1.5 bg-[#141b24] hover:bg-zinc-800 border border-dashed border-zinc-700 text-xs text-zinc-400 hover:text-cyan-300 rounded transition-colors"
      >
        + 添加掉落物项
      </button>
    </div>
  );
}
