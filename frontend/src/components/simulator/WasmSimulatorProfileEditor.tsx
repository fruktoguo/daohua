import { memo, useCallback, useMemo, useState } from 'react'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { TRAIT_DEFINITIONS, calculateTraitCapacity } from '../../game/combatConstants'
import { resolveItemMetadata } from '../../game/ids'
import type {
  SimulatorAbilitySlotDto, SimulatorAssetDto, SimulatorAutoUseSlotDto, SimulatorProfileDto
} from '../../game/simulator/wasmSimulatorTypes'
import { ItemGridPicker, type ItemPickerEntry } from '../ItemGridPicker'
import { AbilityGridPicker } from '../AbilityGridPicker'
import { ItemIcon, ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { CombatTriggerEditor } from '../CombatTriggerEditor'
import { TraitBarItem } from '../TraitBarItem'
import type { CombatAttributes, CombatTrigger, EquipmentSlot } from '../../game/types'

interface Props {
  member: SimulatorProfileDto
  index: number
  disabled: boolean
  allMembers: readonly SimulatorProfileDto[]
  resolved: CombatAttributes | null | undefined
  onChange: (next: SimulatorProfileDto) => void
  onRemove: () => void
}

type TriggerTarget = { kind: 'ability' | 'consumable'; key: string } | null
type PickerTarget =
  | { kind: 'equipment'; match: readonly EquipmentSlot[]; replaceSlot?: EquipmentSlot }
  | { kind: 'consumable'; slotIndex: number; group: 'potion' | 'general' }
  | { kind: 'asset' }

const MAX_TRAIT_LEVEL = 20
const ABILITY_SLOT_COUNT = 5
const MAX_ENHANCEMENT = 20

const CORE_FIELDS = ['staminaExp', 'meridiansExp', 'attackExp', 'boneExp', 'agilityExp', 'consciousnessExp'] as const
const CORE_LABELS: Record<(typeof CORE_FIELDS)[number], string> = {
  staminaExp: 'combat.simulator.core.stamina', meridiansExp: 'combat.simulator.core.meridians',
  attackExp: 'combat.simulator.core.attack', boneExp: 'combat.simulator.core.bone',
  agilityExp: 'combat.simulator.core.agility', consciousnessExp: 'combat.simulator.core.consciousness'
}

const EQUIPMENT_SLOTS: readonly { slot: EquipmentSlot; labelKey: string; match: readonly EquipmentSlot[] }[] = [
  { slot: 'weapon', labelKey: 'app.equipmentPanel.slotWeapon', match: ['weapon', 'two-hand'] },
  { slot: 'off-hand', labelKey: 'app.equipmentPanel.slotOffHand', match: ['off-hand'] },
  { slot: 'head', labelKey: 'app.equipmentPanel.slotHead', match: ['head'] },
  { slot: 'armor', labelKey: 'app.equipmentPanel.slotArmor', match: ['armor'] },
  { slot: 'legs', labelKey: 'app.equipmentPanel.slotLegs', match: ['legs'] },
  { slot: 'trinket', labelKey: 'app.equipmentPanel.slotTrinket', match: ['ring', 'neck', 'trinket', 'talisman'] },
]

const assetKey = (asset: Pick<SimulatorAssetDto, 'itemId' | 'variant'>) => `${asset.itemId}${asset.variant}`

export const WasmSimulatorProfileEditor = memo(function WasmSimulatorProfileEditor({
  member, index, disabled, allMembers, resolved, onChange, onRemove
}: Props) {
  const t = useT()
  const items = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const abilities = useGameStore(state => state.abilities)
  const setNotice = useGameStore(state => state.setNotice)
  const [expanded, setExpanded] = useState(index === 0)
  const [picker, setPicker] = useState<{ anchor: HTMLElement; target: NonNullable<PickerTarget> } | null>(null)
  const [abilityAnchor, setAbilityAnchor] = useState<{ el: HTMLElement; slot: number } | null>(null)
  const [triggerTarget, setTriggerTarget] = useState<TriggerTarget>(null)
  const [formError, setFormError] = useState<string | null>(null)

  const itemName = (id: string) =>
    items.find(item => item.itemId === id)?.name
    ?? equipmentCatalog.find(eq => eq.equipmentId === id)?.name
    ?? id
  const abilityName = (id: string | null) =>
    id ? (abilities.find(a => a.abilityId === id)?.name ?? id) : t('combat.simulator.emptySlot')

  const patch = useCallback((partial: Partial<SimulatorProfileDto>) => onChange({ ...member, ...partial }), [member, onChange])

  const num = (value: string, fallback: number) => {
    const v = Number(value)
    return Number.isFinite(v) ? v : fallback
  }

  const integerOr = (value: string, fallback: number) => {
    const v = Number(value)
    return Number.isInteger(v) ? v : fallback
  }

  const allPickerItems: ItemPickerEntry[] = useMemo(() => items.map(item => ({
    itemId: item.itemId, name: item.name, description: item.description ?? '',
    category: item.category ?? '', tier: item.tier ?? 1, amount: 0, definition: item
  })), [items])

  const combatEligible = useCallback((entry: ItemPickerEntry) => {
    const def = entry.definition
    return def?.category === 'consumable' && (def.usableBars ?? []).includes('combat')
  }, [])

  const consumablePickerItems = useCallback((group: 'potion' | 'general'): ItemPickerEntry[] =>
    allPickerItems.filter(entry => combatEligible(entry)
      && (group !== 'potion' || entry.definition?.subCategory === 'pill')), [allPickerItems, combatEligible])

  const equipmentPickerItems = useCallback((match: readonly EquipmentSlot[]): ItemPickerEntry[] =>
    equipmentCatalog
      .filter(eq => match.includes(eq.slot))
      .map(eq => ({
        itemId: eq.equipmentId, name: eq.name, description: eq.description ?? '',
        category: eq.slot, tier: eq.tier ?? 1, amount: 0, equipmentDefinition: eq
      })), [equipmentCatalog])

  const abilityTriggers = member.abilityTriggers ?? {}
  const consumableTriggers = member.consumableTriggers ?? {}
  const triggerMode = (dict: Record<string, unknown[]>, key: string): 'default' | 'none' | 'custom' =>
    !Object.prototype.hasOwnProperty.call(dict, key) ? 'default' : (dict[key]?.length ?? 0) === 0 ? 'none' : 'custom'

  const setTriggerMode = (target: { kind: 'ability' | 'consumable'; key: string }, mode: 'default' | 'none' | 'custom') => {
    const dict = target.kind === 'ability' ? { ...abilityTriggers } : { ...consumableTriggers }
    if (mode === 'default') delete dict[target.key]
    else if (mode === 'none') dict[target.key] = []
    else {
      setTriggerTarget(target)
      return
    }
    if (target.kind === 'ability') patch({ abilityTriggers: dict })
    else patch({ consumableTriggers: dict })
  }

  const saveTrigger = (target: NonNullable<TriggerTarget>, triggers: CombatTrigger[] | null) => {
    const dict = target.kind === 'ability' ? { ...abilityTriggers } : { ...consumableTriggers }
    if (triggers === null) delete dict[target.key]
    else dict[target.key] = triggers
    if (target.kind === 'ability') patch({ abilityTriggers: dict })
    else patch({ consumableTriggers: dict })
    setTriggerTarget(null)
  }

  const triggerButtons = (kind: 'ability' | 'consumable', key: string) => {
    const dict = kind === 'ability' ? abilityTriggers : consumableTriggers
    const mode = triggerMode(dict, key)
    return (
      <span className="wasm-sim-trigger-modes">
        <button type="button" disabled={disabled} className={mode === 'default' ? 'is-active' : ''}
          onClick={() => setTriggerMode({ kind, key }, 'default')}>{t('combat.simulator.triggerDefault')}</button>
        <button type="button" disabled={disabled} className={mode === 'none' ? 'is-active' : ''}
          onClick={() => setTriggerMode({ kind, key }, 'none')}>{t('combat.simulator.triggerNone')}</button>
        <button type="button" disabled={disabled} className={mode === 'custom' ? 'is-active' : ''}
          onClick={() => setTriggerMode({ kind, key }, 'custom')}>
          {mode === 'custom' ? `${t('combat.simulator.triggerCustom')}(${(dict[key]?.length ?? 0)})` : t('combat.simulator.triggerCustom')}
        </button>
      </span>
    )
  }

  const traitIdSet = useMemo(() => new Set(member.traitIds ?? []), [member.traitIds])
  const unlockedMap = useMemo(() =>
    new Map((member.unlockedTraits ?? []).map(tr => [tr.id, tr])), [member.unlockedTraits])
  const traitCapacity = useMemo(() => calculateTraitCapacity(member.realmLevel), [member.realmLevel])
  const traitCost = useMemo(() =>
    (member.traitIds ?? []).reduce((sum, id) => sum + (TRAIT_DEFINITIONS[id]?.cost ?? 0), 0), [member.traitIds])
  const sortedTraits = useMemo(() => Object.values(TRAIT_DEFINITIONS)
    .sort((a, b) => Number(traitIdSet.has(b.id)) - Number(traitIdSet.has(a.id))
      || b.quality - a.quality || a.id.localeCompare(b.id)), [traitIdSet])

  const toggleTrait = useCallback((id: string) => {
    const ids = member.traitIds ?? []
    if (ids.includes(id)) {
      patch({ traitIds: ids.filter(x => x !== id) })
      return
    }
    const cost = TRAIT_DEFINITIONS[id]?.cost ?? 0
    if (traitCost + cost > traitCapacity) {
      setNotice(t('insight.traits.overCapacityTip'))
      return
    }
    const partial: Partial<SimulatorProfileDto> = { traitIds: [...ids, id] }
    if (!unlockedMap.has(id))
      partial.unlockedTraits = [...(member.unlockedTraits ?? []), { id, level: 1, exp: 0 }]
    patch(partial)
  }, [member, patch, setNotice, t, traitCapacity, traitCost, unlockedMap])

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
    const next = abilitySlots.map((slot, j) => j === i ? { ...slot, ...partial } : slot)
    patch({ abilitySlots: next })
  }, [abilitySlots, patch])

  const consumableSlots = useMemo(() => {
    const real = member.combatAutoUseSlots ?? []
    const list: SimulatorAutoUseSlotDto[] = []
    for (let i = 0; i < 6; i++) {
      const group = i < 3 ? 'potion' : 'general'
      const slotIndex = i % 3
      const found = real.find(slot => slot.slotIndex === slotIndex && slot.group === group && slot.actionType === 'combat')
      list.push(found ?? {
        actionType: 'combat', slotIndex, itemId: null, quantity: 1, enabled: false,
        remainingDurationSeconds: 0, cooldownUntilUnixSeconds: 0, group
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

  const sharedQuantity = useCallback((itemId: string) =>
    (member.assets ?? []).filter(a => a.itemId === itemId && a.variant === '')
      .reduce((sum, a) => sum + a.quantity, 0), [member.assets])

  const assets = useMemo(() => {
    const map = new Map<string, SimulatorAssetDto>()
    for (const asset of member.assets ?? []) {
      const key = assetKey(asset)
      const prev = map.get(key)
      map.set(key, prev ? { ...prev, quantity: prev.quantity + asset.quantity } : { ...asset })
    }
    return [...map.values()]
  }, [member.assets])

  const writeAsset = useCallback((key: string, partial: Partial<SimulatorAssetDto>) => {
    const rows = member.assets ?? []
    const idx = rows.findIndex(a => assetKey(a) === key)
    if (partial.variant !== undefined) {
      const nextKey = assetKey({ itemId: partial.itemId ?? rows[idx]?.itemId ?? '', variant: partial.variant })
      if (rows.some((a, i) => i !== idx && assetKey(a) === nextKey)) {
        setFormError(t('combat.simulator.error.variantConflict'))
        return
      }
    }
    setFormError(null)
    if (idx >= 0)
      patch({ assets: rows.map((a, i) => i === idx ? { ...a, ...partial } : a) })
    else if (partial.itemId)
      patch({ assets: [...rows, { itemId: partial.itemId, variant: partial.variant ?? '', quantity: partial.quantity ?? 1 }] })
  }, [member.assets, patch, t])

  const removeAsset = useCallback((key: string) => {
    patch({ assets: (member.assets ?? []).filter(a => assetKey(a) !== key) })
  }, [member.assets, patch])

  const respawnTargets = allMembers.filter((m, i) => i !== index && m.sourcePlayerId)
  const maxHitpoints = resolved?.maxHitpoints ?? member.maxHitpoints
  const maxManapoints = resolved?.maxManapoints ?? member.maxManapoints
  const regenRemaining = Math.max(0, 5 - (member.regenCarrySeconds ?? 0))

  return (
    <div className="wasm-sim-member">
      <header className="wasm-sim-member-head">
        <button type="button" className="wasm-sim-member-toggle" onClick={() => setExpanded(v => !v)}>
          {expanded ? '▾' : '▸'} {member.displayName}
          {member.sourcePlayerId ? `（${member.sourcePlayerId.slice(0, 8)}）` : `（${t('combat.simulator.custom')}）`}
        </button>
        <button type="button" disabled={disabled} onClick={onRemove}>{t('combat.simulator.remove')}</button>
      </header>
      {expanded && <div className="wasm-sim-member-body">
        {formError && <div className="wasm-sim-error">{formError}</div>}
        <div className="wasm-sim-grid">
          <label>{t('combat.simulator.fieldName')}<input disabled={disabled} value={member.displayName}
            onChange={e => patch({ displayName: e.target.value })} /></label>
          <label>{t('combat.simulator.realmLevel')}<input type="number" min={0} max={127} disabled={disabled}
            value={member.realmLevel} onChange={e => patch({ realmLevel: integerOr(e.target.value, member.realmLevel) })} /></label>
          <label>{t('combat.simulator.hp')}<input type="number" disabled={disabled} value={member.hitpoints}
            onChange={e => {
              const v = num(e.target.value, member.hitpoints)
              if (v > maxHitpoints) setFormError(t('combat.simulator.error.hpOverMax', { max: Math.floor(maxHitpoints) }))
              else setFormError(null)
              patch({ hitpoints: v })
            }} /></label>
          <span className="wasm-sim-readonly">{t('combat.simulator.maxHp')}：{Math.floor(maxHitpoints)}</span>
          <label>{t('combat.simulator.mp')}<input type="number" disabled={disabled} value={member.manapoints}
            onChange={e => {
              const v = num(e.target.value, member.manapoints)
              if (v > maxManapoints) setFormError(t('combat.simulator.error.mpOverMax', { max: Math.floor(maxManapoints) }))
              else setFormError(null)
              patch({ manapoints: v })
            }} /></label>
          <span className="wasm-sim-readonly">{t('combat.simulator.maxMp')}：{Math.floor(maxManapoints)}</span>
          <label>{t('combat.simulator.respawn')}
            <select disabled={disabled} value={member.respawnPolicy}
              onChange={e => patch({ respawnPolicy: integerOr(e.target.value, member.respawnPolicy) })}>
              <option value={0}>{t('social.respawn.immediate')}</option>
              <option value={1}>{t('social.respawn.allAlliesReady')}</option>
              <option value={2}>{t('social.respawn.targetAllyAlive')}</option>
            </select></label>
          {member.respawnPolicy === 2 && <label>{t('combat.simulator.respawnTarget')}
            <select disabled={disabled} value={member.respawnTargetPlayerId ?? ''}
              onChange={e => patch({ respawnTargetPlayerId: e.target.value === '' ? null : e.target.value })}>
              <option value="">{t('social.respawn.targetPlaceholder')}</option>
              {respawnTargets.map(m => m.sourcePlayerId && (
                <option key={m.sourcePlayerId} value={m.sourcePlayerId}>{m.displayName}</option>))}
            </select></label>}
        </div>
        <h4>{t('combat.simulator.core')}</h4>
        <div className="wasm-sim-grid">
          {CORE_FIELDS.map(field => (
            <label key={field}>{t(CORE_LABELS[field])}
              <input type="number" disabled={disabled} value={member.coreAttributes[field]}
                onChange={e => patch({ coreAttributes: { ...member.coreAttributes, [field]: num(e.target.value, member.coreAttributes[field]) } })} />
            </label>
          ))}
        </div>
        <details className="wasm-sim-advanced">
          <summary>{t('combat.simulator.advancedState')}</summary>
          <div className="wasm-sim-grid">
            <label>{t('combat.simulator.regenRemaining')}<input type="number" min={0.001} max={5} step="any" disabled={disabled}
              value={regenRemaining}
              onChange={e => {
                const value = num(e.target.value, -1)
                if (value > 0 && value <= 5) patch({ regenCarrySeconds: 5 - value })
              }} /></label>
          </div>
        </details>
        <h4>{t('combat.simulator.traits')}（{traitCost}/{traitCapacity}）</h4>
        <details className="wasm-sim-traits-detail" open>
          <summary>{t('combat.simulator.traitsCount', { count: sortedTraits.length })}</summary>
          <div className="wasm-sim-traits">
            {sortedTraits.map(def => {
              const unlocked = unlockedMap.get(def.id)
              const equipped = traitIdSet.has(def.id)
              const level = unlocked?.level ?? 1
              return (
                <div key={def.id} className="wasm-sim-trait">
                  <TraitBarItem def={def} level={level} mode="insight"
                    isEquipped={equipped}
                    onClick={() => { if (!disabled) toggleTrait(def.id) }} />
                  {unlocked && <input type="number" min={1} max={MAX_TRAIT_LEVEL} disabled={disabled} value={level}
                    onChange={e => {
                      const v = integerOr(e.target.value, -1)
                      if (v < 1 || v > MAX_TRAIT_LEVEL) return
                      patch({ unlockedTraits: (member.unlockedTraits ?? []).map(tr =>
                        tr.id === def.id ? { ...tr, level: v } : tr) })
                    }} />}
                </div>
              )
            })}
          </div>
        </details>
        <h4>{t('combat.simulator.equipment')}</h4>
        <div className="wasm-sim-equipment">
          {EQUIPMENT_SLOTS.map(conf => {
            const entries = equippedFor(conf.match)
            return (
              <div key={conf.slot} className="wasm-sim-equip-slot">
                <span className="wasm-sim-equip-label">{t(conf.labelKey)}</span>
                <div className="wasm-sim-equip-items">
                  {entries.map(entry => {
                    const meta = resolveItemMetadata(entry.itemId)
                    const eqDef = equipmentDefOf(entry.itemId)
                    return (
                      <span key={`${entry.itemId}-${eqDef?.slot ?? ''}`} className="wasm-sim-equip-entry">
                        <ItemTooltipTarget itemId={entry.itemId} enhancementLevel={entry.enhancementLevel} equipmentDefinition={eqDef}>
                          <button type="button" className="wasm-sim-equip-tile-btn" disabled={disabled}
                            onClick={e => setPicker({ anchor: e.currentTarget,
                              target: { kind: 'equipment', match: [eqDef?.slot ?? conf.slot], replaceSlot: eqDef?.slot } })}>
                            <ItemTile itemId={entry.itemId} name={itemName(entry.itemId)}
                              category={eqDef?.slot ?? meta?.category ?? ''} className="wasm-sim-equip-tile"
                              enhancementLevel={entry.enhancementLevel} rarity={meta?.rarity ?? 'mundane'} />
                          </button>
                        </ItemTooltipTarget>
                        <input type="number" min={0} max={MAX_ENHANCEMENT} disabled={disabled} value={entry.enhancementLevel}
                          onChange={e => {
                            const v = integerOr(e.target.value, -1)
                            if (v < 0 || v > MAX_ENHANCEMENT) return
                            patch({ equipment: equipment.map(x => x === entry ? { ...x, enhancementLevel: v } : x) })
                          }} />
                        <button type="button" disabled={disabled}
                          onClick={() => patch({ equipment: equipment.filter(x => x !== entry) })}>×</button>
                      </span>
                    )
                  })}
                  <button type="button" className="wasm-sim-equip-empty" disabled={disabled}
                    onClick={e => setPicker({ anchor: e.currentTarget, target: { kind: 'equipment', match: conf.match } })}>
                    {entries.length === 0 ? t('combat.simulator.emptySlot') : '＋'}
                  </button>
                </div>
              </div>
            )
          })}
        </div>
        <h4>{t('combat.simulator.abilities')}</h4>
        <div className="wasm-sim-abilities">
          {abilitySlots.map((slot, i) => {
            return (
              <div key={i} className="wasm-sim-ability">
                <button type="button" disabled={disabled} className="wasm-sim-ability-btn"
                  onClick={e => setAbilityAnchor({ el: e.currentTarget, slot: i })}>
                  {slot.abilityId && <ItemIcon itemId={slot.abilityId} />}
                  {i === 0 ? `[${t('combat.simulator.internalSlot')}] ` : ''}{abilityName(slot.abilityId)}
                </button>
                <input type="number" min={1} step={1} disabled={disabled} value={slot.level}
                  onChange={e => {
                    const v = integerOr(e.target.value, -1)
                    if (v < 1) return
                    writeAbilitySlot(i, { level: v })
                  }} />
                {slot.abilityId && <button type="button" disabled={disabled}
                  onClick={() => writeAbilitySlot(i, { abilityId: null, level: 1 })}>×</button>}
                {slot.abilityId && triggerButtons('ability', slot.abilityId)}
              </div>
            )
          })}
        </div>
        <h4>{t('combat.simulator.consumableSlots')}</h4>
        <div className="wasm-sim-slots">
          {consumableSlots.map((slot, i) => {
            const group = i < 3 ? 'potion' : 'general'
            const shared = slot.itemId ? sharedQuantity(slot.itemId) : 0
            return (
              <div key={i} className="wasm-sim-slot">
                <span className="wasm-sim-slot-badge">{i < 3 ? t('social.card.potionGroup') : t('social.card.generalGroup')}{i % 3 + 1}</span>
                <button type="button" disabled={disabled}
                  onClick={e => setPicker({ anchor: e.currentTarget, target: { kind: 'consumable', slotIndex: i, group } })}>
                  {slot.itemId ? <><ItemIcon itemId={slot.itemId} />{itemName(slot.itemId)}</> : t('combat.simulator.emptySlot')}
                </button>
                {slot.itemId && <button type="button" disabled={disabled}
                  onClick={() => writeConsumableSlot(i, { itemId: null, enabled: false })}>×</button>}
                <label>{t('combat.simulator.slotQty')}<input type="number" min={1} disabled={disabled || !slot.itemId} value={slot.quantity}
                  onChange={e => {
                    const v = num(e.target.value, -1)
                    if (v <= 0) return
                    writeConsumableSlot(i, { quantity: v })
                  }} /></label>
                <label><input type="checkbox" disabled={disabled || !slot.itemId} checked={slot.enabled && !!slot.itemId}
                  onChange={e => writeConsumableSlot(i, { enabled: e.target.checked })} />{t('combat.simulator.slotEnabled')}</label>
                {slot.itemId && <span className="wasm-sim-tag">{t('combat.simulator.sharedStock')}{shared}</span>}
                {slot.remainingDurationSeconds > 0 && <span className="wasm-sim-tag">{t('combat.simulator.slotRemaining')}{slot.remainingDurationSeconds.toFixed(1)}s</span>}
                {slot.cooldownUntilUnixSeconds > member.capturedAtUnixSeconds && <span className="wasm-sim-tag">
                  {t('combat.simulator.slotCooldown')}{(slot.cooldownUntilUnixSeconds - member.capturedAtUnixSeconds).toFixed(1)}s
                </span>}
                {slot.itemId && triggerButtons('consumable', slot.itemId)}
              </div>
            )
          })}
        </div>
        <h4>{t('combat.simulator.inventory')}</h4>
        <div className="wasm-sim-assets">
          {assets.map(asset => {
            const key = assetKey(asset)
            return (
              <div key={key} className="wasm-sim-asset">
                <ItemTooltipTarget itemId={asset.itemId}>
                  <span className="wasm-sim-asset-name"><ItemIcon itemId={asset.itemId} />{itemName(asset.itemId)}</span>
                </ItemTooltipTarget>
                <input type="text" className="wasm-sim-variant" placeholder="+0" disabled={disabled} value={asset.variant}
                  onChange={e => writeAsset(key, { variant: e.target.value })} />
                <input type="number" min={0} disabled={disabled} value={asset.quantity}
                  onChange={e => {
                    const v = num(e.target.value, -1)
                    if (v < 0) return
                    writeAsset(key, { quantity: v })
                  }} />
                <button type="button" disabled={disabled} onClick={() => removeAsset(key)}>×</button>
              </div>
            )
          })}
          <button type="button" disabled={disabled}
            onClick={e => setPicker({ anchor: e.currentTarget, target: { kind: 'asset' } })}>＋</button>
        </div>
        {((member.combatBuffCarry?.length ?? 0) > 0 || (member.buffs?.length ?? 0) > 0 || (member.combatSkills?.cooldowns?.length ?? 0) > 0 || member.combatSkills?.castingAbilityId) && (
          <div className="wasm-sim-state">
            {(member.buffs ?? []).map((b, i) => <span key={`b${i}`} className="wasm-sim-tag">
              <ItemIcon itemId={b.sourceItemId ?? b.buffId} />{b.name || b.buffId}×{b.stacks}
              {' '}{Math.max(0, b.expiresAtUnixSeconds - member.capturedAtUnixSeconds).toFixed(1)}s
            </span>)}
            {(member.combatBuffCarry ?? []).map((b, i) => <span key={`c${i}`} className="wasm-sim-tag">
              <ItemIcon itemId={b.buffId} />{b.name || b.buffId}×{b.stacks} {b.remainingSeconds.toFixed(1)}s
            </span>)}
            {(member.combatSkills?.cooldowns ?? []).map((c, i) =>
              <span key={`cd${i}`} className="wasm-sim-tag"><ItemIcon itemId={c.abilityId} />{abilityName(c.abilityId)} {c.remainingSeconds.toFixed(1)}s</span>)}
            {member.combatSkills?.castingAbilityId &&
              <span className="wasm-sim-tag">{t('combat.simulator.casting')}<ItemIcon itemId={member.combatSkills.castingAbilityId} />{abilityName(member.combatSkills.castingAbilityId)}</span>}
          </div>
        )}
      </div>}
      {picker && <ItemGridPicker open anchor={picker.anchor}
        title={t('combat.simulator.pickItem')}
        items={picker.target.kind === 'equipment' ? equipmentPickerItems(picker.target.match)
          : picker.target.kind === 'consumable' ? consumablePickerItems(picker.target.group) : allPickerItems}
        value={null} onClose={() => setPicker(null)}
        onSelect={itemId => {
          const target = picker.target
          setPicker(null)
          if (!itemId) return
          if (target.kind === 'asset') {
            const key = assetKey({ itemId, variant: '' })
            if (!assets.some(asset => assetKey(asset) === key))
              writeAsset(key, { itemId, variant: '', quantity: 1 })
          }
          else if (target.kind === 'consumable')
            writeConsumableSlot(target.slotIndex, { itemId })
          else
            equipItem(itemId, target.replaceSlot)
        }} />}
      {abilityAnchor && <AbilityGridPicker open anchor={abilityAnchor.el}
        title={t('combat.simulator.pickAbility')} abilities={abilities}
        slotIndex={abilityAnchor.slot}
        value={abilitySlots[abilityAnchor.slot]?.abilityId ?? null}
        onClose={() => setAbilityAnchor(null)}
        onSelect={abilityId => {
          writeAbilitySlot(abilityAnchor.slot, { abilityId })
          setAbilityAnchor(null)
        }} />}
      {triggerTarget && <CombatTriggerEditor open
        title={triggerTarget.kind === 'ability' ? abilityName(triggerTarget.key) : itemName(triggerTarget.key)}
        initialTriggers={(triggerTarget.kind === 'ability' ? abilityTriggers : consumableTriggers)[triggerTarget.key] as CombatTrigger[] | undefined}
        onClose={() => setTriggerTarget(null)}
        onSave={triggers => saveTrigger(triggerTarget, triggers)} />}
    </div>
  )
})
