import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { assignAbilityToSlot, findItemInCatalog, learnedAbilityLevel, resolveCatalogItemMetadata, resolveItemCategoryLabel, resolveItemSubCategory } from '../game/ids'
import { sendLoadoutReplace } from '../game/loadoutCommands'
import { describeAbility, formatAbilityCategory, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { resolveItemIconUrl } from '../game/itemIcons'
import { useT } from '../i18n'
import { resolveAbilityAsideCards } from './EffectTooltip'
import type { CombatTrigger } from '../game/types'
import { AbilityGridPicker } from './AbilityGridPicker'
import { CombatTriggerEditor } from './CombatTriggerEditor'
import { ConditionedTile } from './ConditionedTile'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'

type TriggerEditTarget = { kind: 'ability' | 'item'; id: string }

/**
 * 功法神通管理面板 (AbilitiesPanel)
 *
 * 核心特性：
 * 1. 【横排动态装配槽】：横排排布功法槽位，首格固定为「内功」（心法/本命功法），后续格为「术法」（神通技法），支持后续任意动态扩增；
 *    槽位与功法类别互斥——内功位只收 discipline=internal 的心法，术法位收术法（及「符法·天命」激活时的符箓），与服务端 loadout.replace 校验一致；
 * 2. 【已学功法分组总览】：已习得功法按内功心法 / 神通术法两组分列，支持查看详细词条、五行属性、灵力消耗与装配状态。
 */
export const AbilitiesPanel = memo(function AbilitiesPanel({ locked }: { locked: boolean }): React.JSX.Element {
  const t = useT()
  const combat = useGameStore(state => state.combat)
  const abilities = useGameStore(state => state.abilities)
  const items = useGameStore(state => state.items)
  const inventory = useGameStore(state => state.inventory)
  const traitIds = useGameStore(state => state.stats?.traitIds)
  const combatAutoUseSlots = useGameStore(state => state.queue.autoUseSlots.combat)

  const [activePicker, setActivePicker] = useState<{ index: number; anchor: HTMLElement } | null>(null)
  // 触发条件按功法 ID 或符箓物品 ID 编辑（跟随实体而非槽位）。
  const [editingTrigger, setEditingTrigger] = useState<TriggerEditTarget | null>(null)
  useEffect(() => {
    if (!locked) return
    setActivePicker(null)
    setEditingTrigger(null)
  }, [locked])

  const learnedAbilityIds = useMemo(() => new Set(combat?.learnedAbilityIds ?? []), [combat?.learnedAbilityIds])
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

  const hasFufaTianming = useMemo(() => Boolean(traitIds?.includes('trait.fufatianming')), [traitIds])
  const itemAmountFor = useCallback((itemId: string): number => {
    return inventory.reduce((sum, entry) => entry.itemId === itemId && !entry.variant ? sum + entry.amount : sum, 0)
  }, [inventory])
  const pickerTalismanItems = useMemo(() => {
    if (!hasFufaTianming) return undefined
    const autoUsedIds = new Set((combatAutoUseSlots ?? []).map(s => s.itemId).filter((id): id is string => Boolean(id)))
    return items.filter(item =>
      item.category === 'consumable'
      && item.subCategory === 'talisman'
      && item.usableBars.includes('combat')
      && !autoUsedIds.has(item.itemId)
      && itemAmountFor(item.itemId) > 0,
    )
  }, [combatAutoUseSlots, hasFufaTianming, itemAmountFor, items])

  if (!combat) return <div className="abilities-panel-empty">{t('common.emptyData')}</div>

  // 装配变更统一经 sendLoadoutReplace 发送：内部完成槽位类别互斥与等级归一化。
  const update = (patch: Partial<typeof combat>) => sendLoadoutReplace(patch)

  // 重复装配按「移动」处理：assignAbilityToSlot 会把功法或符箓从原槽位移到新槽位。
  const withAbility = (index: number, abilityId: string | null) => assignAbilityToSlot(combat.abilitySlots, index, abilityId)

  const saveTriggers = (triggers: CombatTrigger[] | null) => {
    if (editingTrigger === null) return
    if (editingTrigger.kind === 'item') {
      update({
        consumableTriggers: {
          ...(combat.consumableTriggers ?? {}),
          [editingTrigger.id]: triggers ?? [],
        },
      })
    } else {
      update({
        abilityTriggers: {
          ...(combat.abilityTriggers ?? {}),
          [editingTrigger.id]: triggers ?? [],
        },
      })
    }
  }

  // 已习得技能的参悟等级由技能经验经立方曲线导出，与服务端 AbilityExpCurve 同式。
  const abilityLevelFor = (abilityId: string) => learnedAbilityLevel(combat.abilityExperiences, abilityId)
  // 检查某个功法当前装备在哪个槽位
  const getEquippedSlotIndex = (abilityId: string): number | null => {
    const idx = combat.abilitySlots.findIndex(slot => slot.abilityId === abilityId)
    return idx >= 0 ? idx : null
  }

  const activePickerSlotIndex = activePicker?.index ?? null
  const currentPickerValue = activePickerSlotIndex !== null
    ? combat.abilitySlots[activePickerSlotIndex]?.abilityId ?? null
    : null
  const currentPickerAbility = currentPickerValue !== null && !currentPickerValue.startsWith('item.')
    ? abilities.find(entry => entry.abilityId === currentPickerValue) ?? null
    : null
  const currentPickerItem = currentPickerValue !== null && currentPickerValue.startsWith('item.')
    ? findItemInCatalog(items, currentPickerValue) ?? null
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
    const isTalismanSlot = !isInternal && Boolean(slot.abilityId?.startsWith('item.'))
    const talismanItem = isTalismanSlot && slot.abilityId ? findItemInCatalog(items, slot.abilityId) : undefined
    const ability = !isTalismanSlot ? abilities.find(entry => entry.abilityId === slot.abilityId) : undefined
    const abilityIconUrl = resolveItemIconUrl(ability?.abilityId)

    const slotTypeTag = isInternal
      ? t('app.abilitiesPanel.slotInternal')
      : t('app.abilitiesPanel.slotSpell', { n: index })

    if (isTalismanSlot && slot.abilityId) {
      const talismanId = slot.abilityId
      const metadata = resolveCatalogItemMetadata(talismanId, { itemDefinition: talismanItem, glyph: '符', tone: 'gold', t })
      const talismanName = talismanItem?.name ?? metadata.label
      const amount = itemAmountFor(talismanId)
      const itemTriggers = combat.consumableTriggers?.[talismanId] ?? talismanItem?.triggers ?? []
      const triggerCount = itemTriggers.length
      const categoryLabel = resolveItemCategoryLabel(resolveItemSubCategory(talismanId, talismanItem), t)
      return (
        <div
          className={`conditioned-tile abilities-slot-card${activePicker?.index === index ? ' is-picking' : ''}`}
          key={index}
        >
          <ItemTooltipTarget
            amount={amount}
            className="combat-ability-slot-shell"
            definition={talismanItem}
            itemId={talismanId}
            metadata={metadata}
            tabIndex={locked ? 0 : -1}
          >
            <button
              aria-label={t('app.combatPanel.currentAbilityAria', { name: talismanName })}
              className="combat-ability-slot is-spell-slot"
              disabled={locked}
              onClick={event => {
                const anchor = event.currentTarget
                setActivePicker(prev => prev?.index === index ? null : { index, anchor })
              }}
              type="button"
            >
              <span className="abilities-slot-tag-badge">{slotTypeTag}</span>
              <ItemTile
                amount={amount}
                category={categoryLabel}
                className="combat-ability-cell"
                itemId={talismanId}
                level={metadata.level}
                name={talismanName}
                rarity={metadata.rarity}
              />
            </button>
          </ItemTooltipTarget>
          {!locked && (
            <button
              aria-label={t('app.combatPanel.editTriggersAria', { name: talismanName, count: triggerCount })}
              className={`item-slot-inline-action item-slot-inline-action--condition${triggerCount > 0 ? ' has-triggers' : ''}`}
              onClick={e => {
                e.stopPropagation()
                setEditingTrigger({ kind: 'item', id: talismanId })
              }}
              title={t('app.combatPanel.editTriggersTitle')}
              type="button"
            >
              {t('app.combatPanel.triggerConditionBtn', { count: triggerCount > 0 ? formatNumber(triggerCount) : '' })}
            </button>
          )}
        </div>
      )
    }

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

    const slotButton = (
      <button
        aria-label={slotAriaLabel}
        className={`combat-ability-slot${isInternal ? ' is-internal-slot' : ' is-spell-slot'}${ability ? '' : ' combat-ability-slot--empty'}`}
        disabled={locked}
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
            <span aria-hidden="true" className="item-tile-glow" />
          </span>
        ) : (
          <span className="abilities-slot-empty-label">{emptyBtnLabel}</span>
        )}
      </button>
    )

    // Lv 徽标与条件按钮都在悬浮卡触发域之外（ConditionedTile 统一收拢）：
    // 鼠标落在徽标/按钮上时不再弹出悬浮卡，点开条件编辑器也不会出现 tooltip 叠 modal。
    // 条件跟随功法：按 abilityId 读写 combat.abilityTriggers，卸下再装配条件保留。
    const triggerCount = ability ? (combat.abilityTriggers?.[ability.abilityId]?.length ?? 0) : 0
    const abilityExp = ability ? (combat.abilityExperiences?.[ability.abilityId] ?? 0) : 0
    const slotAsideCards = ability ? resolveAbilityAsideCards(ability, abilityExp) : undefined
    return (
      <ConditionedTile
        as="div"
        asideCards={slotAsideCards}
        className={`abilities-slot-card${isInternal ? ' is-internal' : ''}${activePicker?.index === index ? ' is-picking' : ''}`}
        condition={!locked && ability ? {
          ariaLabel: t('app.combatPanel.editTriggersAria', { name: ability.name, count: triggerCount }),
          hasTriggers: triggerCount > 0,
          label: t('app.combatPanel.triggerConditionBtn', { count: triggerCount > 0 ? triggerCount : '' }),
          onClick: () => setEditingTrigger({ kind: 'ability', id: ability.abilityId }),
          title: t('app.combatPanel.editTriggersTitle'),
        } : null}
        content={ability ? describeAbility(ability, abilityLevelFor(ability.abilityId)) : null}
        extras={ability && (
          <div className="combat-ability-slot-actions">
            {/* 装配等级恒等于已参悟等级（调低无收益），展示为静态徽标而非输入框 */}
            <span className="combat-ability-level-control">
              <span aria-hidden="true">Lv.</span>
              <span className="combat-ability-level-value">{abilityLevelFor(ability.abilityId)}</span>
            </span>
          </div>
        )}
        key={index}
        triggerClassName="combat-ability-slot-shell"
        triggerTabIndex={locked && ability ? 0 : -1}
      >
        {slotButton}
      </ConditionedTile>
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
                  const abilityExp = combat.abilityExperiences?.[ability.abilityId] ?? 0
                  const cellAsideCards = resolveAbilityAsideCards(ability, abilityExp)
                  const triggerCount = combat.abilityTriggers?.[ability.abilityId]?.length ?? 0
                  return (
                    <ConditionedTile
                      asideCards={cellAsideCards}
                      condition={!locked ? {
                        ariaLabel: t('app.combatPanel.editTriggersAria', { name: ability.name, count: triggerCount }),
                        hasTriggers: triggerCount > 0,
                        label: t('app.combatPanel.triggerConditionBtn', { count: triggerCount > 0 ? triggerCount : '' }),
                        onClick: () => setEditingTrigger({ kind: 'ability', id: ability.abilityId }),
                        title: t('app.combatPanel.editTriggersTitle'),
                      } : null}
                      content={describeAbility(ability, learnedLevel)}
                      key={ability.abilityId}
                      triggerClassName="item-grid-picker-tooltip"
                    >
                      <button
                        className={`item-tile combat-ability-cell${equippedIndex !== null ? ' is-equipped' : ''}`}
                        data-rarity={group.key === 'internal' ? 'emperor' : 'mystic'}
                        disabled={locked}
                        onClick={() => setEditingTrigger({ kind: 'ability', id: ability.abilityId })}
                        title={locked ? undefined : t('app.combatPanel.editTriggersTitle')}
                        type="button"
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
                      </button>
                    </ConditionedTile>
                  )
                })}
              </div>
            </div>
          ))
        )}
      </section>

      {/* 功法待选列表（贴靠目标槽位浮出，顶部旁边展示大号触发条件按钮） */}
      <AbilityGridPicker
        abilities={learnedAbilities}
        abilityExperiences={combat.abilityExperiences}
        allowEmpty
        anchor={activePicker?.anchor ?? null}
        itemAmountFor={itemAmountFor}
        levelFor={abilityLevelFor}
        onClose={() => setActivePicker(null)}
        onEditCondition={
          currentPickerAbility
            ? () => setEditingTrigger({ kind: 'ability', id: currentPickerAbility.abilityId })
            : currentPickerItem
              ? () => setEditingTrigger({ kind: 'item', id: currentPickerItem.itemId })
              : undefined
        }
        onSelect={value => {
          if (activePicker !== null) {
            update({ abilitySlots: withAbility(activePicker.index, value) })
          }
          setActivePicker(null)
        }}
        open={!locked && activePicker !== null}
        slotIndex={activePicker?.index}
        talismanItems={pickerTalismanItems}
        title={pickerTitle}
        triggerCount={
          currentPickerAbility
            ? (combat.abilityTriggers?.[currentPickerAbility.abilityId]?.length ?? 0)
            : currentPickerItem
              ? ((combat.consumableTriggers?.[currentPickerItem.itemId] ?? currentPickerItem.triggers ?? []).length)
              : 0
        }
        value={currentPickerValue}
      />
      {/* 触发条件编辑器（功法或符箓维度） */}
      {!locked && editingTrigger !== null && (
        <CombatTriggerEditor
          defaultTriggers={editingTrigger.kind === 'item'
            ? (findItemInCatalog(items, editingTrigger.id)?.triggers ?? [])
            : (abilities.find(entry => entry.abilityId === editingTrigger.id)?.defaultTriggers ?? [])}
          initialTriggers={editingTrigger.kind === 'item'
            ? (combat.consumableTriggers?.[editingTrigger.id] ?? findItemInCatalog(items, editingTrigger.id)?.triggers ?? [])
            : (combat.abilityTriggers?.[editingTrigger.id] ?? abilities.find(entry => entry.abilityId === editingTrigger.id)?.defaultTriggers ?? [])}
          onClose={() => setEditingTrigger(null)}
          onSave={saveTriggers}
          open
          title={t('app.abilitiesPanel.triggerEditorTitle', {
            name: editingTrigger.kind === 'item'
              ? (findItemInCatalog(items, editingTrigger.id)?.name ?? editingTrigger.id)
              : (abilities.find(entry => entry.abilityId === editingTrigger.id)?.name ?? editingTrigger.id),
          })}
        />
      )}
    </div>
  )
})
