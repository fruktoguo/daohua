import { memo, useMemo } from 'react'
import { calculateRealmInfo, getRootCumulativeExpForLevel } from '../game/combatConstants'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import { findAbilityInCatalog, findEquipmentInCatalog, findItemInCatalog, resolveItemCategoryLabel, resolveItemMetadata, withItemGrade } from '../game/ids'
import { useSocialStore } from '../game/socialStore'
import { useT } from '../i18n'
import type { PlayerCardDto } from '../network/protocol'
import { CharacterAvatar } from './CharacterAvatar'
import { ItemReference, ItemTile, ItemTooltipTarget } from './ItemDisplay'
import { Modal } from './Modal'
import { PillStatLabel, type PillTone } from './PillLabel'
import { RealmBadge } from './RealmBadge'
import { UnitNumber } from './UnitNumber'

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

/** 角色信息卡正文：身份头 + 修为/身家 + 六维 + 装备/功法/战斗物品分组展示。 */
const PlayerCardBody = memo(function PlayerCardBody({ card }: { card: PlayerCardDto }): React.JSX.Element {
  const t = useT()
  const abilities = useGameStore(state => state.abilities)
  const equipmentCatalog = useGameStore(state => state.equipment)
  const myPlayerId = useGameStore(state => state.playerId)
  const isSelf = myPlayerId !== null && myPlayerId === card.playerId

  const realmInfo = useMemo(() => calculateRealmInfo(card.experience, card.realmLevel), [card.experience, card.realmLevel])

  // 装备按槽位语义分两组：tool-* 为技艺工具槽位（服务端同槽唯一），其余归战斗装备；
  // 配表缺失的条目按战斗装备兜底展示，不静默丢数据。
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
            <CharacterAvatar name={card.displayName ?? ''} role="player" />
          </div>
          <div className="player-card-hero-info">
            <div className="player-card-hero-name-row">
              <h3 className="player-card-name" id="player-card-title">{card.displayName ?? '—'}</h3>
              <span className={`chat-presence chat-presence--${card.online ? 'online' : 'offline'}`}>
                {card.online ? t('social.online') : t('social.offline')}
              </span>
            </div>
            <div className="player-card-hero-valuation">
              <span className="player-card-stat-label">{t('social.card.valuation')}：</span>
              <strong className="player-card-stat-value tone-gold">{formatNumber(Math.floor(card.valuation))}</strong>
            </div>
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
        <div className="player-card-stat">
          <span className="player-card-stat-label">{t('social.card.valuation')}</span>
          <strong className="player-card-stat-value tone-gold">{formatNumber(Math.floor(card.valuation))}</strong>
        </div>
      </div>

      <section className="player-card-section">
        <h4 className="player-card-section-title">{t('social.card.sixDimensions')}</h4>
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

      {card.attributes && (
        <section className="player-card-section">
          <h4 className="player-card-section-title">{t('social.card.combatAttributes')}</h4>
          <div className="player-card-dims player-card-grid-2">
            <PillStatLabel label={t('insight.combat.hp')} tone="emerald" tooltip={t('insight.combat.hpTooltip')} tooltipTone="gold" value={<UnitNumber disableTooltip value={card.attributes.maxHitpoints} />} />
            <PillStatLabel label={t('insight.combat.mp')} tone="cyan" tooltip={t('insight.combat.mpTooltip')} tooltipTone="gold" value={<UnitNumber disableTooltip value={card.attributes.maxManapoints} />} />
            <PillStatLabel label={t('insight.combat.physAtk')} tone="crimson" tooltip={t('insight.combat.physAtkTooltip')} tooltipTone="gold" value={<UnitNumber disableTooltip value={card.attributes.physicalAttack} />} />
            <PillStatLabel label={t('insight.combat.spellAtk')} tone="purple" tooltip={t('insight.combat.spellAtkTooltip')} tooltipTone="gold" value={<UnitNumber disableTooltip value={card.attributes.spellAttack} />} />
            <PillStatLabel label={t('insight.combat.armor')} tone="bronze" tooltip={t('insight.combat.armorTooltip')} tooltipTone="gold" value={<UnitNumber disableTooltip value={card.attributes.armor} />} />
            <PillStatLabel label={t('insight.combat.magicRes')} tone="slate" tooltip={t('insight.combat.magicResTooltip')} tooltipTone="gold" value={<UnitNumber disableTooltip value={card.attributes.magicResistance} />} />
          </div>
        </section>
      )}

      {card.techniqueLevels && Object.keys(card.techniqueLevels).length > 0 && (
        <section className="player-card-section">
          <h4 className="player-card-section-title">{t('social.card.techniqueLevels')}</h4>
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

      <section className="player-card-section">
        <h4 className="player-card-section-title">{t('social.card.combatEquipment')}</h4>
        {combatEquipment.length === 0 ? (
          <p className="player-card-empty">{t('social.card.noEquipment')}</p>
        ) : (
          <div className="player-card-items">
            {combatEquipment.map(entry => (
              <PlayerCardEquipmentTile enhancementLevel={entry.enhancementLevel} itemId={entry.itemId} key={`${entry.itemId}:${entry.enhancementLevel}`} />
            ))}
          </div>
        )}
      </section>

      <section className="player-card-section">
        <h4 className="player-card-section-title">{t('social.card.toolEquipment')}</h4>
        {toolEquipment.length === 0 ? (
          <p className="player-card-empty">{t('social.card.noEquipment')}</p>
        ) : (
          <div className="player-card-items">
            {toolEquipment.map(entry => (
              <PlayerCardEquipmentTile enhancementLevel={entry.enhancementLevel} itemId={entry.itemId} key={`${entry.itemId}:${entry.enhancementLevel}`} />
            ))}
          </div>
        )}
      </section>

      <section className="player-card-section">
        <h4 className="player-card-section-title">{t('social.card.abilities')}</h4>
        {(card.abilitySlots ?? []).length === 0 ? (
          <p className="player-card-empty">{t('social.card.noAbility')}</p>
        ) : (
          <ul className="player-card-abilities">
            {(card.abilitySlots ?? []).map((slot, index) => (
              <li className="player-card-ability" key={slot.abilityId ?? `empty-${index}`}>
                <span className="player-card-ability-name">
                  {slot.abilityId === null
                    ? t('social.card.slotEmpty')
                    : findAbilityInCatalog(abilities, slot.abilityId)?.name ?? slot.abilityId}
                </span>
                <span className="player-card-ability-level">{t('social.card.abilityLevel', { level: Math.floor(slot.level) })}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

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
                if (!slot?.itemId) {
                  return <span className="player-card-slot-empty" key={index}>{t('social.card.slotEmpty')}</span>
                }
                return (
                  <ItemReference
                    itemId={slot.itemId}
                    key={index}
                    valueContent={(
                      <span className="underlined-value">
                        × {formatNumber(Math.floor(slot.quantity))}
                        {!slot.enabled && <em className="player-card-slot-off">{t('social.card.slotDisabled')}</em>}
                      </span>
                    )}
                  />
                )
              })}
            </div>
          </div>
        ))}
      </section>
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
