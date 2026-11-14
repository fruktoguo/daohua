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
