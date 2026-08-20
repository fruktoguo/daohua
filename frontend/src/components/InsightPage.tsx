import { memo, useMemo, useState } from 'react'
import { VectorCharacter } from './VectorCharacter'
import { RealmBadge } from './RealmBadge'
import { FloatingInfoCard, FloatingPopover, FloatingTooltip } from './FloatingPopover'
import { formatNumber } from '../game/format'
import {
  PercentValue,
  DEFAULT_ROOT_ATTRIBUTE_POINTS,
  BASIC_ATTACK_CONFIG,
  BASELINE_COMBAT_ATTRIBUTES,
  calculateRootLevelAndProgress,
  calculateRealmInfo,
} from '../game/combatConstants'
import { useGameStore } from '../game/gameStore'

type InsightTab = 'roots' | 'combat' | 'biography'

interface StatRowProps {
  label: string
  value: string | React.JSX.Element
  tooltip: string
  valueClass?: string
}

const StatRow = memo(function StatRow({ label, value, tooltip, valueClass = '' }: StatRowProps): React.JSX.Element {
  return (
    <FloatingTooltip placement="top" title={tooltip} tone="gold">
      <div className="insight-stat-row">
        <span className="insight-stat-label">{label}</span>
        <strong className={`insight-stat-value ${valueClass}`}>{value}</strong>
      </div>
    </FloatingTooltip>
  )
})

export const InsightPage = memo(function InsightPage(): React.JSX.Element {
  const [activeTab, setActiveTab] = useState<InsightTab>('roots')

  const name = useGameStore(state => state.name)
  const stats = useGameStore(state => state.stats)
  const loadout = useGameStore(state => state.combat)
  const attributes = useGameStore(state => state.combatAttributes)
  const world = useGameStore(state => state.world)
  const buffs = useGameStore(state => state.buffs)

  // 境界计算体系 (大境界 · 位阶 · 小境界)
  const realmInfo = useMemo(() => calculateRealmInfo(stats.spirit ?? 0), [stats.spirit])

  // 六维根基换算定义 (横向展示，六维经验累加不清空)
  const dimensions = useMemo(() => {
    const staminaLevel: number = loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS // 体魄 10
    const meridiansLevel: number = loadout?.intelligenceLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS // 经脉 10
    const boneLevel: number = loadout?.defenseLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS // 根骨 10
    const strengthLevel: number = loadout?.attackLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS // 力道 10
    const consciousnessLevel: number = loadout?.swordsmanshipLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS // 神识 10
    const agilityLevel: number = loadout?.talismanLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS // 身法 10

    // 格式化经验显示：例如 5.8万 / 100.0万
    const formatExpProgress = (level: number) => {
      const totalExp = 100.0 * Math.pow(Math.max(1.0, level) - 1.0, 3.0) + (level * 580.0)
      const res = calculateRootLevelAndProgress(totalExp)
      const cur = res.currentProgress >= 10000 ? `${(res.currentProgress / 10000).toFixed(1)}万` : res.currentProgress.toFixed(0)
      const req = res.levelRequirement >= 10000 ? `${(res.levelRequirement / 10000).toFixed(1)}万` : res.levelRequirement.toFixed(0)
      return `${cur} / ${req}`
    }

    return [
      {
        id: 'stamina',
        name: '体魄',
        pinyin: 'Tǐ Pò',
        level: staminaLevel,
        expText: formatExpProgress(staminaLevel),
        color: '#10b981',
        tooltip: '体魄强韧肉身宝体。每点提升 1% 最大生命值、1% 护甲、1% 生命值回复。当前为全属性提供百分比倍率放大。',
        benefits: [
          `生命值 +${staminaLevel.toFixed(1)}%`,
          `护甲 +${staminaLevel.toFixed(1)}%`,
          `生命值回复 +${staminaLevel.toFixed(1)}%`,
        ],
      },
      {
        id: 'meridians',
        name: '经脉',
        pinyin: 'Jīng Mài',
        level: meridiansLevel,
        expText: formatExpProgress(meridiansLevel),
        color: '#38bdf8',
        tooltip: '经脉纳通周天灵气。每点提升 1% 最大灵力值、1% 法术抗性、1% 灵力值回复。',
        benefits: [
          `灵力值 +${meridiansLevel.toFixed(1)}%`,
          `法抗 +${meridiansLevel.toFixed(1)}%`,
          `灵力值回复 +${meridiansLevel.toFixed(1)}%`,
        ],
      },
      {
        id: 'bone',
        name: '根骨',
        pinyin: 'Gēn Gǔ',
        level: boneLevel,
        expText: formatExpProgress(boneLevel),
        color: '#a855f7',
        tooltip: '先天仙骨化解万物。每点提升 1% 最大生命值、1% 最大灵力值、1% 化解(拆招)。',
        benefits: [
          `生命值 +${boneLevel.toFixed(1)}%`,
          `灵力值 +${boneLevel.toFixed(1)}%`,
          `化解(拆招) +${boneLevel.toFixed(1)}%`,
        ],
      },
      {
        id: 'strength',
        name: '力道',
        pinyin: 'Lì Dào',
        level: strengthLevel,
        expText: formatExpProgress(strengthLevel),
        color: '#f59e0b',
        tooltip: '力道拔山断岳破阵。每点提升 1% 物理攻击、1% 暴击点数、1% 破招点数。',
        benefits: [
          `物理攻击 +${strengthLevel.toFixed(1)}%`,
          `暴击 +${strengthLevel.toFixed(1)}%`,
          `破招 +${strengthLevel.toFixed(1)}%`,
        ],
      },
      {
        id: 'consciousness',
        name: '神识',
        pinyin: 'Shén Shí',
        level: consciousnessLevel,
        expText: formatExpProgress(consciousnessLevel),
        color: '#c084fc',
        tooltip: '神识灵台锁定杀机。每点提升 2% 术法攻击 (×2 强效加成)、1% 命中点数。',
        benefits: [
          `术法攻击 +${(consciousnessLevel * 2.0).toFixed(1)}% (×2)`,
          `命中 +${consciousnessLevel.toFixed(1)}%`,
        ],
      },
      {
        id: 'agility',
        name: '身法',
        pinyin: 'Shēn Fǎ',
        level: agilityLevel,
        expText: formatExpProgress(agilityLevel),
        color: '#34d399',
        tooltip: '身法凌波趋吉避凶。每点提升 1% 闪避点数、1% 免爆点数、1% 化解(拆招)。',
        benefits: [
          `闪避 +${agilityLevel.toFixed(1)}%`,
          `免爆 +${agilityLevel.toFixed(1)}%`,
          `化解(拆招) +${agilityLevel.toFixed(1)}%`,
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

  // 1. 基础战斗属性 (固定数值类型，全部使用 double)
  // 默认基础: hp100, mp50, ad10, ap5, 物抗10, 法抗0, 命中/闪避/暴击/免爆/破招/拆招均为1, 回复为2/1, 百分比全为0
  const maxHp: number = attributes?.maxHitpoints ?? BASELINE_COMBAT_ATTRIBUTES.maxHp
  const maxMp: number = attributes?.maxManapoints ?? BASELINE_COMBAT_ATTRIBUTES.maxMp
  const physAtk: number = attributes ? Math.max(1.0, attributes.maxDamage * 0.9) : BASELINE_COMBAT_ATTRIBUTES.physAtk
  const spellAtk: number = attributes ? Math.max(1.0, attributes.maxDamage * 1.1) : BASELINE_COMBAT_ATTRIBUTES.spellAtk
  const armor: number = Math.max(0.0, attributes?.armor ?? BASELINE_COMBAT_ATTRIBUTES.armor)
  const magicRes: number = Math.max(0.0, attributes?.waterResistance ?? BASELINE_COMBAT_ATTRIBUTES.magicRes)
  const accuracy: number = attributes?.accuracyRating ?? BASELINE_COMBAT_ATTRIBUTES.accuracy
  const evasion: number = attributes?.evasionRating ?? BASELINE_COMBAT_ATTRIBUTES.evasion
  const critPoint: number = attributes ? attributes.criticalChance * 100.0 : BASELINE_COMBAT_ATTRIBUTES.criticalRating
  const critAvoidPoint: number = attributes ? attributes.criticalAvoidance * 100.0 : BASELINE_COMBAT_ATTRIBUTES.critAvoidance
  const parryBreakPoint: number = attributes ? attributes.pierceChance * 100.0 : BASELINE_COMBAT_ATTRIBUTES.breakPower
  const parryResolvePoint: number = attributes ? attributes.damageAbsorptionRatio * 100.0 : BASELINE_COMBAT_ATTRIBUTES.parryResolve

  // 生命回复与灵力回复 (固定值属性，每 5 秒回复量，受六维加成)
  const hpRegenBase = BASELINE_COMBAT_ATTRIBUTES.hpRegen
  const mpRegenBase = BASELINE_COMBAT_ATTRIBUTES.mpRegen
  const hpRegenTotal = hpRegenBase * (1.0 + (loadout?.staminaLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)
  const mpRegenTotal = mpRegenBase * (1.0 + (loadout?.intelligenceLevel ?? DEFAULT_ROOT_ATTRIBUTE_POINTS) * 0.01)

  // 2. 特殊战斗属性 (统一百分比类型 PercentValue，内部均为 double 点数)
  const critDamagePercent = PercentValue.linear((attributes?.criticalDamageRatio ?? BASELINE_COMBAT_ATTRIBUTES.percentPoints) * 100.0 + 150.0)
  const actionSpeedPoints: number = BASIC_ATTACK_CONFIG.defaultActionSpeedPoints // 默认 0.0 点
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
                <strong>{realmInfo.layerProgress.toFixed(1)} / 100.0 ({realmInfo.layerProgress.toFixed(1)}%)</strong>
              </div>
              <div className="insight-progress-bar">
                <div className="insight-progress-fill" style={{ width: `${realmInfo.layerProgress}%` }} />
              </div>
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

        {/* 内视三大核心分类导航：根基 | 斗法 | 生平 */}
        <nav className="insight-tabs" role="tablist">
          <button
            className={`insight-tab-btn ${activeTab === 'roots' ? 'active' : ''}`}
            onClick={() => setActiveTab('roots')}
            type="button"
          >
            根基
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
        {/* TAB 1: 根基 (所有六维改为横向显示，六维经验累加不清空)                         */}
        {/* ========================================================================= */}
        {activeTab === 'roots' && (
          <div className="insight-panel insight-panel--roots">
            <div className="insight-dimensions-list">
              {dimensions.map(dim => (
                <FloatingPopover
                  key={dim.id}
                  placement="top"
                  content={
                    <FloatingInfoCard
                      title={`${dim.name} (${dim.pinyin})`}
                      badge={`Lv. ${dim.level.toFixed(0)}`}
                      badgeTone="gold"
                      subtitle={`道基经验：${dim.expText}`}
                      description={dim.tooltip}
                      lines={dim.benefits.map(b => `收益提升：${b}`)}
                    />
                  }
                >
                  <div className="insight-dimension-row" style={{ borderColor: `${dim.color}44` }}>
                    <div className="insight-dim-row-left">
                      <div className="insight-dim-identity">
                        <strong className="insight-dim-name" style={{ color: dim.color }}>{dim.name}</strong>
                        <span className="insight-dim-level" style={{ backgroundColor: `${dim.color}22`, color: dim.color, borderColor: `${dim.color}66` }}>
                          {dim.level.toFixed(0)}
                        </span>
                      </div>
                      <div className="insight-dim-exp">
                        <span className="insight-dim-exp-label">修为：</span>
                        <strong className="insight-dim-exp-val">{dim.expText}</strong>
                      </div>
                    </div>

                    <div className="insight-dim-row-right">
                      <div className="insight-dim-benefits-row">
                        {dim.benefits.map((benefit, idx) => (
                          <span className="insight-benefit-tag" key={idx}>
                            {benefit}
                          </span>
                        ))}
                      </div>
                    </div>
                  </div>
                </FloatingPopover>
              ))}
            </div>
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 2: 斗法 (所有属性具体效果卸载到 hover 悬浮菜单 Tooltip 中)                */}
        {/* ========================================================================= */}
        {activeTab === 'combat' && (
          <div className="insight-panel insight-panel--combat">
            <div className="insight-stat-sections">
              {/* 1. 基础战斗属性 (固定数值类型 double) */}
              <div className="insight-stat-box">
                <h4 className="insight-box-title">基础战斗属性</h4>
                <div className="insight-stat-rows">
                  <StatRow label="生命值" value={maxHp.toFixed(1)} tooltip="仙躯气血总量。归零时重伤败退。受体魄与根骨百分比加成。" valueClass="tone-green" />
                  <StatRow label="灵力值" value={maxMp.toFixed(1)} tooltip="气海灵力储量。施展神通与法术时消耗。受经脉与根骨百分比加成。" valueClass="tone-blue" />
                  <StatRow label="生命值回复" value={`${hpRegenTotal.toFixed(1)} / 5s`} tooltip="仙躯每 5 秒自然回复的生命值点数。基础 2.0，受体魄百分比加成。" valueClass="tone-green" />
                  <StatRow label="灵力回复" value={`${mpRegenTotal.toFixed(1)} / 5s`} tooltip="气海每 5 秒自然回复的灵力值点数。基础 1.0，受经脉百分比加成。" valueClass="tone-blue" />
                  <StatRow label="物理攻击" value={physAtk.toFixed(1)} tooltip="物理杀伐威力。决定物理技能与普攻的基础输出。受力道百分比加成。" valueClass="tone-gold" />
                  <StatRow label="术法攻击" value={spellAtk.toFixed(1)} tooltip="术法神通威力。决定术法轰击与治疗效果。受神识×2 百分比加成。" valueClass="tone-blue" />
                  <StatRow label="护甲 (物抗)" value={armor.toFixed(1)} tooltip="抵御物理攻击伤害。减伤公式：伤害 = 原始伤害 × (原始伤害 / (原始伤害 + 护甲 × 5.0 + 1.0))。" />
                  <StatRow label="法抗" value={magicRes.toFixed(1)} tooltip="抵御术法与元素伤害。减伤公式：伤害 = 原始伤害 × (原始伤害 / (原始伤害 + 法抗 × 5.0 + 1.0))。" />
                  <StatRow label="命中" value={accuracy.toFixed(1)} tooltip="命中 vs 闪避对抗。命中率 = 自身命中 / (自身命中 + 对方闪避 × 1.0 + 1.0)。破招时等效翻倍。" />
                  <StatRow label="闪避" value={evasion.toFixed(1)} tooltip="闪避攻击概率。若成功闪避则完全免除本次伤害。拆招时等效翻倍。" />
                  <StatRow label="暴击" value={critPoint.toFixed(1)} tooltip="暴击 vs 免爆对抗。暴击率 = 自身暴击 / (自身暴击 + 对方免爆 × 1.0 + 1.0)。破招时等效翻倍。" valueClass="tone-gold" />
                  <StatRow label="免爆" value={critAvoidPoint.toFixed(1)} tooltip="抵抗暴击概率。削弱敌方对自身的暴击概率。" />
                  <StatRow label="破招" value={parryBreakPoint.toFixed(1)} tooltip="破招对抗判定。当破招 > 对方拆招时按对抗概率触发。触发成功：本次命中美与暴击效果翻倍！" valueClass="tone-gold" />
                  <StatRow label="拆招 (化解)" value={parryResolvePoint.toFixed(1)} tooltip="拆招对抗判定。当拆招 > 对方破招时按对抗概率触发。触发成功：本次双抗与闪避效果翻倍！" />
                </div>
              </div>

              {/* 2. 特殊战斗属性 (统一百分比类型 PercentValue) */}
              <div className="insight-stat-box">
                <h4 className="insight-box-title">特殊战斗属性</h4>
                <div className="insight-stat-rows">
                  <StatRow label="暴击伤害" value={critDamagePercent.format()} tooltip="暴击成功时的伤害倍率。基础 150.0% + 额外加成。" valueClass="tone-gold" />
                  <StatRow label="行动速度" value={actionSpeedPercent.format()} tooltip="普通攻击与动作出招速度。默认 0 点无增减。" />
                  <StatRow label="冷却速度" value={cooldownSpeedPercent.format()} tooltip="非线性冷却缩减。公式：缩减率 = 点数 / (点数 + 100)。100 点对应 50% CD缩减。" />
                  <StatRow label="灵力消耗减少" value={manaCostReductionPercent.format()} tooltip="施法与功法灵力消耗非线性减免率。" />
                  <StatRow label="增伤" value={damageBoostPercent.format()} tooltip="最终输出伤害线性放大乘区。倍率：1 + 增伤/100。" valueClass="tone-gold" />
                  <StatRow label="免伤" value={damageReductionPercent.format()} tooltip="最终承伤线性减免乘区。受到伤害降低对应百分比。" valueClass="tone-green" />
                  <StatRow label="护甲穿透" value={armorPenPercent.format()} tooltip="无视敌方护甲百分比。有效护甲 = 护甲 / (1 + 穿透率)。" />
                  <StatRow label="法抗穿透" value={magicPenPercent.format()} tooltip="无视敌方法抗百分比。有效法抗 = 法抗 / (1 + 穿透率)。" />
                  <StatRow label="仇恨增加" value={threatBonusPercent.format()} tooltip="战斗中吸引怪物与敌手攻击的仇恨放大倍率。" />
                </div>
              </div>

              {/* 3. 普通攻击机制 */}
              <div className="insight-stat-box">
                <h4 className="insight-box-title">普通攻击机制 (普攻)</h4>
                <div className="insight-stat-rows">
                  <StatRow label="普攻伤害" value={`${(BASIC_ATTACK_CONFIG.damageRatio * 100.0).toFixed(0)}% 物理伤害`} tooltip="未施展或等待技能冷却时自动进行的普通攻击物理伤害。" valueClass="tone-gold" />
                  <StatRow label="冷却时间 (CD)" value={`${(BASIC_ATTACK_CONFIG.cooldownMilliseconds / 1000.0).toFixed(1)}s`} tooltip="普通攻击无冷却时间。" />
                  <StatRow label="引导施法时间" value={`${(BASIC_ATTACK_CONFIG.castMilliseconds / 1000.0).toFixed(1)}s`} tooltip="普通攻击每次出招固定耗时 1.0 秒。" />
                  <StatRow label="灵力消耗" value={`${BASIC_ATTACK_CONFIG.manaCost.toFixed(0)} 灵力`} tooltip="普通攻击不消耗任何灵力。" valueClass="tone-blue" />
                </div>
              </div>

              {/* 4. 其他属性 · 增免伤与五行 */}
              <div className="insight-stat-box">
                <h4 className="insight-box-title">其他属性 · 增免伤与五行</h4>
                <div className="insight-stat-rows">
                  <StatRow label="物理增伤 / 免伤" value={`${physBoostPercent.format()} / ${physReductionPercent.format()}`} tooltip="物理伤害专属独立放大与减免乘区。" />
                  <StatRow label="术法增伤 / 免伤" value={`${spellBoostPercent.format()} / ${spellReductionPercent.format()}`} tooltip="术法伤害专属独立放大与减免乘区。" />
                  <StatRow label="金系增伤 / 免伤" value={`${metalBoostPercent.format()} / ${metalReductionPercent.format()}`} tooltip="庚金属性伤害专属放大与减免乘区。" />
                  <StatRow label="木系增伤 / 免伤" value={`${woodBoostPercent.format()} / ${woodReductionPercent.format()}`} tooltip="乙木属性伤害专属放大与减免乘区。" />
                  <StatRow label="水系增伤 / 免伤" value={`${waterBoostPercent.format()} / ${waterReductionPercent.format()}`} tooltip="癸水属性伤害专属放大与减免乘区。" />
                  <StatRow label="火系增伤 / 免伤" value={`${fireBoostPercent.format()} / ${fireReductionPercent.format()}`} tooltip="离火属性伤害专属放大与减免乘区。" />
                  <StatRow label="土系增伤 / 免伤" value={`${earthBoostPercent.format()} / ${earthReductionPercent.format()}`} tooltip="戊土属性伤害专属放大与减免乘区。" />
                </div>
              </div>
            </div>

            {/* 运转中的丹药与神通 Buff */}
            {buffs.length > 0 && (
              <div className="insight-buffs-box">
                <h4 className="insight-box-title">当前运转丹药与神通加持</h4>
                <div className="insight-buff-list">
                  {buffs.map(buff => (
                    <div className="insight-buff-pill" key={buff.buffId}>
                      <strong>{buff.name}</strong>
                      {buff.stacks > 1 && <span>×{buff.stacks}</span>}
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

        {/* ========================================================================= */}
        {/* TAB 3: 生平 (精简四大项：斩妖、破敌、败走、苦修，全部使用 double)               */}
        {/* ========================================================================= */}
        {activeTab === 'biography' && (
          <div className="insight-panel insight-panel--biography">
            <div className="insight-records-grid insight-records-grid--clean">
              {/* 1. 斩妖: 击杀怪物数量 (普通, 精英, 妖王) */}
              <FloatingPopover
                placement="top"
                content={
                  <FloatingInfoCard
                    title="斩妖降魔"
                    badge="历练战绩"
                    badgeTone="gold"
                    description="诛灭修仙历练途中遭遇的妖兽邪修统计，降妖除魔以正天道。"
                    stats={[
                      { label: '普通妖修', value: biographyData.normalKills.toFixed(0), tone: 'muted' },
                      { label: '精英大妖', value: biographyData.eliteKills.toFixed(0), tone: 'qi' },
                      { label: '妖王魔尊', value: biographyData.bossKills.toFixed(0), tone: 'gold' },
                    ]}
                  />
                }
              >
                <div className="insight-record-card insight-record-card--featured">
                  <div className="insight-record-header">
                    <h4 className="insight-record-title">斩妖</h4>
                    <span className="insight-record-subtitle">诛灭妖邪总计 {biographyData.totalMonsters.toFixed(0)} 尊</span>
                  </div>
                  <div className="insight-record-subrows">
                    <div className="insight-record-subrow">
                      <span>普通妖修：</span>
                      <strong>{biographyData.normalKills.toFixed(0)}</strong>
                    </div>
                    <div className="insight-record-subrow">
                      <span>精英大妖：</span>
                      <strong className="tone-blue">{biographyData.eliteKills.toFixed(0)}</strong>
                    </div>
                    <div className="insight-record-subrow">
                      <span>妖王魔尊：</span>
                      <strong className="tone-gold">{biographyData.bossKills.toFixed(0)}</strong>
                    </div>
                  </div>
                </div>
              </FloatingPopover>

              {/* 2. 破敌: 击败玩家数量 */}
              <FloatingTooltip placement="top" title="仙魔争锋斗法中击败其他同道修士的战绩" tone="jade">
                <div className="insight-record-card">
                  <div className="insight-record-header">
                    <h4 className="insight-record-title">破敌</h4>
                    <span className="insight-record-subtitle">击败其他修士玩家</span>
                  </div>
                  <div className="insight-record-main-value">
                    <strong className="tone-green">{biographyData.playersDefeated.toFixed(0)}</strong>
                    <span className="insight-record-unit">人</span>
                  </div>
                </div>
              </FloatingTooltip>

              {/* 3. 败走: 任何模式被击败的次数 */}
              <FloatingTooltip placement="top" title="历练或斗法中仙躯重伤败退的累计次数" tone="danger">
                <div className="insight-record-card">
                  <div className="insight-record-header">
                    <h4 className="insight-record-title">败走</h4>
                    <span className="insight-record-subtitle">任何战斗模式败退</span>
                  </div>
                  <div className="insight-record-main-value">
                    <strong className="tone-red">{biographyData.totalDefeats.toFixed(0)}</strong>
                    <span className="insight-record-unit">次</span>
                  </div>
                </div>
              </FloatingTooltip>

              {/* 4. 苦修: 进行修炼的总时长 */}
              <FloatingTooltip placement="top" title="大周天吐纳修炼所消耗的仙道光阴" tone="gold">
                <div className="insight-record-card">
                  <div className="insight-record-header">
                    <h4 className="insight-record-title">苦修</h4>
                    <span className="insight-record-subtitle">潜心闭关修炼总时长</span>
                  </div>
                  <div className="insight-record-main-value">
                    <strong className="tone-gold">{biographyData.cultivationHours.toFixed(2)}</strong>
                    <span className="insight-record-unit">小时 ({biographyData.cultivationSeconds.toFixed(0)} 秒)</span>
                  </div>
                </div>
              </FloatingTooltip>
            </div>
          </div>
        )}
      </div>
    </section>
  )
})
