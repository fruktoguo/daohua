import { useEffect, useId, useMemo, useState } from 'react'
import { ChoiceMenu } from './CatalogPicker'
import { Modal } from './Modal'
import { describeCombatTrigger, formatNumber } from '../game/format'
import type { CombatTrigger } from '../game/types'
import { useT } from '../i18n'

/** 每个槽位最多 4 条触发条件，与服务端 XiuxianCombatLimits.TriggerCountPerSlot 保持一致。 */
const MAX_TRIGGERS = 4

const STATUS_COMPARATORS = ['is-active', 'is-inactive'] as const

interface CombatTriggerEditorProps {
  open: boolean
  title: string
  initialTriggers?: CombatTrigger[]
  onClose: () => void
  onSave: (triggers: CombatTrigger[] | null) => void
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
  const t = useT()
  const titleId = useId()
  const [triggers, setTriggers] = useState<CombatTrigger[]>(() => cloneTriggers(initialTriggers))

  useEffect(() => {
    if (open) setTriggers(cloneTriggers(initialTriggers))
  }, [initialTriggers, open])

  // 条件切换时重置比较符与数值，避免残留“已生效 + 数值 1”这类非法组合。
  const updateTrigger = (index: number, patch: Partial<CombatTrigger>) => {
    setTriggers(current => current.map((trigger, triggerIndex) => {
      if (triggerIndex !== index) return trigger
      const next = { ...trigger, ...patch }
      if (patch.condition !== undefined && patch.condition !== trigger.condition) {
        next.comparator = STATUS_COMPARATORS.includes(next.condition as typeof STATUS_COMPARATORS[number])
          ? 'is-active'
          : 'greater-than-equal'
        next.value = 0
        if (next.condition !== 'buff-stacks') next.buffId = null
      }
      return next
    }))
  }

  const summary = useMemo(() => (
    triggers.map(trigger => describeCombatTrigger(trigger)).join(t('combat.trigger.summarySeparator'))
  ), [t, triggers])

  const statusCondition = (condition: string) => condition.endsWith('-status')
  const isPercentage = (condition: string) => condition.includes('percentage')

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
          {triggers.length > 0 && (
            <p className="combat-trigger-summary" title={summary}>{summary || '　'}</p>
          )}
        </header>
        <div className="combat-trigger-list">
          {triggers.length === 0 && (
            <p className="combat-trigger-empty">{t('game.trigger.editor.empty')}</p>
          )}
          {triggers.map((trigger, index) => {
            const numeric = !statusCondition(trigger.condition)
            const percentage = isPercentage(trigger.condition)
            return (
              <section className="combat-trigger-card" key={index}>
                <header className="combat-trigger-card-header">
                  <span className="combat-trigger-index">{t('combat.trigger.indexLabel', { n: formatNumber(index + 1) })}</span>
                  <span className="combat-trigger-preview">{describeCombatTrigger(trigger)}</span>
                  <button
                    aria-label={t('combat.trigger.removeAria', { n: formatNumber(index + 1) })}
                    className="combat-trigger-remove"
                    onClick={() => setTriggers(current => current.filter((_, triggerIndex) => triggerIndex !== index))}
                    type="button"
                  >×</button>
                </header>
                <div className="combat-trigger-fields">
                  <ChoiceMenu
                    label={t('combat.trigger.dependencyLabel')}
                    onSelect={value => updateTrigger(index, { dependency: value })}
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
                  <ChoiceMenu
                    label={t('combat.trigger.conditionLabel')}
                    onSelect={value => updateTrigger(index, { condition: value })}
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
                  <ChoiceMenu
                    label={t('combat.trigger.comparatorLabel')}
                    onSelect={value => updateTrigger(index, { comparator: value })}
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
                  {trigger.condition === 'buff-stacks' && (
                    <ChoiceMenu
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
                  {numeric && (
                    <label className="combat-trigger-value">
                      <small>{percentage ? t('combat.trigger.thresholdPercent') : t('combat.trigger.threshold')}</small>
                      <input
                        inputMode="decimal"
                        max={percentage ? 100 : undefined}
                        min={0}
                        onChange={event => updateTrigger(index, { value: Number(event.target.value) || 0 })}
                        step={percentage ? 0.5 : 1}
                        type="number"
                        value={Number.isFinite(trigger.value) ? trigger.value : 0}
                      />
                    </label>
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
