import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useGameStore } from '../game/gameStore'
import {
  buildHexDumpRows,
  deriveTerminalTelemetry,
   generateDaemonLogLine,
  HACKER_TYPER_COMMANDS,
  pseudoNoise01,
  renderAsciiBar,
  renderBrailleSparkline,
  type TerminalHealthTone,
} from '../game/terminalIdleTelemetry'
import { gameClient } from '../network/gameClient'
import { useUiStore } from '../uiStore'

type InspectorTab = 'inspector' | 'hexdump' | 'sockets' | 'procs'

interface LogEntry {
  id: number
  tone: TerminalHealthTone
  text: string
}

const MAX_LOG_ENTRIES = 26
const SPARK_HISTORY_LEN = 56

function formatUtcClock(ms: number): string {
  const d = new Date(ms)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}T${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}Z`
}

function formatShortIsoTime(ms: number): string {
  const d = new Date(ms)
  const pad = (n: number, len = 2) => String(n).padStart(len, '0')
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`
}

function formatUptimeSeconds(totalSec: number): string {
  const safe = Math.max(0, Math.floor(totalSec))
  const hh = String(Math.floor(safe / 3600)).padStart(2, '0')
  const mm = String(Math.floor((safe % 3600) / 60)).padStart(2, '0')
  const ss = String(safe % 60).padStart(2, '0')
  return `${hh}:${mm}:${ss}`
}

/**
 * 纯字符终端挂机伪装遮罩组件 (TerminalIdleOverlay)
 *
 * 严格约束：
 * 1. 零图像元素（无 img / svg / canvas / Emoji），纯 ASCII 与 Unicode 制表/块/盲文点阵字符；
 * 2. 零中文与游戏术语暴露，同步伪装浏览器标签标题与置空图标，退出时完整还原；
 * 3. 暗中映射真实行动队列状态/单轮进度、HP/MP 占比与 WebSocket 延迟，仅点击右上角按钮退出。
 */
export const TerminalIdleOverlay = memo(function TerminalIdleOverlay(): React.JSX.Element {
  const setIdleModeOpen = useUiStore(state => state.setIdleModeOpen)

  const connection = useGameStore(state => state.connection)
  const latencyMs = useGameStore(state => state.latencyMs)
  const onlineCount = useGameStore(state => state.onlineCount)
  const offlineIdleCount = useGameStore(state => state.offlineIdleCount)
  const queue = useGameStore(state => state.queue)
  const combatResources = useGameStore(state => state.combatResources)
  const version = useGameStore(state => state.version)
  const sequenceId = useGameStore(state => state.sequenceId)
  const lastQueueSyncPerformanceTime = useGameStore(
    state => state.lastQueueSyncPerformanceTime || state.lastSyncPerformanceTime,
  )

  const [inspectorTab, setInspectorTab] = useState<InspectorTab>('inspector')
  const [tick, setTick] = useState(1)
  const [nowMs, setNowMs] = useState(() => Date.now())
  const [perfNow, setPerfNow] = useState(() => (typeof performance !== 'undefined' ? performance.now() : 0))
  const [rxSamples, setRxSamples] = useState<readonly number[]>(() =>
    Array.from({ length: SPARK_HISTORY_LEN }, (_, idx) => 0.25 + pseudoNoise01(idx * 1.7) * 0.45),
  )
  const [opsSamples, setOpsSamples] = useState<readonly number[]>(() =>
    Array.from({ length: SPARK_HISTORY_LEN }, (_, idx) => 0.3 + pseudoNoise01(idx * 2.3 + 9) * 0.5),
  )
  const [logs, setLogs] = useState<readonly LogEntry[]>(() => {
    const bootMs = Date.now() - 2400
    return [
      { id: -4, tone: 'info', text: `${formatShortIsoTime(bootMs)} [systemd/init] Attached session tty1 -> /srv/cluster-ctl (TLSv1.3 AEAD-AES256-GCM)` },
      { id: -3, tone: 'ok', text: `${formatShortIsoTime(bootMs + 600)} [cgroup2/init] memory.high=32.0GiB cpu.weight=100 io.bfq.weight=100` },
      { id: -2, tone: 'info', text: `${formatShortIsoTime(bootMs + 1200)} [netfilter/ebpf] Loaded xdp_ingress_filter prog_id=418 jit_len=3904` },
      { id: -1, tone: 'ok', text: `${formatShortIsoTime(bootMs + 1800)} [supervisor] Cluster telemetry stream online; awaiting ring checkpoints` },
    ]
  })
  const [typedCommand, setTypedCommand] = useState('')

  const mountWallMsRef = useRef<number>(Date.now())
  const prevVersionRef = useRef<number>(version)
  const logSeqRef = useRef<number>(1)
  const cmdPoolIndexRef = useRef<number>(0)
  const logContainerRef = useRef<HTMLDivElement>(null)

  const elapsedSeconds = useMemo(() => {
    if (lastQueueSyncPerformanceTime <= 0 || perfNow <= 0) return 0
    return Math.max(0, (perfNow - lastQueueSyncPerformanceTime) / 1000)
  }, [lastQueueSyncPerformanceTime, perfNow])

  const telemetry = useMemo(
    () =>
      deriveTerminalTelemetry({
        connection,
        latencyMs,
        onlineCount,
        offlineIdleCount,
        queue,
        combatResources,
        version,
        sequenceId,
        elapsedSeconds,
        tick,
      }),
    [combatResources, connection, elapsedSeconds, latencyMs, offlineIdleCount, onlineCount, queue, sequenceId, tick, version],
  )

  const telemetryRef = useRef(telemetry)
  telemetryRef.current = telemetry

  // 伪装浏览器标签标题与置空图标，卸载时无损还原
  useEffect(() => {
    if (typeof document === 'undefined') return undefined
    const previousTitle = document.title
    const iconEl = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
    const previousIconHref = iconEl?.getAttribute('href') ?? null

    document.title = `root@${telemetryRef.current.hostTag}: /srv/cluster-ctl`
    if (iconEl) {
      iconEl.setAttribute('href', 'data:,')
    }

    return () => {
      document.title = previousTitle
      if (iconEl && previousIconHref !== null) {
        iconEl.setAttribute('href', previousIconHref)
      }
    }
  }, [])

  // 固定节拍推进波形图与守护进程滚动流（后台页签暂停计算以节约 CPU）
  useEffect(() => {
    const timer = window.setInterval(() => {
      if (typeof document !== 'undefined' && document.visibilityState !== 'visible') {
        return
      }
      const wall = Date.now()
      const pNow = typeof performance !== 'undefined' ? performance.now() : 0
      setNowMs(wall)
      setPerfNow(pNow)
      setTick(prev => {
        const nextTick = prev + 1
        const snap = telemetryRef.current
        const rxSample = snap.netLink === 'ESTABLISHED'
          ? 0.22 + pseudoNoise01(nextTick * 1.91) * (snap.jobStatus === 'RUN' ? 0.72 : 0.28)
          : 0.04
        const opsSample = snap.jobStatus === 'RUN'
          ? 0.35 + pseudoNoise01(nextTick * 2.47 + 17) * 0.62
          : 0.08 + pseudoNoise01(nextTick * 1.13) * 0.12

        setRxSamples(buf => [...buf.slice(-(SPARK_HISTORY_LEN - 1)), rxSample])
        setOpsSamples(buf => [...buf.slice(-(SPARK_HISTORY_LEN - 1)), opsSample])

        const verChanged = prevVersionRef.current !== version
        prevVersionRef.current = version

        if (verChanged || nextTick % 2 === 0) {
          const line = generateDaemonLogLine(
            logSeqRef.current++,
            snap,
            formatShortIsoTime(wall),
            verChanged,
          )
          setLogs(prevLogs => [...prevLogs.slice(-(MAX_LOG_ENTRIES - 1)), line])
        }
        return nextTick
      })
    }, 450)
    return () => window.clearInterval(timer)
  }, [version])

  // 日志追加后自动贴底
  useEffect(() => {
    const el = logContainerRef.current
    if (el) {
      el.scrollTop = el.scrollHeight
    }
  }, [logs])

  // 黑客打字机交互（Hacker-Typer）：随手敲击键盘即可在底部终端逐段打出真实运维排查指令，回车输出诊断表
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.altKey || event.metaKey) return
      if (event.key === 'Escape' || event.key.startsWith('F')) return

      const currentEntry = HACKER_TYPER_COMMANDS[cmdPoolIndexRef.current % HACKER_TYPER_COMMANDS.length]!

      if (event.key === 'Backspace') {
        event.preventDefault()
        setTypedCommand(prev => prev.slice(0, -1))
        return
      }

      if (event.key === 'Enter') {
        event.preventDefault()
        const executed = typedCommand.trim() || currentEntry.cmd
        const stamp = formatShortIsoTime(Date.now())
        const appended: LogEntry[] = [
          {
            id: logSeqRef.current++,
            tone: 'bright' as unknown as TerminalHealthTone,
            text: `root@${telemetryRef.current.hostTag}:/srv/cluster-ctl# ${executed}`,
          },
          ...currentEntry.output.map(outLine => ({
            id: logSeqRef.current++,
            tone: 'ok' as TerminalHealthTone,
            text: `  ${outLine}`,
          })),
          {
            id: logSeqRef.current++,
            tone: 'info' as TerminalHealthTone,
            text: `${stamp} [cli/audit] exit_code=0 elapsed=${4 + (cmdPoolIndexRef.current % 9)}ms`,
          },
        ]
        setLogs(prev => [...prev, ...appended].slice(-MAX_LOG_ENTRIES))
        setTypedCommand('')
        cmdPoolIndexRef.current += 1
        return
      }

      if (event.key.length === 1) {
        event.preventDefault()
        setTypedCommand(prev => {
          if (prev.length >= currentEntry.cmd.length) {
            return currentEntry.cmd
          }
          const step = 3 + (prev.length % 3)
          return currentEntry.cmd.slice(0, prev.length + step)
        })
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [typedCommand])

  const handleExit = useCallback(() => {
    setIdleModeOpen(false)
  }, [setIdleModeOpen])

  const handleReconnect = useCallback(() => {
    gameClient.connect()
  }, [])

  const sessionUptime = formatUptimeSeconds((nowMs - mountWallMsRef.current) / 1000)
  const rxSparkline = useMemo(() => renderBrailleSparkline(rxSamples, 28), [rxSamples])
  const opsSparkline = useMemo(() => renderBrailleSparkline(opsSamples, 28), [opsSamples])
  const hexRows = useMemo(
    () => buildHexDumpRows(0x41a0_0800, version, tick, inspectorTab === 'hexdump' ? 12 : 6),
    [inspectorTab, tick, version],
  )

  const netArrow = telemetry.netLink === 'ESTABLISHED' ? '═══>' : telemetry.netLink === 'SYN_RETRANS' ? '─?─>' : '─X─ '
  const meshDiagram = [
    `[EDGE-GW0]══╦${netArrow}[SHARD-CORE0]──(${telemetry.netLink.padEnd(11, ' ')} ${String(telemetry.rttMs).padStart(3, ' ')}ms)`,
    `            ╠${netArrow}[PG-WAL-SYNC]──(LSN ${telemetry.walLsn.padEnd(14, ' ')})`,
    `            ╚${netArrow}[WORKER-POOL]──(ACTV:${String(telemetry.activePeers).padStart(4, '0')} STBY:${String(telemetry.standbyNodes).padStart(4, '0')})`,
  ].join('\n')

  const procRows = useMemo(
    () => [
      {
        pid: 1408,
        user: 'root',
        res: `${telemetry.memUsedGiB}G`,
        state: telemetry.jobStatus === 'RUN' ? 'R' : 'S',
        cpu: (telemetry.cpuAverage * 68).toFixed(1),
        mem: (telemetry.memResidentRatio * 48).toFixed(1),
        cmd: `/usr/sbin/${telemetry.daemonUnit} --ring-fd=16`,
        highlight: true,
      },
      {
        pid: 2194,
        user: 'postgres',
        res: `${telemetry.bufUsedGiB}G`,
        state: telemetry.netLink === 'ESTABLISHED' ? 'S' : 'D',
        cpu: (telemetry.cpuAverage * 21 + 1.4).toFixed(1),
        mem: (telemetry.bufKernelRatio * 28).toFixed(1),
        cmd: `postgres: walwriter ${telemetry.walLsn}`,
        highlight: false,
      },
      {
        pid: 1852,
        user: 'envoy',
        res: '0.8G',
        state: 'S',
        cpu: (2.1 + pseudoNoise01(tick * 3.3) * 4.5).toFixed(1),
        mem: '2.5',
        cmd: 'envoy -c /etc/envoy/edge-tls13.yaml --concurrency 4',
        highlight: false,
      },
      {
        pid: 912,
        user: 'nobody',
        res: '0.2G',
        state: 'S',
        cpu: (0.6 + pseudoNoise01(tick * 1.9) * 1.2).toFixed(1),
        mem: '0.6',
        cmd: 'node_exporter --collector.systemd --collector.netdev',
        highlight: false,
      },
      {
        pid: 604,
        user: 'root',
        res: '0.4G',
        state: 'S',
        cpu: '0.4',
        mem: '1.2',
        cmd: 'systemd-journald --flush-interval=10s',
        highlight: false,
      },
    ],
    [telemetry, tick],
  )

  const socketRows = useMemo(
    () => [
      { proto: 'tcp6', recvQ: 0, sendQ: telemetry.netLink === 'ESTABLISHED' ? 0 : 128, local: '10.16.0.4:443', peer: '10.16.8.91:51842', state: telemetry.netLink },
      { proto: 'tcp4', recvQ: 0, sendQ: 0, local: '10.16.0.4:5432', peer: '10.16.0.12:39210', state: 'ESTABLISHED' },
      { proto: 'tcp4', recvQ: 0, sendQ: 0, local: '127.0.0.1:9100', peer: '127.0.0.1:48104', state: 'ESTABLISHED' },
      { proto: 'udp4', recvQ: 0, sendQ: 0, local: '10.16.0.4:51820', peer: '10.16.2.01:51820', state: 'WG_TUNNEL' },
      { proto: 'unix', recvQ: 0, sendQ: 0, local: '/run/systemd/journal/socket', peer: '*:0', state: 'STREAM_OK' },
    ],
    [telemetry.netLink],
  )

  return (
    <div className="term-idle-root" role="region" aria-label="Cluster administration terminal">
      <header className="term-idle-topbar">
        <div className="term-idle-topbar-left">
          <span className="term-idle-session-badge">[tmux:0]</span>
          <span className="term-tone-bright">root@{telemetry.hostTag}:/srv/cluster-ctl</span>
          <nav className="term-idle-tmux-tabs" aria-label="Terminal panes">
            {(
              [
                { id: 'inspector', label: '0:overview' },
                { id: 'hexdump', label: '1:mem-hex' },
                { id: 'sockets', label: '2:net-sock' },
                { id: 'procs', label: '3:proc-top' },
              ] as const
            ).map(tab => (
              <button
                key={tab.id}
                type="button"
                className={`term-idle-tmux-btn${inspectorTab === tab.id ? ' is-active' : ''}`}
                onClick={() => setInspectorTab(tab.id)}
              >
                [{tab.label}{inspectorTab === tab.id ? '*' : ' '}]
              </button>
            ))}
          </nav>
        </div>

        <div className="term-idle-topbar-center">
          <span className="term-tone-dim">LSN:</span>
          <span className="term-tone-info">{telemetry.walLsn}</span>
          <span className="term-tone-dim">|</span>
          <span className="term-tone-dim">UP:</span>
          <span>{sessionUptime}</span>
          <span className="term-tone-dim">|</span>
          <span>{formatUtcClock(nowMs)}</span>
        </div>

        <div className="term-idle-topbar-right">
          {connection === 'offline' && (
            <button
              type="button"
              className="term-idle-action-btn term-idle-action-btn--warn"
              onClick={handleReconnect}
              aria-label="Reconnect session"
            >
              [ ! RECONNECT ]
            </button>
          )}
          <button
            type="button"
            className="term-idle-action-btn"
            onClick={handleExit}
            aria-label="Disconnect terminal session"
          >
            [ ^C / DISCONNECT ]
          </button>
        </div>
      </header>

      <div className="term-idle-grid">
        {/* 窗格 01：多核 CPU 利用率与 Braille 实时点阵波形 */}
        <section className="term-idle-pane term-idle-pane--cpu" aria-label="CPU and I/O telemetry">
          <div className="term-idle-pane-header">
            <span>+--[ 01 // CPU_CORES &amp; THROUGHPUT ]</span>
            <span className="term-tone-ok">AVG {(telemetry.cpuAverage * 100).toFixed(1)}%</span>
          </div>
          <div className="term-idle-pane-body">
            <div className="term-cpu-grid">
              {telemetry.cpuCores.map((val, idx) => (
                <div key={idx} className="term-kv-row">
                  <span className="term-tone-dim">CPU{idx}</span>
                  <span className={val > 0.78 ? 'term-tone-warn' : 'term-tone-ok'}>
                    [{renderAsciiBar(val, 10)}] {(val * 100).toFixed(0).padStart(3, ' ')}%
                  </span>
                </div>
              ))}
            </div>
            <div className="term-divider">------------------------------------------------------------</div>
            <div className="term-kv-row">
              <span className="term-tone-dim">NET_RX_BPS</span>
              <span className="term-tone-info">{rxSparkline}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-tone-dim">RING_IOPS </span>
              <span className="term-tone-ok">{opsSparkline}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-tone-dim">LOAD_AVG  </span>
              <span className="term-tone-bright">
                {telemetry.loadAvg[0]}  {telemetry.loadAvg[1]}  {telemetry.loadAvg[2]}  (x86_64 SMP)
              </span>
            </div>
          </div>
        </section>

        {/* 窗格 02：任务调度管道（暗中映射当前行动队列运行态与实时单轮进度） */}
        <section className="term-idle-pane term-idle-pane--job" aria-label="Worker scheduler pipeline">
          <div className="term-idle-pane-header">
            <span>+--[ 02 // JOB_SCHEDULER &amp; WORKER_PIPE ]</span>
            <span className={`term-badge term-badge--${telemetry.jobTone}`}>[{telemetry.jobStatus}]</span>
          </div>
          <div className="term-idle-pane-body">
            <div className="term-kv-row">
              <span className="term-tone-dim">UNIT_SVC</span>
              <span className="term-tone-bright">{telemetry.daemonUnit}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-tone-dim">PIPE_PRG</span>
              <span className={`term-tone-${telemetry.jobTone}`}>
                [{renderAsciiBar(telemetry.jobProgressRatio, 20)}] {(telemetry.jobProgressRatio * 100).toFixed(1).padStart(5, ' ')}%
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-tone-dim">Q_DEPTH </span>
              <span>
                {String(telemetry.queueDepth).padStart(2, '0')} ACTIVE  |  CYCLES: {String(telemetry.completedCycles).padStart(6, '0')}
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-tone-dim">POLICY  </span>
              <span className="term-tone-info">{telemetry.schedulePolicy}  |  EPOLL_ET</span>
            </div>
            <div className="term-divider">------------------------------------------------------------</div>
            <pre className="term-pre-block">
              {[0, 1, 2].map(slot => {
                const slotActive = slot < telemetry.queueDepth && telemetry.jobStatus === 'RUN'
                const tag = slot === 0 ? telemetry.jobStatus.padEnd(7, ' ') : slot < telemetry.queueDepth ? 'STAGED ' : 'STANDBY'
                const fd = 16 + slot * 4
                return `RING#${slot}  fd=${fd}  state=${tag}  irq=${slotActive ? 'OK  ' : 'IDLE'}\n`
              }).join('').trimEnd()}
            </pre>
          </div>
        </section>

        {/* 窗格 03：内存与缓冲池 + 集群网格拓扑（暗中映射 HP / MP 百分比与网络连接态） */}
        <section className="term-idle-pane term-idle-pane--mem" aria-label="Memory allocation and cluster mesh">
          <div className="term-idle-pane-header">
            <span>+--[ 03 // MEM_ALLOC &amp; CLUSTER_MESH ]</span>
            <span className={`term-tone-${telemetry.netTone}`}>{telemetry.netLink}</span>
          </div>
          <div className="term-idle-pane-body">
            <div className="term-kv-row">
              <span className="term-tone-dim">MEM_RES </span>
              <span className={`term-tone-${telemetry.memTone}`}>
                [{renderAsciiBar(telemetry.memResidentRatio, 16)}] {(telemetry.memResidentRatio * 100).toFixed(0).padStart(3, ' ')}% ({telemetry.memUsedGiB}/{telemetry.memTotalGiB}G)
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-tone-dim">BUF_KRN </span>
              <span className={`term-tone-${telemetry.bufTone}`}>
                [{renderAsciiBar(telemetry.bufKernelRatio, 16)}] {(telemetry.bufKernelRatio * 100).toFixed(0).padStart(3, ' ')}% ({telemetry.bufUsedGiB}/{telemetry.bufTotalGiB}G)
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-tone-dim">SWP_BLK </span>
              <span className="term-tone-dim">
                [{renderAsciiBar(telemetry.swapRatio, 16)}] {(telemetry.swapRatio * 100).toFixed(0).padStart(3, ' ')}% (0.7/16.0G)
              </span>
            </div>
            <div className="term-divider">------------------------------------------------------------</div>
            <pre className={`term-pre-block term-tone-${telemetry.netTone}`}>{meshDiagram}</pre>
          </div>
        </section>

        {/* 窗格 04：实时守护进程日志流与 Hacker-Typer 交互命令行 */}
        <section className="term-idle-pane term-idle-pane--stream" aria-label="System journal stream">
          <div className="term-idle-pane-header">
            <span>+--[ 04 // JOURNALD_STREAM &amp; TTY_CONSOLE ]</span>
            <span className="term-tone-dim">/var/log/cluster-supervisor.log [FOLLOW]</span>
          </div>
          <div className="term-idle-pane-body">
            <div className="term-log-list" ref={logContainerRef}>
              {logs.map(entry => (
                <div key={entry.id} className={`term-log-line term-tone-${entry.tone}`}>
                  {entry.text}
                </div>
              ))}
            </div>
            <div className="term-prompt-bar">
              <span className="term-tone-ok">root@{telemetry.hostTag}</span>
              <span className="term-tone-dim">:</span>
              <span className="term-tone-info">/srv/cluster-ctl</span>
              <span className="term-tone-dim"># </span>
              <span>{typedCommand}</span>
              <span className="term-cursor" aria-hidden="true">█</span>
            </div>
          </div>
        </section>

        {/* 窗格 05：多功能内存页十六进制检视与进程/套接字表 */}
        <section className="term-idle-pane term-idle-pane--inspector" aria-label="Memory and process inspector">
          <div className="term-idle-pane-header">
            <span>+--[ 05 // {inspectorTab.toUpperCase()}_INSPECTOR ]</span>
            <span className="term-tone-dim">PAGE_ALIGN=4096B</span>
          </div>
          <div className="term-idle-pane-body">
            {(inspectorTab === 'inspector' || inspectorTab === 'procs') && (
              <table className="term-table">
                <thead>
                  <tr>
                    <th>PID</th>
                    <th>USER</th>
                    <th>RES</th>
                    <th>S</th>
                    <th>CPU%</th>
                    <th>MEM%</th>
                    <th>COMMAND</th>
                  </tr>
                </thead>
                <tbody>
                  {procRows.map(row => (
                    <tr key={row.pid} className={row.highlight ? 'is-highlight' : undefined}>
                      <td>{row.pid}</td>
                      <td>{row.user}</td>
                      <td>{row.res}</td>
                      <td>{row.state}</td>
                      <td>{row.cpu}</td>
                      <td>{row.mem}</td>
                      <td>{row.cmd}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            {inspectorTab === 'sockets' && (
              <table className="term-table">
                <thead>
                  <tr>
                    <th>PROTO</th>
                    <th>RECV-Q</th>
                    <th>SEND-Q</th>
                    <th>LOCAL_ADDR</th>
                    <th>PEER_ADDR</th>
                    <th>STATE</th>
                  </tr>
                </thead>
                <tbody>
                  {socketRows.map((sock, idx) => (
                    <tr key={idx} className={idx === 0 ? 'is-highlight' : undefined}>
                      <td>{sock.proto}</td>
                      <td>{sock.recvQ}</td>
                      <td>{sock.sendQ}</td>
                      <td>{sock.local}</td>
                      <td>{sock.peer}</td>
                      <td>{sock.state}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            {inspectorTab === 'inspector' && (
              <div className="term-divider">------------------------------------------------------------</div>
            )}

            {(inspectorTab === 'inspector' || inspectorTab === 'hexdump') && (
              <pre className="term-pre-block term-tone-dim">
                {hexRows.join('\n')}
              </pre>
            )}
          </div>
        </section>
      </div>

      <footer className="term-idle-statusbar">
        <span>TTY1 | UTF-8 | LF | KERNEL 6.8.0-generic | TLSv1.3-GCM | RTT {telemetry.rttMs}ms</span>
        <span>TYPE KEYS + [ENTER] FOR CLI EXEC  //  CLICK [^C / DISCONNECT] TO EXIT</span>
      </footer>
    </div>
  )
})

export default TerminalIdleOverlay
