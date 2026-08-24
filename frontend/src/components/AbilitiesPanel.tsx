import { memo, useMemo, useState } from 'react'
import { gameClient } from '../network/gameClient'
import { describeAbility, formatAbilityCategory } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import type { CombatTrigger } from '../game/types'
import { CatalogPicker, type CatalogPickerOption } from './CatalogPicker'
import { CombatTriggerEditor } from './CombatTriggerEditor'
import { EffectTooltipTarget } from './EffectTooltip'

const EMPTY_ABILITY_COOLDOWNS: Readonly<Record<string, number>> = Object.freeze({})

/**
 * 技能与物品槽位冷却扇形遮罩覆盖层
 */
const AbilityCooldownOverlay = memo(function AbilityCooldownOverlay({
  remainingMilliseconds,
  totalMilliseconds,
}: {
  remainingMilliseconds: number
  totalMilliseconds: number
}): React.JSX.Element | null {
  if (remainingMilliseconds <= 0 || totalMilliseconds <= 0) return null
  const ratio = Math.max(0, Math.min(1, remainingMilliseconds / totalMilliseconds))
  const degrees = Math.round(ratio * 360)
  const remainingSeconds = (remainingMilliseconds / 1000).toFixed(1)

  return (
    <span
      aria-hidden="true"
      className="item-slot-duration-overlay item-slot-duration-overlay--cooling"
      style={{ '--duration-sweep': `${degrees}deg` } as React.CSSProperties}
    >
      <span className="item-slot-duration-text">{remainingSeconds}s</span>
    </span>
  )
})

/**
 * 功法神通管理面板 (AbilitiesPanel)
 *
 * 核心特性：
 * 1. 【横排动态装配槽】：横排排布功法槽位，首格固定为「内功」（心法/本命功法），后续格为「术法」（神通技法），支持后续任意动态扩增；
 * 2. 【已学功法总览】：收拢展示修士已习得的全部功法秘籍，支持查看详细词条、五行属性、灵力消耗与装配状态。
 */
export const AbilitiesPanel = memo(function AbilitiesPanel(): React.JSX.Element {
  const t = useT()
  const combat = useGameStore(state => state.combat)
  const abilities = useGameStore(state => state.abilities)
  const abilityCooldowns = useGameStore(
    state => state.queue.entries[0]?.expedition?.abilityCooldownMilliseconds
      ?? state.combatResources?.abilityCooldownMilliseconds,
  ) ?? EMPTY_ABILITY_COOLDOWNS

  const [activePickerSlotIndex, setActivePickerSlotIndex] = useState<number | null>(null)
  const [editingTriggerSlotIndex, setEditingTriggerSlotIndex] = useState<number | null>(null)

  if (!combat) return <div className="abilities-panel-empty">{t('common.emptyData')}</div>

  const update = (patch: Partial<typeof combat>) => {
    gameClient.sendCommand('set_combat_loadout', { ...combat, ...patch })
  }

  const withAbility = (index: number, abilityId: string | null) => combat.abilitySlots.map((slot, slotIndex) => (
    slotIndex === index
      ? { ...slot, abilityId, triggers: abilityId === slot.abilityId ? slot.triggers : [] }
      : slot
  ))

  const saveTriggers = (triggers: CombatTrigger[] | null) => {
    if (editingTriggerSlotIndex === null) return
    const nextTriggers = triggers ?? []
    update({
      abilitySlots: combat.abilitySlots.map((slot, index) =>
        index === editingTriggerSlotIndex ? { ...slot, triggers: nextTriggers } : slot,
      ),
    })
  }

  const learnedAbilityIds = useMemo(() => new Set(combat.learnedAbilityIds ?? []), [combat.learnedAbilityIds])

  // 获得已学功法定义列表
  const learnedAbilities = useMemo(() => {
    return abilities.filter(ability => learnedAbilityIds.has(ability.abilityId))
  }, [abilities, learnedAbilityIds])

  // 选项列表（供 CatalogPicker 使用）
  const pickerOptions = useMemo<CatalogPickerOption[]>(() => {
    return learnedAbilities.map(ability => ({
      id: ability.abilityId,
      label: ability.name,
      subtitle: t('app.combatPanel.abilitySubtitle', {
        mana: Math.round(ability.manaCost),
        cast: (ability.castMilliseconds / 1000).toFixed(1),
      }),
      category: formatAbilityCategory(ability.category, ability.element),
      tone: ability.effectType === 'heal' ? 'green' : 'violet',
      tooltip: describeAbility(ability, 1),
    }))
  }, [learnedAbilities, t])

  // 检查某个功法当前装备在哪个槽位
  const getEquippedSlotIndex = (abilityId: string): number | null => {
    const idx = combat.abilitySlots.findIndex(slot => slot.abilityId === abilityId)
    return idx >= 0 ? idx : null
  }

  const currentPickerValue = activePickerSlotIndex !== null
    ? combat.abilitySlots[activePickerSlotIndex]?.abilityId ?? null
    : null

  const isPickingInternal = activePickerSlotIndex === 0
  const pickerTitle = activePickerSlotIndex !== null
    ? isPickingInternal
      ? t('app.abilitiesPanel.chooseInternalTitle')
      : t('app.abilitiesPanel.chooseSpellTitle', { slot: activePickerSlotIndex })
    : ''

  const slotCount = combat.abilitySlots.length

  return (
    <div className="abilities-view-panel">
      {/* 上部：斗法横排装配槽 */}
      <section className="abilities-section abilities-loadout-section">
        <div className="abilities-section-header">
          <span className="abilities-section-title">{t('app.abilitiesPanel.equippedSlotsTitle')}</span>
          <span className="abilities-section-count">
            {t('app.abilitiesPanel.equippedCount', { count: slotCount })}
          </span>
        </div>

        <div className="abilities-slot-row">
          {combat.abilitySlots.map((slot, index) => {
            const isInternal = index === 0
            const ability = abilities.find(entry => entry.abilityId === slot.abilityId)
            const remainingCooldown = ability ? (abilityCooldowns[ability.abilityId] ?? 0) : 0

            const slotTypeTag = isInternal
              ? t('app.abilitiesPanel.slotInternal')
              : t('app.abilitiesPanel.slotSpell', { n: index })

            const ribbonLabel = isInternal
              ? t('app.combatPanel.internalRibbon')
              : t('app.combatPanel.spellRibbon')

            const emptyBtnLabel = isInternal
              ? t('app.abilitiesPanel.chooseInternal')
              : t('app.abilitiesPanel.chooseSpell')

            const slotAriaLabel = ability
              ? t('app.combatPanel.currentAbilityAria', { name: ability.name })
              : isInternal
                ? t('app.combatPanel.chooseInternalSlotAria')
                : t('app.combatPanel.chooseSpellSlotAria', { slot: index })

            const slotContent = (
              <>
                <button
                  aria-label={slotAriaLabel}
                  className={`combat-ability-slot${isInternal ? ' is-internal-slot' : ' is-spell-slot'}${ability ? '' : ' combat-ability-slot--empty'}`}
                  onClick={() => setActivePickerSlotIndex(index)}
                  type="button"
                >
                  <span className={`abilities-slot-tag-badge${isInternal ? ' is-internal' : ''}`}>
                    {slotTypeTag}
                  </span>

                  {ability ? (
                    <span className="item-tile combat-ability-cell" data-rarity={isInternal ? 'emperor' : 'mystic'}>
                      <span className="item-type-ribbon" aria-hidden="true">
                        <span>{ribbonLabel}</span>
                      </span>
                      <strong className="item-tile-name">{ability.name}</strong>
                      <span className="item-corner item-corner--bottom-right">
                        {t('app.combatPanel.manaCost', { mana: Math.round(ability.manaCost) })}
                      </span>
                      {remainingCooldown > 0 && (
                        <AbilityCooldownOverlay
                          remainingMilliseconds={remainingCooldown}
                          totalMilliseconds={ability.cooldownMilliseconds}
                        />
                      )}
                    </span>
                  ) : (
                    <span className="abilities-empty-slot-btn">
                      <span className="abilities-slot-plus">+</span>
                      <span className="abilities-slot-empty-text">{emptyBtnLabel}</span>
                    </span>
                  )}
                </button>

                {ability && (
                  <div className="combat-ability-slot-actions">
                    <span className="combat-ability-level-control" onClick={e => e.stopPropagation()}>
                      <span aria-hidden="true">Lv.</span>
                      <input
                        aria-label={t('app.combatPanel.levelAria', { name: ability.name })}
                        className="combat-ability-level-input"
                        max={100}
                        min={1}
                        onChange={event => update({
                          abilitySlots: combat.abilitySlots.map((current, slotIndex) => (
                            slotIndex === index
                              ? { ...current, level: Math.max(1, Math.min(100, Number(event.target.value) || 1)) }
                              : current
                          )),
                        })}
                        type="number"
                        value={slot.level}
                      />
                    </span>
                    <button
                      aria-label={t('app.combatPanel.editTriggersAria', { name: ability.name, count: slot.triggers?.length ?? 0 })}
                      className="item-slot-inline-action item-slot-inline-action--condition"
                      onClick={() => setEditingTriggerSlotIndex(index)}
                      title={t('app.combatPanel.editTriggersTitle')}
                      type="button"
                    >
                      {t('app.combatPanel.triggerConditionBtn', { count: (slot.triggers?.length ?? 0) > 0 ? (slot.triggers?.length ?? 0) : '' })}
                    </button>
                  </div>
                )}
              </>
            )

            return (
              <div className={`abilities-slot-card${isInternal ? ' is-internal' : ''}`} key={index}>
                {ability ? (
                  <EffectTooltipTarget className="combat-ability-slot-shell" content={describeAbility(ability, slot.level)} tabIndex={-1}>
                    {slotContent}
                  </EffectTooltipTarget>
                ) : (
                  <span className="combat-ability-slot-shell">{slotContent}</span>
                )}
              </div>
            )
          })}
        </div>
      </section>

      {/* 下部：已学功法秘籍目录 */}
      <section className="abilities-section abilities-catalog-section">
        <div className="abilities-section-header">
          <span className="abilities-section-title">{t('app.abilitiesPanel.learnedAbilitiesTitle')}</span>
          <span className="abilities-section-badge">
            {t('app.abilitiesPanel.learnedCount', { count: learnedAbilities.length })}
          </span>
        </div>

        <div className="abilities-list">
          {learnedAbilities.length === 0 ? (
            <div className="abilities-empty-state">
              <p>{t('app.abilitiesPanel.noLearnedAbilities')}</p>
            </div>
          ) : (
            learnedAbilities.map(ability => {
              const equippedIndex = getEquippedSlotIndex(ability.abilityId)
              const tooltipContent = describeAbility(ability, 1)

              return (
                <EffectTooltipTarget
                  className="abilities-card-wrap"
                  content={tooltipContent}
                  key={ability.abilityId}
                >
                  <div className={`abilities-card ${equippedIndex !== null ? 'is-equipped' : ''}`}>
                    <div className="abilities-card-main">
                      <div className="abilities-card-title-row">
                        <strong className="abilities-card-name">{ability.name}</strong>
                        <span className={`abilities-card-tag tone-${ability.effectType === 'heal' ? 'green' : 'violet'}`}>
                          {formatAbilityCategory(ability.category, ability.element)}
                        </span>
                      </div>
                      <div className="abilities-card-meta">
                        <span className="abilities-card-stat">
                          {t('app.abilitiesPanel.manaCost', { mana: Math.round(ability.manaCost) })}
                        </span>
                        <span className="abilities-card-sep">·</span>
                        <span className="abilities-card-stat">
                          {t('app.abilitiesPanel.castTime', { cast: (ability.castMilliseconds / 1000).toFixed(1) })}
                        </span>
                        <span className="abilities-card-sep">·</span>
                        <span className="abilities-card-stat">
                          {t('app.abilitiesPanel.cooldown', { cd: (ability.cooldownMilliseconds / 1000).toFixed(1) })}
                        </span>
                      </div>
                    </div>
                    {equippedIndex !== null && (
                      <span className={`abilities-equipped-badge${equippedIndex === 0 ? ' is-internal' : ''}`}>
                        {equippedIndex === 0
                          ? t('app.abilitiesPanel.equippedInternalBadge')
                          : t('app.abilitiesPanel.equippedSpellBadge', { slot: equippedIndex })}
                      </span>
                    )}
                  </div>
                </EffectTooltipTarget>
              )
            })
          )}
        </div>
      </section>

      {/* 功法挑选弹窗 */}
      <CatalogPicker
        allowEmpty
        onClose={() => setActivePickerSlotIndex(null)}
        onSelect={value => {
          if (activePickerSlotIndex !== null) {
            update({ abilitySlots: withAbility(activePickerSlotIndex, value) })
          }
          setActivePickerSlotIndex(null)
        }}
        open={activePickerSlotIndex !== null}
        options={pickerOptions}
        title={pickerTitle}
        value={currentPickerValue}
      />

      {/* 触发条件编辑器 */}
      {editingTriggerSlotIndex !== null && (
        <CombatTriggerEditor
          initialTriggers={combat.abilitySlots[editingTriggerSlotIndex]?.triggers ?? []}
          onClose={() => setEditingTriggerSlotIndex(null)}
          onSave={saveTriggers}
          open
          title={`${editingTriggerSlotIndex === 0 ? t('app.abilitiesPanel.slotInternalBadge') : t('app.abilitiesPanel.slotSpell', { n: editingTriggerSlotIndex })} 触发条件`}
        />
      )}
    </div>
  )
})
