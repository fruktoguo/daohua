import React, { memo, useMemo } from 'react'

import { useGameStore } from '../game/gameStore'
import {
  findItemInCatalog,
  resolveActionLevel,
  resolveActionRarity,
  resolveItemCategoryLabel,
  resolveItemGrade,
  resolveItemSubCategory,
} from '../game/ids'
import type {
  ActionDefinition,
  BuildingDefinition,
  CombatAbilityDefinition,
  CombatantDefinition,
  CombatEncounterDefinition,
  CombatEquipmentDefinition,
  ItemDefinition,
} from '../game/types'
import { useT } from '../i18n'
import type { CodexCraftingSubTab } from '../uiStore'
import { useUiStore } from '../uiStore'
import { CharacterAvatar } from './CharacterAvatar'
import { FloatingInfoCard, FloatingPopover } from './FloatingPopover'
import { ItemTile } from './ItemDisplay'

/** 制造分类映射定义 */
const CRAFTING_SUB_TABS: ReadonlyArray<{ id: CodexCraftingSubTab; labelKey: string }> = [
  { id: 'alchemy', labelKey: 'craftingAlchemy' },
  { id: 'forging', labelKey: 'craftingForging' },
  { id: 'formation', labelKey: 'craftingFormation' },
  { id: 'talisman', labelKey: 'craftingTalisman' },
]

/**
 * 万象图鉴页面组件
 * 对应主界面顶级页签 'codex'
 * 包含四大分类：制造配方、建筑图谱、功法神通、大世界妖兽
 */
export const CodexPage = memo(function CodexPage(): React.JSX.Element {
  const t = useT()

  const codexCategory = useUiStore(state => state.codexCategory)
  const setCodexCategory = useUiStore(state => state.setCodexCategory)
  const codexCraftingSubTab = useUiStore(state => state.codexCraftingSubTab)
  const setCodexCraftingSubTab = useUiStore(state => state.setCodexCraftingSubTab)

  const stats = useGameStore(state => state.stats)
  const realmLevel = stats.realmLevel ?? 1

  const actions = useGameStore(state => state.actions)
  const buildings = useGameStore(state => state.buildings)
  const abilities = useGameStore(state => state.abilities)
  const encounters = useGameStore(state => state.encounters)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const learnedAbilityIds = useGameStore(state => state.combat?.learnedAbilityIds ?? [])
  // 1. 制造配方分类池
  const craftingPool = useMemo(() => {
    const pool = actions.filter(a =>
      a.type === 'alchemy' || a.id.startsWith('craft.') || a.id.startsWith('forge.') || a.id.startsWith('alchemy.')
    )
    if (codexCraftingSubTab === 'alchemy') {
      return pool.filter(a =>
        a.technique === 'alchemy' ||
        a.id.startsWith('alchemy.') ||
        a.id.startsWith('craft.extract-')
      )
    }
    if (codexCraftingSubTab === 'forging') {
      return pool.filter(a =>
        a.technique === 'forging' ||
        a.id.startsWith('forge.') ||
        a.id.startsWith('craft.refine-')
      )
    }
    if (codexCraftingSubTab === 'formation') {
      return pool.filter(a => a.technique === 'formation')
    }
    if (codexCraftingSubTab === 'talisman') {
      return pool.filter(a => a.technique === 'talisman')
    }
    return pool
  }, [actions, codexCraftingSubTab])

  // 制造配方按所需境界排序
  const sortedCraftingActions = useMemo(() => {
    return [...craftingPool].sort((a, b) => {
      const levelA = resolveActionLevel(a, items, equipment, encounters)
      const levelB = resolveActionLevel(b, items, equipment, encounters)
      if (levelA !== levelB) return levelA - levelB
      return a.id.localeCompare(b.id)
    })
  }, [craftingPool, items, equipment, encounters])

  // 2. 建筑图谱按 requiredRealmLevel 排序
  const sortedBuildings = useMemo(() => {
    return [...buildings].sort((a, b) => a.requiredRealmLevel - b.requiredRealmLevel)
  }, [buildings])

  // 3. 功法神通按等阶与 ID 排序
  const sortedAbilities = useMemo(() => {
    return [...abilities].sort((a, b) => ((a.tier ?? 1) - (b.tier ?? 1)) || a.abilityId.localeCompare(b.abilityId))
  }, [abilities])
  // 4. 从遭遇战全量聚合去重的大世界怪物列表
  const allMonsters = useMemo(() => {
    const map = new Map<string, CombatantDefinition>()
    for (const enc of encounters) {
      if (enc.enemy?.enemyId) map.set(enc.enemy.enemyId, enc.enemy)
      for (const spawn of enc.randomSpawns ?? []) {
        if (spawn.enemy?.enemyId) map.set(spawn.enemy.enemyId, spawn.enemy)
      }
      for (const boss of enc.bossEnemies ?? []) {
        if (boss?.enemyId) map.set(boss.enemyId, boss)
      }
    }
    return Array.from(map.values()).sort((a, b) => (a.level ?? 1) - (b.level ?? 1))
  }, [encounters])

  // 进度计数
  const defeatedSet = useMemo(() => new Set(stats.defeatedMonsterIds ?? []), [stats.defeatedMonsterIds])
  const learnedSet = useMemo(() => new Set(learnedAbilityIds), [learnedAbilityIds])

  const craftingUnlockedCount = useMemo(() => {
    return sortedCraftingActions.filter(a => resolveActionLevel(a, items, equipment, encounters) <= realmLevel).length
  }, [sortedCraftingActions, items, equipment, encounters, realmLevel])

  const buildingUnlockedCount = useMemo(() => {
    return sortedBuildings.filter(b => b.requiredRealmLevel <= realmLevel).length
  }, [sortedBuildings, realmLevel])

  const abilityLearnedCount = useMemo(() => {
    return sortedAbilities.filter(a => learnedSet.has(a.abilityId)).length
  }, [sortedAbilities, learnedSet])

  const monsterDefeatedCount = useMemo(() => {
    return allMonsters.filter(m => defeatedSet.has(m.enemyId)).length
  }, [allMonsters, defeatedSet])

  // 当前分类总数与已解锁数
  const [currentUnlocked, currentTotal] = useMemo(() => {
    switch (codexCategory) {
      case 'crafting':
        return [craftingUnlockedCount, sortedCraftingActions.length]
      case 'building':
        return [buildingUnlockedCount, sortedBuildings.length]
      case 'ability':
        return [abilityLearnedCount, sortedAbilities.length]
      case 'monster':
        return [monsterDefeatedCount, allMonsters.length]
    }
  }, [codexCategory, craftingUnlockedCount, sortedCraftingActions.length, buildingUnlockedCount, sortedBuildings.length, abilityLearnedCount, sortedAbilities.length, monsterDefeatedCount, allMonsters.length])

  const currentPercent = currentTotal > 0 ? Math.round((currentUnlocked / currentTotal) * 100) : 0

  return (
    <div className="codex-page panel">
      {/* 顶栏与全景收集度统计 */}
      <header className="codex-header">
        <div className="codex-hero-row">
          <div className="codex-title-group">
            <h2 className="codex-title">{t('app.codex.title')}</h2>
            <span className="codex-progress-badge">
              {t('app.codex.totalProgress')}：{currentUnlocked} / {currentTotal} ({currentPercent}%)
            </span>
          </div>
        </div>

        {/* 四大主分类切换 */}
        <nav className="codex-tabs" role="tablist">
          <button
            className={`codex-tab-btn${codexCategory === 'crafting' ? ' active' : ''}`}
            onClick={() => setCodexCategory('crafting')}
            type="button"
          >
            <span>{t('app.codex.tabCrafting')}</span>
            <span className="codex-tab-count">{craftingUnlockedCount}/{sortedCraftingActions.length}</span>
          </button>
          <button
            className={`codex-tab-btn${codexCategory === 'building' ? ' active' : ''}`}
            onClick={() => setCodexCategory('building')}
            type="button"
          >
            <span>{t('app.codex.tabBuilding')}</span>
            <span className="codex-tab-count">{buildingUnlockedCount}/{sortedBuildings.length}</span>
          </button>
          <button
            className={`codex-tab-btn${codexCategory === 'ability' ? ' active' : ''}`}
            onClick={() => setCodexCategory('ability')}
            type="button"
          >
            <span>{t('app.codex.tabAbility')}</span>
            <span className="codex-tab-count">{abilityLearnedCount}/{sortedAbilities.length}</span>
          </button>
          <button
            className={`codex-tab-btn${codexCategory === 'monster' ? ' active' : ''}`}
            onClick={() => setCodexCategory('monster')}
            type="button"
          >
            <span>{t('app.codex.tabMonster')}</span>
            <span className="codex-tab-count">{monsterDefeatedCount}/{allMonsters.length}</span>
          </button>
        </nav>

        {/* 制造分类下的二级子切换（炼丹、炼器、阵法、符箓） */}
        {codexCategory === 'crafting' && (
          <div className="codex-sub-tabs" role="tablist">
            {CRAFTING_SUB_TABS.map(tab => (
              <button
                className={`codex-sub-tab-btn${codexCraftingSubTab === tab.id ? ' active' : ''}`}
                key={tab.id}
                onClick={() => setCodexCraftingSubTab(tab.id)}
                type="button"
              >
                {t(`app.codex.${tab.labelKey}`)}
              </button>
            ))}
          </div>
        )}
      </header>

      {/* 主体图鉴内容 */}
      <main className="codex-content">
        {codexCategory === 'crafting' && (
          <div className="codex-grid codex-tile-grid">
            {sortedCraftingActions.map(action => (
              <CodexCraftingEntry
                action={action}
                encounters={encounters}
                equipment={equipment}
                items={items}
                key={action.id}
                realmLevel={realmLevel}
              />
            ))}
            {sortedCraftingActions.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}

        {codexCategory === 'building' && (
          <div className="codex-grid codex-grid--wide">
            {sortedBuildings.map(building => (
              <CodexBuildingEntry
                building={building}
                key={building.buildingId}
                realmLevel={realmLevel}
              />
            ))}
            {sortedBuildings.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}

        {codexCategory === 'ability' && (
          <div className="codex-grid codex-tile-grid">
            {sortedAbilities.map(ability => (
              <CodexAbilityEntry
                ability={ability}
                isLearned={learnedSet.has(ability.abilityId)}
                key={ability.abilityId}
              />
            ))}
            {sortedAbilities.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}

        {codexCategory === 'monster' && (
          <div className="codex-grid codex-grid--wide">
            {allMonsters.map(monster => (
              <CodexMonsterEntry
                isDefeated={defeatedSet.has(monster.enemyId)}
                key={monster.enemyId}
                monster={monster}
              />
            ))}
            {allMonsters.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}
      </main>
    </div>
  )
})

/**
 * 制造配方条目渲染
 */
const CodexCraftingEntry = memo(function CodexCraftingEntry({
  action,
  realmLevel,
  items,
  equipment,
  encounters,
}: {
  action: ActionDefinition
  realmLevel: number
  items: readonly ItemDefinition[]
  equipment: readonly CombatEquipmentDefinition[]
  encounters: readonly CombatEncounterDefinition[]
}): React.JSX.Element {
  const t = useT()
  const requiredLevel = resolveActionLevel(action, items, equipment, encounters)
  const isUnlocked = realmLevel >= requiredLevel

  // 首选产出物品
  const outputYield = action.yields?.find(y => y.kind === 'item' && y.id)
  const outputItemId = outputYield?.id ?? action.id
  const outputItemDef = findItemInCatalog(items, outputItemId)
  const outputEqDef = equipment.find(e => e.equipmentId === outputItemId)

  const categoryLabel = resolveItemCategoryLabel(
    resolveItemSubCategory(outputItemId, outputItemDef, outputEqDef),
    t
  )

  const inputsDesc = Object.entries(action.inputs ?? {})
    .map(([id, qty]) => `${findItemInCatalog(items, id)?.name ?? id} ×${qty}`)
    .join('、')

  const hoverContent = (
    <FloatingInfoCard
      badge={isUnlocked ? t('app.codex.unlocked') : t('app.codex.locked')}
      badgeTone={isUnlocked ? 'jade' : 'muted'}
      description={
        <div>
          {inputsDesc && (
            <div style={{ marginBottom: 4 }}>
              <strong>{t('app.codex.requiresInputs')}：</strong>
              <span>{inputsDesc}</span>
            </div>
          )}
          {action.description && <div>{action.description}</div>}
          <div
            className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
              isUnlocked ? 'unlocked' : 'locked'
            }`}
          >
            {t('app.codex.conditionLevel', { level: requiredLevel })}
          </div>
        </div>
      }
      subtitle={`${action.name} · 耗时 ${action.durationSeconds}s`}
      title={action.name}
    />
  )

  return (
    <FloatingPopover content={hoverContent} placement="top" size="medium">
      <div
        className={`codex-entry-wrapper codex-tile-anchor${
          isUnlocked ? '' : ' codex-entry-wrapper--locked'
        }`}
      >
        <ItemTile
          category={categoryLabel}
          className="inventory-cell"
          itemId={outputItemId}
          level={requiredLevel}
          name={action.name}
          rarity={resolveActionRarity(action, items, equipment, encounters)}
        />
        {!isUnlocked && (
          <span className="codex-lock-badge" title={t('app.codex.locked')}>
            🔒
          </span>
        )}
      </div>
    </FloatingPopover>
  )
})

/**
 * 建筑图谱条目渲染
 */
const CodexBuildingEntry = memo(function CodexBuildingEntry({
  building,
  realmLevel,
}: {
  building: BuildingDefinition
  realmLevel: number
}): React.JSX.Element {
  const t = useT()
  const isUnlocked = realmLevel >= building.requiredRealmLevel

  const groupLabel =
    building.group === 'production'
      ? '生产'
      : building.group === 'combat'
        ? '战斗'
        : '其他'

  const materialsDesc = (building.materials ?? [])
    .map(m => `${m.itemId} ×${m.quantity}`)
    .join('、')

  const hoverContent = (
    <FloatingInfoCard
      badge={isUnlocked ? t('app.codex.unlocked') : t('app.codex.locked')}
      badgeTone={isUnlocked ? 'jade' : 'muted'}
      description={
        <div>
          <div style={{ marginBottom: 4 }}>{building.description}</div>
          <div style={{ marginBottom: 4 }}>
            <strong>{t('app.codex.buildingEffect')}：</strong>
            <span>{building.effect}</span>
          </div>
          {materialsDesc && (
            <div style={{ marginBottom: 4 }}>
              <strong>{t('app.codex.buildingMaterials')}：</strong>
              <span>{materialsDesc}</span>
            </div>
          )}
          <div
            className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
              isUnlocked ? 'unlocked' : 'locked'
            }`}
          >
            {t('app.codex.conditionBuildingLevel', { level: building.requiredRealmLevel })}
          </div>
        </div>
      }
      subtitle={`公共设施 · ${groupLabel}`}
      title={building.name}
    />
  )

  return (
    <FloatingPopover content={hoverContent} placement="top" size="medium">
      <div
        className={`codex-wide-card${
          isUnlocked ? '' : ' codex-wide-card--locked'
        }`}
      >
        <div className="codex-card-avatar-box">
          <span style={{ fontSize: 20 }}>🏛️</span>
          {!isUnlocked && (
            <span className="codex-lock-badge" title={t('app.codex.locked')}>
              🔒
            </span>
          )}
        </div>
        <div className="codex-card-info">
          <div className="codex-card-name-row">
            <span className="codex-card-name">{building.name}</span>
            <span className="codex-card-level">Lv.{building.requiredRealmLevel}</span>
          </div>
          <span className="codex-card-sub">{building.effect}</span>
        </div>
      </div>
    </FloatingPopover>
  )
})

/**
 * 功法神通条目渲染
 */
const CodexAbilityEntry = memo(function CodexAbilityEntry({
  ability,
  isLearned,
}: {
  ability: CombatAbilityDefinition
  isLearned: boolean
}): React.JSX.Element {
  const t = useT()

  const hoverContent = (
    <FloatingInfoCard
      badge={isLearned ? t('app.codex.learned') : t('app.codex.unlearned')}
      badgeTone={isLearned ? 'qi' : 'muted'}
      description={
        <div>
          {ability.description && <div style={{ marginBottom: 4 }}>{ability.description}</div>}
          <div style={{ fontSize: '0.8rem', opacity: 0.85, marginBottom: 4 }}>
            {t('app.codex.cooldown')}：{(ability.cooldownMilliseconds ?? 0) / 1000}s
            {ability.castMilliseconds ? ` · ${t('app.codex.castTime')}：${ability.castMilliseconds / 1000}s` : ''}
            {ability.manaCost ? ` · ${t('app.codex.manaCost')}：${ability.manaCost}` : ''}
          </div>
          <div
            className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
              isLearned ? 'unlocked' : 'locked'
            }`}
          >
            {t('app.codex.conditionAbilityLearn')}
          </div>
        </div>
      }
      subtitle={`等阶 ${ability.tier} 阶 · ${ability.category === 'spell' ? '术法' : '物理'}`}
      title={ability.name}
    />
  )

  return (
    <FloatingPopover content={hoverContent} placement="top" size="medium">
      <div
        className={`codex-entry-wrapper codex-tile-anchor${
          isLearned ? '' : ' codex-entry-wrapper--locked'
        }`}
      >
        <ItemTile
          category={ability.category === 'spell' ? '术法' : '武技'}
          className="inventory-cell"
          itemId={ability.abilityId}
          level={ability.tier ?? 1}
          name={ability.name}
          rarity={resolveItemGrade(ability.tier ?? 1).rarity}
        />
        {!isLearned && (
          <span className="codex-lock-badge" title={t('app.codex.unlearned')}>
            🔒
          </span>
        )}
      </div>
    </FloatingPopover>
  )
})

/**
 * 大世界妖兽条目渲染
 */
const CodexMonsterEntry = memo(function CodexMonsterEntry({
  monster,
  isDefeated,
}: {
  monster: CombatantDefinition
  isDefeated: boolean
}): React.JSX.Element {
  const t = useT()

  const rankLabel =
    monster.rank === 'boss'
      ? '妖王'
      : monster.rank === 'elite'
        ? '精英'
        : '妖兽'

  const dropsDesc = (monster.drops ?? [])
    .map(d => `${d.itemId} (${Math.round(d.dropChance * 100)}%)`)
    .join('、')

  const hoverContent = (
    <FloatingInfoCard
      badge={isDefeated ? t('app.codex.defeated') : t('app.codex.undefeated')}
      badgeTone={isDefeated ? 'danger' : 'muted'}
      description={
        <div>
          {isDefeated ? (
            <>
              <div style={{ fontSize: '0.82rem', marginBottom: 4 }}>
                气血：{monster.attributes?.maxHitpoints ?? 0} · 攻击：{monster.attributes?.physicalAttack || monster.attributes?.spellAttack || 0}
              </div>
              {dropsDesc && (
                <div style={{ fontSize: '0.8rem', opacity: 0.85, marginBottom: 4 }}>
                  掉落：{dropsDesc}
                </div>
              )}
            </>
          ) : (
            <div style={{ marginBottom: 4 }}>{t('app.codex.unknownMonsterDesc')}</div>
          )}
          <div
            className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
              isDefeated ? 'unlocked' : 'locked'
            }`}
          >
            {t('app.codex.conditionMonsterDefeat')}
          </div>
        </div>
      }
      subtitle={isDefeated ? `Lv.${monster.level ?? 1} · ${rankLabel}` : '？？？'}
      title={isDefeated ? monster.name : t('app.codex.unknownMonster')}
    />
  )

  return (
    <FloatingPopover content={hoverContent} placement="top" size="medium">
      <div
        className={`codex-wide-card${
          isDefeated ? '' : ' codex-wide-card--locked'
        }`}
      >
        <div className="codex-card-avatar-box">
          {isDefeated ? (
            <CharacterAvatar
              fallback={monster.name.slice(0, 1)}
              isFlipped
              visual={monster.visual}
            />
          ) : (
            <span style={{ fontSize: 20, filter: 'blur(1px)' }}>❓</span>
          )}
          {!isDefeated && (
            <span className="codex-lock-badge" title={t('app.codex.undefeated')}>
              🔒
            </span>
          )}
        </div>
        <div className="codex-card-info">
          <div className="codex-card-name-row">
            <span className="codex-card-name">
              {isDefeated ? monster.name : t('app.codex.unknownMonster')}
            </span>
            <span className="codex-card-level">
              {isDefeated ? `Lv.${monster.level ?? 1}` : 'Lv.???'}
            </span>
          </div>
          <span className="codex-card-sub">
            {isDefeated ? rankLabel : '？？？'}
          </span>
        </div>
      </div>
    </FloatingPopover>
  )
})
