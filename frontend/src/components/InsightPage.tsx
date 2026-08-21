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

type InsightTab = 'roots' | 'foundation' | 'combat' | 'biography'
type RootsSubTab = 'sixDimensions' | 'baseAttributes'

export const InsightPage = memo(function InsightPage(): React.JSX.Element {
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
        name: '体魄',
        tone: 'emerald' as PillTone,
        level: staminaLevel,
        tooltip: '体魄强韧肉身宝体。每点提升 1% 最大生命值、1% 护甲、1% 生命值回复。',
        bonuses: [
          { label: '生命上限', value: `+${staminaLevel.toFixed(1)}%`, tone: 'emerald' as PillTone },
          { label: '护甲', value: `+${staminaLevel.toFixed(1)}%`, tone: 'bronze' as PillTone },
          { label: '生命回复', value: `+${staminaLevel.toFixed(1)}%`, tone: 'emerald' as PillTone },
        ],
      },
      {
        id: 'meridians',
        name: '经脉',
        tone: 'cyan' as PillTone,
        level: meridiansLevel,
        tooltip: '经脉纳通周天灵气。每点提升 1% 最大灵力值、1% 法术抗性、1% 灵力值回复。',
        bonuses: [
          { label: '灵力上限', value: `+${meridiansLevel.toFixed(1)}%`, tone: 'cyan' as PillTone },
          { label: '法抗', value: `+${meridiansLevel.toFixed(1)}%`, tone: 'violet' as PillTone },
          { label: '灵力回复', value: `+${meridiansLevel.toFixed(1)}%`, tone: 'cyan' as PillTone },
        ],
      },
      {
        id: 'bone',
        name: '根骨',
        tone: 'violet' as PillTone,
        level: boneLevel,
        tooltip: '先天仙骨化解万物。每点提升 1% 最大生命值、1% 最大灵力值、1% 化解。',
        bonuses: [
          { label: '生命上限', value: `+${boneLevel.toFixed(1)}%`, tone: 'emerald' as PillTone },
          { label: '灵力上限', value: `+${boneLevel.toFixed(1)}%`, tone: 'cyan' as PillTone },
          { label: '化解', value: `+${boneLevel.toFixed(1)}%`, tone: 'silver' as PillTone },
        ],
      },
      {
        id: 'strength',
        name: '力道',
        tone: 'amber' as PillTone,
        level: strengthLevel,
        tooltip: '力道拔山断岳破阵。每点提升 1% 物理攻击、1% 暴击点数、1% 破招点数。',
        bonuses: [
          { label: '物理攻击', value: `+${strengthLevel.toFixed(1)}%`, tone: 'crimson' as PillTone },
          { label: '暴击', value: `+${strengthLevel.toFixed(1)}%`, tone: 'gold' as PillTone },
          { label: '破招', value: `+${strengthLevel.toFixed(1)}%`, tone: 'amber' as PillTone },
        ],
      },
      {
        id: 'consciousness',
        name: '神识',
        tone: 'indigo' as PillTone,
        level: consciousnessLevel,
        tooltip: '神识灵台锁定杀机。每点提升 2% 术法攻击 (×2 强效加成)、1% 命中点数。',
        bonuses: [
          { label: '术法攻击', value: `+${(consciousnessLevel * 2.0).toFixed(1)}%`, tone: 'indigo' as PillTone },
          { label: '命中', value: `+${consciousnessLevel.toFixed(1)}%`, tone: 'sky' as PillTone },
        ],
      },
      {
        id: 'agility',
        name: '身法',
        tone: 'teal' as PillTone,
        level: agilityLevel,
        tooltip: '身法凌波趋吉避凶。每点提升 1% 闪避点数、1% 免爆点数、1% 化解。',
        bonuses: [
          { label: '闪避', value: `+${agilityLevel.toFixed(1)}%`, tone: 'teal' as PillTone },
          { label: '免爆', value: `+${agilityLevel.toFixed(1)}%`, tone: 'slate' as PillTone },
          { label: '化解', value: `+${agilityLevel.toFixed(1)}%`, tone: 'silver' as PillTone },
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
              <span>所在洞天：</span>
              <strong>{world.locationName || '青云宗·灵脉福地'}</strong>
              <em className="insight-multiplier">（灵气倍率 {(world.cultivationMultiplier || 1.0).toFixed(2)}x）</em>
            </p>
            {/* 突破修为进度 */}
            <div className="insight-progress-block">
              <div className="insight-progress-meta">
                <span>层境突破修为</span>
                {isPendingBreakthrough || realmInfo.layerProgress >= 99.99 ? (
                  <strong className="tone-gold">修为圆满 (待破境)</strong>
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
                  ⚡ 破境突破
                </button>
              )}
            </div>
            {/* 核心生息快照 */}
            <div className="insight-quick-stats">
              <div className="insight-quick-item">
                <span>生命值</span>
                <strong className="tone-green">{formatNumber(maxHp)}</strong>
              </div>
              <div className="insight-quick-item">
                <span>灵力值</span>
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
            根基
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'foundation' ? 'active' : ''}`}
            onClick={() => setActiveTab('foundation')}
            type="button"
          >
            底蕴
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'combat' ? 'active' : ''}`}
            onClick={() => setActiveTab('combat')}
            type="button"
          >
            斗法
          </button>
          <button
            className={`insight-tab-btn ${activeTab === 'biography' ? 'active' : ''}`}
            onClick={() => setActiveTab('biography')}
            type="button"
          >
            生平
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
                六维
              </button>
              <button
                className={`insight-sub-tab-btn ${rootsSubTab === 'baseAttributes' ? 'active' : ''}`}
                onClick={() => setRootsSubTab('baseAttributes')}
                type="button"
              >
                基础
              </button>
            </div>

            {rootsSubTab === 'sixDimensions' ? (
              <div className="insight-roots-list">
                {dimensions.map(dim => (
                  <div className="insight-root-row" key={dim.id}>
                    <PillStatLabel
                      label={dim.name}
                      value={formatNumber(dim.level)}
                      tone={dim.tone}
                      tooltip={dim.tooltip}
                      tooltipTone={dim.tone === 'purple' ? 'purple' : dim.tone === 'qi' ? 'qi' : dim.tone === 'jade' ? 'jade' : 'gold'}
                    />
                    <div className="insight-root-bonuses">
                      {dim.bonuses.map((bonus, idx) => (
                        <PillBonusLabel
                          key={idx}
                          label={bonus.label}
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
                    label="生命上限"
                    value={formatNumber(basePool.baseHp)}
                    tone="emerald"
                    tooltip={`最大生命值，能承受 ${formatNumber(basePool.baseHp)} 点伤害。`}
                  />
                  <PillStatLabel
                    label="灵力上限"
                    value={formatNumber(basePool.baseMp)}
                    tone="cyan"
                    tooltip={`最大灵力值，施展神通与法术的初始灵力储量（${formatNumber(basePool.baseMp)} 点）。`}
                  />
                  <PillStatLabel
                    label="生命回复"
                    value={`${formatNumber(basePool.baseHpRegen)}/5s`}
                    tone="emerald"
                    tooltip={`仙躯每 5 秒自然回复的基础生命值（约为当前境界生命上限的 2%，即 ${formatNumber(basePool.baseHpRegen)} 点）。`}
                  />
                  <PillStatLabel
                    label="灵力回复"
                    value={`${formatNumber(basePool.baseMpRegen)}/5s`}
                    tone="cyan"
                    tooltip={`气海每 5 秒自然回复的基础灵力值（约为当前境界灵力上限的 2%，即 ${formatNumber(basePool.baseMpRegen)} 点）。`}
                  />
                </div>

                {/* 第 2 排：AD / AP / 物理护甲 / 术法抗性 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    label="物理攻击"
                    value={formatNumber(basePool.baseAd)}
                    tone="crimson"
                    tooltip="物理杀伤基础威力，决定物理技能与普通攻击的伤害。"
                  />
                  <PillStatLabel
                    label="术法攻击"
                    value={formatNumber(basePool.baseAp)}
                    tone="indigo"
                    tooltip="法术神通基础威力，决定法术轰击与奇门遁甲的伤害。"
                  />
                  <PillStatLabel
                    label="物理护甲"
                    value={formatNumber(basePool.baseArmor)}
                    tone="bronze"
                    tooltip="抵御物理攻击，护甲越高所受物理伤害越低。"
                  />
                  <PillStatLabel
                    label="术法抗性"
                    value={formatNumber(basePool.baseMagicRes)}
                    tone="violet"
                    tooltip="抵御法术攻击，抗性越高所受法术与元素伤害越低。"
                  />
                </div>

                {/* 第 3 排：命中闪避 暴击免暴 破招化解 */}
                <div className="insight-base-row">
                  <PillStatLabel
                    label="命中"
                    value={formatNumber(basePool.baseAccuracy)}
                    tone="sky"
                    tooltip="命中概率基础点数，提升击中目标的概率。"
                  />
                  <PillStatLabel
                    label="闪避"
                    value={formatNumber(basePool.baseEvasion)}
                    tone="teal"
                    tooltip="闪避回避基础点数，提升回避敌方攻击的概率。"
                  />
                  <PillStatLabel
                    label="暴击"
                    value={formatNumber(basePool.baseCrit)}
                    tone="gold"
                    tooltip="暴击概率基础点数，提升造成会心一击的概率。"
                  />
                  <PillStatLabel
                    label="免暴"
                    value={formatNumber(basePool.baseCritAvoid)}
                    tone="slate"
                    tooltip="免暴抗爆基础点数，降低遭受敌方暴击的概率。"
                  />
                  <PillStatLabel
                    label="破招"
                    value={formatNumber(basePool.baseBreak)}
                    tone="amber"
                    tooltip="破招对抗基础点数，破招成功时本次命中与暴击效果翻倍。"
                  />
                  <PillStatLabel
                    label="化解"
                    value={formatNumber(basePool.baseParry)}
                    tone="silver"
                    tooltip="拆招化解基础点数，化解成功时本次双抗与闪避效果翻倍。"
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
                  label="道基底蕴"
                  value={formatNumber(stats.foundationExp ?? 0)}
                  tone="gold"
                  tooltip="修为达到当前境界满值未突破时，获得的修为将100%转化为底蕴。突破后修炼享受3倍经验加速（消耗等额底蕴）。"
                  tooltipTone="gold"
                />
              </div>

              <div className="insight-foundation-pill-row">
                <PillStatLabel
                  label="战斗经验"
                  value={(stats.combatExperience ?? 0).toFixed(1)}
                  tone="qi"
                  tooltip="击败真实敌方单位基础获得1.0点。每高自身1级+10%(指数增幅)，每低1级-20%(指数递减)。"
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
              <h4 className="insight-pill-group-title">基础战斗属性</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label="生命值" value={maxHp.toFixed(1)} tone="emerald" tooltip="仙躯气血总量。归零时重伤败退。受体魄与根骨百分比加成。" />
                <PillStatLabel label="灵力值" value={maxMp.toFixed(1)} tone="cyan" tooltip="气海灵力储量。施展神通与法术时消耗。受经脉与根骨百分比加成。" />
                <PillStatLabel label="生命回复" value={`${hpRegenTotal.toFixed(1)}/5s`} tone="emerald" tooltip="仙躯每 5 秒自然回复的生命值点数。基础 2.0，受体魄百分比加成。" />
                <PillStatLabel label="灵力回复" value={`${mpRegenTotal.toFixed(1)}/5s`} tone="cyan" tooltip="气海每 5 秒自然回复的灵力值点数。基础 1.0，受经脉百分比加成。" />
                <PillStatLabel label="物理攻击" value={physAtk.toFixed(1)} tone="crimson" tooltip="物理杀伐威力。决定物理技能与普攻的基础输出。受力道百分比加成。" />
                <PillStatLabel label="术法攻击" value={spellAtk.toFixed(1)} tone="indigo" tooltip="术法神通威力。决定术法轰击与治疗效果。受神识×2 百分比加成。" />
                <PillStatLabel label="护甲" value={armor.toFixed(1)} tone="bronze" tooltip="抵御物理攻击伤害。减伤公式：伤害 = 原始伤害 × (原始伤害 / (原始伤害 + 护甲 × 5.0 + 1.0))。" />
                <PillStatLabel label="法抗" value={magicRes.toFixed(1)} tone="violet" tooltip="抵御术法与元素伤害。减伤公式：伤害 = 原始伤害 × (原始伤害 / (原始伤害 + 法抗 × 5.0 + 1.0))。" />
                <PillStatLabel label="命中" value={accuracy.toFixed(1)} tone="sky" tooltip="命中 vs 闪避对抗。命中率 = 自身命中 / (自身命中 + 对方闪避 × 1.0 + 1.0)。破招时等效翻倍。" />
                <PillStatLabel label="闪避" value={evasion.toFixed(1)} tone="teal" tooltip="闪避攻击概率。若成功闪避则完全免除本次伤害。化解时等效翻倍。" />
                <PillStatLabel label="暴击" value={critPoint.toFixed(1)} tone="gold" tooltip="暴击 vs 免爆对抗。暴击率 = 自身暴击 / (自身暴击 + 对方免爆 × 1.0 + 1.0)。破招时等效翻倍。" />
                <PillStatLabel label="免爆" value={critAvoidPoint.toFixed(1)} tone="slate" tooltip="抵抗暴击概率。削弱敌方对自身的暴击概率。" />
                <PillStatLabel label="破招" value={parryBreakPoint.toFixed(1)} tone="amber" tooltip="破招对抗判定。当破招 > 对方化解时按对抗概率触发。触发成功：本次命中与暴击效果翻倍！" />
                <PillStatLabel label="化解" value={parryResolvePoint.toFixed(1)} tone="silver" tooltip="化解对抗判定。当化解 > 对方破招时按对抗概率触发。触发成功：本次双抗与闪避效果翻倍！" />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">特殊战斗属性</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label="暴击伤害" value={critDamagePercent.format()} tone="gold" tooltip="暴击成功时的伤害倍率。基础 150.0% + 额外加成。" />
                <PillStatLabel label="行动速度" value={actionSpeedPercent.format()} tone="teal" tooltip="普通攻击与动作出招速度。默认 0 点无增减。" />
                <PillStatLabel label="冷却速度" value={cooldownSpeedPercent.format()} tone="cyan" tooltip="非线性冷却缩减。公式：缩减率 = 点数 / (点数 + 100)。100 点对应 50% CD缩减。" />
                <PillStatLabel label="灵力减耗" value={manaCostReductionPercent.format()} tone="cyan" tooltip="施法与功法灵力消耗非线性减免率。" />
                <PillStatLabel label="伤害增加" value={damageBoostPercent.format()} tone="gold" tooltip="最终输出伤害线性放大乘区。倍率：1 + 增伤/100。" />
                <PillStatLabel label="伤害减免" value={damageReductionPercent.format()} tone="emerald" tooltip="最终承伤线性减免乘区。受到伤害降低对应百分比。" />
                <PillStatLabel label="护甲穿透" value={armorPenPercent.format()} tone="bronze" tooltip="无视敌方护甲百分比。有效护甲 = 护甲 / (1 + 穿透率)。" />
                <PillStatLabel label="法抗穿透" value={magicPenPercent.format()} tone="violet" tooltip="无视敌方法抗百分比。有效法抗 = 法抗 / (1 + 穿透率)。" />
                <PillStatLabel label="仇恨增加" value={threatBonusPercent.format()} tone="crimson" tooltip="战斗中吸引怪物与敌手攻击的仇恨放大倍率。" />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">增免伤与五行属性</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label="物理增免" value={`${physBoostPercent.format()} / ${physReductionPercent.format()}`} tone="crimson" tooltip="物理伤害专属独立放大与减免乘区。" />
                <PillStatLabel label="术法增免" value={`${spellBoostPercent.format()} / ${spellReductionPercent.format()}`} tone="indigo" tooltip="术法伤害专属独立放大与减免乘区。" />
                <PillStatLabel label="金系增免" value={`${metalBoostPercent.format()} / ${metalReductionPercent.format()}`} tone="gold" tooltip="庚金属性伤害专属放大与减免乘区。" />
                <PillStatLabel label="木系增免" value={`${woodBoostPercent.format()} / ${woodReductionPercent.format()}`} tone="emerald" tooltip="乙木属性伤害专属放大与减免乘区。" />
                <PillStatLabel label="水系增免" value={`${waterBoostPercent.format()} / ${waterReductionPercent.format()}`} tone="cyan" tooltip="癸水属性伤害专属放大与减免乘区。" />
                <PillStatLabel label="火系增免" value={`${fireBoostPercent.format()} / ${fireReductionPercent.format()}`} tone="amber" tooltip="离火属性伤害专属放大与减免乘区。" />
                <PillStatLabel label="土系增免" value={`${earthBoostPercent.format()} / ${earthReductionPercent.format()}`} tone="bronze" tooltip="戊土属性伤害专属放大与减免乘区。" />
              </div>
            </div>

            {/* 运转中的丹药与神通 Buff */}
            {buffs.length > 0 && (
              <div className="insight-pill-group">
                <h4 className="insight-pill-group-title">当前运转丹药与神通加持</h4>
                <div className="insight-pill-group-items">
                  {buffs.map(buff => (
                    <PillStatLabel
                      key={buff.buffId}
                      label={buff.name}
                      value={buff.stacks > 1 ? `×${buff.stacks}` : '生效中'}
                      tone="gold"
                      tooltip={`${buff.name}（${buff.stacks}层）加持运转中`}
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
              <h4 className="insight-pill-group-title">历练</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label="普通妖修" value={biographyData.normalKills.toFixed(0)} tone="slate" tooltip="诛灭普通妖修魔物统计" />
                <PillStatLabel label="精英大妖" value={biographyData.eliteKills.toFixed(0)} tone="cyan" tooltip="斩杀深山大泽精英大妖统计" />
                <PillStatLabel label="妖王魔尊" value={biographyData.bossKills.toFixed(0)} tone="gold" tooltip="镇压绝世妖王魔尊战绩统计" />
                <PillStatLabel label="斩妖总计" value={biographyData.totalMonsters.toFixed(0)} tone="crimson" tooltip="修仙历练途中诛灭妖邪魔物总尊数" />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">争锋</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label="击破同道" value={biographyData.playersDefeated.toFixed(0)} tone="emerald" tooltip="仙魔争锋斗法中击败其他同道修士的战绩统计" />
                <PillStatLabel label="重伤败走" value={biographyData.totalDefeats.toFixed(0)} tone="crimson" tooltip="历练或斗法中仙躯重伤败退的累计次数" />
              </div>
            </div>

            <div className="insight-pill-group">
              <h4 className="insight-pill-group-title">修行</h4>
              <div className="insight-pill-group-items">
                <PillStatLabel label="修炼" value={`${biographyData.cultivationHours.toFixed(2)} 时`} tone="gold" tooltip={`潜心闭关修炼总时长（${biographyData.cultivationSeconds.toFixed(0)} 秒）`} />
              </div>
            </div>
          </div>
        )}
      </div>
    </section>
  )
})
