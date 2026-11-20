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
  ENGLISH_AUTO_USE_BAR_NAMES,
  ENGLISH_TECHNIQUE_NAMES,
  formatEnglishNumber,
  formatEnglishQueueEstimate,
  formatEnglishRealmDisplay,
  formatPlainLogSegments,
  humanizeEntityId,
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
import { useUiStore, type IdleTerminalLang } from '../uiStore'

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

/** 按当前挂机终端语言解析队列行动显示名（中文取配表与本地化名，英文取语义 ID Title） */
function formatEntryDisplayName(
  entry: QueueEntry,
  lang: IdleTerminalLang,
  actions: readonly ActionDefinition[],
  items: ReturnType<typeof useGameStore.getState>['items'],
  equipment: ReturnType<typeof useGameStore.getState>['equipment'],
  world: WorldState | undefined,
  maps: readonly WorldMapDefinition[],
  difficultyLabel: (n: number) => string,
): string {
  if (lang === 'en') {
    let base = entry.executionKind === 'travel' && entry.travelDestinationNodeId
      ? `Travel -> ${humanizeEntityId(entry.travelDestinationNodeId, entry.actionId)}`
      : humanizeEntityId(entry.actionId, entry.actionId)
    if (entry.targetItemId) {
      const targetEn = humanizeEntityId(entry.targetItemId, entry.targetItemId)
      const curPart = entry.enhancementCurrentLevel != null && entry.enhancementCurrentLevel > 0
        ? ` +${Math.floor(entry.enhancementCurrentLevel)}`
        : ''
      const tgtPart = entry.targetLevel != null && entry.targetLevel > 0
        ? ` -> +${Math.floor(entry.targetLevel)}`
        : ''
      base = `${base} · ${targetEn}${curPart}${tgtPart}`
    }
    if (entry.difficulty > 0) {
      base = `${base} [T${entry.difficulty}]`
    }
    return base
  }

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
 * 纯字符双语终端挂机模式组件 (TerminalIdleOverlay)
 *
 * 严格约束：
 * 1. 零图像元素（无 img / svg / canvas / Emoji），纯等宽字符框线、块状进度条与盲文点阵波形；
 * 2. 支持右上角一键切换中文与英文（直译游戏真实数据指标），所有中文词条经 i18n 闭环；
 * 3. 仅铺满网页视口，仅通过点击右上角退出按钮返回常规游戏界面。
 */
export const TerminalIdleOverlay = memo(function TerminalIdleOverlay(): React.JSX.Element {
  const t = useT()
  const setIdleModeOpen = useUiStore(state => state.setIdleModeOpen)
  const lang = useUiStore(state => state.idleTerminalLang)
  const toggleLang = useUiStore(state => state.toggleIdleTerminalLang)
  const isEn = lang === 'en'

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

  const fmtNum = useCallback(
    (val: number): string => (isEn ? formatEnglishNumber(val) : formatNumber(val)),
    [isEn],
  )

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
    [expToNextCap, stats?.experience, stats?.realmLevel, t],
  )
  const realmRatio = clamp01((realmProgress.progressPercentage ?? 0) / 100)
  const realmDisplayText = isEn
    ? formatEnglishRealmDisplay(realmProgress.level)
    : realmInfo.fullDisplayName

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
    if (!headEntry || !headEstimate) {
      return isEn ? 'Unlimited' : t('app.idleTerminal.estimateNone')
    }
    const liveSec = telemetry.jobStatus === 'RUN' ? elapsedQueueSec : 0
    const rem = resolveLiveRemainingSeconds(headEstimate, liveSec)
    return isEn ? formatEnglishQueueEstimate(headEstimate, rem) : formatQueueRepeatsText(headEstimate, rem, t)
  }, [elapsedQueueSec, headEntry, headEstimate, isEn, t, telemetry.jobStatus])

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
      return {
        barLabel: isEn ? 'No Matching Bar' : t('app.idleTerminal.supplyBarNone'),
        rows: [] as Array<{ slotLabel: string; itemName: string; stock: number; enabled: boolean; activeSec: number }>,
      }
    }
    const barLabel = isEn ? ENGLISH_AUTO_USE_BAR_NAMES[bar] : autoUseBarLabel(bar)
    const rawSlots = queue.autoUseSlots?.[bar] ?? []
    const rows: Array<{ slotLabel: string; itemName: string; stock: number; enabled: boolean; activeSec: number }> = []
    rawSlots.forEach((slot, idx) => {
      if (!slot.itemId) return
      const itemDef = findItemInCatalog(items, slot.itemId)
      const itemName = isEn
        ? humanizeEntityId(slot.itemId, slot.itemId)
        : (itemDef?.name ?? slot.itemId)
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
  }, [headActionDef?.technique, headEntry, inventory, isEn, items, queue.autoUseSlots, t])

  const resolveJobStatusLabel = useCallback(
    (status: TerminalJobStatus): string => {
      if (isEn) {
        switch (status) {
          case 'RUN':
            return 'RUNNING'
          case 'PAUSED':
            return 'PAUSED'
          case 'BLOCKED':
            return 'BLOCKED'
          case 'QUEUED':
            return 'QUEUED'
          case 'IDLE':
            return 'IDLE'
        }
      }
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
    [isEn, t],
  )

  const jobStatusLabel = resolveJobStatusLabel(telemetry.jobStatus)
  const headActionName = useMemo(() => {
    if (!headEntry) {
      return isEn ? 'None (Queue is empty)' : t('app.idleTerminal.noActiveAction')
    }
    return formatEntryDisplayName(
      headEntry,
      lang,
      actions,
      items,
      equipment,
      world,
      maps,
      n => t('app.idleTerminal.difficultyBadge', { n }),
    )
  }, [actions, equipment, headEntry, isEn, items, lang, maps, t, world])

  // 备份与还原浏览器标签页标题和空图标，并在标题动态同步当前语言的挂机状态
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
    document.title = isEn
      ? `[${jobStatusLabel}] DaoHua Terminal`
      : t('app.idleTerminal.docTitlePrefix', {
          status: jobStatusLabel,
          name: baseTitleRef.current,
        })
  }, [isEn, jobStatusLabel, t])

  // 订阅真实游戏日志流
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
        cmd: isEn ? 'status --summary' : t('app.idleTerminal.cliCmdStatus'),
        buildOutput: () =>
          isEn
            ? `  Player: ${name || 'Player'} | Realm: ${realmDisplayText} (${(realmRatio * 100).toFixed(1)}%) | HP: ${fmtNum(telemetry.hpCurrent)}/${fmtNum(telemetry.hpMax)} | MP: ${fmtNum(telemetry.mpCurrent)}/${fmtNum(telemetry.mpMax)} | Spirit Stones: ${fmtNum(spiritStones)}`
            : t('app.idleTerminal.cliOutRole', {
                name,
                realm: realmInfo.minorName,
                expPct: (realmRatio * 100).toFixed(1),
                hp: `${fmtNum(telemetry.hpCurrent)}/${fmtNum(telemetry.hpMax)}`,
                mp: `${fmtNum(telemetry.mpCurrent)}/${fmtNum(telemetry.mpMax)}`,
                stones: fmtNum(spiritStones),
              }),
      },
      {
        cmd: isEn ? 'queue --inspect-rates' : t('app.idleTerminal.cliCmdQueue'),
        buildOutput: () =>
          isEn
            ? `  Action: ${headActionName} [${jobStatusLabel}] | Progress: ${(telemetry.jobProgressRatio * 100).toFixed(1)}% | Queue: ${telemetry.queueDepth} | Est. Stones/Day: +${fmtNum(briefStats?.dailyRevenue ?? 0)}`
            : t('app.idleTerminal.cliOutAction', {
                action: headActionName,
                status: jobStatusLabel,
                pct: (telemetry.jobProgressRatio * 100).toFixed(1),
                depth: telemetry.queueDepth,
                daily: fmtNum(briefStats?.dailyRevenue ?? 0),
              }),
      },
      {
        cmd: isEn ? 'supply --check-stock' : t('app.idleTerminal.cliCmdSupply'),
        buildOutput: () =>
          isEn
            ? `  Supply: ${supplyInspection.rows.length} configured | Exhausted: ${supplyInspection.rows.filter(r => r.stock <= 0).length} | Latency: ${telemetry.rttMs}ms`
            : t('app.idleTerminal.cliOutSupply', {
                configured: supplyInspection.rows.length,
                empty: supplyInspection.rows.filter(r => r.stock <= 0).length,
                rtt: telemetry.rttMs,
              }),
      },
    ],
    [
      briefStats?.dailyRevenue,
      fmtNum,
      headActionName,
      isEn,
      jobStatusLabel,
      name,
      realmDisplayText,
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

  // 切换语言时清空未敲完的半截提示命令，防止中英混串
  useEffect(() => {
    setTypedCommand('')
  }, [lang])

  // 键盘快捷命令交互
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
        ? (isEn ? '[CMB]' : t('app.idleTerminal.logTagCombat'))
        : (isEn ? '[SYS]' : t('app.idleTerminal.logTagSystem'))
      const body = formatPlainLogSegments(entry.segments, lang)
      const countSuffix = entry.count > 1 ? ` (x${entry.count})` : ''
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
      text: `[${formatClockHms(entry.timestampMs)}] ${isEn ? '[CLI]' : t('app.idleTerminal.logTagCli')} ${entry.text}`,
    }))

    return [...mappedGame, ...mappedCli]
      .sort((a, b) => a.timestampMs - b.timestampMs)
      .slice(-MAX_MERGED_LOGS)
  }, [cliLogs, gameLogs, isEn, lang, logFilter, t])

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
    ? (isEn ? 'ONLINE' : t('app.idleTerminal.netOnline'))
    : telemetry.netState === 'reconnecting'
      ? (isEn ? 'RECONNECTING' : t('app.idleTerminal.netReconnecting'))
      : (isEn ? 'OFFLINE' : t('app.idleTerminal.netOffline'))

  const expeditionPhaseText = useMemo(() => {
    if (headEntry?.executionKind === 'travel') {
      return isEn ? 'Traveling' : t('app.idleTerminal.phaseTravel')
    }
    if (!expedition) return null
    switch (expedition.phase) {
      case 'initial-search':
      case 'search':
        return isEn ? 'Searching Target' : t('app.idleTerminal.phaseSearch')
      case 'combat':
        return isEn ? 'In Combat' : t('app.idleTerminal.phaseCombat')
      case 'respawn':
        return isEn ? 'Respawning' : t('app.idleTerminal.phaseRespawn')
      case 'duel-pending':
      case 'duel':
        return isEn ? 'PvP Duel' : t('app.idleTerminal.phaseDuel')
    }
  }, [expedition, headEntry?.executionKind, isEn, t])

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
  const locationName = isEn
    ? (world?.nodeId
        ? humanizeEntityId(world.nodeId, 'Transit')
        : world?.edgeId
          ? 'In Transit'
          : 'Unknown')
    : (world?.locationName || t('app.idleTerminal.locationFallback'))

  const cultivatorDisplay = isEn
    ? `Player#${sequenceId > 0 ? sequenceId : 1}`
    : `${name || '-'}${sequenceId > 0 ? `#${sequenceId}` : ''}`

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
          name: isEn ? ENGLISH_TECHNIQUE_NAMES[techId] : t(`insight.technique.${techId}`),
          level: prog.level,
          ratio,
          speedPct,
        }
      }),
    [combat?.techniqueExperiences, isEn, t],
  )

  const netArrow = telemetry.netState === 'online' ? '═══>' : telemetry.netState === 'reconnecting' ? '─?─>' : '─X─ '
  const nodeMeshText = isEn
    ? [
        `[Client]══╦${netArrow}[Server: ${netLabel} ${telemetry.rttMs}ms]`,
        `          ╠${netArrow}[Location: ${locationName}]`,
        `          ╚${netArrow}[Cluster: Online:${telemetry.onlinePlayers} Idle:${telemetry.offlineIdlePlayers}]`,
      ].join('\n')
    : [
        `[${t('app.idleTerminal.meshClient')}]══╦${netArrow}[${t('app.idleTerminal.meshGateway')}: ${netLabel} ${telemetry.rttMs}ms]`,
        `          ╠${netArrow}[${t('app.idleTerminal.meshLocation')}: ${locationName}]`,
        `          ╚${netArrow}[${t('app.idleTerminal.meshCluster')}: ${t('app.idleTerminal.meshOnlineCount', { online: telemetry.onlinePlayers, idle: telemetry.offlineIdlePlayers })}]`,
      ].join('\n')

  const pane01Title = isEn ? '+--[ 01 // CHARACTER & RESOURCES ]' : t('app.idleTerminal.pane01Title')
  const pane02Title = isEn ? '+--[ 02 // ACTIVE ACTION & EFFICIENCY ]' : t('app.idleTerminal.pane02Title')
  const pane03Title = isCombatEntry
    ? (isEn ? '+--[ 03 // LIVE COMBAT & UNIT HP ]' : t('app.idleTerminal.pane03CombatTitle'))
    : (isEn ? '+--[ 03 // NODE MESH & COMPUTE LOAD ]' : t('app.idleTerminal.pane03NodeTitle'))
  const pane04Title = isEn ? '+--[ 04 // REAL-TIME ACTIVITY & LOOT LOG ]' : t('app.idleTerminal.pane04Title')
  const pane05Title = inspectorTab === 'queue'
    ? (isEn ? '+--[ 05 // ACTION QUEUE & AUTO-SUPPLY ]' : t('app.idleTerminal.pane05TitleQueue'))
    : inspectorTab === 'yield'
      ? (isEn ? '+--[ 05 // CURRENT REPORT TOP YIELDS ]' : t('app.idleTerminal.pane05TitleYield'))
      : inspectorTab === 'skills'
        ? (isEn ? '+--[ 05 // LIFE TECHNIQUES PROGRESS ]' : t('app.idleTerminal.pane05TitleSkills'))
        : (isEn ? '+--[ 05 // MEMORY FRAME HEX DUMP ]' : t('app.idleTerminal.pane05TitleHex'))

  return (
    <div
      className="term-idle-root"
      role="region"
      aria-label={isEn ? 'Idle monitoring terminal' : t('app.idleTerminal.regionAria')}
    >
      <header className="term-idle-topbar">
        <div className="term-idle-topbar-left">
          <span className="term-idle-session-badge">
            [{isEn ? 'IDLE-TERM' : t('app.idleTerminal.badge')}]
          </span>
          <span className="term-tone-bright">{cultivatorDisplay} @ {locationName}</span>
          <nav
            className="term-idle-tmux-tabs"
            aria-label={isEn ? 'Inspector tabs' : t('app.idleTerminal.panesAria')}
          >
            {(
              [
                { id: 'queue', label: isEn ? '1:Queue & Supply' : t('app.idleTerminal.tabQueue') },
                { id: 'yield', label: isEn ? '2:Loot & Yield' : t('app.idleTerminal.tabYield') },
                { id: 'skills', label: isEn ? '3:Techniques' : t('app.idleTerminal.tabSkills') },
                { id: 'hex', label: isEn ? '4:Hex Dump' : t('app.idleTerminal.tabHex') },
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
          <span className="term-tone-dim">{isEn ? 'NET' : t('app.idleTerminal.netLabel')}:</span>
          <span className={`term-tone-${telemetry.netTone}`}>
            {netLabel}{telemetry.netState === 'online' ? ` (${telemetry.rttMs}ms)` : ''}
          </span>
          <span className="term-tone-dim">|</span>
          <span>
            {isEn
              ? `Online ${telemetry.onlinePlayers}`
              : t('app.idleTerminal.onlinePlayers', { n: telemetry.onlinePlayers })}
          </span>
          <span className="term-tone-dim">|</span>
          <span className="term-tone-dim">{isEn ? 'Uptime' : t('app.idleTerminal.uptimeLabel')}:</span>
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
              aria-label={isEn ? 'Reconnect to server' : t('app.idleTerminal.reconnectAria')}
            >
              {isEn ? '[ ! RECONNECT ]' : t('app.idleTerminal.reconnectBtn')}
            </button>
          )}
          <button
            type="button"
            className="term-idle-action-btn"
            onClick={toggleLang}
            aria-label={isEn ? 'Switch idle terminal to Chinese' : t('app.idleTerminal.langToggleZhAria')}
          >
            {isEn ? '[ ZH / EN* ]' : t('app.idleTerminal.langToggleZhActive')}
          </button>
          <button
            type="button"
            className="term-idle-action-btn"
            onClick={handleExit}
            aria-label={isEn ? 'Exit idle terminal mode' : t('app.idleTerminal.exitAria')}
          >
            {isEn ? '[ x EXIT ]' : t('app.idleTerminal.exitBtn')}
          </button>
        </div>
      </header>

      <div className="term-idle-grid">
        {/* 窗格 01：角色状态、气血灵力、修为进度与随身资产 */}
        <section className="term-idle-pane term-idle-pane--role" aria-label={pane01Title}>
          <div className="term-idle-pane-header">
            <span>{pane01Title}</span>
            {isPendingBreakthrough ? (
              <span className="term-badge term-badge--warn">
                {isEn ? '[BREAKTHROUGH]' : t('app.idleTerminal.breakthroughReady')}
              </span>
            ) : (
              <span className="term-tone-ok">
                {isEn ? `Lv.${realmProgress.level}` : t('app.idleTerminal.realmLevelSuffix', { level: realmProgress.level })}
              </span>
            )}
          </div>
          <div className="term-idle-pane-body">
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Realm' : t('app.idleTerminal.realmLabel')}</span>
              <span className="term-kv-val term-tone-gold">{realmDisplayText}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Experience' : t('app.idleTerminal.realmExpLabel')}</span>
              <span className="term-kv-val term-tone-info">
                [{renderAsciiBar(realmRatio, 12)}] {(realmRatio * 100).toFixed(1)}% ({fmtNum(realmProgress.currentLayerExp)}/{fmtNum(Math.max(1, realmProgress.expToNext))})
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'HP' : t('app.idleTerminal.hpLabel')}</span>
              <span className={`term-kv-val term-tone-${telemetry.hpTone}`}>
                [{renderAsciiBar(telemetry.hpRatio, 12)}] {fmtNum(telemetry.hpCurrent)}/{fmtNum(telemetry.hpMax)}
                {telemetry.hpTone === 'crit' ? ` ${isEn ? '[LOW HP]' : t('app.idleTerminal.hpLowWarn')}` : ''}
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'MP' : t('app.idleTerminal.mpLabel')}</span>
              <span className={`term-kv-val term-tone-${telemetry.mpTone}`}>
                [{renderAsciiBar(telemetry.mpRatio, 12)}] {fmtNum(telemetry.mpCurrent)}/{fmtNum(telemetry.mpMax)}
              </span>
            </div>
            <div className="term-divider">------------------------------------------------------------</div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Spirit Stones' : t('app.idleTerminal.spiritStoneLabel')}</span>
              <span className="term-kv-val term-tone-gold">{fmtNum(spiritStones)}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">
                {isEn ? 'Merit / Foundation' : `${t('app.idleTerminal.meritLabel')} / ${t('app.idleTerminal.foundationLabel')}`}
              </span>
              <span className="term-kv-val term-tone-bright">
                {fmtNum(totalMerit)} / {fmtNum(foundation)}
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Net Pulse' : t('app.idleTerminal.waveNetLabel')}</span>
              <span className="term-kv-val term-tone-info">{rxSparkline}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Throughput' : t('app.idleTerminal.waveOpsLabel')}</span>
              <span className="term-kv-val term-tone-ok">{opsSparkline}</span>
            </div>
          </div>
        </section>

        {/* 窗格 02：当前行动、单轮进度、剩余耗时与每日效率 */}
        <section className="term-idle-pane term-idle-pane--job" aria-label={pane02Title}>
          <div className="term-idle-pane-header">
            <span>{pane02Title}</span>
            <span className={`term-badge term-badge--${telemetry.jobTone}`}>[{jobStatusLabel}]</span>
          </div>
          <div className="term-idle-pane-body">
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Action' : t('app.idleTerminal.currentActionLabel')}</span>
              <span className="term-kv-val term-tone-bright">{headActionName}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Progress' : t('app.idleTerminal.roundProgressLabel')}</span>
              <span className={`term-kv-val term-tone-${telemetry.jobTone}`}>
                [{renderAsciiBar(telemetry.jobProgressRatio, 14)}] {roundProgressRightText}
              </span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Remaining' : t('app.idleTerminal.estimateLabel')}</span>
              <span className="term-kv-val term-tone-gold">{estimatedRemainingText}</span>
            </div>
            <div className="term-kv-row">
              <span className="term-kv-label">{isEn ? 'Queue Info' : t('app.idleTerminal.queueConfigLabel')}</span>
              <span className="term-kv-val">
                {isEn
                  ? `${telemetry.queueDepth} queued | Done ${telemetry.completedCycles} | Loop:${queue.loopEnabled ? 'ON' : 'OFF'} | AutoTravel:${queue.autoTravelEnabled ? 'ON' : 'OFF'}`
                  : t('app.idleTerminal.queueConfigValue', {
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
                  <span className="term-kv-label">{isEn ? 'Stones / Day' : t('app.idleTerminal.dailyRevenueLabel')}</span>
                  <span className="term-kv-val term-tone-gold">
                    {isEn
                      ? `+${fmtNum(briefStats.dailyRevenue)} /d (+${fmtNum(briefStats.revenuePerHour)} /h)`
                      : t('app.idleTerminal.dailyRevenueValue', {
                          daily: fmtNum(briefStats.dailyRevenue),
                          hourly: fmtNum(briefStats.revenuePerHour),
                        })}
                  </span>
                </div>
                <div className="term-kv-row">
                  <span className="term-kv-label">{isEn ? 'Exp Rate' : t('app.idleTerminal.expRateLabel')}</span>
                  <span className="term-kv-val term-tone-info">
                    {isEn
                      ? `+${fmtNum(briefStats.cultivationPerHour)} /h (~+${fmtNum(briefStats.dailyCultivation)} /d)`
                      : t('app.idleTerminal.expRateValue', {
                          hourly: fmtNum(briefStats.cultivationPerHour),
                          daily: fmtNum(briefStats.dailyCultivation),
                        })}
                  </span>
                </div>
                <div className="term-kv-row">
                  <span className="term-kv-label">{isEn ? 'Frequency' : t('app.idleTerminal.freqLabel')}</span>
                  <span className="term-kv-val term-tone-ok">
                    {briefStats.isCombat
                      ? isEn
                        ? `${briefStats.encountersPerHour.toFixed(1)} battles/h (Total ${duel.currentReport.cycles} · Deaths ${duel.currentReport.deaths ?? 0})`
                        : t('app.idleTerminal.freqCombatValue', {
                            rate: briefStats.encountersPerHour.toFixed(1),
                            total: duel.currentReport.cycles,
                            deaths: duel.currentReport.deaths ?? 0,
                          })
                      : isEn
                        ? `${briefStats.cyclesPerHour.toFixed(1)} cycles/h (Total ${duel.currentReport.cycles})`
                        : t('app.idleTerminal.freqLifeValue', {
                            rate: briefStats.cyclesPerHour.toFixed(1),
                            total: duel.currentReport.cycles,
                          })}
                  </span>
                </div>
              </>
            ) : (
              <div className="term-tone-dim">
                {isEn
                  ? 'Awaiting first completed cycle to compute live efficiency rates...'
                  : t('app.idleTerminal.noReportHint')}
              </div>
            )}
          </div>
        </section>

        {/* 窗格 03：战斗中展示实时纯字符敌我气血条；非战斗展示节点链路与算力 */}
        <section className="term-idle-pane term-idle-pane--battle" aria-label={pane03Title}>
          <div className="term-idle-pane-header">
            <span>{pane03Title}</span>
            {expedition?.waveNumber ? (
              <span className="term-tone-gold">
                {isEn ? `Wave ${expedition.waveNumber}` : t('app.idleTerminal.waveBadge', { wave: expedition.waveNumber })}
              </span>
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
                    const allyLabel = isEn
                      ? `${isSelf ? '[SELF]' : '[ALLY]'} ${isSelf ? cultivatorDisplay : `Member#${ally.memberIndex + 1}`}`
                      : `${isSelf ? t('app.idleTerminal.selfTag') : t('app.idleTerminal.allyTag')} ${ally.displayName}`
                    return (
                      <div key={ally.playerId} className="term-unit-row">
                        <span className="term-unit-name term-tone-ok">{allyLabel}</span>
                        <span className={cur <= 0 ? 'term-tone-crit' : 'term-tone-ok'}>
                          [{renderAsciiBar(ratio, 10)}] {cur <= 0 ? (isEn ? 'DEAD' : t('app.idleTerminal.unitDead')) : `${fmtNum(cur)}/${fmtNum(max)}`}
                        </span>
                      </div>
                    )
                  })
                ) : (
                  <div className="term-unit-row">
                    <span className="term-unit-name term-tone-ok">
                      {isEn ? `[SELF] ${cultivatorDisplay}` : `${t('app.idleTerminal.selfTag')} ${name}`}
                    </span>
                    <span className={`term-tone-${telemetry.hpTone}`}>
                      [{renderAsciiBar(telemetry.hpRatio, 10)}] {fmtNum(telemetry.hpCurrent)}/{fmtNum(telemetry.hpMax)}
                    </span>
                  </div>
                )}

                <div className="term-divider">------------------------------------------------------------</div>

                {expedition?.duel ? (
                  <div className="term-unit-row">
                    <span className="term-unit-name term-tone-warn">
                      {isEn ? '[OPPONENT] Rival' : `${t('app.idleTerminal.duelFoeTag')} ${expedition.duel.opponentName}`}
                    </span>
                    <span className="term-tone-warn">
                      [{renderAsciiBar(
                        clamp01(expedition.duel.opponentHitpoints / Math.max(1, expedition.duel.opponentMaxHitpoints ?? 1)),
                        10,
                      )}] {fmtNum(Math.max(0, Math.round(expedition.duel.opponentHitpoints)))}/{fmtNum(Math.max(1, Math.round(expedition.duel.opponentMaxHitpoints ?? 1)))}
                    </span>
                  </div>
                ) : expedition?.enemies && expedition.enemies.length > 0 ? (
                  expedition.enemies.map((foe, idx) => {
                    const max = Math.max(1, Math.round(foe.maxHitpoints ?? Math.max(1, foe.hitpoints)))
                    const cur = Math.max(0, Math.round(foe.hitpoints))
                    const ratio = clamp01(cur / max)
                    const rankTag = foe.rank === 'boss'
                      ? (isEn ? '[BOSS]' : t('app.idleTerminal.rankBoss'))
                      : foe.rank === 'elite'
                        ? (isEn ? '[ELITE]' : t('app.idleTerminal.rankElite'))
                        : (isEn ? '[MOB]' : t('app.idleTerminal.rankNormal'))
                    const rankClass = foe.rank === 'boss'
                      ? 'term-tone-crit'
                      : foe.rank === 'elite'
                        ? 'term-tone-gold'
                        : 'term-tone-info'
                    const foeName = isEn ? humanizeEntityId(foe.enemyId, foe.enemyId) : foe.name
                    return (
                      <div key={`${foe.enemyId}-${idx}`} className="term-unit-row">
                        <span className={`term-unit-name ${rankClass}`}>
                          {rankTag} {foeName}
                        </span>
                        <span className={cur <= 0 ? 'term-tone-dim' : rankClass}>
                          [{renderAsciiBar(ratio, 10)}] {cur <= 0 ? (isEn ? 'DEAD' : t('app.idleTerminal.unitDead')) : `${fmtNum(cur)}/${fmtNum(max)}`}
                        </span>
                      </div>
                    )
                  })
                ) : (
                  <div className="term-tone-dim">
                    {isEn ? 'Searching for next enemy wave...' : t('app.idleTerminal.combatIdleHint')}
                  </div>
                )}
              </div>
            ) : (
              <>
                <pre className={`term-pre-block term-tone-${telemetry.netTone}`}>{nodeMeshText}</pre>
                <div className="term-divider">------------------------------------------------------------</div>
                <div className="term-cpu-grid">
                  {telemetry.cpuCores.map((val, idx) => (
                    <div key={idx} className="term-kv-row">
                      <span className="term-tone-dim">
                        {isEn ? `Core#${idx}` : t('app.idleTerminal.coreLabel', { n: idx })}
                      </span>
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
        <section className="term-idle-pane term-idle-pane--stream" aria-label={pane04Title}>
          <div className="term-idle-pane-header">
            <span>{pane04Title}</span>
            <div
              className="term-filter-group"
              role="group"
              aria-label={isEn ? 'Log channel filter' : t('app.idleTerminal.filterAria')}
            >
              {(
                [
                  { id: 'all', label: isEn ? 'ALL' : t('app.idleTerminal.filterAll') },
                  { id: 'system', label: isEn ? 'LOOT/SYS' : t('app.idleTerminal.filterSystem') },
                  { id: 'combat', label: isEn ? 'COMBAT' : t('app.idleTerminal.filterCombat') },
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
                <div className="term-tone-dim">
                  {isEn
                    ? 'Terminal log stream ready; listening for live cultivation, crafting, and combat loot events...'
                    : t('app.idleTerminal.logEmpty')}
                </div>
              ) : (
                mergedLogs.map(entry => (
                  <div key={entry.id} className={`term-log-line term-tone-${entry.tone}`}>
                    {entry.text}
                  </div>
                ))
              )}
            </div>
            <div className="term-prompt-bar">
              <span className="term-tone-ok">{isEn ? cultivatorDisplay.toLowerCase() : (name || 'player')}</span>
              <span className="term-tone-dim">:/</span>
              <span className="term-tone-info">{isEn ? 'idle-monitor' : t('app.idleTerminal.promptSuffix')}</span>
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
                  <div className="term-tone-dim">
                    {isEn
                      ? 'Action queue is empty. Exit idle mode to add actions.'
                      : t('app.idleTerminal.queueEmpty')}
                  </div>
                ) : (
                  <table className="term-table">
                    <thead>
                      <tr>
                        <th>{t('app.idleTerminal.colIndex')}</th>
                        <th>{isEn ? 'Queued Action' : t('app.idleTerminal.colAction')}</th>
                        <th>{isEn ? 'Status' : t('app.idleTerminal.colState')}</th>
                        <th>{isEn ? 'Repeats' : t('app.idleTerminal.colRepeats')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {queue.entries.slice(0, 5).map((entry, idx) => {
                        const rowName = formatEntryDisplayName(
                          entry,
                          lang,
                          actions,
                          items,
                          equipment,
                          world,
                          maps,
                          n => t('app.idleTerminal.difficultyBadge', { n }),
                        )
                        const stateLabel = queue.paused && idx === 0
                          ? (isEn ? 'PAUSED' : t('app.idleTerminal.statusPaused'))
                          : entry.status === 'running'
                            ? (isEn ? 'RUNNING' : t('app.idleTerminal.statusRun'))
                            : entry.status === 'blocked'
                              ? (isEn ? 'BLOCKED' : t('app.idleTerminal.statusBlocked'))
                              : (isEn ? 'QUEUED' : t('app.idleTerminal.statusQueued'))
                        const repeatsText = entry.remainingRepeats < 0
                          ? (isEn ? 'INF' : t('app.queue.repeatsInfinite'))
                          : (isEn ? `x${entry.remainingRepeats}` : t('common.repeatsCount', { n: entry.remainingRepeats }))
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
                  {isEn
                    ? `Auto-Supply Slots (${supplyInspection.barLabel})`
                    : t('app.idleTerminal.supplyHeader', { bar: supplyInspection.barLabel })}
                </div>
                {supplyInspection.rows.length === 0 ? (
                  <div className="term-tone-dim">
                    {isEn
                      ? 'No auto-use consumables configured in this slot bar.'
                      : t('app.idleTerminal.supplyEmpty')}
                  </div>
                ) : (
                  <table className="term-table">
                    <thead>
                      <tr>
                        <th>{isEn ? 'Slot' : t('app.idleTerminal.colSlot')}</th>
                        <th>{isEn ? 'Consumable' : t('app.idleTerminal.colItem')}</th>
                        <th>{isEn ? 'Stock' : t('app.idleTerminal.colStock')}</th>
                        <th>{isEn ? 'State' : t('app.idleTerminal.colSupplyState')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {supplyInspection.rows.map(row => {
                        const exhausted = row.stock <= 0
                        const stateText = !row.enabled
                          ? (isEn ? 'OFF' : t('app.idleTerminal.supplyDisabled'))
                          : exhausted
                            ? (isEn ? 'EMPTY(0)' : t('app.idleTerminal.supplyExhausted'))
                            : row.activeSec > 0
                              ? (isEn ? `ACTIVE(${row.activeSec}s)` : t('app.idleTerminal.supplyActiveSec', { s: row.activeSec }))
                              : (isEn ? 'READY' : t('app.idleTerminal.supplyReady'))
                        return (
                          <tr key={row.slotLabel} className={exhausted ? 'is-danger' : undefined}>
                            <td>{row.slotLabel}</td>
                            <td>{row.itemName}</td>
                            <td>{exhausted ? (isEn ? 'EMPTY(0)' : t('app.idleTerminal.supplyExhausted')) : fmtNum(row.stock)}</td>
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
                      <th>{isEn ? 'Item' : t('app.idleTerminal.colYieldItem')}</th>
                      <th>{isEn ? 'Gained' : t('app.idleTerminal.colYieldTotal')}</th>
                      <th>{isEn ? 'Est./Day' : t('app.idleTerminal.colYieldDaily')}</th>
                      <th>{isEn ? 'Value/Day' : t('app.idleTerminal.colYieldValue')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {briefStats.topYields.slice(0, 8).map((y, idx) => {
                      const itemName = isEn
                        ? humanizeEntityId(y.itemId, y.itemId)
                        : (findItemInCatalog(items, y.itemId)?.name
                          ?? findEquipmentInCatalog(equipment, y.itemId)?.name
                          ?? y.itemId)
                      return (
                        <tr key={y.itemId} className={idx === 0 ? 'is-highlight' : undefined}>
                          <td>{itemName}</td>
                          <td>+{fmtNum(y.quantity)}</td>
                          <td>+{fmtNum(y.dailyQty)}</td>
                          <td>+{fmtNum(y.dailyVal)}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              ) : (
                <div className="term-tone-dim">
                  {isEn
                    ? 'No item drops recorded in current action report yet.'
                    : t('app.idleTerminal.yieldEmpty')}
                </div>
              )
            )}

            {inspectorTab === 'skills' && (
              <table className="term-table">
                <thead>
                  <tr>
                    <th>{isEn ? 'Technique' : t('app.idleTerminal.colTechName')}</th>
                    <th>{isEn ? 'Level' : t('app.idleTerminal.colTechLevel')}</th>
                    <th>{isEn ? 'Progress' : t('app.idleTerminal.colTechProgress')}</th>
                    <th>{isEn ? 'Speed Bonus' : t('app.idleTerminal.colTechBonus')}</th>
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
        <span>
          {isEn
            ? `DaoHua CLI Console | Text-Only View | Sync #${version} | RTT ${telemetry.rttMs}ms`
            : t('app.idleTerminal.statusLeft', { version, rtt: telemetry.rttMs })}
        </span>
        <span>
          {isEn
            ? 'TYPE KEYS + [ENTER] FOR LIVE SNAPSHOT  //  CLICK [ x EXIT ] TO RETURN'
            : t('app.idleTerminal.statusRight')}
        </span>
      </footer>
    </div>
  )
})

export default TerminalIdleOverlay
