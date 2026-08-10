import { ITEM_METAS } from '../game/catalog'
import { sim, useSimStore } from '../game/store'

// 背包只放材料/丹药/资源；货币（灵石）在顶栏钱包展示。
const visibleItems = Object.values(ITEM_METAS).filter(meta => meta.category !== 'currency')

export function InventoryPanel() {
  useSimStore(state => state.inventory)
  const inventory = sim.state.inventory

  return (
    <section className="panel inventory-panel">
      <header className="panel-header">
        <h2>背包</h2>
      </header>
      <div className="inventory-grid">
        {visibleItems.map(meta => {
          const amount = inventory[meta.id] ?? 0
          return (
            <div className="inventory-cell" key={meta.id} title={meta.description}>
              <span className={`item-glyph tone-${meta.tone}`}>{meta.glyph}</span>
              <span className="inventory-name">{meta.name}</span>
              <span className="inventory-amount">{amount.toLocaleString()}</span>
            </div>
          )
        })}
      </div>
    </section>
  )
}
