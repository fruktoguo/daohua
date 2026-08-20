import { memo, useMemo } from 'react'
import { formatNumber } from '../game/format'
import type { CombatEncounterDefinition, CombatSpawnDefinition, CombatantDefinition } from '../game/types'
import { FloatingTooltip } from './FloatingPopover'

function damageTypeLabel(value: string): string {
  return {
    physical: '物理',
    water: '水行',
    nature: '木行',
    fire: '火行',
  }[value] ?? value
}

const EnemyCard = memo(function EnemyCard({
  enemy,
  strength,
  boss = false,
}: {
  enemy: CombatantDefinition
  strength?: number
  boss?: boolean
}): React.JSX.Element {
  return (
    <article className={`combat-enemy-card${boss ? ' is-boss' : ''}`}>
      <div className="combat-enemy-heading">
        <span className="combat-enemy-seal" aria-hidden="true">{enemy.name.slice(0, 1)}</span>
        <div>
          <strong>{enemy.name}</strong>
          <span>{damageTypeLabel(enemy.damageType)} · {enemy.experience} 经验</span>
        </div>
        {strength !== undefined && (
          <FloatingTooltip placement="top" title="妖邪生成强度等阶" tone="gold">
            <b>{strength}</b>
          </FloatingTooltip>
        )}
      </div>
      <FloatingTooltip placement="top" title={`妖兽生命气血上限：${formatNumber(enemy.maxHitpoints)}`} tone="jade">
        <div className="combat-resource-line">
          <span>生命</span>
          <div><i /></div>
          <strong>{formatNumber(enemy.maxHitpoints)}</strong>
        </div>
      </FloatingTooltip>
      <FloatingTooltip placement="top" title={`妖兽灵力上限：${formatNumber(enemy.maxManapoints)}`} tone="qi">
        <div className="combat-resource-line mana">
          <span>灵力</span>
          <div><i /></div>
          <strong>{formatNumber(enemy.maxManapoints)}</strong>
        </div>
      </FloatingTooltip>
      <dl className="combat-enemy-stats">
        <div><dt>伤害</dt><dd>{formatNumber(enemy.maxDamage)}</dd></div>
        <div><dt>间隔</dt><dd>{(enemy.attackIntervalMilliseconds / 1000).toFixed(2)}s</dd></div>
        <div><dt>护甲</dt><dd>{formatNumber(enemy.armor)}</dd></div>
      </dl>
    </article>
  )
})

export const CombatEncounterPanel = memo(function CombatEncounterPanel({
  encounter,
  compact = false,
}: {
  encounter: CombatEncounterDefinition
  compact?: boolean
}): React.JSX.Element {
  const spawns = useMemo<CombatSpawnDefinition[]>(() => (
    encounter.randomSpawns.length > 0
      ? encounter.randomSpawns
      : [{ enemy: encounter.enemy, weight: 1, strength: 1 }]
  ), [encounter])

  return (
    <section className={`combat-encounter-panel${compact ? ' is-compact' : ''}`} aria-label={`${encounter.name}敌情`}>
      <header className="combat-encounter-heading">
        <div>
          <span>战区敌情</span>
          <h3>{encounter.name}</h3>
        </div>
        <dl>
          <div><dt>同场上限</dt><dd>{encounter.maxSpawnCount}</dd></div>
          <div><dt>强度上限</dt><dd>{encounter.maxTotalStrength}</dd></div>
          <div><dt>刷新</dt><dd>{(encounter.respawnMilliseconds / 1000).toFixed(0)}s</dd></div>
        </dl>
      </header>
      <div className="combat-enemy-grid">
        {spawns.map(spawn => (
          <EnemyCard enemy={spawn.enemy} key={spawn.enemy.enemyId} strength={spawn.strength} />
        ))}
      </div>
      {encounter.bossEnemies.length > 0 && (
        <div className="combat-boss-line">
          <span>第 {encounter.battlesPerBoss} 场</span>
          {encounter.bossEnemies.map(enemy => <EnemyCard boss enemy={enemy} key={enemy.enemyId} />)}
        </div>
      )}
    </section>
  )
})
