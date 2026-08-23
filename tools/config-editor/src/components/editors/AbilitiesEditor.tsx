// ==============================================================================
// 文件名称: AbilitiesEditor.tsx
// 主要职责: abilities.json 专属编辑器，支持流派/元素/伤害大类过滤、数值成长与控制机制配置
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { AbilityConfig } from '../../types/config';
import {
  COMBAT_STYLES,
  DAMAGE_CATEGORIES,
  ELEMENTS,
  COMBAT_ABILITY_EFFECT_TYPES,
  COMBAT_ABILITY_TARGET_TYPES,
  TIER_OPTIONS
} from '../../types/enums';
import { EnumSelect } from '../common/EnumSelect';
import { NumberInput } from '../common/NumberInput';
import { CombatTriggerEditor } from '../common/CombatTriggerEditor';

export function AbilitiesEditor() {
  const { configs, updateConfig } = useConfigContext();
  const abilities = configs?.['abilities.json'] || [];

  const [selectedId, setSelectedId] = useState<string>(abilities[0]?.id || '');
  const [filterStyle, setFilterStyle] = useState<string>('all');
  const [filterCategory, setFilterCategory] = useState<string>('all');
  const [searchTerm, setSearchTerm] = useState<string>('');

  // 过滤列表
  const filteredAbilities = useMemo(() => {
    return abilities.filter(ab => {
      if (filterStyle !== 'all' && ab.style !== filterStyle) return false;
      if (filterCategory !== 'all' && ab.category !== filterCategory) return false;
      if (searchTerm.trim()) {
        const term = searchTerm.toLowerCase();
        return ab.id.toLowerCase().includes(term) || ab.name.toLowerCase().includes(term);
      }
      return true;
    });
  }, [abilities, filterStyle, filterCategory, searchTerm]);

  // 当前正在编辑的技能
  const currentAbility = useMemo(() => {
    return abilities.find(a => a.id === selectedId) || filteredAbilities[0];
  }, [abilities, selectedId, filteredAbilities]);

  const handleUpdate = (patch: Partial<AbilityConfig>) => {
    if (!currentAbility) return;
    updateConfig('abilities.json', prev =>
      prev.map(a => (a.id === currentAbility.id ? { ...a, ...patch } : a))
    );
  };

  const handleAdd = () => {
    const newId = `ability_${Date.now().toString(36)}`;
    const newAbility: AbilityConfig = {
      id: newId,
      name: '新神通法诀',
      style: 'fiveElements',
      category: 'magic',
      element: 'none',
      cooldownMilliseconds: 5000,
      castMilliseconds: 1000,
      damageRatio: 1.5,
      effectType: 'damage',
      targetType: 'enemy'
    };
    updateConfig('abilities.json', prev => [newAbility, ...prev]);
    setSelectedId(newId);
  };

  const handleClone = () => {
    if (!currentAbility) return;
    const clonedId = `${currentAbility.id}_copy_${Date.now().toString(36).slice(-4)}`;
    const cloned: AbilityConfig = {
      ...JSON.parse(JSON.stringify(currentAbility)),
      id: clonedId,
      name: `${currentAbility.name} (复制)`
    };
    updateConfig('abilities.json', prev => [cloned, ...prev]);
    setSelectedId(clonedId);
  };

  const handleDelete = () => {
    if (!currentAbility) return;
    if (!confirm(`确定要删除神通技能 [${currentAbility.name}] (${currentAbility.id}) 吗？`)) return;
    updateConfig('abilities.json', prev => prev.filter(a => a.id !== currentAbility.id));
    const remaining = abilities.filter(a => a.id !== currentAbility.id);
    if (remaining.length > 0) setSelectedId(remaining[0].id);
  };

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧列表栏 */}
      <div className="w-80 border-r border-zinc-800 bg-[#0e131a] flex flex-col shrink-0">
        <div className="p-3 border-b border-zinc-800 flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">神通列表 ({filteredAbilities.length})</span>
            <button
              type="button"
              onClick={handleAdd}
              className="px-2 py-0.5 bg-cyan-950 hover:bg-cyan-900 border border-cyan-800 text-cyan-300 text-xs rounded transition-colors"
            >
              + 新增神通
            </button>
          </div>

          <input
            type="text"
            placeholder="搜索名称 / ID..."
            value={searchTerm}
            onChange={e => setSearchTerm(e.target.value)}
            className="w-full bg-[#141b24] border border-zinc-700/80 rounded px-2.5 py-1 text-xs text-zinc-100 placeholder-zinc-500 outline-none"
          />

          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => setFilterStyle('all')}
              className={`px-2 py-0.5 text-[11px] rounded ${
                filterStyle === 'all'
                  ? 'bg-zinc-700 text-zinc-100 font-medium'
                  : 'text-zinc-400 hover:bg-zinc-800'
              }`}
            >
              全部流派
            </button>
            {COMBAT_STYLES.map(s => (
              <button
                key={s.value}
                type="button"
                onClick={() => setFilterStyle(s.value)}
                className={`px-2 py-0.5 text-[11px] rounded ${
                  filterStyle === s.value
                    ? 'bg-indigo-900 text-indigo-200 font-medium'
                    : 'text-zinc-400 hover:bg-zinc-800'
                }`}
              >
                {s.label.split(' ')[0]}
              </button>
            ))}
          </div>

          <div className="flex items-center gap-1.5 pt-1">
            <span className="text-[11px] text-zinc-500">伤害大类:</span>
            <select
              value={filterCategory}
              onChange={e => setFilterCategory(e.target.value)}
              className="flex-1 bg-[#141b24] border border-zinc-700 text-[11px] text-zinc-300 rounded px-2 py-0.5"
            >
              <option value="all">全部大类</option>
              {DAMAGE_CATEGORIES.map(c => (
                <option key={c.value} value={c.value}>
                  {c.label.split(' ')[0]}
                </option>
              ))}
            </select>
          </div>
        </div>

        {/* 列表项 */}
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40">
          {filteredAbilities.map(ab => {
            const isSelected = ab.id === currentAbility?.id;
            const styleOpt = COMBAT_STYLES.find(s => s.value === ab.style);
            return (
              <div
                key={ab.id}
                onClick={() => setSelectedId(ab.id)}
                className={`p-2.5 cursor-pointer flex flex-col gap-1 transition-colors ${
                  isSelected
                    ? 'bg-indigo-950/60 border-l-2 border-indigo-400'
                    : 'hover:bg-[#141b24]'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-medium truncate ${isSelected ? 'text-indigo-200' : 'text-zinc-200'}`}>
                    {ab.name}
                  </span>
                  {styleOpt && (
                    <span className={`px-1.5 py-0.2 text-[10px] rounded border ${styleOpt.badgeColor}`}>
                      {styleOpt.label.split(' ')[0]}
                    </span>
                  )}
                </div>
                <div className="flex items-center justify-between text-[11px] text-zinc-500 font-mono">
                  <span className="truncate">{ab.id}</span>
                  <span>CD: {((ab.cooldownMilliseconds || 0) / 1000).toFixed(1)}s</span>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 右侧详细编辑表单 */}
      {currentAbility ? (
        <div className="flex-1 overflow-y-auto p-6 flex flex-col gap-6 bg-[#0b0f14]">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-3 border-b border-zinc-800">
            <div>
              <h2 className="text-base font-bold text-zinc-100 flex items-center gap-2">
                <span>{currentAbility.name}</span>
                <span className="text-xs text-zinc-500 font-mono font-normal">({currentAbility.id})</span>
              </h2>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={handleClone}
                className="px-2.5 py-1 bg-zinc-800 hover:bg-zinc-700 border border-zinc-700 text-xs text-zinc-200 rounded"
              >
                克隆复制
              </button>
              <button
                type="button"
                onClick={handleDelete}
                className="px-2.5 py-1 bg-red-950/50 hover:bg-red-900/60 border border-red-800 text-xs text-red-300 rounded"
              >
                删除
              </button>
            </div>
          </div>

          {/* 基础类型与流派 */}
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4 p-4 bg-[#10151c] border border-zinc-800 rounded-lg">
            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">唯一标识符 (ID)</label>
              <input
                type="text"
                value={currentAbility.id}
                onChange={e => handleUpdate({ id: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 font-mono text-sm text-zinc-100"
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <label className="text-xs font-medium text-zinc-300">技能名称 (Name)</label>
              <input
                type="text"
                value={currentAbility.name || ''}
                onChange={e => handleUpdate({ name: e.target.value })}
                className="w-full bg-[#121820] border border-zinc-700/80 focus:border-cyan-500 rounded px-3 py-1.5 text-sm text-zinc-100 font-medium"
              />
            </div>

            <EnumSelect
              label="道统流派 (Style)"
              value={currentAbility.style}
              options={COMBAT_STYLES}
              onChange={v => handleUpdate({ style: v || 'fiveElements' })}
              allowClear={false}
            />

            <EnumSelect
              label="伤害大类 (Category)"
              value={currentAbility.category}
              options={DAMAGE_CATEGORIES}
              onChange={v => handleUpdate({ category: v || 'magic' })}
              allowClear={false}
            />

            <EnumSelect
              label="五行属性 (Element)"
              value={currentAbility.element}
              options={ELEMENTS}
              onChange={v => handleUpdate({ element: v || 'none' })}
              allowClear={false}
            />

            <EnumSelect
              label="技能阶位设计 (Tier)"
              value={currentAbility.tier}
              options={TIER_OPTIONS}
              onChange={v => handleUpdate({ tier: v })}
            />

            <EnumSelect
              label="效果类型 (EffectType)"
              value={currentAbility.effectType}
              options={COMBAT_ABILITY_EFFECT_TYPES}
              onChange={v => handleUpdate({ effectType: v })}
            />

            <EnumSelect
              label="目标类型 (TargetType)"
              value={currentAbility.targetType}
              options={COMBAT_ABILITY_TARGET_TYPES}
              onChange={v => handleUpdate({ targetType: v })}
            />
          </div>

          {/* 施法、冷却与真元消耗 */}
          <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-4">
            <h3 className="text-sm font-semibold text-zinc-200">施法节奏与灵力消耗</h3>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <NumberInput
                label="施法前摇/吟唱 (CastMilliseconds)"
                description="受角色动作加速(ATR)缩减"
                value={currentAbility.castMilliseconds}
                onChange={v => handleUpdate({ castMilliseconds: v })}
                step={100}
                min={0}
                unit="ms"
              />

              <NumberInput
                label="技能冷却时间 (CooldownMilliseconds)"
                description="受角色冷却缩减(CDR)缩减"
                value={currentAbility.cooldownMilliseconds}
                onChange={v => handleUpdate({ cooldownMilliseconds: v })}
                step={500}
                min={0}
                unit="ms"
              />

              <NumberInput
                label="灵力消耗系数 (ManaCostRatio)"
                description="1.0 = 阶位基准最大灵力的 1%"
                value={currentAbility.manaCostRatio}
                onChange={v => handleUpdate({ manaCostRatio: v })}
                step={0.5}
                unit="×"
              />

              <NumberInput
                label="释放血线阈值 (TriggerHpRatio)"
                description="低于此生命比例才自动释放"
                value={currentAbility.triggerHpRatio}
                onChange={v => handleUpdate({ triggerHpRatio: v })}
                step={0.05}
                min={0}
                max={1}
                unit="×"
              />
            </div>
          </div>

          {/* 直接伤害与治疗 */}
          <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-4">
            <h3 className="text-sm font-semibold text-zinc-200">直接伤害与治疗加成</h3>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <NumberInput
                label="伤害倍率 (DamageRatio)"
                description="1.0 为 100% 攻击力"
                value={currentAbility.damageRatio}
                onChange={v => handleUpdate({ damageRatio: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="固定伤害 (FlatDamage)"
                value={currentAbility.flatDamage}
                onChange={v => handleUpdate({ flatDamage: v })}
                min={0}
              />

              <NumberInput
                label="每级伤害倍率成长"
                value={currentAbility.damageRatioLevelBonus}
                onChange={v => handleUpdate({ damageRatioLevelBonus: v })}
                step={0.01}
                unit="×"
              />

              <NumberInput
                label="每级固定伤害成长"
                value={currentAbility.flatDamageLevelBonus}
                onChange={v => handleUpdate({ flatDamageLevelBonus: v })}
                min={0}
              />

              <NumberInput
                label="治疗倍率 (HealingRatio)"
                value={currentAbility.healingRatio}
                onChange={v => handleUpdate({ healingRatio: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="固定治疗点数 (FlatHealing)"
                value={currentAbility.flatHealing}
                onChange={v => handleUpdate({ flatHealing: v })}
                min={0}
              />

              <NumberInput
                label="每级治疗倍率成长"
                value={currentAbility.healingRatioLevelBonus}
                onChange={v => handleUpdate({ healingRatioLevelBonus: v })}
                step={0.01}
                unit="×"
              />

              <NumberInput
                label="每级固定治疗成长"
                value={currentAbility.flatHealingLevelBonus}
                onChange={v => handleUpdate({ flatHealingLevelBonus: v })}
                min={0}
              />
            </div>
          </div>

          {/* 持续伤害 (Dot) 与控制异常机制 */}
          <div className="p-4 bg-[#10151c] border border-zinc-800 rounded-lg flex flex-col gap-4">
            <h3 className="text-sm font-semibold text-zinc-200">持续伤害 (Dot) 与控制/特殊异常</h3>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <NumberInput
                label="Dot 持续伤害倍率"
                value={currentAbility.damageOverTimeRatio}
                onChange={v => handleUpdate({ damageOverTimeRatio: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="Dot 持续时长 (ms)"
                value={currentAbility.damageOverTimeMilliseconds}
                onChange={v => handleUpdate({ damageOverTimeMilliseconds: v })}
                step={500}
                unit="ms"
              />

              <NumberInput
                label="Dot 触发跳字间隔 (ms)"
                value={currentAbility.damageOverTimeTickMilliseconds}
                onChange={v => handleUpdate({ damageOverTimeTickMilliseconds: v })}
                step={500}
                unit="ms"
              />

              <NumberInput
                label="眩晕几率 (StunChance)"
                value={currentAbility.stunChance}
                onChange={v => handleUpdate({ stunChance: v })}
                step={0.05}
                min={0}
                max={1}
                unit="×"
              />

              <NumberInput
                label="眩晕时长 (StunMilliseconds)"
                value={currentAbility.stunMilliseconds}
                onChange={v => handleUpdate({ stunMilliseconds: v })}
                step={500}
                unit="ms"
              />

              <NumberInput
                label="致盲几率 (BlindChance)"
                value={currentAbility.blindChance}
                onChange={v => handleUpdate({ blindChance: v })}
                step={0.05}
                min={0}
                max={1}
                unit="×"
              />

              <NumberInput
                label="致盲时长 (BlindMilliseconds)"
                value={currentAbility.blindMilliseconds}
                onChange={v => handleUpdate({ blindMilliseconds: v })}
                step={500}
                unit="ms"
              />

              <NumberInput
                label="沉默几率 (SilenceChance)"
                value={currentAbility.silenceChance}
                onChange={v => handleUpdate({ silenceChance: v })}
                step={0.05}
                min={0}
                max={1}
                unit="×"
              />

              <NumberInput
                label="沉默时长 (SilenceMilliseconds)"
                value={currentAbility.silenceMilliseconds}
                onChange={v => handleUpdate({ silenceMilliseconds: v })}
                step={500}
                unit="ms"
              />

              <NumberInput
                label="穿甲削防比例 (PenetrationRatio)"
                value={currentAbility.penetrationRatio}
                onChange={v => handleUpdate({ penetrationRatio: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="气血汲取比例 (HpDrainRatio)"
                value={currentAbility.hpDrainRatio}
                onChange={v => handleUpdate({ hpDrainRatio: v })}
                step={0.05}
                unit="×"
              />

              <NumberInput
                label="贯穿概率 (PierceChance)"
                value={currentAbility.pierceChance}
                onChange={v => handleUpdate({ pierceChance: v })}
                step={0.05}
                min={0}
                max={1}
                unit="×"
              />
            </div>
          </div>

          {/* 默认触发策略 */}
          <CombatTriggerEditor
            label="神通默认释放触发策略 (DefaultTriggers)"
            description="战斗中该技能的自动释放策略"
            triggers={currentAbility.defaultTriggers}
            onChange={triggers => handleUpdate({ defaultTriggers: triggers })}
          />
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center text-zinc-500 text-sm">
          请在左侧选择或新增一门神通
        </div>
      )}
    </div>
  );
}
