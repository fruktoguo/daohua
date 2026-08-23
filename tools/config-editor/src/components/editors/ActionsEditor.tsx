// ==============================================================================
// 文件名称: ActionsEditor.tsx
// 主要职责: actions.json 专属编辑器，支持分类筛选、配方编辑、可用节点多选与遭遇关联
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

  // 当前正在编辑的 action
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
      description: '行动描述...',
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
      name: `${currentAction.name} (复制)`
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
      <div className="w-80 border-r border-zinc-800 bg-[#0e131a] flex flex-col shrink-0">
        {/* 顶部搜索与过滤 */}
        <div className="p-3 border-b border-zinc-800 flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">行动列表 ({filteredActions.length})</span>
            <button
              type="button"
              onClick={handleAdd}
              className="px-2 py-0.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-300 text-xs rounded transition-colors"
            >
              + 新增行动
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
              onClick={() => setFilterType('all')}
              className={`px-2 py-0.5 text-[11px] rounded ${
                filterType === 'all'
                  ? 'bg-zinc-700 text-zinc-100 font-medium'
                  : 'text-zinc-400 hover:bg-zinc-800'
              }`}
            >
              全部
            </button>
            {ACTION_TYPES.map(t => (
              <button
                key={t.value}
                type="button"
                onClick={() => setFilterType(t.value)}
                className={`px-2 py-0.5 text-[11px] rounded ${
                  filterType === t.value
                    ? 'bg-cyan-900 text-cyan-200 font-medium'
                    : 'text-zinc-400 hover:bg-zinc-800'
                }`}
              >
                {t.label.split(' ')[0]}
              </button>
            ))}
          </div>
        </div>

        {/* 列表项 */}
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40">
          {filteredActions.map(act => {
            const isSelected = act.id === currentAction?.id;
            const typeOpt = ACTION_TYPES.find(t => t.value === act.type);
            return (
              <div
                key={act.id}
                onClick={() => setSelectedId(act.id)}
                className={`p-2.5 cursor-pointer flex flex-col gap-1 transition-colors ${
                  isSelected
                    ? 'bg-cyan-950/60 border-l-2 border-cyan-400'
                    : 'hover:bg-[#141b24]'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-medium truncate ${isSelected ? 'text-cyan-200' : 'text-zinc-200'}`}>
                    {act.name}
                  </span>
                  {typeOpt && (
                    <span className={`px-1.5 py-0.2 text-[10px] rounded border ${typeOpt.badgeColor}`}>
                      {typeOpt.label.split(' ')[0]}
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between text-[11px] text-zinc-500 font-mono">
                  <span className="truncate">{act.id}</span>
                  <span>{act.durationSeconds}s</span>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细编辑表单 */}
      {currentAction ? (
        <div className="flex-1 overflow-y-auto p-6 flex flex-col gap-6 bg-[#0b0f14]">
          {/* 表单顶部操作栏 */}
          <div className="flex items-center justify-between pb-3 border-b border-zinc-800">
            <div>
              <h2 className="text-base font-bold text-zinc-100 flex items-center gap-2">
                <span>{currentAction.name}</span>
                <span className="text-xs text-zinc-500 font-mono font-normal">({currentAction.id})</span>
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
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4 p-4 bg-[#10151c] border border-zinc-800 rounded-lg">
            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">唯一标识符 (ID)</label>
              <input
                type="text"
                value={currentAction.id}
                onChange={e => handleUpdate({ id: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 font-mono text-sm text-zinc-100"
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">行动名称 (Name)</label>
              <input
                type="text"
                value={currentAction.name || ''}
                onChange={e => handleUpdate({ name: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 text-sm text-zinc-100 font-medium"
              />
            </div>

            <div className="md:col-span-2 flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">行动描述 (Description)</label>
              <textarea
                rows={2}
                value={currentAction.description || ''}
                onChange={e => handleUpdate({ description: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 text-xs text-zinc-200"
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

          {/* 关联遭遇与可用节点 */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4 p-4 bg-[#10151c] border border-zinc-800 rounded-lg">
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

          {/* 配方系统 (Inputs / Outputs) */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
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
