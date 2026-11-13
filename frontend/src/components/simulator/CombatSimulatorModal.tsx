import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Modal } from '../Modal'
import { Icon } from '../Icon'
import { FloatingInfoCard, FloatingPopover } from '../FloatingPopover'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { useSocialStore } from '../../game/socialStore'
import {
  createSimulatorBridge,
  recommendedSimulatorConcurrency,
  runSimulatorBatch,
  type SimulatorBridge,
} from '../../game/simulator/simulatorBridge'
import { fetchSimulatorEnvironment, fetchSimulatorProfile } from '../../network/apiClient'
import type {
  SimulatorEnvironmentDto, SimulatorProfileDto,
  SimulatorResolveRequestDto, SimulatorResolveResultDto, SimulatorRunRequestDto, SimulatorRunResultDto,
} from '../../game/simulator/simulatorTypes'
type SimulatorNodeEnvironmentDto = SimulatorEnvironmentDto['nodes'][number]
import { SIMULATOR_FORMAT_VERSION } from '../../game/simulator/simulatorTypes'
import { useSimulatorStore } from '../../game/simulator/simulatorStore'
import { SimulatorProfileEditor } from './SimulatorProfileEditor'
import { SimulatorResults } from './SimulatorResults'
import type { CombatAttributes } from '../../game/types'

interface Props { open: boolean; onClose: () => void }

const DURATION_PRESETS = [600, 1800, 3600, 7200, 21600, 86400]
const MAX_MEMBERS = 5
const SIMULATED_CONSUMABLE_STOCK = 1_000_000
const INT64_MIN = -(1n << 63n)
const INT64_MAX = (1n << 63n) - 1n

/** 模拟推演前补足已启用消耗品槽位所需虚拟库存，不污染面板状态且不在 UI 展示库存。 */
function withSimulatedConsumableStock(members: readonly SimulatorProfileDto[]): SimulatorProfileDto[] {
  return members.map(member => {
    const enabledIds = new Set<string>()
    for (const slot of member.combatAutoUseSlots) {
      if (slot.enabled && slot.itemId) enabledIds.add(slot.itemId)
    }
    if (enabledIds.size === 0) return member
    const assets = member.assets.map(a =>
      !a.variant && enabledIds.has(a.itemId)
        ? { ...a, quantity: Math.max(a.quantity, SIMULATED_CONSUMABLE_STOCK) }
        : a
    )
    for (const itemId of enabledIds) {
      if (!assets.some(a => a.itemId === itemId && !a.variant)) {
        assets.push({ itemId, variant: '', quantity: SIMULATED_CONSUMABLE_STOCK })
      }
    }
    const combatAutoUseSlots = member.combatAutoUseSlots.map(slot =>
      slot.enabled && slot.itemId
        ? { ...slot, quantity: Math.max(slot.quantity, SIMULATED_CONSUMABLE_STOCK) }
        : slot
    )
    return { ...member, assets, combatAutoUseSlots }
  })
}

function parseSeed(value: string): boolean {
  if (!/^-?\d+$/.test(value)) return false
  try {
    const v = BigInt(value)
    return v >= INT64_MIN && v <= INT64_MAX
  } catch { return false }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function validateProfileShape(value: unknown, fingerprint: string): boolean {
  if (!isRecord(value)) return false
  if (value.formatVersion !== SIMULATOR_FORMAT_VERSION || value.gameDataFingerprint !== fingerprint) return false
  if (typeof value.displayName !== 'string' || !isFiniteNumber(value.realmLevel)) return false
  const core = value.coreAttributes
  if (!isRecord(core)) return false
  for (const key of ['staminaExp', 'meridiansExp', 'attackExp', 'boneExp', 'agilityExp', 'consciousnessExp'])
    if (!isFiniteNumber(core[key])) return false
  if (!Array.isArray(value.abilitySlots) || !Array.isArray(value.equipment) || !Array.isArray(value.assets)) return false
  for (const key of ['hitpoints', 'maxHitpoints', 'manapoints', 'maxManapoints', 'regenCarrySeconds',
    'experience', 'foundation', 'combatExperience', 'lifetimeExperience', 'capturedAtUnixSeconds', 'respawnPolicy'])
    if (!isFiniteNumber(value[key])) return false
  if (!(value.sourcePlayerId === null || typeof value.sourcePlayerId === 'string')) return false
  return true
}

function toCombatAttributes(raw: Record<string, unknown> | undefined): CombatAttributes | null {
  if (!raw || !isFiniteNumber(raw.maxHitpoints)) return null
  const n = (key: string) => isFiniteNumber(raw[key]) ? raw[key] : 0
  const effects = isRecord(raw.effects) ? raw.effects : {}
  const effect = (key: string) => isFiniteNumber(effects[key]) ? effects[key] : 0
  return {
    maxHitpoints: n('maxHitpoints'), maxManapoints: n('maxManapoints'),
    physicalAttack: n('physicalAttack'), spellAttack: n('spellAttack'),
    armor: n('armor'), magicResistance: n('magicResistance'),
    accuracy: n('accuracy'), evasion: n('evasion'),
    critical: n('critical'), criticalResistance: n('criticalAvoidance'),
    criticalDamagePoints: n('criticalDamagePoints'),
    breakPower: n('breakPower'), parryResolve: n('parry'),
    hpRegenPerFiveSeconds: n('hpRegen'), mpRegenPerFiveSeconds: n('mpRegen'),
    attackIntervalMilliseconds: n('attackIntervalSeconds') * 1000,
    physicalDamageBonusPoints: n('physicalDamageBonusPoints'),
    spellDamageBonusPoints: n('spellDamageBonusPoints'),
    metalDamageBonusPoints: n('metalDamageBonusPoints'),
    woodDamageBonusPoints: n('woodDamageBonusPoints'),
    waterDamageBonusPoints: n('waterDamageBonusPoints'),
    fireDamageBonusPoints: n('fireDamageBonusPoints'),
    earthDamageBonusPoints: n('earthDamageBonusPoints'),
    totalDamageBonusPoints: n('totalDamageBonusPoints'),
    basicAttackBonusPoints: n('basicAttackBonusPoints'),
    skillBonusPoints: n('skillBonusPoints'),
    physicalDamageReductionPoints: n('physicalDamageReductionPoints'),
    spellDamageReductionPoints: n('spellDamageReductionPoints'),
    metalDamageReductionPoints: n('metalDamageReductionPoints'),
    woodDamageReductionPoints: n('woodDamageReductionPoints'),
    waterDamageReductionPoints: n('waterDamageReductionPoints'),
    fireDamageReductionPoints: n('fireDamageReductionPoints'),
    earthDamageReductionPoints: n('earthDamageReductionPoints'),
    damageAbsorptionPoints: n('damageAbsorptionPoints'),
    fixedDamageBonus: n('fixedDamageBonus'), fixedDamageReduction: n('fixedDamageReduction'),
    fixedPhysicalDamageBonus: n('fixedPhysicalDamageBonus'),
    fixedPhysicalDamageReduction: n('fixedPhysicalDamageReduction'),
    fixedSpellDamageBonus: n('fixedSpellDamageBonus'),
    fixedSpellDamageReduction: n('fixedSpellDamageReduction'),
    attackSpeedPoints: n('attackSpeedPoints'), actionSpeedPoints: n('actionSpeedPoints'),
    cooldownReductionPoints: n('cooldownReductionPoints'),
    cultivationExpBonusPoints: n('cultivationExpBonusPoints'),
    combatExpBonusPoints: n('combatExpBonusPoints'),
    threatBonusPoints: n('threatBonusPoints'),
    hpDrainPoints: effect('hpDrainPoints'), mpDrainPoints: effect('mpDrainPoints'),
    damageReflectPoints: effect('damageReflectPoints'), healingBonusPoints: effect('healingBonusPoints'),
    traits: Array.isArray(raw.traits) ? raw.traits.filter((x): x is string => typeof x === 'string') : null,
    traitLevels: isRecord(raw.traitLevels) ? raw.traitLevels as Record<string, number> : null,
  }
}

export const CombatSimulatorModal = memo(function CombatSimulatorModal({ open, onClose }: Props) {
  const t = useT()
  const playerId = useGameStore(state => state.playerId)
  const name = useGameStore(state => state.name)
  const maps = useGameStore(state => state.maps)
  const worldNodeId = useGameStore(state => state.world?.nodeId)
  const setNotice = useGameStore(state => state.setNotice)
  const party = useSocialStore(state => state.party)
  const [allyTarget, setAllyTarget] = useState('')
  const bridgeRef = useRef<SimulatorBridge | null>(null)
  const cancelBatchRef = useRef<(() => void) | null>(null)
  const openGenRef = useRef(0)
  const runIdRef = useRef(0)
  const previewGenRef = useRef(0)
  const importRef = useRef<Record<string, { seq: number; ctl: AbortController }>>({})

  const initialized = useSimulatorStore(s => s.initialized)
  const setInitialized = useSimulatorStore(s => s.setInitialized)
  const members = useSimulatorStore(s => s.members)
  const setMembers = useSimulatorStore(s => s.setMembers)
  const environment = useSimulatorStore(s => s.environment)
  const setEnvironment = useSimulatorStore(s => s.setEnvironment)
  const combatNodeIds = useSimulatorStore(s => s.combatNodeIds)
  const setCombatNodeIds = useSimulatorStore(s => s.setCombatNodeIds)
  const activeTab = useSimulatorStore(s => s.activeTab)
  const setActiveTab = useSimulatorStore(s => s.setActiveTab)
  const profileSubTab = useSimulatorStore(s => s.profileSubTab)
  const setProfileSubTab = useSimulatorStore(s => s.setProfileSubTab)
  const selectedMemberIndex = useSimulatorStore(s => s.selectedMemberIndex)
  const setSelectedMemberIndex = useSimulatorStore(s => s.setSelectedMemberIndex)
  const selectedMapId = useSimulatorStore(s => s.selectedMapId)
  const setSelectedMapId = useSimulatorStore(s => s.setSelectedMapId)
  const selectedNodeIds = useSimulatorStore(s => s.selectedNodeIds)
  const setSelectedNodeIds = useSimulatorStore(s => s.setSelectedNodeIds)
  const difficulties = useSimulatorStore(s => s.difficulties)
  const setDifficulties = useSimulatorStore(s => s.setDifficulties)
  const durationSeconds = useSimulatorStore(s => s.durationSeconds)
  const setDurationSeconds = useSimulatorStore(s => s.setDurationSeconds)
  const seed = useSimulatorStore(s => s.seed)
  const setSeed = useSimulatorStore(s => s.setSeed)
  const results = useSimulatorStore(s => s.results)
  const setResults = useSimulatorStore(s => s.setResults)
  const runStamp = useSimulatorStore(s => s.runStamp)
  const setRunStamp = useSimulatorStore(s => s.setRunStamp)
  const preview = useSimulatorStore(s => s.preview)
  const setPreview = useSimulatorStore(s => s.setPreview)
  const [running, setRunning] = useState(false)
  const [elapsedMs, setElapsedMs] = useState(0)
  const [progress, setProgress] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [resolvingPreview, setResolvingPreview] = useState(false)
  const configDirty = useSimulatorStore(s => s.configDirty)
  const setConfigDirty = useSimulatorStore(s => s.setConfigDirty)
  const resetSession = useSimulatorStore(s => s.resetSession)

  const bridge = useCallback(() => {
    if (!bridgeRef.current) bridgeRef.current = createSimulatorBridge()
    return bridgeRef.current
  }, [])

  const nodeEnvMap = useMemo(() => {
    const map = new Map<string, SimulatorNodeEnvironmentDto>()
    for (const node of environment?.nodes ?? []) map.set(node.nodeId, node)
    return map
  }, [environment])

  const validCombatNodeSet = useMemo(() => (
    combatNodeIds ? new Set(combatNodeIds) : null
  ), [combatNodeIds])

  const combatNodes = useMemo(() => {
    if (!environment || !validCombatNodeSet) return []
    return (maps ?? []).flatMap(map => (map.nodes ?? [])
      .filter(node => (
        (node.encounterIds?.length ?? 0) > 0
        && nodeEnvMap.has(node.nodeId)
        && validCombatNodeSet.has(node.nodeId)
      ))
      .map(node => ({
        id: node.nodeId,
        name: node.name ?? node.nodeId,
        mapId: map.mapId,
        mapName: map.name ?? map.mapId,
        entry: nodeEnvMap.get(node.nodeId)!,
      })))
  }, [environment, maps, nodeEnvMap, validCombatNodeSet])

  const regionOptions = useMemo(() => {
    const seen = new Map<string, { mapId: string; mapName: string; count: number }>()
    for (const node of combatNodes) {
      const existing = seen.get(node.mapId)
      if (existing) existing.count += 1
      else seen.set(node.mapId, { mapId: node.mapId, mapName: node.mapName, count: 1 })
    }
    return [...seen.values()]
  }, [combatNodes])

  const filteredNodes = useMemo(() =>
    selectedMapId === 'all' ? combatNodes : combatNodes.filter(n => n.mapId === selectedMapId),
  [combatNodes, selectedMapId])

  const nodeName = useCallback((id: string) =>
    combatNodes.find(n => n.id === id)?.name
    ?? maps?.flatMap(m => m.nodes ?? []).find(n => n.nodeId === id)?.name ?? id, [combatNodes, maps])

  const alive = useCallback((gen: number) => open && openGenRef.current === gen, [open])

  const nextImport = useCallback((key: string) => {
    const gen = openGenRef.current
    importRef.current[key]?.ctl.abort()
    const entry = { seq: (importRef.current[key]?.seq ?? 0) + 1, ctl: new AbortController() }
    importRef.current[key] = entry
    return { gen, seq: entry.seq, signal: entry.ctl.signal }
  }, [])

  const importOk = useCallback((key: string, gen: number, seq: number) =>
    alive(gen) && importRef.current[key]?.seq === seq, [alive])

  const importSelf = useCallback(async () => {
    const { gen, seq, signal } = nextImport('self')
    const profile = await fetchSimulatorProfile(playerId, undefined, signal)
    if (!importOk('self', gen, seq)) return
    setMembers(prev => {
      const exists = prev.some(m => m.sourcePlayerId === profile.sourcePlayerId)
      if (!exists && prev.length >= MAX_MEMBERS) {
        setError(t('combat.simulator.error.memberFull'))
        return prev
      }
      return exists ? prev.map(m => m.sourcePlayerId === profile.sourcePlayerId ? profile : m) : [profile, ...prev]
    })
    setConfigDirty(v => v + 1)
  }, [playerId, nextImport, importOk, t])

  const importAlly = useCallback(async (targetPlayerId: string) => {
    const { gen, seq, signal } = nextImport(`ally:${targetPlayerId}`)
    const profile = await fetchSimulatorProfile(playerId, targetPlayerId, signal)
    if (!importOk(`ally:${targetPlayerId}`, gen, seq)) return
    setMembers(prev => {
      const exists = prev.some(m => m.sourcePlayerId === profile.sourcePlayerId)
      if (!exists && prev.length >= MAX_MEMBERS) {
        setError(t('combat.simulator.error.memberFull'))
        return prev
      }
      return exists ? prev.map(m => m.sourcePlayerId === profile.sourcePlayerId ? profile : m) : [...prev, profile]
    })
    setConfigDirty(v => v + 1)
  }, [playerId, nextImport, importOk, t])

  const refreshEnvironment = useCallback(async () => {
    const { gen, seq, signal } = nextImport('env')
    const env = await fetchSimulatorEnvironment(playerId, signal)
    if (!importOk('env', gen, seq)) return
    setEnvironment(env)
    setConfigDirty(v => v + 1)
  }, [playerId, nextImport, importOk])

  useEffect(() => {
    if (!open) return
    const gen = ++openGenRef.current
    setError(null)
    setLoadError(null)
    // 弹窗打开即预热纯 TS Worker 与内嵌配表，并同步可用讨伐节点过滤清单
    void bridge().warm().catch(() => {})
    if (!combatNodeIds) {
      void bridge().listCombatNodeIds()
        .then(json => {
          if (!alive(gen)) return
          const parsed = JSON.parse(json)
          if (Array.isArray(parsed)) setCombatNodeIds(parsed.filter((id): id is string => typeof id === 'string'))
        })
        .catch(() => {})
    }
    if (initialized && environment !== null && members.length > 0) return
    Promise.all([
      members.length === 0 ? importSelf() : Promise.resolve(),
      environment === null ? refreshEnvironment() : Promise.resolve(),
    ])
      .then(() => {
        if (alive(gen)) setInitialized(true)
      })
      .catch(err => {
        if (alive(gen)) setLoadError(err instanceof Error ? err.message : String(err))
      })
  }, [open, initialized])

  useEffect(() => {
    if (!open) {
      openGenRef.current += 1
      runIdRef.current += 1
      previewGenRef.current += 1
      for (const entry of Object.values(importRef.current)) entry.ctl.abort()
      importRef.current = {}
      cancelBatchRef.current?.()
      cancelBatchRef.current = null
      bridgeRef.current?.cancelAll()
      setRunning(false)
      setProgress('')
    }
  }, [open])

  useEffect(() => () => {
    openGenRef.current += 1
    runIdRef.current += 1
    previewGenRef.current += 1
    for (const entry of Object.values(importRef.current)) entry.ctl.abort()
    cancelBatchRef.current?.()
    cancelBatchRef.current = null
    bridgeRef.current?.cancelAll()
  }, [])

  useEffect(() => {
    if (combatNodes.length === 0) return
    const validIds = new Set(combatNodes.map(n => n.id))
    const filtered = selectedNodeIds.filter(id => validIds.has(id))
    if (filtered.length !== selectedNodeIds.length) {
      const preferred = worldNodeId && validIds.has(worldNodeId) ? worldNodeId : combatNodes[0].id
      setSelectedNodeIds(filtered.length > 0 ? filtered : [preferred])
      return
    }
    if (selectedNodeIds.length === 0) {
      const preferred = worldNodeId && validIds.has(worldNodeId) ? worldNodeId : combatNodes[0].id
      setSelectedNodeIds([preferred])
    }
  }, [combatNodes, selectedNodeIds, worldNodeId, setSelectedNodeIds])

  useEffect(() => {
    if (selectedMapId !== 'all' && regionOptions.length > 0 && !regionOptions.some(r => r.mapId === selectedMapId)) {
      setSelectedMapId('all')
    }
  }, [selectedMapId, regionOptions, setSelectedMapId])

  const updateMember = useCallback((index: number, next: SimulatorProfileDto) => {
    setMembers(prev => prev.map((m, i) => (i === index ? next : m)))
    setConfigDirty(v => v + 1)
  }, [])

  const addCustom = useCallback(async () => {
    const gen = openGenRef.current
    try {
      const json = await bridge().createDefaultProfile()
      if (!alive(gen)) return
      const profile = JSON.parse(json) as SimulatorProfileDto
      profile.displayName = `${t('combat.simulator.customMember')} ${members.length + 1}`
      setMembers(prev => prev.length >= MAX_MEMBERS ? prev : [...prev, profile])
      setConfigDirty(v => v + 1)
    } catch (err) { if (alive(gen)) setError(err instanceof Error ? err.message : String(err)) }
  }, [bridge, alive, members.length, t])

  const exportConfig = useCallback(() => {
    const payload = {
      formatVersion: SIMULATOR_FORMAT_VERSION, members, environment,
      selectedNodeIds, difficulties, durationSeconds, seed,
    }
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'simulator-config.json'
    a.click()
    URL.revokeObjectURL(url)
  }, [members, environment, selectedNodeIds, difficulties, durationSeconds, seed])

  const importConfig = useCallback(async (file: File) => {
    const gen = openGenRef.current
    const fail = (key: string) => setError(t(key))
    try {
      const payload: unknown = JSON.parse(await file.text())
      if (!isRecord(payload) || payload.formatVersion !== SIMULATOR_FORMAT_VERSION) return fail('combat.simulator.error.version')
      if (!Array.isArray(payload.members) || payload.members.length < 1 || payload.members.length > MAX_MEMBERS)
        return fail('combat.simulator.error.members')
      const env = payload.environment
      if (!isRecord(env) || env.formatVersion !== SIMULATOR_FORMAT_VERSION || !Array.isArray(env.nodes)
        || !env.nodes.every(n => isRecord(n) && typeof n.nodeId === 'string'))
        return fail('combat.simulator.error.environment')
      const fingerprint = typeof env.gameDataFingerprint === 'string' ? env.gameDataFingerprint : ''
      if (!payload.members.every(m => validateProfileShape(m, fingerprint)))
        return fail('combat.simulator.error.members')
      if (!Array.isArray(payload.selectedNodeIds) || payload.selectedNodeIds.length === 0
        || !payload.selectedNodeIds.every(id => typeof id === 'string'
          && (env.nodes as { nodeId: string }[]).some(n => n.nodeId === id)))
        return fail('combat.simulator.error.nodes')
      if (!Array.isArray(payload.difficulties) || payload.difficulties.length === 0
        || !payload.difficulties.every(d => Number.isInteger(d) && d >= 0 && d <= 5))
        return fail('combat.simulator.error.difficulties')
      if (!isFiniteNumber(payload.durationSeconds)
        || payload.durationSeconds < 1 || payload.durationSeconds > 86400)
        return fail('combat.simulator.error.duration')
      if (typeof payload.seed !== 'string' || !parseSeed(payload.seed))
        return fail('combat.simulator.error.seed')
      const membersIn = payload.members as SimulatorProfileDto[]
      const envIn = env as unknown as SimulatorEnvironmentDto
      const nodeIn = payload.selectedNodeIds as string[]
      const verifyReq: SimulatorResolveRequestDto = {
        formatVersion: SIMULATOR_FORMAT_VERSION, nodeId: nodeIn[0], members: membersIn, environment: envIn,
      }
      await bridge().resolveProfiles(JSON.stringify(verifyReq))
      if (!alive(gen)) return
      setMembers(membersIn)
      setEnvironment(envIn)
      setSelectedNodeIds(nodeIn)
      setDifficulties(payload.difficulties as number[])
      setDurationSeconds(payload.durationSeconds)
      setSeed(payload.seed)
      setConfigDirty(v => v + 1)
      setError(null)
    } catch (err) {
      if (alive(gen)) setError(err instanceof Error ? err.message : String(err))
    }
  }, [alive, bridge, t])

  useEffect(() => {
    if (!running) {
      setElapsedMs(0)
      return
    }
    const started = Date.now()
    const timer = setInterval(() => setElapsedMs(Date.now() - started), 100)
    return () => clearInterval(timer)
  }, [running])

  const disabledReason = useMemo(() => {
    if (!environment) return t('combat.simulator.hintLoadingEnv')
    if (members.length === 0) return t('combat.simulator.hintNeedMembers')
    if (selectedNodeIds.length === 0) return t('combat.simulator.hintNeedNodes')
    if (difficulties.length === 0) return t('combat.simulator.hintNeedDifficulties')
    return null
  }, [difficulties.length, environment, members.length, selectedNodeIds.length, t])

  const runAll = useCallback(async () => {
    if (!environment || members.length === 0 || selectedNodeIds.length === 0 || difficulties.length === 0 || running) return
    if (!parseSeed(seed)) { setError(t('combat.simulator.error.seed')); return }
    if (!Number.isFinite(durationSeconds) || durationSeconds < 1 || durationSeconds > 86400) {
      setError(t('combat.simulator.error.duration')); return
    }
    const runId = ++runIdRef.current
    const gen = openGenRef.current
    previewGenRef.current += 1
    setRunning(true)
    setElapsedMs(0)
    setError(null)
    setProgress(t('combat.simulator.initializingKernel'))
    const frozenEnv = JSON.parse(JSON.stringify(environment)) as SimulatorEnvironmentDto
    const frozenMembers = withSimulatedConsumableStock(JSON.parse(JSON.stringify(members)) as SimulatorProfileDto[])
    const combos = selectedNodeIds.flatMap(nodeId => difficulties.map(difficulty => ({ nodeId, difficulty })))
    const comboLabel = (combo: { nodeId: string; difficulty: number }) =>
      `${nodeName(combo.nodeId)} ${combo.difficulty === 0 ? t('combat.simulator.difficultyDefault') : `T${combo.difficulty}`}`
    const slots: Array<SimulatorRunResultDto | undefined> = new Array(combos.length)
    const owns = () => runIdRef.current === runId && openGenRef.current === gen
    const concurrency = recommendedSimulatorConcurrency()
    let cancelBatch: (() => void) | null = null
    try {
      // 按当前最新配置解析真实气血/灵力上限，以满血满蓝入场推演（避免沿用导入快照残血或防抖延迟的旧上限）
      const resolveReq: SimulatorResolveRequestDto = {
        formatVersion: SIMULATOR_FORMAT_VERSION,
        nodeId: selectedNodeIds[0],
        members: frozenMembers,
        environment: frozenEnv,
      }
      const resolvedJson = await bridge().resolveProfiles(JSON.stringify(resolveReq))
      if (!owns()) return
      const resolved = JSON.parse(resolvedJson) as SimulatorResolveResultDto
      for (const entry of resolved.members) {
        const target = frozenMembers[entry.memberIndex]
        const attrs = toCombatAttributes(entry.attributes as Record<string, unknown> | undefined)
        if (target && attrs) {
          target.maxHitpoints = attrs.maxHitpoints
          target.hitpoints = attrs.maxHitpoints
          target.maxManapoints = attrs.maxManapoints
          target.manapoints = attrs.maxManapoints
        }
      }
      const requestsJson = combos.map((combo) => {
        const request: SimulatorRunRequestDto = {
          formatVersion: SIMULATOR_FORMAT_VERSION,
          nodeId: combo.nodeId,
          difficulty: combo.difficulty,
          durationSeconds,
          seed,
          members: frozenMembers,
          environment: frozenEnv,
        }
        return JSON.stringify(request)
      })
      const batch = runSimulatorBatch(requestsJson, {
        primaryBridge: bridge(),
        concurrency,
        durationSeconds,
        onItemStart: (index, startedCount, totalCount, activeConcurrency) => {
          if (!owns()) return
          if (activeConcurrency > 1) {
            if (startedCount === 1) {
              setProgress(`${t('combat.simulator.parallelWorkers', { count: activeConcurrency })} · 0/${totalCount} ${comboLabel(combos[index])}`)
            }
            return
          }
          setProgress(`${startedCount}/${totalCount} ${comboLabel(combos[index])}`)
        },
        onItemComplete: (index, resultJson, completedCount, totalCount, activeConcurrency) => {
          if (!owns()) return
          slots[index] = JSON.parse(resultJson) as SimulatorRunResultDto
          const prefix = activeConcurrency > 1
            ? `${t('combat.simulator.parallelWorkers', { count: activeConcurrency })} · `
            : ''
          setProgress(`${prefix}${completedCount}/${totalCount} ${comboLabel(combos[index])}`)
        },
      })
      cancelBatch = batch.cancel
      cancelBatchRef.current = cancelBatch
      await batch.promise
      if (!owns()) return
      const finalResults = slots.filter((x): x is SimulatorRunResultDto => x !== undefined)
      setResults(finalResults)
      setRunStamp({ dirty: configDirty, seed, startedAtUnixSeconds: environment.capturedAtUnixSeconds })
      if (finalResults.length > 0) setActiveTab('results')
    } catch (err) {
      if (!owns()) return
      const message = err instanceof Error ? err.message : String(err)
      if (!message.includes('已取消')) {
        setError(message)
        const partial = slots.filter((x): x is SimulatorRunResultDto => x !== undefined)
        if (partial.length > 0) {
          setResults(partial)
          setRunStamp({ dirty: configDirty, seed, startedAtUnixSeconds: environment.capturedAtUnixSeconds })
        }
      }
    } finally {
      if (cancelBatch && cancelBatchRef.current === cancelBatch) cancelBatchRef.current = null
      if (owns()) {
        setRunning(false)
        setProgress('')
      }
    }
  }, [bridge, configDirty, difficulties, durationSeconds, environment, members, nodeName, running, seed, selectedNodeIds, t])

  const cancelRun = useCallback(() => {
    runIdRef.current += 1
    cancelBatchRef.current?.()
    cancelBatchRef.current = null
    bridgeRef.current?.cancelAll()
    setRunning(false)
    setProgress('')
  }, [])

  const staleResults = runStamp !== null && runStamp.dirty !== configDirty

  useEffect(() => {
    if (!environment || members.length === 0 || selectedNodeIds.length === 0) {
      setPreview(null)
      setResolvingPreview(false)
      return
    }
    if (!open || running) {
      setResolvingPreview(false)
      return
    }
    const ticket = ++previewGenRef.current
    const nodeId = selectedNodeIds[0]
    setResolvingPreview(true)
    const timer = setTimeout(() => {
      const request: SimulatorResolveRequestDto = {
        formatVersion: SIMULATOR_FORMAT_VERSION, nodeId, members, environment,
      }
      bridge().resolveProfiles(JSON.stringify(request))
        .then(json => {
          if (previewGenRef.current === ticket) {
            setPreview(JSON.parse(json) as SimulatorResolveResultDto)
            setResolvingPreview(false)
          }
        })
        .catch(err => {
          if (previewGenRef.current === ticket) {
            setError(err instanceof Error ? err.message : String(err))
            setResolvingPreview(false)
          }
        })
    }, 250)
    return () => { clearTimeout(timer); previewGenRef.current += 1; setResolvingPreview(false) }
  }, [open, running, environment, members, selectedNodeIds, configDirty, bridge, setPreview])

  const randomSeed = useCallback(() => {
    const buf = new BigInt64Array(1)
    crypto.getRandomValues(buf)
    setSeed(String(buf[0]))
    setConfigDirty(v => v + 1)
  }, [])

  const copySeed = useCallback(() => {
    if (!navigator.clipboard) { setError(t('combat.simulator.error.clipboard')); return }
    navigator.clipboard.writeText(seed)
      .then(() => setNotice(t('combat.simulator.seedCopied')))
      .catch(() => setError(t('combat.simulator.error.clipboard')))
  }, [seed, setNotice, t])

  const resolvedAttrs = useMemo(() => {
    const map = new Map<number, CombatAttributes>()
    if (!preview) return map
    for (const member of preview.members) {
      const attrs = toCombatAttributes(member.attributes as Record<string, unknown> | undefined)
      if (attrs) map.set(member.memberIndex, attrs)
    }
    return map
  }, [preview])

  const safeMemberIndex = Math.min(selectedMemberIndex, Math.max(0, members.length - 1))
  const activeMember = members[safeMemberIndex] ?? null
  const selectedNodeSet = useMemo(() => new Set(selectedNodeIds), [selectedNodeIds])
  const difficultySet = useMemo(() => new Set(difficulties), [difficulties])
  const allyCandidates = useMemo(() =>
    (party?.members ?? []).filter(m => m.playerId !== playerId
      && !members.some(existing => existing.sourcePlayerId === m.playerId)),
  [members, party?.members, playerId])

  const removeMember = useCallback((idx: number) => {
    setMembers(prev => {
      const next = prev.filter((_, i) => i !== idx)
      setSelectedMemberIndex(curr => Math.min(curr > idx ? curr - 1 : curr, Math.max(0, next.length - 1)))
      return next
    })
    setConfigDirty(v => v + 1)
  }, [])

  return (
    <Modal open={open} onClose={() => { cancelRun(); onClose() }} labelledBy="wasm-sim-title" size="large" className="sim-modal">
      <div className="sim-modal-header">
        <div className="sim-modal-title-wrap">
          <span className="sim-title-icon"><Icon name="combat" size={20} /></span>
          <h2 id="wasm-sim-title" className="sim-title-text">{t('combat.simulator.modalTitle')}</h2>
        </div>

        <div className="sim-main-tabs" role="tablist">
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'config'}
            className={`sim-main-tab ${activeTab === 'config' ? 'is-active' : ''}`}
            onClick={() => setActiveTab('config')}
          >
            {t('combat.simulator.tabConfig')}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'results'}
            className={`sim-main-tab ${activeTab === 'results' ? 'is-active' : ''}`}
            onClick={() => setActiveTab('results')}
          >
            {t('combat.simulator.tabResults')}
            {((results && results.length > 0) || running) && <span className="sim-tab-dot" />}
          </button>
        </div>
      </div>

      <div className="sim-modal-body">
        {activeTab === 'config' ? (
          <div className="sim-config-layout">
            {/* 左栏：阵容与属性编辑 */}
            <section className="sim-left-column">
              <div className="sim-party-editor">
                {/* 成员标签栏 */}
                <div className="sim-party-tabs">
                  {members.map((member, index) => (
                    <div
                      key={`${member.sourcePlayerId ?? 'custom'}-${index}`}
                      role="button"
                      tabIndex={0}
                      className={`sim-party-tab-btn ${index === safeMemberIndex ? 'is-active' : ''}`}
                      onClick={() => setSelectedMemberIndex(index)}
                      onKeyDown={e => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault()
                          setSelectedMemberIndex(index)
                        }
                      }}
                    >
                      <span>{member.displayName || `${t('combat.simulator.customMember')} ${index + 1}`}</span>
                      {member.sourcePlayerId === playerId && (
                        <span className="sim-tag-self">{t('combat.simulator.tagSelf')}</span>
                      )}
                      {!member.sourcePlayerId && (
                        <span className="sim-tag-self">{t('combat.simulator.custom')}</span>
                      )}
                      {!running && members.length > 1 && (
                        <button
                          type="button"
                          className="sim-tab-close"
                          title={t('combat.simulator.remove')}
                          onClick={e => { e.stopPropagation(); removeMember(index) }}
                        >
                          ×
                        </button>
                      )}
                    </div>
                  ))}
                  {members.length < MAX_MEMBERS && (
                    <button
                      type="button"
                      className="sim-party-tab-add"
                      disabled={running}
                      onClick={() => { void addCustom().then(() => setSelectedMemberIndex(members.length)) }}
                    >
                      + {t('combat.simulator.addCustom')}
                    </button>
                  )}
                </div>

                {/* 导入与配置操作栏 */}
                <div className="sim-editor-toolbar">
                  <button type="button" className="sim-btn sim-btn--primary" disabled={running}
                    onClick={() => importSelf().catch(err => { if (err?.name !== 'AbortError') setError(String(err?.message ?? err)) })}>
                    {t('combat.simulator.importSelf')}（{name}）
                  </button>
                  {allyCandidates.length > 0 && (
                    <div className="sim-teammate-import-select">
                      <span>{t('combat.simulator.importTeammate')}:</span>
                      <select className="sim-select wasm-sim-ally-select"
                        disabled={running || members.length >= MAX_MEMBERS}
                        value={allyTarget}
                        onChange={e => setAllyTarget(e.target.value)}>
                        <option value="">{t('combat.simulator.selectTeammate')}</option>
                        {allyCandidates.map(m => (
                          <option key={m.playerId} value={m.playerId}>{m.displayName}</option>
                        ))}
                      </select>
                      <button type="button" className="sim-btn sim-btn--secondary"
                        disabled={running || members.length >= MAX_MEMBERS || !allyTarget}
                        onClick={() => {
                          importAlly(allyTarget).catch(err => { if (err?.name !== 'AbortError') setError(String(err?.message ?? err)) })
                          setAllyTarget('')
                        }}>
                        {t('combat.simulator.importAlly')}
                      </button>
                    </div>
                  )}
                  <div className="wasm-sim-toolbar-right">
                    <button type="button" className="sim-btn sim-btn--secondary" disabled={running}
                      onClick={() => refreshEnvironment().catch(err => { if (err?.name !== 'AbortError') setError(String(err?.message ?? err)) })}>
                      {t('combat.simulator.refreshEnv')}
                    </button>
                    <button type="button" className="sim-btn sim-btn--secondary" disabled={running}
                      onClick={() => {
                        setError(null)
                        setLoadError(null)
                        resetSession()
                      }}>
                      {t('combat.simulator.resetAllConfig')}
                    </button>
                    <button type="button" className="sim-btn sim-btn--secondary" disabled={running || members.length === 0} onClick={exportConfig}>
                      {t('combat.simulator.exportJson')}
                    </button>
                    <label className="sim-btn sim-btn--secondary wasm-sim-import-label">
                      {t('combat.simulator.importJson')}
                      <input type="file" accept="application/json" hidden disabled={running}
                        onChange={e => { const f = e.target.files?.[0]; if (f) void importConfig(f); e.target.value = '' }} />
                    </label>
                  </div>
                </div>

                {loadError && <div className="wasm-sim-error">{loadError}</div>}
                {error && <div className="wasm-sim-error">{error}</div>}
                <div className="wasm-sim-note">{t('combat.simulator.note')} {t('combat.simulator.cachedSessionNote')}</div>

                {activeMember ? (
                  <SimulatorProfileEditor
                    key={`${activeMember.sourcePlayerId ?? 'custom'}-${safeMemberIndex}`}
                    member={activeMember}
                    index={safeMemberIndex}
                    disabled={running}
                    allMembers={members}
                    subTab={profileSubTab}
                    onSubTabChange={setProfileSubTab}
                    resolvedAttributes={resolvedAttrs.get(safeMemberIndex) ?? null}
                    resolvingPreview={resolvingPreview && preview === null}
                    onChange={next => updateMember(safeMemberIndex, next)}
                  />
                ) : (
                  <div className="sim-results-empty">{t('combat.simulator.emptyParty')}</div>
                )}
              </div>
            </section>

            {/* 右栏：目标地点、难度、时长与开始推演 */}
            <section className="sim-right-column">
              <div className="sim-config-panel">
                <div className="sim-config-section sim-config-section--targets">
                  <div className="sim-inline-header-bar">
                    <div className="sim-inline-title-group">
                      <span className="sim-config-title">{t('combat.simulator.targets')}</span>
                      <span className="sim-selection-count-badge">
                        {t('combat.simulator.selectedNodesCount', { count: selectedNodeIds.length })}
                      </span>
                    </div>
                    <div className="sim-inline-toolbar-controls">
                      <select
                        className="sim-select sim-select--compact"
                        aria-label={t('combat.simulator.regionFilterLabel')}
                        disabled={running}
                        value={selectedMapId}
                        onChange={e => setSelectedMapId(e.target.value)}
                      >
                        <option value="all">{t('combat.simulator.regionAll')}（{combatNodes.length}）</option>
                        {regionOptions.map(region => (
                          <option key={region.mapId} value={region.mapId}>
                            {region.mapName}（{region.count}）
                          </option>
                        ))}
                      </select>
                      <button
                        type="button"
                        className="sim-batch-btn"
                        disabled={running || filteredNodes.length === 0}
                        onClick={() => {
                          setSelectedNodeIds(prev => {
                            const merged = new Set(prev)
                            for (const n of filteredNodes) merged.add(n.id)
                            return [...merged]
                          })
                          setConfigDirty(v => v + 1)
                        }}
                      >
                        {t('combat.simulator.selectAllNodes')}
                      </button>
                      <button
                        type="button"
                        className="sim-batch-btn sim-batch-btn--clear"
                        disabled={running || selectedNodeIds.length === 0}
                        onClick={() => { setSelectedNodeIds([]); setConfigDirty(v => v + 1) }}
                      >
                        {t('combat.simulator.clearSelectedNodes')}
                      </button>
                    </div>
                  </div>

                  <div className="sim-node-multiselect-container sim-node-multiselect-container--dense">
                    {filteredNodes.length > 0 ? (
                      <div className="sim-node-cards-grid sim-node-cards-grid--dense">
                        {filteredNodes.map(node => {
                          const checked = selectedNodeSet.has(node.id)
                          const hpRegen = node.entry.effects.combatHitpointRegen ?? 0
                          const mpRegen = node.entry.effects.combatManapointRegen ?? 0
                          const dr = node.entry.effects.combatDamageReductionRatio ?? 0
                          const weaken = node.entry.effects.monsterWeakenRatio ?? 0
                          const crowded = node.entry.usages.filter(u => u.efficiency < 1)
                          const effectTexts: string[] = []
                          if (hpRegen !== 0) effectTexts.push(t('combat.simulator.nodeHpRegen', { value: hpRegen }))
                          if (mpRegen !== 0) effectTexts.push(t('combat.simulator.nodeMpRegen', { value: mpRegen }))
                          if (dr !== 0) effectTexts.push(`${t('combat.simulator.reduction')}${Math.round(dr * 100)}%`)
                          if (weaken !== 0) effectTexts.push(`${t('combat.simulator.weaken')}${Math.round(weaken * 100)}%`)
                          for (const u of crowded) {
                            effectTexts.push(t('combat.simulator.crowdedEff', { pct: Math.round(u.efficiency * 100) }))
                          }
                          return (
                            <FloatingPopover
                              key={node.id}
                              trigger="hover"
                              placement="top"
                              size="small"
                              interactive={false}
                              displayContents
                              content={() => (
                                <FloatingInfoCard
                                  title={node.name}
                                  subtitle={node.mapName}
                                  badge={effectTexts.length > 0 ? t('combat.simulator.nodeBonusCount', { count: effectTexts.length }) : undefined}
                                  badgeTone="gold"
                                  lines={effectTexts.length > 0 ? effectTexts : [t('combat.simulator.noBuildingBonus')]}
                                />
                              )}
                            >
                              <label className={`sim-node-card sim-node-card--dense ${checked ? 'is-selected' : ''}`}>
                                <div className="sim-node-card-header">
                                  <input
                                    type="checkbox"
                                    className="sim-node-checkbox"
                                    disabled={running}
                                    checked={checked}
                                    onChange={e => {
                                      setSelectedNodeIds(prev =>
                                        e.target.checked ? [...prev, node.id] : prev.filter(id => id !== node.id))
                                      setConfigDirty(v => v + 1)
                                    }}
                                  />
                                  <span className="sim-node-name">{node.name}</span>
                                  {effectTexts.length > 0 && (
                                    <span className="sim-node-bonus-pill">
                                      {t('combat.simulator.nodeBonusCount', { count: effectTexts.length })}
                                    </span>
                                  )}
                                </div>
                                {selectedMapId === 'all' && (
                                  <div className="sim-node-dense-tags">
                                    <span className="sim-node-map-tag">{node.mapName}</span>
                                  </div>
                                )}
                              </label>
                            </FloatingPopover>
                          )
                        })}
                      </div>
                    ) : (
                      <div className="sim-difficulty-hint">
                        {environment ? t('combat.simulator.noNodes') : (loadError ?? t('combat.simulator.envLoading'))}
                      </div>
                    )}
                  </div>
                </div>

                <div className="sim-config-section sim-config-section--inline">
                  <div className="sim-inline-section-row">
                    <span className="sim-inline-label">{t('combat.simulator.difficultyLabel')}</span>
                    <div className="sim-difficulty-inline-pills">
                      {[0, 1, 2, 3, 4, 5].map(d => {
                        const checked = difficultySet.has(d)
                        return (
                          <label key={d} className={`sim-difficulty-checkbox-card sim-difficulty-pill ${checked ? 'is-checked' : ''}`}>
                            <input
                              type="checkbox"
                              className="sim-difficulty-checkbox"
                              disabled={running}
                              checked={checked}
                              onChange={e => {
                                setDifficulties(prev => e.target.checked ? [...prev, d] : prev.filter(x => x !== d))
                                setConfigDirty(v => v + 1)
                              }}
                            />
                            <span className="sim-difficulty-card-label">
                              {d === 0 ? t('combat.simulator.difficultyDefault') : `T${d}`}
                            </span>
                          </label>
                        )
                      })}
                    </div>
                    <div className="sim-inline-actions">
                      <button
                        type="button"
                        className="sim-batch-btn"
                        disabled={running}
                        onClick={() => { setDifficulties([0, 1, 2, 3, 4, 5]); setConfigDirty(v => v + 1) }}
                      >
                        {t('combat.simulator.selectAllDifficulties')}
                      </button>
                      <button
                        type="button"
                        className="sim-batch-btn sim-batch-btn--clear"
                        disabled={running}
                        onClick={() => { setDifficulties([0]); setConfigDirty(v => v + 1) }}
                      >
                        {t('combat.simulator.resetDifficulties')}
                      </button>
                    </div>
                  </div>
                </div>

                <div className="sim-config-section sim-config-section--inline">
                  <div className="sim-inline-section-row">
                    <span className="sim-inline-label">{t('combat.simulator.durationTitle')}</span>
                    <div className="sim-duration-inline-presets">
                      {DURATION_PRESETS.map(seconds => (
                        <button
                          key={seconds}
                          type="button"
                          disabled={running}
                          className={`sim-preset-btn sim-preset-btn--compact ${durationSeconds === seconds ? 'is-active' : ''}`}
                          onClick={() => { setDurationSeconds(seconds); setConfigDirty(v => v + 1) }}
                        >
                          {seconds >= 3600 ? `${seconds / 3600}h` : `${seconds / 60}m`}
                        </button>
                      ))}
                    </div>
                    <div className="sim-inline-duration-custom">
                      <input
                        type="number"
                        aria-label={t('combat.simulator.customDuration')}
                        className="sim-input sim-input--compact"
                        min={1}
                        max={86400}
                        step={1}
                        disabled={running}
                        value={durationSeconds}
                        onChange={e => {
                          const v = Number(e.target.value)
                          if (Number.isFinite(v)) { setDurationSeconds(v); setConfigDirty(x => x + 1) }
                        }}
                      />
                      <span className="sim-seconds-hint">
                        {t('combat.simulator.secondsUnit')} ({(durationSeconds / 3600).toFixed(2)}{t('combat.simulator.hoursUnit')})
                      </span>
                    </div>
                  </div>
                  <div className="sim-inline-section-row">
                    <span className="sim-inline-label">{t('combat.simulator.seedTitle')}</span>
                    <div className="wasm-sim-seed sim-inline-seed-group">
                      <input
                        type="text"
                        aria-label={t('combat.simulator.seedTitle')}
                        className="sim-input sim-input--compact wasm-sim-seed-input"
                        inputMode="numeric"
                        disabled={running}
                        value={seed}
                        onChange={e => {
                          if (/^-?\d{0,19}$/.test(e.target.value)) {
                            setSeed(e.target.value)
                            setConfigDirty(v => v + 1)
                          }
                        }}
                      />
                      <button type="button" className="sim-batch-btn" disabled={running} onClick={randomSeed}>
                        {t('combat.simulator.randomSeed')}
                      </button>
                      <button type="button" className="sim-batch-btn" disabled={running} onClick={copySeed}>
                        {t('combat.simulator.copySeed')}
                      </button>
                    </div>
                  </div>
                </div>

                {error && <div className="wasm-sim-error wasm-sim-error--inline" role="alert">{error}</div>}
                <div className="sim-action-area">
                  {!running ? (
                    <>
                      <button
                        type="button"
                        className="sim-run-button"
                        disabled={!environment || members.length === 0 || selectedNodeIds.length === 0 || difficulties.length === 0}
                        onClick={runAll}
                      >
                        {t('combat.simulator.startSimulation')}
                      </button>
                      {disabledReason && (
                        <div className="wasm-sim-disabled-hint">{disabledReason}</div>
                      )}
                    </>
                  ) : (
                    <div className="wasm-sim-running-bar" role="status" aria-live="polite">
                      <span className="wasm-sim-spinner" aria-hidden="true" />
                      <span className="wasm-sim-progress">
                        {progress || t('combat.simulator.initializingKernel')}
                      </span>
                      <span className="wasm-sim-elapsed">
                        {t('combat.simulator.runningElapsed', { s: (elapsedMs / 1000).toFixed(1) })}
                      </span>
                      <button type="button" className="sim-btn sim-btn--secondary" onClick={cancelRun}>
                        {t('common.cancel')}
                      </button>
                    </div>
                  )}
                </div>
              </div>
            </section>
          </div>
        ) : (
          <div className="sim-results-layout">
            {error && <div className="wasm-sim-error" role="alert">{error}</div>}
            {staleResults && (
              <div className="wasm-sim-stale-bar">
                <span>{t('combat.simulator.staleResults')}</span>
                <button
                  type="button"
                  className="sim-btn sim-btn--primary"
                  disabled={running || !environment || members.length === 0 || selectedNodeIds.length === 0 || difficulties.length === 0}
                  onClick={runAll}
                >
                  {t('combat.simulator.rerunBtn')}
                </button>
              </div>
            )}
            {results && results.length > 0 ? (
              <SimulatorResults
                results={results}
                nodeName={nodeName}
                nodeEnvMap={nodeEnvMap}
                runSeed={runStamp?.seed ?? seed}
                startedAtUnixSeconds={runStamp?.startedAtUnixSeconds ?? 0}
              />
            ) : running ? (
              <div className="sim-results-empty">
                <div className="wasm-sim-running-bar" role="status" aria-live="polite">
                  <span className="wasm-sim-spinner" aria-hidden="true" />
                  <span className="wasm-sim-progress">
                    {progress || t('combat.simulator.initializingKernel')}
                  </span>
                  <span className="wasm-sim-elapsed">
                    {t('combat.simulator.runningElapsed', { s: (elapsedMs / 1000).toFixed(1) })}
                  </span>
                  <button type="button" className="sim-btn sim-btn--secondary" onClick={cancelRun}>
                    {t('common.cancel')}
                  </button>
                </div>
              </div>
            ) : (
              <div className="sim-results-empty">
                <p>{t('combat.simulator.noReportHint')}</p>
              </div>
            )}
          </div>
        )}
      </div>
    </Modal>
  )
})
