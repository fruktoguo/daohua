// ==============================================================================
// 文件名称: GlobalValidator.tsx
// 主要职责: 全局跨表关联完整性校验面板，检测死链与孤立引用并支持一键定位修复
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
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 backdrop-blur-sm p-4">
      <div className="bg-[#10151c] border border-zinc-700 rounded-xl shadow-2xl w-full max-w-4xl max-h-[85vh] flex flex-col overflow-hidden">
        {/* 顶部标题栏 */}
        <div className="p-4 border-b border-zinc-800 flex items-center justify-between bg-[#141b24]">
          <div className="flex items-center gap-2.5">
            <span className="text-base font-bold text-zinc-100">游戏数据全局完整性校验</span>
            <span
              className={`px-2 py-0.5 text-xs rounded font-mono ${
                validationErrors.length === 0
                  ? 'bg-emerald-950 text-emerald-300 border border-emerald-800'
                  : 'bg-red-950 text-red-300 border border-red-800'
              }`}
            >
              {validationErrors.length === 0 ? '全表引用正常' : `发现 ${validationErrors.length} 处问题`}
            </span>
          </div>

          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={validating}
              onClick={runFullValidation}
              className="px-3 py-1.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-200 text-xs rounded transition-colors disabled:opacity-50 flex items-center gap-1.5"
            >
              {validating ? '正在运行领域层验证...' : '运行 .NET 后端校验'}
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

        {/* 内容区 */}
        <div className="flex-1 overflow-y-auto p-4 flex flex-col gap-4">
          {/* 前端跨表引用死链列表 */}
          <div className="flex flex-col gap-2">
            <h3 className="text-xs font-semibold text-zinc-400">
              跨表外键关联实时检测 (掉落物/技能装配/刷怪池/遭遇战/节点路径)
            </h3>

            {validationErrors.length === 0 ? (
              <div className="p-6 bg-[#141b24] border border-emerald-900/40 rounded-lg text-center flex flex-col items-center gap-2">
                <span className="text-2xl text-emerald-400">✓</span>
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
                    className={`p-3 rounded-lg border text-xs flex items-start justify-between gap-3 ${
                      err.severity === 'error'
                        ? 'bg-red-950/20 border-red-800/80 text-red-200'
                        : 'bg-amber-950/20 border-amber-800/80 text-amber-200'
                    }`}
                  >
                    <div className="flex flex-col gap-1">
                      <div className="flex items-center gap-2">
                        <span className="font-bold text-zinc-200">[{err.sourceFile}]</span>
                        {err.itemId && <span className="font-mono text-zinc-400">ID: {err.itemId}</span>}
                        <span className="px-1.5 py-0.2 rounded bg-zinc-800 text-[10px] text-zinc-400">
                          字段: {err.field}
                        </span>
                      </div>
                      <p className="text-zinc-300">{err.message}</p>
                    </div>

                    <button
                      type="button"
                      onClick={() => handleNavigate(err.sourceFile)}
                      className="px-2.5 py-1 bg-zinc-800 hover:bg-zinc-700 text-zinc-200 text-xs rounded shrink-0"
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
            <div className="p-4 bg-[#141b24] border border-zinc-800 rounded-lg flex flex-col gap-2">
              <h3 className="text-xs font-semibold text-zinc-300 flex items-center justify-between">
                <span>.NET 领域层装配与 Schema 校验报告 (DataTool)</span>
                <span
                  className={`font-mono ${
                    backendValidationResult.ok ? 'text-emerald-400' : 'text-red-400'
                  }`}
                >
                  {backendValidationResult.ok ? '通过 (Pass)' : '失败 (Failed)'}
                </span>
              </h3>
              {backendValidationResult.output ? (
                <pre className="p-3 bg-[#0e131a] rounded text-xs font-mono text-zinc-300 overflow-x-auto">
                  {JSON.stringify(backendValidationResult.output, null, 2)}
                </pre>
              ) : (
                <pre className="p-3 bg-[#0e131a] rounded text-xs font-mono text-red-400 overflow-x-auto">
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
