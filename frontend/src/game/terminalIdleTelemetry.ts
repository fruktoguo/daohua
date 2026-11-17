import type { LogSegment } from '../logging/gameLogService'
import type { IdleTerminalLang } from '../uiStore'
import type { ConnectionState } from './gameStore'
import type { QueueActionEstimate } from './queueTimeEstimate'
import type { AutoUseBarType, CombatResourceState, ExpeditionProgress, QueueState, TechniqueId } from './types'

/**
 * 终端面板健康度色彩语义：
 * - 'ok': 翠绿正常态
 * - 'info': 青蓝信息态
 * - 'warn': 琥珀预警态（暂停/空闲/重连）
 * - 'crit': 绯红告警态（受阻/低血量/断线）
 */
export type TerminalHealthTone = 'ok' | 'info' | 'warn' | 'crit'

/**
 * 终端任务运行态标识
 */
export type TerminalJobStatus = 'RUN' | 'PAUSED' | 'BLOCKED' | 'QUEUED' | 'IDLE'

/**
 * 终端网络连接态标识
 */
export type TerminalNetState = 'online' | 'reconnecting' | 'offline'

/**
 * 挂机终端遥测输入快照
 */
export interface TerminalTelemetryInput {
  connection: ConnectionState
  latencyMs: number | null
  onlineCount: number | null
  offlineIdleCount: number | null
  queue: QueueState
  combatResources: CombatResourceState | null | undefined
  expedition?: ExpeditionProgress | null
  version: number
  elapsedSeconds: number
  tick: number
}

/**
 * 挂机终端推导出的纯字符监控指标快照
 */
export interface TerminalTelemetrySnapshot {
  netState: TerminalNetState
  netTone: TerminalHealthTone
  rttMs: number
  onlinePlayers: number
  offlineIdlePlayers: number
  jobStatus: TerminalJobStatus
  jobTone: TerminalHealthTone
  jobProgressRatio: number
  queueDepth: number
  completedCycles: number
  hpCurrent: number
  hpMax: number
  hpRatio: number
  hpTone: TerminalHealthTone
  mpCurrent: number
  mpMax: number
  mpRatio: number
  mpTone: TerminalHealthTone
  cpuCores: readonly number[]
}

/** Unicode Braille 左列点位掩码（自下而上 0..4 级高度） */
const BRAILLE_LEFT_MASK = [0x00, 0x40, 0x44, 0x46, 0x47] as const
/** Unicode Braille 右列点位掩码（自下而上 0..4 级高度） */
const BRAILLE_RIGHT_MASK = [0x00, 0x80, 0xa0, 0xb0, 0xb8] as const

/** 九大修真大境界英文名（严格对齐总纲 §19.1 顺序：练气~渡劫） */
const ENGLISH_MAJOR_REALMS = [
  'Qi Refining',
  'Foundation',
  'Golden Core',
  'Nascent Soul',
  'Soul Formation',
  'Void Refining',
  'Integration',
  'Mahayana',
  'Tribulation',
] as const

/** 七门生活技艺标准英文名（对齐总纲 §19.3） */
export const ENGLISH_TECHNIQUE_NAMES: Readonly<Record<TechniqueId, string>> = {
  alchemy: 'Alchemy',
  forging: 'Forging',
  talisman: 'Talisman',
  enhancement: 'Secret Art',
  formation: 'Formation',
  gathering: 'Gathering',
  mining: 'Mining',
}

/** 自动消耗品栏位标准英文名 */
export const ENGLISH_AUTO_USE_BAR_NAMES: Readonly<Record<AutoUseBarType, string>> = {
  exploration: 'Exploration',
  alchemy: 'Alchemy',
  forging: 'Forging',
  talisman: 'Talisman',
  formation: 'Formation',
  enhancement: 'Enhancement',
  combat: 'Combat',
}

/** 特定常见实体 ID 的标准英文名覆写 */
const SPECIAL_ENTITY_ENGLISH_NAMES: Readonly<Record<string, string>> = {
  'currency.spirit-stone': 'Spirit Stone',
  'item.gongde': 'Merit',
  'item.gongde-bound': 'Bound Merit',
  'item.storage-bag.broken': 'Broken Storage Bag',
  'item.reverse-fate-pill': 'Reverse-Fate Pill',
}

/** 需剥离的实体语义 ID 命名空间前缀 */
const ENTITY_ID_PREFIX_RE = /^(?:currency|item\.tome|item\.fragment|item|equipment\.tool|equipment|explore|cultivate|alchemy|forging|talisman|formation|enhancement|combat|encounter|monster|enemy|node|ability|buff|trait|building)[.-]/i

export function clamp01(value: number): number {
  if (!Number.isFinite(value)) return 0
  if (value < 0) return 0
  if (value > 1) return 1
  return value
}

/** 确定性轻量伪随机归一化函数（0..1），保证测试可复现 */
export function pseudoNoise01(seed: number): number {
  const x = Math.sin(seed * 12.9898 + 78.233) * 43758.5453
  return x - Math.floor(x)
}

/**
 * 将配表语义 ID（如 `item.ningxue-grass`、`explore.gather-ningxue-grass`）规范化为可读英文名称
 */
export function humanizeEntityId(id: string | null | undefined, fallback = 'Unknown'): string {
  if (!id || !id.trim()) return fallback
  const trimmed = id.trim()
  const special = SPECIAL_ENTITY_ENGLISH_NAMES[trimmed]
  if (special) return special
  if (trimmed.startsWith('item.tome.')) {
    return `Tome: ${humanizeEntityId(trimmed.slice('item.tome.'.length), fallback)}`
  }
  if (trimmed.startsWith('item.fragment.')) {
    return `Fragment: ${humanizeEntityId(trimmed.slice('item.fragment.'.length), fallback)}`
  }
  const stripped = trimmed.replace(ENTITY_ID_PREFIX_RE, '')
  const words = stripped
    .split(/[._-]+/)
    .filter(Boolean)
    .map(part => part.charAt(0).toUpperCase() + part.slice(1))
  return words.length > 0 ? words.join(' ') : trimmed
}

/**
 * 紧凑英文数值格式化（k / M / B / T）
 */
export function formatEnglishNumber(value: number): string {
  if (!Number.isFinite(value)) return '0'
  const sign = value < 0 ? '-' : ''
  const abs = Math.abs(value)
  if (abs < 10_000) {
    return `${sign}${Math.round(abs).toLocaleString('en-US')}`
  }
  if (abs < 1_000_000) {
    return `${sign}${(abs / 1_000).toFixed(1)}k`
  }
  if (abs < 1_000_000_000) {
    return `${sign}${(abs / 1_000_000).toFixed(2)}M`
  }
  if (abs < 1_000_000_000_000) {
    return `${sign}${(abs / 1_000_000_000).toFixed(2)}B`
  }
  return `${sign}${(abs / 1_000_000_000_000).toFixed(2)}T`
}

/**
 * 紧凑英文时长格式化（如 `5h 48m`、`12m 30s`、`45s`）
 */
export function formatEnglishDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return '0s'
  const s = Math.ceil(seconds)
  if (s < 60) return `${s}s`
  const totalMinutes = Math.floor(s / 60)
  const remSec = s % 60
  if (totalMinutes < 60) {
    return remSec > 0 ? `${totalMinutes}m ${remSec}s` : `${totalMinutes}m`
  }
  const totalHours = Math.floor(totalMinutes / 60)
  const remMin = totalMinutes % 60
  if (totalHours < 24) {
    return remMin > 0 ? `${totalHours}h ${remMin}m` : `${totalHours}h`
  }
  const days = Math.floor(totalHours / 24)
  const remHours = totalHours % 24
  return remHours > 0 ? `${days}d ${remHours}h` : `${days}d`
}

/**
 * 英文模式下的行动队列剩余轮次与预计耗时格式化（与 `formatQueueRepeatsText` 分支严格对称）
 */
export function formatEnglishQueueEstimate(estimate: QueueActionEstimate, remainingSeconds: number): string {
  if (!estimate.hasEstimate) {
    return estimate.isInfinite ? 'INF' : `x${estimate.remainingRepeats}`
  }
  if (estimate.effectiveCount <= 0) {
    if (estimate.isInfinite) return 'INF (x0)'
    if (estimate.showEffectiveCount) return `x${estimate.remainingRepeats} (avail x0)`
    return `x${estimate.remainingRepeats}`
  }
  if (estimate.roundDurationSeconds <= 0) {
    if (estimate.isInfinite) return `INF (x${estimate.effectiveCount})`
    if (estimate.showEffectiveCount) return `x${estimate.remainingRepeats} (cap x${estimate.effectiveCount})`
    return `x${estimate.remainingRepeats}`
  }
  const dur = formatEnglishDuration(remainingSeconds)
  if (estimate.isInfinite) {
    return `INF (x${estimate.effectiveCount} · ~${dur})`
  }
  if (estimate.showEffectiveCount) {
    return `x${estimate.remainingRepeats} (cap x${estimate.effectiveCount} · ~${dur})`
  }
  return `x${estimate.remainingRepeats} (~${dur})`
}

/**
 * 英文模式下的境界层级显示名（1~127 层，与 `calculateRealmInfo` 段表完全一致）
 */
export function formatEnglishRealmDisplay(level: number): string {
  const safeLevel = Math.max(1, Math.min(127, Math.floor(level || 1)))
  if (safeLevel <= 18) {
    const tierTag = safeLevel <= 6 ? 'Martial' : safeLevel <= 12 ? 'Mystic' : 'Heaven'
    return `Mortal · ${tierTag} · Stage ${safeLevel}`
  }
  if (safeLevel >= 127) {
    return 'Immortal · Ascension'
  }
  const zeroBased = safeLevel - 19
  const majorIndex = Math.floor(zeroBased / 12)
  const layerInMajor = (zeroBased % 12) + 1
  const majorName = ENGLISH_MAJOR_REALMS[majorIndex] ?? 'Immortal'
  const nextMajorName = ENGLISH_MAJOR_REALMS[majorIndex + 1] ?? 'Ascension'

  if (layerInMajor <= 9) {
    return `${majorName} · Layer ${layerInMajor}`
  }
  if (layerInMajor === 10) {
    return `${majorName} · Peak`
  }
  if (layerInMajor === 11) {
    return `${majorName} · Perfection`
  }
  return `Half-Step ${nextMajorName}`
}

/**
 * 绘制纯字符固定宽度块状进度条（零图像/零 SVG）
 */
export function renderAsciiBar(ratio: number, width: number): string {
  const safeWidth = Math.max(4, Math.floor(width))
  const clamped = clamp01(ratio)
  const exact = clamped * safeWidth
  const fullBlocks = Math.floor(exact)
  const remainder = exact - fullBlocks
  const hasHalf = fullBlocks < safeWidth && remainder >= 0.45
  const emptyBlocks = Math.max(0, safeWidth - fullBlocks - (hasHalf ? 1 : 0))
  return `${'█'.repeat(fullBlocks)}${hasHalf ? '▓' : ''}${'░'.repeat(emptyBlocks)}`
}

/**
 * 绘制纯字符 Unicode Braille 双列高频点阵波形曲线
 * 每个输出字符编码相邻 2 个采样点的高度（0..4 档）
 */
export function renderBrailleSparkline(samples: readonly number[], charWidth: number): string {
  const safeChars = Math.max(4, Math.floor(charWidth))
  const neededSamples = safeChars * 2
  const padded: number[] = []
  const padCount = Math.max(0, neededSamples - samples.length)
  for (let i = 0; i < padCount; i += 1) padded.push(0)
  const sliceStart = Math.max(0, samples.length - neededSamples)
  for (let i = sliceStart; i < samples.length; i += 1) {
    padded.push(clamp01(samples[i] ?? 0))
  }

  let out = ''
  for (let c = 0; c < safeChars; c += 1) {
    const leftVal = padded[c * 2] ?? 0
    const rightVal = padded[c * 2 + 1] ?? 0
    const leftLevel = Math.min(4, Math.max(0, Math.round(leftVal * 4)))
    const rightLevel = Math.min(4, Math.max(0, Math.round(rightVal * 4)))
    const codePoint = 0x2800 | BRAILLE_LEFT_MASK[leftLevel] | BRAILLE_RIGHT_MASK[rightLevel]
    out += String.fromCodePoint(codePoint)
  }
  return out
}

/**
 * 将富文本日志分词（LogSegment[]）按所选终端语言（'zh' | 'en'）扁平化为纯字符文本
 */
export function formatPlainLogSegments(segments: readonly LogSegment[], lang: IdleTerminalLang = 'zh'): string {
  if (lang === 'zh') {
    let out = ''
    for (const seg of segments) {
      if (seg.type === 'text') {
        out += seg.value.replace(/\n+/g, ' · ')
      } else {
        const amountSuffix = seg.amount !== undefined ? `×${seg.amount}` : ''
        out += `[${seg.label}]${amountSuffix}`
      }
    }
    return out.trim()
  }

  // 英文模式：基于引用分段的语义 ID 与数值增量构造紧凑纯英文事件行
  const refs: string[] = []
  const deltas: string[] = []
  for (const seg of segments) {
    if (seg.type === 'reference') {
      const enName = humanizeEntityId(seg.id, seg.id)
      const amt = seg.amount !== undefined ? ` x${seg.amount}` : ''
      refs.push(`[${enName}]${amt}`)
    } else {
      const matches = seg.value.match(/[+-]\d+(?:\.\d+)?%?/g)
      if (matches) {
        for (const m of matches) deltas.push(m)
      }
    }
  }
  if (refs.length > 0) {
    const deltaPart = deltas.length > 0 ? ` (${deltas.join(', ')})` : ''
    return `${refs.join(' -> ')}${deltaPart}`
  }
  const asciiFallback = segments
    .map(s => (s.type === 'text' ? s.value : s.id))
    .join(' ')
    .replace(/[^\x20-\x7e]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
  return asciiFallback || 'Event recorded'
}

/**
 * 将游戏实时状态纯函数聚合为挂机终端监控指标
 */
export function deriveTerminalTelemetry(input: TerminalTelemetryInput): TerminalTelemetrySnapshot {
  let netState: TerminalNetState = 'online'
  let netTone: TerminalHealthTone = 'ok'
  if (input.connection === 'offline') {
    netState = 'offline'
    netTone = 'crit'
  } else if (input.connection === 'connecting' || input.connection === 'reconnecting') {
    netState = 'reconnecting'
    netTone = 'warn'
  }

  const rttMs = input.connection === 'online'
    ? Math.max(1, Math.round(input.latencyMs ?? 12))
    : 0
  const onlinePlayers = Math.max(0, Math.floor(input.onlineCount ?? 0))
  const offlineIdlePlayers = Math.max(0, Math.floor(input.offlineIdleCount ?? 0))

  const headEntry = input.queue.entries[0]
  const queueDepth = input.queue.entries.length
  let jobStatus: TerminalJobStatus
  let jobTone: TerminalHealthTone
  if (queueDepth === 0 || !headEntry) {
    jobStatus = 'IDLE'
    jobTone = 'warn'
  } else if (input.queue.paused) {
    jobStatus = 'PAUSED'
    jobTone = 'warn'
  } else if (headEntry.status === 'blocked') {
    jobStatus = 'BLOCKED'
    jobTone = 'crit'
  } else if (headEntry.status === 'running') {
    jobStatus = 'RUN'
    jobTone = 'ok'
  } else {
    jobStatus = 'QUEUED'
    jobTone = 'info'
  }

  const completedCycles = Math.max(0, Math.floor(headEntry?.completedRepeats ?? 0))
  const exp = input.expedition ?? headEntry?.expedition ?? null

  let jobProgressRatio = 0
  if (headEntry) {
    if (headEntry.durationSeconds > 0) {
      const liveSec = headEntry.progressSeconds + (jobStatus === 'RUN' ? Math.max(0, input.elapsedSeconds) : 0)
      jobProgressRatio = clamp01(liveSec / headEntry.durationSeconds)
    } else if (exp && exp.durationSeconds > 0 && (exp.phase === 'search' || exp.phase === 'initial-search' || exp.phase === 'respawn')) {
      jobProgressRatio = clamp01(exp.progress)
    } else if (jobStatus === 'RUN') {
      jobProgressRatio = 0.2 + ((input.tick % 16) / 16) * 0.74
    } else if (jobStatus === 'PAUSED' || jobStatus === 'BLOCKED') {
      jobProgressRatio = 0.35
    }
  }

  const hpMax = Math.max(0, Math.round(input.combatResources?.maxHitpoints ?? 0))
  const rawHp = exp?.playerHitpoints != null
    ? exp.playerHitpoints
    : (input.combatResources?.hitpoints ?? hpMax)
  const hpCurrent = Math.max(0, Math.min(hpMax || Math.max(0, rawHp), Math.round(rawHp)))
  const hpRatio = hpMax > 0 ? clamp01(hpCurrent / hpMax) : 1
  const hpTone: TerminalHealthTone = hpRatio < 0.3 ? 'crit' : hpRatio < 0.55 ? 'warn' : 'ok'

  const mpMax = Math.max(0, Math.round(input.combatResources?.maxManapoints ?? 0))
  const rawMp = exp?.playerManapoints != null
    ? exp.playerManapoints
    : (input.combatResources?.manapoints ?? mpMax)
  const mpCurrent = Math.max(0, Math.min(mpMax || Math.max(0, rawMp), Math.round(rawMp)))
  const mpRatio = mpMax > 0 ? clamp01(mpCurrent / mpMax) : 1
  const mpTone: TerminalHealthTone = mpRatio < 0.2 ? 'warn' : 'info'

  const baseCpu = jobStatus === 'RUN' && netState === 'online' ? 0.42 : 0.08
  const swingCpu = jobStatus === 'RUN' && netState === 'online' ? 0.44 : 0.08
  const cpuCores: number[] = []
  for (let core = 0; core < 6; core += 1) {
    const wave = pseudoNoise01(input.tick * 1.37 + core * 19.1 + (input.version % 17))
    cpuCores.push(clamp01(baseCpu + wave * swingCpu))
  }

  return {
    netState,
    netTone,
    rttMs,
    onlinePlayers,
    offlineIdlePlayers,
    jobStatus,
    jobTone,
    jobProgressRatio,
    queueDepth,
    completedCycles,
    hpCurrent,
    hpMax,
    hpRatio,
    hpTone,
    mpCurrent,
    mpMax,
    mpRatio,
    mpTone,
    cpuCores,
  }
}

/**
 * 生成标准 `hexdump -C` 风格的十六进制内存页检视行
 */
export function buildHexDumpRows(baseAddress: number, version: number, tick: number, rowCount: number): string[] {
  const rows: string[] = []
  const safeRows = Math.max(1, Math.min(16, Math.floor(rowCount)))
  for (let r = 0; r < safeRows; r += 1) {
    const addr = (baseAddress + r * 16) >>> 0
    const addrHex = `00007ff8${addr.toString(16).padStart(8, '0')}`
    const hexLeft: string[] = []
    const hexRight: string[] = []
    let ascii = ''
    for (let b = 0; b < 16; b += 1) {
      const n = Math.floor(pseudoNoise01((r + 1) * 31 + b * 17 + (version % 97) + Math.floor(tick / 2)) * 256) & 0xff
      const hex = n.toString(16).padStart(2, '0')
      if (b < 8) hexLeft.push(hex)
      else hexRight.push(hex)
      ascii += n >= 33 && n <= 126 ? String.fromCharCode(n) : '.'
    }
    rows.push(`${addrHex}  ${hexLeft.join(' ')}  ${hexRight.join(' ')}  |${ascii}|`)
  }
  return rows
}
