import { memo, useMemo } from 'react'
import { formatNumber } from '../game/format'
import type { CombatEncounterDefinition, CombatSpawnDefinition, CombatantDefinition } from '../game/types'
import { useT } from '../i18n'
import { FloatingTooltip } from './FloatingPopover'
import { CharacterAvatar } from './CharacterAvatar'

/** 拼接敌怪类型标签：伤害类别 + 可选五行元素（none 元素省略后缀） */
function getEnemyTypeLabel(category: string, element: string, t: ReturnType<typeof useT>): string {
  const catKey = `combat.encounter.damageCategory.${category}` as const
  const elemKey = `combat.encounter.element.${element}` as const
  const cat = t(catKey) || category
  const elem = element && element !== 'none' ? ` · ${t(elemKey) || element}` : ''
  return `${cat}${elem}`
}

/**
 * 敌怪属性卡片小组件 (EnemyCard)
 *
 * 渲染遭遇中的单个敌怪/Boss 的立绘头像 (水平翻转对齐)、伤害类型、经验掉落、血气条、真元条及攻防基础三维。
 */
const EnemyCard = memo(function EnemyCard({
  enemy,
  strength,
  boss = false,
}: {
  /** 敌怪数值与基础定义 */
  enemy: CombatantDefinition
  /** 敌怪战力评分 */
  strength?: number
  /** 是否为 Boss 级强敌 */
  boss?: boolean
}): React.JSX.Element {
  const t = useT()
  return (
    <article className={`combat-enemy-card${boss ? ' is-boss' : ''}`}>
      <div className="combat-enemy-heading">
        <div className="combat-enemy-avatar-wrap">
          <CharacterAvatar
            actorId={enemy.enemyId}
            className="combat-enemy-avatar"
            fallback={enemy.name.slice(0, 1)}
            isFlipped={true}
            name={enemy.name}
            role="enemy"
          />
        </div>
        <div>
          <strong>{enemy.name}</strong>
          <span>{t('combat.encounter.experience', { type: getEnemyTypeLabel(enemy.category, enemy.element, t), n: formatNumber(enemy.experience) })}</span>
        </div>
        {strength !== undefined && (
          <FloatingTooltip placement="top" title={t('combat.encounter.strengthTip')} tone="gold">
            <b>{formatNumber(strength)}</b>
          </FloatingTooltip>
        )}
      </div>
      <FloatingTooltip placement="top" title={t('combat.encounter.hpTip', { n: formatNumber(enemy.attributes.maxHitpoints) })} tone="jade">
        <div className="combat-resource-line">
          <span>{t('combat.attr.hp')}</span>
          <div><i /></div>
          <strong>{formatNumber(enemy.attributes.maxHitpoints)}</strong>
        </div>
      </FloatingTooltip>
      <FloatingTooltip placement="top" title={t('combat.encounter.manaTip', { n: formatNumber(enemy.attributes.maxManapoints) })} tone="qi">
        <div className="combat-resource-line mana">
          <span>{t('combat.attr.mp')}</span>
          <div><i /></div>
          <strong>{formatNumber(enemy.attributes.maxManapoints)}</strong>
        </div>
      </FloatingTooltip>
      <dl className="combat-enemy-stats">
        <div><dt>{t('combat.encounter.statDamage')}</dt><dd>{formatNumber(enemy.category === 'spell' ? enemy.attributes.spellAttack : enemy.attributes.physicalAttack)}</dd></div>
        <div><dt>{t('combat.encounter.statInterval')}</dt><dd>{formatNumber(enemy.attributes.attackIntervalMilliseconds / 1000)}s</dd></div>
        <div><dt>{t('combat.encounter.statArmor')}</dt><dd>{formatNumber(enemy.attributes.armor)}</dd></div>
      </dl>
    </article>
  )
})

/**
 * 战斗遭遇/副本地牢配置面板组件 (CombatEncounterPanel)
 *
 * 用于地图探索或副本详情中展示当前遭遇的刷怪上限、战力总额、刷新周期、普通怪网格及 Boss 出没轮次。
 */
export const CombatEncounterPanel = memo(function CombatEncounterPanel({
  encounter,
  compact = false,
}: {
  /** 战斗遭遇定义 */
  encounter: CombatEncounterDefinition
  /** 是否以紧凑样式渲染 */
  compact?: boolean
}): React.JSX.Element {
  const t = useT()
  // 无随机刷怪池的遭遇退化为固定单怪卡片，保证网格至少展示主战敌怪。
  const spawns = useMemo<CombatSpawnDefinition[]>(() => (
    encounter.randomSpawns.length > 0
      ? encounter.randomSpawns
      : [{ enemy: encounter.enemy, weight: 1, strength: 1 }]
  ), [encounter])

  return (
    <section className={`combat-encounter-panel${compact ? ' is-compact' : ''}`} aria-label={t('combat.encounter.aria', { name: encounter.name })}>
      <header className="combat-encounter-heading">
        <div>
          <span>{t('combat.encounter.heading')}</span>
          <h3>{encounter.name}</h3>
        </div>
        <dl>
          <div><dt>{t('combat.encounter.maxSpawns')}</dt><dd>{formatNumber(encounter.maxSpawnCount)}</dd></div>
          <div><dt>{t('combat.encounter.maxStrength')}</dt><dd>{formatNumber(encounter.maxTotalStrength)}</dd></div>
          <div><dt>{t('combat.encounter.respawn')}</dt><dd>{formatNumber(encounter.respawnMilliseconds / 1000)}s</dd></div>
        </dl>
      </header>
      <div className="combat-enemy-grid">
        {spawns.map(spawn => (
          <EnemyCard enemy={spawn.enemy} key={spawn.enemy.enemyId} strength={spawn.strength} />
        ))}
      </div>
      {encounter.bossEnemies.length > 0 && (
        <div className="combat-boss-line">
          <span>{t('combat.encounter.bossRound', { n: formatNumber(encounter.battlesPerBoss) })}</span>
          {encounter.bossEnemies.map(enemy => <EnemyCard boss enemy={enemy} key={enemy.enemyId} />)}
        </div>
      )}
    </section>
  )
})
