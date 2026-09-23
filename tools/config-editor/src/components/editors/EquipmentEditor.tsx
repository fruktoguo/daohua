// ==============================================================================
// 文件名称: EquipmentEditor.tsx
// 主要职责: 现代扁平化 equipment.json 专属编辑器，支持部位过滤与三段式属性字典
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { EquipmentConfig } from '../../types/config';
import {
  EQUIPMENT_SLOTS,
  TIER_OPTIONS
} from '../../types/enums';
import { EnumSelect } from '../common/EnumSelect';
import { NumberInput } from '../common/NumberInput';
import { StatDictEditor } from '../common/StatDictEditor';

export function EquipmentEditor() {
  const { configs, updateConfig } = useConfigContext();
  const equipment = configs?.['equipment.json'] || [];

  const [selectedId, setSelectedId] = useState<string>(equipment[0]?.id || '');
  const [filterSlot, setFilterSlot] = useState<string>('all');
  const [filterTier, setFilterTier] = useState<number | 'all'>('all');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // 过滤列表
  const filteredEquipment = useMemo(() => {
    return equipment.filter(eq => {
      if (filterSlot !== 'all' && eq.slot !== filterSlot) return false;
      if (filterTier !== 'all' && eq.tier !== filterTier) return false;
      if (searchTerm.trim()) {
        const term = searchTerm.toLowerCase();
        return eq.id.toLowerCase().includes(term) || eq.name.toLowerCase().includes(term);
      }
      return true;
    });
  }, [equipment, filterSlot, filterTier, searchTerm]);

  // 当前正在编辑的装备
  const currentEquipment = useMemo(() => {
    return equipment.find(e => e.id === selectedId) || filteredEquipment[0];
  }, [equipment, selectedId, filteredEquipment]);

  const handleUpdate = (patch: Partial<EquipmentConfig>) => {
    if (!currentEquipment) return;
    updateConfig('equipment.json', prev =>
      prev.map(e => (e.id === currentEquipment.id ? { ...e, ...patch } : e))
    );
  };

  const handleAdd = () => {
    const newId = `equip_${Date.now().toString(36)}`;
    const newEquip: EquipmentConfig = {
      id: newId,
      name: '新法宝神兵',
      description: '',
      slot: 'weapon',
      tier: 1,
      quality: 1,
      baseStatRatios: { physicalAttack: 0.2 }
    };
    updateConfig('equipment.json', prev => [newEquip, ...prev]);
    setSelectedId(newId);
  };

  const handleClone = () => {
    if (!currentEquipment) return;
    const clonedId = `${currentEquipment.id}_copy_${Date.now().toString(36).slice(-4)}`;
    const cloned: EquipmentConfig = {
      ...JSON.parse(JSON.stringify(currentEquipment)),
      id: clonedId,
      name: `${currentEquipment.name} (副本)`
    };
    updateConfig('equipment.json', prev => [cloned, ...prev]);
    setSelectedId(clonedId);
  };

  const handleDelete = () => {
    if (!currentEquipment) return;
    if (!confirm(`确定要删除装备 [${currentEquipment.name}] (${currentEquipment.id}) 吗？`)) return;
    updateConfig('equipment.json', prev => prev.filter(e => e.id !== currentEquipment.id));
    const remaining = equipment.filter(e => e.id !== currentEquipment.id);
    if (remaining.length > 0) setSelectedId(remaining[0].id);
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧列表栏 */}
      <div className="w-80 border-r border-zinc-800/80 bg-zinc-950 flex flex-col shrink-0 select-none">
        <div className="p-3.5 border-b border-zinc-800/80 flex flex-col gap-2.5">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">法宝列表 ({filteredEquipment.length})</span>
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
              onClick={() => setFilterSlot('all')}
              className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                filterSlot === 'all'
                  ? 'bg-zinc-800 text-zinc-100'
                  : 'text-zinc-500 hover:text-zinc-300'
              }`}
            >
              全部位
            </button>
            {EQUIPMENT_SLOTS.map(s => (
              <button
                key={s.value}
                type="button"
                onClick={() => setFilterSlot(s.value)}
                className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                  filterSlot === s.value
                    ? 'bg-purple-500/20 text-purple-300 border border-purple-500/30'
                    : 'text-zinc-500 hover:text-zinc-300'
                }`}
              >
                {s.label.split(' ')[0]}
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
          {filteredEquipment.map(eq => {
            const isSelected = eq.id === currentEquipment?.id;
            const slotOpt = EQUIPMENT_SLOTS.find(s => s.value === eq.slot);
            return (
              <div
                key={eq.id}
                onClick={() => setSelectedId(eq.id)}
                className={`p-3 rounded-xl cursor-pointer flex flex-col gap-1 transition-all duration-150 ${
                  isSelected
                    ? 'bg-zinc-900 border border-zinc-700/80 shadow-sm'
                    : 'hover:bg-zinc-900/50 border border-transparent'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-semibold truncate ${isSelected ? 'text-white' : 'text-zinc-200'}`}>
                    {eq.name}
                  </span>
                  {eq.tier && (
                    <span className="px-2 py-0.2 text-[10px] rounded-full bg-zinc-800 text-zinc-300 font-mono font-medium border border-zinc-700/50">
                      T{eq.tier}
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between text-[11px] text-zinc-500 font-mono">
                  <span className="truncate">{eq.id}</span>
                  {slotOpt && <span>{slotOpt.label.split(' ')[0]}</span>}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细编辑表单 */}
      {currentEquipment ? (
        <div className="flex-1 overflow-y-auto p-8 flex flex-col gap-6 bg-zinc-950">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-4 border-b border-zinc-800/80">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-zinc-100 tracking-tight">{currentEquipment.name}</h2>
              <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                {currentEquipment.id}
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

          {/* 基础信息卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">基础属性</h3>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">唯一标识符 (ID)</label>
                <input
                  type="text"
                  value={currentEquipment.id}
                  onChange={e => handleUpdate({ id: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">法宝名称 (Name)</label>
                <input
                  type="text"
                  value={currentEquipment.name || ''}
                  onChange={e => handleUpdate({ name: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                />
              </div>

              <EnumSelect
                label="装备槽位 (Slot)"
                value={currentEquipment.slot}
                options={EQUIPMENT_SLOTS}
                onChange={v => handleUpdate({ slot: v || 'weapon' })}
                allowClear={false}
              />

              <div className="md:col-span-3 flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">法宝描述 (Description)</label>
                <textarea
                  rows={2}
                  value={currentEquipment.description || ''}
                  onChange={e => handleUpdate({ description: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-2 text-xs text-zinc-200 outline-none"
                />
              </div>

              <EnumSelect
                label="装备品阶 (Tier)"
                value={currentEquipment.tier}
                options={TIER_OPTIONS}
                onChange={v => handleUpdate({ tier: v })}
              />

              <NumberInput
                label="品质等级 (Quality)"
                description="配表品质标签（0=凡品起）"
                value={currentEquipment.quality}
                onChange={v => handleUpdate({ quality: v })}
                min={0}
              />
            </div>
          </div>

          {/* 三段区间属性字典 */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
            <StatDictEditor
              label="基础属性百分比加成 (BaseStatRatios)"
              description="按 14 维基准属性百分比加成"
              value={currentEquipment.baseStatRatios}
              onChange={dict => handleUpdate({ baseStatRatios: dict })}
              unit="%"
              step={0.01}
            />

            <StatDictEditor
              label="固定附加属性点 (FlatStats)"
              description="直接附加固定的命中/闪避/攻防点数"
              value={currentEquipment.flatStats}
              onChange={dict => handleUpdate({ flatStats: dict })}
              unit="点"
              step={1}
            />

            <StatDictEditor
              label="特殊机制属性 (SpecialMechanics)"
              description="百分比点数键：threatBonusPoints/hpDrainPoints/damageReflectPoints/各增减伤 Points 等"
              value={currentEquipment.specialMechanics}
              onChange={dict => handleUpdate({ specialMechanics: dict })}
              unit="%"
              step={0.01}
            />

            <StatDictEditor
              label="强化附加加成 (EnhancementBonus)"
              description="每级强化额外属性（映射层透传展示）"
              value={currentEquipment.enhancementBonus}
              onChange={dict => handleUpdate({ enhancementBonus: dict })}
              unit="%"
              step={0.01}
            />
          </div>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-zinc-500 text-sm">
          请在左侧选择或新增一件法宝装备
        </div>
      )}
    </div>
  );
}
