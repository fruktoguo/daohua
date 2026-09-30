import { memo, useMemo, useState } from 'react'
import { assignAbilityToSlot, learnedAbilityLevel } from '../game/ids'
import { sendLoadoutReplace } from '../game/loadoutCommands'
import { describeAbility, formatAbilityCategory } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { resolveItemIconUrl } from '../game/itemIcons'
import { useT } from '../i18n'
import type { CombatTrigger } from '../game/types'
import { AbilityGridPicker } from './AbilityGridPicker'
import { CombatTriggerEditor } from './CombatTriggerEditor'
import { EffectTooltipTarget } from './EffectTooltip'

// 共享的冻结空表：combatResources 未下发冷却数据时给选择器返回同一引用，避免每次取新对象触发重渲染。
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
  const remainingSeconds = (remainingMilliseconds / 1000).toFixed(1)

  // 必须透传 --cooldown-ratio：遮罩的扇形完全由该变量驱动，其缺省值为 1，
  // 漏传会让整块瓦片被暗区盖满，冷却期间技能图标与名称完全不可读。
  // 原先写入的 --duration-sweep 在任何样式表中都没有消费者（已删除）。
  return (
    <span
      aria-hidden="true"
      className="item-slot-duration-overlay"
      style={{ '--cooldown-ratio': ratio } as React.CSSProperties}
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
 *    槽位与功法类别互斥——内功位只收 discipline=internal 的心法，术法位只收术法，与服务端 loadout.replace 校验一致；
 * 2. 【已学功法分组总览】：已习得功法按内功心法 / 神通术法两组分列，支持查看详细词条、五行属性、灵力消耗与装配状态。
 */
export const AbilitiesPanel = memo(function AbilitiesPanel(): React.JSX.Element {
  const t = useT()
  const combat = useGameStore(state => state.combat)
  const abilities = useGameStore(state => state.abilities)
  const abilityCooldowns = useGameStore(
    state => state.combatResources?.abilityCooldownMilliseconds,
  ) ?? EMPTY_ABILITY_COOLDOWNS

  const [activePicker, setActivePicker] = useState<{ index: number; anchor: HTMLElement } | null>(null)
  const [editingTriggerSlotIndex, setEditingTriggerSlotIndex] = useState<number | null>(null)

  if (!combat) return <div className="abilities-panel-empty">{t('common.emptyData')}</div>

  // 装配变更统一经 sendLoadoutReplace 发送：内部完成槽位类别互斥与等级归一化。
  const update = (patch: Partial<typeof combat>) => sendLoadoutReplace(patch)

  // 重复装配按「移动」处理：assignAbilityToSlot 会把功法从原槽位移到新槽位。
  const withAbility = (index: number, abilityId: string | null) => assignAbilityToSlot(combat.abilitySlots, index, abilityId)

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
  // 已习得技能的参悟等级由技能经验经立方曲线导出，与服务端 AbilityExpCurve 同式。
  const abilityLevelFor = (abilityId: string) => learnedAbilityLevel(combat.abilityExperiences, abilityId)

  // 获得已学功法定义列表，并按 discipline 分为内功心法与神通术法两组（缺省按术法归组）
  const learnedAbilities = useMemo(() => {
    return abilities.filter(ability => learnedAbilityIds.has(ability.abilityId))
  }, [abilities, learnedAbilityIds])
  const learnedInternal = useMemo(
    () => learnedAbilities.filter(ability => ability.discipline === 'internal'),
    [learnedAbilities],
  )
  const learnedSpells = useMemo(
    () => learnedAbilities.filter(ability => ability.discipline !== 'internal'),
    [learnedAbilities],
  )

  // 检查某个功法当前装备在哪个槽位
  const getEquippedSlotIndex = (abilityId: string): number | null => {
    const idx = combat.abilitySlots.findIndex(slot => slot.abilityId === abilityId)
    return idx >= 0 ? idx : null
  }

  const activePickerSlotIndex = activePicker?.index ?? null
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

  const internalSlot = combat.abilitySlots[0]
  const spellSlots = combat.abilitySlots.slice(1)

  const renderSlot = (slot: typeof combat.abilitySlots[0], index: number) => {
    const isInternal = index === 0
    const ability = abilities.find(entry => entry.abilityId === slot.abilityId)
    const remainingCooldown = ability ? (abilityCooldowns[ability.abilityId] ?? 0) : 0
    const abilityIconUrl = resolveItemIconUrl(ability?.abilityId)

    const slotTypeTag = isInternal
      ? t('app.abilitiesPanel.slotInternal')
      : t('app.abilitiesPanel.slotSpell', { n: index })

    const ribbonLabel = isInternal
      ? t('app.combatPanel.internalRibbon')
      : t('app.combatPanel.spellRibbon')

    const emptyBtnLabel = isInternal
      ? t('app.abilitiesPanel.slotInternal')
      : t('app.abilitiesPanel.slotSpell', { n: index })

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
          onClick={event => {
            const anchor = event.currentTarget
            setActivePicker(prev => prev?.index === index ? null : { index, anchor })
          }}
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
              {abilityIconUrl
                ? <img alt={ability.name} className="item-tile-icon" decoding="async" draggable={false} src={abilityIconUrl} />
                : <strong className="item-tile-name">{ability.name}</strong>}
              <span className="item-corner item-corner--bottom-right">
                {t('app.combatPanel.manaCost', { mana: Math.round(ability.manaCost ?? 0) })}
              </span>
              {remainingCooldown > 0 && (
                <AbilityCooldownOverlay
                  remainingMilliseconds={remainingCooldown}
                  totalMilliseconds={ability.cooldownMilliseconds ?? 0}
                />
              )}
              <span aria-hidden="true" className="item-tile-glow" />
            </span>
          ) : (
            <span className="abilities-slot-empty-label">{emptyBtnLabel}</span>
          )}
        </button>

        {ability && (
          <div className="combat-ability-slot-actions">
            {/* 装配等级恒等于已参悟等级（调低无收益），展示为静态徽标而非输入框 */}
            <span className="combat-ability-level-control">
              <span aria-hidden="true">Lv.</span>
              <span className="combat-ability-level-value">{abilityLevelFor(ability.abilityId)}</span>
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
      <div className={`abilities-slot-card${isInternal ? ' is-internal' : ''}${activePicker?.index === index ? ' is-picking' : ''}`} key={index}>
        {ability ? (
          <EffectTooltipTarget className="combat-ability-slot-shell" content={describeAbility(ability, abilityLevelFor(ability.abilityId))} tabIndex={-1}>
            {slotContent}
          </EffectTooltipTarget>
        ) : (
          <span className="combat-ability-slot-shell">{slotContent}</span>
        )}
      </div>
    )
  }

  return (
    <div className="abilities-view-panel">
      {/* 上部：斗法装配槽（第一排内功，第二排术法多行换行） */}
      <section className="abilities-section abilities-loadout-section">
        <div className="abilities-section-header">
          <span className="abilities-section-title">{t('app.abilitiesPanel.equippedSlotsTitle')}</span>
          <span className="abilities-section-count">
            {t('app.abilitiesPanel.equippedCount', { count: slotCount })}
          </span>
        </div>

        {/* 第一排：内功心法 */}
        <div className="abilities-group-row abilities-group-row--internal">
          <div className="abilities-group-header">
            <span className="abilities-group-label">{t('app.abilitiesPanel.slotInternal')}</span>
          </div>
          <div className="abilities-group-slots">
            {internalSlot && renderSlot(internalSlot, 0)}
          </div>
        </div>

        {/* 第二排：神通术法（多槽位自动折行） */}
        <div className="abilities-group-row abilities-group-row--spells">
          <div className="abilities-group-header">
            <span className="abilities-group-label">{t('app.combatPanel.spellRibbon')}</span>
          </div>
          <div className="abilities-group-slots abilities-group-slots--wrap">
            {spellSlots.map((slot, i) => renderSlot(slot, i + 1))}
          </div>
        </div>
      </section>

      {/* 下部：已学功法秘籍目录（去除冗余提示文本） */}
      <section className="abilities-section abilities-catalog-section">
        <div className="abilities-section-header">
          <span className="abilities-section-title">{t('app.abilitiesPanel.learnedAbilitiesTitle')}</span>
          <span className="abilities-section-badge">
            {t('app.abilitiesPanel.learnedCount', { count: learnedAbilities.length })}
          </span>
        </div>

        {learnedAbilities.length === 0 ? (
          <div className="abilities-empty-state">
            <p>{t('app.abilitiesPanel.noLearnedAbilities')}</p>
          </div>
        ) : (
          ([
            { key: 'internal', title: t('app.abilitiesPanel.learnedInternalTitle'), list: learnedInternal },
            { key: 'spell', title: t('app.abilitiesPanel.learnedSpellTitle'), list: learnedSpells },
          ] as const).map(group => (
            <div className="abilities-learned-group" key={group.key}>
              <div className="abilities-group-header">
                <span className="abilities-group-label">{group.title}</span>
                <span className="abilities-section-count">{group.list.length}</span>
              </div>
              {/* 已习得功法采用与背包/选择器一致的方块瓦片：左上元素丝带、右上参悟等级、右下装配槽位 */}
              <div className="abilities-learned-grid">
                {group.list.map(ability => {
                  const equippedIndex = getEquippedSlotIndex(ability.abilityId)
                  const learnedLevel = abilityLevelFor(ability.abilityId)
                  const iconUrl = resolveItemIconUrl(ability.abilityId)
                  const ribbonLabel = ability.element && ability.element !== 'none'
                    ? t(`combat.encounter.element.${ability.element}`)
                    : formatAbilityCategory(ability.category)

                  return (
                    <EffectTooltipTarget
                      className="item-grid-picker-tooltip"
                      content={describeAbility(ability, learnedLevel)}
                      key={ability.abilityId}
                    >
                      <span
                        className={`item-tile combat-ability-cell${equippedIndex !== null ? ' is-equipped' : ''}`}
                        data-rarity={group.key === 'internal' ? 'emperor' : 'mystic'}
                      >
                        <span aria-hidden="true" className="item-type-ribbon">
                          <span>{ribbonLabel}</span>
                        </span>
                        <span className="item-corner item-corner--top-right">
                          Lv. {learnedLevel}
                        </span>
                        {iconUrl
                          ? <img alt={ability.name} className="item-tile-icon" decoding="async" draggable={false} src={iconUrl} />
                          : <strong className="item-tile-name">{ability.name}</strong>}
                        {equippedIndex !== null && (
                          <span className="item-corner item-corner--bottom-right abilities-equipped-corner">
                            {equippedIndex === 0
                              ? t('app.combatPanel.internalRibbon')
                              : `${t('app.combatPanel.spellRibbon')}${equippedIndex}`}
                          </span>
                        )}
                        <span aria-hidden="true" className="item-tile-glow" />
                      </span>
                    </EffectTooltipTarget>
                  )
                })}
              </div>
            </div>
          ))
        )}
      </section>

      {/* 功法待选列表（贴靠目标槽位浮出） */}
      <AbilityGridPicker
        abilities={learnedAbilities}
        allowEmpty
        anchor={activePicker?.anchor ?? null}
        levelFor={abilityLevelFor}
        onClose={() => setActivePicker(null)}
        onSelect={value => {
          if (activePicker !== null) {
            update({ abilitySlots: withAbility(activePicker.index, value) })
          }
          setActivePicker(null)
        }}
        open={activePicker !== null}
        slotIndex={activePicker?.index}
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
          title={t('app.abilitiesPanel.triggerEditorTitle', {
            slot: editingTriggerSlotIndex === 0
              ? t('app.abilitiesPanel.slotInternalBadge')
              : t('app.abilitiesPanel.slotSpell', { n: editingTriggerSlotIndex }),
          })}
        />
      )}
    </div>
  )
})
