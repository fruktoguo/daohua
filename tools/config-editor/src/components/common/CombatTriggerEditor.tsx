// ==============================================================================
// 文件名称: CombatTriggerEditor.tsx
// 主要职责: 技能默认触发条件五元组编辑器（dependency/condition/comparator/value/buffId）
// 说明: 字段与 CombatSkillEngine.ValidateTrigger / DuelTriggerEvaluator 消费集一致
// ==============================================================================

import type { CombatTrigger } from '../../types/config';
import { TRIGGER_DEPENDENCIES, TRIGGER_CONDITIONS, TRIGGER_COMPARATORS,
  TRIGGER_SET_DEPENDENCIES, TRIGGER_SET_CONDITIONS, TRIGGER_BOOL_CONDITIONS } from '../../types/enums';
import { EnumSelect } from './EnumSelect';
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
    const next: CombatTrigger[] = [
      ...currentTriggers,
      {
        dependency: 'target-enemy',
        condition: 'current-hp',
        comparator: 'greater-than-equal',
        value: 1
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
            {currentTriggers.length} 条条件
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
                <span>条件 #{idx + 1}</span>
              </span>
              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="text-zinc-500 hover:text-rose-400 p-1 hover:bg-zinc-800 rounded transition-colors text-sm leading-none"
              >
                ✕
              </button>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
              <EnumSelect
                label="依赖目标 (Dependency)"
                value={trig.dependency}
                options={TRIGGER_DEPENDENCIES}
                onChange={v => {
                  const dependency = v || 'self';
                  // 对象-类型矩阵联动：切到集合对象时条件重置为存活数量，切回单体时还原当前气血。
                  const isSet = TRIGGER_SET_DEPENDENCIES.has(dependency);
                  const condition = isSet
                    ? (TRIGGER_SET_CONDITIONS.has(trig.condition) ? trig.condition : 'alive-count')
                    : (TRIGGER_SET_CONDITIONS.has(trig.condition) ? 'current-hp' : trig.condition);
                  handleUpdate(idx, { dependency, condition });
                }}
                allowClear={false}
              />

              <EnumSelect
                label="条件字段 (Condition)"
                value={trig.condition}
                options={TRIGGER_SET_DEPENDENCIES.has(trig.dependency)
                  ? TRIGGER_CONDITIONS.filter(option => TRIGGER_SET_CONDITIONS.has(option.value))
                  : TRIGGER_CONDITIONS.filter(option => !TRIGGER_SET_CONDITIONS.has(option.value))}
                onChange={v => {
                  const condition = v || 'current-hp';
                  // 类型-比对矩阵联动：Boolean 条件仅是否比对，数值条件仅大小等于比对。
                  const isBool = TRIGGER_BOOL_CONDITIONS.has(condition);
                  const comparator = isBool
                    ? (trig.comparator === 'is-active' || trig.comparator === 'is-inactive' ? trig.comparator : 'is-active')
                    : (trig.comparator === 'is-active' || trig.comparator === 'is-inactive' ? 'greater-than-equal' : trig.comparator);
                  handleUpdate(idx, { condition, comparator });
                }}
                allowClear={false}
              />

              <EnumSelect
                label="比较方式 (Comparator)"
                value={trig.comparator}
                options={TRIGGER_BOOL_CONDITIONS.has(trig.condition)
                  ? TRIGGER_COMPARATORS.filter(option => option.value === 'is-active' || option.value === 'is-inactive')
                  : TRIGGER_COMPARATORS.filter(option => option.value !== 'is-active' && option.value !== 'is-inactive')}
                onChange={v => {
                  const comparator = v || 'greater-than-equal';
                  handleUpdate(idx, { comparator });
                }}
                allowClear={false}
              />

              <NumberInput
                label="阈值 (Value)"
                value={trig.value}
                onChange={v => handleUpdate(idx, { value: v ?? 0 })}
                step={1}
                min={0}
              />
            </div>

            {trig.condition === 'buff-stacks' && (
              <div className="flex flex-col gap-1.5">
                <label className="text-[11px] text-zinc-400 font-medium">Buff ID（buff-stacks 条件必填）</label>
                <input
                  type="text"
                  value={trig.buffId || ''}
                  onChange={e => handleUpdate(idx, { buffId: e.target.value || null })}
                  placeholder="例如 buff.pozhen-melody"
                  className="bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-1 text-xs text-zinc-100 font-mono outline-none"
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
        <span>+ 添加触发条件</span>
      </button>
    </div>
  );
}
