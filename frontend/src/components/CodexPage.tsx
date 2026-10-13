import React, { memo, useCallback, useMemo } from 'react'

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
import { useT } from '../i18n'
import type { CodexCraftingSubTab } from '../uiStore'
import { useUiStore } from '../uiStore'
import { CharacterAvatar } from './CharacterAvatar'
import { FloatingInfoCard, FloatingPopover, type FloatingTone } from './FloatingPopover'
import {
  describeEquipmentEffects,
  describeItemEffects,
  ItemTile,
  rarityToTone,
  resolveItemTooltipAsideCards,
} from './ItemDisplay'
import { ValueText } from './ValueText'

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
    <FloatingPopover asideCards={renderAsideCards} content={hoverContent} placement="top" size="medium">
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
      lines={abilityInfo.lines.map(line => (
        <ValueText className="effect-value-capsule" key={line} text={line} />
      ))}
      title={ability.name}
    />
  )

  const grade = resolveItemGrade(ability.tier ?? 1)

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
          rarity={grade.rarity}
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
  items,
  isDefeated,
}: {
  monster: CombatantDefinition
  items: readonly ItemDefinition[]
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
    .map(d => `${findItemInCatalog(items, d.itemId)?.name ?? d.itemId} (${Math.round(d.dropChance * 100)}%)`)
    .join('、')

  const hoverContent = (
    <FloatingInfoCard
      badge={isDefeated ? t('app.codex.defeated') : t('app.codex.undefeated')}
      badgeTone={isDefeated ? 'danger' : 'muted'}
      description={
        <div>
          {isDefeated ? (
            <>
              <div className="codex-chips-wrap" style={{ marginBottom: 4 }}>
                <span className="codex-chip">
                  气血：{monster.attributes?.maxHitpoints ?? 0}
                </span>
                <span className="codex-chip">
                  攻击：{monster.attributes?.physicalAttack || monster.attributes?.spellAttack || 0}
                </span>
                <span className="codex-chip">
                  护甲：{monster.attributes?.armor ?? 0}
                </span>
                <span className="codex-chip">
                  抗性：{monster.attributes?.magicResistance ?? 0}
                </span>
              </div>
              {dropsDesc && (
                <div className="codex-stat-line" style={{ marginTop: 4, marginBottom: 4 }}>
                  <strong>掉落物：</strong>{dropsDesc}
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
            在野外讨伐并击败该妖兽
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
