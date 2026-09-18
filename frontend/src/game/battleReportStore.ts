import type { BattleReport, DuelState } from './types'

/**
 * 战斗收益战报本地缓存（localStorage）。
 *
 * 服务端权威仅保留最近 10 条战报；前端每次收到含 duel 分段的快照/增量时，
 * 将 reports 与 currentReport 合并写入本地，按 reportId 去重（新数据覆盖）、
 * 按 startedAtUnixSeconds 倒序截断 50 条，延长战报历史的可回溯窗口。
 * localStorage 不可用（隐私模式等）时静默跳过，容错风格同 avatarStore。
 */

const STORAGE_KEY_PREFIX = 'daohua.battleReports.'
const MAX_STORED_REPORTS = 50

/** 本地缓存条目的最小结构校验（自写自读，仅过滤损坏数据） */
function isStoredReport(value: unknown): value is BattleReport {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return false
  const row = value as Record<string, unknown>
  return typeof row.reportId === 'string' && row.reportId.length > 0
    && typeof row.actionId === 'string'
    && typeof row.startedAtUnixSeconds === 'number' && Number.isFinite(row.startedAtUnixSeconds)
}

/** 非负有限数值兜底（旧缓存字段缺失时归 0） */
function storedNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

/** 非数组兜底为空列表（旧缓存字段缺失时归 []） */
function storedArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? value as T[] : []
}

/**
 * 旧版本缓存条目缺少账本泛化后新增的字段（abilityExp/levelUps/itemsLost/计数器等），
 * 读取时统一补齐默认值，保证渲染路径不依赖字段存在性。
 */
function normalizeStoredReport(report: BattleReport): BattleReport {
  const rootExp = (report.rootExp ?? {}) as Partial<BattleReport['rootExp']>
  return {
    ...report,
    actionName: typeof report.actionName === 'string' ? report.actionName : '',
    endedAtUnixSeconds: storedNumber(report.endedAtUnixSeconds),
    qi: storedNumber(report.qi),
    experience: storedNumber(report.experience),
    rootExp: {
      stamina: storedNumber(rootExp.stamina),
      intelligence: storedNumber(rootExp.intelligence),
      attack: storedNumber(rootExp.attack),
      defense: storedNumber(rootExp.defense),
      swordsmanship: storedNumber(rootExp.swordsmanship),
      talisman: storedNumber(rootExp.talisman),
    },
    abilityExp: storedArray(report.abilityExp),
    levelUps: storedArray(report.levelUps),
    items: storedArray(report.items),
    itemsLost: storedArray(report.itemsLost),
    cycles: storedNumber(report.cycles),
    encounters: storedNumber(report.encounters),
    normalKills: storedNumber(report.normalKills),
    eliteKills: storedNumber(report.eliteKills),
    bossKills: storedNumber(report.bossKills),
    deaths: storedNumber(report.deaths),
    dayIndex: storedNumber(report.dayIndex),
    duelWins: storedNumber(report.duelWins),
    duelLosses: storedNumber(report.duelLosses),
  }
}

/**
 * 读取指定玩家的本地战报历史缓存；存储不可用或数据损坏时返回空列表。
 */
export function loadBattleReportHistory(playerId: string): BattleReport[] {
  if (!playerId) return []
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY_PREFIX + playerId)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter(isStoredReport).map(normalizeStoredReport)
  } catch {
    // 隐私模式等 localStorage 不可用场景：静默回退空历史。
    return []
  }
}

/**
 * 将最新 duel 分段中的战报合并写入本地缓存：
 * reports + currentReport 与既有记录按 reportId 去重（新数据覆盖旧记录），
 * 按开始时间倒序截断保留 50 条。
 */
export function mergeBattleReportHistory(playerId: string, duel: DuelState | null | undefined): void {
  if (!playerId || !duel) return
  try {
    const merged = new Map<string, BattleReport>()
    for (const report of loadBattleReportHistory(playerId)) merged.set(report.reportId, report)
    for (const report of duel.reports) merged.set(report.reportId, report)
    if (duel.currentReport) merged.set(duel.currentReport.reportId, duel.currentReport)
    const sorted = [...merged.values()]
      .sort((left, right) => right.startedAtUnixSeconds - left.startedAtUnixSeconds)
      .slice(0, MAX_STORED_REPORTS)
    window.localStorage.setItem(STORAGE_KEY_PREFIX + playerId, JSON.stringify(sorted))
  } catch {
    // 持久化失败仅影响历史回溯窗口，不阻断状态应用。
  }
}
