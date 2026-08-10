import { BottomNav } from './components/BottomNav'
import { BuffBar } from './components/BuffBar'
import { InventoryPanel } from './components/InventoryPanel'
import { LogPanel } from './components/LogPanel'
import { SettingsPage } from './components/SettingsPage'
import { Sidebar } from './components/Sidebar'
import { SkillPage } from './components/SkillPage'
import { TopBar } from './components/TopBar'
import { ACTION_TYPES } from './game/ids'
import { useUiStore } from './uiStore'

export default function App() {
  const activeTab = useUiStore(state => state.activeTab)

  return (
    <div className="app">
      <TopBar />
      <div className="app-body">
        <Sidebar />
        <div className="center-column">
          <BuffBar />
          <div className="main-content">
            {/* 各行动页保持常驻，只切换可见性，切换 tab 不会打断任何操作状态 */}
            {ACTION_TYPES.map(type => (
              <div key={type} hidden={activeTab !== type}>
                <SkillPage type={type} />
              </div>
            ))}
            <div hidden={activeTab !== 'settings'}>
              <SettingsPage />
            </div>
            {/* 移动端专属 tab；桌面端由右栏/下中底部常驻展示 */}
            <div className="mobile-tab" hidden={activeTab !== 'inventory'}>
              <InventoryPanel />
            </div>
            <div className="mobile-tab" hidden={activeTab !== 'log'}>
              <LogPanel />
            </div>
          </div>
          <div className="desktop-log">
            <LogPanel />
          </div>
        </div>
        <aside className="right-column">
          <InventoryPanel />
        </aside>
      </div>
      <BottomNav />
    </div>
  )
}
