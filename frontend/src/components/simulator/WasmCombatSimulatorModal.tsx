import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Modal } from '../Modal'
import { useT } from '../../i18n'
import { useGameStore } from '../../game/gameStore'
import { useSocialStore } from '../../game/socialStore'
import { createSimulatorWasmBridge, type SimulatorWasmBridge } from '../../game/simulator/wasmSimulatorBridge'
import { fetchSimulatorEnvironment, fetchSimulatorProfile } from '../../network/apiClient'
import type {
  SimulatorEnvironmentDto, SimulatorProfileDto, SimulatorResolveRequestDto, SimulatorResolveResultDto,
  SimulatorRunRequestDto, SimulatorRunResultDto
} from '../../game/simulator/wasmSimulatorTypes'
import { SIMULATOR_FORMAT_VERSION } from '../../game/simulator/wasmSimulatorTypes'
import { WasmSimulatorProfileEditor } from './WasmSimulatorProfileEditor'
import { WasmSimulatorResults } from './WasmSimulatorResults'
import { CombatAttributePanel } from '../CombatAttributePanel'
import type { CombatAttributes } from '../../game/types'

interface Props { open: boolean; onClose: () => void }

const DURATION_PRESETS = [600, 1800, 3600, 7200, 21600, 86400]
const MAX_MEMBERS = 5
const INT64_MIN = -(1n << 63n)
const INT64_MAX = (1n << 63n) - 1n

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
  const bridgeRef = useRef<SimulatorWasmBridge | null>(null)
  const openGenRef = useRef(0)
  const runIdRef = useRef(0)
  const previewGenRef = useRef(0)
  const importRef = useRef<Record<string, { seq: number; ctl: AbortController }>>({})

  const [members, setMembers] = useState<SimulatorProfileDto[]>([])
  const [environment, setEnvironment] = useState<SimulatorEnvironmentDto | null>(null)
  const [selectedNodeIds, setSelectedNodeIds] = useState<string[]>([])
  const [difficulties, setDifficulties] = useState<number[]>([0])
  const [durationSeconds, setDurationSeconds] = useState(3600)
  const [seed, setSeed] = useState('1')
  const [results, setResults] = useState<SimulatorRunResultDto[] | null>(null)
  const [runStamp, setRunStamp] = useState<{ dirty: number; seed: string; startedAtUnixSeconds: number } | null>(null)
  const [preview, setPreview] = useState<SimulatorResolveResultDto | null>(null)
  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [configDirty, setConfigDirty] = useState(0)

  const bridge = useCallback(() => {
    if (!bridgeRef.current) bridgeRef.current = createSimulatorWasmBridge()
    return bridgeRef.current
  }, [])

  const combatNodes = useMemo(() => {
    if (!environment) return []
    const allowed = new Map(environment.nodes.map(node => [node.nodeId, node]))
    return (maps ?? []).flatMap(map => (map.nodes ?? [])
      .filter(node => (node.encounterIds?.length ?? 0) > 0 && allowed.has(node.nodeId))
      .map(node => ({ id: node.nodeId, name: node.name ?? node.nodeId, entry: allowed.get(node.nodeId)! })))
  }, [environment, maps])

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
    Promise.all([
      members.length === 0 ? importSelf() : Promise.resolve(),
      environment === null ? refreshEnvironment() : Promise.resolve()
    ]).catch(err => {
      if (alive(gen)) setLoadError(err instanceof Error ? err.message : String(err))
    })
  }, [open])

  useEffect(() => {
    if (!open) {
      openGenRef.current += 1
      runIdRef.current += 1
      previewGenRef.current += 1
      for (const entry of Object.values(importRef.current)) entry.ctl.abort()
      importRef.current = {}
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
    bridgeRef.current?.cancelAll()
  }, [])

  const defaultNodeGenRef = useRef(-1)
  useEffect(() => {
    if (selectedNodeIds.length === 0 && combatNodes.length > 0 && defaultNodeGenRef.current !== openGenRef.current) {
      defaultNodeGenRef.current = openGenRef.current
      const preferred = worldNodeId && combatNodes.some(n => n.id === worldNodeId) ? worldNodeId : combatNodes[0].id
      setSelectedNodeIds([preferred])
    }
  }, [combatNodes, selectedNodeIds.length, worldNodeId])

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
    const payload = { formatVersion: SIMULATOR_FORMAT_VERSION, members, environment,
      selectedNodeIds, difficulties, durationSeconds, seed }
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
        formatVersion: SIMULATOR_FORMAT_VERSION, nodeId: nodeIn[0], members: membersIn, environment: envIn
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

  const runAll = useCallback(async () => {
    if (!environment || members.length === 0 || selectedNodeIds.length === 0 || difficulties.length === 0 || running) return
    if (!parseSeed(seed)) { setError(t('combat.simulator.error.seed')); return }
    if (!Number.isFinite(durationSeconds) || durationSeconds < 1 || durationSeconds > 86400) {
      setError(t('combat.simulator.error.duration')); return
    }
    const runId = ++runIdRef.current
    const gen = openGenRef.current
    setRunning(true)
    setError(null)
    setResults([])
    setRunStamp({ dirty: configDirty, seed, startedAtUnixSeconds: environment.capturedAtUnixSeconds })
    const frozenEnv = JSON.parse(JSON.stringify(environment)) as SimulatorEnvironmentDto
    const frozenMembers = JSON.parse(JSON.stringify(members)) as SimulatorProfileDto[]
    const combos = selectedNodeIds.flatMap(nodeId => difficulties.map(difficulty => ({ nodeId, difficulty })))
    const acc: SimulatorRunResultDto[] = []
    const owns = () => runIdRef.current === runId && openGenRef.current === gen
    try {
      for (const [index, combo] of combos.entries()) {
        if (!owns()) return
        setProgress(`${index + 1}/${combos.length} ${nodeName(combo.nodeId)} ${t('combat.simulator.difficultyLabel')}${combo.difficulty}`)
        const request: SimulatorRunRequestDto = {
          formatVersion: SIMULATOR_FORMAT_VERSION, nodeId: combo.nodeId, difficulty: combo.difficulty,
          durationSeconds, seed, members: frozenMembers, environment: frozenEnv
        }
        const resultJson = await bridge().run(JSON.stringify(request))
        if (!owns()) return
        acc.push(JSON.parse(resultJson) as SimulatorRunResultDto)
        setResults([...acc])
      }
    } catch (err) {
      if (!owns()) return
      const message = err instanceof Error ? err.message : String(err)
      if (!message.includes('已取消')) setError(message)
      if (acc.length > 0) setResults([...acc])
    } finally {
      if (owns()) {
        setRunning(false)
        setProgress('')
      }
    }
  }, [bridge, configDirty, difficulties, durationSeconds, environment, members, nodeName, running, seed, selectedNodeIds, t])

  const cancelRun = useCallback(() => {
    runIdRef.current += 1
    bridgeRef.current?.cancelAll()
    setRunning(false)
    setProgress('')
  }, [])

  const staleResults = runStamp !== null && runStamp.dirty !== configDirty

  useEffect(() => {
    if (!open || running || !environment || members.length === 0 || selectedNodeIds.length === 0) {
      setPreview(null)
      return
    }
    const ticket = ++previewGenRef.current
    const nodeId = selectedNodeIds[0]
    const timer = setTimeout(() => {
      const request: SimulatorResolveRequestDto = {
        formatVersion: SIMULATOR_FORMAT_VERSION, nodeId, members, environment
      }
      bridge().resolveProfiles(JSON.stringify(request))
        .then(json => { if (previewGenRef.current === ticket) setPreview(JSON.parse(json) as SimulatorResolveResultDto) })
        .catch(err => {
          if (previewGenRef.current === ticket)
            setError(err instanceof Error ? err.message : String(err))
        })
    }, 300)
    return () => { clearTimeout(timer); previewGenRef.current += 1; setPreview(null) }
  }, [open, running, environment, members, selectedNodeIds, configDirty, bridge])

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

  return (
    <Modal open={open} onClose={() => { cancelRun(); onClose() }} labelledBy="wasm-sim-title" size="large" className="sim-modal">
      <div className="wasm-sim">
        <h2 id="wasm-sim-title" className="wasm-sim-title">{t('combat.simulator.modalTitle')}</h2>
        <div className="wasm-sim-toolbar">
          <button type="button" disabled={running}
            onClick={() => importSelf().catch(err => { if (err?.name !== 'AbortError') setError(String(err?.message ?? err)) })}>
            {t('combat.simulator.importSelf')}（{name}）</button>
          <button type="button" disabled={running}
            onClick={() => refreshEnvironment().catch(err => { if (err?.name !== 'AbortError') setError(String(err?.message ?? err)) })}>
            {t('combat.simulator.refreshEnv')}</button>
          <button type="button" disabled={running || members.length >= MAX_MEMBERS} onClick={addCustom}>{t('combat.simulator.addCustom')}</button>
          <button type="button" disabled={running || members.length === 0} onClick={exportConfig}>{t('combat.simulator.exportJson')}</button>
          <label className="wasm-sim-import">{t('combat.simulator.importJson')}
            <input type="file" accept="application/json" hidden disabled={running}
              onChange={e => { const f = e.target.files?.[0]; if (f) void importConfig(f); e.target.value = '' }} />
          </label>
        </div>
        {loadError && <div className="wasm-sim-error">{loadError}</div>}
        {error && <div className="wasm-sim-error">{error}</div>}
        <div className="wasm-sim-note">{t('combat.simulator.note')}</div>
        <div className="wasm-sim-columns">
          <section className="wasm-sim-members">
            <h3>{t('combat.simulator.members')}（{members.length}/{MAX_MEMBERS}）</h3>
            {members.map((member, index) => (
              <WasmSimulatorProfileEditor key={`${member.sourcePlayerId ?? 'custom'}-${index}`}
                member={member} index={index} disabled={running}
                allMembers={members}
                resolved={resolvedAttrs.get(index)}
                onChange={next => updateMember(index, next)}
                onRemove={() => { setMembers(prev => prev.filter((_, i) => i !== index)); setConfigDirty(v => v + 1) }} />
            ))}
            {(party?.members?.length ?? 0) > 1 && <div className="wasm-sim-ally">
              <select disabled={running || members.length >= MAX_MEMBERS} value={allyTarget}
                onChange={e => setAllyTarget(e.target.value)}>
                <option value="">{t('combat.simulator.selectTeammate')}</option>
                {party!.members.filter(m => m.playerId !== playerId
                  && !members.some(existing => existing.sourcePlayerId === m.playerId))
                  .map(m => <option key={m.playerId} value={m.playerId}>{m.displayName}</option>)}
              </select>
              <button type="button" disabled={running || members.length >= MAX_MEMBERS || !allyTarget}
                onClick={() => {
                  importAlly(allyTarget).catch(err => { if (err?.name !== 'AbortError') setError(String(err?.message ?? err)) })
                  setAllyTarget('')
                }}>
                {t('combat.simulator.importAlly')}
              </button>
            </div>}
          </section>
          <section className="wasm-sim-config">
            <h3>{t('combat.simulator.targets')}</h3>
            <div className="wasm-sim-nodes">
              {combatNodes.map(node => (
                <label key={node.id} className="wasm-sim-node">
                  <input type="checkbox" disabled={running}
                    checked={selectedNodeIds.includes(node.id)}
                    onChange={e => {
                      setSelectedNodeIds(prev => e.target.checked ? [...prev, node.id] : prev.filter(id => id !== node.id))
                      setConfigDirty(v => v + 1)
                    }} />
                  <span>{node.name}</span>
                  {(node.entry.effects.combatHitpointRegen ?? 0) !== 0 &&
                    <em>{t('combat.simulator.nodeHpRegen', { value: node.entry.effects.combatHitpointRegen ?? 0 })}</em>}
                  {(node.entry.effects.combatManapointRegen ?? 0) !== 0 &&
                    <em>{t('combat.simulator.nodeMpRegen', { value: node.entry.effects.combatManapointRegen ?? 0 })}</em>}
                  {(node.entry.effects.combatDamageReductionRatio ?? 0) !== 0 &&
                    <em>{t('combat.simulator.reduction')}{Math.round((node.entry.effects.combatDamageReductionRatio ?? 0) * 100)}%</em>}
                  {(node.entry.effects.monsterWeakenRatio ?? 0) !== 0 &&
                    <em>{t('combat.simulator.weaken')}{Math.round((node.entry.effects.monsterWeakenRatio ?? 0) * 100)}%</em>}
                  {node.entry.usages.filter(u => u.efficiency < 1).map(u =>
                    <em key={u.buildingId}>{t('combat.simulator.crowdedEff', { pct: Math.round(u.efficiency * 100) })}</em>)}
                </label>
              ))}
              {environment && combatNodes.length === 0 && <div className="wasm-sim-empty">{t('combat.simulator.noNodes')}</div>}
              {!environment && !loadError && <div className="wasm-sim-empty">{t('combat.simulator.envLoading')}</div>}
            </div>
            <h3>{t('combat.simulator.difficultyLabel')}</h3>
            <div className="wasm-sim-difficulties">
              {[0, 1, 2, 3, 4, 5].map(d => (
                <label key={d}><input type="checkbox" disabled={running} checked={difficulties.includes(d)}
                  onChange={e => {
                    setDifficulties(prev => e.target.checked ? [...prev, d] : prev.filter(x => x !== d))
                    setConfigDirty(v => v + 1)
                  }} />{d}</label>
              ))}
            </div>
            <h3>{t('combat.simulator.durationTitle')}</h3>
            <div className="wasm-sim-duration">
              {DURATION_PRESETS.map(seconds => (
                <button key={seconds} type="button" disabled={running}
                  className={durationSeconds === seconds ? 'is-active' : ''}
                  onClick={() => { setDurationSeconds(seconds); setConfigDirty(v => v + 1) }}>
                  {seconds >= 3600 ? `${seconds / 3600}h` : `${seconds / 60}m`}
                </button>
              ))}
              <input type="number" min={1} max={86400} step="any" disabled={running} value={durationSeconds}
                onChange={e => {
                  const v = Number(e.target.value)
                  if (Number.isFinite(v)) { setDurationSeconds(v); setConfigDirty(x => x + 1) }
                }} />
            </div>
            <h3>{t('combat.simulator.seedTitle')}</h3>
            <div className="wasm-sim-seed">
              <input type="text" inputMode="numeric" disabled={running} value={seed}
                onChange={e => { if (/^-?\d{0,19}$/.test(e.target.value)) { setSeed(e.target.value); setConfigDirty(v => v + 1) } }} />
              <button type="button" disabled={running} onClick={randomSeed}>{t('combat.simulator.randomSeed')}</button>
              <button type="button" disabled={running} onClick={copySeed}>{t('combat.simulator.copySeed')}</button>
            </div>
            {preview && <div className="wasm-sim-preview">
              <h4>{t('combat.simulator.previewTitle')}（{nodeName(preview.nodeId)}）</h4>
              {preview.members.map(member => {
                const attrs = resolvedAttrs.get(member.memberIndex)
                return (
                  <details key={member.memberIndex} className="wasm-sim-preview-member" open={preview.members.length === 1}>
                    <summary>{member.displayName}</summary>
                    {attrs && <CombatAttributePanel attributes={attrs} hideTabs={false} />}
                  </details>
                )
              })}
            </div>}
            <div className="wasm-sim-actions">
              {!running
                ? <button type="button" className="primary"
                    disabled={!environment || members.length === 0 || selectedNodeIds.length === 0 || difficulties.length === 0}
                    onClick={runAll}>{t('combat.simulator.startSimulation')}</button>
                : <button type="button" onClick={cancelRun}>{t('common.cancel')}</button>}
              {progress && <span className="wasm-sim-progress">{progress}</span>}
            </div>
          </section>
        </div>
        {results && <div className="wasm-sim-results">
          {staleResults && <div className="wasm-sim-stale">{t('combat.simulator.staleResults')}</div>}
          <WasmSimulatorResults results={results} nodeName={nodeName} runSeed={runStamp?.seed ?? seed}
            startedAtUnixSeconds={runStamp?.startedAtUnixSeconds ?? 0} />
        </div>}
      </div>
    </Modal>
  )
})
