// ==============================================================================
// 文件名称: EquipmentEditor.tsx
// 主要职责: equipment.json 专属编辑器，支持部位/品阶筛选、三段式属性字典与预算配置
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { EquipmentConfig } from '../../types/config';
import {
  EQUIPMENT_SLOTS,
  TIER_OPTIONS,
  COMBAT_STYLES,
  DAMAGE_CATEGORIES,
  ELEMENTS
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
      slot: 'weapon',
      tier: 1,
      style: 'body',
      category: 'physical',
      element: 'none',
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
      name: `${currentEquipment.name} (复制)`
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
      <div className="w-80 border-r border-zinc-800 bg-[#0e131a] flex flex-col shrink-0">
        <div className="p-3 border-b border-zinc-800 flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">法宝列表 ({filteredEquipment.length})</span>
            <button
              type="button"
              onClick={handleAdd}
              className="px-2 py-0.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-300 text-xs rounded transition-colors"
            >
              + 新增法宝
            </button>
          </div>

          <input
            type="text"
            placeholder="搜索名称 / ID..."
            value={searchTerm}
            onChange={e => setSearchTerm(e.target.value)}
            className="w-full bg-[#141b24] border border-zinc-700/80 rounded px-2.5 py-1 text-xs text-zinc-100 placeholder-zinc-500 outline-none"
          />

          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => setFilterSlot('all')}
              className={`px-2 py-0.5 text-[11px] rounded ${
                filterSlot === 'all'
                  ? 'bg-zinc-700 text-zinc-100 font-medium'
                  : 'text-zinc-400 hover:bg-zinc-800'
              }`}
            >
              全部部位
            </button>
            {EQUIPMENT_SLOTS.map(s => (
              <button
                key={s.value}
                type="button"
                onClick={() => setFilterSlot(s.value)}
                className={`px-2 py-0.5 text-[11px] rounded ${
                  filterSlot === s.value
                    ? 'bg-purple-900 text-purple-200 font-medium'
                    : 'text-zinc-400 hover:bg-zinc-800'
                }`}
              >
                {s.label.split(' ')[0]}
              </button>
            ))}
          </div>

          <div className="flex items-center gap-1.5 pt-1">
            <span className="text-[11px] text-zinc-500">品阶:</span>
            <select
              value={String(filterTier)}
              onChange={e => setFilterTier(e.target.value === 'all' ? 'all' : parseInt(e.target.value))}
              className="flex-1 bg-[#141b24] border border-zinc-700 text-[11px] text-zinc-300 rounded px-2 py-0.5"
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
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40">
          {filteredEquipment.map(eq => {
            const isSelected = eq.id === currentEquipment?.id;
            const slotOpt = EQUIPMENT_SLOTS.find(s => s.value === eq.slot);
            return (
              <div
                key={eq.id}
                onClick={() => setSelectedId(eq.id)}
                className={`p-2.5 cursor-pointer flex flex-col gap-1 transition-colors ${
                  isSelected
                    ? 'bg-purple-950/60 border-l-2 border-purple-400'
                    : 'hover:bg-[#141b24]'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-medium truncate ${isSelected ? 'text-purple-200' : 'text-zinc-200'}`}>
                    {eq.name}
                  </span>
                  {eq.tier && (
                    <span className="px-1.5 py-0.2 text-[10px] rounded bg-zinc-800 border border-zinc-700 text-zinc-300 font-mono">
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
        <div className="flex-1 overflow-y-auto p-6 flex flex-col gap-6 bg-[#0b0f14]">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-3 border-b border-zinc-800">
            <div>
              <h2 className="text-base font-bold text-zinc-100 flex items-center gap-2">
                <span>{currentEquipment.name}</span>
                <span className="text-xs text-zinc-500 font-mono font-normal">({currentEquipment.id})</span>
              </h2>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={handleClone}
                className="px-2.5 py-1 bg-zinc-800 hover:bg-zinc-700 border border-zinc-700 text-xs text-zinc-200 rounded"
              >
                克隆复制
              </button>
              <button
                type="button"
                onClick={handleDelete}
                className="px-2.5 py-1 bg-red-950/50 hover:bg-red-900/60 border border-red-800 text-xs text-red-300 rounded"
              >
                删除
              </button>
            </div>
          </div>

          {/* 基础信息 */}
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4 p-4 bg-[#10151c] border border-zinc-800 rounded-lg">
            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">唯一标识符 (ID)</label>
              <input
                type="text"
                value={currentEquipment.id}
                onChange={e => handleUpdate({ id: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 font-mono text-sm text-zinc-100"
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">法宝装备名称 (Name)</label>
              <input
                type="text"
                value={currentEquipment.name || ''}
                onChange={e => handleUpdate({ name: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 text-sm text-zinc-100 font-medium"
              />
            </div>

            <EnumSelect
              label="装备槽位部位 (Slot)"
              value={currentEquipment.slot}
              options={EQUIPMENT_SLOTS}
              onChange={v => handleUpdate({ slot: v || 'weapon' })}
              allowClear={false}
            />

            <div className="md:col-span-3 flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">法宝描述 (Description)</label>
              <textarea
                rows={2}
                value={currentEquipment.description || ''}
                onChange={e => handleUpdate({ description: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 text-xs text-zinc-200"
              />
            </div>

            <EnumSelect
              label="装备品阶 (Tier)"
              value={currentEquipment.tier}
              options={TIER_OPTIONS}
              onChange={v => handleUpdate({ tier: v })}
            />

            <EnumSelect
              label="道统流派偏向 (Style)"
              value={currentEquipment.style}
              options={COMBAT_STYLES}
              onChange={v => handleUpdate({ style: v })}
            />

            <EnumSelect
              label="伤害类型偏向 (Category)"
              value={currentEquipment.category}
              options={DAMAGE_CATEGORIES}
              onChange={v => handleUpdate({ category: v })}
            />

            <EnumSelect
              label="五行属性 (Element)"
              value={currentEquipment.element}
              options={ELEMENTS}
              onChange={v => handleUpdate({ element: v })}
            />

            <NumberInput
              label="攻击间隔 (AttackIntervalMilliseconds)"
              description="仅主武器生效，毫秒 (如 1000ms)"
              value={currentEquipment.attackIntervalMilliseconds}
              onChange={v => handleUpdate({ attackIntervalMilliseconds: v })}
              step={100}
              unit="ms"
            />
          </div>

          {/* 三段区间属性字典系统 */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <StatDictEditor
              label="基础属性百分比 (BaseStatRatios)"
              description="按 14 维基准属性的百分比加成 (如 0.20 为 20%)"
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
              description="暴击率/穿透率/吸血/CDR 等特殊机制加成"
              value={currentEquipment.specialMechanics}
              onChange={dict => handleUpdate({ specialMechanics: dict })}
              unit="%"
              step={0.01}
            />

            <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-4">
              <h3 className="text-xs font-semibold text-zinc-200">数值预算与强度权重 (可选)</h3>
              <div className="grid grid-cols-2 gap-3">
                <NumberInput
                  label="强度系数 (Intensity)"
                  value={currentEquipment.intensity}
                  onChange={v => handleUpdate({ intensity: v })}
                  step={0.1}
                />
                <NumberInput
                  label="数值预算 (Budget)"
                  value={currentEquipment.budget}
                  onChange={v => handleUpdate({ budget: v })}
                  step={10}
                />
              </div>
              <StatDictEditor
                label="属性分配权重 (Weights)"
                value={currentEquipment.weights}
                onChange={dict => handleUpdate({ weights: dict })}
                unit="w"
                step={0.1}
              />
            </div>
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
