// ==============================================================================
// 文件名称: ItemsEditor.tsx
// 主要职责: 现代扁平化 items.json 专属编辑器，支持品阶/分类过滤与消耗品 Buff
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { ItemConfig, ConsumableConfig, BuffEffectConfig } from '../../types/config';
import { ITEM_KINDS, ITEM_CATEGORIES, TIER_OPTIONS, ACTION_TYPES } from '../../types/enums';
import { EnumSelect, MultiEnumSelect } from '../common/EnumSelect';
import { BooleanSelect } from '../common/BooleanSelect';
import { NumberInput } from '../common/NumberInput';

export function ItemsEditor() {
  const { configs, updateConfig } = useConfigContext();
  const items = configs?.['items.json'] || [];

  const [selectedId, setSelectedId] = useState<string>(items[0]?.id || '');
  const [filterKind, setFilterKind] = useState<string>('all');
  const [filterTier, setFilterTier] = useState<number | 'all'>('all');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // 过滤列表
  const filteredItems = useMemo(() => {
    return items.filter(item => {
      if (filterKind !== 'all' && item.kind !== filterKind) return false;
      if (filterTier !== 'all' && item.tier !== filterTier) return false;
      if (searchTerm.trim()) {
        const term = searchTerm.toLowerCase();
        return (
          item.id.toLowerCase().includes(term) ||
          item.name.toLowerCase().includes(term) ||
          item.description?.toLowerCase().includes(term)
        );
      }
      return true;
    });
  }, [items, filterKind, filterTier, searchTerm]);

  // 当前正在编辑的道具
  const currentItem = useMemo(() => {
    return items.find(i => i.id === selectedId) || filteredItems[0];
  }, [items, selectedId, filteredItems]);

  const handleUpdate = (patch: Partial<ItemConfig>) => {
    if (!currentItem) return;
    updateConfig('items.json', prev =>
      prev.map(i => (i.id === currentItem.id ? { ...i, ...patch } : i))
    );
  };

  const handleUpdateConsumable = (patch: Partial<ConsumableConfig> | null) => {
    if (!currentItem) return;
    if (patch === null) {
      handleUpdate({ consumable: undefined });
      return;
    }
    const currentConsumable = currentItem.consumable || { usableInActionTypes: ['cultivation', 'combat'] };
    handleUpdate({ consumable: { ...currentConsumable, ...patch } });
  };

  const handleAdd = () => {
    const newId = `item_${Date.now().toString(36)}`;
    const newItem: ItemConfig = {
      id: newId,
      name: '新灵物',
      description: '描述天地灵物的来源与效用...',
      kind: 'material',
      category: 'herb',
      tier: 1,
      sellPrice: 10,
      isTradable: true
    };
    updateConfig('items.json', prev => [newItem, ...prev]);
    setSelectedId(newId);
  };

  const handleClone = () => {
    if (!currentItem) return;
    const clonedId = `${currentItem.id}_copy_${Date.now().toString(36).slice(-4)}`;
    const cloned: ItemConfig = {
      ...JSON.parse(JSON.stringify(currentItem)),
      id: clonedId,
      name: `${currentItem.name} (副本)`
    };
    updateConfig('items.json', prev => [cloned, ...prev]);
    setSelectedId(clonedId);
  };

  const handleDelete = () => {
    if (!currentItem) return;
    if (!confirm(`确定要删除道具 [${currentItem.name}] (${currentItem.id}) 吗？`)) return;
    updateConfig('items.json', prev => prev.filter(i => i.id !== currentItem.id));
    const remaining = items.filter(i => i.id !== currentItem.id);
    if (remaining.length > 0) setSelectedId(remaining[0].id);
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧列表栏 */}
      <div className="w-80 border-r border-zinc-800/80 bg-zinc-950 flex flex-col shrink-0 select-none">
        {/* 顶部搜索与过滤 */}
        <div className="p-3.5 border-b border-zinc-800/80 flex flex-col gap-2.5">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">道具列表 ({filteredItems.length})</span>
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
              placeholder="搜索名称 / ID / 描述..."
              value={searchTerm}
              onChange={e => setSearchTerm(e.target.value)}
              className="w-full bg-zinc-900 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-100 placeholder-zinc-500 outline-none transition-all"
            />
          </div>

          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => setFilterKind('all')}
              className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                filterKind === 'all'
                  ? 'bg-zinc-800 text-zinc-100'
                  : 'text-zinc-500 hover:text-zinc-300'
              }`}
            >
              全类型
            </button>
            {ITEM_KINDS.map(k => (
              <button
                key={k.value}
                type="button"
                onClick={() => setFilterKind(k.value)}
                className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                  filterKind === k.value
                    ? 'bg-emerald-500/20 text-emerald-300 border border-emerald-500/30'
                    : 'text-zinc-500 hover:text-zinc-300'
                }`}
              >
                {k.label.split(' ')[0]}
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
          {filteredItems.map(item => {
            const isSelected = item.id === currentItem?.id;
            const kindOpt = ITEM_KINDS.find(k => k.value === item.kind);
            return (
              <div
                key={item.id}
                onClick={() => setSelectedId(item.id)}
                className={`p-3 rounded-xl cursor-pointer flex flex-col gap-1 transition-all duration-150 ${
                  isSelected
                    ? 'bg-zinc-900 border border-zinc-700/80 shadow-sm'
                    : 'hover:bg-zinc-900/50 border border-transparent'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-semibold truncate ${isSelected ? 'text-white' : 'text-zinc-200'}`}>
                    {item.name}
                  </span>
                  {item.tier && (
                    <span className="px-2 py-0.2 text-[10px] rounded-full bg-zinc-800 text-zinc-300 font-mono font-medium border border-zinc-700/50">
                      T{item.tier}
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between text-[11px] text-zinc-500 font-mono">
                  <span className="truncate">{item.id}</span>
                  {kindOpt && <span>{kindOpt.label.split(' ')[0]}</span>}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细编辑表单 */}
      {currentItem ? (
        <div className="flex-1 overflow-y-auto p-8 flex flex-col gap-6 bg-zinc-950">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-4 border-b border-zinc-800/80">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-zinc-100 tracking-tight">{currentItem.name}</h2>
              <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                {currentItem.id}
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

          {/* 基础通用属性卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">基础属性</h3>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">唯一标识符 (ID)</label>
                <input
                  type="text"
                  value={currentItem.id}
                  onChange={e => handleUpdate({ id: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">道具名称 (Name)</label>
                <input
                  type="text"
                  value={currentItem.name || ''}
                  onChange={e => handleUpdate({ name: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                />
              </div>

              <EnumSelect
                label="道具品阶 (Tier)"
                value={currentItem.tier}
                options={TIER_OPTIONS}
                onChange={v => handleUpdate({ tier: v })}
              />

              <div className="md:col-span-3 flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">背景描述 (Description)</label>
                <textarea
                  rows={2}
                  value={currentItem.description || ''}
                  onChange={e => handleUpdate({ description: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-2 text-xs text-zinc-200 outline-none"
                />
              </div>

              <EnumSelect
                label="道具大类 (Kind)"
                value={currentItem.kind}
                options={ITEM_KINDS}
                onChange={v => handleUpdate({ kind: v || 'material' })}
                allowClear={false}
              />

              <EnumSelect
                label="细分子类 (Category)"
                value={currentItem.category}
                options={ITEM_CATEGORIES}
                onChange={v => handleUpdate({ category: v })}
              />

              <NumberInput
                label="坊市指导售价 (SellPrice)"
                value={currentItem.sellPrice}
                onChange={v => handleUpdate({ sellPrice: v })}
                min={0}
                unit="灵石"
              />

              <div className="md:col-span-3">
                <BooleanSelect
                  label="坊市自由交易 (IsTradable)"
                  value={currentItem.isTradable}
                  onChange={v => handleUpdate({ isTradable: v })}
                />
              </div>
            </div>
          </div>

          {/* 消耗品专有配置卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-5">
            <div className="flex items-center justify-between pb-3 border-b border-zinc-800/80">
              <div className="flex items-center gap-2.5">
                <span className="text-xs font-semibold text-zinc-200 uppercase tracking-wider">消耗品专项服食参数</span>
                {currentItem.consumable ? (
                  <span className="px-2 py-0.5 text-[10px] font-medium rounded-full bg-emerald-500/10 text-emerald-300 border border-emerald-500/20">
                    已启用
                  </span>
                ) : (
                  <span className="px-2 py-0.5 text-[10px] font-medium rounded-full bg-zinc-800 text-zinc-400">
                    未配置 (普通原料/装备)
                  </span>
                )}
              </div>

              {currentItem.consumable ? (
                <button
                  type="button"
                  onClick={() => handleUpdateConsumable(null)}
                  className="text-xs text-rose-400 hover:text-rose-300 font-medium"
                >
                  移除消耗品属性
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() =>
                    handleUpdateConsumable({
                      usableInActionTypes: ['cultivation', 'exploration', 'combat', 'alchemy']
                    })
                  }
                  className="px-3 py-1 bg-emerald-600 hover:bg-emerald-500 text-white font-medium text-xs rounded-lg transition-colors shadow-sm"
                >
                  + 启用消耗品配置
                </button>
              )}
            </div>

            {currentItem.consumable && (
              <div className="flex flex-col gap-5">
                <MultiEnumSelect
                  label="允许服食生效的行动 (UsableInActionTypes)"
                  description="在哪些行动挂机时自动/手动允许使用"
                  values={currentItem.consumable.usableInActionTypes}
                  options={ACTION_TYPES}
                  onChange={vals => handleUpdateConsumable({ usableInActionTypes: vals })}
                />

                <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                  <NumberInput
                    label="持续时长 (DurationSeconds)"
                    description="秒数，0 为瞬时生效"
                    value={currentItem.consumable.durationSeconds}
                    onChange={v => handleUpdateConsumable({ durationSeconds: v })}
                    unit="秒"
                  />

                  <BooleanSelect
                    label="挂机持续消耗 (IsContinuous)"
                    description="按秒平滑扣减"
                    value={currentItem.consumable.isContinuous}
                    onChange={v => handleUpdateConsumable({ isContinuous: v })}
                  />

                  <NumberInput
                    label="修炼修为倍率 (CultivationMultiplier)"
                    description="1.5 为加成 50%"
                    value={currentItem.consumable.cultivationMultiplier}
                    onChange={v => handleUpdateConsumable({ cultivationMultiplier: v })}
                    step={0.05}
                    unit="×"
                  />

                  <NumberInput
                    label="采集产出倍率 (GatheringMultiplier)"
                    value={currentItem.consumable.gatheringMultiplier}
                    onChange={v => handleUpdateConsumable({ gatheringMultiplier: v })}
                    step={0.05}
                    unit="×"
                  />

                  <NumberInput
                    label="炼丹产出倍率 (AlchemyMultiplier)"
                    value={currentItem.consumable.alchemyMultiplier}
                    onChange={v => handleUpdateConsumable({ alchemyMultiplier: v })}
                    step={0.05}
                    unit="×"
                  />

                  <NumberInput
                    label="恢复气血点数 (HitpointRestore)"
                    description="即时消耗品生效"
                    value={currentItem.consumable.hitpointRestore}
                    onChange={v => handleUpdateConsumable({ hitpointRestore: v })}
                    unit="HP"
                  />

                  <NumberInput
                    label="恢复真元点数 (ManapointRestore)"
                    description="即时消耗品生效"
                    value={currentItem.consumable.manapointRestore}
                    onChange={v => handleUpdateConsumable({ manapointRestore: v })}
                    unit="MP"
                  />

                  <NumberInput
                    label="服食冷却 (CooldownSeconds)"
                    description="自动使用即时消耗品必须为正冷却"
                    value={currentItem.consumable.cooldownSeconds}
                    onChange={v => handleUpdateConsumable({ cooldownSeconds: v })}
                    min={0}
                    unit="秒"
                  />
                </div>

                {/* 增益效果 Buff 列表 */}
                <BuffsListEditor
                  buffs={currentItem.consumable.buffs}
                  onChange={buffs => handleUpdateConsumable({ buffs })}
                />
              </div>
            )}
          </div>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-zinc-500 text-sm">
          请在左侧选择或新增一个道具
        </div>
      )}
    </div>
  );
}

// 嵌套 Buff 效果编辑器
function BuffsListEditor({
  buffs = [],
  onChange
}: {
  buffs?: BuffEffectConfig[];
  onChange: (buffs: BuffEffectConfig[] | undefined) => void;
}) {
  const currentBuffs = buffs || [];

  const handleAdd = () => {
    const next: BuffEffectConfig = {
      buffId: `buff.new-${Date.now().toString(36)}`,
      name: '增益光环',
      durationSeconds: 30
    };
    onChange([...currentBuffs, next]);
  };

  const handleUpdate = (index: number, patch: Partial<BuffEffectConfig>) => {
    const next = [...currentBuffs];
    next[index] = { ...next[index], ...patch };
    onChange(next);
  };

  const handleRemove = (index: number) => {
    const next = currentBuffs.filter((_, i) => i !== index);
    onChange(next.length > 0 ? next : undefined);
  };

  return (
    <div className="p-4 bg-zinc-900/60 border border-zinc-800/80 rounded-xl flex flex-col gap-3">
      <div className="flex items-center justify-between">
        <label className="text-xs font-semibold text-zinc-200 tracking-tight flex items-center gap-2">
          <span>附加增益 Buff 效果列表</span>
          <span className="px-2 py-0.5 text-[10px] font-mono font-medium rounded-full bg-zinc-800 text-zinc-400">
            {currentBuffs.length} 项
          </span>
        </label>
      </div>

      <div className="flex flex-col gap-3">
        {currentBuffs.map((buff, idx) => (
          <div key={idx} className="p-3.5 bg-zinc-900/90 border border-zinc-800 rounded-lg flex flex-col gap-3 hover:border-zinc-700 transition-colors">
            <div className="flex items-center justify-between pb-2 border-b border-zinc-800/80">
              <span className="text-xs font-semibold text-indigo-300">Buff #{idx + 1}: {buff.name}</span>
              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="text-zinc-500 hover:text-rose-400 text-sm leading-none"
              >
                ✕
              </button>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
              <div className="flex flex-col gap-1">
                <label className="text-[11px] text-zinc-400 font-medium">Buff ID</label>
                <input
                  type="text"
                  value={buff.buffId}
                  onChange={e => handleUpdate(idx, { buffId: e.target.value })}
                  className="bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-1 text-xs text-zinc-100 font-mono outline-none"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-[11px] text-zinc-400 font-medium">Buff 名称</label>
                <input
                  type="text"
                  value={buff.name}
                  onChange={e => handleUpdate(idx, { name: e.target.value })}
                  className="bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-1 text-xs text-zinc-100 outline-none"
                />
              </div>

              <NumberInput
                label="持续时间 (DurationSeconds)"
                value={buff.durationSeconds}
                onChange={v => handleUpdate(idx, { durationSeconds: v ?? 30 })}
                unit="秒"
              />

              <NumberInput
                label="气血回复 (HpRegen)"
                value={buff.hpRegen}
                onChange={v => handleUpdate(idx, { hpRegen: v })}
                step={0.5}
              />

              <NumberInput
                label="灵力回复 (MpRegen)"
                value={buff.mpRegen}
                onChange={v => handleUpdate(idx, { mpRegen: v })}
                step={0.5}
              />

              <NumberInput
                label="攻速加成 (AttackSpeedRatio)"
                value={buff.attackSpeedRatio}
                onChange={v => handleUpdate(idx, { attackSpeedRatio: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="免伤比例 (DamageAbsorptionRatio)"
                value={buff.damageAbsorptionRatio}
                onChange={v => handleUpdate(idx, { damageAbsorptionRatio: v })}
                step={0.05}
                unit="×"
              />
            </div>
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={handleAdd}
        className="w-full py-2 bg-zinc-900/40 hover:bg-zinc-800/80 border border-dashed border-zinc-700 text-xs font-medium text-zinc-400 hover:text-indigo-300 rounded-lg transition-all duration-150 flex items-center justify-center gap-1.5"
      >
        <span>+ 增加附加 Buff 效果</span>
      </button>
    </div>
  );
}
