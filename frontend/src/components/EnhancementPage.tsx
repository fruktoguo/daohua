import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { getItemAmount, ROOT_ASPECT_LABEL_KEYS } from '../game/actionCalculations'
import {
  ENHANCE_ACTION_ID,
  ENHANCE_MAX_LEVEL,
  ENHANCE_MIN_PROTECT_LEVEL,
  SPIRIT_STONE_ITEM_ID,
  TECHNIQUE_SPEED_RATIO_PER_LEVEL,
  calculateEnhancementYields,
  calculateTechniqueProgress,
  enhanceBaseDurationSeconds,
  enhanceSpiritStoneCost,
  enhanceSuccessRate,
} from '../game/combatConstants'
import { formatNumber } from '../game/format'
import { useEnhancementStats } from '../game/enhancementRecordStore'
import { useGameStore } from '../game/gameStore'
import { collectOccupiedEquipmentCounts, convertTierToLevel, findEquipmentInCatalog, findItemInCatalog, parseEnhancementVariant, resolveItemCategoryLabel, resolveItemSubCategory } from '../game/ids'
import type { CombatEquipmentDefinition, EquippedItem, InventoryItem, ItemDefinition, QueueEntry } from '../game/types'
import { useT, type TFunction } from '../i18n'
import { gameClient } from '../network/gameClient'
import { ChoiceMenu } from './CatalogPicker'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { ActionDetailItem, ItemReference } from './ItemDisplay'
import { ItemGridPicker, ItemUseSlot, itemPickerEntryKey, type ItemPickerEntry } from './ItemGridPicker'
import { QuantitySelector } from './QuantitySelector'
import { UnitNumber } from './UnitNumber'

/**
 * 装备强化候选条目：仅取背包内未穿戴的装备——按 (itemId, 强化等级) 扣除已穿戴件数，
 * 剩余数量 > 0 的有效变体行分别作为独立候选条目进入选择器（避免不同强化等级被误聚合成单项）。
 * 装备中、挂售中均不可选。包括 tool-* 技艺工具在内的全身装备均可强化。
 */
function collectEquipmentCandidates(
  inventory: readonly InventoryItem[],
  combatEquipment: readonly EquippedItem[] | undefined,
  queueEntries: readonly QueueEntry[] | undefined,
  equipmentCatalog: readonly CombatEquipmentDefinition[],
  itemCatalog: readonly ItemDefinition[],
  t: TFunction,
): ItemPickerEntry[] {
  const occupiedCounts = collectOccupiedEquipmentCounts(combatEquipment, queueEntries)
  const heldCounts = new Map<string, { itemId: string; level: number; amount: number }>()
  for (const inv of inventory) {
    const eqDef = findEquipmentInCatalog(equipmentCatalog, inv.itemId)
    if (!eqDef) continue
    const level = parseEnhancementVariant(inv.variant)
    const key = `${inv.itemId}#${level}`
    const existing = heldCounts.get(key)
    if (existing) {
      existing.amount += inv.amount
    } else {
      heldCounts.set(key, { itemId: inv.itemId, level, amount: inv.amount })
    }
  }

  const candidates: ItemPickerEntry[] = []
  for (const [key, held] of heldCounts) {
    const occupied = occupiedCounts.get(key) ?? 0
    const available = held.amount - occupied
    if (available <= 0) continue

    const eqDef = findEquipmentInCatalog(equipmentCatalog, held.itemId)
    if (!eqDef) continue
    const itemDef = findItemInCatalog(itemCatalog, held.itemId)
    const baseName = itemDef?.name ?? eqDef.name
    candidates.push({
      itemId: held.itemId,
      name: held.level > 0 ? `${baseName} +${held.level}` : baseName,
      amount: available,
      tier: itemDef?.tier ?? eqDef.tier,
      category: resolveItemCategoryLabel(resolveItemSubCategory(held.itemId, itemDef, eqDef), t),
      description: itemDef?.description ?? eqDef.description ?? '',
      definition: itemDef,
      equipmentDefinition: eqDef,
      variant: held.level > 0 ? `+${held.level}` : null,
      enhancementLevel: held.level,
    })
  }
  candidates.sort((a, b) =>
    (b.tier - a.tier) ||
    a.itemId.localeCompare(b.itemId) ||
    ((a.enhancementLevel ?? 0) - (b.enhancementLevel ?? 0)) ||
    a.name.localeCompare(b.name)
  )
  return candidates
}

/**
 * 强化方案术语悬停说明 (TermHint)
 *
 * 将「保护材料」「起护等级」等机制长句收进名词本体上的 Hover 机制卡：
 * 行内只留名词（虚线下划线提示可查），避免说明文本在窄栏折行撑高左栏。
 */
function TermHint({ hint, term }: { hint: string; term: string }): React.JSX.Element {
  return (
    <FloatingPopover
      closeDelay={80}
      content={<FloatingInfoCard description={hint} title={term} />}
      gap={6}
      interactive={false}
      openDelay={80}
      placement="top"
      size="small"
      triggerClassName="enhancement-term-trigger"
    >
      <span className="setting-label enhancement-term">{term}</span>
    </FloatingPopover>
  )
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
  const world = useGameStore(state => state.world)
  const queueEntries = useGameStore(state => state.queue?.entries)
  const stats = useGameStore(state => state.stats)
  const playerId = useGameStore(state => state.playerId)

  // 正在执行或排队中的强化行动（从权威队列获取）
  const activeEnhanceEntry = useMemo(
    () => queueEntries?.find(e => e.actionId === ENHANCE_ACTION_ID || e.type === 'enhancement') ?? null,
    [queueEntries]
  )
  const isEnhancing = activeEnhanceEntry !== null

  // 若当前正在强化，将正在强化中的装备构造成选中条目（即使已从背包扣除）
  const activeTargetItem = useMemo<ItemPickerEntry | null>(() => {
    if (!activeEnhanceEntry?.targetItemId) return null
    const targetId = activeEnhanceEntry.targetItemId
    const targetEq = findEquipmentInCatalog(equipmentCatalog, targetId)
    const targetItem = findItemInCatalog(items, targetId)
    const level = Math.floor(
      activeEnhanceEntry.enhancementCurrentLevel
        ?? (typeof activeEnhanceEntry.parameters?.initialLevel === 'number'
            ? activeEnhanceEntry.parameters.initialLevel
            : 0)
    )
    const variant = level > 0 ? `+${level}` : null
    const baseName = targetEq?.name ?? targetItem?.name ?? targetId
    return {
      itemId: targetId,
      name: level > 0 ? `${baseName} +${level}` : baseName,
      amount: 1,
      tier: targetEq?.tier ?? targetItem?.tier ?? 1,
      category: resolveItemCategoryLabel(resolveItemSubCategory(targetId, targetItem, targetEq), t),
      description: targetItem?.description ?? targetEq?.description ?? '',
      definition: targetItem,
      equipmentDefinition: targetEq,
      variant,
      enhancementLevel: level,
    }
  }, [activeEnhanceEntry, equipmentCatalog, items, t])

  const [userSelectedKey, setUserSelectedKey] = useState<string | null>(null)
  const [userTargetLevel, setUserTargetLevel] = useState(1)
  const [userProtectLevel, setUserProtectLevel] = useState(ENHANCE_MIN_PROTECT_LEVEL)
  const [userUseProtection, setUserUseProtection] = useState(false)
  const [userProtectionItemId, setUserProtectionItemId] = useState<string | null>(null)
  const [userRepeats, setUserRepeats] = useState(-1)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)

  const candidates = useMemo(
    () => collectEquipmentCandidates(inventory, combat?.equipment, queueEntries, equipmentCatalog, items, t),
    [combat?.equipment, equipmentCatalog, inventory, items, queueEntries, t],
  )
  const userSelected = candidates.find(c => itemPickerEntryKey(c.itemId, c.variant) === userSelectedKey) ?? null
  const selected = isEnhancing ? activeTargetItem : userSelected

  const currentLevel = Math.floor(selected?.enhancementLevel ?? 0)
  const nextLevel = Math.min(ENHANCE_MAX_LEVEL, currentLevel + 1)
  const maxedOut = selected !== null && currentLevel >= ENHANCE_MAX_LEVEL

  // 强化进行中时所有设定直读权威任务信息，切换回该界面时完整重现；未强化时读取玩家本地选择状态
  const targetLevel = isEnhancing ? Math.floor(activeEnhanceEntry?.targetLevel ?? nextLevel) : userTargetLevel
  const useProtection = isEnhancing ? Boolean(activeEnhanceEntry?.auxItemId) : userUseProtection
  const protectionItemId = isEnhancing ? (activeEnhanceEntry?.auxItemId ?? null) : userProtectionItemId
  const protectLevel = isEnhancing
    ? Math.floor(activeEnhanceEntry?.parameters?.protectLevel ?? ENHANCE_MIN_PROTECT_LEVEL)
    : userProtectLevel
  const repeats = isEnhancing ? (activeEnhanceEntry?.remainingRepeats ?? -1) : userRepeats

  // 追踪上一次选中的目标，支持强化升降级导致变体 key 变化时平滑迁移到新变体
  const lastSelectedRef = useRef<{ itemId: string; level: number } | null>(null)
  useEffect(() => {
    if (isEnhancing) return
    if (userSelected) {
      lastSelectedRef.current = { itemId: userSelected.itemId, level: currentLevel }
    } else if (userSelectedKey) {
      if (lastSelectedRef.current) {
        const sameItemCandidates = candidates.filter(c => c.itemId === lastSelectedRef.current!.itemId)
        if (sameItemCandidates.length > 0) {
          const nextCandidate = sameItemCandidates.find(c => (c.enhancementLevel ?? 0) >= lastSelectedRef.current!.level)
            ?? sameItemCandidates[0]
          setUserSelectedKey(itemPickerEntryKey(nextCandidate.itemId, nextCandidate.variant))
          return
        }
      }
      setUserSelectedKey(null)
    }
  }, [candidates, currentLevel, isEnhancing, userSelected, userSelectedKey])

  useEffect(() => {
    if (isEnhancing) return
    setUserTargetLevel(prev => Math.min(ENHANCE_MAX_LEVEL, Math.max(nextLevel, prev)))
  }, [isEnhancing, nextLevel, userSelectedKey])

  useEffect(() => {
    if (isEnhancing) return
    setUserProtectLevel(prev => Math.min(Math.max(ENHANCE_MIN_PROTECT_LEVEL, prev), Math.max(ENHANCE_MIN_PROTECT_LEVEL, userTargetLevel)))
  }, [isEnhancing, userTargetLevel])
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
    if (isEnhancing) return
    setUserProtectionItemId(protectionOptions[0] ?? null)
  }, [isEnhancing, protectionOptions])
  // 保护材料仅限背包内未装备件（若为装备则扣除已装备在身上的 0 级件）
  const getAvailableProtectionAmount = useCallback((id: string) => {
    const rawAmount = getItemAmount(inventory, id)
    const eqDef = findEquipmentInCatalog(equipmentCatalog, id)
    if (!eqDef) return rawAmount
    let wornZeroCount = 0
    for (const eq of combat?.equipment ?? []) {
      if (eq.itemId === id && Math.floor(eq.enhancementLevel) === 0) {
        wornZeroCount++
      }
    }
    return Math.max(0, rawAmount - wornZeroCount)
  }, [combat?.equipment, equipmentCatalog, inventory])
  const protectionOwned = protectionItemId ? getAvailableProtectionAmount(protectionItemId) : 0
  // 保护材料即目标装备且当前为 +0 时，本体与代护件共用同一资产键，需持有 ≥2。
  const protectionRequired = protectionItemId !== null && protectionItemId === selected?.itemId && currentLevel === 0 ? 2 : 1
  const isProtectedThisRound = useProtection && protectionItemId !== null && currentLevel >= protectLevel && protectionOwned >= protectionRequired

  const protectionName = (id: string) =>
    findItemInCatalog(items, id)?.name ?? findEquipmentInCatalog(equipmentCatalog, id)?.name ?? id

  // 淬灵砧等已装备强化工具的速度加成：techniqueBonus.technique === 'enhancement' 的
  // speedRatio 与技艺等级加成并入同一除数（服务端 TimedActionDuration 同口径）。
  const toolSpeedRatio = useMemo(() => {
    let ratio = 0
    for (const eq of combat?.equipment ?? []) {
      const bonus = findEquipmentInCatalog(equipmentCatalog, eq.itemId)?.techniqueBonus
      if (bonus && bonus.technique === 'enhancement' && Number.isFinite(bonus.speedRatio) && bonus.speedRatio > 0) {
        const level = eq.enhancementLevel ?? 0
        ratio += bonus.speedRatio * Math.pow(1.05, level)
      }
    }
    return ratio
  }, [combat?.equipment, equipmentCatalog])
  // 金灵根对装备强化速度的加成（100 点 = +25%）
  const linggenSpeedRatio = useMemo(() => {
    if (!stats?.heavenGateEntered || !stats.heavenGateConfirmed) return 0
    return 0.25 * (Math.max(0, Math.min(100, stats.heavenGateConfirmed.metal ?? 0)) / 100)
  }, [stats?.heavenGateEntered, stats?.heavenGateConfirmed])

  const itemTier = selected?.equipmentDefinition?.tier ?? 1
  const itemLevel = convertTierToLevel(itemTier)
  const yields = useMemo(
    () => calculateEnhancementYields(itemLevel, nextLevel),
    [itemLevel, nextLevel],
  )

  const preview = useMemo(() => {
    const successRate = enhanceSuccessRate(nextLevel, techniqueLevel)
    const spiritCost = enhanceSpiritStoneCost(nextLevel)
    const buildingSpeed = world?.buildingEffects?.enhancementSpeedRatio ?? 0
    const baseDuration = enhanceBaseDurationSeconds(nextLevel)
    const durationAfterBuilding = buildingSpeed > 0 ? baseDuration / (1 + buildingSpeed) : baseDuration
    const actualDuration = durationAfterBuilding / (1 + TECHNIQUE_SPEED_RATIO_PER_LEVEL * techniqueLevel + toolSpeedRatio + linggenSpeedRatio)
    return { successRate, spiritCost, baseDuration, actualDuration }
  }, [nextLevel, techniqueLevel, toolSpeedRatio, linggenSpeedRatio, world?.buildingEffects?.enhancementSpeedRatio])

  const enhancementStats = useEnhancementStats(playerId, selected?.itemId)
  const historyRows = useMemo(() => {
    if (!selected?.itemId) return []
    const rows: Array<{ level: number; attempts: number; successes: number; successRate: number }> = []
    for (let lvl = ENHANCE_MAX_LEVEL; lvl >= 1; lvl--) {
      const stat = enhancementStats[lvl]
      if (stat && stat.attempts > 0) {
        rows.push({
          level: lvl,
          attempts: stat.attempts,
          successes: stat.successes,
          successRate: stat.successRate,
        })
      }
    }
    return rows
  }, [enhancementStats, selected?.itemId])

  const submit = () => {
    if (!selected || maxedOut) return
    // 强化行动入队：targetItemId 携带装备基础 ID，parameters 附带目标等级与起始等级；
    // 勾选保护材料有效时经 auxItemId 下发。
    gameClient.sendCommand('action.enqueue', {
      actionId: ENHANCE_ACTION_ID,
      repeatCount: repeats,
      targetItemId: selected.itemId,
      ...(useProtection && protectionItemId ? { auxItemId: protectionItemId } : {}),
      parameters: {
        targetLevel,
        initialLevel: currentLevel,
        protectLevel: useProtection ? protectLevel : 0,
      },
    })
  }
  const stopEnhancement = useCallback(() => {
    if (!activeEnhanceEntry) return
    gameClient.sendCommand('action.remove', { actionInstanceId: activeEnhanceEntry.entryId })
  }, [activeEnhanceEntry])


  return (
    <div className="enhancement-layout">
      <div className="settings-group">
        <h3 className="settings-group-title">
          <span>{t('app.enhancement.targetSlot')}</span>
          {isEnhancing && (
            <span className="enhancement-active-badge">
              {t('app.enhancement.enhancing')}
            </span>
          )}
        </h3>
        <div className="enhancement-target">
          <ItemUseSlot
            item={selected}
            onClick={event => {
              if (!isEnhancing) setPickerAnchor(prev => (prev ? null : event.currentTarget))
            }}
          />
          <div className="enhancement-target-config">
            <span className="setting-label">{t('app.enhancement.targetLevel')}</span>
            <QuantitySelector
              ariaLabel={t('app.enhancement.targetLevelAria')}
              disabled={isEnhancing || !selected || maxedOut}
              max={ENHANCE_MAX_LEVEL}
              maxShortcut
              min={nextLevel}
              onChange={setUserTargetLevel}
              value={targetLevel}
            />
          </div>
        </div>
        <div className="setting-row">
          <TermHint
            hint={t('app.enhancement.protectionHint')}
            term={t('app.enhancement.protectionSlot')}
          />
          <label className="enhancement-charm-toggle">
            <input
              checked={useProtection}
              disabled={isEnhancing || !selected || protectionOptions.length === 0 || protectionOwned < 1}
              onChange={event => setUserUseProtection(event.target.checked)}
              type="checkbox"
            />
            {protectionItemId !== null
              ? <ItemReference amount={protectionOwned} itemId={protectionItemId} />
              : protectionOptions.length === 0 && (
                <span className="enhancement-empty-note">{t('app.enhancement.protectionUnavailable')}</span>
              )}
          </label>
        </div>
        {useProtection && (
          <div className="setting-row">
            <TermHint
              hint={t('app.enhancement.protectLevelHint')}
              term={t('app.enhancement.protectLevel')}
            />
            <QuantitySelector
              ariaLabel={t('app.enhancement.protectLevelAria')}
              disabled={isEnhancing || !useProtection || targetLevel <= ENHANCE_MIN_PROTECT_LEVEL}
              max={Math.max(ENHANCE_MIN_PROTECT_LEVEL, targetLevel)}
              min={ENHANCE_MIN_PROTECT_LEVEL}
              onChange={setUserProtectLevel}
              value={protectLevel}
            />
          </div>
        )}
        {useProtection && protectionOptions.length > 1 && (
          <div className="setting-row">
            <span className="setting-label">{t('app.enhancement.protectionMaterial')}</span>
            <ChoiceMenu
              label={t('app.enhancement.protectionMaterialAria')}
              onSelect={id => {
                if (!isEnhancing) setUserProtectionItemId(id)
              }}
              options={protectionOptions.map(id => ({
                disabled: isEnhancing || getAvailableProtectionAmount(id) < 1,
                id,
                label: protectionName(id),
                subtitle: t('app.enhancement.protectionOwned', { n: getAvailableProtectionAmount(id) }),
              }))}
              value={protectionItemId ?? ''}
            />
          </div>
        )}
        <div className="setting-row">
          <span className="setting-label">{t('app.enhancement.repeats')}</span>
          <QuantitySelector
            allowInfinity
            ariaLabel={t('app.enhancement.repeatsAria')}
            disabled={isEnhancing || !selected || maxedOut}
            max={1000}
            onChange={setUserRepeats}
            presets={[1, 10, 100]}
            value={repeats}
          />
        </div>
        {isEnhancing ? (
          <button
            className="action-modal-submit action-modal-submit--danger"
            onClick={stopEnhancement}
            type="button"
          >
            {t('app.enhancement.stop')}
          </button>
        ) : (
          <button
            className="action-modal-submit"
            disabled={!selected || maxedOut}
            onClick={submit}
            type="button"
          >
            {maxedOut ? t('app.enhancement.successRateMaxed', { max: ENHANCE_MAX_LEVEL }) : t('app.enhancement.submit')}
          </button>
        )}
      </div>

      <div className="settings-group">
        <h3 className="settings-group-title">{t('app.enhancement.previewTitle')}</h3>
        {!selected ? (
          <p className="setting-desc">{t('app.enhancement.previewEmpty')}</p>
        ) : (
          <dl className="action-detail-list">
            <div>
              <dt>{t('app.enhancement.levelFlow')}</dt>
              <dd className="enhancement-level-flow">
                <strong>{`+${currentLevel}`}</strong>
                <span aria-hidden="true" className="enhancement-level-arrow">→</span>
                <strong className="enhancement-level-next">{`+${nextLevel}`}</strong>
              </dd>
            </div>
            <div>
              <dt>{t('app.enhancement.successRateLabel')}</dt>
              <dd><strong className="enhancement-rate">{`${formatNumber(preview.successRate * 100, { maxDecimals: 1 })}%`}</strong></dd>
            </div>
            <div>
              <dt>{t('app.enhancement.costLabel')}</dt>
              <dd>
                <ActionDetailItem amount={preview.spiritCost} current={spiritOwned} itemId={SPIRIT_STONE_ITEM_ID} />
                {isProtectedThisRound && protectionItemId !== null && (
                  <ActionDetailItem amount={protectionRequired} current={protectionOwned} itemId={protectionItemId} />
                )}
              </dd>
            </div>
            <div>
              <dt>{t('app.enhancement.outcomeLabel')}</dt>
              <dd className="enhancement-outcome">
                <span>{t('app.enhancement.outcomeSuccess', { level: nextLevel })}</span>
                <span>{isProtectedThisRound
                  ? t('app.enhancement.outcomeFailProtected', { level: Math.max(0, currentLevel - 1) })
                  : t('app.enhancement.outcomeFailReset')}</span>
              </dd>
            </div>
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
            <div>
              <dt>{t('app.enhancement.durationLabel')}</dt>
              <dd className="enhancement-duration">
                <strong>{t('app.enhancement.durationSeconds', { seconds: formatNumber(preview.actualDuration, { maxDecimals: 1 }) })}</strong>
                {techniqueLevel > 0 && (
                  <span className="enhancement-duration-note">{t('app.enhancement.techniqueInline', {
                    level: techniqueLevel,
                    value: formatNumber(techniqueLevel * TECHNIQUE_SPEED_RATIO_PER_LEVEL * 100, { maxDecimals: 0 }),
                  })}</span>
                )}
              </dd>
            </div>
          </dl>
        )}
      </div>

      <div className="settings-group enhancement-history-group">
        <h3 className="settings-group-title">{t('app.enhancement.historyTitle')}</h3>
        {!selected ? (
          <p className="setting-desc">{t('app.enhancement.previewEmpty')}</p>
        ) : historyRows.length > 0 ? (
          <div className="enhancement-history-list">
            {historyRows.map(row => (
              <div className="enhancement-history-row" key={row.level}>
                <span className="enhancement-history-level">+{row.level}</span>
                <span className="enhancement-history-counts">
                  <span className="enhancement-history-success">
                    {t('app.enhancement.historyCountSuccess', { n: formatNumber(row.successes) })}
                  </span>
                  <span className="enhancement-history-divider">/</span>
                  <span className="enhancement-history-total">
                    {t('app.enhancement.historyCountTotal', { n: formatNumber(row.attempts) })}
                  </span>
                </span>
                <span className="enhancement-history-rate">
                  {t('app.enhancement.historyRate', {
                    rate: formatNumber(row.successRate * 100, { maxDecimals: 1 }),
                  })}
                </span>
              </div>
            ))}
          </div>
        ) : (
          <p className="setting-desc">{t('app.enhancement.historyEmpty')}</p>
        )}
      </div>

      <ItemGridPicker
        allowEmpty={false}
        anchor={pickerAnchor}
        items={candidates}
        onClose={() => setPickerAnchor(null)}
        onSelect={(itemId, entry) => {
          setUserSelectedKey(itemId === null ? null : itemPickerEntryKey(itemId, entry?.variant))
          setPickerAnchor(null)
        }}
        open={pickerAnchor !== null}
        title={t('app.enhancement.targetPickerTitle')}
        value={userSelectedKey}
      />
    </div>
  )
})
