import { memo, useMemo, useState } from 'react'
import { calculateRealmInfo, getRootCumulativeExpForLevel } from '../game/combatConstants'
import { describeAbility, formatAbilityCategory, formatNumber } from '../game/format'
import { resolveItemIconUrl } from '../game/itemIcons'
import { ConditionedTile } from './ConditionedTile'
import { useGameStore } from '../game/gameStore'
import { findAbilityInCatalog, findEquipmentInCatalog, findItemInCatalog, resolveItemCategoryLabel, resolveItemMetadata, withItemGrade } from '../game/ids'
import { useSocialStore } from '../game/socialStore'
import { useT } from '../i18n'
import type { PlayerCardAutoUseSlotDto, PlayerCardDto } from '../network/protocol'
import { resolvePlayerActorId } from '../game/avatarStore'
import { CharacterAvatar } from './CharacterAvatar'
import { ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { FloatingTooltip } from './FloatingPopover'
import { Modal } from './Modal'
import { PillStatLabel, type PillTone } from './PillLabel'
import { RealmBadge } from './RealmBadge'
import { CombatAttributePanel } from './CombatAttributePanel'
import { ValuationHoverTarget } from './ValuationDisplay'

/** 战斗消耗品每组栏位数（与服务端 AutoUseBar.DefaultSlotCount 一致：药品 3 + 通用 3） */
const CONSUMABLE_GROUP_SLOTS = 3

/** 六维条目：i18n 键 id（attack 对应「力道」词条）、胶囊色调与取值字段。 */
const DIMENSION_DEFS: ReadonlyArray<{ id: string; tone: PillTone; levelKey: 'staminaLevel' | 'meridiansLevel' | 'attackLevel' | 'boneLevel' | 'agilityLevel' | 'consciousnessLevel'; expKey: 'staminaExp' | 'meridiansExp' | 'attackExp' | 'boneExp' | 'agilityExp' | 'consciousnessExp' }> = [
  { id: 'stamina', tone: 'emerald', levelKey: 'staminaLevel', expKey: 'staminaExp' },
  { id: 'meridians', tone: 'cyan', levelKey: 'meridiansLevel', expKey: 'meridiansExp' },
  { id: 'bone', tone: 'amber', levelKey: 'boneLevel', expKey: 'boneExp' },
  { id: 'strength', tone: 'crimson', levelKey: 'attackLevel', expKey: 'attackExp' },
  { id: 'consciousness', tone: 'purple', levelKey: 'consciousnessLevel', expKey: 'consciousnessExp' },
  { id: 'agility', tone: 'teal', levelKey: 'agilityLevel', expKey: 'agilityExp' },
]

/** 技艺条目：i18n 键 id 与胶囊色调 */
const TECHNIQUE_DEFS: ReadonlyArray<{ id: string; tone: PillTone }> = [
  { id: 'alchemy', tone: 'emerald' },
  { id: 'forging', tone: 'crimson' },
  { id: 'talisman', tone: 'amber' },
  { id: 'enhancement', tone: 'gold' },
  { id: 'formation', tone: 'purple' },
  { id: 'gathering', tone: 'teal' },
  { id: 'mining', tone: 'bronze' },
]

/** 六维经验进度（与内视页同一口径：当前级内经验/本级所需经验）。 */
function dimensionProgress(level: number, exp: number): { current: number; max: number; text: string } {
  const baseExp = getRootCumulativeExpForLevel(level)
  const nextExp = getRootCumulativeExpForLevel(level + 1.0)
  const required = Math.max(1, Math.floor(nextExp - baseExp))
  const current = Math.min(required, Math.max(0, Math.floor(exp - baseExp)))
  return { current, max: required, text: `${formatNumber(current)}/${formatNumber(required)}` }
}

/** 装备条目只读瓦片：复用装备面板的 tooltip + 瓦片链路，剥离穿脱交互。 */
const PlayerCardEquipmentTile = memo(function PlayerCardEquipmentTile({ itemId, enhancementLevel }: {
  itemId: string
  enhancementLevel: number
}): React.JSX.Element {
  const t = useT()
  const itemCatalog = useGameStore(state => state.items)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const equipmentDef = findEquipmentInCatalog(equipmentCatalog, itemId)
  const definition = findItemInCatalog(itemCatalog, itemId)
  const meta = resolveItemMetadata(itemId)
  const metadata = withItemGrade(
    meta ?? {
      label: equipmentDef?.name ?? itemId,
      glyph: '·',
      tone: 'gold',
      category: resolveItemCategoryLabel(equipmentDef?.slot, t),
      rarity: 'mundane',
      rarityLabel: '',
      description: equipmentDef?.description ?? definition?.description ?? '',
      source: '',
      usage: '',
    },
    equipmentDef?.tier ?? meta?.tier ?? 1,
  )
  const displayName = enhancementLevel > 0
    ? `${equipmentDef?.name ?? definition?.name ?? metadata.label} +${Math.floor(enhancementLevel)}`
    : (equipmentDef?.name ?? definition?.name ?? metadata.label)
  return (
    <ItemTooltipTarget
      className="equipment-slot-anchor"
      definition={definition}
      equipmentDefinition={equipmentDef}
      enhancementLevel={enhancementLevel}
      itemId={itemId}
      metadata={metadata}
    >
      <ItemTile
        category={resolveItemCategoryLabel(equipmentDef?.slot, t)}
        className="inventory-cell"
        enhancementLevel={enhancementLevel}
        itemId={itemId}
        level={metadata.level}
        name={displayName}
        rarity={metadata.rarity}
      />
    </ItemTooltipTarget>
  )
})
/** 技能条目只读瓦片：标准 ConditionedTile + ItemTile 格子。 */
const PlayerCardAbilityTile = memo(function PlayerCardAbilityTile({ abilityId, level }: {
  abilityId: string | null
  level: number
}): React.JSX.Element {
  const t = useT()
  const abilities = useGameStore(state => state.abilities)
  if (!abilityId) {
    return (
      <span className="item-use-slot-shell item-use-slot-shell--empty">
        <span className="item-use-slot item-use-slot--empty">
          <span>{t('social.card.slotEmpty')}</span>
        </span>
      </span>
    )
  }
  const ability = findAbilityInCatalog(abilities, abilityId)
  const iconUrl = resolveItemIconUrl(abilityId)
  const lv = Math.max(1, Math.floor(level))
  const ribbonLabel = ability?.element && ability.element !== 'none'
    ? t(`combat.encounter.element.${ability.element}`)
    : formatAbilityCategory(ability?.category)

  return (
    <ConditionedTile
      className="abilities-learned-cell"
      content={ability ? describeAbility(ability, lv) : null}
      triggerClassName="item-grid-picker-tooltip"
    >
      <span
        className="item-tile combat-ability-cell"
        data-rarity={ability?.discipline === 'internal' ? 'emperor' : 'mystic'}
      >
        <span aria-hidden="true" className="item-type-ribbon"><span>{ribbonLabel}</span></span>
        <span className="item-corner item-corner--top-right">Lv. {lv}</span>
        {iconUrl
          ? <img alt={ability?.name ?? abilityId} className="item-tile-icon" decoding="async" draggable={false} src={iconUrl} />
          : <strong className="item-tile-name">{ability?.name ?? abilityId}</strong>}
        <span aria-hidden="true" className="item-tile-glow" />
      </span>
    </ConditionedTile>
  )
})

/** 战斗消耗品只读瓦片；普通钱包和不可售离线储备分别展示，不合并数量。 */
const PlayerCardConsumableTile = memo(function PlayerCardConsumableTile({ slot }: {
  slot?: PlayerCardAutoUseSlotDto
}): React.JSX.Element {
  const t = useT()
  const itemCatalog = useGameStore(state => state.items)
  if (!slot?.itemId) {
    return (
      <span className="item-use-slot-shell item-use-slot-shell--empty">
        <span className="item-use-slot item-use-slot--empty">
          <span>{t('social.card.slotEmpty')}</span>
        </span>
      </span>
    )
  }
  const itemDef = findItemInCatalog(itemCatalog, slot.itemId)
  const meta = resolveItemMetadata(slot.itemId)
  const metadata = withItemGrade(
    meta ?? {
      label: itemDef?.name ?? slot.itemId,
      glyph: '🧪',
      tone: 'emerald',
      category: resolveItemCategoryLabel(itemDef?.category ?? 'consumable', t),
      rarity: 'mundane',
      rarityLabel: '',
      description: itemDef?.description ?? '',
      source: '',
      usage: '',
    },
    itemDef?.tier ?? meta?.tier ?? 1,
  )

  return (
    <span className="player-card-consumable-group">
      <ItemTooltipTarget
        amount={slot.quantity}
        definition={itemDef}
        itemId={slot.itemId}
        metadata={metadata}
      >
        <span className="item-use-slot-shell" data-rarity={metadata.rarity}>
          <ItemTile
            amount={slot.quantity}
            category={metadata.category}
            className="item-use-slot-cell"
            itemId={slot.itemId}
            name={itemDef?.name ?? metadata.label}
            rarity={metadata.rarity}
          />
          {!slot.enabled && <span className="player-card-slot-disabled-badge">{t('social.card.slotDisabled')}</span>}
        </span>
      </ItemTooltipTarget>
      <span className="player-card-consumable-label">
        {t('social.card.availableQuantity', { quantity: formatNumber(slot.quantity) })}
      </span>
      {slot.offlineReserveQuantity > 0 && (
        <FloatingTooltip title={t('social.card.offlineReserveHint')}>
          <span className="player-card-consumable-label">
            {t('social.card.offlineReserveQuantity', { quantity: formatNumber(slot.offlineReserveQuantity) })}
          </span>
        </FloatingTooltip>
      )}
    </span>
  )
})

/** 角色信息卡正文：身份头 + 修为/身家 + 六维 + 装备/功法/战斗物品分组展示。 */
const PlayerCardBody = memo(function PlayerCardBody({ card }: { card: PlayerCardDto }): React.JSX.Element {
  const t = useT()
  const equipmentCatalog = useGameStore(state => state.equipment)
  const [activeTab, setActiveTab] = useState<'base' | 'special' | 'loadout' | 'dimensions' | 'techniques'>('base')
  const myPlayerId = useGameStore(state => state.playerId)
  const isSelf = myPlayerId !== null && myPlayerId === card.playerId

  // 名片瓶颈由服务端按灵根乘区下发（experienceToNext），徽章进度沿用同一口径，
  // 否则入天门玩家名片会在裸瓶颈处显示圆满（总纲 §7.7.5.1）。
  const realmInfo = useMemo(
    () => calculateRealmInfo(card.experience, card.realmLevel,
      card.experienceToNext > 0 ? card.experienceToNext : undefined),
    [card.experience, card.realmLevel, card.experienceToNext],
  )

  const { combatEquipment, toolEquipment } = useMemo(() => {
    const combat: NonNullable<PlayerCardDto['equipment']> = []
    const tools: NonNullable<PlayerCardDto['equipment']> = []
    for (const entry of card.equipment ?? []) {
      const def = findEquipmentInCatalog(equipmentCatalog, entry.itemId)
      if (def?.slot.startsWith('tool-')) tools.push(entry)
      else combat.push(entry)
    }
    return { combatEquipment: combat, toolEquipment: tools }
  }, [card.equipment, equipmentCatalog])

  const potionSlots = (card.autoUseSlots ?? []).filter(slot => slot.group === 'potion')
  const generalSlots = (card.autoUseSlots ?? []).filter(slot => slot.group !== 'potion')

  return (
    <div className="player-card">
      <header className="player-card-hero">
        {isSelf && (
          <div className="player-card-self-ribbon" aria-label={t('combat.nearby.isSelf')}>
            <span>{t('combat.nearby.isSelf')}</span>
          </div>
        )}
        {/* 上部 80%：左 40% 形象，右 60% 名字与身家 */}
        <div className="player-card-hero-top">
          <div className="player-card-hero-avatar">
            <CharacterAvatar actorId={resolvePlayerActorId(card.avatar)} name={card.displayName ?? ''} role="player" />
          </div>
          <div className="player-card-hero-info">
            <div className="player-card-hero-name-row">
              <h3 className="player-card-name" id="player-card-title">{card.displayName ?? '—'}</h3>
              <span className={`chat-presence chat-presence--${card.online ? 'online' : 'offline'}`}>
                {card.online ? t('social.online') : t('social.offline')}
              </span>
            </div>
            <ValuationHoverTarget breakdown={card.valuationBreakdown} valuation={card.valuation}>
              <div className="player-card-hero-valuation" style={{ cursor: 'help' }}>
                <span className="player-card-stat-label">{t('social.card.valuation')}：</span>
                <strong className="player-card-stat-value tone-gold">{formatNumber(Math.floor(card.valuation))}</strong>
              </div>
            </ValuationHoverTarget>
          </div>
        </div>
        {/* 下部 20%：三段式牌匾 */}
        <div className="player-card-hero-bottom">
          <RealmBadge realmInfo={realmInfo} />
        </div>
      </header>

      <div className="player-card-stats">
        <div className="player-card-stat">
          <span className="player-card-stat-label">{t('social.card.cultivation')}</span>
          <strong className="player-card-stat-value">
            {formatNumber(Math.floor(card.experience))}
            {card.experienceToNext > 0 && ` / ${formatNumber(card.experienceToNext)}`}
          </strong>
        </div>
        <div className="player-card-stat">
          <span className="player-card-stat-label">{t('social.card.combatExp')}</span>
          <strong className="player-card-stat-value">{formatNumber(Math.floor(card.combatExperience))}</strong>
        </div>
        <ValuationHoverTarget breakdown={card.valuationBreakdown} valuation={card.valuation}>
          <div className="player-card-stat" style={{ cursor: 'help' }}>
            <span className="player-card-stat-label">{t('social.card.valuation')}</span>
            <strong className="player-card-stat-value tone-gold">{formatNumber(Math.floor(card.valuation))}</strong>
          </div>
        </ValuationHoverTarget>
      </div>

      {/* 5 大分组 Tab 切换 */}
      <div className="insight-sub-tabs" role="tablist">
        <button
          className={`insight-sub-tab-btn ${activeTab === 'base' ? 'active' : ''}`}
          onClick={() => setActiveTab('base')}
          type="button"
        >
          {t('insight.combat.groupBase')}
        </button>
        <button
          className={`insight-sub-tab-btn ${activeTab === 'special' ? 'active' : ''}`}
          onClick={() => setActiveTab('special')}
          type="button"
        >
          {t('insight.combat.groupSpecial')}
        </button>
        <button
          className={`insight-sub-tab-btn ${activeTab === 'loadout' ? 'active' : ''}`}
          onClick={() => setActiveTab('loadout')}
          type="button"
        >
          {t('insight.combat.groupLoadout')}
        </button>
        <button
          className={`insight-sub-tab-btn ${activeTab === 'dimensions' ? 'active' : ''}`}
          onClick={() => setActiveTab('dimensions')}
          type="button"
        >
          {t('social.card.sixDimensions')}
        </button>
        <button
          className={`insight-sub-tab-btn ${activeTab === 'techniques' ? 'active' : ''}`}
          onClick={() => setActiveTab('techniques')}
          type="button"
        >
          {t('social.card.techniqueLevels')}
        </button>
      </div>

      {/* 基础战斗属性与特殊博弈属性 Tab */}
      {(activeTab === 'base' || activeTab === 'special') && (
        <section className="player-card-section player-card-combat-panel-wrap">
          {card.attributes ? (
            <CombatAttributePanel
              activeGroup={activeTab}
              attributes={card.attributes}
              hideTabs
            />
          ) : (
            <p className="nearby-empty">{t('insight.combat.attributesLoading')}</p>
          )}
        </section>
      )}

      {/* 装配 Tab：功法技能 -> 装备 -> 战斗消耗品（置于装备下方） */}
      {activeTab === 'loadout' && (
        <div className="player-card-tab-content">
          <section className="player-card-section">
            <h4 className="player-card-section-title">{t('social.card.abilities')}</h4>
            {(card.abilitySlots ?? []).length === 0 ? (
              <p className="player-card-empty">{t('social.card.noAbility')}</p>
            ) : (
              <div className="abilities-learned-grid">
                {(card.abilitySlots ?? []).map((slot, index) => (
                  <PlayerCardAbilityTile
                    abilityId={slot.abilityId}
                    key={slot.abilityId ?? `empty-${index}`}
                    level={slot.level}
                  />
                ))}
              </div>
            )}
          </section>

          <section className="player-card-section">
            <h4 className="player-card-section-title">{t('social.card.combatEquipment')}</h4>
            {combatEquipment.length === 0 ? (
              <p className="player-card-empty">{t('social.card.noEquipment')}</p>
            ) : (
              <div className="abilities-learned-grid">
                {combatEquipment.map(entry => (
                  <PlayerCardEquipmentTile enhancementLevel={entry.enhancementLevel} itemId={entry.itemId} key={`${entry.itemId}:${entry.enhancementLevel}`} />
                ))}
              </div>
            )}
          </section>

          {toolEquipment.length > 0 && (
            <section className="player-card-section">
              <h4 className="player-card-section-title">{t('social.card.toolEquipment')}</h4>
              <div className="abilities-learned-grid">
                {toolEquipment.map(entry => (
                  <PlayerCardEquipmentTile enhancementLevel={entry.enhancementLevel} itemId={entry.itemId} key={`${entry.itemId}:${entry.enhancementLevel}`} />
                ))}
              </div>
            </section>
          )}

          {/* 战斗消耗品：置于装备下方 */}
          <section className="player-card-section">
            <h4 className="player-card-section-title">{t('social.card.consumables')}</h4>
            {([
              { id: 'potion', label: t('social.card.potionGroup'), slots: potionSlots },
              { id: 'general', label: t('social.card.generalGroup'), slots: generalSlots },
            ]).map(group => (
              <div className="player-card-consumable-group" key={group.id}>
                <h5 className="player-card-consumable-label">{group.label}</h5>
                <div className="player-card-consumable-slots">
                  {Array.from({ length: CONSUMABLE_GROUP_SLOTS }, (_, index) => {
                    const slot = group.slots.find(entry => entry.slotIndex === index)
                    return <PlayerCardConsumableTile key={index} slot={slot} />
                  })}
                </div>
              </div>
            ))}
          </section>
        </div>
      )}

      {/* 六维 Tab */}
      {activeTab === 'dimensions' && (
        <section className="player-card-section">
          <div className="player-card-dims">
            {DIMENSION_DEFS.map(dim => {
              const level = Math.floor(Math.max(0, card.coreAttributes?.[dim.levelKey] ?? 0))
              return (
                <PillStatLabel
                  key={dim.id}
                  label={t(`insight.root.${dim.id}.name`)}
                  progress={dimensionProgress(level, card.coreAttributes?.[dim.expKey] ?? 0)}
                  tone={dim.tone}
                  tooltip={t(`insight.root.${dim.id}.desc`)}
                  value={level}
                />
              )
            })}
          </div>
        </section>
      )}

      {/* 生活技艺 Tab */}
      {activeTab === 'techniques' && (
        <section className="player-card-section">
          <div className="player-card-dims player-card-grid-2">
            {TECHNIQUE_DEFS.map(tech => {
              const level = Math.floor(card.techniqueLevels?.[tech.id] ?? 0)
              return (
                <PillStatLabel
                  key={tech.id}
                  label={t(`insight.technique.${tech.id}`)}
                  tone={tech.tone}
                  tooltip={t(`insight.technique.${tech.id}Tooltip`)}
                  tooltipTone="gold"
                  value={t('insight.technique.levelLabel', { level })}
                />
              )
            })}
          </div>
        </section>
      )}
    </div>
  )
})

/**
 * 队伍成员角色信息卡 (PlayerCardModal)
 *
 * §21.14：点击同道队伍成员按需请求服务端白名单投影（PlayerCardRequest → PlayerCard 应答），
 * 覆盖昵称/形象/序号/境界/修为/战斗经验/身家/六维/战斗装备/技艺装备/技能/战斗物品配置。
 * 状态完全挂在 socialStore（playerCard + playerCardRequestId）：断线与关闭统一清理在途请求。
 */
export const PlayerCardModal = memo(function PlayerCardModal(): React.JSX.Element | null {
  const t = useT()
  const card = useSocialStore(state => state.playerCard)
  const pending = useSocialStore(state => state.playerCardRequestId !== null)
  const closePlayerCard = useSocialStore(state => state.closePlayerCard)

  const open = pending || card !== null
  if (!open) return null

  const errorText = card?.error === 'forbidden'
    ? t('social.card.errorForbidden')
    : card?.error === 'not-found'
      ? t('social.card.errorNotFound')
      : card?.error
        ? t('social.card.errorGeneric')
        : null

  return (
    <Modal className="player-card-modal" labelledBy="player-card-title" onClose={closePlayerCard} open size="medium">
      {card === null && !errorText ? (
        <p className="player-card-loading">{t('social.card.loading')}</p>
      ) : errorText !== null ? (
        <p className="player-card-error" id="player-card-title">{errorText}</p>
      ) : card !== null ? (
        <PlayerCardBody card={card} />
      ) : null}
    </Modal>
  )
})
