// ==============================================================================
// 文件名称: MapsEditor.tsx
// 主要职责: 现代扁平化 maps.json 专属编辑器，支持大地图元数据、节点与路径网络
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { WorldMapDefinition, WorldMapNodeDefinition, WorldMapEdgeDefinition } from '../../types/config';
import { NumberInput } from '../common/NumberInput';
import { MultiReferenceSelect } from '../common/ReferenceSelect';

export function MapsEditor() {
  const { configs, updateConfig } = useConfigContext();
  const maps = configs?.['maps.json'] || [];

  const [selectedMapId, setSelectedMapId] = useState<string>(maps[0]?.id || '');
  const [selectedNodeId, setSelectedNodeId] = useState<string>('');
  const [activeTab, setActiveTab] = useState<'nodes' | 'edges'>('nodes');

  // 当前正在编辑的地图
  const currentMap = useMemo(() => {
    return maps.find(m => m.id === selectedMapId) || maps[0];
  }, [maps, selectedMapId]);

  // 当前选中的节点
  const currentNode = useMemo(() => {
    if (!currentMap) return null;
    return currentMap.nodes.find(n => n.id === selectedNodeId) || currentMap.nodes[0];
  }, [currentMap, selectedNodeId]);

  const handleUpdateMap = (patch: Partial<WorldMapDefinition>) => {
    if (!currentMap) return;
    updateConfig('maps.json', prev =>
      prev.map(m => (m.id === currentMap.id ? { ...m, ...patch } : m))
    );
  };

  const handleUpdateNode = (nodeId: string, patch: Partial<WorldMapNodeDefinition>) => {
    if (!currentMap) return;
    const nextNodes = currentMap.nodes.map(n => (n.id === nodeId ? { ...n, ...patch } : n));
    handleUpdateMap({ nodes: nextNodes });
  };

  const handleAddNode = () => {
    if (!currentMap) return;
    const newId = `node_${Date.now().toString(36)}`;
    const newNode: WorldMapNodeDefinition = {
      id: newId,
      name: '新区域节点',
      description: '灵气缭绕的修行福地...',
      x: 100,
      y: 100,
      cultivationMultiplier: 1.0
    };
    handleUpdateMap({ nodes: [...currentMap.nodes, newNode] });
    setSelectedNodeId(newId);
  };

  const handleDeleteNode = (nodeId: string) => {
    if (!currentMap) return;
    if (!confirm(`确定要删除地图节点 [${nodeId}] 吗？相关的路径连线也将一并清理。`)) return;
    const nextNodes = currentMap.nodes.filter(n => n.id !== nodeId);
    const nextEdges = (currentMap.edges || []).filter(
      e => e.fromNodeId !== nodeId && e.toNodeId !== nodeId
    );
    handleUpdateMap({ nodes: nextNodes, edges: nextEdges });
    if (nextNodes.length > 0) setSelectedNodeId(nextNodes[0].id);
  };

  const handleAddEdge = () => {
    if (!currentMap || currentMap.nodes.length < 2) {
      alert('地图至少需要 2 个节点才能创建路径连线');
      return;
    }
    const nextEdge: WorldMapEdgeDefinition = {
      fromNodeId: currentMap.nodes[0].id,
      toNodeId: currentMap.nodes[1].id,
      distance: 10
    };
    handleUpdateMap({ edges: [...(currentMap.edges || []), nextEdge] });
  };

  const handleUpdateEdge = (index: number, patch: Partial<WorldMapEdgeDefinition>) => {
    if (!currentMap) return;
    const nextEdges = [...(currentMap.edges || [])];
    nextEdges[index] = { ...nextEdges[index], ...patch };
    handleUpdateMap({ edges: nextEdges });
  };

  const handleDeleteEdge = (index: number) => {
    if (!currentMap) return;
    const nextEdges = (currentMap.edges || []).filter((_, i) => i !== index);
    handleUpdateMap({ edges: nextEdges });
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧地图与节点列表栏 */}
      <div className="w-80 border-r border-zinc-800/80 bg-zinc-950 flex flex-col shrink-0 select-none">
        {/* 地图选择 */}
        <div className="p-3.5 border-b border-zinc-800/80 flex flex-col gap-2.5">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">当前地图</span>
            <span className="text-[11px] text-zinc-500 font-mono">共 {maps.length} 幅</span>
          </div>
          <select
            value={currentMap?.id || ''}
            onChange={e => setSelectedMapId(e.target.value)}
            className="w-full bg-zinc-900 border border-zinc-800 rounded-lg px-3 py-1.5 text-xs text-zinc-100 outline-none focus:border-indigo-500"
          >
            {maps.map(m => (
              <option key={m.id} value={m.id}>
                {m.name} ({m.id})
              </option>
            ))}
          </select>

          {/* 切换节点 / 连线 Tab */}
          <div className="flex p-0.5 bg-zinc-900 border border-zinc-800 rounded-lg mt-0.5">
            <button
              type="button"
              onClick={() => setActiveTab('nodes')}
              className={`flex-1 py-1 text-xs font-medium rounded-md transition-all ${
                activeTab === 'nodes' ? 'bg-zinc-800 text-zinc-100 shadow-sm' : 'text-zinc-500 hover:text-zinc-300'
              }`}
            >
              区域节点 ({currentMap?.nodes.length || 0})
            </button>
            <button
              type="button"
              onClick={() => setActiveTab('edges')}
              className={`flex-1 py-1 text-xs font-medium rounded-md transition-all ${
                activeTab === 'edges' ? 'bg-zinc-800 text-zinc-100 shadow-sm' : 'text-zinc-500 hover:text-zinc-300'
              }`}
            >
              路径网络 ({currentMap?.edges?.length || 0})
            </button>
          </div>
        </div>

        {/* 节点列表 */}
        {activeTab === 'nodes' ? (
          <div className="flex-1 flex flex-col overflow-hidden">
            <div className="p-2.5 border-b border-zinc-800/80 flex items-center justify-between">
              <span className="text-[11px] text-zinc-500 font-medium">区域列表</span>
              <button
                type="button"
                onClick={handleAddNode}
                className="px-2 py-0.5 bg-indigo-600 hover:bg-indigo-500 text-white font-medium text-xs rounded-md transition-colors"
              >
                + 新增节点
              </button>
            </div>
            <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-1.5">
              {currentMap?.nodes.map(node => {
                const isSelected = node.id === currentNode?.id;
                return (
                  <div
                    key={node.id}
                    onClick={() => setSelectedNodeId(node.id)}
                    className={`p-3 rounded-xl cursor-pointer flex flex-col gap-0.5 transition-all duration-150 ${
                      isSelected
                        ? 'bg-zinc-900 border border-zinc-700/80 shadow-sm'
                        : 'hover:bg-zinc-900/50 border border-transparent'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <span className={`text-xs font-semibold truncate ${isSelected ? 'text-white' : 'text-zinc-200'}`}>
                        {node.name}
                      </span>
                      <span className="text-[10px] text-zinc-500 font-mono">
                        ({node.x}, {node.y})
                      </span>
                    </div>
                    <span className="text-[11px] text-zinc-500 font-mono truncate">{node.id}</span>
                  </div>
                );
              })}
            </div>
          </div>
        ) : (
          <div className="flex-1 flex flex-col overflow-hidden">
            <div className="p-2.5 border-b border-zinc-800/80 flex items-center justify-between">
              <span className="text-[11px] text-zinc-500 font-medium">连线网络</span>
              <button
                type="button"
                onClick={handleAddEdge}
                className="px-2 py-0.5 bg-indigo-600 hover:bg-indigo-500 text-white font-medium text-xs rounded-md transition-colors"
              >
                + 添加路径
              </button>
            </div>
            <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-2 flex flex-col gap-2">
              {(currentMap?.edges || []).map((edge, idx) => (
                <div key={idx} className="p-3 bg-zinc-900/90 border border-zinc-800 rounded-xl flex flex-col gap-2 text-xs">
                  <div className="flex items-center justify-between">
                    <span className="text-xs font-semibold text-indigo-300">路径 #{idx + 1}</span>
                    <button
                      type="button"
                      onClick={() => handleDeleteEdge(idx)}
                      className="text-zinc-500 hover:text-rose-400 leading-none"
                    >
                      ✕
                    </button>
                  </div>
                  <div className="grid grid-cols-2 gap-2">
                    <select
                      value={edge.fromNodeId}
                      onChange={e => handleUpdateEdge(idx, { fromNodeId: e.target.value })}
                      className="bg-zinc-950 border border-zinc-800 rounded-md px-2 py-1 text-xs text-zinc-200"
                    >
                      {currentMap?.nodes.map(n => (
                        <option key={n.id} value={n.id}>起点: {n.name}</option>
                      ))}
                    </select>

                    <select
                      value={edge.toNodeId}
                      onChange={e => handleUpdateEdge(idx, { toNodeId: e.target.value })}
                      className="bg-zinc-950 border border-zinc-800 rounded-md px-2 py-1 text-xs text-zinc-200"
                    >
                      {currentMap?.nodes.map(n => (
                        <option key={n.id} value={n.id}>终点: {n.name}</option>
                      ))}
                    </select>
                  </div>
                  <div className="flex items-center gap-2 pt-1">
                    <span className="text-zinc-500 text-xs">距离 / 赶路耗时:</span>
                    <input
                      type="number"
                      min={1}
                      value={edge.distance}
                      onChange={e => handleUpdateEdge(idx, { distance: parseInt(e.target.value) || 1 })}
                      className="w-20 bg-zinc-950 border border-zinc-800 focus:border-indigo-500 rounded-md px-2.5 py-0.5 text-xs text-right font-mono text-zinc-100 outline-none"
                    />
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>

      {/* 右侧详细编辑表单 */}
      {currentMap && (
        <div className="flex-1 overflow-y-auto p-8 flex flex-col gap-6 bg-zinc-950">
          {/* 大地图全局元数据卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-zinc-100 tracking-tight">地图: {currentMap.name}</h2>
              <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                {currentMap.id}
              </span>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">地图 ID</label>
                <input
                  type="text"
                  value={currentMap.id}
                  onChange={e => handleUpdateMap({ id: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">地图名称</label>
                <input
                  type="text"
                  value={currentMap.name}
                  onChange={e => handleUpdateMap({ name: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                />
              </div>

              <NumberInput
                label="背景随机种子 (BackgroundSeed)"
                value={currentMap.backgroundSeed}
                onChange={v => handleUpdateMap({ backgroundSeed: v })}
              />

              <div className="md:col-span-3 flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">世界观生态描述</label>
                <textarea
                  rows={2}
                  value={currentMap.description || ''}
                  onChange={e => handleUpdateMap({ description: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-2 text-xs text-zinc-200 outline-none"
                />
              </div>
            </div>
          </div>

          {/* 节点详细编辑卡片 */}
          {currentNode && activeTab === 'nodes' && (
            <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-5">
              <div className="flex items-center justify-between pb-3 border-b border-zinc-800/80">
                <div className="flex items-center gap-3">
                  <h3 className="text-sm font-bold text-zinc-100 tracking-tight">节点: {currentNode.name}</h3>
                  <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                    {currentNode.id}
                  </span>
                </div>
                <button
                  type="button"
                  onClick={() => handleDeleteNode(currentNode.id)}
                  className="px-3 py-1 bg-rose-500/10 hover:bg-rose-500/20 border border-rose-500/30 text-xs font-medium text-rose-300 rounded-lg transition-colors"
                >
                  删除此节点
                </button>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-zinc-300 tracking-tight">节点 ID</label>
                  <input
                    type="text"
                    value={currentNode.id}
                    onChange={e => handleUpdateNode(currentNode.id, { id: e.target.value })}
                    className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                  />
                </div>

                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-zinc-300 tracking-tight">节点名称</label>
                  <input
                    type="text"
                    value={currentNode.name}
                    onChange={e => handleUpdateNode(currentNode.id, { name: e.target.value })}
                    className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                  />
                </div>

                <NumberInput
                  label="大地图 X 坐标"
                  value={currentNode.x}
                  onChange={v => handleUpdateNode(currentNode.id, { x: v ?? 0 })}
                  step={10}
                />

                <NumberInput
                  label="大地图 Y 坐标"
                  value={currentNode.y}
                  onChange={v => handleUpdateNode(currentNode.id, { y: v ?? 0 })}
                  step={10}
                />

                <NumberInput
                  label="灵气浓度倍率 (CultivationMultiplier)"
                  description="1.0 为基准 100%"
                  value={currentNode.cultivationMultiplier}
                  onChange={v => handleUpdateNode(currentNode.id, { cultivationMultiplier: v })}
                  step={0.1}
                  unit="×"
                />

                <div className="md:col-span-3 flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-zinc-300 tracking-tight">区域生态描述</label>
                  <input
                    type="text"
                    value={currentNode.description || ''}
                    onChange={e => handleUpdateNode(currentNode.id, { description: e.target.value })}
                    className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-200 outline-none"
                  />
                </div>
              </div>

              {/* 关联可用行动与遭遇 */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-5 pt-2 border-t border-zinc-800/80">
                <MultiReferenceSelect
                  kind="action"
                  label="该节点可用行动 (AvailableActionIds)"
                  description="在当前地图节点允许执行的采集/打坐/历练行动"
                  values={currentNode.availableActionIds}
                  onChange={ids => handleUpdateNode(currentNode.id, { availableActionIds: ids.length > 0 ? ids : undefined })}
                  placeholder="+ 添加允许执行的行动..."
                />

                <MultiReferenceSelect
                  kind="encounter"
                  label="该节点包含遭遇战 (EncounterIds)"
                  description="在当前地图节点探索或历练时可触发的秘境遭遇"
                  values={currentNode.encounterIds}
                  onChange={ids => handleUpdateNode(currentNode.id, { encounterIds: ids.length > 0 ? ids : undefined })}
                  placeholder="+ 添加关联的秘境遭遇..."
                />
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
