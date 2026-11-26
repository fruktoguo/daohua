import React, { memo } from 'react'
import { formatLeaderboardBoardName, resolveRankTier } from '../game/leaderboardHelpers'
import { useT } from '../i18n'
import type { GuideRankBadgeDto } from '../network/protocol'
import { FloatingTooltip } from './FloatingPopover'

/**
 * 全服 12 排行榜专属纯白二值镂空图腾静态映射表（由 Vite import.meta.glob 自动收集）。
 * 资产位置：`frontend/src/assets/glyphs/lb.<boardId>.webp`（64×64 无损纯白二值 WebP）。
 */
const LEADERBOARD_GLYPH_MODULES = import.meta.glob('../assets/glyphs/lb.*.webp', {
  eager: true,
  import: 'default',
}) as Record<string, string>

/**
 * 根据榜单 `boardId` 解析对应的排行榜图腾 URL。
 * @param boardId 榜单标识（如 `realm`、`duel`、`technique.alchemy`、`zongmen`）
 */
export function resolveLeaderboardGlyphUrl(boardId?: string | null): string | null {
  if (!boardId) return null
  const normalized = boardId.trim().replace(/^lb\./, '')
  if (!normalized) return null
  return LEADERBOARD_GLYPH_MODULES[`../assets/glyphs/lb.${normalized}.webp`] ?? null
}

/**
 * 全服排行榜纯白二值镂空图腾组件 (`LeaderboardGlyph`)。
 *
 * 与 `StatGlyph` 同源采用 CSS `mask-image` + `currentColor` 动态单色染色，
 * 自动适配金/银/铜名次色、页签激活态及深浅双主题。
 */
export const LeaderboardGlyph = memo(function LeaderboardGlyph({
  boardId,
  className = '',
  size = 15,
}: {
  boardId: string
  className?: string
  size?: number
}): React.JSX.Element | null {
  const url = resolveLeaderboardGlyphUrl(boardId)
  if (!url) return null

  return (
    <span
      aria-hidden="true"
      className={`leaderboard-glyph ${className}`.trim()}
      style={{
        display: 'inline-block',
        width: size,
        height: size,
        maskImage: `url(${url})`,
        WebkitMaskImage: `url(${url})`,
        maskSize: 'contain',
        WebkitMaskSize: 'contain',
        maskRepeat: 'no-repeat',
        WebkitMaskRepeat: 'no-repeat',
        maskPosition: 'center',
        WebkitMaskPosition: 'center',
        backgroundColor: 'currentColor',
        flexShrink: 0,
      }}
    />
  )
})

/**
 * 排行榜最高名次荣誉徽标组件 (`LeaderboardRankBadge`)。
 *
 * - `variant="chat"`：传音行头紧凑胶囊 `[图腾 #N]`，悬停展示完整榜名与名次（如「境界榜 第 1 名」）；
 * - `variant="guide"`：道友攻略作者头衔胶囊 `[图腾 榜名 #N]`。
 */
export const LeaderboardRankBadge = memo(function LeaderboardRankBadge({
  badge,
  variant = 'chat',
}: {
  badge: GuideRankBadgeDto
  variant?: 'chat' | 'guide'
}): React.JSX.Element {
  const t = useT()
  const tier = resolveRankTier(badge.rank)
  const boardName = formatLeaderboardBoardName(badge, t)

  if (variant === 'guide') {
    return (
      <span className={`guide-rank-pill is-${tier}`}>
        <LeaderboardGlyph boardId={badge.boardId} size={13} />
        <span>{boardName} #{badge.rank}</span>
      </span>
    )
  }

  const tooltip = t('app.leaderboard.rankTooltip', { board: boardName, rank: badge.rank })
  return (
    <FloatingTooltip placement="top" title={tooltip} tone="gold">
      <span
        aria-label={tooltip}
        className={`chat-rank-capsule is-${tier}`}
      >
        <LeaderboardGlyph boardId={badge.boardId} size={13} />
        <span className="chat-rank-num">#{badge.rank}</span>
      </span>
    </FloatingTooltip>
  )
})
