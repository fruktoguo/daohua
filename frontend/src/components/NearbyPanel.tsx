import { memo, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { useSocialStore } from '../game/socialStore'
import { useT } from '../i18n'
import type { BuildingDefinition, NearbyMonsterDefinition } from '../game/types'
import { resolveItemMetadata } from '../game/ids'
import { CharacterAvatar } from './CharacterAvatar'
import { FloatingPopover, FloatingInfoCard } from './FloatingPopover'
import { Modal } from './Modal'
import { UnitNumber } from './UnitNumber'
import { formatInteger, formatNumber } from '../game/format'
import { calculateRealmInfo } from '../game/combatConstants'
import { gameClient } from '../network/gameClient'
import { ItemDropReference, ItemReference } from './ItemDisplay'
import {
  getBuildingBonusItems,
  getBuildingUpgradeComparison,
  getBuildingSummaryBonusText,
} from '../game/buildingCalculations'

// 地点/NPC/建筑/怪物名称来自服务端数据，面板框架文本走 i18n。

/** 附近面板分类（由地图页顶部 Tab 传入） */
export type NearbyTab = 'monsters' | 'people' | 'buildings'

/** 建筑分组展示顺序（与配表 group 字段一致） */
const BUILDING_GROUP_ORDER = ['production', 'combat', 'other'] as const

const buildingGroupLabelKey = (group: string): string =>
  group === 'production' ? 'combat.nearby.groupProduction'
    : group === 'combat' ? 'combat.nearby.groupCombat'
      : 'combat.nearby.groupOther'

/** 百分比进度条（材料/工期双进度共用） */
const ProgressBar = ({ value }: { value: number }): React.JSX.Element => (
  <span className="building-progress-track">
    <span className="building-progress-fill" style={{ width: `${Math.round(Math.max(0, Math.min(1, value)) * 100)}%` }} />
  </span>
)

/**
 * 栖息生灵单卡片（普通怪 / 精英 / 领主共用模板）
 *
 * 卡片本体展示立绘、名称、等级与一行摘要；悬停浮层展开等级、生命层次、战力预算、气血、伤害与护甲明细。
 */
const MonsterCard = memo(function MonsterCard({ monster, intervalHint, isElite, isBoss }: {
  monster: NearbyMonsterDefinition
  /** 刷出节奏提示（精英/领主按每 N 战出现一次展示） */
  intervalHint?: string
  /** 精英生命层次标记（驱动紫金徽记与卡片描边） */
  isElite?: boolean
  /** 领主生命层次标记（驱动危险色徽记与卡片描边） */
  isBoss?: boolean
}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  const world = useGameStore(state => state.world)

  const isActualBoss = isBoss || monster.rank === 'boss'
  const isActualElite = isElite || monster.rank === 'elite'
  const mark = isActualBoss ? t('combat.nearby.markBoss') : isActualElite ? t('combat.nearby.markElite') : (monster.visualSource || t('combat.nearby.markMonster'))
  const cardClassName = `nearby-card ${isActualBoss ? 'is-boss' : isActualElite ? 'is-elite' : 'is-monster'}`
  const rankLabel = monster.rank === 'boss' ? t('combat.nearby.rankBoss') : monster.rank === 'elite' ? t('combat.nearby.rankElite') : t('combat.nearby.rankMonster')
  const rankDesc = monster.rank === 'boss' ? t('combat.nearby.rankDescBoss') : monster.rank === 'elite' ? t('combat.nearby.rankDescElite') : t('combat.nearby.rankDescMonster')
  const summaryType = isActualBoss ? t('combat.nearby.titleBoss') : isActualElite ? t('combat.nearby.titleElite') : t('combat.nearby.titleMonster')
  const realm = calculateRealmInfo(0, monster.level)

  // 尝试在静态目录当前节点查找匹配的完整 CombatantDefinition（具有灵力、法攻、抗性与击杀经验等详尽数据）
  const staticMonster = useMemo(() => {
    if (world.nodeId === null) return null
    const node = maps.find(m => m.mapId === world.mapId)?.nodes.find(n => n.nodeId === world.nodeId)
    if (!node) return null
    return (node.monsters ?? [])
      .concat(node.eliteEnemies ?? [])
      .concat(node.bossEnemies ?? [])
      .find(m => m.enemyId === monster.monsterId) ?? null
  }, [maps, world.mapId, world.nodeId, monster.monsterId])

  const drops = staticMonster?.drops ?? monster.drops ?? []
  const experience = staticMonster?.experience ?? 0
  const rewardParts = [
    experience > 0 ? `${t('combat.report.experience')} +${formatInteger(experience)}` : null,
  ].filter((part): part is string => part !== null)

  const attrs = staticMonster?.attributes ?? {
    maxHitpoints: monster.maxHitpoints,
    maxManapoints: 0,
    physicalAttack: monster.physicalAttack,
    spellAttack: 0,
    armor: monster.armor,
    magicResistance: 0,
  }

  const details = [
    { label: t('insight.combat.hp'), value: <UnitNumber disableTooltip value={attrs.maxHitpoints} /> },
    ...(attrs.maxManapoints > 0 ? [{ label: t('insight.combat.mp'), value: <UnitNumber disableTooltip value={attrs.maxManapoints} /> }] : []),
    { label: t('insight.combat.physAtk'), value: <UnitNumber disableTooltip value={attrs.physicalAttack} /> },
    ...(attrs.spellAttack > 0 ? [{ label: t('insight.combat.spellAtk'), value: <UnitNumber disableTooltip value={attrs.spellAttack} /> }] : []),
    { label: t('insight.combat.armor'), value: <UnitNumber disableTooltip value={attrs.armor} /> },
    ...(attrs.magicResistance > 0 ? [{ label: t('insight.combat.magicRes'), value: <UnitNumber disableTooltip value={attrs.magicResistance} /> }] : []),
  ]

  const lines = [
    ...(intervalHint ? [`${t('combat.nearby.statInterval')}：${intervalHint}`] : []),
    ...(rewardParts.length > 0 ? [`${t('combat.map.monsterReward')}：${rewardParts.join(' · ')}`] : []),
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
  ]

  return (
    <FloatingPopover
      trigger="hover"
      placement="top"
      content={
        <FloatingInfoCard
          title={monster.name}
          badge={rankLabel}
          badgeTone={isActualBoss ? 'danger' : isActualElite ? 'purple' : 'gold'}
          subtitle={t('combat.map.monsterSubtitle', {
            realm: realm.minorName,
            level: monster.level,
            tier: monster.tier,
          })}
          description={t('combat.nearby.monsterDescription', { rankDesc })}
          lines={lines}
          stats={details}
        />
      }
    >
      <article className={cardClassName}>
        <span className="nearby-mark">
          <CharacterAvatar
            actorId={monster.monsterId}
            fallback={mark}
            name={monster.name}
            role="enemy"
          />
        </span>
        <div>
          <div className="nearby-monster-title">
            <strong>{monster.name}</strong>
            <span className="nearby-level-badge">Lv.{monster.level}</span>
          </div>
          <p>
            {t('combat.nearby.monsterSummary', { type: summaryType })}
          </p>
          {intervalHint && <small className="nearby-interval-hint">{intervalHint}</small>}
        </div>
      </article>
    </FloatingPopover>
  )
})

/**
 * 附近同域修士、生灵生态、NPC 与建筑列表面板 (NearbyPanel)
 * 怪物 / 人物 / 建筑 三个分类由地图页顶部 Tab 选择，通过 tab 属性传入；内容区纵向滚动。
 */
export const NearbyPanel = memo(function NearbyPanel({ tab }: { tab: NearbyTab }): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const name = useGameStore(state => state.name)
  const world = useGameStore(state => state.world)
  const stats = useGameStore(state => state.stats)
  const buildingCatalog = useGameStore(state => state.buildings)
  const inventory = useGameStore(state => state.inventory)
  const nearbyPlayers = useSocialStore(state => state.nearbyPlayers)
  const myValuation = useSocialStore(state => state.myValuation)
  const [buildPickerOpen, setBuildPickerOpen] = useState(false)
  const [detailBuildingId, setDetailBuildingId] = useState<string | null>(null)
  const [contributeBuildingId, setContributeBuildingId] = useState<string | null>(null)
  const [contributeDraft, setContributeDraft] = useState<Record<string, number>>({})
  const [contributeConfirming, setContributeConfirming] = useState(false)

  const monsters = world.monsters ?? []
  const eliteEnemies = world.eliteEnemies ?? []
  const bossEnemies = world.bossEnemies ?? []
  const eliteIds = useMemo(() => new Set(eliteEnemies.map(monster => monster.monsterId)), [eliteEnemies])
  const bossIds = useMemo(() => new Set(bossEnemies.map(monster => monster.monsterId)), [bossEnemies])
  const displayMonsters = useMemo(
    () => monsters.filter(monster => !eliteIds.has(monster.monsterId) && !bossIds.has(monster.monsterId) && monster.rank !== 'elite' && monster.rank !== 'boss'),
    [monsters, eliteIds, bossIds],
  )
  const hasMonsters = monsters.length > 0 || eliteEnemies.length > 0 || bossEnemies.length > 0
  const hasBudget = (world.maxBudget ?? 0) > 0
  const onEdge = world.edgeId !== null
  const nodeBuildings = world.nodeBuildings ?? []
  const buildableIds = world.buildableBuildingIds ?? null
  // 同节点在线修士来自服务端社交状态（含自己），这里排除自己后作为「附近修士」列表展示。
  const otherPlayers = nearbyPlayers.filter(player => player.playerId !== playerId)

  // 背包材料存量（itemId → 数量），供材料提交输入框上限使用。
  const inventoryAmounts = useMemo(() => {
    const amounts = new Map<string, number>()
    for (const item of inventory) amounts.set(item.itemId, (amounts.get(item.itemId) ?? 0) + item.amount)
    return amounts
  }, [inventory])

  // 建造候选：节点可建列表（buildableList/buildableTemplate 下发）∩ 建筑目录，
  // 排除已存在同类与未解锁图鉴的建筑（未达所需境界不出现在候选列表中）。null = 该节点不可建造（隐藏加号卡）。
  const buildCandidates = useMemo(() => {
    if (buildableIds === null) return []
    const realmLevel = stats.realmLevel ?? 1
    const occupied = new Set(nodeBuildings.map(building => building.entityId))
    return buildableIds
      .map(id => buildingCatalog.find(definition => definition.buildingId === id))
      .filter((definition): definition is BuildingDefinition =>
        definition !== undefined
        && !occupied.has(definition.buildingId)
        && (definition.availableNodeIds == null
          || definition.availableNodeIds.length === 0
          || (world.nodeId !== null && definition.availableNodeIds.includes(world.nodeId)))
        && realmLevel >= definition.requiredRealmLevel)
      .map(definition => ({ definition, locked: false }))
  }, [buildableIds, nodeBuildings, buildingCatalog, stats.realmLevel, world.nodeId])

  const detailBuilding = nodeBuildings.find(building => building.entityId === detailBuildingId) ?? null
  const contributeBuilding = nodeBuildings.find(building => building.entityId === contributeBuildingId) ?? null
  const contributeEntries = (contributeBuilding?.materials ?? []).filter(entry => entry.required - entry.contributed > 0)
  const contributeTotal = contributeEntries.reduce((sum, entry) => sum + (contributeDraft[entry.itemId] ?? 0), 0)

  const handleBuild = (definition: BuildingDefinition) => {
    gameClient.sendCommand('building.build', { buildingId: definition.buildingId })
    setBuildPickerOpen(false)
  }

  const openContribute = (buildingId: string) => {
    setContributeDraft({})
    setContributeConfirming(false)
    setContributeBuildingId(buildingId)
  }

  const closeContribute = () => {
    setContributeBuildingId(null)
    setContributeDraft({})
    setContributeConfirming(false)
  }

  const submitContribute = () => {
    if (!contributeBuilding) return
    const materials = Object.fromEntries(
      contributeEntries
        .map(entry => [entry.itemId, contributeDraft[entry.itemId] ?? 0])
        .filter((entry): entry is [string, number] => typeof entry[1] === 'number' && entry[1] > 0))
    if (Object.keys(materials).length === 0) return
    gameClient.sendCommand('building.contribute', { buildingId: contributeBuilding.entityId, materials })
    closeContribute()
  }

  return (
    <div className="nearby-layout">
      <header className="nearby-heading">
        <span>{t('combat.nearby.currentLocation')}</span>
        <div className="nearby-heading-row">
          <h3>{world.locationName}</h3>
          {hasBudget && (
            <span className="nearby-budget-pill">
              {t('combat.nearby.dangerBudget')} {formatInteger(world.minBudget ?? 0)} ~ {formatInteger(world.maxBudget ?? 0)}
            </span>
          )}
        </div>
      </header>

      <div className="nearby-scroll">
        {tab === 'monsters' && (
          <section className="nearby-section">
            <div className="nearby-grid">
              {displayMonsters.map(monster => (
                <MonsterCard key={monster.monsterId} monster={monster} />
              ))}
              {eliteEnemies.map(monster => (
                <MonsterCard
                  key={`elite-${monster.monsterId}`}
                  monster={monster}
                  isElite
                  intervalHint={t('combat.nearby.eliteIntervalHint', { count: (world.eliteInterval && world.eliteInterval > 0) ? world.eliteInterval : 5 })}
                />
              ))}
              {bossEnemies.map(monster => (
                <MonsterCard
                  key={`boss-${monster.monsterId}`}
                  monster={monster}
                  isBoss
                  intervalHint={t('combat.nearby.bossIntervalHint', { count: (world.bossInterval && world.bossInterval > 0) ? world.bossInterval : 20 })}
                />
              ))}
              {!hasMonsters && <p className="nearby-empty">{t('combat.nearby.noMonsters')}</p>}
            </div>
          </section>
        )}

        {tab === 'people' && (
          <>
            <section className="nearby-section">
              <h4>{t('combat.nearby.npcsHeading')}</h4>
              <div className="nearby-grid">
                {world.npcs.map(entity => (
                  <article className="nearby-card is-npc" key={entity.entityId}>
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

            <section className="nearby-section">
              <h4>{t('combat.nearby.playersHeading')}</h4>
              <div className="nearby-grid">
                <article
                  className="nearby-card is-player is-interactive"
                  onClick={() => gameClient.requestPlayerCard(playerId)}
                  title={t('social.card.viewDetail')}
                >
                  <span className="nearby-mark">
                    <CharacterAvatar name={name} role="player" />
                  </span>
                  <div>
                    <div className="nearby-player-name-row">
                      <strong>{name}</strong>
                      <span className="nearby-level-badge">{calculateRealmInfo(0, stats.realmLevel ?? 1).minorName}</span>
                      <small className="nearby-player-self-tag">{t('combat.nearby.you', { id: playerId.slice(0, 8) })}</small>
                    </div>
                    <div className="nearby-player-sub-row">
                      <span className="nearby-player-valuation">
                        {t('social.card.valuation')}：<span className="tone-gold">{formatNumber(Math.floor(myValuation ?? 0))}</span>
                      </span>
                    </div>
                  </div>
                </article>
                {otherPlayers.map(player => (
                  <article
                    className="nearby-card is-player is-interactive"
                    key={player.playerId}
                    onClick={() => gameClient.requestPlayerCard(player.playerId)}
                    title={t('social.card.viewDetail')}
                  >
                    <span className="nearby-mark">
                      <CharacterAvatar name={player.displayName} role="player" />
                    </span>
                    <div>
                      <div className="nearby-player-name-row">
                        <strong>{player.displayName}</strong>
                        <span className="nearby-level-badge">{calculateRealmInfo(0, player.realmLevel ?? 1).minorName}</span>
                      </div>
                      <div className="nearby-player-sub-row">
                        <span className="nearby-player-valuation">
                          {t('social.card.valuation')}：<span className="tone-gold">{formatNumber(Math.floor(player.valuation ?? 0))}</span>
                        </span>
                        <span className="nearby-player-actions" onClick={e => e.stopPropagation()}>
                          <button
                            onClick={() => gameClient.sendSocialAction('friend.request', player.playerId)}
                            type="button"
                          >{t('combat.chat.addFriendAction')}</button>
                          <button
                            onClick={() => gameClient.sendSocialAction('party.invite', player.playerId)}
                            type="button"
                          >{t('combat.chat.partyInvite')}</button>
                        </span>
                      </div>
                    </div>
                  </article>
                ))}
                {otherPlayers.length === 0 && <p className="nearby-empty">{t('combat.nearby.noPlayers')}</p>}
              </div>
            </section>
          </>
        )}

        {tab === 'buildings' && (
          <section className="nearby-section">
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
              {nodeBuildings.map(building => {
                const definition = buildingCatalog.find(d => d.buildingId === building.entityId)
                const bonusSummary = getBuildingSummaryBonusText(definition, building.level, (key, def) => t(key) || def)
                const isOverloaded = building.activeUsers > building.capacity || building.efficiency < 1
                return (
                  <button
                    className="nearby-card is-building is-player-building"
                    key={building.entityId}
                    onClick={() => setDetailBuildingId(building.entityId)}
                    type="button"
                  >
                    <span className="nearby-mark">{t('combat.nearby.markBuilding')}</span>
                    <div>
                      <div className="nearby-monster-title">
                        <strong>{building.name}</strong>
                        <span className="nearby-level-badge">
                          {building.level > 0
                            ? t('combat.nearby.buildingLevelValue', { level: building.level })
                            : t('combat.nearby.buildingConstructing')}
                        </span>
                      </div>
                      {bonusSummary ? (
                        <div className="building-card-bonus">
                          <span className="building-card-bonus-tag">{bonusSummary}</span>
                        </div>
                      ) : (
                        <small>{building.effect}</small>
                      )}
                      <small className="building-usage">
                        <span>{t('combat.nearby.buildingUsage', { users: formatInteger(building.activeUsers), capacity: formatInteger(building.capacity) })}</span>
                        <span className={isOverloaded ? 'building-usage-overload' : 'building-usage-idle'}>
                          {isOverloaded
                            ? t('combat.nearby.buildingEfficiencyReduced', { percent: Math.round(building.efficiency * 100) })
                            : t('combat.nearby.buildingEfficiencyFull')}
                        </span>
                      </small>
                      {building.targetLevel > 0 && (
                        <span className="building-card-progress">
                          <ProgressBar value={building.buildProgress} />
                        </span>
                      )}
                    </div>
                  </button>
                )
              })}
              {!onEdge && buildableIds !== null && buildableIds.length > 0 && (
                <button
                  aria-label={t('combat.nearby.buildAdd')}
                  className="nearby-card nearby-card--add"
                  onClick={() => setBuildPickerOpen(true)}
                  title={t('combat.nearby.buildAdd')}
                  type="button"
                >
                  <span className="nearby-add-glyph" aria-hidden="true">＋</span>
                </button>
              )}
            </div>
          </section>
        )}
      </div>

      {buildPickerOpen && (
        <Modal
          className="build-picker-modal"
          labelledBy="build-picker-title"
          onClose={() => setBuildPickerOpen(false)}
          open
          size="medium"
        >
          <header className="build-picker-header">
            <h2 id="build-picker-title">{t('combat.nearby.buildPickerTitle')}</h2>
          </header>
          <div className="build-picker-list">
            {BUILDING_GROUP_ORDER.map(group => {
              const entries = buildCandidates.filter(candidate => candidate.definition.group === group)
              if (entries.length === 0) return null
              return (
                <section className="build-picker-group" key={group}>
                  <h4>{t(buildingGroupLabelKey(group))}</h4>
                  {entries.map(({ definition, locked }) => (
                    <button
                      className={`nearby-card is-building build-picker-item ${locked ? 'is-locked' : ''}`}
                      disabled={locked}
                      key={definition.buildingId}
                      onClick={() => handleBuild(definition)}
                      type="button"
                    >
                      <span className="nearby-mark">{t('combat.nearby.markBuilding')}</span>
                      <div>
                        <strong>{definition.name}</strong>
                        <p>{definition.description}</p>
                        <small>
                          {locked
                            ? t('combat.nearby.buildingLocked', { level: definition.requiredRealmLevel })
                            : definition.effect}
                        </small>
                      </div>
                    </button>
                  ))}
                </section>
              )
            })}
            {buildCandidates.length === 0 && (
              <p className="nearby-empty">{t('combat.nearby.buildEmpty')}</p>
            )}
          </div>
        </Modal>
      )}

      {detailBuilding && (() => {
        const definition = buildingCatalog.find(d => d.buildingId === detailBuilding.entityId)
        const isUpgrading = detailBuilding.targetLevel > 0
        const targetLevel = isUpgrading ? detailBuilding.targetLevel : detailBuilding.level + 1
        const bonusItems = getBuildingBonusItems(definition, detailBuilding.level, (key, def) => t(key) || def)
        const comparisonRows = getBuildingUpgradeComparison(definition, detailBuilding.level, targetLevel, (key, def) => t(key) || def)
        const isOverloaded = detailBuilding.activeUsers > detailBuilding.capacity || detailBuilding.efficiency < 1
        const efficiencyPercent = Math.round(detailBuilding.efficiency * 100)
        const overUsers = Math.max(0, Math.round(detailBuilding.activeUsers - detailBuilding.capacity))

        return (
          <Modal
            className="building-detail-modal"
            labelledBy="building-detail-title"
            onClose={() => setDetailBuildingId(null)}
            open
            size="medium"
          >
            <header className="build-picker-header">
              <h2 id="building-detail-title">
                {detailBuilding.name}
                <span className="nearby-level-badge">
                  {detailBuilding.level > 0
                    ? t('combat.nearby.buildingLevelValue', { level: detailBuilding.level })
                    : t('combat.nearby.buildingConstructing')}
                </span>
              </h2>
            </header>

            <div className="building-detail-body">
              {/* 一、信息 */}
              <section className="building-section-card">
                <h4 className="building-section-title">{t('combat.nearby.buildingSectionInfo')}</h4>
                <p className="building-desc">{detailBuilding.description}</p>
                <div className="building-bonuses-current">
                  <span className="building-bonus-title">{t('combat.nearby.buildingCurrentBonus')}：</span>
                  {bonusItems.length > 0 && detailBuilding.level > 0 ? (
                    <div className="building-bonus-tags">
                      {bonusItems.map(item => (
                        <span className={`building-bonus-pill pill-tone--${item.tone}`} key={item.key}>
                          <span className="capsule-label">{item.label}</span>
                          <span className="underlined-value">{item.valueText}</span>
                        </span>
                      ))}
                    </div>
                  ) : (
                    <small className="building-bonus-empty">{t('combat.nearby.buildingBonusNone')}</small>
                  )}
                </div>
              </section>

              {/* 二、升级 */}
              <section className="building-section-card">
                <div className="building-section-header-row">
                  <h4 className="building-section-title">{t('combat.nearby.buildingSectionUpgrade')}</h4>
                  {isUpgrading && (
                    <span className="building-tag-upgrading">
                      {detailBuilding.level > 0
                        ? t('combat.nearby.buildingUpgrading', { level: detailBuilding.targetLevel })
                        : t('combat.nearby.buildingConstructing')}
                    </span>
                  )}
                </div>

                <div className="building-upgrade-box">
                  <div className="breakthrough-modal-realm building-upgrade-level-jump">
                    <span>{detailBuilding.level > 0 ? `Lv.${detailBuilding.level}` : t('combat.nearby.buildingConstructing')}</span>
                    <span aria-hidden="true" className="breakthrough-modal-realm-arrow">→</span>
                    <strong>Lv.{targetLevel}</strong>
                  </div>

                  {comparisonRows.length > 0 ? (
                    <ul className="breakthrough-modal-attrs building-upgrade-attrs">
                      {comparisonRows.map(row => (
                        <li className={`breakthrough-modal-row pill-tone--${row.tone}`} key={row.key}>
                          <span className="breakthrough-modal-row-label">{row.label}</span>
                          <span className="breakthrough-modal-row-before">{row.before}</span>
                          <span aria-hidden="true" className="breakthrough-modal-row-arrow underlined-value">→</span>
                          <span className="breakthrough-modal-row-after underlined-value">{row.after}</span>
                          <span className="breakthrough-modal-row-gain underlined-value">+{row.delta}</span>
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <p className="breakthrough-modal-empty">{t('combat.nearby.buildingEffectNone')}</p>
                  )}
                </div>

                {isUpgrading ? (
                  <div className="building-construction-panel">
                    <div className="building-progress-row">
                      <span>{t('combat.nearby.buildingMaterialProgress', { percent: Math.round(detailBuilding.materialProgress * 100) })}</span>
                      <ProgressBar value={detailBuilding.materialProgress} />
                    </div>
                    <div className="building-progress-row">
                      <span>{t('combat.nearby.buildingTimeProgress', { percent: Math.round(detailBuilding.timeProgress * 100) })}</span>
                      <ProgressBar value={detailBuilding.timeProgress} />
                    </div>
                    {detailBuilding.materials.length > 0 && (
                      <ul className="building-materials">
                        {detailBuilding.materials.map(material => (
                          <li key={material.itemId}>
                            <ItemReference
                              amount={material.required}
                              itemId={material.itemId}
                              valueContent={
                                <span className="underlined-value">
                                  {formatInteger(material.contributed)} / {formatInteger(material.required)}
                                </span>
                              }
                            />
                          </li>
                        ))}
                      </ul>
                    )}
                    <button
                      className="building-action-btn"
                      onClick={() => openContribute(detailBuilding.entityId)}
                      type="button"
                    >
                      {t('combat.nearby.buildingContribute')}
                    </button>
                  </div>
                ) : (
                  detailBuilding.level > 0 && (
                    <div className="building-upgrade-action-row">
                      <button
                        className="building-action-btn"
                        onClick={() => gameClient.sendCommand('building.upgrade', { buildingId: detailBuilding.entityId })}
                        type="button"
                      >
                        {t('combat.nearby.buildingUpgrade')}
                      </button>
                    </div>
                  )
                )}
              </section>

              {/* 三、负载 */}
              <section className="building-section-card">
                <h4 className="building-section-title">{t('combat.nearby.buildingSectionLoad')}</h4>
                <div className="building-load-grid">
                  <div className="building-load-item">
                    <span className="building-load-item-label">{t('combat.nearby.buildingCapacityDesc')}</span>
                    <span className="building-load-item-value">
                      <strong>{formatInteger(detailBuilding.activeUsers)}</strong>
                      <span className="building-load-sep">/</span>
                      <span>{formatInteger(detailBuilding.capacity)} 人</span>
                    </span>
                  </div>
                  <div className="building-load-item">
                    <span className="building-load-item-label">{t('combat.nearby.buildingEfficiencyLabel')}</span>
                    <span className={`building-load-item-value ${isOverloaded ? 'is-overload' : 'is-normal'}`}>
                      <strong>{isOverloaded ? t('combat.nearby.buildingEfficiencyReduced', { percent: efficiencyPercent }) : t('combat.nearby.buildingEfficiencyFull')}</strong>
                    </span>
                  </div>
                </div>

                <div className={`building-load-status-bar ${isOverloaded ? 'is-overload' : 'is-normal'}`}>
                  {isOverloaded ? (
                    <p>
                      {t('combat.nearby.buildingEfficiencyReducedDesc', {
                        overUsers: formatInteger(overUsers),
                        percent: efficiencyPercent,
                      })}
                    </p>
                  ) : (
                    <p>{t('combat.nearby.buildingEfficiencyNormalDesc')}</p>
                  )}
                </div>
                <small className="building-load-hint">{t('combat.nearby.buildingLoadHint')}</small>
              </section>
            </div>
          </Modal>
        )
      })()}

      {contributeBuilding && (
        <Modal
          className="building-detail-modal"
          labelledBy="building-contribute-title"
          onClose={closeContribute}
          open
          size="medium"
        >
          <header className="build-picker-header">
            <h2 id="building-contribute-title">
              {t('combat.nearby.contributeTitle', { name: contributeBuilding.name })}
            </h2>
          </header>
          <div className="building-detail-body">
            <p className="contribute-hint">{t('combat.nearby.contributeHint')}</p>
            <ul className="building-materials contribute-list">
              {contributeEntries.map(entry => {
                const remaining = entry.required - entry.contributed
                const owned = inventoryAmounts.get(entry.itemId) ?? 0
                const max = Math.min(remaining, owned)
                const draft = contributeDraft[entry.itemId] ?? 0
                return (
                  <li key={entry.itemId}>
                    <ItemReference itemId={entry.itemId} />
                    <small>
                      {t('combat.nearby.contributeOwned', { amount: formatInteger(owned) })}
                      {' · '}
                      {t('combat.nearby.contributeNeed', { amount: formatInteger(remaining) })}
                    </small>
                    <input
                      aria-label={resolveItemMetadata(entry.itemId)?.label ?? entry.itemId}
                      max={max}
                      min={0}
                      onChange={event => {
                        const parsed = Number(event.target.value)
                        setContributeDraft(previous => ({
                          ...previous,
                          [entry.itemId]: Number.isFinite(parsed) ? Math.max(0, Math.min(max, parsed)) : 0,
                        }))
                      }}
                      type="number"
                      value={draft}
                    />
                  </li>
                )
              })}
            </ul>
            {!contributeConfirming ? (
              <button
                className="building-action-btn"
                disabled={contributeTotal <= 0}
                onClick={() => setContributeConfirming(true)}
                type="button"
              >{t('combat.nearby.contributeSubmit')}</button>
            ) : (
              <div className="contribute-confirm-row">
                <button
                  className="building-action-btn is-confirm"
                  onClick={submitContribute}
                  type="button"
                >{t('combat.nearby.contributeConfirm')}</button>
                <button
                  className="building-action-btn is-cancel"
                  onClick={() => setContributeConfirming(false)}
                  type="button"
                >{t('combat.nearby.contributeCancel')}</button>
              </div>
            )}
          </div>
        </Modal>
      )}
    </div>
  )
})
