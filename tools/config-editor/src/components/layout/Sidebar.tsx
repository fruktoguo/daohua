// ==============================================================================
// 文件名称: Sidebar.tsx
// 主要职责: 配置编辑器左侧导航栏，展示 9 大文件切换列表、数量与修改状态指示
// ==============================================================================

import { useConfigContext } from '../../context/ConfigContext';
import { CONFIG_FILE_LIST, type ConfigFileName } from '../../types/config';

export function Sidebar() {
  const { activeFile, setActiveFile, dirtyFiles, configs, validationErrors } = useConfigContext();

  const getCount = (filename: ConfigFileName) => {
    if (!configs) return null;
    const data = configs[filename];
    if (Array.isArray(data)) return data.length;
    if (filename === 'expected-attributes.json') return configs['expected-attributes.json']?.tiers?.length ?? null;
    return null;
  };

  const getFileErrorCount = (filename: ConfigFileName) => {
    return validationErrors.filter(e => e.sourceFile === filename).length;
  };

  return (
    <aside className="w-64 bg-[#0e131a] border-r border-zinc-800 flex flex-col shrink-0 select-none">
      {/* 顶部 Brand */}
      <div className="p-4 border-b border-zinc-800 flex items-center gap-3">
        <div className="w-8 h-8 rounded bg-gradient-to-br from-cyan-600 to-indigo-900 flex items-center justify-center font-bold text-sm text-white shadow-md shadow-cyan-950/50">
          道
        </div>
        <div className="flex flex-col">
          <h1 className="text-sm font-bold text-zinc-100 tracking-wide">道化太初 · 配置编辑器</h1>
          <span className="text-[10px] text-zinc-500 font-mono">Game Data Workbench</span>
        </div>
      </div>

      {/* 配置文件列表导航 */}
      <div className="flex-1 overflow-y-auto p-2 flex flex-col gap-1">
        <span className="px-2.5 py-1 text-[11px] font-semibold text-zinc-500 uppercase tracking-wider">
          全量游戏配置文件
        </span>

        {CONFIG_FILE_LIST.map(item => {
          const isActive = item.name === activeFile;
          const isDirty = dirtyFiles.has(item.name);
          const count = getCount(item.name);
          const errorCount = getFileErrorCount(item.name);

          return (
            <button
              key={item.name}
              type="button"
              onClick={() => setActiveFile(item.name)}
              className={`w-full px-3 py-2 rounded-lg text-xs font-medium flex items-center justify-between transition-all ${
                isActive
                  ? 'bg-cyan-950/80 text-cyan-200 border border-cyan-800/80 shadow-sm'
                  : 'text-zinc-400 hover:bg-[#141b24] hover:text-zinc-200 border border-transparent'
              }`}
            >
              <div className="flex items-center gap-2.5 min-w-0">
                <span className={`w-2 h-2 rounded-full shrink-0 ${isDirty ? 'bg-amber-400 animate-pulse' : 'bg-transparent'}`} />
                <span className="truncate">{item.label}</span>
              </div>

              <div className="flex items-center gap-1.5 shrink-0">
                {errorCount > 0 && (
                  <span className="px-1.5 py-0.2 text-[10px] rounded bg-red-950 text-red-300 border border-red-800 font-mono">
                    {errorCount}
                  </span>
                )}
                {count !== null && (
                  <span className="px-1.5 py-0.2 text-[10px] rounded bg-zinc-800/80 text-zinc-400 font-mono">
                    {count}
                  </span>
                )}
              </div>
            </button>
          );
        })}
      </div>

      {/* 底部未保存状态统计 */}
      <div className="p-3 border-t border-zinc-800 bg-[#0b0f14] flex flex-col gap-1.5 text-xs">
        <div className="flex items-center justify-between text-zinc-400">
          <span>未保存改动:</span>
          <span className={`font-mono font-bold ${dirtyFiles.size > 0 ? 'text-amber-400' : 'text-emerald-400'}`}>
            {dirtyFiles.size} 个文件
          </span>
        </div>
        <div className="flex items-center justify-between text-zinc-400">
          <span>全局死链校验:</span>
          <span className={`font-mono font-bold ${validationErrors.length > 0 ? 'text-red-400' : 'text-emerald-400'}`}>
            {validationErrors.length === 0 ? '完全一致' : `${validationErrors.length} 处告警`}
          </span>
        </div>
      </div>
    </aside>
  );
}
