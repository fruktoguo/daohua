// ==============================================================================
// 文件名称: ItemsEditor.tsx
// 主要职责: items.json 专属编辑器，支持品阶/分类过滤、消耗品与 Buff 效果配置
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { ItemConfig, ConsumableConfig, BuffEffectConfig } from '../../types/config';
import { ITEM_KINDS, ITEM_CATEGORIES, TIER_OPTIONS, ACTION_TYPES } from '../../types/enums';
import { EnumSelect, MultiEnumSelect } from '../common/EnumSelect';
import { BooleanSelect } from '../common/BooleanSelect';
import { NumberInput } from '../common/NumberInput';
import { CombatTriggerEditor } from '../common/CombatTriggerEditor';

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

  // 当前正在编辑的 item
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
    const currentConsumable = currentItem.consumable || { usableInActionTypes: ['cultivate', 'combat'] };
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
      name: `${currentItem.name} (复制)`
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
      <div className="w-80 border-r border-zinc-800 bg-[#0e131a] flex flex-col shrink-0">
        {/* 顶部搜索与过滤 */}
        <div className="p-3 border-b border-zinc-800 flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">道具列表 ({filteredItems.length})</span>
            <button
              type="button"
              onClick={handleAdd}
              className="px-2 py-0.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-300 text-xs rounded transition-colors"
            >
              + 新增道具
            </button>
          </div>

          <input
            type="text"
            placeholder="搜索名称 / ID / 描述..."
            value={searchTerm}
            onChange={e => setSearchTerm(e.target.value)}
            className="w-full bg-[#141b24] border border-zinc-700/80 rounded px-2.5 py-1 text-xs text-zinc-100 placeholder-zinc-500 outline-none"
          />

          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => setFilterKind('all')}
              className={`px-2 py-0.5 text-[11px] rounded ${
                filterKind === 'all'
                  ? 'bg-zinc-700 text-zinc-100 font-medium'
                  : 'text-zinc-400 hover:bg-zinc-800'
              }`}
            >
              全部类型
            </button>
            {ITEM_KINDS.map(k => (
              <button
                key={k.value}
                type="button"
                onClick={() => setFilterKind(k.value)}
                className={`px-2 py-0.5 text-[11px] rounded ${
                  filterKind === k.value
                    ? 'bg-teal-900 text-teal-200 font-medium'
                    : 'text-zinc-400 hover:bg-zinc-800'
                }`}
              >
                {k.label.split(' ')[0]}
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
          {filteredItems.map(item => {
            const isSelected = item.id === currentItem?.id;
            const kindOpt = ITEM_KINDS.find(k => k.value === item.kind);
            return (
              <div
                key={item.id}
                onClick={() => setSelectedId(item.id)}
                className={`p-2.5 cursor-pointer flex flex-col gap-1 transition-colors ${
                  isSelected
                    ? 'bg-teal-950/50 border-l-2 border-teal-400'
                    : 'hover:bg-[#141b24]'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-medium truncate ${isSelected ? 'text-teal-200' : 'text-zinc-200'}`}>
                    {item.name}
                  </span>
                  {item.tier && (
                    <span className="px-1.5 py-0.2 text-[10px] rounded bg-zinc-800 border border-zinc-700 text-zinc-300 font-mono">
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
        <div className="flex-1 overflow-y-auto p-6 flex flex-col gap-6 bg-[#0b0f14]">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-3 border-b border-zinc-800">
            <div>
              <h2 className="text-base font-bold text-zinc-100 flex items-center gap-2">
                <span>{currentItem.name}</span>
                <span className="text-xs text-zinc-500 font-mono font-normal">({currentItem.id})</span>
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

          {/* 基础通用属性 */}
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4 p-4 bg-[#10151c] border border-zinc-800 rounded-lg">
            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">唯一标识符 (ID)</label>
              <input
                type="text"
                value={currentItem.id}
                onChange={e => handleUpdate({ id: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 font-mono text-sm text-zinc-100"
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">道具名称 (Name)</label>
              <input
                type="text"
                value={currentItem.name || ''}
                onChange={e => handleUpdate({ name: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 text-sm text-zinc-100 font-medium"
              />
            </div>

            <EnumSelect
              label="道具品阶 (Tier)"
              value={currentItem.tier}
              options={TIER_OPTIONS}
              onChange={v => handleUpdate({ tier: v })}
            />

            <div className="md:col-span-3 flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">道具背景与效用描述 (Description)</label>
              <textarea
                rows={2}
                value={currentItem.description || ''}
                onChange={e => handleUpdate({ description: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 text-xs text-zinc-200"
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
              label="坊市指导基准售价 (SellPrice)"
              value={currentItem.sellPrice}
              onChange={v => handleUpdate({ sellPrice: v })}
              min={0}
              unit="灵石"
            />

            <BooleanSelect
              label="是否允许坊市自由交易 (IsTradable)"
              value={currentItem.isTradable}
              onChange={v => handleUpdate({ isTradable: v })}
            />
          </div>

          {/* 消耗品专有配置模块 */}
          <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-4">
            <div className="flex items-center justify-between pb-2 border-b border-zinc-800">
              <div className="flex items-center gap-2">
                <span className="text-sm font-semibold text-zinc-200">消耗品专项服食参数 (Consumable)</span>
                {currentItem.consumable ? (
                  <span className="px-1.5 py-0.5 text-[10px] rounded bg-emerald-950 text-emerald-300 border border-emerald-800">
                    已启用消耗品属性
                  </span>
                ) : (
                  <span className="px-1.5 py-0.5 text-[10px] rounded bg-zinc-800 text-zinc-400">
                    未配置 (普通材料/装备)
                  </span>
                )}
              </div>

              {currentItem.consumable ? (
                <button
                  type="button"
                  onClick={() => handleUpdateConsumable(null)}
                  className="text-xs text-red-400 hover:text-red-300"
                >
                  移除消耗品配置
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() =>
                    handleUpdateConsumable({
                      usableInActionTypes: ['cultivate', 'combat', 'gathering', 'alchemy']
                    })
                  }
                  className="px-2.5 py-1 bg-emerald-950 hover:bg-emerald-900 border border-emerald-800 text-xs text-emerald-300 rounded"
                >
                  + 启用消耗品配置
                </button>
              )}
            </div>

            {currentItem.consumable && (
              <div className="flex flex-col gap-4">
                <MultiEnumSelect
                  label="允许服食生效的行动类型 (UsableInActionTypes)"
                  description="在哪些行动挂机时自动/手动允许使用"
                  values={currentItem.consumable.usableInActionTypes}
                  options={ACTION_TYPES}
                  onChange={vals => handleUpdateConsumable({ usableInActionTypes: vals })}
                />

                <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                  <NumberInput
                    label="持续生效时长 (DurationSeconds)"
                    description="秒数，0 为瞬时生效"
                    value={currentItem.consumable.durationSeconds}
                    onChange={v => handleUpdateConsumable({ durationSeconds: v })}
                    unit="秒"
                  />

                  <BooleanSelect
                    label="挂机持续消耗 (IsContinuous)"
                    description="银河奶牛模式挂机按秒平滑扣减"
                    value={currentItem.consumable.isContinuous}
                    onChange={v => handleUpdateConsumable({ isContinuous: v })}
                  />

                  <NumberInput
                    label="修炼修为倍率 (CultivationMultiplier)"
                    description="1.0 为无加成，1.5 为加成 50%"
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
                    label="直接恢复气血点数 (HitpointRestore)"
                    value={currentItem.consumable.hitpointRestore}
                    onChange={v => handleUpdateConsumable({ hitpointRestore: v })}
                    unit="HP"
                  />

                  <NumberInput
                    label="直接恢复真元点数 (ManapointRestore)"
                    value={currentItem.consumable.manapointRestore}
                    onChange={v => handleUpdateConsumable({ manapointRestore: v })}
                    unit="MP"
                  />

                  <NumberInput
                    label="自动吃药生命阈值 (TriggerHpRatio)"
                    description="低于此生命比例时战斗自动吃药"
                    value={currentItem.consumable.triggerHpRatio}
                    onChange={v => handleUpdateConsumable({ triggerHpRatio: v })}
                    step={0.05}
                    unit="×"
                  />

                  <BooleanSelect
                    label="战斗开局服食 (TriggerAtCombatStart)"
                    value={currentItem.consumable.triggerAtCombatStart}
                    onChange={v => handleUpdateConsumable({ triggerAtCombatStart: v })}
                  />
                </div>

                {/* 增益效果 Buff 列表 */}
                <BuffsListEditor
                  buffs={currentItem.consumable.buffs}
                  onChange={buffs => handleUpdateConsumable({ buffs })}
                />

                {/* 默认战斗触发策略 */}
                <CombatTriggerEditor
                  label="默认战斗触发策略 (DefaultCombatTriggers)"
                  description="战斗中该消耗品的自动释放触发逻辑"
                  triggers={currentItem.consumable.defaultCombatTriggers}
                  onChange={triggers => handleUpdateConsumable({ defaultCombatTriggers: triggers })}
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

// 嵌套 Buff 效果编辑器组件
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
      buffId: `buff_${Date.now().toString(36)}`,
      name: '增益效果',
      durationSeconds: 30,
      stackable: false,
      maxStacks: 1,
      qiGainMultiplier: 1.2
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
    <div className="p-3 bg-[#141b24] border border-zinc-700/60 rounded-lg flex flex-col gap-2.5">
      <div className="flex items-center justify-between">
        <label className="text-xs font-semibold text-zinc-200 flex items-center gap-2">
          <span>附加增益效果列表 (Buffs)</span>
          <span className="px-1.5 py-0.2 text-[10px] bg-zinc-800 text-zinc-400 rounded">
            {currentBuffs.length} 项 Buff
          </span>
        </label>
      </div>

      <div className="flex flex-col gap-3">
        {currentBuffs.map((buff, idx) => (
          <div key={idx} className="p-3 bg-[#0e131a] border border-zinc-800 rounded flex flex-col gap-2">
            <div className="flex items-center justify-between pb-1 border-b border-zinc-800">
              <span className="text-xs font-medium text-cyan-300">Buff #{idx + 1}: {buff.name}</span>
              <button
                type="button"
                onClick={() => handleRemove(idx)}
                className="text-zinc-500 hover:text-red-400 text-xs"
              >
                删除此 Buff
              </button>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
              <div className="flex flex-col gap-1">
                <label className="text-[11px] text-zinc-400">Buff ID</label>
                <input
                  type="text"
                  value={buff.buffId}
                  onChange={e => handleUpdate(idx, { buffId: e.target.value })}
                  className="bg-[#121820] border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-100 font-mono"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-[11px] text-zinc-400">Buff 名称</label>
                <input
                  type="text"
                  value={buff.name}
                  onChange={e => handleUpdate(idx, { name: e.target.value })}
                  className="bg-[#121820] border border-zinc-700 rounded px-2 py-1 text-xs text-zinc-100"
                />
              </div>

              <NumberInput
                label="持续时间 (DurationSeconds)"
                value={buff.durationSeconds}
                onChange={v => handleUpdate(idx, { durationSeconds: v ?? 30 })}
                unit="秒"
              />

              <NumberInput
                label="灵气获取倍率 (QiGainMultiplier)"
                value={buff.qiGainMultiplier}
                onChange={v => handleUpdate(idx, { qiGainMultiplier: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="攻击加成比例 (DamageRatio)"
                value={buff.damageRatio}
                onChange={v => handleUpdate(idx, { damageRatio: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="攻速加成比例 (AttackSpeedRatio)"
                value={buff.attackSpeedRatio}
                onChange={v => handleUpdate(idx, { attackSpeedRatio: v })}
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
        className="w-full py-1 bg-[#121820] hover:bg-zinc-800 border border-dashed border-zinc-700 text-xs text-zinc-400 hover:text-cyan-300 rounded transition-colors"
      >
        + 增加附加 Buff 效果
      </button>
    </div>
  );
}
