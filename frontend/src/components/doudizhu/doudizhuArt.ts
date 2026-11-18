/** 原型认可的矢量牌面；花色编号在边界转换，内部图案不包含玩家数据。 */
export const CARD_ASPECT = 108 / 76
const LABELS: Record<number, string> = { 11: 'J', 12: 'Q', 13: 'K', 14: 'A', 15: '2' }
export const rank = (card: number): number => card < 52 ? Math.floor(card / 4) + 3 : card - 36
export interface JokerLabels { small: string; big: string }
export const rankLabel = (card: number, jokers: JokerLabels): string =>
  rank(card) === 16 ? jokers.small : rank(card) === 17 ? jokers.big : LABELS[rank(card)] || String(rank(card))
export const isRed = (card: number): boolean => card === 53 || card < 52 && [1, 2].includes(card % 4)
export const cardName = (card: number, jokers: JokerLabels): string => card >= 52 ? rankLabel(card, jokers) : `${rankLabel(card, jokers)}${['♣', '♦', '♥', '♠'][card % 4]}`

export const PIPS: Record<number, number[][]> = {
  15: [[2,1],[2,5]],
  3: [[2,1],[2,3],[2,5]], 4: [[1,1],[3,1],[1,5],[3,5]],
  5: [[1,1],[3,1],[2,3],[1,5],[3,5]], 6: [[1,1],[3,1],[1,3],[3,3],[1,5],[3,5]],
  7: [[1,1],[3,1],[2,2],[1,3],[3,3],[1,5],[3,5]],
  8: [[1,1],[3,1],[2,2],[1,3],[3,3],[2,4],[1,5],[3,5]],
  9: [[1,1],[3,1],[1,2],[3,2],[2,3],[1,4],[3,4],[1,5],[3,5]],
  10: [[1,1],[3,1],[1,2],[3,2],[2,2],[2,4],[1,4],[3,4],[1,5],[3,5]],
}
