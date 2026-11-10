import { CARD_ASPECT } from './doudizhuArt'

export interface HandLayout {
  width: number
  height: number
  step: number
  total: number
  rowStep: number
  extent: number
  portrait: boolean
}

/** 牌的大小只取决于可用空间，出牌减少数量时不重新缩放或改变间距。 */
export function measureHand(available: number, tableHeight: number, portrait: boolean): HandLayout {
  const width = portrait
    ? Math.min(76, available / 5.2, tableHeight * .12 / CARD_ASPECT)
    : Math.min(82, available * .13)
  const height = width * CARD_ASPECT
  const step = Math.min(width * (portrait ? .88 : .48), (available - width) / (portrait ? 6 : 19))
  const rowStep = portrait ? height + 12 : 0
  return { width, height, step, total: available, rowStep, extent: height + 48 + rowStep * (portrait ? 2 : 0), portrait }
}

/** 展示、飞牌落点与理牌共用相同的行列坐标，三排分别居中。 */
export function handPosition(index: number, count: number, layout: HandLayout): { left: number; top: number } {
  const columns = layout.portrait ? Math.max(1, Math.ceil(count / 3)) : Math.max(1, count)
  const row = Math.floor(index / columns), column = index % columns
  const rowCount = Math.min(columns, count - row * columns)
  const rowWidth = layout.width + Math.max(0, rowCount - 1) * layout.step
  return { left: (layout.total - rowWidth) / 2 + column * layout.step, top: 28 + row * layout.rowStep }
}
