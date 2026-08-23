// ==============================================================================
// 文件名称: ActionsEditor.tsx
// 主要职责: 现代扁平化 actions.json 专属编辑器，支持极简搜索、分类 Tabs 与配方卡片
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { ActionConfig } from '../../types/config';
import { ACTION_TYPES, ACTION_EXECUTION_KINDS } from '../../types/enums';
import { EnumSelect } from '../common/EnumSelect';
import { BooleanSelect } from '../common/BooleanSelect';
import { NumberInput } from '../common/NumberInput';
import { ReferenceSelect, MultiReferenceSelect } from '../common/ReferenceSelect';
import { RecipeEditor } from '../common/RecipeEditor';

export function ActionsEditor() {
  const { configs, updateConfig } = useConfigContext();
  const actions = configs?.['actions.json'] || [];

  const [selectedId, setSelectedId] = useState<string>(actions[0]?.id || '');
  const [filterType, setFilterType] = useState<string>('all');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // 过滤列表
  const filteredActions = useMemo(() => {
    return actions.filter(act => {
      if (filterType !== 'all' && act.type !== filterType) return false;
      if (searchTerm.trim()) {
        const term = searchTerm.toLowerCase();
        return (
          act.id.toLowerCase().includes(term) ||
          act.name.toLowerCase().includes(term) ||
          act.description?.toLowerCase().includes(term)
        );
      }
      return true;
    });
  }, [actions, filterType, searchTerm]);

  // 当前选中的行动
  const currentAction = useMemo(() => {
    return actions.find(a => a.id === selectedId) || filteredActions[0];
  }, [actions, selectedId, filteredActions]);

  const handleUpdate = (patch: Partial<ActionConfig>) => {
    if (!currentAction) return;
    updateConfig('actions.json', prev =>
      prev.map(a => (a.id === currentAction.id ? { ...a, ...patch } : a))
    );
  };

  const handleAdd = () => {
    const newId = `action_${Date.now().toString(36)}`;
    const newAction: ActionConfig = {
      id: newId,
      name: '新建行动',
      description: '描述该行动的修真过程与效用...',
      type: 'gathering',
      durationSeconds: 5,
      executionKind: 'timed'
    };
    updateConfig('actions.json', prev => [newAction, ...prev]);
    setSelectedId(newId);
  };

  const handleClone = () => {
    if (!currentAction) return;
    const clonedId = `${currentAction.id}_copy_${Date.now().toString(36).slice(-4)}`;
    const cloned: ActionConfig = {
      ...JSON.parse(JSON.stringify(currentAction)),
      id: clonedId,
      name: `${currentAction.name} (副本)`
    };
    updateConfig('actions.json', prev => [cloned, ...prev]);
    setSelectedId(clonedId);
  };

  const handleDelete = () => {
    if (!currentAction) return;
    if (!confirm(`确定要删除行动 [${currentAction.name}] (${currentAction.id}) 吗？`)) return;
    updateConfig('actions.json', prev => prev.filter(a => a.id !== currentAction.id));
    const remaining = actions.filter(a => a.id !== currentAction.id);
    if (remaining.length > 0) setSelectedId(remaining[0].id);
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧列表栏 */}
      <div className="w-80 border-r border-zinc-800/80 bg-zinc-950 flex flex-col shrink-0 select-none">
        {/* 顶部搜索与过滤 */}
        <div className="p-3.5 border-b border-zinc-800/80 flex flex-col gap-2.5">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">行动列表 ({filteredActions.length})</span>
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

          {/* 分类切换 Pill Tabs */}
          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => setFilterType('all')}
              className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                filterType === 'all'
                  ? 'bg-zinc-800 text-zinc-100'
                  : 'text-zinc-500 hover:text-zinc-300'
              }`}
            >
              全部
            </button>
            {ACTION_TYPES.map(t => (
              <button
                key={t.value}
                type="button"
                onClick={() => setFilterType(t.value)}
                className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                  filterType === t.value
                    ? 'bg-indigo-500/20 text-indigo-300 border border-indigo-500/30'
                    : 'text-zinc-500 hover:text-zinc-300'
                }`}
              >
                {t.label.split(' ')[0]}
              </button>
            ))}
          </div>
        </div>

        {/* 列表项 */}
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-1.5">
          {filteredActions.map(act => {
            const isSelected = act.id === currentAction?.id;
            const typeOpt = ACTION_TYPES.find(t => t.value === act.type);
            return (
              <div
                key={act.id}
                onClick={() => setSelectedId(act.id)}
                className={`p-3 rounded-xl cursor-pointer flex flex-col gap-1 transition-all duration-150 ${
                  isSelected
                    ? 'bg-zinc-900 border border-zinc-700/80 shadow-sm'
                    : 'hover:bg-zinc-900/50 border border-transparent'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-semibold truncate ${isSelected ? 'text-white' : 'text-zinc-200'}`}>
                    {act.name}
                  </span>
                  {typeOpt && (
                    <span className={`px-2 py-0.5 text-[10px] font-medium rounded-full border ${typeOpt.badgeColor}`}>
                      {typeOpt.label.split(' ')[0]}
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between text-[11px] text-zinc-500 font-mono">
                  <span className="truncate">{act.id}</span>
                  <span>{act.durationSeconds}s 结算</span>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细编辑表单 */}
      {currentAction ? (
        <div className="flex-1 overflow-y-auto p-8 flex flex-col gap-6 bg-zinc-950">
          {/* 表单顶部操作栏 */}
          <div className="flex items-center justify-between pb-4 border-b border-zinc-800/80">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-zinc-100 tracking-tight">{currentAction.name}</h2>
              <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                {currentAction.id}
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
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">唯一标识符 (ID)</label>
                <input
                  type="text"
                  value={currentAction.id}
                  onChange={e => handleUpdate({ id: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">行动名称 (Name)</label>
                <input
                  type="text"
                  value={currentAction.name || ''}
                  onChange={e => handleUpdate({ name: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                />
              </div>

              <div className="md:col-span-2 flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">行动描述 (Description)</label>
                <textarea
                  rows={2}
                  value={currentAction.description || ''}
                  onChange={e => handleUpdate({ description: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-2 text-xs text-zinc-200 outline-none"
                />
              </div>

              <EnumSelect
                label="行动类型 (Type)"
                description="决定结算逻辑与技艺分类"
                value={currentAction.type}
                options={ACTION_TYPES}
                onChange={v => handleUpdate({ type: v || 'gathering' })}
                allowClear={false}
              />

              <NumberInput
                label="行动耗时 / 结算周期 (DurationSeconds)"
                description="单位为秒 (如 5s 结算一次)"
                value={currentAction.durationSeconds}
                onChange={v => handleUpdate({ durationSeconds: v ?? 5 })}
                min={0.1}
                step={0.5}
                unit="秒"
              />

              <EnumSelect
                label="执行模式 (ExecutionKind)"
                description="周期性 / 挂机持续消耗 / 即时"
                value={currentAction.executionKind}
                options={ACTION_EXECUTION_KINDS}
                onChange={v => handleUpdate({ executionKind: v })}
              />

              <BooleanSelect
                label="从主面板目录隐藏 (HiddenFromCatalog)"
                description="内置行动或特定剧情才展示"
                value={currentAction.hiddenFromCatalog}
                onChange={v => handleUpdate({ hiddenFromCatalog: v })}
              />
            </div>
          </div>

          {/* 关联遭遇与可用节点 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">地点与战斗关联</h3>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <ReferenceSelect
                kind="encounter"
                label="关联秘境遭遇战 (CombatEncounterId)"
                description="历练战斗行动专属挂钩副本"
                value={currentAction.combatEncounterId}
                onChange={id => handleUpdate({ combatEncounterId: id })}
                placeholder="选择关联的秘境遭遇..."
              />

              <MultiReferenceSelect
                kind="node"
                label="限定生效的地图节点 (AvailableNodeIds)"
                description="仅在指定大地图节点才可执行"
                values={currentAction.availableNodeIds}
                onChange={ids => handleUpdate({ availableNodeIds: ids.length > 0 ? ids : undefined })}
                placeholder="+ 选择允许执行该行动的地图节点..."
              />
            </div>
          </div>

          {/* 配方系统 (Inputs / Outputs) */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
            <RecipeEditor
              label="消耗材料配方 (Inputs)"
              description="执行该行动每次消耗的道具及数量"
              value={currentAction.inputs}
              onChange={dict => handleUpdate({ inputs: dict })}
            />

            <RecipeEditor
              label="产出物品配方 (Outputs)"
              description="执行该行动每次获得的道具及数量"
              value={currentAction.outputs}
              onChange={dict => handleUpdate({ outputs: dict })}
            />
          </div>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-zinc-500 text-sm">
          请在左侧选择或新增一个行动
        </div>
      )}
    </div>
  );
}
