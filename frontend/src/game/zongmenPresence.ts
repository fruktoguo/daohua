import type { ZongmenMemberDto, ZongmenMemberPresenceStatus } from '../network/protocol'

/** 宗门成员展示态投影：由服务端三态与离线时长归一化得到，前端不硬编码挂机超时阈值。 */
export interface ZongmenMemberPresenceView {
  status: ZongmenMemberPresenceStatus
  /** 仅彻底离线（offline）且具备有效离线秒数时有值，如 "2d0h"、"2d3h"、"5h"。 */
  compactOfflineDuration: string | null
}

/**
 * 将已离线秒数格式化为紧凑的 xdxh（不足 1 天时为 xh）。
 * 向下取整到整小时；整天保留 0h（如 48h -> "2d0h"，50h -> "2d2h"，72h -> "3d0h"，5h -> "5h"）。
 */
export function formatOfflineDurationCompact(seconds: number): string {
  const safeSeconds = Number.isFinite(seconds) ? Math.max(0, seconds) : 0
  const totalHours = Math.floor(safeSeconds / 3600)
  if (totalHours < 24) {
    return `${totalHours}h`
  }
  const days = Math.floor(totalHours / 24)
  const hours = totalHours % 24
  return `${days}d${hours}h`
}

/**
 * 归一化宗门成员三态存在感与离线时长文案输入。
 * 在线优先；挂机中（idle）不展示离线时长；彻底离线（offline）按服务端下发秒数输出紧凑格式。
 */
export function resolveZongmenMemberPresence(
  member: Pick<ZongmenMemberDto, 'online' | 'presenceStatus' | 'offlineDurationSeconds'>,
): ZongmenMemberPresenceView {
  if (member.online || member.presenceStatus === 'online') {
    return { status: 'online', compactOfflineDuration: null }
  }
  if (member.presenceStatus === 'idle') {
    return { status: 'idle', compactOfflineDuration: null }
  }
  const seconds = member.offlineDurationSeconds
  const compactOfflineDuration =
    typeof seconds === 'number' && Number.isFinite(seconds) && seconds >= 0
      ? formatOfflineDurationCompact(seconds)
      : null
  return { status: 'offline', compactOfflineDuration }
}
