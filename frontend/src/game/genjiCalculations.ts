/**
 * 筑基期「根基（Genji）」纯计算模块：与服务端 `Domain.Player.GenjiRules` 逐位同源（总纲 §3.2.4）。
 *
 * 核心规则：
 * 1. Lv.30 半步筑基满修为后开启常驻凝练，Lv.31+ 天然解锁；
 * 2. 每 1 点根基提升 +1% 全六维，单项加成向上取整 `(basePoints * genjiPoints + 99) / 100`；
 * 3. Lv.30 -> Lv.31 突破至筑基一层要求任意一项有效六维（含根基加成）>= 99；
 * 4. 根基上限保留在累计池中（档位覆盖不叠加）：Lv.30 起步 30 点，Lv.31 起大境 +20、中境(4/7/10层) +8、小境 +4；
 * 5. 消耗当前层修为（受灵根乘区影响）：50% 上限前线性降费（0 点起为正常要求的 20% = 10% 升级修为），
 *    50% 处恰为正常要求（50% 升级修为），前 50% 省下的修为对称守恒转移至后 50% 极限养成区间。
 */

/** 开启根基系统的境界等级（Lv.30 半步筑基） */
export const GENJI_REALM_LEVEL = 30

/** 半步筑基初始根基上限 */
export const GENJI_INITIAL_CAP = 30

/** 普通小境界追加上限 */
export const GENJI_MINOR_CAP_BONUS = 4

/** 中境界阶段（4/7/10层）追加上限 */
export const GENJI_PHASE_CAP_BONUS = 8

/** 大境界突破（筑基一层 Lv.31、金丹一层 Lv.43……飞升 Lv.127）追加上限 */
export const GENJI_MAJOR_CAP_BONUS = 20

/** 突破筑基一层要求任意一项有效六维达到的点数 */
export const GENJI_REQUIRED_ATTR_LEVEL = 99

/** 标准单点根基消耗占当前境界升级所需修为的比例（50%） */
export const GENJI_BASE_EXP_COST_RATIO = 0.5

/** 0 点起步相对于标准单点成本的最低倍率（20%，即 10% 升级修为） */
export const GENJI_MIN_RELATIVE_MULTIPLIER = 0.2

/**
 * 计算指定境界等级的累计根基上限（与 `GenjiRules.CalculateCap` 完全一致）。
 */
export function calculateGenjiCap(realmLevel: number): number {
  const safeLevel = Math.floor(realmLevel)
  if (!Number.isFinite(safeLevel) || safeLevel < GENJI_REALM_LEVEL) return 0
  let cap = GENJI_INITIAL_CAP
  for (let lv = GENJI_REALM_LEVEL + 1; lv <= safeLevel; lv++) {
    if (lv <= 127) {
      const cultLevel = lv - 18
      const layerInMajor = ((cultLevel - 1) % 12) + 1
      if (lv === 127 || layerInMajor === 1) {
        cap += GENJI_MAJOR_CAP_BONUS
      } else if (layerInMajor === 4 || layerInMajor === 7 || layerInMajor === 10) {
        cap += GENJI_PHASE_CAP_BONUS
      } else {
        cap += GENJI_MINOR_CAP_BONUS
      }
    } else {
      cap += GENJI_MINOR_CAP_BONUS
    }
  }
  return cap
}

/**
 * 计算单项六维在根基百分比增幅下的额外加成点数（每点 +1%，向上取整，纯整数精确运算）。
 */
export function resolveGenjiBonusPoints(basePoints: number, genjiPoints: number): number {
  const b = Math.floor(Math.max(0, Number.isFinite(basePoints) ? basePoints : 0))
  const g = Math.floor(Math.max(0, Number.isFinite(genjiPoints) ? genjiPoints : 0))
  if (b <= 0 || g <= 0) return 0
  return Math.floor((b * g + 99) / 100)
}

/**
 * 计算相对于标准单点基准成本（50% 升级修为）的消耗倍率（与 `GenjiRules.ResolveRelativeCostMultiplier` 一致）。
 */
export function resolveGenjiRelativeMultiplier(currentPoints: number, maxCap: number): number {
  const cap = Math.floor(maxCap)
  const cur = Math.floor(currentPoints)
  if (!Number.isFinite(cap) || !Number.isFinite(cur) || cap <= 0 || cur < 0 || cur >= cap) return 0
  const half = Math.floor(cap / 2)
  if (half <= 0) return 1.0
  if (cur < half) {
    return GENJI_MIN_RELATIVE_MULTIPLIER + ((1.0 - GENJI_MIN_RELATIVE_MULTIPLIER) * cur) / half
  }
  const offset = cur - half
  if (offset === 0) return 1.0
  if (half === 1) return 2.0 - GENJI_MIN_RELATIVE_MULTIPLIER
  const slope = ((1.0 - GENJI_MIN_RELATIVE_MULTIPLIER) * (half + 1)) / (half * (half - 1))
  return 1.0 + slope * offset
}

/** C# MidpointRounding.AwayFromZero 对偶四舍五入 */
function roundAwayFromZero(value: number): number {
  return value >= 0 ? Math.floor(value + 0.5) : Math.ceil(value - 0.5)
}

/**
 * 计算凝练下一级根基所需消耗的当前层修为（与 `GenjiRules.ResolveCondenseExpCost` 一致）。
 */
export function resolveGenjiCondenseCost(
  expCapWithLinggen: number,
  currentPoints: number,
  maxCap: number,
): number {
  if (!Number.isFinite(expCapWithLinggen) || expCapWithLinggen <= 0) return 0
  const mult = resolveGenjiRelativeMultiplier(currentPoints, maxCap)
  if (mult <= 0) return 0
  const raw = expCapWithLinggen * GENJI_BASE_EXP_COST_RATIO * mult
  return Math.max(1, roundAwayFromZero(raw))
}

/**
 * 判定角色当前是否已解锁根基常驻面板。
 */
export function isGenjiUnlocked(
  realmLevel: number,
  unlockedFlag: boolean | undefined,
  currentPoints: number,
  expFull: boolean,
): boolean {
  if (realmLevel < GENJI_REALM_LEVEL) return false
  if (realmLevel > GENJI_REALM_LEVEL || unlockedFlag === true || currentPoints > 0) return true
  return expFull
}

/**
 * 半步筑基满修为时，若任意有效六维尚未达到 99 点，顶部突破位应展示「凝练根基」引导而非直接破境。
 */
export function shouldShowCondenseGenjiForBreakthrough(
  realmLevel: number,
  maxEffectiveRootLevel: number,
  expFull: boolean,
): boolean {
  return realmLevel === GENJI_REALM_LEVEL && expFull && maxEffectiveRootLevel < GENJI_REQUIRED_ATTR_LEVEL
}
