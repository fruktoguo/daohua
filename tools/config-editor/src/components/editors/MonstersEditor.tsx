// ==============================================================================
// 文件名称: MonstersEditor.tsx
// 主要职责: 现代扁平化 monsters.json 专属编辑器，支持品质过滤、攻防抗性矩阵与掉落
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { CombatantConfig, CharacterVisualDefinition } from '../../types/config';
import {
  UNIT_RANKS,
  TIER_OPTIONS,
  COMBAT_STYLES,
  DAMAGE_CATEGORIES,
  ELEMENTS,
  VISUAL_RENDERER_KINDS
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
  const [filterTier, setFilterTier] = useState<number | 'all'>('all');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // 过滤列表
  const filteredMonsters = useMemo(() => {
    return monsters.filter(m => {
      if (filterRank !== 'all' && m.rank !== filterRank) return false;
      if (filterTier !== 'all' && m.tier !== filterTier) return false;
      if (searchTerm.trim()) {
        const term = searchTerm.toLowerCase();
        return m.id.toLowerCase().includes(term) || m.name.toLowerCase().includes(term);
      }
      return true;
    });
  }, [monsters, filterRank, filterTier, searchTerm]);

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

  const handleAdd = () => {
    const newId = `monster_${Date.now().toString(36)}`;
    const newMonster: CombatantConfig = {
      id: newId,
      name: '新精怪妖兽',
      rank: 'normal',
      level: 1,
      tier: 1,
      style: 'body',
      category: 'physical',
      element: 'none',
      visual: { renderer: 'text', source: '妖' },
      maxHitpoints: 100,
      attackIntervalMilliseconds: 1000
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
              全品质
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

          <div className="flex items-center gap-2 pt-0.5">
            <span className="text-[11px] text-zinc-500 font-medium shrink-0">品阶:</span>
            <select
              value={String(filterTier)}
              onChange={e => setFilterTier(e.target.value === 'all' ? 'all' : parseInt(e.target.value))}
              className="w-full bg-zinc-900 border border-zinc-800 text-[11px] text-zinc-300 rounded-md px-2 py-1 outline-none focus:border-indigo-500"
            >
              <option value="all">全阶位 (T1~T12)</option>
              {TIER_OPTIONS.map(t => (
                <option key={t.value} value={t.value}>
                  T{t.value} {t.label.split('·')[1]}
                </option>
              ))}
            </select>
          </div>
        </div>

        {/* 列表项 */}
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-1.5">
          {filteredMonsters.map(m => {
            const isSelected = m.id === currentMonster?.id;
            const rankOpt = UNIT_RANKS.find(r => r.value === m.rank);
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
                  <span>Lv.{m.level || 1} · T{m.tier || 1}</span>
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

              <EnumSelect
                label="妖兽品质等阶 (Rank)"
                value={currentMonster.rank}
                options={UNIT_RANKS}
                onChange={v => handleUpdate({ rank: v })}
              />

              <EnumSelect
                label="品阶 (Tier)"
                value={currentMonster.tier}
                options={TIER_OPTIONS}
                onChange={v => handleUpdate({ tier: v })}
              />

              <NumberInput
                label="基础等级 (Level)"
                value={currentMonster.level}
                onChange={v => handleUpdate({ level: v })}
                min={1}
              />

              <EnumSelect
                label="战斗流派 (Style)"
                value={currentMonster.style}
                options={COMBAT_STYLES}
                onChange={v => handleUpdate({ style: v || 'body' })}
                allowClear={false}
              />

              <EnumSelect
                label="伤害类型 (Category)"
                value={currentMonster.category}
                options={DAMAGE_CATEGORIES}
                onChange={v => handleUpdate({ category: v || 'physical' })}
                allowClear={false}
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

          {/* 攻防抗性矩阵卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">气血生命与攻防抗性矩阵</h3>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
              <NumberInput
                label="最大气血 (MaxHitpoints)"
                value={currentMonster.maxHitpoints}
                onChange={v => handleUpdate({ maxHitpoints: v })}
                min={1}
                unit="HP"
              />

              <NumberInput
                label="最大灵力 (MaxManapoints)"
                value={currentMonster.maxManapoints}
                onChange={v => handleUpdate({ maxManapoints: v })}
                unit="MP"
              />

              <NumberInput
                label="攻击间隔 (AttackInterval)"
                value={currentMonster.attackIntervalMilliseconds}
                onChange={v => handleUpdate({ attackIntervalMilliseconds: v })}
                step={100}
                unit="ms"
              />

              <NumberInput
                label="物理护甲 (Armor)"
                value={currentMonster.armor}
                onChange={v => handleUpdate({ armor: v })}
                min={0}
              />

              <NumberInput
                label="法术抗性 (MagicResistance)"
                value={currentMonster.magicResistance}
                onChange={v => handleUpdate({ magicResistance: v })}
                min={0}
              />

              <NumberInput
                label="金系抗性"
                value={currentMonster.metalResistance}
                onChange={v => handleUpdate({ metalResistance: v })}
              />

              <NumberInput
                label="木系抗性"
                value={currentMonster.woodResistance}
                onChange={v => handleUpdate({ woodResistance: v })}
              />

              <NumberInput
                label="水系抗性"
                value={currentMonster.waterResistance}
                onChange={v => handleUpdate({ waterResistance: v })}
              />

              <NumberInput
                label="火系抗性"
                value={currentMonster.fireResistance}
                onChange={v => handleUpdate({ fireResistance: v })}
              />

              <NumberInput
                label="土系抗性"
                value={currentMonster.earthResistance}
                onChange={v => handleUpdate({ earthResistance: v })}
              />

              <NumberInput
                label="命中等级"
                value={currentMonster.accuracyRating}
                onChange={v => handleUpdate({ accuracyRating: v })}
              />

              <NumberInput
                label="闪避等级"
                value={currentMonster.evasionRating}
                onChange={v => handleUpdate({ evasionRating: v })}
              />

              <NumberInput
                label="暴击等级"
                value={currentMonster.criticalRating}
                onChange={v => handleUpdate({ criticalRating: v })}
              />

              <NumberInput
                label="暴击抗性"
                value={currentMonster.criticalAvoidance}
                onChange={v => handleUpdate({ criticalAvoidance: v })}
              />

              <NumberInput
                label="破招等级"
                value={currentMonster.breakRating}
                onChange={v => handleUpdate({ breakRating: v })}
              />

              <NumberInput
                label="招架等级"
                value={currentMonster.parryRating}
                onChange={v => handleUpdate({ parryRating: v })}
              />
            </div>
          </div>

          {/* 技能装配 */}
          <SkillAssemblyEditor
            label="精怪装配神通技能 (Skills)"
            description="妖兽在战斗中自动释放的神通法决及释放血线"
            skills={currentMonster.skills}
            onChange={skills => handleUpdate({ skills })}
          />

          {/* 掉落表配置 */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
            <DropTableEditor
              label="常规击杀掉落物 (DropTable)"
              description="基础材料与普通战利品"
              drops={currentMonster.dropTable}
              onChange={drops => handleUpdate({ dropTable: drops })}
            />

            <DropTableEditor
              label="稀有专属掉落物 (RareDropTable)"
              description="极品法宝、妖丹或特殊奇物"
              drops={currentMonster.rareDropTable}
              onChange={drops => handleUpdate({ rareDropTable: drops })}
            />
          </div>

          {/* 收益与狂暴卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">击败收益与狂暴时长</h3>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <NumberInput
                label="击杀经验值 (Experience)"
                value={currentMonster.experience}
                onChange={v => handleUpdate({ experience: v })}
                min={0}
              />
              <NumberInput
                label="掉落灵石下限 (SpiritMin)"
                value={currentMonster.spiritMin}
                onChange={v => handleUpdate({ spiritMin: v })}
                min={0}
              />
              <NumberInput
                label="掉落灵石上限 (SpiritMax)"
                value={currentMonster.spiritMax}
                onChange={v => handleUpdate({ spiritMax: v })}
                min={0}
              />
              <NumberInput
                label="狂暴倒计时 (EnrageTimeMs)"
                value={currentMonster.enrageTimeMilliseconds}
                onChange={v => handleUpdate({ enrageTimeMilliseconds: v })}
                step={1000}
                unit="ms"
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
