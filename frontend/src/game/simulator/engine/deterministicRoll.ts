import { doubleToInt64Bits } from './math.ts'

/**
 * 确定性 Combat SplitMix64 伪随机采样器，1:1 复刻 `Domain.Combat.DeterministicRoll.Next`。
 * 注意：`seed` / `side` / `attackCount` / `channel` 均使用 64 位无符号算术。
 */
export function deterministicRollNext(
  seed: bigint,
  side: bigint,
  attackCount: number,
  channel: bigint | number = 0n,
): number {
  const s = BigInt.asUintN(64, seed)
  const atk = BigInt.asUintN(64, doubleToInt64Bits(attackCount))
  const sd = BigInt.asUintN(64, typeof side === 'bigint' ? side : BigInt(side))
  const ch = BigInt.asUintN(64, typeof channel === 'bigint' ? channel : BigInt(channel))

  let value = BigInt.asUintN(
    64,
    s ^
      BigInt.asUintN(64, atk * 0x9e3779b97f4a7c15n) ^
      BigInt.asUintN(64, (sd + 1n) * 0xd1b54a32d192ed03n) ^
      BigInt.asUintN(64, (ch + 1n) * 0x94d049bb133111ebn),
  )
  value = BigInt.asUintN(64, (value ^ (value >> 30n)) * 0xbf58476d1ce4e5b9n)
  value = BigInt.asUintN(64, (value ^ (value >> 27n)) * 0x94d049bb133111ebn)
  value = value ^ (value >> 31n)

  // (value >> 11) * (1d / 9007199254740992d)
  return Number(value >> 11n) * (1 / 9007199254740992)
}
