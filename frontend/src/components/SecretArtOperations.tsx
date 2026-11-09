import { memo, useMemo, useState } from 'react'
import { ROOT_ASPECT_LABEL_KEYS } from '../game/actionCalculations'
import {
  calculateSecretArtYields,
  CONVERT_ACTION_ID,
  CONVERT_BASE_DURATION,
  DISASSEMBLE_ACTION_ID,
  DISASSEMBLE_BASE_DURATION,
  LIQUEFY_ACTION_ID,
  LIQUEFY_BASE_DURATION,
  TECHNIQUE_SPEED_RATIO_PER_LEVEL,
} from '../game/combatConstants'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import {
  collectOccupiedEquipmentCounts,
  convertTierToLevel,
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
import { MAX_ACTION_REPEATS } from '../network/protocol'
import { ItemReference } from './ItemDisplay'
import { ItemGridPicker, ItemUseSlot, itemPickerEntryKey, type ItemPickerEntry } from './ItemGridPicker'
import { QuantitySelector } from './QuantitySelector'
import { UnitNumber } from './UnitNumber'

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
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)
  const queueEntries = useGameStore(state => state.queue?.entries)
  const actions = useGameStore(state => state.actions)
  const world = useGameStore(state => state.world)
  const stats = useGameStore(state => state.stats)

  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [repeats, setRepeats] = useState<number>(1)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)

  const currentActionId = operation === 'liquefy'
    ? LIQUEFY_ACTION_ID
    : operation === 'disassemble'
      ? DISASSEMBLE_ACTION_ID
      : CONVERT_ACTION_ID

  const baseDuration = operation === 'liquefy'
    ? LIQUEFY_BASE_DURATION
    : operation === 'disassemble'
      ? DISASSEMBLE_BASE_DURATION
      : CONVERT_BASE_DURATION

  const activeEntry = useMemo(
    () => queueEntries?.find(e => e.actionId === currentActionId) ?? null,
    [queueEntries, currentActionId],
  )
  const isActive = activeEntry !== null

  const occupied = useMemo(() => collectOccupiedEquipmentCounts(combatEquipment, queueEntries), [combatEquipment, queueEntries])
  const candidates = useMemo(
    () => collectSecretArtCandidates(operation, inventory, items, equipment, occupied, actions, t),
    [actions, equipment, inventory, items, occupied, operation, t],
  )

  const selected = useMemo(() => {
    if (selectedKey) {
      const found = candidates.find(item => itemPickerEntryKey(item.itemId, item.variant) === selectedKey)
      if (found) return found
    }
    if (activeEntry?.targetItemId) {
      const found = candidates.find(item => item.itemId === activeEntry.targetItemId)
      if (found) return found
    }
    return null
  }, [activeEntry?.targetItemId, candidates, selectedKey])

  // 秘法技艺等级与加速（统称秘法速度）
  const techniqueLevel = Math.max(0, Math.floor((techniqueExperiences?.['enhancement'] ?? 0) / 100))
  const toolSpeedRatio = useMemo(() => {
    let ratio = 0
    for (const eq of combatEquipment ?? []) {
      const bonus = findEquipmentInCatalog(equipment, eq.itemId)?.techniqueBonus
      if (bonus && bonus.technique === 'enhancement' && Number.isFinite(bonus.speedRatio) && bonus.speedRatio > 0) {
        const level = eq.enhancementLevel ?? 0
        ratio += bonus.speedRatio * Math.pow(1.05, level)
      }
    }
    return ratio
  }, [combatEquipment, equipment])

  const linggenSpeedRatio = useMemo(() => {
    if (!stats?.heavenGateEntered || !stats.heavenGateConfirmed) return 0
    return 0.25 * (Math.max(0, Math.min(100, stats.heavenGateConfirmed.metal ?? 0)) / 100)
  }, [stats?.heavenGateConfirmed, stats?.heavenGateEntered])

  const buildingSpeed = world?.buildingEffects?.enhancementSpeedRatio ?? 0
  const durationAfterBuilding = buildingSpeed > 0 ? baseDuration / (1 + buildingSpeed) : baseDuration
  const actualDuration = durationAfterBuilding / (1 + TECHNIQUE_SPEED_RATIO_PER_LEVEL * techniqueLevel + toolSpeedRatio + linggenSpeedRatio)

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

  const itemTier = selected?.tier ?? 1
  const itemLevel = convertTierToLevel(itemTier)
  const yields = useMemo(
    () => calculateSecretArtYields(itemLevel, baseDuration),
    [itemLevel, baseDuration],
  )

  const queueLength = queueEntries?.length ?? 0

  const submitAction = (startImmediately: boolean) => {
    if (!selected) return
    gameClient.sendCommand('action.enqueue', {
      actionId: currentActionId,
      repeatCount: repeats,
      targetItemId: selected.itemId,
      priority: startImmediately,
    })
  }

  const stop = () => {
    if (!activeEntry) return
    gameClient.sendCommand('action.remove', {
      actionInstanceId: activeEntry.entryId,
    })
  }

  const opCap = operation[0].toUpperCase() + operation.slice(1)

  return (
    <div className="enhancement-layout">
      {/* 左侧：目标槽位与重复次数配置 */}
      <div className="settings-group">
        <h3 className="settings-group-title">
          <span>{t(`app.secretArt.targetSlot${opCap}`)}</span>
          {isActive && (
            <span className="enhancement-active-badge">
              <span className="enhancement-active-dot" />
              {t('app.secretArt.active')}
            </span>
          )}
        </h3>
        <div className="enhancement-target">
          <ItemUseSlot
            item={selected}
            onClick={event => {
              if (!isActive) setPickerAnchor(prev => (prev ? null : event.currentTarget))
            }}
          />
          <div className="enhancement-target-config">
            <span className="setting-label">{t('app.enhancement.repeats')}</span>
            <QuantitySelector
              allowInfinity
              ariaLabel={t('app.enhancement.repeatsAria')}
              disabled={isActive || !selected}
              max={MAX_ACTION_REPEATS}
              min={1}
              onChange={setRepeats}
              presets={[1, 10, 100]}
              value={repeats}
            />
          </div>
        </div>

        <div className="enhancement-action-row">
          {isActive ? (
            <button
              className="action-modal-submit action-modal-submit--danger"
              onClick={stop}
              type="button"
            >
              {t(`app.secretArt.stop${opCap}`)}
            </button>
          ) : queueLength > 0 ? (
            <div className="action-modal-button-group">
              <button
                className="action-modal-submit action-modal-submit--secondary"
                disabled={!selected}
                onClick={() => submitAction(true)}
                type="button"
              >
                {t(`app.secretArt.start${opCap}`)}
              </button>
              <button
                className="action-modal-submit"
                disabled={!selected}
                onClick={() => submitAction(false)}
                type="button"
              >
                {t(`app.secretArt.enqueue${opCap}`)}
              </button>
            </div>
          ) : (
            <button
              className="action-modal-submit"
              disabled={!selected}
              onClick={() => submitAction(true)}
              type="button"
            >
              {t(`app.secretArt.start${opCap}`)}
            </button>
          )}
        </div>
      </div>

      {/* 右侧：规范四段式固定预览（消耗 / 产出 / 感悟 / 单次耗时） */}
      <div className="settings-group">
        <h3 className="settings-group-title">{t('app.secretArt.previewTitle')}</h3>
        <p className="setting-desc">{t(`app.secretArt.${operation}Desc`)}</p>

        {!selected ? (
          <p className="setting-desc">{t(`app.secretArt.previewEmpty${opCap}`)}</p>
        ) : (
          <dl className="action-detail-list">
            {/* 1. 消耗 */}
            <div>
              <dt>{t('app.secretArt.previewSectionCost')}</dt>
              <dd>
                <ItemReference amount={1} itemId={selected.itemId} />
              </dd>
            </div>

            {/* 2. 产出 */}
            <div>
              <dt>{t('app.secretArt.previewSectionYield')}</dt>
              <dd>
                {operation === 'liquefy' && (
                  <ItemReference amount={liquefyUnitPrice} itemId="currency.spirit-stone" />
                )}
                {operation === 'disassemble' && (
                  disassemblyReturns.length > 0 ? (
                    <div className="secret-art-returns-list">
                      {disassemblyReturns.map(mat => (
                        <ItemReference key={mat.itemId} amount={mat.amount} itemId={mat.itemId} />
                      ))}
                    </div>
                  ) : (
                    <span className="setting-desc">{t('app.secretArt.noReturns')}</span>
                  )
                )}
                {operation === 'convert' && (
                  <div className="secret-art-convert-yields">
                    <div className="convert-yield-row">
                      <ItemReference amount={1} itemId={tomeId ?? 'item.tome.unknown'} />
                      <span className="yield-chance-tag yield-chance-tag--rare">5%</span>
                    </div>
                    <div className="convert-yield-row">
                      <ItemReference amount={100} itemId="currency.spirit-stone" />
                      <span className="yield-chance-tag">95%</span>
                    </div>
                  </div>
                )}
              </dd>
            </div>

            {/* 3. 感悟 */}
            <div>
              <dt>{t('app.action.insights')}</dt>
              <dd>
                <span className="action-stat-pill action-stat-pill--rootexp">
                  <span className="action-stat-pill-label">{t(ROOT_ASPECT_LABEL_KEYS.consciousness ?? 'combat.attr.divineSense')}</span>
                  <strong className="action-stat-pill-value">
                    <UnitNumber disableTooltip maxDecimals={1} prefix="×" value={yields.consciousnessExp} />
                  </strong>
                </span>
                <span className="action-stat-pill action-stat-pill--technique">
                  <span className="action-stat-pill-label">{t('insight.technique.enhancement')}</span>
                  <strong className="action-stat-pill-value">
                    <UnitNumber disableTooltip maxDecimals={1} prefix="×" value={yields.techniqueExp} />
                  </strong>
                </span>
              </dd>
            </div>

            {/* 4. 单次耗时 */}
            <div>
              <dt>{t('app.secretArt.previewSectionDuration')}</dt>
              <dd className="enhancement-duration">
                <strong>{t('app.enhancement.durationSeconds', { seconds: formatNumber(actualDuration, { maxDecimals: 1 }) })}</strong>
                {techniqueLevel > 0 && (
                  <span className="enhancement-duration-note">
                    {t('app.secretArt.speedNote', {
                      level: techniqueLevel,
                      percent: formatNumber(techniqueLevel * TECHNIQUE_SPEED_RATIO_PER_LEVEL * 100, { maxDecimals: 0 }),
                    })}
                  </span>
                )}
              </dd>
            </div>
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
          setPickerAnchor(null)
        }}
        open={pickerAnchor !== null}
        title={t(`app.secretArt.targetPicker${opCap}`)}
        value={selected ? itemPickerEntryKey(selected.itemId, selected.variant) : null}
      />
    </div>
  )
})
