import React, { memo, useCallback, useMemo } from 'react'

import { isMaterialSubTabItem, isMaterialUnlocked } from '../game/codexCalculations'
import { calculateRealmInfo } from '../game/combatConstants'
import { describeAbility, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import {
  convertTierToLevel,
  findItemInCatalog,
  resolveActionLevel,
  resolveItemCategoryLabel,
  resolveItemGrade,
  resolveItemMetadata,
  resolveItemSubCategory,
  type ItemMetadata,
} from '../game/ids'
import { resolveItemIconUrl } from '../game/itemIcons'
import type {
  ActionDefinition,
  BuildingDefinition,
  CombatAbilityDefinition,
  CombatantDefinition,
  CombatEncounterDefinition,
  CombatEquipmentDefinition,
  ItemDefinition,
} from '../game/types'
import { resolveBuildingIconUrl } from '../game/buildingIcons'
import { useT } from '../i18n'
import type { CodexCraftingSubTab, CodexMaterialSubTab } from '../uiStore'
import { useUiStore } from '../uiStore'
import { PillBonusLabel, type PillTone } from './PillLabel'
import { RootAspectRadarChart } from './RootAspectRadarChart'
import { CharacterAvatar } from './CharacterAvatar'
import { FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import { MonsterTooltipTarget } from './MonsterDisplay'
import { createInternalRootExpAsideCard } from './EffectTooltip'
import {
  describeEquipmentEffects,
  describeItemEffects,
  ItemTile,
  rarityToTone,
  resolveItemTooltipAsideCards,
} from './ItemDisplay'
import { ValueText } from './ValueText'

/** 材料分类映射定义 */
const MATERIAL_SUB_TABS: ReadonlyArray<{ id: CodexMaterialSubTab; labelKey: string }> = [
  { id: 'herb', labelKey: 'materialHerb' },
  { id: 'mineral', labelKey: 'materialMineral' },
  { id: 'exotic', labelKey: 'materialExotic' },
]

/** 制造分类映射定义 */
const CRAFTING_SUB_TABS: ReadonlyArray<{ id: CodexCraftingSubTab; labelKey: string }> = [
  { id: 'alchemy', labelKey: 'craftingAlchemy' },
  { id: 'forging', labelKey: 'craftingForging' },
  { id: 'formation', labelKey: 'craftingFormation' },
  { id: 'talisman', labelKey: 'craftingTalisman' },
]

/**
 * 格式化角色境界解锁文案：
 * - 1 级及以下：默认解锁
 * - 18 级特例：境界达到 lv18[叩天门] 解锁
 * - 其余等级：境界达到 lv{level}[{realmName}] 解锁
 */
function formatRealmUnlockCondition(level: number): string {
  if (level <= 1) return '默认解锁'
  if (level === 18) return '境界达到 lv18[叩天门] 解锁'
  const info = calculateRealmInfo(0, level)
  const name = level <= 18 ? info.minorName : `${info.stageName}${info.minorName}`
  return `境界达到 lv${level}[${name}] 解锁`
}

/**
 * 万象图鉴页面组件
 * 对应主界面顶级页签 'codex'
 * 包含四大分类：制造配方、建筑图谱、功法神通、大世界妖兽
 */
export const CodexPage = memo(function CodexPage(): React.JSX.Element {
  const t = useT()

  const codexCategory = useUiStore(state => state.codexCategory)
  const setCodexCategory = useUiStore(state => state.setCodexCategory)
  const codexMaterialSubTab = useUiStore(state => state.codexMaterialSubTab)
  const setCodexMaterialSubTab = useUiStore(state => state.setCodexMaterialSubTab)
  const codexCraftingSubTab = useUiStore(state => state.codexCraftingSubTab)
  const setCodexCraftingSubTab = useUiStore(state => state.setCodexCraftingSubTab)

  const stats = useGameStore(state => state.stats)
  const realmLevel = stats.realmLevel ?? 1

  const actions = useGameStore(state => state.actions)
  const buildings = useGameStore(state => state.buildings)
  const abilities = useGameStore(state => state.abilities)
  const encounters = useGameStore(state => state.encounters)
  const maps = useGameStore(state => state.maps)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const inventory = useGameStore(state => state.inventory)
  const learnedAbilityIds = useGameStore(state => state.combat?.learnedAbilityIds ?? [])

  // 0. 天地材料分类池：全量材料池（药材、矿石、异材）
  const allMaterials = useMemo(() => {
    return items.filter(it => it.category === 'material')
  }, [items])

  const isItemUnlocked = useCallback(
    (item: ItemDefinition): boolean => isMaterialUnlocked(item, realmLevel, inventory),
    [realmLevel, inventory]
  )

  const currentSubTabMaterials = useMemo(() => {
    return allMaterials.filter(it => isMaterialSubTabItem(it, codexMaterialSubTab))
  }, [allMaterials, codexMaterialSubTab])


  const sortedMaterials = useMemo(() => {
    return [...currentSubTabMaterials].sort((a, b) => {
      const levelA = convertTierToLevel(a.tier ?? 1)
      const levelB = convertTierToLevel(b.tier ?? 1)
      if (levelA !== levelB) return levelA - levelB
      return a.itemId.localeCompare(b.itemId)
    })
  }, [currentSubTabMaterials])

  const materialSubTabProgress = useMemo(() => {
    const map: Record<CodexMaterialSubTab, { unlocked: number; total: number }> = {
      herb: { unlocked: 0, total: 0 },
      mineral: { unlocked: 0, total: 0 },
      exotic: { unlocked: 0, total: 0 },
    }
    for (const item of allMaterials) {
      const isUnlocked = isItemUnlocked(item)
      for (const tab of MATERIAL_SUB_TABS) {
        if (isMaterialSubTabItem(item, tab.id)) {
          map[tab.id].total += 1
          if (isUnlocked) {
            map[tab.id].unlocked += 1
          }
        }
      }
    }
    return map
  }, [allMaterials, isItemUnlocked])

  const totalMaterialUnlockedCount = useMemo(() => {
    return allMaterials.filter(isItemUnlocked).length
  }, [allMaterials, isItemUnlocked])
  const totalMaterialCount = allMaterials.length

  // 1. 制造配方分类池：全量技艺配方池（包含炼丹、炼器、阵法、符箓）
  const allCraftingActions = useMemo(() => {
    return actions.filter(a =>
      a.type === 'alchemy' ||
      a.technique === 'alchemy' ||
      a.technique === 'forging' ||
      a.technique === 'formation' ||
      a.technique === 'talisman' ||
      a.id.startsWith('craft.') ||
      a.id.startsWith('forge.') ||
      a.id.startsWith('alchemy.') ||
      a.id.startsWith('formation.') ||
      a.id.startsWith('talisman.')
    )
  }, [actions])

  // 子分类判定函数
  const isSubTabAction = useCallback((action: ActionDefinition, subTab: CodexCraftingSubTab): boolean => {
    if (subTab === 'alchemy') {
      return (
        action.technique === 'alchemy' ||
        action.id.startsWith('alchemy.') ||
        action.id.startsWith('craft.extract-')
      )
    }
    if (subTab === 'forging') {
      return (
        action.technique === 'forging' ||
        action.id.startsWith('forge.') ||
        action.id.startsWith('craft.refine-')
      )
    }
    if (subTab === 'formation') {
      return action.technique === 'formation' || action.id.startsWith('formation.')
    }
    if (subTab === 'talisman') {
      return action.technique === 'talisman' || action.id.startsWith('talisman.')
    }
    return false
  }, [])

  // 当前选中子分类的配方池
  const currentSubTabActions = useMemo(() => {
    return allCraftingActions.filter(a => isSubTabAction(a, codexCraftingSubTab))
  }, [allCraftingActions, isSubTabAction, codexCraftingSubTab])

  // 制造配方按所需境界排序（当前展示列表）
  const sortedCraftingActions = useMemo(() => {
    return [...currentSubTabActions].sort((a, b) => {
      const levelA = resolveActionLevel(a, items, equipment, encounters)
      const levelB = resolveActionLevel(b, items, equipment, encounters)
      if (levelA !== levelB) return levelA - levelB
      return a.id.localeCompare(b.id)
    })
  }, [currentSubTabActions, items, equipment, encounters])

  // 各子 Tab 独立的解锁与总数统计
  const subTabProgress = useMemo(() => {
    const map: Record<CodexCraftingSubTab, { unlocked: number; total: number }> = {
      alchemy: { unlocked: 0, total: 0 },
      forging: { unlocked: 0, total: 0 },
      formation: { unlocked: 0, total: 0 },
      talisman: { unlocked: 0, total: 0 },
    }
    for (const action of allCraftingActions) {
      const isUnlocked = resolveActionLevel(action, items, equipment, encounters) <= realmLevel
      for (const tab of CRAFTING_SUB_TABS) {
        if (isSubTabAction(action, tab.id)) {
          map[tab.id].total += 1
          if (isUnlocked) {
            map[tab.id].unlocked += 1
          }
        }
      }
    }
    return map
  }, [allCraftingActions, isSubTabAction, items, equipment, encounters, realmLevel])

  // 2. 建筑图谱按 requiredRealmLevel 排序
  const sortedBuildings = useMemo(() => {
    return [...buildings].sort((a, b) => a.requiredRealmLevel - b.requiredRealmLevel)
  }, [buildings])

  // 3. 功法神通按等阶与 ID 排序
  const sortedAbilities = useMemo(() => {
    return [...abilities].sort((a, b) => ((a.tier ?? 1) - (b.tier ?? 1)) || a.abilityId.localeCompare(b.abilityId))
  }, [abilities])

  // 4. 从遭遇战与地图节点生态全量聚合去重大世界怪物列表
  const allMonsters = useMemo(() => {
    const map = new Map<string, CombatantDefinition>()
    const addMonster = (enemy?: CombatantDefinition | null) => {
      if (enemy?.enemyId && !map.has(enemy.enemyId)) {
        map.set(enemy.enemyId, enemy)
      }
    }
    for (const enc of encounters) {
      addMonster(enc.enemy)
      for (const spawn of enc.randomSpawns ?? []) {
        addMonster(spawn.enemy)
      }
      for (const elite of enc.eliteEnemies ?? []) {
        addMonster(elite)
      }
      for (const boss of enc.bossEnemies ?? []) {
        addMonster(boss)
      }
    }
    for (const mapDef of maps) {
      for (const node of mapDef.nodes ?? []) {
        for (const m of node.monsters ?? []) addMonster(m)
        for (const e of node.eliteEnemies ?? []) addMonster(e)
        for (const b of node.bossEnemies ?? []) addMonster(b)
      }
    }
    return Array.from(map.values()).sort((a, b) => (a.level ?? 1) - (b.level ?? 1))
  }, [encounters, maps])

  // 进度计数
  const defeatedSet = useMemo(() => new Set(stats.defeatedMonsterIds ?? []), [stats.defeatedMonsterIds])
  const learnedSet = useMemo(() => new Set(learnedAbilityIds), [learnedAbilityIds])

  // 技艺总解锁数与总配方数（全量技艺，不随子 Tab 改变）
  const totalCraftingUnlockedCount = useMemo(() => {
    return allCraftingActions.filter(a => resolveActionLevel(a, items, equipment, encounters) <= realmLevel).length
  }, [allCraftingActions, items, equipment, encounters, realmLevel])
  const totalCraftingCount = allCraftingActions.length

  const buildingUnlockedCount = useMemo(() => {
    return sortedBuildings.filter(b => b.requiredRealmLevel <= realmLevel).length
  }, [sortedBuildings, realmLevel])
  const totalBuildingCount = sortedBuildings.length

  const abilityLearnedCount = useMemo(() => {
    return sortedAbilities.filter(a => learnedSet.has(a.abilityId)).length
  }, [sortedAbilities, learnedSet])
  const totalAbilityCount = sortedAbilities.length

  const monsterDefeatedCount = useMemo(() => {
    return allMonsters.filter(m => defeatedSet.has(m.enemyId)).length
  }, [allMonsters, defeatedSet])
  const totalMonsterCount = allMonsters.length

  // 万象图鉴全景总进度
  const grandUnlocked = totalMaterialUnlockedCount + totalCraftingUnlockedCount + buildingUnlockedCount + abilityLearnedCount + monsterDefeatedCount
  const grandTotal = totalMaterialCount + totalCraftingCount + totalBuildingCount + totalAbilityCount + totalMonsterCount
  const grandPercent = grandTotal > 0 ? Math.round((grandUnlocked / grandTotal) * 100) : 0

  return (
    <div className="codex-page panel">
      {/* 顶栏与全景收集度统计 */}
      <header className="codex-header">
        <div className="codex-hero-row">
          <div className="codex-title-group">
            <h2 className="codex-title">{t('app.codex.title')}</h2>
            <span className="codex-progress-badge">
              {t('app.codex.totalProgress')}：{grandUnlocked} / {grandTotal} ({grandPercent}%)
            </span>
          </div>
        </div>

        {/* 五大主分类切换：材料居首 */}
        <nav className="codex-tabs" role="tablist">
          <button
            className={`codex-tab-btn${codexCategory === 'material' ? ' active' : ''}`}
            onClick={() => setCodexCategory('material')}
            type="button"
          >
            <span>{t('app.codex.tabMaterial')}</span>
            <span className="codex-tab-count">{totalMaterialUnlockedCount}/{totalMaterialCount}</span>
          </button>
          <button
            className={`codex-tab-btn${codexCategory === 'crafting' ? ' active' : ''}`}
            onClick={() => setCodexCategory('crafting')}
            type="button"
          >
            <span>{t('app.codex.tabCrafting')}</span>
            <span className="codex-tab-count">{totalCraftingUnlockedCount}/{totalCraftingCount}</span>
          </button>
          <button
            className={`codex-tab-btn${codexCategory === 'building' ? ' active' : ''}`}
            onClick={() => setCodexCategory('building')}
            type="button"
          >
            <span>{t('app.codex.tabBuilding')}</span>
            <span className="codex-tab-count">{buildingUnlockedCount}/{totalBuildingCount}</span>
          </button>
          <button
            className={`codex-tab-btn${codexCategory === 'ability' ? ' active' : ''}`}
            onClick={() => setCodexCategory('ability')}
            type="button"
          >
            <span>{t('app.codex.tabAbility')}</span>
            <span className="codex-tab-count">{abilityLearnedCount}/{totalAbilityCount}</span>
          </button>
          <button
            className={`codex-tab-btn${codexCategory === 'monster' ? ' active' : ''}`}
            onClick={() => setCodexCategory('monster')}
            type="button"
          >
            <span>{t('app.codex.tabMonster')}</span>
            <span className="codex-tab-count">{monsterDefeatedCount}/{totalMonsterCount}</span>
          </button>
        </nav>

        {/* 材料分类下的二级子切换（药材、矿石、异材） */}
        {codexCategory === 'material' && (
          <div className="codex-sub-tabs" role="tablist">
            {MATERIAL_SUB_TABS.map(tab => {
              const prog = materialSubTabProgress[tab.id]
              return (
                <button
                  className={`codex-sub-tab-btn${codexMaterialSubTab === tab.id ? ' active' : ''}`}
                  key={tab.id}
                  onClick={() => setCodexMaterialSubTab(tab.id)}
                  type="button"
                >
                  <span>{t(`app.codex.${tab.labelKey}`)}</span>
                  <span className="codex-sub-tab-count">{prog.unlocked}/{prog.total}</span>
                </button>
              )
            })}
          </div>
        )}

        {/* 制造分类下的二级子切换（炼丹、炼器、阵法、符箓） */}
        {codexCategory === 'crafting' && (
          <div className="codex-sub-tabs" role="tablist">
            {CRAFTING_SUB_TABS.map(tab => {
              const prog = subTabProgress[tab.id]
              return (
                <button
                  className={`codex-sub-tab-btn${codexCraftingSubTab === tab.id ? ' active' : ''}`}
                  key={tab.id}
                  onClick={() => setCodexCraftingSubTab(tab.id)}
                  type="button"
                >
                  <span>{t(`app.codex.${tab.labelKey}`)}</span>
                  <span className="codex-sub-tab-count">{prog.unlocked}/{prog.total}</span>
                </button>
              )
            })}
          </div>
        )}
      </header>

      {/* 主体图鉴内容 */}
      <main className="codex-content">
        {codexCategory === 'material' && (
          <div className="codex-grid codex-tile-grid">
            {sortedMaterials.map(item => (
              <CodexMaterialEntry
                isUnlocked={isItemUnlocked(item)}
                item={item}
                key={item.itemId}
              />
            ))}
            {sortedMaterials.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}
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
                items={items}
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
                abilities={abilities}
                isDefeated={defeatedSet.has(monster.enemyId)}
                items={items}
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
 * 材料条目渲染
 * 悬浮卡复用标准物品卡片格式，包含：
 * 1. 顶部：等阶、品类（药材/矿石/异材）、售价与收录/解锁条件
 * 2. 主体：材料风味背景描述
 * 3. 附属卡片：市场行情 asideCards
 */
const CodexMaterialEntry = memo(function CodexMaterialEntry({
  item,
  isUnlocked,
}: {
  item: ItemDefinition
  isUnlocked: boolean
}): React.JSX.Element {
  const t = useT()
  const tier = item.tier ?? 1
  const grade = resolveItemGrade(tier)
  const levelNumber = convertTierToLevel(tier)

  const baseMetadata = resolveItemMetadata(item.itemId) ?? {
    label: item.name,
    glyph: '·',
    tone: 'gold',
    category: resolveItemCategoryLabel('material', t),
    rarity: 'mundane',
    rarityLabel: t('game.grade.mundane'),
    description: '',
    source: '',
    usage: '',
  }

  const subCategoryLabel = resolveItemCategoryLabel(
    resolveItemSubCategory(item.itemId, item, undefined, baseMetadata.category),
    t
  )

  const metadata: ItemMetadata = {
    ...baseMetadata,
    label: item.name || baseMetadata.label,
    category: subCategoryLabel,
    rarity: grade.rarity,
    rarityLabel: grade.label,
    level: levelNumber,
    description: item.description || baseMetadata.description,
  }

  const tone = rarityToTone(metadata.rarity)

  const stats: { label: string; value: React.ReactNode; tone?: FloatingTone }[] = [
    { label: t('market.tooltip.category'), value: metadata.category, tone: 'muted' },
    { label: t('market.tooltip.level'), value: `Lv. ${formatNumber(levelNumber)}`, tone: 'gold' },
  ]
  if (item.sellPrice !== undefined) {
    stats.push({ label: t('market.tooltip.price'), value: `${formatNumber(item.sellPrice)} 灵石`, tone: 'jade' })
  }

  const hoverContent = (
    <FloatingInfoCard
      badge={metadata.rarityLabel}
      badgeTone={tone}
      description={
        <div>
          {/* 解锁/收录状态胶囊 */}
          <div className="codex-hover-section" style={{ marginTop: 0, paddingTop: 0, borderTop: 'none', marginBottom: 8 }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 6 }}>
              <span className="codex-hover-title" style={{ color: 'var(--gold)' }}>
                {metadata.category}
              </span>
              <span
                className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
                  isUnlocked ? 'unlocked' : 'locked'
                }`}
                style={{ marginTop: 0 }}
              >
                {isUnlocked ? t('app.codex.collected') : formatRealmUnlockCondition(levelNumber)}
              </span>
            </div>
          </div>

          {/* 物品背景故事描述 */}
          {metadata.description && (
            <div style={{ paddingTop: 6, borderTop: '1px solid rgba(201, 180, 109, 0.15)', color: 'var(--text-dim)', fontSize: '0.85rem', lineHeight: 1.5, whiteSpace: 'pre-line' }}>
              {metadata.description}
            </div>
          )}
        </div>
      }
      stats={stats}
      title={metadata.label}
      titleIcon={resolveItemIconUrl(item.itemId)}
    />
  )

  const renderAsideCards = useCallback(
    () => resolveItemTooltipAsideCards(item.itemId, metadata, item),
    [item, metadata]
  )

  return (
    <FloatingPopover asideCards={renderAsideCards} content={hoverContent} interactive={false} placement="top" size="medium">
      <div
        className={`codex-entry-wrapper codex-tile-anchor${
          isUnlocked ? '' : ' codex-entry-wrapper--locked'
        }`}
      >
        <ItemTile
          category={metadata.category}
          className="inventory-cell"
          itemId={item.itemId}
          level={metadata.level}
          name={metadata.label}
          rarity={metadata.rarity}
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
 * 制造配方条目渲染
 * 悬浮卡复用标准物品卡片格式（如 Image #2），包含：
 * 1. 顶部：制作技艺、耗时、所需材料（中文名+数量）与解锁条件
 * 2. 主体：完整成品属性（品类、等阶、攻防数值比率与固定属性，复用 describeEquipmentEffects / describeItemEffects）
 * 3. 附属卡片：若物品具有特殊机制/Buff/战技参悟，自动并排挂载 asideCards
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

  const baseMetadata = resolveItemMetadata(outputItemId) ?? {
    label: outputItemDef?.name ?? outputEqDef?.name ?? action.name,
    glyph: '·',
    tone: 'gold',
    category: resolveItemCategoryLabel(undefined, t),
    rarity: 'mundane',
    rarityLabel: t('game.grade.mundane'),
    description: '',
    source: '',
    usage: '',
  }

  const subCategoryLabel = resolveItemCategoryLabel(
    resolveItemSubCategory(outputItemId, outputItemDef, outputEqDef, baseMetadata.category),
    t
  )

  const rawMetadata = outputItemDef
    ? {
        ...baseMetadata,
        label: outputItemDef.name || baseMetadata.label,
        category: subCategoryLabel,
        level: outputItemDef.tier ?? baseMetadata.level,
        description: outputItemDef.description || baseMetadata.description,
      }
    : outputEqDef
    ? {
        ...baseMetadata,
        label: outputEqDef.name || baseMetadata.label,
        category: subCategoryLabel,
        level: outputEqDef.tier ?? baseMetadata.level,
        description: outputEqDef.description || baseMetadata.description,
      }
    : baseMetadata

  const tier = rawMetadata.level ?? (outputEqDef?.tier || outputItemDef?.tier || 1)
  const grade = resolveItemGrade(tier)
  const levelNumber = convertTierToLevel(tier)

  const metadata: ItemMetadata = {
    ...rawMetadata,
    rarity: grade.rarity,
    rarityLabel: grade.label,
    level: levelNumber,
  }

  // 产出物品的真实主干攻防数值与加成行（复用 ItemDisplay 的核心效果解算）
  const effectLines = [
    ...describeItemEffects(outputItemDef),
    ...describeEquipmentEffects(outputEqDef, 0),
  ]

  // 统计网格（品类、等阶）
  const stats: { label: string; value: React.ReactNode; tone?: FloatingTone }[] = [
    { label: t('market.tooltip.category'), value: metadata.category, tone: 'muted' },
  ]
  if (metadata.level !== undefined) {
    stats.push({ label: t('market.tooltip.level'), value: `Lv. ${formatNumber(metadata.level)}`, tone: 'gold' })
  }

  const tone = rarityToTone(metadata.rarity)

  // 制作所需技艺、耗时、材料
  const inputEntries = Object.entries(action.inputs ?? {})
  const techniqueName = t(`app.crafting.${action.technique ?? 'forging'}`)

  const hoverContent = (
    <FloatingInfoCard
      badge={metadata.rarityLabel}
      badgeTone={tone}
      description={
        <div>
          {/* 前面：制作所需技艺等级、耗时与解锁条件 */}
          <div className="codex-hover-section" style={{ marginTop: 0, paddingTop: 0, borderTop: 'none', marginBottom: 8 }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 6, marginBottom: 4 }}>
              <span className="codex-hover-title" style={{ color: 'var(--gold)' }}>
                {techniqueName} · 耗时 {action.durationSeconds}s
              </span>
              <span
                className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
                  isUnlocked ? 'unlocked' : 'locked'
                }`}
                style={{ marginTop: 0 }}
              >
                {formatRealmUnlockCondition(requiredLevel)}
              </span>
            </div>
            {inputEntries.length > 0 && (
              <div>
                <span className="codex-stat-line" style={{ display: 'block', marginBottom: 3 }}>
                  {t('app.codex.requiresInputs')}：
                </span>
                <div className="codex-chips-wrap">
                  {inputEntries.map(([id, qty]) => {
                    const def = findItemInCatalog(items, id)
                    return (
                      <span className="codex-chip" key={id}>
                        {def?.name ?? id} ×{qty}
                      </span>
                    )
                  })}
                </div>
              </div>
            )}
          </div>

          {/* 后面：物品背景故事描述 */}
          {metadata.description && (
            <div style={{ paddingTop: 6, borderTop: '1px solid rgba(201, 180, 109, 0.15)', color: 'var(--text-dim)', fontSize: '0.85rem', lineHeight: 1.5 }}>
              {metadata.description}
            </div>
          )}
        </div>
      }
      lines={effectLines}
      stats={stats}
      title={metadata.label}
      titleIcon={resolveItemIconUrl(outputItemId)}
    />
  )

  // 附属 Aside 卡片：特殊机制、Buff 增益等自动并排在右侧挂载
  const renderAsideCards = useCallback(
    () => resolveItemTooltipAsideCards(outputItemId, metadata, outputItemDef, outputEqDef),
    [outputItemId, metadata, outputItemDef, outputEqDef]
  )

  return (
    <FloatingPopover asideCards={renderAsideCards} content={hoverContent} interactive={false} placement="top" size="medium">
      <div
        className={`codex-entry-wrapper codex-tile-anchor${
          isUnlocked ? '' : ' codex-entry-wrapper--locked'
        }`}
      >
        <ItemTile
          category={metadata.category}
          className="inventory-cell"
          itemId={outputItemId}
          level={metadata.level}
          name={metadata.label}
          rarity={metadata.rarity}
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
  items,
  realmLevel,
}: {
  building: BuildingDefinition
  items: readonly ItemDefinition[]
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

  // 具体建筑效果拆解
  const modifierLines: string[] = []
  if (building.modifiers) {
    const m = building.modifiers
    if (m.explorationOutputRatio) modifierLines.push(`历练采集收益 +${Math.round(m.explorationOutputRatio * 100)}%`)
    if (m.alchemyOutputRatio) modifierLines.push(`炼丹产出收益 +${Math.round(m.alchemyOutputRatio * 100)}%`)
    if (m.cultivationOutputRatio) modifierLines.push(`吐纳修炼收益 +${Math.round(m.cultivationOutputRatio * 100)}%`)
    if (m.alchemySpeedRatio) modifierLines.push(`炼制行动速度 +${Math.round(m.alchemySpeedRatio * 100)}%`)
    if (m.combatDamageReductionRatio) modifierLines.push(`战斗伤害减免 +${Math.round(m.combatDamageReductionRatio * 100)}%`)
    if (m.combatHitpointRegen) modifierLines.push(`战斗气血回复 +${m.combatHitpointRegen}/秒`)
  }

  const hoverContent = (
    <FloatingInfoCard
      badge={isUnlocked ? t('app.codex.unlocked') : t('app.codex.locked')}
      badgeTone={isUnlocked ? 'jade' : 'muted'}
      description={
        <div>
          {building.description && (
            <div style={{ marginBottom: 6, color: 'var(--text-dim)', fontSize: '0.85rem', lineHeight: 1.5 }}>
              {building.description}
            </div>
          )}

          {/* 建造材料（中文物品名） */}
          <div className="codex-hover-section" style={{ marginTop: 0, paddingTop: 4, borderTop: 'none', marginBottom: 6 }}>
            <span className="codex-hover-title">{t('app.codex.buildingMaterials')}：</span>
            <div className="codex-chips-wrap">
              {(building.materials ?? []).map(m => {
                const def = findItemInCatalog(items, m.itemId)
                return (
                  <span className="codex-chip" key={m.itemId}>
                    {def?.name ?? m.itemId} ×{m.quantity}
                  </span>
                )
              })}
            </div>
          </div>

          {/* 解锁条件 */}
          <div
            className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
              isUnlocked ? 'unlocked' : 'locked'
            }`}
          >
            {formatRealmUnlockCondition(building.requiredRealmLevel)}
          </div>
        </div>
      }
      lines={modifierLines.length > 0 ? modifierLines : [building.effect]}
      stats={[
        { label: '建筑类型', value: groupLabel, tone: 'muted' },
        { label: '建筑等阶', value: `Lv. ${building.requiredRealmLevel}`, tone: 'gold' },
      ]}
      subtitle={`公共设施 · ${groupLabel}`}
      title={building.name}
      titleIcon={resolveBuildingIconUrl(building.buildingId)}
    />
  )

  return (
    <FloatingPopover content={hoverContent} interactive={false} placement="top" size="medium">
      <div
        className={`codex-wide-card${
          isUnlocked ? '' : ' codex-wide-card--locked'
        }`}
      >
        <div className="codex-card-avatar-box">
          {resolveBuildingIconUrl(building.buildingId) ? (
            <img
              alt={building.name}
              className="codex-card-avatar-img"
              loading="lazy"
              src={resolveBuildingIconUrl(building.buildingId)}
            />
          ) : (
            <span style={{ fontSize: 20 }}>🏛️</span>
          )}
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
 * 复用 describeAbility 招式解算器与 EffectTooltip 卡片标准
 */
const CodexAbilityEntry = memo(function CodexAbilityEntry({
  ability,
  isLearned,
}: {
  ability: CombatAbilityDefinition
  isLearned: boolean
}): React.JSX.Element {
  const t = useT()

  // 1 级招式具体加成与伤害数值
  const abilityInfo = describeAbility(ability, 1)

  const hoverContent = (
    <FloatingInfoCard
      badge={abilityInfo.badge}
      badgeTone="qi"
      description={
        <div>
          {/* 功法真实伤害构成与具体加成叙述 */}
          {abilityInfo.description && (
            <div className="codex-effect-text" style={{ marginBottom: 6 }}>
              <ValueText className="effect-value-capsule" text={abilityInfo.description} />
            </div>
          )}

          {/* 技能风味描述 */}
          {ability.description && ability.description !== abilityInfo.description && (
            <div className="codex-stat-line" style={{ marginBottom: 6 }}>
              {ability.description}
            </div>
          )}

          {/* 六维经验获取雷达图（内功心法） */}
          {abilityInfo.radarMultipliers && (
            <RootAspectRadarChart
              efficiency={abilityInfo.radarEfficiency}
              multipliers={abilityInfo.radarMultipliers}
            />
          )}

          {/* 解锁条件：学习后解锁 */}
          <div
            className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
              isLearned ? 'unlocked' : 'locked'
            }`}
          >
            学习后解锁
          </div>
        </div>
      }
      lines={[
        ...abilityInfo.lines.map(line => (
          <ValueText className="effect-value-capsule" key={line} text={line} />
        )),
        ...(abilityInfo.bonuses?.map((bonus) => (
          <PillBonusLabel
            key={bonus.key}
            label={bonus.label}
            tone={bonus.tone as PillTone}
            value={bonus.value}
          />
        )) ?? []),
      ]}
      title={ability.name}
    />
  )

  const grade = resolveItemGrade(ability.tier ?? 1)

  const asideCards = ability.discipline === 'internal'
    ? [createInternalRootExpAsideCard()]
    : undefined

  return (
    <FloatingPopover
      asideCards={asideCards}
      content={hoverContent}
      interactive={false}
      placement="top"
      size="medium"
    >
      <div
        className={`codex-entry-wrapper codex-tile-anchor${
          isLearned ? '' : ' codex-entry-wrapper--locked'
        }`}
      >
        <ItemTile
          category={ability.discipline === 'internal' ? '内功' : (ability.category === 'spell' ? '术法' : '武技')}
          className="inventory-cell"
          itemId={ability.abilityId}
          level={ability.tier ?? 1}
          name={ability.name}
          rarity={ability.discipline === 'internal' ? 'emperor' : grade.rarity}
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
  items?: readonly ItemDefinition[]
  abilities?: readonly CombatAbilityDefinition[]
  isDefeated: boolean
}): React.JSX.Element {
  const t = useT()
  const isBoss = monster.rank === 'boss'
  const isElite = monster.rank === 'elite'
  const rankLabel = isBoss
    ? t('combat.nearby.rankBoss')
    : isElite
      ? t('combat.nearby.rankElite')
      : t('combat.nearby.rankMonster')

  const footer = (
    <div
      className={`codex-unlock-condition-pill codex-unlock-condition-pill--${
        isDefeated ? 'unlocked' : 'locked'
      }`}
      style={{ marginTop: 0 }}
    >
      {isDefeated ? t('app.codex.defeated') : '在野外讨伐并击败该妖兽'}
    </div>
  )

  return (
    <MonsterTooltipTarget
      footer={footer}
      isBoss={isBoss}
      isElite={isElite}
      monster={monster}
      placement="top"
    >
      <div
        className={`codex-wide-card${
          isDefeated ? '' : ' codex-wide-card--locked'
        }`}
      >
        <div className="codex-card-avatar-box">
          <CharacterAvatar
            actorId={monster.enemyId}
            fallback={monster.name.slice(0, 1)}
            isFlipped
            name={monster.name}
            role="enemy"
            visual={monster.visual}
          />
          {!isDefeated && (
            <span className="codex-lock-badge" title={t('app.codex.undefeated')}>
              🔒
            </span>
          )}
        </div>
        <div className="codex-card-info">
          <div className="codex-card-name-row">
            <span className="codex-card-name">{monster.name}</span>
            <span className="codex-card-level">Lv.{monster.level ?? 1}</span>
          </div>
          <span className="codex-card-sub">{rankLabel}</span>
        </div>
      </div>
    </MonsterTooltipTarget>
  )
})
