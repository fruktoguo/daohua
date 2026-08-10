import { sim, useSimStore } from '../game/store'
import { QueueCenter } from './QueueCenter'

const QI_PER_LAYER = 100

// 顶栏：左玩家信息 / 中当前任务队列 / 右钱包。
export function TopBar() {
  useSimStore(state => state.stats)
  const { name, stats } = sim.state
  const layer = 1 + Math.floor(stats.qi / QI_PER_LAYER)
  const intoLayer = stats.qi % QI_PER_LAYER

  return (
    <header className="topbar">
      <div className="player-block">
        <div className="player-avatar">修</div>
        <div className="player-info">
          <div className="player-line">
            <strong className="player-name">{name}</strong>
            <span className="realm-badge">炼气 {layer} 层</span>
          </div>
          <div className="realm-progress" title={`修为 ${stats.qi.toLocaleString()}`}>
            <i style={{ width: `${intoLayer}%` }} />
            <span>
              修为 {intoLayer}/{QI_PER_LAYER}
            </span>
          </div>
        </div>
      </div>

      <QueueCenter />

      <div className="wallet-block" title="灵石">
        <span className="item-glyph tone-violet">灵</span>
        <div className="wallet-text">
          <span className="wallet-label">灵石</span>
          <strong className="wallet-amount">{stats.spirit.toLocaleString()}</strong>
        </div>
      </div>
    </header>
  )
}
