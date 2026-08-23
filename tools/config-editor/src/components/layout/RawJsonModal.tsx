// ==============================================================================
// 文件名称: RawJsonModal.tsx
// 主要职责: 原始 JSON 预览与复制弹窗
// ==============================================================================

import { useState } from 'react';
import { useConfigContext } from '../../context/ConfigContext';

interface RawJsonModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export function RawJsonModal({ isOpen, onClose }: RawJsonModalProps) {
  const { configs, activeFile } = useConfigContext();
  const [copied, setCopied] = useState(false);

  if (!isOpen || !configs) return null;

  const currentData = configs[activeFile];
  const jsonString = JSON.stringify(currentData, null, 2);

  const handleCopy = () => {
    navigator.clipboard.writeText(jsonString);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
      <div className="bg-[#10151c] border border-zinc-700 rounded-xl shadow-2xl w-full max-w-4xl max-h-[85vh] flex flex-col overflow-hidden">
        {/* 顶部 */}
        <div className="p-4 border-b border-zinc-800 flex items-center justify-between bg-[#141b24]">
          <div className="flex items-center gap-2">
            <span className="text-sm font-bold text-zinc-100">原始 JSON 预览</span>
            <span className="text-xs px-2 py-0.5 rounded bg-zinc-800 text-zinc-300 font-mono">
              {activeFile}
            </span>
          </div>

          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={handleCopy}
              className="px-3 py-1 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-200 text-xs rounded transition-colors"
            >
              {copied ? '✓ 已复制到剪贴板' : '复制 JSON'}
            </button>
            <button
              type="button"
              onClick={onClose}
              className="text-zinc-400 hover:text-zinc-100 p-1 text-lg font-mono"
            >
              ×
            </button>
          </div>
        </div>

        {/* 内容 */}
        <div className="flex-1 overflow-y-auto p-4 bg-[#0e131a]">
          <pre className="text-xs font-mono text-cyan-200/90 whitespace-pre-wrap leading-relaxed select-text">
            {jsonString}
          </pre>
        </div>
      </div>
    </div>
  );
}
