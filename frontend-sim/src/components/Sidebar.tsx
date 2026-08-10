import { ACTION_TYPE_NAMES, type ActionType } from '../game/ids'
import { useUiStore } from '../uiStore'

const NAV_ITEMS: Array<{ tab: ActionType; glyph: string; hint: string }> = [
  { tab: 'cultivation', glyph: '修', hint: '吐纳灵气，提升修为' },
  { tab: 'exploration', glyph: '探', hint: '外出采集，寻觅灵草' },
  { tab: 'alchemy', glyph: '丹', hint: '开炉炼丹，化草为丹' }
]

// 下左：只放行动切换 tab + 设置入口。
export function Sidebar() {
  const activeTab = useUiStore(state => state.activeTab)
  const setActiveTab = useUiStore(state => state.setActiveTab)

  return (
    <aside className="sidebar panel">
      <nav className="sidebar-nav">
        {NAV_ITEMS.map(item => (
          <button
            key={item.tab}
            type="button"
            className={`nav-item${activeTab === item.tab ? ' active' : ''}`}
            onClick={() => setActiveTab(item.tab)}
          >
            <span className="nav-glyph">{item.glyph}</span>
            <span className="nav-text">
              <span className="nav-label">{ACTION_TYPE_NAMES[item.tab]}</span>
              <span className="nav-hint">{item.hint}</span>
            </span>
          </button>
        ))}
      </nav>

      <button
        type="button"
        className={`nav-item settings-entry${activeTab === 'settings' ? ' active' : ''}`}
        onClick={() => setActiveTab('settings')}
      >
        <span className="nav-glyph">设</span>
        <span className="nav-text">
          <span className="nav-label">设置</span>
          <span className="nav-hint">存档与调试</span>
        </span>
      </button>
    </aside>
  )
}
