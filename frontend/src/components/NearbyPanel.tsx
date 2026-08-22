import { memo } from 'react'
import { useGameStore } from '../game/gameStore'
import { useT } from '../i18n'
import type { NearbyMonsterDefinition } from '../game/types'
import { FloatingPopover, FloatingInfoCard } from './FloatingPopover'

// 地点/NPC/建筑/怪物名称来自服务端数据，面板框架文本走 i18n。

function MonsterCard({ monster, intervalHint, isElite, isBoss }: {
  monster: NearbyMonsterDefinition
  intervalHint?: string
  isElite?: boolean
  isBoss?: boolean
}): React.JSX.Element {
  const mark = isBoss ? '魁' : isElite ? '精' : (monster.visualSource || '兽')
  const cardClassName = `nearby-card ${isBoss ? 'is-boss' : isElite ? 'is-elite' : 'is-monster'}`

  const details = [
    { label: '等级', value: `Lv.${monster.level}` },
    { label: '位阶', value: monster.rank === 'boss' ? '领主首领' : monster.rank === 'elite' ? '精英妖兽' : '普通生灵' },
    { label: '消耗预算', value: `${monster.budgetCost}` },
    { label: '气血上限', value: `${monster.maxHitpoints}` },
    { label: '攻击威能', value: `${monster.maxDamage}` },
    { label: '护甲抗性', value: `${monster.armor}` },
  ]

  if (intervalHint) {
    details.unshift({ label: '遭遇周期', value: intervalHint })
  }

  return (
    <FloatingPopover
      trigger="hover"
      placement="top"
      content={
        <FloatingInfoCard
          title={monster.name}
          badge={monster.rank === 'boss' ? '领主' : monster.rank === 'elite' ? '精英' : '普通'}
          badgeTone={monster.rank === 'boss' ? 'danger' : monster.rank === 'elite' ? 'purple' : 'gold'}
          subtitle={`Lv.${monster.level} · 阶位 ${monster.tier}`}
          description={`栖息于此地的${monster.rank === 'boss' ? '关底领主' : monster.rank === 'elite' ? '精英强敌' : '常见妖物'}，战力消耗预算 ${monster.budgetCost}。`}
          stats={details}
        />
      }
    >
      <article className={cardClassName}>
        <span className="nearby-mark">{mark}</span>
        <div>
          <div className="nearby-monster-title">
            <strong>{monster.name}</strong>
            <span className="nearby-level-badge">Lv.{monster.level}</span>
          </div>
          <p>
            {isBoss ? '关底领主' : isElite ? '精英强敌' : '普通生灵'} · 预算 {monster.budgetCost}
          </p>
          {intervalHint && <small className="nearby-interval-hint">{intervalHint}</small>}
        </div>
      </article>
    </FloatingPopover>
  )
}

/**
 * 附近同域修士、生灵生态、NPC 与灵地建筑列表面板 (NearbyPanel)
 */
export const NearbyPanel = memo(function NearbyPanel(): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const name = useGameStore(state => state.name)
  const world = useGameStore(state => state.world)

  const monsters = world.monsters ?? []
  const eliteEnemies = world.eliteEnemies ?? []
  const bossEnemies = world.bossEnemies ?? []
  const hasMonsters = monsters.length > 0 || eliteEnemies.length > 0 || bossEnemies.length > 0
  const hasBudget = (world.maxBudget ?? 0) > 0

  return (
    <div className="nearby-layout">
      <header className="nearby-heading">
        <span>{t('combat.nearby.currentLocation')}</span>
        <div className="nearby-heading-row">
          <h3>{world.locationName}</h3>
          {hasBudget && (
            <span className="nearby-budget-pill">
              {t('combat.nearby.dangerBudget')} {world.minBudget} ~ {world.maxBudget}
            </span>
          )}
        </div>
      </header>

      {/* 栖息生灵生态池 */}
      <section className="nearby-section">
        <h4>{t('combat.nearby.monstersHeading')}</h4>
        <div className="nearby-grid">
          {monsters.map(monster => (
            <MonsterCard key={monster.monsterId} monster={monster} />
          ))}
          {eliteEnemies.map(monster => (
            <MonsterCard
              key={`elite-${monster.monsterId}`}
              monster={monster}
              isElite
              intervalHint={t('combat.nearby.eliteIntervalHint', { count: world.eliteInterval ?? 5 })}
            />
          ))}
          {bossEnemies.map(monster => (
            <MonsterCard
              key={`boss-${monster.monsterId}`}
              monster={monster}
              isBoss
              intervalHint={t('combat.nearby.bossIntervalHint', { count: world.bossInterval ?? 20 })}
            />
          ))}
          {!hasMonsters && <p className="nearby-empty">{t('combat.nearby.noMonsters')}</p>}
        </div>
      </section>

      {/* 人物 NPC */}
      <section className="nearby-section">
        <h4>{t('combat.nearby.npcsHeading')}</h4>
        <div className="nearby-grid">
          {world.npcs.map(entity => (
            <article className="nearby-card" key={entity.entityId}>
              <span className="nearby-mark">{t('combat.nearby.markNpc')}</span>
              <div>
                <strong>{entity.name}</strong>
                <p>{entity.description}</p>
                <small>{entity.effect}</small>
              </div>
            </article>
          ))}
          {world.npcs.length === 0 && <p className="nearby-empty">{t('combat.nearby.noNpcs')}</p>}
        </div>
      </section>

      {/* 建筑设施 */}
      <section className="nearby-section">
        <h4>{t('combat.nearby.buildingsHeading')}</h4>
        <div className="nearby-grid">
          {world.buildings.map(entity => (
            <article className="nearby-card is-building" key={entity.entityId}>
              <span className="nearby-mark">{t('combat.nearby.markBuilding')}</span>
              <div>
                <strong>{entity.name}</strong>
                <p>{entity.description}</p>
                <small>{entity.effect}</small>
              </div>
            </article>
          ))}
          {world.buildings.length === 0 && <p className="nearby-empty">{t('combat.nearby.noBuildings')}</p>}
        </div>
      </section>

      {/* 附近同域玩家 */}
      <section className="nearby-section">
        <h4>{t('combat.nearby.playersHeading')}</h4>
        <div className="nearby-grid">
          <article className="nearby-card is-player">
            <span className="nearby-mark">{t('combat.nearby.markPlayer')}</span>
            <div>
              <strong>{name}</strong>
              <small>{t('combat.nearby.you', { id: playerId.slice(0, 8) })}</small>
            </div>
          </article>
          <p className="nearby-empty">{t('combat.nearby.noPlayers')}</p>
        </div>
      </section>
    </div>
  )
})
