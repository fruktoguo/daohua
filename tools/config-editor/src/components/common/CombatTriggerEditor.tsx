// ==============================================================================
// 文件名称: CombatTriggerEditor.tsx
// 主要职责: 战斗触发器列表配置器，支持条件、阈值与动作全部下拉选择
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
    <div className={`p-3 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-2.5 ${className}`}>
      <div className="flex items-center justify-between">
        <label className="text-xs font-semibold text-zinc-200 flex items-center gap-2">
          <span>{label}</span>
          <span className="px-1.5 py-0.2 text-[10px] bg-zinc-800 text-zinc-400 rounded">
            {currentTriggers.length} 条战斗策略
          </span>
        </label>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2">
        {currentTriggers.map((trig, idx) => (
          <div
            key={idx}
            className="p-2.5 bg-[#141b24] border border-zinc-700/60 rounded-md flex flex-col gap-2 text-xs"
          >
            <div className="flex items-center justify-between">
              <span className="font-semibold text-zinc-300">策略 #{idx + 1}</span>
              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="text-zinc-400 hover:text-red-400 p-1 hover:bg-zinc-800 rounded"
              >
                删除此条
              </button>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-3 gap-2">
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
                label="指定目标神通技能"
                value={trig.targetAbilityId}
                onChange={id => handleUpdate(idx, { targetAbilityId: id })}
                placeholder="默认使用自身技能或指定技能..."
              />
            )}

            {trig.action === 'useItem' && (
              <ReferenceSelect
                kind="item"
                label="指定消耗品道具"
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
        className="w-full py-1.5 bg-[#141b24] hover:bg-zinc-800 border border-dashed border-zinc-700 text-xs text-zinc-400 hover:text-cyan-300 rounded transition-colors"
      >
        + 添加战斗触发条件
      </button>
    </div>
  );
}
