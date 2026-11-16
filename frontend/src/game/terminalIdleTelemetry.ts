import type { ConnectionState } from './gameStore'
import type { ActionType, CombatResourceState, QueueState } from './types'

/**
 * 终端面板健康度色彩语义：
 * - 'ok': 翠绿正常态
 * - 'info': 青蓝信息态
 * - 'warn': 琥珀预警态（暂停/空闲/重连）
 * - 'crit': 绯红告警态（受阻/低血量/断线）
 */
export type TerminalHealthTone = 'ok' | 'info' | 'warn' | 'crit'

/**
 * 终端任务调度器伪装运行态（暗中对应行动队列真实状态）
 */
export type TerminalJobStatus = 'RUN' | 'PAUSED' | 'BLOCKED' | 'QUEUED' | 'IDLE'

/**
 * 终端网络网格伪装连接态（暗中对应 WebSocket 连接状态）
 */
export type TerminalNetLinkState = 'ESTABLISHED' | 'SYN_RETRANS' | 'LINK_DOWN'

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
  version: number
  sequenceId: number
  elapsedSeconds: number
  tick: number
}

/**
 * 挂机终端推导出的纯字符监控指标快照
 */
export interface TerminalTelemetrySnapshot {
  hostTag: string
  walLsn: string
  netLink: TerminalNetLinkState
  netTone: TerminalHealthTone
  rttMs: number
  activePeers: number
  standbyNodes: number
  jobStatus: TerminalJobStatus
  jobTone: TerminalHealthTone
  daemonUnit: string
  jobProgressRatio: number
  queueDepth: number
  completedCycles: number
  schedulePolicy: 'DAEMON_INF' | 'RING_LOOP' | 'BATCH_FIN' | 'NO_WORK'
  memResidentRatio: number
  memTone: TerminalHealthTone
  memUsedGiB: string
  memTotalGiB: string
  bufKernelRatio: number
  bufTone: TerminalHealthTone
  bufUsedGiB: string
  bufTotalGiB: string
  swapRatio: number
  cpuCores: readonly number[]
  cpuAverage: number
  loadAvg: readonly [string, string, string]
}

/** 行动类别 -> 伪装 Linux 守护进程单元名（严禁暴露任何游戏/修仙词汇） */
const ACTION_DAEMON_MAP: Readonly<Record<ActionType, string>> = {
  combat: 'sec-auditd.service',
  cultivation: 'numa-balancerd.service',
  exploration: 'vfs-indexerd.service',
  alchemy: 'etl-compactor.service',
  enhancement: 'ecc-scrubber.service',
}

/** Unicode Braille 左列点位掩码（自下而上 0..4 级高度） */
const BRAILLE_LEFT_MASK = [0x00, 0x40, 0x44, 0x46, 0x47] as const
/** Unicode Braille 右列点位掩码（自下而上 0..4 级高度） */
const BRAILLE_RIGHT_MASK = [0x00, 0x80, 0xa0, 0xb0, 0xb8] as const

/** 预置的硬核运维排查命令池（供用户在终端内敲击键盘时触发 Hacker-Typer 演戏回显） */
export const HACKER_TYPER_COMMANDS: readonly {
  cmd: string
  output: readonly string[]
}[] = [
  {
    cmd: 'kubectl -n prod-mesh get pods -l tier=shard-worker -o wide',
    output: [
      'NAME                        READY   STATUS    RESTARTS   AGE     IP            NODE',
      'shard-worker-00-7c9f8b4d2   2/2     Running   0          14d     10.244.1.18   node-prod-01',
      'shard-worker-01-6b4d91a0c   2/2     Running   0          14d     10.244.2.41   node-prod-02',
      'wal-archiver-58d90c3e1-x9   1/1     Running   0          29d     10.244.3.09   node-prod-04',
    ],
  },
  {
    cmd: 'ss -tanp | grep ESTAB | head -n 4',
    output: [
      'ESTAB  0      0      10.16.0.4:443          10.16.8.91:51842   users:(("envoy",pid=1408,fd=29))',
      'ESTAB  0      0      10.16.0.4:5432         10.16.0.12:39210   users:(("postgres",pid=2194,fd=14))',
      'ESTAB  0      0      10.16.0.4:9100         10.16.0.3:48104    users:(("node_exporter",pid=912,fd=8))',
    ],
  },
  {
    cmd: 'iostat -xz nvme0n1 1 1',
    output: [
      'Device            r/s     rkB/s   rrqm/s  %rrqm r_await rareq-sz     w/s     wkB/s   %util',
      'nvme0n1        412.00   6592.00     4.20   1.01    0.18    16.00  286.50  11460.00   12.40',
    ],
  },
  {
    cmd: 'psql -Atc "SELECT pid, state, wait_event_type, pg_current_wal_lsn() FROM pg_stat_activity LIMIT 3;"',
    output: [
      '2194|active|IO|0/04A8F1C0',
      '2208|idle|Client|0/04A8F1C0',
      '2215|active|LWLock|0/04A8F1C0',
    ],
  },
  {
    cmd: 'journalctl -u sec-auditd.service -n 3 --no-pager -o short-iso',
    output: [
      '[systemd] Started epoll worker ring [fd=16..31] cpu_affinity=0x00ff',
      '[auditd] verify_batch crc32c=0x8fa2190c blocks=2048 latency_us=312',
      '[auditd] ring_buffer watermark=18% status=HEALTHY',
    ],
  },
]

function clamp01(value: number): number {
  if (!Number.isFinite(value)) return 0
  if (value < 0) return 0
  if (value > 1) return 1
  return value
}

/** 确定性轻量伪随机归一化函数（0..1），保证单测可复现且无需全局可变种子 */
export function pseudoNoise01(seed: number): number {
  const x = Math.sin(seed * 12.9898 + 78.233) * 43758.5453
  return x - Math.floor(x)
}

/** 将快照版本号格式化为标准 PostgreSQL 64-bit WAL LSN 字符串（如 `0/04A8F1C0`） */
export function formatWalLsn(version: number): string {
  const safeVer = Math.max(0, Math.floor(version || 0))
  const byteOffset = safeVer * 0x180 + 0x01a0_4000
  const hi = Math.floor(byteOffset / 0x1_0000_0000).toString(16).toUpperCase()
  const lo = (byteOffset >>> 0).toString(16).toUpperCase().padStart(8, '0')
  return `${hi}/${lo}`
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
 * 绘制纯字符 Unicode Braille 双列高频点阵波形曲线（对齐 Linux `btop` 视觉质感）
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
 * 将游戏实时状态纯函数映射为伪装服务器集群遥测指标
 */
export function deriveTerminalTelemetry(input: TerminalTelemetryInput): TerminalTelemetrySnapshot {
  const nodeIndex = ((Math.max(1, Math.floor(input.sequenceId || 4)) - 1) % 64) + 1
  const hostTag = `srv-prod-${String(nodeIndex).padStart(2, '0')}`
  const walLsn = formatWalLsn(input.version)

  let netLink: TerminalNetLinkState = 'ESTABLISHED'
  let netTone: TerminalHealthTone = 'ok'
  if (input.connection === 'offline') {
    netLink = 'LINK_DOWN'
    netTone = 'crit'
  } else if (input.connection === 'connecting' || input.connection === 'reconnecting') {
    netLink = 'SYN_RETRANS'
    netTone = 'warn'
  }

  const rttMs = input.connection === 'online'
    ? Math.max(1, Math.round(input.latencyMs ?? 14))
    : 0
  const activePeers = Math.max(1, Math.floor(input.onlineCount ?? 48))
  const standbyNodes = Math.max(0, Math.floor(input.offlineIdleCount ?? 192))

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

  const daemonUnit = headEntry ? (ACTION_DAEMON_MAP[headEntry.type] ?? 'worker-exec.service') : 'idle-watchdog.target'
  const completedCycles = Math.max(0, Math.floor(headEntry?.completedRepeats ?? 0))
  const schedulePolicy = !headEntry
    ? 'NO_WORK'
    : headEntry.remainingRepeats < 0
      ? 'DAEMON_INF'
      : input.queue.loopEnabled
        ? 'RING_LOOP'
        : 'BATCH_FIN'

  let jobProgressRatio = 0
  if (headEntry) {
    if (headEntry.durationSeconds > 0) {
      const liveSec = headEntry.progressSeconds + (jobStatus === 'RUN' ? Math.max(0, input.elapsedSeconds) : 0)
      jobProgressRatio = clamp01(liveSec / headEntry.durationSeconds)
    } else if (jobStatus === 'RUN') {
      // 战斗等事件驱动型行动无固定单轮秒数，以确定性流水线相位呈现持续运转态
      jobProgressRatio = 0.18 + ((input.tick % 16) / 16) * 0.76
    } else if (jobStatus === 'PAUSED' || jobStatus === 'BLOCKED') {
      jobProgressRatio = 0.35
    }
  }

  const maxHp = input.combatResources?.maxHitpoints ?? 0
  const curHp = input.combatResources?.hitpoints ?? 0
  const memResidentRatio = maxHp > 0 ? clamp01(curHp / maxHp) : 1
  const memTone: TerminalHealthTone = memResidentRatio < 0.3 ? 'crit' : memResidentRatio < 0.55 ? 'warn' : 'ok'

  const maxMp = input.combatResources?.maxManapoints ?? 0
  const curMp = input.combatResources?.manapoints ?? 0
  const bufKernelRatio = maxMp > 0 ? clamp01(curMp / maxMp) : 1
  const bufTone: TerminalHealthTone = bufKernelRatio < 0.2 ? 'warn' : 'info'

  const memTotal = 32
  const bufTotal = 16
  const memUsedGiB = (memResidentRatio * memTotal).toFixed(1)
  const memTotalGiB = memTotal.toFixed(1)
  const bufUsedGiB = (bufKernelRatio * bufTotal).toFixed(1)
  const bufTotalGiB = bufTotal.toFixed(1)
  const swapRatio = clamp01(0.04 + pseudoNoise01(input.tick * 0.25) * 0.03)

  const baseCpu = jobStatus === 'RUN' && netLink === 'ESTABLISHED' ? 0.44 : 0.07
  const swingCpu = jobStatus === 'RUN' && netLink === 'ESTABLISHED' ? 0.42 : 0.09
  const cpuCores: number[] = []
  let cpuSum = 0
  for (let core = 0; core < 8; core += 1) {
    const wave = pseudoNoise01(input.tick * 1.37 + core * 19.1)
    const val = clamp01(baseCpu + wave * swingCpu)
    cpuCores.push(val)
    cpuSum += val
  }
  const cpuAverage = cpuSum / 8
  const load1 = (cpuAverage * 6.4 + (jobStatus === 'RUN' ? 0.45 : 0.08)).toFixed(2)
  const load5 = (cpuAverage * 5.6 + 0.32).toFixed(2)
  const load15 = (cpuAverage * 4.9 + 0.28).toFixed(2)

  return {
    hostTag,
    walLsn,
    netLink,
    netTone,
    rttMs,
    activePeers,
    standbyNodes,
    jobStatus,
    jobTone,
    daemonUnit,
    jobProgressRatio,
    queueDepth,
    completedCycles,
    schedulePolicy,
    memResidentRatio,
    memTone,
    memUsedGiB,
    memTotalGiB,
    bufKernelRatio,
    bufTone,
    bufUsedGiB,
    bufTotalGiB,
    swapRatio,
    cpuCores,
    cpuAverage,
    loadAvg: [load1, load5, load15],
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

/**
 * 生成单行逼真的系统内核/守护进程滚动日志（纯英文与十六进制）
 */
export function generateDaemonLogLine(
  tick: number,
  telemetry: TerminalTelemetrySnapshot,
  isoTimeStr: string,
  versionAdvanced: boolean,
): { id: number; tone: TerminalHealthTone; text: string } {
  if (telemetry.netLink === 'LINK_DOWN') {
    return {
      id: tick,
      tone: 'crit',
      text: `${isoTimeStr} [net/tcp_timer] ERR socket fd=19 peer_reset state=LINK_DOWN backoff_wait=1`,
    }
  }
  if (telemetry.netLink === 'SYN_RETRANS') {
    return {
      id: tick,
      tone: 'warn',
      text: `${isoTimeStr} [envoy/upstream] WARN cluster=core_gw0 syn_retransmit attempt=${(tick % 5) + 1}`,
    }
  }
  if (versionAdvanced) {
    const crc = Math.floor(pseudoNoise01(tick * 7.3) * 0xffffffff) >>> 0
    return {
      id: tick,
      tone: 'ok',
      text: `${isoTimeStr} [pg_wal/commit] lsn=${telemetry.walLsn} crc32c=0x${crc.toString(16).padStart(8, '0')} rtt=${telemetry.rttMs}ms status=SYNC_OK`,
    }
  }
  if (telemetry.jobStatus === 'BLOCKED') {
    return {
      id: tick,
      tone: 'crit',
      text: `${isoTimeStr} [${telemetry.daemonUnit}] ERR ring_stall code=EAGAIN_RESOURCE queue_depth=${telemetry.queueDepth}`,
    }
  }
  if (telemetry.jobStatus === 'IDLE' || telemetry.jobStatus === 'PAUSED') {
    return {
      id: tick,
      tone: 'warn',
      text: `${isoTimeStr} [sched/idle] unit=${telemetry.daemonUnit} state=${telemetry.jobStatus} epoll_wait=500ms`,
    }
  }

  const templates = [
    () => {
      const blk = 1024 + Math.floor(pseudoNoise01(tick * 3.1) * 7168)
      return `${isoTimeStr} [${telemetry.daemonUnit}] batch_seq=${telemetry.completedCycles} io_pages=${blk} pipe=${(telemetry.jobProgressRatio * 100).toFixed(1)}%`
    },
    () => {
      const port = 41000 + Math.floor(pseudoNoise01(tick * 5.7) * 20000)
      return `${isoTimeStr} [netfilter/ebpf] xdp_pass iface=eth0 src=10.244.1.18:${port} tls=AEAD-GCM rtt=${telemetry.rttMs}ms`
    },
    () => {
      const pages = Math.floor(telemetry.memResidentRatio * 8192)
      return `${isoTimeStr} [mm/vmstat] nr_active_anon=${pages} slab_reclaimable=${Math.floor(telemetry.bufKernelRatio * 4096)} dirty_pages=12`
    },
    () => {
      const irq = 200 + Math.floor(pseudoNoise01(tick * 9.9) * 650)
      return `${isoTimeStr} [kernel/sched] cfs_slice=4ms load=${telemetry.loadAvg[0]} softirq_net_rx=${irq}/s peers=${telemetry.activePeers}`
    },
    () => {
      const txId = (0x800000 + tick * 13) >>> 0
      return `${isoTimeStr} [kubelet/pleg] pod_sync container_id=c8f29a1e tx=0x${txId.toString(16)} probe=LIVENESS_PASS`
    },
  ]
  const pick = templates[Math.abs(tick) % templates.length]!
  return {
    id: tick,
    tone: 'info',
    text: pick(),
  }
}
