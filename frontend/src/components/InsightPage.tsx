import { memo, useMemo, useState } from 'react'
import { VectorCharacter } from './VectorCharacter'
import { RealmBadge } from './RealmBadge'
import { PillStatLabel, PillBonusLabel, type PillTone } from './PillLabel'
import { formatNumber } from '../game/format'
import {
  PercentValue,
  DEFAULT_ROOT_ATTRIBUTE_POINTS,
  BASELINE_COMBAT_ATTRIBUTES,
  calculateRealmInfo,
  calculateBaseAttributes,
} from '../game/combatConstants'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'

type InsightTab = 'roots' | 'foundation' | 'combat' | 'biography'
type RootsSubTab = 'sixDimensions' | 'baseAttributes'

export const InsightPage = memo(function InsightPage(): React.JSX.Element {
  const t = useT()
  const [activeTab, setActiveTab] = useState<InsightTab>('roots')
  const [rootsSubTab, setRootsSubTab] = useState<RootsSubTab>('sixDimensions')

  const name = useGameStore(state => state.name)
  const stats = useGameStore(state => state.stats)
  const isPendingBreakthrough = useGameStore(state => state.isPendingBreakthrough)
  const loadout = useGameStore(state => state.combat)
  const attributes = useGameStore(state => state.combatAttributes)
  const world = useGameStore(state => state.world)
  const buffs = useGameStore(state => state.buffs)

  // 境界计算体系 (大境界 · 位阶 · 小境界)
  const realmInfo = useMemo(() => calculateRealmInfo(stats.spirit ?? 0), [stats.spirit])

  // 六维根基换算定义 (横向展示：数值胶囊 + 属性加成胶囊列表)
  // 文本统一从 insight.root.* 词典取，此处只保留 id / tone / level / bonus 数值
  const dimensions = useMemo(() => {
    const staminaLevel: number = Math.max(10, loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) // 体魄 10
    const meridiansLevel: number = Math.max(10, loadout?.intelligenceLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) // 经脉 10
    const boneLevel: number = Math.max(10, loadout?.defenseLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) // 根骨 10
    const strengthLevel: number = Math.max(10, loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) // 力道 10
    const consciousnessLevel: number = Math.max(10, loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) // 神识 10
    const agilityLevel: number = Math.max(10, loadout?.talismanLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) // 身法 10

    return [
      {
        id: 'stamina',
        tone: 'emerald' as PillTone,
        level: staminaLevel,
        bonuses: [
          { value: `+${staminaLevel.toFixed(1)}%`, tone: 'emerald' as PillTone },
          { value: `+${staminaLevel.toFixed(1)}%`, tone: 'bronze' as PillTone },
          { value: `+${staminaLevel.toFixed(1)}%`, tone: 'emerald' as PillTone },
        ],
      },
      {
        id: 'meridians',
        tone: 'cyan' as PillTone,
        level: meridiansLevel,
        bonuses: [
          { value: `+${meridiansLevel.toFixed(1)}%`, tone: 'cyan' as PillTone },
          { value: `+${meridiansLevel.toFixed(1)}%`, tone: 'violet' as PillTone },
          { value: `+${meridiansLevel.toFixed(1)}%`, tone: 'cyan' as PillTone },
        ],
      },
      {
        id: 'bone',
        tone: 'violet' as PillTone,
        level: boneLevel,
        bonuses: [
          { value: `+${boneLevel.toFixed(1)}%`, tone: 'emerald' as PillTone },
          { value: `+${boneLevel.toFixed(1)}%`, tone: 'cyan' as PillTone },
          { value: `+${boneLevel.toFixed(1)}%`, tone: 'silver' as PillTone },
        ],
      },
      {
        id: 'strength',
        tone: 'amber' as PillTone,
        level: strengthLevel,
        bonuses: [
          { value: `+${strengthLevel.toFixed(1)}%`, tone: 'crimson' as PillTone },
          { value: `+${strengthLevel.toFixed(1)}%`, tone: 'gold' as PillTone },
          { value: `+${strengthLevel.toFixed(1)}%`, tone: 'amber' as PillTone },
        ],
      },
      {
        id: 'consciousness',
        tone: 'indigo' as PillTone,
        level: consciousnessLevel,
        bonuses: [
          { value: `+${(consciousnessLevel * 2.0).toFixed(1)}%`, tone: 'indigo' as PillTone },
          { value: `+${consciousnessLevel.toFixed(1)}%`, tone: 'sky' as PillTone },
        ],
      },
      {
        id: 'agility',
        tone: 'teal' as PillTone,
        level: agilityLevel,
        bonuses: [
          { value: `+${agilityLevel.toFixed(1)}%`, tone: 'teal' as PillTone },
          { value: `+${agilityLevel.toFixed(1)}%`, tone: 'slate' as PillTone },
          { value: `+${agilityLevel.toFixed(1)}%`, tone: 'silver' as PillTone },
        ],
      },
    ]
  }, [
    loadout?.attackLevel,
    loadout?.defenseLevel,
    loadout?.intelligenceLevel,
    loadout?.staminaLevel,
    loadout?.swordsmanshipLevel,
    loadout?.talismanLevel,
  ])

  // 0. 当前境界基础属性底座 (Base Flat · 乘区一)
  const basePool = useMemo(() => calculateBaseAttributes(stats.spirit ?? 0), [stats.spirit])

  // 1. 基础战斗属性 (固定数值类型，全部使用 double)
  // 当 attributes 存在时使用实时数值；未推送时以 basePool 为底座应用六维加成
  const staminaBonus = (loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const intelligenceBonus = (loadout?.intelligenceLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const boneBonus = (loadout?.defenseLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01

  const maxHp: number = attributes?.maxHitpoints ?? Math.round(basePool.baseHp * (1.0 + staminaBonus) * (1.0 + boneBonus))
  const maxMp: number = attributes?.maxManapoints ?? Math.round(basePool.baseMp * (1.0 + intelligenceBonus) * (1.0 + boneBonus))
  const physAtk: number = attributes ? Math.max(1.0, attributes.maxDamage * 0.9) : Math.round(basePool.baseAd * (1.0 + (loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01))
  const spellAtk: number = attributes ? Math.max(1.0, attributes.maxDamage * 1.1) : Math.round(basePool.baseAp * (1.0 + (loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.02))
  const armor: number = Math.max(0.0, attributes?.armor ?? (basePool.baseArmor * (1.0 + staminaBonus)))
  const magicRes: number = Math.max(0.0, attributes?.waterResistance ?? (basePool.baseMagicRes * (1.0 + intelligenceBonus)))
  const accuracy: number = attributes?.accuracyRating ?? basePool.baseAccuracy
  const evasion: number = attributes?.evasionRating ?? basePool.baseEvasion
  const critPoint: number = attributes ? attributes.criticalChance * 100.0 : basePool.baseCrit
  const critAvoidPoint: number = attributes ? attributes.criticalAvoidance * 100.0 : basePool.baseCritAvoid
  const parryBreakPoint: number = attributes ? attributes.pierceChance * 100.0 : basePool.baseBreak
  const parryResolvePoint: number = attributes ? attributes.damageAbsorptionRatio * 100.0 : basePool.baseParry

  // 生命回复与灵力回复 (固定值属性，每 5 秒回复量，基础为 2% 上限，受六维加成)
  const hpRegenTotal = attributes ? attributes.hpRegenPerTenSeconds / 2.0 : (basePool.baseHpRegen * (1.0 + staminaBonus))
  const mpRegenTotal = attributes ? attributes.mpRegenPerTenSeconds / 2.0 : (basePool.baseMpRegen * (1.0 + intelligenceBonus))

  // 2. 特殊战斗属性 (统一百分比类型 PercentValue，内部均为 double 点数)
  const critDamagePercent = PercentValue.linear((attributes?.criticalDamageRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints) * 100.0 + 150.0)
  const actionSpeedPoints = 0.0 // 默认 0.0 点
  const actionSpeedPercent = PercentValue.nonLinear(actionSpeedPoints)
  const cooldownSpeedPercent = PercentValue.nonLinear(BASELINE_COMBAT_ATTRIBUTES.percentPoints)
  const manaCostReductionPercent = PercentValue.nonLinear(BASELINE_COMBAT_ATTRIBUTES.percentPoints)
  const damageBoostPercent = PercentValue.linear((attributes?.furyRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints) * 100.0)
  const damageReductionPercent = PercentValue.linear((attributes?.damageAbsorptionRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints) * 100.0)
  const armorPenPercent = PercentValue.linear((attributes?.penetrationRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints) * 100.0)
  const magicPenPercent = PercentValue.linear((attributes?.penetrationRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints) * 100.0)
  const threatBonusPercent = PercentValue.linear(BASELINE_COMBAT_ATTRIBUTES.percentPoints)

  // 3. 其他属性 (增免伤与五行，全部为 PercentValue double)
  const physBoostPercent = PercentValue.linear(0.0)
  const physReductionPercent = PercentValue.linear(0.0)
  const spellBoostPercent = PercentValue.linear(0.0)
  const spellReductionPercent = PercentValue.linear(0.0)
  const metalBoostPercent = PercentValue.linear(0.0)
  const metalReductionPercent = PercentValue.linear(0.0)
  const woodBoostPercent = PercentValue.linear(0.0)
  const woodReductionPercent = PercentValue.linear(0.0)
  const waterBoostPercent = PercentValue.linear(0.0)
  const waterReductionPercent = PercentValue.linear(0.0)
  const fireBoostPercent = PercentValue.linear(0.0)
  const fireReductionPercent = PercentValue.linear(0.0)
  const earthBoostPercent = PercentValue.linear(0.0)
  const earthReductionPercent = PercentValue.linear(0.0)

  // 4. 生平记录 (斩妖为怪物击败数，破敌严格为击败玩家数，败走为败退次数，苦修为修炼时长)
  const biographyData = useMemo(() => {
    const normalKills: number = stats.combatEnemiesDefeated ?? 0.0
    const eliteKills: number = 0.0
    const bossKills: number = 0.0
    const totalMonsters: number = normalKills + eliteKills + bossKills
    const playersDefeated: number = stats.combatPlayersDefeated ?? 0.0
    const totalDefeats: number = stats.combatLosses ?? 0.0
    const cultivationSeconds: number = (stats.cultivationCycles ?? 0.0) * 10.0
    const cultivationHours: number = cultivationSeconds / 3600.0

    return {
      normalKills,
      eliteKills,
      bossKills,
      totalMonsters,
      playersDefeated,
      totalDefeats,
      cultivationSeconds,
      cultivationHours,
    }
  }, [
    stats.combatEnemiesDefeated,
    stats.combatPlayersDefeated,
    stats.combatLosses,
    stats.cultivationCycles,
  ])

  return (
    <section className="panel insight-page">
      {/* 头部：道号、境界与形象总览 */}
      <header className="panel-header insight-page-header">
        <div className="insight-hero">
          <div className="insight-avatar-stage">
            <VectorCharacter isDead={false} kind="player" role="player" />
          </div>
          <div className="insight-hero-info">
            <div className="insight-title-row">
              <h2 className="insight-main-title">{name}</h2>
              <RealmBadge realmInfo={realmInfo} />
            </div>
            <p className="insight-location">
              <span>{t('insight.location')}</span>
              <strong>{world.locationName || t('insight.locationFallback')}</strong>
              <em className="insight-multiplier">{t('insight.multiplier', { value: (world.cultivationMultiplier || 1.0).toFixed(2) })}</em>
            </p>
            {/* 突破修为进度 */}
            <div className="insight-progress-block">
              <div className="insight-progress-meta">
                <span>{t('insight.progressLabel')}</span>
                {isPendingBreakthrough || realmInfo.layerProgress >= 99.99 ? (
                  <strong className="tone-gold">{t('insight.progressComplete')}</strong>
                ) : (
                  <strong>{realmInfo.layerProgress.toFixed(1)} / 100.0 ({realmInfo.layerProgress.toFixed(1)}%)</strong>
                )}
              </div>
              <div className="insight-progress-bar">
                <div className="insight-progress-fill" style={{ width: `${isPendingBreakthrough || realmInfo.layerProgress >= 99.99 ? 100 : realmInfo.layerProgress}%` }} />
              </div>
              {(isPendingBreakthrough || (realmInfo.layerProgress >= 99.99 && (stats.spirit ?? 0) > 0)) && (
                <button
                  className="insight-breakthrough-btn"
                  onClick={() => gameClient.sendCommand('breakthrough', {})}
                  type="button"
                >
                  {t('insight.breakthroughBtn')}
                </button>
              )}
            </div>
            {/* 核心生息快照 */}
            <div className="insight-quick-stats">
              <div className="insight-quick-item">
                <span>{t('insight.quick.hp')}</span>
                <strong className="tone-green">{formatNumber(maxHp)}</strong>
              </div>
              <div className="insight-quick-item">
                <span>{t('insight.quick.mp')}</span>
                <strong className="tone-blue">{formatNumber(maxMp)}</strong>
              </div>
            </div>
          </div>
        </div>

        {/* 内视四大核心分类导航：根基 | 底蕴 | 斗法 | 生平 */}
        <nav className="insight-tabs" role="tablist">
          <button
            className={`insight-tab-btn ${activeTab === 'roots' ? 'active' : ''}`}
            onClick={() => setActiveTab('roots')}
            type="button"
          >
            {t('insight.tab.roots')}
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'foundation' ? 'active' : ''}`}
            onClick={() => setActiveTab('foundation')}
            type="button"
          >
            {t('insight.tab.foundation')}
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'combat' ? 'active' : ''}`}
            onClick={() => setActiveTab('combat')}
            type="button"
          >
            {t('insight.tab.combat')}
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'biography' ? 'active' : ''}`}
            onClick={() => setActiveTab('biography')}
            type="button"
          >
            {t('insight.tab.biography')}
          </button>
        </nav>
      </header>

      {/* 主体内容 */}
      <div className="insight-body">
        {/* ========================================================================= */}
        {/* TAB 1: 根基 (支持二级 Tab 切换：六维 | 基础)                                  */}
        {/* ========================================================================= */}
        {activeTab === 'roots' && (
          <div className="insight-panel insight-panel--roots">
            <div className="insight-sub-tabs" role="tablist">
              <button
                className={`insight-sub-tab-btn ${rootsSubTab === 'sixDimensions' ? 'active' : ''}`}
                onClick={() => setRootsSubTab('sixDimensions')}
                type="button"
              >
                {t('insight.subTab.sixDimensions')}
              </button>
              <button
                className={`insight-sub-tab-btn ${rootsSubTab === 'baseAttributes' ? 'active' : ''}`}
                onClick={() => setRootsSubTab('baseAttributes')}
                type="button"
              >
                {t('insight.subTab.base')}
              </button>
            </div>

            {rootsSubTab === 'sixDimensions' ? (
              <div className="insight-roots-list">
                {dimensions.map(dim => (
                  <div className="insight-root-row" key={dim.id}>
                    <PillStatLabel
                      label={t(`insight.root.${dim.id}.name`)}
                      value={formatNumber(dim.level)}
                      tone={dim.tone}
                      tooltip={t(`insight.root.${dim.id}.desc`)}
                      tooltipTone={dim.tone === 'purple' ? 'purple' : dim.tone === 'qi' ? 'qi' : dim.tone === 'jade' ? 'jade' : 'gold'}
                    />
                    <div className="insight-root-bonuses">
                      {dim.bonuses.map((bonus, idx) => (
                        <PillBonusLabel
                          key={idx}
                          label={t(`insight.root.${dim.id}.benefit${idx + 1}`)}
                          value={bonus.value}
                          tone={bonus.tone}
                        />
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            ) : (
              <div className="insight-base-layout">
                {/* 第 1 排：血蓝与自然回复 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    label={t('insight.base.maxHp')}
                    value={formatNumber(basePool.baseHp)}
                    tone="emerald"
                    tooltip={t('insight.base.maxHpTooltip', { value: formatNumber(basePool.baseHp) })}
                  />
                  <PillStatLabel
                    label={t('insight.base.maxMp')}
                    value={formatNumber(basePool.baseMp)}
                    tone="cyan"
                    tooltip={t('insight.base.maxMpTooltip', { value: formatNumber(basePool.baseMp) })}
                  />
                  <PillStatLabel
                    label={t('insight.base.hpRegen')}
                    value={`${formatNumber(basePool.baseHpRegen)}/5s`}
                    tone="emerald"
                    tooltip={t('insight.base.hpRegenTooltip', { value: formatNumber(basePool.baseHpRegen) })}
                  />
                  <PillStatLabel
                    label={t('insight.base.mpRegen')}
                    value={`${formatNumber(basePool.baseMpRegen)}/5s`}
                    tone="cyan"
                    tooltip={t('insight.base.mpRegenTooltip', { value: formatNumber(basePool.baseMpRegen) })}
                  />
                </div>

                {/* 第 2 排：AD / AP / 物理护甲 / 术法抗性 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    label={t('insight.base.physAtk')}
                    value={formatNumber(basePool.baseAd)}
                    tone="crimson"
                    tooltip={t('insight.base.physAtkTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.spellAtk')}
                    value={formatNumber(basePool.baseAp)}
                    tone="indigo"
                    tooltip={t('insight.base.spellAtkTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.armor')}
                    value={formatNumber(basePool.baseArmor)}
                    tone="bronze"
                    tooltip={t('insight.base.armorTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.magicRes')}
                    value={formatNumber(basePool.baseMagicRes)}
                    tone="violet"
                    tooltip={t('insight.base.magicResTooltip')}
                  />
                </div>

                {/* 第 3 排：命中闪避 暴击免暴 破招化解 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    label={t('insight.base.accuracy')}
                    value={formatNumber(basePool.baseAccuracy)}
                    tone="sky"
                    tooltip={t('insight.base.accuracyTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.evasion')}
                    value={formatNumber(basePool.baseEvasion)}
                    tone="teal"
                    tooltip={t('insight.base.evasionTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.crit')}
                    value={formatNumber(basePool.baseCrit)}
                    tone="gold"
                    tooltip={t('insight.base.critTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.critAvoid')}
                    value={formatNumber(basePool.baseCritAvoid)}
                    tone="slate"
                    tooltip={t('insight.base.critAvoidTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.parryBreak')}
                    value={formatNumber(basePool.baseBreak)}
                    tone="amber"
                    tooltip={t('insight.base.parryBreakTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.parryResolve')}
                    value={formatNumber(basePool.baseParry)}
                    tone="silver"
                    tooltip={t('insight.base.parryResolveTooltip')}
                  />
                </div>
              </div>
            )}
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 2: 底蕴 (直接显示纯数值，无图标，无多余加成标签)                         */}
        {/* ========================================================================= */}
        {activeTab === 'foundation' && (
          <div className="insight-panel insight-panel--foundation">
            <div className="insight-foundation-pills">
              <div className="insight-foundation-pill-row">
                <PillStatLabel
                  label={t('insight.foundation.daoFoundation')}
                  value={formatNumber(stats.foundationExp ?? 0)}
                  tone="gold"
                  tooltip={t('insight.foundation.daoFoundationTooltip')}
                  tooltipTone="gold"
                />
              </div>

              <div className="insight-foundation-pill-row">
                <PillStatLabel
                  label={t('insight.foundation.combatExp')}
                  value={(stats.combatExperience ?? 0).toFixed(1)}
                  tone="qi"
                  tooltip={t('insight.foundation.combatExpTooltip')}
                  tooltipTone="qi"
                />
              </div>
            </div>
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 3: 斗法 (全面采用复合 Label 展示各战斗属性，移除普攻机制)                   */}
        {/* ========================================================================= */}
        {activeTab === 'combat' && (
          <div className="insight-panel insight-panel--combat">
            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.combat.groupBase')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.combat.hp')} value={maxHp.toFixed(1)} tone="emerald" tooltip={t('insight.combat.hpTooltip')} />
                <PillStatLabel label={t('insight.combat.mp')} value={maxMp.toFixed(1)} tone="cyan" tooltip={t('insight.combat.mpTooltip')} />
                <PillStatLabel label={t('insight.combat.hpRegen')} value={`${hpRegenTotal.toFixed(1)}/5s`} tone="emerald" tooltip={t('insight.combat.hpRegenTooltip')} />
                <PillStatLabel label={t('insight.combat.mpRegen')} value={`${mpRegenTotal.toFixed(1)}/5s`} tone="cyan" tooltip={t('insight.combat.mpRegenTooltip')} />
                <PillStatLabel label={t('insight.combat.physAtk')} value={physAtk.toFixed(1)} tone="crimson" tooltip={t('insight.combat.physAtkTooltip')} />
                <PillStatLabel label={t('insight.combat.spellAtk')} value={spellAtk.toFixed(1)} tone="indigo" tooltip={t('insight.combat.spellAtkTooltip')} />
                <PillStatLabel label={t('insight.combat.armor')} value={armor.toFixed(1)} tone="bronze" tooltip={t('insight.combat.armorTooltip')} />
                <PillStatLabel label={t('insight.combat.magicRes')} value={magicRes.toFixed(1)} tone="violet" tooltip={t('insight.combat.magicResTooltip')} />
                <PillStatLabel label={t('insight.combat.accuracy')} value={accuracy.toFixed(1)} tone="sky" tooltip={t('insight.combat.accuracyTooltip')} />
                <PillStatLabel label={t('insight.combat.evasion')} value={evasion.toFixed(1)} tone="teal" tooltip={t('insight.combat.evasionTooltip')} />
                <PillStatLabel label={t('insight.combat.crit')} value={critPoint.toFixed(1)} tone="gold" tooltip={t('insight.combat.critTooltip')} />
                <PillStatLabel label={t('insight.combat.critAvoid')} value={critAvoidPoint.toFixed(1)} tone="slate" tooltip={t('insight.combat.critAvoidTooltip')} />
                <PillStatLabel label={t('insight.combat.parryBreak')} value={parryBreakPoint.toFixed(1)} tone="amber" tooltip={t('insight.combat.parryBreakTooltip')} />
                <PillStatLabel label={t('insight.combat.parryResolve')} value={parryResolvePoint.toFixed(1)} tone="silver" tooltip={t('insight.combat.parryResolveTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.combat.groupSpecial')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.combat.critDamage')} value={critDamagePercent.format()} tone="gold" tooltip={t('insight.combat.critDamageTooltip')} />
                <PillStatLabel label={t('insight.combat.actionSpeed')} value={actionSpeedPercent.format()} tone="teal" tooltip={t('insight.combat.actionSpeedTooltip')} />
                <PillStatLabel label={t('insight.combat.cooldownSpeed')} value={cooldownSpeedPercent.format()} tone="cyan" tooltip={t('insight.combat.cooldownSpeedTooltip')} />
                <PillStatLabel label={t('insight.combat.manaCostReduction')} value={manaCostReductionPercent.format()} tone="cyan" tooltip={t('insight.combat.manaCostReductionTooltip')} />
                <PillStatLabel label={t('insight.combat.damageBoost')} value={damageBoostPercent.format()} tone="gold" tooltip={t('insight.combat.damageBoostTooltip')} />
                <PillStatLabel label={t('insight.combat.damageReduction')} value={damageReductionPercent.format()} tone="emerald" tooltip={t('insight.combat.damageReductionTooltip')} />
                <PillStatLabel label={t('insight.combat.armorPen')} value={armorPenPercent.format()} tone="bronze" tooltip={t('insight.combat.armorPenTooltip')} />
                <PillStatLabel label={t('insight.combat.magicPen')} value={magicPenPercent.format()} tone="violet" tooltip={t('insight.combat.magicPenTooltip')} />
                <PillStatLabel label={t('insight.combat.threatBonus')} value={threatBonusPercent.format()} tone="crimson" tooltip={t('insight.combat.threatBonusTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.combat.groupElements')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.combat.physBoostReduce')} value={`${physBoostPercent.format()} / ${physReductionPercent.format()}`} tone="crimson" tooltip={t('insight.combat.physBoostReduceTooltip')} />
                <PillStatLabel label={t('insight.combat.spellBoostReduce')} value={`${spellBoostPercent.format()} / ${spellReductionPercent.format()}`} tone="indigo" tooltip={t('insight.combat.spellBoostReduceTooltip')} />
                <PillStatLabel label={t('insight.combat.metalBoostReduce')} value={`${metalBoostPercent.format()} / ${metalReductionPercent.format()}`} tone="gold" tooltip={t('insight.combat.metalBoostReduceTooltip')} />
                <PillStatLabel label={t('insight.combat.woodBoostReduce')} value={`${woodBoostPercent.format()} / ${woodReductionPercent.format()}`} tone="emerald" tooltip={t('insight.combat.woodBoostReduceTooltip')} />
                <PillStatLabel label={t('insight.combat.waterBoostReduce')} value={`${waterBoostPercent.format()} / ${waterReductionPercent.format()}`} tone="cyan" tooltip={t('insight.combat.waterBoostReduceTooltip')} />
                <PillStatLabel label={t('insight.combat.fireBoostReduce')} value={`${fireBoostPercent.format()} / ${fireReductionPercent.format()}`} tone="amber" tooltip={t('insight.combat.fireBoostReduceTooltip')} />
                <PillStatLabel label={t('insight.combat.earthBoostReduce')} value={`${earthBoostPercent.format()} / ${earthReductionPercent.format()}`} tone="bronze" tooltip={t('insight.combat.earthBoostReduceTooltip')} />
              </div>
            </div>

            {/* 运转中的丹药与神通 Buff */}
            {buffs.length > 0 && (
              <div className="insight-pill-group">
                <h4 className="insight-pill-group-title">{t('insight.combat.groupBuffs')}</h4>
                <div className="insight-pill-group-items">
                  {buffs.map(buff => (
                    <PillStatLabel
                      key={buff.buffId}
                      label={buff.name}
                      value={buff.stacks > 1 ? `×${formatNumber(buff.stacks)}` : t('insight.combat.buffActive')}
                      tone="gold"
                      tooltip={t('insight.combat.buffTooltip', { name: buff.name, stacks: formatNumber(buff.stacks) })}
                    />
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 4: 生平 (历练、争锋、修行)                                             */}
        {/* ========================================================================= */}
        {activeTab === 'biography' && (
          <div className="insight-panel insight-panel--biography">
            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupAdventure')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.bio.normalKills')} value={biographyData.normalKills.toFixed(0)} tone="slate" tooltip={t('insight.bio.normalKillsTooltip')} />
                <PillStatLabel label={t('insight.bio.eliteKills')} value={biographyData.eliteKills.toFixed(0)} tone="cyan" tooltip={t('insight.bio.eliteKillsTooltip')} />
                <PillStatLabel label={t('insight.bio.bossKills')} value={biographyData.bossKills.toFixed(0)} tone="gold" tooltip={t('insight.bio.bossKillsTooltip')} />
                <PillStatLabel label={t('insight.bio.totalMonsters')} value={biographyData.totalMonsters.toFixed(0)} tone="crimson" tooltip={t('insight.bio.totalMonstersTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupRivalry')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.bio.playersDefeated')} value={biographyData.playersDefeated.toFixed(0)} tone="emerald" tooltip={t('insight.bio.playersDefeatedTooltip')} />
                <PillStatLabel label={t('insight.bio.totalDefeats')} value={biographyData.totalDefeats.toFixed(0)} tone="crimson" tooltip={t('insight.bio.totalDefeatsTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupCultivation')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.bio.cultivation')} value={t('insight.bio.cultivationValue', { hours: formatNumber(biographyData.cultivationHours) })} tone="gold" tooltip={t('insight.bio.cultivationTooltip', { seconds: formatNumber(biographyData.cultivationSeconds) })} />
              </div>
            </div>
          </div>
        )}
      </div>
    </section>
  )
})
