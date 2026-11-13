import { memo, useCallback, useMemo, useState } from 'react'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { resolvePlayerActorId } from '../../game/avatarStore'
import {
  MAX_REALM_LEVEL,
  ROOT_LEVEL_CAP,
  TRAIT_DEFINITIONS,
  calculateAbilityLevelAndProgress,
  calculateRealmInfo,
  calculateTraitCapacity,
  getAbilityCumulativeExpForLevel,
  getRootCumulativeExpForLevel,
  getRootPointsForExp,
} from '../../game/combatConstants'
import { describeAbility, describeCombatTrigger } from '../../game/format'
import { resolveCatalogItemMetadata, resolveItemCategoryLabel } from '../../game/ids'
import { resolveItemIconUrl } from '../../game/itemIcons'
import type {
  SimulatorAbilitySlotDto, SimulatorAutoUseSlotDto, SimulatorLinggenDto, SimulatorProfileDto,
} from '../../game/simulator/simulatorTypes'
import type { SimulatorProfileSubTab } from '../../game/simulator/simulatorStore'
import { ItemGridPicker, ItemUseSlot, type ItemPickerEntry } from '../ItemGridPicker'
import { AbilityGridPicker } from '../AbilityGridPicker'
import { CharacterAvatar } from '../CharacterAvatar'
import { ConditionedTile } from '../ConditionedTile'
import { resolveAbilityAsideCards } from '../EffectTooltip'
import { ItemIcon, ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { CombatTriggerEditor } from '../CombatTriggerEditor'
import { CombatAttributePanel } from '../CombatAttributePanel'
import { RealmBadge } from '../RealmBadge'
import { TraitBarItem } from '../TraitBarItem'
import { FloatingPopover } from '../FloatingPopover'
import { StatGlyph } from '../StatGlyph'
import type { CombatAttributes, CombatTrigger, EquipmentSlot } from '../../game/types'

interface Props {
  member: SimulatorProfileDto
  index: number
  disabled: boolean
  allMembers: readonly SimulatorProfileDto[]
  subTab?: SimulatorProfileSubTab
  onSubTabChange?: (next: SimulatorProfileSubTab) => void
  resolvedAttributes?: CombatAttributes | null
  resolvingPreview?: boolean
  onChange: (next: SimulatorProfileDto) => void
}

type SubTabKey = 'profile' | 'attributes' | 'equipment' | 'abilities' | 'consumables'
type TriggerTarget = { kind: 'ability' | 'consumable'; key: string } | null
type PickerTarget =
  | { kind: 'equipment'; match: readonly EquipmentSlot[]; replaceSlot?: EquipmentSlot; currentItemId?: string | null }
  | { kind: 'consumable'; slotIndex: number; group: 'potion' | 'general'; currentItemId?: string | null }

const MAX_TRAIT_LEVEL = 20
const ABILITY_SLOT_COUNT = 5
const MAX_ENHANCEMENT = 20
const DEFAULT_LINGGEN: SimulatorLinggenDto = { metal: 20, wood: 20, water: 20, fire: 20, earth: 20 }
const LINGGEN_KEYS: readonly (keyof SimulatorLinggenDto)[] = ['metal', 'wood', 'water', 'fire', 'earth']

const CORE_FIELDS = ['staminaExp', 'meridiansExp', 'attackExp', 'boneExp', 'agilityExp', 'consciousnessExp'] as const
const CORE_LABELS: Record<(typeof CORE_FIELDS)[number], string> = {
  staminaExp: 'combat.simulator.core.stamina',
  meridiansExp: 'combat.simulator.core.meridians',
  attackExp: 'combat.simulator.core.attack',
  boneExp: 'combat.simulator.core.bone',
  agilityExp: 'combat.simulator.core.agility',
  consciousnessExp: 'combat.simulator.core.consciousness',
}
const CORE_GLYPHS: Record<(typeof CORE_FIELDS)[number], string> = {
  staminaExp: 'stamina',
  meridiansExp: 'meridians',
  attackExp: 'attack',
  boneExp: 'bone',
  agilityExp: 'agility',
  consciousnessExp: 'consciousness',
}

const EQUIPMENT_SLOTS: readonly { slot: EquipmentSlot; labelKey: string; match: readonly EquipmentSlot[] }[] = [
  { slot: 'weapon', labelKey: 'app.equipmentPanel.slotWeapon', match: ['weapon', 'two-hand'] },
  { slot: 'off-hand', labelKey: 'app.equipmentPanel.slotOffHand', match: ['off-hand'] },
  { slot: 'head', labelKey: 'app.equipmentPanel.slotHead', match: ['head'] },
  { slot: 'armor', labelKey: 'app.equipmentPanel.slotArmor', match: ['armor'] },
  { slot: 'legs', labelKey: 'app.equipmentPanel.slotLegs', match: ['legs'] },
  { slot: 'trinket', labelKey: 'app.equipmentPanel.slotTrinket', match: ['ring', 'neck', 'trinket', 'talisman'] },
]

const LEFT_EQUIP_SLOTS: readonly EquipmentSlot[] = ['weapon', 'head', 'legs']
const RIGHT_EQUIP_SLOTS: readonly EquipmentSlot[] = ['off-hand', 'armor', 'trinket']

export const SimulatorProfileEditor = memo(function SimulatorProfileEditor({
  member,
  index,
  disabled,
  allMembers,
  subTab: controlledSubTab,
  onSubTabChange,
  resolvedAttributes,
  resolvingPreview = false,
  onChange,
}: Props) {
  const t = useT()
  const items = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const abilities = useGameStore(state => state.abilities)

  const [internalSubTab, setInternalSubTab] = useState<SubTabKey>('profile')
  const subTab = controlledSubTab ?? internalSubTab
  const setSubTab = useCallback((next: SubTabKey) => {
    setInternalSubTab(next)
    onSubTabChange?.(next)
  }, [onSubTabChange])
  const [picker, setPicker] = useState<{ anchor: HTMLElement; target: PickerTarget } | null>(null)
  const [abilityAnchor, setAbilityAnchor] = useState<{ el: HTMLElement; slot: number } | null>(null)
  const [triggerTarget, setTriggerTarget] = useState<TriggerTarget>(null)
  const [traitPickerOpen, setTraitPickerOpen] = useState(false)

  const itemName = useCallback((id: string) =>
    items.find(item => item.itemId === id)?.name
    ?? equipmentCatalog.find(eq => eq.equipmentId === id)?.name
    ?? id, [equipmentCatalog, items])

  const abilityName = useCallback((id: string | null) =>
    id ? (abilities.find(a => a.abilityId === id)?.name ?? id) : t('combat.simulator.emptySlot'), [abilities, t])

  const patch = useCallback((partial: Partial<SimulatorProfileDto>) => onChange({ ...member, ...partial }), [member, onChange])

  const num = (value: string, fallback: number) => {
    const v = Number(value)
    return Number.isFinite(v) ? v : fallback
  }

  const integerOr = (value: string, fallback: number) => {
    const v = Number(value)
    return Number.isInteger(v) ? v : fallback
  }

  // 无限制沙盒：展示所有战斗可用消耗品（丹药即时栏排除持续丹）
  const combatEligibleConsumables: ItemPickerEntry[] = useMemo(() =>
    items
      .filter(item => item.category === 'consumable' && (item.usableBars ?? []).includes('combat'))
      .map(item => ({
        itemId: item.itemId,
        name: item.name,
        description: item.description ?? '',
        category: item.category ?? '',
        tier: item.tier ?? 1,
        amount: 1,
        definition: item,
      }))
      .sort((a, b) => a.tier - b.tier || a.name.localeCompare(b.name)),
  [items])

  const consumablePickerItems = useCallback((group: 'potion' | 'general'): ItemPickerEntry[] =>
    combatEligibleConsumables.filter(entry =>
      group !== 'potion' || (entry.definition?.subCategory === 'pill' && !entry.definition?.isContinuous)
    ),
  [combatEligibleConsumables])

  // 无限制沙盒：全量列出部位匹配装备，不限制境界或背包持有
  const equipmentPickerItems = useCallback((match: readonly EquipmentSlot[]): ItemPickerEntry[] =>
    equipmentCatalog
      .filter(eq => match.includes(eq.slot))
      .map(eq => ({
        itemId: eq.equipmentId,
        name: eq.name,
        description: eq.description ?? '',
        category: eq.slot,
        tier: eq.tier ?? 1,
        amount: 1,
        equipmentDefinition: eq,
        definition: items.find(i => i.itemId === eq.equipmentId),
      }))
      .sort((a, b) => a.tier - b.tier || a.name.localeCompare(b.name)),
  [equipmentCatalog, items])

  const abilityTriggers = member.abilityTriggers ?? {}
  const consumableTriggers = member.consumableTriggers ?? {}
  const triggerMode = useCallback((dict: Record<string, unknown[]>, key: string): 'default' | 'none' | 'custom' =>
    !Object.prototype.hasOwnProperty.call(dict, key) ? 'default' : (dict[key]?.length ?? 0) === 0 ? 'none' : 'custom', [])

  const resolveDefaultTriggers = useCallback((kind: 'ability' | 'consumable', key: string): CombatTrigger[] => {
    if (kind === 'ability') return (abilities.find(a => a.abilityId === key)?.defaultTriggers ?? []) as CombatTrigger[]
    return (items.find(i => i.itemId === key)?.triggers ?? []) as CombatTrigger[]
  }, [abilities, items])

  const resolveActiveTriggers = useCallback((kind: 'ability' | 'consumable', key: string): CombatTrigger[] => {
    const dict = kind === 'ability' ? abilityTriggers : consumableTriggers
    const mode = triggerMode(dict, key)
    if (mode === 'none') return []
    if (mode === 'custom') return (dict[key] ?? []) as CombatTrigger[]
    return resolveDefaultTriggers(kind, key)
  }, [abilityTriggers, consumableTriggers, resolveDefaultTriggers, triggerMode])

  const resolveTriggerSummary = useCallback((kind: 'ability' | 'consumable', key: string): string => {
    const dict = kind === 'ability' ? abilityTriggers : consumableTriggers
    const mode = triggerMode(dict, key)
    if (mode === 'none') return t('combat.simulator.triggerNone')
    const list = resolveActiveTriggers(kind, key)
    return list.length > 0 ? list.map(describeCombatTrigger).join(' · ') : t('combat.simulator.triggerDefault')
  }, [abilityTriggers, consumableTriggers, resolveActiveTriggers, t, triggerMode])

  const saveTrigger = (target: NonNullable<TriggerTarget>, triggers: CombatTrigger[] | null) => {
    const dict = target.kind === 'ability' ? { ...abilityTriggers } : { ...consumableTriggers }
    dict[target.key] = triggers ?? []
    if (target.kind === 'ability') patch({ abilityTriggers: dict })
    else patch({ consumableTriggers: dict })
    setTriggerTarget(null)
  }

  const realmInfo = useMemo(
    () => calculateRealmInfo(member.experience, member.realmLevel),
    [member.experience, member.realmLevel],
  )

  const traitIdSet = useMemo(() => new Set(member.traitIds ?? []), [member.traitIds])
  const unlockedMap = useMemo(() =>
    new Map((member.unlockedTraits ?? []).map(tr => [tr.id, tr])), [member.unlockedTraits])
  const traitCapacity = useMemo(() => calculateTraitCapacity(member.realmLevel), [member.realmLevel])
  const traitCost = useMemo(() =>
    (member.traitIds ?? []).reduce((sum, id) => sum + (TRAIT_DEFINITIONS[id]?.cost ?? 0), 0), [member.traitIds])

  const equippedTraits = useMemo(() =>
    (member.traitIds ?? [])
      .map(id => TRAIT_DEFINITIONS[id])
      .filter((def): def is NonNullable<typeof def> => Boolean(def)),
  [member.traitIds])

  const allTraitDefs = useMemo(() =>
    Object.values(TRAIT_DEFINITIONS).sort((a, b) => b.quality - a.quality || a.cost - b.cost || a.id.localeCompare(b.id)),
  [])

  // 无限制沙盒：特质自由装卸，不受道韵容量与解锁限制
  const toggleTrait = useCallback((id: string) => {
    const ids = member.traitIds ?? []
    if (ids.includes(id)) {
      patch({ traitIds: ids.filter(x => x !== id) })
      return
    }
    const partial: Partial<SimulatorProfileDto> = { traitIds: [...ids, id] }
    if (!unlockedMap.has(id))
      partial.unlockedTraits = [...(member.unlockedTraits ?? []), { id, level: 1, exp: 0 }]
    patch(partial)
  }, [member.traitIds, member.unlockedTraits, patch, unlockedMap])

  const updateTraitLevel = useCallback((id: string, level: number) => {
    const existing = member.unlockedTraits ?? []
    const found = existing.some(tr => tr.id === id)
    const nextUnlocked = found
      ? existing.map(tr => tr.id === id ? { ...tr, level } : tr)
      : [...existing, { id, level, exp: 0 }]
    patch({ unlockedTraits: nextUnlocked })
  }, [member.unlockedTraits, patch])

  const equipment = member.equipment ?? []
  const equipmentDefOf = useCallback((itemId: string) =>
    equipmentCatalog.find(eq => eq.equipmentId === itemId), [equipmentCatalog])
  const equippedFor = useCallback((match: readonly EquipmentSlot[]) =>
    equipment.filter(entry => {
      const def = equipmentDefOf(entry.itemId)
      return def ? match.includes(def.slot) : false
    }), [equipment, equipmentDefOf])

  const equipItem = useCallback((itemId: string, replaceSlot?: EquipmentSlot) => {
    const def = equipmentDefOf(itemId)
    if (!def) return
    const slot = replaceSlot ?? def.slot
    let next = equipment.filter(entry => {
      const entryDef = equipmentDefOf(entry.itemId)
      return entryDef && entryDef.slot !== slot
    })
    if (def.slot === 'two-hand')
      next = next.filter(entry => {
        const entryDef = equipmentDefOf(entry.itemId)
        return entryDef && entryDef.slot !== 'weapon' && entryDef.slot !== 'off-hand'
      })
    if (def.slot === 'weapon' || def.slot === 'off-hand')
      next = next.filter(entry => equipmentDefOf(entry.itemId)?.slot !== 'two-hand')
    patch({ equipment: [...next, { itemId, enhancementLevel: 0 }] })
  }, [equipment, equipmentDefOf, patch])

  const abilitySlots = useMemo(() => {
    const list: SimulatorAbilitySlotDto[] = []
    for (let i = 0; i < Math.max(ABILITY_SLOT_COUNT, member.abilitySlots.length); i++)
      list.push(member.abilitySlots?.[i] ?? { abilityId: null, level: 1 })
    return list
  }, [member.abilitySlots])

  const writeAbilitySlot = useCallback((i: number, partial: Partial<SimulatorAbilitySlotDto>) => {
    const prevSlot = abilitySlots[i]
    const nextExpMap = { ...(member.abilityExperiences ?? {}) }
    const next = abilitySlots.map(slot => ({ ...slot }))
    if (partial.abilityId !== undefined) {
      const pickedId = partial.abilityId
      if (pickedId) {
        const dupIdx = next.findIndex((s, j) => j !== i && s.abilityId === pickedId)
        if (dupIdx >= 0 && dupIdx > 0 && i > 0) {
          // 功法若已在另一法术槽装配，则交换位置，避免产生重复槽位校验错误
          const dupLevel = next[dupIdx].level
          next[dupIdx] = { abilityId: prevSlot.abilityId, level: prevSlot.level }
          next[i] = {
            abilityId: pickedId,
            level: partial.level ?? Math.max(1, Math.min(127, dupLevel || calculateAbilityLevelAndProgress(nextExpMap[pickedId] ?? 0).level)),
          }
        } else {
          const savedLevel = calculateAbilityLevelAndProgress(nextExpMap[pickedId] ?? 0).level
          next[i] = {
            abilityId: pickedId,
            level: partial.level ?? Math.max(1, Math.min(127, savedLevel || 1)),
          }
        }
      } else {
        next[i] = { abilityId: null, level: 1 }
      }
    } else if (partial.level !== undefined) {
      const safeLevel = Math.max(1, Math.min(127, partial.level))
      next[i] = { ...prevSlot, level: safeLevel }
    }
    const target = next[i]
    if (target.abilityId) {
      if (target.level <= 1) delete nextExpMap[target.abilityId]
      else nextExpMap[target.abilityId] = getAbilityCumulativeExpForLevel(target.level)
    }
    patch({ abilitySlots: next, abilityExperiences: nextExpMap })
  }, [abilitySlots, member.abilityExperiences, patch])

  const consumableSlots = useMemo(() => {
    const real = member.combatAutoUseSlots ?? []
    const list: SimulatorAutoUseSlotDto[] = []
    for (let i = 0; i < 6; i++) {
      const group = i < 3 ? 'potion' : 'general'
      const slotIndex = i % 3
      const found = real.find(slot => slot.slotIndex === slotIndex && slot.group === group && slot.actionType === 'combat')
      list.push(found ?? {
        actionType: 'combat', slotIndex, itemId: null, quantity: 1, enabled: false,
        remainingDurationSeconds: 0, cooldownUntilUnixSeconds: 0, group,
      })
    }
    return list
  }, [member.combatAutoUseSlots])

  const writeConsumableSlot = useCallback((i: number, partial: Partial<SimulatorAutoUseSlotDto>) => {
    const slot = consumableSlots[i]
    const merged = { ...slot, ...partial }
    const real = member.combatAutoUseSlots ?? []
    const idx = real.findIndex(x => x.slotIndex === slot.slotIndex && x.group === slot.group && x.actionType === 'combat')
    const next = [...real]
    if (idx >= 0) next[idx] = merged
    else next.push(merged)
    patch({ combatAutoUseSlots: next })
  }, [consumableSlots, member.combatAutoUseSlots, patch])

  const respawnTargets = allMembers.filter((m, i) => i !== index && m.sourcePlayerId)
  const slottedAbilitiesCount = abilitySlots.filter(s => s.abilityId !== null).length
  const enabledConsumablesCount = consumableSlots.filter(s => s.enabled && s.itemId !== null).length

  const realmOptions = useMemo(() => {
    const list: { level: number; label: string }[] = []
    for (let lv = 1; lv <= MAX_REALM_LEVEL; lv += 1) {
      const info = calculateRealmInfo(0, lv)
      const label = lv <= 18 ? `${info.stageName}·${info.minorName}` : info.minorName
      list.push({ level: lv, label: `Lv.${lv} ${label}` })
    }
    return list
  }, [t])

  const hasCarryState = (member.combatBuffCarry?.length ?? 0) > 0
    || (member.buffs?.length ?? 0) > 0
    || (member.combatSkills?.cooldowns?.length ?? 0) > 0
    || Boolean(member.combatSkills?.castingAbilityId)

  const traitPopoverContent = (
    <div className="wasm-sim-trait-popover">
      <div className="wasm-sim-trait-popover-head">
        <span>{t('combat.simulator.addTraitBtn')}</span>
        <span className="wasm-sim-trait-cap">{traitCost}/{traitCapacity}</span>
      </div>
      <div className="wasm-sim-trait-popover-list">
        {allTraitDefs.map(def => {
          const equipped = traitIdSet.has(def.id)
          const level = unlockedMap.get(def.id)?.level ?? 1
          return (
            <TraitBarItem
              key={def.id}
              def={def}
              level={level}
              mode="insight"
              isEquipped={equipped}
              disabled={disabled}
              onClick={() => { if (!disabled) toggleTrait(def.id) }}
            />
          )
        })}
      </div>
    </div>
  )

  return (
    <div className="wasm-sim-editor">
      {/* 子 Tab 导航：基础与特质 / 详细属性 / 装备 / 功法 / 消耗品 */}
      <div className="sim-sub-tabs">
        <button type="button" className={`sim-sub-tab-btn ${subTab === 'profile' ? 'is-active' : ''}`}
          onClick={() => setSubTab('profile')}>
          {t('combat.simulator.subTabProfile')}
        </button>
        <button type="button" className={`sim-sub-tab-btn ${subTab === 'attributes' ? 'is-active' : ''}`}
          onClick={() => setSubTab('attributes')}>
          {t('combat.simulator.subTabAttributes')}
        </button>
        <button type="button" className={`sim-sub-tab-btn ${subTab === 'equipment' ? 'is-active' : ''}`}
          onClick={() => setSubTab('equipment')}>
          {t('combat.simulator.subTabEquipment')} ({equipment.length})
        </button>
        <button type="button" className={`sim-sub-tab-btn ${subTab === 'abilities' ? 'is-active' : ''}`}
          onClick={() => setSubTab('abilities')}>
          {t('combat.simulator.subTabAbilities')} ({slottedAbilitiesCount}/{ABILITY_SLOT_COUNT})
        </button>
        <button type="button" className={`sim-sub-tab-btn ${subTab === 'consumables' ? 'is-active' : ''}`}
          onClick={() => setSubTab('consumables')}>
          {t('combat.simulator.subTabConsumables')} ({enabledConsumablesCount}/6)
        </button>
      </div>

      {/* 1. 基础信息、境界下拉与战斗经验、六维点数、已装配特质 */}
      {subTab === 'profile' && (
        <div className="sim-stats-grid-container">
          <div className="sim-stats-category-block">
            <h5 className="sim-stats-category-title">{t('combat.simulator.basicInfoSection')}</h5>
            <div className="sim-stats-grid">
              <label className="sim-stat-field">
                <span className="sim-stat-name">{t('combat.simulator.fieldName')}</span>
                <input className="sim-input" disabled={disabled} value={member.displayName}
                  onChange={e => patch({ displayName: e.target.value })} />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">
                  <StatGlyph glyph="realm" /> {t('combat.simulator.realmLevel')}
                </span>
                <select
                  className="sim-select"
                  disabled={disabled}
                  value={Math.min(MAX_REALM_LEVEL, Math.max(1, Math.round(member.realmLevel)))}
                  onChange={e => patch({ realmLevel: integerOr(e.target.value, member.realmLevel) })}
                >
                  {realmOptions.map(opt => (
                    <option key={opt.level} value={opt.level}>{opt.label}</option>
                  ))}
                </select>
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">
                  <StatGlyph glyph="combat-exp" /> {t('combat.simulator.combatExpField')}
                </span>
                <input type="number" className="sim-input" min={0} step={1} disabled={disabled}
                  value={Math.round(member.combatExperience)}
                  onChange={e => patch({ combatExperience: Math.max(0, Math.round(num(e.target.value, Math.round(member.combatExperience)))) })} />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">{t('combat.simulator.respawn')}</span>
                <select className="sim-select" disabled={disabled} value={member.respawnPolicy}
                  onChange={e => patch({ respawnPolicy: integerOr(e.target.value, member.respawnPolicy) })}>
                  <option value={0}>{t('social.respawn.immediate')}</option>
                  <option value={1}>{t('social.respawn.allAlliesReady')}</option>
                  <option value={2}>{t('social.respawn.targetAllyAlive')}</option>
                </select>
              </label>
              {member.respawnPolicy === 2 && (
                <label className="sim-stat-field">
                  <span className="sim-stat-name">{t('combat.simulator.respawnTarget')}</span>
                  <select className="sim-select" disabled={disabled} value={member.respawnTargetPlayerId ?? ''}
                    onChange={e => patch({ respawnTargetPlayerId: e.target.value === '' ? null : e.target.value })}>
                    <option value="">{t('social.respawn.targetPlaceholder')}</option>
                    {respawnTargets.map(m => m.sourcePlayerId && (
                      <option key={m.sourcePlayerId} value={m.sourcePlayerId}>{m.displayName}</option>
                    ))}
                  </select>
                </label>
              )}
            </div>

            <details className="wasm-sim-advanced">
              <summary>{t('combat.simulator.advancedState')}</summary>
              <div className="wasm-sim-advanced-body">
                <div className="sim-stats-grid">
                  <label className="sim-stat-field">
                    <span className="sim-stat-name">{t('combat.simulator.linggenTitle')}</span>
                    <label className="sim-consumable-toggle">
                      <input type="checkbox" disabled={disabled} checked={member.confirmedLinggen !== null}
                        onChange={e => patch({
                          confirmedLinggen: e.target.checked ? { ...DEFAULT_LINGGEN } : null,
                          heavenGateEntered: e.target.checked,
                        })} />
                      <span>{t('combat.simulator.linggenEnable')}</span>
                    </label>
                  </label>
                </div>
                {member.confirmedLinggen && (
                  <div className="sim-stats-grid">
                    {LINGGEN_KEYS.map(elem => (
                      <label key={elem} className="sim-stat-field">
                        <span className="sim-stat-name">
                          <StatGlyph glyph={`element-${elem}`} /> {t(`combat.simulator.linggen.${elem}`)}
                        </span>
                        <input type="number" step="any" min={0} className="sim-input" disabled={disabled}
                          value={member.confirmedLinggen![elem]}
                          onChange={e => patch({
                            confirmedLinggen: {
                              ...member.confirmedLinggen!,
                              [elem]: Math.max(0, num(e.target.value, member.confirmedLinggen![elem])),
                            },
                          })} />
                      </label>
                    ))}
                  </div>
                )}
                {hasCarryState && (
                  <div className="wasm-sim-state">
                    {(member.buffs ?? []).map((b, i) => (
                      <span key={`b${i}`} className="wasm-sim-tag">
                        <ItemIcon itemId={b.sourceItemId ?? b.buffId} />{b.name || b.buffId}×{b.stacks}{' '}
                        {Math.max(0, b.expiresAtUnixSeconds - member.capturedAtUnixSeconds).toFixed(1)}s
                      </span>
                    ))}
                    {(member.combatBuffCarry ?? []).map((b, i) => (
                      <span key={`c${i}`} className="wasm-sim-tag">
                        <ItemIcon itemId={b.buffId} />{b.name || b.buffId}×{b.stacks} {b.remainingSeconds.toFixed(1)}s
                      </span>
                    ))}
                    {(member.combatSkills?.cooldowns ?? []).map((c, i) => (
                      <span key={`cd${i}`} className="wasm-sim-tag">
                        <ItemIcon itemId={c.abilityId} />{abilityName(c.abilityId)} {c.remainingSeconds.toFixed(1)}s
                      </span>
                    ))}
                    {member.combatSkills?.castingAbilityId && (
                      <span className="wasm-sim-tag">
                        {t('combat.simulator.casting')}
                        <ItemIcon itemId={member.combatSkills.castingAbilityId} />
                        {abilityName(member.combatSkills.castingAbilityId)}
                      </span>
                    )}
                  </div>
                )}
              </div>
            </details>
          </div>

          <div className="sim-stats-category-block">
            <h5 className="sim-stats-category-title">{t('combat.simulator.core')}</h5>
            <div className="sim-stats-grid">
              {CORE_FIELDS.map(field => {
                const currentPoints = getRootPointsForExp(member.coreAttributes[field])
                return (
                  <label key={field} className="sim-stat-field">
                    <span className="sim-stat-name">
                      <StatGlyph glyph={CORE_GLYPHS[field]} /> {t(CORE_LABELS[field])}
                    </span>
                    <input type="number" min={0} max={ROOT_LEVEL_CAP} step={1} className="sim-input" disabled={disabled}
                      value={currentPoints}
                      onChange={e => {
                        const raw = num(e.target.value, currentPoints)
                        const nextPoints = Math.max(0, Math.min(ROOT_LEVEL_CAP, Math.round(raw)))
                        patch({
                          coreAttributes: {
                            ...member.coreAttributes,
                            [field]: getRootCumulativeExpForLevel(nextPoints),
                          },
                        })
                      }} />
                  </label>
                )
              })}
            </div>
          </div>

          {/* 只显示当前已装配特质，通过悬浮选择器无限制自由装卸 */}
          <div className="sim-stats-category-block">
            <div className="wasm-sim-section-head">
              <h5 className="sim-stats-category-title">
                {t('combat.simulator.equippedTraits')}（{traitCost}/{traitCapacity}）
              </h5>
              <FloatingPopover
                open={traitPickerOpen}
                onOpenChange={setTraitPickerOpen}
                trigger="click"
                placement="bottom-end"
                interactive
                size="medium"
                content={traitPopoverContent}
              >
                <button type="button" className="sim-btn sim-btn--secondary wasm-sim-add-trait-btn" disabled={disabled}>
                  ＋ {t('combat.simulator.addTraitBtn')}
                </button>
              </FloatingPopover>
            </div>
            {equippedTraits.length === 0 ? (
              <div className="wasm-sim-empty">{t('combat.simulator.noEquippedTraits')}</div>
            ) : (
              <div className="wasm-sim-equipped-traits">
                {equippedTraits.map(def => {
                  const level = unlockedMap.get(def.id)?.level ?? 1
                  return (
                    <div key={def.id} className="wasm-sim-trait-row">
                      <TraitBarItem
                        def={def}
                        level={level}
                        mode="insight"
                        isEquipped
                      />
                      <label className="wasm-sim-inline-level">
                        <span>{t('combat.simulator.traitLevelLabel')}</span>
                        <input type="number" className="sim-input wasm-sim-level-input"
                          min={1} max={MAX_TRAIT_LEVEL} disabled={disabled} value={level}
                          onChange={e => {
                            const v = integerOr(e.target.value, -1)
                            if (v >= 1 && v <= MAX_TRAIT_LEVEL) updateTraitLevel(def.id, v)
                          }} />
                      </label>
                      <button type="button" className="sim-equip-remove-btn" disabled={disabled}
                        onClick={() => toggleTrait(def.id)} aria-label={t('combat.simulator.remove')}>
                        ×
                      </button>
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        </div>
      )}

      {/* 1.5. 详细属性（独立页签复用 CombatAttributePanel） */}
      {subTab === 'attributes' && (
        <div className="sim-attributes-panel">
          {resolvedAttributes ? (
            <CombatAttributePanel attributes={resolvedAttributes} />
          ) : (
            <div className="sim-results-empty">
              {resolvingPreview
                ? t('combat.simulator.attributesResolvingHint')
                : t('combat.simulator.attributesEmptyHint')}
            </div>
          )}
        </div>
      )}

      {/* 2. 穿戴装备（复用角色六部位舞台 + 下方强化微调/多饰品条，无背包与境界限制） */}
      {subTab === 'equipment' && (
        <div className="sim-equipment-editor-panel">
          <div className="equipment-stage sim-equipment-stage-wrap">
            {[
              { colClass: 'equipment-slot-col--left', slots: LEFT_EQUIP_SLOTS },
              null,
              { colClass: 'equipment-slot-col--right', slots: RIGHT_EQUIP_SLOTS },
            ].map((col, colIdx) => {
              if (!col) {
                return (
                  <div key="avatar-center" className="equipment-avatar-center">
                    <div className="equipment-avatar-phantom-halo" aria-hidden="true" />
                    <div className="equipment-avatar-phantom-stage">
                      <CharacterAvatar
                        actorId={resolvePlayerActorId(member.avatar || 'avatar.default')}
                        name={member.displayName}
                        role="player"
                      />
                    </div>
                    <div className="equipment-avatar-meta">
                      <strong className="equipment-avatar-name">{member.displayName}</strong>
                      <div className="equipment-avatar-badge-wrap">
                        <RealmBadge realmInfo={realmInfo} />
                      </div>
                    </div>
                  </div>
                )
              }

              return (
                <div key={col.colClass + colIdx} className={`equipment-slot-col ${col.colClass}`}>
                  {col.slots.map(slotKey => {
                    const conf = EQUIPMENT_SLOTS.find(s => s.slot === slotKey)!
                    const entries = equippedFor(conf.match)
                    const primary = entries[0]
                    const slotName = t(conf.labelKey)
                    const isPicking = picker?.target.kind === 'equipment' && picker.target.match === conf.match

                    if (primary) {
                      const eqDef = equipmentDefOf(primary.itemId)
                      const itemDef = items.find(i => i.itemId === primary.itemId)
                      const displayMeta = resolveCatalogItemMetadata(primary.itemId, {
                        itemDefinition: itemDef,
                        equipmentDefinition: eqDef,
                        name: itemName(primary.itemId),
                        category: eqDef?.slot ?? slotKey,
                        t,
                      })
                      const displayName = primary.enhancementLevel > 0
                        ? `${itemName(primary.itemId)} +${primary.enhancementLevel}`
                        : itemName(primary.itemId)

                      return (
                        <div key={slotKey} className="sim-stage-slot-holder">
                          <ItemTooltipTarget
                            itemId={primary.itemId}
                            className="equipment-slot-anchor"
                            definition={itemDef}
                            equipmentDefinition={eqDef}
                            enhancementLevel={primary.enhancementLevel}
                            metadata={displayMeta}
                          >
                            <button
                              type="button"
                              className={`equipment-slot-btn is-equipped${isPicking ? ' is-picking' : ''}`}
                              disabled={disabled}
                              aria-label={`${slotName}: ${displayName}`}
                              onClick={e => {
                                const anchor = e.currentTarget
                                setPicker(prev =>
                                  prev?.target.kind === 'equipment' && prev.target.match === conf.match
                                    ? null
                                    : {
                                        anchor,
                                        target: {
                                          kind: 'equipment',
                                          match: conf.match,
                                          replaceSlot: eqDef?.slot,
                                          currentItemId: primary.itemId,
                                        },
                                      },
                                )
                              }}
                            >
                              <ItemTile
                                itemId={primary.itemId}
                                name={displayName}
                                category={resolveItemCategoryLabel(eqDef?.slot ?? slotKey, t)}
                                className="inventory-cell"
                                enhancementLevel={primary.enhancementLevel}
                                level={displayMeta.level}
                                rarity={displayMeta.rarity}
                              />
                            </button>
                          </ItemTooltipTarget>
                          {slotKey === 'trinket' && entries.length > 1 && (
                            <span className="sim-trinket-extra-badge">×{entries.length}</span>
                          )}
                        </div>
                      )
                    }

                    return (
                      <button
                        key={slotKey}
                        type="button"
                        className={`equipment-slot-btn is-empty${isPicking ? ' is-picking' : ''}`}
                        disabled={disabled}
                        aria-label={slotName}
                        onClick={e => {
                          const anchor = e.currentTarget
                          setPicker(prev =>
                            prev?.target.kind === 'equipment' && prev.target.match === conf.match
                              ? null
                              : { anchor, target: { kind: 'equipment', match: conf.match, currentItemId: null } },
                          )
                        }}
                      >
                        <span className="equipment-slot-empty-label">{slotName}</span>
                      </button>
                    )
                  })}
                </div>
              )
            })}
          </div>

          {/* 舞台下方：当前全部穿戴强化微调（含多饰品管理）与增补饰品入口 */}
          <div className="sim-equipment-enhance-panel">
            <div className="sim-equipment-enhance-head">
              <span className="sim-section-sublabel">{t('combat.simulator.equipmentEnhancementStrip')}</span>
              <button
                type="button"
                className="sim-btn sim-btn--secondary wasm-sim-trinket-add"
                disabled={disabled}
                onClick={e => {
                  const trinketConf = EQUIPMENT_SLOTS.find(s => s.slot === 'trinket')!
                  const anchor = e.currentTarget
                  setPicker({ anchor, target: { kind: 'equipment', match: trinketConf.match, currentItemId: null } })
                }}
              >
                ＋ {t('app.equipmentPanel.slotTrinket')}
              </button>
            </div>
            {equipment.length === 0 ? (
              <p className="wasm-sim-hint">{t('combat.simulator.equipmentEmptyStageHint')}</p>
            ) : (
              <div className="sim-equipment-enhance-grid">
                {equipment.map((entry, idx) => {
                  const eqDef = equipmentDefOf(entry.itemId)
                  const slotLabel = resolveItemCategoryLabel(eqDef?.slot ?? 'equipment', t)
                  return (
                    <div key={`${entry.itemId}-${eqDef?.slot ?? idx}`} className="sim-equipment-enhance-card">
                      <button
                        type="button"
                        className="sim-equipment-enhance-item-btn"
                        disabled={disabled}
                        onClick={e => {
                          const anchor = e.currentTarget
                          const matchSlot = eqDef?.slot ?? 'weapon'
                          const matchedConf = EQUIPMENT_SLOTS.find(s => s.match.includes(matchSlot))
                          setPicker({
                            anchor,
                            target: {
                              kind: 'equipment',
                              match: matchedConf?.match ?? [matchSlot],
                              replaceSlot: eqDef?.slot,
                              currentItemId: entry.itemId,
                            },
                          })
                        }}
                      >
                        <ItemIcon itemId={entry.itemId} />
                        <span className="sim-equipment-enhance-meta">
                          <strong>{itemName(entry.itemId)}</strong>
                          <small>{slotLabel}{eqDef?.tier ? ` · T${eqDef.tier}` : ''}</small>
                        </span>
                      </button>
                      <div className="sim-stepper">
                        <button
                          type="button"
                          className="sim-stepper-btn"
                          disabled={disabled || entry.enhancementLevel <= 0}
                          onClick={() => patch({
                            equipment: equipment.map(x => x === entry ? { ...x, enhancementLevel: Math.max(0, x.enhancementLevel - 1) } : x),
                          })}
                          aria-label="-"
                        >-</button>
                        <span className="sim-enhance-prefix">+</span>
                        <input
                          type="number"
                          className="sim-input sim-enhance-input"
                          min={0}
                          max={MAX_ENHANCEMENT}
                          disabled={disabled}
                          value={entry.enhancementLevel}
                          onChange={e => {
                            const v = integerOr(e.target.value, -1)
                            if (v < 0 || v > MAX_ENHANCEMENT) return
                            patch({ equipment: equipment.map(x => x === entry ? { ...x, enhancementLevel: v } : x) })
                          }}
                        />
                        <button
                          type="button"
                          className="sim-stepper-btn"
                          disabled={disabled || entry.enhancementLevel >= MAX_ENHANCEMENT}
                          onClick={() => patch({
                            equipment: equipment.map(x => x === entry ? { ...x, enhancementLevel: Math.min(MAX_ENHANCEMENT, x.enhancementLevel + 1) } : x),
                          })}
                          aria-label="+"
                        >+</button>
                      </div>
                      <button
                        type="button"
                        className="sim-equip-remove-btn"
                        disabled={disabled}
                        onClick={() => patch({ equipment: equipment.filter(x => x !== entry) })}
                      >
                        {t('combat.simulator.unequipSlot')}
                      </button>
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        </div>
      )}

      {/* 3. 功法槽（复用战斗详情 .combat-slot-tray 五槽布局 + 下方自定义等级/条件条，无学习限制） */}
      {subTab === 'abilities' && (
        <div className="sim-skills-editor-panel">
          <p className="wasm-sim-hint">{t('combat.simulator.abilityUnlimitedHint')}</p>
          <div className="combat-slot-tray sim-combat-slot-tray">
            <section className="slot-group combat-slot-section combat-slot-section--abilities" aria-label={t('app.combatPanel.abilitiesAria')}>
              <span className="slot-group-label">{t('app.combatPanel.abilitiesGroup')}</span>
              <div className="combat-slot-grid combat-slot-grid--abilities">
                {abilitySlots.slice(0, ABILITY_SLOT_COUNT).map((slot, i) => {
                  const isInternal = i === 0
                  const ability = slot.abilityId ? abilities.find(a => a.abilityId === slot.abilityId) : undefined
                  const abilityIconUrl = resolveItemIconUrl(ability?.abilityId)
                  const slotTypeTag = isInternal
                    ? t('app.combatPanel.slotInternal')
                    : t('app.combatPanel.slotSpell', { n: i })
                  const ribbonLabel = isInternal
                    ? t('app.combatPanel.internalRibbon')
                    : t('app.combatPanel.spellRibbon')
                  const emptyBtnLabel = isInternal
                    ? t('app.combatPanel.chooseInternal')
                    : t('app.combatPanel.chooseSpell')
                  const slotAriaLabel = ability
                    ? t('app.combatPanel.currentAbilityAria', { name: ability.name })
                    : isInternal
                      ? t('app.combatPanel.chooseInternalSlotAria')
                      : t('app.combatPanel.chooseSpellSlotAria', { slot: i })
                  const activeTriggers = ability && !isInternal ? resolveActiveTriggers('ability', ability.abilityId) : []
                  const triggerCount = activeTriggers.length
                  const abilityExp = getAbilityCumulativeExpForLevel(slot.level)
                  const slotAsideCards = ability ? resolveAbilityAsideCards(ability, abilityExp) : undefined

                  const slotButton = (
                    <button
                      aria-label={slotAriaLabel}
                      className={`combat-ability-slot${isInternal ? ' is-internal-slot' : ' is-spell-slot'}${ability ? '' : ' combat-ability-slot--empty'}`}
                      disabled={disabled}
                      onClick={e => {
                        const anchor = e.currentTarget
                        setAbilityAnchor(prev => prev?.slot === i ? null : { el: anchor, slot: i })
                      }}
                      type="button"
                    >
                      <span className={`abilities-slot-tag-badge${isInternal ? ' is-internal' : ''}`}>
                        {slotTypeTag}
                      </span>
                      {ability ? (
                        <span className="item-tile combat-ability-cell" data-rarity={isInternal ? 'emperor' : 'mystic'}>
                          <span className="item-type-ribbon" aria-hidden="true"><span>{ribbonLabel}</span></span>
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

                  return (
                    <ConditionedTile
                      as="div"
                      asideCards={slotAsideCards}
                      className={`combat-slot-card${abilityAnchor?.slot === i ? ' is-picking' : ''}`}
                      condition={ability && !isInternal ? {
                        ariaLabel: t('app.combatPanel.editTriggersAria', { name: ability.name, count: triggerCount }),
                        hasTriggers: triggerCount > 0,
                        label: t('app.combatPanel.triggerConditionBtn', { count: triggerCount > 0 ? triggerCount : '' }),
                        onClick: () => setTriggerTarget({ kind: 'ability', key: ability.abilityId }),
                        title: t('app.combatPanel.editTriggersTitle'),
                      } : null}
                      content={ability ? describeAbility(ability, slot.level) : null}
                      extras={ability && (
                        <span className="combat-ability-level-control">
                          <span aria-hidden="true">Lv.</span>
                          <span className="combat-ability-level-value">{slot.level}</span>
                        </span>
                      )}
                      key={i}
                      triggerClassName="combat-ability-slot-shell"
                      triggerTabIndex={-1}
                    >
                      {slotButton}
                    </ConditionedTile>
                  )
                })}
              </div>
            </section>
          </div>

          {/* 功法槽下方独立条：直接自定义等级 1~127 与查看/编辑施放条件 */}
          <div className="sim-ability-levels-bar">
            {abilitySlots.slice(0, ABILITY_SLOT_COUNT).map((slot, i) => {
              const isInternal = i === 0
              const abilityId = slot.abilityId
              return (
                <div key={i} className={`sim-ability-level-row ${isInternal ? 'is-internal' : ''}`}>
                  <span className="sim-skill-slot-badge">
                    {isInternal ? t('combat.simulator.internalSlot') : t('combat.simulator.spellSlotLabel', { n: i })}
                  </span>
                  <button
                    type="button"
                    className="wasm-sim-slot-picker-btn sim-slot-inline-pick"
                    disabled={disabled}
                    onClick={e => setAbilityAnchor({ el: e.currentTarget, slot: i })}
                  >
                    {abilityId && <ItemIcon itemId={abilityId} />}
                    <span>{abilityName(abilityId)}</span>
                  </button>
                  {abilityId && (
                    <>
                      <div className="sim-stepper">
                        <span className="sim-skill-level-label">{t('combat.simulator.abilityLevelLabel')}</span>
                        <button
                          type="button"
                          className="sim-stepper-btn"
                          disabled={disabled || slot.level <= 1}
                          onClick={() => writeAbilitySlot(i, { level: Math.max(1, slot.level - 1) })}
                          aria-label="-"
                        >-</button>
                        <input
                          type="number"
                          min={1}
                          max={127}
                          step={1}
                          disabled={disabled}
                          className="sim-input sim-skill-level-input"
                          value={slot.level}
                          onChange={e => {
                            const v = integerOr(e.target.value, -1)
                            if (v >= 1 && v <= 127) writeAbilitySlot(i, { level: v })
                          }}
                        />
                        <button
                          type="button"
                          className="sim-stepper-btn"
                          disabled={disabled || slot.level >= 127}
                          onClick={() => writeAbilitySlot(i, { level: Math.min(127, slot.level + 1) })}
                          aria-label="+"
                        >+</button>
                      </div>
                      {!isInternal && (
                        <button
                          type="button"
                          disabled={disabled}
                          className={`sim-condition-pill-btn${resolveActiveTriggers('ability', abilityId).length > 0 ? ' has-triggers' : ''}`}
                          onClick={() => setTriggerTarget({ kind: 'ability', key: abilityId })}
                          title={resolveTriggerSummary('ability', abilityId)}
                        >
                          {resolveTriggerSummary('ability', abilityId)}
                        </button>
                      )}
                      <button
                        type="button"
                        className="sim-equip-remove-btn"
                        disabled={disabled}
                        onClick={() => writeAbilitySlot(i, { abilityId: null, level: 1 })}
                        aria-label={t('combat.simulator.remove')}
                      >×</button>
                    </>
                  )}
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* 4. 消耗品栏（复用战斗详情 .combat-slot-row--consumables 六槽布局 + ignoreQuantity 无数量限制） */}
      {subTab === 'consumables' && (
        <div className="sim-consumables-editor-panel">
          <p className="wasm-sim-hint">{t('combat.simulator.consumablesUnlimitedHint')}</p>
          <div className="combat-slot-tray sim-combat-slot-tray">
            <div className="combat-slot-row combat-slot-row--consumables">
              {([
                { group: 'potion' as const, offset: 0, label: t('app.combatPanel.consumablesGroup') },
                { group: 'general' as const, offset: 3, label: t('app.combatPanel.generalGroup') },
              ]).map(sec => (
                <section key={sec.group} aria-label={sec.label} className="slot-group combat-slot-section">
                  <span className="slot-group-label">{sec.label}</span>
                  <div className="auto-use-slots">
                    {consumableSlots.slice(sec.offset, sec.offset + 3).map((slot, localIdx) => {
                      const slotIndex = sec.offset + localIdx
                      const def = slot.itemId ? items.find(i => i.itemId === slot.itemId) : undefined
                      const selectedItem: ItemPickerEntry | null = def ? {
                        itemId: def.itemId,
                        name: def.name,
                        description: def.description ?? '',
                        category: resolveItemCategoryLabel(def.subCategory ?? def.category ?? 'consumable', t),
                        tier: def.tier ?? 1,
                        amount: 1,
                        definition: def,
                      } : slot.itemId ? {
                        itemId: slot.itemId,
                        name: slot.itemId,
                        description: '',
                        category: 'consumable',
                        tier: 1,
                        amount: 1,
                      } : null
                      const isPicking = picker?.target.kind === 'consumable' && picker.target.slotIndex === slotIndex
                      const activeCount = selectedItem ? resolveActiveTriggers('consumable', selectedItem.itemId).length : 0
                      return (
                        <div
                          key={slotIndex}
                          className={`auto-use-slot${isPicking ? ' is-picking' : ''}${selectedItem && !slot.enabled ? ' is-sim-disabled' : ''}`}
                        >
                          <ItemUseSlot
                            bar="combat"
                            ignoreQuantity
                            item={selectedItem}
                            conditionCount={selectedItem ? activeCount : undefined}
                            onCondition={selectedItem ? () => setTriggerTarget({ kind: 'consumable', key: selectedItem.itemId }) : undefined}
                            onClick={e => {
                              if (disabled) return
                              const anchor = e.currentTarget
                              setPicker(prev =>
                                prev?.target.kind === 'consumable' && prev.target.slotIndex === slotIndex
                                  ? null
                                  : {
                                      anchor,
                                      target: {
                                        kind: 'consumable',
                                        slotIndex,
                                        group: sec.group,
                                        currentItemId: slot.itemId,
                                      },
                                    },
                              )
                            }}
                          />
                        </div>
                      )
                    })}
                  </div>
                </section>
              ))}
            </div>
          </div>

          {/* 消耗品槽下方控制条：自定义物品、启停开关、触发条件（无数量框） */}
          <div className="sim-consumables-control-strip">
            {consumableSlots.map((slot, i) => {
              const group = i < 3 ? 'potion' : 'general'
              const isPotion = group === 'potion'
              const prefix = isPotion
                ? t('combat.simulator.consumableSlotPotionPrefix')
                : t('combat.simulator.consumableSlotGeneralPrefix')
              const slotItemId = slot.itemId
              return (
                <div key={i} className={`sim-consumable-strip-row ${isPotion ? 'is-potion' : ''}`}>
                  <span className="sim-skill-slot-badge">{prefix}{i % 3 + 1}</span>
                  <button
                    type="button"
                    disabled={disabled}
                    className="wasm-sim-slot-picker-btn sim-slot-inline-pick"
                    onClick={e => setPicker({
                      anchor: e.currentTarget,
                      target: { kind: 'consumable', slotIndex: i, group, currentItemId: slotItemId },
                    })}
                  >
                    {slotItemId
                      ? <><ItemIcon itemId={slotItemId} /><span>{itemName(slotItemId)}</span></>
                      : <span className="text-dim">{t('combat.simulator.consumableEmptyHint')}</span>}
                  </button>
                  {slotItemId && (
                    <>
                      <button
                        type="button"
                        disabled={disabled}
                        className={`sim-preset-btn wasm-sim-tiny-btn${slot.enabled ? ' is-active' : ''}`}
                        onClick={() => writeConsumableSlot(i, { enabled: !slot.enabled })}
                      >
                        {slot.enabled ? t('combat.simulator.consumableSlotEnable') : t('combat.simulator.consumableSlotDisable')}
                      </button>
                      <button
                        type="button"
                        disabled={disabled}
                        className={`sim-condition-pill-btn${resolveActiveTriggers('consumable', slotItemId).length > 0 ? ' has-triggers' : ''}`}
                        onClick={() => setTriggerTarget({ kind: 'consumable', key: slotItemId })}
                        title={resolveTriggerSummary('consumable', slotItemId)}
                      >
                        {resolveTriggerSummary('consumable', slotItemId)}
                      </button>
                      {slot.remainingDurationSeconds > 0 && (
                        <span className="wasm-sim-tag">{t('combat.simulator.slotRemaining')}{slot.remainingDurationSeconds.toFixed(1)}s</span>
                      )}
                      {slot.cooldownUntilUnixSeconds > member.capturedAtUnixSeconds && (
                        <span className="wasm-sim-tag">
                          {t('combat.simulator.slotCooldown')}{(slot.cooldownUntilUnixSeconds - member.capturedAtUnixSeconds).toFixed(1)}s
                        </span>
                      )}
                      <button
                        type="button"
                        className="sim-equip-remove-btn"
                        disabled={disabled}
                        onClick={() => writeConsumableSlot(i, { itemId: null, enabled: false })}
                        aria-label={t('combat.simulator.remove')}
                      >×</button>
                    </>
                  )}
                </div>
              )
            })}
          </div>
        </div>
      )}

      {picker && (
        <ItemGridPicker
          open
          ignoreQuantity
          allowEmpty
          anchor={picker.anchor}
          title={t('combat.simulator.pickItem')}
          items={picker.target.kind === 'equipment'
            ? equipmentPickerItems(picker.target.match)
            : consumablePickerItems(picker.target.group)}
          value={picker.target.currentItemId ?? null}
          triggerCount={picker.target.kind === 'consumable' && picker.target.currentItemId
            ? resolveActiveTriggers('consumable', picker.target.currentItemId).length
            : undefined}
          onEditCondition={picker.target.kind === 'consumable' && picker.target.currentItemId
            ? () => {
                const key = picker.target.kind === 'consumable' ? picker.target.currentItemId : null
                setPicker(null)
                if (key) setTriggerTarget({ kind: 'consumable', key })
              }
            : undefined}
          onClose={() => setPicker(null)}
          onSelect={itemId => {
            const target = picker.target
            setPicker(null)
            if (target.kind === 'consumable') {
              writeConsumableSlot(target.slotIndex, { itemId, enabled: Boolean(itemId) })
            } else if (itemId) {
              equipItem(itemId, target.replaceSlot)
            } else if (target.currentItemId) {
              patch({ equipment: equipment.filter(entry => entry.itemId !== target.currentItemId) })
            } else if (target.replaceSlot) {
              patch({
                equipment: equipment.filter(entry => equipmentDefOf(entry.itemId)?.slot !== target.replaceSlot),
              })
            }
          }}
        />
      )}

      {abilityAnchor && (() => {
        const currentAbilityId = abilitySlots[abilityAnchor.slot]?.abilityId ?? null
        const canEditCond = Boolean(currentAbilityId && abilityAnchor.slot > 0)
        return (
          <AbilityGridPicker
            open
            allowEmpty
            anchor={abilityAnchor.el}
            title={t('combat.simulator.pickAbility')}
            abilities={abilities}
            slotIndex={abilityAnchor.slot}
            levelFor={id => {
              const slotMatch = abilitySlots.find(s => s.abilityId === id)
              return slotMatch?.level ?? 1
            }}
            triggerCount={canEditCond && currentAbilityId ? resolveActiveTriggers('ability', currentAbilityId).length : 0}
            onEditCondition={canEditCond && currentAbilityId ? () => {
              setAbilityAnchor(null)
              setTriggerTarget({ kind: 'ability', key: currentAbilityId })
            } : undefined}
            value={currentAbilityId}
            onClose={() => setAbilityAnchor(null)}
            onSelect={abilityId => {
              writeAbilitySlot(abilityAnchor.slot, { abilityId })
              setAbilityAnchor(null)
            }}
          />
        )
      })()}

      {triggerTarget && (
        <CombatTriggerEditor
          open
          title={triggerTarget.kind === 'ability' ? abilityName(triggerTarget.key) : itemName(triggerTarget.key)}
          initialTriggers={((triggerTarget.kind === 'ability' ? abilityTriggers : consumableTriggers)[triggerTarget.key] as CombatTrigger[] | undefined) ?? resolveDefaultTriggers(triggerTarget.kind, triggerTarget.key)}
          defaultTriggers={resolveDefaultTriggers(triggerTarget.kind, triggerTarget.key)}
          onClose={() => setTriggerTarget(null)}
          onSave={triggers => saveTrigger(triggerTarget, triggers)}
        />
      )}
    </div>
  )
})
