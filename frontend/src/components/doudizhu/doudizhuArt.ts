/** 原型认可的矢量牌面；花色编号在边界转换，内部图案不包含玩家数据。 */
export const CARD_ASPECT = 108 / 76
const LABELS: Record<number, string> = { 11: 'J', 12: 'Q', 13: 'K', 14: 'A', 15: '2' }
const rank = (card: number): number => card < 52 ? Math.floor(card / 4) + 3 : card - 36
export interface JokerLabels { small: string; big: string }
const rankLabel = (card: number, jokers: JokerLabels): string =>
  rank(card) === 16 ? jokers.small : rank(card) === 17 ? jokers.big : LABELS[rank(card)] || String(rank(card))
export const isRed = (card: number): boolean => card === 53 || card < 52 && [1, 2].includes(card % 4)
export const cardName = (card: number, jokers: JokerLabels): string => card >= 52 ? rankLabel(card, jokers) : `${rankLabel(card, jokers)}${['♣', '♦', '♥', '♠'][card % 4]}`
const suitSvg = (suit: number, x: number, y: number, width: number, height: number): string => `<use href="#ddz-suit-${suit}" x="${x}" y="${y}" width="${width}" height="${height}" fill="currentColor"/>`;
  const PIPS: Record<number, number[][]> = {
    15: [[2,1],[2,5]],
    3: [[2,1],[2,3],[2,5]], 4: [[1,1],[3,1],[1,5],[3,5]],
    5: [[1,1],[3,1],[2,3],[1,5],[3,5]], 6: [[1,1],[3,1],[1,3],[3,3],[1,5],[3,5]],
    7: [[1,1],[3,1],[2,2],[1,3],[3,3],[1,5],[3,5]],
    8: [[1,1],[3,1],[2,2],[1,3],[3,3],[2,4],[1,5],[3,5]],
    9: [[1,1],[3,1],[1,2],[3,2],[2,3],[1,4],[3,4],[1,5],[3,5]],
    10: [[1,1],[3,1],[1,2],[3,2],[2,2],[2,4],[1,4],[3,4],[1,5],[3,5]],
  };
  /** J/Q/K 使用对称双头人物，王牌采用与金纹牌背一致的宫廷几何图案。 */
  function courtArt(r: number, suit: number) {
    const ink = [1, 3].includes(suit) ? '#a83f4d' : '#344b54', dark = '#253f46', gold = '#b99c54', skin = '#e4cba6';
    const crown = r === 13 ? '<path d="m25 15 2-12 9 7 7-9 7 9 10-7 2 12Z" fill="#c1a05a"/><path d="M28 15h32v5H28z" fill="#e7cf90"/>'
      : r === 12 ? '<path d="m28 17 3-10 8 5 5-9 6 9 7-5 3 10Z" fill="#d4b36a"/><circle cx="44" cy="6" r="3" fill="#9b3e4b"/>'
      : `<path d="M25 18c2-18 33-18 38 0Z" fill="${ink}"/><path d="m31 10 5 7M46 5l-3 11M59 10l-8 8" stroke="#d4b36a" stroke-width="3"/>`;
    const figure = `<path d="M8 75 14 51l19-9h25l17 10 7 23Z" fill="${ink}"/><path d="m20 49 11-6 14 19 15-19 11 7-9 25H27Z" fill="${gold}"/><path d="M37 44V33h16v11l-8 12Z" fill="${skin}"/><path d="M27 20h34l-2 16-9 9H39l-10-9Z" fill="${skin}"/>${crown}<path d="m32 25 5-1m12 0 5 1M42 27l-2 7h5M36 36q9 7 17-1" fill="none" stroke="${dark}" stroke-width="1.4" stroke-linecap="round"/>
      ${r === 13 ? `<path d="m30 32 5 11 9 5 12-7 3-10-7 7-8 4-7-4Z" fill="${dark}"/>` : r === 12 ? `<path d="m26 18-5 23 12 6-4-17m31-12 8 24-13 6 5-19" fill="${dark}"/><path d="M65 72V38m0 0c-14-7-4-22 0-13 8-11 16 4 0 13Z" fill="#9d4b5b" stroke="#7b8f58" stroke-width="2"/>` : `<path d="m68 72-3-39m0 0 4-15 5 14Z" fill="#e6dfc6" stroke="${gold}" stroke-width="2"/>`}
      <path d="m28 57 17 10 16-11M25 65l20 10 20-10" fill="none" stroke="#e7cf90" stroke-width="2"/><path d="M15 54v22M75 54v22" stroke="#d1b872" stroke-width="4"/><path d="m40 53 5 5 5-5-5-5Z" fill="${ink}"/><path d="m29 52 4 4-4 4-4-4Zm33 0 4 4-4 4-4-4Z" fill="#e4d3a0"/>`;
    return `<rect x="20" y="7" width="41" height="94" rx="2" fill="#ebe2c3" stroke="#b5a16a" stroke-opacity=".6"/><svg x="21" y="8" width="39" height="92" viewBox="0 0 90 144" fill="none"><g>${figure}</g><g transform="translate(90 144) rotate(180)">${figure}</g></svg><g transform="rotate(-25 40 54)"><path d="M20 47h41v14H20Z" fill="#ece1c0" stroke="#a48c5f" stroke-opacity=".45"/>${suitSvg(suit, 36, 49, 8, 10)}</g>`;
  }
  function jokerArt(big: boolean) {
    const color = big ? '#a83e4e' : '#344f58';
    return `<svg x="17" y="6" width="49" height="94" viewBox="0 0 76 116" fill="none"><path d="M38 8 65 30v55l-27 23L11 85V30Z" fill="none" stroke="#b7a16b" opacity=".4"/><path d="M38 15 59 33v47l-21 23-21-23V33Z" fill="#ded1a22b"/><path d="M25 48C8 29 11 7 28 27 35 3 49 3 51 27 70 7 69 32 52 48" fill="${color}"/><path d="m19 29 8 17 12-24 12 24 8-17" fill="#c6aa66"/><circle cx="17" cy="21" r="4" fill="#c5a35f"/><circle cx="39" cy="13" r="4" fill="#c5a35f"/><circle cx="62" cy="21" r="4" fill="#c5a35f"/><path d="M24 45h29l-2 19-12 12-13-12Z" fill="#ead6b4"/><path d="m29 53 5-1m10 0 5 1M32 62q8 8 15-1" stroke="${color}" stroke-width="2" fill="none" stroke-linecap="round"/><path d="m24 71-10 14 15-1 10 17 10-17 14 1-11-14-13 10Z" fill="${color}"/><path d="m27 80 12 14 12-14-12 3Z" fill="#c7ac65"/><circle cx="39" cy="87" r="3" fill="#e9d6a0"/><path d="m38 36 4 5-4 5-4-5Z" fill="#ead6a3"/><path d="m9 51 4 4-4 4-4-4Zm56 18 4 4-4 4-4-4Z" fill="#c4aa6a" opacity=".7"/></svg>`;
  }
/** 76×108 是所有用途唯一的牌面坐标系，容器只能等比缩放整张图。 */
export function cardFace(card: number, jokers: JokerLabels): string {
  const r = rank(card), suit = [2, 3, 1, 0][card % 4]
  let content: string
  if (card >= 52) {
    const english = [...'JOKER'].map((letter, i) => `<tspan x="6" y="${14 + i * 10}">${letter}</tspan>`).join('')
    const chinese = [...rankLabel(card, jokers)].map((letter, i) => `<tspan x="64" y="${88 + i * 11}">${letter}</tspan>`).join('')
    content = `${jokerArt(r === 17)}<text font-size="9" font-weight="700">${english}</text><text font-family="serif" font-size="9.5">${chinese}</text>`
  } else {
    const corner = `<text x="5" y="17" font-size="17" font-weight="700" letter-spacing="-.8">${rankLabel(card, jokers)}</text>${suitSvg(suit, 5, 20, 10, 12)}`
    let center: string
    if (PIPS[r]) center = PIPS[r].map(([x, y]) => {
      const cx = 25 + (x - 1) * 14, cy = 24 + (y - 1) * 15
      return `<g${y > 3 ? ` transform="rotate(180 ${cx} ${cy})"` : ''}>${suitSvg(suit, cx - 5.5, cy - 6.2, 11, 12.4)}</g>`
    }).join('')
    else if (r === 14) center = `<path d="m40 22 23 31-23 31-23-31Z" fill="none" stroke="currentColor" opacity=".12"/>${suitSvg(suit, 22, 31, 36, 42)}<text x="40" y="94" text-anchor="middle" font-size="4.5" letter-spacing="1.7" opacity=".45">DAOHUA</text>`
    else center = courtArt(r, suit)
    content = `${center}${corner}<g transform="translate(76 108) rotate(180)">${corner}</g>`
  }
  return `<svg class="ddz-card-face" viewBox="0 0 76 108" preserveAspectRatio="xMidYMid meet" font-family="Georgia, Times New Roman, serif" fill="currentColor" aria-hidden="true">${content}</svg>`
}

export const visualSymbols = '<symbol id="ddz-suit-0" viewBox="0 0 32 36"><path d="M16 1C12 8 2 12 2 20c0 7 8 10 13 4-1 5-3 8-5 10h12c-3-3-5-6-5-10 5 6 13 3 13-4C30 12 20 8 16 1Z"/></symbol><symbol id="ddz-suit-1" viewBox="0 0 32 36"><path d="M16 33C12 27 1 19 1 11 1 1 13-2 16 8 20-2 31 1 31 11c0 8-11 16-15 22Z"/></symbol><symbol id="ddz-suit-2" viewBox="0 0 32 36"><path d="M16 2c-8 0-11 10-5 14-8-5-15 6-8 12 4 4 10 1 12-3-1 5-3 8-5 9h12c-3-2-5-5-5-9 4 6 12 7 14 0 3-8-5-14-11-9 7-6 3-14-4-14Z"/></symbol><symbol id="ddz-suit-3" viewBox="0 0 32 36"><path d="M16 1C12 8 7 14 1 18c6 5 11 11 15 17 4-6 9-12 15-17C25 14 20 8 16 1Z"/></symbol><symbol id="ddz-prop-tomato" viewBox="0 0 64 64"><path d="M32 15C17 6 7 21 9 37c2 14 13 21 24 19 14 1 23-9 23-22 0-15-11-25-24-19Z" fill="#cf5141"/><path d="M33 21c-8-9-20-1-19 11 0 10 4 16 12 19-5-10-5-21 7-30Z" fill="#ef7557"/><path d="M33 55c13-1 20-10 20-24-3 12-9 17-20 24Z" fill="#a63132"/><path d="m31 10-6-6 2 11-14 2 12 6-2 7 10-8 10 6-3-9 13-3-16-2 1-10-7 6Z" fill="#638844"/><path d="m32 16 2-10" fill="none" stroke="#99ad59" stroke-width="3" stroke-linecap="round"/><path d="M16 29c0-4 3-7 6-8" fill="none" stroke="#ffb48a" stroke-width="3" stroke-linecap="round" opacity=".7"/></symbol><symbol id="ddz-prop-flower" viewBox="0 0 64 64"><path d="M32 27c3 15 4 23 1 34" fill="none" stroke="#759951" stroke-width="4"/><path d="M33 49C17 45 16 34 16 34c13 0 17 6 17 15ZM35 54c13-2 15-14 15-14-13 1-15 8-15 14Z" fill="#7c9c56"/><path d="M31 6c6-10 16 2 11 8 12-3 16 9 6 14 7 10-7 18-14 10-7 10-21 1-14-9-14-2-11-17 1-16-3-10 9-16 10-7Z" fill="#e8b0a4"/><path d="M26 16c1-9 11-9 14 0 9 0 11 10 3 14 0 10-11 13-16 4-10 3-15-9-7-14 0-4 2-5 6-4Z" fill="#cc6e73"/><path d="M29 19c7-6 16 4 9 11-7 6-15-4-9-11Z" fill="#a43f57"/><path d="M31 23c3-2 7 0 5 4" fill="none" stroke="#f4b7a1" stroke-width="2"/></symbol><symbol id="ddz-prop-tea" viewBox="0 0 64 64"><path d="M16 24h33l-4 29H21l-5-29Z" fill="#d7d7b9"/><path d="M23 25h21l-3 26H25l-2-26Z" fill="#91a58e"/><path d="M48 27h5c10 0 9 17-7 17" fill="none" stroke="#d6d6b7" stroke-width="5"/><ellipse cx="32" cy="24" rx="17" ry="5" fill="#ede5c9"/><ellipse cx="32" cy="24" rx="12" ry="3" fill="#747d45"/><path d="M11 56h44" fill="none" stroke="#d9ca9d" stroke-width="4" stroke-linecap="round"/><path d="M25 17c-7-9 8-7 1-15M37 16c-8-8 7-9 1-15" fill="none" stroke="#dfe1b7" stroke-width="2" stroke-linecap="round" opacity=".7"/><path d="m29 34 8 0-4 9-4-9Z" fill="#e4d09c"/></symbol><symbol id="ddz-prop-applause" viewBox="0 0 64 64"><path d="m12 41-4-18c-1-5 4-6 6-1l3 11-2-20c0-5 5-5 6 0l3 19 1-23c0-5 5-4 5 0l1 24 4-15c2-5 6-2 5 2l-4 21 6-5c5-4 8 1 4 5l-14 14c-8 6-17 0-20-14Z" fill="#d9ba88" stroke="#a17a4d" stroke-width="1"/><path d="m28 43 4-18c1-5 6-4 6 1l-1 11 7-19c2-4 6-2 4 2l-5 19 10-20c3-4 7-1 5 3l-9 21 10-11c3-4 7 0 3 4L48 53c-10 9-20 3-20-10Z" fill="#efd09b" stroke="#bd955c" stroke-width="1"/><path d="m8 9-4-4M45 6l3-5M58 11l5-3" fill="none" stroke="#e6cf8c" stroke-width="3" stroke-linecap="round"/></symbol>'
