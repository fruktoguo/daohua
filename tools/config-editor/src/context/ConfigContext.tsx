// ==============================================================================
// 文件名称: ConfigContext.tsx
// 主要职责: 配置编辑器全局状态管理、跨表索引构建、未保存变更追踪与统一保存机制
// ==============================================================================

import React, { createContext, useContext, useState, useEffect, useMemo, useCallback } from 'react';
import type {
  AllGameConfigFiles,
  ConfigFileName,
  ItemConfig,
  AbilityConfig,
  CombatantConfig,
  CombatEncounterConfig,
  ActionConfig,
  WorldMapNodeDefinition
} from '../types/config';
import {
  fetchAllConfigs,
  saveSingleConfigFile,
  saveAllConfigFiles,
  validateDomainConfigs,
  createConfigBackup,
  type ApiResponse
} from '../api/client';

export interface GlobalLookups {
  items: Map<string, ItemConfig>;
  itemList: { id: string; name: string; kind?: string; tier?: number; category?: string }[];
  abilities: Map<string, AbilityConfig>;
  abilityList: { id: string; name: string; style?: string; category?: string; element?: string }[];
  monsters: Map<string, CombatantConfig>;
  monsterList: { id: string; name: string; rank?: string; level?: number }[];
  encounters: Map<string, CombatEncounterConfig>;
  encounterList: { id: string; name: string; bossCount?: number }[];
  actions: Map<string, ActionConfig>;
  actionList: { id: string; name: string; type?: string }[];
  nodes: Map<string, WorldMapNodeDefinition & { mapId: string; mapName: string }>;
  nodeList: { id: string; name: string; mapId: string; mapName: string; x: number; y: number }[];
}

export interface ValidationErrorItem {
  id: string;
  sourceFile: ConfigFileName;
  itemId?: string;
  field: string;
  message: string;
  severity: 'error' | 'warning';
}

interface ConfigContextValue {
  loading: boolean;
  error: string | null;
  configs: AllGameConfigFiles | null;
  dirtyFiles: Set<ConfigFileName>;
  activeFile: ConfigFileName;
  setActiveFile: (file: ConfigFileName) => void;
  lookups: GlobalLookups;
  validationErrors: ValidationErrorItem[];
  backendValidationResult: ApiResponse | null;
  validating: boolean;

  // Actions
  updateConfig: <K extends ConfigFileName>(filename: K, updater: (prev: AllGameConfigFiles[K]) => AllGameConfigFiles[K]) => void;
  saveCurrentFile: () => Promise<boolean>;
  saveAllFiles: () => Promise<boolean>;
  resetCurrentFile: () => void;
  resetAllFiles: () => void;
  reloadAllConfigs: () => Promise<void>;
  runFullValidation: () => Promise<void>;
  triggerBackup: () => Promise<boolean>;
}

const ConfigContext = createContext<ConfigContextValue | null>(null);

export function ConfigProvider({ children }: { children: React.ReactNode }) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [originalConfigs, setOriginalConfigs] = useState<AllGameConfigFiles | null>(null);
  const [configs, setConfigs] = useState<AllGameConfigFiles | null>(null);
  const [dirtyFiles, setDirtyFiles] = useState<Set<ConfigFileName>>(new Set());
  const [activeFile, setActiveFile] = useState<ConfigFileName>('items.json');
  const [validating, setValidating] = useState(false);
  const [backendValidationResult, setBackendValidationResult] = useState<ApiResponse | null>(null);

  // 加载数据
  const loadData = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await fetchAllConfigs();
      setOriginalConfigs(JSON.parse(JSON.stringify(data)));
      setConfigs(data);
      setDirtyFiles(new Set());
    } catch (err: any) {
      setError(err.message || '加载配置失败');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  // 构建全局跨表索引字典
  const lookups = useMemo<GlobalLookups>(() => {
    const items = new Map<string, ItemConfig>();
    const itemList: GlobalLookups['itemList'] = [];
    if (configs?.['items.json']) {
      for (const item of configs['items.json']) {
        if (item.id) {
          items.set(item.id, item);
          itemList.push({
            id: item.id,
            name: item.name || item.id,
            kind: item.kind,
            tier: item.tier,
            category: item.category
          });
        }
      }
    }

    const abilities = new Map<string, AbilityConfig>();
    const abilityList: GlobalLookups['abilityList'] = [];
    if (configs?.['abilities.json']) {
      for (const ab of configs['abilities.json']) {
        if (ab.id) {
          abilities.set(ab.id, ab);
          abilityList.push({
            id: ab.id,
            name: ab.name || ab.id,
            style: ab.style,
            category: ab.category,
            element: ab.element
          });
        }
      }
    }

    const monsters = new Map<string, CombatantConfig>();
    const monsterList: GlobalLookups['monsterList'] = [];
    if (configs?.['monsters.json']) {
      for (const m of configs['monsters.json']) {
        if (m.id) {
          monsters.set(m.id, m);
          monsterList.push({
            id: m.id,
            name: m.name || m.id,
            rank: m.rank,
            level: m.level
          });
        }
      }
    }

    const encounters = new Map<string, CombatEncounterConfig>();
    const encounterList: GlobalLookups['encounterList'] = [];
    if (configs?.['encounters.json']) {
      for (const enc of configs['encounters.json']) {
        if (enc.id) {
          encounters.set(enc.id, enc);
          encounterList.push({
            id: enc.id,
            name: enc.name || enc.id,
            bossCount: enc.bossEnemyIds?.length || 0
          });
        }
      }
    }

    const actions = new Map<string, ActionConfig>();
    const actionList: GlobalLookups['actionList'] = [];
    if (configs?.['actions.json']) {
      for (const act of configs['actions.json']) {
        if (act.id) {
          actions.set(act.id, act);
          actionList.push({
            id: act.id,
            name: act.name || act.id,
            type: act.type
          });
        }
      }
    }

    const nodes = new Map<string, WorldMapNodeDefinition & { mapId: string; mapName: string }>();
    const nodeList: GlobalLookups['nodeList'] = [];
    if (configs?.['maps.json']) {
      for (const map of configs['maps.json']) {
        for (const node of map.nodes || []) {
          if (node.id) {
            const enriched = { ...node, mapId: map.id, mapName: map.name };
            nodes.set(node.id, enriched);
            nodeList.push({
              id: node.id,
              name: node.name || node.id,
              mapId: map.id,
              mapName: map.name,
              x: node.x,
              y: node.y
            });
          }
        }
      }
    }

    return {
      items,
      itemList,
      abilities,
      abilityList,
      monsters,
      monsterList,
      encounters,
      encounterList,
      actions,
      actionList,
      nodes,
      nodeList
    };
  }, [configs]);

  // 前端实时跨表关联完整性校验
  const validationErrors = useMemo<ValidationErrorItem[]>(() => {
    if (!configs) return [];
    const errors: ValidationErrorItem[] = [];

    // 1. 检查 Actions
    if (configs['actions.json']) {
      for (const act of configs['actions.json']) {
        if (act.combatEncounterId && !lookups.encounters.has(act.combatEncounterId)) {
          errors.push({
            id: `act-${act.id}-enc`,
            sourceFile: 'actions.json',
            itemId: act.id,
            field: 'combatEncounterId',
            message: `行动 [${act.name}] 引用的遭遇战 [${act.combatEncounterId}] 不存在于 encounters.json`,
            severity: 'error'
          });
        }
        if (act.availableNodeIds) {
          for (const nid of act.availableNodeIds) {
            if (!lookups.nodes.has(nid)) {
              errors.push({
                id: `act-${act.id}-node-${nid}`,
                sourceFile: 'actions.json',
                itemId: act.id,
                field: 'availableNodeIds',
                message: `行动 [${act.name}] 引用的地图节点 [${nid}] 不存在于 maps.json`,
                severity: 'warning'
              });
            }
          }
        }
        if (act.inputs) {
          for (const iid of Object.keys(act.inputs)) {
            if (!lookups.items.has(iid)) {
              errors.push({
                id: `act-${act.id}-input-${iid}`,
                sourceFile: 'actions.json',
                itemId: act.id,
                field: 'inputs',
                message: `行动 [${act.name}] 消耗的材料 [${iid}] 不存在于 items.json`,
                severity: 'error'
              });
            }
          }
        }
        if (act.yields) {
          for (const y of act.yields) {
            if (y.kind === 'item' && y.id && !lookups.items.has(y.id)) {
              errors.push({
                id: `act-${act.id}-yield-${y.id}`,
                sourceFile: 'actions.json',
                itemId: act.id,
                field: 'yields',
                message: `行动 [${act.name}] 产出的物品 [${y.id}] 不存在于 items.json`,
                severity: 'error'
              });
            }
          }
        }
      }
    }

    // 2. 检查 Monsters
    if (configs['monsters.json']) {
      for (const m of configs['monsters.json']) {
        if (m.skills) {
          for (const sk of m.skills) {
            if (sk.abilityId && !lookups.abilities.has(sk.abilityId)) {
              errors.push({
                id: `mon-${m.id}-sk-${sk.abilityId}`,
                sourceFile: 'monsters.json',
                itemId: m.id,
                field: 'skills',
                message: `怪物 [${m.name}] 装配的技能 [${sk.abilityId}] 不存在于 abilities.json`,
                severity: 'error'
              });
            }
          }
        }
        if (m.dropTable) {
          for (const drop of m.dropTable) {
            if (drop.itemId && !lookups.items.has(drop.itemId)) {
              errors.push({
                id: `mon-${m.id}-drop-${drop.itemId}`,
                sourceFile: 'monsters.json',
                itemId: m.id,
                field: 'dropTable',
                message: `怪物 [${m.name}] 掉落表引用的道具 [${drop.itemId}] 不存在于 items.json`,
                severity: 'error'
              });
            }
          }
        }

      }
    }

    // 3. 检查 Encounters
    if (configs['encounters.json']) {
      for (const enc of configs['encounters.json']) {
        if (enc.randomSpawns) {
          for (const sp of enc.randomSpawns) {
            if (sp.enemyId && !lookups.monsters.has(sp.enemyId)) {
              errors.push({
                id: `enc-${enc.id}-sp-${sp.enemyId}`,
                sourceFile: 'encounters.json',
                itemId: enc.id,
                field: 'randomSpawns',
                message: `遭遇 [${enc.name}] 刷怪引用的怪物 [${sp.enemyId}] 不存在于 monsters.json`,
                severity: 'error'
              });
            }
          }
        }
        if (enc.bossEnemyIds) {
          for (const bid of enc.bossEnemyIds) {
            if (!lookups.monsters.has(bid)) {
              errors.push({
                id: `enc-${enc.id}-boss-${bid}`,
                sourceFile: 'encounters.json',
                itemId: enc.id,
                field: 'bossEnemyIds',
                message: `遭遇 [${enc.name}] 首领怪物 [${bid}] 不存在于 monsters.json`,
                severity: 'error'
              });
            }
          }
        }
        if (enc.eliteEnemyIds) {
          for (const eid of enc.eliteEnemyIds) {
            if (!lookups.monsters.has(eid)) {
              errors.push({
                id: `enc-${enc.id}-elite-${eid}`,
                sourceFile: 'encounters.json',
                itemId: enc.id,
                field: 'eliteEnemyIds',
                message: `遭遇 [${enc.name}] 精英怪物 [${eid}] 不存在于 monsters.json`,
                severity: 'error'
              });
            }
          }
        }

      }
    }

    // 4. 检查 Maps
    if (configs['maps.json']) {
      for (const map of configs['maps.json']) {
        const localNodeIds = new Set(map.nodes.map(n => n.id));
        for (const node of map.nodes) {
          if (node.availableActionIds) {
            for (const aid of node.availableActionIds) {
              if (!lookups.actions.has(aid)) {
                errors.push({
                  id: `map-${map.id}-node-${node.id}-act-${aid}`,
                  sourceFile: 'maps.json',
                  itemId: map.id,
                  field: 'availableActionIds',
                  message: `地图 [${map.name}] 节点 [${node.name}] 可用行动 [${aid}] 不存在于 actions.json`,
                  severity: 'warning'
                });
              }
            }
          }
          if (node.encounterIds) {
            for (const eid of node.encounterIds) {
              if (!lookups.encounters.has(eid)) {
                errors.push({
                  id: `map-${map.id}-node-${node.id}-enc-${eid}`,
                  sourceFile: 'maps.json',
                  itemId: map.id,
                  field: 'encounterIds',
                  message: `地图 [${map.name}] 节点 [${node.name}] 关联遭遇 [${eid}] 不存在于 encounters.json`,
                  severity: 'error'
                });
              }
            }
          }
        }
        for (const edge of map.edges || []) {
          if (!localNodeIds.has(edge.fromNodeId)) {
            errors.push({
              id: `map-${map.id}-edge-from-${edge.fromNodeId}`,
              sourceFile: 'maps.json',
              itemId: map.id,
              field: 'edges',
              message: `地图 [${map.name}] 路径起点 [${edge.fromNodeId}] 不存在于本图节点列表中`,
              severity: 'error'
            });
          }
          if (!localNodeIds.has(edge.toNodeId)) {
            errors.push({
              id: `map-${map.id}-edge-to-${edge.toNodeId}`,
              sourceFile: 'maps.json',
              itemId: map.id,
              field: 'edges',
              message: `地图 [${map.name}] 路径终点 [${edge.toNodeId}] 不存在于本图节点列表中`,
              severity: 'error'
            });
          }
        }
      }
    }

    return errors;
  }, [configs, lookups]);

  // 更新配置并标记 dirty
  const updateConfig = useCallback(<K extends ConfigFileName>(
    filename: K,
    updater: (prev: AllGameConfigFiles[K]) => AllGameConfigFiles[K]
  ) => {
    setConfigs(prev => {
      if (!prev) return prev;
      const updated = updater(prev[filename]);
      return {
        ...prev,
        [filename]: updated
      };
    });
    setDirtyFiles(prev => {
      const next = new Set(prev);
      next.add(filename);
      return next;
    });
  }, []);

  // 保存当前单文件
  const saveCurrentFile = useCallback(async (): Promise<boolean> => {
    if (!configs || !configs[activeFile]) return false;
    try {
      const res = await saveSingleConfigFile(activeFile, configs[activeFile]);
      if (res.ok) {
        setDirtyFiles(prev => {
          const next = new Set(prev);
          next.delete(activeFile);
          return next;
        });
        if (originalConfigs) {
          setOriginalConfigs({
            ...originalConfigs,
            [activeFile]: JSON.parse(JSON.stringify(configs[activeFile]))
          });
        }
        return true;
      }
      alert('保存失败: ' + (res.error || '未知原因'));
      return false;
    } catch (e: any) {
      alert('保存发生异常: ' + e.message);
      return false;
    }
  }, [activeFile, configs, originalConfigs]);

  // 保存全部修改的文件
  const saveAllFiles = useCallback(async (): Promise<boolean> => {
    if (!configs) return false;
    const modified: Partial<AllGameConfigFiles> = {};
    for (const file of dirtyFiles) {
      modified[file] = configs[file] as any;
    }
    if (Object.keys(modified).length === 0) return true;

    try {
      const res = await saveAllConfigFiles(modified);
      if (res.ok) {
        setDirtyFiles(new Set());
        setOriginalConfigs(JSON.parse(JSON.stringify(configs)));
        return true;
      }
      alert('批量保存失败: ' + (res.error || '未知原因'));
      return false;
    } catch (e: any) {
      alert('批量保存发生异常: ' + e.message);
      return false;
    }
  }, [configs, dirtyFiles]);

  // 放弃单文件修改
  const resetCurrentFile = useCallback(() => {
    if (!originalConfigs || !originalConfigs[activeFile]) return;
    setConfigs(prev => {
      if (!prev) return prev;
      return {
        ...prev,
        [activeFile]: JSON.parse(JSON.stringify(originalConfigs[activeFile]))
      };
    });
    setDirtyFiles(prev => {
      const next = new Set(prev);
      next.delete(activeFile);
      return next;
    });
  }, [activeFile, originalConfigs]);

  // 放弃全部修改
  const resetAllFiles = useCallback(() => {
    if (!originalConfigs) return;
    setConfigs(JSON.parse(JSON.stringify(originalConfigs)));
    setDirtyFiles(new Set());
  }, [originalConfigs]);

  // 触发后端领域校验
  const runFullValidation = useCallback(async () => {
    try {
      setValidating(true);
      const res = await validateDomainConfigs();
      setBackendValidationResult(res);
    } catch (e: any) {
      setBackendValidationResult({ ok: false, error: e.message });
    } finally {
      setValidating(false);
    }
  }, []);

  // 触发快照备份
  const triggerBackup = useCallback(async (): Promise<boolean> => {
    try {
      const res = await createConfigBackup();
      if (res.ok) {
        alert(res.message || '备份成功');
        return true;
      }
      alert('备份失败: ' + res.error);
      return false;
    } catch (e: any) {
      alert('备份异常: ' + e.message);
      return false;
    }
  }, []);

  return (
    <ConfigContext.Provider
      value={{
        loading,
        error,
        configs,
        dirtyFiles,
        activeFile,
        setActiveFile,
        lookups,
        validationErrors,
        backendValidationResult,
        validating,
        updateConfig,
        saveCurrentFile,
        saveAllFiles,
        resetCurrentFile,
        resetAllFiles,
        reloadAllConfigs: loadData,
        runFullValidation,
        triggerBackup
      }}
    >
      {children}
    </ConfigContext.Provider>
  );
}

export function useConfigContext() {
  const context = useContext(ConfigContext);
  if (!context) {
    throw new Error('useConfigContext must be used within a ConfigProvider');
  }
  return context;
}
