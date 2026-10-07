import { memo, useMemo } from 'react'
import { formatInteger, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findAbilityInCatalog, findEncounterInCatalog } from '../game/ids'
import { calculateRealmInfo } from '../game/combatConstants'
import type { ActionDefinition, CombatantDefinition } from '../game/types'
import { useT } from '../i18n'
import { CharacterAvatar } from './CharacterAvatar'
import { FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import { ItemDropReference } from './ItemDisplay'
import { UnitNumber } from './UnitNumber'

/** 拼接敌怪类型标签：伤害类别 + 可选五行元素（none 元素省略后缀） */
function getEnemyTypeLabel(category: string, element: string, t: ReturnType<typeof useT>): string {
  const catKey = `combat.encounter.damageCategory.${category}` as const
  const elemKey = `combat.encounter.element.${element}` as const
  const cat = t(catKey) || category
  const elem = element && element !== 'none' ? ` · ${t(elemKey) || element}` : ''
  return `${cat}${elem}`
}

/** 节点生态怪物的生命层次分组（驱动角标徽记与瓦片描边色调） */
type EcologyMonsterRank = 'normal' | 'elite' | 'boss'

interface EcologyMonsterEntry {
  /** 怪物配表定义（服务端目录投影，CombatantDefinition 全字段） */
  enemy: CombatantDefinition
  rank: EcologyMonsterRank
  /** 精英/领主出没周期提示（如「逢 20 场出没」） */
  intervalHint?: string
}

const RANK_BADGE_TONE: Record<EcologyMonsterRank, FloatingTone> = {
  normal: 'gold',
  elite: 'purple',
  boss: 'danger',
}

/**
 * 栖息妖兽图标瓦片 (EcologyMonsterTile)
 *
 * 瓦片本体只保留立绘图标、生命层次角标与等级底标；
 * 悬停弹出简要信息卡（生命层次/境界/气血/攻击/护甲），
 * 单击弹出详细信息卡（六维属性、消耗预算、攻速间隔、出没周期、击杀收益、施展功法与掉落物图标）。
 */
const EcologyMonsterTile = memo(function EcologyMonsterTile({ entry }: { entry: EcologyMonsterEntry }): React.JSX.Element {
  const t = useT()
  const abilities = useGameStore(state => state.abilities)
  const { enemy, rank, intervalHint } = entry
  const attrs = enemy.attributes
  const realm = calculateRealmInfo(0, enemy.level ?? 1)
  const rankLabel = rank === 'boss'
    ? t('combat.nearby.rankBoss')
    : rank === 'elite' ? t('combat.nearby.rankElite') : t('combat.nearby.rankMonster')
  const badgeTone = RANK_BADGE_TONE[rank]
  const subtitle = t('combat.map.monsterSubtitle', {
    realm: realm.minorName,
    level: enemy.level ?? 1,
    tier: enemy.tier ?? 1,
  })

  const briefCard = (
    <FloatingInfoCard
      badge={rankLabel}
      badgeTone={badgeTone}
      stats={[
        { label: t('combat.nearby.statMaxHp'), value: <UnitNumber disableTooltip value={attrs.maxHitpoints} /> },
        { label: t('combat.nearby.statMaxDamage'), value: <UnitNumber disableTooltip value={enemy.category === 'spell' ? attrs.spellAttack : attrs.physicalAttack} /> },
        { label: t('combat.nearby.statArmor'), value: <UnitNumber disableTooltip value={attrs.armor} /> },
      ]}
      subtitle={subtitle}
      title={enemy.name}
    />
  )

  const drops = enemy.drops ?? []
  const abilityNames = enemy.abilityIds
    .map(id => findAbilityInCatalog(abilities, id)?.name ?? id)
    .filter(name => name.length > 0)
  const rewardParts = [
    enemy.experience > 0 ? `${t('combat.report.experience')} +${formatInteger(enemy.experience)}` : null,
  ].filter((part): part is string => part !== null)

  const detailCard = (
    <FloatingInfoCard
      badge={rankLabel}
      badgeTone={badgeTone}
      description={getEnemyTypeLabel(enemy.category, enemy.element, t)}
      lines={[
        ...(intervalHint ? [`${t('combat.nearby.statInterval')}：${intervalHint}`] : []),
        ...(rewardParts.length > 0 ? [`${t('combat.map.monsterReward')}：${rewardParts.join(' · ')}`] : []),
        ...(abilityNames.length > 0 ? [`${t('combat.ecology.abilities')}：${abilityNames.join('、')}`] : []),
        ...(drops.length > 0
          ? [
              <span key="drops">
                {t('combat.map.monsterDrops')}：
                <span className="world-monster-drops-row">
                  {drops.map(drop => (
                    <ItemDropReference drop={drop} key={drop.itemId} />
                  ))}
                </span>
              </span>,
            ]
          : []),
      ]}
      stats={[
        { label: t('insight.combat.hp'), value: <UnitNumber disableTooltip value={attrs.maxHitpoints} /> },
        { label: t('insight.combat.mp'), value: <UnitNumber disableTooltip value={attrs.maxManapoints} /> },
        { label: t('insight.combat.physAtk'), value: <UnitNumber disableTooltip value={attrs.physicalAttack} /> },
        { label: t('insight.combat.spellAtk'), value: <UnitNumber disableTooltip value={attrs.spellAttack} /> },
        { label: t('insight.combat.armor'), value: <UnitNumber disableTooltip value={attrs.armor} /> },
        { label: t('insight.combat.magicRes'), value: <UnitNumber disableTooltip value={attrs.magicResistance} /> },
        { label: t('combat.encounter.statInterval'), value: `${formatNumber(attrs.attackIntervalMilliseconds / 1000)}s` },
      ]}
      subtitle={subtitle}
      title={enemy.name}
    />
  )

  return (
    <FloatingPopover
      content={detailCard}
      displayContents
      placement="bottom"
      size="medium"
      trigger="click"
    >
      <FloatingPopover
        content={briefCard}
        displayContents
        interactive={false}
        placement="top"
        size="small"
        trigger="hover"
      >
        <button
          aria-label={t('combat.ecology.inspectAria', { name: enemy.name })}
          className={`ecology-monster-tile is-${rank}`}
          type="button"
        >
          <CharacterAvatar
            actorId={enemy.enemyId}
            className="ecology-monster-avatar"
            fallback={enemy.name.slice(0, 1)}
            isFlipped
            name={enemy.name}
            role="enemy"
            visual={enemy.visual}
          />
          {rank !== 'normal' && (
            <span aria-hidden="true" className={`ecology-monster-rank is-${rank}`}>
              {rank === 'boss' ? t('combat.nearby.markBoss') : t('combat.nearby.markElite')}
            </span>
          )}
          <span className="ecology-monster-level">Lv.{enemy.level ?? 1}</span>
        </button>
      </FloatingPopover>
    </FloatingPopover>
  )
})

/**
 * 讨伐行动节点生态面板 (CombatEcologyPanel)
 *
 * 展示当前驻留地点的栖息妖兽：数据取静态目录中当前节点按 `encounterIds` 预解析的
 * monsters/eliteEnemies/bossEnemies（与服务端 ResolveCombatEncounter 实战遭遇同根同源），
 * 替代旧版按行动固定 `combatEncounterId` 平铺全部数值的遭遇面板。
 * 行进在道路上或节点未配置生态时，回退展示行动钉死遭遇的怪群。
 */
export const CombatEcologyPanel = memo(function CombatEcologyPanel({
  action,
}: {
  /** 当前打开配置弹窗的讨伐行动定义 */
  action: ActionDefinition
}): React.JSX.Element {
  const t = useT()
  const world = useGameStore(state => state.world)
  const maps = useGameStore(state => state.maps)
  const encounters = useGameStore(state => state.encounters)

  const node = useMemo(() => (
    world.nodeId === null
      ? undefined
      : maps.find(map => map.mapId === world.mapId)?.nodes.find(item => item.nodeId === world.nodeId)
  ), [maps, world.mapId, world.nodeId])

  const hasEcology = (node?.monsters?.length ?? 0)
    + (node?.eliteEnemies?.length ?? 0)
    + (node?.bossEnemies?.length ?? 0) > 0

  // 节点生态优先；无生态（行进中/未配遭遇）回退行动钉死遭遇，与服务端无节点遭遇时的回退口径一致
  const entries = useMemo<EcologyMonsterEntry[]>(() => {
    if (node && hasEcology) {
      return [
        ...(node.monsters ?? []).map(enemy => ({ enemy, rank: 'normal' as const })),
        ...(node.eliteEnemies ?? []).map(enemy => ({
          enemy,
          rank: 'elite' as const,
          intervalHint: t('combat.nearby.eliteIntervalHint', { count: world.eliteInterval ?? 5 }),
        })),
        ...(node.bossEnemies ?? []).map(enemy => ({
          enemy,
          rank: 'boss' as const,
          intervalHint: t('combat.nearby.bossIntervalHint', { count: world.bossInterval ?? 20 }),
        })),
      ]
    }
    const encounter = action.combatEncounterId
      ? findEncounterInCatalog(encounters, action.combatEncounterId)
      : undefined
    if (!encounter) return []
    const spawns = encounter.randomSpawns.length > 0
      ? encounter.randomSpawns
      : [{ enemy: encounter.enemy, weight: 1, strength: 1 }]
    const list: EcologyMonsterEntry[] = spawns.map(spawn => ({ enemy: spawn.enemy, rank: 'normal' }))
    for (const enemy of encounter.bossEnemies) {
      if (!list.some(item => item.enemy.enemyId === enemy.enemyId)) {
        list.push({
          enemy,
          rank: 'boss',
          intervalHint: t('combat.nearby.bossIntervalHint', { count: encounter.battlesPerBoss }),
        })
      }
    }
    return list
  }, [node, hasEcology, world.eliteInterval, world.bossInterval, action.combatEncounterId, encounters, t])

  // 遭遇来源名：节点生态取节点 encounterIds 解析名，回退取行动遭遇名
  const encounterNames = useMemo(() => {
    const ids = node && hasEcology
      ? node.encounterIds
      : (action.combatEncounterId ? [action.combatEncounterId] : [])
    return ids
      .map(id => findEncounterInCatalog(encounters, id)?.name)
      .filter((name): name is string => typeof name === 'string' && name.length > 0)
  }, [node, hasEcology, action.combatEncounterId, encounters])

  const heading = node && hasEcology ? world.locationName : (encounterNames[0] ?? world.locationName)
  const hasBudget = node && hasEcology && (world.maxBudget ?? 0) > 0
  const fallbackEncounter = !hasEcology && action.combatEncounterId
    ? findEncounterInCatalog(encounters, action.combatEncounterId)
    : undefined

  return (
    <section className="combat-ecology-panel" aria-label={t('combat.ecology.aria', { name: heading })}>
      <header className="combat-encounter-heading">
        <div>
          <span>{t('combat.ecology.heading')}</span>
          <h3>{heading}</h3>
        </div>
        <dl>
          {encounterNames.length > 0 && (
            <div><dt>{t('combat.ecology.encounters')}</dt><dd>{encounterNames.join('、')}</dd></div>
          )}
          {hasBudget && (
            <div>
              <dt>{t('combat.nearby.dangerBudget')}</dt>
              <dd>{formatInteger(world.minBudget ?? 0)} ~ {formatInteger(world.maxBudget ?? 0)}</dd>
            </div>
          )}
          {fallbackEncounter && (fallbackEncounter.maxTotalStrength ?? 0) > 0 && (
            <div>
              <dt>{t('combat.encounter.maxStrength')}</dt>
              <dd>{formatInteger(fallbackEncounter.maxTotalStrength)}</dd>
            </div>
          )}
        </dl>
      </header>
      {entries.length > 0 ? (
        <div className="ecology-monster-strip">
          {entries.map(item => (
            <EcologyMonsterTile entry={item} key={item.enemy.enemyId} />
          ))}
        </div>
      ) : (
        <p className="nearby-empty">{t('combat.nearby.noMonsters')}</p>
      )}
    </section>
  )
})
