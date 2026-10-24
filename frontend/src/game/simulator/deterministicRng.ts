/**
 * 确定性随机数生成器（与服务端 Domain.Combat.DeterministicRoll 与 Domain.Common.DeterministicSequence 逐位完全一致）。
 * 采用 64 位无符号整数 BigInt 算术与 IEEE 754 浮点转换。
 */

const floatView = new Float64Array(1)
const bigIntView = new BigInt64Array(floatView.buffer)

const MASK64 = (1n << 64n) - 1n
function u64(v: bigint): bigint {
  return v & MASK64
}

/** 将 IEEE 754 double 转换为等价的 64 位二进制表示（与 .NET BitConverter.DoubleToInt64Bits 一致） */
export function doubleToInt64Bits(value: number): bigint {
  floatView[0] = value
  return BigInt.asUintN(64, bigIntView[0])
}

/**
 * 推进到确定性序列的下一个种子状态（xorshift64）。
 * 与 C# DeterministicSequence.NextSeed 完全对齐。
 */
export function nextSeed(seed: bigint): bigint {
  let v = u64(seed)
  v = u64(v ^ (v << 13n))
  v = u64(v ^ (v >> 7n))
  v = u64(v ^ (v << 17n))
  return BigInt.asIntN(64, v)
}

/**
 * 掷出 [0, 1) 确定性双精度浮点数（SplitMix64 混合固定计数器）。
 * 与 C# DeterministicRoll.Next 完全对齐。
 * 
 * @param seed 战斗种子
 * @param side 阵营/角色侧位
 * @param attackCount 攻击出手计数
 * @param channel 判定通道号
 */
export function deterministicRoll(seed: bigint, side: bigint, attackCount: number, channel: bigint): number {
  let value = u64(seed)
    ^ u64(doubleToInt64Bits(attackCount) * 0x9E3779B97F4A7C15n)
    ^ u64((side + 1n) * 0xD1B54A32D192ED03n)
    ^ u64((channel + 1n) * 0x94D049BB133111EBn)

  value = u64((value ^ (value >> 30n)) * 0xBF58476D1CE4E5B9n)
  value = u64((value ^ (value >> 27n)) * 0x94D049BB133111EBn)
  value = u64(value ^ (value >> 31n))
  return Number(value >> 11n) * (1 / 9007199254740992)
}

/** 掉落数量侧流使用的固定盐位（对齐 CombatActionRules.DropQuantityChannelSalt） */
export const DROP_QUANTITY_CHANNEL_SALT = 0x9E3779B97F4A7C15n

/**
 * 单波遭遇预算与怪物抽取随机数：
 * (Next(ref seed) % 1_000_000) / 1_000_000
 */
export function rollMillionth(seed: bigint): { roll: number; nextSeed: bigint } {
  const next = nextSeed(seed)
  const roll = Number(u64(next) % 1_000_000n) / 1_000_000
  return { roll, nextSeed: next }
}

/**
 * 掉落判定与掉落数量抽取（对齐 CombatActionRules.ApplyCombatRewards）
 */
export function rollDropTable(seed: bigint, minQuantity: number, maxQuantity: number): {
  chanceRoll: number
  quantity: number
  nextSeed: bigint
} {
  const nextMain = nextSeed(seed)
  const chanceRoll = Number(u64(nextMain) % 1_000_000n) / 1_000_000

  const quantitySeed = nextSeed(seed ^ DROP_QUANTITY_CHANNEL_SALT)
  const quantityRoll = Number(u64(quantitySeed) % 1_000_000n) / 1_000_000

  const quantity = Math.min(
    maxQuantity,
    Math.max(minQuantity, Math.floor(minQuantity + (maxQuantity - minQuantity + 1) * quantityRoll))
  )

  return {
    chanceRoll,
    quantity,
    nextSeed: nextMain,
  }
}
