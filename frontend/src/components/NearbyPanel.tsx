import { memo } from 'react'
import { useGameStore } from '../game/gameStore'

export const NearbyPanel = memo(function NearbyPanel(): React.JSX.Element {
  const playerId = useGameStore(state => state.playerId)
  const name = useGameStore(state => state.name)
  const world = useGameStore(state => state.world)

  return (
    <div className="nearby-layout">
      <header className="nearby-heading">
        <span>当前位置</span>
        <h3>{world.locationName}</h3>
      </header>
      <section className="nearby-section">
        <h4>附近玩家</h4>
        <div className="nearby-grid">
          <article className="nearby-card is-player">
            <span className="nearby-mark">修</span>
            <div><strong>{name}</strong><small>{playerId.slice(0, 8)} · 自己</small></div>
          </article>
          <p className="nearby-empty">当前分片暂无其他可见玩家</p>
        </div>
      </section>
      <section className="nearby-section">
        <h4>人物</h4>
        <div className="nearby-grid">
          {world.npcs.map(entity => (
            <article className="nearby-card" key={entity.entityId}>
              <span className="nearby-mark">人</span>
              <div><strong>{entity.name}</strong><p>{entity.description}</p><small>{entity.effect}</small></div>
            </article>
          ))}
          {world.npcs.length === 0 && <p className="nearby-empty">附近没有可交互人物</p>}
        </div>
      </section>
      <section className="nearby-section">
        <h4>建筑</h4>
        <div className="nearby-grid">
          {world.buildings.map(entity => (
            <article className="nearby-card is-building" key={entity.entityId}>
              <span className="nearby-mark">筑</span>
              <div><strong>{entity.name}</strong><p>{entity.description}</p><small>{entity.effect}</small></div>
            </article>
          ))}
          {world.buildings.length === 0 && <p className="nearby-empty">此地尚无可用建筑</p>}
        </div>
      </section>
    </div>
  )
})
