import type { BattleReport, DuelState } from './types'

/**
 * 战斗收益战报本地缓存（localStorage）。
 *
 * 服务端权威仅保留最近 10 条战报；前端每次收到含 duel 分段的快照/增量时，
 * 将 reports 与 currentReport 合并写入本地，按 reportId 去重（新数据覆盖）、
 * 按 startedAtUnixSeconds 倒序截断 50 条，延长战报历史的可回溯窗口。
 *
 * 写放大控制：战斗中 duel 分段几乎每次击杀都随增量下发，此处以
 * 「reportId:endedAt 集合签名」短路无变化读写、内存镜像替代每次 JSON.parse 全量读、
 * 防抖批量写 + 页面隐藏 flush 替代每次 setItem 全量写。localStorage 仅为本地缓存
 * （非权威），进行中账本的计数变化由实时 duel 状态呈现，无需逐次落盘。
 * localStorage 不可用（隐私模式等）时静默跳过，容错风格同 avatarStore。
 */

const STORAGE_KEY_PREFIX = 'daohua.battleReports.'
const MAX_STORED_REPORTS = 50
/** 落盘防抖窗口：高频 merge 在此窗口内合并为单次写盘 */
const WRITE_DEBOUNCE_MS = 5_000

/**
 * 每玩家战报缓存槽：
 * - merged 为 localStorage 内容的内存镜像（本模块是该键唯一写者），首次合并时一次性种子化；
 * - signature 为最近一次已处理下发集合的签名（reportId:endedAt 有序拼接）；
 * - dirty 标记存在待落盘数据，timer 为防抖计时器。
 */
interface ReportHistoryCache {
  merged: Map<string, BattleReport>
  signature: string
  timer: number | null
  dirty: boolean
}

const caches = new Map<string, ReportHistoryCache>()

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
 * 旧版本缓存条目缺少账本泛化后新增的字段（abilityExp/techniqueExp/levelUps/itemsLost/计数器等），
 * 读取时统一补齐默认值，保证渲染路径不依赖字段存在性。
 */
function normalizeStoredReport(report: BattleReport): BattleReport {
  const rootExp = (report.rootExp ?? {}) as Partial<BattleReport['rootExp']>
  return {
    ...report,
    actionName: typeof report.actionName === 'string' ? report.actionName : '',
    endedAtUnixSeconds: storedNumber(report.endedAtUnixSeconds),

    experience: storedNumber(report.experience),
    foundation: storedNumber(report.foundation),
    combatExperience: storedNumber(report.combatExperience),
    rootExp: {
      stamina: storedNumber(rootExp.stamina),
      meridians: storedNumber(rootExp.meridians),
      attack: storedNumber(rootExp.attack),
      bone: storedNumber(rootExp.bone),
      agility: storedNumber(rootExp.agility),
      consciousness: storedNumber(rootExp.consciousness),
    },
    abilityExp: storedArray(report.abilityExp),
    techniqueExp: storedArray(report.techniqueExp),
    levelUps: storedArray(report.levelUps),
    itemsLost: storedArray(report.itemsLost),
    duelItemsGained: storedArray(report.duelItemsGained),
    duelItemsLost: storedArray(report.duelItemsLost),
    cycles: storedNumber(report.cycles),
    encounters: storedNumber(report.encounters),
    normalKills: storedNumber(report.normalKills),
    eliteKills: storedNumber(report.eliteKills),
    bossKills: storedNumber(report.bossKills),
    deaths: storedNumber(report.deaths),
    dayIndex: storedNumber(report.dayIndex),
    duelWins: storedNumber(report.duelWins),
    duelLosses: storedNumber(report.duelLosses),
    // 战斗单位统计仅进行中账本携带；归档/旧缓存统一归空列表。
    combatants: storedArray(report.combatants),
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

/** 将内存镜像排序截断后写入 localStorage；失败保留 dirty 标记等待下次调度重试。 */
function flushBattleReportHistory(playerId: string, cache: ReportHistoryCache): void {
  if (!cache.dirty) return
  try {
    const sorted = [...cache.merged.values()]
      .sort((left, right) => right.startedAtUnixSeconds - left.startedAtUnixSeconds)
      .slice(0, MAX_STORED_REPORTS)
    window.localStorage.setItem(STORAGE_KEY_PREFIX + playerId, JSON.stringify(sorted))
    // 内存镜像与已落盘内容对齐：掉出 50 条窗口的最旧条目不再参与后续合并。
    cache.merged = new Map(sorted.map(report => [report.reportId, report]))
    cache.dirty = false
  } catch {
    // 持久化失败仅影响历史回溯窗口，不阻断状态应用；dirty 保持置位，下次 flush 重试。
  }
}

/** 防抖调度：同一玩家已挂起计时器时不重复排程，窗口到期统一落盘。 */
function scheduleFlush(playerId: string, cache: ReportHistoryCache): void {
  if (cache.timer !== null) return
  try {
    cache.timer = window.setTimeout(() => {
      cache.timer = null
      flushBattleReportHistory(playerId, cache)
    }, WRITE_DEBOUNCE_MS)
  } catch {
    // 无 window 环境（SSR/测试注入）：退化为同步直写，落盘语义不变。
    flushBattleReportHistory(playerId, cache)
  }
}

/** 所有玩家的待写数据立即落盘（页面切入隐藏时调用）。 */
function flushAllBattleReportHistories(): void {
  for (const [playerId, cache] of caches) {
    if (cache.timer !== null) {
      window.clearTimeout(cache.timer)
      cache.timer = null
    }
    flushBattleReportHistory(playerId, cache)
  }
}

// 页面切入后台/关闭前立即落盘，避免防抖窗口内的合并结果滞留内存丢失。
// 仅浏览器环境注册监听：契约门禁经 ssrLoadModule 注入的 document mock 无 addEventListener。
if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') flushAllBattleReportHistories()
  })
}

/**
 * 将最新 duel 分段中的战报合并写入本地缓存：
 * reports + currentReport 与既有记录按 reportId 去重（新数据覆盖旧记录），
 * 按开始时间倒序截断保留 50 条。
 * 下发集合签名（reportId:endedAt）未变时读写整体跳过；变化时只更新内存镜像并防抖落盘。
 */
export function mergeBattleReportHistory(playerId: string, duel: DuelState | null | undefined): void {
  if (!playerId || !duel) return
  const incoming = duel.currentReport ? [...duel.reports, duel.currentReport] : duel.reports
  if (incoming.length === 0) return
  const signature = incoming
    .map(report => `${report.reportId}:${report.endedAtUnixSeconds}`)
    .sort()
    .join('|')
  let cache = caches.get(playerId)
  if (cache?.signature === signature) return
  if (!cache) {
    cache = { merged: new Map(), signature: '', timer: null, dirty: false }
    for (const report of loadBattleReportHistory(playerId)) cache.merged.set(report.reportId, report)
    caches.set(playerId, cache)
  }
  cache.signature = signature
  for (const report of incoming) cache.merged.set(report.reportId, report)
  cache.dirty = true
  scheduleFlush(playerId, cache)
}
