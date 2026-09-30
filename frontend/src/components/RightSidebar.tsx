import { memo, useMemo } from 'react'
import { useGameStore } from '../game/gameStore'
import { INVENTORY_GROUP_ORDER, resolveInventoryGroup, type InventoryGroupKey } from '../game/ids'
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

  const groups = useMemo(() => {
    // 已穿戴装备留在装备面板管理：背包不再陈列该 itemId（资产仍计钱包持存，卸下后回到背包）。
    const equippedIds = new Set((equippedList ?? []).map(entry => entry.itemId))
    const buckets = new Map<InventoryGroupKey, InventoryItem[]>()
    for (const item of inventory) {
      if (item.amount <= 0 || equippedIds.has(item.itemId)) continue
      const key = resolveInventoryGroup(item.itemId, items, equipment)
      const bucket = buckets.get(key)
      if (bucket) bucket.push(item)
      else buckets.set(key, [item])
    }
    return INVENTORY_GROUP_ORDER
      .map(key => ({ key, list: buckets.get(key) ?? [] }))
      .filter(group => group.list.length > 0)
  }, [inventory, items, equipment, equippedList])

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
