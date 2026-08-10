import { useEffect, useState } from 'react'
import { ChoiceMenu } from './CatalogPicker'
import { Modal } from './Modal'
import type { CombatTrigger } from '../game/types'

const MAX_TRIGGERS = 4

const DEPENDENCIES = [
  { value: 'self', label: '自身' },
  { value: 'target-enemy', label: '目标妖兽' },
] as const

const CONDITIONS = [
  { value: 'missing-hp', label: '生命缺口' },
  { value: 'current-hp', label: '当前生命' },
  { value: 'missing-mana', label: '灵力缺口' },
  { value: 'current-mana', label: '当前灵力' },
  { value: 'current-hp-percentage', label: '生命百分比' },
  { value: 'missing-hp-percentage', label: '生命缺口百分比' },
  { value: 'current-mana-percentage', label: '灵力百分比' },
  { value: 'missing-mana-percentage', label: '灵力缺口百分比' },
  { value: 'buff-stacks', label: '增益层数' },
  { value: 'stun-status', label: '定身状态' },
  { value: 'blind-status', label: '致盲状态' },
  { value: 'silence-status', label: '沉默状态' },
] as const

const COMPARATORS = [
  { value: 'greater-than-equal', label: '大于等于' },
  { value: 'less-than-equal', label: '小于等于' },
  { value: 'is-active', label: '已激活' },
  { value: 'is-inactive', label: '未激活' },
] as const

const BUFFS = [
  { value: 'buff.spirit-tea', label: '凝神' },
  { value: 'buff.fire-talisman', label: '赤焰' },
  { value: 'buff.qi-gathering', label: '引气增益' },
]

interface CombatTriggerEditorProps {
  open: boolean
  title: string
  initialTriggers?: CombatTrigger[]
  onClose: () => void
  onSave: (triggers: CombatTrigger[]) => void
}

function cloneTriggers(triggers: CombatTrigger[] | undefined): CombatTrigger[] {
  return (triggers ?? []).map(trigger => ({ ...trigger }))
}

export function CombatTriggerEditor({
  open,
  title,
  initialTriggers,
  onClose,
  onSave,
}: CombatTriggerEditorProps): React.JSX.Element | null {
  const [triggers, setTriggers] = useState<CombatTrigger[]>(() => cloneTriggers(initialTriggers))

  useEffect(() => {
    if (open) setTriggers(cloneTriggers(initialTriggers))
  }, [initialTriggers, open])

  const updateTrigger = (index: number, patch: Partial<CombatTrigger>) => {
    setTriggers(current => current.map((trigger, triggerIndex) => (
      triggerIndex === index ? { ...trigger, ...patch } : trigger
    )))
  }

  return (
    <Modal open={open} labelledBy="combat-trigger-editor-title" onClose={onClose} size="large">
      <div className="combat-trigger-editor">
        <header className="modal-header">
          <h2 id="combat-trigger-editor-title">{title}</h2>
        </header>
        <div className="combat-trigger-list">
          {triggers.map((trigger, index) => (
            <div className="combat-trigger-row" key={`${index}-${trigger.condition}`}>
              <ChoiceMenu
                label={`条件 ${index + 1} 依赖对象`}
                onSelect={value => updateTrigger(index, { dependency: value })}
                options={DEPENDENCIES.map(option => ({ id: option.value, label: option.label }))}
                value={trigger.dependency}
              />
              <ChoiceMenu
                label={`条件 ${index + 1} 条件`}
                onSelect={value => updateTrigger(index, { condition: value })}
                options={CONDITIONS.map(option => ({ id: option.value, label: option.label }))}
                value={trigger.condition}
              />
              <ChoiceMenu
                label={`条件 ${index + 1} 比较方式`}
                onSelect={value => updateTrigger(index, { comparator: value })}
                options={COMPARATORS.map(option => ({ id: option.value, label: option.label }))}
                value={trigger.comparator}
              />
              {trigger.condition === 'buff-stacks' ? (
                <ChoiceMenu
                  label={`条件 ${index + 1} 增益`}
                  onSelect={value => updateTrigger(index, { buffId: value || null })}
                  options={[{ id: '', label: '选择增益' }, ...BUFFS.map(option => ({ id: option.value, label: option.label }))]}
                  value={trigger.buffId ?? ''}
                />
              ) : (
                <input
                  aria-label={`条件 ${index + 1} 数值`}
                  inputMode="decimal"
                  min={0}
                  onChange={event => updateTrigger(index, { value: Number(event.target.value) || 0 })}
                  type="number"
                  value={trigger.value}
                />
              )}
              <button
                aria-label={`删除条件 ${index + 1}`}
                className="combat-trigger-remove"
                onClick={() => setTriggers(current => current.filter((_, triggerIndex) => triggerIndex !== index))}
                title="删除条件"
                type="button"
              >×</button>
            </div>
          ))}
        </div>
        <div className="combat-trigger-actions">
          <button
            disabled={triggers.length >= MAX_TRIGGERS}
            onClick={() => setTriggers(current => [
              ...current,
              {
                dependency: 'self',
                condition: 'missing-hp',
                comparator: 'greater-than-equal',
                value: 1,
              },
            ])}
            type="button"
          >+ 添加条件</button>
          <span>{triggers.length}/{MAX_TRIGGERS}</span>
          <button
            className="primary"
            onClick={() => {
              onSave(triggers.map(trigger => ({ ...trigger, value: Number.isFinite(trigger.value) ? trigger.value : 0 })))
              onClose()
            }}
            type="button"
          >保存条件</button>
        </div>
      </div>
    </Modal>
  )
}
