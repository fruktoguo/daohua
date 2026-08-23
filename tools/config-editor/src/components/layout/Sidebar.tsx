// ==============================================================================
// 文件名称: Sidebar.tsx
// 主要职责: 现代扁平化配置编辑器侧边栏导航，支持 SVG 图标、状态胶囊与计数
// ==============================================================================

import { useConfigContext } from '../../context/ConfigContext';
import { CONFIG_FILE_LIST, type ConfigFileName } from '../../types/config';

// 9 大配置文件对应现代化扁平 SVG 图标
function getFileIcon(name: ConfigFileName) {
  switch (name) {
    case 'manifest.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
        </svg>
      );
    case 'actions.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M13 10V3L4 14h7v7l9-11h-7z" />
        </svg>
      );
    case 'items.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4" />
        </svg>
      );
    case 'abilities.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M5 3v4M3 5h4M6 17v4m-2-2h4m5-16l2.286 6.857L21 12l-5.714 2.143L13 21l-2.286-6.857L5 12l5.714-2.143L13 3z" />
        </svg>
      );
    case 'equipment.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
        </svg>
      );
    case 'monsters.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
        </svg>
      );
    case 'encounters.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M17.657 18.657A8 8 0 016.343 7.343S7 9 9 10c0-2 .5-5 2.986-7C14 5 16.09 5.777 17.656 7.343A7.975 7.975 0 0120 13a7.975 7.975 0 01-2.343 5.657z" />
        </svg>
      );
    case 'maps.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M9 20l-5.447-2.724A1 1 0 013 16.382V5.618a1 1 0 011.447-.894L9 7m0 13l6-3m-6 3V7m6 10l4.553 2.276A1 1 0 0021 18.382V7.618a1 1 0 00-.553-.894L15 4m0 13V4m0 0L9 7" />
        </svg>
      );
    case 'expected-attributes.json':
      return (
        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.75" d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
        </svg>
      );
  }
}

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
    <aside className="w-64 bg-zinc-950 border-r border-zinc-800/80 flex flex-col shrink-0 select-none">
      {/* 顶部 Brand */}
      <div className="p-4 border-b border-zinc-800/80 flex items-center gap-3">
        <div className="w-8 h-8 rounded-lg bg-indigo-600 text-white flex items-center justify-center font-bold text-sm shadow-md shadow-indigo-950/40 shrink-0">
          太
        </div>
        <div className="flex flex-col min-w-0">
          <h1 className="text-xs font-bold text-zinc-100 tracking-tight truncate">道化太初 · 配置台</h1>
          <span className="text-[10px] text-zinc-500 font-mono truncate">Game Data Workbench</span>
        </div>
      </div>

      {/* 配置文件列表导航 */}
      <div className="flex-1 overflow-y-auto p-2 flex flex-col gap-0.5">
        <div className="px-2.5 py-1.5 text-[10px] font-semibold text-zinc-500 uppercase tracking-wider">
          全量游戏配置 ({CONFIG_FILE_LIST.length})
        </div>

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
              className={`w-full px-3 py-2 rounded-lg text-xs font-medium flex items-center justify-between transition-all duration-150 group ${
                isActive
                  ? 'bg-zinc-900 text-white shadow-sm border border-zinc-800'
                  : 'text-zinc-400 hover:bg-zinc-900/60 hover:text-zinc-200 border border-transparent'
              }`}
            >
              <div className="flex items-center gap-2.5 min-w-0">
                <span className={`shrink-0 transition-colors ${isActive ? 'text-indigo-400' : 'text-zinc-500 group-hover:text-zinc-400'}`}>
                  {getFileIcon(item.name)}
                </span>
                <span className="truncate">{item.label}</span>
              </div>

              <div className="flex items-center gap-1.5 shrink-0">
                {isDirty && (
                  <span className="w-1.5 h-1.5 rounded-full bg-amber-400 animate-pulse" title="有未保存修改" />
                )}
                {errorCount > 0 && (
                  <span className="px-1.5 py-0.2 text-[9px] font-mono font-bold rounded-full bg-rose-500/20 text-rose-300 border border-rose-500/30">
                    {errorCount}
                  </span>
                )}
                {count !== null && (
                  <span className="px-1.5 py-0.2 text-[10px] font-mono rounded-md bg-zinc-900 text-zinc-500 group-hover:text-zinc-400">
                    {count}
                  </span>
                )}
              </div>
            </button>
          );
        })}
      </div>

      {/* 底部状态简报 */}
      <div className="p-3 border-t border-zinc-800/80 bg-zinc-950 flex flex-col gap-1.5 text-xs">
        <div className="flex items-center justify-between text-zinc-400">
          <span className="text-[11px]">未保存改动:</span>
          <span className={`font-mono text-xs font-semibold ${dirtyFiles.size > 0 ? 'text-amber-400' : 'text-zinc-500'}`}>
            {dirtyFiles.size > 0 ? `${dirtyFiles.size} 个文件` : '全部已保存'}
          </span>
        </div>
        <div className="flex items-center justify-between text-zinc-400">
          <span className="text-[11px]">跨表死链检测:</span>
          <span className={`font-mono text-xs font-semibold ${validationErrors.length > 0 ? 'text-rose-400' : 'text-emerald-400'}`}>
            {validationErrors.length === 0 ? '✓ 引用正常' : `⚠ ${validationErrors.length} 处告警`}
          </span>
        </div>
      </div>
    </aside>
  );
}
