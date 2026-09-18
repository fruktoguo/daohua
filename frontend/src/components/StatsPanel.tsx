import { memo, useEffect, useMemo, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import { loadBattleReportHistory } from '../game/battleReportStore'
import { formatRemainingSeconds } from '../game/format'
import { findAbilityInCatalog } from '../game/ids'
import { useT } from '../i18n'
import { ItemReference } from './ItemDisplay'
import { UnitNumber } from './UnitNumber'
import type { BattleReport, BattleReportRootExp } from '../game/types'

/** 斗法暂存区到期窗口（秒）：与服务端 StagingWindowSeconds=1200 对齐 */
const STAGED_WINDOW_SECONDS = 1200
/** 历史战报展示上限 */
const HISTORY_LIMIT = 10

/** 六维经验展示顺序与行标签（复用 combat.attr.* 既有词条） */
const ROOT_EXP_ROWS: ReadonlyArray<{ key: keyof BattleReportRootExp; labelKey: string }> = [
  { key: 'stamina', labelKey: 'combat.attr.stamina' },
  { key: 'intelligence', labelKey: 'combat.attr.meridian' },
  { key: 'attack', labelKey: 'combat.attr.attack' },
  { key: 'defense', labelKey: 'combat.attr.defense' },
  { key: 'swordsmanship', labelKey: 'combat.attr.divineSense' },
  { key: 'talisman', labelKey: 'combat.attr.movement' },
]

/** 离线挂机与每日统计单例账本的固定 reportId：归档历史与本地缓存中需剔除，避免与专属分区重复展示 */
const SINGLETON_REPORT_IDS: ReadonlySet<string> = new Set(['offline', 'daily'])

/** 战报开始时刻的 HH:MM 短格式（本地时区） */
function formatReportTime(unixSeconds: number): string {
  const date = new Date(unixSeconds * 1000)
  const hh = String(date.getHours()).padStart(2, '0')
  const mm = String(date.getMinutes()).padStart(2, '0')
  return `${hh}:${mm}`
}

/**
 * 账本是否有任何可展示内容：经验、物品、轮次、遭遇、击杀、死亡、斗法胜负任一非零即视为有内容。
 * 用于离线挂机 / 每日统计分区的空账本抑制（全零账本不渲染分区）。
 */
function reportHasContent(report: BattleReport): boolean {
  if (report.qi !== 0 || report.experience !== 0) return true
  if (ROOT_EXP_ROWS.some(row => report.rootExp[row.key] !== 0)) return true
  if (report.abilityExp.length > 0 || report.levelUps.length > 0) return true
  if (report.items.length > 0 || report.itemsLost.length > 0) return true
  if (report.cycles > 0 || report.encounters > 0 || report.deaths > 0) return true
  if (report.normalKills > 0 || report.eliteKills > 0 || report.bossKills > 0) return true
  return report.duelWins > 0 || report.duelLosses > 0
}

/**
 * 「获取经验」小节：修为 / 底蕴 / 六维 / 功法技能经验 + 期间等级提升（境界/六维/功法）。
 * 所有数值行仅在 >0 时展示；功法名按目录解析，未命中回退 abilityId。
 */
const ReportExpSection = memo(function ReportExpSection({ report }: { report: BattleReport }): React.JSX.Element | null {
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
  const levelUpEntries = report.levelUps.map((entry, index) => {
    let name: string
    if (entry.kind === 'realm') {
      name = t('combat.report.realmLabel')
    } else if (entry.kind === 'root') {
      const row = ROOT_EXP_ROWS.find(item => item.key === entry.targetId)
      name = row ? t(row.labelKey) : entry.targetId
    } else {
      name = findAbilityInCatalog(abilities, entry.targetId)?.name || entry.targetId
    }
    return { key: `${entry.kind}:${entry.targetId}:${index}`, name, from: entry.fromLevel, to: entry.toLevel }
  })

  const hasAny = report.qi > 0 || report.experience > 0
    || rootExpEntries.length > 0 || abilityExpEntries.length > 0 || levelUpEntries.length > 0
  if (!hasAny) return null

  return (
    <div className="battle-report-section">
      <span className="battle-report-section-title">{t('combat.report.expTitle')}</span>
      <div className="battle-report-metrics">
        {report.qi > 0 && (
          <span>{t('combat.report.qi')} <strong><UnitNumber disableTooltip prefix="+" value={report.qi} /></strong></span>
        )}
        {report.experience > 0 && (
          <span>{t('combat.report.experience')} <strong><UnitNumber disableTooltip prefix="+" value={report.experience} /></strong></span>
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
 * 某一侧为空时不渲染该列，两侧皆空时整节不渲染。
 */
const ReportItemsSection = memo(function ReportItemsSection({ report }: { report: BattleReport }): React.JSX.Element | null {
  const t = useT()
  if (report.items.length === 0 && report.itemsLost.length === 0) return null
  return (
    <div className="battle-report-section">
      <span className="battle-report-section-title">{t('combat.report.itemsTitle')}</span>
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
const ReportInfoSection = memo(function ReportInfoSection({ report, nowSeconds }: {
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
      <span className="battle-report-section-title">{t('combat.report.infoTitle')}</span>
      <div className="battle-report-metrics">{lines}</div>
    </div>
  )
})

/**
 * 单本账本的完整渲染块：可选头行（标题 + 徽标）+ 三小节。
 * 当前行动 / 离线挂机 / 今日统计三个分区共用同一结构。
 */
const ReportBlock = memo(function ReportBlock({ report, title, badge, nowSeconds, className = '' }: {
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
      <ReportExpSection report={report} />
      <ReportItemsSection report={report} />
      <ReportInfoSection nowSeconds={nowSeconds} report={report} />
    </div>
  )
})

/**
 * 行动统计面板 (StatsPanel)
 *
 * 展示内容：
 * 1. 当前行动：队首行动账本（duel.currentReport），头行为行动名 + 「进行中」徽标；
 * 2. 离线挂机：duel.offlineReport 单例账本（reportId 固定 "offline"，全零账本不渲染）；
 * 3. 今日统计：duel.dailyReport 单例账本（reportId 固定 "daily"，自然日重置，全零账本不渲染）；
 *    以上三个分区共用「获取经验 / 物品得失 / 行动信息」三小节结构（ReportBlock）。
 * 4. 斗法暂存区（队首行动勾选斗法且有暂存物品时展示，含逐项到期结算倒计时）；
 * 5. 近期战报历史（服务端 reports 合并 localStorage 本地缓存，按 reportId 去重、倒序取 10 条）；
 * 6. 全部分区皆空时显示「暂无统计」空态。
 */
export const StatsPanel = memo(function StatsPanel(): React.JSX.Element {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const duel = useGameStore(state => state.duel)
  const headEntry = useGameStore(state => state.queue.entries[0])
  const serverTime = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)

  // 暂存区仅在「队首为勾选斗法的战斗行动」且存在暂存物品时展示。
  const pvpArmed = Boolean(headEntry && headEntry.executionKind === 'combat-encounter' && headEntry.pvpEnabled)
  const stagedItems = duel.stagedItems
  const showStaged = pvpArmed && stagedItems.length > 0

  const current = duel.currentReport
  // 离线/每日账本仅在存在内容时渲染分区（全零账本抑制）。
  const offline = duel.offlineReport && reportHasContent(duel.offlineReport) ? duel.offlineReport : null
  const daily = duel.dailyReport && reportHasContent(duel.dailyReport) ? duel.dailyReport : null

  // 进行中时长与暂存倒计时需要每秒刷新：仅存在动态内容时启用 tick，
  // 面板关闭时组件随 Modal 一起卸载，定时器自动清理。
  const needsTick = showStaged || Boolean(current) || Boolean(daily)
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!needsTick) return undefined
    const timer = window.setInterval(() => setTick(value => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [needsTick])

  // 以「快照服务端时刻 + 距上次同步的本地流逝」估算当前服务端时间：倒计时以服务端时钟为准，避免本地时钟偏差。
  const estimatedServerNow = serverTime + Math.max(0, performance.now() - lastSyncPerformanceTime) / 1000

  // 历史 = 服务端 reports ∪ 本地缓存，按 reportId 去重（新数据覆盖）、开始时间倒序、
  // 剔除当前进行中条目与 offline/daily 单例账本（两者由专属分区展示）。
  const history = useMemo(() => {
    const merged = new Map<string, BattleReport>()
    for (const report of loadBattleReportHistory(playerId)) merged.set(report.reportId, report)
    for (const report of duel.reports) merged.set(report.reportId, report)
    return [...merged.values()]
      .filter(report => report.reportId !== current?.reportId && !SINGLETON_REPORT_IDS.has(report.reportId))
      .sort((left, right) => right.startedAtUnixSeconds - left.startedAtUnixSeconds)
      .slice(0, HISTORY_LIMIT)
  }, [playerId, duel.reports, current?.reportId])

  const isEmpty = !current && !offline && !daily && !showStaged && history.length === 0

  return (
    <section aria-label={t('combat.report.title')} className="battle-report-panel">
      <header className="battle-report-header">
        <span className="battle-report-title" id="battle-report-panel-title">{t('combat.report.title')}</span>
      </header>
      {isEmpty ? (
        <p className="battle-report-empty">{t('combat.report.empty')}</p>
      ) : (
        <div className="battle-report-body">
          {current && (
            <ReportBlock
              badge={t('combat.report.ongoing')}
              className="battle-report-block--current"
              nowSeconds={estimatedServerNow}
              report={current}
              title={current.actionName || current.actionId || t('combat.report.currentSection')}
            />
          )}
          {offline && (
            <ReportBlock
              nowSeconds={estimatedServerNow}
              report={offline}
              title={t('combat.report.offlineSection')}
            />
          )}
          {daily && (
            <ReportBlock
              nowSeconds={estimatedServerNow}
              report={daily}
              title={t('combat.report.dailySection')}
            />
          )}
          {showStaged && (
            <div className="battle-report-staged">
              <span className="battle-report-staged-title">{t('combat.report.stagedTitle')}</span>
              <div className="battle-report-staged-list">
                {stagedItems.map(item => {
                  const remaining = Math.max(0, item.acquiredAtUnixSeconds + STAGED_WINDOW_SECONDS - estimatedServerNow)
                  return (
                    <span className="battle-report-staged-item" key={`${item.itemId}:${item.acquiredAtUnixSeconds}`}>
                      <ItemReference amount={item.quantity} itemId={item.itemId} />
                      <em>{t('combat.report.stagedRemaining', { time: formatRemainingSeconds(remaining) })}</em>
                    </span>
                  )
                })}
              </div>
            </div>
          )}
          {history.length > 0 && (
            <div className="battle-report-history-block">
              <span className="battle-report-history-title">{t('combat.report.historyTitle')}</span>
              <ul className="battle-report-history">
                {history.map(report => (
                  <li className="battle-report-row" key={report.reportId}>
                    <strong>{report.actionName || report.actionId}</strong>
                    <span className="battle-report-row-time">{formatReportTime(report.startedAtUnixSeconds)}</span>
                    <div className="battle-report-row-sections">
                      <ReportExpSection report={report} />
                      <ReportItemsSection report={report} />
                      <ReportInfoSection nowSeconds={estimatedServerNow} report={report} />
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </section>
  )
})
