// ==============================================================================
// 文件名称: GlobalValidator.tsx
// 主要职责: 现代扁平化全局跨表引用校验与后端 DataTool 检查面板
// ==============================================================================

import { useConfigContext } from '../../context/ConfigContext';
import type { ConfigFileName } from '../../types/config';

interface GlobalValidatorProps {
  isOpen: boolean;
  onClose: () => void;
}

export function GlobalValidator({ isOpen, onClose }: GlobalValidatorProps) {
  const {
    validationErrors,
    backendValidationResult,
    validating,
    runFullValidation,
    setActiveFile
  } = useConfigContext();

  if (!isOpen) return null;

  const handleNavigate = (sourceFile: ConfigFileName) => {
    setActiveFile(sourceFile);
    onClose();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-md p-4 animate-in fade-in-0 duration-150">
      <div className="bg-zinc-950 border border-zinc-800 rounded-2xl shadow-2xl w-full max-w-4xl max-h-[85vh] flex flex-col overflow-hidden">
        {/* 顶部标题栏 */}
        <div className="p-4 border-b border-zinc-800/80 flex items-center justify-between bg-zinc-900/50">
          <div className="flex items-center gap-2.5">
            <span className="text-sm font-bold text-zinc-100">跨表完整性校验与领域门禁</span>
            <span
              className={`px-2.5 py-0.5 text-xs font-semibold rounded-full border font-mono ${
                validationErrors.length === 0
                  ? 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20'
                  : 'bg-rose-500/10 text-rose-300 border-rose-500/20'
              }`}
            >
              {validationErrors.length === 0 ? '全表外键有效' : `发现 ${validationErrors.length} 处告警`}
            </span>
          </div>

          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={validating}
              onClick={runFullValidation}
              className="px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 text-white text-xs font-semibold rounded-lg transition-colors shadow-sm disabled:opacity-40 flex items-center gap-1.5"
            >
              {validating ? (
                <>
                  <span className="w-3 h-3 border-2 border-white border-t-transparent rounded-full animate-spin" />
                  <span>校验中...</span>
                </>
              ) : (
                <span>运行 .NET 后端校验</span>
              )}
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

        {/* 内容区 */}
        <div className="flex-1 overflow-y-auto p-5 flex flex-col gap-5">
          {/* 前端实时死链列表 */}
          <div className="flex flex-col gap-2.5">
            <h3 className="text-xs font-semibold text-zinc-400">
              跨表外键实时检测 (掉落物 / 技能装配 / 刷怪池 / 遭遇战 / 节点路径)
            </h3>

            {validationErrors.length === 0 ? (
              <div className="p-6 bg-zinc-900/40 border border-emerald-500/20 rounded-xl text-center flex flex-col items-center gap-2">
                <div className="w-8 h-8 rounded-full bg-emerald-500/10 text-emerald-400 flex items-center justify-center font-bold text-base">
                  ✓
                </div>
                <span className="text-sm font-medium text-emerald-300">所有配置文件跨表外键引用均有效</span>
                <p className="text-xs text-zinc-500">
                  技能、道具、怪物、遭遇、行动和地图节点之间无任何死链或未声明的孤立 ID。
                </p>
              </div>
            ) : (
              <div className="flex flex-col gap-2">
                {validationErrors.map(err => (
                  <div
                    key={err.id}
                    className="p-3.5 rounded-xl border border-rose-500/30 bg-rose-500/5 text-xs flex items-start justify-between gap-3"
                  >
                    <div className="flex flex-col gap-1">
                      <div className="flex items-center gap-2">
                        <span className="font-bold text-zinc-100 font-mono">[{err.sourceFile}]</span>
                        {err.itemId && <span className="font-mono text-zinc-400">ID: {err.itemId}</span>}
                        <span className="px-2 py-0.2 rounded bg-zinc-800 text-[10px] text-zinc-400 font-mono">
                          字段: {err.field}
                        </span>
                      </div>
                      <p className="text-rose-200 mt-0.5">{err.message}</p>
                    </div>

                    <button
                      type="button"
                      onClick={() => handleNavigate(err.sourceFile)}
                      className="px-3 py-1 bg-zinc-800 hover:bg-zinc-700 text-zinc-200 text-xs font-medium rounded-lg shrink-0 transition-colors shadow-sm"
                    >
                      前往修复 →
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* 后端 DataTool 校验结果 */}
          {backendValidationResult && (
            <div className="p-4 bg-zinc-900/40 border border-zinc-800 rounded-xl flex flex-col gap-2.5">
              <div className="flex items-center justify-between">
                <span className="text-xs font-semibold text-zinc-300">
                  .NET 领域层装配与 Schema 校验报告 (DataTool)
                </span>
                <span
                  className={`text-xs font-mono font-bold ${
                    backendValidationResult.ok ? 'text-emerald-400' : 'text-rose-400'
                  }`}
                >
                  {backendValidationResult.ok ? '✓ PASS' : '✕ FAILED'}
                </span>
              </div>
              {backendValidationResult.output ? (
                <pre className="p-3.5 bg-zinc-950 border border-zinc-800/80 rounded-lg text-xs font-mono text-zinc-300 overflow-x-auto">
                  {JSON.stringify(backendValidationResult.output, null, 2)}
                </pre>
              ) : (
                <pre className="p-3.5 bg-zinc-950 border border-rose-900/40 rounded-lg text-xs font-mono text-rose-300 overflow-x-auto">
                  {backendValidationResult.error || backendValidationResult.stderr || backendValidationResult.rawStderr}
                </pre>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
