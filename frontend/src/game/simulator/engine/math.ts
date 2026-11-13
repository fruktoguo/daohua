/**
 * 模拟引擎底层数值、整数种子、GUID 与时间确定性原语。
 * 与 .NET Domain / Ecs 层的舍入模式、64 位整数运算和浮点位表示保持位级一致。
 */

const f64Buf = new ArrayBuffer(8)
const f64View = new DataView(f64Buf)

/** 将双精度浮点数转换为有符号 64 位整数位表示（对应 BitConverter.DoubleToInt64Bits）。 */
export function doubleToInt64Bits(value: number): bigint {
  f64View.setFloat64(0, value, true)
  return f64View.getBigInt64(0, true)
}

/** 将双精度浮点数转换为无符号 64 位整数位表示。 */
export function doubleToUint64Bits(value: number): bigint {
  f64View.setFloat64(0, value, true)
  return f64View.getBigUint64(0, true)
}

/**
 * 四舍五入到最近偶数（银行家舍入，对应 C# `Math.Round(double)` 默认 MidpointRounding.ToEven）。
 */
export function roundToEven(value: number): number {
  if (!Number.isFinite(value)) return value
  const floor = Math.floor(value)
  const diff = value - floor
  if (diff < 0.5) return floor
  if (diff > 0.5) return floor + 1
  return floor % 2 === 0 ? floor : floor + 1
}

/**
 * 四舍五入并远离零取整（对应 C# `Math.Round(double, MidpointRounding.AwayFromZero)`）。
 */
export function roundAwayFromZero(value: number): number {
  if (!Number.isFinite(value)) return value
  return value >= 0 ? Math.floor(value + 0.5) : Math.ceil(value - 0.5)
}

/** 字符串序数比较（对应 C# `StringComparer.Ordinal`）。 */
export function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0
}

/** ASCII / OrdinalIgnoreCase 比较。 */
export function equalsIgnoreCase(a: string | null | undefined, b: string | null | undefined): boolean {
  if (a === b) return true
  if (a == null || b == null) return false
  return a.toLowerCase() === b.toLowerCase()
}

/** 限制数值到闭区间 [min, max]。 */
export function clamp(value: number, min: number, max: number): number {
  if (value < min) return min
  if (value > max) return max
  return value
}

/** 将大整数截断为有符号 64 位整数。 */
export function toInt64(value: bigint): bigint {
  return BigInt.asIntN(64, value)
}

/** 将大整数截断为无符号 64 位整数。 */
export function toUint64(value: bigint): bigint {
  return BigInt.asUintN(64, value)
}

/**
 * 确定性伪随机序列推进（xorshift64，对应 `Domain.Common.DeterministicSequence.NextSeed`）。
 */
export function nextSeed(seed: bigint): bigint {
  let value = BigInt.asUintN(64, seed)
  value = BigInt.asUintN(64, value ^ (value << 13n))
  value = value ^ (value >> 7n)
  value = BigInt.asUintN(64, value ^ (value << 17n))
  return BigInt.asIntN(64, value)
}

/**
 * 无符号 64 位整数对正整数长度取模（对应 `(int)((ulong)seed % (ulong)len)`）。
 */
export function seedModulo(seed: bigint, len: number): number {
  return Number(BigInt.asUintN(64, seed) % BigInt(len))
}

/**
 * 百万分比统一随机浮点生成：`(ulong)NextSeed(seed) % 1_000_000 / 1_000_000`。
 */
export function nextMillionthRoll(seed: bigint): { next: bigint; roll: number } {
  const next = nextSeed(seed)
  const roll = Number(BigInt.asUintN(64, next) % 1_000_000n) / 1_000_000
  return { next, roll }
}

/**
 * FNV-1a 64 初始种子派生（对应 `SimulationEngine.CreateSeed(encounterId, completedRepeats)`）。
 */
export function createSeed(encounterId: string, completedRepeats: number): bigint {
  let hash = 14695981039346656037n
  const prime = 1099511628211n
  for (let i = 0; i < encounterId.length; i++) {
    hash = BigInt.asUintN(64, hash ^ BigInt(encounterId.charCodeAt(i)))
    hash = BigInt.asUintN(64, hash * prime)
  }
  hash = BigInt.asUintN(64, hash ^ doubleToUint64Bits(completedRepeats))
  return BigInt.asIntN(64, hash)
}

// ---------- 纯 TS 同步 SHA-256（无 Node crypto 依赖，浏览器/Worker 通用） ----------

const SHA256_K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
])

function rotr(x: number, n: number): number {
  return (x >>> n) | (x << (32 - n))
}

/** 计算 ASCII 字符串的 SHA-256 前 16 字节并按 .NET `new Guid(bytes[..16])` 格式转为 32 位小写无连字符十六进制字符串。 */
export function deriveSessionGuidN(seed: bigint, nodeId: string, difficulty: number): string {
  const inputStr = `simulator:${seed.toString()}:${nodeId}:${difficulty}`
  const msg = new Uint8Array(inputStr.length)
  for (let i = 0; i < inputStr.length; i++) {
    msg[i] = inputStr.charCodeAt(i) & 0xff
  }
  const bitLen = msg.length * 8
  const paddedLen = Math.ceil((msg.length + 9) / 64) * 64
  const padded = new Uint8Array(paddedLen)
  padded.set(msg)
  padded[msg.length] = 0x80
  const view = new DataView(padded.buffer)
  view.setUint32(paddedLen - 4, bitLen >>> 0, false)
  view.setUint32(paddedLen - 8, Math.floor(bitLen / 0x100000000), false)

  let h0 = 0x6a09e667
  let h1 = 0xbb67ae85
  let h2 = 0x3c6ef372
  let h3 = 0xa54ff53a
  let h4 = 0x510e527f
  let h5 = 0x9b05688c
  let h6 = 0x1f83d9ab
  let h7 = 0x5be0cd19
  const w = new Uint32Array(64)

  for (let offset = 0; offset < paddedLen; offset += 64) {
    for (let i = 0; i < 16; i++) {
      w[i] = view.getUint32(offset + i * 4, false)
    }
    for (let i = 16; i < 64; i++) {
      const s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3)
      const s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10)
      w[i] = (w[i - 16] + s0 + w[i - 7] + s1) >>> 0
    }
    let a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7
    for (let i = 0; i < 64; i++) {
      const S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)
      const ch = (e & f) ^ (~e & g)
      const temp1 = (h + S1 + ch + SHA256_K[i] + w[i]) >>> 0
      const S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)
      const maj = (a & b) ^ (a & c) ^ (b & c)
      const temp2 = (S0 + maj) >>> 0
      h = g
      g = f
      f = e
      e = (d + temp1) >>> 0
      d = c
      c = b
      b = a
      a = (temp1 + temp2) >>> 0
    }
    h0 = (h0 + a) >>> 0
    h1 = (h1 + b) >>> 0
    h2 = (h2 + c) >>> 0
    h3 = (h3 + d) >>> 0
    h4 = (h4 + e) >>> 0
    h5 = (h5 + f) >>> 0
    h6 = (h6 + g) >>> 0
    h7 = (h7 + h) >>> 0
  }

  const bytes = new Uint8Array(16)
  const out = new DataView(bytes.buffer)
  out.setUint32(0, h0, false)
  out.setUint32(4, h1, false)
  out.setUint32(8, h2, false)
  out.setUint32(12, h3, false)

  // .NET new Guid(byte[16])：Data1(4B LE), Data2(2B LE), Data3(2B LE), Data4(8B BE)
  const hex = (b: number) => b.toString(16).padStart(2, '0')
  return (
    hex(bytes[3]) + hex(bytes[2]) + hex(bytes[1]) + hex(bytes[0]) +
    hex(bytes[5]) + hex(bytes[4]) +
    hex(bytes[7]) + hex(bytes[6]) +
    hex(bytes[8]) + hex(bytes[9]) + hex(bytes[10]) + hex(bytes[11]) +
    hex(bytes[12]) + hex(bytes[13]) + hex(bytes[14]) + hex(bytes[15])
  )
}

// ---------- 时间精度原语（对齐 .NET DateTimeOffset ticks） ----------

/** 1 秒 = 10_000_000 ticks（.NET TimeSpan.TicksPerSecond）。 */
export const TICKS_PER_SECOND = 10_000_000n
export const TICKS_PER_MILLISECOND = 10_000n

/** 从 unix 秒构造 tick 数：`FromUnixTimeMilliseconds(roundToEven(sec * 1000))` 转 ticks。 */
export function ticksFromUnixSeconds(unixSeconds: number): bigint {
  const ms = BigInt(roundToEven(unixSeconds * 1000))
  return ms * TICKS_PER_MILLISECOND
}

/** 对齐 `SimulationClock.AddSecondsExact`：`AddTicks((long)Math.Round(seconds * 1e7, AwayFromZero))`。 */
export function addSecondsExactTicks(baseTicks: bigint, seconds: number): bigint {
  return baseTicks + BigInt(roundAwayFromZero(seconds * 10_000_000))
}

/** 对齐 `SimulatorRunner.Run` 目标时刻：`startAt.AddTicks((long)(DurationSeconds * 1e7 + 0.5))`。 */
export function addDurationTruncTicks(baseTicks: bigint, durationSeconds: number): bigint {
  return baseTicks + BigInt(Math.trunc(durationSeconds * 10_000_000 + 0.5))
}

/** 将 ticks 转换为 .NET `ToUnixTimeMilliseconds() / 1000.0`（截断整毫秒转秒）。 */
export function ticksToUnixSeconds(ticks: bigint): number {
  const ms = ticks / TICKS_PER_MILLISECOND
  return Number(ms) / 1000
}

/** 计算 `(toTicks - fromTicks).TotalSeconds`。 */
export function ticksDiffSeconds(fromTicks: bigint, toTicks: bigint): number {
  return Number(toTicks - fromTicks) / 10_000_000
}

/** 规范化 GUID（小写 8-4-4-4-12）。 */
const GUID_REGEX = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/
const GUID_N_REGEX = /^[0-9a-fA-F]{32}$/

export function tryParseGuid(raw: string | null | undefined): string | null {
  if (!raw) return null
  const trimmed = raw.trim()
  if (GUID_REGEX.test(trimmed)) return trimmed.toLowerCase()
  if (GUID_N_REGEX.test(trimmed)) {
    const s = trimmed.toLowerCase()
    return `${s.slice(0, 8)}-${s.slice(8, 12)}-${s.slice(12, 16)}-${s.slice(16, 20)}-${s.slice(20, 32)}`
  }
  return null
}

export const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'
