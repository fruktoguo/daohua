// ==============================================================================
// 文件名称: ReferenceSelect.tsx
// 主要职责: 跨表外键 ID 引用下拉选择器，支持搜索、品阶/流派提示与多选 Tag 模式
// ==============================================================================

import { useState, useMemo } from 'react';
import { useConfigContext } from '../../context/ConfigContext';

export type ReferenceKind = 'item' | 'ability' | 'monster' | 'encounter' | 'action' | 'node';

interface ReferenceSelectProps {
  kind: ReferenceKind;
  label?: string;
  description?: string;
  value?: string | null;
  onChange: (id: string | undefined) => void;
  allowClear?: boolean;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
}

export function ReferenceSelect({
  kind,
  label,
  description,
  value,
  onChange,
  allowClear = true,
  placeholder = '— 请选择关联条目 —',
  className = '',
  disabled = false
}: ReferenceSelectProps) {
  const { lookups } = useConfigContext();
  const [searchTerm, setSearchTerm] = useState('');
  const [isOpen, setIsOpen] = useState(false);

  // 获取选项列表
  const options = useMemo(() => {
    switch (kind) {
      case 'item':
        return lookups.itemList.map(i => ({
          id: i.id,
          title: i.name,
          subtitle: `T${i.tier || 1} · ${i.category || i.kind || '物品'}`,
          badge: i.kind
        }));
      case 'ability':
        return lookups.abilityList.map(a => ({
          id: a.id,
          title: a.name,
          subtitle: `${a.style || '五行'} · ${a.category || '物理'} · ${a.element || '无'}`,
          badge: a.style
        }));
      case 'monster':
        return lookups.monsterList.map(m => ({
          id: m.id,
          title: m.name,
          subtitle: `Lv.${m.level || 1} · T${m.tier || 1} · ${m.rank || '普通'}`,
          badge: m.rank
        }));
      case 'encounter':
        return lookups.encounterList.map(e => ({
          id: e.id,
          title: e.name,
          subtitle: `副本遭遇 · ${e.bossCount ? `包含 ${e.bossCount} 位首领` : '普通怪物群'}`,
          badge: '遭遇'
        }));
      case 'action':
        return lookups.actionList.map(a => ({
          id: a.id,
          title: a.name,
          subtitle: `行动类型: ${a.type || '常规'}`,
          badge: a.type
        }));
      case 'node':
        return lookups.nodeList.map(n => ({
          id: n.id,
          title: n.name,
          subtitle: `${n.mapName} · (${n.x}, ${n.y})`,
          badge: n.mapId
        }));
      default:
        return [];
    }
  }, [kind, lookups]);

  // 当前选中的展示信息
  const currentOption = useMemo(() => {
    if (!value) return null;
    return options.find(o => o.id === value);
  }, [value, options]);

  // 过滤后的选项
  const filteredOptions = useMemo(() => {
    if (!searchTerm.trim()) return options;
    const term = searchTerm.toLowerCase();
    return options.filter(
      o => o.id.toLowerCase().includes(term) || o.title.toLowerCase().includes(term) || o.subtitle.toLowerCase().includes(term)
    );
  }, [options, searchTerm]);

  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300 flex items-center gap-1.5">
            {label}
            {value && !currentOption && (
              <span className="px-1.5 py-0.2 text-[10px] rounded bg-red-950 text-red-300 border border-red-800">
                未找到 ID: {value}
              </span>
            )}
          </label>
          {description && <span className="text-[11px] text-zinc-500">{description}</span>}
        </div>
      )}

      <div className="relative">
        <div
          onClick={() => !disabled && setIsOpen(!isOpen)}
          className={`w-full min-h-[34px] px-3 py-1.5 bg-[#121820] border rounded flex items-center justify-between cursor-pointer text-sm transition-colors ${
            isOpen
              ? 'border-cyan-500 ring-1 ring-cyan-500/30'
              : !currentOption && value
              ? 'border-red-600/80 bg-red-950/20'
              : 'border-zinc-700/80 hover:border-zinc-500'
          } ${disabled ? 'opacity-50 cursor-not-allowed' : ''}`}
        >
          {currentOption ? (
            <div className="flex items-center gap-2 truncate">
              <span className="font-medium text-zinc-100">{currentOption.title}</span>
              <span className="text-xs text-zinc-400 font-mono">({currentOption.id})</span>
              <span className="text-[11px] text-zinc-500 hidden sm:inline">{currentOption.subtitle}</span>
            </div>
          ) : value ? (
            <span className="text-red-300 font-mono text-xs truncate">无效引用: {value}</span>
          ) : (
            <span className="text-zinc-500 text-xs">{placeholder}</span>
          )}

          <div className="flex items-center gap-1.5 text-zinc-400 shrink-0">
            {allowClear && value && !disabled && (
              <button
                type="button"
                onClick={e => {
                  e.stopPropagation();
                  onChange(undefined);
                }}
                className="hover:text-red-400 p-0.5"
              >
                ×
              </button>
            )}
            <svg className={`w-3.5 h-3.5 transition-transform ${isOpen ? 'rotate-180' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 9l-7 7-7-7" />
            </svg>
          </div>
        </div>

        {/* 弹出下拉搜索面板 */}
        {isOpen && (
          <>
            <div className="fixed inset-0 z-40" onClick={() => setIsOpen(false)} />
            <div className="absolute z-50 left-0 right-0 top-full mt-1 bg-[#141b24] border border-zinc-700 rounded-lg shadow-2xl overflow-hidden max-h-72 flex flex-col">
              <div className="p-2 border-b border-zinc-800 bg-[#0e131a]">
                <input
                  type="text"
                  autoFocus
                  placeholder={`搜索名称、ID、分类 (共 ${options.length} 项)...`}
                  value={searchTerm}
                  onChange={e => setSearchTerm(e.target.value)}
                  className="w-full bg-[#18202c] border border-zinc-700 focus:border-cyan-500 rounded px-2.5 py-1 text-xs text-zinc-100 outline-none placeholder-zinc-500"
                />
              </div>
              <div className="overflow-y-auto divide-y divide-zinc-800/60 flex-1">
                {allowClear && (
                  <div
                    onClick={() => {
                      onChange(undefined);
                      setIsOpen(false);
                    }}
                    className="p-2 hover:bg-zinc-800/80 cursor-pointer text-xs text-zinc-400 flex items-center justify-between"
                  >
                    <span>— 清除选择 (置空) —</span>
                  </div>
                )}
                {filteredOptions.length === 0 ? (
                  <div className="p-4 text-center text-xs text-zinc-500">无匹配结果</div>
                ) : (
                  filteredOptions.map(opt => (
                    <div
                      key={opt.id}
                      onClick={() => {
                        onChange(opt.id);
                        setIsOpen(false);
                      }}
                      className={`p-2 hover:bg-cyan-950/40 cursor-pointer flex items-center justify-between text-xs transition-colors ${
                        opt.id === value ? 'bg-cyan-950/70 border-l-2 border-cyan-400' : ''
                      }`}
                    >
                      <div className="flex flex-col gap-0.5 truncate">
                        <div className="flex items-center gap-2">
                          <span className="font-medium text-zinc-100">{opt.title}</span>
                          <span className="font-mono text-[11px] text-zinc-400">({opt.id})</span>
                        </div>
                        <span className="text-[11px] text-zinc-500">{opt.subtitle}</span>
                      </div>
                      {opt.badge && (
                        <span className="px-1.5 py-0.5 text-[10px] rounded bg-zinc-800 border border-zinc-700 text-zinc-300 shrink-0 ml-2">
                          {opt.badge}
                        </span>
                      )}
                    </div>
                  ))
                )}
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

interface MultiReferenceSelectProps {
  kind: ReferenceKind;
  label?: string;
  description?: string;
  values?: string[];
  onChange: (ids: string[]) => void;
  placeholder?: string;
  className?: string;
}

export function MultiReferenceSelect({
  kind,
  label,
  description,
  values = [],
  onChange,
  placeholder = '+ 添加关联条目...',
  className = ''
}: MultiReferenceSelectProps) {
  const { lookups } = useConfigContext();
  const currentSet = new Set(values || []);

  const add = (id: string | undefined) => {
    if (id && !currentSet.has(id)) {
      onChange([...(values || []), id]);
    }
  };

  const remove = (id: string) => {
    onChange((values || []).filter(v => v !== id));
  };

  const getName = (id: string) => {
    switch (kind) {
      case 'item': return lookups.items.get(id)?.name || id;
      case 'ability': return lookups.abilities.get(id)?.name || id;
      case 'monster': return lookups.monsters.get(id)?.name || id;
      case 'encounter': return lookups.encounters.get(id)?.name || id;
      case 'action': return lookups.actions.get(id)?.name || id;
      case 'node': return lookups.nodes.get(id)?.name || id;
      default: return id;
    }
  };

  return (
    <div className={`flex flex-col gap-1.5 ${className}`}>
      {label && (
        <div className="flex items-center justify-between">
          <label className="text-xs font-medium text-zinc-300">{label} ({values.length})</label>
          {description && <span className="text-[11px] text-zinc-500">{description}</span>}
        </div>
      )}

      <div className="p-2 bg-[#121820] border border-zinc-700/80 rounded flex flex-col gap-2">
        <div className="flex flex-wrap gap-1.5 min-h-[28px] items-center">
          {values.length === 0 ? (
            <span className="text-xs text-zinc-500 italic">暂无关联项</span>
          ) : (
            values.map(id => (
              <span
                key={id}
                className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded bg-zinc-800 border border-zinc-700 text-xs text-zinc-200"
              >
                <span className="font-medium">{getName(id)}</span>
                <span className="font-mono text-[10px] text-zinc-400">({id})</span>
                <button
                  type="button"
                  onClick={() => remove(id)}
                  className="hover:text-red-400 ml-0.5 text-zinc-400"
                >
                  ×
                </button>
              </span>
            ))
          )}
        </div>

        <div className="pt-1 border-t border-zinc-800">
          <ReferenceSelect
            kind={kind}
            value={null}
            onChange={add}
            placeholder={placeholder}
            allowClear={false}
          />
        </div>
      </div>
    </div>
  );
}
