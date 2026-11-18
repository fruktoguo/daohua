import { memo, useCallback, useMemo, type KeyboardEvent, type ReactNode } from 'react'
import { getBuildingBonusFormulas } from '../game/buildingCalculations'
import { resolveBuildingIconUrl } from '../game/buildingIcons'
import { formatRealmUnlockCondition } from '../game/codexCalculations'
import { TRAIT_DEFINITIONS } from '../game/combatConstants'
import { describeAbility, formatNumber, resolveTriggerBuffName } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { resolveEntityName } from '../game/entityNames'
import {
  findAbilityInCatalog,
  findActionInCatalog,
  findCombatantInCatalog,
  findEncounterInCatalog,
  resolveActionDefinition,
  resolveActionLevel,
  resolveAssetLabel,
} from '../game/ids'
import { resolveItemIconUrl } from '../game/itemIcons'
import type {
  ActionDefinition,
  ActionYield,
  BuildingDefinition,
  CombatAbilityDefinition,
  ItemDefinition,
  WorldMapDefinition,
  WorldNodeDefinition,
} from '../game/types'
import { useT, type TFunction } from '../i18n'
import { parseLogTemplate, type LogSegment } from '../logging/gameLogService'
import { useUiStore } from '../uiStore'
import { createAbilityBuffAsideCards, resolveAbilityAsideCards } from './EffectTooltip'
import {
  FloatingInfoCard,
  FloatingPopover,
  FloatingTooltip,
  type FloatingAsideCard,
  type FloatingTone,
} from './FloatingPopover'
import {
  BonusAsideList,
  ItemIcon,
  ItemReference,
  extractBuffBonusItems,
  resolveItemTooltipAsideCards,
} from './ItemDisplay'
import { MonsterTooltipTarget } from './MonsterDisplay'
import { PillBonusLabel, type PillTone } from './PillLabel'
import { RootAspectRadarChart } from './RootAspectRadarChart'
import { StatGlyph, resolveStatGlyph } from './StatGlyph'
import { TraitInfoCard, resolveTraitAsideCards } from './TraitBarItem'
import { ValueText } from './ValueText'

/** 导出统一的富文本片段类型，供日志、战斗记录与更新日志等富文本容器共享。 */
export type RichContentSegment = LogSegment

function joinClasses(...parts: Array<string | undefined | false>): string {
  return parts.filter(Boolean).join(' ')
}

function resolveBuildingGroupLabel(group: string, t: TFunction): string {
  if (group === 'production') return t('combat.nearby.groupProduction')
  if (group === 'combat') return t('combat.nearby.groupCombat')
  return t('combat.nearby.groupOther')
}

function resolveActionTypeLabel(actionType: ActionDefinition['type'], t: TFunction): string {
  switch (actionType) {
    case 'cultivation': return t('app.changelog.ref.actionTypeCultivation')
    case 'exploration': return t('app.changelog.ref.actionTypeExploration')
    case 'alchemy': return t('app.changelog.ref.actionTypeAlchemy')
    case 'combat': return t('app.changelog.ref.actionTypeCombat')
    case 'enhancement': return t('app.changelog.ref.actionTypeEnhancement')
    default: return actionType
  }
}

function formatActionYieldSummary(
  yieldEntry: ActionYield,
  items: readonly ItemDefinition[],
  t: TFunction,
): string | null {
  switch (yieldEntry.kind) {
    case 'experience': {
      const pct = yieldEntry.percentage ?? (yieldEntry.ratio ? Math.round(yieldEntry.ratio * 100) : null)
      const label = t('app.changelog.ref.yieldExperience')
      return pct ? `${label} (${t('app.changelog.ref.yieldRatioPercent', { percent: formatNumber(pct) })})` : label
    }
    case 'rootexp': {
      const aspect = yieldEntry.aspect === 'attack' ? 'strength' : (yieldEntry.aspect ?? 'stamina')
      const aspectName = t(`insight.root.${aspect}.name`)
      const pct = yieldEntry.percentage ?? (yieldEntry.ratio ? Math.round(yieldEntry.ratio * 100) : null)
      return pct ? `${aspectName} (${t('app.changelog.ref.yieldRatioPercent', { percent: formatNumber(pct) })})` : aspectName
    }
    case 'mana':
      return t('app.changelog.ref.yieldMana', { amount: formatNumber(yieldEntry.amount) })
    case 'hitpoints':
      return t('app.changelog.ref.yieldHp', { amount: formatNumber(yieldEntry.amount) })
    case 'item': {
      if (!yieldEntry.id) return null
      const itemName = resolveAssetLabel(yieldEntry.id, items)
      return yieldEntry.amount > 1 ? `${itemName} ×${formatNumber(yieldEntry.amount)}` : itemName
    }
    case 'buff':
      return yieldEntry.name ?? (yieldEntry.id ? resolveTriggerBuffName(yieldEntry.id) : null)
    default:
      return null
  }
}

export interface AbilityInfoCardProps {
  /** 功法技能静态配置定义 */
  ability: CombatAbilityDefinition
  /** 展示等级（默认 1 级基础口径，战报与更新日志与图鉴一致） */
  level?: number
  /** 覆写右上角徽记文案 */
  badge?: ReactNode
  /** 覆写徽记色调 */
  badgeTone?: FloatingTone
  /** 插入描述区下方的附加区块（如图鉴解锁条件胶囊） */
  extraDescription?: ReactNode
  /** 底部操作或状态插槽 */
  footer?: ReactNode
  className?: string
}

/**
 * 功法技能图鉴级主悬浮卡片：复用 describeAbility 与六维雷达图，
 * 供万象图鉴、实时日志与更新日志引用共享，避免卡片结构重复实现。
 */
export const AbilityInfoCard = memo(function AbilityInfoCard({
  ability,
  level = 1,
  badge,
  badgeTone = 'qi',
  extraDescription,
  footer,
  className,
}: AbilityInfoCardProps): React.JSX.Element {
  const tooltip = useMemo(() => describeAbility(ability, level), [ability, level])

  return (
    <FloatingInfoCard
      badge={badge ?? tooltip.badge}
      badgeTone={badgeTone}
      className={className}
      titleIcon={resolveItemIconUrl(ability.abilityId)}
      description={
        <div>
          {tooltip.description && (
            <div className="codex-effect-text" style={{ marginBottom: 6 }}>
              <ValueText className="effect-value-capsule" text={tooltip.description} />
            </div>
          )}
          {tooltip.radarMultipliers && (
            <RootAspectRadarChart
              efficiency={tooltip.radarEfficiency}
              multipliers={tooltip.radarMultipliers}
            />
          )}
          {extraDescription}
        </div>
      }
      footer={footer}
      lines={[
        ...(tooltip.lines?.map(line => (
          <ValueText className="effect-value-capsule" key={line} text={line} />
        )) ?? []),
        ...(tooltip.bonuses?.map(bonus => {
          const glyph = resolveStatGlyph(bonus.key) || resolveStatGlyph(bonus.label)
          return (
            <PillBonusLabel
              icon={glyph ? <StatGlyph glyph={glyph} size={14.5} /> : undefined}
              key={bonus.key}
              label={bonus.label}
              tone={bonus.tone as PillTone}
              value={bonus.value}
            />
          )
        }) ?? []),
      ]}
      title={ability.name}
    />
  )
})

export interface BuildingInfoCardProps {
  /** 公共建筑静态配表定义 */
  building: BuildingDefinition
  /** 物品目录（用于解析一级建造材料图标与名称） */
  items?: readonly ItemDefinition[]
  /** 覆写徽记文案（默认使用所需境界门槛） */
  badge?: ReactNode
  /** 覆写徽记色调 */
  badgeTone?: FloatingTone
  /** 附加解锁状态节点（供万象图鉴注入） */
  unlockCondition?: ReactNode
  className?: string
}

/**
 * 公共建筑图鉴级主悬浮卡片：复用 getBuildingBonusFormulas（1 级基准 + 每级增幅）与建造材料清单，
 * 供万象图鉴与富文本建筑引用共用。
 */
export const BuildingInfoCard = memo(function BuildingInfoCard({
  building,
  items = [],
  badge,
  badgeTone = 'jade',
  unlockCondition,
  className,
}: BuildingInfoCardProps): React.JSX.Element {
  const t = useT()
  const groupLabel = resolveBuildingGroupLabel(building.group, t)
  const bonusFormulas = useMemo(
    () => getBuildingBonusFormulas(building, (key, def) => t(key) || def),
    [building, t],
  )
  const defaultBadge = formatRealmUnlockCondition(building.requiredRealmLevel)

  return (
    <FloatingInfoCard
      badge={badge ?? defaultBadge}
      badgeTone={badgeTone}
      className={className}
      description={
        <div>
          {building.description && (
            <div style={{ marginBottom: 6, color: 'var(--text-dim)', fontSize: 'var(--text-sm)', lineHeight: 1.5 }}>
              {building.description}
            </div>
          )}

          {bonusFormulas.length > 0 ? (
            <div className="codex-hover-section" style={{ marginTop: 0, paddingTop: 4, borderTop: 'none', marginBottom: 6 }}>
              <span className="codex-hover-title">{t('app.codex.buildingEffect')}</span>
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
              <span className="codex-hover-title">{t('app.codex.buildingEffect')}</span>
              <div className="codex-effect-text">{building.effect}</div>
            </div>
          ) : null}

          {building.materials && building.materials.length > 0 && (
            <div className="codex-hover-section" style={{ marginTop: 0, paddingTop: 4, borderTop: 'none', marginBottom: 6 }}>
              <span className="codex-hover-title">{t('app.codex.buildingMaterials')}：</span>
              <div className="codex-chips-wrap">
                {building.materials.map(mat => {
                  const matName = resolveAssetLabel(mat.itemId, items)
                  return (
                    <span className="codex-chip" key={mat.itemId}>
                      <ItemIcon itemId={mat.itemId} />
                      {matName} ×{formatNumber(mat.quantity)}
                    </span>
                  )
                })}
              </div>
            </div>
          )}

          {unlockCondition}
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
})

/** 行内功法/神通引用组件：悬浮展示完整图鉴级主卡与战内 Buff/变招副卡片。 */
export const AbilityReference = memo(function AbilityReference({
  abilityId,
  label,
  level = 1,
  className,
}: {
  abilityId: string
  label?: string
  level?: number
  className?: string
}): React.JSX.Element {
  const t = useT()
  const ability = useGameStore(state => findAbilityInCatalog(state.abilities, abilityId))
  const displayLabel = resolveEntityName(t, 'ability', abilityId, label, ability?.name)

  const renderAsideCards = useCallback(
    () => (ability ? resolveAbilityAsideCards(ability, undefined, 'qi', level) : []),
    [ability, level],
  )

  if (!ability) {
    return (
      <FloatingTooltip placement="top" title={t('combat.log.abilityTooltip', { label: displayLabel })} tone="qi">
        <span className={joinClasses('log-reference log-reference--ability', className)}>{displayLabel}</span>
      </FloatingTooltip>
    )
  }

  return (
    <FloatingPopover
      asideCards={renderAsideCards}
      closeDelay={120}
      content={<AbilityInfoCard ability={ability} level={level} />}
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      size="medium"
      trigger="hover"
      triggerClassName={joinClasses('log-reference log-reference--ability', className)}
    >
      <ItemIcon itemId={abilityId} />
      {displayLabel}
    </FloatingPopover>
  )
})

/** 行内妖兽/遭遇战引用组件：直接复用 MonsterTooltipTarget，完整展示属性、技能与掉落详情。 */
export const EnemyReference = memo(function EnemyReference({
  enemyId,
  kind = 'enemy',
  label,
  className,
}: {
  enemyId: string
  kind?: 'enemy' | 'encounter'
  label?: string
  className?: string
}): React.JSX.Element {
  const t = useT()
  const encounters = useGameStore(state => state.encounters)
  const maps = useGameStore(state => state.maps)
  const encounter = kind === 'encounter' ? findEncounterInCatalog(encounters, enemyId) : undefined
  const combatant = findCombatantInCatalog(encounters, maps, enemyId)
  const monster = encounter?.enemy ?? combatant
  const displayLabel = resolveEntityName(t, 'enemy', enemyId, label, monster?.name, encounter?.name)
  const classes = joinClasses(`log-reference log-reference--${kind}`, className)

  if (monster) {
    return (
      <MonsterTooltipTarget
        monster={monster}
        placement="bottom"
        triggerClassName={classes}
      >
        {displayLabel}
      </MonsterTooltipTarget>
    )
  }

  return (
    <FloatingTooltip placement="top" title={t('combat.log.enemyTooltip', { label: displayLabel })} tone="gold">
      <span className={classes}>{displayLabel}</span>
    </FloatingTooltip>
  )
})

/** 行内特质引用组件：悬浮复用 TraitInfoCard 与关联战内 Buff 副卡，保留品质高亮色调。 */
export const TraitReference = memo(function TraitReference({
  traitId,
  label,
  level = 1,
  className,
}: {
  traitId: string
  label?: string
  level?: number
  className?: string
}): React.JSX.Element {
  const t = useT()
  const def = TRAIT_DEFINITIONS[traitId]
  const displayLabel = resolveEntityName(t, 'trait', traitId, label, def?.name)

  const renderAsideCards = useCallback(
    () => (def ? resolveTraitAsideCards(def, level) : []),
    [def, level],
  )

  if (!def) {
    return (
      <FloatingTooltip placement="top" title={t('app.changelog.ref.traitTooltip', { label: displayLabel })} tone="gold">
        <span className={joinClasses('log-reference log-reference--trait', className)}>{displayLabel}</span>
      </FloatingTooltip>
    )
  }

  return (
    <FloatingPopover
      asideCards={renderAsideCards}
      closeDelay={120}
      content={<TraitInfoCard def={def} level={level} />}
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      size="medium"
      tabIndex={0}
      trigger="hover"
      triggerClassName={joinClasses('log-reference log-reference--trait', `log-reference--trait-q${def.quality}`, className)}
    >
      {displayLabel}
    </FloatingPopover>
  )
})

/** 行内公共建筑引用组件：悬浮复用 BuildingInfoCard，展示全阶成长曲线与材料清单。 */
export const BuildingReference = memo(function BuildingReference({
  buildingId,
  label,
  className,
}: {
  buildingId: string
  label?: string
  className?: string
}): React.JSX.Element {
  const t = useT()
  const buildings = useGameStore(state => state.buildings)
  const items = useGameStore(state => state.items)
  const building = useMemo(
    () => buildings.find(entry => entry.buildingId === buildingId),
    [buildings, buildingId],
  )
  const displayLabel = resolveEntityName(t, 'building', buildingId, label, building?.name)

  if (!building) {
    return (
      <FloatingTooltip placement="top" title={t('app.changelog.ref.buildingTooltip', { label: displayLabel })} tone="jade">
        <span className={joinClasses('log-reference log-reference--building', className)}>{displayLabel}</span>
      </FloatingTooltip>
    )
  }

  return (
    <FloatingPopover
      closeDelay={120}
      content={<BuildingInfoCard building={building} items={items} />}
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      size="medium"
      tabIndex={0}
      trigger="hover"
      triggerClassName={joinClasses('log-reference log-reference--building', className)}
    >
      {displayLabel}
    </FloatingPopover>
  )
})

interface ResolvedBuffCard {
  title: string
  badge: string
  tone: FloatingTone
  subtitle: string
  content?: ReactNode
  cardLines?: readonly (string | ReactNode)[]
  description?: string
  durationSeconds?: number
}

function resolveBuffCard(
  buffId: string,
  abilities: readonly CombatAbilityDefinition[],
  items: readonly ItemDefinition[],
  actions: readonly ActionDefinition[],
  t: TFunction,
): ResolvedBuffCard | null {
  // 1. 优先从特质定义（含移形、燃烧、隐身等核心机制）查找，复用 resolveTraitAsideCards
  for (const trait of Object.values(TRAIT_DEFINITIONS)) {
    if (trait.buff?.buffId !== buffId) continue
    const card = resolveTraitAsideCards(trait, 1)[0]
    if (!card) continue
    return {
      title: trait.buff.name || trait.name,
      badge: card.mark ?? t('app.changelog.ref.buffBadge'),
      tone: card.tone ?? 'purple',
      subtitle: t('app.changelog.ref.buffSourceTrait', { name: trait.name }),
      content: card.content,
      cardLines: card.lines,
      description: trait.description,
      durationSeconds: trait.buff.durationSeconds,
    }
  }

  // 2. 检索功法所挂接的战内 Buff（含常规 buff/buffs、命中附加 onHitBuff/onHitBuffs、自身附加 selfBuff/selfBuffs），复用 createAbilityBuffAsideCards
  for (const ability of abilities) {
    const primarySpecs = ability.buffs && ability.buffs.length > 0
      ? ability.buffs
      : ability.buff ? [ability.buff] : []
    const onHitSpecs = ability.onHitBuffs && ability.onHitBuffs.length > 0
      ? ability.onHitBuffs
      : ability.onHitBuff ? [ability.onHitBuff] : []
    const selfSpecs = ability.selfBuffs && ability.selfBuffs.length > 0
      ? ability.selfBuffs
      : ability.selfBuff ? [ability.selfBuff] : []
    const seenBuffIds = new Set<string>()
    const specs = [...primarySpecs, ...onHitSpecs, ...selfSpecs].filter((spec, index) => {
      const key = spec.buffId || `__idx_${index}`
      if (seenBuffIds.has(key)) return false
      seenBuffIds.add(key)
      return true
    })
    const matchedIndex = specs.findIndex(spec => spec.buffId === buffId)
    if (matchedIndex < 0) continue
    const spec = specs[matchedIndex]
    const card: FloatingAsideCard | undefined = createAbilityBuffAsideCards(ability, 1)[matchedIndex]
    if (!card) continue
    return {
      title: spec.name || resolveTriggerBuffName(buffId),
      badge: card.mark ?? t('app.changelog.ref.buffBadge'),
      tone: card.tone ?? 'qi',
      subtitle: t('app.changelog.ref.buffSourceAbility', { name: ability.name }),
      content: card.content,
      cardLines: card.lines,
      durationSeconds: spec.durationSeconds,
    }
  }

  // 3. 检索丹药与阵法物品所挂接的持续/战内 Buff，复用 resolveItemTooltipAsideCards
  for (const item of items) {
    const buffDef = item.buffEffects?.find(entry => entry.buffId === buffId)
    if (!buffDef) continue
    const cards = resolveItemTooltipAsideCards(item.itemId)
    const matched = cards.find(card => card.title === buffDef.name) ?? cards[0]
    if (!matched) continue
    return {
      title: buffDef.name || item.name,
      badge: matched.mark ?? t('app.changelog.ref.buffBadge'),
      tone: matched.tone ?? 'emerald',
      subtitle: t('app.changelog.ref.buffSourceItem', { name: item.name }),
      content: matched.content,
      cardLines: matched.lines,
      durationSeconds: buffDef.durationSeconds,
    }
  }

  // 4. 检索行动收益付与的 Buff
  for (const action of actions) {
    const buffYield = action.yields?.find(y => y.kind === 'buff' && y.id === buffId)
    if (!buffYield) continue
    const bonusItems = extractBuffBonusItems({
      buffId,
      name: buffYield.name ?? buffId,
      effects: buffYield.modifiers?.effects,
      statRatios: buffYield.modifiers?.statRatios,
    })
    return {
      title: buffYield.name ?? resolveTriggerBuffName(buffId),
      badge: t('app.changelog.ref.buffBadge'),
      tone: 'jade',
      subtitle: t('app.changelog.ref.buffSourceAction', { name: action.name }),
      content: bonusItems.length > 0 ? <BonusAsideList items={bonusItems} /> : null,
      durationSeconds: buffYield.durationSeconds ?? undefined,
    }
  }

  return null
}

/** 行内状态 Buff 引用组件：自动回查特质、功法、物品与行动中的状态数值效果通道。 */
export const BuffReference = memo(function BuffReference({
  buffId,
  label,
  className,
}: {
  buffId: string
  label?: string
  className?: string
}): React.JSX.Element {
  const t = useT()
  const abilities = useGameStore(state => state.abilities)
  const items = useGameStore(state => state.items)
  const actions = useGameStore(state => state.actions)

  const resolved = useMemo(
    () => resolveBuffCard(buffId, abilities, items, actions, t),
    [abilities, items, actions, buffId, t],
  )
  const fallbackName = resolveTriggerBuffName(buffId)
  const displayLabel = resolveEntityName(t, 'buff', buffId, label, resolved?.title, fallbackName)

  if (!resolved) {
    return (
      <FloatingTooltip placement="top" title={t('app.changelog.ref.buffTooltip', { label: displayLabel })} tone="purple">
        <span className={joinClasses('log-reference log-reference--buff', className)}>{displayLabel}</span>
      </FloatingTooltip>
    )
  }

  return (
    <FloatingPopover
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={resolved.badge}
          badgeTone={resolved.tone}
          description={
            <div>
              {resolved.content}
              {resolved.description && (
                <div className="codex-effect-text" style={{ marginTop: resolved.content ? 6 : 0 }}>
                  <ValueText className="effect-value-capsule" text={resolved.description} />
                </div>
              )}
            </div>
          }
          lines={[
            ...(resolved.cardLines ?? []),
            ...(resolved.durationSeconds && resolved.durationSeconds > 0
              ? [t('app.changelog.ref.buffDuration', { seconds: formatNumber(resolved.durationSeconds) })]
              : []),
          ]}
          subtitle={resolved.subtitle}
          title={resolved.title}
        />
      }
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      size="medium"
      tabIndex={0}
      trigger="hover"
      triggerClassName={joinClasses('log-reference log-reference--buff', className)}
    >
      {displayLabel}
    </FloatingPopover>
  )
})

/** 行内行动引用组件：悬浮展示行动所需等级、单轮耗时、消耗与期望产出。 */
export const ActionReference = memo(function ActionReference({
  actionId,
  label,
  className,
}: {
  actionId: string
  label?: string
  className?: string
}): React.JSX.Element {
  const t = useT()
  const actions = useGameStore(state => state.actions)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const encounters = useGameStore(state => state.encounters)

  const action = useMemo(
    () => findActionInCatalog(actions, actionId) ?? resolveActionDefinition(actionId),
    [actions, actionId],
  )
  const displayLabel = resolveEntityName(t, 'action', actionId, label, action?.name, t(`game.action.${actionId}.name`))

  if (!action) {
    return (
      <FloatingTooltip placement="top" title={t('app.changelog.ref.actionTooltip', { label: displayLabel })} tone="gold">
        <span className={joinClasses('log-reference log-reference--action', className)}>{displayLabel}</span>
      </FloatingTooltip>
    )
  }

  const level = resolveActionLevel(action, items, equipment, encounters)
  const typeLabel = resolveActionTypeLabel(action.type, t)
  const inputEntries = action.inputs ? Object.entries(action.inputs) : []
  const yieldSummaries = (action.yields ?? [])
    .map(y => formatActionYieldSummary(y, items, t))
    .filter((entry): entry is string => Boolean(entry))

  return (
    <FloatingPopover
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={`Lv.${level}`}
          badgeTone="gold"
          description={
            <div>
              {action.description && (
                <div className="codex-effect-text" style={{ marginBottom: 6 }}>
                  {action.description}
                </div>
              )}
              {action.foundationCostRatio && action.foundationCostRatio > 0 ? (
                <div className="codex-effect-text" style={{ marginBottom: 6 }}>
                  {t('app.changelog.ref.actionFoundationCost', { percent: formatNumber(action.foundationCostRatio * 100) })}
                </div>
              ) : null}
              {inputEntries.length > 0 && (
                <div className="codex-hover-section" style={{ marginTop: 4, paddingTop: 6 }}>
                  <span className="codex-hover-title">{t('app.changelog.ref.actionCostLabel')}</span>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 4 }}>
                    {inputEntries.map(([inId, count]) => (
                      <span className="codex-stat-line" key={inId} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                        <ItemIcon itemId={inId} />
                        <span>{resolveAssetLabel(inId, items, equipment)} ×{formatNumber(count)}</span>
                      </span>
                    ))}
                  </div>
                </div>
              )}
              {yieldSummaries.length > 0 && (
                <div className="codex-hover-section" style={{ marginTop: 6, paddingTop: 6 }}>
                  <span className="codex-hover-title">{t('app.changelog.ref.actionYieldLabel')}</span>
                  <div className="codex-effect-text" style={{ marginTop: 2 }}>
                    <ValueText className="effect-value-capsule" text={yieldSummaries.join('、')} />
                  </div>
                </div>
              )}
            </div>
          }
          stats={
            action.durationSeconds > 0
              ? [{ label: t('app.changelog.ref.actionDurationLabel'), value: t('app.changelog.ref.actionDuration', { seconds: formatNumber(action.durationSeconds) }), tone: 'qi' }]
              : undefined
          }
          subtitle={t('app.changelog.ref.actionSubtitle', { type: typeLabel })}
          title={action.name}
        />
      }
      gap={8}
      interactive={false}
      openDelay={60}
      placement="bottom"
      size="medium"
      tabIndex={0}
      trigger="hover"
      triggerClassName={joinClasses('log-reference log-reference--action', className)}
    >
      {displayLabel}
    </FloatingPopover>
  )
})

function findMapNode(
  maps: readonly WorldMapDefinition[],
  nodeId: string,
): { map: WorldMapDefinition; node: WorldNodeDefinition } | null {
  for (const map of maps) {
    const node = map.nodes.find(entry => entry.nodeId === nodeId)
    if (node) return { map, node }
  }
  return null
}

/** 行内地点/节点引用组件：悬浮展示灵脉倍率、栖息生灵与设施摘要，点击可直达大地图对应节点。 */
export const LocationReference = memo(function LocationReference({
  locationId,
  label,
  className,
  onNavigate,
}: {
  locationId: string
  label?: string
  className?: string
  onNavigate?: () => void
}): React.JSX.Element {
  const t = useT()
  const maps = useGameStore(state => state.maps)
  const found = useMemo(() => findMapNode(maps, locationId), [maps, locationId])
  const displayLabel = resolveEntityName(t, 'location', locationId, label, found?.node.name)

  const handleLocate = useCallback(() => {
    if (!found) return
    useUiStore.getState().navigateToMapNode(locationId)
    onNavigate?.()
  }, [found, locationId, onNavigate])

  const handleKeyDown = useCallback((event: KeyboardEvent<HTMLSpanElement>) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      handleLocate()
    }
  }, [handleLocate])

  if (!found) {
    return (
      <FloatingTooltip placement="top" title={t('app.changelog.ref.locationTooltip', { label: displayLabel })} tone="jade">
        <span className={joinClasses('log-reference log-reference--location', className)}>{displayLabel}</span>
      </FloatingTooltip>
    )
  }

  const { map, node } = found
  const stats = [
    ...(node.cultivationMultiplier > 0 && Math.abs(node.cultivationMultiplier - 1) > 0.001
      ? [{ label: t('app.changelog.ref.locationSpiritVein'), value: `×${formatNumber(node.cultivationMultiplier)}`, tone: 'qi' as const }]
      : []),
    ...(node.encounterIds.length > 0
      ? [{ label: t('app.changelog.ref.locationEncounters'), value: t('app.changelog.ref.locationEncounterCount', { count: node.encounterIds.length }), tone: 'amber' as const }]
      : []),
  ]

  return (
    <FloatingPopover
      closeDelay={120}
      content={
        <FloatingInfoCard
          badge={map.name}
          badgeTone="jade"
          description={
            <div>
              {node.description && (
                <div className="codex-effect-text" style={{ marginBottom: 6 }}>{node.description}</div>
              )}
              {node.buildings.length > 0 && (
                <div className="codex-hover-section" style={{ marginTop: 4, paddingTop: 6 }}>
                  <span className="codex-hover-title">{t('app.changelog.ref.locationBuildings')}</span>
                  <div className="codex-effect-text" style={{ marginTop: 2 }}>
                    {node.buildings.map(b => b.name).join('、')}
                  </div>
                </div>
              )}
            </div>
          }
          footer={<span className="codex-stat-line">{t('app.changelog.ref.locationClickHint')}</span>}
          stats={stats.length > 0 ? stats : undefined}
          title={node.name}
        />
      }
      displayContents
      interactive={false}
      openDelay={60}
      placement="bottom"
      size="medium"
    >
      <span
        className={joinClasses('log-reference log-reference--location log-reference--interactive', className)}
        onClick={handleLocate}
        onKeyDown={handleKeyDown}
        role="button"
        tabIndex={0}
      >
        {displayLabel}
      </span>
    </FloatingPopover>
  )
})

/** 渲染单个富文本解析片段（供实时日志分段渲染与通用 RichTemplateText 共用）。 */
export const RichContentSegmentView = memo(function RichContentSegmentView({
  segment,
  onNavigate,
}: {
  segment: RichContentSegment
  onNavigate?: () => void
}): React.JSX.Element {
  const t = useT()
  if (segment.type === 'text') {
    return <ValueText className="log-value-capsule" text={segment.value} />
  }
  switch (segment.kind) {
    case 'item':
      return (
        <ItemReference
          amount={segment.amount}
          className="log-reference log-reference--item"
          itemId={segment.id}
          label={segment.label}
        />
      )
    case 'ability':
      return <AbilityReference abilityId={segment.id} label={segment.label} />
    case 'enemy':
    case 'encounter':
      return <EnemyReference enemyId={segment.id} kind={segment.kind} label={segment.label} />
    case 'trait':
      return <TraitReference label={segment.label} traitId={segment.id} />
    case 'building':
      return <BuildingReference buildingId={segment.id} label={segment.label} />
    case 'buff':
      return <BuffReference buffId={segment.id} label={segment.label} />
    case 'action':
      return <ActionReference actionId={segment.id} label={segment.label} />
    case 'location':
      return <LocationReference label={segment.label} locationId={segment.id} onNavigate={onNavigate} />
    case 'mechanic':
      return (
        <span
          className={`log-reference log-reference--mechanic log-reference--mechanic-${segment.id}`}
          data-mechanic={segment.id}
        >
          {resolveEntityName(t, 'mechanic', segment.id, segment.label)}
        </span>
      )
    case 'character':
      return (
        <span className="log-reference log-reference--character">
          {resolveEntityName(t, 'character', segment.id, segment.label)}
        </span>
      )
  }
})

/**
 * 通用模板富文本渲染组件：解析 `{{kind:id|label}}` 词条为可悬浮高亮引用，
 * 供实时日志、战报详情、更新日志页与首次更新公告弹窗共同调用。
 */
export function RichTemplateText({
  template,
  onNavigate,
}: {
  template: string
  onNavigate?: () => void
}): React.JSX.Element {
  const segments = useMemo(() => parseLogTemplate(template), [template])
  return (
    <>
      {segments.map((segment, index) => (
        <RichContentSegmentView key={index} onNavigate={onNavigate} segment={segment} />
      ))}
    </>
  )
}
