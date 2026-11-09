import { memo, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import {
  collectOccupiedEquipmentCounts,
  findEquipmentInCatalog,
  findItemInCatalog,
  parseEnhancementVariant,
  resolveInventoryItemUnitPrice,
  resolveItemCategoryLabel,
  resolveItemSubCategory,
} from '../game/ids'
import type { ActionDefinition, CombatEquipmentDefinition, InventoryItem, ItemDefinition } from '../game/types'
import { useT, type TFunction } from '../i18n'
import { gameClient } from '../network/gameClient'
import { ConfirmDialog } from './ConfirmDialog'
import { ItemReference } from './ItemDisplay'
import { ItemGridPicker, ItemUseSlot, itemPickerEntryKey, type ItemPickerEntry } from './ItemGridPicker'
import { QuantitySelector } from './QuantitySelector'

export type SecretArtOperation = 'liquefy' | 'disassemble' | 'convert'

/**
 * 展开装备锻造配方并汇总为基础物品原材料，按每种材料 70% 向下取整（同构于服务端 SecretArtRules.ComputeDisassemblyReturns）。
 */
export function computeDisassemblyReturns(equipmentId: string, actions: readonly ActionDefinition[]): Array<{ itemId: string; amount: number }> {
  const maxDepth = 4
  function collect(id: string, depth: number): Map<string, number> | null {
    if (depth > maxDepth) return null
    const recipe = actions.find(a =>
      a.technique === 'forging' &&
      a.yields?.some(y => y.kind === 'item' && y.id === id)
    )
    if (!recipe || !recipe.inputs) return null
    const outputYield = recipe.yields?.find(y => y.kind === 'item' && y.id === id)
    const outputAmount = outputYield?.amount ?? 1
    if (outputAmount <= 0) return null

    const map = new Map<string, number>()
    for (const [inputId, inputAmount] of Object.entries(recipe.inputs)) {
      const perOutput = inputAmount / outputAmount
      if (inputId.startsWith('equipment.')) {
        const nested = collect(inputId, depth + 1)
        if (nested) {
          for (const [matId, matAmt] of nested) {
            map.set(matId, (map.get(matId) ?? 0) + matAmt * perOutput)
          }
        }
      } else {
        map.set(inputId, (map.get(inputId) ?? 0) + perOutput)
      }
    }
    return map
  }

  const baseMats = collect(equipmentId, 0)
  if (!baseMats) return []
  const result: Array<{ itemId: string; amount: number }> = []
  for (const [matId, amt] of baseMats) {
    const floored = Math.floor(amt * 0.70)
    if (floored > 0) {
      result.push({ itemId: matId, amount: floored })
    }
  }
  return result
}

/**
 * 收集当前秘法操作（化灵 / 分解 / 转化）的待选物品，适配为通用 ItemPickerEntry
 */
export function collectSecretArtCandidates(
  operation: SecretArtOperation,
  inventory: readonly InventoryItem[],
  items: readonly ItemDefinition[],
  equipment: readonly CombatEquipmentDefinition[],
  occupiedCounts: Map<string, number>,
  actions: readonly ActionDefinition[],
  t: TFunction,
): ItemPickerEntry[] {
  const candidates: ItemPickerEntry[] = []

  for (const inv of inventory) {
    if (inv.amount <= 0) continue
    if (inv.variant && !inv.variant.startsWith('+')) continue

    const eqDef = findEquipmentInCatalog(equipment, inv.itemId)
    const level = parseEnhancementVariant(inv.variant)
    const occupied = eqDef ? (occupiedCounts.get(`${inv.itemId}#${level}`) ?? 0) : 0
    const available = inv.amount - occupied
    if (available <= 0) continue

    const itemDef = findItemInCatalog(items, inv.itemId)

    if (operation === 'liquefy') {
      if (inv.itemId.startsWith('currency.') || itemDef?.category === 'currency') continue
      const unitPrice = resolveInventoryItemUnitPrice(inv.itemId, inv.variant, items, equipment)
      if (unitPrice <= 0) continue
    } else if (operation === 'disassemble') {
      if (!eqDef) continue
      const returns = computeDisassemblyReturns(inv.itemId, actions)
      if (returns.length === 0) continue
    } else {
      if (!inv.itemId.startsWith('item.fragment.') || inv.variant) continue
    }

    const baseName = itemDef?.name ?? eqDef?.name ?? inv.name
    const displayName = level > 0 ? `${baseName} +${level}` : baseName

    candidates.push({
      itemId: inv.itemId,
      name: displayName,
      amount: available,
      tier: itemDef?.tier ?? eqDef?.tier ?? 1,
      category: resolveItemCategoryLabel(resolveItemSubCategory(inv.itemId, itemDef, eqDef, inv.subCategory), t),
      description: itemDef?.description ?? eqDef?.description ?? inv.description ?? '',
      definition: itemDef,
      equipmentDefinition: eqDef,
      variant: inv.variant ?? null,
      enhancementLevel: level,
    })
  }

  return candidates.sort((a, b) =>
    (b.tier - a.tier) ||
    a.itemId.localeCompare(b.itemId) ||
    ((a.enhancementLevel ?? 0) - (b.enhancementLevel ?? 0)) ||
    a.name.localeCompare(b.name)
  )
}

export const SecretArtOperations = memo(function SecretArtOperations({
  operation,
}: {
  operation: SecretArtOperation
}): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const combatEquipment = useGameStore(state => state.combat?.equipment)
  const queueEntries = useGameStore(state => state.queue?.entries)
  const actions = useGameStore(state => state.actions)

  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [quantity, setQuantity] = useState(1)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)

  const occupied = useMemo(() => collectOccupiedEquipmentCounts(combatEquipment, queueEntries), [combatEquipment, queueEntries])
  const candidates = useMemo(
    () => collectSecretArtCandidates(operation, inventory, items, equipment, occupied, actions, t),
    [actions, equipment, inventory, items, occupied, operation, t],
  )

  const selected = useMemo(() => {
    if (!selectedKey) return null
    return candidates.find(item => itemPickerEntryKey(item.itemId, item.variant) === selectedKey) ?? null
  }, [candidates, selectedKey])

  const maxQuantity = selected ? Math.max(1, Math.floor(selected.amount)) : 1
  const effectiveQuantity = Math.min(Math.max(1, quantity), maxQuantity)

  // 产出收益实时解析
  const recycleUnitPrice = useMemo(() => {
    if (!selected || operation !== 'liquefy') return 0
    return resolveInventoryItemUnitPrice(selected.itemId, selected.variant, items, equipment)
  }, [equipment, items, operation, selected])
  const liquefyUnitPrice = recycleUnitPrice * 3

  const disassemblyReturns = useMemo(() => {
    if (!selected || operation !== 'disassemble') return []
    return computeDisassemblyReturns(selected.itemId, actions)
  }, [actions, operation, selected])

   const tomeId = useMemo(() => {
     if (!selected || operation !== 'convert') return null
    return 'item.tome.' + selected.itemId.slice('item.fragment.'.length)
   }, [operation, selected])

  const submit = () => {
    if (!selected) return
    gameClient.sendCommand(`item.${operation}`, {
      itemId: selected.itemId,
      ...(selected.variant ? { variant: selected.variant } : {}),
      quantity: effectiveQuantity,
    })
    setConfirmOpen(false)
  }

  const opCap = operation[0].toUpperCase() + operation.slice(1)

  return (
    <div className="enhancement-layout">
      {/* 目标槽位与操作数量配置 */}
      <div className="settings-group">
        <h3 className="settings-group-title">
          <span>{t(`app.secretArt.targetSlot${opCap}`)}</span>
        </h3>
        <div className="enhancement-target">
          <ItemUseSlot
            item={selected}
            onClick={event => setPickerAnchor(prev => (prev ? null : event.currentTarget))}
          />
          <div className="enhancement-target-config">
            <span className="setting-label">{t('app.secretArt.quantityLabel')}</span>
            <QuantitySelector
              ariaLabel={t('app.secretArt.quantityAria')}
              disabled={!selected}
              max={maxQuantity}
              maxShortcut
              min={1}
              onChange={setQuantity}
              value={effectiveQuantity}
            />
          </div>
        </div>

        <div className="enhancement-action-row">
          <button
            className="action-modal-submit"
            disabled={!selected || maxQuantity <= 0}
            onClick={() => setConfirmOpen(true)}
            type="button"
          >
            {t(`app.secretArt.submit${opCap}`)}
          </button>
        </div>
      </div>

      {/* 产出收益实时预览 */}
      <div className="settings-group">
        <h3 className="settings-group-title">{t('app.secretArt.previewTitle')}</h3>
        <p className="setting-desc">{t(`app.secretArt.${operation}Desc`)}</p>

        {!selected ? (
          <p className="setting-desc">{t(`app.secretArt.previewEmpty${opCap}`)}</p>
        ) : (
          <dl className="action-detail-list">
            {operation === 'liquefy' && (
              <>
                <div>
                  <dt>{t('app.secretArt.unitPriceLabel')}</dt>
                  <dd>
                    <ItemReference amount={recycleUnitPrice} itemId="currency.spirit-stone" />
                  </dd>
                </div>
                <div>
                  <dt>{t('app.secretArt.liquefyUnitPriceLabel')}</dt>
                  <dd>
                    <ItemReference amount={liquefyUnitPrice} itemId="currency.spirit-stone" />
                  </dd>
                </div>
                <div>
                  <dt>{t('app.secretArt.totalRewardLabel')}</dt>
                  <dd className="enhancement-outcome">
                    <ItemReference amount={liquefyUnitPrice * effectiveQuantity} itemId="currency.spirit-stone" />
                  </dd>
                </div>
              </>
            )}

            {operation === 'disassemble' && (
              <>
                <div>
                  <dt>{t('app.secretArt.disassembleCountLabel')}</dt>
                  <dd>
                    <ItemReference amount={effectiveQuantity} enhancementLevel={selected.enhancementLevel} itemId={selected.itemId} />
                  </dd>
                </div>
                <div>
                  <dt>{t('app.secretArt.totalRewardLabel')}</dt>
                  <dd>
                    <div className="secret-art-returns-list">
                      {disassemblyReturns.map(r => (
                        <ItemReference
                          amount={r.amount * effectiveQuantity}
                          itemId={r.itemId}
                          key={r.itemId}
                        />
                      ))}
                    </div>
                  </dd>
                </div>
              </>
            )}

            {operation === 'convert' && (
              <>
                <div>
                  <dt>{t('app.secretArt.convertCountLabel')}</dt>
                  <dd>
                    <ItemReference amount={effectiveQuantity} itemId={selected.itemId} />
                  </dd>
                </div>
                <div>
                  <dt>{t('app.secretArt.convertOutcomeTome')}</dt>
                  <dd>
                    {tomeId ? <ItemReference amount={1} itemId={tomeId} /> : '-'}
                    <span className="setting-desc">（5%）</span>
                  </dd>
                </div>
                <div>
                  <dt>{t('app.secretArt.convertOutcomeStone')}</dt>
                  <dd>
                    <ItemReference amount={100} itemId="currency.spirit-stone" />
                    <span className="setting-desc">（95%）</span>
                  </dd>
                </div>
              </>
            )}
          </dl>
        )}
      </div>

      {/* 浮动物品选择器 */}
      <ItemGridPicker
        allowEmpty={false}
        anchor={pickerAnchor}
        items={candidates}
        onClose={() => setPickerAnchor(null)}
        onSelect={(itemId, entry) => {
          setSelectedKey(itemId === null ? null : itemPickerEntryKey(itemId, entry?.variant))
          setQuantity(1)
          setPickerAnchor(null)
        }}
        open={pickerAnchor !== null}
        title={t(`app.secretArt.targetPicker${opCap}`)}
        value={selectedKey}
      />

      {/* 二次确认弹窗 */}
      <ConfirmDialog
        confirmLabel={t('app.secretArt.confirm')}
        danger
        description={t(`app.secretArt.confirm${opCap}`, { quantity: effectiveQuantity })}
        onClose={() => setConfirmOpen(false)}
        onConfirm={submit}
        open={confirmOpen}
        title={t('app.secretArt.confirmTitle')}
      />
    </div>
  )
})
