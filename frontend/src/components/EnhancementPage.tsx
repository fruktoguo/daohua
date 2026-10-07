import { memo, useEffect, useMemo, useState } from 'react'
import { getItemAmount } from '../game/actionCalculations'
import {
  ENHANCE_ACTION_ID,
  ENHANCE_MAX_LEVEL,
  SPIRIT_STONE_ITEM_ID,
  TECHNIQUE_SPEED_RATIO_PER_LEVEL,
  calculateTechniqueProgress,
  enhanceBaseDurationSeconds,
  enhanceSpiritStoneCost,
  enhanceSuccessRate,
} from '../game/combatConstants'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findEquipmentInCatalog, findItemInCatalog, parseEnhancementVariant, resolveItemCategoryLabel, resolveItemSubCategory } from '../game/ids'
import { useT } from '../i18n'
import { gameClient } from '../network/gameClient'
import { ChoiceMenu } from './CatalogPicker'
import { ActionDetailItem, ItemReference } from './ItemDisplay'
import { ItemGridPicker, ItemUseSlot, itemPickerEntryKey, type ItemPickerEntry } from './ItemGridPicker'
import { QuantitySelector } from './QuantitySelector'

/**
 * 装备强化候选条目：以装备基础 itemId 归并——同物不同 +N 资产变体行与已装备
 * 槽位按服务端权威口径取「最高强化等级行」作为目标物展示（协议 targetItemId
 * 只携带基础 ID，服务端按目标物自行裁定当前等级）。
 * tool-* 技艺工具槽位装备不可强化（服务端 EnhancementRules 同步拦截），不进候选。
 */
function collectEquipmentCandidates(
  inventory: ReturnType<typeof useGameStore.getState>['inventory'],
  combatEquipment: readonly { itemId: string; enhancementLevel: number }[] | undefined,
  equipmentCatalog: ReturnType<typeof useGameStore.getState>['equipment'],
  itemCatalog: ReturnType<typeof useGameStore.getState>['items'],
  t: ReturnType<typeof useT>,
): ItemPickerEntry[] {
  const rows = new Map<string, { maxLevel: number; total: number }>()
  for (const inv of inventory) {
    const eqDef = findEquipmentInCatalog(equipmentCatalog, inv.itemId)
    if (!eqDef || eqDef.slot?.startsWith('tool-')) continue
    const level = parseEnhancementVariant(inv.variant)
    const row = rows.get(inv.itemId) ?? { maxLevel: 0, total: 0 }
    row.maxLevel = Math.max(row.maxLevel, level)
    row.total += Math.max(0, inv.amount)
    rows.set(inv.itemId, row)
  }
  for (const eq of combatEquipment ?? []) {
    // 已装备的工具槽位同样拦截：工具不进入强化目标归并
    const eqDef = findEquipmentInCatalog(equipmentCatalog, eq.itemId)
    if (eqDef?.slot?.startsWith('tool-')) continue
    const row = rows.get(eq.itemId) ?? { maxLevel: 0, total: 0 }
    row.maxLevel = Math.max(row.maxLevel, Math.floor(eq.enhancementLevel))
    // 已装备行未必以钱包变体行存在，兜底计 1 保证目标可选。
    row.total = Math.max(row.total, 1)
    rows.set(eq.itemId, row)
  }
  const candidates: ItemPickerEntry[] = []
  for (const [itemId, row] of rows) {
    const eqDef = findEquipmentInCatalog(equipmentCatalog, itemId)
    if (!eqDef) continue
    const itemDef = findItemInCatalog(itemCatalog, itemId)
    const baseName = itemDef?.name ?? eqDef.name
    candidates.push({
      itemId,
      name: row.maxLevel > 0 ? `${baseName} +${row.maxLevel}` : baseName,
      amount: row.total,
      tier: itemDef?.tier ?? eqDef.tier,
      category: resolveItemCategoryLabel(resolveItemSubCategory(itemId, itemDef, eqDef), t),
      description: itemDef?.description ?? eqDef.description ?? '',
      definition: itemDef,
      equipmentDefinition: eqDef,
      variant: row.maxLevel > 0 ? `+${row.maxLevel}` : null,
      enhancementLevel: row.maxLevel,
    })
  }
  candidates.sort((a, b) => (b.tier - a.tier) || a.name.localeCompare(b.name))
  return candidates
}

/**
 * 装备强化页面 (EnhancementPage)
 *
 * 目标选择（背包 +N 变体与已装备槽位归并）→ 强化至目标等级 → 护符勾选 →
 * 循环轮次，右侧实时镜像服务端公式预览：费用、成功率、成败产物、技艺经验与
 * 单轮耗时。成功率/耗时仅为前端预览镜像，权威结算以服务端为准。
 */
export const EnhancementPage = memo(function EnhancementPage(): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const items = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const combat = useGameStore(state => state.combat)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [targetLevel, setTargetLevel] = useState(1)
  const [useProtection, setUseProtection] = useState(false)
  const [protectionItemId, setProtectionItemId] = useState<string | null>(null)
  const [repeats, setRepeats] = useState(-1)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)

  const candidates = useMemo(
    () => collectEquipmentCandidates(inventory, combat?.equipment, equipmentCatalog, items, t),
    [combat?.equipment, equipmentCatalog, inventory, items, t],
  )
  const selected = candidates.find(c => itemPickerEntryKey(c.itemId, c.variant) === selectedKey) ?? null

  const currentLevel = Math.floor(selected?.enhancementLevel ?? 0)
  const nextLevel = Math.min(ENHANCE_MAX_LEVEL, currentLevel + 1)
  const maxedOut = selected !== null && currentLevel >= ENHANCE_MAX_LEVEL

  // 目标资产行消失（强化中被消耗/卸下）时清除选择；目标等级随当前等级钳制。
  useEffect(() => {
    if (selectedKey && !selected) setSelectedKey(null)
  }, [selected, selectedKey])
  useEffect(() => {
    // 目标等级始终钳制在 [下一轮目标, 上限] 区间（换目标或当前等级变化时收敛）。
    setTargetLevel(prev => Math.min(ENHANCE_MAX_LEVEL, Math.max(nextLevel, prev)))
  }, [nextLevel, selectedKey])

  const techniqueProgress = calculateTechniqueProgress(combat?.techniqueExperiences?.enhancement ?? 0)
  const techniqueLevel = techniqueProgress.level

  const spiritOwned = getItemAmount(inventory, SPIRIT_STONE_ITEM_ID)

  // 保护材料候选：装备配了 enhanceProtectionItems 白名单则从列表任选，
  // 未配置时回退为同装备无强化基础件（每轮消耗 1 件代护）。
  const protectionOptions = useMemo(() => {
    const configured = selected?.equipmentDefinition?.enhanceProtectionItems
    return configured && configured.length > 0 ? configured : (selected ? [selected.itemId] : [])
  }, [selected])
  useEffect(() => {
    setProtectionItemId(protectionOptions[0] ?? null)
  }, [protectionOptions])
  const protectionOwned = protectionItemId ? getItemAmount(inventory, protectionItemId) : 0
  // 保护材料即目标装备且当前为 +0 时，本体与代护件共用同一资产键，需持有 ≥2。
  const protectionRequired = protectionItemId !== null && protectionItemId === selected?.itemId && currentLevel === 0 ? 2 : 1
  const protectionActive = useProtection && protectionItemId !== null && protectionOwned >= protectionRequired

  const protectionName = (id: string) =>
    findItemInCatalog(items, id)?.name ?? findEquipmentInCatalog(equipmentCatalog, id)?.name ?? id

  // 淬灵砧等已装备强化工具的速度加成：techniqueBonus.technique === 'enhancement' 的
  // speedRatio 与技艺等级加成并入同一除数（服务端 TimedActionDuration 同口径）。
  const toolSpeedRatio = useMemo(() => {
    let ratio = 0
    for (const eq of combat?.equipment ?? []) {
      const bonus = findEquipmentInCatalog(equipmentCatalog, eq.itemId)?.techniqueBonus
      if (bonus && bonus.technique === 'enhancement' && Number.isFinite(bonus.speedRatio) && bonus.speedRatio > 0) {
        ratio += bonus.speedRatio
      }
    }
    return ratio
  }, [combat?.equipment, equipmentCatalog])

  const preview = useMemo(() => {
    const successRate = enhanceSuccessRate(nextLevel, techniqueLevel)
    const spiritCost = enhanceSpiritStoneCost(nextLevel)
    const baseDuration = enhanceBaseDurationSeconds(nextLevel)
    const actualDuration = baseDuration / (1 + TECHNIQUE_SPEED_RATIO_PER_LEVEL * techniqueLevel + toolSpeedRatio)
    return { successRate, spiritCost, baseDuration, actualDuration }
  }, [nextLevel, techniqueLevel, toolSpeedRatio])

  const submit = () => {
    if (!selected || maxedOut) return
    // 强化行动入队：targetItemId 仅携带装备基础 ID，当前等级由服务端按
    // 目标物权威裁定；勾选保护材料有效时经 auxItemId 下发，目标等级走 parameters。
    gameClient.sendCommand('action.enqueue', {
      actionId: ENHANCE_ACTION_ID,
      repeatCount: repeats,
      targetItemId: selected.itemId,
      ...(protectionActive && protectionItemId ? { auxItemId: protectionItemId } : {}),
      parameters: { targetLevel },
    })
  }

  const baseName = selected?.equipmentDefinition?.name ?? selected?.name ?? ''

  return (
    <div className="enhancement-layout">
      <div className="settings-group">
        <h3 className="settings-group-title">{t('app.enhancement.targetSlot')}</h3>
        <ItemUseSlot
          item={selected}
          onClick={event => setPickerAnchor(prev => (prev ? null : event.currentTarget))}
        />
        <div className="setting-row">
          <span className="setting-label">{t('app.enhancement.targetLevel')}</span>
          <QuantitySelector
            ariaLabel={t('app.enhancement.targetLevelAria')}
            disabled={!selected || maxedOut}
            max={ENHANCE_MAX_LEVEL}
            maxShortcut
            min={nextLevel}
            onChange={setTargetLevel}
            value={targetLevel}
          />
        </div>
        <div className="setting-row">
          <span className="setting-text">
            <span className="setting-label">{t('app.enhancement.protectionSlot')}</span>
            <span className="setting-desc">
              {protectionOptions.length > 0 && protectionOwned >= protectionRequired
                ? t('app.enhancement.protectionUse')
                : t('app.enhancement.protectionUnavailable')}
            </span>
          </span>
          <label className="enhancement-charm-toggle">
            <input
              checked={protectionActive}
              disabled={!selected || protectionOptions.length === 0 || protectionOwned < protectionRequired}
              onChange={event => setUseProtection(event.target.checked)}
              type="checkbox"
            />
            {protectionItemId !== null && (
              <ItemReference amount={protectionOwned} itemId={protectionItemId} />
            )}
          </label>
        </div>
        {useProtection && protectionOptions.length > 1 && (
          <div className="setting-row">
            <span className="setting-label">{t('app.enhancement.protectionMaterial')}</span>
            <ChoiceMenu
              label={t('app.enhancement.protectionMaterialAria')}
              onSelect={setProtectionItemId}
              options={protectionOptions.map(id => ({
                disabled: getItemAmount(inventory, id) < 1,
                id,
                label: protectionName(id),
                subtitle: t('app.enhancement.protectionOwned', { n: getItemAmount(inventory, id) }),
              }))}
              value={protectionItemId ?? ''}
            />
          </div>
        )}
        <div className="repeat-row">
          <span>{t('app.enhancement.repeats')}</span>
          <QuantitySelector
            allowInfinity
            ariaLabel={t('app.enhancement.repeatsAria')}
            max={1000}
            onChange={setRepeats}
            presets={[1, 10, 100]}
            value={repeats}
          />
        </div>
        <button
          className="action-modal-submit"
          disabled={!selected || maxedOut}
          onClick={submit}
          type="button"
        >{maxedOut ? t('app.enhancement.successRateMaxed', { max: ENHANCE_MAX_LEVEL }) : t('app.enhancement.submit')}</button>
      </div>

      <div className="settings-group">
        <h3 className="settings-group-title">{t('app.enhancement.previewTitle')}</h3>
        {!selected ? (
          <p className="setting-desc">{t('app.enhancement.previewEmpty')}</p>
        ) : (
          <dl className="action-detail-list">
            <div>
              <dt>{t('app.enhancement.currentLabel')}</dt>
              <dd><strong>{`+${currentLevel}`}</strong></dd>
            </div>
            <div>
              <dt>{t('app.enhancement.attemptLabel')}</dt>
              <dd><strong>{`+${nextLevel}`}</strong></dd>
            </div>
            <div>
              <dt>{t('app.enhancement.costLabel')}</dt>
              <dd>
                <ActionDetailItem amount={preview.spiritCost} current={spiritOwned} itemId={SPIRIT_STONE_ITEM_ID} />
                {protectionActive && protectionItemId !== null && (
                  <ActionDetailItem amount={protectionRequired} current={protectionOwned} itemId={protectionItemId} />
                )}
              </dd>
            </div>
            <div>
              <dt>{t('app.enhancement.successRateLabel')}</dt>
              <dd><strong>{`${formatNumber(preview.successRate * 100, { maxDecimals: 1 })}%`}</strong></dd>
            </div>
            <div>
              <dt>{t('app.enhancement.outcomeLabel')}</dt>
              <dd className="enhancement-outcome">
                <span>{t('app.enhancement.outcomeSuccess', { level: nextLevel, name: baseName })}</span>
                <span>{protectionActive
                  ? t('app.enhancement.outcomeFailProtected', { level: Math.max(0, nextLevel - 1), name: baseName })
                  : t('app.enhancement.outcomeFailReset', { name: baseName })}</span>
              </dd>
            </div>
            <div>
              <dt>{t('app.enhancement.expGainLabel')}</dt>
              <dd><strong>{t('app.enhancement.expPerRound', { n: formatNumber(preview.baseDuration) })}</strong></dd>
            </div>
            <div>
              <dt>{t('app.enhancement.durationLabel')}</dt>
              <dd><strong>{t('app.enhancement.durationSeconds', { seconds: formatNumber(preview.actualDuration, { maxDecimals: 1 }) })}</strong></dd>
            </div>
            <div>
              <dt>{t('app.enhancement.techniqueLabel', { level: techniqueLevel })}</dt>
              <dd><strong>{t('app.enhancement.techniqueBonus', {
                value: formatNumber(techniqueLevel * TECHNIQUE_SPEED_RATIO_PER_LEVEL * 100, { maxDecimals: 0 }),
              })}</strong></dd>
            </div>
          </dl>
        )}
      </div>

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
        title={t('app.enhancement.targetPickerTitle')}
        value={selectedKey}
      />
    </div>
  )
})
