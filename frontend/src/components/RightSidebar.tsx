import { memo, useMemo } from 'react'
import { useGameStore } from '../game/gameStore'
import { INVENTORY_GROUP_ORDER, collectOccupiedEquipmentCounts, getEquipmentMap, getItemMap, parseEnhancementVariant, resolveInventoryGroup, type InventoryGroupKey } from '../game/ids'
import type { InventoryItem } from '../game/types'
import { useUiStore, type RightColumnTab } from '../uiStore'
import { useT } from '../i18n'
import { InventoryItemCell } from './InventoryItemCell'
import { EquipmentPanel } from './EquipmentPanel'
import { AbilitiesPanel } from './AbilitiesPanel'

/**
 * 储物袋（背包）物品网格展示面板：
 * 物品按「重要 / 消耗品 / 装备法宝 / 功法 / 材料」五组小标题分段陈列（非顶部 Tab），
 * 空组不渲染；组内保持服务端资产顺序（asset_id 字典序）。
 */
export const InventoryPanel = memo(function InventoryPanel({ className = '' }: { className?: string }): React.JSX.Element {
  const t = useT()
  const inventory = useGameStore(state => state.inventory)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const equippedList = useGameStore(state => state.combat?.equipment)
  const queueEntries = useGameStore(state => state.queue?.entries)

  const groups = useMemo(() => {
    // 已穿戴与当前强化锁定的装备从背包中扣减：按 (itemId, 强化等级) 精确扣减占用件数，
    // 背包只陈列「钱包持存 − 同级已占用」仍有剩余的变体行；同物不同 +N 行互不串位。
    const occupiedCounts = collectOccupiedEquipmentCounts(equippedList, queueEntries)
    const buckets = new Map<InventoryGroupKey, InventoryItem[]>()
    for (const item of inventory) {
      if (item.amount <= 0) continue
      const occupied = occupiedCounts.get(`${item.itemId}#${parseEnhancementVariant(item.variant)}`) ?? 0
      if (item.amount - occupied <= 0) continue
      const key = resolveInventoryGroup(item.itemId, items, equipment)
      const bucket = buckets.get(key)
      if (bucket) bucket.push(item)
      else buckets.set(key, [item])
    }
    const itemMap = getItemMap(items)
    const eqMap = getEquipmentMap(equipment)
    const resolveItemTier = (inv: InventoryItem): number => {
      const eqDef = eqMap.get(inv.itemId)
      if (eqDef?.tier != null) return eqDef.tier
      const itemDef = itemMap.get(inv.itemId)
      if (itemDef?.tier != null) return itemDef.tier
      return inv.tier ?? 1
    }

    return INVENTORY_GROUP_ORDER
      .map(key => {
        const list = buckets.get(key) ?? []
        list.sort((left, right) => {
          const tierDiff = resolveItemTier(right) - resolveItemTier(left)
          if (tierDiff !== 0) return tierDiff
          const enhanceDiff = parseEnhancementVariant(right.variant) - parseEnhancementVariant(left.variant)
          if (enhanceDiff !== 0) return enhanceDiff
          return left.name.localeCompare(right.name, 'zh-CN') || left.itemId.localeCompare(right.itemId)
        })
        return { key, list }
      })
      .filter(group => group.list.length > 0)
  }, [inventory, items, equipment, equippedList, queueEntries])
  return (
    <div className={`inventory-view-panel ${className}`.trim()}>
      {groups.map(group => (
        <section className="inventory-group" key={group.key}>
          <div className="inventory-group-header">
            <span className="inventory-group-title">{t(`app.inventoryPanel.group.${group.key}`)}</span>
            <span className="inventory-group-count">{group.list.length}</span>
          </div>
          <div className="inventory-grid">
            {group.list.map(item => (
              <InventoryItemCell item={item} key={`${item.itemId}#${item.variant ?? ''}`} />
            ))}
          </div>
        </section>
      ))}
    </div>
  )
})

/** 侧栏 Tab 配置：id 对应 uiStore 的 RightColumnTab，labelKey 拼入 app.rightTab.* 词条 */
interface TabConfig {
  id: RightColumnTab
  labelKey: 'inventory' | 'equipment' | 'abilities'
}

/** 侧栏固定三 Tab：储物袋 / 装备 / 功法 */
const TABS: readonly TabConfig[] = [
  { id: 'inventory', labelKey: 'inventory' },
  { id: 'equipment', labelKey: 'equipment' },
  { id: 'abilities', labelKey: 'abilities' },
]

/**
 * 客户端右侧主栏多 Tab 容器组件 (RightSidebar)
 *
 * 聚合三大核心修士系统：
 * 1. 【储物袋】：行囊材料、丹药、装备与道具网格；
 * 2. 【装备】：本命虚影与六部位装备阵列及技艺工具预留；
 * 3. 【功法】：斗法神通装配与已学功法秘籍目录。
 */
export const RightSidebar = memo(function RightSidebar({ className = '' }: { className?: string }): React.JSX.Element {
  const t = useT()
  const rightColumnTab = useUiStore(state => state.rightColumnTab)
  const setRightColumnTab = useUiStore(state => state.setRightColumnTab)

  return (
    <section className={`panel right-sidebar-panel ${className}`.trim()}>
      <header className="right-sidebar-header">
        <nav aria-label={t('app.rightTab.tabAria')} className="right-sidebar-nav">
          {TABS.map(tab => {
            const isActive = rightColumnTab === tab.id
            return (
              <button
                className={`right-sidebar-tab-btn ${isActive ? 'is-active' : ''}`}
                key={tab.id}
                onClick={() => setRightColumnTab(tab.id)}
                type="button"
              >
                <span className="right-sidebar-tab-label">{t(`app.rightTab.${tab.labelKey}`)}</span>
              </button>
            )
          })}
        </nav>
      </header>

      <div className="right-sidebar-body">
        {rightColumnTab === 'inventory' && <InventoryPanel />}
        {rightColumnTab === 'equipment' && <EquipmentPanel />}
        {rightColumnTab === 'abilities' && <AbilitiesPanel />}
      </div>
    </section>
  )
})
