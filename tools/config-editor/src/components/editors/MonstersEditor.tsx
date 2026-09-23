// ==============================================================================
// 文件名称: MonstersEditor.tsx
// 主要职责: 现代扁平化 monsters 专属编辑器；字段与 GameDataCatalog.ParseMonsterDefinitions
//           消费集对齐——战斗属性全部由「等级锚点 × 品阶倍率 × weights 倾向权重」派生
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { CombatantConfig, CharacterVisualDefinition } from '../../types/config';
import {
  UNIT_RANKS,
  COMBAT_STYLES,
  DAMAGE_CATEGORIES,
  ELEMENTS,
  VISUAL_RENDERER_KINDS,
  MONSTER_WEIGHT_KEYS,
  MONSTER_MODIFIER_KEYS
} from '../../types/enums';
import { EnumSelect } from '../common/EnumSelect';
import { NumberInput } from '../common/NumberInput';
import { SkillAssemblyEditor } from '../common/SkillAssemblyEditor';
import { DropTableEditor } from '../common/DropTableEditor';

export function MonstersEditor() {
  const { configs, updateConfig } = useConfigContext();
  const monsters = configs?.['monsters.json'] || [];

  const [selectedId, setSelectedId] = useState<string>(monsters[0]?.id || '');
  const [filterRank, setFilterRank] = useState<string>('all');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // 过滤列表
  const filteredMonsters = useMemo(() => {
    return monsters.filter(m => {
      if (filterRank !== 'all' && (m.rank || 'minion') !== filterRank) return false;
      if (searchTerm.trim()) {
        const term = searchTerm.toLowerCase();
        return m.id.toLowerCase().includes(term) || m.name.toLowerCase().includes(term);
      }
      return true;
    });
  }, [monsters, filterRank, searchTerm]);

  // 当前正在编辑的怪物
  const currentMonster = useMemo(() => {
    return monsters.find(m => m.id === selectedId) || filteredMonsters[0];
  }, [monsters, selectedId, filteredMonsters]);

  const handleUpdate = (patch: Partial<CombatantConfig>) => {
    if (!currentMonster) return;
    updateConfig('monsters.json', prev =>
      prev.map(m => (m.id === currentMonster.id ? { ...m, ...patch } : m))
    );
  };

  const handleUpdateVisual = (patch: Partial<CharacterVisualDefinition>) => {
    if (!currentMonster) return;
    const current = currentMonster.visual || { renderer: 'text', source: currentMonster.name?.slice(0, 1) || '妖' };
    handleUpdate({ visual: { ...current, ...patch } });
  };

  const handleUpdateWeight = (key: string, value: number | undefined) => {
    if (!currentMonster) return;
    const next = { ...(currentMonster.weights || {}) };
    if (value === undefined) {
      delete next[key];
    } else {
      next[key] = value;
    }
    handleUpdate({ weights: Object.keys(next).length > 0 ? next : undefined });
  };

  const handleUpdateModifier = (key: string, value: number | undefined) => {
    if (!currentMonster) return;
    const next = { ...(currentMonster.modifiers || {}) };
    if (value === undefined) {
      delete next[key];
    } else {
      next[key] = value;
    }
    handleUpdate({ modifiers: Object.keys(next).length > 0 ? next : undefined });
  };

  const handleAdd = () => {
    const newId = `enemy.new-${Date.now().toString(36)}`;
    const newMonster: CombatantConfig = {
      id: newId,
      name: '新精怪妖兽',
      rank: 'minion',
      level: 1,
      budgetCost: 10,
      element: 'none',
      style: 'body',
      visual: { renderer: 'text', source: '妖' },
      weights: { hp: 1 },
      qiReward: 10,
      experienceReward: 10,
      rootExpReward: 10
    };
    updateConfig('monsters.json', prev => [newMonster, ...prev]);
    setSelectedId(newId);
  };

  const handleClone = () => {
    if (!currentMonster) return;
    const clonedId = `${currentMonster.id}_copy_${Date.now().toString(36).slice(-4)}`;
    const cloned: CombatantConfig = {
      ...JSON.parse(JSON.stringify(currentMonster)),
      id: clonedId,
      name: `${currentMonster.name} (副本)`
    };
    updateConfig('monsters.json', prev => [cloned, ...prev]);
    setSelectedId(clonedId);
  };

  const handleDelete = () => {
    if (!currentMonster) return;
    if (!confirm(`确定要删除妖兽 [${currentMonster.name}] (${currentMonster.id}) 吗？`)) return;
    updateConfig('monsters.json', prev => prev.filter(m => m.id !== currentMonster.id));
    const remaining = monsters.filter(m => m.id !== currentMonster.id);
    if (remaining.length > 0) setSelectedId(remaining[0].id);
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧列表栏 */}
      <div className="w-80 border-r border-zinc-800/80 bg-zinc-950 flex flex-col shrink-0 select-none">
        <div className="p-3.5 border-b border-zinc-800/80 flex flex-col gap-2.5">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">精怪列表 ({filteredMonsters.length})</span>
            <button
              type="button"
              onClick={handleAdd}
              className="px-2.5 py-1 bg-indigo-600 hover:bg-indigo-500 text-white font-medium text-xs rounded-lg transition-colors shadow-sm"
            >
              + 新增
            </button>
          </div>

          <div className="relative">
            <input
              type="text"
              placeholder="搜索名称 / ID..."
              value={searchTerm}
              onChange={e => setSearchTerm(e.target.value)}
              className="w-full bg-zinc-900 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-100 placeholder-zinc-500 outline-none transition-all"
            />
          </div>

          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => setFilterRank('all')}
              className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                filterRank === 'all'
                  ? 'bg-zinc-800 text-zinc-100'
                  : 'text-zinc-500 hover:text-zinc-300'
              }`}
            >
              全品阶
            </button>
            {UNIT_RANKS.map(r => (
              <button
                key={r.value}
                type="button"
                onClick={() => setFilterRank(r.value)}
                className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                  filterRank === r.value
                    ? 'bg-rose-500/20 text-rose-300 border border-rose-500/30'
                    : 'text-zinc-500 hover:text-zinc-300'
                }`}
              >
                {r.label.split(' ')[0]}
              </button>
            ))}
          </div>
        </div>

        {/* 列表项 */}
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-1.5">
          {filteredMonsters.map(m => {
            const isSelected = m.id === currentMonster?.id;
            const rankOpt = UNIT_RANKS.find(r => r.value === (m.rank || 'minion'));
            return (
              <div
                key={m.id}
                onClick={() => setSelectedId(m.id)}
                className={`p-3 rounded-xl cursor-pointer flex flex-col gap-1 transition-all duration-150 ${
                  isSelected
                    ? 'bg-zinc-900 border border-zinc-700/80 shadow-sm'
                    : 'hover:bg-zinc-900/50 border border-transparent'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-semibold truncate ${isSelected ? 'text-white' : 'text-zinc-200'}`}>
                    {m.name}
                  </span>
                  {rankOpt && (
                    <span className={`px-2 py-0.5 text-[10px] font-medium rounded-full border ${rankOpt.badgeColor}`}>
                      {rankOpt.label.split(' ')[0]}
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between text-[11px] text-zinc-500 font-mono">
                  <span className="truncate">{m.id}</span>
                  <span>Lv.{m.level || 1} · 预算{m.budgetCost ?? 1}</span>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细编辑表单 */}
      {currentMonster ? (
        <div className="flex-1 overflow-y-auto p-8 flex flex-col gap-6 bg-zinc-950">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-4 border-b border-zinc-800/80">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-zinc-100 tracking-tight">{currentMonster.name}</h2>
              <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                {currentMonster.id}
              </span>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={handleClone}
                className="px-3 py-1.5 bg-zinc-900 hover:bg-zinc-800 border border-zinc-800 text-xs font-medium text-zinc-300 rounded-lg transition-colors"
              >
                克隆复制
              </button>
              <button
                type="button"
                onClick={handleDelete}
                className="px-3 py-1.5 bg-rose-500/10 hover:bg-rose-500/20 border border-rose-500/30 text-xs font-medium text-rose-300 rounded-lg transition-colors"
              >
                删除
              </button>
            </div>
          </div>

          {/* 基础身份与视觉卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">身份与外观</h3>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">唯一标识符 (ID)</label>
                <input
                  type="text"
                  value={currentMonster.id}
                  onChange={e => handleUpdate({ id: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">妖兽名称 (Name)</label>
                <input
                  type="text"
                  value={currentMonster.name || ''}
                  onChange={e => handleUpdate({ name: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                />
              </div>

              <NumberInput
                label="等级 (Level)"
                description="决定锚点与派生品阶 T"
                value={currentMonster.level}
                onChange={v => handleUpdate({ level: v })}
                min={1}
                max={127}
              />

              <EnumSelect
                label="品阶 (Rank)"
                description="minion/elite/boss，决定属性与气血倍率"
                value={currentMonster.rank || 'minion'}
                options={UNIT_RANKS}
                onChange={v => handleUpdate({ rank: v })}
                allowClear={false}
              />

              <NumberInput
                label="遭遇预算成本 (BudgetCost)"
                value={currentMonster.budgetCost}
                onChange={v => handleUpdate({ budgetCost: v })}
                min={0}
              />

              <EnumSelect
                label="战斗流派 (Style)"
                description="前端展示标签"
                value={currentMonster.style}
                options={COMBAT_STYLES}
                onChange={v => handleUpdate({ style: v })}
              />

              <EnumSelect
                label="伤害类别 (Category)"
                description="前端展示标签"
                value={currentMonster.category}
                options={DAMAGE_CATEGORIES}
                onChange={v => handleUpdate({ category: v })}
              />

              <EnumSelect
                label="五行属性 (Element)"
                value={currentMonster.element}
                options={ELEMENTS}
                onChange={v => handleUpdate({ element: v || 'none' })}
                allowClear={false}
              />

              {/* 视觉配置 */}
              <EnumSelect
                label="视觉渲染器 (Visual.Renderer)"
                value={currentMonster.visual?.renderer || 'text'}
                options={VISUAL_RENDERER_KINDS}
                onChange={v => handleUpdateVisual({ renderer: v || 'text' })}
                allowClear={false}
              />

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">视觉源字符/路径</label>
                <input
                  type="text"
                  value={currentMonster.visual?.source || ''}
                  onChange={e => handleUpdateVisual({ source: e.target.value })}
                  placeholder="例如: 妖 或 wolf.svg"
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-100 font-mono outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5 md:col-span-2">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">高清立绘海报路径</label>
                <input
                  type="text"
                  value={currentMonster.visual?.poster || ''}
                  onChange={e => handleUpdateVisual({ poster: e.target.value })}
                  placeholder="例如: /assets/monsters/boss_dragon.webp"
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-100 font-mono outline-none"
                />
              </div>
            </div>
          </div>

          {/* 派生说明 + 攻速与反应效果 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">节奏与反应效果</h3>
            <p className="text-[11px] text-zinc-500 leading-relaxed">
              气血 / 攻击 / 护甲 / 命中 / 闪避 / 暴击等战斗属性由锚点派生（等级锚点 × 品阶倍率 × 下方权重），
              不在配表中直接书写；此处仅配置引擎直读的攻速基准与汲取/反弹效果包。
            </p>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <NumberInput
                label="普攻间隔基准 (AttackIntervalMilliseconds)"
                description="缺省 1000ms，再乘行动速度乘区"
                value={currentMonster.attackIntervalMilliseconds}
                onChange={v => handleUpdate({ attackIntervalMilliseconds: v })}
                step={100}
                min={0}
                unit="ms"
              />

              <NumberInput
                label="气血汲取点数 (HpDrainPoints)"
                value={currentMonster.hpDrainPoints}
                onChange={v => handleUpdate({ hpDrainPoints: v })}
                step={1}
                min={0}
                unit="点"
              />

              <NumberInput
                label="灵力汲取点数 (MpDrainPoints)"
                value={currentMonster.mpDrainPoints}
                onChange={v => handleUpdate({ mpDrainPoints: v })}
                step={1}
                min={0}
                unit="点"
              />

              <NumberInput
                label="伤害反弹点数 (DamageReflectPoints)"
                value={currentMonster.damageReflectPoints}
                onChange={v => handleUpdate({ damageReflectPoints: v })}
                step={1}
                min={0}
                unit="点"
              />
            </div>
          </div>

          {/* 倾向权重矩阵卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <div className="flex items-center justify-between">
              <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">倾向权重矩阵 (Weights)</h3>
              <span className="text-[11px] text-zinc-500">缺省键 = 1.0；增减伤点数走下方 modifiers 段</span>
            </div>
            <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-4">
              {MONSTER_WEIGHT_KEYS.map(w => (
                <NumberInput
                  key={w.key}
                  label={w.label}
                  value={currentMonster.weights?.[w.key]}
                  onChange={v => handleUpdateWeight(w.key, v)}
                  step={0.05}
                  unit="×"
                />
              ))}
            </div>
          </div>

          {/* 百分比点数修饰矩阵卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <div className="flex items-center justify-between">
              <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">点数修饰矩阵 (Modifiers)</h3>
              <span className="text-[11px] text-zinc-500">100 点 = 100%；增伤 Linear / 减伤速率 NonLinear</span>
            </div>
            <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-4">
              {MONSTER_MODIFIER_KEYS.map(w => (
                <NumberInput
                  key={w.key}
                  label={w.label}
                  value={currentMonster.modifiers?.[w.key]}
                  onChange={v => handleUpdateModifier(w.key, v)}
                  step={1}
                  unit="点"
                />
              ))}
            </div>
          </div>

          {/* 技能装配 */}
          <SkillAssemblyEditor
            label="精怪装配神通技能 (Skills)"
            description="abilityId + level + triggerHpRatio（自身气血低于该比例时可起手）"
            skills={currentMonster.skills}
            onChange={skills => handleUpdate({ skills })}
          />

          {/* 掉落表配置 */}
          <DropTableEditor
            label="击杀掉落物 (DropTable)"
            description="itemId / dropChance / minQuantity / maxQuantity"
            drops={currentMonster.dropTable}
            onChange={drops => handleUpdate({ dropTable: drops })}
          />

          {/* 三项击杀奖励 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">击杀奖励</h3>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <NumberInput
                label="灵气奖励 (QiReward)"
                value={currentMonster.qiReward}
                onChange={v => handleUpdate({ qiReward: v })}
                min={0}
              />
              <NumberInput
                label="经验奖励 (ExperienceReward)"
                value={currentMonster.experienceReward}
                onChange={v => handleUpdate({ experienceReward: v })}
                min={0}
              />
              <NumberInput
                label="根骨经验奖励 (RootExpReward)"
                value={currentMonster.rootExpReward}
                onChange={v => handleUpdate({ rootExpReward: v })}
                min={0}
              />
            </div>
          </div>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-zinc-500 text-sm">
          请在左侧选择或新增一只精怪妖兽
        </div>
      )}
    </div>
  );
}
