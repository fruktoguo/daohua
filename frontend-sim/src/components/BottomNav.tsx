import { ACTION_TYPE_NAMES } from '../game/ids'
import { useUiStore, type TabId } from '../uiStore'

const BOTTOM_TABS: Array<{ tab: TabId; glyph: string }> = [
  { tab: 'cultivation', glyph: '修' },
  { tab: 'exploration', glyph: '探' },
  { tab: 'alchemy', glyph: '丹' },
  { tab: 'inventory', glyph: '包' },
  { tab: 'log', glyph: '志' },
  { tab: 'settings', glyph: '设' }
]

const TAB_LABELS: Record<TabId, string> = {
  ...ACTION_TYPE_NAMES,
  inventory: '背包',
  log: '日志',
  settings: '设置'
}

// 移动端底部导航；桌面端用 CSS 隐藏。
export function BottomNav() {
  const activeTab = useUiStore(state => state.activeTab)
  const setActiveTab = useUiStore(state => state.setActiveTab)

  return (
    <nav className="bottom-nav">
      {BOTTOM_TABS.map(item => (
        <button
          key={item.tab}
          type="button"
          className={activeTab === item.tab ? 'active' : ''}
          onClick={() => setActiveTab(item.tab)}
        >
          <span className="bottom-glyph">{item.glyph}</span>
          <span>{TAB_LABELS[item.tab]}</span>
        </button>
      ))}
    </nav>
  )
}
