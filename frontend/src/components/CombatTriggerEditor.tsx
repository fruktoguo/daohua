import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { FloatingPopover } from './FloatingPopover'
import { Modal } from './Modal'
import { describeCombatTrigger, formatNumber } from '../game/format'
import type { CombatTrigger, CombatTriggerDependency, CombatTriggerCondition, CombatTriggerComparator } from '../game/types'
import { useT } from '../i18n'

/** 每个槽位最多 4 条触发条件，与服务端 CombatLimits.TriggerCountPerSlot 保持一致。 */
const MAX_TRIGGERS = 4

/**
 * 触发条件下拉选项定义
 */
interface TriggerSelectOption {
  id: string
  label: string
  subtitle?: string
  disabled?: boolean
}

/**
 * 触发条件原位微标签下拉选择框
 *
 * 采用微标签结构（上方 9px 语义标题，下方 12px 粗体当前值 + 翻转微光小箭头），
 * 搭配 FloatingPopover 原位悬浮浮层，取代臃肿正方形与全屏二级 Modal，
 * 极大提升信息密度与交互流畅感。
 */
function TriggerSelectField({
  label,
  value,
  options,
  onSelect,
  className = '',
}: {
  label: string
  value: string
  options: readonly TriggerSelectOption[]
  onSelect: (value: string) => void
  className?: string
}): React.JSX.Element {
  const [open, setOpen] = useState(false)
  const selected = options.find(option => option.id === value)

  return (
    <div className={`combat-trigger-field ${className}`.trim()}>
      <FloatingPopover
        open={open}
        onOpenChange={setOpen}
        trigger="click"
        placement="bottom-start"
        gap={4}
        displayContents
        className="combat-trigger-popover"
        interactive
        closeOnContentClick
        closeOnEsc
        content={
          <div className="combat-trigger-dropdown" role="listbox" aria-label={label}>
            <div className="combat-trigger-dropdown-title">{label}</div>
            <div className="combat-trigger-dropdown-options">
              {options.map(option => (
                <button
                  key={option.id}
                  type="button"
                  role="option"
                  aria-selected={option.id === value}
                  disabled={option.disabled}
                  className={`combat-trigger-dropdown-item${option.id === value ? ' is-selected' : ''}`}
                  onClick={() => {
                    onSelect(option.id)
                    setOpen(false)
                  }}
                >
                  <span className="combat-trigger-item-label">{option.label}</span>
                  {option.subtitle && (
                    <span className="combat-trigger-item-sub">{option.subtitle}</span>
                  )}
                </button>
              ))}
            </div>
          </div>
        }
      >
        <button
          type="button"
          className={`combat-trigger-field-btn${open ? ' is-active' : ''}`}
          aria-haspopup="listbox"
          aria-expanded={open}
          aria-label={label}
        >
          <span className="combat-trigger-field-label">{label}</span>
          <span className="combat-trigger-field-main">
            <span className="combat-trigger-field-text" title={selected?.label ?? value}>
              {selected?.label ?? value}
            </span>
            <span className={`combat-trigger-field-arrow${open ? ' is-open' : ''}`} aria-hidden="true">
              ▾
            </span>
          </span>
        </button>
      </FloatingPopover>
    </div>
  )
}

/**
 * 触发条件数值输入微标签控件
 *
 * 尺寸与 TriggerSelectField 严格同构（40px 统一高度与修仙暗金质感），
 * 内嵌等宽数字输入与动态单位徽标（%、层、点）。
 */
function TriggerValueField({
  label,
  value,
  unit,
  min = 0,
  max,
  step = 1,
  onChange,
  className = '',
}: {
  label: string
  value: number
  unit?: string
  min?: number
  max?: number
  step?: number
  onChange: (value: number) => void
  className?: string
}): React.JSX.Element {
  const inputId = useId()

  return (
    <div className={`combat-trigger-field combat-trigger-field--value ${className}`.trim()}>
      <label className="combat-trigger-field-label" htmlFor={inputId}>
        {label}
      </label>
      <div className="combat-trigger-input-wrap">
        <input
          id={inputId}
          type="number"
          inputMode="decimal"
          className="combat-trigger-number-input"
          value={Number.isFinite(value) ? value : 0}
          onChange={event => onChange(Number(event.target.value) || 0)}
          min={min}
          max={max}
          step={step}
        />
        {unit && <span className="combat-trigger-unit-badge">{unit}</span>}
      </div>
    </div>
  )
}

/**
 * 战斗技能触发条件编辑器 Props
 */
interface CombatTriggerEditorProps {
  /** 弹窗展开状态 */
  open: boolean
  /** 弹窗标题（通常为正在编辑的技能名称） */
  title: string
  /** 初始触发条件列表 */
  initialTriggers?: CombatTrigger[]
  /** 关闭弹窗回调 */
  onClose: () => void
  /** 保存触发条件回调（null 表示清空所有条件，默认总是释放） */
  onSave: (triggers: CombatTrigger[] | null) => void
}

/** 深拷贝触发条件数组，避免直接变异外部 props 状态 */
function cloneTriggers(triggers: CombatTrigger[] | undefined): CombatTrigger[] {
  return (triggers ?? []).map(trigger => ({ ...trigger }))
}

/**
 * 战斗技能触发条件编辑器模态窗 (CombatTriggerEditor)
 *
 * 交互与状态机流转机制：
 * 1. 目标依赖 (dependency)：自身 (self)、目标敌怪 (target-enemy)、最低/最高气血队友与敌方；
 * 2. 触发条件 (condition)：当前/损失气血比例、当前/损失真元比例、固定数值阈值、Buff 层数以及眩晕/致盲/沉默等控制状态；
 * 3. 比较符智能切换：数值型条件采用 (<=, <, >=, >)；状态型条件自动切换为 (生效中 is-active / 未生效 is-inactive)；
 * 4. 自动重置安全机制：当条件类型切换时，重置比较符与阈值，清除非 Buff 条件的 buffId，防止非法组合提交；
 * 5. 条件数量上限：严格受限在 MAX_TRIGGERS (4条) 之内，条件之间为逻辑与 (AND) 关系；
 * 6. 极致空间利用率：单条规则规整为 44px 高度单行，彻底消除 200px 巨型正方形与断层割裂。
 */
export function CombatTriggerEditor({
  open,
  title,
  initialTriggers,
  onClose,
  onSave,
}: CombatTriggerEditorProps): React.JSX.Element | null {
  const t = useT()
  const titleId = useId()
  const [triggers, setTriggers] = useState<CombatTrigger[]>(() => cloneTriggers(initialTriggers))

  // initialTriggers 经 ref 中转：战斗快照刷新会让外部 combat 对象（含 triggers 数组）整体换引用，
  // 若把 initialTriggers 放进依赖，弹窗打开期间每次快照下发都会把未保存的编辑内容重置回旧值。
  // 因此只在 open 变为 true 的瞬间经 ref 同步一次，打开期间外部刷新一律不打断编辑。
  const initialTriggersRef = useRef(initialTriggers)
  initialTriggersRef.current = initialTriggers
  useEffect(() => {
    if (open) setTriggers(cloneTriggers(initialTriggersRef.current))
  }, [open])

  const isStatusCondition = (condition: string) => condition.endsWith('-status')
  const isPercentage = (condition: string) => condition.includes('percentage')

  // 条件切换时重置比较符与数值，避免残留“已生效 + 数值 1”这类非法组合。
  const updateTrigger = (index: number, patch: Partial<CombatTrigger>) => {
    setTriggers(current => current.map((trigger, triggerIndex) => {
      if (triggerIndex !== index) return trigger
      const next = { ...trigger, ...patch }
      if (patch.condition !== undefined && patch.condition !== trigger.condition) {
        next.comparator = isStatusCondition(next.condition)
          ? 'is-active'
          : 'greater-than-equal'
        next.value = 0
        if (next.condition !== 'buff-stacks') next.buffId = null
      }
      return next
    }))
  }

  // 格式化当前条件的自然语言总结文本
  const summary = useMemo(() => (
    triggers.map(trigger => describeCombatTrigger(trigger)).join(t('combat.trigger.summarySeparator'))
  ), [t, triggers])

  return (
    <Modal open={open} labelledBy={titleId} onClose={onClose} size="large" className="combat-trigger-modal">
      <div className="combat-trigger-editor">
        <header className="modal-header">
          <div>
            <h2 id={titleId}>{title}</h2>
            <p className="combat-trigger-hint">
              {t('game.trigger.editor.hint', { max: MAX_TRIGGERS })}
            </p>
          </div>
        </header>

        {triggers.length > 0 && (
          <div className="combat-trigger-summary-bar">
            <span className="combat-trigger-summary-tag">{t('combat.trigger.ruleOverview')}</span>
            <span className="combat-trigger-summary-text" title={summary}>{summary}</span>
          </div>
        )}

        <div className="combat-trigger-list">
          {triggers.length === 0 && (
            <p className="combat-trigger-empty">{t('game.trigger.editor.empty')}</p>
          )}
          {triggers.map((trigger, index) => {
            const isStatus = isStatusCondition(trigger.condition)
            const numeric = !isStatus
            const percentage = isPercentage(trigger.condition)
            const isBuff = trigger.condition === 'buff-stacks'
            const unit = percentage ? '%' : isBuff ? t('combat.trigger.buffStackUnit') : t('combat.trigger.pointUnit')

            return (
              <section className="combat-trigger-card" key={index}>
                <header className="combat-trigger-card-header">
                  <span className="combat-trigger-index">
                    {t('combat.trigger.indexLabel', { n: formatNumber(index + 1) })}
                  </span>
                  <span className="combat-trigger-preview" title={describeCombatTrigger(trigger)}>
                    {describeCombatTrigger(trigger)}
                  </span>
                  <button
                    aria-label={t('combat.trigger.removeAria', { n: formatNumber(index + 1) })}
                    className="combat-trigger-remove"
                    onClick={() => setTriggers(current => current.filter((_, triggerIndex) => triggerIndex !== index))}
                    type="button"
                  >×</button>
                </header>

                <div className={`combat-trigger-fields${isBuff ? ' has-buff' : ''}${isStatus ? ' is-status' : ''}`}>
                  <TriggerSelectField
                    className="combat-trigger-field--dep"
                    label={t('combat.trigger.dependencyLabel')}
                    onSelect={value => updateTrigger(index, { dependency: value as CombatTriggerDependency })}
                    options={[
                      { id: 'self', label: t('game.trigger.dependency.self') },
                      { id: 'target-enemy', label: t('game.trigger.dependency.target-enemy'), subtitle: t('combat.trigger.subtitle.targetEnemy') },
                      { id: 'ally-lowest-hp', label: t('game.trigger.dependency.ally-lowest-hp'), subtitle: t('combat.trigger.subtitle.allyLowestHp') },
                      { id: 'ally-highest-hp', label: t('game.trigger.dependency.ally-highest-hp'), subtitle: t('combat.trigger.subtitle.allyHighestHp') },
                      { id: 'enemy-lowest-hp', label: t('game.trigger.dependency.enemy-lowest-hp'), subtitle: t('combat.trigger.subtitle.enemyLowestHp') },
                      { id: 'enemy-highest-hp', label: t('game.trigger.dependency.enemy-highest-hp'), subtitle: t('combat.trigger.subtitle.enemyHighestHp') },
                    ]}
                    value={trigger.dependency}
                  />

                  <TriggerSelectField
                    className="combat-trigger-field--cond"
                    label={t('combat.trigger.conditionLabel')}
                    onSelect={value => updateTrigger(index, { condition: value as CombatTriggerCondition })}
                    options={[
                      { id: 'current-hp-percentage', label: t('game.trigger.condition.current-hp-percentage') },
                      { id: 'missing-hp-percentage', label: t('game.trigger.condition.missing-hp-percentage') },
                      { id: 'current-mana-percentage', label: t('game.trigger.condition.current-mana-percentage') },
                      { id: 'missing-mana-percentage', label: t('game.trigger.condition.missing-mana-percentage') },
                      { id: 'current-hp', label: t('game.trigger.condition.current-hp') },
                      { id: 'missing-hp', label: t('game.trigger.condition.missing-hp') },
                      { id: 'current-mana', label: t('game.trigger.condition.current-mana') },
                      { id: 'missing-mana', label: t('game.trigger.condition.missing-mana') },
                      { id: 'buff-stacks', label: t('game.trigger.condition.buff-stacks'), subtitle: t('combat.trigger.subtitle.buffStacks') },
                      { id: 'stun-status', label: t('game.trigger.condition.stun-status') },
                      { id: 'blind-status', label: t('game.trigger.condition.blind-status') },
                      { id: 'silence-status', label: t('game.trigger.condition.silence-status') },
                    ]}
                    value={trigger.condition}
                  />

                  {isBuff && (
                    <TriggerSelectField
                      className="combat-trigger-field--buff"
                      label={t('combat.trigger.buffLabel')}
                      onSelect={value => updateTrigger(index, { buffId: value || null })}
                      options={[
                        { id: '', label: t('combat.trigger.selectBuff') },
                        { id: 'buff.qi-gathering', label: t('combat.trigger.buff.qi-gathering') },
                        { id: 'buff.spirit-tea', label: t('combat.trigger.buff.spirit-tea') },
                        { id: 'buff.fire-talisman', label: t('combat.trigger.buff.fire-talisman') },
                      ]}
                      value={trigger.buffId ?? ''}
                    />
                  )}

                  <TriggerSelectField
                    className="combat-trigger-field--comp"
                    label={t('combat.trigger.comparatorLabel')}
                    onSelect={value => updateTrigger(index, { comparator: value as CombatTriggerComparator })}
                    options={(numeric
                      ? [
                          { id: 'less-than-equal', label: t('game.trigger.comparator.less-than-equal') },
                          { id: 'less-than', label: t('game.trigger.comparator.less-than') },
                          { id: 'greater-than-equal', label: t('game.trigger.comparator.greater-than-equal') },
                          { id: 'greater-than', label: t('game.trigger.comparator.greater-than') },
                        ]
                      : [
                          { id: 'is-active', label: t('game.trigger.comparator.is-active') },
                          { id: 'is-inactive', label: t('game.trigger.comparator.is-inactive') },
                        ]
                    )}
                    value={trigger.comparator}
                  />

                  {numeric && (
                    <TriggerValueField
                      className="combat-trigger-field--val"
                      label={percentage ? t('combat.trigger.thresholdPercent') : t('combat.trigger.threshold')}
                      value={trigger.value}
                      unit={unit}
                      min={0}
                      max={percentage ? 100 : undefined}
                      step={percentage ? 0.5 : 1}
                      onChange={value => updateTrigger(index, { value })}
                    />
                  )}
                </div>
              </section>
            )
          })}
        </div>

        <footer className="combat-trigger-actions">
          <button
            disabled={triggers.length >= MAX_TRIGGERS}
            onClick={() => setTriggers(current => [
              ...current,
              {
                dependency: 'self',
                condition: 'current-hp-percentage',
                comparator: 'less-than-equal',
                value: 50,
              },
            ])}
            type="button"
          >{t('combat.trigger.addCondition')}</button>
          <button
            onClick={() => onSave(null)}
            type="button"
          >{t('combat.trigger.clearAll')}</button>
          <span>{t('combat.trigger.andHint', { count: formatNumber(triggers.length), max: formatNumber(MAX_TRIGGERS) })}</span>
          <button
            className="primary"
            onClick={() => {
              onSave(triggers.map(trigger => ({
                ...trigger,
                value: Number.isFinite(trigger.value) ? trigger.value : 0,
              })))
              onClose()
            }}
            type="button"
          >{t('combat.trigger.save')}</button>
        </footer>
      </div>
    </Modal>
  )
}
