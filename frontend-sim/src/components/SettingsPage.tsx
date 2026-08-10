import { debugResetSave, debugSimulateOfflineGap, sim, useSimStore } from '../game/store'

export function SettingsPage() {
  useSimStore(state => state.stats)
  const state = sim.state

  return (
    <section className="panel settings-page">
      <header className="panel-header">
        <h2>设置</h2>
      </header>

      <div className="settings-block">
        <h3>存档</h3>
        <div className="stat-row">
          <span>玩家</span>
          <span>
            {state.name}（{state.playerId.slice(0, 8)}）
          </span>
        </div>
        <div className="stat-row">
          <span>状态版本</span>
          <span>{state.version.toLocaleString()}</span>
        </div>
        <div className="stat-row">
          <span>上次结算</span>
          <span>{new Date(state.lastSimulatedAt * 1000).toLocaleString('zh-CN')}</span>
        </div>
        <p className="panel-desc">
          进度自动保存在浏览器本地；关闭页面后重新打开，会自动补全离线期间的行动队列进度。
        </p>
      </div>

      <div className="settings-block">
        <h3>调试</h3>
        <div className="settings-actions">
          <button type="button" onClick={() => debugSimulateOfflineGap(3600)}>
            模拟停机 1 小时补全
          </button>
          <button type="button" className="danger" onClick={debugResetSave}>
            重置存档
          </button>
        </div>
      </div>
    </section>
  )
}
