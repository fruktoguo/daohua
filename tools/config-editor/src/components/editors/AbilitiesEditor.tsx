// ==============================================================================
// 文件名称: AbilitiesEditor.tsx
// 主要职责: 现代扁平化 abilities 专属编辑器，字段与 CombatSkillEngine.ValidateAbility /
//           CombatModels.CastSkill / LoadoutCommand / SimulationEngine 真实消费集对齐
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';
import type { AbilityConfig, AbilityBuffConfig } from '../../types/config';
import {
  COMBAT_STYLES,
  DAMAGE_CATEGORIES,
  ELEMENTS,
  COMBAT_ABILITY_EFFECT_TYPES,
  COMBAT_ABILITY_TARGET_TYPES,
  ABILITY_DISCIPLINES,
  ROOT_ATTRIBUTES,
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

  const handleUpdateBuff = (patch: Partial<AbilityBuffConfig> | null) => {
    if (!currentAbility) return;
    if (patch === null) {
      handleUpdate({ buff: undefined });
      return;
    }
    handleUpdate({ buff: { ...(currentAbility.buff || { durationSeconds: 30 }), ...patch } });
  };

  const handleAdd = () => {
    const newId = `ability.new-${Date.now().toString(36)}`;
    const newAbility: AbilityConfig = {
      id: newId,
      name: '新神通法诀',
      description: '',
      style: 'fiveElements',
      category: 'spell',
      element: 'none',
      discipline: 'spell',
      rootAttribute: 'meridians',
      cooldownMilliseconds: 5000,
      castMilliseconds: 1000,
      manaCost: 10,
      targetType: 'targetEnemy',
      damageRatio: 1.0,
      flatDamage: 0,
      defaultTriggers: [
        { dependency: 'target-enemy', condition: 'current-hp', comparator: 'greater-than-equal', value: 1, buffId: null }
      ]
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
      name: `${currentAbility.name} (副本)`
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

  const isHeal = currentAbility?.effectType === 'heal';
  const isSupport = currentAbility
    ? currentAbility.targetType === 'self' || currentAbility.targetType === 'allAllies' || currentAbility.effectType === 'buff'
    : false;

  return (
    <div className="flex-1 flex overflow-hidden h-full">
      {/* 左侧列表栏 */}
      <div className="w-80 border-r border-zinc-800/80 bg-zinc-950 flex flex-col shrink-0 select-none">
        <div className="p-3.5 border-b border-zinc-800/80 flex flex-col gap-2.5">
          <div className="flex items-center justify-between">
            <span className="text-xs font-bold text-zinc-300">神通列表 ({filteredAbilities.length})</span>
            <button
              type="button"
              onClick={handleAdd}
              className="px-2.5 py-1 bg-indigo-600 hover:bg-indigo-500 text-white font-medium text-xs rounded-lg transition-colors shadow-sm"
            >
              + 新增
            </button>
          </div>

          <div className="relative">
            <input
              type="text"
              placeholder="搜索名称 / ID..."
              value={searchTerm}
              onChange={e => setSearchTerm(e.target.value)}
              className="w-full bg-zinc-900 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-100 placeholder-zinc-500 outline-none transition-all"
            />
          </div>

          <div className="flex flex-wrap gap-1">
            <button
              type="button"
              onClick={() => setFilterStyle('all')}
              className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                filterStyle === 'all'
                  ? 'bg-zinc-800 text-zinc-100'
                  : 'text-zinc-500 hover:text-zinc-300'
              }`}
            >
              全流派
            </button>
            {COMBAT_STYLES.map(s => (
              <button
                key={s.value}
                type="button"
                onClick={() => setFilterStyle(s.value)}
                className={`px-2 py-0.5 text-[11px] font-medium rounded-md transition-all ${
                  filterStyle === s.value
                    ? 'bg-indigo-500/20 text-indigo-300 border border-indigo-500/30'
                    : 'text-zinc-500 hover:text-zinc-300'
                }`}
              >
                {s.label.split(' ')[0]}
              </button>
            ))}
          </div>

          <div className="flex items-center gap-2 pt-0.5">
            <span className="text-[11px] text-zinc-500 font-medium shrink-0">伤害大类:</span>
            <select
              value={filterCategory}
              onChange={e => setFilterCategory(e.target.value)}
              className="w-full bg-zinc-900 border border-zinc-800 text-[11px] text-zinc-300 rounded-md px-2 py-1 outline-none focus:border-indigo-500"
            >
              <option value="all">全部大类 (物理 / 术法)</option>
              {DAMAGE_CATEGORIES.map(c => (
                <option key={c.value} value={c.value}>
                  {c.label.split(' ')[0]}
                </option>
              ))}
            </select>
          </div>
        </div>

        {/* 列表项 */}
        <div className="flex-1 overflow-y-auto divide-y divide-zinc-800/40 p-1.5">
          {filteredAbilities.map(ab => {
            const isSelected = ab.id === currentAbility?.id;
            const styleOpt = COMBAT_STYLES.find(s => s.value === ab.style);
            return (
              <div
                key={ab.id}
                onClick={() => setSelectedId(ab.id)}
                className={`p-3 rounded-xl cursor-pointer flex flex-col gap-1 transition-all duration-150 ${
                  isSelected
                    ? 'bg-zinc-900 border border-zinc-700/80 shadow-sm'
                    : 'hover:bg-zinc-900/50 border border-transparent'
                }`}
              >
                <div className="flex items-center justify-between">
                  <span className={`text-xs font-semibold truncate ${isSelected ? 'text-white' : 'text-zinc-200'}`}>
                    {ab.name}
                  </span>
                  {styleOpt && (
                    <span className={`px-2 py-0.5 text-[10px] font-medium rounded-full border ${styleOpt.badgeColor}`}>
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
        <div className="flex-1 overflow-y-auto p-8 flex flex-col gap-6 bg-zinc-950">
          {/* 顶部操作 */}
          <div className="flex items-center justify-between pb-4 border-b border-zinc-800/80">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-zinc-100 tracking-tight">{currentAbility.name}</h2>
              <span className="text-xs text-zinc-500 font-mono bg-zinc-900 px-2.5 py-0.5 rounded-md border border-zinc-800">
                {currentAbility.id}
              </span>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={handleClone}
                className="px-3 py-1.5 bg-zinc-900 hover:bg-zinc-800 border border-zinc-800 text-xs font-medium text-zinc-300 rounded-lg transition-colors"
              >
                克隆复制
              </button>
              <button
                type="button"
                onClick={handleDelete}
                className="px-3 py-1.5 bg-rose-500/10 hover:bg-rose-500/20 border border-rose-500/30 text-xs font-medium text-rose-300 rounded-lg transition-colors"
              >
                删除
              </button>
            </div>
          </div>

          {/* 基础类型与流派卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">道统与分类</h3>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">唯一标识符 (ID)</label>
                <input
                  type="text"
                  value={currentAbility.id}
                  onChange={e => handleUpdate({ id: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-sm text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">神通名称 (Name)</label>
                <input
                  type="text"
                  value={currentAbility.name || ''}
                  onChange={e => handleUpdate({ name: e.target.value })}
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm font-semibold text-zinc-100 outline-none"
                />
              </div>

              <div className="flex flex-col gap-1.5">
                <label className="text-xs font-medium text-zinc-300 tracking-tight">招式描述 (Description)</label>
                <input
                  type="text"
                  value={currentAbility.description || ''}
                  onChange={e => handleUpdate({ description: e.target.value })}
                  placeholder="如：从掌心发出一道雷光"
                  className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-sm text-zinc-100 outline-none"
                />
              </div>

              <EnumSelect
                label="道统流派 (Style)"
                value={currentAbility.style}
                options={COMBAT_STYLES}
                onChange={v => handleUpdate({ style: v })}
              />

              <EnumSelect
                label="伤害大类 (Category)"
                description="引擎只接受 physical / spell"
                value={currentAbility.category}
                options={DAMAGE_CATEGORIES}
                onChange={v => handleUpdate({ category: v || 'spell' })}
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
                label="功法类别 (Discipline)"
                description="internal=内功心法(0号槽) / 缺省按术法"
                value={currentAbility.discipline}
                options={ABILITY_DISCIPLINES}
                onChange={v => handleUpdate({ discipline: v })}
                clearLabel="— 缺省(术法) —"
              />

              <EnumSelect
                label="根骨维度 (RootAttribute)"
                description="决定该功法分摊的六维经验"
                value={currentAbility.rootAttribute}
                options={ROOT_ATTRIBUTES}
                onChange={v => handleUpdate({ rootAttribute: v })}
                clearLabel="— 全修(六维均分) —"
              />

              <EnumSelect
                label="阶位设计 (Tier)"
                value={currentAbility.tier}
                options={TIER_OPTIONS}
                onChange={v => handleUpdate({ tier: v })}
              />

              <EnumSelect
                label="效果类型 (EffectType)"
                description="缺省为伤害；heal=治疗 buff=友方增益"
                value={currentAbility.effectType}
                options={COMBAT_ABILITY_EFFECT_TYPES}
                onChange={v => handleUpdate({ effectType: v === 'damage' ? undefined : v })}
                clearLabel="— 伤害 (缺省) —"
              />

              <EnumSelect
                label="目标类型 (TargetType)"
                description="缺省按单体敌方"
                value={currentAbility.targetType}
                options={COMBAT_ABILITY_TARGET_TYPES}
                onChange={v => handleUpdate({ targetType: v })}
                clearLabel="— 单体敌方 (缺省) —"
              />

              <NumberInput
                label="目标上限 (MaxTargets)"
                description="仅 allEnemies 生效，0/空为不限"
                value={currentAbility.maxTargets}
                onChange={v => handleUpdate({ maxTargets: v })}
                min={0}
              />
            </div>
          </div>

          {/* 施法、冷却与灵力消耗卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">施法节奏与灵力消耗</h3>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <NumberInput
                label="施法吟唱 (CastMilliseconds)"
                description="受施放方行动速度乘区缩减"
                value={currentAbility.castMilliseconds}
                onChange={v => handleUpdate({ castMilliseconds: v })}
                step={100}
                min={0}
                unit="ms"
              />

              <NumberInput
                label="冷却时间 (CooldownMilliseconds)"
                description="受施放方冷却乘区缩减"
                value={currentAbility.cooldownMilliseconds}
                onChange={v => handleUpdate({ cooldownMilliseconds: v })}
                step={500}
                min={0}
                unit="ms"
              />

              <NumberInput
                label="灵力消耗 (ManaCost)"
                description="起手预扣，不足则跳过"
                value={currentAbility.manaCost}
                onChange={v => handleUpdate({ manaCost: v })}
                step={1}
                min={0}
                unit="MP"
              />
            </div>
          </div>

          {/* 直接伤害与治疗卡片 */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">直接伤害与治疗加成</h3>
            <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
              <NumberInput
                label="伤害倍率 (DamageRatio)"
                description="1.0 为 100% 攻伤"
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
                label="治疗倍率 (HealingRatio)"
                description="仅 effectType=heal 生效"
                value={currentAbility.healingRatio}
                onChange={v => handleUpdate({ healingRatio: v })}
                step={0.05}
                unit="×"
                disabled={!isHeal}
              />

              <NumberInput
                label="固定治疗点数 (FlatHealing)"
                description="仅 effectType=heal 生效"
                value={currentAbility.flatHealing}
                onChange={v => handleUpdate({ flatHealing: v })}
                min={0}
                disabled={!isHeal}
              />
            </div>
          </div>

          {/* 支援效果：威胁值与战内 Buff */}
          <div className="p-5 bg-zinc-900/40 border border-zinc-800/80 rounded-2xl flex flex-col gap-4">
            <div className="flex items-center justify-between">
              <h3 className="text-xs font-semibold text-zinc-300 uppercase tracking-wider">支援效果（嘲讽 / 增益）</h3>
              {isSupport && (
                <span className="text-[11px] text-emerald-400/80">当前目标类型会使该卡生效</span>
              )}
            </div>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <NumberInput
                label="威胁值增量 (ThreatGain)"
                description="结算时直接增加威胁"
                value={currentAbility.threatGain}
                onChange={v => handleUpdate({ threatGain: v })}
                step={10}
                min={0}
              />

              <NumberInput
                label="威胁增幅点数 (ThreatPoints)"
                description="叠加施放方威胁增幅（100 点 = +100%）"
                value={currentAbility.threatPoints}
                onChange={v => handleUpdate({ threatPoints: v })}
                step={10}
                min={0}
                unit="点"
              />
            </div>

            {/* Buff 增益块 */}
            <div className="pt-3 border-t border-zinc-800/80 flex flex-col gap-3">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-zinc-300">战内 Buff 块 (Buff)</span>
                {currentAbility.buff ? (
                  <button
                    type="button"
                    onClick={() => handleUpdateBuff(null)}
                    className="text-xs text-rose-400 hover:text-rose-300 font-medium"
                  >
                    移除 Buff 块
                  </button>
                ) : (
                  <button
                    type="button"
                    onClick={() => handleUpdateBuff({ durationSeconds: 30 })}
                    className="px-2.5 py-1 bg-emerald-600 hover:bg-emerald-500 text-white text-xs font-medium rounded-lg transition-colors"
                  >
                    + 配置 Buff
                  </button>
                )}
              </div>

              {currentAbility.buff && (
                <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                  <div className="flex flex-col gap-1.5">
                    <label className="text-xs font-medium text-zinc-300 tracking-tight">Buff ID（缺省 ability-buff:技能ID）</label>
                    <input
                      type="text"
                      value={currentAbility.buff.buffId || ''}
                      onChange={e => handleUpdateBuff({ buffId: e.target.value || undefined })}
                      className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 font-mono text-xs text-zinc-100 outline-none"
                    />
                  </div>

                  <div className="flex flex-col gap-1.5">
                    <label className="text-xs font-medium text-zinc-300 tracking-tight">Buff 名称</label>
                    <input
                      type="text"
                      value={currentAbility.buff.name || ''}
                      onChange={e => handleUpdateBuff({ name: e.target.value || undefined })}
                      className="w-full bg-zinc-900/90 border border-zinc-800 focus:border-indigo-500 rounded-lg px-3 py-1.5 text-xs text-zinc-100 outline-none"
                    />
                  </div>

                  <NumberInput
                    label="持续时长 (DurationSeconds)"
                    value={currentAbility.buff.durationSeconds}
                    onChange={v => handleUpdateBuff({ durationSeconds: v })}
                    min={0}
                    unit="秒"
                  />

                  <NumberInput
                    label="气血回复 (HpRegen)"
                    value={currentAbility.buff.hpRegen}
                    onChange={v => handleUpdateBuff({ hpRegen: v })}
                    step={0.5}
                  />

                  <NumberInput
                    label="灵力回复 (MpRegen)"
                    value={currentAbility.buff.mpRegen}
                    onChange={v => handleUpdateBuff({ mpRegen: v })}
                    step={0.5}
                  />

                  <NumberInput
                    label="攻击速度点数 (AttackSpeedPoints)"
                    value={currentAbility.buff.attackSpeedPoints}
                    onChange={v => handleUpdateBuff({ attackSpeedPoints: v })}
                    step={1}
                    unit="点"
                  />

                  <NumberInput
                    label="免伤点数 (DamageAbsorptionPoints)"
                    value={currentAbility.buff.damageAbsorptionPoints}
                    onChange={v => handleUpdateBuff({ damageAbsorptionPoints: v })}
                    step={1}
                    unit="点"
                  />

                  <NumberInput
                    label="威胁增幅点数 (Buff.ThreatPoints)"
                    description="按点数叠加全体友方威胁增幅（100 点 = +100%）"
                    value={currentAbility.buff.threatPoints}
                    onChange={v => handleUpdateBuff({ threatPoints: v })}
                    step={10}
                    unit="点"
                  />
                </div>
              )}
            </div>
          </div>

          {/* 默认触发条件 */}
          <CombatTriggerEditor
            label="神通默认触发条件 (DefaultTriggers)"
            description="装配槽未覆写时使用；全部条件满足才自动施放"
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
