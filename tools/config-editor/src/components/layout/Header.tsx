// ==============================================================================
// 文件名称: Header.tsx
// 主要职责: 现代扁平化配置编辑器顶栏工具栏，支持快捷键提示、状态指示灯与批量保存
// ==============================================================================

import { useState } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import { GlobalValidator } from '../validation/GlobalValidator';
import { RawJsonModal } from './RawJsonModal';

/** 编辑器顶栏工具栏：当前文件与保存状态指示、跨表校验入口、JSON 源码预览、快照备份、重载与单文件/批量保存操作。 */
export function Header() {
  const {
    activeFile,
    dirtyFiles,
    saveCurrentFile,
    saveAllFiles,
    resetCurrentFile,
    reloadAllConfigs,
    triggerBackup,
    validationErrors
  } = useConfigContext();

  const [isValidatorOpen, setIsValidatorOpen] = useState(false);
  const [isJsonModalOpen, setIsJsonModalOpen] = useState(false);
  const [saving, setSaving] = useState(false);

  const isCurrentDirty = dirtyFiles.has(activeFile);

  const handleSaveCurrent = async () => {
    setSaving(true);
    await saveCurrentFile();
    setSaving(false);
  };

  const handleSaveAll = async () => {
    setSaving(true);
    await saveAllFiles();
    setSaving(false);
  };

  return (
    <>
      <header className="h-13 bg-zinc-950 border-b border-zinc-800/80 px-6 flex items-center justify-between shrink-0 select-none">
        {/* 左侧文件标识与保存状态 */}
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2">
            <span className="text-xs text-zinc-500 font-mono">File:</span>
            <span className="text-xs font-semibold text-zinc-100 font-mono bg-zinc-900 px-2.5 py-1 rounded-md border border-zinc-800">
              {activeFile}
            </span>
          </div>

          {isCurrentDirty ? (
            <span className="inline-flex items-center gap-1.5 px-2 py-0.5 text-[11px] font-medium rounded-full bg-amber-500/10 text-amber-300 border border-amber-500/20">
              <span className="w-1.5 h-1.5 rounded-full bg-amber-400 animate-pulse" />
              <span>未保存改动</span>
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 px-2 py-0.5 text-[11px] font-medium rounded-full bg-emerald-500/10 text-emerald-300 border border-emerald-500/20">
              <span className="w-1.5 h-1.5 rounded-full bg-emerald-400" />
              <span>已与磁盘同步</span>
            </span>
          )}
        </div>

        {/* 右侧全局操作按钮组 */}
        <div className="flex items-center gap-2">
          {/* 全局完整性校验状态按钮 */}
          <button
            type="button"
            onClick={() => setIsValidatorOpen(true)}
            className={`px-3 py-1.5 text-xs font-medium rounded-lg border transition-all duration-150 flex items-center gap-1.5 ${
              validationErrors.length > 0
                ? 'bg-rose-500/10 border-rose-500/30 text-rose-300 hover:bg-rose-500/20'
                : 'bg-zinc-900 border-zinc-800 text-zinc-300 hover:bg-zinc-800 hover:text-zinc-100'
            }`}
          >
            {validationErrors.length === 0 ? (
              <>
                <span className="text-emerald-400 text-xs">✓</span>
                <span>数据校验正常</span>
              </>
            ) : (
              <>
                <span className="text-rose-400 text-xs">⚠</span>
                <span>{validationErrors.length} 处外键告警</span>
              </>
            )}
          </button>

          {/* 原始 JSON 预览 */}
          <button
            type="button"
            onClick={() => setIsJsonModalOpen(true)}
            className="px-3 py-1.5 bg-zinc-900 hover:bg-zinc-800 border border-zinc-800 text-xs font-medium text-zinc-300 hover:text-zinc-100 rounded-lg transition-colors"
          >
            JSON 源码
          </button>

          {/* 数据备份 */}
          <button
            type="button"
            onClick={triggerBackup}
            className="px-3 py-1.5 bg-zinc-900 hover:bg-zinc-800 border border-zinc-800 text-xs font-medium text-zinc-300 hover:text-zinc-100 rounded-lg transition-colors"
            title="创建配置文件快照备份"
          >
            快照备份
          </button>

          {/* 刷新重载 */}
          <button
            type="button"
            onClick={() => {
              if (dirtyFiles.size > 0 && !confirm('重载将丢弃所有未保存的改动，确定要重载吗？')) return;
              reloadAllConfigs();
            }}
            className="px-2.5 py-1.5 bg-zinc-900 hover:bg-zinc-800 border border-zinc-800 text-xs font-medium text-zinc-400 hover:text-zinc-200 rounded-lg transition-colors"
            title="从磁盘重新加载全量配置"
          >
            ↻
          </button>

          {/* 放弃改动 */}
          {isCurrentDirty && (
            <button
              type="button"
              onClick={resetCurrentFile}
              className="px-3 py-1.5 bg-zinc-900 hover:bg-zinc-800 text-zinc-400 hover:text-rose-300 text-xs font-medium rounded-lg transition-colors"
            >
              放弃修改
            </button>
          )}

          {/* 保存当前文件 */}
          <button
            type="button"
            disabled={saving || !isCurrentDirty}
            onClick={handleSaveCurrent}
            className="px-3.5 py-1.5 bg-indigo-600 hover:bg-indigo-500 disabled:opacity-30 text-white text-xs font-semibold rounded-lg transition-all duration-150 shadow-sm flex items-center gap-1.5"
          >
            <span>{saving ? '保存中...' : '保存'}</span>
            <kbd className="hidden sm:inline-block px-1.5 py-0.2 text-[10px] font-mono rounded bg-indigo-800 text-indigo-200">
              Ctrl+S
            </kbd>
          </button>

          {/* 保存全部修改 */}
          {dirtyFiles.size > 1 && (
            <button
              type="button"
              disabled={saving}
              onClick={handleSaveAll}
              className="px-3.5 py-1.5 bg-emerald-600 hover:bg-emerald-500 border border-emerald-500/40 text-white text-xs font-bold rounded-lg shadow-lg shadow-emerald-950/40 transition-colors"
            >
              保存全部 ({dirtyFiles.size})
            </button>
          )}
        </div>
      </header>

      {/* 弹窗 */}
      <GlobalValidator isOpen={isValidatorOpen} onClose={() => setIsValidatorOpen(false)} />
      <RawJsonModal isOpen={isJsonModalOpen} onClose={() => setIsJsonModalOpen(false)} />
    </>
  );
}
