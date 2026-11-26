/**
 * 坊市价格梯度与回收底价归一化纯计算模块 (marketPricing)
 */

/**
 * 根据价格区间动态计算坊市最小变动价格步长 (Tick Size)
 *
 * 步长规则：
 * - 低价位以 1 为基准；
 * - 随着价格数量级放大，按 1/2/5 阶梯逐步放宽报价精度，避免盘口档位过度碎片化。
 */
export function getMarketPriceIncrement(price: number): number {
  if (price <= 1) return 1
  let firstDigit = Math.floor(price)
  let magnitude = 1
  while (firstDigit >= 10) {
    firstDigit = Math.floor(firstDigit / 10)
    magnitude *= 10
  }

  if ((firstDigit === 1 || firstDigit === 2) && magnitude >= 100) return 5 * (magnitude / 100)
  if ((firstDigit === 3 || firstDigit === 4) && magnitude >= 10) return magnitude / 10
  if (firstDigit >= 5 && magnitude >= 10) return 2 * (magnitude / 10)
  return 1
}

/**
 * 归一化挂单价格至合法的步长网格，并强制不得低于系统回收底价
 */
export function normalizeMarketPrice(price: number, vendorPrice: number, roundUp: boolean): number {
  const minFloor = Math.max(2, Math.floor(vendorPrice))
  if (price <= minFloor) {
    price = minFloor
    roundUp = true
  }
  const increment = getMarketPriceIncrement(price)
  const remainder = price % increment
  let normalized = price - remainder
  if (normalized < minFloor) roundUp = true
  if (roundUp && remainder > 0) normalized += increment
  return Math.max(minFloor, Math.trunc(normalized))
}

/** 挂单默认单价计算输入参数 */
export interface MarketOrderDefaultPriceParams {
  /** 挂单方向：卖出 sell / 买入 buy */
  side: 'sell' | 'buy'
  /** 盘口卖一价（可选） */
  bestAsk?: number
  /** 盘口买一价（可选） */
  bestBid?: number
  /** 盘口无对应侧档位时的市场参考价兜底 */
  referenceDefault: number
  /** 系统回收保底价 */
  vendorPrice: number
  /** 归一化后的最低合法挂单价 */
  minimumPrice: number
  /** 快捷交易入口锁定的初始单价（可选，优先于盘口默认价） */
  initialPrice?: number
}

/**
 * 计算坊市挂单弹窗的默认单价
 *
 * 优先级：快捷锁定价 (initialPrice) > 同侧盘口最优档 (卖单取卖一 / 买单取买一) > 市场参考价兜底；
 * 最终按方向归一化到合法步长网格并由最低限价钳位。
 */
export function resolveMarketOrderDefaultPrice({
  side,
  bestAsk,
  bestBid,
  referenceDefault,
  vendorPrice,
  minimumPrice,
  initialPrice,
}: MarketOrderDefaultPriceParams): number {
  const bookDefault = side === 'sell'
    ? (bestAsk ?? referenceDefault)
    : (bestBid ?? referenceDefault)
  return Math.max(minimumPrice, normalizeMarketPrice(initialPrice ?? bookDefault, vendorPrice, side === 'buy'))
}

/** 弹窗盘口对账后的单价更新决策参数 */
export interface MarketModalBookPriceParams extends Omit<MarketOrderDefaultPriceParams, 'initialPrice'> {
  /** 弹窗当前展示的单价 */
  currentPrice: number
  /** 最近一次已完成定价（初始打开、切等级赋默认价或用户手动调价）的强化等级 */
  pricedLevel: number
  /** 当前选中的强化等级 */
  targetLevel: number
}

/**
 * 决策盘口对账完成后是否应重写挂单单价
 *
 * 只有当用户切换到尚未定价的新强化等级（pricedLevel !== targetLevel）时才按新等级写入一次默认价；
 * 同一等级的后续行情轮询或参考价刷新一律保持用户当前单价不变，防止编辑中单价回退。
 */
export function resolveMarketModalPriceAfterBook(params: MarketModalBookPriceParams): {
  price: number
  pricedLevel: number
  changed: boolean
} {
  if (params.pricedLevel === params.targetLevel) {
    return {
      price: params.currentPrice,
      pricedLevel: params.pricedLevel,
      changed: false,
    }
  }
  return {
    price: resolveMarketOrderDefaultPrice(params),
    pricedLevel: params.targetLevel,
    changed: true,
  }
}
