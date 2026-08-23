// ==============================================================================
// 文件名称: SkillAssemblyEditor.tsx
// 主要职责: 现代扁平化精怪神通技能装配配置组件
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
    <div className={`p-4 bg-zinc-900/60 border border-zinc-800/80 rounded-xl flex flex-col gap-3 ${className}`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <label className="text-xs font-semibold text-zinc-200 tracking-tight">{label}</label>
          <span className="px-2 py-0.5 text-[10px] font-mono font-medium rounded-full bg-zinc-800 text-zinc-400">
            {currentSkills.length} 门神通
          </span>
        </div>
        {description && <span className="text-[11px] text-zinc-500">{description}</span>}
      </div>

      <div className="flex flex-col gap-2">
        {currentSkills.map((sk, idx) => (
          <div
            key={idx}
            className="p-3 bg-zinc-900/90 border border-zinc-800 rounded-lg flex flex-col md:flex-row items-start md:items-center gap-3 text-xs hover:border-zinc-700 transition-colors"
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
                  label="释放血线 (≤HP%)"
                  value={sk.triggerHpRatio ?? 1.0}
                  onChange={v => handleUpdate(idx, { triggerHpRatio: v })}
                  step={0.05}
                  min={0}
                  max={1}
                  unit="×"
                />
              </div>

              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="mt-4 p-1.5 text-zinc-500 hover:text-rose-400 hover:bg-zinc-800 rounded-md transition-colors text-sm leading-none"
                title="移除此技能"
              >
                ✕
              </button>
            </div>
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={handleAdd}
        className="w-full py-2 bg-zinc-900/40 hover:bg-zinc-800/80 border border-dashed border-zinc-700 text-xs font-medium text-zinc-400 hover:text-indigo-300 rounded-lg transition-all duration-150 flex items-center justify-center gap-1.5"
      >
        <span>+ 装配新神通技能</span>
      </button>
    </div>
  );
}
