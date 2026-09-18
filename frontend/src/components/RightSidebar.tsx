import { memo } from 'react'
import { useGameStore } from '../game/gameStore'
import { useUiStore, type RightColumnTab } from '../uiStore'
import { useT } from '../i18n'
import { ItemCard } from './ItemDisplay'
import { EquipmentPanel } from './EquipmentPanel'
import { AbilitiesPanel } from './AbilitiesPanel'

/**
 * 储物袋（背包）物品网格展示面板
 */
export const InventoryPanel = memo(function InventoryPanel({ className = '' }: { className?: string }): React.JSX.Element {
  const inventory = useGameStore(state => state.inventory)
  const itemCatalog = useGameStore(state => state.items)
  const visibleItems = inventory.filter(item => item.amount > 0)

  return (
    <div className={`inventory-view-panel ${className}`.trim()}>
      <div className="inventory-grid">
        {visibleItems.map(item => (
          <ItemCard
            definition={itemCatalog.find(definition => definition.itemId === item.itemId)}
            item={item}
            key={item.itemId}
          />
        ))}
      </div>
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
