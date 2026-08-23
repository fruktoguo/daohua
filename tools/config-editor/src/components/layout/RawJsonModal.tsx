// ==============================================================================
// 文件名称: RawJsonModal.tsx
// 主要职责: 现代扁平化原始 JSON 预览与复制弹窗 (毛玻璃卡片容器)
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
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-md p-4 animate-in fade-in-0 duration-150">
      <div className="bg-zinc-950 border border-zinc-800 rounded-2xl shadow-2xl w-full max-w-4xl max-h-[85vh] flex flex-col overflow-hidden">
        {/* 顶部 */}
        <div className="p-4 border-b border-zinc-800/80 flex items-center justify-between bg-zinc-900/50">
          <div className="flex items-center gap-2.5">
            <span className="text-sm font-bold text-zinc-100">原始 JSON 预览</span>
            <span className="text-xs px-2.5 py-0.5 rounded-full bg-zinc-800 text-zinc-300 font-mono border border-zinc-700/50">
              {activeFile}
            </span>
          </div>

          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={handleCopy}
              className="px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-semibold rounded-lg transition-colors shadow-sm"
            >
              {copied ? '✓ 已复制到剪贴板' : '复制完整 JSON'}
            </button>
            <button
              type="button"
              onClick={onClose}
              className="text-zinc-500 hover:text-zinc-200 p-1 text-sm rounded-lg hover:bg-zinc-800 transition-colors"
            >
              ✕
            </button>
          </div>
        </div>

        {/* 内容 */}
        <div className="flex-1 overflow-y-auto p-4 bg-zinc-950">
          <pre className="text-xs font-mono text-indigo-200/90 whitespace-pre-wrap leading-relaxed select-text p-2">
            {jsonString}
          </pre>
        </div>
      </div>
    </div>
  );
}
