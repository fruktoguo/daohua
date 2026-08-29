import { memo, useMemo, useState } from 'react'
import { gameClient } from '../network/gameClient'
import { useGameStore } from '../game/gameStore'
import { resolveItemCategoryLabel, resolveItemMetadata, withItemGrade } from '../game/ids'
import { useT } from '../i18n'
import type { CombatEquipmentDefinition, EquipmentSlot, EquippedItem, ItemDefinition } from '../game/types'
import { CharacterAvatar } from './CharacterAvatar'
import { ItemGridPicker, type ItemPickerEntry } from './ItemGridPicker'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { RealmBadge } from './RealmBadge'
import { calculateRealmInfo } from '../game/combatConstants'

interface SlotConfig {
  slot: EquipmentSlot
  labelKey: 'slotHead' | 'slotArmor' | 'slotLegs' | 'slotWeapon' | 'slotTrinket' | 'slotBoots'
}

const LEFT_SLOTS: readonly SlotConfig[] = [
  { slot: 'head', labelKey: 'slotHead' },
  { slot: 'armor', labelKey: 'slotArmor' },
  { slot: 'legs', labelKey: 'slotLegs' },
]

const RIGHT_SLOTS: readonly SlotConfig[] = [
  { slot: 'weapon', labelKey: 'slotWeapon' },
  { slot: 'trinket', labelKey: 'slotTrinket' },
  { slot: 'boots', labelKey: 'slotBoots' },
]

const ALL_SLOTS = [...LEFT_SLOTS, ...RIGHT_SLOTS]

function isSlotMatch(equipmentSlot: EquipmentSlot, targetSlot: EquipmentSlot): boolean {
  if (equipmentSlot === targetSlot) return true
  if (targetSlot === 'weapon' && equipmentSlot === 'two-hand') return true
  if (targetSlot === 'trinket' && (equipmentSlot === 'ring' || equipmentSlot === 'neck' || equipmentSlot === 'talisman')) return true
  return false
}

interface EquipmentSlotCardProps {
  config: SlotConfig
  equippedItem?: EquippedItem
  definition?: ItemDefinition
  equipmentDef?: CombatEquipmentDefinition
  onClick: () => void
}
const EquipmentSlotCard = memo(function EquipmentSlotCard({
  config,
  equippedItem,
  definition,
  equipmentDef,
  onClick,
}: EquipmentSlotCardProps): React.JSX.Element {
  const t = useT()
  const slotName = t(`app.equipmentPanel.${config.labelKey}`)

  if (equippedItem && definition) {
    const meta = resolveItemMetadata(equippedItem.itemId)
    const metadata = withItemGrade(
      meta ?? {
        label: definition.name,
        glyph: '·',
        tone: 'gold',
        category: resolveItemCategoryLabel(definition.category, t),
        rarity: 'mundane',
        rarityLabel: '',
        description: definition.description,
        source: '',
        usage: '',
      },
      definition.tier ?? meta?.level ?? 1,
    )

    const displayName = equippedItem.enhancementLevel > 0
      ? `${definition.name} +${equippedItem.enhancementLevel}`
      : definition.name

    return (
      <ItemTooltipTarget
        className="equipment-slot-anchor"
        definition={definition}
        equipmentDefinition={equipmentDef}
        enhancementLevel={equippedItem.enhancementLevel}
        itemId={equippedItem.itemId}
        metadata={metadata}
      >
        <button
          className="equipment-slot-btn is-equipped"
          onClick={onClick}
          type="button"
          aria-label={`${slotName}: ${displayName}`}
        >
          <span className="equipment-slot-badge">{slotName}</span>
          <ItemTile
            category={resolveItemCategoryLabel(definition.category, t)}
            className="inventory-cell"
            level={metadata.level}
            name={displayName}
            rarity={metadata.rarity}
          />
        </button>
      </ItemTooltipTarget>
    )
  }

  return (
    <button
      className="equipment-slot-btn is-empty"
      onClick={onClick}
      type="button"
      aria-label={slotName}
    >
      <span className="equipment-slot-empty-label">{slotName}</span>
    </button>
  )
})

/**
 * 装备管理面板 (EquipmentPanel)
 *
 * 核心特性：
 * 1. 【角色虚影居中】：中间展示角色立绘虚影与灵光法阵；
 * 2. 【六大部位环绕】：左侧（头部/衣服/腿部）与右侧（武器/饰品/鞋靴）对称竖排排列；
 * 3. 【穿脱与挑选】：点击槽位打开专属物品网格挑选弹窗，支持一键穿戴与卸下；
 * 4. 【技艺工具预留】：底部整齐排布 4 个技艺/工具装备扩展槽位（采矿/伐木/炼丹/炼器）。
 */
export const EquipmentPanel = memo(function EquipmentPanel(): React.JSX.Element {
  const t = useT()
  const playerName = useGameStore(state => state.name)
  const visual = useGameStore(state => state.visual)
  const stats = useGameStore(state => state.stats)
  const combat = useGameStore(state => state.combat)
  const inventory = useGameStore(state => state.inventory)
  const itemCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)

  const [activePickingSlot, setActivePickingSlot] = useState<SlotConfig | null>(null)

  const realmInfo = useMemo(() => calculateRealmInfo(stats.spirit ?? 0, stats.realmLevel), [stats.realmLevel, stats.spirit])

  const equippedList = combat?.equipment ?? []

  // 构建各槽位当前装备的映射
  const slotEquippedMap = useMemo(() => {
    const map = new Map<EquipmentSlot, { item: EquippedItem; definition?: ItemDefinition; equipmentDef?: CombatEquipmentDefinition }>()
    for (const eq of equippedList) {
      const eqDef = equipmentCatalog.find(e => e.equipmentId === eq.itemId)
      if (!eqDef) continue
      const itemDef = itemCatalog.find(i => i.itemId === eq.itemId)

      for (const config of ALL_SLOTS) {
        if (isSlotMatch(eqDef.slot, config.slot)) {
          map.set(config.slot, { item: eq, definition: itemDef, equipmentDef: eqDef })
          break
        }
      }
    }
    return map
  }, [equippedList, equipmentCatalog, itemCatalog])

  // 当前挑选弹窗的可选装备列表
  const pickerItems = useMemo<ItemPickerEntry[]>(() => {
    if (!activePickingSlot) return []
    const targetSlot = activePickingSlot.slot

    // 1. 过滤背包中符合该部位的可用装备
    const matchingInventoryItems: ItemPickerEntry[] = []
    for (const inv of inventory) {
      if (inv.amount <= 0) continue
      const eqDef = equipmentCatalog.find(e => e.equipmentId === inv.itemId)
      if (!eqDef || !isSlotMatch(eqDef.slot, targetSlot)) continue

      const itemDef = itemCatalog.find(i => i.itemId === inv.itemId)
      matchingInventoryItems.push({
        itemId: inv.itemId,
        name: inv.name,
        amount: inv.amount,
        tier: inv.tier ?? 1,
        category: resolveItemCategoryLabel(inv.category, t),
        description: itemDef?.description ?? '',
        definition: itemDef,
      })
    }

    // 2. 若当前部位已穿戴，且背包中数量为 0，补充展示当前穿戴装备便于对比/选中
    const currentEquipped = slotEquippedMap.get(targetSlot)
    if (currentEquipped && !matchingInventoryItems.some(item => item.itemId === currentEquipped.item.itemId)) {
      const itemDef = currentEquipped.definition
      matchingInventoryItems.unshift({
        itemId: currentEquipped.item.itemId,
        name: itemDef?.name ?? currentEquipped.item.itemId,
        amount: 1,
        tier: itemDef?.tier ?? 1,
        category: resolveItemCategoryLabel(itemDef?.category, t),
        description: itemDef?.description ?? '',
        definition: itemDef,
      })
    }

    return matchingInventoryItems
  }, [activePickingSlot, inventory, equipmentCatalog, itemCatalog, slotEquippedMap, t])

  // 处理装备选择 / 卸下
  const handleSelectEquipment = (selectedItemId: string | null) => {
    if (!combat || !activePickingSlot) return
    const targetSlot = activePickingSlot.slot

    // 过滤掉当前部位已穿戴的装备
    const nextEquipped = (combat.equipment ?? []).filter(eq => {
      const eqDef = equipmentCatalog.find(e => e.equipmentId === eq.itemId)
      if (!eqDef) return true
      return !isSlotMatch(eqDef.slot, targetSlot)
    })

    // 若选择了新装备则推入
    if (selectedItemId) {
      nextEquipped.push({
        itemId: selectedItemId,
        enhancementLevel: 0,
      })
    }

    gameClient.sendCommand('loadout.replace', {
      ...combat,
      equipment: nextEquipped,
    })

    setActivePickingSlot(null)
  }

  const activeSlotEquipped = activePickingSlot ? slotEquippedMap.get(activePickingSlot.slot) : null
  const currentPickerValue = activeSlotEquipped?.item.itemId ?? null
  const pickerTitle = activePickingSlot
    ? t('app.equipmentPanel.chooseEquipmentTitle', { slot: t(`app.equipmentPanel.${activePickingSlot.labelKey}`) })
    : ''

  return (
    <div className="equipment-view-panel">
      {/* 上部主舞台：六部位装备对称环绕角色虚影 */}
      <div className="equipment-stage">
        {/* 左侧三槽位：头部、衣服、腿部 */}
        <div className="equipment-slot-col equipment-slot-col--left">
          {LEFT_SLOTS.map(config => {
            const data = slotEquippedMap.get(config.slot)
            return (
              <EquipmentSlotCard
                config={config}
                definition={data?.definition}
                equipmentDef={data?.equipmentDef}
                equippedItem={data?.item}
                key={config.slot}
                onClick={() => setActivePickingSlot(config)}
              />
            )
          })}
        </div>

        {/* 中间角色法相虚影与修士名牌 */}
        <div className="equipment-avatar-center">
          <div className="equipment-avatar-phantom-halo" aria-hidden="true" />
          <div className="equipment-avatar-phantom-stage">
            <CharacterAvatar
              actorId="player"
              className="equipment-avatar-phantom"
              name={playerName}
              role="player"
              visual={visual}
            />
          </div>
          <div className="equipment-avatar-meta">
            <strong className="equipment-avatar-name">{playerName}</strong>
            <div className="equipment-avatar-badge-wrap">
              <RealmBadge realmInfo={realmInfo} />
            </div>
          </div>
        </div>

        {/* 右侧三槽位：武器、饰品、鞋靴 */}
        <div className="equipment-slot-col equipment-slot-col--right">
          {RIGHT_SLOTS.map(config => {
            const data = slotEquippedMap.get(config.slot)
            return (
              <EquipmentSlotCard
                config={config}
                definition={data?.definition}
                equipmentDef={data?.equipmentDef}
                equippedItem={data?.item}
                key={config.slot}
                onClick={() => setActivePickingSlot(config)}
              />
            )
          })}
        </div>
      </div>

      {/* 底部技艺法宝与工具装备扩展槽位 */}
      <div className="equipment-tools-section">
        <div className="equipment-tools-header">
          <span className="equipment-tools-title">{t('app.equipmentPanel.toolsTitle')}</span>
        </div>
        <div className="equipment-tools-grid">
          <div className="equipment-tool-slot is-empty">
            <span className="equipment-tool-empty-label">{t('app.equipmentPanel.toolMining')}</span>
          </div>
          <div className="equipment-tool-slot is-empty">
            <span className="equipment-tool-empty-label">{t('app.equipmentPanel.toolLogging')}</span>
          </div>
          <div className="equipment-tool-slot is-empty">
            <span className="equipment-tool-empty-label">{t('app.equipmentPanel.toolAlchemy')}</span>
          </div>
          <div className="equipment-tool-slot is-empty">
            <span className="equipment-tool-empty-label">{t('app.equipmentPanel.toolForging')}</span>
          </div>
        </div>
      </div>

      {/* 装备选择网格弹窗 */}
      <ItemGridPicker
        allowEmpty
        emptyLabel={t('app.equipmentPanel.unequip')}
        items={pickerItems}
        onClose={() => setActivePickingSlot(null)}
        onSelect={handleSelectEquipment}
        open={activePickingSlot !== null}
        title={pickerTitle}
        value={currentPickerValue}
      />
    </div>
  )
})
