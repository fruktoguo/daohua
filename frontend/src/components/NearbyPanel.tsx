import { memo } from 'react'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'

// 地点/NPC/建筑名称与描述来自服务端数据（world.npcs/buildings），仅面板框架文本走 i18n。
export const NearbyPanel = memo(function NearbyPanel(): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const name = useGameStore(state => state.name)
  const world = useGameStore(state => state.world)

  return (
    <div className="nearby-layout">
      <header className="nearby-heading">
        <span>{t('combat.nearby.currentLocation')}</span>
        <h3>{world.locationName}</h3>
      </header>
      <section className="nearby-section">
        <h4>{t('combat.nearby.playersHeading')}</h4>
        <div className="nearby-grid">
          <article className="nearby-card is-player">
            <span className="nearby-mark">{t('combat.nearby.markPlayer')}</span>
            <div><strong>{name}</strong><small>{t('combat.nearby.you', { id: playerId.slice(0, 8) })}</small></div>
          </article>
          <p className="nearby-empty">{t('combat.nearby.noPlayers')}</p>
        </div>
      </section>
      <section className="nearby-section">
        <h4>{t('combat.nearby.npcsHeading')}</h4>
        <div className="nearby-grid">
          {world.npcs.map(entity => (
            <article className="nearby-card" key={entity.entityId}>
              <span className="nearby-mark">{t('combat.nearby.markNpc')}</span>
              <div><strong>{entity.name}</strong><p>{entity.description}</p><small>{entity.effect}</small></div>
            </article>
          ))}
          {world.npcs.length === 0 && <p className="nearby-empty">{t('combat.nearby.noNpcs')}</p>}
        </div>
      </section>
      <section className="nearby-section">
        <h4>{t('combat.nearby.buildingsHeading')}</h4>
        <div className="nearby-grid">
          {world.buildings.map(entity => (
            <article className="nearby-card is-building" key={entity.entityId}>
              <span className="nearby-mark">{t('combat.nearby.markBuilding')}</span>
              <div><strong>{entity.name}</strong><p>{entity.description}</p><small>{entity.effect}</small></div>
            </article>
          ))}
          {world.buildings.length === 0 && <p className="nearby-empty">{t('combat.nearby.noBuildings')}</p>}
        </div>
      </section>
    </div>
  )
})
