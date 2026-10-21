import React, { memo, useCallback, useMemo } from 'react'

import {
  formatDualUnlockCondition,
  formatRealmUnlockCondition,
  isMaterialUnlocked,
  resolveMaterialTechnique,
} from '../game/codexCalculations'
import {
  matchesCodexCollect,
  matchesCodexGrade,
  matchesCodexTraitGrade,
  resolveCodexCraftingSubTab,
  resolveCodexMaterialSubTab,
  resolveCodexMonsterRank,
  tallyCodexBuckets,
  DEFAULT_CODEX_FILTERS,
  type CodexAbilityDamage,
  type CodexAbilityDiscipline,
  type CodexBuildingGroup,
  type CodexCraftingSubTab,
  type CodexFilterState,
  type CodexMaterialSubTab,
  type CodexMonsterRank,
  type CodexTraitGrade,
} from '../game/codexFilters'
import { calculateTechniqueProgress, TECHNIQUE_IDS, TRAIT_DEFINITIONS } from '../game/combatConstants'
import { describeAbility, formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import {
  convertLevelToTier,
  convertTierToLevel,
  findItemInCatalog,
  resolveActionLevel,
  resolveAssetLabel,
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
import { getBuildingBonusFormulas } from '../game/buildingCalculations'
import { resolveBuildingIconUrl } from '../game/buildingIcons'
import { useT } from '../i18n'
import type { CodexCategory } from '../uiStore'
import { useUiStore } from '../uiStore'
import { OptionToggle, type OptionToggleOption } from './OptionToggle'
import { PillBonusLabel, type PillTone } from './PillLabel'
import { StatGlyph } from './StatGlyph'
import { TraitBarItem } from './TraitBarItem'
import { RootAspectRadarChart } from './RootAspectRadarChart'
import { CharacterAvatar } from './CharacterAvatar'
import { FloatingInfoCard, FloatingPopover, type FloatingAsideCard, type FloatingTone } from './FloatingPopover'
import { MonsterTooltipTarget } from './MonsterDisplay'
import { createAbilityBuffAsideCards, createInternalAbilityBonusAsideCard, createInternalRootExpAsideCard } from './EffectTooltip'
import {
  describeItemEffects,
  ItemTile,
  rarityToTone,
  resolveItemTooltipAsideCards,
} from './ItemDisplay'
import { ValueText } from './ValueText'

/** 顶部主分类定义（材料居首，与总纲收录顺序一致） */
const CODEX_CATEGORIES: ReadonlyArray<{ id: CodexCategory; labelKey: string }> = [
  { id: 'material', labelKey: 'tabMaterial' },
  { id: 'crafting', labelKey: 'tabCrafting' },
  { id: 'building', labelKey: 'tabBuilding' },
  { id: 'ability', labelKey: 'tabAbility' },
  { id: 'monster', labelKey: 'tabMonster' },
  { id: 'trait', labelKey: 'tabTrait' },
]

/** 材料子分类（配表 `subCategory` 四类） */
const MATERIAL_SUB_TABS: ReadonlyArray<Exclude<CodexMaterialSubTab, 'all'>> = [
  'spiritMaterial',
  'mineral',
  'exoticMaterial',
  'refinedMaterial',
]
const MATERIAL_TAB_LABELS: Record<Exclude<CodexMaterialSubTab, 'all'>, string> = {
  spiritMaterial: 'materialSpirit',
  mineral: 'materialMineral',
  exoticMaterial: 'materialExotic',
  refinedMaterial: 'materialRefined',
}

/** 技艺子分类（炼丹 / 炼器 / 阵法 / 符箓） */
const CRAFTING_SUB_TABS: ReadonlyArray<Exclude<CodexCraftingSubTab, 'all'>> = [
  'alchemy',
  'forging',
  'formation',
  'talisman',
]
const CRAFTING_TAB_LABELS: Record<Exclude<CodexCraftingSubTab, 'all'>, string> = {
  alchemy: 'craftingAlchemy',
  forging: 'craftingForging',
  formation: 'craftingFormation',
  talisman: 'craftingTalisman',
}

/** 建筑分组（配表 `group`） */
const BUILDING_GROUPS: ReadonlyArray<Exclude<CodexBuildingGroup, 'all'>> = ['production', 'combat']
const BUILDING_GROUP_LABELS: Record<Exclude<CodexBuildingGroup, 'all'>, string> = {
  production: 'buildingProduction',
  combat: 'buildingCombat',
}

/** 功法门类与伤害大类 */
const ABILITY_DISCIPLINES: ReadonlyArray<Exclude<CodexAbilityDiscipline, 'all'>> = ['spell', 'internal']
const ABILITY_DISCIPLINE_LABELS: Record<Exclude<CodexAbilityDiscipline, 'all'>, string> = {
  spell: 'abilitySpell',
  internal: 'abilityInternal',
}
const ABILITY_DAMAGES: ReadonlyArray<Exclude<CodexAbilityDamage, 'all'>> = ['physical', 'spell']
const ABILITY_DAMAGE_LABELS: Record<Exclude<CodexAbilityDamage, 'all'>, string> = {
  physical: 'damagePhysical',
  spell: 'damageSpell',
}

/** 妖兽生命层次 */
const MONSTER_RANKS: ReadonlyArray<Exclude<CodexMonsterRank, 'all'>> = ['normal', 'elite', 'boss']
const MONSTER_RANK_LABELS: Record<Exclude<CodexMonsterRank, 'all'>, string> = {
  normal: 'monsterNormal',
  elite: 'monsterElite',
  boss: 'monsterBoss',
}

/** 特质品阶（Q1 凡品 ~ Q5 天品） */
const TRAIT_GRADES: ReadonlyArray<Exclude<CodexTraitGrade, 'all'>> = ['q1', 'q2', 'q3', 'q4', 'q5']
const TRAIT_GRADE_LABELS: Record<Exclude<CodexTraitGrade, 'all'>, string> = {
  q1: 'traitQ1',
  q2: 'traitQ2',
  q3: 'traitQ3',
  q4: 'traitQ4',
  q5: 'traitQ5',
}
const TRAIT_GRADE_QUALITY: Record<Exclude<CodexTraitGrade, 'all'>, number> = {
  q1: 1,
  q2: 2,
  q3: 3,
  q4: 4,
  q5: 5,
}

/** 等阶下拉档位：与品质档 `ITEM_GRADE_ORDER`（1~8 阶）同域 */
const GRADE_TIERS: ReadonlyArray<number> = [1, 2, 3, 4, 5, 6, 7, 8]

/**
 * 万象图鉴页面组件
 * 对应主界面顶级页签 'codex'
 *
 * 主分类：材料 / 技艺 / 建造 / 功法 / 妖兽 / 特质；
 * 每个主分类下挂一条与坊市同款的「多维筛选栏」——每维一个「全部 + 互斥选项」Toggle 组
 * （首项「全部」表示该维不过滤），维度之间 AND 联合，等阶用原生下拉。
 * Toggle 角标展示该选项的「已收录 / 总数」，筛选状态按分类分别常驻 `useUiStore.codexFilters`。
 */
export const CodexPage = memo(function CodexPage(): React.JSX.Element {
  const t = useT()

  const codexCategory = useUiStore(state => state.codexCategory)
  const setCodexCategory = useUiStore(state => state.setCodexCategory)
  const codexFilters = useUiStore(state => state.codexFilters)
  const setCodexFilters = useUiStore(state => state.setCodexFilters)

  const filters: CodexFilterState = codexFilters[codexCategory] ?? DEFAULT_CODEX_FILTERS
  const updateFilters = useCallback(
    (patch: Partial<CodexFilterState>) => setCodexFilters(codexCategory, patch),
    [setCodexFilters, codexCategory],
  )

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
  const techniqueExperiences = useGameStore(state => state.combat?.techniqueExperiences)

  // 玩家当前各生活技艺等级（材料与配方的技艺解锁判定共用）
  const techniqueLevels = useMemo(() => {
    const map: Record<string, number> = {}
    for (const id of TECHNIQUE_IDS) {
      map[id] = calculateTechniqueProgress(techniqueExperiences?.[id] ?? 0).level
    }
    return map
  }, [techniqueExperiences])

  /** 收录度角标文本：与顶部大分类进度胶囊同一口径 */
  const countLabel = useCallback(
    (entry?: { collected: number; total: number }) => (entry ? `${entry.collected}/${entry.total}` : undefined),
    [],
  )

  // ===== 0. 天地材料 =====
  const allMaterials = useMemo(() => items.filter(it => it.category === 'material'), [items])

  const isItemUnlocked = useCallback(
    (item: ItemDefinition): boolean => isMaterialUnlocked(item, realmLevel, inventory, techniqueLevels),
    [realmLevel, inventory, techniqueLevels]
  )

  const materialTally = useMemo(() => tallyCodexBuckets(
    allMaterials,
    [
      { value: 'all' as CodexMaterialSubTab, match: () => true },
      ...MATERIAL_SUB_TABS.map(sub => ({
        value: sub as CodexMaterialSubTab,
        match: (it: ItemDefinition) => resolveCodexMaterialSubTab(it) === sub,
      })),
    ],
    isItemUnlocked,
  ), [allMaterials, isItemUnlocked])

  const materialTabOptions = useMemo<ReadonlyArray<OptionToggleOption<CodexMaterialSubTab>>>(() => [
    { value: 'all', label: t('app.codex.filterAll'), count: countLabel(materialTally.all) },
    ...MATERIAL_SUB_TABS.map(sub => ({
      value: sub,
      label: t(`app.codex.${MATERIAL_TAB_LABELS[sub]}`),
      count: countLabel(materialTally[sub]),
    })),
  ], [t, materialTally, countLabel])

  const visibleMaterials = useMemo(() => {
    return allMaterials
      .filter(it => matchesCodexGrade(it.tier, filters.grade))
      .filter(it => filters.materialSubTab === 'all' || resolveCodexMaterialSubTab(it) === filters.materialSubTab)
      .filter(it => matchesCodexCollect(filters.collect, isItemUnlocked(it)))
      .sort((a, b) => {
        const levelA = convertTierToLevel(a.tier ?? 1)
        const levelB = convertTierToLevel(b.tier ?? 1)
        if (levelA !== levelB) return levelA - levelB
        return a.itemId.localeCompare(b.itemId)
      })
  }, [allMaterials, filters.grade, filters.materialSubTab, filters.collect, isItemUnlocked])

  // ===== 1. 技艺配方 =====
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

  // 配方是否解锁：境界达到或对应制造技艺等级达到（双重「或」判定）
  const isActionUnlocked = useCallback(
    (action: ActionDefinition): boolean => {
      const requiredLevel = resolveActionLevel(action, items, equipment, encounters)
      if (requiredLevel <= 1) return true
      if (realmLevel >= requiredLevel) return true
      const tech = action.technique
      if (tech && (techniqueLevels[tech] ?? 0) >= requiredLevel) return true
      return false
    },
    [items, equipment, encounters, realmLevel, techniqueLevels]
  )

  const craftingTally = useMemo(() => tallyCodexBuckets(
    allCraftingActions,
    [
      { value: 'all' as CodexCraftingSubTab, match: () => true },
      ...CRAFTING_SUB_TABS.map(sub => ({
        value: sub as CodexCraftingSubTab,
        match: (action: ActionDefinition) => resolveCodexCraftingSubTab(action) === sub,
      })),
    ],
    isActionUnlocked,
  ), [allCraftingActions, isActionUnlocked])

  const craftingTabOptions = useMemo<ReadonlyArray<OptionToggleOption<CodexCraftingSubTab>>>(() => [
    { value: 'all', label: t('app.codex.filterAll'), count: countLabel(craftingTally.all) },
    ...CRAFTING_SUB_TABS.map(sub => ({
      value: sub,
      label: t(`app.codex.${CRAFTING_TAB_LABELS[sub]}`),
      count: countLabel(craftingTally[sub]),
    })),
  ], [t, craftingTally, countLabel])

  const visibleCraftingActions = useMemo(() => {
    return allCraftingActions
      .filter(a => matchesCodexGrade(convertLevelToTier(resolveActionLevel(a, items, equipment, encounters)), filters.grade))
      .filter(a => filters.craftingSubTab === 'all' || resolveCodexCraftingSubTab(a) === filters.craftingSubTab)
      .filter(a => matchesCodexCollect(filters.collect, isActionUnlocked(a)))
      .sort((a, b) => {
        const levelA = resolveActionLevel(a, items, equipment, encounters)
        const levelB = resolveActionLevel(b, items, equipment, encounters)
        if (levelA !== levelB) return levelA - levelB
        return a.id.localeCompare(b.id)
      })
  }, [allCraftingActions, filters.grade, filters.craftingSubTab, filters.collect, isActionUnlocked, items, equipment, encounters])

  // ===== 2. 建筑图谱 =====
  const isBuildingUnlocked = useCallback(
    (building: BuildingDefinition): boolean => realmLevel >= building.requiredRealmLevel,
    [realmLevel],
  )

  const sortedBuildings = useMemo(
    () => [...buildings].sort((a, b) => a.requiredRealmLevel - b.requiredRealmLevel),
    [buildings],
  )

  const buildingTally = useMemo(() => tallyCodexBuckets(
    sortedBuildings,
    [
      { value: 'all' as CodexBuildingGroup, match: () => true },
      ...BUILDING_GROUPS.map(group => ({
        value: group as CodexBuildingGroup,
        match: (b: BuildingDefinition) => b.group === group,
      })),
    ],
    isBuildingUnlocked,
  ), [sortedBuildings, isBuildingUnlocked])

  const buildingGroupOptions = useMemo<ReadonlyArray<OptionToggleOption<CodexBuildingGroup>>>(() => [
    { value: 'all', label: t('app.codex.filterAll'), count: countLabel(buildingTally.all) },
    ...BUILDING_GROUPS.map(group => ({
      value: group,
      label: t(`app.codex.${BUILDING_GROUP_LABELS[group]}`),
      count: countLabel(buildingTally[group]),
    })),
  ], [t, buildingTally, countLabel])

  const visibleBuildings = useMemo(() => sortedBuildings
    .filter(b => filters.buildingGroup === 'all' || b.group === filters.buildingGroup)
    .filter(b => matchesCodexCollect(filters.collect, isBuildingUnlocked(b))),
  [sortedBuildings, filters.buildingGroup, filters.collect, isBuildingUnlocked])

  // ===== 3. 功法神通 =====
  const sortedAbilities = useMemo(
    () => [...abilities].sort((a, b) => ((a.tier ?? 1) - (b.tier ?? 1)) || a.abilityId.localeCompare(b.abilityId)),
    [abilities],
  )

  const learnedSet = useMemo(() => new Set(learnedAbilityIds), [learnedAbilityIds])
  const isAbilityLearned = useCallback(
    (ability: CombatAbilityDefinition): boolean => learnedSet.has(ability.abilityId),
    [learnedSet],
  )

  const abilityDisciplineTally = useMemo(() => tallyCodexBuckets(
    sortedAbilities,
    [
      { value: 'all' as CodexAbilityDiscipline, match: () => true },
      ...ABILITY_DISCIPLINES.map(discipline => ({
        value: discipline as CodexAbilityDiscipline,
        match: (a: CombatAbilityDefinition) => (a.discipline === 'internal' ? 'internal' : 'spell') === discipline,
      })),
    ],
    isAbilityLearned,
  ), [sortedAbilities, isAbilityLearned])

  const abilityDamageTally = useMemo(() => tallyCodexBuckets(
    sortedAbilities,
    [
      { value: 'all' as CodexAbilityDamage, match: () => true },
      ...ABILITY_DAMAGES.map(damage => ({
        value: damage as CodexAbilityDamage,
        match: (a: CombatAbilityDefinition) => a.category === damage,
      })),
    ],
    isAbilityLearned,
  ), [sortedAbilities, isAbilityLearned])

  const abilityDisciplineOptions = useMemo<ReadonlyArray<OptionToggleOption<CodexAbilityDiscipline>>>(() => [
    { value: 'all', label: t('app.codex.filterAll'), count: countLabel(abilityDisciplineTally.all) },
    ...ABILITY_DISCIPLINES.map(discipline => ({
      value: discipline,
      label: t(`app.codex.${ABILITY_DISCIPLINE_LABELS[discipline]}`),
      count: countLabel(abilityDisciplineTally[discipline]),
    })),
  ], [t, abilityDisciplineTally, countLabel])

  const abilityDamageOptions = useMemo<ReadonlyArray<OptionToggleOption<CodexAbilityDamage>>>(() => [
    { value: 'all', label: t('app.codex.filterAll'), count: countLabel(abilityDamageTally.all) },
    ...ABILITY_DAMAGES.map(damage => ({
      value: damage,
      label: t(`app.codex.${ABILITY_DAMAGE_LABELS[damage]}`),
      count: countLabel(abilityDamageTally[damage]),
    })),
  ], [t, abilityDamageTally, countLabel])

  const visibleAbilities = useMemo(() => sortedAbilities
    .filter(a => matchesCodexGrade(a.tier, filters.grade))
    .filter(a => filters.abilityDiscipline === 'all'
      || (a.discipline === 'internal' ? 'internal' : 'spell') === filters.abilityDiscipline)
    .filter(a => filters.abilityDamage === 'all' || a.category === filters.abilityDamage)
    .filter(a => matchesCodexCollect(filters.collect, isAbilityLearned(a))),
  [sortedAbilities, filters.grade, filters.abilityDiscipline, filters.abilityDamage, filters.collect, isAbilityLearned])

  // ===== 4. 大世界妖兽 =====
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

  const defeatedSet = useMemo(() => new Set(stats.defeatedMonsterIds ?? []), [stats.defeatedMonsterIds])
  const isMonsterDefeated = useCallback(
    (monster: CombatantDefinition): boolean => defeatedSet.has(monster.enemyId),
    [defeatedSet],
  )

  const monsterRankTally = useMemo(() => tallyCodexBuckets(
    allMonsters,
    [
      { value: 'all' as CodexMonsterRank, match: () => true },
      ...MONSTER_RANKS.map(rank => ({
        value: rank as CodexMonsterRank,
        match: (m: CombatantDefinition) => resolveCodexMonsterRank(m.rank) === rank,
      })),
    ],
    isMonsterDefeated,
  ), [allMonsters, isMonsterDefeated])

  const monsterRankOptions = useMemo<ReadonlyArray<OptionToggleOption<CodexMonsterRank>>>(() => [
    { value: 'all', label: t('app.codex.filterAll'), count: countLabel(monsterRankTally.all) },
    ...MONSTER_RANKS.map(rank => ({
      value: rank,
      label: t(`app.codex.${MONSTER_RANK_LABELS[rank]}`),
      count: countLabel(monsterRankTally[rank]),
    })),
  ], [t, monsterRankTally, countLabel])

  const visibleMonsters = useMemo(() => allMonsters
    .filter(m => matchesCodexGrade(m.tier, filters.grade))
    .filter(m => filters.monsterRank === 'all' || resolveCodexMonsterRank(m.rank) === filters.monsterRank)
    .filter(m => matchesCodexCollect(filters.collect, isMonsterDefeated(m))),
  [allMonsters, filters.grade, filters.monsterRank, filters.collect, isMonsterDefeated])

  // ===== 5. 特质志 =====
  const unlockedTraitsMap = useMemo(() => {
    const map = new Map<string, number>()
    for (const ut of stats.unlockedTraits ?? []) {
      map.set(ut.id, ut.level)
    }
    return map
  }, [stats.unlockedTraits])

  const allTraits = useMemo(() => Object.values(TRAIT_DEFINITIONS).sort((a, b) => {
    if (a.quality !== b.quality) return a.quality - b.quality
    if (a.cost !== b.cost) return a.cost - b.cost
    return a.id.localeCompare(b.id)
  }), [])

  const isTraitUnlocked = useCallback(
    (trait: { id: string }): boolean => unlockedTraitsMap.has(trait.id),
    [unlockedTraitsMap],
  )

  const traitGradeTally = useMemo(() => tallyCodexBuckets(
    allTraits,
    [
      { value: 'all' as CodexTraitGrade, match: () => true },
      ...TRAIT_GRADES.map(grade => ({
        value: grade as CodexTraitGrade,
        match: (tr: { quality: number }) => tr.quality === TRAIT_GRADE_QUALITY[grade],
      })),
    ],
    isTraitUnlocked,
  ), [allTraits, isTraitUnlocked])

  const traitGradeOptions = useMemo<ReadonlyArray<OptionToggleOption<CodexTraitGrade>>>(() => [
    { value: 'all', label: t('app.codex.filterAll'), count: countLabel(traitGradeTally.all) },
    ...TRAIT_GRADES.map(grade => ({
      value: grade,
      label: t(`app.codex.${TRAIT_GRADE_LABELS[grade]}`),
      count: countLabel(traitGradeTally[grade]),
    })),
  ], [t, traitGradeTally, countLabel])

  const visibleTraits = useMemo(() => allTraits
    .filter(tr => matchesCodexTraitGrade(tr.quality, filters.traitGrade))
    .filter(tr => matchesCodexCollect(filters.collect, isTraitUnlocked(tr))),
  [allTraits, filters.traitGrade, filters.collect, isTraitUnlocked])

  // ===== 收录状态与等阶（跨分类共用维度） =====
  const collectOptions = useMemo<ReadonlyArray<OptionToggleOption<NonNullable<CodexFilterState['collect']>>>>(() => [
    { value: 'all', label: t('app.codex.filterAll') },
    { value: 'collected', label: t('app.codex.collected') },
    { value: 'missing', label: t('app.codex.uncollected') },
  ], [t])

  const gradeOptions = useMemo(() => [
    { value: 0, label: t('app.codex.filterAll') },
    ...GRADE_TIERS.map(tier => ({ value: tier, label: t('app.codex.gradeOption', { tier: formatNumber(tier) }) })),
  ], [t])

  /** 当前分类的主导航分类计数（顶部主 Tab 角标） */
  const categoryCounts = useMemo<Record<CodexCategory, { collected: number; total: number }>>(() => ({
    material: { collected: materialTally.all.collected, total: materialTally.all.total },
    crafting: { collected: craftingTally.all.collected, total: craftingTally.all.total },
    building: { collected: buildingTally.all.collected, total: buildingTally.all.total },
    ability: { collected: abilityDisciplineTally.all.collected, total: abilityDisciplineTally.all.total },
    monster: { collected: monsterRankTally.all.collected, total: monsterRankTally.all.total },
    trait: { collected: traitGradeTally.all.collected, total: traitGradeTally.all.total },
  }), [materialTally, craftingTally, buildingTally, abilityDisciplineTally, monsterRankTally, traitGradeTally])

  const grandUnlocked = Object.values(categoryCounts).reduce((sum, entry) => sum + entry.collected, 0)
  const grandTotal = Object.values(categoryCounts).reduce((sum, entry) => sum + entry.total, 0)
  const grandPercent = grandTotal > 0 ? Math.round((grandUnlocked / grandTotal) * 100) : 0

  /** 当前分类是否具备等阶维度（建造/特质无等阶） */
  const hasGradeFilter = codexCategory === 'material'
    || codexCategory === 'crafting'
    || codexCategory === 'ability'
    || codexCategory === 'monster'

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

        {/* 六大主分类切换：材料居首 */}
        <nav className="codex-tabs" role="tablist">
          {CODEX_CATEGORIES.map(category => (
            <button
              aria-selected={codexCategory === category.id}
              className={`codex-tab-btn${codexCategory === category.id ? ' active' : ''}`}
              key={category.id}
              onClick={() => setCodexCategory(category.id)}
              role="tab"
              type="button"
            >
              <span>{t(`app.codex.${category.labelKey}`)}</span>
              <span className="codex-tab-count">
                {categoryCounts[category.id].collected}/{categoryCounts[category.id].total}
              </span>
            </button>
          ))}
        </nav>

        {/* 分类多维筛选栏：与坊市同款「全部 + 互斥选项」Toggle 组，维度之间 AND 联合 */}
        <div className="filter-bar codex-filter-bar" role="toolbar" aria-label={t('app.codex.filterAria')}>
          {codexCategory === 'material' && (
            <>
              <span className="filter-label">{t('app.codex.filterCategoryLabel')}</span>
              <OptionToggle
                ariaLabel={t('app.codex.filterCategoryAria')}
                onChange={next => updateFilters({ materialSubTab: next })}
                options={materialTabOptions}
                value={filters.materialSubTab}
              />
            </>
          )}

          {codexCategory === 'crafting' && (
            <>
              <span className="filter-label">{t('app.codex.filterCategoryLabel')}</span>
              <OptionToggle
                ariaLabel={t('app.codex.filterCategoryAria')}
                onChange={next => updateFilters({ craftingSubTab: next })}
                options={craftingTabOptions}
                value={filters.craftingSubTab}
              />
            </>
          )}

          {codexCategory === 'building' && (
            <>
              <span className="filter-label">{t('app.codex.filterCategoryLabel')}</span>
              <OptionToggle
                ariaLabel={t('app.codex.filterCategoryAria')}
                onChange={next => updateFilters({ buildingGroup: next })}
                options={buildingGroupOptions}
                value={filters.buildingGroup}
              />
            </>
          )}

          {codexCategory === 'ability' && (
            <>
              <span className="filter-label">{t('app.codex.filterDisciplineLabel')}</span>
              <OptionToggle
                ariaLabel={t('app.codex.filterDisciplineAria')}
                onChange={next => updateFilters({ abilityDiscipline: next })}
                options={abilityDisciplineOptions}
                value={filters.abilityDiscipline}
              />
              <span className="filter-label">{t('app.codex.filterDamageLabel')}</span>
              <OptionToggle
                ariaLabel={t('app.codex.filterDamageAria')}
                onChange={next => updateFilters({ abilityDamage: next })}
                options={abilityDamageOptions}
                value={filters.abilityDamage}
              />
            </>
          )}

          {codexCategory === 'monster' && (
            <>
              <span className="filter-label">{t('app.codex.filterRankLabel')}</span>
              <OptionToggle
                ariaLabel={t('app.codex.filterRankAria')}
                onChange={next => updateFilters({ monsterRank: next })}
                options={monsterRankOptions}
                value={filters.monsterRank}
              />
            </>
          )}

          {codexCategory === 'trait' && (
            <>
              <span className="filter-label">{t('app.codex.filterTraitGradeLabel')}</span>
              <OptionToggle
                ariaLabel={t('app.codex.filterTraitGradeAria')}
                onChange={next => updateFilters({ traitGrade: next })}
                options={traitGradeOptions}
                value={filters.traitGrade}
              />
            </>
          )}

          {hasGradeFilter && (
            <label className="filter-grade-field">
              <span className="filter-label">{t('app.codex.filterTierLabel')}</span>
              <select
                aria-label={t('app.codex.filterTierAria')}
                className="filter-grade-select"
                onChange={event => updateFilters({ grade: Number(event.target.value) })}
                value={filters.grade}
              >
                {gradeOptions.map(option => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>
          )}

          <span className="filter-label">{t('app.codex.filterCollectLabel')}</span>
          <OptionToggle
            ariaLabel={t('app.codex.filterCollectAria')}
            onChange={next => updateFilters({ collect: next })}
            options={collectOptions}
            value={filters.collect}
          />
        </div>
      </header>

      {/* 主体图鉴内容 */}
      <main className="codex-content">
        {codexCategory === 'material' && (
          <div className="codex-grid codex-tile-grid">
            {visibleMaterials.map(item => (
              <CodexMaterialEntry
                isUnlocked={isItemUnlocked(item)}
                item={item}
                key={item.itemId}
              />
            ))}
            {visibleMaterials.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}
        {codexCategory === 'crafting' && (
          <div className="codex-grid codex-tile-grid">
            {visibleCraftingActions.map(action => (
              <CodexCraftingEntry
                action={action}
                encounters={encounters}
                equipment={equipment}
                items={items}
                key={action.id}
                realmLevel={realmLevel}
                techniqueLevel={action.technique ? (techniqueLevels[action.technique] ?? 0) : 0}
              />
            ))}
            {visibleCraftingActions.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}

        {codexCategory === 'building' && (
          <div className="codex-grid codex-grid--wide">
            {visibleBuildings.map(building => (
              <CodexBuildingEntry
                building={building}
                items={items}
                key={building.buildingId}
                realmLevel={realmLevel}
              />
            ))}
            {visibleBuildings.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}

        {codexCategory === 'ability' && (
          <div className="codex-grid codex-tile-grid">
            {visibleAbilities.map(ability => (
              <CodexAbilityEntry
                ability={ability}
                isLearned={learnedSet.has(ability.abilityId)}
                key={ability.abilityId}
              />
            ))}
            {visibleAbilities.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}

        {codexCategory === 'monster' && (
          <div className="codex-grid codex-grid--wide">
            {visibleMonsters.map(monster => (
              <CodexMonsterEntry
                abilities={abilities}
                isDefeated={defeatedSet.has(monster.enemyId)}
                items={items}
                key={monster.enemyId}
                monster={monster}
              />
            ))}
            {visibleMonsters.length === 0 && (
              <div className="codex-empty">{t('app.codex.emptyCategory')}</div>
            )}
          </div>
        )}

        {codexCategory === 'trait' && (
          <div className="codex-trait-list">
            {visibleTraits.map(tr => (
              <TraitBarItem
                def={tr}
                key={tr.id}
                level={unlockedTraitsMap.get(tr.id) ?? 0}
                mode="codex"
              />
            ))}
            {visibleTraits.length === 0 && (
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
                {isUnlocked
                  ? t('app.codex.collected')
                  : (() => {
                      const tech = resolveMaterialTechnique(item)
                      const techName = tech ? t(`insight.technique.${tech}`) : null
                      return formatDualUnlockCondition(levelNumber, techName)
                    })()}
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
  techniqueLevel = 0,
}: {
  action: ActionDefinition
  realmLevel: number
  items: readonly ItemDefinition[]
  equipment: readonly CombatEquipmentDefinition[]
  encounters: readonly CombatEncounterDefinition[]
  techniqueLevel?: number
}): React.JSX.Element {
  const t = useT()
  const requiredLevel = resolveActionLevel(action, items, equipment, encounters)
  const isUnlocked = requiredLevel <= 1 || realmLevel >= requiredLevel || techniqueLevel >= requiredLevel
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

  // 产出物品的直接效果行（装备属性数值解耦至 Aside 独立属性悬浮副卡并排展示）
  const effectLines = [
    ...describeItemEffects(outputItemDef),
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
                {formatDualUnlockCondition(requiredLevel, action.technique ? techniqueName : null)}
              </span>
            </div>
            {inputEntries.length > 0 && (
              <div>
                <span className="codex-stat-line" style={{ display: 'block', marginBottom: 3 }}>
                  {t('app.codex.requiresInputs')}：
                </span>
                <div className="codex-chips-wrap">
                  {inputEntries.map(([id, qty]) => (
                    <span className="codex-chip" key={id}>
                      {resolveAssetLabel(id, items, equipment)} ×{qty}
                    </span>
                  ))}
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
      ? t('combat.nearby.groupProduction')
      : building.group === 'combat'
        ? t('combat.nearby.groupCombat')
        : t('combat.nearby.groupOther')

  // 建筑效果按配表成长模型拆解为「1 级基准 + 每级增幅」，
  // 只读 modifiers 会让所有建筑看起来是恒定加成，与真实结算脱节。
  const bonusFormulas = getBuildingBonusFormulas(building, (key, def) => t(key) || def)

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

          {/* 建筑效果：加成条目胶囊化，基准值与每级增幅分列，避免误读为恒定数值 */}
          {bonusFormulas.length > 0 ? (
            <div className="codex-hover-section" style={{ marginTop: 0, paddingTop: 4, borderTop: 'none', marginBottom: 6 }}>
              <span className="codex-hover-title">{t('app.codex.buildingEffect')}：</span>
              <div className="codex-building-bonus-list">
                {bonusFormulas.map(formula => (
                  <span className="codex-building-bonus-row" key={formula.key}>
                    <PillBonusLabel
                      icon={<StatGlyph glyph={formula.glyph} size={14} />}
                      label={formula.label}
                      tone={formula.tone}
                      value={formula.baseText}
                    />
                    {formula.growthText && (
                      <span className="codex-building-bonus-growth">
                        {t('app.codex.buildingBonusGrowth', { value: formula.growthText })}
                      </span>
                    )}
                  </span>
                ))}
              </div>
            </div>
          ) : building.effect ? (
            <div className="codex-hover-section" style={{ marginTop: 0, paddingTop: 4, borderTop: 'none', marginBottom: 6 }}>
              <span className="codex-hover-title">{t('app.codex.buildingEffect')}：</span>
              <span className="codex-stat-line">{building.effect}</span>
            </div>
          ) : null}

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
      stats={[
        { label: t('app.codex.buildingType'), value: groupLabel, tone: 'muted' },
        { label: t('app.codex.buildingTier'), value: `Lv. ${building.requiredRealmLevel}`, tone: 'gold' },
      ]}
      subtitle={t('app.codex.buildingSubtitle', { group: groupLabel })}
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
            <img decoding="async"
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
          {/* 功法风味 + 伤害构成叙述（describeAbility 已包含配表风味，避免重复渲染） */}
          {abilityInfo.description && (
            <div className="codex-effect-text" style={{ marginBottom: 6 }}>
              <ValueText className="effect-value-capsule" text={abilityInfo.description} />
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

  const asideCards = useMemo(() => {
    const list: FloatingAsideCard[] = []
    list.push(...createAbilityBuffAsideCards(ability, 1))
    if (ability.discipline === 'internal') {
      const internalBonus = createInternalAbilityBonusAsideCard(ability, 1)
      if (internalBonus) {
        list.push(internalBonus)
      }
      list.push(createInternalRootExpAsideCard())
    }
    return list.length > 0 ? list : undefined
  }, [ability])

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
