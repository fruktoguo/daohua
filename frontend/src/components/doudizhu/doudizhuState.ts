import type { DouDiZhuGameDto } from '../../network/protocol.ts'

/**
 * 归一化服务端下发的斗地主牌局投影：
 * 协议序列化启用了 WhenWritingNull，可空字段为 null 时 JSON 键会被省略（反序列化为 undefined），
 * 在入库前统一补齐为 null，避免下游严格相等 `=== null` 判定失真。
 */
export function normalizeDouDiZhuGame(game: DouDiZhuGameDto | null | undefined): DouDiZhuGameDto | null {
  if (!game) return null
  return {
    ...game,
    landlordSeat: game.landlordSeat ?? null,
    lastPlayedSeat: game.lastPlayedSeat ?? null,
    winnerSeat: game.winnerSeat ?? null,
    landlordWon: game.landlordWon ?? null,
    landlordConfirmedAtUnixMilliseconds: game.landlordConfirmedAtUnixMilliseconds ?? null,
    dealHand: Array.isArray(game.dealHand) ? game.dealHand : [],
    bids: Array.isArray(game.bids) ? game.bids : [],
  }
}

/**
 * 判定叫地主阶段当前主操作：
 * 尚无人叫地主（null 或缺省 undefined）时为 `call`（叫地主），已有人叫地主（含 0 号位）时为 `rob`（抢地主）。
 */
export function resolveDouDiZhuBidChoice(game: Pick<DouDiZhuGameDto, 'landlordSeat'>): 'call' | 'rob' {
  return game.landlordSeat == null ? 'call' : 'rob'
}
