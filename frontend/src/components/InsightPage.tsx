import { memo, useMemo, useState } from 'react'
import { RealmBadge } from './RealmBadge'
import { PillStatLabel, PillBonusLabel, type PillTone } from './PillLabel'
import { formatChineseSplitNumber, formatNumber } from '../game/format'
import { FloatingTooltip } from './FloatingPopover'
import { UnitNumber } from './UnitNumber'
import {
  DEFAULT_ROOT_ATTRIBUTE_POINTS,
  getCumulativeExpForLevel,
  calculateRealmInfo,
  calculateRealmProgress,
  calculateBaseAttributes,
} from '../game/combatConstants'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'
import { CharacterAvatar } from './CharacterAvatar'
import { CombatAttributePanel } from './CombatAttributePanel'

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
  const realmProgress = useMemo(() => calculateRealmProgress(stats.experience ?? 0, stats.realmLevel), [stats.realmLevel, stats.experience])
  const realmInfo = useMemo(() => calculateRealmInfo(stats.experience ?? 0, stats.realmLevel), [stats.realmLevel, stats.experience])

  // 六维根基换算定义 (横向展示：数值胶囊 + 属性加成胶囊列表)
  // 文本统一从 insight.root.* 词典取，此处只保留 id / tone / level / bonus 数值
  const dimensions = useMemo(() => {
    const staminaLevel = Math.floor(Math.max(10, loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 体魄 10
    const meridiansLevel = Math.floor(Math.max(10, loadout?.meridiansLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 经脉 10
    const boneLevel = Math.floor(Math.max(10, loadout?.boneLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 根骨 10
    const strengthLevel = Math.floor(Math.max(10, loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 力道 10
    const consciousnessLevel = Math.floor(Math.max(10, loadout?.consciousnessLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 神识 10
    const agilityLevel = Math.floor(Math.max(10, loadout?.agilityLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 身法 10

    // 格式化经验进度：升级所需经验 = 下一级累计经验 - 当前级累计经验；
    // 当前经验 = loadout 下发的真实六维经验 - 当前级累计经验（缺省按 0 处理）。
    const getProgress = (level: number, exp: number | undefined) => {
      const baseExp = getCumulativeExpForLevel(level)
      const nextExp = getCumulativeExpForLevel(level + 1.0)
      const req = Math.max(1, Math.floor(nextExp - baseExp))
      const cur = Math.min(req, Math.max(0, Math.floor((exp ?? 0) - baseExp)))
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
        progress: getProgress(staminaLevel, loadout?.staminaExp),
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
        progress: getProgress(meridiansLevel, loadout?.meridiansExp),
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
        progress: getProgress(boneLevel, loadout?.boneExp),
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
        progress: getProgress(strengthLevel, loadout?.attackExp),
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
        progress: getProgress(consciousnessLevel, loadout?.consciousnessExp),
        bonuses: [
          { value: `+${Math.floor(consciousnessLevel * 2)}%`, tone: 'purple' as PillTone },
          { value: `+${Math.floor(consciousnessLevel)}%`, tone: 'sky' as PillTone },
        ],
      },
      {
        id: 'agility',
        tone: 'teal' as PillTone,
        level: agilityLevel,
        progress: getProgress(agilityLevel, loadout?.agilityExp),
        bonuses: [
          { value: `+${Math.floor(agilityLevel)}%`, tone: 'teal' as PillTone },
          { value: `+${Math.floor(agilityLevel)}%`, tone: 'muted' as PillTone },
          { value: `+${Math.floor(agilityLevel)}%`, tone: 'teal' as PillTone },
        ],
      },
    ]
  }, [
    loadout?.attackLevel,
    loadout?.boneLevel,
    loadout?.meridiansLevel,
    loadout?.staminaLevel,
    loadout?.agilityLevel,
    loadout?.consciousnessLevel,
    loadout?.attackExp,
    loadout?.boneExp,
    loadout?.meridiansExp,
    loadout?.staminaExp,
    loadout?.agilityExp,
    loadout?.consciousnessExp,
  ])

  // 0. 当前境界基础属性底座 (Base Flat · 乘区一)
  const basePool = useMemo(() => calculateBaseAttributes(stats.experience ?? 0, stats.realmLevel), [stats.realmLevel, stats.experience])

  // 斗法战斗属性：直接渲染服务端权威终值 (combat.attributes)，与战场单位详情弹窗共用同一面板

  // 4. 生平记录 (斩妖为怪物击败数，破敌严格为斗法击败玩家数，败走为败退次数，苦修为修炼时长，全部向下取整)
  const duel = useGameStore(state => state.duel)
  const biographyData = useMemo(() => {
    const totalMonsters: number = Math.floor(stats.combatEnemiesDefeated ?? 0.0)
    const playersDefeated: number = Math.floor((duel?.reports ?? []).reduce((sum, report) => sum + report.duelWins, 0))
    const totalDefeats: number = Math.floor(stats.combatLosses ?? 0.0)
    const cultivationSeconds: number = Math.floor(stats.cultivationSeconds ?? 0.0)
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
    stats.combatLosses,
    stats.cultivationSeconds,
    duel,
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
                    <strong className="unit-number-tooltip-split">{formatChineseSplitNumber(Math.floor(stats.lifetimeExperience ?? 0))}</strong>
                  </div>
                }
              >
                <div className="insight-progress-bar">
                  <div className="insight-progress-fill" style={{ width: `${isPendingBreakthrough || realmInfo.layerProgress >= 99.99 ? 100 : realmInfo.layerProgress}%` }} />
                </div>
              </FloatingTooltip>
              {(isPendingBreakthrough || (realmInfo.layerProgress >= 99.99 && (stats.experience ?? 0) > 0)) && (
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
                <strong className="tone-green"><UnitNumber tooltipLabel={t('insight.quick.hp')} tone="jade" value={attributes?.maxHitpoints ?? 0} /></strong>
              </div>
              <div className="insight-quick-item">
                <span>{t('insight.quick.mp')}</span>
                <strong className="tone-blue"><UnitNumber tooltipLabel={t('insight.quick.mp')} tone="qi" value={attributes?.maxManapoints ?? 0} /></strong>
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
                  label={t('insight.foundation.reserve')}
                  value={stats.foundation === undefined ? t('common.emptyData') : Math.floor(stats.foundation)}
                  tone="gold"
                  tooltip={t('insight.foundation.reserveTooltip')}
                  tooltipTone="gold"
                />
              </div>

              <div className="insight-foundation-pill-row">
                <PillStatLabel
                  label={t('insight.foundation.combatExp')}
                  value={stats.combatExperience === undefined ? t('common.emptyData') : Math.floor(stats.combatExperience)}
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
            {attributes && <CombatAttributePanel attributes={attributes} buffs={buffs} />}
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
                <PillStatLabel label={t('insight.bio.totalMonsters')} value={stats.combatEnemiesDefeated === undefined ? t('common.emptyData') : biographyData.totalMonsters} tone="crimson" tooltip={t('insight.bio.totalMonstersTooltip')} />
                <PillStatLabel label={t('insight.bio.normalKills')} value={stats.normalKills === undefined ? t('common.emptyData') : Math.floor(stats.normalKills)} tone="bronze" tooltip={t('insight.bio.normalKillsTooltip')} />
                <PillStatLabel label={t('insight.bio.eliteKills')} value={stats.eliteKills === undefined ? t('common.emptyData') : Math.floor(stats.eliteKills)} tone="gold" tooltip={t('insight.bio.eliteKillsTooltip')} />
                <PillStatLabel label={t('insight.bio.bossKills')} value={stats.bossKills === undefined ? t('common.emptyData') : Math.floor(stats.bossKills)} tone="crimson" tooltip={t('insight.bio.bossKillsTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupRivalry')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.bio.playersDefeated')} value={duel === undefined ? t('common.emptyData') : biographyData.playersDefeated} tone="emerald" tooltip={t('insight.bio.playersDefeatedTooltip')} />
                <PillStatLabel label={t('insight.bio.totalDefeats')} value={stats.combatLosses === undefined ? t('common.emptyData') : biographyData.totalDefeats} tone="crimson" tooltip={t('insight.bio.totalDefeatsTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupCultivation')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label={t('insight.bio.cultivation')} value={stats.cultivationSeconds === undefined ? t('common.emptyData') : t('insight.bio.cultivationValue', { hours: formatNumber(biographyData.cultivationHours) })} tone="gold" />
                <PillStatLabel label={t('insight.bio.lifespan')} value={stats.ageSeconds === undefined || stats.lifespanYears === undefined ? t('common.emptyData') : t('insight.bio.lifespanValue', { age: formatNumber(Math.floor(stats.ageSeconds / 31536000)), lifespan: formatNumber(Math.floor(stats.lifespanYears)) })} tone="emerald" tooltip={t('insight.bio.lifespanTooltip')} />
              </div>
            </div>
          </div>
        )}
      </div>
    </section>
  )
})
