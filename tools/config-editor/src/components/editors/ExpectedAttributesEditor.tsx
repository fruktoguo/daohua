// ==============================================================================
// 文件名称: ExpectedAttributesEditor.tsx
// 主要职责: expected-attributes.json 专属编辑器，展示四大乘区公式、阶位矩阵与战斗仿真基准
// ==============================================================================

import { useState } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import { TIER_OPTIONS, STAT_METADATA_MAP } from '../../types/enums';

export function ExpectedAttributesEditor() {
  const { configs, updateConfig } = useConfigContext();
  const expectedConfig = configs?.['expected-attributes.json'];

  const [selectedTier, setSelectedTier] = useState<number>(1);

  if (!expectedConfig) {
    return <div className="p-8 text-center text-zinc-500">正在加载期望属性矩阵配置...</div>;
  }

  const currentTierData = expectedConfig.tiers?.find(t => t.tier === selectedTier) || expectedConfig.tiers?.[0];

  const handleUpdateCurrentTier = (patch: Partial<typeof currentTierData>) => {
    if (!currentTierData) return;
    const nextTiers = expectedConfig.tiers.map(t => (t.tier === currentTierData.tier ? { ...t, ...patch } : t));
    updateConfig('expected-attributes.json', prev => ({ ...prev, tiers: nextTiers }));
  };

  const handleUpdateBaseFlat = (key: string, val: number) => {
    if (!currentTierData) return;
    const nextBase = { ...(currentTierData.baseFlat || {}), [key]: val };
    handleUpdateCurrentTier({ baseFlat: nextBase });
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧阶位选择导航 */}
      <div className="w-64 border-r border-zinc-800 bg-[#0e131a] flex flex-col shrink-0">
        <div className="p-3 border-b border-zinc-800 flex items-center justify-between">
          <span className="text-xs font-bold text-zinc-300">境界阶位 (T1 ~ T12)</span>
          <span className="text-[11px] text-zinc-500 font-mono">v{expectedConfig.version}</span>
        </div>
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40">
          {(expectedConfig.tiers || []).map(t => {
            const isSelected = t.tier === selectedTier;
            const tierOpt = TIER_OPTIONS.find(opt => opt.value === t.tier);
            return (
              <div
                key={t.tier}
                onClick={() => setSelectedTier(t.tier)}
                className={`p-3 cursor-pointer flex flex-col gap-1 transition-colors ${
                  isSelected
                    ? 'bg-cyan-950/60 border-l-2 border-cyan-400'
                    : 'hover:bg-[#141b24]'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-bold ${tierOpt?.badgeColor || 'text-zinc-200'}`}>
                    T{t.tier} · {t.realmName}
                  </span>
                  <span className="text-[11px] text-zinc-400 font-mono">Lv.{t.level}</span>
                </div>
                <div className="flex items-center justify-between text-[10px] text-zinc-500 font-mono">
                  <span>总乘数: {t.compoundMultiplier?.toFixed(2)}x</span>
                  <span>HP: {Math.round(t.finalExpected?.maxHitpoints || 0)}</span>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细数值与仿真看板 */}
      {currentTierData ? (
        <div className="flex-1 overflow-y-auto p-6 flex flex-col gap-6 bg-[#0b0f14]">
          {/* 四大独立乘区公式卡片 */}
          <div className="p-4 bg-[#10151c] border border-cyan-900/40 rounded-lg flex flex-col gap-2 shadow-lg shadow-cyan-950/20">
            <div className="flex items-center justify-between">
              <h2 className="text-sm font-bold text-cyan-200 flex items-center gap-2">
                <span>四大独立乘区理论模型</span>
                <span className="text-xs font-mono font-normal text-zinc-400">
                  {expectedConfig.fourMultiplierFormula}
                </span>
              </h2>
            </div>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-3 pt-2 text-xs">
              <div className="p-2.5 bg-[#141b24] border border-zinc-800 rounded">
                <span className="text-zinc-400">1. 基础面板 (BaseFlat)</span>
                <p className="font-mono text-cyan-300 font-bold mt-1">
                  基础平铺纯数值
                </p>
              </div>
              <div className="p-2.5 bg-[#141b24] border border-zinc-800 rounded">
                <span className="text-zinc-400">2. 灵根乘区 (1 + Root%)</span>
                <p className="font-mono text-emerald-300 font-bold mt-1">
                  {(currentTierData.rootMultiplier?.multiplier || 1).toFixed(2)}x (+{( (currentTierData.rootMultiplier?.rootPercentage || 0) * 100).toFixed(0)}%)
                </p>
              </div>
              <div className="p-2.5 bg-[#141b24] border border-zinc-800 rounded">
                <span className="text-zinc-400">3. 装备乘区 (1 + Equip%)</span>
                <p className="font-mono text-indigo-300 font-bold mt-1">
                  {(currentTierData.equipBaseline?.multiplier || 1).toFixed(2)}x (+{( (currentTierData.equipBaseline?.allStatsEquivalentPercentage || 0) * 100).toFixed(1)}%)
                </p>
              </div>
              <div className="p-2.5 bg-[#141b24] border border-zinc-800 rounded">
                <span className="text-zinc-400">4. 丹药Buff乘区 (1 + Buff%)</span>
                <p className="font-mono text-amber-300 font-bold mt-1">
                  {(currentTierData.buffBaseline?.multiplier || 1).toFixed(4)}x (+{( (currentTierData.buffBaseline?.buffTotalPercentage || 0) * 100).toFixed(2)}%)
                </p>
              </div>
            </div>
          </div>

          {/* 14维基础平铺与最终期望属性表格 */}
          <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-3">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-semibold text-zinc-200">
                T{currentTierData.tier} · {currentTierData.realmName} (Lv.{currentTierData.level}) 属性解算对照表
              </h3>
              <span className="text-xs text-zinc-400 font-mono">
                综合放大系数: <b className="text-cyan-300">{currentTierData.compoundMultiplier?.toFixed(3)}x</b>
              </span>
            </div>

            <div className="overflow-x-auto">
              <table className="w-full text-xs text-left border border-zinc-800">
                <thead className="bg-[#141b24] text-zinc-400 border-b border-zinc-800">
                  <tr>
                    <th className="p-2">属性名称 (Key)</th>
                    <th className="p-2 text-right">基础白字 (BaseFlat)</th>
                    <th className="p-2 text-right">复合放大倍数</th>
                    <th className="p-2 text-right text-cyan-300">最终期望数值 (FinalExpected)</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-zinc-800/60 font-mono">
                  {Object.entries(currentTierData.baseFlat || {}).map(([key, val]) => {
                    const meta = STAT_METADATA_MAP.get(key);
                    const finalVal = currentTierData.finalExpected?.[key] ?? (val * (currentTierData.compoundMultiplier || 1));
                    return (
                      <tr key={key} className="hover:bg-[#141b24]/50">
                        <td className="p-2 font-sans font-medium text-zinc-200">
                          {meta?.label || key} <span className="text-zinc-500 font-mono text-[11px]">({key})</span>
                        </td>
                        <td className="p-2 text-right">
                          <input
                            type="number"
                            step={1}
                            value={val}
                            onChange={e => handleUpdateBaseFlat(key, parseFloat(e.target.value) || 0)}
                            className="w-24 bg-[#0e131a] border border-zinc-700 rounded px-2 py-0.5 text-right text-zinc-100 outline-none"
                          />
                        </td>
                        <td className="p-2 text-right text-zinc-500">
                          {currentTierData.compoundMultiplier?.toFixed(3)}x
                        </td>
                        <td className="p-2 text-right font-bold text-cyan-300">
                          {finalVal.toFixed(2)}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>

          {/* 双流派仿真对比看板 */}
          {currentTierData.combatSimulationDualStyle && (
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {/* 物理系 */}
              <div className="p-4 bg-[#10151c] border border-orange-900/40 rounded-lg flex flex-col gap-3">
                <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
                  <span className="text-xs font-bold text-orange-300">
                    物理流派基准 (1 AD : 1 暴击 : 1 破招)
                  </span>
                  <span className="text-xs font-mono text-zinc-400">
                    DPS: <b className="text-orange-400">{currentTierData.combatSimulationDualStyle.physicalStyle?.totalDps?.toFixed(1)}</b>
                  </span>
                </div>
                <div className="grid grid-cols-2 gap-2 text-xs font-mono">
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">寻常波次耗时:</span>
                    <p className="text-zinc-200 mt-0.5">{currentTierData.combatSimulationDualStyle.physicalStyle?.normalWaveDurationSeconds?.toFixed(1)}s</p>
                  </div>
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">首领Boss耗时:</span>
                    <p className="text-zinc-200 mt-0.5">{currentTierData.combatSimulationDualStyle.physicalStyle?.bossWaveDurationSeconds?.toFixed(1)}s</p>
                  </div>
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">每小时清怪:</span>
                    <p className="text-zinc-200 mt-0.5">{currentTierData.combatSimulationDualStyle.physicalStyle?.hourlyKills?.toFixed(0)} 只</p>
                  </div>
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">24小时挂机斩获:</span>
                    <p className="text-orange-300 font-bold mt-0.5">{currentTierData.combatSimulationDualStyle.physicalStyle?.dailyKills24h?.toFixed(0)} 只</p>
                  </div>
                </div>
              </div>

              {/* 术法系 */}
              <div className="p-4 bg-[#10151c] border border-cyan-900/40 rounded-lg flex flex-col gap-3">
                <div className="flex items-center justify-between border-b border-zinc-800 pb-2">
                  <span className="text-xs font-bold text-cyan-300">
                    术法流派基准 (固定 2.0x AP : 1 命中)
                  </span>
                  <span className="text-xs font-mono text-zinc-400">
                    DPS: <b className="text-cyan-400">{currentTierData.combatSimulationDualStyle.magicStyle?.totalDps?.toFixed(1)}</b>
                  </span>
                </div>
                <div className="grid grid-cols-2 gap-2 text-xs font-mono">
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">寻常波次耗时:</span>
                    <p className="text-zinc-200 mt-0.5">{currentTierData.combatSimulationDualStyle.magicStyle?.normalWaveDurationSeconds?.toFixed(1)}s</p>
                  </div>
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">首领Boss耗时:</span>
                    <p className="text-zinc-200 mt-0.5">{currentTierData.combatSimulationDualStyle.magicStyle?.bossWaveDurationSeconds?.toFixed(1)}s</p>
                  </div>
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">每小时清怪:</span>
                    <p className="text-zinc-200 mt-0.5">{currentTierData.combatSimulationDualStyle.magicStyle?.hourlyKills?.toFixed(0)} 只</p>
                  </div>
                  <div className="p-2 bg-[#141b24] rounded">
                    <span className="text-zinc-500">24小时挂机斩获:</span>
                    <p className="text-cyan-300 font-bold mt-0.5">{currentTierData.combatSimulationDualStyle.magicStyle?.dailyKills24h?.toFixed(0)} 只</p>
                  </div>
                </div>
              </div>
            </div>
          )}
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-zinc-500 text-sm">
          请选择一个阶位查看矩阵
        </div>
      )}
    </div>
  );
}
