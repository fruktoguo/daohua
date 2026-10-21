import { memo, useCallback, useMemo, useState } from 'react'
import { useGameStore } from '../../game/gameStore'
import { useSocialStore } from '../../game/socialStore'
import { useT } from '../../i18n'
import type { AutoUseSlot, CombatAbilityDefinition, CombatAbilitySlot, CombatAttributes, CombatEquipmentDefinition, EquipmentSlot, EquippedItem, ItemDefinition } from '../../game/types'
import { roundStat, sanitizeAttributes, type SimulatorCombatantConfig } from '../../game/simulator/simulatorTypes'
import { StatGlyph } from '../StatGlyph'
import { ItemGridPicker, type ItemPickerEntry } from '../ItemGridPicker'
import { ItemTile, ItemTooltipTarget } from '../ItemDisplay'
import { findEquipmentInCatalog, findItemInCatalog, resolveItemCategoryLabel, resolveItemMetadata, withItemGrade } from '../../game/ids'

interface SimulatorPartyEditorProps {
  allies: SimulatorCombatantConfig[]
  onChangeAllies: (allies: SimulatorCombatantConfig[]) => void
  abilities: readonly CombatAbilityDefinition[]
}

const DEFAULT_ATTRIBUTES: CombatAttributes = {
  maxHitpoints: 300,
  maxManapoints: 100,
  physicalAttack: 30,
  spellAttack: 20,
  armor: 15,
  magicResistance: 10,
  accuracy: 20,
  evasion: 10,
  critical: 10,
  criticalResistance: 10,
  criticalDamagePoints: 50,
  breakPower: 10,
  parryResolve: 10,
  hpRegenPerFiveSeconds: 5,
  mpRegenPerFiveSeconds: 3,
  attackIntervalMilliseconds: 1000,
  physicalDamageBonusPoints: 0,
  spellDamageBonusPoints: 0,
  metalDamageBonusPoints: 0,
  woodDamageBonusPoints: 0,
  waterDamageBonusPoints: 0,
  fireDamageBonusPoints: 0,
  earthDamageBonusPoints: 0,
  totalDamageBonusPoints: 0,
  basicAttackBonusPoints: 0,
  skillBonusPoints: 0,
  physicalDamageReductionPoints: 0,
  spellDamageReductionPoints: 0,
  metalDamageReductionPoints: 0,
  woodDamageReductionPoints: 0,
  waterDamageReductionPoints: 0,
  fireDamageReductionPoints: 0,
  earthDamageReductionPoints: 0,
  damageAbsorptionPoints: 0,
  fixedDamageBonus: 0,
  fixedDamageReduction: 0,
  fixedPhysicalDamageBonus: 0,
  fixedPhysicalDamageReduction: 0,
  fixedSpellDamageBonus: 0,
  fixedSpellDamageReduction: 0,
  attackSpeedPoints: 0,
  actionSpeedPoints: 0,
  cooldownReductionPoints: 0,
  threatBonusPoints: 0,
  hpDrainPoints: 0,
  mpDrainPoints: 0,
  damageReflectPoints: 0,
  healingBonusPoints: 0,
}

interface SimulatorSlotConfig {
  slot: EquipmentSlot
  labelKey: 'slotWeapon' | 'slotOffHand' | 'slotHead' | 'slotArmor' | 'slotLegs' | 'slotTrinket'
}

const SIMULATOR_SLOTS: readonly SimulatorSlotConfig[] = [
  { slot: 'weapon', labelKey: 'slotWeapon' },
  { slot: 'off-hand', labelKey: 'slotOffHand' },
  { slot: 'head', labelKey: 'slotHead' },
  { slot: 'armor', labelKey: 'slotArmor' },
  { slot: 'legs', labelKey: 'slotLegs' },
  { slot: 'trinket', labelKey: 'slotTrinket' },
]

function isSlotMatch(equipmentSlot: EquipmentSlot, targetSlot: EquipmentSlot): boolean {
  if (equipmentSlot === targetSlot) return true
  if (targetSlot === 'weapon' && equipmentSlot === 'two-hand') return true
  if (targetSlot === 'trinket' && (equipmentSlot === 'ring' || equipmentSlot === 'neck' || equipmentSlot === 'talisman')) return true
  return false
}

type SubTabKey = 'attributes' | 'skills' | 'equipment' | 'consumables'

export const SimulatorPartyEditor = memo(function SimulatorPartyEditor({
  allies,
  onChangeAllies,
  abilities,
}: SimulatorPartyEditorProps): React.JSX.Element {
  const t = useT()
  const [selectedIdx, setSelectedIdx] = useState(0)
  const [activeSubTab, setActiveSubTab] = useState<SubTabKey>('attributes')
  const currentAlly = allies[selectedIdx] || allies[0]

  const party = useSocialStore(state => state.party)
  const myName = useGameStore(state => state.name)
  const myAttributes = useGameStore(state => state.combatAttributes)
  const myCombat = useGameStore(state => state.combat)
  const myConsumables = useGameStore(state => state.queue?.autoUseSlots?.combat)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const itemsCatalog = useGameStore(state => state.items)

  // 当前正在呼出 ItemGridPicker 选择的部位
  const [activePickingSlot, setActivePickingSlot] = useState<{ config: SimulatorSlotConfig; anchor: HTMLElement } | null>(null)

  // 映射当前角色已装备的 6 大部位（对齐游戏内六部位归并逻辑）
  const slotEquippedMap = useMemo(() => {
    const map = new Map<EquipmentSlot, { item: EquippedItem; definition?: ItemDefinition; equipmentDef?: CombatEquipmentDefinition }>()
    for (const eq of currentAlly?.equipment || []) {
      const eqDef = findEquipmentInCatalog(equipmentCatalog, eq.itemId)
      if (!eqDef) continue
      const itemDef = findItemInCatalog(itemsCatalog, eq.itemId)
      for (const config of SIMULATOR_SLOTS) {
        if (isSlotMatch(eqDef.slot, config.slot)) {
          map.set(config.slot, { item: eq, definition: itemDef, equipmentDef: eqDef })
          break
        }
      }
    }
    return map
  }, [currentAlly?.equipment, equipmentCatalog, itemsCatalog])

  // 当前选中部位的待选装备列表（从配表中筛选所有匹配该槽位的可用装备）
  const pickerItems = useMemo<ItemPickerEntry[]>(() => {
    if (!activePickingSlot) return []
    const targetSlot = activePickingSlot.config.slot
    const matching: ItemPickerEntry[] = []

    for (const eqDef of equipmentCatalog) {
      if (!isSlotMatch(eqDef.slot, targetSlot)) continue
      const itemDef = findItemInCatalog(itemsCatalog, eqDef.equipmentId)
      matching.push({
        itemId: eqDef.equipmentId,
        name: eqDef.name,
        amount: 1,
        tier: eqDef.tier,
        category: resolveItemCategoryLabel(eqDef.slot, t),
        description: eqDef.description ?? itemDef?.description ?? '',
        definition: itemDef,
        equipmentDefinition: eqDef,
        enhancementLevel: 0,
      })
    }

    matching.sort((a, b) => a.tier - b.tier || a.name.localeCompare(b.name))
    return matching
  }, [activePickingSlot, equipmentCatalog, itemsCatalog, t])
  // 1. 导入自身完整配置 (属性 + 功法 + 装备 + 消耗品)
  const handleImportSelf = useCallback(() => {
    if (!currentAlly) return
    const newAlly: SimulatorCombatantConfig = {
      ...currentAlly,
      name: myName || t('combat.simulator.selfName'),
      isSelf: true,
      attributes: myAttributes ? sanitizeAttributes(myAttributes) : { ...DEFAULT_ATTRIBUTES },
      abilitySlots: myCombat?.abilitySlots ? myCombat.abilitySlots.map(s => ({ ...s })) : [],
      abilityTriggers: myCombat?.abilityTriggers ? { ...myCombat.abilityTriggers } : {},
      equipment: myCombat?.equipment ? myCombat.equipment.map(e => ({ ...e })) : [],
      consumables: myConsumables ? myConsumables.map(c => ({ ...c })) : [],
    }
    const nextList = [...allies]
    nextList[selectedIdx] = newAlly
    onChangeAllies(nextList)
  }, [allies, currentAlly, myAttributes, myCombat, myConsumables, myName, onChangeAllies, selectedIdx, t])

  // 2. 导入队友
  const handleImportTeammate = useCallback((memberId: string, memberName: string) => {
    if (!currentAlly) return
    const newAlly: SimulatorCombatantConfig = {
      ...currentAlly,
      id: memberId,
      name: memberName,
      isSelf: false,
      attributes: myAttributes ? sanitizeAttributes(myAttributes) : { ...DEFAULT_ATTRIBUTES },
      abilitySlots: myCombat?.abilitySlots ? myCombat.abilitySlots.map(s => ({ ...s })) : [],
      equipment: [],
      consumables: [],
    }
    const nextList = [...allies]
    nextList[selectedIdx] = newAlly
    onChangeAllies(nextList)
  }, [allies, currentAlly, myAttributes, myCombat?.abilitySlots, onChangeAllies, selectedIdx])

  // 3. 修改属性值
  const handleUpdateStat = useCallback((key: keyof CombatAttributes, val: number) => {
    if (!currentAlly) return
    const updated: SimulatorCombatantConfig = {
      ...currentAlly,
      attributes: {
        ...currentAlly.attributes,
        [key]: Math.max(0, roundStat(val)),
      },
    }
    const nextList = [...allies]
    nextList[selectedIdx] = updated
    onChangeAllies(nextList)
  }, [allies, currentAlly, onChangeAllies, selectedIdx])

  // 4. 添加新队员
  const handleAddMember = useCallback(() => {
    if (allies.length >= 5) return
    const newMember: SimulatorCombatantConfig = {
      id: `member-${Date.now()}`,
      name: `${t('combat.simulator.allyLabel')} ${allies.length + 1}`,
      isSelf: false,
      formationIndex: allies.length,
      attributes: { ...DEFAULT_ATTRIBUTES },
      abilitySlots: [
        { abilityId: 'internal.basic-breath', level: 1 },
        { abilityId: abilities[0]?.abilityId || 'spell.basic-fire', level: 1 },
      ],
      equipment: [],
      consumables: [],
    }
    onChangeAllies([...allies, newMember])
    setSelectedIdx(allies.length)
  }, [abilities, allies, onChangeAllies, t])

  // 5. 移除队员
  const handleRemoveMember = useCallback((index: number) => {
    if (allies.length <= 1) return
    const nextList = allies.filter((_, i) => i !== index)
    onChangeAllies(nextList)
    setSelectedIdx(Math.max(0, index - 1))
  }, [allies, onChangeAllies])

  // 6. 功法技能更新
  const handleUpdateSkillSlot = useCallback((slotIndex: number, abilityId: string, level: number) => {
    if (!currentAlly) return
    const currentSlots: CombatAbilitySlot[] = Array.from({ length: 5 }, (_, i) => {
      const s = currentAlly.abilitySlots?.[i]
      return s ? { abilityId: s.abilityId ?? null, level: s.level || 1 } : { abilityId: null, level: 1 }
    })
    currentSlots[slotIndex] = {
      abilityId: abilityId || null,
      level: Math.max(1, Math.min(20, level)),
    }
    const updated: SimulatorCombatantConfig = { ...currentAlly, abilitySlots: currentSlots }
    const nextList = [...allies]
    nextList[selectedIdx] = updated
    onChangeAllies(nextList)
  }, [allies, currentAlly, onChangeAllies, selectedIdx])

  // 7. 装备更新（按 isSlotMatch 精确互斥）
  const handleUpdateEquipment = useCallback((slot: EquipmentSlot, itemId: string, enhancementLevel: number) => {
    if (!currentAlly) return
    const currentEquip = [...(currentAlly.equipment || [])]
    const filtered = currentEquip.filter(eq => {
      const def = findEquipmentInCatalog(equipmentCatalog, eq.itemId)
      if (!def) return false
      return !isSlotMatch(def.slot, slot)
    })
    if (itemId) {
      filtered.push({ itemId, enhancementLevel: Math.max(0, Math.min(20, Math.floor(enhancementLevel))) })
    }
    const updated: SimulatorCombatantConfig = { ...currentAlly, equipment: filtered }
    const nextList = [...allies]
    nextList[selectedIdx] = updated
    onChangeAllies(nextList)
  }, [allies, currentAlly, equipmentCatalog, onChangeAllies, selectedIdx])

  const handleSelectEquipmentFromPicker = useCallback((selectedItemId: string | null) => {
    if (!activePickingSlot || !currentAlly) return
    const targetSlot = activePickingSlot.config.slot
    const currentSlotData = slotEquippedMap.get(targetSlot)
    const currentEnhance = currentSlotData?.item.enhancementLevel ?? 0

    handleUpdateEquipment(targetSlot, selectedItemId || '', currentEnhance)
    setActivePickingSlot(null)
  }, [activePickingSlot, currentAlly, handleUpdateEquipment, slotEquippedMap])

  // 8. 消耗品更新
  const handleUpdateConsumable = useCallback((slotIndex: number, itemId: string | null, quantity: number, enabled: boolean) => {
    if (!currentAlly) return
    const currentConsumables: AutoUseSlot[] = Array.from({ length: 6 }, (_, i) => {
      const c = currentAlly.consumables?.[i]
      return c ? { ...c } : {
        itemId: null,
        quantity: 10,
        enabled: true,
        group: (i < 3 ? 'potion' : 'general') as 'potion' | 'general',
      }
    })
    currentConsumables[slotIndex] = {
      ...currentConsumables[slotIndex],
      itemId: itemId || null,
      quantity: Math.max(1, quantity),
      enabled,
      group: (slotIndex < 3 ? 'potion' : 'general') as 'potion' | 'general',
    }
    const updated: SimulatorCombatantConfig = { ...currentAlly, consumables: currentConsumables }
    const nextList = [...allies]
    nextList[selectedIdx] = updated
    onChangeAllies(nextList)
  }, [allies, currentAlly, onChangeAllies, selectedIdx])

  // 技能分类过滤
  const internalAbilities = useMemo(() => {
    return abilities.filter(a => a.discipline === 'internal')
  }, [abilities])

  const activeAbilities = useMemo(() => {
    return abilities.filter(a => a.discipline !== 'internal')
  }, [abilities])

  // 可选消耗品列表 (丹药、灵散、符箓)
  const consumableItems = useMemo(() => {
    return itemsCatalog.filter(i =>
      i.category === 'consumable' ||
      i.category === 'pill' ||
      i.category === 'talisman' ||
      i.itemId.startsWith('item.')
    )
  }, [itemsCatalog])

  if (!currentAlly) return <div className="sim-empty-party">{t('combat.simulator.emptyParty')}</div>

  return (
    <div className="sim-party-editor">
      {/* 顶部参战成员切换 */}
      <div className="sim-party-tabs">
        {allies.map((ally, idx) => (
          <button
            key={ally.id}
            type="button"
            className={`sim-party-tab-btn ${idx === selectedIdx ? 'is-active' : ''}`}
            onClick={() => setSelectedIdx(idx)}
          >
            <span>{ally.name}</span>
            {ally.isSelf && <span className="sim-tag-self">{t('combat.simulator.tagSelf')}</span>}
            {allies.length > 1 && (
              <span
                role="button"
                tabIndex={0}
                className="sim-tab-close"
                onClick={e => {
                  e.stopPropagation()
                  handleRemoveMember(idx)
                }}
                onKeyDown={e => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.stopPropagation()
                    handleRemoveMember(idx)
                  }
                }}
              >
                ×
              </span>
            )}
          </button>
        ))}
        {allies.length < 5 && (
          <button
            type="button"
            className="sim-party-tab-add"
            onClick={handleAddMember}
            title={t('combat.simulator.addMember')}
          >
            + {t('combat.simulator.addMember')}
          </button>
        )}
      </div>

      {/* 快捷导入操作条 */}
      <div className="sim-editor-toolbar">
        <button
          type="button"
          className="sim-btn sim-btn--primary"
          onClick={handleImportSelf}
        >
          {t('combat.simulator.importSelf')}
        </button>

        {party && party.members && party.members.length > 0 && (
          <div className="sim-teammate-import-select">
            <span className="sim-label">{t('combat.simulator.importTeammate')}:</span>
            <select
              className="sim-select"
              onChange={e => {
                const member = party.members.find(m => m.playerId === e.target.value)
                if (member) handleImportTeammate(member.playerId, member.displayName)
              }}
              defaultValue=""
            >
              <option value="" disabled>{t('combat.simulator.selectTeammate')}</option>
              {party.members.map(m => (
                <option key={m.playerId} value={m.playerId}>
                  {m.displayName}
                </option>
              ))}
            </select>
          </div>
        )}
      </div>

      {/* 参战者详情二级子 Tab (属性 / 技能 / 装备 / 消耗品) */}
      <div className="sim-sub-tabs">
        <button
          type="button"
          className={`sim-sub-tab-btn ${activeSubTab === 'attributes' ? 'is-active' : ''}`}
          onClick={() => setActiveSubTab('attributes')}
        >
          📊 战斗属性
        </button>
        <button
          type="button"
          className={`sim-sub-tab-btn ${activeSubTab === 'skills' ? 'is-active' : ''}`}
          onClick={() => setActiveSubTab('skills')}
        >
          📜 功法技能
        </button>
        <button
          type="button"
          className={`sim-sub-tab-btn ${activeSubTab === 'equipment' ? 'is-active' : ''}`}
          onClick={() => setActiveSubTab('equipment')}
        >
          🛡️ 穿戴装备
        </button>
        <button
          type="button"
          className={`sim-sub-tab-btn ${activeSubTab === 'consumables' ? 'is-active' : ''}`}
          onClick={() => setActiveSubTab('consumables')}
        >
          🧪 战斗消耗
        </button>
      </div>

      {/* 1. 战斗属性子 Tab */}
      {activeSubTab === 'attributes' && (
        <div className="sim-stats-grid-container">
          <div className="sim-stats-category-block">
            <div className="sim-stats-category-title">基础生存与攻防</div>
            <div className="sim-stats-grid">
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="hp" /> 气血上限</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.maxHitpoints}
                  onChange={e => handleUpdateStat('maxHitpoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="mp" /> 灵力上限</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.maxManapoints}
                  onChange={e => handleUpdateStat('maxManapoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="physAtk" /> 物理攻击</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.physicalAttack}
                  onChange={e => handleUpdateStat('physicalAttack', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="spellAtk" /> 法术攻击</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.spellAttack}
                  onChange={e => handleUpdateStat('spellAttack', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="physDef" /> 护甲减伤</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.armor}
                  onChange={e => handleUpdateStat('armor', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="spellDef" /> 法术抗性</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.magicResistance}
                  onChange={e => handleUpdateStat('magicResistance', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="spd" /> 普攻间隔(ms)</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.attackIntervalMilliseconds}
                  onChange={e => handleUpdateStat('attackIntervalMilliseconds', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="hp" /> 5s气血回复</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.hpRegenPerFiveSeconds}
                  onChange={e => handleUpdateStat('hpRegenPerFiveSeconds', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="mp" /> 5s灵力回复</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.mpRegenPerFiveSeconds}
                  onChange={e => handleUpdateStat('mpRegenPerFiveSeconds', Number(e.target.value))}
                />
              </label>
            </div>
          </div>

          <div className="sim-stats-category-block">
            <div className="sim-stats-category-title">技巧博弈与暴击</div>
            <div className="sim-stats-grid">
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="acc" /> 命中点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.accuracy}
                  onChange={e => handleUpdateStat('accuracy', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="eva" /> 闪避点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.evasion}
                  onChange={e => handleUpdateStat('evasion', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="crit" /> 暴击点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.critical}
                  onChange={e => handleUpdateStat('critical', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="antiCrit" /> 免暴点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.criticalResistance}
                  onChange={e => handleUpdateStat('criticalResistance', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="critDmg" /> 暴击伤害点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.criticalDamagePoints}
                  onChange={e => handleUpdateStat('criticalDamagePoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="break" /> 破招点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.breakPower}
                  onChange={e => handleUpdateStat('breakPower', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name"><StatGlyph glyph="parry" /> 化解点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.parryResolve}
                  onChange={e => handleUpdateStat('parryResolve', Number(e.target.value))}
                />
              </label>
            </div>
          </div>

          <div className="sim-stats-category-block">
            <div className="sim-stats-category-title">伤害增减与特殊乘区</div>
            <div className="sim-stats-grid">
              <label className="sim-stat-field">
                <span className="sim-stat-name">全伤害增幅(%)</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.totalDamageBonusPoints}
                  onChange={e => handleUpdateStat('totalDamageBonusPoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">技能增伤(%)</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.skillBonusPoints}
                  onChange={e => handleUpdateStat('skillBonusPoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">攻击速度增幅</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.attackSpeedPoints}
                  onChange={e => handleUpdateStat('attackSpeedPoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">行动速度增幅</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.actionSpeedPoints}
                  onChange={e => handleUpdateStat('actionSpeedPoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">气血吸取点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.hpDrainPoints}
                  onChange={e => handleUpdateStat('hpDrainPoints', Number(e.target.value))}
                />
              </label>
              <label className="sim-stat-field">
                <span className="sim-stat-name">反伤点数</span>
                <input
                  type="number"
                  className="sim-input"
                  value={currentAlly.attributes.damageReflectPoints}
                  onChange={e => handleUpdateStat('damageReflectPoints', Number(e.target.value))}
                />
              </label>
            </div>
          </div>
        </div>
      )}

      {/* 2. 功法技能子 Tab */}
      {activeSubTab === 'skills' && (
        <div className="sim-skills-editor-panel">
          <div className="sim-skills-slot-list">
            {[0, 1, 2, 3, 4].map(slotIdx => {
              const isInternal = slotIdx === 0
              const slot = currentAlly.abilitySlots?.[slotIdx]
              const selectedAbilityId = slot?.abilityId || ''
              const level = slot?.level || 1
              const candidateAbilities = isInternal ? internalAbilities : activeAbilities
              const def = abilities.find(a => a.abilityId === selectedAbilityId)

              return (
                <div key={slotIdx} className={`sim-skill-slot-card ${isInternal ? 'is-internal' : ''}`}>
                  <div className="sim-skill-slot-badge">
                    {isInternal ? '【心法】' : `【神通 ${slotIdx}】`}
                  </div>
                  <div className="sim-skill-slot-body">
                    <div className="sim-skill-slot-row">
                      <select
                        className="sim-select sim-skill-select"
                        value={selectedAbilityId}
                        onChange={e => handleUpdateSkillSlot(slotIdx, e.target.value, level)}
                      >
                        <option value="">(空槽位)</option>
                        {candidateAbilities.map(a => (
                          <option key={a.abilityId} value={a.abilityId}>
                            {a.name} [{a.element === 'none' ? '无' : a.element}] ({a.category === 'physical' ? '物理' : '法术'})
                          </option>
                        ))}
                      </select>
                      {selectedAbilityId && (
                        <div className="sim-skill-level-input-wrap">
                          <span className="sim-skill-level-label">等级:</span>
                          <input
                            type="number"
                            min={1}
                            max={20}
                            className="sim-input sim-skill-level-input"
                            value={level}
                            onChange={e => handleUpdateSkillSlot(slotIdx, selectedAbilityId, Number(e.target.value))}
                          />
                        </div>
                      )}
                    </div>
                    {def && (
                      <div className="sim-skill-desc-row">
                        <span className="sim-skill-tag">消耗: {def.manaCost} MP</span>
                        {typeof def.castMilliseconds === 'number' && def.castMilliseconds > 0 && <span className="sim-skill-tag">吟唱: {(def.castMilliseconds / 1000).toFixed(1)}s</span>}
                        {typeof def.cooldownMilliseconds === 'number' && def.cooldownMilliseconds > 0 && <span className="sim-skill-tag">CD: {(def.cooldownMilliseconds / 1000).toFixed(1)}s</span>}
                        {typeof def.damageRatio === 'number' && def.damageRatio > 0 && <span className="sim-skill-tag text-emerald">倍率: {(def.damageRatio * 100).toFixed(0)}%</span>}
                        {typeof def.healingRatio === 'number' && def.healingRatio > 0 && <span className="sim-skill-tag text-cyan">治疗: {(def.healingRatio * 100).toFixed(0)}%</span>}
                        {def.description && <span className="sim-skill-desc-text">{def.description}</span>}
                      </div>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* 3. 穿戴装备子 Tab */}
      {activeSubTab === 'equipment' && (
        <div className="sim-equipment-editor-panel">
          <div className="sim-equipment-grid">
            {SIMULATOR_SLOTS.map(config => {
              const data = slotEquippedMap.get(config.slot)
              const equippedItem = data?.item
              const equipmentDef = data?.equipmentDef
              const definition = data?.definition
              const slotName = t(`app.equipmentPanel.${config.labelKey}`)
              const picking = activePickingSlot?.config.slot === config.slot

              let metadata = undefined
              let displayName = ''
              if (equippedItem && equipmentDef) {
                const meta = resolveItemMetadata(equippedItem.itemId)
                metadata = withItemGrade(
                  meta ?? {
                    label: equipmentDef.name,
                    glyph: '·',
                    tone: 'gold',
                    category: resolveItemCategoryLabel(equipmentDef.slot, t),
                    rarity: 'mundane',
                    rarityLabel: '',
                    description: equipmentDef.description ?? definition?.description ?? '',
                    source: '',
                    usage: '',
                  },
                  equipmentDef.tier ?? meta?.tier ?? 1,
                )
                displayName = equippedItem.enhancementLevel > 0
                  ? `${equipmentDef.name} +${equippedItem.enhancementLevel}`
                  : equipmentDef.name
              }

              return (
                <div key={config.slot} className={`sim-equipment-slot-card ${equippedItem ? 'is-equipped' : 'is-empty'}`}>
                  {/* 左侧：游戏内标准 64px 装备格子 */}
                  <div className="sim-equipment-slot-tile-anchor">
                    {equippedItem && equipmentDef && metadata ? (
                      <ItemTooltipTarget
                        className="equipment-slot-anchor"
                        definition={definition}
                        equipmentDefinition={equipmentDef}
                        enhancementLevel={equippedItem.enhancementLevel}
                        itemId={equippedItem.itemId}
                        metadata={metadata}
                      >
                        <button
                          className={`equipment-slot-btn is-equipped${picking ? ' is-picking' : ''}`}
                          onClick={e => {
                            const anchor = e.currentTarget
                            setActivePickingSlot(prev => prev?.config.slot === config.slot ? null : { config, anchor })
                          }}
                          type="button"
                          aria-label={`${slotName}: ${displayName}`}
                        >
                          <ItemTile
                            category={resolveItemCategoryLabel(equipmentDef.slot, t)}
                            className="inventory-cell"
                            enhancementLevel={equippedItem.enhancementLevel}
                            itemId={equippedItem.itemId}
                            level={metadata.level}
                            name={displayName}
                            rarity={metadata.rarity}
                          />
                        </button>
                      </ItemTooltipTarget>
                    ) : (
                      <button
                        className={`equipment-slot-btn is-empty${picking ? ' is-picking' : ''}`}
                        onClick={e => {
                          const anchor = e.currentTarget
                          setActivePickingSlot(prev => prev?.config.slot === config.slot ? null : { config, anchor })
                        }}
                        type="button"
                        aria-label={slotName}
                      >
                        <span className="equipment-slot-empty-label">{slotName}</span>
                      </button>
                    )}
                  </div>

                  {/* 右侧：槽位标题、名称、强化等级微调与卸下 */}
                  <div className="sim-equipment-slot-info">
                    <div className="sim-equipment-slot-header">
                      <span className="sim-equipment-slot-title">{slotName}</span>
                      {equippedItem && (
                        <button
                          type="button"
                          className="sim-equip-remove-btn"
                          onClick={() => handleUpdateEquipment(config.slot, '', 0)}
                        >
                          卸下
                        </button>
                      )}
                    </div>

                    {equippedItem && equipmentDef && metadata ? (
                      <div className="sim-equipment-slot-details">
                        <div className="sim-equipment-item-name" data-rarity={metadata.rarity}>
                          {equipmentDef.name}
                          <span className="sim-equipment-tier-tag">T{equipmentDef.tier}</span>
                        </div>
                        <div className="sim-equip-enhance-row">
                          <span className="sim-enhance-label">强化:</span>
                          <input
                            type="number"
                            min={0}
                            max={20}
                            className="sim-input sim-enhance-input"
                            value={equippedItem.enhancementLevel}
                            onChange={e => handleUpdateEquipment(config.slot, equippedItem.itemId, Number(e.target.value))}
                          />
                          <span className="sim-enhance-badge text-gold">+{equippedItem.enhancementLevel}</span>
                        </div>
                      </div>
                    ) : (
                      <div
                        className="sim-equipment-slot-empty-hint"
                        onClick={e => {
                          const card = e.currentTarget.closest('.sim-equipment-slot-card')
                          const btn = card?.querySelector('.equipment-slot-btn') as HTMLElement | null
                          if (btn) {
                            setActivePickingSlot({ config, anchor: btn })
                          }
                        }}
                      >
                        (点击格子挑选装备)
                      </div>
                    )}
                  </div>
                </div>
              )
            })}
          </div>

          {/* 装备待选网格列表 */}
          <ItemGridPicker
            allowEmpty
            anchor={activePickingSlot?.anchor ?? null}
            items={pickerItems}
            onClose={() => setActivePickingSlot(null)}
            onSelect={handleSelectEquipmentFromPicker}
            open={activePickingSlot !== null}
            title={activePickingSlot ? t('app.equipmentPanel.chooseEquipmentTitle', { slot: t(`app.equipmentPanel.${activePickingSlot.config.labelKey}`) }) : ''}
            value={activePickingSlot ? (slotEquippedMap.get(activePickingSlot.config.slot)?.item.itemId ?? null) : null}
          />
        </div>
      )}
      {/* 4. 战斗消耗品子 Tab */}
      {activeSubTab === 'consumables' && (
        <div className="sim-consumables-editor-panel">
          <div className="sim-consumables-grid">
            {[0, 1, 2, 3, 4, 5].map(sIdx => {
              const isPotion = sIdx < 3
              const slot = (currentAlly.consumables || [])[sIdx]
              const itemId = slot?.itemId || ''
              const quantity = slot?.quantity ?? 10
              const enabled = slot?.enabled ?? true

              return (
                <div key={sIdx} className={`sim-consumable-card ${isPotion ? 'is-potion' : ''}`}>
                  <div className="sim-consumable-header">
                    <span className="sim-consumable-slot-name">
                      {isPotion ? `【药品槽 ${sIdx + 1}】` : `【通用槽 ${sIdx - 2}】`}
                    </span>
                    <label className="sim-consumable-toggle">
                      <input
                        type="checkbox"
                        checked={enabled}
                        onChange={e => handleUpdateConsumable(sIdx, itemId, quantity, e.target.checked)}
                      />
                      <span>启用</span>
                    </label>
                  </div>
                  <div className="sim-consumable-body">
                    <select
                      className="sim-select sim-consumable-select"
                      value={itemId}
                      onChange={e => handleUpdateConsumable(sIdx, e.target.value || null, quantity, enabled)}
                    >
                      <option value="">(空槽位)</option>
                      {consumableItems.map(item => (
                        <option key={item.itemId} value={item.itemId}>
                          {item.name}
                        </option>
                      ))}
                    </select>
                    {itemId && (
                      <div className="sim-consumable-qty-row">
                        <span className="sim-qty-label">备弹数量:</span>
                        <input
                          type="number"
                          min={1}
                          max={999}
                          className="sim-input sim-qty-input"
                          value={quantity}
                          onChange={e => handleUpdateConsumable(sIdx, itemId, Number(e.target.value), enabled)}
                        />
                      </div>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
})
