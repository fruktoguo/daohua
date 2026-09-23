// ==============================================================================
// 文件名称: YieldsEditor.tsx
// 主要职责: 行动声明式收益列表编辑器；结构与 GameDataCatalog.ParseYields 消费集对齐
//           （kind/per/amount + item 的 id + buff 的 id/name/durationSeconds/modifiers）
// ==============================================================================

import type { ActionYieldConfig, ActionYieldModifiers } from '../../types/config';
import { ACTION_YIELD_KINDS, ACTION_YIELD_PERS } from '../../types/enums';
import { EnumSelect } from './EnumSelect';
import { NumberInput } from './NumberInput';
import { ReferenceSelect } from './ReferenceSelect';
import { StatDictEditor } from './StatDictEditor';

interface YieldsEditorProps {
  label: string;
  description?: string;
  yields?: ActionYieldConfig[] | null;
  onChange: (yields: ActionYieldConfig[] | undefined) => void;
  className?: string;
}

export function YieldsEditor({
  label,
  description,
  yields = [],
  onChange,
  className = ''
}: YieldsEditorProps) {
  const currentYields = yields || [];

  const handleAdd = () => {
    const next: ActionYieldConfig[] = [
      ...currentYields,
      { kind: 'item', per: 'repeat', id: '', amount: 1 }
    ];
    onChange(next);
  };

  const handleUpdate = (index: number, patch: Partial<ActionYieldConfig>) => {
    const next = [...currentYields];
    next[index] = { ...next[index], ...patch };
    onChange(next);
  };

  const handleUpdateModifiers = (index: number, patch: Partial<ActionYieldModifiers> | null) => {
    const current = currentYields[index];
    if (patch === null) {
      handleUpdate(index, { modifiers: undefined });
      return;
    }
    handleUpdate(index, { modifiers: { ...(current.modifiers || {}), ...patch } });
  };

  const handleRemove = (index: number) => {
    const next = currentYields.filter((_, i) => i !== index);
    onChange(next.length > 0 ? next : undefined);
  };

  return (
    <div className={`p-4 bg-zinc-900/60 border border-zinc-800/80 rounded-xl flex flex-col gap-3 ${className}`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <label className="text-xs font-semibold text-zinc-200 tracking-tight">{label}</label>
          <span className="px-2 py-0.5 text-[10px] font-mono font-medium rounded-full bg-zinc-800 text-zinc-400">
            {currentYields.length} 条收益
          </span>
        </div>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2.5">
        {currentYields.map((y, idx) => (
          <div
            key={idx}
            className="p-3.5 bg-zinc-900/90 border border-zinc-800 rounded-lg flex flex-col gap-2.5 text-xs hover:border-zinc-700 transition-colors"
          >
            <div className="flex items-center justify-between">
              <span className="font-semibold text-zinc-300 flex items-center gap-1.5">
                <span className="w-1.5 h-1.5 rounded-full bg-emerald-400" />
                <span>收益 #{idx + 1}</span>
              </span>
              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="text-zinc-500 hover:text-rose-400 p-1 hover:bg-zinc-800 rounded transition-colors text-sm leading-none"
              >
                ✕
              </button>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
              <EnumSelect
                label="收益类型 (Kind)"
                value={y.kind}
                options={ACTION_YIELD_KINDS}
                onChange={v => handleUpdate(idx, { kind: v || 'item' })}
                allowClear={false}
              />

              <EnumSelect
                label="结算频率 (Per)"
                value={y.per}
                options={ACTION_YIELD_PERS}
                onChange={v => handleUpdate(idx, { per: v || 'repeat' })}
                allowClear={false}
              />

              <NumberInput
                label="数量 (Amount)"
                value={y.amount}
                onChange={v => handleUpdate(idx, { amount: v })}
                min={0}
              />
            </div>

            {y.kind === 'item' && (
              <ReferenceSelect
                kind="item"
                label="产出物品 (Id)"
                value={y.id}
                onChange={id => handleUpdate(idx, { id: id || '' })}
                placeholder="选择产出的道具/装备..."
                allowClear={false}
              />
            )}

            {y.kind === 'buff' && (
              <div className="flex flex-col gap-3 pt-1 border-t border-zinc-800/60">
                <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
                  <div className="flex flex-col gap-1.5">
                    <label className="text-[11px] text-zinc-400 font-medium">Buff ID</label>
                    <input
                      type="text"
                      value={y.id || ''}
                      onChange={e => handleUpdate(idx, { id: e.target.value })}
                      placeholder="例如 buff.qi-infusion"
                      className="bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-1 text-xs text-zinc-100 font-mono outline-none"
                    />
                  </div>

                  <div className="flex flex-col gap-1.5">
                    <label className="text-[11px] text-zinc-400 font-medium">Buff 名称</label>
                    <input
                      type="text"
                      value={y.name || ''}
                      onChange={e => handleUpdate(idx, { name: e.target.value })}
                      className="bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-1 text-xs text-zinc-100 outline-none"
                    />
                  </div>

                  <NumberInput
                    label="持续时长 (DurationSeconds)"
                    value={y.durationSeconds}
                    onChange={v => handleUpdate(idx, { durationSeconds: v })}
                    min={0}
                    unit="秒"
                  />

                  <NumberInput
                    label="气血回复 (Modifiers.HpRegen)"
                    value={y.modifiers?.hpRegen}
                    onChange={v => handleUpdateModifiers(idx, { hpRegen: v })}
                    step={0.5}
                  />

                  <NumberInput
                    label="灵力回复 (Modifiers.MpRegen)"
                    value={y.modifiers?.mpRegen}
                    onChange={v => handleUpdateModifiers(idx, { mpRegen: v })}
                    step={0.5}
                  />

                  <NumberInput
                    label="攻击速度点数 (Modifiers.AttackSpeedPoints)"
                    value={y.modifiers?.attackSpeedPoints}
                    onChange={v => handleUpdateModifiers(idx, { attackSpeedPoints: v })}
                    step={1}
                    unit="点"
                  />

                  <NumberInput
                    label="免伤点数 (Modifiers.DamageAbsorptionPoints)"
                    value={y.modifiers?.damageAbsorptionPoints}
                    onChange={v => handleUpdateModifiers(idx, { damageAbsorptionPoints: v })}
                    step={1}
                    unit="点"
                  />
                </div>

                <StatDictEditor
                  label="属性倍率修正 (Modifiers.StatRatios)"
                  value={y.modifiers?.statRatios}
                  onChange={dict => handleUpdateModifiers(idx, { statRatios: dict })}
                />
              </div>
            )}
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={handleAdd}
        className="w-full py-2 bg-zinc-900/40 hover:bg-zinc-800/80 border border-dashed border-zinc-700 text-xs font-medium text-zinc-400 hover:text-indigo-300 rounded-lg transition-all duration-150 flex items-center justify-center gap-1.5"
      >
        <span>+ 添加收益条目</span>
      </button>
    </div>
  );
}
