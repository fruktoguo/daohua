// ==============================================================================
// 文件名称: SkillAssemblyEditor.tsx
// 主要职责: 怪物技能装配配置器，支持技能下拉选择、技能等级与血线触发比例
// ==============================================================================

import type { MonsterSkillConfig } from '../../types/config';
import { ReferenceSelect } from './ReferenceSelect';
import { NumberInput } from './NumberInput';

interface SkillAssemblyEditorProps {
  label: string;
  description?: string;
  skills?: MonsterSkillConfig[] | null;
  onChange: (skills: MonsterSkillConfig[] | undefined) => void;
  className?: string;
}

export function SkillAssemblyEditor({
  label,
  description,
  skills = [],
  onChange,
  className = ''
}: SkillAssemblyEditorProps) {
  const currentSkills = skills || [];

  const handleAdd = () => {
    const next = [...currentSkills, { abilityId: '', level: 1, triggerHpRatio: 1.0 }];
    onChange(next);
  };

  const handleUpdate = (index: number, patch: Partial<MonsterSkillConfig>) => {
    const next = [...currentSkills];
    next[index] = { ...next[index], ...patch };
    onChange(next);
  };

  const handleRemove = (index: number) => {
    const next = currentSkills.filter((_, i) => i !== index);
    onChange(next.length > 0 ? next : undefined);
  };

  return (
    <div className={`p-3 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-2.5 ${className}`}>
      <div className="flex items-center justify-between">
        <label className="text-xs font-semibold text-zinc-200 flex items-center gap-2">
          <span>{label}</span>
          <span className="px-1.5 py-0.2 text-[10px] bg-zinc-800 text-zinc-400 rounded">
            已装配 {currentSkills.length} 门神通
          </span>
        </label>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2">
        {currentSkills.map((sk, idx) => (
          <div
            key={idx}
            className="p-2.5 bg-[#141b24] border border-zinc-700/60 rounded-md flex flex-col md:flex-row items-start md:items-center gap-3 text-xs"
          >
            <div className="flex-1 w-full min-w-[200px]">
              <ReferenceSelect
                kind="ability"
                value={sk.abilityId}
                onChange={id => handleUpdate(idx, { abilityId: id || '' })}
                placeholder="请选择装配的神通技能..."
                allowClear={false}
              />
            </div>

            <div className="flex items-center gap-2 w-full md:w-auto">
              <div className="w-24">
                <NumberInput
                  label="技能等级"
                  value={sk.level ?? 1}
                  onChange={v => handleUpdate(idx, { level: v ?? 1 })}
                  min={1}
                  max={20}
                />
              </div>

              <div className="w-28">
                <NumberInput
                  label="触发血线(≤HP%)"
                  value={sk.triggerHpRatio ?? 1.0}
                  onChange={v => handleUpdate(idx, { triggerHpRatio: v })}
                  step={0.05}
                  min={0}
                  max={1}
                  unit="倍"
                />
              </div>

              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="mt-4 p-1.5 text-zinc-400 hover:text-red-400 hover:bg-zinc-800 rounded text-sm"
                title="移除此技能"
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
        + 装配新神通技能
      </button>
    </div>
  );
}
