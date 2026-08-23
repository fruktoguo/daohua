// ==============================================================================
// 文件名称: EncountersEditor.tsx
// 主要职责: 现代扁平化 encounters.json 专属编辑器，支持刷怪池与波次狂暴
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { CombatEncounterConfig, CombatSpawnConfig } from '../../types/config';
import { NumberInput } from '../common/NumberInput';
import { ReferenceSelect, MultiReferenceSelect } from '../common/ReferenceSelect';
import { DropTableEditor } from '../common/DropTableEditor';

export function EncountersEditor() {
  const { configs, updateConfig } = useConfigContext();
  const encounters = configs?.['encounters.json'] || [];

  const [selectedId, setSelectedId] = useState<string>(encounters[0]?.id || '');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // 过滤列表
  const filteredEncounters = useMemo(() => {
    return encounters.filter(enc => {
      if (searchTerm.trim()) {
        const term = searchTerm.toLowerCase();
        return enc.id.toLowerCase().includes(term) || enc.name.toLowerCase().includes(term);
      }
      return true;
    });
  }, [encounters, searchTerm]);

  // 当前正在编辑的遭遇
  const currentEncounter = useMemo(() => {
    return encounters.find(e => e.id === selectedId) || filteredEncounters[0];
  }, [encounters, selectedId, filteredEncounters]);

  const handleUpdate = (patch: Partial<CombatEncounterConfig>) => {
    if (!currentEncounter) return;
    updateConfig('encounters.json', prev =>
      prev.map(e => (e.id === currentEncounter.id ? { ...e, ...patch } : e))
    );
  };

  const handleAdd = () => {
    const newId = `encounter_${Date.now().toString(36)}`;
    const newEncounter: CombatEncounterConfig = {
      id: newId,
      name: '新秘境遭遇战',
      maxSpawnCount: 3,
      maxTotalStrength: 50,
      searchMilliseconds: 5000,
      respawnMilliseconds: 3000
    };
    updateConfig('encounters.json', prev => [newEncounter, ...prev]);
    setSelectedId(newId);
  };

  const handleClone = () => {
    if (!currentEncounter) return;
    const clonedId = `${currentEncounter.id}_copy_${Date.now().toString(36).slice(-4)}`;
    const cloned: CombatEncounterConfig = {
      ...JSON.parse(JSON.stringify(currentEncounter)),
      id: clonedId,
      name: `${currentEncounter.name} (副本)`
    };
    updateConfig('encounters.json', prev => [cloned, ...prev]);
    setSelectedId(clonedId);
  };

  const handleDelete = () => {
    if (!currentEncounter) return;
    if (!confirm(`确定要删除秘境遭遇 [${currentEncounter.name}] (${currentEncounter.id}) 吗？`)) return;
    updateConfig('encounters.json', prev => prev.filter(e => e.id !== currentEncounter.id));
    const remaining = encounters.filter(e => e.id !== currentEncounter.id);
    if (remaining.length > 0) setSelectedId(remaining[0].id);
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧列表栏 */}
      <div className="w-80 border-r border-zinc-800/80 bg-zinc-950 flex flex-col shrink-0 select-none">
        <div className="p-3.5 border-b border-zinc-800/80 flex flex-col gap-2.5">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">遭遇列表 ({filteredEncounters.length})</span>
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
              placeholder="搜索遭遇名称 / ID..."
              value={searchTerm}
              onChange={e => setSearchTerm(e.target.value)}
              className="w-full bg-zinc-900 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-100 placeholder-zinc-500 outline-none transition-all"
            />
          </div>
        </div>

        {/* 列表项 */}
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-1.5">
          {filteredEncounters.map(enc => {
            const isSelected = enc.id === currentEncounter?.id;
            return (
              <div
                key={enc.id}
                onClick={() => setSelectedId(enc.id)}
                className={`p-3 rounded-xl cursor-pointer flex flex-col gap-1 transition-all duration-150 ${
                  isSelected
                    ? 'bg-zinc-900 border border-zinc-700/80 shadow-sm'
                    : 'hover:bg-zinc-900/50 border border-transparent'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-semibold truncate ${isSelected ? 'text-white' : 'text-zinc-200'}`}>
                    {enc.name}
                  </span>
                  {enc.bossEnemyIds && enc.bossEnemyIds.length > 0 && (
                    <span className="px-2 py-0.2 text-[10px] font-medium rounded-full bg-rose-500/20 text-rose-300 border border-rose-500/30">
                      Boss
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between text-[11px] text-zinc-500 font-mono">
                  <span className="truncate">{enc.id}</span>
                  <span>{enc.randomSpawns?.length || 0} 种刷怪</span>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细编辑表单 */}
      {currentEncounter ? (
        <div className="flex-1 overflow-y-auto p-8 flex flex-col gap-6 bg-zinc-950">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-4 border-b border-zinc-800/80">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-zinc-100 tracking-tight">{currentEncounter.name}</h2>
              <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                {currentEncounter.id}
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

          {/* 基础元数据与波次容量卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">基础属性与容量</h3>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">唯一标识符 (ID)</label>
                <input
                  type="text"
                  value={currentEncounter.id}
                  onChange={e => handleUpdate({ id: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">遭遇名称 (Name)</label>
                <input
                  type="text"
                  value={currentEncounter.name || ''}
                  onChange={e => handleUpdate({ name: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                />
              </div>

              <NumberInput
                label="同屏最大刷怪数 (MaxSpawnCount)"
                description="默认 3 只"
                value={currentEncounter.maxSpawnCount}
                onChange={v => handleUpdate({ maxSpawnCount: v })}
                min={1}
                max={10}
              />

              <NumberInput
                label="总强度上限 (MaxTotalStrength)"
                description="控制小怪强度总和"
                value={currentEncounter.maxTotalStrength}
                onChange={v => handleUpdate({ maxTotalStrength: v })}
                min={1}
              />

              <NumberInput
                label="搜怪耗时 (SearchMilliseconds)"
                description="波次间搜寻等待"
                value={currentEncounter.searchMilliseconds}
                onChange={v => handleUpdate({ searchMilliseconds: v })}
                step={1000}
                unit="ms"
              />

              <NumberInput
                label="怪物复活间隔 (RespawnMilliseconds)"
                value={currentEncounter.respawnMilliseconds}
                onChange={v => handleUpdate({ respawnMilliseconds: v })}
                step={1000}
                unit="ms"
              />

              <NumberInput
                label="最小预算 (MinBudget)"
                value={currentEncounter.minBudget}
                onChange={v => handleUpdate({ minBudget: v })}
              />

              <NumberInput
                label="最大预算 (MaxBudget)"
                value={currentEncounter.maxBudget}
                onChange={v => handleUpdate({ maxBudget: v })}
              />
            </div>
          </div>

          {/* 刷怪池权重 */}
          <RandomSpawnsEditor
            spawns={currentEncounter.randomSpawns}
            onChange={spawns => handleUpdate({ randomSpawns: spawns })}
          />

          {/* 首领与精英配置卡片 */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-5 p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl">
            <div className="flex flex-col gap-4">
              <MultiReferenceSelect
                kind="monster"
                label="首领怪物列表 (BossEnemyIds)"
                description="触发 Boss 波次时随机出现的首领怪物"
                values={currentEncounter.bossEnemyIds}
                onChange={ids => handleUpdate({ bossEnemyIds: ids.length > 0 ? ids : undefined })}
                placeholder="+ 添加 Boss 怪物..."
              />

              <NumberInput
                label="首领出场间隔 (BattlesPerBoss)"
                description="每经历多少波寻常战斗出现一次 Boss"
                value={currentEncounter.battlesPerBoss}
                onChange={v => handleUpdate({ battlesPerBoss: v })}
                min={1}
                unit="波"
              />
            </div>

            <div className="flex flex-col gap-4">
              <MultiReferenceSelect
                kind="monster"
                label="精英怪物列表 (EliteEnemyIds)"
                description="精英波次刷出的强力精英怪"
                values={currentEncounter.eliteEnemyIds}
                onChange={ids => handleUpdate({ eliteEnemyIds: ids.length > 0 ? ids : undefined })}
                placeholder="+ 添加精英怪物..."
              />

              <NumberInput
                label="精英出场间隔 (EliteInterval)"
                description="每经历多少波出现一次精英"
                value={currentEncounter.eliteInterval}
                onChange={v => handleUpdate({ eliteInterval: v })}
                min={1}
                unit="波"
              />
            </div>
          </div>

          {/* 狂暴机制卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">副本狂暴与通关收益</h3>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <NumberInput
                label="狂暴倒计时 (EnrageTimeMs)"
                value={currentEncounter.enrageTimeMilliseconds}
                onChange={v => handleUpdate({ enrageTimeMilliseconds: v })}
                step={10000}
                unit="ms"
              />

              <NumberInput
                label="狂暴叠加间隔 (EnrageTickMs)"
                value={currentEncounter.enrageTickMilliseconds}
                onChange={v => handleUpdate({ enrageTickMilliseconds: v })}
                step={10000}
                unit="ms"
              />

              <NumberInput
                label="狂暴最大层数 (MaxEnrageStacks)"
                value={currentEncounter.maxEnrageStacks}
                onChange={v => handleUpdate({ maxEnrageStacks: v })}
                min={1}
              />

              <NumberInput
                label="每层伤害加成 (EnrageRatioPerStack)"
                value={currentEncounter.enrageRatioPerStack}
                onChange={v => handleUpdate({ enrageRatioPerStack: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="每场通关经验 (ExperiencePerWin)"
                value={currentEncounter.experiencePerWin}
                onChange={v => handleUpdate({ experiencePerWin: v })}
                min={0}
              />

              <NumberInput
                label="通关灵石下限 (SpiritMinPerWin)"
                value={currentEncounter.spiritMinPerWin}
                onChange={v => handleUpdate({ spiritMinPerWin: v })}
                min={0}
              />

              <NumberInput
                label="通关灵石上限 (SpiritMaxPerWin)"
                value={currentEncounter.spiritMaxPerWin}
                onChange={v => handleUpdate({ spiritMaxPerWin: v })}
                min={0}
              />
            </div>
          </div>

          {/* 掉落表配置 */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
            <DropTableEditor
              label="副本额外通关掉落 (DropTable)"
              drops={currentEncounter.dropTable}
              onChange={drops => handleUpdate({ dropTable: drops })}
            />

            <DropTableEditor
              label="稀有通关专属掉落 (RareDropTable)"
              drops={currentEncounter.rareDropTable}
              onChange={drops => handleUpdate({ rareDropTable: drops })}
            />
          </div>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-zinc-500 text-sm">
          请在左侧选择或新增一个秘境遭遇
        </div>
      )}
    </div>
  );
}

function RandomSpawnsEditor({
  spawns = [],
  onChange
}: {
  spawns?: CombatSpawnConfig[];
  onChange: (spawns: CombatSpawnConfig[] | undefined) => void;
}) {
  const currentSpawns = spawns || [];

  const handleAdd = () => {
    const next: CombatSpawnConfig = {
      enemyId: '',
      weight: 1.0,
      strength: 10
    };
    onChange([...currentSpawns, next]);
  };

  const handleUpdate = (index: number, patch: Partial<CombatSpawnConfig>) => {
    const next = [...currentSpawns];
    next[index] = { ...next[index], ...patch };
    onChange(next);
  };

  const handleRemove = (index: number) => {
    const next = currentSpawns.filter((_, i) => i !== index);
    onChange(next.length > 0 ? next : undefined);
  };

  return (
    <div className="p-4 bg-zinc-900/60 border border-zinc-800/80 rounded-xl flex flex-col gap-3">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <label className="text-xs font-semibold text-zinc-200 tracking-tight">随机刷怪池与权重 (RandomSpawns)</label>
          <span className="px-2 py-0.5 text-[10px] font-mono font-medium rounded-full bg-zinc-800 text-zinc-400">
            {currentSpawns.length} 种常驻怪
          </span>
        </div>
      </div>

      <div className="flex flex-col gap-2">
        {currentSpawns.map((sp, idx) => (
          <div
            key={idx}
            className="p-3 bg-zinc-900/90 border border-zinc-800 rounded-lg flex flex-col md:flex-row items-start md:items-center gap-3 text-xs hover:border-zinc-700 transition-colors"
          >
            <div className="flex-1 w-full min-w-[200px]">
              <ReferenceSelect
                kind="monster"
                value={sp.enemyId}
                onChange={id => handleUpdate(idx, { enemyId: id || '' })}
                placeholder="选择刷出的怪物..."
                allowClear={false}
              />
            </div>

            <div className="flex items-center gap-2 w-full md:w-auto">
              <div className="w-24">
                <NumberInput
                  label="刷出权重"
                  value={sp.weight}
                  onChange={v => handleUpdate(idx, { weight: v ?? 1 })}
                  step={0.5}
                  min={0.1}
                />
              </div>

              <div className="w-24">
                <NumberInput
                  label="怪物体量强度"
                  value={sp.strength}
                  onChange={v => handleUpdate(idx, { strength: v ?? 10 })}
                  min={1}
                />
              </div>

              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="mt-4 p-1.5 text-zinc-500 hover:text-rose-400 hover:bg-zinc-800 rounded-md transition-colors text-sm leading-none"
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
        <span>+ 增加刷怪池怪物项</span>
      </button>
    </div>
  );
}
