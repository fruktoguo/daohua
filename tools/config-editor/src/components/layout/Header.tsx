// ==============================================================================
// 文件名称: Header.tsx
// 主要职责: 顶部操作工具栏，支持一键保存、批量写盘、放弃修改、快照备份与全局校验
// ==============================================================================

import { useState } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import { GlobalValidator } from '../validation/GlobalValidator';
import { RawJsonModal } from './RawJsonModal';

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
      <header className="h-14 bg-[#0e131a] border-b border-zinc-800 px-6 flex items-center justify-between shrink-0 select-none">
        {/* 左侧文件标识 */}
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2">
            <span className="text-xs text-zinc-500 font-mono">当前编辑:</span>
            <span className="text-sm font-bold text-zinc-200 font-mono">{activeFile}</span>
          </div>

          {isCurrentDirty ? (
            <span className="px-2 py-0.5 text-[11px] rounded bg-amber-950/80 text-amber-300 border border-amber-800 font-medium">
              未保存
            </span>
          ) : (
            <span className="px-2 py-0.5 text-[11px] rounded bg-emerald-950/80 text-emerald-300 border border-emerald-800">
              已同步
            </span>
          )}
        </div>

        {/* 右侧全局操作 */}
        <div className="flex items-center gap-2.5">
          {/* 校验报告按钮 */}
          <button
            type="button"
            onClick={() => setIsValidatorOpen(true)}
            className={`px-3 py-1.5 text-xs rounded border transition-colors flex items-center gap-1.5 ${
              validationErrors.length > 0
                ? 'bg-red-950/40 border-red-800 text-red-300 hover:bg-red-900/40'
                : 'bg-zinc-800 border-zinc-700 text-zinc-300 hover:bg-zinc-700'
            }`}
          >
            <span>{validationErrors.length === 0 ? '✓ 全局校验正常' : `⚠ ${validationErrors.length} 处引用问题`}</span>
          </button>

          {/* 原始 JSON 预览 */}
          <button
            type="button"
            onClick={() => setIsJsonModalOpen(true)}
            className="px-3 py-1.5 bg-zinc-800 hover:bg-zinc-700 border border-zinc-700 text-xs text-zinc-200 rounded transition-colors"
          >
            查看原始 JSON
          </button>

          {/* 数据备份 */}
          <button
            type="button"
            onClick={triggerBackup}
            className="px-3 py-1.5 bg-zinc-800 hover:bg-zinc-700 border border-zinc-700 text-xs text-zinc-200 rounded transition-colors"
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
            className="px-2.5 py-1.5 bg-zinc-800 hover:bg-zinc-700 border border-zinc-700 text-xs text-zinc-400 hover:text-zinc-200 rounded"
            title="从磁盘重新加载全量配置"
          >
            ↻
          </button>

          {/* 放弃改动 */}
          {isCurrentDirty && (
            <button
              type="button"
              onClick={resetCurrentFile}
              className="px-3 py-1.5 bg-zinc-800 hover:bg-zinc-700 text-zinc-400 hover:text-zinc-200 text-xs rounded transition-colors"
            >
              放弃本文件修改
            </button>
          )}

          {/* 保存当前单文件 */}
          <button
            type="button"
            disabled={saving || !isCurrentDirty}
            onClick={handleSaveCurrent}
            className="px-3 py-1.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 disabled:opacity-40 text-cyan-200 text-xs font-medium rounded transition-colors"
          >
            {saving ? '保存中...' : '保存当前文件'}
          </button>

          {/* 保存全部修改 */}
          {dirtyFiles.size > 1 && (
            <button
              type="button"
              disabled={saving}
              onClick={handleSaveAll}
              className="px-3.5 py-1.5 bg-emerald-900 hover:bg-emerald-800 border border-emerald-700 text-emerald-100 text-xs font-bold rounded shadow-lg shadow-emerald-950/40 transition-colors"
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
