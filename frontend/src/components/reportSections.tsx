import { memo } from 'react'
import { useGameStore } from '../game/gameStore'
import { formatRemainingSeconds } from '../game/format'
import { findAbilityInCatalog } from '../game/ids'
import { useT } from '../i18n'
import { ItemReference } from './ItemDisplay'
import { UnitNumber } from './UnitNumber'
import type { BattleReport, BattleReportRootExp } from '../game/types'

/** 六维经验展示顺序与行标签（复用 combat.attr.* 既有词条） */
export const ROOT_EXP_ROWS: ReadonlyArray<{ key: keyof BattleReportRootExp; labelKey: string }> = [
  { key: 'stamina', labelKey: 'combat.attr.stamina' },
  { key: 'meridians', labelKey: 'combat.attr.meridian' },
  { key: 'attack', labelKey: 'combat.attr.attack' },
  { key: 'bone', labelKey: 'combat.attr.defense' },
  { key: 'agility', labelKey: 'combat.attr.movement' },
  { key: 'consciousness', labelKey: 'combat.attr.divineSense' },
]

/** 战报开始时刻的 HH:MM 短格式（本地时区） */
export function formatReportTime(unixSeconds: number): string {
  const date = new Date(unixSeconds * 1000)
  const hh = String(date.getHours()).padStart(2, '0')
  const mm = String(date.getMinutes()).padStart(2, '0')
  return `${hh}:${mm}`
}

/**
 * 账本是否有任何可展示内容：经验、物品、轮次、遭遇、击杀、死亡、斗法胜负任一非零即视为有内容。
 * 用于离线挂机 / 每日统计分区的空账本抑制（全零账本不渲染分区）。
 */
export function reportHasContent(report: BattleReport): boolean {
  if (report.experience !== 0 || report.foundation !== 0 || report.combatExperience !== 0) return true
  if (ROOT_EXP_ROWS.some(row => report.rootExp[row.key] !== 0)) return true
  if (report.abilityExp.length > 0 || report.levelUps.length > 0) return true
  if (report.items.length > 0 || report.itemsLost.length > 0) return true
  if (report.cycles > 0 || report.encounters > 0 || report.deaths > 0) return true
  if (report.normalKills > 0 || report.eliteKills > 0 || report.bossKills > 0) return true
  return report.duelWins > 0 || report.duelLosses > 0
}

/**
 * 「获取经验」小节：修为 / 底蕴净变动 / 战斗经验 / 六维 / 功法技能经验 + 期间等级提升（境界/六维/功法）。
 * 除底蕴（净变动，可为负，非零即展示）外，其余数值行仅在 >0 时展示；功法名按目录解析，未命中回退 abilityId。
 */
export const ReportExpSection = memo(function ReportExpSection({ report }: { report: BattleReport }): React.JSX.Element | null {
  const t = useT()
  const abilities = useGameStore(state => state.abilities)
  const rootExpEntries = ROOT_EXP_ROWS
    .map(row => ({ key: row.key, label: t(row.labelKey), value: report.rootExp[row.key] }))
    .filter(entry => entry.value > 0)
  const abilityExpEntries = report.abilityExp
    .filter(entry => entry.experience > 0)
    .map(entry => ({
      abilityId: entry.abilityId,
      label: findAbilityInCatalog(abilities, entry.abilityId)?.name || entry.abilityId,
      value: entry.experience,
    }))
  // 等级提升行：realm→「境界」、root→六维标签（targetId 为六维键）、ability→功法名（targetId 为功法 ID）。
  // 同一目标在账期内的多段升级合并为一条「首段起点 → 末段终点」，顺序按目标首次出现排列。
  const levelUpEntries: Array<{ key: string; name: string; from: number; to: number }> = []
  const levelUpByTarget = new Map<string, { key: string; name: string; from: number; to: number }>()
  for (const entry of report.levelUps) {
    let name: string
    if (entry.kind === 'realm') {
      name = t('combat.report.realmLabel')
    } else if (entry.kind === 'root') {
      const row = ROOT_EXP_ROWS.find(item => item.key === entry.targetId)
      name = row ? t(row.labelKey) : entry.targetId
    } else {
      name = findAbilityInCatalog(abilities, entry.targetId)?.name || entry.targetId
    }
    const key = `${entry.kind}:${entry.targetId}`
    const existing = levelUpByTarget.get(key)
    if (existing) {
      existing.from = Math.min(existing.from, entry.fromLevel)
      existing.to = Math.max(existing.to, entry.toLevel)
    } else {
      const merged = { key, name, from: entry.fromLevel, to: entry.toLevel }
      levelUpByTarget.set(key, merged)
      levelUpEntries.push(merged)
    }
  }

  const hasAny = report.experience > 0 || report.foundation !== 0 || report.combatExperience > 0
    || rootExpEntries.length > 0 || abilityExpEntries.length > 0 || levelUpEntries.length > 0
  if (!hasAny) return null

  return (
    <div className="battle-report-section">
      <div className="battle-report-metrics">
        {report.experience > 0 && (
          <span>{t('combat.report.experience')} <strong><UnitNumber disableTooltip prefix="+" value={report.experience} /></strong></span>
        )}
        {/* 底蕴为净变动口径：沉淀为正、消耗兑换为负，负值由数值自带负号呈现 */}
        {report.foundation !== 0 && (
          <span>{t('combat.report.foundation')} <strong><UnitNumber disableTooltip prefix={report.foundation > 0 ? '+' : undefined} value={report.foundation} /></strong></span>
        )}
        {report.combatExperience > 0 && (
          <span>{t('combat.report.combatExperience')} <strong><UnitNumber disableTooltip prefix="+" value={report.combatExperience} /></strong></span>
        )}
        {rootExpEntries.map(entry => (
          <span key={entry.key}>{entry.label} <strong><UnitNumber disableTooltip prefix="+" value={entry.value} /></strong></span>
        ))}
        {abilityExpEntries.map(entry => (
          <span key={entry.abilityId}>{entry.label} <strong><UnitNumber disableTooltip prefix="+" value={entry.value} /></strong></span>
        ))}
      </div>
      {levelUpEntries.length > 0 && (
        <div className="battle-report-levelups">
          {levelUpEntries.map(entry => (
            <span className="battle-report-levelup" key={entry.key}>
              {t('combat.report.levelUp', { name: entry.name, from: entry.from, to: entry.to })}
            </span>
          ))}
        </div>
      )}
    </div>
  )
})

/**
 * 「物品得失」小节：获得（items）与失去（itemsLost）分组两列展示；
 * 物品一律按暂存区同款复合胶囊渲染（行内图标 + 名称 + ×数量下划线值，悬浮出详情卡片）。
 * 某一侧为空时不渲染该列，两侧皆空时整节不渲染。
 */
export const ReportItemsSection = memo(function ReportItemsSection({ report }: { report: BattleReport }): React.JSX.Element | null {
  const t = useT()
  if (report.items.length === 0 && report.itemsLost.length === 0) return null
  return (
    <div className="battle-report-section">
      <div className="battle-report-item-columns">
        {report.items.length > 0 && (
          <div className="battle-report-item-column">
            <span className="battle-report-item-column-title">{t('combat.report.itemsGained')}</span>
            <div className="battle-report-items">
              {report.items.map(item => (
                <ItemReference amount={item.quantity} itemId={item.itemId} key={item.itemId} />
              ))}
            </div>
          </div>
        )}
        {report.itemsLost.length > 0 && (
          <div className="battle-report-item-column">
            <span className="battle-report-item-column-title">{t('combat.report.itemsLost')}</span>
            <div className="battle-report-items">
              {report.itemsLost.map(item => (
                <ItemReference amount={item.quantity} itemId={item.itemId} key={item.itemId} />
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  )
})

/**
 * 「行动信息」小节：轮次 / 时长 / 遭遇场次 / 分级击杀 / 死亡 / 斗法胜负。
 * 时长：进行中账本用估算服务端当前时刻 - 开始时刻；归档账本用 结束 - 开始。
 * 除时长外的指标行仅在 >0 时展示。
 */
export const ReportInfoSection = memo(function ReportInfoSection({ report, nowSeconds }: {
  report: BattleReport
  /** 估算的服务端当前时刻（Unix 秒），仅进行中账本使用 */
  nowSeconds: number
}): React.JSX.Element {
  const t = useT()
  const durationSeconds = Math.max(0, (report.endedAtUnixSeconds > 0 ? report.endedAtUnixSeconds : nowSeconds) - report.startedAtUnixSeconds)
  const totalKills = report.normalKills + report.eliteKills + report.bossKills
  const lines: React.JSX.Element[] = []
  if (report.cycles > 0) {
    lines.push(<span key="cycles">{t('combat.report.cycles', { n: report.cycles })}</span>)
  }
  lines.push(<span key="duration">{t('combat.report.duration', { time: formatRemainingSeconds(durationSeconds) })}</span>)
  if (report.encounters > 0) {
    lines.push(<span key="encounters">{t('combat.report.encounters', { n: report.encounters })}</span>)
  }
  if (totalKills > 0) {
    lines.push(<span key="kills">{t('combat.report.kills', { normal: report.normalKills, elite: report.eliteKills, boss: report.bossKills })}</span>)
  }
  if (report.deaths > 0) {
    lines.push(<span key="deaths">{t('combat.report.deaths', { n: report.deaths })}</span>)
  }
  if (report.duelWins > 0 || report.duelLosses > 0) {
    lines.push(
      <span className="battle-report-duel-record" key="duel">
        {t('combat.report.duelRecord', { wins: report.duelWins, losses: report.duelLosses })}
      </span>,
    )
  }
  return (
    <div className="battle-report-section">
      <div className="battle-report-metrics">{lines}</div>
    </div>
  )
})

/**
 * 单本账本的完整渲染块：可选头行（标题 + 徽标）+ 三小节。
 * 统计面板与记录面板的各账本分区共用同一结构。
 */
export const ReportBlock = memo(function ReportBlock({ report, title, badge, nowSeconds, className = '' }: {
  report: BattleReport
  /** 头行标题（行动名或固定分区标题；与 badge 同时为空时不渲染头行） */
  title?: string
  /** 头行徽标（如「进行中」） */
  badge?: string
  nowSeconds: number
  className?: string
}): React.JSX.Element {
  return (
    <div className={`battle-report-block ${className}`.trim()}>
      {(title || badge) && (
        <div className="battle-report-block-head">
          {title && <strong>{title}</strong>}
          {badge && <span className="badge">{badge}</span>}
        </div>
      )}
      <ReportInfoSection nowSeconds={nowSeconds} report={report} />
      <ReportItemsSection report={report} />
      <ReportExpSection report={report} />
    </div>
  )
})
