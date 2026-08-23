// ==============================================================================
// 文件名称: MapsEditor.tsx
// 主要职责: maps.json 专属编辑器，支持大地图元数据、区域节点多选配置与路径网络连线
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

  // 当前正在编辑的节点
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
      <div className="w-80 border-r border-zinc-800 bg-[#0e131a] flex flex-col shrink-0">
        {/* 选择地图 */}
        <div className="p-3 border-b border-zinc-800 flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">当前地图</span>
            <span className="text-[11px] text-zinc-500 font-mono">共 {maps.length} 幅大图</span>
          </div>
          <select
            value={currentMap?.id || ''}
            onChange={e => setSelectedMapId(e.target.value)}
            className="w-full bg-[#141b24] border border-zinc-700/80 rounded px-2.5 py-1 text-xs text-zinc-100 outline-none"
          >
            {maps.map(m => (
              <option key={m.id} value={m.id}>
                {m.name} ({m.id})
              </option>
            ))}
          </select>

          {/* 切换节点 / 路径连线 Tab */}
          <div className="flex border border-zinc-800 rounded p-0.5 bg-[#141b24] mt-1">
            <button
              type="button"
              onClick={() => setActiveTab('nodes')}
              className={`flex-1 py-1 text-xs font-medium rounded transition-colors ${
                activeTab === 'nodes' ? 'bg-cyan-900 text-cyan-200' : 'text-zinc-400 hover:text-zinc-200'
              }`}
            >
              区域节点 ({currentMap?.nodes.length || 0})
            </button>
            <button
              type="button"
              onClick={() => setActiveTab('edges')}
              className={`flex-1 py-1 text-xs font-medium rounded transition-colors ${
                activeTab === 'edges' ? 'bg-cyan-900 text-cyan-200' : 'text-zinc-400 hover:text-zinc-200'
              }`}
            >
              路径网络 ({currentMap?.edges?.length || 0})
            </button>
          </div>
        </div>

        {/* 节点列表 */}
        {activeTab === 'nodes' ? (
          <div className="flex-1 flex flex-col overflow-hidden">
            <div className="p-2 border-b border-zinc-800 flex items-center justify-between">
              <span className="text-[11px] text-zinc-500">节点列表</span>
              <button
                type="button"
                onClick={handleAddNode}
                className="px-2 py-0.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-300 text-xs rounded"
              >
                + 新增节点
              </button>
            </div>
            <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40">
              {currentMap?.nodes.map(node => {
                const isSelected = node.id === currentNode?.id;
                return (
                  <div
                    key={node.id}
                    onClick={() => setSelectedNodeId(node.id)}
                    className={`p-2.5 cursor-pointer flex flex-col gap-0.5 transition-colors ${
                      isSelected
                        ? 'bg-emerald-950/60 border-l-2 border-emerald-400'
                        : 'hover:bg-[#141b24]'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <span className={`text-xs font-medium truncate ${isSelected ? 'text-emerald-200' : 'text-zinc-200'}`}>
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
            <div className="p-2 border-b border-zinc-800 flex items-center justify-between">
              <span className="text-[11px] text-zinc-500">路径连线</span>
              <button
                type="button"
                onClick={handleAddEdge}
                className="px-2 py-0.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-300 text-xs rounded"
              >
                + 添加路径
              </button>
            </div>
            <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-2 flex flex-col gap-2">
              {(currentMap?.edges || []).map((edge, idx) => (
                <div key={idx} className="p-2 bg-[#141b24] border border-zinc-800 rounded flex flex-col gap-1.5 text-xs">
                  <div className="flex items-center justify-between">
                    <span className="text-[11px] text-cyan-400 font-medium">路径 #{idx + 1}</span>
                    <button
                      type="button"
                      onClick={() => handleDeleteEdge(idx)}
                      className="text-zinc-500 hover:text-red-400"
                    >
                      ×
                    </button>
                  </div>
                  <div className="grid grid-cols-2 gap-1.5">
                    <select
                      value={edge.fromNodeId}
                      onChange={e => handleUpdateEdge(idx, { fromNodeId: e.target.value })}
                      className="bg-[#0e131a] border border-zinc-700 rounded px-1.5 py-1 text-xs text-zinc-200"
                    >
                      {currentMap?.nodes.map(n => (
                        <option key={n.id} value={n.id}>起点: {n.name}</option>
                      ))}
                    </select>

                    <select
                      value={edge.toNodeId}
                      onChange={e => handleUpdateEdge(idx, { toNodeId: e.target.value })}
                      className="bg-[#0e131a] border border-zinc-700 rounded px-1.5 py-1 text-xs text-zinc-200"
                    >
                      {currentMap?.nodes.map(n => (
                        <option key={n.id} value={n.id}>终点: {n.name}</option>
                      ))}
                    </select>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className="text-zinc-500 text-[11px]">距离/耗时:</span>
                    <input
                      type="number"
                      min={1}
                      value={edge.distance}
                      onChange={e => handleUpdateEdge(idx, { distance: parseInt(e.target.value) || 1 })}
                      className="w-20 bg-[#0e131a] border border-zinc-700 rounded px-2 py-0.5 text-xs text-right font-mono text-zinc-100"
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
        <div className="flex-1 overflow-y-auto p-6 flex flex-col gap-6 bg-[#0b0f14]">
          {/* 大地图全局元数据 */}
          <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-4">
            <h2 className="text-base font-bold text-zinc-100 flex items-center gap-2">
              <span>地图: {currentMap.name}</span>
              <span className="text-xs text-zinc-500 font-mono font-normal">({currentMap.id})</span>
            </h2>

            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300">地图 ID</label>
                <input
                  type="text"
                  value={currentMap.id}
                  onChange={e => handleUpdateMap({ id: e.target.value })}
                  className="w-full bg-[#121820] border border-zinc-700/80 rounded px-3 py-1.5 font-mono text-sm text-zinc-100"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300">地图名称</label>
                <input
                  type="text"
                  value={currentMap.name}
                  onChange={e => handleUpdateMap({ name: e.target.value })}
                  className="w-full bg-[#121820] border border-zinc-700/80 rounded px-3 py-1.5 text-sm text-zinc-100"
                />
              </div>

              <NumberInput
                label="背景随机种子 (BackgroundSeed)"
                value={currentMap.backgroundSeed}
                onChange={v => handleUpdateMap({ backgroundSeed: v })}
              />

              <div className="md:col-span-3 flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300">地图世界观描述</label>
                <textarea
                  rows={2}
                  value={currentMap.description || ''}
                  onChange={e => handleUpdateMap({ description: e.target.value })}
                  className="w-full bg-[#121820] border border-zinc-700/80 rounded px-3 py-1.5 text-xs text-zinc-200"
                />
              </div>
            </div>
          </div>

          {/* 当前选中的节点详细编辑 */}
          {currentNode && activeTab === 'nodes' && (
            <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-4">
              <div className="flex items-center justify-between pb-2 border-b border-zinc-800">
                <h3 className="text-sm font-semibold text-zinc-200 flex items-center gap-2">
                  <span>节点详情: {currentNode.name}</span>
                  <span className="text-xs text-zinc-500 font-mono font-normal">({currentNode.id})</span>
                </h3>
                <button
                  type="button"
                  onClick={() => handleDeleteNode(currentNode.id)}
                  className="px-2.5 py-1 bg-red-950/50 hover:bg-red-900/60 border border-red-800 text-xs text-red-300 rounded"
                >
                  删除此节点
                </button>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-zinc-300">节点 ID</label>
                  <input
                    type="text"
                    value={currentNode.id}
                    onChange={e => handleUpdateNode(currentNode.id, { id: e.target.value })}
                    className="w-full bg-[#121820] border border-zinc-700/80 rounded px-3 py-1.5 font-mono text-sm text-zinc-100"
                  />
                </div>

                <div className="flex flex-col gap-1.5">
                  <label className="text-xs font-medium text-zinc-300">节点名称</label>
                  <input
                    type="text"
                    value={currentNode.name}
                    onChange={e => handleUpdateNode(currentNode.id, { name: e.target.value })}
                    className="w-full bg-[#121820] border border-zinc-700/80 rounded px-3 py-1.5 text-sm text-zinc-100 font-medium"
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
                  <label className="text-xs font-medium text-zinc-300">区域生态描述</label>
                  <input
                    type="text"
                    value={currentNode.description || ''}
                    onChange={e => handleUpdateNode(currentNode.id, { description: e.target.value })}
                    className="w-full bg-[#121820] border border-zinc-700/80 rounded px-3 py-1.5 text-xs text-zinc-200"
                  />
                </div>
              </div>

              {/* 关联可用行动与遭遇 */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 pt-2">
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
