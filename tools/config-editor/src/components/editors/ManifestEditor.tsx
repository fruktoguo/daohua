// ==============================================================================
// 文件名称: ManifestEditor.tsx
// 主要职责: 现代扁平化 manifest.json 专属编辑器与全量数据概览看板
// ==============================================================================

import { useConfigContext } from '../../context/ConfigContext';
import { NumberInput } from '../common/NumberInput';

/** manifest.json 编辑器：编辑 Schema/Content 双版本号，并展示全量数据要素统计与跨表外键完整性概览。 */
export function ManifestEditor() {
  const { configs, updateConfig, lookups, validationErrors } = useConfigContext();
  const manifest = configs?.['manifest.json'];

  if (!manifest) {
    return <div className="p-8 text-center text-zinc-500 text-sm">正在加载清单配置...</div>;
  }

  const handleUpdate = (patch: Partial<typeof manifest>) => {
    updateConfig('manifest.json', prev => ({ ...prev, ...patch }));
  };

  return (
    <div className="flex-1 flex flex-col p-8 overflow-y-auto max-w-5xl mx-auto w-full gap-8">
      {/* 顶部标题与说明 */}
      <div className="flex flex-col gap-1 pb-4 border-b border-zinc-800/80">
        <div className="flex items-center gap-3">
          <h2 className="text-xl font-bold text-zinc-100 tracking-tight">数据版本与清单概览</h2>
          <span className="text-xs px-2.5 py-0.5 rounded-full bg-indigo-500/10 border border-indigo-500/20 text-indigo-300 font-mono font-medium">
            manifest.json
          </span>
        </div>
        <p className="text-xs text-zinc-500">
          定义游戏数据 Schema 版本与静态资源发行版本号，用于客户端与服务端启动一致性校验。
        </p>
      </div>

      {/* 核心配置卡片 */}
      <div className="p-6 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-5">
        <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">版本元数据</h3>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
          <NumberInput
            label="Schema 结构版本 (SchemaVersion)"
            description="必须与服务端常量 SupportedSchemaVersion 一致"
            value={manifest.schemaVersion}
            onChange={v => handleUpdate({ schemaVersion: v ?? 1 })}
            min={1}
          />

          <div className="flex flex-col gap-1.5">
            <div className="flex items-center justify-between">
              <label className="text-xs font-medium text-zinc-300 tracking-tight">内容版本号 (ContentVersion)</label>
              <span className="text-[11px] text-zinc-500 font-normal">格式: YYYY.MM.DD.次版本</span>
            </div>
            <input
              type="text"
              value={manifest.contentVersion || ''}
              onChange={e => handleUpdate({ contentVersion: e.target.value })}
              placeholder="例如 2026.08.24.1"
              className="w-full bg-zinc-900/90 border border-zinc-800 hover:border-zinc-700 focus:border-indigo-500/80 focus:ring-2 focus:ring-indigo-500/20 text-sm font-mono text-zinc-100 rounded-lg px-3 py-1.5 transition-all outline-none"
            />
          </div>
        </div>
      </div>

      {/* 全局数据要素统计网格 */}
      <div className="flex flex-col gap-3">
        <h3 className="text-xs font-semibold text-zinc-400 uppercase tracking-wider">全量数据要素矩阵</h3>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3.5">
          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">行动条目 (Actions)</span>
            <span className="text-2xl font-bold text-cyan-400 font-mono mt-1">
              {configs?.['actions.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">修炼 / 采集 / 炼丹 / 战斗</span>
          </div>

          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">道具物品 (Items)</span>
            <span className="text-2xl font-bold text-emerald-400 font-mono mt-1">
              {configs?.['items.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">灵植 / 矿石 / 丹药 / 典籍</span>
          </div>

          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">神通法诀 (Abilities)</span>
            <span className="text-2xl font-bold text-indigo-400 font-mono mt-1">
              {configs?.['abilities.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">五行道法 / 纯阳剑罡 / 炼体</span>
          </div>

          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">法宝神兵 (Equipment)</span>
            <span className="text-2xl font-bold text-purple-400 font-mono mt-1">
              {configs?.['equipment.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">八大槽位法宝神兵</span>
          </div>

          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">精怪妖兽 (Monsters)</span>
            <span className="text-2xl font-bold text-rose-400 font-mono mt-1">
              {configs?.['monsters.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">小妖 / 天地精英 / 秘境首领</span>
          </div>

          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">秘境遭遇 (Encounters)</span>
            <span className="text-2xl font-bold text-amber-400 font-mono mt-1">
              {configs?.['encounters.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">关卡副本 / 刷怪波次</span>
          </div>

          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">世界大地图 (Maps)</span>
            <span className="text-2xl font-bold text-blue-400 font-mono mt-1">
              {configs?.['maps.json']?.length ?? 0}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">
              共 {lookups.nodeList.length} 个区域节点
            </span>
          </div>

          <div className="p-4 bg-zinc-900/40 border border-zinc-800/80 rounded-xl flex flex-col hover:border-zinc-700 transition-colors">
            <span className="text-xs text-zinc-500 font-medium">跨表外键完整性</span>
            <span className={`text-2xl font-bold font-mono mt-1 ${validationErrors.length > 0 ? 'text-rose-400' : 'text-emerald-400'}`}>
              {validationErrors.length === 0 ? '100% 正常' : `${validationErrors.length} 处告警`}
            </span>
            <span className="text-[11px] text-zinc-500 mt-1">
              {validationErrors.length === 0 ? '全表外键无死链' : '点击顶栏查看修复'}
            </span>
          </div>
        </div>
      </div>
    </div>
  );
}
