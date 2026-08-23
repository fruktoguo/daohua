// ==============================================================================
// 文件名称: ManifestEditor.tsx
// 主要职责: manifest.json 专属编辑器与全量数据概览看板
// ==============================================================================

import { useConfigContext } from '../../context/ConfigContext';
import { NumberInput } from '../common/NumberInput';

export function ManifestEditor() {
  const { configs, updateConfig, lookups, validationErrors } = useConfigContext();
  const manifest = configs?.['manifest.json'];

  if (!manifest) {
    return <div className="p-8 text-center text-zinc-500">正在加载清单配置...</div>;
  }

  const handleUpdate = (patch: Partial<typeof manifest>) => {
    updateConfig('manifest.json', prev => ({ ...prev, ...patch }));
  };

  return (
    <div className="flex-1 flex flex-col p-6 overflow-y-auto max-w-5xl mx-auto w-full gap-6">
      {/* 顶部标题 */}
      <div className="flex items-center justify-between pb-4 border-b border-zinc-800">
        <div>
          <h2 className="text-lg font-bold text-zinc-100 flex items-center gap-2">
            <span>游戏数据版本清单</span>
            <span className="text-xs px-2 py-0.5 rounded bg-cyan-950 border border-cyan-800 text-cyan-300 font-mono">
              manifest.json
            </span>
          </h2>
          <p className="text-xs text-zinc-500 mt-1">
            定义游戏数据 Schema 版本号与静态资源内容发行版本号，用于客户端与服务端启动一致性校验。
          </p>
        </div>
      </div>

      {/* 核心配置表单 */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4 p-4 bg-[#10151c] border border-zinc-800 rounded-lg">
        <NumberInput
          label="Schema 结构版本 (SchemaVersion)"
          description="必须与服务端常量 SupportedSchemaVersion 一致"
          value={manifest.schemaVersion}
          onChange={v => handleUpdate({ schemaVersion: v ?? 1 })}
          min={1}
        />

        <div className="flex flex-col gap-1.5">
          <div className="flex items-center justify-between">
            <label className="text-xs font-medium text-zinc-300">内容版本号 (ContentVersion)</label>
            <span className="text-[11px] text-zinc-500">格式: YYYY.MM.DD.次版本</span>
          </div>
          <input
            type="text"
            value={manifest.contentVersion || ''}
            onChange={e => handleUpdate({ contentVersion: e.target.value })}
            placeholder="例如 2026.08.24.1"
            className="w-full bg-[#121820] border border-zinc-700/80 hover:border-zinc-500 focus:border-cyan-500 focus:ring-1 focus:ring-cyan-500/30 text-sm text-zinc-100 rounded px-3 py-1.5 font-mono transition-colors outline-none"
          />
        </div>
      </div>

      {/* 全局数据要素概览 */}
      <div className="flex flex-col gap-3">
        <h3 className="text-sm font-semibold text-zinc-300">全量游戏数据要素统计</h3>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">行动配置 (Actions)</span>
            <span className="text-2xl font-bold text-cyan-400 font-mono mt-1">
              {configs?.['actions.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">修炼/采集/炼丹/战斗</span>
          </div>

          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">物品道具 (Items)</span>
            <span className="text-2xl font-bold text-teal-400 font-mono mt-1">
              {configs?.['items.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">材料/丹药/消耗品</span>
          </div>

          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">神通技能 (Abilities)</span>
            <span className="text-2xl font-bold text-indigo-400 font-mono mt-1">
              {configs?.['abilities.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">五行/炼体道法</span>
          </div>

          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">法宝装备 (Equipment)</span>
            <span className="text-2xl font-bold text-purple-400 font-mono mt-1">
              {configs?.['equipment.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">八大部位法宝</span>
          </div>

          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">精怪妖兽 (Monsters)</span>
            <span className="text-2xl font-bold text-rose-400 font-mono mt-1">
              {configs?.['monsters.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">小怪/精英/首领</span>
          </div>

          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">秘境遭遇 (Encounters)</span>
            <span className="text-2xl font-bold text-amber-400 font-mono mt-1">
              {configs?.['encounters.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">关卡副本与波次</span>
          </div>

          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">世界大地图 (Maps)</span>
            <span className="text-2xl font-bold text-emerald-400 font-mono mt-1">
              {configs?.['maps.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">
              共 {lookups.nodeList.length} 个区域节点
            </span>
          </div>

          <div className="p-3 bg-[#121820] border border-zinc-800 rounded-lg flex flex-col">
            <span className="text-xs text-zinc-500">跨表完整性状态</span>
            <span className={`text-2xl font-bold font-mono mt-1 ${validationErrors.length > 0 ? 'text-red-400' : 'text-emerald-400'}`}>
              {validationErrors.length === 0 ? '正常' : `${validationErrors.length} 处告警`}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">
              {validationErrors.length === 0 ? '无死链或孤立引用' : '请查看全局校验面板'}
            </span>
          </div>
        </div>
      </div>
    </div>
  );
}
