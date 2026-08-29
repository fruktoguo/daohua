import { memo, useMemo, useState } from 'react'
import { RealmBadge } from './RealmBadge'
import { PillStatLabel, PillBonusLabel, type PillTone } from './PillLabel'
import { formatChineseSplitNumber, formatNumber } from '../game/format'
import { FloatingTooltip } from './FloatingPopover'
import { UnitNumber } from './UnitNumber'
import {
  PercentValue,
  DEFAULT_ROOT_ATTRIBUTE_POINTS,
  BASELINE_COMBAT_ATTRIBUTES,
  getCumulativeExpForLevel,
  calculateRealmInfo,
  calculateRealmProgress,
  calculateBaseAttributes,
} from '../game/combatConstants'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'
import { CharacterAvatar } from './CharacterAvatar'

/** 内视四大核心模块 Tab 标识 */
type InsightTab = 'roots' | 'foundation' | 'combat' | 'biography'

/** 根基模块二级子 Tab 标识 (六维灵根与基础属性乘区) */
type RootsSubTab = 'sixDimensions' | 'baseAttributes'

/**
 * 角色内视修行总览主页面 (InsightPage)
 *
 * 体系与交互结构：
 * 1. 头部修仙境界展示：当前大境界/位阶/小境界 (RealmBadge)、所在地灵气倍率、修为突破进度条与一键突破按钮；
 * 2. 根基 Tab (roots)：
 *    - 六维根基 (sixDimensions)：体魄、经脉、根骨、力道、神识、身法等级与属性百分比加成；
 *    - 基础属性 (baseAttributes)：展示当前境界的基础属性底座数值 (Base Pool)；
 * 3. 底蕴 Tab (foundation)：道行底蕴与战斗历练值纯净展示；
 * 4. 斗法 Tab (combat)：展示攻防、博弈对抗 (命中/闪避/暴击/化解)、时序与五行增免伤等全量乘区，以及运转中的丹药与神通 Buff；
 * 5. 生平 Tab (biography)：记录斩妖总数、玩家争锋破敌战绩及闭关苦修累计时长。
 */
export const InsightPage = memo(function InsightPage(): React.JSX.Element {
  const t = useT()
  const [activeTab, setActiveTab] = useState<InsightTab>('roots')
  const [rootsSubTab, setRootsSubTab] = useState<RootsSubTab>('sixDimensions')

  const playerId = useGameStore(state => state.playerId)
  const name = useGameStore(state => state.name)
  const visual = useGameStore(state => state.visual)
  const stats = useGameStore(state => state.stats)
  const isPendingBreakthrough = useGameStore(state => state.isPendingBreakthrough)
  const loadout = useGameStore(state => state.combat)
  const attributes = useGameStore(state => state.combatAttributes)
  const world = useGameStore(state => state.world)
  const buffs = useGameStore(state => state.buffs)

  // 境界计算体系 (大境界 · 位阶 · 小境界)
  const realmProgress = useMemo(() => calculateRealmProgress(stats.spirit ?? 0, stats.realmLevel), [stats.realmLevel, stats.spirit])
  const realmInfo = useMemo(() => calculateRealmInfo(stats.spirit ?? 0, stats.realmLevel), [stats.realmLevel, stats.spirit])

  // 六维根基换算定义 (横向展示：数值胶囊 + 属性加成胶囊列表)
  // 文本统一从 insight.root.* 词典取，此处只保留 id / tone / level / bonus 数值
  const dimensions = useMemo(() => {
    const staminaLevel = Math.floor(Math.max(10, loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 体魄 10
    const meridiansLevel = Math.floor(Math.max(10, loadout?.intelligenceLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 经脉 10
    const boneLevel = Math.floor(Math.max(10, loadout?.defenseLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 根骨 10
    const strengthLevel = Math.floor(Math.max(10, loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 力道 10
    const consciousnessLevel = Math.floor(Math.max(10, loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 神识 10
    const agilityLevel = Math.floor(Math.max(10, loadout?.talismanLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 身法 10

    // 格式化经验进度：当前等级初始 0 经验，升级所需经验 = 下一级累计经验 - 当前级累计经验 (例如 0/2.71万)
    const getProgress = (level: number) => {
      const baseExp = getCumulativeExpForLevel(level)
      const nextExp = getCumulativeExpForLevel(level + 1.0)
      const req = Math.max(1, Math.floor(nextExp - baseExp))
      const cur = 0
      return {
        current: cur,
        max: req,
        text: `${formatNumber(cur)}/${formatNumber(req)}`,
      }
    }

    return [
      {
        id: 'stamina',
        tone: 'emerald' as PillTone,
        level: staminaLevel,
        progress: getProgress(staminaLevel),
        bonuses: [
          { value: `+${Math.floor(staminaLevel)}%`, tone: 'emerald' as PillTone },
          { value: `+${Math.floor(staminaLevel)}%`, tone: 'bronze' as PillTone },
          { value: `+${Math.floor(staminaLevel)}%`, tone: 'emerald' as PillTone },
        ],
      },
      {
        id: 'meridians',
        tone: 'cyan' as PillTone,
        level: meridiansLevel,
        progress: getProgress(meridiansLevel),
        bonuses: [
          { value: `+${Math.floor(meridiansLevel)}%`, tone: 'cyan' as PillTone },
          { value: `+${Math.floor(meridiansLevel)}%`, tone: 'violet' as PillTone },
          { value: `+${Math.floor(meridiansLevel)}%`, tone: 'cyan' as PillTone },
        ],
      },
      {
        id: 'bone',
        tone: 'amber' as PillTone,
        level: boneLevel,
        progress: getProgress(boneLevel),
        bonuses: [
          { value: `+${Math.floor(boneLevel)}%`, tone: 'amber' as PillTone },
          { value: `+${Math.floor(boneLevel)}%`, tone: 'silver' as PillTone },
          { value: `+${Math.floor(boneLevel)}%`, tone: 'amber' as PillTone },
        ],
      },
      {
        id: 'strength',
        tone: 'crimson' as PillTone,
        level: strengthLevel,
        progress: getProgress(strengthLevel),
        bonuses: [
          { value: `+${Math.floor(strengthLevel)}%`, tone: 'crimson' as PillTone },
          { value: `+${Math.floor(strengthLevel)}%`, tone: 'slate' as PillTone },
          { value: `+${Math.floor(strengthLevel)}%`, tone: 'crimson' as PillTone },
        ],
      },
      {
        id: 'consciousness',
        tone: 'purple' as PillTone,
        level: consciousnessLevel,
        progress: getProgress(consciousnessLevel),
        bonuses: [
          { value: `+${Math.floor(consciousnessLevel * 2)}%`, tone: 'purple' as PillTone },
          { value: `+${Math.floor(consciousnessLevel)}%`, tone: 'sky' as PillTone },
        ],
      },
      {
        id: 'agility',
        tone: 'teal' as PillTone,
        level: agilityLevel,
        progress: getProgress(agilityLevel),
        bonuses: [
          { value: `+${Math.floor(agilityLevel)}%`, tone: 'teal' as PillTone },
          { value: `+${Math.floor(agilityLevel)}%`, tone: 'muted' as PillTone },
          { value: `+${Math.floor(agilityLevel)}%`, tone: 'teal' as PillTone },
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
  const basePool = useMemo(() => calculateBaseAttributes(stats.spirit ?? 0, stats.realmLevel), [stats.realmLevel, stats.spirit])

  // 1. 基础战斗属性 (固定数值类型，全部向下取整)
  // 当 attributes 存在时使用实时数值；未推送时以 basePool 为底座应用六维加成
  const staminaBonus = (loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const intelligenceBonus = (loadout?.intelligenceLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01
  const boneBonus = (loadout?.defenseLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01

  const maxHp: number = Math.floor(attributes?.maxHitpoints ?? (basePool.baseHp * (1.0 + staminaBonus) * (1.0 + boneBonus)))
  const maxMp: number = Math.floor(attributes?.maxManapoints ?? (basePool.baseMp * (1.0 + intelligenceBonus) * (1.0 + boneBonus)))
  const physAtk: number = Math.floor(attributes
    ? (attributes.style === 'five-elements' ? (basePool.baseAd * (1.0 + (loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)) : attributes.maxDamage)
    : (basePool.baseAd * (1.0 + (loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)))
  const spellAtk: number = Math.floor(attributes
    ? (attributes.style === 'five-elements' ? attributes.maxDamage : (basePool.baseAp * (1.0 + (loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.02)))
    : (basePool.baseAp * (1.0 + (loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.02)))
  const armor: number = Math.floor(Math.max(0.0, attributes?.armor ?? (basePool.baseArmor * (1.0 + staminaBonus))))
  const magicRes: number = Math.floor(Math.max(0.0, attributes?.magicResistance ?? (basePool.baseMagicRes * (1.0 + intelligenceBonus))))
  const accuracy: number = Math.floor(attributes?.accuracyRating ?? (basePool.baseAccuracy * (1.0 + (loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)))
  const evasion: number = Math.floor(attributes?.evasionRating ?? (basePool.baseEvasion * (1.0 + (loadout?.talismanLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)))
  const critPoint: number = Math.floor(attributes ? (attributes.criticalRating ?? attributes.criticalChance * 100.0) : (basePool.baseCrit * (1.0 + (loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)))
  const critAvoidPoint: number = Math.floor(attributes ? (attributes.criticalAvoidanceRating ?? attributes.criticalAvoidance * 100.0) : (basePool.baseCritAvoid * (1.0 + (loadout?.talismanLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)))
  const parryBreakPoint: number = Math.floor(basePool.baseBreak * (1.0 + (loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01))
  const parryResolvePoint: number = Math.floor(basePool.baseParry * (1.0 + boneBonus + (loadout?.talismanLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01))

  // 生命回复与灵力回复 (固定值属性，每 5 秒回复量，全部向下取整)
  // 服务端 HpRegenPerTenSeconds 是比率 (如 0.044 = 每 10 秒回复 maxHp 的 4.4%)
  // 实际每 5 秒回复量 = maxHp * ratio / 2
  const hpRegenTotal = Math.floor(attributes ? (maxHp * attributes.hpRegenPerTenSeconds / 2.0) : (basePool.baseHpRegen * (1.0 + staminaBonus)))
  const mpRegenTotal = Math.floor(attributes ? (maxMp * attributes.mpRegenPerTenSeconds / 2.0) : (basePool.baseMpRegen * (1.0 + intelligenceBonus)))

  // 2. 特殊战斗属性 (统一百分比类型 PercentValue，内部均为 double 点数)
  const critDamagePercent = PercentValue.linear((attributes?.criticalDamageRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints) * 100.0 + 150.0)
  const actionSpeedPoints = 0.0 // 默认 0.0 点
  const actionSpeedPercent = PercentValue.nonLinear(actionSpeedPoints)
  const cooldownSpeedPercent = PercentValue.nonLinear(BASELINE_COMBAT_ATTRIBUTES.percentPoints)
  const manaCostReductionPercent = PercentValue.nonLinear(BASELINE_COMBAT_ATTRIBUTES.percentPoints)
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

  // 4. 生平记录 (斩妖为怪物击败数，破敌严格为击败玩家数，败走为败退次数，苦修为修炼时长，全部向下取整)
  const biographyData = useMemo(() => {
    const totalMonsters: number = Math.floor(stats.combatEnemiesDefeated ?? 0.0)
    const playersDefeated: number = Math.floor(stats.combatPlayersDefeated ?? 0.0)
    const totalDefeats: number = Math.floor(stats.combatLosses ?? 0.0)
    const cultivationSeconds: number = Math.floor((stats.cultivationCycles ?? 0.0) * 10.0)
    const cultivationHours: number = Math.floor(cultivationSeconds / 3600.0)

    return {
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
            <CharacterAvatar
              actorId={playerId}
              className="insight-avatar-img"
              name={name}
              role="player"
              visual={visual}
            />
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
                  <strong>
                    <UnitNumber tooltipLabel={t('insight.currentExp')} value={Math.floor(realmProgress.currentLayerExp)} /> /{' '}
                    <UnitNumber tooltipLabel={t('insight.nextExp')} value={Math.floor(realmProgress.expToNext)} /> ({Math.floor(realmInfo.layerProgress * 10) / 10}%)
                  </strong>
                )}
              </div>
              <FloatingTooltip
                placement="top"
                title={
                  <div className="unit-number-tooltip-inline">
                    <span className="unit-number-tooltip-label">{t('insight.totalSpirit')}</span>
                    <strong className="unit-number-tooltip-split">{formatChineseSplitNumber(Math.floor(stats.spirit ?? 0))}</strong>
                  </div>
                }
              >
                <div className="insight-progress-bar">
                  <div className="insight-progress-fill" style={{ width: `${isPendingBreakthrough || realmInfo.layerProgress >= 99.99 ? 100 : realmInfo.layerProgress}%` }} />
                </div>
              </FloatingTooltip>
              {(isPendingBreakthrough || (realmInfo.layerProgress >= 99.99 && (stats.spirit ?? 0) > 0)) && (
                <button
                  className="insight-breakthrough-btn"
                  onClick={() => gameClient.sendCommand('progression.breakthrough', {})}
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
                <strong className="tone-green"><UnitNumber tooltipLabel={t('insight.quick.hp')} tone="jade" value={maxHp} /></strong>
              </div>
              <div className="insight-quick-item">
                <span>{t('insight.quick.mp')}</span>
                <strong className="tone-blue"><UnitNumber tooltipLabel={t('insight.quick.mp')} tone="qi" value={maxMp} /></strong>
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
                      value={dim.level}
                      tone={dim.tone}
                      progress={dim.progress}
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
                    value={Math.floor(basePool.baseHp)}
                    tone="emerald"
                    tooltip={t('insight.combat.hpTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.maxMp')}
                    value={Math.floor(basePool.baseMp)}
                    tone="cyan"
                    tooltip={t('insight.combat.mpTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.hpRegen')}
                    value={Math.floor(basePool.baseHpRegen)}
                    tone="emerald"
                    tooltip={t('insight.combat.hpRegenTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.mpRegen')}
                    value={Math.floor(basePool.baseMpRegen)}
                    tone="cyan"
                    tooltip={t('insight.combat.mpRegenTooltip')}
                  />
                </div>

                {/* 第 2 排：AD / AP / 物理护甲 / 术法抗性 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    label={t('insight.base.physAtk')}
                    value={Math.floor(basePool.baseAd)}
                    tone="crimson"
                    tooltip={t('insight.base.physAtkTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.spellAtk')}
                    value={Math.floor(basePool.baseAp)}
                    tone="indigo"
                    tooltip={t('insight.base.spellAtkTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.armor')}
                    value={Math.floor(basePool.baseArmor)}
                    tone="bronze"
                    tooltip={t('insight.base.armorTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.magicRes')}
                    value={Math.floor(basePool.baseMagicRes)}
                    tone="violet"
                    tooltip={t('insight.base.magicResTooltip')}
                  />
                </div>

                {/* 第 3 排：命中闪避 暴击免暴 破招化解 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    label={t('insight.base.accuracy')}
                    value={Math.floor(basePool.baseAccuracy)}
                    tone="sky"
                    tooltip={t('insight.base.accuracyTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.evasion')}
                    value={Math.floor(basePool.baseEvasion)}
                    tone="teal"
                    tooltip={t('insight.base.evasionTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.crit')}
                    value={Math.floor(basePool.baseCrit)}
                    tone="gold"
                    tooltip={t('insight.base.critTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.critAvoid')}
                    value={Math.floor(basePool.baseCritAvoid)}
                    tone="slate"
                    tooltip={t('insight.base.critAvoidTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.parryBreak')}
                    value={Math.floor(basePool.baseBreak)}
                    tone="amber"
                    tooltip={t('insight.base.parryBreakTooltip')}
                  />
                  <PillStatLabel
                    label={t('insight.base.parryResolve')}
                    value={Math.floor(basePool.baseParry)}
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
                  value={Math.floor(stats.foundationExp ?? 0)}
                  tone="gold"
                  tooltip={t('insight.foundation.daoFoundationTooltip')}
                  tooltipTone="gold"
                />
              </div>

              <div className="insight-foundation-pill-row">
                <PillStatLabel
                  label={t('insight.foundation.combatExp')}
                  value={Math.floor(stats.combatExperience ?? 0)}
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
              <div className="insight-base-layout">
                {/* 第 1 排：气血、灵力与自然回复 */}
                <div className="insight-base-row">
                  <PillStatLabel label={t('insight.combat.hp')} value={maxHp} tone="emerald" tooltip={t('insight.combat.hpTooltip')} />
                  <PillStatLabel label={t('insight.combat.mp')} value={maxMp} tone="cyan" tooltip={t('insight.combat.mpTooltip')} />
                  <PillStatLabel label={t('insight.combat.hpRegen')} value={hpRegenTotal} tone="emerald" tooltip={t('insight.combat.hpRegenTooltip')} />
                  <PillStatLabel label={t('insight.combat.mpRegen')} value={mpRegenTotal} tone="cyan" tooltip={t('insight.combat.mpRegenTooltip')} />
                </div>

                {/* 第 2 排：攻防属性 (物理攻击、术法攻击、物理护甲、术法抗性) */}
                <div className="insight-base-row">
                  <PillStatLabel label={t('insight.combat.physAtk')} value={physAtk} tone="crimson" tooltip={t('insight.combat.physAtkTooltip')} />
                  <PillStatLabel label={t('insight.combat.spellAtk')} value={spellAtk} tone="indigo" tooltip={t('insight.combat.spellAtkTooltip')} />
                  <PillStatLabel label={t('insight.combat.armor')} value={armor} tone="bronze" tooltip={t('insight.combat.armorTooltip')} />
                  <PillStatLabel label={t('insight.combat.magicRes')} value={magicRes} tone="violet" tooltip={t('insight.combat.magicResTooltip')} />
                </div>

                {/* 第 3 排：博弈对抗 (命中/闪避、暴击/免暴、破招/化解) */}
                <div className="insight-base-row">
                  <PillStatLabel label={t('insight.combat.accuracy')} value={accuracy} tone="sky" tooltip={t('insight.combat.accuracyTooltip')} />
                  <PillStatLabel label={t('insight.combat.evasion')} value={evasion} tone="teal" tooltip={t('insight.combat.evasionTooltip')} />
                  <PillStatLabel label={t('insight.combat.crit')} value={critPoint} tone="gold" tooltip={t('insight.combat.critTooltip')} />
                  <PillStatLabel label={t('insight.combat.critAvoid')} value={critAvoidPoint} tone="slate" tooltip={t('insight.combat.critAvoidTooltip')} />
                  <PillStatLabel label={t('insight.combat.parryBreak')} value={parryBreakPoint} tone="amber" tooltip={t('insight.combat.parryBreakTooltip')} />
                  <PillStatLabel label={t('insight.combat.parryResolve')} value={parryResolvePoint} tone="silver" tooltip={t('insight.combat.parryResolveTooltip')} />
                </div>
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.combat.groupSpecial')}</h4>
              <div className="insight-base-layout">
                {/* 第 1 排：时序与施法效能 */}
                <div className="insight-base-row">
                  <PillStatLabel label={t('insight.combat.critDamage')} value={critDamagePercent.format()} tone="gold" tooltip={t('insight.combat.critDamageTooltip')} />
                  <PillStatLabel label={t('insight.combat.actionSpeed')} value={actionSpeedPercent.format()} tone="teal" tooltip={t('insight.combat.actionSpeedTooltip')} />
                  <PillStatLabel label={t('insight.combat.cooldownSpeed')} value={cooldownSpeedPercent.format()} tone="cyan" tooltip={t('insight.combat.cooldownSpeedTooltip')} />
                  <PillStatLabel label={t('insight.combat.manaCostReduction')} value={manaCostReductionPercent.format()} tone="cyan" tooltip={t('insight.combat.manaCostReductionTooltip')} />
                </div>

                {/* 第 2 排：增伤减伤与穿透 */}
                <div className="insight-base-row">
                  <PillStatLabel label={t('insight.combat.damageReduction')} value={damageReductionPercent.format()} tone="emerald" tooltip={t('insight.combat.damageReductionTooltip')} />
                  <PillStatLabel label={t('insight.combat.armorPen')} value={armorPenPercent.format()} tone="bronze" tooltip={t('insight.combat.armorPenTooltip')} />
                  <PillStatLabel label={t('insight.combat.magicPen')} value={magicPenPercent.format()} tone="violet" tooltip={t('insight.combat.magicPenTooltip')} />
                  <PillStatLabel label={t('insight.combat.threatBonus')} value={threatBonusPercent.format()} tone="crimson" tooltip={t('insight.combat.threatBonusTooltip')} />
                </div>
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.combat.groupElements')}</h4>
              <div className="insight-base-layout">
                {/* 第 1 排：物法基础增减伤 */}
                <div className="insight-base-row">
                  <PillStatLabel label={t('insight.combat.physBoostReduce')} value={`${physBoostPercent.format()} / ${physReductionPercent.format()}`} tone="crimson" tooltip={t('insight.combat.physBoostReduceTooltip')} />
                  <PillStatLabel label={t('insight.combat.spellBoostReduce')} value={`${spellBoostPercent.format()} / ${spellReductionPercent.format()}`} tone="indigo" tooltip={t('insight.combat.spellBoostReduceTooltip')} />
                </div>

                {/* 第 2 排：五行克制增免 */}
                <div className="insight-base-row">
                  <PillStatLabel label={t('insight.combat.metalBoostReduce')} value={`${metalBoostPercent.format()} / ${metalReductionPercent.format()}`} tone="gold" tooltip={t('insight.combat.metalBoostReduceTooltip')} />
                  <PillStatLabel label={t('insight.combat.woodBoostReduce')} value={`${woodBoostPercent.format()} / ${woodReductionPercent.format()}`} tone="emerald" tooltip={t('insight.combat.woodBoostReduceTooltip')} />
                  <PillStatLabel label={t('insight.combat.waterBoostReduce')} value={`${waterBoostPercent.format()} / ${waterReductionPercent.format()}`} tone="cyan" tooltip={t('insight.combat.waterBoostReduceTooltip')} />
                  <PillStatLabel label={t('insight.combat.fireBoostReduce')} value={`${fireBoostPercent.format()} / ${fireReductionPercent.format()}`} tone="amber" tooltip={t('insight.combat.fireBoostReduceTooltip')} />
                  <PillStatLabel label={t('insight.combat.earthBoostReduce')} value={`${earthBoostPercent.format()} / ${earthReductionPercent.format()}`} tone="bronze" tooltip={t('insight.combat.earthBoostReduceTooltip')} />
                </div>
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
                <PillStatLabel label={t('insight.bio.totalMonsters')} value={biographyData.totalMonsters} tone="crimson" tooltip={t('insight.bio.totalMonstersTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupRivalry')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.bio.playersDefeated')} value={biographyData.playersDefeated} tone="emerald" tooltip={t('insight.bio.playersDefeatedTooltip')} />
                <PillStatLabel label={t('insight.bio.totalDefeats')} value={biographyData.totalDefeats} tone="crimson" tooltip={t('insight.bio.totalDefeatsTooltip')} />
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
