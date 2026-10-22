import { memo, useMemo, useState } from 'react'
import { RealmBadge } from './RealmBadge'
import { PillStatLabel, PillBonusLabel, type PillTone } from './PillLabel'
import { formatChineseSplitNumber, formatNumber } from '../game/format'
import { FloatingTooltip } from './FloatingPopover'
import { UnitNumber } from './UnitNumber'
import {
  DEFAULT_ROOT_ATTRIBUTE_POINTS,
  TECHNIQUE_IDS,
  TECHNIQUE_SPEED_RATIO_PER_LEVEL,
  getRootCumulativeExpForLevel,
  calculateRealmInfo,
  calculateRealmProgress,
  calculateBaseAttributes,
  calculateTechniqueProgress,
  calculateTraitCapacity,
  TRAIT_DEFINITIONS,
} from '../game/combatConstants'
import { useGameStore } from '../game/gameStore'
import { gameClient } from '../network/gameClient'
import { useT } from '../i18n'
import { CombatAttributePanel } from './CombatAttributePanel'
import { BreakthroughConfirmModal } from './BreakthroughConfirmModal'
import { HeavenGateModal, LINGGEN_AXIS_CAP, LINGGEN_ELEMENT_KEYS } from './HeavenGateModal'
import { linggenBonusPercent, linggenReducePercent, shouldShowOpenHeavenGate } from '../game/linggenCalculations'
import { StatGlyph } from './StatGlyph'
import { TraitBarItem } from './TraitBarItem'

/** 内视五大核心模块 Tab 标识 */
type InsightTab = 'roots' | 'foundation' | 'combat' | 'technique' | 'biography' | 'traits'

/** 根基模块二级子 Tab 标识 (六维灵根与基础属性乘区) */
type RootsSubTab = 'sixDimensions' | 'baseAttributes' | 'linggen'

/**
 * 角色内视修行总览主页面 (InsightPage)
 *
 * 体系与交互结构：
 * 1. 头部修仙境界展示：当前大境界/境界阶段/小境界 (RealmBadge)、所在地灵气倍率、修为突破进度条与一键突破按钮；
 * 2. 根基 Tab (roots)：
 *    - 六维根基 (sixDimensions)：体魄、经脉、根骨、力道、神识、身法等级与属性固定值加成；
 *    - 基础属性 (baseAttributes)：展示当前境界的基础属性底座数值 (Base Pool)；
 * 3. 底蕴 Tab (foundation)：道行底蕴与战斗历练值纯净展示；
 * 4. 斗法 Tab (combat)：展示攻防、博弈对抗 (命中/闪避/暴击/化解)、时序与五行增免伤等全量乘区，以及运转中的丹药与神通 Buff；
 * 5. 生平 Tab (biography)：记录斩妖总数、玩家争锋破敌战绩及闭关苦修累计时长。
 */
export const InsightPage = memo(function InsightPage(): React.JSX.Element {
  const t = useT()
  const [activeTab, setActiveTab] = useState<InsightTab>('roots')
  const [rootsSubTab, setRootsSubTab] = useState<RootsSubTab>('sixDimensions')
  const [isBreakthroughModalOpen, setIsBreakthroughModalOpen] = useState(false)
  const [isHeavenGateModalOpen, setIsHeavenGateModalOpen] = useState(false)

  const name = useGameStore(state => state.name)
  const stats = useGameStore(state => state.stats)
  const isPendingBreakthrough = useGameStore(state => state.isPendingBreakthrough)
  const loadout = useGameStore(state => state.combat)
  const attributes = useGameStore(state => state.combatAttributes)
  const buffs = useGameStore(state => state.buffs)
  const inventory = useGameStore(state => state.inventory)
  // 逆命丹持有量：逆天改命时可选消耗，把底蕴成本从 25% 降到 5%。
  const reverseFatePillCount = useMemo(
    () => inventory.reduce((total, item) => item.itemId === 'item.reverse-fate-pill' ? total + item.amount : total, 0),
    [inventory],
  )

  // 境界计算体系 (大境界 · 境界阶段 · 小境界)
  const realmProgress = useMemo(() => calculateRealmProgress(stats.experience ?? 0, stats.realmLevel), [stats.realmLevel, stats.experience])
  // 叩天门满修但尚未入天门：突破按钮语义改为「开天门」，且服务端会阻断直接突破。
  const expFull = isPendingBreakthrough || realmProgress.progressPercentage >= 99.99
  const showOpenGateBtn = shouldShowOpenHeavenGate(stats.realmLevel ?? 1, stats.heavenGateEntered === true, expFull)
  const realmInfo = useMemo(() => calculateRealmInfo(stats.experience ?? 0, stats.realmLevel), [stats.realmLevel, stats.experience])

  // 六维根基换算定义 (横向展示：数值胶囊 + 属性加成胶囊列表)
  // 文本统一从 insight.root.* 词典取，此处只保留 id / tone / level / bonus 数值
  const dimensions = useMemo(() => {
    const staminaLevel = Math.floor(Math.max(0, loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 体魄
    const meridiansLevel = Math.floor(Math.max(0, loadout?.meridiansLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 经脉
    const boneLevel = Math.floor(Math.max(0, loadout?.boneLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 根骨
    const strengthLevel = Math.floor(Math.max(0, loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 力道
    const consciousnessLevel = Math.floor(Math.max(0, loadout?.consciousnessLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 神识
    const agilityLevel = Math.floor(Math.max(0, loadout?.agilityLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS)) // 身法

    // 格式化经验进度：升级所需经验 = 下一级累计经验 - 当前级累计经验；
    // 当前经验 = loadout 下发的真实六维经验 - 当前级累计经验（缺省按 0 处理）。
    const getProgress = (level: number, exp: number | undefined) => {
      const baseExp = getRootCumulativeExpForLevel(level)
      const nextExp = getRootCumulativeExpForLevel(level + 1.0)
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
          { value: `${Math.floor(staminaLevel)}% +${Math.floor(staminaLevel * 12)}`, tone: 'emerald' as PillTone },
          { value: `${Math.floor(staminaLevel)}% +${Math.floor(staminaLevel)}`, tone: 'bronze' as PillTone },
          { value: `${Math.floor(staminaLevel)}% +${Number((staminaLevel * 0.5).toFixed(1))}`, tone: 'emerald' as PillTone },
        ],
      },
      {
        id: 'meridians',
        tone: 'cyan' as PillTone,
        level: meridiansLevel,
        progress: getProgress(meridiansLevel, loadout?.meridiansExp),
        bonuses: [
          { value: `${Math.floor(meridiansLevel)}% +${Math.floor(meridiansLevel * 7)}`, tone: 'cyan' as PillTone },
          { value: `${Math.floor(meridiansLevel)}% +${Math.floor(meridiansLevel)}`, tone: 'violet' as PillTone },
          { value: `${Math.floor(meridiansLevel * 2)}%`, tone: 'cyan' as PillTone },
        ],
      },
      {
        id: 'bone',
        tone: 'amber' as PillTone,
        level: boneLevel,
        progress: getProgress(boneLevel, loadout?.boneExp),
        bonuses: [
          { value: `${Math.floor(boneLevel)}% +${Math.floor(boneLevel * 12)}`, tone: 'amber' as PillTone },
          { value: `${Math.floor(boneLevel)}% +${Math.floor(boneLevel * 7)}`, tone: 'silver' as PillTone },
          { value: `${Math.floor(boneLevel)}% +${Math.floor(boneLevel)}`, tone: 'amber' as PillTone },
        ],
      },
      {
        id: 'strength',
        tone: 'crimson' as PillTone,
        level: strengthLevel,
        progress: getProgress(strengthLevel, loadout?.attackExp),
        bonuses: [
          { value: `${Math.floor(strengthLevel)}% +${Math.floor(strengthLevel)}`, tone: 'crimson' as PillTone },
          { value: `${Math.floor(strengthLevel)}% +${Math.floor(strengthLevel)}`, tone: 'slate' as PillTone },
          { value: `${Math.floor(strengthLevel)}% +${Math.floor(strengthLevel)}`, tone: 'crimson' as PillTone },
        ],
      },
      {
        id: 'consciousness',
        tone: 'purple' as PillTone,
        level: consciousnessLevel,
        progress: getProgress(consciousnessLevel, loadout?.consciousnessExp),
        bonuses: [
          { value: `${Math.floor(consciousnessLevel * 2)}% +${Math.floor(consciousnessLevel * 2)}`, tone: 'purple' as PillTone },
          { value: `${Math.floor(consciousnessLevel)}% +${Math.floor(consciousnessLevel)}`, tone: 'sky' as PillTone },
        ],
      },
      {
        id: 'agility',
        tone: 'teal' as PillTone,
        level: agilityLevel,
        progress: getProgress(agilityLevel, loadout?.agilityExp),
        bonuses: [
          { value: `${Math.floor(agilityLevel)}% +${Math.floor(agilityLevel)}`, tone: 'teal' as PillTone },
          { value: `${Math.floor(agilityLevel)}% +${Math.floor(agilityLevel)}`, tone: 'muted' as PillTone },
          { value: `${Math.floor(agilityLevel)}% +${Math.floor(agilityLevel)}`, tone: 'teal' as PillTone },
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

  // 特质容量与当前装配特质列表
  const traitCapacity = useMemo(() => calculateTraitCapacity(stats.realmLevel ?? 1), [stats.realmLevel])
  const equippedTraits = useMemo(() => {
    const ids = stats.traitIds ?? []
    const progressMap = new Map((stats.unlockedTraits ?? []).map(t => [t.id, t]))
    return ids
      .map(id => {
        const def = TRAIT_DEFINITIONS[id]
        const prog = progressMap.get(id)
        const level = prog?.level ?? 1
        const exp = prog?.exp ?? 0
        return def ? { id, def, level, exp } : null
      })
      .filter((t): t is { id: string; def: (typeof TRAIT_DEFINITIONS)[string]; level: number; exp: number } => t !== null)
  }, [stats.traitIds, stats.unlockedTraits])

  const equippedTraitCost = useMemo(() => {
    return equippedTraits.reduce((sum, item) => sum + item.def.cost, 0)
  }, [equippedTraits])

  const unlockedTraits = useMemo(() => {
    const list = stats.unlockedTraits ?? []
    return [...list]
      .map(item => {
        const def = TRAIT_DEFINITIONS[item.id]
        return def ? { ...item, def } : null
      })
      .filter((item): item is NonNullable<typeof item> => item !== null)
      .sort((a, b) => {
        if (b.def.quality !== a.def.quality) return b.def.quality - a.def.quality
        if (b.level !== a.level) return b.level - a.level
        return a.id.localeCompare(b.id)
      })
  }, [stats.unlockedTraits])

  const equippedTraitIdSet = useMemo(() => new Set(stats.traitIds ?? []), [stats.traitIds])

  const handleToggleTrait = (id: string, cost: number) => {
    const currentIds = stats.traitIds ?? []
    if (currentIds.includes(id)) {
      const nextIds = currentIds.filter(item => item !== id)
      gameClient.sendCommand('trait.set', { traitIds: nextIds })
    } else {
      if (equippedTraitCost + cost > traitCapacity) {
        useGameStore.getState().setNotice(t('insight.traits.overCapacityTip'))
        return
      }
      const nextIds = [...currentIds, id]
      gameClient.sendCommand('trait.set', { traitIds: nextIds })
    }
  }

  // 七项生活技艺：等级/经验进度复用修为曲线前 30 级线性削减的技艺曲线（0 级起步），每级提供对应技艺行动 5% 速度加成
  const techniques = useMemo(() => {
    const experiences = loadout?.techniqueExperiences ?? {}
    const tones: Record<string, PillTone> = {
      alchemy: 'emerald',
      forging: 'crimson',
      talisman: 'amber',
      enhancement: 'gold',
      formation: 'purple',
      gathering: 'teal',
      mining: 'bronze',
    }
    return TECHNIQUE_IDS.map(id => {
      const progress = calculateTechniqueProgress(experiences[id] ?? 0)
      const maxed = progress.expToNext <= 0 && progress.level > 0
      return {
        id,
        tone: tones[id] ?? ('default' as PillTone),
        level: progress.level,
        speedPercent: progress.level * TECHNIQUE_SPEED_RATIO_PER_LEVEL * 100,
        progress: {
          current: Math.floor(progress.currentLayerExp),
          max: Math.max(1, Math.floor(progress.expToNext)),
          text: maxed
            ? t('insight.technique.maxLevelLabel')
            : t('insight.technique.expProgress', {
                current: formatNumber(Math.floor(progress.currentLayerExp)),
                max: formatNumber(Math.floor(progress.expToNext)),
              }),
        },
      }
    })
  }, [loadout?.techniqueExperiences, t])

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
          <div className="insight-hero-info">
            <div className="insight-title-row">
              <h2 className="insight-main-title">{name}</h2>
              <RealmBadge realmInfo={realmInfo} />
            </div>
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
              {showOpenGateBtn && (
                <button
                  className="insight-breakthrough-btn"
                  onClick={() => setIsHeavenGateModalOpen(true)}
                  type="button"
                >
                  {t('insight.heavenGate.openBtn')}
                </button>
              )}
              {!showOpenGateBtn && (isPendingBreakthrough || (realmInfo.layerProgress >= 99.99 && (stats.experience ?? 0) > 0)) && (
                <button
                  className="insight-breakthrough-btn"
                  onClick={() => setIsBreakthroughModalOpen(true)}
                  type="button"
                >
                  {t('insight.breakthroughBtn')}
                </button>
              )}
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
            className={`insight-tab-btn ${activeTab === 'technique' ? 'active' : ''}`}
            onClick={() => setActiveTab('technique')}
            type="button"
          >
            {t('insight.tab.technique')}
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'biography' ? 'active' : ''}`}
            onClick={() => setActiveTab('biography')}
            type="button"
          >
            {t('insight.tab.biography')}
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'traits' ? 'active' : ''}`}
            onClick={() => setActiveTab('traits')}
            type="button"
          >
            {t('insight.tab.traits')}
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
              {stats.heavenGateEntered === true && (
                <button
                  className={`insight-sub-tab-btn ${rootsSubTab === 'linggen' ? 'active' : ''}`}
                  onClick={() => setRootsSubTab('linggen')}
                  type="button"
                >
                  {t('insight.subTab.linggen')}
                </button>
              )}
            </div>

            {rootsSubTab === 'sixDimensions' ? (
              <div className="insight-roots-list">
                {dimensions.map(dim => (
                  <div className="insight-root-row" key={dim.id}>
                    <PillStatLabel
                      icon={<StatGlyph glyph={dim.id === 'strength' ? 'attack' : dim.id} />}
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
            ) : rootsSubTab === 'linggen' ? (
              <div className="insight-linggen-layout">
                <div className="insight-roots-list">
                  {LINGGEN_ELEMENT_KEYS.map(key => {
                    const points = stats.heavenGateConfirmed?.[key] ?? 0
                    const bonus = linggenBonusPercent(points)
                    const reduce = linggenReducePercent(points)
                    return (
                      <div className="insight-root-row" key={key}>
                        <PillStatLabel
                          label={t(`insight.heavenGate.element.${key}`)}
                          value={points}
                          tone="gold"
                          tooltip={t('insight.heavenGate.enterHint')}
                          tooltipTone="gold"
                        />
                        <div className="insight-root-bonuses">
                          <PillBonusLabel
                            label={t('insight.heavenGate.statBonus')}
                            value={`${bonus}%`}
                            tone="gold"
                          />
                          <PillBonusLabel
                            label={t('insight.heavenGate.statReduce')}
                            value={`${reduce}%`}
                            tone="jade"
                          />
                        </div>
                      </div>
                    )
                  })}
                </div>
                <div className="insight-linggen-actions">
                  <span className="insight-linggen-meta">
                    {t('insight.heavenGate.rerollCount', { count: stats.heavenGateRerollCount ?? 0 })}
                  </span>
                  <button
                    className="insight-breakthrough-btn"
                    type="button"
                    onClick={() => setIsHeavenGateModalOpen(true)}
                  >
                    {t('insight.heavenGate.rerollBtn')}
                  </button>
                </div>
              </div>
            ) : (
              <div className="insight-base-layout">
                {/* 第 1 排：血蓝与自然回复 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    icon={<StatGlyph glyph="hp" />}
                    label={t('insight.base.maxHp')}
                    value={Math.floor(basePool.baseHp)}
                    tone="emerald"
                    tooltip={t('insight.combat.hpTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="mp" />}
                    label={t('insight.base.maxMp')}
                    value={Math.floor(basePool.baseMp)}
                    tone="cyan"
                    tooltip={t('insight.combat.mpTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="hp-regen" />}
                    label={t('insight.base.hpRegen')}
                    value={Math.floor(basePool.baseHpRegen)}
                    tone="emerald"
                    tooltip={t('insight.combat.hpRegenTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="mp-regen" />}
                    label={t('insight.base.mpRegen')}
                    value={Math.floor(basePool.baseMpRegen)}
                    tone="cyan"
                    tooltip={t('insight.combat.mpRegenTooltip')}
                  />
                </div>

                {/* 第 2 排：AD / AP / 物理护甲 / 术法抗性 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    icon={<StatGlyph glyph="phys-atk" />}
                    label={t('insight.base.physAtk')}
                    value={Math.floor(basePool.baseAd)}
                    tone="crimson"
                    tooltip={t('insight.base.physAtkTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="spell-atk" />}
                    label={t('insight.base.spellAtk')}
                    value={Math.floor(basePool.baseAp)}
                    tone="indigo"
                    tooltip={t('insight.base.spellAtkTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="armor" />}
                    label={t('insight.base.armor')}
                    value={Math.floor(basePool.baseArmor)}
                    tone="bronze"
                    tooltip={t('insight.combat.armorTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="magic-res" />}
                    label={t('insight.base.magicRes')}
                    value={Math.floor(basePool.baseMagicRes)}
                    tone="violet"
                    tooltip={t('insight.combat.magicResTooltip')}
                  />
                </div>

                {/* 第 3 排：命中闪避 暴击免暴 破招化解 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    icon={<StatGlyph glyph="accuracy" />}
                    label={t('insight.base.accuracy')}
                    value={Math.floor(basePool.baseAccuracy)}
                    tone="sky"
                    tooltip={t('insight.base.accuracyTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="evasion" />}
                    label={t('insight.base.evasion')}
                    value={Math.floor(basePool.baseEvasion)}
                    tone="teal"
                    tooltip={t('insight.base.evasionTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="crit" />}
                    label={t('insight.base.crit')}
                    value={Math.floor(basePool.baseCrit)}
                    tone="gold"
                    tooltip={t('insight.base.critTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="crit-avoid" />}
                    label={t('insight.base.critAvoid')}
                    value={Math.floor(basePool.baseCritAvoid)}
                    tone="slate"
                    tooltip={t('insight.base.critAvoidTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="parry-break" />}
                    label={t('insight.base.parryBreak')}
                    value={Math.floor(basePool.baseBreak)}
                    tone="amber"
                    tooltip={t('insight.base.parryBreakTooltip')}
                  />
                  <PillStatLabel
                    icon={<StatGlyph glyph="parry-resolve" />}
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
                  icon={<StatGlyph glyph="foundation" />}
                  label={t('insight.foundation.reserve')}
                  value={stats.foundation === undefined ? t('common.emptyData') : Math.floor(stats.foundation)}
                  tone="gold"
                  tooltip={t('insight.foundation.reserveTooltip')}
                  tooltipTone="gold"
                />
              </div>

              <div className="insight-foundation-pill-row">
                <PillStatLabel
                  icon={<StatGlyph glyph="combat-exp" />}
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
            {attributes && (
              <CombatAttributePanel
                attributes={attributes}
                buffs={buffs}
                loadout={{ skills: loadout?.abilitySlots ?? [], equipment: loadout?.equipment }}
              />
            )}
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 4: 技艺 (七项生活技艺：等级 + 本级经验进度 + 每级 5% 行动速度加成)          */}
        {/* ========================================================================= */}
        {activeTab === 'technique' && (
          <div className="insight-panel insight-panel--technique">
            <div className="insight-roots-list">
              {techniques.map(tech => (
                <div className="insight-root-row" key={tech.id}>
                  <PillStatLabel
                    icon={<StatGlyph glyph={tech.id} />}
                    label={t(`insight.technique.${tech.id}`)}
                    progress={tech.progress}
                    tone={tech.tone}
                    tooltip={t(`insight.technique.${tech.id}Tooltip`)}
                    tooltipTone="gold"
                    value={t('insight.technique.levelLabel', { level: tech.level })}
                  />
                  <div className="insight-root-bonuses">
                    <PillBonusLabel
                      label={t('insight.technique.speedBonusLabel')}
                      tooltip={t('insight.technique.speedBonusTooltip')}
                      tooltipTone="gold"
                      tone={tech.tone}
                      value={`+${formatNumber(tech.speedPercent, { maxDecimals: 0 })}%`}
                    />
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 5: 生平 (历练、争锋、修行)                                             */}
        {/* ========================================================================= */}
        {activeTab === 'biography' && (
          <div className="insight-panel insight-panel--biography">
            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupAdventure')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel icon={<StatGlyph glyph="bio-monsters" />} label={t('insight.bio.totalMonsters')} value={stats.combatEnemiesDefeated === undefined ? t('common.emptyData') : biographyData.totalMonsters} tone="crimson" tooltip={t('insight.bio.totalMonstersTooltip')} />
                <PillStatLabel icon={<StatGlyph glyph="bio-normal" />} label={t('insight.bio.normalKills')} value={stats.normalKills === undefined ? t('common.emptyData') : Math.floor(stats.normalKills)} tone="bronze" tooltip={t('insight.bio.normalKillsTooltip')} />
                <PillStatLabel icon={<StatGlyph glyph="bio-elite" />} label={t('insight.bio.eliteKills')} value={stats.eliteKills === undefined ? t('common.emptyData') : Math.floor(stats.eliteKills)} tone="gold" tooltip={t('insight.bio.eliteKillsTooltip')} />
                <PillStatLabel icon={<StatGlyph glyph="bio-boss" />} label={t('insight.bio.bossKills')} value={stats.bossKills === undefined ? t('common.emptyData') : Math.floor(stats.bossKills)} tone="crimson" tooltip={t('insight.bio.bossKillsTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupRivalry')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel icon={<StatGlyph glyph="bio-pvp-win" />} label={t('insight.bio.playersDefeated')} value={duel === undefined ? t('common.emptyData') : biographyData.playersDefeated} tone="emerald" tooltip={t('insight.bio.playersDefeatedTooltip')} />
                <PillStatLabel icon={<StatGlyph glyph="bio-pvp-loss" />} label={t('insight.bio.totalDefeats')} value={stats.combatLosses === undefined ? t('common.emptyData') : biographyData.totalDefeats} tone="crimson" tooltip={t('insight.bio.totalDefeatsTooltip')} />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">{t('insight.bio.groupCultivation')}</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel icon={<StatGlyph glyph="bio-cultivation" />} label={t('insight.bio.cultivation')} value={stats.cultivationSeconds === undefined ? t('common.emptyData') : t('insight.bio.cultivationValue', { hours: formatNumber(biographyData.cultivationHours) })} tone="gold" />
                <PillStatLabel icon={<StatGlyph glyph="bio-lifespan" />} label={t('insight.bio.lifespan')} value={stats.ageSeconds === undefined || stats.lifespanYears === undefined ? t('common.emptyData') : t('insight.bio.lifespanValue', { age: formatNumber(Math.floor(stats.ageSeconds / 31536000)), lifespan: formatNumber(Math.floor(stats.lifespanYears)) })} tone="emerald" tooltip={t('insight.bio.lifespanTooltip')} />
              </div>
            </div>
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 6: 特质 (展现当前装配特质与容量仪表盘)                                     */}
        {/* ========================================================================= */}
        {activeTab === 'traits' && (
          <div className="insight-panel insight-panel--traits">
            {/* 顶部特质容量横条 */}
            <div className="insight-traits-capacity-card">
              <div className="insight-traits-capacity-info">
                <span className="insight-traits-capacity-title">{t('insight.traits.capacityLabel')}</span>
                <strong className={equippedTraitCost > traitCapacity ? 'tone-danger' : 'tone-gold'}>
                  {equippedTraitCost} / {traitCapacity} {t('insight.traits.pointUnit')}
                  {traitCapacity >= equippedTraitCost && (
                    <span className="insight-traits-capacity-remain">
                      {' '}({t('insight.traits.remainLabel', { count: traitCapacity - equippedTraitCost })})
                    </span>
                  )}
                </strong>
              </div>
              <div className="insight-traits-progress-bar">
                <div
                  className={`insight-traits-progress-fill ${equippedTraitCost > traitCapacity ? 'is-overflow' : ''}`}
                  style={{ width: `${Math.min(100, (equippedTraitCost / Math.max(1, traitCapacity)) * 100)}%` }}
                />
              </div>
            </div>

            {/* 已装配特质列表 */}
            <div className="insight-traits-section">
              <h3 className="insight-traits-section-title">
                {t('insight.traits.equippedListTitle')}
                <span className="insight-traits-section-count">({equippedTraits.length})</span>
              </h3>
              {equippedTraits.length === 0 ? (
                <div className="nearby-empty insight-traits-empty">
                  <p>{t('insight.traits.noneEquippedTip')}</p>
                </div>
              ) : (
                <div className="insight-traits-bar-list">
                  {equippedTraits.map(item => (
                    <TraitBarItem
                      actionLabel={t('insight.traits.unequipBtn')}
                      def={item.def}
                      exp={item.exp}
                      isEquipped
                      key={item.id}
                      level={item.level}
                      mode="insight"
                      onAction={() => handleToggleTrait(item.id, item.def.cost)}
                    />
                  ))}
                </div>
              )}
            </div>

            {/* 已解锁特质列表 */}
            <div className="insight-traits-section">
              <h3 className="insight-traits-section-title">
                {t('insight.traits.unlockedListTitle')}
                <span className="insight-traits-section-count">({unlockedTraits.length})</span>
              </h3>
              {unlockedTraits.length === 0 ? (
                <div className="nearby-empty insight-traits-empty">
                  <p>{t('insight.traits.noneUnlockedTip')}</p>
                </div>
              ) : (
                <div className="insight-traits-bar-list">
                  {unlockedTraits.map(item => {
                    const isEquipped = equippedTraitIdSet.has(item.id)
                    const canEquip = isEquipped || equippedTraitCost + item.def.cost <= traitCapacity
                    return (
                      <TraitBarItem
                        actionLabel={isEquipped ? t('insight.traits.unequipBtn') : t('insight.traits.equipBtn')}
                        def={item.def}
                        disabled={!isEquipped && !canEquip}
                        exp={item.exp}
                        isEquipped={isEquipped}
                        key={item.id}
                        level={item.level}
                        mode="insight"
                        onAction={() => handleToggleTrait(item.id, item.def.cost)}
                      />
                    )
                  })}
                </div>
              )}
            </div>
          </div>
        )}
      </div>

      {/* 开天门阵盘：叩天门满修后由此取得五行灵根，入天门后方可继续突破 */}
      <HeavenGateModal
        open={isHeavenGateModalOpen}
        rolled={stats.heavenGateRolled}
        confirmed={stats.heavenGateConfirmed}
        entered={stats.heavenGateEntered === true}
        rerollCount={stats.heavenGateRerollCount ?? 0}
        expToNext={realmProgress.expToNext ?? 0}
        rerollCostPlain={Math.max(1, Math.round((realmProgress.expToNext ?? 0) * 0.25))}
        rerollCostPill={Math.max(1, Math.round((realmProgress.expToNext ?? 0) * 0.05))}
        reverseFatePillCount={reverseFatePillCount}
        onRoll={() => gameClient.sendCommand('progression.heaven_gate', { action: 'roll' })}
        onAllocate={values => gameClient.sendCommand('progression.heaven_gate', {
          action: 'allocate',
          metal: values.metal,
          wood: values.wood,
          water: values.water,
          fire: values.fire,
          earth: values.earth,
        })}
        onEnter={() => gameClient.sendCommand('progression.heaven_gate', { action: 'enter' })}
        onReroll={usePill => gameClient.sendCommand('progression.heaven_gate', { action: 'reroll', usePill })}
        onClose={() => setIsHeavenGateModalOpen(false)}
      />
      {/* 破境突破二次确认弹窗：确认后才发送突破命令，发送即关闭（突破后预览已失效） */}
      <BreakthroughConfirmModal
        open={isBreakthroughModalOpen}
        currentRealmLevel={stats.realmLevel}
        onConfirm={() => {
          gameClient.sendCommand('progression.breakthrough', {})
          setIsBreakthroughModalOpen(false)
        }}
        onClose={() => setIsBreakthroughModalOpen(false)}
      />
    </section>
  )
})
