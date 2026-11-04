import React from 'react'
import { getT, type TFunction } from '../i18n'
import { useGameStore } from './gameStore'
import { resolveAssetLabel } from './ids'
import type {
  ActionDefinition,
  CombatantDefinition,
  CombatEncounterDefinition,
  CombatEquipmentDefinition,
  ItemDefinition,
} from './types'
import type { FloatingAsideCard } from '../components/FloatingPopover'

/**
 * 物品来源大类
 */
export type ItemSourceKind = 'monster' | 'crafting' | 'gathering' | 'pack' | 'general'

/**
 * 妖兽掉落条目
 */
export interface ItemMonsterSource {
  enemyId: string
  enemyName: string
  rank?: string
  tier?: number
  level?: number
  encounterName?: string
  dropChance: number
  minQuantity: number
  maxQuantity: number
}

/**
 * 配方/行动产出条目
 */
export interface ItemActionSource {
  actionId: string
  actionName: string
  category: 'crafting' | 'gathering'
  technique?: string | null
  level?: number
  amount: number
}

/**
 * 储物袋/福袋开启条目
 */
export interface ItemPackSource {
  packItemId: string
  packName: string
  chance: number
  chanceText: string
  quantityText: string
}

/**
 * 通用资源概括条目（灵石、功德等高频流通资产）
 */
export interface ItemGeneralSource {
  tag: string
  title: string
  desc: string
}

/**
 * 物品来源反查聚合结果
 */
export interface ItemSourcesResult {
  itemId: string
  monsters: ItemMonsterSource[]
  crafting: ItemActionSource[]
  gatherings: ItemActionSource[]
  packs: ItemPackSource[]
  generals: ItemGeneralSource[]
  /** 掉落该物品的怪物总数（用于超量折叠提示） */
  totalMonstersCount: number
}

/**
 * 储物袋/礼包静态开包产出规则（与服务端 StorageBagOpenRules 保持权威同构）
 */
interface StaticPackRule {
  packItemId: string
  targetItemId: string
  chance: number
  chanceText: string
  quantityText: string
}

const STATIC_PACK_RULES: readonly StaticPackRule[] = [
  // 破损的储物袋产物
  {
    packItemId: 'item.storage-bag.broken',
    targetItemId: 'currency.spirit-stone',
    chance: 1.0,
    chanceText: '100%',
    quantityText: '1 ~ 1,000',
  },
  {
    packItemId: 'item.storage-bag.broken',
    targetItemId: 'currency.spirit-stone',
    chance: 0.1,
    chanceText: '10% 额外',
    quantityText: '1 ~ 10,000',
  },
  {
    packItemId: 'item.storage-bag.broken',
    targetItemId: 'item.gongde',
    chance: 0.1,
    chanceText: '10%',
    quantityText: '5 ~ 10',
  },
  {
    packItemId: 'item.storage-bag.broken',
    targetItemId: 'item.gongde',
    chance: 0.01,
    chanceText: '1% 暴击',
    quantityText: '50 ~ 100',
  },
  {
    packItemId: 'item.storage-bag.broken',
    targetItemId: 'item.awakening-stone',
    chance: 0.001,
    chanceText: '0.1%',
    quantityText: '1',
  },
]

/**
 * 缓存的倒排索引（当 encounters 或 actions 数组引用改变时惰性重建）
 */
let cachedEncountersRef: readonly CombatEncounterDefinition[] | null = null
let cachedActionsRef: readonly ActionDefinition[] | null = null

let monsterDropsIndex: Map<string, ItemMonsterSource[]> = new Map()
let craftingYieldsIndex: Map<string, ItemActionSource[]> = new Map()
let gatheringYieldsIndex: Map<string, ItemActionSource[]> = new Map()

/**
 * 从 encounters 和 maps 中抽取所有唯一怪兽定义
 */
function extractAllMonsters(encounters: readonly CombatEncounterDefinition[]): Array<{
  monster: CombatantDefinition
  encounterName: string
}> {
  const result: Array<{ monster: CombatantDefinition; encounterName: string }> = []
  const seenIds = new Set<string>()

  for (const encounter of encounters) {
    const encName = encounter.name || encounter.encounterId

    // 1. 主敌
    if (encounter.enemy && !seenIds.has(encounter.enemy.enemyId)) {
      seenIds.add(encounter.enemy.enemyId)
      result.push({ monster: encounter.enemy, encounterName: encName })
    }

    // 2. 随机刷怪候选池
    if (Array.isArray(encounter.randomSpawns)) {
      for (const spawn of encounter.randomSpawns) {
        if (spawn.enemy && !seenIds.has(spawn.enemy.enemyId)) {
          seenIds.add(spawn.enemy.enemyId)
          result.push({ monster: spawn.enemy, encounterName: encName })
        }
      }
    }

    // 3. 精英波次
    if (Array.isArray(encounter.eliteEnemies)) {
      for (const elite of encounter.eliteEnemies) {
        if (elite && !seenIds.has(elite.enemyId)) {
          seenIds.add(elite.enemyId)
          result.push({ monster: elite, encounterName: encName })
        }
      }
    }

    // 4. Boss 波次
    if (Array.isArray(encounter.bossEnemies)) {
      for (const boss of encounter.bossEnemies) {
        if (boss && !seenIds.has(boss.enemyId)) {
          seenIds.add(boss.enemyId)
          result.push({ monster: boss, encounterName: encName })
        }
      }
    }
  }

  return result
}

/**
 * 重建怪物掉落与行动配方倒排索引
 */
function rebuildIndexIfNeeded(
  encounters: readonly CombatEncounterDefinition[],
  actions: readonly ActionDefinition[]
): void {
  if (encounters === cachedEncountersRef && actions === cachedActionsRef) {
    return
  }

  const nextMonsterMap = new Map<string, ItemMonsterSource[]>()
  const nextCraftingMap = new Map<string, ItemActionSource[]>()
  const nextGatheringMap = new Map<string, ItemActionSource[]>()

  // 1. 扫描怪物掉落
  const monsterList = extractAllMonsters(encounters)
  for (const { monster, encounterName } of monsterList) {
    if (!Array.isArray(monster.drops)) continue

    for (const drop of monster.drops) {
      if (!drop.itemId) continue
      const existing = nextMonsterMap.get(drop.itemId) ?? []
      existing.push({
        enemyId: monster.enemyId,
        enemyName: monster.name || monster.enemyId,
        rank: monster.rank,
        tier: monster.tier,
        level: monster.level,
        encounterName,
        dropChance: drop.dropChance,
        minQuantity: drop.minQuantity,
        maxQuantity: drop.maxQuantity,
      })
      nextMonsterMap.set(drop.itemId, existing)
    }
  }

  // 排序：等阶 T 级降序 -> 等级降序 -> 掉落概率降序
  for (const list of nextMonsterMap.values()) {
    list.sort((a, b) => {
      const tierDiff = (b.tier ?? 0) - (a.tier ?? 0)
      if (tierDiff !== 0) return tierDiff
      const levelDiff = (b.level ?? 0) - (a.level ?? 0)
      if (levelDiff !== 0) return levelDiff
      return b.dropChance - a.dropChance
    })
  }

  // 2. 扫描行动产出（yields）
  for (const action of actions) {
    if (action.hiddenFromCatalog) continue
    if (!Array.isArray(action.yields)) continue

    for (const y of action.yields) {
      if (y.kind !== 'item' || !y.id) continue

      const isCrafting =
        action.type === 'alchemy' ||
        action.technique === 'alchemy' ||
        action.technique === 'forging' ||
        action.technique === 'talismans' ||
        action.technique === 'formations' ||
        action.technique === 'crafting' ||
        action.id.startsWith('forge.') ||
        action.id.startsWith('craft.') ||
        action.id.startsWith('alchemy.') ||
        action.id.startsWith('talisman.') ||
        action.id.startsWith('formation.')

      const entry: ItemActionSource = {
        actionId: action.id,
        actionName: action.name || action.id,
        category: isCrafting ? 'crafting' : 'gathering',
        technique: action.technique,
        level: action.level,
        amount: y.amount,
      }

      if (isCrafting) {
        const existing = nextCraftingMap.get(y.id) ?? []
        existing.push(entry)
        nextCraftingMap.set(y.id, existing)
      } else {
        const existing = nextGatheringMap.get(y.id) ?? []
        existing.push(entry)
        nextGatheringMap.set(y.id, existing)
      }
    }
  }

  // 排序：等级升序，便于玩家由易到难了解
  for (const list of nextCraftingMap.values()) {
    list.sort((a, b) => (a.level ?? 0) - (b.level ?? 0))
  }
  for (const list of nextGatheringMap.values()) {
    list.sort((a, b) => (a.level ?? 0) - (b.level ?? 0))
  }

  monsterDropsIndex = nextMonsterMap
  craftingYieldsIndex = nextCraftingMap
  gatheringYieldsIndex = nextGatheringMap
  cachedEncountersRef = encounters
  cachedActionsRef = actions
}

/**
 * 动态反查指定物品的完整获取途径
 */
export function resolveItemSources(itemId: string): ItemSourcesResult {
  const store = useGameStore.getState()
  rebuildIndexIfNeeded(store.encounters, store.actions)

  const isSpiritStone =
    itemId === 'currency.spirit-stone' ||
    itemId === 'currency.spirit-stones' ||
    itemId === 'spirit-stone'

  const isGongde = itemId === 'item.gongde' || itemId === 'gongde'
  const isGongdeBound = itemId === 'item.gongde-bound'

  const rawMonsters = monsterDropsIndex.get(itemId) ?? []
  const crafting = craftingYieldsIndex.get(itemId) ?? []
  const gatherings = gatheringYieldsIndex.get(itemId) ?? []

  // 礼包/储物袋开包反查
  const packs: ItemPackSource[] = []
  for (const rule of STATIC_PACK_RULES) {
    if (rule.targetItemId === itemId) {
      const packDef = store.items.find(i => i.itemId === rule.packItemId)
      const packName = packDef?.name || resolveAssetLabel(rule.packItemId, store.items)
      packs.push({
        packItemId: rule.packItemId,
        packName,
        chance: rule.chance,
        chanceText: rule.chanceText,
        quantityText: rule.quantityText,
      })
    }
  }

  const generals: ItemGeneralSource[] = []
  const t = getT()

  // 特殊资源处理：灵石与功德
  if (isSpiritStone) {
    generals.push(
      {
        tag: t('game.itemSources.tagMonster'),
        title: t('game.itemSources.spiritStoneMonsterTitle'),
        desc: t('game.itemSources.spiritStoneMonsterDesc'),
      },
      {
        tag: t('game.itemSources.tagPack'),
        title: t('game.itemSources.spiritStoneBagTitle'),
        desc: t('game.itemSources.spiritStoneBagDesc'),
      },
      {
        tag: t('game.itemSources.tagMarket'),
        title: t('game.itemSources.spiritStoneMarketTitle'),
        desc: t('game.itemSources.spiritStoneMarketDesc'),
      },
      {
        tag: t('game.itemSources.tagAction'),
        title: t('game.itemSources.spiritStoneActionTitle'),
        desc: t('game.itemSources.spiritStoneActionDesc'),
      }
    )
  } else if (isGongde) {
    generals.push(
      {
        tag: t('game.itemSources.tagPack'),
        title: t('game.itemSources.gongdeBagTitle'),
        desc: t('game.itemSources.gongdeBagDesc'),
      },
      {
        tag: t('game.itemSources.tagFate'),
        title: t('game.itemSources.gongdeFateTitle'),
        desc: t('game.itemSources.gongdeFateDesc'),
      }
    )
  } else if (isGongdeBound) {
    generals.push({
      tag: t('game.itemSources.tagParty'),
      title: t('game.itemSources.gongdeBoundTitle'),
      desc: t('game.itemSources.gongdeBoundDesc'),
    })
  }

  // 常规物品怪物截断保护：最多展示前 5 只核心怪
  const totalMonstersCount = rawMonsters.length
  const monsters = isSpiritStone ? [] : rawMonsters.slice(0, 5)

  return {
    itemId,
    monsters,
    crafting,
    gatherings,
    packs: isSpiritStone || isGongde ? [] : packs, // 灵石和功德已收录进 generals，避免重复展示
    generals,
    totalMonstersCount: isSpiritStone ? 0 : totalMonstersCount,
  }
}

/**
 * 格式化概率显示
 */
function formatDropChance(chance: number): string {
  if (chance >= 1.0) return '100%'
  const percent = chance * 100
  if (percent >= 1.0) {
    return Number.isInteger(percent) ? `${percent}%` : `${percent.toFixed(1)}%`
  }
  if (percent >= 0.1) {
    return `${percent.toFixed(2)}%`
  }
  return `${percent.toFixed(3)}%`
}

/**
 * 格式化数量区间
 */
function formatQuantityRange(min: number, max: number): string {
  if (min === max) {
    return `× ${min}`
  }
  return `× ${min}~${max}`
}

/**
 * 解析生活技艺显示名称，支持单复数键归一并保持强类型契约
 */
function resolveTechniqueDisplayName(
  technique: string | null | undefined,
  t: TFunction
): string {
  if (!technique) return t('game.itemSources.craftingAction')
  switch (technique) {
    case 'alchemy':
      return t('insight.technique.alchemy')
    case 'forging':
    case 'crafting':
      return t('insight.technique.forging')
    case 'talisman':
    case 'talismans':
      return t('insight.technique.talisman')
    case 'formation':
    case 'formations':
      return t('insight.technique.formation')
    case 'enhancement':
      return t('insight.technique.enhancement')
    case 'gathering':
      return t('insight.technique.gathering')
    case 'mining':
      return t('insight.technique.mining')
    default:
      return t('game.itemSources.craftingAction')
  }
}

/**
 * 渲染独立来源副气泡内容 (Aside Card Content)
 */
function ItemSourcesAsideContent({
  sources,
}: {
  sources: ItemSourcesResult
}): React.JSX.Element {
  const t = getT()

  return (
    <div className="daohua-source-aside-container">
      {/* 1. 特殊概括渠道（灵石、功德等通用资源） */}
      {sources.generals.length > 0 && (
        <div className="daohua-source-aside-group">
          <div className="daohua-source-aside-group-title">
            <span className="daohua-source-aside-badge daohua-source-aside-badge--gold">
              {t('game.itemSources.categoryGeneral')}
            </span>
          </div>
          <div className="daohua-source-aside-general-list">
            {sources.generals.map((gen, idx) => (
              <div className="daohua-source-aside-general-row" key={idx}>
                <div className="daohua-source-aside-general-head">
                  <span className="daohua-source-aside-tag">{gen.tag}</span>
                  <span className="daohua-source-aside-title-text">{gen.title}</span>
                </div>
                <div className="daohua-source-aside-desc-text">{gen.desc}</div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* 2. 妖兽掉落渠道 */}
      {sources.monsters.length > 0 && (
        <div className="daohua-source-aside-group">
          <div className="daohua-source-aside-group-title">
            <span className="daohua-source-aside-badge daohua-source-aside-badge--crimson">
              {t('game.itemSources.categoryMonsters')}
            </span>
            <span className="daohua-source-aside-sub-count">
              {sources.totalMonstersCount} {t('game.itemSources.unitMonster')}
            </span>
          </div>
          <div className="daohua-source-aside-item-list">
            {sources.monsters.map((m, idx) => {
              const rankClass =
                m.rank === 'boss'
                  ? 'is-boss'
                  : m.rank === 'elite'
                  ? 'is-elite'
                  : 'is-normal'
              return (
                <div className="daohua-source-aside-row" key={`${m.enemyId}-${idx}`}>
                  <div className="daohua-source-aside-row-left">
                    <span className={`daohua-source-aside-name ${rankClass}`}>
                      {m.enemyName}
                    </span>
                    {m.level !== undefined && (
                      <span className="daohua-source-aside-level">Lv.{m.level}</span>
                    )}
                  </div>
                  <div className="daohua-source-aside-row-right">
                    <span className="daohua-source-aside-rate">
                      {formatDropChance(m.dropChance)}
                    </span>
                    <span className="daohua-source-aside-amount">
                      {formatQuantityRange(m.minQuantity, m.maxQuantity)}
                    </span>
                  </div>
                </div>
              )
            })}
            {sources.totalMonstersCount > sources.monsters.length && (
              <div className="daohua-source-aside-more">
                {t('game.itemSources.moreMonsters', {
                  count: sources.totalMonstersCount - sources.monsters.length,
                })}
              </div>
            )}
          </div>
        </div>
      )}

      {/* 3. 技艺配方制造 */}
      {sources.crafting.length > 0 && (
        <div className="daohua-source-aside-group">
          <div className="daohua-source-aside-group-title">
            <span className="daohua-source-aside-badge daohua-source-aside-badge--qi">
              {t('game.itemSources.categoryCrafting')}
            </span>
          </div>
          <div className="daohua-source-aside-item-list">
            {sources.crafting.map((c, idx) => {
              const techLabel = resolveTechniqueDisplayName(c.technique, t)
              return (
                <div className="daohua-source-aside-row" key={`${c.actionId}-${idx}`}>
                  <div className="daohua-source-aside-row-left">
                    <span className="daohua-source-aside-name">{c.actionName}</span>
                    <span className="daohua-source-aside-meta">
                      {techLabel}
                      {c.level ? ` Lv.${c.level}` : ''}
                    </span>
                  </div>
                  <div className="daohua-source-aside-row-right">
                    <span className="daohua-source-aside-amount">
                      × {c.amount || 1}
                    </span>
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* 4. 天地采伐探索 */}
      {sources.gatherings.length > 0 && (
        <div className="daohua-source-aside-group">
          <div className="daohua-source-aside-group-title">
            <span className="daohua-source-aside-badge daohua-source-aside-badge--emerald">
              {t('game.itemSources.categoryGathering')}
            </span>
          </div>
          <div className="daohua-source-aside-item-list">
            {sources.gatherings.map((g, idx) => (
              <div className="daohua-source-aside-row" key={`${g.actionId}-${idx}`}>
                <div className="daohua-source-aside-row-left">
                  <span className="daohua-source-aside-name">{g.actionName}</span>
                </div>
                <div className="daohua-source-aside-row-right">
                  <span className="daohua-source-aside-amount">
                    × {g.amount || 1}
                  </span>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* 5. 储物袋/福袋开启 */}
      {sources.packs.length > 0 && (
        <div className="daohua-source-aside-group">
          <div className="daohua-source-aside-group-title">
            <span className="daohua-source-aside-badge daohua-source-aside-badge--amber">
              {t('game.itemSources.categoryPacks')}
            </span>
          </div>
          <div className="daohua-source-aside-item-list">
            {sources.packs.map((p, idx) => (
              <div className="daohua-source-aside-row" key={`${p.packItemId}-${idx}`}>
                <div className="daohua-source-aside-row-left">
                  <span className="daohua-source-aside-name">{p.packName}</span>
                </div>
                <div className="daohua-source-aside-row-right">
                  <span className="daohua-source-aside-rate">{p.chanceText}</span>
                  <span className="daohua-source-aside-amount">
                    {p.quantityText.startsWith('×') ? p.quantityText : `× ${p.quantityText}`}
                  </span>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * 生成「获取途径」独立副卡片 (Aside Card)
 *
 * 若该物品没有任何探明的产出来源，返回 null（不渲染空卡片）；
 * 若有产出来源，构造带修仙微光胶囊与结构化排版的 Aside Card。
 */
export function resolveItemSourcesAsideCard(
  itemId: string,
  _itemDef?: ItemDefinition,
  _eqDef?: CombatEquipmentDefinition
): FloatingAsideCard | null {
  const sources = resolveItemSources(itemId)

  const hasAnySources =
    sources.generals.length > 0 ||
    sources.monsters.length > 0 ||
    sources.crafting.length > 0 ||
    sources.gatherings.length > 0 ||
    sources.packs.length > 0

  if (!hasAnySources) {
    return null
  }

  const t = getT()

  // 计算渠道总数
  let routeCount = 0
  if (sources.generals.length > 0) routeCount += sources.generals.length
  if (sources.monsters.length > 0) routeCount += 1
  if (sources.crafting.length > 0) routeCount += sources.crafting.length
  if (sources.gatherings.length > 0) routeCount += sources.gatherings.length
  if (sources.packs.length > 0) routeCount += sources.packs.length

  return {
    id: `aside-item-sources-${itemId}`,
    mark: t('game.itemSources.mark'),
    title: t('game.itemSources.title'),
    subtitle: t('game.itemSources.routesCount', { count: routeCount }),
    tone: 'jade',
    content: <ItemSourcesAsideContent sources={sources} />,
  }
}
