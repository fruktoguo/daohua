import { memo, useId, useState } from 'react'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findEquipmentInCatalog, findItemInCatalog, parseEnhancementVariant } from '../game/ids'
import { sendLoadoutReplace } from '../game/loadoutCommands'
import { useMarketStore } from '../game/marketStore'
import { useT } from '../i18n'
import { gameClient } from '../network/gameClient'
import { useUiStore } from '../uiStore'
import type { InventoryItem } from '../game/types'
import { FloatingMenuDivider, FloatingMenuGroup, FloatingMenuItem, FloatingPopover } from './FloatingPopover'
import { ItemCard } from './ItemDisplay'
import { Modal } from './Modal'
import { QuantitySelector } from './QuantitySelector'

/** 物品数量弹窗模式：使用（批量研习）、丢弃（永久销毁）或系统回收（按 sellPrice 兑灵石） */
type ItemQuantityDialogMode = 'use' | 'discard' | 'recycle'

/**
 * 判断已装配装备与新装备之间的槽位冲突：
 * 同槽互斥；双手武器（two-hand）与单手武器（weapon）/副手（off-hand）互斥，
 * 与服务端 loadout.replace 的槽位唯一性约束保持一致。
 */
function equipmentSlotConflicts(existingSlot: string, targetSlot: string): boolean {
  if (existingSlot === targetSlot) return true
  if (targetSlot === 'two-hand' && (existingSlot === 'weapon' || existingSlot === 'off-hand')) return true
  if ((targetSlot === 'weapon' || targetSlot === 'off-hand') && existingSlot === 'two-hand') return true
  return false
}

/**
 * 物品数量弹窗 (ItemQuantityDialog)
 *
 * 使用、丢弃与系统回收共用的「数量选择 + 二次确认」模态框；
 * 使用模式发送 ability.learn 批量研读，回收模式额外展示配表单价与合计灵石收益。
 */
function ItemQuantityDialog({
  item,
  mode,
  unitPrice,
  onClose,
}: {
  item: InventoryItem
  mode: ItemQuantityDialogMode
  /** 系统回收单价（灵石/件），仅回收模式使用 */
  unitPrice: number
  onClose: () => void
}): React.JSX.Element {
  const t = useT()
  const titleId = useId()
  const isUse = mode === 'use'
  const isDiscard = mode === 'discard'
  const isTome = item.itemId.startsWith('item.tome.')
  const max = Math.max(1, Math.floor(item.amount))
  const [quantity, setQuantity] = useState(1)
  const presets = [...new Set([1, 10, 100, max].filter(value => value >= 1 && value <= max))].sort((a, b) => a - b)

  const confirm = () => {
    if (isUse) gameClient.sendCommand('ability.learn', { itemId: item.itemId, quantity })
    else gameClient.sendCommand(isDiscard ? 'item.discard' : 'item.sell', { itemId: item.itemId, quantity })
    onClose()
  }

  return (
    <Modal labelledBy={titleId} onClose={onClose} open size="small">
      <div className="confirm-dialog">
        <h2 id={titleId}>
          {isUse
            ? t('app.inventoryPanel.useTitle', { name: item.name })
            : t(isDiscard ? 'app.inventoryPanel.discardTitle' : 'app.inventoryPanel.recycleTitle', { name: item.name })}
        </h2>
        <p>
          {isUse
            ? t(isTome ? 'app.inventoryPanel.useTomeDescription' : 'app.inventoryPanel.useFragmentDescription')
            : isDiscard
              ? t('app.inventoryPanel.discardDescription')
              : t('app.inventoryPanel.recycleDescription', { price: formatNumber(unitPrice) })}
        </p>
        <QuantitySelector
          ariaLabel={t(isUse ? 'app.inventoryPanel.useQuantityAria' : 'app.inventoryPanel.quantityAria')}
          max={max}
          onChange={value => setQuantity(Math.max(1, Math.min(max, value)))}
          presets={presets}
          value={quantity}
        />
        {mode === 'recycle' && (
          <p className="inventory-dialog-total">
            {t('app.inventoryPanel.recycleTotal', { total: formatNumber(unitPrice * quantity) })}
          </p>
        )}
        <div className="confirm-dialog-actions">
          <button onClick={onClose} type="button">{t('common.cancel')}</button>
          <button
            className={isDiscard ? 'danger confirm-primary' : 'confirm-primary'}
            onClick={confirm}
            type="button"
          >
            {t(isUse ? 'app.inventoryPanel.confirmUse' : isDiscard ? 'app.inventoryPanel.confirmDiscard' : 'app.inventoryPanel.confirmRecycle')}
          </button>
        </div>
      </div>
    </Modal>
  )
}

/**
 * 储物袋物品交互单元 (InventoryItemCell)
 *
 * 背包格子的完整交互封装：
 * 1. 【悬停】保留 ItemTooltipTarget 修仙详情卡片（含 Buff/机制副气泡与数量副气泡）；
 * 2. 【左键】弹出悬浮操作菜单——按物品类型动态给出可用动作：
 *    - 使用：功法秘籍/残卷（ability.learn 参悟，弹数量弹窗支持批量研读）与装备（loadout.replace 穿戴，自动互斥同槽位）；
 *    - 交易：可交易物品/装备跳转坊市标的详情视图（买入/挂售同屏）；
 *    - 回收：配表 sellPrice > 0 的物品按系统价兑换灵石（item.sell）；
 *    - 丢弃：任意持有物品销毁（item.discard），已装备物品禁用；
 * 3. 【右键】可直接使用的物品立即触发使用：秘籍/残卷进批量研读弹窗，装备直接穿戴，其余仅屏蔽浏览器菜单；
 * 4. 【数量】使用（秘籍/残卷）、丢弃与回收经统一数量选择 + 二次确认弹窗执行。
 */
export const InventoryItemCell = memo(function InventoryItemCell({
  item,
}: {
  item: InventoryItem
}): React.JSX.Element {
  const t = useT()
  const definition = useGameStore(state => findItemInCatalog(state.items, item.itemId))
  const equipmentDef = useGameStore(state => findEquipmentInCatalog(state.equipment, item.itemId))
  // 已装配判定按 (itemId, 强化等级) 精确匹配：背包中同物不同 +N 变体行互不串位。
  const enhancementLevel = parseEnhancementVariant(item.variant)
  const equipped = useGameStore(
    state => state.combat?.equipment?.some(
      entry => entry.itemId === item.itemId && Math.floor(entry.enhancementLevel) === enhancementLevel
    ) ?? false
  )
  const [dialog, setDialog] = useState<ItemQuantityDialogMode | null>(null)

  const isManual = item.itemId.startsWith('item.tome.') || item.itemId.startsWith('item.fragment.')
  const usable = !equipped && (isManual || equipmentDef !== undefined)
  const tradable = equipmentDef !== undefined || definition?.isTradable === true
  // 强化变体行暂不可回收/丢弃：item.sell/item.discard 协议目前只结算基础变体行，
  // 放行会错扣同物基础资产；待服务端命令支持 variant 参数后放开。
  const isVariantRow = Boolean(item.variant)
  const recyclable = !equipped
    && !isVariantRow
    && definition !== undefined
    && definition.sellPrice > 0
    && !item.itemId.startsWith('currency.')

  /** 使用：秘籍/残卷进批量研读弹窗（ability.learn 携带 quantity）；装备立即穿戴（loadout.replace 剔除冲突槽位后推入） */
  const use = () => {
    if (isManual) {
      setDialog('use')
      return
    }
    if (!equipmentDef) return
    const { combat, equipment } = useGameStore.getState()
    if (!combat) return
    const nextEquipment = (combat.equipment ?? []).filter(entry => {
      const equippedDef = findEquipmentInCatalog(equipment, entry.itemId)
      return !equippedDef || !equipmentSlotConflicts(equippedDef.slot, equipmentDef.slot)
    })
    nextEquipment.push({ itemId: item.itemId, enhancementLevel })
    // 经统一入口发送：功法槽位会顺带完成类别互斥与等级归一化。
    sendLoadoutReplace({ equipment: nextEquipment })
  }

  /** 交易：跳转坊市浏览页并选中该标的，进入含买入/挂售按钮的详情视图 */
  const trade = () => {
    useUiStore.getState().setActiveTab('market')
    const market = useMarketStore.getState()
    market.setActiveTab('browse')
    market.setSelectedItemId(item.itemId)
  }

  const handleContextMenu = (event: React.MouseEvent) => {
    event.preventDefault()
    if (usable) use()
  }

  const menuContent = (
    <FloatingMenuGroup title={enhancementLevel > 0 ? `${item.name} +${enhancementLevel}` : item.name}>
      {usable && (
        <FloatingMenuItem
          label={equipmentDef ? t('app.inventoryPanel.useEquip') : t('app.inventoryPanel.useLearn')}
          onClick={use}
          tone="jade"
        />
      )}
      {tradable && (
        <FloatingMenuItem label={t('app.inventoryPanel.trade')} onClick={trade} />
      )}
      {recyclable && definition && (
        <FloatingMenuItem
          hint={t('app.inventoryPanel.recycleHint', { price: formatNumber(definition.sellPrice) })}
          label={t('app.inventoryPanel.recycle')}
          onClick={() => setDialog('recycle')}
        />
      )}
      {(usable || tradable || recyclable) && <FloatingMenuDivider />}
      <FloatingMenuItem
        disabled={equipped || isVariantRow}
        hint={equipped ? t('app.inventoryPanel.equippedHint') : isVariantRow ? t('app.inventoryPanel.variantDisposeHint') : undefined}
        label={t('app.inventoryPanel.discard')}
        onClick={() => setDialog('discard')}
        tone="danger"
      />
    </FloatingMenuGroup>
  )

  return (
    <>
      <FloatingPopover
        className="inventory-item-menu-card"
        content={menuContent}
        displayContents
        gap={6}
        onContextMenu={handleContextMenu}
        placement="right-start"
        size="auto"
        trigger="click"
      >
        <ItemCard definition={definition} equipmentDefinition={equipmentDef} item={item} />
      </FloatingPopover>
      {dialog !== null && (
        <ItemQuantityDialog
          item={item}
          mode={dialog}
          onClose={() => setDialog(null)}
          unitPrice={definition?.sellPrice ?? 0}
        />
      )}
    </>
  )
})
