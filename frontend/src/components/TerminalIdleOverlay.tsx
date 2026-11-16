import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  calculateRealmInfo,
  calculateRealmProgress,
  calculateTechniqueProgress,
  serverExpToNextCap,
  TECHNIQUE_IDS,
  TECHNIQUE_SPEED_RATIO_PER_LEVEL,
} from '../game/combatConstants'
import { formatNumber } from '../game/format'
import { useGameStore } from '../game/gameStore'
import {
  autoUseBarFor,
  autoUseBarLabel,
  findActionInCatalog,
  findEquipmentInCatalog,
  findItemInCatalog,
  localizeActionDisplayText,
  resolveActionDefinition,
} from '../game/ids'
import {
  calculateQueueActionEstimate,
  formatQueueRepeatsText,
  resolveLiveRemainingSeconds,
} from '../game/queueTimeEstimate'
import { calculateBriefStats } from '../game/statsCalculations'
import {
  buildHexDumpRows,
  clamp01,
  deriveTerminalTelemetry,
  formatPlainLogSegments,
  pseudoNoise01,
  renderAsciiBar,
  renderBrailleSparkline,
  type TerminalHealthTone,
  type TerminalJobStatus,
} from '../game/terminalIdleTelemetry'
import type { ActionDefinition, QueueEntry, WorldMapDefinition, WorldState } from '../game/types'
import { useT } from '../i18n'
import { gameLogService, type GameLogEntry, type LogChannel } from '../logging/gameLogService'
import { gameClient } from '../network/gameClient'
import { useUiStore } from '../uiStore'

type InspectorTab = 'queue' | 'yield' | 'skills' | 'hex'
type LogFilter = 'all' | LogChannel

interface CliEntry {
  id: string
  timestampMs: number
  tone: TerminalHealthTone
  text: string
}

const SPARK_HISTORY_LEN = 56
const MAX_CLI_LOGS = 24
const MAX_MERGED_LOGS = 60

function formatClockHms(ms: number): string {
  const d = new Date(ms)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

function formatUptimeSeconds(totalSec: number): string {
  const safe = Math.max(0, Math.floor(totalSec))
  const hh = String(Math.floor(safe / 3600)).padStart(2, '0')
  const mm = String(Math.floor((safe % 3600) / 60)).padStart(2, '0')
  const ss = String(safe % 60).padStart(2, '0')
  return `${hh}:${mm}:${ss}`
}

/** 解析队列行动的完整中文显示名称（含地点替换、强化等级后缀、讨伐难度标签） */
function formatEntryDisplayName(
  entry: QueueEntry,
  actions: readonly ActionDefinition[],
  items: ReturnType<typeof useGameStore.getState>['items'],
  equipment: ReturnType<typeof useGameStore.getState>['equipment'],
  world: WorldState | undefined,
  maps: readonly WorldMapDefinition[],
  difficultyLabel: (n: number) => string,
): string {
  const rawName = (entry.actionName && entry.actionName !== entry.actionId)
    ? entry.actionName
    : (findActionInCatalog(actions, entry.actionId) ?? resolveActionDefinition(entry.actionId))?.name
      || entry.actionName
      || entry.actionId
  let display = world ? localizeActionDisplayText(rawName, world, maps) : rawName
  if (entry.targetItemId) {
    const targetName = findEquipmentInCatalog(equipment, entry.targetItemId)?.name
      ?? findItemInCatalog(items, entry.targetItemId)?.name
      ?? entry.targetItemId
    const curPart = entry.enhancementCurrentLevel != null && entry.enhancementCurrentLevel > 0
      ? ` +${Math.floor(entry.enhancementCurrentLevel)}`
      : ''
    const tgtPart = entry.targetLevel != null && entry.targetLevel > 0
      ? ` -> +${Math.floor(entry.targetLevel)}`
      : ''
    display = `${display} · ${targetName}${curPart}${tgtPart}`
  }
  if (entry.difficulty > 0) {
    display = `${display} [${difficultyLabel(entry.difficulty)}]`
  }
  return display
}

/**
 * 纯字符中文终端挂机模式组件 (TerminalIdleOverlay)
 *
 * 严格约束：
 * 1. 零图像元素（无 img / svg / canvas / Emoji），纯等宽字符框线、块状进度条与盲文点阵波形；
 * 2. 全面使用清晰的中文指标展示玩家真实挂机数据，所有 UI 词条经 i18n 闭环；
 * 3. 仅铺满网页视口，仅通过点击右上角退出按钮返回常规游戏界面。
 */
export const TerminalIdleOverlay = memo(function TerminalIdleOverlay(): React.JSX.Element {
  const t = useT()
  const setIdleModeOpen = useUiStore(state => state.setIdleModeOpen)

  const name = useGameStore(state => state.name)
  const sequenceId = useGameStore(state => state.sequenceId)
  const connection = useGameStore(state => state.connection)
  const latencyMs = useGameStore(state => state.latencyMs)
  const onlineCount = useGameStore(state => state.onlineCount)
  const offlineIdleCount = useGameStore(state => state.offlineIdleCount)
  const version = useGameStore(state => state.version)
  const serverTimeUnixSeconds = useGameStore(state => state.serverTimeUnixSeconds)
  const lastSyncPerformanceTime = useGameStore(state => state.lastSyncPerformanceTime)
  const lastQueueSyncPerformanceTime = useGameStore(
    state => state.lastQueueSyncPerformanceTime || state.lastSyncPerformanceTime,
  )

  const isPendingBreakthrough = useGameStore(state => state.isPendingBreakthrough)
  const stats = useGameStore(state => state.stats)
  const queue = useGameStore(state => state.queue)
  const inventory = useGameStore(state => state.inventory)
  const combat = useGameStore(state => state.combat)
  const combatResources = useGameStore(state => state.combatResources)
  const world = useGameStore(state => state.world)
  const duel = useGameStore(state => state.duel)
  const actions = useGameStore(state => state.actions)
  const items = useGameStore(state => state.items)
  const equipment = useGameStore(state => state.equipment)
  const maps = useGameStore(state => state.maps)
  const marketStats = useGameStore(state => state.marketStats)

  const [inspectorTab, setInspectorTab] = useState<InspectorTab>('queue')
  const [logFilter, setLogFilter] = useState<LogFilter>('all')
  const [tick, setTick] = useState(1)
  const [nowMs, setNowMs] = useState(() => Date.now())
  const [perfNow, setPerfNow] = useState(() => (typeof performance !== 'undefined' ? performance.now() : 0))
  const [rxSamples, setRxSamples] = useState<readonly number[]>(() =>
    Array.from({ length: SPARK_HISTORY_LEN }, (_, idx) => 0.25 + pseudoNoise01(idx * 1.7) * 0.45),
  )
  const [opsSamples, setOpsSamples] = useState<readonly number[]>(() =>
    Array.from({ length: SPARK_HISTORY_LEN }, (_, idx) => 0.3 + pseudoNoise01(idx * 2.3 + 9) * 0.5),
  )
  const [gameLogs, setGameLogs] = useState<readonly GameLogEntry[]>(() => gameLogService.getSnapshot().latest)
  const [cliLogs, setCliLogs] = useState<readonly CliEntry[]>([])
  const [typedCommand, setTypedCommand] = useState('')

  const mountWallMsRef = useRef<number>(Date.now())
  const baseTitleRef = useRef<string>('')
  const cliSeqRef = useRef<number>(1)
  const cmdPoolIndexRef = useRef<number>(0)
  const logContainerRef = useRef<HTMLDivElement>(null)

  const headEntry = queue.entries[0]
  const expedition = headEntry?.expedition ?? null

  const elapsedQueueSec = useMemo(() => {
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
        expedition,
        version,
        elapsedSeconds: elapsedQueueSec,
        tick,
      }),
    [combatResources, connection, elapsedQueueSec, expedition, latencyMs, offlineIdleCount, onlineCount, queue, tick, version],
  )

  const expToNextCap = serverExpToNextCap(stats?.expToNextLinggen, stats?.realmLevel)
  const realmProgress = useMemo(
    () => calculateRealmProgress(stats?.experience ?? 0, stats?.realmLevel, expToNextCap),
    [expToNextCap, stats?.experience, stats?.realmLevel],
  )
  const realmInfo = useMemo(
    () => calculateRealmInfo(stats?.experience ?? 0, stats?.realmLevel, expToNextCap),
    [expToNextCap, stats?.experience, stats?.realmLevel],
  )
  const realmRatio = clamp01((realmProgress.progressPercentage ?? 0) / 100)

  const spiritStones = useMemo(
    () => inventory.find(i => i.itemId === 'currency.spirit-stone' && !i.variant)?.amount ?? 0,
    [inventory],
  )
  const totalMerit = useMemo(() => {
    const bound = inventory.find(i => i.itemId === 'item.gongde-bound' && !i.variant)?.amount ?? 0
    const normal = inventory.find(i => i.itemId === 'item.gongde' && !i.variant)?.amount ?? 0
    return bound + normal
  }, [inventory])
  const foundation = stats?.foundation ?? 0

  const headActionDef = useMemo(
    () => (headEntry ? findActionInCatalog(actions, headEntry.actionId) : undefined),
    [actions, headEntry],
  )

  const headEstimate = useMemo(() => {
    if (!headEntry) return null
    return calculateQueueActionEstimate({
      entry: headEntry,
      action: headActionDef,
      inventory,
      combatEquipment: combat?.equipment ?? [],
      queueEntries: queue.entries,
      equipmentCatalog: equipment,
      stats: {
        foundation,
        experience: stats?.experience ?? 0,
        realmLevel: stats?.realmLevel,
        expToNextLinggen: stats?.expToNextLinggen,
      },
    })
  }, [
    combat?.equipment,
    equipment,
    foundation,
    headActionDef,
    headEntry,
    inventory,
    queue.entries,
    stats?.expToNextLinggen,
    stats?.experience,
    stats?.realmLevel,
  ])

  const estimatedRemainingText = useMemo(() => {
    if (!headEntry || !headEstimate) return t('app.idleTerminal.estimateNone')
    const liveSec = telemetry.jobStatus === 'RUN' ? elapsedQueueSec : 0
    const rem = resolveLiveRemainingSeconds(headEstimate, liveSec)
    return formatQueueRepeatsText(headEstimate, rem, t)
  }, [elapsedQueueSec, headEntry, headEstimate, t, telemetry.jobStatus])

  const estimatedServerNowSec = useMemo(() => {
    const base = serverTimeUnixSeconds > 0 ? serverTimeUnixSeconds : Math.floor(nowMs / 1000)
    if (lastSyncPerformanceTime <= 0 || perfNow <= 0) return base
    return base + Math.max(0, (perfNow - lastSyncPerformanceTime) / 1000)
  }, [lastSyncPerformanceTime, nowMs, perfNow, serverTimeUnixSeconds])

  const briefStats = useMemo(() => {
    const report = duel?.currentReport
    if (!report) return null
    return calculateBriefStats(report, estimatedServerNowSec, {
      items,
      marketStats,
      equipments: equipment,
    })
  }, [duel?.currentReport, equipment, estimatedServerNowSec, items, marketStats])

  const supplyInspection = useMemo(() => {
    const bar = headEntry
      ? autoUseBarFor({ type: headEntry.type, technique: headActionDef?.technique })
      : null
    if (!bar) {
      return { barLabel: t('app.idleTerminal.supplyBarNone'), rows: [] as Array<{ slotLabel: string; itemName: string; stock: number; enabled: boolean; activeSec: number }> }
    }
    const barLabel = autoUseBarLabel(bar)
    const rawSlots = queue.autoUseSlots?.[bar] ?? []
    const rows: Array<{ slotLabel: string; itemName: string; stock: number; enabled: boolean; activeSec: number }> = []
    rawSlots.forEach((slot, idx) => {
      if (!slot.itemId) return
      const itemDef = findItemInCatalog(items, slot.itemId)
      const itemName = itemDef?.name ?? slot.itemId
      const stock = inventory.find(inv => inv.itemId === slot.itemId && !inv.variant)?.amount ?? 0
      rows.push({
        slotLabel: `#${idx + 1}`,
        itemName,
        stock,
        enabled: slot.enabled,
        activeSec: Math.max(0, Math.round(slot.remainingDurationSeconds ?? 0)),
      })
    })
    return { barLabel, rows }
  }, [headActionDef?.technique, headEntry, inventory, items, queue.autoUseSlots, t])

  const resolveJobStatusLabel = useCallback(
    (status: TerminalJobStatus): string => {
      switch (status) {
        case 'RUN':
          return t('app.idleTerminal.statusRun')
        case 'PAUSED':
          return t('app.idleTerminal.statusPaused')
        case 'BLOCKED':
          return t('app.idleTerminal.statusBlocked')
        case 'QUEUED':
          return t('app.idleTerminal.statusQueued')
        case 'IDLE':
          return t('app.idleTerminal.statusIdle')
      }
    },
    [t],
  )

  const jobStatusLabel = resolveJobStatusLabel(telemetry.jobStatus)
  const headActionName = useMemo(() => {
    if (!headEntry) return t('app.idleTerminal.noActiveAction')
    return formatEntryDisplayName(
      headEntry,
      actions,
      items,
      equipment,
      world,
      maps,
      n => t('app.idleTerminal.difficultyBadge', { n }),
    )
  }, [actions, equipment, headEntry, items, maps, t, world])

  // 备份与还原浏览器标签页标题和空图标，并在标题动态提示当前挂机状态
  useEffect(() => {
    if (typeof document === 'undefined') return undefined
    const previousTitle = document.title
    baseTitleRef.current = previousTitle
    const iconEl = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
    const previousIconHref = iconEl?.getAttribute('href') ?? null
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

  useEffect(() => {
    if (typeof document === 'undefined' || !baseTitleRef.current) return
    document.title = t('app.idleTerminal.docTitlePrefix', {
      status: jobStatusLabel,
      name: baseTitleRef.current,
    })
  }, [jobStatusLabel, t])

  // 订阅真实游戏日志流（采集产出、制造、战斗击杀与掉落）
  useEffect(() => {
    setGameLogs(gameLogService.getSnapshot().latest)
    return gameLogService.subscribe(() => {
      setGameLogs(gameLogService.getSnapshot().latest)
    })
  }, [])

  // 节拍器：驱动动态波形与时间预估刷新（页面切后台时自动静默）
  const telemetryRef = useRef(telemetry)
  telemetryRef.current = telemetry
  useEffect(() => {
    const timer = window.setInterval(() => {
      if (typeof document !== 'undefined' && document.visibilityState !== 'visible') {
        return
      }
      setNowMs(Date.now())
      setPerfNow(typeof performance !== 'undefined' ? performance.now() : 0)
      setTick(prev => {
        const next = prev + 1
        const snap = telemetryRef.current
        const rx = snap.netState === 'online'
          ? 0.24 + pseudoNoise01(next * 1.91) * (snap.jobStatus === 'RUN' ? 0.72 : 0.24)
          : 0.04
        const ops = snap.jobStatus === 'RUN'
          ? 0.34 + pseudoNoise01(next * 2.47 + 17) * 0.62
          : 0.08 + pseudoNoise01(next * 1.13) * 0.1
        setRxSamples(buf => [...buf.slice(-(SPARK_HISTORY_LEN - 1)), rx])
        setOpsSamples(buf => [...buf.slice(-(SPARK_HISTORY_LEN - 1)), ops])
        return next
      })
    }, 500)
    return () => window.clearInterval(timer)
  }, [])

  const cliCommandTemplates = useMemo(
    () => [
      {
        cmd: t('app.idleTerminal.cliCmdStatus'),
        buildOutput: () =>
          t('app.idleTerminal.cliOutRole', {
            name,
            realm: realmInfo.minorName,
            expPct: (realmRatio * 100).toFixed(1),
            hp: `${formatNumber(telemetry.hpCurrent)}/${formatNumber(telemetry.hpMax)}`,
            mp: `${formatNumber(telemetry.mpCurrent)}/${formatNumber(telemetry.mpMax)}`,
            stones: formatNumber(spiritStones),
          }),
      },
      {
        cmd: t('app.idleTerminal.cliCmdQueue'),
        buildOutput: () =>
          t('app.idleTerminal.cliOutAction', {
            action: headActionName,
            status: jobStatusLabel,
            pct: (telemetry.jobProgressRatio * 100).toFixed(1),
            depth: telemetry.queueDepth,
            daily: formatNumber(briefStats?.dailyRevenue ?? 0),
          }),
      },
      {
        cmd: t('app.idleTerminal.cliCmdSupply'),
        buildOutput: () =>
          t('app.idleTerminal.cliOutSupply', {
            configured: supplyInspection.rows.length,
            empty: supplyInspection.rows.filter(r => r.stock <= 0).length,
            rtt: telemetry.rttMs,
          }),
      },
    ],
    [
      briefStats?.dailyRevenue,
      headActionName,
      jobStatusLabel,
      name,
      realmInfo.minorName,
      realmRatio,
      spiritStones,
      supplyInspection.rows,
      t,
      telemetry.hpCurrent,
      telemetry.hpMax,
      telemetry.jobProgressRatio,
      telemetry.mpCurrent,
      telemetry.mpMax,
      telemetry.queueDepth,
      telemetry.rttMs,
    ],
  )

  // 键盘快捷命令交互：随手敲击字符键即可输出中文挂机快照行
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.altKey || event.metaKey) return
      if (event.key === 'Escape' || event.key.startsWith('F')) return

      const tpl = cliCommandTemplates[cmdPoolIndexRef.current % cliCommandTemplates.length]!

      if (event.key === 'Backspace') {
        event.preventDefault()
        setTypedCommand(prev => prev.slice(0, -1))
        return
      }

      if (event.key === 'Enter') {
        event.preventDefault()
        const executed = typedCommand.trim() || tpl.cmd
        const now = Date.now()
        const nextEntries: CliEntry[] = [
          {
            id: `cli-cmd-${cliSeqRef.current++}`,
            timestampMs: now,
            tone: 'info',
            text: `$ ${executed}`,
          },
          {
            id: `cli-out-${cliSeqRef.current++}`,
            timestampMs: now + 1,
            tone: 'ok',
            text: tpl.buildOutput(),
          },
        ]
        setCliLogs(prev => [...prev, ...nextEntries].slice(-MAX_CLI_LOGS))
        setTypedCommand('')
        cmdPoolIndexRef.current += 1
        return
      }

      if (event.key.length === 1) {
        event.preventDefault()
        setTypedCommand(prev => {
          if (prev.length >= tpl.cmd.length) return tpl.cmd
          return tpl.cmd.slice(0, prev.length + 3)
        })
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [cliCommandTemplates, typedCommand])

  const mergedLogs = useMemo(() => {
    const filteredGame = logFilter === 'all'
      ? gameLogs
      : gameLogs.filter(entry => entry.channel === logFilter)

    const mappedGame = filteredGame.slice(-MAX_MERGED_LOGS).map(entry => {
      const tag = entry.channel === 'combat'
        ? t('app.idleTerminal.logTagCombat')
        : t('app.idleTerminal.logTagSystem')
      const body = formatPlainLogSegments(entry.segments)
      const countSuffix = entry.count > 1 ? ` (×${entry.count})` : ''
      return {
        id: entry.id,
        timestampMs: entry.latestTimestampMs || entry.timestampMs,
        tone: (entry.channel === 'combat' ? 'info' : 'ok') as TerminalHealthTone,
        text: `[${formatClockHms(entry.latestTimestampMs || entry.timestampMs)}] ${tag} ${body}${countSuffix}`,
      }
    })

    const mappedCli = cliLogs.map(entry => ({
      id: entry.id,
      timestampMs: entry.timestampMs,
      tone: entry.tone,
      text: `[${formatClockHms(entry.timestampMs)}] ${t('app.idleTerminal.logTagCli')} ${entry.text}`,
    }))

    return [...mappedGame, ...mappedCli]
      .sort((a, b) => a.timestampMs - b.timestampMs)
      .slice(-MAX_MERGED_LOGS)
  }, [cliLogs, gameLogs, logFilter, t])

  useEffect(() => {
    const el = logContainerRef.current
    if (el) {
      el.scrollTop = el.scrollHeight
    }
  }, [mergedLogs])

  const handleExit = useCallback(() => {
    setIdleModeOpen(false)
  }, [setIdleModeOpen])

  const handleReconnect = useCallback(() => {
    gameClient.connect()
  }, [])

  const netLabel = telemetry.netState === 'online'
    ? t('app.idleTerminal.netOnline')
    : telemetry.netState === 'reconnecting'
      ? t('app.idleTerminal.netReconnecting')
      : t('app.idleTerminal.netOffline')

  const expeditionPhaseText = useMemo(() => {
    if (headEntry?.executionKind === 'travel') {
      return t('app.idleTerminal.phaseTravel')
    }
    if (!expedition) return null
    switch (expedition.phase) {
      case 'initial-search':
      case 'search':
        return t('app.idleTerminal.phaseSearch')
      case 'combat':
        return t('app.idleTerminal.phaseCombat')
      case 'respawn':
        return t('app.idleTerminal.phaseRespawn')
      case 'duel-pending':
      case 'duel':
        return t('app.idleTerminal.phaseDuel')
    }
  }, [expedition, headEntry?.executionKind, t])

  const roundProgressRightText = useMemo(() => {
    if (!headEntry) return '0.0%'
    if (headEntry.durationSeconds > 0) {
      const curSec = Math.min(
        headEntry.durationSeconds,
        headEntry.progressSeconds + (telemetry.jobStatus === 'RUN' ? elapsedQueueSec : 0),
      )
      return `${(telemetry.jobProgressRatio * 100).toFixed(1)}% (${curSec.toFixed(1)}s/${headEntry.durationSeconds.toFixed(1)}s)`
    }
    if (expeditionPhaseText) {
      return expeditionPhaseText
    }
    return `${(telemetry.jobProgressRatio * 100).toFixed(0)}%`
  }, [elapsedQueueSec, expeditionPhaseText, headEntry, telemetry.jobProgressRatio, telemetry.jobStatus])

  const isCombatEntry = headEntry?.type === 'combat' || Boolean(expedition)
  const locationName = world?.locationName || t('app.idleTerminal.locationFallback')
  const cultivatorDisplay = `${name || '-'}${sequenceId > 0 ? `#${sequenceId}` : ''}`
  const rxSparkline = useMemo(() => renderBrailleSparkline(rxSamples, 24), [rxSamples])
  const opsSparkline = useMemo(() => renderBrailleSparkline(opsSamples, 24), [opsSamples])
  const hexRows = useMemo(() => buildHexDumpRows(0x41a0_0800, version, tick, 10), [tick, version])
  const sessionUptime = formatUptimeSeconds((nowMs - mountWallMsRef.current) / 1000)

  const techniqueRows = useMemo(
    () =>
      TECHNIQUE_IDS.map(techId => {
        const exp = combat?.techniqueExperiences?.[techId] ?? 0
        const prog = calculateTechniqueProgress(exp)
        const ratio = prog.expToNext > 0 ? clamp01(prog.currentLayerExp / prog.expToNext) : 1
        const speedPct = (prog.level * TECHNIQUE_SPEED_RATIO_PER_LEVEL * 100).toFixed(1)
        return {
          id: techId,
          name: t(`insight.technique.${techId}`),
          level: prog.level,
          ratio,
          speedPct,
        }
      }),
    [combat?.techniqueExperiences, t],
  )

  const netArrow = telemetry.netState === 'online' ? '═══>' : telemetry.netState === 'reconnecting' ? '─?─>' : '─X─ '
  const nodeMeshText = [
    `[${t('app.idleTerminal.meshClient')}]══╦${netArrow}[${t('app.idleTerminal.meshGateway')}: ${netLabel} ${telemetry.rttMs}ms]`,
    `          ╠${netArrow}[${t('app.idleTerminal.meshLocation')}: ${locationName}]`,
    `          ╚${netArrow}[${t('app.idleTerminal.meshCluster')}: ${t('app.idleTerminal.meshOnlineCount', { online: telemetry.onlinePlayers, idle: telemetry.offlineIdlePlayers })}]`,
  ].join('\n')

  const pane05Title = inspectorTab === 'queue'
    ? t('app.idleTerminal.pane05TitleQueue')
    : inspectorTab === 'yield'
      ? t('app.idleTerminal.pane05TitleYield')
      : inspectorTab === 'skills'
        ? t('app.idleTerminal.pane05TitleSkills')
        : t('app.idleTerminal.pane05TitleHex')

  return (
    <div className="term-idle-root" role="region" aria-label={t('app.idleTerminal.regionAria')}>
      <header className="term-idle-topbar">
        <div className="term-idle-topbar-left">
          <span className="term-idle-session-badge">[{t('app.idleTerminal.badge')}]</span>
          <span className="term-tone-bright">{cultivatorDisplay} @ {locationName}</span>
          <nav className="term-idle-tmux-tabs" aria-label={t('app.idleTerminal.panesAria')}>
            {(
              [
                { id: 'queue', label: t('app.idleTerminal.tabQueue') },
                { id: 'yield', label: t('app.idleTerminal.tabYield') },
                { id: 'skills', label: t('app.idleTerminal.tabSkills') },
                { id: 'hex', label: t('app.idleTerminal.tabHex') },
              ] as const
            ).map(tab => (
              <button
                key={tab.id}
                type="button"
                className={`term-idle-tmux-btn${inspectorTab === tab.id ? ' is-active' : ''}`}
                onClick={() => setInspectorTab(tab.id)}
              >
                [{tab.label}{inspectorTab === tab.id ? '*' : ''}]
              </button>
            ))}
          </nav>
        </div>

        <div className="term-idle-topbar-center">
          <span className="term-tone-dim">{t('app.idleTerminal.netLabel')}:</span>
          <span className={`term-tone-${telemetry.netTone}`}>
            {netLabel}{telemetry.netState === 'online' ? ` (${telemetry.rttMs}ms)` : ''}
          </span>
          <span className="term-tone-dim">|</span>
          <span>{t('app.idleTerminal.onlinePlayers', { n: telemetry.onlinePlayers })}</span>
          <span className="term-tone-dim">|</span>
          <span className="term-tone-dim">{t('app.idleTerminal.uptimeLabel')}:</span>
          <span>{sessionUptime}</span>
          <span className="term-tone-dim">|</span>
          <span>{formatClockHms(nowMs)}</span>
        </div>

        <div className="term-idle-topbar-right">
          {connection === 'offline' && (
            <button
              type="button"
              className="term-idle-action-btn term-idle-action-btn--warn"
              onClick={handleReconnect}
              aria-label={t('app.idleTerminal.reconnectAria')}
            >
              {t('app.idleTerminal.reconnectBtn')}
            </button>
          )}
          <button
            type="button"
            className="term-idle-action-btn"
            onClick={handleExit}
            aria-label={t('app.idleTerminal.exitAria')}
          >
            {t('app.idleTerminal.exitBtn')}
          </button>
        </div>
      </header>

      <div className="term-idle-grid">
        {/* 窗格 01：角色状态、气血灵力、修为进度与随身资产 */}
        <section className="term-idle-pane term-idle-pane--role" aria-label={t('app.idleTerminal.pane01Title')}>
          <div className="term-idle-pane-header">
            <span>{t('app.idleTerminal.pane01Title')}</span>
            {isPendingBreakthrough ? (
              <span className="term-badge term-badge--warn">{t('app.idleTerminal.breakthroughReady')}</span>
            ) : (
              <span className="term-tone-ok">{t('app.idleTerminal.realmLevelSuffix', { level: realmProgress.level })}</span>
            )}
          </div>
          <div className="term-idle-pane-body">
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.realmLabel')}</span>
              <span className="term-kv-val term-tone-gold">{realmInfo.fullDisplayName}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.realmExpLabel')}</span>
              <span className="term-kv-val term-tone-info">
                [{renderAsciiBar(realmRatio, 12)}] {(realmRatio * 100).toFixed(1)}% ({formatNumber(realmProgress.currentLayerExp)}/{formatNumber(Math.max(1, realmProgress.expToNext))})
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.hpLabel')}</span>
              <span className={`term-kv-val term-tone-${telemetry.hpTone}`}>
                [{renderAsciiBar(telemetry.hpRatio, 12)}] {formatNumber(telemetry.hpCurrent)}/{formatNumber(telemetry.hpMax)}
                {telemetry.hpTone === 'crit' ? ` ${t('app.idleTerminal.hpLowWarn')}` : ''}
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.mpLabel')}</span>
              <span className={`term-kv-val term-tone-${telemetry.mpTone}`}>
                [{renderAsciiBar(telemetry.mpRatio, 12)}] {formatNumber(telemetry.mpCurrent)}/{formatNumber(telemetry.mpMax)}
              </span>
            </div>
            <div className="term-divider">------------------------------------------------------------</div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.spiritStoneLabel')}</span>
              <span className="term-kv-val term-tone-gold">{formatNumber(spiritStones)}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.meritLabel')} / {t('app.idleTerminal.foundationLabel')}</span>
              <span className="term-kv-val term-tone-bright">
                {formatNumber(totalMerit)} / {formatNumber(foundation)}
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.waveNetLabel')}</span>
              <span className="term-kv-val term-tone-info">{rxSparkline}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.waveOpsLabel')}</span>
              <span className="term-kv-val term-tone-ok">{opsSparkline}</span>
            </div>
          </div>
        </section>

        {/* 窗格 02：当前行动、单轮进度、剩余耗时与每日效率 */}
        <section className="term-idle-pane term-idle-pane--job" aria-label={t('app.idleTerminal.pane02Title')}>
          <div className="term-idle-pane-header">
            <span>{t('app.idleTerminal.pane02Title')}</span>
            <span className={`term-badge term-badge--${telemetry.jobTone}`}>[{jobStatusLabel}]</span>
          </div>
          <div className="term-idle-pane-body">
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.currentActionLabel')}</span>
              <span className="term-kv-val term-tone-bright">{headActionName}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.roundProgressLabel')}</span>
              <span className={`term-kv-val term-tone-${telemetry.jobTone}`}>
                [{renderAsciiBar(telemetry.jobProgressRatio, 14)}] {roundProgressRightText}
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.estimateLabel')}</span>
              <span className="term-kv-val term-tone-gold">{estimatedRemainingText}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{t('app.idleTerminal.queueConfigLabel')}</span>
              <span className="term-kv-val">
                {t('app.idleTerminal.queueConfigValue', {
                  depth: telemetry.queueDepth,
                  cycles: telemetry.completedCycles,
                  loop: queue.loopEnabled ? t('app.idleTerminal.toggleOn') : t('app.idleTerminal.toggleOff'),
                  travel: queue.autoTravelEnabled ? t('app.idleTerminal.toggleOn') : t('app.idleTerminal.toggleOff'),
                })}
              </span>
            </div>
            <div className="term-divider">------------------------------------------------------------</div>
            {briefStats && duel?.currentReport ? (
              <>
                <div className="term-kv-row">
                  <span className="term-kv-label">{t('app.idleTerminal.dailyRevenueLabel')}</span>
                  <span className="term-kv-val term-tone-gold">
                    {t('app.idleTerminal.dailyRevenueValue', {
                      daily: formatNumber(briefStats.dailyRevenue),
                      hourly: formatNumber(briefStats.revenuePerHour),
                    })}
                  </span>
                </div>
                <div className="term-kv-row">
                  <span className="term-kv-label">{t('app.idleTerminal.expRateLabel')}</span>
                  <span className="term-kv-val term-tone-info">
                    {t('app.idleTerminal.expRateValue', {
                      hourly: formatNumber(briefStats.cultivationPerHour),
                      daily: formatNumber(briefStats.dailyCultivation),
                    })}
                  </span>
                </div>
                <div className="term-kv-row">
                  <span className="term-kv-label">{t('app.idleTerminal.freqLabel')}</span>
                  <span className="term-kv-val term-tone-ok">
                    {briefStats.isCombat
                      ? t('app.idleTerminal.freqCombatValue', {
                          rate: briefStats.encountersPerHour.toFixed(1),
                          total: duel.currentReport.cycles,
                          deaths: duel.currentReport.deaths ?? 0,
                        })
                      : t('app.idleTerminal.freqLifeValue', {
                          rate: briefStats.cyclesPerHour.toFixed(1),
                          total: duel.currentReport.cycles,
                        })}
                  </span>
                </div>
              </>
            ) : (
              <div className="term-tone-dim">{t('app.idleTerminal.noReportHint')}</div>
            )}
          </div>
        </section>

        {/* 窗格 03：战斗中展示实时纯字符敌我气血条；非战斗展示节点链路与算力 */}
        <section
          className="term-idle-pane term-idle-pane--battle"
          aria-label={isCombatEntry ? t('app.idleTerminal.pane03CombatTitle') : t('app.idleTerminal.pane03NodeTitle')}
        >
          <div className="term-idle-pane-header">
            <span>{isCombatEntry ? t('app.idleTerminal.pane03CombatTitle') : t('app.idleTerminal.pane03NodeTitle')}</span>
            {expedition?.waveNumber ? (
              <span className="term-tone-gold">{t('app.idleTerminal.waveBadge', { wave: expedition.waveNumber })}</span>
            ) : (
              <span className={`term-tone-${telemetry.netTone}`}>{netLabel}</span>
            )}
          </div>
          <div className="term-idle-pane-body">
            {isCombatEntry ? (
              <div className="term-unit-list">
                {expedition?.allies && expedition.allies.length > 0 ? (
                  expedition.allies.map(ally => {
                    const max = Math.max(1, Math.round(ally.maxHitpoints ?? telemetry.hpMax ?? 1))
                    const cur = Math.max(0, Math.round(ally.hitpoints))
                    const ratio = clamp01(cur / max)
                    const isSelf = expedition.selfIndex === ally.memberIndex
                    return (
                      <div key={ally.playerId} className="term-unit-row">
                        <span className="term-unit-name term-tone-ok">
                          {isSelf ? t('app.idleTerminal.selfTag') : t('app.idleTerminal.allyTag')} {ally.displayName}
                        </span>
                        <span className={cur <= 0 ? 'term-tone-crit' : 'term-tone-ok'}>
                          [{renderAsciiBar(ratio, 10)}] {cur <= 0 ? t('app.idleTerminal.unitDead') : `${formatNumber(cur)}/${formatNumber(max)}`}
                        </span>
                      </div>
                    )
                  })
                ) : (
                  <div className="term-unit-row">
                    <span className="term-unit-name term-tone-ok">{t('app.idleTerminal.selfTag')} {name}</span>
                    <span className={`term-tone-${telemetry.hpTone}`}>
                      [{renderAsciiBar(telemetry.hpRatio, 10)}] {formatNumber(telemetry.hpCurrent)}/{formatNumber(telemetry.hpMax)}
                    </span>
                  </div>
                )}

                <div className="term-divider">------------------------------------------------------------</div>

                {expedition?.duel ? (
                  <div className="term-unit-row">
                    <span className="term-unit-name term-tone-warn">
                      {t('app.idleTerminal.duelFoeTag')} {expedition.duel.opponentName}
                    </span>
                    <span className="term-tone-warn">
                      [{renderAsciiBar(
                        clamp01(expedition.duel.opponentHitpoints / Math.max(1, expedition.duel.opponentMaxHitpoints ?? 1)),
                        10,
                      )}] {formatNumber(Math.max(0, Math.round(expedition.duel.opponentHitpoints)))}/{formatNumber(Math.max(1, Math.round(expedition.duel.opponentMaxHitpoints ?? 1)))}
                    </span>
                  </div>
                ) : expedition?.enemies && expedition.enemies.length > 0 ? (
                  expedition.enemies.map((foe, idx) => {
                    const max = Math.max(1, Math.round(foe.maxHitpoints ?? Math.max(1, foe.hitpoints)))
                    const cur = Math.max(0, Math.round(foe.hitpoints))
                    const ratio = clamp01(cur / max)
                    const rankTag = foe.rank === 'boss'
                      ? t('app.idleTerminal.rankBoss')
                      : foe.rank === 'elite'
                        ? t('app.idleTerminal.rankElite')
                        : t('app.idleTerminal.rankNormal')
                    const rankClass = foe.rank === 'boss'
                      ? 'term-tone-crit'
                      : foe.rank === 'elite'
                        ? 'term-tone-gold'
                        : 'term-tone-info'
                    return (
                      <div key={`${foe.enemyId}-${idx}`} className="term-unit-row">
                        <span className={`term-unit-name ${rankClass}`}>
                          {rankTag} {foe.name}
                        </span>
                        <span className={cur <= 0 ? 'term-tone-dim' : rankClass}>
                          [{renderAsciiBar(ratio, 10)}] {cur <= 0 ? t('app.idleTerminal.unitDead') : `${formatNumber(cur)}/${formatNumber(max)}`}
                        </span>
                      </div>
                    )
                  })
                ) : (
                  <div className="term-tone-dim">{t('app.idleTerminal.combatIdleHint')}</div>
                )}
              </div>
            ) : (
              <>
                <pre className={`term-pre-block term-tone-${telemetry.netTone}`}>{nodeMeshText}</pre>
                <div className="term-divider">------------------------------------------------------------</div>
                <div className="term-cpu-grid">
                  {telemetry.cpuCores.map((val, idx) => (
                    <div key={idx} className="term-kv-row">
                      <span className="term-tone-dim">{t('app.idleTerminal.coreLabel', { n: idx })}</span>
                      <span className={val > 0.75 ? 'term-tone-warn' : 'term-tone-ok'}>
                        [{renderAsciiBar(val, 8)}] {(val * 100).toFixed(0).padStart(3, ' ')}%
                      </span>
                    </div>
                  ))}
                </div>
              </>
            )}
          </div>
        </section>

        {/* 窗格 04：实时修行、采集制造与战斗掉落纯文本日志流 */}
        <section className="term-idle-pane term-idle-pane--stream" aria-label={t('app.idleTerminal.pane04Title')}>
          <div className="term-idle-pane-header">
            <span>{t('app.idleTerminal.pane04Title')}</span>
            <div className="term-filter-group" role="group" aria-label={t('app.idleTerminal.filterAria')}>
              {(
                [
                  { id: 'all', label: t('app.idleTerminal.filterAll') },
                  { id: 'system', label: t('app.idleTerminal.filterSystem') },
                  { id: 'combat', label: t('app.idleTerminal.filterCombat') },
                ] as const
              ).map(f => (
                <button
                  key={f.id}
                  type="button"
                  className={`term-filter-btn${logFilter === f.id ? ' is-active' : ''}`}
                  onClick={() => setLogFilter(f.id)}
                >
                  {f.label}
                </button>
              ))}
            </div>
          </div>
          <div className="term-idle-pane-body">
            <div className="term-log-list" ref={logContainerRef}>
              {mergedLogs.length === 0 ? (
                <div className="term-tone-dim">{t('app.idleTerminal.logEmpty')}</div>
              ) : (
                mergedLogs.map(entry => (
                  <div key={entry.id} className={`term-log-line term-tone-${entry.tone}`}>
                    {entry.text}
                  </div>
                ))
              )}
            </div>
            <div className="term-prompt-bar">
              <span className="term-tone-ok">{name || 'player'}</span>
              <span className="term-tone-dim">:/</span>
              <span className="term-tone-info">{t('app.idleTerminal.promptSuffix')}</span>
              <span className="term-tone-dim">$ </span>
              <span>{typedCommand}</span>
              <span className="term-cursor" aria-hidden="true">█</span>
            </div>
          </div>
        </section>

        {/* 窗格 05：队列与自动补给余量 / 产出明细 / 生活技艺进度 / 内存帧检视 */}
        <section className="term-idle-pane term-idle-pane--inspector" aria-label={pane05Title}>
          <div className="term-idle-pane-header">
            <span>{pane05Title}</span>
            <span className="term-tone-dim">#{version}</span>
          </div>
          <div className="term-idle-pane-body">
            {inspectorTab === 'queue' && (
              <>
                {queue.entries.length === 0 ? (
                  <div className="term-tone-dim">{t('app.idleTerminal.queueEmpty')}</div>
                ) : (
                  <table className="term-table">
                    <thead>
                      <tr>
                        <th>{t('app.idleTerminal.colIndex')}</th>
                        <th>{t('app.idleTerminal.colAction')}</th>
                        <th>{t('app.idleTerminal.colState')}</th>
                        <th>{t('app.idleTerminal.colRepeats')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {queue.entries.slice(0, 5).map((entry, idx) => {
                        const rowName = formatEntryDisplayName(
                          entry,
                          actions,
                          items,
                          equipment,
                          world,
                          maps,
                          n => t('app.idleTerminal.difficultyBadge', { n }),
                        )
                        const stateLabel = queue.paused && idx === 0
                          ? t('app.idleTerminal.statusPaused')
                          : entry.status === 'running'
                            ? t('app.idleTerminal.statusRun')
                            : entry.status === 'blocked'
                              ? t('app.idleTerminal.statusBlocked')
                              : t('app.idleTerminal.statusQueued')
                        const repeatsText = entry.remainingRepeats < 0
                          ? t('app.queue.repeatsInfinite')
                          : t('common.repeatsCount', { n: entry.remainingRepeats })
                        return (
                          <tr
                            key={entry.entryId}
                            className={entry.status === 'blocked' ? 'is-danger' : idx === 0 ? 'is-highlight' : undefined}
                          >
                            <td>{idx + 1}</td>
                            <td>{rowName}</td>
                            <td>{stateLabel}</td>
                            <td>{repeatsText}</td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                )}

                <div className="term-sub-heading">
                  {t('app.idleTerminal.supplyHeader', { bar: supplyInspection.barLabel })}
                </div>
                {supplyInspection.rows.length === 0 ? (
                  <div className="term-tone-dim">{t('app.idleTerminal.supplyEmpty')}</div>
                ) : (
                  <table className="term-table">
                    <thead>
                      <tr>
                        <th>{t('app.idleTerminal.colSlot')}</th>
                        <th>{t('app.idleTerminal.colItem')}</th>
                        <th>{t('app.idleTerminal.colStock')}</th>
                        <th>{t('app.idleTerminal.colSupplyState')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {supplyInspection.rows.map(row => {
                        const exhausted = row.stock <= 0
                        const stateText = !row.enabled
                          ? t('app.idleTerminal.supplyDisabled')
                          : exhausted
                            ? t('app.idleTerminal.supplyExhausted')
                            : row.activeSec > 0
                              ? t('app.idleTerminal.supplyActiveSec', { s: row.activeSec })
                              : t('app.idleTerminal.supplyReady')
                        return (
                          <tr key={row.slotLabel} className={exhausted ? 'is-danger' : undefined}>
                            <td>{row.slotLabel}</td>
                            <td>{row.itemName}</td>
                            <td>{exhausted ? t('app.idleTerminal.supplyExhausted') : formatNumber(row.stock)}</td>
                            <td>{stateText}</td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                )}
              </>
            )}

            {inspectorTab === 'yield' && (
              briefStats && briefStats.topYields.length > 0 ? (
                <table className="term-table">
                  <thead>
                    <tr>
                      <th>{t('app.idleTerminal.colYieldItem')}</th>
                      <th>{t('app.idleTerminal.colYieldTotal')}</th>
                      <th>{t('app.idleTerminal.colYieldDaily')}</th>
                      <th>{t('app.idleTerminal.colYieldValue')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {briefStats.topYields.slice(0, 8).map((y, idx) => {
                      const itemName = findItemInCatalog(items, y.itemId)?.name
                        ?? findEquipmentInCatalog(equipment, y.itemId)?.name
                        ?? y.itemId
                      return (
                        <tr key={y.itemId} className={idx === 0 ? 'is-highlight' : undefined}>
                          <td>{itemName}</td>
                          <td>+{formatNumber(y.quantity)}</td>
                          <td>+{formatNumber(y.dailyQty)}</td>
                          <td>+{formatNumber(y.dailyVal)}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              ) : (
                <div className="term-tone-dim">{t('app.idleTerminal.yieldEmpty')}</div>
              )
            )}

            {inspectorTab === 'skills' && (
              <table className="term-table">
                <thead>
                  <tr>
                    <th>{t('app.idleTerminal.colTechName')}</th>
                    <th>{t('app.idleTerminal.colTechLevel')}</th>
                    <th>{t('app.idleTerminal.colTechProgress')}</th>
                    <th>{t('app.idleTerminal.colTechBonus')}</th>
                  </tr>
                </thead>
                <tbody>
                  {techniqueRows.map(row => (
                    <tr key={row.id} className={headActionDef?.technique === row.id ? 'is-highlight' : undefined}>
                      <td>{row.name}</td>
                      <td>Lv.{row.level}</td>
                      <td>[{renderAsciiBar(row.ratio, 10)}] {(row.ratio * 100).toFixed(0)}%</td>
                      <td>+{row.speedPct}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            {inspectorTab === 'hex' && (
              <pre className="term-pre-block term-tone-dim">
                {hexRows.join('\n')}
              </pre>
            )}
          </div>
        </section>
      </div>

      <footer className="term-idle-statusbar">
        <span>{t('app.idleTerminal.statusLeft', { version, rtt: telemetry.rttMs })}</span>
        <span>{t('app.idleTerminal.statusRight')}</span>
      </footer>
    </div>
  )
})

export default TerminalIdleOverlay
