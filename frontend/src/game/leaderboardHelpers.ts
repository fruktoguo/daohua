/**
 * 全服排行榜前端纯辅助函数（无 React / DOM 依赖，供视图组件与单测共用）。
 */

export type LeaderboardCategory =
  | 'realm'
  | 'technique'
  | 'valuation'
  | 'spirit-stone'
  | 'zongmen'
  | 'duel'

export const LEADERBOARD_CATEGORIES: readonly LeaderboardCategory[] = [
  'realm',
  'technique',
  'valuation',
  'spirit-stone',
  'zongmen',
  'duel',
] as const

/** 全部 12 张全服排行榜的规范 boardId 清单（与服务端 LeaderboardAssembler.CanonicalBoards 对齐）。 */
export const ALL_LEADERBOARD_BOARD_IDS: readonly string[] = [
  'realm',
  'technique.alchemy',
  'technique.forging',
  'technique.talisman',
  'technique.enhancement',
  'technique.formation',
  'technique.gathering',
  'technique.mining',
  'valuation',
  'spirit-stone',
  'zongmen',
  'duel',
] as const

/** 名次荣誉档位（用于金/银/铜/常规上榜视觉分层）。 */
export type LeaderboardRankTier = 'top1' | 'top2' | 'top3' | 'ranked'

/** 根据 1-based 榜单名次解析荣誉视觉档位。 */
export function resolveRankTier(rank: number): LeaderboardRankTier {
  if (rank === 1) return 'top1'
  if (rank === 2) return 'top2'
  if (rank === 3) return 'top3'
  return 'ranked'
}

/**
 * 将榜单元数据格式化为本地化全称（如「境界榜」「炼丹榜」「斗法榜」）。
 */
export function formatLeaderboardBoardName(
  board: { boardId: string; kind?: string; techniqueId?: string | null },
  t: (key: string, params?: Record<string, string | number>) => string,
): string {
  const split = splitBoardId(board.boardId)
  const kind = board.kind || split.category
  const techniqueId = board.techniqueId ?? split.techniqueId
  switch (kind) {
    case 'realm':
      return t('app.leaderboard.tabRealm')
    case 'valuation':
      return t('app.leaderboard.tabValuation')
    case 'spirit-stone':
      return t('app.leaderboard.tabSpiritStone')
    case 'zongmen':
      return t('app.leaderboard.tabZongmen')
    case 'duel':
      return t('app.leaderboard.tabDuel')
    case 'technique':
      return techniqueId
        ? t('app.leaderboard.techniqueBoardName', { name: t(`insight.technique.${techniqueId}`) })
        : t('app.leaderboard.tabTechnique')
    default:
      return board.boardId
  }
}

/**
 * 将主类与子技艺 ID 组合为后端契约中的 `boardId`。
 */
export function resolveBoardId(category: LeaderboardCategory, techniqueId: string): string {
  return category === 'technique' ? `technique.${techniqueId}` : category
}

/**
 * 将后端 `boardId` 拆回主类与可选技艺 ID。
 */
export function splitBoardId(boardId: string): { category: LeaderboardCategory; techniqueId?: string } {
  if (boardId.startsWith('technique.')) {
    return {
      category: 'technique',
      techniqueId: boardId.slice('technique.'.length),
    }
  }
  switch (boardId) {
    case 'realm':
    case 'valuation':
    case 'spirit-stone':
    case 'zongmen':
    case 'duel':
      return { category: boardId }
    default:
      return { category: 'realm' }
  }
}

/**
 * 将 ISO 时间戳格式化为本地 `MM-DD HH:mm`（非合法日期返回 `—`）。
 */
export function formatLeaderboardTimestamp(iso?: string | null): string {
  if (!iso) return '—'
  const parsed = new Date(iso)
  if (Number.isNaN(parsed.getTime())) return '—'
  const month = String(parsed.getMonth() + 1).padStart(2, '0')
  const day = String(parsed.getDate()).padStart(2, '0')
  const hour = String(parsed.getHours()).padStart(2, '0')
  const minute = String(parsed.getMinutes()).padStart(2, '0')
  return `${month}-${day} ${hour}:${minute}`
}

/**
 * 计算距目标整点时间剩余的秒数（最低为 0；无效 ISO 返回 null）。
 */
export function getRemainingSecondsUntil(targetIso?: string | null, nowMs = Date.now()): number | null {
  if (!targetIso) return null
  const targetMs = Date.parse(targetIso)
  if (!Number.isFinite(targetMs)) return null
  return Math.max(0, Math.floor((targetMs - nowMs) / 1000))
}
