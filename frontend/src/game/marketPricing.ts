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
