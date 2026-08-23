// ==============================================================================
// 文件名称: CombatTriggerEditor.tsx
// 主要职责: 现代扁平化战斗触发策略配置组件
// ==============================================================================

import type { CombatTrigger } from '../../types/config';
import { COMBAT_TRIGGER_CONDITIONS, COMBAT_TRIGGER_ACTIONS } from '../../types/enums';
import { EnumSelect } from './EnumSelect';
import { ReferenceSelect } from './ReferenceSelect';
import { NumberInput } from './NumberInput';

interface CombatTriggerEditorProps {
  label: string;
  description?: string;
  triggers?: CombatTrigger[] | null;
  onChange: (triggers: CombatTrigger[] | undefined) => void;
  className?: string;
}

export function CombatTriggerEditor({
  label,
  description,
  triggers = [],
  onChange,
  className = ''
}: CombatTriggerEditorProps) {
  const currentTriggers = triggers || [];

  const handleAdd = () => {
    const next = [
      ...currentTriggers,
      {
        condition: 'hpBelow',
        thresholdRatio: 0.5,
        action: 'useAbility'
      }
    ];
    onChange(next);
  };

  const handleUpdate = (index: number, patch: Partial<CombatTrigger>) => {
    const next = [...currentTriggers];
    next[index] = { ...next[index], ...patch };
    onChange(next);
  };

  const handleRemove = (index: number) => {
    const next = currentTriggers.filter((_, i) => i !== index);
    onChange(next.length > 0 ? next : undefined);
  };

  return (
    <div className={`p-4 bg-zinc-900/60 border border-zinc-800/80 rounded-xl flex flex-col gap-3 ${className}`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <label className="text-xs font-semibold text-zinc-200 tracking-tight">{label}</label>
          <span className="px-2 py-0.5 text-[10px] font-mono font-medium rounded-full bg-zinc-800 text-zinc-400">
            {currentTriggers.length} 条策略
          </span>
        </div>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2.5">
        {currentTriggers.map((trig, idx) => (
          <div
            key={idx}
            className="p-3.5 bg-zinc-900/90 border border-zinc-800 rounded-lg flex flex-col gap-2.5 text-xs hover:border-zinc-700 transition-colors"
          >
            <div className="flex items-center justify-between">
              <span className="font-semibold text-zinc-300 flex items-center gap-1.5">
                <span className="w-1.5 h-1.5 rounded-full bg-indigo-400" />
                <span>策略 #{idx + 1}</span>
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
                label="触发条件"
                value={trig.condition}
                options={COMBAT_TRIGGER_CONDITIONS}
                onChange={v => handleUpdate(idx, { condition: v || 'hpBelow' })}
                allowClear={false}
              />

              <NumberInput
                label="阈值比例 (0.0 ~ 1.0)"
                value={trig.thresholdRatio}
                onChange={v => handleUpdate(idx, { thresholdRatio: v ?? 0.5 })}
                step={0.05}
                min={0}
                max={1}
                unit="×"
              />

              <EnumSelect
                label="触发动作"
                value={trig.action}
                options={COMBAT_TRIGGER_ACTIONS}
                onChange={v => handleUpdate(idx, { action: v || 'useAbility' })}
                allowClear={false}
              />
            </div>

            {trig.action === 'useAbility' && (
              <ReferenceSelect
                kind="ability"
                label="指定神通技能"
                value={trig.targetAbilityId}
                onChange={id => handleUpdate(idx, { targetAbilityId: id })}
                placeholder="默认使用自身神通或指定技能..."
              />
            )}

            {trig.action === 'useItem' && (
              <ReferenceSelect
                kind="item"
                label="指定消耗品灵丹"
                value={trig.targetItemId}
                onChange={id => handleUpdate(idx, { targetItemId: id })}
                placeholder="选择要服食的丹药/道具..."
              />
            )}
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={handleAdd}
        className="w-full py-2 bg-zinc-900/40 hover:bg-zinc-800/80 border border-dashed border-zinc-700 text-xs font-medium text-zinc-400 hover:text-indigo-300 rounded-lg transition-all duration-150 flex items-center justify-center gap-1.5"
      >
        <span>+ 添加战斗触发条件</span>
      </button>
    </div>
  );
}
